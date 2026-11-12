using System.Globalization;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Observability;
using AgentStudio.Domain.Metrics;
using AgentStudio.Observability.Usage;
using AgentStudio.Tests.Memory;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.Observability;

/// <summary>
/// The usage panel of Phase 9 (docs/16 D-34, F9-6): the milestone and the three axes of the activity are dimensions the person can
/// pick, every group says the wait in the queue and the wait for the usage limit apart from its duration, what a milestone shares is
/// said apart, and «Por hito y por tipo de trabajo» splits the same attempts on each axis so its rows add up to the total. The report
/// is grouped by the real <see cref="UsageAggregator"/>, so what is added up is what the projection gives.
/// </summary>
public sealed class UsageDashboardPhase9Tests
{
    private const string Root = @"C:\proyectos\calc";

    private static readonly DateTimeOffset Now = new(2026, 10, 18, 10, 0, 0, TimeSpan.FromHours(2));

    /// <summary>
    /// Four attempts: two in milestone A (an implementation and its retry that was discarded, with a saved version and a wait for the
    /// usage limit), one in A and B at once (tests, kept) and one in none (analysis, pending).
    /// </summary>
    internal static IReadOnlyList<UsageAttempt> Attempts { get; } =
    [
        (UsageSamples.ClaudeAttempt with { RunId = "r1", TaskId = "T1", Attempt = 1 }).WithPhase9(["mil-a"], ActivityKind.Implementation, AttemptOrder.First, AttemptFate.Kept, false),
        (UsageSamples.ClaudeAttempt with { RunId = "r1", TaskId = "T2", Attempt = 2, RecordedAt = UsageSamples.Day.AddSeconds(-20), QuotaWaitMilliseconds = 90_000 })
            .WithPhase9(["mil-a"], ActivityKind.Implementation, AttemptOrder.Retry, AttemptFate.Discarded, true),
        (UsageSamples.ClaudeAttempt with { RunId = "r2", TaskId = "T1", Attempt = 1 }).WithPhase9(["mil-a", "mil-b"], ActivityKind.Tests, AttemptOrder.First, AttemptFate.Kept, false),
        (UsageSamples.ClaudeAttempt with { RunId = "r3", TaskId = null, Attempt = 1 }).WithPhase9([], ActivityKind.Analysis, AttemptOrder.First, AttemptFate.Pending, false),
    ];

    private static UsageReport Report(params UsageDimension[] dimensions) =>
        UsageSamples.Empty with
        {
            Attempts = Attempts,
            Groups = UsageAggregator.Group(Attempts, new UsageQuery(Now.AddDays(-30), Now.AddDays(1), TimeZoneInfo.Utc, dimensions)),
        };

    private static UsageDashboardViewModel Dashboard(UsageReport report, FakeUsageReport? fake = null)
    {
        var store = new RecordingSettingsStore();
        var clock = new FakeClock(Now);
        return new UsageDashboardViewModel(
            fake ?? new FakeUsageReport(() => report),
            new FakeUsageExporter(),
            new FakeSaveFilePicker(null),
            store,
            clock,
            new QuotaViewModel(new FakeQuotaObservations(), null, clock),
            new ObservabilitySettingsViewModel(store));
    }

    [Fact]
    public void The_milestone_and_the_three_axes_of_the_activity_are_offered_and_labelled()
    {
        var dashboard = Dashboard(UsageSamples.Empty);

        var offered = dashboard.Dimensions.Select(d => d.Dimension).ToList();
        Assert.Equal(Enum.GetValues<UsageDimension>(), offered);
        foreach (var dimension in new[] { UsageDimension.Milestone, UsageDimension.Activity, UsageDimension.ActivityAttempt, UsageDimension.ActivityDestination })
        {
            var label = dashboard.Dimensions.Single(d => d.Dimension == dimension).Label;
            Assert.DoesNotContain("[", label, StringComparison.Ordinal);
            Assert.NotEqual(dimension.ToString(), label);
        }

        Assert.Equal("Hito", dashboard.Dimensions.Single(d => d.Dimension == UsageDimension.Milestone).Label);
    }

    [Fact]
    public async Task Picking_the_new_dimensions_sends_them_in_the_query_in_the_order_of_the_enum()
    {
        var fake = new FakeUsageReport(() => Report(UsageDimension.Milestone, UsageDimension.Activity));
        var dashboard = Dashboard(UsageSamples.Empty, fake);
        foreach (var option in dashboard.Dimensions)
        {
            option.IsSelected = option.Dimension is UsageDimension.Milestone or UsageDimension.Activity or UsageDimension.ActivityDestination;
        }

        await dashboard.SetProjectAsync(Root);

        var (_, query) = Assert.Single(fake.Queries);
        Assert.Equal([UsageDimension.Milestone, UsageDimension.Activity, UsageDimension.ActivityDestination], query.GroupBy);
        Assert.Contains(dashboard.Groups, g => g.Key.Contains("Hito: mil-a · Tipo de trabajo: Implementación", StringComparison.Ordinal));
        Assert.Null(dashboard.ErrorMessage);
    }

