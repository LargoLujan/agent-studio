using System.IO;
using System.Text.Json;
using AgentStudio.Application.Debate;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Audit;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Debate.Audit.Fixtures;
using AgentStudio.Tests.Debate.Fakes;
using static AgentStudio.Tests.Debate.Audit.Fixtures.AuditWorld;
using static AgentStudio.Tests.Debate.Audit.Fixtures.ProjectAuditBench;

namespace AgentStudio.Tests.Debate.Audit;

/// <summary>
/// docs/15 D-28 to D-31 and F5-5 with doubles: a broken log launches no judge and the report is not trustworthy; the
/// verification is bound to HEAD; steps without isolation never run; rule P with the declared authors; both vendors
/// producers launch nothing; the pending problems of a prepared log; and the order of what a judged audit writes.
/// </summary>
public sealed class ProjectAuditServiceTests
{
    private static T Payload<T>(DecisionEntry entry)
        where T : DecisionPayload => DecisionPayloadSerializer.Deserialize<T>(entry.PayloadJson);

    private static async Task<ProjectAuditBench> FreshProjectAsync()
    {
        var bench = new ProjectAuditBench();
        await bench.SaveRequirementsAsync();
        return bench;
    }

    /// <summary>A project whose log holds Claude's clean F3-8 run, delivered: Anthropic produced code.</summary>
    private static async Task<ProjectAuditBench> ProjectWithClaudeRunAsync()
    {
        var run = await PreparedRun.CleanAsync();
        await run.DeliverAsync();
        return new ProjectAuditBench(run);
    }

    private static JsonElement Index(JudgeLaunch launch)
    {
        var dossier = Path.Combine(launch.Files!.Folder, "dossier.json");
        using var document = JsonDocument.Parse(File.ReadAllText(dossier));
        return document.RootElement.Clone();
    }

    private static void AssertNothingRan(ProjectAuditBench bench, int entriesBefore)
    {
        Assert.Equal(entriesBefore, bench.Entries.Count);
        Assert.Empty(bench.Gate.Requests);
        Assert.Empty(bench.Verifier.Calls);
        Assert.Empty(bench.Judges.Created);
        Assert.Empty(bench.Audits.Created);
        Assert.Empty(bench.Repository.Archives);
        Assert.Empty(bench.Verdicts.Stored);
    }

    private static void AssertSystemEscalation(AuditReport report)
    {
        Assert.Null(report.VerdictId);
        Assert.False(report.IsTrustworthy);
        Assert.Equal(ReviewDisposition.NeedsHuman, report.Disposition);
        Assert.Equal(VerdictDecision.EscalateToHuman, report.Verdict.Decision);
        Assert.Equal(IndependentReview.SystemIdentity, report.Verdict.Judge);
        Assert.True(report.Verdict.RequiresHumanIntervention);
    }

    [Fact]
    public async Task With_a_broken_log_no_judge_is_offered_nor_launched_nothing_is_written_and_the_report_is_not_trustworthy()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        var proposal = await bench.PrepareAsync(bench.Request());
        Assert.True(proposal.CanJudge);
        var edited = bench.Entries.First(e => e.Kind == DecisionKind.TaskAssigned);
        bench.World.Store.Tamper(AuditWorld.Project, edited.Sequence, e => e with { Summary = e.Summary + " (editado)" });
        var before = bench.Entries.Count;

        var again = await bench.PrepareAsync(bench.Request());
        var report = await bench.AuditAsync(proposal);

