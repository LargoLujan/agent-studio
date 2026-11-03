using System.Text.Json.Nodes;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Tests.Debate.Fakes;
using static AgentStudio.Tests.Debate.Fakes.ScriptedDebateWorld;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>
/// The actions of the room (docs/15 D-20, D-26 and D-27): «Pedir juicio ahora», «Decidir yo» through the approval gate,
/// «Añadir evidencia» and «Ejecutar pruebas» with the trusted profile on the commit of the debate.
/// </summary>
public sealed class DebateActionsTests
{
    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static Task WaitUntilAsync(Func<bool> condition) => WaitUntilAsync(() => Task.FromResult(condition()));

    private static DebateRequest Mixed(ScriptedDebateWorld world) =>
        world.Request(DebateMode.Free, [(Arquitecto, DebateRole.Proponent), (Critico, DebateRole.Critic)], [JuezClaude, JuezCodex], DebateJudgingKind.CrossVendorPanel);

    private static ScriptedDebateWorld WithTests(ScriptedDebateWorld world, VerificationProfile? profile, out ScriptedDebateVerificationRunner runner)
    {
        runner = new ScriptedDebateVerificationRunner(world.Evidence);
        world.Ports = world.Ports with { Verification = runner, TestProfiles = new ScriptedDebateTestProfiles(profile) };
        world.Coordinator = new DebateCoordinator(world.Ports);
        return world;
    }

    [Fact]
    public async Task Asking_for_judgment_cancels_the_turns_in_course_publishes_those_that_answered_and_judges()
    {
        using var world = new ScriptedDebateWorld();
        world.Codex.Reply = (r, _) =>
        {
            if (r.TaskId == "r2-critico")
            {
                world.Codex.Hold = true;
            }

            return ScriptedDebateProvider.Valid(r);
        };

        await using var run = await world.OpenAsync(Mixed(world));
        await WaitUntilAsync(async () => world.Codex.Runs.Count == 2
            && (await world.DebateLogAsync(run.Id)).Turns.Any(t => t.Task!.Value.Value == "r2-arquitecto" && !t.IsOpen));
        await run.RequestJudgmentAsync();
        var result = await CompletedAsync(run);

        Assert.Equal((DebatePhase.Closed, DebateJudgmentReason.Requested), (result.Phase, result.JudgmentReason));
        var log = await world.DebateLogAsync(run.Id);
        Assert.Equal([Arquitecto, Critico, Arquitecto], log.Contributions.Select(c => c.Payload.Author));
        Assert.Equal(AttemptOutcome.Cancelled, log.Turns.Single(t => t.Task!.Value.Value == "r2-critico").Finished!.Status);
        Assert.Single(world.Codex.Requests, r => r.TaskId == "r2-critico");
        var cut = log.RoundsClosed[^1].Payload;
        Assert.Equal((2, DebateJudgmentReason.Requested, DebatePhase.AwaitingJudgment), (cut.Round, cut.Judgment, cut.Next));
        Assert.Equal([run.Id], world.Judgment.Calls);
    }

    [Fact]
    public async Task Asking_for_judgment_before_anything_was_published_is_refused()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Hold = true;
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(run.RequestJudgmentAsync);

