using System.IO;
using System.Text.Json.Nodes;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Debate;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Usage;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Integration.Phase4;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;
using DebateAnswers = AgentStudio.Tests.Debate.Judging.DebateAnswers;

namespace AgentStudio.Tests.Integration.Phase5;

/// <summary>
/// A whole debate on the real modules (docs/15 F5-1 to F5-4 and D-46, section 9 «Integración»): the arena, the judgment and
/// the memory composed as the app composes them, on SQLite and a temporary git repository, with the two CLIs scripted. Two
/// modes, one of each family: a debate that chooses between the proposals of a participant of Claude and one of Codex,
/// judged by a cross-vendor panel (D-14), and an audit of the implementation of a real run of the engine (produced by
/// Claude) judged by the vendor that did not produce it (D-13). In both: the rounds are sealed (no answer is captured nor
/// announced before every turn of its round ended, and no prompt of round 1 has anything of another participant), the limit
/// of rounds sends the debate to judgment with the tokens the log counted, and the verdict closes it. Then the app closes
/// and opens again: the domain replays the debate from the reopened database into the same state; the memory derives one
/// summary of it; and the usage report shows every turn and every judge.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class DebateOnSqliteTests
{
    private const string ChoiceQuestion = "¿Dónde guardamos la caché de las respuestas del servicio: en memoria o en disco?";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_debate_that_chooses_between_both_vendors_is_judged_by_a_cross_vendor_panel_and_replays_after_a_restart()
    {
        using var repo = Phase5Runs.Repo();
        var paths = Phase4Paths.Beside(repo.Root);
        var head = repo.Head;
        var clis = new Phase5Clis
        {
            // The proposal of P1 quotes two lines of the commit and a file the commit does not have.
            Contribute = (request, answer) =>
            {
                if (request.Agent.Id == Phase5Roster.Architect && request.TaskId == "r1-arquitecta-azul")
                {
                    answer["citations"] = new JsonArray(
                        new JsonObject { ["id"] = "CITA-1", ["path"] = "shared.txt", ["startLine"] = 2, ["endLine"] = 3, ["why"] = "Las líneas que se cachean." },
                        new JsonObject { ["id"] = "CITA-2", ["path"] = "src/NoExiste.cs", ["startLine"] = 1, ["endLine"] = 2, ["why"] = "Un fichero que no está." });
                    answer["claims"] = new JsonArray(new JsonObject { ["claim"] = "Las líneas 2 y 3 se leen en cada petición.", ["support"] = new JsonArray("CITA-1") });
                }
            },
        };

        DebateResult result;
        DebateSnapshot live;
        RunId id;
        string headHash;
        await using (var app = await Phase4App.OpenAsync(repo.Root, Phase5Composition.Services(paths, new ScriptedApprovalPrompt(), clis.Providers), Phase5Roster.Agents()))
        {
            var request = new DebateRequest(
                app.Project,
                DebateMode.Free,
                ChoiceQuestion,
                Phase5Checks.ChoiceRequirements(),
                new DebateSubject(DebateSubjectKind.Commit, null),
                [new DebateParticipantRequest(Phase5Roster.Architect, DebateRole.Proponent), new DebateParticipantRequest(Phase5Roster.GreenCritic, DebateRole.Critic)],
                [Phase5Roster.ClaudeJudge, Phase5Roster.CodexJudge],
                DebateJudgingKind.CrossVendorPanel,
                new DebateLimits(2, TimeSpan.FromMinutes(30), 400_000));
            await using var debate = await Phase5Checks.OpenAsync(app, request);
            result = await debate.Completion.WaitAsync(Phase5Runs.Guard, Ct);
            live = debate.Snapshot();
            id = debate.Id;
            var unit = id.Value;
            Assert.True(result is { Phase: DebatePhase.Closed, Outcome: DebateOutcome.Resolved }, result.Summary + "\n" + Phase4Checks.Describe(await app.LoadLogAsync()));

            // The header: about HEAD, both vendors waived and recorded (ADR-0027), the debate's own id, anchored by its hash.
            Assert.True(DebateIds.IsDebate(unit), unit);
            var record = (await app.Get<IDebateRegistry>().FindAsync(app.Project.ProjectId, id, Ct))!;
            var (_, opened) = Assert.Single(await app.PayloadsAsync<DebateOpenedPayload>(unit));
            Assert.Equal(DebateHeaderHash.Of(record), opened.HeaderHash);
            Assert.Equal((DebateMode.Free, DebateSubjectKind.Commit, head, head), (opened.Mode, opened.Subject, opened.SubjectRef, opened.SnapshotCommit));
            Assert.Equal(DebateJudgingKind.CrossVendorPanel, opened.Judging);
            Assert.Equal([VendorId.Anthropic, VendorId.OpenAI], opened.WaivedParticipantVendors);
            Assert.Empty(opened.ProducerVendors);
            Assert.Equal(["P1:arquitecta-azul:anthropic", "P2:critico-verde:openai"], opened.Participants.Select(p => $"{p.Label}:{p.Agent}:{p.Vendor}"));
            Assert.Equal(["juez-claude", "juez-codex"], opened.Judges.Select(j => j.Agent.Value));
            Assert.Equal((2, 6), (opened.MaxRounds, opened.MaxAgentRuns));

            await AssertSealedRoundsAsync(app, unit, rounds: 2, [Phase5Roster.Architect, Phase5Roster.GreenCritic]);
            await AssertRoundLimitAsync(app, unit, rounds: 2, participants: 2);

            // The quote of P1 is anchored to the commit: the two lines, captured by the system; the missing file is a fact for the judge.
            var first = Assert.Single(await app.PayloadsAsync<DebateContributionRecordedPayload>(unit), p => p.Payload is { Round: 1, Index: 0 }).Payload;
            Assert.Equal(Phase5Roster.Architect, first.Author);
            Assert.Equal(["CITA-2: NotFound"], first.UnresolvedCitations);
            var quote = (await app.Get<IEvidenceStore>().ListByRunAsync(app.Project.ProjectId, unit, Ct)).Single(e => e.Id == Assert.Single(first.Cites));
            Assert.Equal((EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, head), (quote.Kind, quote.Origin, quote.SubjectCommit));
            var lines = await Phase5Checks.TextAsync(app, quote.Id, quote.ContentSha256);
            Assert.Contains("linea 2", lines, StringComparison.Ordinal);
            Assert.Contains("linea 3", lines, StringComparison.Ordinal);
            Assert.DoesNotContain("linea 4", lines, StringComparison.Ordinal);

            // The panel: both judges read one dossier at one head, the second with the proposals reversed, blind to who is who.
            var judges = clis.LaunchesOf(unit).Where(r => r.Agent.Role == AgentRole.Judge).ToList();
            Assert.Equal(["juez-claude", "juez-codex"], judges.Select(r => r.Agent.Id.Value).Order(StringComparer.Ordinal));
            Assert.Equal(["P1,P2", "P2,P1"], judges.Select(r => string.Join(",", DebateAnswers.Proposals(r.Prompt))).Order(StringComparer.Ordinal));
            Assert.Single(judges.Select(r => DebateAnswers.Dossier(r.Prompt).GetProperty("decisionLogHeadHash").GetString()).Distinct());
            Assert.All(judges, r => AssertBlind(r.Prompt));

            // Agreement: the decision and the proposal of both, with the lower confidence (D-14), and both verdicts kept as given.
            var panel = Assert.IsType<PanelOutcome>(result.Panel);
            Assert.Equal((DebateOutcome.Resolved, VerdictDecision.Approved, "P1", JudgingPanel.Agree), (panel.Outcome, panel.Decision, panel.ChosenProposal, panel.Rule));
            var (closedEntry, closed) = Assert.Single(await app.PayloadsAsync<DebateClosedPayload>(unit));
            Assert.Equal((DebateOutcome.Resolved, JudgingPanel.Agree, "P1"), (closed.Outcome, closed.Rule, closed.ChosenProposal));
            var stored = await app.Get<IVerdictStore>().ListAsync(app.Project.ProjectId, unit, Ct);
            Assert.Equal(closed.Verdicts.Order(StringComparer.Ordinal), stored.Select(v => v.Id).Order(StringComparer.Ordinal));
            Assert.All(stored, v => Assert.Equal((true, VerdictDecision.Approved, "P1", AuditScope.Debate, (string?)null), (v.Valid, v.Verdict.Decision, v.Verdict.Resolution?.ChosenProposal, v.Scope, v.Supersedes)));
            Assert.Equal(["juez-juez-claude", "juez-juez-codex"], stored.Select(v => v.TaskId!).Order(StringComparer.Ordinal));
            var issued = await app.PayloadsAsync<VerdictIssuedPayload>(unit);
            Assert.Equal(2, issued.Count);
            Assert.All(issued, i => Assert.Contains("dispensa de la regla de los participantes para anthropic, openai", i.Payload.VendorNote, StringComparison.Ordinal));

            // The copy of the commit is gone once the debate closed.
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(paths.Root, "debates"), "snapshot", SearchOption.AllDirectories));

            await AssertUsageAsync(app, unit, [Phase5Roster.Architect, Phase5Roster.GreenCritic], rounds: 2, judges: 2);
            await AssertSummaryAsync(app, unit, closedEntry.Sequence, ChoiceQuestion, "libre");
            headHash = (await app.LoadLogAsync()).HeadHash;
        }

        await AssertReplaysAfterRestartAsync(repo.Root, paths, id, result, live, headHash, DebateLimitReached.Rounds);
    }

    [Fact]
    public async Task An_audit_of_the_implementation_of_a_real_run_is_judged_by_the_vendor_that_did_not_produce_it_and_replays_after_a_restart()
    {
        using var repo = Phase5Runs.Repo();
        var paths = Phase4Paths.Beside(repo.Root);
        var clis = new Phase5Clis();

        DebateResult result;
        DebateSnapshot live;
        RunId id;
        string headHash;
        await using (var app = await Phase4App.OpenAsync(repo.Root, Phase5Composition.Services(paths, new ScriptedApprovalPrompt(), clis.Providers), Phase5Roster.Agents()))
        {
            // A real run of the engine: Claude planned and executed it, Codex's juez approved its integration.
            var run = await Phase5Runs.SucceededAsync(app);
            var candidate = run.IntegrationCommit!;
            await using var debate = await Phase5Checks.OpenAsync(app, ImplementationAudit(app, run, rounds: 2));
            result = await debate.Completion.WaitAsync(Phase5Runs.Guard, Ct);
            live = debate.Snapshot();
            id = debate.Id;
            var unit = id.Value;
            Assert.True(result is { Phase: DebatePhase.Closed, Outcome: DebateOutcome.Resolved }, result.Summary + "\n" + Phase4Checks.Describe(await app.LoadLogAsync()));

            // Rule P from the run (Anthropic produced it) and rule Q waived only for the vendor that did not (D-12, D-13).
            var (_, opened) = Assert.Single(await app.PayloadsAsync<DebateOpenedPayload>(unit));
            Assert.Equal((DebateSubjectKind.Run, run.Id.Value, candidate), (opened.Subject, opened.SubjectRef, opened.SnapshotCommit));
            Assert.Equal(DebateJudgingKind.NonProducerJudge, opened.Judging);
            Assert.Equal([VendorId.Anthropic], opened.ProducerVendors);
            Assert.Equal([VendorId.OpenAI], opened.WaivedParticipantVendors);
            Assert.Equal(Requirements().ContentHash, opened.RequirementSetHash);

            await AssertSealedRoundsAsync(app, unit, rounds: 2, [Phase5Roster.Architect, Phase5Roster.GreenCritic]);
            await AssertRoundLimitAsync(app, unit, rounds: 2, participants: 2);

            // The judge of Codex judged the run's candidate under the rules of Phase 3: the green test run of that commit.
            var judge = Assert.Single(clis.LaunchesOf(unit), r => r.Agent.Role == AgentRole.Judge);
            Assert.Equal(Phase5Roster.CodexJudge, judge.Agent.Id);
            var dossier = DebateAnswers.Dossier(judge.Prompt);
            Assert.Equal(("review", candidate), (dossier.GetProperty("family").GetString(), dossier.GetProperty("candidateCommit").GetString()));
            Assert.Empty(dossier.GetProperty("proposals").EnumerateArray());
            Assert.Contains(Phase5Answers.SystemTestRuns(dossier), t => t.Commit == candidate);

            // Blind (D-15) in everything the debate adds: the rubric, the participants, the contributions and the protocol. The
            // run it audits goes in whole (ADR-0029, 7), and only there do the agents of the template appear (see the test below).
            AssertBlind(Phase5Checks.OutsideDossier(judge.Prompt));
            Assert.All(BlindTokens.SelectMany(t => Phase5Checks.PathsContaining(dossier, t)), path => Assert.StartsWith("$.subject.run.", path, StringComparison.Ordinal));

            var panel = Assert.IsType<PanelOutcome>(result.Panel);
            Assert.Equal((DebateOutcome.Resolved, VerdictDecision.Approved, JudgingPanel.Single), (panel.Outcome, panel.Decision, panel.Rule));
            var (closedEntry, closed) = Assert.Single(await app.PayloadsAsync<DebateClosedPayload>(unit));
            var verdict = Assert.Single(await app.Get<IVerdictStore>().ListAsync(app.Project.ProjectId, unit, Ct));
            Assert.Equal([verdict.Id], closed.Verdicts);
            Assert.Equal((true, VerdictDecision.Approved, VendorId.OpenAI, AuditScope.Debate), (verdict.Valid, verdict.Verdict.Decision, verdict.Verdict.Judge.Model.Vendor, verdict.Scope));
            var (_, issued) = Assert.Single(await app.PayloadsAsync<VerdictIssuedPayload>(unit));
            Assert.Contains("disposición NonProducerJudge, dispensa de la regla de los participantes para openai; vendors excluidos: anthropic", issued.VendorNote, StringComparison.Ordinal);

            await AssertUsageAsync(app, unit, [Phase5Roster.Architect, Phase5Roster.GreenCritic], rounds: 2, judges: 1);
            await AssertSummaryAsync(app, unit, closedEntry.Sequence, AuditQuestion, "auditoría de implementación");
            headHash = (await app.LoadLogAsync()).HeadHash;
        }

        await AssertReplaysAfterRestartAsync(repo.Root, paths, id, result, live, headHash, DebateLimitReached.Rounds);
    }

    /// <summary>
    /// D-15 says the judge of a debate names each participant by its label and role, never by its agent, CLI, vendor or model.
    /// In an audit of an implementation the run goes in whole (ADR-0029, 7), and its <c>RunStarted</c> lists the template of
    /// agents of the project when it started: in the app the participants of a later debate are agents of that same template,
    /// so the judge reads their ids with their role, CLI and vendor, and the roles of the debate (proponent, critic) let it tell
    /// who is who.
    /// </summary>
    [Fact]
    [Trait("Category", "LocalTools")]
    public async Task The_judge_of_an_implementation_audit_reads_no_agent_of_its_participants()
    {
        using var repo = Phase5Runs.Repo();
        var clis = new Phase5Clis();
        await using var app = await Phase4App.OpenAsync(repo.Root, Phase5Composition.Services(Phase4Paths.Beside(repo.Root), new ScriptedApprovalPrompt(), clis.Providers), Phase5Roster.Agents());
        var run = await Phase5Runs.SucceededAsync(app);
        await using var debate = await Phase5Checks.OpenAsync(app, ImplementationAudit(app, run, rounds: 1));
        var result = await debate.Completion.WaitAsync(Phase5Runs.Guard, Ct);
        Assert.Equal(DebatePhase.Closed, result.Phase);

        var judge = Assert.Single(clis.LaunchesOf(debate.Id.Value), r => r.Agent.Role == AgentRole.Judge);
        var dossier = DebateAnswers.Dossier(judge.Prompt);
        var seen = BlindTokens.SelectMany(t => Phase5Checks.PathsContaining(dossier, t).Select(path => $"{t} en {path}")).ToList();
        Assert.True(seen.Count == 0, "El juez lee la identidad de los participantes:\n" + string.Join("\n", seen));
    }

    private const string AuditQuestion = "¿Cumple la integración de la ejecución los requisitos congelados?";

    /// <summary>The agents and the models of the participants, which no blind dossier may show (D-15).</summary>
    private static IReadOnlyList<string> BlindTokens { get; } = [.. Phase5Roster.Codes.Keys.Select(a => a.Value), .. Phase5Roster.ParticipantModels];

    /// <summary>An audit of the implementation of <paramref name="run"/> (produced by Claude): a participant of each vendor and the judge of Codex, with the waiver of its vendor.</summary>
    private static DebateRequest ImplementationAudit(Phase4App app, RunResult run, int rounds) => new(
        app.Project,
        DebateMode.ImplementationAudit,
        AuditQuestion,
        Requirements(),
        new DebateSubject(DebateSubjectKind.Run, run.Id.Value),
        [new DebateParticipantRequest(Phase5Roster.Architect, DebateRole.Proponent), new DebateParticipantRequest(Phase5Roster.GreenCritic, DebateRole.Critic)],
        [Phase5Roster.CodexJudge],
        DebateJudgingKind.NonProducerJudge,
        new DebateLimits(rounds, TimeSpan.FromMinutes(30), 400_000));

    /// <summary>
    /// F5-2 (D-17) on the log of SQLite: in every round each turn ended (its end carries no answer) before any of its
    /// contributions was captured, announced or published; in round 1 no prompt has the text, nor the id, of another
    /// participant; in later rounds a prompt has what was published before, never what another wrote in the same round.
    /// </summary>
    private static async Task AssertSealedRoundsAsync(Phase4App app, string unit, int rounds, IReadOnlyList<AgentId> participants)
    {
        var entries = (await Phase5Checks.EntriesOfAsync(app, unit))
            .Select(e => (Entry: e, Payload: DecisionPayloadSerializer.Deserialize(e.Kind, e.PayloadJson)))
            .ToList();
        for (var round = 1; round <= rounds; round++)
        {
            var prefix = $"r{round}-";
            var ends = entries.Where(e => e.Payload is AgentRunFinishedPayload { Task: { } task } && task.Value.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            Assert.Equal(participants.Count, ends.Count);
            Assert.All(ends, e => Assert.Null(((AgentRunFinishedPayload)e.Payload!).ReportEvidenceId));
            var lastEnd = ends.Max(e => e.Entry.Sequence);
            var published = entries.Where(e => e.Payload is DebateContributionRecordedPayload { Round: var r } && r == round).ToList();
            Assert.Equal(participants.Count, published.Count);
            var answers = published.Select(p => ((DebateContributionRecordedPayload)p.Payload!).Evidence).ToHashSet();
            var announced = entries.Where(e => e.Payload is EvidenceRegisteredPayload registered && answers.Contains(registered.Evidence)).ToList();
            Assert.Equal(participants.Count, announced.Count);
            Assert.All(published.Concat(announced), e => Assert.True(e.Entry.Sequence > lastEnd, $"Round {round}: entry {e.Entry.Sequence} ({e.Entry.Kind}) came before the last turn of the round ended (entry {lastEnd})."));

            foreach (var (_, payload) in entries.Where(e => e.Payload is AgentRunStartedPayload { Purpose: AgentRunPurpose.DebateParticipant, Task: { } task } && task.Value.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var started = (AgentRunStartedPayload)payload!;
                Assert.False(started.Attempt != 1, $"{started.Task} needed attempt {started.Attempt}.");
                var prompt = await Phase5Checks.TextAsync(app, started.PromptEvidenceId, started.PromptSha256);
                foreach (var other in participants.Where(p => p != started.Agent))
                {
                    Assert.DoesNotContain(Phase5Clis.Marker(other, round), prompt, StringComparison.Ordinal);
                    if (round == 1)
                    {
                        Assert.DoesNotContain(other.Value, prompt, StringComparison.Ordinal);
                    }
                }

                for (var before = 1; before < round; before++)
                {
                    Assert.All(participants, p => Assert.Contains(Phase5Clis.Marker(p, before), prompt, StringComparison.Ordinal));
                }
            }
        }
    }

    /// <summary>F5-3: each round closes with the tokens of the log (input and output of the turns, all reported), and the limit of rounds sends the debate to judgment.</summary>
    private static async Task AssertRoundLimitAsync(Phase4App app, string unit, int rounds, int participants)
    {
        var closed = (await app.PayloadsAsync<DebateRoundClosedPayload>(unit)).Select(p => p.Payload).ToList();
        Assert.Equal(Enumerable.Range(1, rounds), closed.Select(c => c.Round));
        var perRound = participants * (Phase5Clis.TurnInput + Phase5Clis.TurnOutput);
        for (var i = 0; i < closed.Count; i++)
        {
            var close = closed[i];
            Assert.Equal(perRound * (i + 1), close.TokensUsed.Value);
            Assert.Equal(MetricOrigin.ReportedByProvider, close.TokensUsed.Origin);
            Assert.False(close.TokensIncludeEstimates);
            var last = i == closed.Count - 1;
            Assert.Equal(last ? DebatePhase.AwaitingJudgment : DebatePhase.Deliberation, close.Next);
            Assert.Equal(last ? DebateJudgmentReason.Limit : null, close.Judgment);
            Assert.Equal(last ? DebateLimitReached.Rounds : DebateLimitReached.None, close.Limit);
        }
    }

    /// <summary>D-44: the usage report counts every turn under its own key and every judge, each with what its CLI reported.</summary>
    private static async Task AssertUsageAsync(Phase4App app, string unit, IReadOnlyList<AgentId> participants, int rounds, int judges)
    {
        var now = DateTimeOffset.UtcNow;
        var report = await app.Get<IUsageReport>().QueryAsync([app.Project.RootPath], new UsageQuery(now.AddDays(-1), now.AddDays(1), TimeZoneInfo.Local, []), Ct);
        Assert.True(report.Excluded.Count == 0, string.Join("; ", report.Excluded.Select(e => $"{e.Root}: {e.Reason}")));
        var attempts = report.Attempts.Where(a => a.RunId == unit).ToList();
        var turns = attempts.Where(a => a.Purpose == AgentRunPurpose.DebateParticipant).ToList();
        Assert.Equal(
            [.. Enumerable.Range(1, rounds).SelectMany(r => participants.Select(p => $"r{r}-{p}#1")).Order(StringComparer.Ordinal)],
            turns.Select(a => $"{a.TaskId}#{a.Attempt}").Order(StringComparer.Ordinal));
        Assert.All(turns, a => Assert.Equal((Phase5Clis.TurnInput, Phase5Clis.TurnOutput, MetricOrigin.ReportedByProvider), (a.Own.InputTokens.Value!.Value, a.Own.OutputTokens.Value!.Value, a.Own.InputTokens.Origin)));
        var judged = attempts.Where(a => a.Purpose == AgentRunPurpose.DebateJudge).ToList();
        Assert.Equal(judges, judged.Count);
        Assert.All(judged, a => Assert.StartsWith(JudgeTaskPrefix, a.TaskId, StringComparison.Ordinal));
        Assert.All(judged, a => Assert.Equal((Phase5Clis.JudgeInput, Phase5Clis.JudgeOutput), (a.Own.InputTokens.Value!.Value, a.Own.OutputTokens.Value!.Value)));
        // The turns are recorded in version 2; since Phase 8 the judges are in version 3, with the class of the end (docs/16 D-41).
        Assert.All(turns, a => Assert.Equal((AttemptOutcome.Succeeded, AgentRunFinishedPayload.UsageVersion), (a.Outcome, a.RecordedVersion)));
        Assert.All(judged, a => Assert.Equal((AttemptOutcome.Succeeded, AgentRunFinishedPayload.EndClassVersion), (a.Outcome, a.RecordedVersion)));
        Assert.All(attempts, a => Assert.Equal(a.Provider == ProviderId.ClaudeCode ? VendorId.Anthropic : VendorId.OpenAI, a.Vendor));
        Assert.Equal(rounds * participants.Count + judges, attempts.Count);
    }

    private const string JudgeTaskPrefix = "juez-";

    /// <summary>D-46: one summary of the debate, from its close, quoting the question the person wrote; deriving again adds nothing.</summary>
    private static async Task AssertSummaryAsync(Phase4App app, string unit, long closedSequence, string question, string mode)
    {
        var derivation = app.Get<IMemoryDerivation>();
        var derived = await derivation.DeriveAsync(app.Project, Ct);
        Assert.True(derived.Completed, derived.Reason);
        Assert.Empty(derived.Problems);
        var summary = Assert.Single(derived.Recorded, e => e.Kind == MemoryKind.DebateSummary);
        Assert.Equal((MemoryOrigin.System, MemorySources.LogEntry(unit, closedSequence)), (summary.Origin, summary.Source));
        Assert.Contains(question, summary.Content, StringComparison.Ordinal);
        Assert.Contains(unit, summary.Topic + summary.Content, StringComparison.Ordinal);
        Assert.Contains(mode, summary.Topic + summary.Content, StringComparison.Ordinal);

        var again = await derivation.DeriveAsync(app.Project, Ct);
        Assert.True(again.Completed, again.Reason);
        Assert.Empty(again.Recorded);
        Assert.Contains(summary.Source, again.AlreadyRecorded);
    }

    /// <summary>D-15: the judge never reads the agent nor the model of a participant.</summary>
    private static void AssertBlind(string prompt)
    {
        foreach (var token in BlindTokens)
        {
            Assert.DoesNotContain(token, prompt, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// D-01 and D-04: the app closes, and another instance opens the project from disk. The log verifies and ends where it
    /// ended; the domain replays the debate from the reopened stores into the state the live debate reached; the history and
    /// the transcript, read by the coordinator of the new instance, say the same with nothing to report; and nothing is launched.
    /// </summary>
    private static async Task AssertReplaysAfterRestartAsync(string root, Phase4Paths paths, RunId id, DebateResult result, DebateSnapshot live, string headHash, DebateLimitReached limit)
    {
        var clis = new Phase5Clis();
        await using var reopened = await Phase4App.OpenAsync(root, Phase5Composition.Services(paths, new ScriptedApprovalPrompt(), clis.Providers));
        var log = await reopened.LoadLogAsync();
        Assert.True(log.Verification.IsValid, log.Verification.Problem);
        Assert.Equal(headHash, log.HeadHash);

        var (replay, debateLog, record) = await Phase5Checks.ReplayAsync(reopened, id);
        Assert.True(replay.Problems.Count == 0 && debateLog.Problems.Count == 0, string.Join(" | ", replay.Problems.Concat(debateLog.Problems)));
        var session = replay.Session!;
        Assert.Equal((result.Phase, result.Outcome, result.LimitReached, result.JudgmentReason), (session.Phase, session.Outcome, session.LimitReached, session.JudgmentReason));
        Assert.Equal((DebatePhase.Closed, limit, DebateJudgmentReason.Limit), (session.Phase, session.LimitReached, session.JudgmentReason));
        Assert.Equal(live.Round, session.CurrentRound);
        Assert.Equal(
            live.Contributions.Select(c => $"{c.Index}|{c.Round}|{c.Author}|{c.Kind}|{c.RepliesTo}|{c.Evidence}|{c.AnsweredAt:O}|{string.Join(",", c.Cites)}"),
            session.Contributions.Select((c, i) => $"{i}|{c.Round}|{record.Participants.First(p => p.Agent.Id == c.Author).Label}|{c.Kind}|{(c.RepliesTo is { } r ? ContributionLabels.Of(r) : null)}|{c.Content}|{c.At:O}|{string.Join(",", c.Evidence)}"));
        var panel = result.Panel!;
        var replayed = session.Panel!;
        Assert.Equal((panel.Outcome, panel.Decision, panel.ChosenProposal, panel.Rule), (replayed.Outcome, replayed.Decision, replayed.ChosenProposal, replayed.Rule));
        Assert.Equal(result.Verdicts.Select(v => v.Verdict.Decision), session.Verdicts.Select(v => v.Decision));

        // What the room and the history show after the restart comes from the same replay.
        var coordinator = reopened.Get<IDebateCoordinator>();
        var transcript = await coordinator.ReadAsync(reopened.Project, id, Ct);
        Assert.True(transcript.Problems.Count == 0, string.Join(" | ", transcript.Problems));
        Assert.Equal((DebatePhase.Closed, result.Outcome, false), (transcript.Summary.Phase, transcript.Summary.Outcome, transcript.Summary.Interrupted));
        Assert.Equal(live.Contributions.Select(c => c.Text), transcript.Contributions.Select(c => c.Text));
        Assert.Equal(result.Verdicts.Select(v => v.Id).Order(StringComparer.Ordinal), transcript.Verdicts.Select(v => v.Id).Order(StringComparer.Ordinal));
        var summary = Assert.Single(await coordinator.ListAsync(reopened.Project, Ct));
        Assert.Equal((id, DebatePhase.Closed, false), (summary.Debate, summary.Phase, summary.Interrupted));
        Assert.Empty(clis.Launches);
    }
}