        Assert.Null(again.Judge);
        Assert.False(again.CanJudge);
        Assert.Contains(again.Problems, p => p.Contains("no está íntegro", StringComparison.Ordinal));
        Assert.False(report.DecisionLogIntact);
        AssertSystemEscalation(report);
        Assert.Contains($"entrada {edited.Sequence}", report.Verdict.Summary, StringComparison.Ordinal);
        Assert.Empty(report.Verification);
        AssertNothingRan(bench, before);
    }

    /// <summary>
    /// An audit is a critical moment (ADR-0010, addendum of 2026-11-04): its proposal (who may judge), its start, the dossier
    /// of its judge and its close all read the whole log.
    /// </summary>
    [Fact]
    public async Task Every_load_of_an_audit_reads_the_whole_log()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        var proposal = await bench.PrepareAsync(bench.Request());
        Assert.Equal([LogLoad.Full], bench.Recorder.Loads);

        var report = await bench.AuditAsync(proposal);

        Assert.True(report.IsTrustworthy);
        Assert.Equal([LogLoad.Full, LogLoad.Full, LogLoad.Full, LogLoad.Full], bench.Recorder.Loads);
    }

    [Fact]
    public async Task The_close_of_an_audit_that_throws_reads_the_whole_log_too()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        bench.Verdict = (_, _) => throw new InvalidOperationException("se cayó el juez");
        var proposal = await bench.PrepareAsync(bench.Request());

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => bench.AuditAsync(proposal));

        Assert.Equal(DecisionKind.ProjectAuditFinished, bench.Entries[^1].Kind);
        Assert.Equal(5, bench.Recorder.Loads.Count);
        Assert.All(bench.Recorder.Loads, l => Assert.Equal(LogLoad.Full, l));
    }

    [Fact]
    public async Task A_judged_audit_writes_its_unit_in_order_and_binds_the_approval_to_what_was_shown()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        var proposal = await bench.PrepareAsync(bench.Request());
        var before = bench.Entries.Count;

        var report = await bench.AuditAsync(proposal);

        Assert.True(proposal.CanJudge);
        Assert.Equal((Head, false), (proposal.HeadCommit, proposal.UncommittedChanges));
        Assert.Equal([VendorId.Anthropic], proposal.ProducerVendors);
        var approval = Assert.Single(bench.Gate.Requests);
        Assert.Equal((ProjectAuditService.ApprovalAction, proposal.HeaderHash, report.Audit.Value, RiskLevel.Critical), (approval.Action, approval.Binding, approval.RunId, approval.Risk));
        Assert.Equal(proposal.ApprovalText, approval.Description);
        Assert.StartsWith(AuditIds.Prefix + "20261019-", report.Audit.Value, StringComparison.Ordinal);

        var unit = bench.EntriesOf(report.Audit);
        Assert.Equal(unit, bench.Entries.Skip(before));
        Assert.Equal(
            [DecisionKind.EvidenceRegistered, DecisionKind.ProjectAuditStarted, DecisionKind.EvidenceRegistered, DecisionKind.EvidenceRegistered, DecisionKind.EvidenceRegistered, DecisionKind.VerdictIssued, DecisionKind.ProjectAuditFinished],
            unit.Select(e => e.Kind));
        var header = Assert.Single(bench.Audits.Created);
        var started = Payload<ProjectAuditStartedPayload>(unit[1]);
        Assert.Equal(DebateHeaderHash.Of(header), started.HeaderHash);
        Assert.Equal((Head, false, bench.World.Frozen.ContentHash, Profile().ContentHash), (started.HeadCommit, started.UncommittedChanges, started.RequirementSetHash, started.VerificationProfileHash));
        Assert.Equal((AuditWorld.Judge, VendorId.OpenAI), (started.Judge.Agent, started.Judge.Vendor));
        Assert.Equal([VendorId.Anthropic], started.ProducerVendors);
        Assert.Equal(DebateHeaderHash.Proposal(header, header.Request is var id && bench.World.Evidence.ContentText(id) is { } text ? ContentHash.Sha256Hex(text) : string.Empty), proposal.HeaderHash);

        var request = Payload<EvidenceRegisteredPayload>(unit[0]);
        Assert.Equal(header.Request, request.Evidence);
        Assert.Equal(proposal.ApprovalText, bench.World.Evidence.ContentText(header.Request));
        var captured = (await bench.World.Evidence.ListAsync(AuditWorld.Project, TestContext.Current.CancellationToken)).Single(e => e.Id == header.Request);
        Assert.Equal((EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, ProjectAuditService.RequestTitle), (captured.Kind, captured.Origin, captured.Title));

        var indexed = Payload<EvidenceRegisteredPayload>(unit[4]);
        Assert.Equal((EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, ProjectAuditService.IndexTitle, report.Audit.Value), (indexed.Kind, indexed.Origin, indexed.Title, indexed.RunId));
        Assert.Equal(File.ReadAllText(Path.Combine(bench.Launches[0].Files!.Folder, "dossier.json")), bench.World.Evidence.ContentText(indexed.Evidence));

        Assert.True(report.IsTrustworthy);
        Assert.Equal(ReviewDisposition.Approved, report.Disposition);
        var stored = Assert.Single(bench.Verdicts.Stored);
        Assert.Equal((report.VerdictId, report.Audit.Value, "juez-juez", AuditScope.WholeProject, true), (stored.Id, stored.RunId, stored.TaskId, stored.Scope, stored.Valid));
        var finished = Payload<ProjectAuditFinishedPayload>(unit[^1]);
        Assert.Equal([stored.Id], finished.Verdicts);
        Assert.Equal((ReviewDisposition.Approved, true), (finished.Disposition, finished.Trustworthy));
        Assert.Equal([ProjectActivityKind.Audit], bench.Activity.Entered);
        Assert.Equal(1, bench.Activity.Released);
    }

    [Fact]
    public async Task The_verification_runs_isolated_on_HEAD_and_its_evidence_is_bound_to_that_commit_for_the_judge()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        bench.Repository.Uncommitted = true;
        var proposal = await bench.PrepareAsync(bench.Request());

        var report = await bench.AuditAsync(proposal);

        var (directory, scope, steps) = Assert.Single(bench.Verifier.Calls);
        Assert.Equal((AuditWorld.Project, report.Audit, (TaskId?)null, (int?)null, Head, bench.Project.RootPath), (scope.ProjectId, scope.Run!.Value, scope.Task, scope.Attempt, scope.SubjectCommit, scope.RepositoryRoot));
        Assert.Equal(["build", "test"], steps.Select(s => s.Id));
        Assert.All(steps, s => Assert.Equal(ProcessIsolation.LowIntegrity, s.Isolation));
        Assert.Equal(bench.Project.RootPath, directory);
        Assert.Equal([EvidenceKind.BuildLog, EvidenceKind.TestRun], report.Verification.Select(e => e.Kind));
        Assert.All(report.Verification, e => Assert.Equal((Head, EvidenceOrigin.CapturedBySystem), (e.SubjectCommit, e.Origin)));
        var registered = bench.EntriesOf(report.Audit).Where(e => e.Kind == DecisionKind.EvidenceRegistered).Select(e => Payload<EvidenceRegisteredPayload>(e).Evidence).ToList();
        Assert.Subset(registered.ToHashSet(), report.Verification.Select(e => e.Id).ToHashSet());

        var dossier = Assert.Single(bench.Judge!.Evaluated);
        Assert.Equal(Head, dossier.CandidateCommit);
        Assert.Empty(dossier.IntegrityProblems);
        Assert.Contains(dossier.Evidence, e => e.Kind == EvidenceKind.TestRun && e.SubjectCommit == Head);
        var index = Index(bench.Launches[0]);
        Assert.Equal(Head, index.GetProperty("headCommit").GetString());
        Assert.Equal(["BuildLog", "TestRun"], index.GetProperty("verification").EnumerateArray().Select(v => v.GetProperty("kind").GetString()));
        Assert.All(index.GetProperty("verification").EnumerateArray(), v => Assert.Equal(Head, v.GetProperty("subjectCommit").GetString()));
        Assert.True(report.UncommittedChanges);
        Assert.Contains("cambios sin confirmar", proposal.ApprovalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_revision_finds_the_whole_audit_in_its_log_and_evidence_with_nothing_unannounced()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        var report = await bench.AuditAsync(await bench.PrepareAsync(bench.Request()));

        // What the judgment of a revision reads of an audit it revises (D-38): ProjectAuditStarted and the evidence its log registered.
        var unit = bench.EntriesOf(report.Audit);
        var problems = new List<string>();
        var registered = RunLog.RegisteredEvidence(unit, await bench.World.Evidence.ListByRunAsync(AuditWorld.Project, report.Audit.Value, TestContext.Current.CancellationToken), problems);

        Assert.Empty(problems);
        Assert.Equal(
            [(EvidenceKind.Document, ProjectAuditService.RequestTitle), (EvidenceKind.BuildLog, "Compilar (build)"), (EvidenceKind.TestRun, "Pruebas (test)"), (EvidenceKind.Document, ProjectAuditService.IndexTitle)],
            registered.Select(e => (e.Kind, e.Title)));
        Assert.All(registered.Where(e => e.Kind is not EvidenceKind.Document), e => Assert.Equal(Head, e.SubjectCommit));
        var started = Payload<ProjectAuditStartedPayload>(Assert.Single(unit, e => e.Kind == DecisionKind.ProjectAuditStarted));
        Assert.Equal((Head, bench.World.Frozen.ContentHash), (started.HeadCommit, started.RequirementSetHash));
        Assert.Equal([VendorId.Anthropic], started.ProducerVendors);
        Assert.Equal(report.VerdictId, Payload<VerdictIssuedPayload>(Assert.Single(unit, e => e.Kind == DecisionKind.VerdictIssued)).VerdictId);
    }

    [Fact]
    public async Task The_judge_reads_a_copy_of_HEAD_that_is_removed_when_the_audit_ends()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        var proposal = await bench.PrepareAsync(bench.Request());

        var report = await bench.AuditAsync(proposal);

        var launch = Assert.Single(bench.Launches);
        var archive = Assert.Single(bench.Repository.Archives);
        Assert.Equal((bench.Project.RootPath, Head, launch.WorkingDirectory), archive);
        Assert.StartsWith(Path.GetFullPath(bench.Options.SnapshotRoot), launch.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["README.md", "docs/adr/0001-decision.md", "docs/guia.md", "src/Calc.cs"], bench.SnapshotSeen[0]);
        Assert.False(Directory.Exists(launch.WorkingDirectory));
        Assert.IsType<ProjectAuditPrompt>(launch.Brief);
        Assert.Equal(report.Audit.Value, launch.Brief!.DebateId);
        Assert.Equal("juez-juez", launch.TaskId);

        var documents = Index(launch).GetProperty("documents").EnumerateArray().Select(d => (d.GetProperty("path").GetString(), d.GetProperty("sha256").GetString())).ToList();
        Assert.Equal(
            [("README.md", ContentHash.Sha256Hex(bench.Repository.Files["README.md"])), ("docs/adr/0001-decision.md", ContentHash.Sha256Hex(bench.Repository.Files["docs/adr/0001-decision.md"])), ("docs/guia.md", ContentHash.Sha256Hex(bench.Repository.Files["docs/guia.md"]))],
            documents);
    }

    [Fact]
    public async Task Steps_without_isolation_never_run_and_the_proposal_and_the_dossier_say_so()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        bench.Profiles.Profile = Profile() with
        {
            Steps = [.. Profile().Steps, new VerificationStep("lint", "Analizador", "tool", ["lint"], EvidenceKind.CommandOutput, TestReportFormat.None, null, TimeSpan.FromMinutes(1), ProcessIsolation.None)],
        };
        var proposal = await bench.PrepareAsync(bench.Request());

        await bench.AuditAsync(proposal);

        Assert.Equal(["lint"], proposal.SkippedSteps);
        Assert.Contains("sin aislamiento, no se ejecutan: lint", proposal.ApprovalText, StringComparison.Ordinal);
        Assert.DoesNotContain(bench.Verifier.Calls.SelectMany(c => c.Steps), s => s.Isolation == ProcessIsolation.None);
        Assert.Equal(["lint"], Index(bench.Launches[0]).GetProperty("skippedSteps").EnumerateArray().Select(s => s.GetString()));
    }

    [Fact]
    public async Task A_profile_whose_steps_are_all_without_isolation_runs_nothing_and_the_judge_sees_no_verification()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        bench.Profiles.Profile = new VerificationProfile("solo sin aislamiento", [.. Profile().Steps.Select(s => s with { Isolation = ProcessIsolation.None })]);
        bench.Verdict = (d, identity) => ScriptedJudge.From(JudgeAnswers.Deciding(d, new EvidenceId("EV-1"), VerdictDecision.Unresolved), identity);
        bench.Revise = (_, previous, _, _) => previous;
        var proposal = await bench.PrepareAsync(bench.Request());

        var report = await bench.AuditAsync(proposal);

        Assert.Equal(["build", "test"], proposal.SkippedSteps);
        Assert.Empty(bench.Verifier.Calls);
        Assert.Empty(report.Verification);
        Assert.Empty(Index(bench.Launches[0]).GetProperty("verification").EnumerateArray());
    }

    [Fact]
    public async Task Rule_P_counts_the_authors_the_user_declares_even_when_the_log_knows_no_producer()
    {
        using var bench = await FreshProjectAsync();
        var before = bench.Entries.Count;

        var claude = await bench.PrepareAsync(bench.Request(ProjectAuditBench.ClaudeJudge, VendorId.Anthropic));
        var codex = await bench.PrepareAsync(bench.Request(AuditWorld.Judge, VendorId.Anthropic));
        var report = await bench.AuditAsync(claude);

        Assert.Empty(claude.ProducerVendors);
        Assert.Null(claude.Judge);
        Assert.False(claude.CanJudge);
        Assert.Contains(claude.Problems, p => p.Contains("autores declarados: anthropic", StringComparison.Ordinal) && p.Contains("no se dispensa", StringComparison.Ordinal));
        Assert.True(codex.CanJudge);
        Assert.Equal(AuditWorld.Judge, codex.Judge!.Id);
        Assert.Contains("Autores declarados: anthropic", codex.ApprovalText, StringComparison.Ordinal);
        Assert.True(report.DecisionLogIntact);
        AssertSystemEscalation(report);
        AssertNothingRan(bench, before);
    }

    [Fact]
    public async Task When_both_vendors_produced_code_nobody_may_judge_and_the_audit_is_not_launched()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        var before = bench.Entries.Count;

        var codex = await bench.PrepareAsync(bench.Request(AuditWorld.Judge, VendorId.OpenAI));
        var claude = await bench.PrepareAsync(bench.Request(ProjectAuditBench.ClaudeJudge, VendorId.OpenAI));
        var report = await bench.AuditAsync(codex);

        Assert.All([codex, claude], p =>
        {
            Assert.Null(p.Judge);
            Assert.False(p.CanJudge);
            Assert.Equal([VendorId.Anthropic], p.ProducerVendors);
        });
        Assert.Contains(claude.Problems, p => p.Contains("vendors productores según el registro: anthropic", StringComparison.Ordinal));
        AssertSystemEscalation(report);
        Assert.Contains("no se dispensa", report.Verdict.Summary, StringComparison.Ordinal);
        AssertNothingRan(bench, before);
    }

    [Fact]
    public async Task The_judges_of_earlier_runs_are_not_producers_so_the_judge_of_the_template_may_audit_its_own_vendor_s_reviews()
    {
        using var bench = await ProjectWithClaudeRunAsync();

        var proposal = await bench.PrepareAsync(bench.Request(AuditWorld.Judge));

        Assert.DoesNotContain(VendorId.OpenAI, proposal.ProducerVendors);
        Assert.True(proposal.CanJudge);
    }

    [Fact]
    public async Task The_pending_problems_of_the_log_are_in_the_report_and_in_the_dossier()
    {
        var run = PreparedRun.Create();
        await run.StartAsync();
        await run.WorkAsync("T1");
        await run.RecordAsync(OrchestratorAudit.Auditor, DecisionKind.ReplanRequested, "El auditor pide replanificar",
            new ReplanRequestedPayload([OrchestratorAudit.ApprovalMissingCode], [], "Replanificar.", null) { RunId = Run.Value });
        using var bench = new ProjectAuditBench(run);
        var proposal = await bench.PrepareAsync(bench.Request());

        var report = await bench.AuditAsync(proposal);

        Assert.Equal([PendingProblems.RunUnfinishedCode, PendingProblems.ReplanPendingCode], report.Pending.Select(p => p.Code));
        Assert.Equal(PendingProblems.From(run.Entries.Where(e => e.Sequence <= run.Entries.First(x => x.Kind == DecisionKind.ReplanRequested).Sequence).ToList()), report.Pending);
        Assert.Equal(report.Pending.Select(p => p.Code), Index(bench.Launches[0]).GetProperty("pendingProblems").EnumerateArray().Select(p => p.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task Pending_problems_are_listed_also_when_nobody_may_judge()
    {
        var run = PreparedRun.Create();
        await run.StartAsync();
        await run.WorkAsync("T1");
        await run.FinishAsync(RunPhase.Failed);
        using var bench = new ProjectAuditBench(run);

        var report = await bench.AuditAsync(await bench.PrepareAsync(bench.Request(ProjectAuditBench.ClaudeJudge)));

        Assert.Equal([PendingProblems.RunFailedCode], report.Pending.Select(p => p.Code));
        AssertSystemEscalation(report);
    }

    [Fact]
    public async Task A_project_busy_with_another_activity_is_refused_before_anything_is_asked_or_written()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        var proposal = await bench.PrepareAsync(bench.Request());
        bench.Activity.Hold(ProjectActivityKind.Debate);
        var before = bench.Entries.Count;

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => bench.AuditAsync(proposal));

        Assert.Contains("Debate", refused.Message, StringComparison.Ordinal);
        AssertNothingRan(bench, before);
    }

    [Fact]
    public async Task A_denied_approval_runs_and_writes_nothing_and_releases_the_project()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        bench.Gate.Approve = false;
        var proposal = await bench.PrepareAsync(bench.Request());
        var before = bench.Entries.Count;

        var report = await bench.AuditAsync(proposal);

        Assert.Single(bench.Gate.Requests);
        AssertSystemEscalation(report);
        Assert.Contains("no se aprobó", report.Verdict.Summary, StringComparison.Ordinal);
        Assert.Equal(before, bench.Entries.Count);
        Assert.Empty(bench.Verifier.Calls);
        Assert.Empty(bench.Judges.Created);
        Assert.Equal(1, bench.Activity.Released);
    }

    [Fact]
    public async Task An_approval_that_answers_another_proposal_is_refused_before_anything_runs()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        bench.Gate.Answers = new string('a', 64);
        var proposal = await bench.PrepareAsync(bench.Request());
        var before = bench.Entries.Count;

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => bench.AuditAsync(proposal));

        Assert.Equal(before, bench.Entries.Count);
        Assert.Empty(bench.Verifier.Calls);
        Assert.Empty(bench.Judges.Created);
    }

    [Theory]
    [InlineData("head")]
    [InlineData("uncommitted")]
    [InlineData("profile")]
    [InlineData("producer")]
    public async Task What_changed_since_the_proposal_was_shown_is_refused_before_the_approval(string change)
    {
        using var bench = await ProjectWithClaudeRunAsync();
        var proposal = await bench.PrepareAsync(bench.Request());
        switch (change)
        {
            case "head":
                bench.Repository.Head = CommitOf("otro head");
                break;
            case "uncommitted":
                bench.Repository.Uncommitted = true;
                break;
            case "profile":
                bench.Profiles.Profile = ProfileWithoutTests();
                break;
            case "producer":
                await bench.World.RecordAsync(PreparedRun.System, DecisionKind.AgentRunStarted, "Otro agente",
                    new AgentRunStartedPayload(Tid("T1"), 2, Executor, ProviderId.CodexCli, VendorId.Google, null, "wt", new EvidenceId("EV-otro"), new string('a', 64), []) { RunId = Run.Value });
                break;
        }

        var before = bench.Entries.Count;

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => bench.AuditAsync(proposal));

        Assert.Contains("prepare it again", refused.Message, StringComparison.Ordinal);
        AssertNothingRan(bench, before);
    }

    [Fact]
    public async Task An_invalid_verdict_gets_one_correction_and_both_are_kept_and_closed()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        bench.Verdict = (d, identity) => ProjectAuditBench.Approval(d, identity) with { DecisionLogHeadHash = new string('9', 64) };
        bench.Revise = (d, _, _, identity) => ProjectAuditBench.Approval(d, identity);
        var proposal = await bench.PrepareAsync(bench.Request());

        var report = await bench.AuditAsync(proposal);

        Assert.True(report.Corrected);
        Assert.True(report.IsTrustworthy);
        var (first, second) = (bench.Verdicts.Stored[0], bench.Verdicts.Stored[1]);
        Assert.Equal((false, true, first.Id), (first.Valid, second.Valid, second.Supersedes));
        var finished = Payload<ProjectAuditFinishedPayload>(bench.EntriesOf(report.Audit)[^1]);
        Assert.Equal([first.Id, second.Id], finished.Verdicts);
        Assert.Single(bench.Judge!.Revisions);
    }

    [Fact]
    public async Task A_verdict_that_stays_invalid_needs_a_person_and_is_not_trustworthy()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        bench.Verdict = (d, identity) => ProjectAuditBench.Approval(d, identity) with { DecisionLogHeadHash = new string('9', 64) };
        bench.ReviseFailure = new JudgeOutputException("not a verdict");
        var proposal = await bench.PrepareAsync(bench.Request());

        var report = await bench.AuditAsync(proposal);

        Assert.Equal((ReviewDisposition.NeedsHuman, false, true), (report.Disposition, report.IsTrustworthy, report.Corrected));
        Assert.NotEmpty(report.ValidationIssues);
        Assert.False(Payload<ProjectAuditFinishedPayload>(bench.EntriesOf(report.Audit)[^1]).Trustworthy);
    }

    [Fact]
    public async Task A_judge_the_factory_gives_on_a_producer_vendor_is_not_launched_and_the_audit_closes_with_an_alert()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        bench.IdentityOf = launch => new JudgeIdentity(launch.Agent.Id, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"));
        var proposal = await bench.PrepareAsync(bench.Request());

        var report = await bench.AuditAsync(proposal);

        Assert.Empty(bench.Judge!.Evaluated);
        AssertSystemEscalation(report);
        var unit = bench.EntriesOf(report.Audit);
        var alert = Payload<AuditAlertPayload>(Assert.Single(unit, e => e.Kind == DecisionKind.AuditAlertRaised));
        Assert.Equal(ProjectAuditService.JudgeNotIndependentAlert, alert.Code);
        Assert.Equal(DecisionKind.ProjectAuditFinished, unit[^1].Kind);
        Assert.Empty(Payload<ProjectAuditFinishedPayload>(unit[^1]).Verdicts);
    }

    [Fact]
    public async Task When_the_commit_cannot_be_copied_the_verification_still_runs_and_no_judge_is_launched()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        bench.Repository.ArchiveFailure = new IOException("disco lleno");
        var proposal = await bench.PrepareAsync(bench.Request());

        var report = await bench.AuditAsync(proposal);

        Assert.Single(bench.Verifier.Calls);
        Assert.Equal(2, report.Verification.Count);
        Assert.Empty(bench.Judges.Created);
        AssertSystemEscalation(report);
        Assert.Contains("disco lleno", report.Verdict.Summary, StringComparison.Ordinal);
        Assert.Equal(DecisionKind.ProjectAuditFinished, bench.EntriesOf(report.Audit)[^1].Kind);
        Assert.False(Directory.Exists(bench.Repository.Archives[0].Destination));
    }

    [Fact]
    public async Task An_audit_that_throws_after_it_started_is_still_closed_in_the_log()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        bench.Verdict = (_, _) => throw new InvalidOperationException("se cayó el juez");
        var proposal = await bench.PrepareAsync(bench.Request());

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => bench.AuditAsync(proposal));

        var started = bench.Entries.Last(e => e.Kind == DecisionKind.ProjectAuditStarted);
        var audit = Payload<ProjectAuditStartedPayload>(started).RunId;
        var finished = Payload<ProjectAuditFinishedPayload>(bench.Entries[^1]);
        Assert.Equal((audit, ReviewDisposition.NeedsHuman, false), (finished.RunId, finished.Disposition, finished.Trustworthy));
        Assert.Contains("se cayó el juez", finished.Summary, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(bench.Options.SnapshotRoot, AuditWorld.Project, audit)));
    }

    [Fact]
    public async Task A_judge_that_hits_its_usage_limit_closes_the_audit_as_rate_limited()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        bench.Verdict = (_, _) => throw new JudgeRunException("límite de uso", AgentRunStatus.RateLimited);
        var proposal = await bench.PrepareAsync(bench.Request());

        var report = await bench.AuditAsync(proposal);

        Assert.Equal(ReviewDisposition.RateLimited, report.Disposition);
        Assert.Null(report.VerdictId);
        Assert.Equal(ReviewDisposition.RateLimited, Payload<ProjectAuditFinishedPayload>(bench.EntriesOf(report.Audit)[^1]).Disposition);
    }

    [Theory]
    [InlineData("requirements")]
    [InlineData("profile")]
    [InlineData("repository")]
    [InlineData("judge")]
    [InlineData("role")]
    public async Task Without_its_material_the_audit_cannot_judge_and_says_why(string missing)
    {
        using var bench = await FreshProjectAsync();
        var request = bench.Request();
        switch (missing)
        {
            case "requirements":
                request = request with { RequirementSetHash = new string('c', 64) };
                break;
            case "profile":
                bench.Profiles.Profile = null;
                break;
            case "repository":
                bench.Repository.InspectFailure = new InvalidOperationException("not a git repository");
                break;
            case "judge":
                request = request with { Judge = new AgentId("nadie") };
                break;
            case "role":
                request = request with { Judge = Executor };
                break;
        }

        var proposal = await bench.PrepareAsync(request);

        Assert.False(proposal.CanJudge);
        Assert.NotEmpty(proposal.Problems);
        Assert.Empty(bench.Entries);
    }

    [Fact]
    public async Task Preparing_writes_nothing_and_gives_the_same_hash_for_the_same_material()
    {
        using var bench = await ProjectWithClaudeRunAsync();
        var before = bench.Entries.Count;

        var first = await bench.PrepareAsync(bench.Request());
        var second = await bench.PrepareAsync(bench.Request());

        Assert.Equal(first.HeaderHash, second.HeaderHash);
        Assert.Equal(before, bench.Entries.Count);
        Assert.Equal(MetricOrigin.EstimatedBySystem, first.EstimatedTokens.Origin);
        Assert.Equal(ProjectAuditService.EstimatedJudgeTokens, first.EstimatedTokens.Value);
        Assert.Contains("estimado por Agent Studio", first.ApprovalText, StringComparison.Ordinal);
    }
}
