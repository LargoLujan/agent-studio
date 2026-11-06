using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Models;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// What Phase 13 adds to the stored template and to the stored header of a run (docs/17 D-25 and D-33): the effort of an agent
/// and the consent to answer with the recommended option. Like the options of Phase 8, each is written only when it is set, so
/// a template or a header stored before them is stored and read exactly as it was, and hashes the same; and with them, they
/// survive the store, so a template saved with an effort still starts a run and a header with the consent still resumes.
/// </summary>
public sealed class Phase13StorageTests : IDisposable
{
    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agent_without_an_effort_is_stored_without_the_field_and_one_with_it_keeps_it_after_reopening()
    {
        var roster = FullRoster();
        var withEffort = roster[^1] with { Effort = ReasoningEffort.XHigh };
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            await first.Roster.SaveAsync(first.ProjectId, roster[0], Ct);
            await first.Roster.SaveAsync(first.ProjectId, withEffort, Ct);
            using var connection = first.Connect();
            Assert.DoesNotContain("effort", Text(connection, $"SELECT definition_json FROM agents WHERE id = '{roster[0].Id.Value}'"), StringComparison.Ordinal);
            Assert.Contains("\"effort\":\"XHigh\"", Text(connection, $"SELECT definition_json FROM agents WHERE id = '{withEffort.Id.Value}'"), StringComparison.Ordinal);
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var listed = await second.Roster.ListAsync(second.ProjectId, Ct);

        Assert.Null(listed[0].Effort);
        Assert.Equal(ReasoningEffort.XHigh, listed[1].Effort);
        Assert.Equal(RosterHash.Of([roster[0], withEffort]), RosterHash.Of(listed));
    }

    [Fact]
    public async Task The_additions_of_phase_13_survive_the_header_of_a_run_and_it_hashes_the_same_after_reopening()
    {
        RunRecord run;
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            var seeded = await SeedRunAsync(first);
            run = seeded with
            {
                Roster = [.. seeded.Roster.Select((a, i) => i == 0 ? a with { Effort = ReasoningEffort.Low } : a)],
                Options = seeded.Options with { AnswerWithRecommendedAfter = TimeSpan.FromMinutes(20) },
            };
            await first.Runs.CreateAsync(run, Ct);
            using var connection = first.Connect();
            Assert.Equal(
                """{"maxParallelTasks":2,"maxAgentRuns":12,"stallThreshold":"00:07:00","maxAutoWaitForQuota":"00:01:30.5000000","answerWithRecommendedAfter":"00:20:00"}""",
                Text(connection, $"SELECT options_json FROM orchestration_runs WHERE id = '{run.Id.Value}'"));
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var found = await second.Runs.FindAsync(second.ProjectId, run.Id, Ct);

        Assert.NotNull(found);
        Assert.Equal(TimeSpan.FromMinutes(20), found.Options.AnswerWithRecommendedAfter);
        Assert.Equal(ReasoningEffort.Low, found.Roster[0].Effort);
        Assert.Equal(RunHeaderHash.Of(run), RunHeaderHash.Of(found));
    }
}
