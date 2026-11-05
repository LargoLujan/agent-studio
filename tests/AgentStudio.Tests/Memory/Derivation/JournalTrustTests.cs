using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Decisions;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory;
using AgentStudio.Memory.Derivation;
using AgentStudio.Security;
using AgentStudio.Tests.Infrastructure.Persistence;
using AgentStudio.Tests.Memory.Store;
using Microsoft.Extensions.DependencyInjection;
using static AgentStudio.Tests.Memory.Derivation.JournalWorld;

namespace AgentStudio.Tests.Memory.Derivation;

/// <summary>
/// «Confiar en la bitácora de este proyecto», and the same by days (docs/17 D-02; ADR-0047 and its addendum of 2026-11-03):
/// a decision of a person, in the log outside every run, that takes the journal entries Agent Studio did not write out of
/// quarantine and puts them back when it is withdrawn. Its precedence: a retraction of the entry, then a review of the
/// entry, then the decision about its day, then the one about the project. What is inherited is reviewed one by one, a
/// decision that came with an inherited history does not count, and like every write of the memory it fails closed and
/// waits while a run goes on.
/// </summary>
public sealed class JournalTrustTests
{
    private static readonly DateOnly Day3 = new(2026, 3, 12);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task SetAsync(JournalWorld world, JournalTrustScope scope, DateOnly? day, bool trusted, string? comment = null) =>
        world.Trust.SetAsync(world.World.Project, scope, day, trusted, "manuel", comment, Ct);

    private static async Task<IReadOnlyList<(DecisionEntry Entry, JournalTrustChangedPayload Decision)>> DecisionsAsync(JournalWorld world) =>
        [.. (await world.World.World.EntriesAsync(DecisionKind.JournalTrustChanged)).Select(e => (e, DecisionPayloadSerializer.Deserialize<JournalTrustChangedPayload>(e.PayloadJson)))];

    /// <summary>A journal of two days written by the hook: one entry on the first, one on the second, anchored.</summary>
    private static async Task<JournalWorld> TwoDaysAsync()
    {
        var world = await CreateAsync();
        world.Append(Day1, Hook("10:00:00", "uno"), "  - Hecho: hecho uno.");
        world.Append(Day2, Hook("11:00:00", "dos"));
        Assert.Equal(2, (await world.DeriveAsync()).Recorded.Count);
        return world;
    }

    private static async Task<MemoryEntry> EntryOfAsync(JournalWorld world, DateOnly day) =>
        Assert.Single(await world.JournalAsync(), e => e.Source.StartsWith(MemorySources.JournalPrefix + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "#", StringComparison.Ordinal));

    [Fact]
    public async Task Trusting_the_project_takes_its_journal_out_of_quarantine_and_withdrawing_it_puts_it_back()
    {
        using var world = await TwoDaysAsync();
        Assert.Empty(await world.TrustedJournalAsync());
        Assert.Equal((false, 0, 2), Read(await world.Trust.ReadAsync(world.World.Project, Ct)));

        await SetAsync(world, JournalTrustScope.Project, null, true, "Son mis prompts.");

        Assert.Equal(2, (await world.TrustedJournalAsync()).Count);
        Assert.Equal((true, 0, 0), Read(await world.Trust.ReadAsync(world.World.Project, Ct)));
        var (entry, decision) = Assert.Single(await DecisionsAsync(world));
        Assert.Equal((ActorKind.User, "manuel"), (entry.Actor.Kind, entry.Actor.Id));
        Assert.Equal((DecisionPayload.NoRun, JournalTrustScope.Project, true, "Son mis prompts."), (decision.RunId, decision.Scope, decision.Trusted, decision.Comment));

        // The days to come are covered too.
        world.Append(Day3, Hook("09:00:00", "tres"));
        Assert.Equal(MemoryTrust.Trusted, Assert.Single((await world.DeriveAsync()).Recorded).Trust);

        await SetAsync(world, JournalTrustScope.Project, null, false);

        Assert.Empty(await world.TrustedJournalAsync());
        Assert.Equal((false, 0, 3), Read(await world.Trust.ReadAsync(world.World.Project, Ct)));
        Assert.Equal(2, (await DecisionsAsync(world)).Count);
    }

