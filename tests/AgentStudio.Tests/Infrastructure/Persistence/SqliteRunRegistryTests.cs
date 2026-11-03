using AgentStudio.Application.Execution;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Orchestration;
using Microsoft.Data.Sqlite;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

public sealed class SqliteRunRegistryTests : IDisposable
{
    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_header_is_found_after_reopening_exactly_as_it_was_created()
    {
        RunRecord run;
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            run = await SeedRunAsync(first);
            await first.Runs.CreateAsync(run, Ct);
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var found = await second.Runs.FindAsync(second.ProjectId, run.Id, Ct);

        Assert.NotNull(found);
        Assert.Equal(Shape(run), Shape(found));
        Assert.Equal(run.Verification.ContentHash, found.Verification.ContentHash);
    }

    [Fact]
    public async Task The_hash_RunStarted_anchors_is_the_hash_of_the_header_read_back_after_reopening()
    {
        RunRecord run;
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            run = await SeedRunAsync(first);
            await first.Runs.CreateAsync(run, Ct);
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var found = await second.Runs.FindAsync(second.ProjectId, run.Id, Ct);

        // Every member of every agent (the full roster), the options with their timings and the request survive the store.
        Assert.NotNull(found);
        Assert.Equal(RunHeaderHash.Of(run), RunHeaderHash.Of(found));
    }

    [Fact]
    public async Task A_step_the_profile_runs_without_isolation_is_found_without_isolation_after_reopening()
    {
        RunRecord run;
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            run = await SeedRunAsync(first);
            var steps = run.Verification.Steps;
            run = run with { Verification = run.Verification with { Steps = [steps[0] with { Isolation = ProcessIsolation.None }, .. steps.Skip(1)] } };
            await first.Runs.CreateAsync(run, Ct);
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var found = await second.Runs.FindAsync(second.ProjectId, run.Id, Ct);

        Assert.NotNull(found);
        Assert.Equal([ProcessIsolation.None, ProcessIsolation.LowIntegrity], found.Verification.Steps.Select(s => s.Isolation));
        Assert.Equal(run.Verification.ContentHash, found.Verification.ContentHash);
    }

    [Fact]
    public async Task A_step_that_requires_another_is_found_with_its_requirement_after_reopening_and_the_header_hashes_as_anchored()
    {
        // B6: a resumed run builds before it tests, as it did, and resumes at all: its profile hashes to what RunStarted holds.
        RunRecord run;
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            run = await SeedRunAsync(first);
            var steps = run.Verification.Steps;
            run = run with { Verification = run.Verification with { Steps = [steps[0], steps[1] with { Requires = [steps[0].Id] }, .. steps.Skip(2)] } };
            await first.Runs.CreateAsync(run, Ct);
            await first.Runs.CreateAsync(run, Ct);
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var found = await second.Runs.FindAsync(second.ProjectId, run.Id, Ct);

        Assert.NotNull(found);
        Assert.Null(found.Verification.Steps[0].Requires);
        Assert.Equal([run.Verification.Steps[0].Id], found.Verification.Steps[1].Requires);
        Assert.Equal(run.Verification.ContentHash, found.Verification.ContentHash);
        Assert.Equal(RunHeaderHash.Of(run), RunHeaderHash.Of(found));
    }

    [Fact]
    public async Task A_header_stored_before_steps_had_an_isolation_reads_every_step_as_isolated_and_is_still_the_same_header()
    {
        // The frozen profile exactly as Agent Studio stored it before a step could turn isolation off: no such field.
        const string Stored =
            """{"name":"dotnet","steps":[{"id":"build","title":"Compilar","executable":"dotnet","arguments":["build","--no-restore"],"kind":"BuildLog","report":"None","timeout":"00:10:00"},"""
            + """{"id":"test","title":"Tests","executable":"dotnet","arguments":["test","--no-build","--","--report-xunit-trx","--report-xunit-trx-filename","r.trx"],"kind":"TestRun","report":"Trx","reportPath":"TestResults/r.trx","timeout":"00:20:00"}]}""";
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var run = await SeedRunAsync(instance);
        await instance.Runs.CreateAsync(run, Ct);
        using (var connection = instance.Connect())
        {
            Exec(connection, "DROP TRIGGER orchestration_runs_no_update");
            Exec(connection, $"UPDATE orchestration_runs SET verification_profile_json = '{Stored}' WHERE id = '{run.Id.Value}'");
        }

        var found = await instance.Runs.FindAsync(instance.ProjectId, run.Id, Ct);

        // Without the field a step is isolated: the safe default.
        Assert.NotNull(found);
        Assert.All(found.Verification.Steps, s => Assert.Equal(ProcessIsolation.LowIntegrity, s.Isolation));
        Assert.Equal(run.Verification.ContentHash, found.Verification.ContentHash);

        // And a profile that isolates every step is still written as that text: creating the run again stays harmless.
        await instance.Runs.CreateAsync(run, Ct);
    }

    [Fact]
    public async Task Runs_are_listed_in_the_order_they_were_created_and_a_follow_up_names_the_run_it_continues()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var first = await SeedRunAsync(instance, "20261015-aaaaaa");
        await instance.Runs.CreateAsync(first, Ct);
        var second = await SeedRunAsync(instance, "20261014-bbbbbb", follows: first.Id);
        await instance.Runs.CreateAsync(second, Ct);

        var runs = await instance.Runs.ListAsync(instance.ProjectId, Ct);

        Assert.Equal([first.Id, second.Id], runs.Select(r => r.Id));
        Assert.Equal(first.Id, runs[1].Follows);
        Assert.Null(runs[0].Follows);
    }

    [Fact]
    public async Task Creating_the_same_run_again_is_harmless()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var run = await SeedRunAsync(instance);

        await instance.Runs.CreateAsync(run, Ct);
        await instance.Runs.CreateAsync(run, Ct);

        Assert.Single(await instance.Runs.ListAsync(instance.ProjectId, Ct));
    }

    [Fact]
    public async Task The_same_id_with_another_header_is_refused_and_the_first_header_stays()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var run = await SeedRunAsync(instance);
        await instance.Runs.CreateAsync(run, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => instance.Runs.CreateAsync(run with { BaseCommit = "ffffffffffffffffffffffffffffffffffffffff" }, Ct));

        Assert.Equal(run.BaseCommit, (await instance.Runs.FindAsync(instance.ProjectId, run.Id, Ct))?.BaseCommit);
    }

    [Fact]
    public async Task A_run_must_point_at_a_stored_plan()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var run = await SeedRunAsync(instance);

        var ex = await Assert.ThrowsAsync<SqliteException>(() => instance.Runs.CreateAsync(run with { PlanHash = new string('c', 64) }, Ct));

        Assert.Contains("FOREIGN KEY", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unknown_run_finds_nothing()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);

        Assert.Null(await instance.Runs.FindAsync(instance.ProjectId, new RunId("20261015-nadaaa"), Ct));
        Assert.Empty(await instance.Runs.ListAsync(instance.ProjectId, Ct));
    }
}
