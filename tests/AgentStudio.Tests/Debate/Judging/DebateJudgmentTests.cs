using System.IO;
using System.Text.Json.Nodes;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Debate.Judging;
using AgentStudio.Debate.Judging.Debates;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Application;
using AgentStudio.Tests.Debate.Fakes;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Debate.Judging;

/// <summary>
/// docs/15 F5-4 (D-23 to D-26): the judge of Phase 3 judges a debate with its brief; a valid verdict decides, an invalid one
/// gets one correction, and a person decides when it stays invalid, never comes or a panel disagrees. The judges of a panel
/// read one dossier at one head, at the same time, the second with the proposals reversed; a judge is the one of the header
/// by agent, CLI and vendor, never by model.
/// </summary>
public sealed class DebateJudgmentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DebateJudgment Judgment(DebateWorld world, IAgentJudgeFactory judges, DebateDossierBuilder? builder = null) =>
        new(world.World.Recorder, builder ?? world.Builder, judges, world.World.Verdicts, world.World.Evidence, world.World.Clock);

    private static Task<DebateJudgmentResult> JudgeAsync(DebateWorld world, IAgentJudgeFactory judges, RunId? debate = null, IProgress<AgentEvent>? progress = null) =>
        Judgment(world, judges).JudgeAsync(world.World.ProjectContext, debate ?? DebateWorld.Debate, progress, Ct);

    /// <summary>
    /// Judges scripted from the dossier they get, one per launch, whose sessions report a model other than the configured one
    /// (as Claude reports its full name): the identity is that of the launch, as <see cref="AgentJudge"/> sets it.
    /// </summary>
    private static (FakeJudgeFactory Factory, List<ScriptedJudge> Judges) Scripted(
        Func<JudgeLaunch, JudgeIdentity, JudgeDossier, Verdict> evaluate,
        Func<JudgeLaunch, JudgeIdentity, JudgeDossier, Verdict>? revise = null,
        Exception? reviseFailure = null)
    {
        var judges = new List<ScriptedJudge>();
        var factory = new FakeJudgeFactory(launch =>
        {
            var identity = new JudgeIdentity(launch.Agent.Id, launch.Agent.Model with { Model = launch.Agent.Model.Model + "-2026-10-01" });
            var judge = new ScriptedJudge(identity, d => evaluate(launch, identity, d), revise is null ? null : (d, _, _) => revise(launch, identity, d)) { ReviseFailure = reviseFailure };
            judges.Add(judge);
            return judge;
        });
        return (factory, judges);
    }

    private static IReadOnlyList<EvidenceId> Contributions(JudgeDossier dossier) =>
        [.. dossier.Evidence.Where(e => e.Origin == EvidenceOrigin.ReportedByAgent).Select(e => e.Id)];

    private static Verdict Choosing(JudgeIdentity judge, JudgeDossier dossier, string? chosen, VerdictDecision decision = VerdictDecision.Approved, ConfidenceLevel confidence = ConfidenceLevel.High) =>
        DebateWorld.Choosing(dossier, judge, chosen, Contributions(dossier), decision, confidence);

    private static Verdict WrongHead(JudgeIdentity judge, JudgeDossier dossier) => Choosing(judge, dossier, "P1") with { DecisionLogHeadHash = new string('9', 64) };

    /// <summary>The one alert a judgment left in the debate, so a person can see why it needs one.</summary>
    private static AuditAlertPayload Alert(DebateWorld world, string code, string judgeTask = "juez-juez-revision")
    {
        var alert = Assert.Single(Payloads<AuditAlertPayload>(world, DecisionKind.AuditAlertRaised));
        Assert.Equal((code, DebateWorld.Debate.Value, judgeTask), (alert.Code, alert.RunId, Assert.Single(alert.Tasks).Value));
        return alert;
    }

    private static IReadOnlyList<T> Payloads<T>(DebateWorld world, DecisionKind kind)
        where T : DecisionPayload =>
        [.. world.World.LogStore.Of(world.Project).Where(e => e.Kind == kind).Select(e => DecisionPayloadSerializer.Deserialize<T>(e.PayloadJson))];

    // ---- one judge ----

    [Fact]
    public async Task A_valid_verdict_at_the_first_attempt_decides_the_debate_and_is_kept_in_the_debate()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.OneVendor());
        var (factory, judges) = Scripted((_, judge, d) => Choosing(judge, d, "P1"));

        var result = await JudgeAsync(world, factory);

        var judged = Assert.Single(result.Verdicts);
        Assert.Equal((false, 0), (judged.Corrected, judged.Violations.Count));
        Assert.Equal((DebateOutcome.Resolved, VerdictDecision.Approved, "P1", JudgingPanel.Single, ConfidenceLevel.High), (result.Outcome.Outcome, result.Outcome.Decision, result.Outcome.ChosenProposal, result.Outcome.Rule, result.Outcome.Confidence));
        var stored = Assert.Single(world.World.Verdicts.Stored);
        Assert.Equal((judged.Id, DebateWorld.Debate.Value, "juez-juez-revision", AuditScope.Debate, true, (string?)null), (stored.Id, stored.RunId, stored.TaskId, stored.Scope, stored.Valid, stored.Supersedes));
        Assert.Same(judges[0].Given[0], stored.Verdict);
        var issued = Assert.Single(Payloads<VerdictIssuedPayload>(world, DecisionKind.VerdictIssued));
        var dossier = judges[0].Evaluated[0];
        Assert.Equal((DebateWorld.Debate.Value, dossier.DecisionLogHeadHash, dossier.Requirements.ContentHash), (issued.RunId, issued.DecisionLogHeadHash, issued.RequirementSetHash));
        Assert.Contains("disposición IndependentJudge", issued.VendorNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_judgment_of_a_debate_reads_the_whole_log_before_anything_is_written()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.OneVendor());
        var (factory, _) = Scripted((_, judge, d) => Choosing(judge, d, "P1"));
        var spy = new LoadSpyRecorder(world.World.Recorder);
        var judgment = new DebateJudgment(spy, world.Builder, factory, world.World.Verdicts, world.World.Evidence, world.World.Clock);

        var result = await judgment.JudgeAsync(world.World.ProjectContext, DebateWorld.Debate, null, Ct);

        // The verdict of a debate is a critical moment (ADR-0010, addendum of 2026-11-04), as its dossier is.
        Assert.Single(result.Verdicts);
        Assert.Equal([LogLoad.Full], spy.Loads);
        Assert.Equal(LogLoad.Full.ToString(), spy.Calls[0]);
    }

    [Fact]
    public async Task An_invalid_verdict_gets_one_correction_that_supersedes_it()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.OneVendor());
        var (factory, judges) = Scripted((_, judge, d) => WrongHead(judge, d), (_, judge, d) => Choosing(judge, d, "P2"));

        var result = await JudgeAsync(world, factory);

        var judged = Assert.Single(result.Verdicts);
        Assert.True(judged.Corrected);
        Assert.Empty(judged.Violations);
        Assert.Equal([VerdictViolationCode.DecisionLogMismatch], Assert.Single(judges[0].Revisions).Select(v => v.Code));
        Assert.Equal(2, world.World.Verdicts.Stored.Count);
        var (first, second) = (world.World.Verdicts.Stored[0], world.World.Verdicts.Stored[1]);
        Assert.Equal((false, (string?)null), (first.Valid, first.Supersedes));
        Assert.Equal((true, first.Id, judged.Id), (second.Valid, second.Supersedes, second.Id));
        Assert.Equal((JudgingPanel.Single, "P2"), (result.Outcome.Rule, result.Outcome.ChosenProposal));
    }

    [Fact]
    public async Task A_verdict_still_invalid_after_its_correction_needs_a_person()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.OneVendor());
        var (factory, _) = Scripted((_, judge, d) => WrongHead(judge, d), (_, judge, d) => WrongHead(judge, d));

        var result = await JudgeAsync(world, factory);

        var judged = Assert.Single(result.Verdicts);
        Assert.True(judged.Corrected);
        Assert.Contains(judged.Violations, v => v.Code == VerdictViolationCode.DecisionLogMismatch);
        Assert.True(result.Outcome.NeedsHuman);
        Assert.Equal((JudgingPanel.Invalid, DebateOutcome.EscalatedToUser, (VerdictDecision?)null), (result.Outcome.Rule, result.Outcome.Outcome, result.Outcome.Decision));
    }

    [Fact]
    public async Task A_judge_that_gives_no_verdict_needs_a_person_and_nothing_is_stored_as_a_verdict()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.OneVendor());
        var (factory, _) = Scripted((_, _, _) => throw new JudgeRunException("The judge's run ended Failed: boom.", AgentRunStatus.Failed));

        var result = await JudgeAsync(world, factory);

        Assert.Empty(result.Verdicts);
        Assert.Equal((JudgingPanel.Invalid, true), (result.Outcome.Rule, result.Outcome.NeedsHuman));
        Assert.Empty(world.World.Verdicts.Stored);
        Assert.Contains("boom", Alert(world, DebateJudgment.NoVerdictAlert).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_that_is_not_a_verdict_needs_a_person()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.OneVendor());
        var (factory, _) = Scripted((_, _, _) => throw new JudgeOutputException("The judge's answer is a JSON Array, not an object."));

        var result = await JudgeAsync(world, factory);

        Assert.Empty(result.Verdicts);
        Assert.True(result.Outcome.NeedsHuman);
        Assert.Contains("JSON Array", Alert(world, DebateJudgment.NoVerdictAlert).Message, StringComparison.Ordinal);
    }

    /// <summary>D-22 and D-26: nothing goes on in silence, not even a launch that could not be recorded and so never happened.</summary>
    [Fact]
    public async Task A_launch_of_the_judge_that_cannot_be_recorded_does_not_happen_and_the_alert_says_why()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.OneVendor());
        var codex = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, (request, _) => DebateAnswers.Run(DebateAnswers.Choosing(request.Prompt, "P1"), "thread-1", null));
        world.World.LogStore.FailBeforeStoring = 1; // AgentRunStarted of the judge

        var result = await JudgeAsync(world, new AgentJudgeFactory(new FakeProjectAgentProviders(codex), world.World.Clock));

        Assert.Empty(codex.Requests);
        Assert.Empty(result.Verdicts);
        Assert.True(result.Outcome.NeedsHuman);
        Assert.Contains("disk full", Alert(world, DebateJudgment.NoVerdictAlert).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_correction_that_cannot_be_had_leaves_the_first_verdict_with_its_issues()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.OneVendor());
        var (factory, judges) = Scripted((_, judge, d) => WrongHead(judge, d), reviseFailure: new JudgeRunException("The judge's run ended LimitReached.", AgentRunStatus.LimitReached));

        var result = await JudgeAsync(world, factory);

        var judged = Assert.Single(result.Verdicts);
        Assert.False(judged.Corrected);
        Assert.Same(judges[0].Given[0], judged.Verdict);
        Assert.Contains(judged.Violations, v => v.Code == VerdictViolationCode.DecisionLogMismatch);
        Assert.Single(world.World.Verdicts.Stored);
        Assert.True(result.Outcome.NeedsHuman);
        var alert = Alert(world, DebateJudgment.NoCorrectionAlert);
        Assert.Contains("LimitReached", alert.Message, StringComparison.Ordinal);
        Assert.Contains(judged.Id, alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_judge_that_hits_the_usage_limit_needs_a_person_and_the_signal_reaches_the_caller()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.OneVendor());
        var resets = T0.AddMinutes(20);
        var codex = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, (_, _) =>
            new ScriptedRun(new AgentRunOutcome(AgentRunStatus.RateLimited, 1, null, null, "thread-9", "usage limit"), new RateLimitSignaled(T0, "usage limit", resets)));
        var progress = new ListProgress<AgentEvent>();

        var result = await JudgeAsync(world, new AgentJudgeFactory(new FakeProjectAgentProviders(codex), world.World.Clock), progress: progress);

        Assert.Empty(result.Verdicts);
        Assert.True(result.Outcome.NeedsHuman);
        Assert.Contains(progress.Items, e => e is RateLimitSignaled { ResetsAt: var at } && at == resets);
        var finished = Payloads<AgentRunFinishedPayload>(world, DecisionKind.AgentRunFinished).Last();
        Assert.Equal((AttemptOutcome.RateLimited, "juez-juez-revision", DebateWorld.Debate.Value), (finished.Status, finished.Task!.Value.Value, finished.RunId));
        Assert.Contains("RateLimited", Alert(world, DebateJudgment.NoVerdictAlert).Message, StringComparison.Ordinal);
    }

    // ---- the panel ----

    [Fact]
    public async Task A_cross_vendor_panel_that_agrees_decides_with_the_lower_confidence()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.MixedPanel());
        var (factory, _) = Scripted((launch, judge, d) => Choosing(judge, d, "P2", confidence: launch.Agent.Id == DebateWorld.ClaudeJudge.Id ? ConfidenceLevel.High : ConfidenceLevel.Medium));

        var result = await JudgeAsync(world, factory);

        Assert.Equal(2, result.Verdicts.Count);
        Assert.Equal((DebateOutcome.Resolved, VerdictDecision.Approved, "P2", JudgingPanel.Agree, ConfidenceLevel.Medium), (result.Outcome.Outcome, result.Outcome.Decision, result.Outcome.ChosenProposal, result.Outcome.Rule, result.Outcome.Confidence));
        Assert.Equal(["juez-juez-claude", "juez-juez-revision"], world.World.Verdicts.Stored.Select(v => v.TaskId!).Order(StringComparer.Ordinal));
        Assert.All(world.World.Verdicts.Stored, v => Assert.Equal((DebateWorld.Debate.Value, (string?)null), (v.RunId, v.Supersedes)));
    }

    [Theory]
    [InlineData("decision")]
    [InlineData("proposal")]
    [InlineData("invalid")]
    public async Task A_panel_that_disagrees_or_has_an_invalid_verdict_is_unresolved_for_a_person(string how)
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.MixedPanel());
        Verdict Second(JudgeIdentity judge, JudgeDossier d) => how switch
        {
            "decision" => Choosing(judge, d, null, VerdictDecision.Rejected),
            "proposal" => Choosing(judge, d, "P1"),
            _ => Choosing(judge, d, "P7"),
        };
        var (factory, _) = Scripted(
            (launch, judge, d) => launch.Agent.Id == DebateWorld.ClaudeJudge.Id ? Choosing(judge, d, "P2") : Second(judge, d),
            (launch, judge, d) => launch.Agent.Id == DebateWorld.ClaudeJudge.Id ? Choosing(judge, d, "P2") : Second(judge, d));

        var result = await JudgeAsync(world, factory);

        Assert.Equal(2, result.Verdicts.Count);
        Assert.Equal((DebateOutcome.Unresolved, JudgingPanel.Disagree, (VerdictDecision?)null, (string?)null), (result.Outcome.Outcome, result.Outcome.Rule, result.Outcome.Decision, result.Outcome.ChosenProposal));
        Assert.True(result.Outcome.NeedsHuman);
    }

    [Fact]
    public async Task A_panel_with_a_judge_that_gave_no_verdict_is_an_invalid_set()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.MixedPanel());
        var (factory, _) = Scripted((launch, judge, d) => launch.Agent.Id == DebateWorld.ClaudeJudge.Id
            ? Choosing(judge, d, "P2")
            : throw new JudgeRunException("The judge's run ended Failed.", AgentRunStatus.Failed));

        var result = await JudgeAsync(world, factory);

        Assert.Single(result.Verdicts);
        Assert.Equal((JudgingPanel.Invalid, DebateOutcome.EscalatedToUser), (result.Outcome.Rule, result.Outcome.Outcome));
        Alert(world, DebateJudgment.NoVerdictAlert); // the judge that gave none, and only it
    }

    [Fact]
    public async Task The_two_judges_of_a_panel_run_at_the_same_time_on_one_dossier_the_second_with_the_proposals_reversed()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.MixedPanel());
        var barrier = new AsyncBarrier(2);
        var judges = new List<GatedJudge>();
        var factory = new FakeJudgeFactory(launch =>
        {
            var judge = new GatedJudge(new JudgeIdentity(launch.Agent.Id, launch.Agent.Model), barrier, (d, identity) => Choosing(identity, d, "P1"));
            judges.Add(judge);
            return judge;
        });

        var result = await JudgeAsync(world, factory);

        Assert.Equal(2, result.Verdicts.Count);
        Assert.Equal(JudgingPanel.Agree, result.Outcome.Rule);
        Assert.Same(judges[0].Evaluated[0], judges[1].Evaluated[0]);
        var briefs = factory.Created.Select(c => Assert.IsType<DebateJudgeBrief>(c.Launch.Brief)).ToList();
        Assert.Equal([0, 1], briefs.Select(b => b.Seat));
        Assert.Equal(["P1", "P2"], briefs[0].Proposals);
        Assert.Equal(["P2", "P1"], briefs[1].Proposals);
        var prompts = briefs.Select(b => b.Evaluation(judges[0].Evaluated[0], b.Package.Files, b.Package.Files.Folder)).ToList();
        Assert.Equal(["P1", "P2"], DebateAnswers.Proposals(prompts[0]));
        Assert.Equal(["P2", "P1"], DebateAnswers.Proposals(prompts[1]));
        Assert.Contains(DebateDossierBuilder.ReversedDossierFile, prompts[1], StringComparison.Ordinal);
        Assert.Equal(DebateAnswers.Dossier(prompts[0]).GetProperty("decisionLogHeadHash").GetString(), DebateAnswers.Dossier(prompts[1]).GetProperty("decisionLogHeadHash").GetString());
    }

    // ---- who judges ----

    /// <summary>
    /// D-26: the CLI announces the full name of the model (<c>claude-haiku-4-5-…</c>) while the header says <c>haiku</c>; the
    /// judge is the same agent on the same CLI and vendor, so its verdicts combine and close the debate.
    /// </summary>
    [Fact]
    public async Task A_judge_whose_session_reports_the_full_name_of_its_model_is_still_the_judge_of_the_header()
    {
        using var world = await DebateWorld.CreateAsync();
        var header = await world.ChoiceDebateAsync(DebateWorld.MixedPanel());
        var claude = new ScriptedAgentProvider(ProviderId.ClaudeCode, VendorId.Anthropic, (request, _) => DebateAnswers.Run(DebateAnswers.Choosing(request.Prompt, "P2"), "sess-1", "claude-haiku-4-5-20251001"));
        var codex = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, (request, _) => DebateAnswers.Run(DebateAnswers.Choosing(request.Prompt, "P2", confidence: "Medium"), "thread-1", null));

        var result = await JudgeAsync(world, new AgentJudgeFactory(new FakeProjectAgentProviders(claude, codex), world.World.Clock));

        Assert.Equal(JudgingPanel.Agree, result.Outcome.Rule);
        Assert.Equal(["claude-haiku-4-5-20251001", "gpt-5"], result.Verdicts.Select(v => v.Verdict.Judge.Model.Model));
        Assert.All(result.Verdicts, v => Assert.Empty(v.Violations));
        var session = DebateSession.Open(new DebateId(header.Id.Value), header.Mode, header.Subject.Kind, "pregunta", world.Frozen,
            DebateDossierBuilder.Participants(header), DebateDossierBuilder.Arrangement(header), header.Limits, header.CreatedAt);
        session.Submit(new DebateContribution(1, DebateWorld.Architect.Id, ContributionKind.Proposal, "C1", [], header.CreatedAt));
        session.Submit(new DebateContribution(1, DebateWorld.Critic.Id, ContributionKind.Proposal, "C2", [], header.CreatedAt));
        session.RequestJudgment();
        session.Close(result.Outcome, [.. result.Verdicts.Select(v => v.Verdict)]);
        Assert.Equal((DebatePhase.Closed, DebateOutcome.Resolved), (session.Phase, session.Outcome));
    }

    [Fact]
    public async Task A_judge_that_runs_on_another_vendor_than_its_definition_is_not_the_judge_of_the_debate()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.OneVendor());
        var impostor = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.Google, (request, _) => DebateAnswers.Run(DebateAnswers.Choosing(request.Prompt, "P1"), "thread-1", "gemini"));

        var result = await JudgeAsync(world, new AgentJudgeFactory(new FakeProjectAgentProviders(impostor), world.World.Clock));

        var judged = Assert.Single(result.Verdicts);
        Assert.Equal(VendorId.Google, judged.Verdict.Judge.Model.Vendor);
        Assert.Equal((JudgingPanel.Invalid, true), (result.Outcome.Rule, result.Outcome.NeedsHuman));
    }

    [Fact]
    public async Task No_judge_is_launched_on_a_header_rewritten_after_the_debate_opened()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.OneVendor());
        world.Debates.Replace(DebateWorld.Debate, h => h with { Judges = [h.Judges[0] with { AllowedTools = ["Read", "Bash"] }] });
        var (factory, _) = Scripted((_, judge, d) => Choosing(judge, d, "P1"));

        var result = await JudgeAsync(world, factory);

        Assert.Empty(factory.Created);
        Assert.Empty(result.Verdicts);
        Assert.Equal(JudgingPanel.Invalid, result.Outcome.Rule);
        var alert = Assert.Single(Payloads<AuditAlertPayload>(world, DecisionKind.AuditAlertRaised));
        Assert.Equal((DebateJudgment.HeaderMismatchAlert, DebateWorld.Debate.Value), (alert.Code, alert.RunId));
    }

    [Fact]
    public async Task No_judge_is_launched_and_nothing_is_written_when_the_log_does_not_verify()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.OneVendor());
        var entries = world.World.LogStore.Of(world.Project);
        world.World.LogStore.Tamper(world.Project, entries[3].Sequence, e => e with { Summary = "reescrita" });
        var (factory, _) = Scripted((_, judge, d) => Choosing(judge, d, "P1"));

        var result = await JudgeAsync(world, factory);

        Assert.Empty(factory.Created);
        Assert.Empty(result.Verdicts);
        Assert.Equal(JudgingPanel.Invalid, result.Outcome.Rule);
        Assert.Equal(entries.Count, world.World.LogStore.Of(world.Project).Count);
        Assert.False(Directory.Exists(Path.Combine(world.World.DossierRoot, world.Project, DebateWorld.Debate.Value)));
    }

    /// <summary>Rule P is never waived (ADR-0027): the producers of the run exclude a judge of their vendor even when the header forgot them.</summary>
    [Fact]
    public async Task A_judge_of_a_vendor_that_produced_the_run_is_never_launched()
    {
        using var world = await DebateWorld.CreateAsync();
        var spec = new DebateSpec
        {
            Mode = DebateMode.ImplementationAudit,
            Subject = new DebateSubject(DebateSubjectKind.Run, JudgingWorld.Run.Value),
            Requirements = world.World.Frozen,
            Participants = [new Seat(DebateWorld.Reviewer, DebateRole.Proponent, "P1"), new Seat(DebateWorld.Critic, DebateRole.Critic, "P2")],
            Judges = [DebateWorld.ClaudeJudge],
        };
        var header = await world.OpenAsync(spec);
        await world.RoundAsync(header, 1, [new DebateWorld.Turn(spec.Participants[0], ContributionKind.Proposal, "Cumple"), new DebateWorld.Turn(spec.Participants[1], ContributionKind.Proposal, "No cumple")],
            DebatePhase.AwaitingJudgment, DebateJudgmentReason.Requested);
        var claude = new ScriptedAgentProvider(ProviderId.ClaudeCode, VendorId.Anthropic, (request, _) => DebateAnswers.Run(DebateAnswers.Choosing(request.Prompt, null), "sess-1", "haiku"));

        var result = await JudgeAsync(world, new AgentJudgeFactory(new FakeProjectAgentProviders(claude), world.World.Clock));

        Assert.Empty(claude.Requests);
        Assert.Empty(result.Verdicts);
        var alert = Assert.Single(Payloads<AuditAlertPayload>(world, DecisionKind.AuditAlertRaised));
        Assert.Equal(DebateJudgment.JudgeNotIndependentAlert, alert.Code);
        Assert.Contains("anthropic", alert.Message, StringComparison.Ordinal);
    }

    // ---- how the judge is launched ----

    [Fact]
    public async Task The_judge_is_launched_read_only_in_the_package_with_the_brief_and_the_schema_of_the_debate()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.OneVendor());
        var codex = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, (request, _) => DebateAnswers.Run(DebateAnswers.Choosing(request.Prompt, "P1"), "thread-1", null));

        var result = await JudgeAsync(world, new AgentJudgeFactory(new FakeProjectAgentProviders(codex), world.World.Clock));

        Assert.Equal(JudgingPanel.Single, result.Outcome.Rule);
        var request = Assert.Single(codex.Requests);
        var folder = Path.Combine(world.World.DossierRoot, world.Project, DebateWorld.Debate.Value);
        Assert.StartsWith(folder, request.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((false, false, false), (request.Permissions.AllowFileWrites, request.Permissions.AllowShell, request.Permissions.AllowNetwork));
        Assert.Equal([request.WorkingDirectory], request.Permissions.AdditionalDirectories);
        Assert.Contains("Bash", request.Permissions.DeniedTools);
        Assert.Equal((true, (string?)null), (request.PersistSession, request.ResumeSessionId));
        Assert.Equal((DebateWorld.Debate.Value, "juez-juez-revision", $"Juicio del debate {DebateWorld.Debate}"), (request.DebateId, request.TaskId, request.Purpose));
        Assert.Equal(["#juicio"], request.Tags!);
        Assert.Equal(DebateVerdictSchema.Build(world.Frozen, ["P1", "P2"]), request.OutputJsonSchema);
        Assert.Contains("There is no copy of the code", request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_launches_of_the_judge_are_recorded_in_the_debate_with_its_task_and_purpose_and_without_memory()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.OneVendor());
        var codex = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, (request, _) => DebateAnswers.Run(DebateAnswers.Choosing(request.Prompt, "P1"), "thread-1", null));

        await JudgeAsync(world, new AgentJudgeFactory(new FakeProjectAgentProviders(codex), world.World.Clock));

        var start = Payloads<AgentRunStartedPayload>(world, DecisionKind.AgentRunStarted).Single(s => s.Purpose == AgentRunPurpose.DebateJudge);
        Assert.Equal((DebateWorld.Debate.Value, "juez-juez-revision", 1, DebateWorld.CodexJudge.Id), (start.RunId, start.Task!.Value.Value, start.Attempt, start.Agent));
        Assert.Null(start.ContextPack);
        var end = Payloads<AgentRunFinishedPayload>(world, DecisionKind.AgentRunFinished).Last();
        Assert.Equal((AttemptOutcome.Succeeded, "juez-juez-revision"), (end.Status, end.Task!.Value.Value));
        Assert.NotNull(end.ReportEvidenceId);
    }

    [Fact]
    public async Task The_judges_work_in_a_copy_of_the_commit_inside_the_package_when_there_is_one()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.OneVendor() with { Subject = new DebateSubject(DebateSubjectKind.Commit, CommitA), SnapshotCommit = CommitA });
        var git = new CopyingWorktreeManager();
        var builder = new DebateDossierBuilder(world.World.Recorder, world.Debates, world.World.Requirements, world.World.Evidence, world.World.Verdicts, DebateWorld.RunBuilder(world.World), new JudgingOptions(world.World.DossierRoot), git);
        var codex = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, (request, _) => DebateAnswers.Run(DebateAnswers.Choosing(request.Prompt, "P1"), "thread-1", null));

        var result = await Judgment(world, new AgentJudgeFactory(new FakeProjectAgentProviders(codex), world.World.Clock), builder).JudgeAsync(world.World.ProjectContext, DebateWorld.Debate, null, Ct);

        Assert.Equal(JudgingPanel.Single, result.Outcome.Rule);
        var (repo, commit, destination) = Assert.Single(git.Copies);
        Assert.Equal((world.World.ProjectContext.RootPath, CommitA), (repo, commit));
        var request = Assert.Single(codex.Requests);
        Assert.Equal(destination, request.WorkingDirectory);
        Assert.Equal(DebateDossierBuilder.SnapshotFolder, Path.GetFileName(destination));
        Assert.Contains(Path.GetDirectoryName(destination)!, request.Permissions.AdditionalDirectories);
        Assert.Contains($"Copy of commit {CommitA}", request.Prompt, StringComparison.Ordinal);
        Assert.Equal("P1", result.Verdicts[0].Verdict.Resolution!.ChosenProposal);
    }

    [Fact]
    public async Task A_copy_of_the_commit_that_fails_leaves_the_judges_in_the_package_and_the_prompt_says_so()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.OneVendor() with { Subject = new DebateSubject(DebateSubjectKind.Commit, CommitA), SnapshotCommit = CommitA });
        var git = new CopyingWorktreeManager { Failure = new InvalidDataException("a link in the tree") };
        var builder = new DebateDossierBuilder(world.World.Recorder, world.Debates, world.World.Requirements, world.World.Evidence, world.World.Verdicts, DebateWorld.RunBuilder(world.World), new JudgingOptions(world.World.DossierRoot), git);
        var codex = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, (request, _) => DebateAnswers.Run(DebateAnswers.Choosing(request.Prompt, "P1"), "thread-1", null));

        await Judgment(world, new AgentJudgeFactory(new FakeProjectAgentProviders(codex), world.World.Clock), builder).JudgeAsync(world.World.ProjectContext, DebateWorld.Debate, null, Ct);

        var request = Assert.Single(codex.Requests);
        Assert.NotEqual(DebateDossierBuilder.SnapshotFolder, Path.GetFileName(request.WorkingDirectory));
        Assert.False(Directory.Exists(Path.Combine(request.WorkingDirectory, DebateDossierBuilder.SnapshotFolder)));
        Assert.Contains("There is no copy of the code", request.Prompt, StringComparison.Ordinal);
    }

    // ---- the rules of the validator by family ----

    /// <summary>D-25: a requirement that is the judge's own assessment rests on the contributions only where the debate chooses.</summary>
    [Fact]
    public async Task The_exemption_of_the_judges_own_assessment_holds_only_in_a_debate_that_chooses()
    {
        using var choosing = await DebateWorld.CreateAsync();
        await choosing.ChoiceDebateAsync(DebateWorld.OneVendor());
        var (factory, _) = Scripted((_, judge, d) => Choosing(judge, d, "P1"));

        var chosen = await JudgeAsync(choosing, factory);

        Assert.Empty(Assert.Single(chosen.Verdicts).Violations);

        using var reviewing = await DebateWorld.CreateAsync();
        var spec = DebateWorld.OneVendor() with { Mode = DebateMode.ImplementationAudit, Subject = new DebateSubject(DebateSubjectKind.Run, JudgingWorld.Run.Value), ProducerVendors = [VendorId.Anthropic] };
        var header = await reviewing.OpenAsync(spec);
        await reviewing.RoundAsync(header, 1, [new DebateWorld.Turn(spec.Participants[0], ContributionKind.Proposal, "Cumple"), new DebateWorld.Turn(spec.Participants[1], ContributionKind.Proposal, "No cumple")],
            DebatePhase.AwaitingJudgment, DebateJudgmentReason.Requested);
        var (reviewFactory, _) = Scripted((_, judge, d) => Choosing(judge, d, null), (_, judge, d) => Choosing(judge, d, null));

        var reviewed = await JudgeAsync(reviewing, reviewFactory);

        Assert.Contains(Assert.Single(reviewed.Verdicts).Violations, v => v.Code == VerdictViolationCode.ApprovedOnAgentClaimsOnly);
        Assert.True(reviewed.Outcome.NeedsHuman);
    }

    // ---- revisions (D-40) ----

    [Fact]
    public async Task The_verdicts_of_a_revision_go_to_the_unit_of_the_challenged_verdict_and_the_first_supersedes_it()
    {
        using var world = await DebateWorld.CreateAsync();
        var challenged = await world.RunVerdictAsync();
        await world.RevisionAsync(challenged, world.World.Frozen.ContentHash);
        Verdict Approval(JudgeIdentity judge, JudgeDossier d) => ScriptedJudge.From(JudgeAnswers.Approval(d, world.World.TestEvidence.Id), judge);
        var (factory, _) = Scripted((_, judge, d) => Approval(judge, d) with { DecisionLogHeadHash = new string('9', 64) }, (_, judge, d) => Approval(judge, d));

        var result = await JudgeAsync(world, factory, DebateWorld.Revision);

        Assert.Equal((JudgingPanel.Single, VerdictDecision.Approved), (result.Outcome.Rule, result.Outcome.Decision));
        var revision = world.World.Verdicts.Stored.Where(v => v.Id != challenged.Id).ToList();
        Assert.Equal(2, revision.Count);
        Assert.All(revision, v => Assert.Equal((JudgingWorld.Run.Value, AuditScope.CurrentPlan, "juez-juez-revision"), (v.RunId, v.Scope, v.TaskId)));
        Assert.Equal([challenged.Id, revision[0].Id], revision.Select(v => v.Supersedes));
        var issued = Payloads<VerdictIssuedPayload>(world, DecisionKind.VerdictIssued).Where(i => i.VerdictId != challenged.Id).ToList();
        Assert.All(issued, i => Assert.Equal(JudgingWorld.Run.Value, i.RunId));
        Assert.All(issued, i => Assert.Contains($"revisa el veredicto {challenged.Id}", i.VendorNote, StringComparison.Ordinal));
        var recorder = Assert.IsType<JudgeRunRecorder>(Assert.Single(factory.Created).Launch.Observer);
        Assert.Equal((DebateWorld.Revision.Value, AgentRunPurpose.DebateJudge), (recorder.Scope.Run, recorder.Scope.Purpose)); // its launches count in the revision debate
    }
}