    [Fact]
    public async Task The_decision_about_a_day_goes_before_the_one_about_the_project()
    {
        using var world = await TwoDaysAsync();

        await SetAsync(world, JournalTrustScope.Day, Day1, true);

        Assert.Equal(MemoryTrust.Trusted, (await EntryOfAsync(world, Day1)).Trust);
        Assert.Equal(MemoryTrust.Quarantined, (await EntryOfAsync(world, Day2)).Trust);
        Assert.Equal([Day1], (await world.Trust.ReadAsync(world.World.Project, Ct)).TrustedDays);

        // The project is trusted, but its second day was explicitly not: the day decides.
        await SetAsync(world, JournalTrustScope.Project, null, true);
        await SetAsync(world, JournalTrustScope.Day, Day2, false);

        Assert.Equal(MemoryTrust.Trusted, (await EntryOfAsync(world, Day1)).Trust);
        Assert.Equal(MemoryTrust.Quarantined, (await EntryOfAsync(world, Day2)).Trust);
        Assert.Equal((true, 1, 1), Read(await world.Trust.ReadAsync(world.World.Project, Ct)));
    }

    [Fact]
    public async Task A_review_of_the_entry_goes_before_the_journal_and_the_most_recent_one_decides()
    {
        using var world = await TwoDaysAsync();
        var memory = world.World.World.Memory;
        var project = world.World.Project;
        var first = await EntryOfAsync(world, Day1);
        var second = await EntryOfAsync(world, Day2);

        // Accepting one entry is a review: it stays trusted when the journal's trust comes and goes.
        await memory.ReviewAsync(project, first.Id, MemoryReview.Accepted, "manuel", null, Ct);
        await SetAsync(world, JournalTrustScope.Project, null, true);
        await memory.ReviewAsync(project, second.Id, MemoryReview.Rejected, "manuel", "No es mío.", Ct);
        await SetAsync(world, JournalTrustScope.Project, null, false);

        Assert.Equal(MemoryTrust.Trusted, (await EntryOfAsync(world, Day1)).Trust);
        Assert.Equal(MemoryTrust.Rejected, (await EntryOfAsync(world, Day2)).Trust);

        // Within the entry, the most recent review decides, as in the rest of the memory: an acceptance after a retraction
        // brings it back, and a retraction after it takes it out again even with its day trusted.
        await memory.ReviewAsync(project, first.Id, MemoryReview.Retracted, "manuel", null, Ct);
        Assert.Equal(MemoryTrust.Retracted, (await EntryOfAsync(world, Day1)).Trust);
        await memory.ReviewAsync(project, first.Id, MemoryReview.Accepted, "manuel", null, Ct);
        Assert.Equal(MemoryTrust.Trusted, (await EntryOfAsync(world, Day1)).Trust);
        await SetAsync(world, JournalTrustScope.Day, Day1, true);
        await memory.ReviewAsync(project, first.Id, MemoryReview.Retracted, "manuel", null, Ct);

        Assert.Equal(MemoryTrust.Retracted, (await EntryOfAsync(world, Day1)).Trust);
        Assert.Empty(await world.TrustedJournalAsync());
    }

    [Fact]
    public async Task An_entry_inherited_from_a_history_accepted_as_a_new_origin_is_reviewed_one_by_one()
    {
        using var world = await TwoDaysAsync();
        foreach (var anchor in Directory.GetFiles(world.World.World.AnchorFolder))
        {
            File.Delete(anchor);
        }

        await world.ReopenAsync();
        await world.World.AcceptHistoryAsNewOriginAsync();
        await SetAsync(world, JournalTrustScope.Project, null, true);
        world.Append(Day3, Hook("09:00:00", "después"));
        var fresh = Assert.Single((await world.DeriveAsync()).Recorded);

        Assert.Equal(MemoryTrust.Trusted, fresh.Trust);
        Assert.Equal(MemoryTrust.Quarantined, (await EntryOfAsync(world, Day1)).Trust);
        Assert.Equal(MemoryTrust.Quarantined, (await EntryOfAsync(world, Day2)).Trust);

        await world.World.World.Memory.ReviewAsync(world.World.Project, (await EntryOfAsync(world, Day1)).Id, MemoryReview.Accepted, "manuel", null, Ct);

        Assert.Equal(MemoryTrust.Trusted, (await EntryOfAsync(world, Day1)).Trust);
        Assert.Equal(MemoryTrust.Quarantined, (await EntryOfAsync(world, Day2)).Trust);
    }

