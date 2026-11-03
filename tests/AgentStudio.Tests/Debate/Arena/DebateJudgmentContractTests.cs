using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Debate;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Debate.Fakes;
using AgentStudio.Tests.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using static AgentStudio.Tests.Debate.Fakes.ScriptedDebateWorld;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>
/// What the judgment of a debate (WP2) needs of the arena: <c>DebateOpened</c> anchors the header the registry gives back,
/// with the proposal the user approved computed on it; every row of evidence of the debate is announced by the log; and the
/// arena uses the one activity of the process. The real dossier of the judgment is built over what the arena wrote, and it
/// finds nothing to report.
/// </summary>
public sealed class DebateJudgmentContractTests
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

    private static void NothingToReport(ScriptedDebateDossiers dossiers, RunId debate)
    {
        var built = Assert.Single(dossiers.Built);
        Assert.Equal(debate, built.Debate);
        Assert.True(built.HeaderVerified);
        Assert.True(built.Checks.Count == 0, string.Join(" | ", built.Checks));
    }

    [Fact]
    public async Task The_proposal_the_user_approved_is_the_one_of_the_header_the_registry_gives_back()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic(subject: new DebateSubject(DebateSubjectKind.Commit, null)));
        await CompletedAsync(run);

        var stored = (await world.Debates.FindAsync(Project, run.Id, TestContext.Current.CancellationToken))!;
        var opened = (await world.DebateLogAsync(run.Id)).Opened!;
        Assert.Equal(opened.ApprovedProposalHash, DebateHeaderHash.Proposal(stored, opened.QuestionSha256));
        Assert.Equal(opened.HeaderHash, DebateHeaderHash.Of(stored));
        Assert.Equal(ContentHash.Sha256Hex(Encoding.UTF8.GetBytes(world.Evidence.ContentText(stored.Question))), opened.QuestionSha256);
        Assert.Equal(opened.ApprovedProposalHash, world.Prompt.Asked.Single(a => a.Action == DebateCoordinator.OpenAction).Binding);
    }

    [Fact]
    public async Task The_header_the_arena_builds_comes_back_from_the_sqlite_registry_with_the_hash_the_user_approved()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Hold = true;
        await using var run = await world.OpenAsync(world.FreeOnAnthropic(new DebateLimits(3, TimeSpan.FromMinutes(12.5), 250_000), maxAgentRuns: 9, subject: new DebateSubject(DebateSubjectKind.Commit, null)));
        var built = (await world.Debates.FindAsync(Project, run.Id, TestContext.Current.CancellationToken))!;
        var opened = (await world.DebateLogAsync(run.Id)).Opened!;
        await run.AbortAsync("Fin de la prueba.");
        world.Claude.Release();
        await CompletedAsync(run);

        // The same header (every member of every agent of the template) through the registry of WP4, on a database of its own.
        using var folder = new TempProjectFolder();
        using var instance = await ProjectInstance.OpenAsync(folder.Root);
        await instance.Requirements.SaveAsync(instance.ProjectId, world.Frozen, TestContext.Current.CancellationToken);
        using var text = new MemoryStream(Encoding.UTF8.GetBytes(world.Evidence.ContentText(built.Question)));
        var question = await instance.Evidence.CaptureAsync(
            instance.ProjectId,
            new EvidenceDraft(EvidenceKind.Document, EvidenceOrigin.ProvidedByHuman, "Pregunta del debate", "usuario", RunId: built.Id.Value),
            text,
            TestContext.Current.CancellationToken);
        var header = built with { ProjectId = instance.ProjectId, Question = question.Id };
        var registry = new SqliteDebateRegistry(instance.Databases);
        await registry.CreateAsync(header, TestContext.Current.CancellationToken);
        var found = (await registry.FindAsync(instance.ProjectId, header.Id, TestContext.Current.CancellationToken))!;

        Assert.Equal(opened.QuestionSha256, question.ContentSha256);
        Assert.Equal(opened.ApprovedProposalHash, DebateHeaderHash.Proposal(found, question.ContentSha256));
        Assert.Equal(DebateHeaderHash.Of(header), DebateHeaderHash.Of(found));
    }

    [Fact]
    public async Task A_registry_that_keeps_another_header_than_the_approved_one_opens_nothing()
    {
        using var world = new ScriptedDebateWorld();
        world.Debates.OnStore = debate => debate with { MaxAgentRuns = debate.MaxAgentRuns + 1 };

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => world.OpenAsync(world.FreeOnAnthropic()));

        Assert.Contains("no es la propuesta que aprobaste", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain((await world.LogAsync()).Entries, e => e.Kind == DecisionKind.DebateOpened);
        Assert.Empty(world.Claude.Requests);
        Assert.False(world.Activity.IsBusy(Project));
    }

    [Fact]
    public async Task The_dossier_of_the_judgment_backs_with_the_log_every_piece_of_a_debate_with_corrections_retries_citations_notes_and_tests()
    {
        using var world = new ScriptedDebateWorld();
        var dossiers = new ScriptedDebateDossiers(world);
        world.Ports = world.Ports with { Verification = new ScriptedDebateVerificationRunner(world.Evidence), TestProfiles = new ScriptedDebateTestProfiles(ScriptedDebateTestProfiles.Profile()) };
        world.Coordinator = new DebateCoordinator(world.Ports);
        var attempts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        world.Claude.Hold = true;
        world.Claude.Reply = (r, _) =>
        {
            var attempt = attempts.AddOrUpdate(r.TaskId!, 1, (_, n) => n + 1);
            return (r.TaskId, attempt) switch
            {
                ("r1-arquitecto", 1) => ScriptedDebateProvider.Valid(r, customize: a => a["kind"] = "Nada"),
                ("r1-revisor", 1) => new ScriptedDebateReply(AgentRunStatus.Failed, null, null, "fallo guionizado"),
                _ => ScriptedDebateProvider.Valid(r, finished: r.TaskId!.StartsWith("r2-", StringComparison.Ordinal), customize: a =>
                {
                    a["claims"] = new JsonArray(new JsonObject { ["claim"] = "Se ve en el código.", ["support"] = new JsonArray("CITA-1", "CITA-2") });
                    a["citations"] = new JsonArray(
                        new JsonObject { ["id"] = "CITA-1", ["path"] = "src/Cache.cs", ["startLine"] = 1, ["endLine"] = 2, ["why"] = "La caché." },
                        new JsonObject { ["id"] = "CITA-2", ["path"] = "src/NoExiste.cs", ["startLine"] = 1, ["endLine"] = 1, ["why"] = "No está." });
                }),
            };
        };

        await using var run = await world.OpenAsync(world.FreeOnAnthropic(subject: new DebateSubject(DebateSubjectKind.Commit, null)));
        await WaitUntilAsync(() => Task.FromResult(world.Claude.Runs.Count >= 1));
        await run.AddHumanEvidenceAsync("Nota del usuario", "La caché tiene que caducar en una hora.");
        await run.RunTestsAsync();
        world.Claude.Release();
        var result = await CompletedAsync(run);

        Assert.Equal(DebatePhase.Closed, result.Phase);
        var log = await world.DebateLogAsync(run.Id);
        Assert.Equal(4, log.Contributions.Count);
        Assert.Contains(log.Turns, t => t.Task!.Value.Value == "r1-arquitecto" && t.Attempt == 2);
        Assert.Contains(log.Turns, t => t.Task!.Value.Value == "r1-revisor" && t.Attempt == 2);
        NothingToReport(dossiers, run.Id);
    }

    [Fact]
    public async Task The_dossier_of_the_judgment_backs_with_the_log_a_debate_whose_process_ended_and_was_resumed()
    {
        using var world = new ScriptedDebateWorld();
        var dossiers = new ScriptedDebateDossiers(world);
        var crashed = 0;
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: r.TaskId!.StartsWith("r2-", StringComparison.Ordinal));
        world.Codex.Reply = (r, _) =>
        {
            if (r.TaskId == "r2-critico" && Volatile.Read(ref crashed) == 0)
            {
                world.Codex.Hold = true;
            }

            return ScriptedDebateProvider.Valid(r, finished: r.TaskId!.StartsWith("r2-", StringComparison.Ordinal));
        };

        var first = await world.OpenAsync(world.Request(DebateMode.Free, [(Arquitecto, DebateRole.Proponent), (Critico, DebateRole.Critic)], [JuezClaude, JuezCodex], DebateJudgingKind.CrossVendorPanel));
        await WaitUntilAsync(async () => world.Codex.Runs.Count == 2 && (await world.DebateLogAsync(first.Id)).Turns.Any(t => t.Task!.Value.Value == "r2-arquitecto" && !t.IsOpen));
        await first.DisposeAsync();
        Volatile.Write(ref crashed, 1);
        world.Codex.Hold = false;
        await using var resumed = await world.Coordinator.ResumeAsync(world.Context, first.Id, TestContext.Current.CancellationToken);
        var result = await CompletedAsync(resumed);

        Assert.Equal(DebatePhase.Closed, result.Phase);
        NothingToReport(dossiers, first.Id);
    }

    [Fact]
    public void The_arena_uses_the_one_activity_of_the_process_and_never_registers_its_own()
    {
        var services = new ServiceCollection().AddAgentStudioDebateArena();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IProjectActivity) || d.ServiceType == typeof(IRunActivity));
    }

    [Fact]
    public async Task With_the_activity_of_the_engine_a_debate_shares_its_project_with_no_run_and_no_other_debate()
    {
        using var world = new ScriptedDebateWorld();
        var activity = new OrchestrationActivity();
        world.Ports = world.Ports with { Activity = activity };
        world.Coordinator = new DebateCoordinator(world.Ports);
        var proposal = await world.Coordinator.PrepareAsync(world.FreeOnAnthropic(), TestContext.Current.CancellationToken);

        using (activity.TryEnter(Project, ProjectActivityKind.Run))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => world.Coordinator.OpenAsync(proposal, TestContext.Current.CancellationToken));
        }

        Assert.Empty((await world.LogAsync()).Entries);
        world.Claude.Hold = true;
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);
        await using var run = await world.Coordinator.OpenAsync(proposal, TestContext.Current.CancellationToken);
        Assert.Equal(ProjectActivityKind.Debate, activity.Current(Project));
        Assert.Null(activity.TryEnter(Project, ProjectActivityKind.Run));
        Assert.Null(activity.TryEnter(Project, ProjectActivityKind.Debate));
        world.Claude.Release();
        await CompletedAsync(run);
        Assert.False(activity.IsBusy(Project));
    }
}
