using System.IO;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Providers;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Providers.Cli.Codex;
using AgentStudio.Tests.Debate.Fakes;
using static AgentStudio.Tests.Debate.Fakes.ScriptedDebateWorld;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>
/// F5-2: the rounds are simultaneous and sealed (docs/15 D-16 to D-18): every turn of a round is launched at once, each in a
/// new session that is not kept and in a folder of its own, read-only; nothing of a round is captured or announced until all
/// its turns ended; and no prompt carries the text, the id or the hash of another participant's answer of its round.
/// </summary>
public sealed class DebateSealingTests
{
    /// <summary>The marker a scripted answer carries: whose it is and of which round.</summary>
    private static string Marker(AgentRunRequest request) => $"MARCA-{request.Agent.Id}-{request.TaskId}";

    private static ScriptedDebateReply Marked(AgentRunRequest request, bool finishedFromRound2)
    {
        var round2 = request.TaskId!.StartsWith("r2-", StringComparison.Ordinal);
        return ScriptedDebateProvider.Valid(request, finished: !finishedFromRound2 || round2, customize: a => a["summary"] = Marker(request) + " resumen");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Every_turn_of_a_round_starts_before_any_of_them_ends_and_nothing_of_it_is_written_meanwhile()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Hold = true;
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await WaitUntilAsync(() => world.Claude.Runs.Count == 2);

        Assert.All(world.Claude.Runs, r => Assert.True(r.Running));
        Assert.Equal(2, run.Snapshot().TurnsInFlight);
        var during = await world.DebateLogAsync(run.Id);
        Assert.Equal(2, during.Turns.Count());
        Assert.All(during.Turns, t => Assert.True(t.IsOpen));
        Assert.Empty(during.Contributions);
        Assert.DoesNotContain(world.Evidence.Drafts, d => d.Kind == EvidenceKind.AgentStatement);

        world.Claude.Release();
        var result = await CompletedAsync(run);

        Assert.Equal(DebateJudgmentReason.Converged, result.JudgmentReason);
    }

    [Fact]
    public async Task The_answer_of_a_turn_that_ended_stays_sealed_while_another_turn_of_its_round_runs()
    {
        using var world = new ScriptedDebateWorld();
        world.Codex.Hold = true;
        world.Claude.Reply = (r, _) => Marked(r, finishedFromRound2: false);
        world.Codex.Reply = (r, _) => Marked(r, finishedFromRound2: false);

        await using var run = await world.OpenAsync(world.Request(DebateMode.Free, [(Arquitecto, DebateRole.Proponent), (Critico, DebateRole.Critic)], [JuezClaude, JuezCodex], DebateJudgingKind.CrossVendorPanel));
        await WaitUntilAsync(() => world.Codex.Runs.Count == 1 && world.Claude.Runs.Count == 1 && !world.Claude.Runs[0].Running);
        var claudeFinished = (await world.DebateLogAsync(run.Id)).Turns.Single(t => t.Task!.Value.Value == "r1-arquitecto");
        await WaitUntilAsync(() => world.DebateLogAsync(run.Id).Result.Turns.Single(t => t.Task!.Value.Value == "r1-arquitecto").Finished is not null);

        var sealedLog = await world.DebateLogAsync(run.Id);
        Assert.Empty(sealedLog.Contributions);
        Assert.Null(sealedLog.Turns.Single(t => t.Task!.Value.Value == "r1-arquitecto").Finished!.ReportEvidenceId);
        Assert.DoesNotContain(world.Evidence.Drafts, d => d.Kind == EvidenceKind.AgentStatement);
        Assert.DoesNotContain(sealedLog.Entries, e => e.PayloadJson.Contains("MARCA-", StringComparison.Ordinal));
        Assert.Empty(run.Snapshot().Contributions);
        Assert.NotNull(claudeFinished);

        world.Codex.Release();
        await CompletedAsync(run);
    }