    [Fact]
    public async Task A_decision_that_changes_nothing_records_nothing()
    {
        using var world = await TwoDaysAsync();

        await SetAsync(world, JournalTrustScope.Project, null, false);
        await SetAsync(world, JournalTrustScope.Project, null, true);
        await SetAsync(world, JournalTrustScope.Project, null, true);

        // A day without a decision follows the project, so «not trusted» changes it.
        await SetAsync(world, JournalTrustScope.Day, Day1, false);
        await SetAsync(world, JournalTrustScope.Day, Day1, false);

        var decisions = await DecisionsAsync(world);
        Assert.Equal([(JournalTrustScope.Project, true), (JournalTrustScope.Day, false)], decisions.Select(d => (d.Decision.Scope, d.Decision.Trusted)));
        Assert.Equal(MemoryTrust.Quarantined, (await EntryOfAsync(world, Day1)).Trust);
        Assert.Equal(MemoryTrust.Trusted, (await EntryOfAsync(world, Day2)).Trust);
    }

    [Fact]
    public async Task Nothing_is_read_nor_decided_on_a_log_that_does_not_verify()
    {
        using var world = await TwoDaysAsync();
        foreach (var anchor in Directory.GetFiles(world.World.World.AnchorFolder))
        {
            // The log now looks cut short: its anchor says there were more entries.
            var json = await File.ReadAllTextAsync(anchor, Ct);
            await File.WriteAllTextAsync(anchor, Regex.Replace(json, "\"entryCount\": \\d+", "\"entryCount\": 999"), Ct);
        }

        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => SetAsync(world, JournalTrustScope.Project, null, true));
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => world.Trust.ReadAsync(world.World.Project, Ct));
        Assert.Empty(await DecisionsAsync(world));
    }

    [Fact]
    public async Task A_decision_waits_while_a_run_or_a_planning_goes_on()
    {
        using var world = await TwoDaysAsync();
        var activity = world.World.World.Activity;
        activity.Busy = true;
        var checks = activity.Checks;

        var pending = SetAsync(world, JournalTrustScope.Project, null, true);
        await activity.WaitForChecksAsync(checks + 1);

        Assert.False(pending.IsCompleted);
        Assert.Empty(await DecisionsAsync(world));

        activity.Busy = false;
        await world.World.World.AdvanceUntilAsync(pending);

        Assert.Single(await DecisionsAsync(world));
    }

    [Fact]
    public async Task A_decision_names_its_day_only_when_it_is_about_a_day()
    {
        using var world = await TwoDaysAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => SetAsync(world, JournalTrustScope.Day, null, true));
        await Assert.ThrowsAsync<ArgumentException>(() => SetAsync(world, JournalTrustScope.Project, Day1, true));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Trust.SetAsync(world.World.Project, JournalTrustScope.Project, null, true, " ", null, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SetAsync(world, (JournalTrustScope)7, null, true));
        Assert.Empty(await DecisionsAsync(world));
    }

    [Fact]
    public async Task The_comment_of_a_decision_is_redacted()
    {
        using var world = await TwoDaysAsync();

        await SetAsync(world, JournalTrustScope.Day, Day1, true, @"Revisado en C:\Users\persona\notas con token=ghp_abcdefghijklmnopqrstuvwxyz0123456789");

        var comment = Assert.Single(await DecisionsAsync(world)).Decision.Comment;
        Assert.DoesNotContain("ghp_", comment, StringComparison.Ordinal);
        Assert.Contains(@"~\notas", comment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_service_of_the_decision_is_composed_with_the_memory()
    {
        using var folder = new TempProjectFolder();
        var anchors = Path.Combine(Path.GetTempPath(), "as-anclas-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var services = new ServiceCollection();
            services.AddAgentStudioPersistence(new DecisionLogAnchorOptions(anchors));
            services.AddAgentStudioSecurity();
            services.AddAgentStudioProjectMemory();
            services.AddSingleton<AgentStudio.Application.Orchestration.IRunActivity>(new FakeRunActivity());
            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            var project = await provider.GetRequiredService<IProjectWorkspace>().OpenAsync(folder.Root, Ct);

            var trust = provider.GetRequiredService<IJournalTrust>();

            Assert.IsType<JournalTrust>(trust);
            Assert.Same(trust, provider.GetRequiredService<IJournalTrust>());
            Assert.Equal((false, 0, 0), Read(await trust.ReadAsync(project, Ct)));
        }
        finally
        {
            TempProjectFolder.ForceDelete(anchors);
        }
    }

    private static (bool Project, int Days, int Quarantined) Read(JournalTrustState state) => (state.ProjectTrusted, state.TrustedDays.Count, state.QuarantinedEntries);
}