    [Fact]
    public async Task The_values_of_the_axes_are_said_in_words_and_not_as_the_names_of_the_enums()
    {
        var dashboard = Dashboard(Report(UsageDimension.Milestone));
        await dashboard.SetProjectAsync(Root);

        var keys = dashboard.Axes.SelectMany(a => a.Rows).Select(r => r.Key).ToList();

        Assert.Contains("sin hito", keys);
        Assert.Contains("Implementación", keys);
        Assert.Contains("Reintento", keys);
        Assert.Contains("Descartado, con versión guardada", keys);
        Assert.Contains("Pendiente", keys);
        Assert.DoesNotContain(keys, k => k is "Implementation" or "Retry" or "Discarded+commit" or "Pending");
    }

    [Fact]
    public async Task Every_axis_adds_up_to_the_total_and_the_milestones_add_up_to_it_plus_what_they_share()
    {
        var dashboard = Dashboard(Report(UsageDimension.Milestone));
        await dashboard.SetProjectAsync(Root);

        Assert.Equal(4, dashboard.Axes.Count);
        var counts = dashboard.Axes.Select(a => a.Rows.Sum(r => int.Parse(r.Attempts.Value!, CultureInfo.CurrentCulture))).ToList();

        // Milestone: A has three attempts, B one and «sin hito» one: five rows of attempts for four attempts, the one shared counted twice.
        Assert.Equal(5, counts[0]);
        Assert.Equal([4, 4, 4], counts.Skip(1));
        Assert.All(dashboard.Axes, a => Assert.Equal("Total: 4 intentos", a.TotalText));
    }

    [Fact]
    public async Task What_a_milestone_shares_is_said_apart_and_only_when_there_is_something_shared()
    {
        var dashboard = Dashboard(Report(UsageDimension.Milestone));
        await dashboard.SetProjectAsync(Root);

        var milestones = dashboard.Axes[0];
        Assert.Equal("Compartidos: 1 intentos están en más de un hito. Cuentan en cada hito y una sola vez en el total.", milestones.SharedNote);
        Assert.Equal("1 compartidos con otro hito", milestones.Rows.Single(r => r.Key == "mil-a").SharedNote);
        Assert.Equal("1 compartidos con otro hito", milestones.Rows.Single(r => r.Key == "mil-b").SharedNote);
        Assert.Null(milestones.Rows.Single(r => r.Key == "sin hito").SharedNote);
        Assert.All(dashboard.Axes.Skip(1), a => Assert.Null(a.SharedNote));

        // The groups the person picks say it too, and the grand total never does.
        var picked = dashboard.Groups.Single(g => g.Key == "Hito: mil-a");
        Assert.Equal("1 compartidos con otro hito", picked.SharedNote);
    }

    [Fact]
    public async Task The_wait_in_the_queue_and_the_wait_for_the_usage_limit_are_figures_apart_from_the_duration()
    {
        var dashboard = Dashboard(Report(UsageDimension.Milestone));
        await dashboard.SetProjectAsync(Root);

        var milestoneA = dashboard.Axes[0].Rows.Single(r => r.Key == "mil-a");

        // Three attempts: 5 s, 25 s (it started 20 s earlier) and 5 s in the queue; 90 s of wait for the usage limit; the duration is not touched.
        Assert.Equal("35 s", milestoneA.QueueWait.Value);
        Assert.Equal("1 min", milestoneA.QuotaWait.Value);
        Assert.Equal("9 min", milestoneA.Duration.Value);
        Assert.Contains("medido por Agent Studio", milestoneA.QueueWait.Origin, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_axes_say_their_values_in_english_when_the_language_is_english()
    {
        var english = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));
        using var scope = Texts.Scope(english);
        var dashboard = Dashboard(Report(UsageDimension.Milestone));

        await dashboard.SetProjectAsync(Root);

        Assert.Equal(["By milestone", "By kind of work", "First attempt or retry", "What became of the work"], dashboard.Axes.Select(a => a.Title));
        Assert.Contains("no milestone", dashboard.Axes[0].Rows.Select(r => r.Key));
        Assert.Contains("Discarded, with a saved version", dashboard.Axes[3].Rows.Select(r => r.Key));
        Assert.Equal("Total: 4 attempts", dashboard.Axes[0].TotalText);
        Assert.Equal("Model company", dashboard.Dimensions.Single(d => d.Dimension == UsageDimension.Vendor).Label);
    }

    [Fact]
    public async Task A_report_without_attempts_has_no_axes()
    {
        var dashboard = Dashboard(UsageSamples.Empty);

        await dashboard.SetProjectAsync(Root);

        Assert.Empty(dashboard.Axes);
    }

    [Fact]
    public void Every_usage_text_exists_in_both_languages_and_the_english_has_no_spanish_signs()
    {
        var files = ResourceFiles()["UsageStrings"];
        var keys = Read(files.Neutral).Keys.ToList();
        var english = Read(files.Satellites["en"]);

        Assert.Equal(keys.Order(StringComparer.Ordinal), english.Keys.Order(StringComparer.Ordinal));
        Assert.All(english.Values, text => Assert.DoesNotContain(text, c => "áéíóúñ¿¡«»".Contains(c, StringComparison.Ordinal)));
        Assert.True(keys.Count > 100);
    }
}

internal static class UsagePhase9AttemptExtensions
{
    /// <summary>The attempt with the milestones it is linked to and its value on the three axes of the activity (what the projection fills).</summary>
    public static UsageAttempt WithPhase9(this UsageAttempt attempt, IReadOnlyList<string> milestones, ActivityKind kind, AttemptOrder order, AttemptFate fate, bool hasCommit) =>
        attempt with { Milestones = milestones, Activity = new ActivityClass(kind, order, fate, hasCommit) };
}
