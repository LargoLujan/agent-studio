using AgentStudio.Application.Decisions;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Integration.Phase3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Integration.Phase4;

/// <summary>
/// The reconciliation of the usage panel (docs/14 D-08) with the real writers on both sides: an agent run outside an
/// orchestration (<see cref="AuxiliaryAgentRunner"/>) records <c>AgentRunStarted</c> version 2 in the project's SQLite log,
/// and the provider pipeline writes its prompt in the project's Markdown journal; the report (<see cref="IUsageReport"/>)
/// reads both back. The clock is fixed, so the instant of both writes is known.
/// </summary>
public sealed class JournalReconciliationTests
{
    /// <summary>Real time: the clock of the app is fixed, so nothing in it may wait on that clock.</summary>
    private static readonly TimeSpan Guard = TimeSpan.FromMinutes(2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// One auxiliary run at <paramref name="at"/> (UTC, which is also the clock's local zone): its start in the log and
    /// its prompt in the journal at that very instant. Returns the reconciliation of a report of the hour around it.
    /// </summary>
    private static async Task<UsageReconciliation> ReconcileOneRunAtAsync(DateTimeOffset at)
    {
        using var temp = new TempDirectory();
        var clock = new FakeTimeProvider(at);
        var claude = ScriptedCli.Claude(TimeProvider.System);
        var services = Phase4Composition.Services(Phase4Paths.Under(temp.Path), new ScriptedApprovalPrompt(), Phase4ScriptedProviders.Over(claude, ScriptedCli.Codex(TimeProvider.System)))
            .WithoutCliProbes();
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
        await using var app = await Phase4App.OpenAsync(temp.CreateDirectory("proyecto"), services);

        var agent = new AgentDefinition(
            new AgentId("resumidor"),
            "Resumidor",
            AgentRole.Architect,
            new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"),
            string.Empty,
            [],
            ["Read"],
            [],
            new ExecutionBudget(2, TimeSpan.FromMinutes(1)),
            1,
            MemoryPolicy.None,
            new ReviewPolicy(false, false));
        var request = new AgentRunRequest(agent, "Resume el día.", temp.CreateDirectory("vacia"), new PermissionProfile(false, false, false, ["Read"], [], []), Purpose: "Resumen diario", Tags: ["resumen"]);
        await app.Get<IAuxiliaryAgentRunner>().RunAsync(app.Project, new AuxiliaryRunRequest(AgentRunPurpose.DailySummary, request), null, Ct);

        var report = await app.Get<IUsageReport>().QueryAsync([app.Project.RootPath], new UsageQuery(at.AddHours(-1), at.AddHours(1), TimeZoneInfo.Utc, []), Ct);
        Assert.Equal(AgentRunPurpose.DailySummary, Assert.Single(report.Attempts).Purpose);
        return report.Journal;
    }

    [Fact]
    public async Task On_a_whole_second_the_prompt_of_the_journal_and_the_attempt_of_the_log_agree()
    {
        var journal = await ReconcileOneRunAtAsync(new DateTimeOffset(2026, 10, 18, 9, 30, 15, TimeSpan.Zero)).WaitAsync(Guard, Ct);

        Assert.Equal(((int?)1, 1, (int?)0), (journal.JournalPrompts, journal.RecordedAttempts, journal.Difference));
        Assert.DoesNotContain(journal.Warnings, w => w.StartsWith("Bitácora:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_first_prompt_of_phase_4_counts_even_when_it_is_written_in_the_same_second_as_the_first_start()
    {
        // The journal writes the time to the second (MarkdownDailyJournal: HH:mm:ss) and the log to the tick: a prompt
        // journaled 600 ms into the second of the first start of Phase 4 must still be counted.
        var journal = await ReconcileOneRunAtAsync(new DateTimeOffset(2026, 10, 18, 9, 30, 15, 600, TimeSpan.Zero)).WaitAsync(Guard, Ct);

        Assert.Equal(((int?)1, 1, (int?)0), (journal.JournalPrompts, journal.RecordedAttempts, journal.Difference));
        Assert.DoesNotContain(journal.Warnings, w => w.StartsWith("Bitácora:", StringComparison.Ordinal));
    }
}
