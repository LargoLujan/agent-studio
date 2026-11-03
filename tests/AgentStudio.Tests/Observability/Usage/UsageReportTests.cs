using System.IO;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Providers;
using AgentStudio.Observability.Usage;
using static AgentStudio.Tests.Observability.Usage.Snapshots;

namespace AgentStudio.Tests.Observability.Usage;

/// <summary>
/// docs/14 D-05 and D-08: the report reads every log through <see cref="IProjectLogReader"/>, counts only the logs that
/// verify against a recognised anchor (any other is listed as excluded with its reason, never mixed) and compares the
/// prompts of the journal of the period with the attempts of the log, without adding the difference.
/// </summary>
public sealed class UsageReportTests
{
    private const string RootA = "C:\\repos\\alfa";
    private const string RootB = "C:\\repos\\beta";
    private const string ProjectB = "6f1c2b0e-0000-4000-8000-000000000002";
    private const string Run = "20261020-a1b2c3";

    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    // 2026-10-20 in Madrid (CEST, +02:00), until 23:00 local.
    private static readonly DateTimeOffset From = new(2026, 10, 20, 0, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset To = new(2026, 10, 20, 23, 0, 0, TimeSpan.FromHours(2));

    private static UsageQuery Query(params UsageDimension[] groupBy) => new(From, To, Madrid, groupBy);

    private static DateTimeOffset Utc(int hour, int minute = 0) => new(2026, 10, 20, hour, minute, 0, TimeSpan.Zero);

    private static ProjectLogSnapshot Trusted(string root, IReadOnlyList<DecisionEntry> entries, string projectId = UsageLogBuilder.ProjectId) =>
        new(projectId, root, entries, DecisionLogVerification.Valid, entries.Count == 0 ? AnchorStatus.NoHistory : AnchorStatus.Anchored);

    /// <summary>Three attempts on 2026-10-20 (two inside the period) and one the day before.</summary>
    private static IReadOnlyList<DecisionEntry> ThreeAttempts(string run = Run) => new UsageLogBuilder(Utc(7) - TimeSpan.FromDays(1))
        .Start(run, "T0", 1).Finish(run, "T0", 1, session: "s0", usage: Claude(5, 0, 0, 1, 0.001m))
        .At(Utc(8)).Start(run, "T1", 1).Finish(run, "T1", 1, session: "s1", usage: Claude(100, 0, 0, 10, 0.01m))
        .At(Utc(8, 5)).Start(run, "T2", 1, provider: ProviderId.CodexCli, model: "gpt-6").Finish(run, "T2", 1, session: "th1", usage: Codex(300, 0, 0, 30, 5))
        .Entries;

    private static UsageProjector Projector(FakeLogs logs, FakeStorage? storage = null) => new(logs, storage ?? new FakeStorage());

    [Fact]
    public async Task The_attempts_of_the_period_are_projected_and_added_up()
    {
        var logs = new FakeLogs { [RootA] = () => Trusted(RootA, ThreeAttempts()) };

        var report = await Projector(logs).QueryAsync([RootA], Query(UsageDimension.Provider), TestContext.Current.CancellationToken);

        Assert.Equal(["T1", "T2"], report.Attempts.Select(a => a.TaskId));
        Assert.Empty(report.Excluded);
        Assert.Equal(2, report.Groups[0].Totals.Attempts);
        Assert.Equal(400L, report.Groups[0].Totals.InputTokens.Sum);
        Assert.Equal([["claude-code"], ["codex-cli"]], report.Groups.Skip(1).Select(g => g.Key.Select(k => k.Value)));
    }

    [Fact]
    public async Task A_project_whose_log_does_not_verify_is_excluded_with_its_reason_and_never_mixed()
    {
        var logs = new FakeLogs
        {
            [RootA] = () => Trusted(RootA, ThreeAttempts()),
            [RootB] = () => new ProjectLogSnapshot(ProjectB, RootB, ThreeAttempts("20261020-bbbbbb"), new DecisionLogVerification(false, 3, "The entry content does not match its hash (it was modified)."), AnchorStatus.Anchored),
        };

        var report = await Projector(logs).QueryAsync([RootA, RootB], Query(), TestContext.Current.CancellationToken);

        Assert.All(report.Attempts, a => Assert.Equal(UsageLogBuilder.ProjectId, a.ProjectId));
        var excluded = Assert.Single(report.Excluded);
        Assert.Equal((RootB, (string?)ProjectB), (excluded.Root, excluded.ProjectId));
        Assert.Contains("no verifica", excluded.Reason, StringComparison.Ordinal);
        Assert.Contains("entrada 3", excluded.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AnchorStatus.Unanchored, "ningún ancla")]
    [InlineData(AnchorStatus.AnotherProject, "otro proyecto")]
    [InlineData(AnchorStatus.Unreadable, "no se puede leer")]
    public async Task A_recent_project_whose_history_no_anchor_recognises_is_excluded(AnchorStatus anchor, string reason)
    {
        var logs = new FakeLogs { [RootB] = () => new ProjectLogSnapshot(ProjectB, RootB, ThreeAttempts(), DecisionLogVerification.Valid, anchor) };

        var report = await Projector(logs).QueryAsync([RootB], Query(), TestContext.Current.CancellationToken);

        Assert.Empty(report.Attempts);
        Assert.Contains(reason, Assert.Single(report.Excluded).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, report.Groups[0].Totals.Attempts);
    }

    [Fact]
    public async Task A_project_that_cannot_be_read_is_excluded_with_why()
    {
        var logs = new FakeLogs { [RootB] = () => throw new IOException("'C:\\repos\\beta\\.agentstudio' is a link.") };

        var report = await Projector(logs).QueryAsync([RootB], Query(), TestContext.Current.CancellationToken);

        var excluded = Assert.Single(report.Excluded);
        Assert.Null(excluded.ProjectId);
        Assert.Contains("is a link", excluded.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Several_projects_add_up_and_can_be_told_apart()
    {
        var logs = new FakeLogs
        {
            [RootA] = () => Trusted(RootA, ThreeAttempts()),
            [RootB] = () => Trusted(RootB, ThreeAttempts("20261020-bbbbbb"), ProjectB),
        };

        var report = await Projector(logs).QueryAsync([RootA, RootB], Query(UsageDimension.Project), TestContext.Current.CancellationToken);

        Assert.Equal(4, report.Attempts.Count);
        Assert.Equal([2, 2], report.Groups.Skip(1).Select(g => g.Totals.Attempts));
        Assert.Equal([UsageLogBuilder.ProjectId, ProjectB], report.Groups.Skip(1).Select(g => g.Key[0].Value));
    }

    [Fact]
    public async Task A_root_given_twice_is_read_once()
    {
        var logs = new FakeLogs { [RootA] = () => Trusted(RootA, ThreeAttempts()) };

        var report = await Projector(logs).QueryAsync([RootA, RootA + "\\", RootA.ToUpperInvariant()], Query(), TestContext.Current.CancellationToken);

        Assert.Equal(1, logs.Reads);
        Assert.Equal(2, report.Attempts.Count);
    }

    // ---- the projection is kept while the log does not change (the measurement of WP2 asked for it) ----

    [Fact]
    public async Task A_second_query_over_the_same_log_reuses_its_projection()
    {
        var entries = ThreeAttempts();
        var logs = new FakeLogs { [RootA] = () => Trusted(RootA, entries) };
        var projector = Projector(logs);

        var first = await projector.QueryAsync([RootA], Query(), TestContext.Current.CancellationToken);
        var second = await projector.QueryAsync([RootA], Query(UsageDimension.Provider), TestContext.Current.CancellationToken);

        Assert.Equal(2, logs.Reads);
        Assert.Same(first.Attempts[0], second.Attempts[0]);
        Assert.Equal(3, second.Groups.Count);
    }

    [Fact]
    public async Task A_log_that_grew_is_projected_again()
    {
        var log = new UsageLogBuilder(Utc(8)).Start(Run, "T1", 1).Finish(Run, "T1", 1, session: "s1", usage: Claude(100, 0, 0, 10, 0.01m));
        var logs = new FakeLogs { [RootA] = () => Trusted(RootA, log.Entries) };
        var projector = Projector(logs);
        var first = await projector.QueryAsync([RootA], Query(), TestContext.Current.CancellationToken);

        log.Start(Run, "T1", 2).Finish(Run, "T1", 2, session: "s1", resumed: "s1", usage: Claude(150, 0, 0, 15, 0.015m));
        var second = await projector.QueryAsync([RootA], Query(), TestContext.Current.CancellationToken);

        Assert.Single(first.Attempts);
        Assert.Equal([100L, 50L], second.Attempts.Select(a => a.Own.InputTokens.Value!.Value));
        Assert.Equal(150L, second.Attempts[0].SessionTotal?.InputTokens.Value);
    }

    [Fact]
    public async Task A_log_of_the_same_length_with_another_head_is_projected_again()
    {
        var one = new UsageLogBuilder(Utc(8)).Start(Run, "T1", 1).Finish(Run, "T1", 1, session: "s1", usage: Claude(100, 0, 0, 10, 0.01m)).Entries;
        var other = new UsageLogBuilder(Utc(8)).Start(Run, "T1", 1).Finish(Run, "T1", 1, session: "s1", usage: Claude(999, 0, 0, 10, 0.01m)).Entries;
        var current = one;
        var logs = new FakeLogs { [RootA] = () => Trusted(RootA, current) };
        var projector = Projector(logs);
        await projector.QueryAsync([RootA], Query(), TestContext.Current.CancellationToken);

        current = other;
        var second = await projector.QueryAsync([RootA], Query(), TestContext.Current.CancellationToken);

        Assert.Equal(999L, Assert.Single(second.Attempts).Own.InputTokens.Value);
    }

    // ---- reconciliation with the journal (D-08) ----

    /// <summary>
    /// The log of the sample journal's day: a version 1 start at 09:00 local (before Phase 4), three version 2 starts
    /// at 10:00, 10:05 and 10:20, and one more the next day.
    /// </summary>
    private static IReadOnlyList<DecisionEntry> JournalDayLog() => new UsageLogBuilder(Utc(7))
        .Start(Run, null, 1, agent: "orquestador", version: 1).Finish(Run, null, 1, session: "p", usage: ClaudeV1(10, 0, 1, 0m), version: 1)
        .At(Utc(8)).Start(Run, "T1", 1).Finish(Run, "T1", 1, AttemptOutcome.Failed, session: "s1", usage: Claude(10, 0, 0, 1, 0m))
        .At(Utc(8, 5)).Start(Run, "T1", 2).Finish(Run, "T1", 2, session: "s1", resumed: "s1", usage: Claude(20, 0, 0, 2, 0m))
        .At(Utc(8, 20)).Start(Run, null, 1, agent: "juez", provider: ProviderId.CodexCli, model: "gpt-6", purpose: AgentRunPurpose.Judge)
        .Finish(Run, null, 1, session: "j", usage: Codex(30, 0, 0, 3, 0))
        .At(Utc(23)).Start(Run, "T3", 1)
        .Entries;

    private static Stream SampleJournal() =>
        File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Observability", "Usage", "Fixtures", "bitacora-2026-10-20.md"));

    [Fact]
    public async Task The_prompts_of_the_journal_since_phase_4_are_compared_with_the_attempts_of_the_log()
    {
        var logs = new FakeLogs { [RootA] = () => Trusted(RootA, JournalDayLog()) };
        var storage = new FakeStorage { [(RootA, "bitacora/2026-10-20.md")] = SampleJournal };

        var report = await Projector(logs, storage).QueryAsync([RootA], Query(), TestContext.Current.CancellationToken);

        // P-0002, P-0003, P-0004 and the live test P-0006; P-0001 is before the first version 2 start and P-0005 after
        // the period. The hook's entries, the results, the «Hecho» lines and the broken line are no prompts.
        Assert.Equal(4, report.Journal.JournalPrompts);
        Assert.Equal(3, report.Journal.RecordedAttempts);
        Assert.Equal(1, report.Journal.Difference);
        Assert.Contains(report.Journal.Warnings, w => w.Contains("1 prompt", StringComparison.Ordinal));
        Assert.Equal(["bitacora/2026-10-20.md"], storage.Opened.Select(o => o.Path));
    }

    [Fact]
    public async Task The_difference_is_never_added_to_the_figures()
    {
        var logs = new FakeLogs { [RootA] = () => Trusted(RootA, JournalDayLog()) };
        var storage = new FakeStorage { [(RootA, "bitacora/2026-10-20.md")] = SampleJournal };

        var report = await Projector(logs, storage).QueryAsync([RootA], Query(), TestContext.Current.CancellationToken);

        Assert.Equal(4, report.Groups[0].Totals.Attempts);
        Assert.Equal(4, report.Attempts.Count);
    }

    [Fact]
    public async Task Without_any_journal_in_the_period_there_is_nothing_to_compare()
    {
        var logs = new FakeLogs { [RootA] = () => Trusted(RootA, JournalDayLog()) };

        var report = await Projector(logs).QueryAsync([RootA], Query(), TestContext.Current.CancellationToken);

        Assert.Null(report.Journal.JournalPrompts);
        Assert.Null(report.Journal.Difference);
        Assert.Equal(3, report.Journal.RecordedAttempts);
        Assert.Contains("Sin bitácora del periodo.", report.Journal.Warnings);
    }

    [Fact]
    public async Task A_day_of_the_journal_that_cannot_be_read_counts_nothing_and_says_why()
    {
        var logs = new FakeLogs { [RootA] = () => Trusted(RootA, JournalDayLog()) };
        var storage = new FakeStorage { [(RootA, "bitacora/2026-10-20.md")] = () => throw new IOException("'bitacora\\2026-10-20.md' is a link.") };

        var report = await Projector(logs, storage).QueryAsync([RootA], Query(), TestContext.Current.CancellationToken);

        Assert.Null(report.Journal.JournalPrompts);
        Assert.Contains(report.Journal.Warnings, w => w.Contains("2026-10-20", StringComparison.Ordinal) && w.Contains("is a link", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_log_without_any_attempt_of_phase_4_is_not_reconciled()
    {
        var log = new UsageLogBuilder(Utc(7)).Start(Run, null, 1, version: 1).Finish(Run, null, 1, session: "p", usage: ClaudeV1(10, 0, 1, 0m), version: 1).Entries;
        var logs = new FakeLogs { [RootA] = () => Trusted(RootA, log) };
        var storage = new FakeStorage { [(RootA, "bitacora/2026-10-20.md")] = SampleJournal };

        var report = await Projector(logs, storage).QueryAsync([RootA], Query(), TestContext.Current.CancellationToken);

        Assert.Null(report.Journal.JournalPrompts);
        Assert.Equal(0, report.Journal.RecordedAttempts);
        Assert.Contains(report.Journal.Warnings, w => w.Contains("Fase 4", StringComparison.Ordinal));
        Assert.Empty(storage.Opened);
    }

    [Fact]
    public async Task Warnings_of_the_projection_reach_the_report()
    {
        var log = new UsageLogBuilder(Utc(8)).Finish(Run, "T9", 1, session: "s", usage: Claude(1, 0, 0, 1, 0m)).Entries;
        var logs = new FakeLogs { [RootA] = () => Trusted(RootA, log) };

        var report = await Projector(logs).QueryAsync([RootA], Query(), TestContext.Current.CancellationToken);

        Assert.Contains(report.Journal.Warnings, w => w.Contains("T9", StringComparison.Ordinal));
    }

    private sealed class FakeLogs : Dictionary<string, Func<ProjectLogSnapshot>>, IProjectLogReader
    {
        public int Reads { get; private set; }

        public Task<ProjectLogSnapshot> ReadAsync(string projectRoot, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(this[projectRoot]());
        }
    }

    private sealed class FakeStorage : Dictionary<(string Root, string Path), Func<Stream>>, IProjectStorageReader
    {
        public List<(string Root, string Path)> Opened { get; } = [];

        public Stream OpenForRead(string projectRoot, string relativePath)
        {
            if (!TryGetValue((projectRoot, relativePath), out var open))
            {
                throw new FileNotFoundException("No such day.", relativePath);
            }

            Opened.Add((projectRoot, relativePath));
            return open();
        }
    }
}