        Assert.Contains("ninguna contribución", refused.Message, StringComparison.Ordinal);
        world.Claude.Release();
        Assert.Equal(DebateJudgmentReason.Converged, (await CompletedAsync(run)).JudgmentReason);
    }

    [Fact]
    public async Task Deciding_myself_closes_the_debate_through_the_approval_gate_with_the_rule_user()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);
        world.Judgment.Decisions[0] = VerdictDecision.EscalateToHuman;

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await WaitUntilAsync(() => run.Snapshot().Awaiting?.Reason == DebateAwaitReason.HumanResolution);
        await run.DecideAsync(VerdictDecision.Approved, "C1", "Me quedo con la primera propuesta.");
        var result = await CompletedAsync(run);

        Assert.Equal((DebatePhase.Closed, DebateOutcome.Resolved, JudgingPanel.User), (result.Phase, result.Outcome, result.Panel!.Rule));
        var asked = world.Prompt.Asked[^1];
        Assert.Equal((DebateRun.HumanResolutionAction, run.Id.Value), (asked.Action, asked.RunId));
        Assert.Contains("Me quedo con la primera propuesta.", asked.Description, StringComparison.Ordinal);
        Assert.Contains((await world.LogAsync()).Entries, e => e.Kind == DecisionKind.ApprovalGranted && e.PayloadJson.Contains(DebateRun.HumanResolutionAction, StringComparison.Ordinal));
        var closed = (await world.DebateLogAsync(run.Id)).Closed!.Value.Payload;
        Assert.Equal((JudgingPanel.User, "C1", DebateOutcome.Resolved), (closed.Rule, closed.ChosenProposal, closed.Outcome));
        Assert.Equal([$"ver-{run.Id}-1"], closed.Verdicts);
    }

    [Fact]
    public async Task A_decision_the_user_does_not_approve_leaves_the_debate_waiting_for_it()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);
        world.Judgment.Decisions[0] = VerdictDecision.Unresolved;
        world.Prompt.Approve = a => a.Action != DebateRun.HumanResolutionAction;

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await WaitUntilAsync(() => run.Snapshot().Awaiting?.Reason == DebateAwaitReason.HumanResolution);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => run.DecideAsync(VerdictDecision.Rejected, null, "No me convence."));

        Assert.Contains("no se aprobó", refused.Message, StringComparison.Ordinal);
        await WaitUntilAsync(() => run.Snapshot().Awaiting?.Reason == DebateAwaitReason.HumanResolution);
        Assert.Null((await world.DebateLogAsync(run.Id)).Closed);

        await run.AbortAsync("Lo dejo para otro día.");
        Assert.Equal(DebateOutcome.Aborted, (await CompletedAsync(run)).Outcome);
    }

    [Fact]
    public async Task Deciding_or_retrying_is_refused_while_the_debate_does_not_wait_for_a_person()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Hold = true;
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());

        await Assert.ThrowsAsync<InvalidOperationException>(() => run.DecideAsync(VerdictDecision.Approved, "C1", "Ya."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.RetryTurnAsync(Arquitecto));
        world.Claude.Release();
        await CompletedAsync(run);
    }

    [Fact]
    public async Task A_note_of_the_user_is_evidence_provided_by_a_human_that_the_next_round_reads_and_may_cite()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Hold = true;
        string? note = null;
        world.Claude.Reply = (r, _) => r.TaskId!.StartsWith("r2-", StringComparison.Ordinal)
            ? ScriptedDebateProvider.Valid(r, finished: true, customize: a => a["claims"] = new JsonArray(new JsonObject { ["claim"] = "La nota lo respalda.", ["support"] = new JsonArray(Volatile.Read(ref note)) }))
            : ScriptedDebateProvider.Valid(r);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await WaitUntilAsync(() => world.Claude.Runs.Count == 2);
        await run.AddHumanEvidenceAsync("Nota del usuario", "Usad Redis: ya está en producción.");

        var registered = Assert.Single((await world.DebateLogAsync(run.Id)).Evidence, e => e.Payload.Kind == EvidenceKind.HumanNote).Payload;
        Assert.Equal((EvidenceOrigin.ProvidedByHuman, "usuario", "Nota del usuario"), (registered.Origin, registered.ProducedBy, registered.Title));
        Assert.Equal("Usad Redis: ya está en producción.", world.Evidence.ContentText(registered.Evidence));
        Volatile.Write(ref note, registered.Evidence.Value);
        world.Claude.Release();
        var result = await CompletedAsync(run);

        Assert.Equal(DebateJudgmentReason.Converged, result.JudgmentReason);
        Assert.All(world.Claude.Requests.Where(r => r.TaskId!.StartsWith("r2-", StringComparison.Ordinal)), r => Assert.Contains($"\"id\":\"{note}\"", r.Prompt, StringComparison.Ordinal));
        Assert.All(world.Claude.Requests.Where(r => r.TaskId!.StartsWith("r1-", StringComparison.Ordinal)), r => Assert.DoesNotContain(note!, r.Prompt, StringComparison.Ordinal));
        Assert.All((await world.DebateLogAsync(run.Id)).Contributions.Where(c => c.Payload.Round == 2), c => Assert.Contains(registered.Evidence, c.Payload.Cites));
    }

    [Fact]
    public async Task Running_the_tests_runs_only_the_isolated_steps_of_the_trusted_profile_on_the_commit_as_evidence_the_next_round_reads()
    {
        using var world = WithTests(new ScriptedDebateWorld(), ScriptedDebateTestProfiles.Profile(), out var runner);
        world.Claude.Hold = true;
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: r.TaskId!.StartsWith("r2-", StringComparison.Ordinal));

        await using var run = await world.OpenAsync(world.FreeOnAnthropic(subject: new DebateSubject(DebateSubjectKind.Commit, null)));
        Assert.True(run.Snapshot().CanRunTests);
        await run.RunTestsAsync();

        var (folder, steps, scope) = Assert.Single(runner.Calls);
        Assert.Equal(["tests"], steps.Select(s => s.Id));
        Assert.EndsWith("snapshot", folder, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(run.Id, scope.Run);
        Assert.Equal(("pruebas", 1, world.Worktrees.HeadCommit, world.RepoRoot), (scope.Task!.Value.Value, scope.Attempt, scope.SubjectCommit, scope.RepositoryRoot));
        var registered = Assert.Single((await world.DebateLogAsync(run.Id)).Evidence, e => e.Payload.Kind == EvidenceKind.TestRun).Payload;
        Assert.Equal(("pruebas", 1, world.Worktrees.HeadCommit, EvidenceOrigin.CapturedBySystem), (registered.Task!.Value.Value, registered.Attempt, registered.SubjectCommit, registered.Origin));
        Assert.Equal(12, registered.TestRun!.Passed);

        world.Claude.Release();
        await CompletedAsync(run);
        Assert.All(world.Claude.Requests.Where(r => r.TaskId!.StartsWith("r2-", StringComparison.Ordinal)), r => Assert.Contains($"\"id\":\"{registered.Evidence}\"", r.Prompt, StringComparison.Ordinal));
        Assert.False(run.Snapshot().CanRunTests);
    }

    [Fact]
    public async Task Running_the_tests_is_refused_without_a_commit_without_a_profile_or_without_an_isolated_step()
    {
        using (var world = WithTests(new ScriptedDebateWorld(), ScriptedDebateTestProfiles.Profile(), out var runner))
        {
            world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);
            await using var run = await world.OpenAsync(world.FreeOnAnthropic());
            Assert.False(run.Snapshot().CanRunTests);
            await Assert.ThrowsAsync<InvalidOperationException>(run.RunTestsAsync);
            await CompletedAsync(run);
            Assert.Empty(runner.Calls);
        }

        using (var world = WithTests(new ScriptedDebateWorld(), null, out var runner))
        {
            world.Claude.Hold = true;
            await using var run = await world.OpenAsync(world.FreeOnAnthropic(subject: new DebateSubject(DebateSubjectKind.Commit, null)));
            Assert.False(run.Snapshot().CanRunTests);
            await Assert.ThrowsAsync<InvalidOperationException>(run.RunTestsAsync);
            Assert.Empty(runner.Calls);
            await run.AbortAsync("Fin de la prueba.");
            await CompletedAsync(run);
        }

        var unsafeOnly = new VerificationProfile("sin aislar", [ScriptedDebateTestProfiles.Profile().Steps[1]]);
        using (var world = WithTests(new ScriptedDebateWorld(), unsafeOnly, out var runner))
        {
            world.Claude.Hold = true;
            await using var run = await world.OpenAsync(world.FreeOnAnthropic(subject: new DebateSubject(DebateSubjectKind.Commit, null)));
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(run.RunTestsAsync);
            Assert.Contains("ningún paso aislado", refused.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Calls);
            await run.AbortAsync("Fin de la prueba.");
            await CompletedAsync(run);
        }
    }
}
