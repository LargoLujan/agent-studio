using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Observability;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Memory;

namespace AgentStudio.Tests.Desktop.Observability;

/// <summary>
/// The subscription quota of each provider (docs/14 D-14 to D-16): Claude's windows as its runs announced them, with how
/// old each observation is; Codex only with the user's opt-in, read on demand and once after a Codex run (never more
/// often than every five minutes); Gemini «no expuesto».
/// </summary>
public sealed class QuotaViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 10, 0, 0, TimeSpan.FromHours(2));

    private static QuotaObservation Claude(string window, double utilization, TimeSpan age, DateTimeOffset? resetsAt = null) =>
        new(ProviderId.ClaudeCode, window, MetricValue<double>.Exact(utilization, "claude rate_limit_event"), null, resetsAt, Now - age, "claude rate_limit_event");

    private static QuotaObservation Codex(string window, double utilization, TimeSpan? length = null, string? label = null) =>
        new(ProviderId.CodexCli, window, MetricValue<double>.Exact(utilization, "codex account/rateLimits/read"), length, null, Now, "codex account/rateLimits/read") { Label = label };

    [Fact]
    public async Task The_read_of_codex_never_runs_on_the_thread_that_asks()
    {
        // The reader loads the settings, looks the executable up and starts a process before its first wait: not on the UI thread.
        var readOn = 0;
        var reader = new FakeQuotaReader(() =>
        {
            readOn = Environment.CurrentManagedThreadId;
            return new QuotaReadResult(QuotaReadStatus.Read, []);
        });

        var askedOn = await WpfHost.RunAsync(async () =>
        {
            var quota = new QuotaViewModel(new FakeQuotaObservations(), reader, new FakeClock(Now)) { CodexOptedIn = true };
            var ui = Environment.CurrentManagedThreadId;
            await quota.ReadCodexCommand.ExecuteAsync(null);
            return ui;
        });

        Assert.Equal(1, reader.Reads);
        Assert.NotEqual(askedOn, readOn);
    }

    [Fact]
    public async Task A_window_whose_reset_passed_since_it_was_seen_is_never_shown_as_its_old_figure()
    {
        // Seen at 7:00 at 92 %, its 5 hours reset at 9:30: at 10:00 that figure says nothing of now.
        var clock = new FakeClock(Now);
        var observations = new FakeQuotaObservations
        {
            Latest = { Claude("five_hour", 0.92, TimeSpan.FromHours(3), resetsAt: Now.AddMinutes(-30)) },
        };
        var quota = new QuotaViewModel(observations, null, clock);

        await quota.RefreshAsync();

        var row = Assert.Single(quota.Claude.Windows);
        Assert.False(row.Bar!.HasValue);
        Assert.Equal("se reinició a las 09:30, sin dato nuevo", row.Bar.ValueText);

        // V36: the bar says when it reset; the line under it does not say it again.
        Assert.Null(row.Resets);
        Assert.Contains("antes del reinicio: 92 %", row.Bar.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("exacto", row.Bar.CompactText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_window_not_reset_yet_shows_its_figure_with_how_old_it_is_next_to_it()
    {
        var observations = new FakeQuotaObservations
        {
            Latest = { Claude("five_hour", 0.47, TimeSpan.FromMinutes(12), resetsAt: Now.AddHours(2)) },
        };
        var quota = new QuotaViewModel(observations, null, new FakeClock(Now));

        await quota.RefreshAsync();

        var bar = Assert.Single(quota.Claude.Windows).Bar!;
        Assert.Equal(47, bar.Value!.Value, 6);
        Assert.Equal("47 % exacto · hace 12 min", bar.CompactText);
        Assert.EndsWith("vista hace 12 min", bar.Text, StringComparison.Ordinal);
        Assert.Equal("se reinicia hoy a las 12:00", bar.Detail);
    }

    [Fact]
    public async Task Claude_windows_show_their_utilization_with_its_origin_and_how_old_they_are()
    {
        var observations = new FakeQuotaObservations
        {
            Latest =
            {
                Claude("seven_day", 0.12, TimeSpan.FromHours(3)),
                Claude("five_hour", 0.47, TimeSpan.FromMinutes(12), resetsAt: new DateTimeOffset(2026, 10, 18, 15, 0, 0, TimeSpan.FromHours(2))),
            },
        };
        var quota = new QuotaViewModel(observations, null, new FakeClock(Now));

        await quota.RefreshAsync();

        Assert.Equal("Claude Code", quota.Claude.Name);
        Assert.Equal(["5 horas", "7 días"], quota.Claude.Windows.Select(w => w.Name));
        var fiveHours = quota.Claude.Windows[0];
        Assert.Equal("47 % · exacto · informado por el CLI", fiveHours.Utilization.Text);
        Assert.Equal("vista hace 12 min", fiveHours.Seen);
        Assert.Equal("se reinicia hoy a las 15:00", fiveHours.Resets);
        Assert.Equal("vista hace 3 h", quota.Claude.Windows[1].Seen);
        Assert.Equal("Del último anuncio de Claude al empezar una ejecución (rate_limit_event): sin llamadas extra.", quota.Claude.Status);
    }

    [Fact]
    public async Task Without_observations_Claude_says_how_they_arrive_and_shows_no_number()
    {
        var quota = new QuotaViewModel(new FakeQuotaObservations(), null, new FakeClock(Now));

        await quota.RefreshAsync();

        Assert.Empty(quota.Claude.Windows);
        Assert.Equal("Sin dato todavía: Claude anuncia su cuota al empezar cada ejecución.", quota.Claude.Status);
    }

    [Fact]
    public async Task Codex_is_off_until_the_opt_in_and_nothing_is_launched()
    {
        var reader = new FakeQuotaReader();
        var quota = new QuotaViewModel(new FakeQuotaObservations(), reader, new FakeClock(Now));

        await quota.RefreshAsync();
        await quota.CodexRunFinishedAsync();

        Assert.False(quota.ReadCodexCommand.CanExecute(null));
        Assert.Equal(0, reader.Reads);
        Assert.Equal("Desactivado: activa «Leer la cuota de Codex» en «Ajustes». Sin tu permiso no se lanza nada.", quota.Codex.Status);
        Assert.Empty(quota.Codex.Windows);
    }

    [Fact]
    public async Task With_the_opt_in_a_read_shows_the_windows_keeps_them_and_says_what_the_CLI_said()
    {
        var observations = new FakeQuotaObservations();
        var read = new QuotaReadResult(
            QuotaReadStatus.Read,
            [Codex("codex/primary", 0.41, TimeSpan.FromHours(5), "Codex"), Codex("codex/secondary", 0.08, TimeSpan.FromDays(7), "Codex")])
        {
            PlanType = "plus",
            OrdinaryUsageAllowed = true,
            Warnings = ["versión de Codex no probada (0.161.0)"],
        };
        var reader = new FakeQuotaReader(() => read);
        var quota = new QuotaViewModel(observations, reader, new FakeClock(Now)) { CodexOptedIn = true };

        Assert.True(quota.ReadCodexCommand.CanExecute(null));
        await quota.ReadCodexCommand.ExecuteAsync(null);

        Assert.Equal(1, reader.Reads);
        Assert.Equal(["Codex · ventana de 5 h", "Codex · ventana de 7 días"], quota.Codex.Windows.Select(w => w.Name));
        Assert.Equal("41 % · exacto · informado por el CLI", quota.Codex.Windows[0].Utilization.Text);
        Assert.Equal("vista hace menos de 1 min", quota.Codex.Windows[0].Seen);
        Assert.Equal("Leída del app-server de Codex (account/rateLimits/read), sin turno del modelo.", quota.Codex.Status);
        Assert.Equal(["Plan: plus"], quota.Codex.Notes);
        Assert.Equal(["versión de Codex no probada (0.161.0)"], quota.Codex.Warnings);
        Assert.Equal(read.Observations, observations.Appended);
    }

    [Fact]
    public async Task Codex_with_an_API_key_is_not_exposed_and_says_why()
    {
        var reader = new FakeQuotaReader(() => new QuotaReadResult(QuotaReadStatus.NotExposed, [], "Codex usa API key"));
        var quota = new QuotaViewModel(new FakeQuotaObservations(), reader, new FakeClock(Now)) { CodexOptedIn = true };

        await quota.ReadCodexCommand.ExecuteAsync(null);

        Assert.Equal("no expuesto: Codex usa API key", quota.Codex.Status);
        Assert.Empty(quota.Codex.Windows);
    }

    [Fact]
    public async Task A_read_that_timed_out_is_sin_dato_with_its_reason()
    {
        var reader = new FakeQuotaReader(() => new QuotaReadResult(QuotaReadStatus.Missing, [], "el app-server no contestó en 20 s"));
        var quota = new QuotaViewModel(new FakeQuotaObservations(), reader, new FakeClock(Now)) { CodexOptedIn = true };

        await quota.ReadCodexCommand.ExecuteAsync(null);

        Assert.Equal("sin dato: el app-server no contestó en 20 s", quota.Codex.Status);
    }

    [Fact]
    public async Task A_reader_that_throws_is_shown_as_sin_dato_and_never_thrown()
    {
        var reader = new FakeQuotaReader(() => throw new InvalidOperationException("codex.exe no está."));
        var quota = new QuotaViewModel(new FakeQuotaObservations(), reader, new FakeClock(Now)) { CodexOptedIn = true };

        await quota.ReadCodexCommand.ExecuteAsync(null);

        Assert.Equal("sin dato: no se pudo leer la cuota de Codex: codex.exe no está.", quota.Codex.Status);
    }

    [Fact]
    public async Task After_each_Codex_run_it_is_read_once_but_never_twice_within_five_minutes()
    {
        var clock = new FakeClock(Now);
        var reader = new FakeQuotaReader();
        var quota = new QuotaViewModel(new FakeQuotaObservations(), reader, clock) { CodexOptedIn = true };

        await quota.CodexRunFinishedAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        await quota.CodexRunFinishedAsync();
        clock.Advance(TimeSpan.FromMinutes(4));
        await quota.CodexRunFinishedAsync();

        Assert.Equal(2, reader.Reads);
    }

    [Fact]
    public async Task With_the_opt_in_and_no_reader_composed_it_says_so()
    {
        var quota = new QuotaViewModel(new FakeQuotaObservations(), null, new FakeClock(Now)) { CodexOptedIn = true };

        await quota.RefreshAsync();

        Assert.False(quota.ReadCodexCommand.CanExecute(null));
        Assert.Equal("La lectura de la cuota de Codex no está disponible en esta composición.", quota.Codex.Status);
    }

    [Fact]
    public async Task Turning_the_opt_in_off_disables_the_read_and_hides_the_Codex_windows()
    {
        var observations = new FakeQuotaObservations { Latest = { Codex("codex/primary", 0.41, TimeSpan.FromHours(5), "Codex") } };
        var quota = new QuotaViewModel(observations, new FakeQuotaReader(), new FakeClock(Now)) { CodexOptedIn = true };
        await quota.RefreshAsync();
        Assert.Single(quota.Codex.Windows);

        quota.CodexOptedIn = false;

        Assert.False(quota.ReadCodexCommand.CanExecute(null));
        Assert.Empty(quota.Codex.Windows);
        Assert.StartsWith("Desactivado", quota.Codex.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_window_whose_limit_the_provider_says_was_reached_says_so()
    {
        var reached = Codex("codex/primary", 1.0, TimeSpan.FromHours(5), "Codex") with { Reached = "primary" };
        var reader = new FakeQuotaReader(() => new QuotaReadResult(QuotaReadStatus.Read, [reached]) { OrdinaryUsageAllowed = false });
        var quota = new QuotaViewModel(new FakeQuotaObservations(), reader, new FakeClock(Now)) { CodexOptedIn = true };

        await quota.ReadCodexCommand.ExecuteAsync(null);

        Assert.Equal("límite alcanzado: primary", Assert.Single(quota.Codex.Windows).Reached);
        Assert.Equal(["Codex dice que el uso ordinario no está permitido ahora."], quota.Codex.Warnings);
        Assert.Empty(quota.Codex.Notes);
    }

    [Theory]
    [InlineData("five_hour", "5 horas")]
    [InlineData("seven_day", "7 días")]
    [InlineData("seven_day_opus", "7 días · Opus")]
    [InlineData("seven_day_sonnet", "7 días · Sonnet")]
    [InlineData("codex/primary", "codex/primary")]
    public void Every_screen_names_a_window_with_the_same_human_label(string window, string label)
    {
        Assert.Equal(label, QuotaViewModel.WindowLabel(window));
    }

    [Fact]
    public async Task Gemini_is_not_exposed()
    {
        var quota = new QuotaViewModel(new FakeQuotaObservations(), null, new FakeClock(Now));

        await quota.RefreshAsync();

        Assert.Equal("Gemini CLI", quota.Gemini.Name);
        Assert.Equal("no expuesto: Gemini está aparcado (ADR-0009) y su cuota de plan no tiene interfaz.", quota.Gemini.Status);
        Assert.Empty(quota.Gemini.Windows);
    }
}