    [Fact]
    public async Task Each_turn_is_a_new_read_only_session_that_is_not_kept_in_a_folder_of_its_own()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => Marked(r, finishedFromRound2: true);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic(subject: new DebateSubject(DebateSubjectKind.Commit, null)));
        await CompletedAsync(run);

        var requests = world.Claude.Requests;
        Assert.Equal(4, requests.Count);
        Assert.All(requests, r =>
        {
            Assert.False(r.PersistSession);
            Assert.Null(r.ResumeSessionId);
            Assert.Equal(run.Id.Value, r.DebateId);
            Assert.False(r.Permissions.AllowFileWrites);
            Assert.False(r.Permissions.AllowShell);
            Assert.False(r.Permissions.AllowNetwork);
            Assert.Equal(["Read", "Grep", "Glob"], r.Permissions.AllowedTools);
            Assert.Null(r.McpServers);
            Assert.Null(r.SkillsDirectory);
            Assert.NotNull(r.OutputJsonSchema);
            Assert.EndsWith("snapshot", r.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(4, requests.Select(r => r.Permissions.AdditionalDirectories.Single()).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(["r1-arquitecto", "r1-revisor", "r2-arquitecto", "r2-revisor"], requests.Select(r => r.TaskId!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_read_rules_of_a_participant_on_claude_name_only_the_copy_and_its_own_folder()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic(subject: new DebateSubject(DebateSubjectKind.Commit, null)));
        await CompletedAsync(run);

        var folders = world.Claude.Requests.Select(r => r.Permissions.AdditionalDirectories.Single()).ToList();
        foreach (var request in world.Claude.Requests)
        {
            var arguments = ClaudeCodeArguments.Build(request, Guid.NewGuid().ToString(), null).ToList();
            var allowed = arguments[arguments.IndexOf("--allowedTools") + 1];
            var own = request.Permissions.AdditionalDirectories.Single();
            Assert.StartsWith("Read(", allowed, StringComparison.Ordinal);
            Assert.Contains(Rule(request.WorkingDirectory), allowed, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Rule(own), allowed, StringComparison.OrdinalIgnoreCase);
            Assert.All(folders.Where(f => !string.Equals(f, own, StringComparison.OrdinalIgnoreCase)), other => Assert.DoesNotContain(Rule(other), allowed, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("Edit(", allowed, StringComparison.Ordinal);
            Assert.Contains("--no-session-persistence", arguments);
            Assert.DoesNotContain("--resume", arguments);
        }

        static string Rule(string folder) => folder.Replace('\\', '/').TrimEnd('/')[2..];
    }

    [Fact]
    public async Task Each_turn_of_a_participant_on_codex_is_an_ephemeral_read_only_thread()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);
        world.Codex.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);

        await using var run = await world.OpenAsync(world.Request(DebateMode.Free, [(Arquitecto, DebateRole.Proponent), (Critico, DebateRole.Critic)], [JuezClaude, JuezCodex], DebateJudgingKind.CrossVendorPanel));
        await CompletedAsync(run);

        var request = Assert.Single(world.Codex.Requests);
        var arguments = CodexExecArguments.Build(request, Path.Combine(world.Root, "schema.json"), isRepository: false).ToList();
        Assert.Contains("--ephemeral", arguments);
        Assert.Contains("history.persistence=none", arguments);
        Assert.Contains("read-only", arguments);
        Assert.DoesNotContain("resume", arguments);
    }

    [Fact]
    public async Task No_prompt_of_a_round_has_anything_of_another_participants_answer_of_that_round()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => Marked(r, finishedFromRound2: true);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await CompletedAsync(run);

        var requests = world.Claude.Requests;
        var log = await world.DebateLogAsync(run.Id);
        var round1 = log.Contributions.Where(c => c.Payload.Round == 1).ToList();
        foreach (var request in requests.Where(r => r.TaskId!.StartsWith("r1-", StringComparison.Ordinal)))
        {
            var other = request.Agent.Id == Arquitecto ? Revisor : Arquitecto;
            Assert.DoesNotContain("MARCA-", request.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain(other.Value, request.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain(request.Agent.Id == Arquitecto ? "P2" : "P1", request.Prompt, StringComparison.Ordinal);
            Assert.All(round1, c => Assert.DoesNotContain(c.Payload.Sha256, request.Prompt, StringComparison.Ordinal));
            Assert.All(round1, c => Assert.DoesNotContain(c.Payload.Evidence.Value, request.Prompt, StringComparison.Ordinal));
        }

        foreach (var request in requests.Where(r => r.TaskId!.StartsWith("r2-", StringComparison.Ordinal)))
        {
            var other = request.Agent.Id == Arquitecto ? Revisor : Arquitecto;
            Assert.Contains($"MARCA-{other}-r1-{other}", request.Prompt, StringComparison.Ordinal);
            Assert.Contains($"MARCA-{request.Agent.Id}-r1-{request.Agent.Id}", request.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain($"MARCA-{other}-r2-", request.Prompt, StringComparison.Ordinal);
            Assert.All(round1, c => Assert.Contains(c.Payload.Sha256, request.Prompt, StringComparison.Ordinal));
        }

        // The prompts captured as evidence are the prompts sent, anchored by AgentRunStarted.
        foreach (var turn in log.Turns)
        {
            Assert.Equal(requests.Single(r => r.TaskId == turn.Task!.Value.Value).Prompt, world.Evidence.ContentText(turn.Started.PromptEvidenceId));
        }
    }

    [Fact]
    public async Task No_contribution_nor_its_evidence_is_announced_before_the_last_turn_of_its_round_ended()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => Marked(r, finishedFromRound2: true);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await CompletedAsync(run);

        var log = await world.DebateLogAsync(run.Id);
        foreach (var round in new[] { 1, 2 })
        {
            var lastFinished = log.Turns.Where(t => t.Task!.Value.Value.StartsWith($"r{round}-", StringComparison.Ordinal)).Max(t => t.FinishedEntry!.Sequence);
            var firstPublished = log.Contributions.Where(c => c.Payload.Round == round).Min(c => c.Entry.Sequence);
            var firstStatement = log.Evidence.Where(e => e.Payload.Kind == EvidenceKind.AgentStatement && e.Payload.Task!.Value.Value.StartsWith($"r{round}-", StringComparison.Ordinal)).Min(e => e.Entry.Sequence);
            Assert.True(lastFinished < firstPublished, $"round {round}: published at {firstPublished}, a turn ended at {lastFinished}");
            Assert.True(lastFinished < firstStatement, $"round {round}: announced at {firstStatement}, a turn ended at {lastFinished}");
        }

        Assert.All(log.Turns, t => Assert.Null(t.Finished!.ReportEvidenceId));
        Assert.All(log.Contributions, c => Assert.True(c.Payload.AnsweredAt <= c.Entry.RecordedAt));
    }

    [Fact]
    public async Task The_published_contribution_is_the_redacted_canonical_answer_and_its_hash_is_what_the_others_read()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: r.TaskId!.StartsWith("r2-", StringComparison.Ordinal), customize: a => a["argument"] = "La clave es password=hunter2 y no se comparte.");

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await CompletedAsync(run);

        var log = await world.DebateLogAsync(run.Id);
        Assert.All(log.Contributions, c =>
        {
            var text = world.Evidence.ContentText(c.Payload.Evidence);
            Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
            Assert.Equal(1, c.Payload.Redactions);
            Assert.Equal(ContentHash.Sha256Hex(text), c.Payload.Sha256);
        });
        Assert.All(world.Claude.Requests.Where(r => r.TaskId!.StartsWith("r2-", StringComparison.Ordinal)), r => Assert.DoesNotContain("hunter2", r.Prompt, StringComparison.Ordinal));
    }
}
