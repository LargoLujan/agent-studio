using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// The options of Phase 8 in <c>orchestration_runs.options_json</c> (docs/16 D-36, D-37 and D-39): the margin after a reset,
/// the cap of a wait without one and the consent to resume by itself are written only when they are not the defaults, like the
/// isolation of a step (ADR-0016). So a header stored before them is stored and read exactly as it was, and hashes the same.
/// </summary>
public sealed class RunOptionsStorageTests : IDisposable
{
    /// <summary>The options of <see cref="PersistenceTestData.SeedRunAsync"/> exactly as Agent Studio stored them before Phase 8.</summary>
    private const string StoredBeforePhase8 = """{"maxParallelTasks":2,"maxAgentRuns":12,"stallThreshold":"00:07:00","maxAutoWaitForQuota":"00:01:30.5000000"}""";

    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RunRecord WithPhase8Options(RunRecord run) => run with
    {
        Options = run.Options with { QuotaResetMargin = TimeSpan.FromMinutes(4), MaxEstimatedQuotaWait = TimeSpan.FromMinutes(45), AutoResumeConsent = true },
    };

    [Fact]
    public async Task Options_at_the_defaults_of_phase_8_are_stored_as_they_were_before_it()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var run = await SeedRunAsync(instance);

        await instance.Runs.CreateAsync(run, Ct);

        using var connection = instance.Connect();
        Assert.Equal(StoredBeforePhase8, Text(connection, $"SELECT options_json FROM orchestration_runs WHERE id = '{run.Id.Value}'"));
    }

    [Fact]
    public async Task The_options_of_phase_8_survive_the_store_and_the_header_hashes_the_same_after_reopening()
    {
        RunRecord run;
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            run = WithPhase8Options(await SeedRunAsync(first));
            await first.Runs.CreateAsync(run, Ct);
            using var connection = first.Connect();
            Assert.Equal(
                """{"maxParallelTasks":2,"maxAgentRuns":12,"stallThreshold":"00:07:00","maxAutoWaitForQuota":"00:01:30.5000000","quotaResetMargin":"00:04:00","maxEstimatedQuotaWait":"00:45:00","autoResumeConsent":true}""",
                Text(connection, $"SELECT options_json FROM orchestration_runs WHERE id = '{run.Id.Value}'"));
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var found = await second.Runs.FindAsync(second.ProjectId, run.Id, Ct);

        Assert.NotNull(found);
        Assert.Equal(run.Options, found.Options);
        Assert.Equal(RunHeaderHash.Of(run), RunHeaderHash.Of(found));
        await second.Runs.CreateAsync(run, Ct); // the same header again is harmless: it is stored as the same text
    }

    /// <summary>A row written before the options existed (no field at all) reads with their defaults: no consent, the engine's margin and cap.</summary>
    [Fact]
    public async Task A_header_stored_before_phase_8_reads_with_the_defaults_and_is_still_the_same_header()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var seeded = await SeedRunAsync(instance);
        var run = WithPhase8Options(seeded);
        await instance.Runs.CreateAsync(run, Ct);
        using (var connection = instance.Connect())
        {
            Exec(connection, "DROP TRIGGER orchestration_runs_no_update");
            Exec(connection, $"UPDATE orchestration_runs SET options_json = '{StoredBeforePhase8}' WHERE id = '{run.Id.Value}'");
        }

        var found = await instance.Runs.FindAsync(instance.ProjectId, run.Id, Ct);

        Assert.NotNull(found);
        Assert.Null(found.Options.QuotaResetMargin);
        Assert.Null(found.Options.MaxEstimatedQuotaWait);
        Assert.False(found.Options.AutoResumeConsent);
        Assert.Equal(RunHeaderHash.Of(seeded), RunHeaderHash.Of(found));
        Assert.NotEqual(RunHeaderHash.Of(run), RunHeaderHash.Of(found));
    }

    /// <summary>
    /// A header stored with odd waits (before a new run had them checked) still reads as it was stored and hashes the same, so
    /// its anchored hash verifies and the run replays; the engine resolves the waits it uses to sensible ones.
    /// </summary>
    [Fact]
    public async Task A_header_stored_with_odd_waits_reads_as_it_was_and_hashes_the_same()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var run = await SeedRunAsync(instance);
        run = run with { Options = run.Options with { QuotaResetMargin = TimeSpan.FromMinutes(-2), MaxEstimatedQuotaWait = TimeSpan.Zero, AutoResumeConsent = true } };
        await instance.Runs.CreateAsync(run, Ct);

        var found = await instance.Runs.FindAsync(instance.ProjectId, run.Id, Ct);

        Assert.NotNull(found);
        Assert.Equal(run.Options, found.Options);
        Assert.Equal(RunHeaderHash.Of(run), RunHeaderHash.Of(found));
        Assert.Equal((TimeSpan.Zero, RunOptions.MinEstimatedQuotaWait), (found.Options.ResolvedQuotaResetMargin(), found.Options.ResolvedMaxEstimatedQuotaWait()));
    }

    [Fact]
    public async Task A_consent_not_given_is_not_written_and_a_margin_given_is()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var run = await SeedRunAsync(instance);
        run = run with { Options = run.Options with { QuotaResetMargin = TimeSpan.FromMinutes(3), AutoResumeConsent = false } };

        await instance.Runs.CreateAsync(run, Ct);

        using var connection = instance.Connect();
        var stored = Text(connection, $"SELECT options_json FROM orchestration_runs WHERE id = '{run.Id.Value}'");
        Assert.EndsWith(",\"quotaResetMargin\":\"00:03:00\"}", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("autoResumeConsent", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("maxEstimatedQuotaWait", stored, StringComparison.Ordinal);
    }
}
