using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Tests.Debate.Fakes;
using static AgentStudio.Tests.Debate.Fakes.ScriptedDebateWorld;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>
/// What protects the project around a debate (docs/15 D-05, section 7 and WP1): a busy project refuses the debate before
/// anything is written, an opening the user does not approve leaves nothing, the user's repository is checked around every
/// round and the judgment, and no payload of the log carries text of an agent.
/// </summary>
public sealed class DebateSafetyTests
{
    private const string Marker = "MARCA-AGENTE";

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Every property name and string value of a JSON document, at any depth.</summary>
    private static IEnumerable<string> Strings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString()!;
                break;
            case JsonValueKind.Array:
                foreach (var text in element.EnumerateArray().SelectMany(Strings))
                {
                    yield return text;
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var text in Strings(property.Value))
                    {
                        yield return text;
                    }
                }

                break;
        }
    }

    [Fact]
    public async Task A_busy_project_refuses_the_debate_before_writing_asking_or_launching_anything()
    {
        using var world = new ScriptedDebateWorld();
        var proposal = await world.Coordinator.PrepareAsync(world.FreeOnAnthropic(subject: new DebateSubject(DebateSubjectKind.Commit, null)), TestContext.Current.CancellationToken);

        using (world.Activity.TryEnter(Project, ProjectActivityKind.Run))
        {
            var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => world.Coordinator.OpenAsync(proposal, TestContext.Current.CancellationToken));
            Assert.Equal(DebateUserTexts.ProjectBusy, Assert.IsType<UserTextException>(refused).Text.Code);
            Assert.Contains("busy (Run)", refused.Message, StringComparison.Ordinal);
        }

        Assert.Empty((await world.LogAsync()).Entries);
        Assert.Empty(world.Prompt.Asked);
        Assert.Empty(await world.Debates.ListAsync(Project, TestContext.Current.CancellationToken));
        Assert.Empty(world.Evidence.Drafts);
        Assert.Empty(world.Worktrees.Archived);
        Assert.Empty(world.Claude.Requests);
        Assert.False(Directory.Exists(world.DebatesRoot));
        Assert.False(world.Activity.IsBusy(Project));
    }

    [Fact]
    public async Task A_debate_holds_its_project_while_it_runs_so_another_debate_or_a_run_is_refused()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Hold = true;
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        var second = await world.Coordinator.PrepareAsync(world.FreeOnAnthropic(), TestContext.Current.CancellationToken);

        Assert.Equal(ProjectActivityKind.Debate, world.Activity.Current(Project));
        Assert.Null(world.Activity.TryEnter(Project, ProjectActivityKind.Run));
        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => world.Coordinator.OpenAsync(second, TestContext.Current.CancellationToken));
        Assert.Equal(DebateUserTexts.ProjectBusy, Assert.IsType<UserTextException>(refused).Text.Code);
        Assert.Contains("busy (Debate)", refused.Message, StringComparison.Ordinal);

        world.Claude.Release();
        await CompletedAsync(run);
        Assert.Null(world.Activity.Current(Project));
        Assert.Single(await world.Debates.ListAsync(Project, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_opening_the_user_does_not_approve_leaves_no_header_no_opening_no_evidence_and_no_copy()
    {
        using var world = new ScriptedDebateWorld();
        world.Prompt.Approve = _ => false;
        var proposal = await world.Coordinator.PrepareAsync(world.FreeOnAnthropic(subject: new DebateSubject(DebateSubjectKind.Commit, null)), TestContext.Current.CancellationToken);

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => world.Coordinator.OpenAsync(proposal, TestContext.Current.CancellationToken));

        Assert.Equal(DebateUserTexts.OpeningNotApproved, Assert.IsType<UserTextException>(refused).Text.Code);
        var asked = Assert.Single(world.Prompt.Asked);
        Assert.Equal((DebateCoordinator.OpenAction, proposal.HeaderHash), (asked.Action, asked.Binding));
        var log = await world.LogAsync();
        Assert.DoesNotContain(log.Entries, e => e.Kind == DecisionKind.DebateOpened);
        Assert.Contains(log.Entries, e => e.Kind == DecisionKind.ApprovalDenied);
        Assert.Empty(await world.Debates.ListAsync(Project, TestContext.Current.CancellationToken));
        Assert.Empty(await world.Coordinator.ListAsync(world.Context, TestContext.Current.CancellationToken));
        Assert.Empty(world.Evidence.Drafts);
        Assert.Empty(world.Worktrees.Archived);
        Assert.Empty(world.Claude.Requests);
        Assert.False(world.Activity.IsBusy(Project));
    }

    [Fact]
    public async Task The_users_repository_is_checked_before_and_after_every_round_and_around_the_judgment()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: r.TaskId!.StartsWith("r2-", StringComparison.Ordinal));

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        var result = await CompletedAsync(run);

        Assert.Equal((DebateOutcome.Resolved, 2), (result.Outcome, (await world.DebateLogAsync(run.Id)).RoundsClosed.Count));

        // The baseline when it opens; before and after each of its two rounds; before and after its judgment.
        Assert.Equal(1 + (2 * 2) + 2, world.Worktrees.Captures);
    }

    [Fact]
    public async Task A_change_in_the_users_repository_during_a_round_raises_its_alert_and_aborts_without_publishing_the_round()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Hold = true;

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await WaitUntilAsync(() => world.Claude.Runs.Count == 2);
        world.Worktrees.State = ScriptedDebateWorktrees.Clean() with { HeadCommit = new string('b', 40) };
        world.Claude.Release();
        var result = await CompletedAsync(run);

        Assert.Equal((DebatePhase.Closed, DebateOutcome.Aborted), (result.Phase, result.Outcome));
        var log = await world.DebateLogAsync(run.Id);
        Assert.Empty(log.Contributions);
        var alert = Assert.Single(log.Of<AuditAlertPayload>()).Payload;
        Assert.Equal(DebateRepositoryGuard.ChangedAlert, alert.Code);
        Assert.Contains("during round 1", alert.Message, StringComparison.Ordinal);
        Assert.Contains("repositorio del usuario cambió", log.Closed!.Value.Payload.Reason, StringComparison.Ordinal);
        Assert.Empty(world.Judgment.Calls);
        Assert.DoesNotContain(world.Evidence.Drafts, d => d.Kind == EvidenceKind.AgentStatement);
    }

    [Fact]
    public async Task A_change_in_the_users_repository_while_the_judges_work_aborts_the_debate_instead_of_closing_it()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);
        var judging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Judgment.Hold = judging;

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await WaitUntilAsync(() => world.Judgment.Calls.Count == 1);
        world.Worktrees.State = ScriptedDebateWorktrees.Clean() with { PorcelainStatus = ["?? nuevo.txt"] };
        judging.SetResult();
        var result = await CompletedAsync(run);

        Assert.Equal(DebateOutcome.Aborted, result.Outcome);
        var log = await world.DebateLogAsync(run.Id);
        Assert.Equal(2, log.Contributions.Count);
        Assert.Contains("during the judgment", Assert.Single(log.Of<AuditAlertPayload>()).Payload.Message, StringComparison.Ordinal);
        Assert.Equal(DebateClosedPayload.AbortedRule, log.Closed!.Value.Payload.Rule);
    }

    [Fact]
    public async Task No_payload_nor_summary_of_the_log_carries_text_of_a_participant_or_a_judge()
    {
        using var world = new ScriptedDebateWorld();
        world.Judgment.SummaryPrefix = Marker + "-JUEZ";
        var invalid = 1;
        world.Claude.Reply = (r, _) =>
        {
            if (r.TaskId == "r1-arquitecto" && Interlocked.Exchange(ref invalid, 0) == 1)
            {
                // Its one correction: the problems name the members and the values admitted, never what it wrote.
                return ScriptedDebateProvider.Valid(r, customize: a =>
                {
                    a["kind"] = Marker;
                    a[Marker] = Marker;
                });
            }

            return Marked(r, finished: r.TaskId!.StartsWith("r2-", StringComparison.Ordinal));
        };

        await using var run = await world.OpenAsync(world.FreeOnAnthropic(subject: new DebateSubject(DebateSubjectKind.Commit, null)));
        var result = await CompletedAsync(run);

        Assert.Equal(DebateOutcome.Resolved, result.Outcome);
        var log = await world.LogAsync();
        Assert.NotEmpty(log.Entries);
        foreach (var entry in log.Entries)
        {
            Assert.DoesNotContain(Marker, entry.Summary, StringComparison.Ordinal);
            using var payload = JsonDocument.Parse(entry.PayloadJson);
            Assert.All(Strings(payload.RootElement), text => Assert.DoesNotContain(Marker, text, StringComparison.Ordinal));
        }

        // What the participants wrote lives in the store, as evidence the log names by id and hash.
        var debate = await world.DebateLogAsync(run.Id);
        Assert.Equal(4, debate.Contributions.Count);
        Assert.All(debate.Contributions, c =>
        {
            Assert.Contains(Marker, world.Evidence.ContentText(c.Payload.Evidence), StringComparison.Ordinal);
            Assert.Equal(["CITA-2: NotFound"], c.Payload.UnresolvedCitations);
            var cited = Assert.Single(c.Payload.Cites);
            Assert.Equal("// línea 2\n// línea 3\n// línea 4\n", world.Evidence.ContentText(cited));
        });
        var citations = debate.Evidence.Where(e => e.Payload.Kind == EvidenceKind.Document && e.Payload.Origin == EvidenceOrigin.CapturedBySystem).ToList();
        Assert.Equal(4, citations.Count);
        Assert.All(citations, c => Assert.Equal(world.Worktrees.HeadCommit, c.Payload.SubjectCommit));
        Assert.DoesNotContain(world.Claude.Requests.Where(r => r.TaskId!.StartsWith("r1-", StringComparison.Ordinal)), r => r.Prompt.Contains(Marker, StringComparison.Ordinal));
    }

    /// <summary>A valid contribution whose every text carries the marker, citing three lines of the copy and a file that is not there.</summary>
    private static ScriptedDebateReply Marked(AgentRunRequest request, bool finished)
    {
        var answer = ScriptedDebateProvider.Answer(request, finished);
        answer["summary"] = Marker + " resumen";
        answer["argument"] = Marker + " argumento";
        answer["claims"] = new JsonArray(new JsonObject { ["claim"] = Marker + " afirmación", ["support"] = new JsonArray("CITA-1", "CITA-2") });
        answer["citations"] = new JsonArray(
            new JsonObject { ["id"] = "CITA-1", ["path"] = "src/Cache.cs", ["startLine"] = 2, ["endLine"] = 4, ["why"] = Marker + " por qué" },
            new JsonObject { ["id"] = "CITA-2", ["path"] = "docs/" + Marker + ".md", ["startLine"] = 1, ["endLine"] = 2, ["why"] = Marker + " otra" });
        return new ScriptedDebateReply(AgentRunStatus.Succeeded, answer.ToJsonString(), ScriptedDebateProvider.Usage(1_000, 100));
    }
}
