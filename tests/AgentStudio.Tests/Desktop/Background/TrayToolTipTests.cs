using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Background;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;

namespace AgentStudio.Tests.Desktop.Background;

/// <summary>
/// F8-7 and docs/16 D-14: the tooltip of the tray icon summarizes in 127 characters at most (<c>szTip</c>), with counts and
/// times, and names no project or run.
/// </summary>
public sealed class TrayToolTipTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");

    private static WorkStatus Run(RunPhase phase = RunPhase.Running, DateTimeOffset? quotaUntil = null, int awaiting = 0, string root = RootA, string id = "20261021-aaaaaa") =>
        new(BackgroundWorkKind.Run, root, id, true, phase, quotaUntil, phase == RunPhase.WaitingForQuota || quotaUntil is not null, awaiting, 1);

    [Fact]
    public void Two_runs_one_waiting_for_quota_read_as_the_example_of_d14()
    {
        var text = TrayToolTip.Compose([Run(), Run(RunPhase.WaitingForQuota, T0.AddMinutes(5), root: RootB, id: "20261021-bbbbbb")], ExitState.None, null, locked: false, Zone);

        // 12:05 UTC is 14:05 in the zone of the test.
        Assert.Equal("Agent Studio · 2 ejecuciones · 1 espera cuota hasta las 14:05", text);
    }

    [Fact]
    public void Nothing_going_on_says_so()
    {
        Assert.Equal("Agent Studio · sin trabajo en marcha", TrayToolTip.Compose([], ExitState.None, null, locked: false, Zone));
        Assert.Equal("Agent Studio · sin trabajo en marcha", TrayToolTip.Compose([Run() with { IsActive = false }], ExitState.None, null, locked: false, Zone));
    }

    [Fact]
    public void Every_kind_of_work_and_what_waits_for_the_person_are_counted()
    {
        WorkStatus[] active =
        [
            Run(RunPhase.AwaitingHuman, awaiting: 2),
            Run(RunPhase.Paused, root: RootB, id: "20261021-bbbbbb"),
            Run(RunPhase.Blocked, root: RootB, id: "20261021-cccccc"),
            new(BackgroundWorkKind.Debate, RootC, "deb-20261021-dddddd", true, null, null, false, 1, 2),
            new(BackgroundWorkKind.Audit, RootC + "-b", null, true, null, null, false, 0, null),
        ];

        var text = TrayToolTip.Compose(active, ExitState.None, null, locked: false, Zone);

        Assert.Equal("Agent Studio · 3 ejecuciones · 1 debate · 1 auditoría · 3 esperan por ti · 1 en pausa · 1 bloqueada", text);
    }

    [Fact]
    public void Several_waiting_for_quota_show_the_earliest_reset()
    {
        var text = TrayToolTip.Compose(
            [Run(RunPhase.WaitingForQuota, T0.AddHours(3)), Run(RunPhase.WaitingForQuota, T0.AddMinutes(30), root: RootB, id: "20261021-bbbbbb"), Run(RunPhase.WaitingForQuota, root: RootC, id: "20261021-cccccc")],
            ExitState.None,
            null,
            locked: false,
            Zone);

        Assert.Equal("Agent Studio · 3 ejecuciones · 3 esperan cuota hasta las 14:30", text);
    }

    [Theory]
    [InlineData(2, "Agent Studio · saldrá al terminar 2 intentos en curso · 1 ejecución")]
    [InlineData(1, "Agent Studio · saldrá al terminar 1 intento en curso · 1 ejecución")]
    [InlineData(null, "Agent Studio · saldrá cuando termine lo que está en curso · 1 ejecución")]
    public void Waiting_to_exit_says_what_it_waits_for(int? left, string expected) =>
        Assert.Equal(expected, TrayToolTip.Compose([Run()], ExitState.WaitingForIdle, left, locked: false, Zone));

    [Fact]
    public void A_locked_interface_says_it_is_locked_and_still_counts_what_goes_on()
    {
        Assert.Equal("Agent Studio · bloqueado · 1 ejecución", TrayToolTip.Compose([Run()], ExitState.None, null, locked: true, Zone));
    }

    [Fact]
    public void Whatever_goes_on_the_tooltip_fits_in_127_characters_and_names_no_project_nor_run()
    {
        // Seeded, so a failure always reproduces (docs/16 §4.2).
        var random = new Random(20261021);
        var roots = new[] { RootA, RootB, RootC, @"C:\un\proyecto\con-un-nombre-muy-largo-que-no-cabe-en-ningun-tooltip-de-windows" };
        for (var round = 0; round < 500; round++)
        {
            var active = Enumerable.Range(0, random.Next(0, 40)).Select(i => new WorkStatus(
                (BackgroundWorkKind)random.Next(0, 4),
                roots[random.Next(roots.Length)],
                $"20261021-{i:x6}",
                random.Next(5) > 0,
                (RunPhase)random.Next(0, 9),
                random.Next(3) == 0 ? T0.AddMinutes(random.Next(0, 600)) : null,
                random.Next(3) == 0,
                random.Next(0, 3),
                random.Next(4) == 0 ? null : random.Next(0, 5))).ToList();
            var exit = (ExitState)random.Next(0, 3);

            var text = TrayToolTip.Compose(active, exit, random.Next(3) == 0 ? null : random.Next(0, 12), locked: random.Next(2) == 0, Zone);

            Assert.True(text.Length <= TrayToolTip.MaxLength, $"Round {round}: {text.Length} characters: «{text}».");
            Assert.StartsWith("Agent Studio", text, StringComparison.Ordinal);
            Assert.DoesNotContain("proyecto", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("monorepo", text, StringComparison.Ordinal);
            Assert.DoesNotContain("20261021-", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void When_it_does_not_fit_the_least_important_parts_go_first_and_what_waits_for_you_stays()
    {
        // 50 of each kind, all waiting for quota and for the person: every part is there and the whole does not fit.
        var active = Enumerable.Range(0, 200).Select(i => new WorkStatus(
            (BackgroundWorkKind)(i % 4),
            RootA,
            $"r{i}",
            true,
            (RunPhase)(i % 4),
            T0.AddMinutes(i),
            true,
            1,
            1)).ToList();

        var text = TrayToolTip.Compose(active, ExitState.WaitingForIdle, 120, locked: true, Zone);

        Assert.Equal("Agent Studio · bloqueado · saldrá al terminar 120 intentos en curso · 200 esperan cuota hasta las 14:00 · 200 esperan por ti", text);
        Assert.True(text.Length <= TrayToolTip.MaxLength, text);
    }

    [Theory]
    [InlineData("RateLimitedUntil(2026-10-21T12:05:00.0000000+00:00)", "2026-10-21T12:05:00+00:00")]
    [InlineData("RateLimitedUntil(2026-10-21T14:05:00.0000000+02:00)", "2026-10-21T12:05:00+00:00")]
    [InlineData("Interrupted", null)]
    [InlineData("AwaitingHuman", null)]
    [InlineData("RateLimitedUntil(no es una hora)", null)]
    [InlineData("RateLimitedUntil(", null)]
    [InlineData(null, null)]
    public void The_reset_of_a_task_comes_from_its_structured_block_reason(string? reason, string? expected)
    {
        var until = WorkStatus.QuotaResetOf(reason);

        Assert.Equal(expected is null ? null : DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), until);
    }
}
