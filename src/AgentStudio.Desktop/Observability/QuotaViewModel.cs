using System.Globalization;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Providers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Observability;

/// <summary>One window of a subscription as the panel shows it: its name, the utilization with its origin and how old it is.</summary>
/// <param name="Name">«5 horas», «7 días», or the provider's name of the limit with the length of its window.</param>
/// <param name="Seen">How old the observation is: «vista hace 12 min».</param>
/// <param name="Resets">
/// When the window resets, in local time; null when the provider does not say, and once the reset passed (its bar says
/// then when it reset, V36).
/// </param>
/// <param name="Reached">The limit the provider says was reached; null when it says none.</param>
public sealed record QuotaWindowRow(string Name, Figure Utilization, string Seen, string? Resets, string? Reached = null)
{
    /// <summary>The window as the provider names it (<c>five_hour</c>, <c>seven_day</c>, <c>codex/primary</c>…); null when not given.</summary>
    public string? Window { get; init; }

    /// <summary>The window in a few words, for the status bar: «5 h», «7 días».</summary>
    public string ShortName { get; init; } = string.Empty;

    /// <summary>The bar of the window (docs/18 P3): the utilization the provider gave, with its origin and its age.</summary>
    public MetricBarReading? Bar { get; init; }

    /// <summary>What UI Automation names the row with, as an item of the list of «Consumo»: the bar's whole line, or the words.</summary>
    public override string ToString() => Bar?.AccessibleName ?? $"{Name}: {Utilization.Text} · {Seen}";
}

//// <summary>The quota of one provider: its name, where the figures come from (or why there are none), its windows and notes.</summary>
public sealed record ProviderQuota(string Name, string Status, IReadOnlyList<QuotaWindowRow> Windows, IReadOnlyList<string> Notes)
{
    /// <summary>What the user should heed, such as «versión de Codex no probada» or a limit the provider says applies now.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// The subscription quota of each provider (docs/14 D-14 to D-16). Claude: the latest observation of each window from the
/// user's record (<see cref="IQuotaObservations"/>), which its runs fill when they start, with how old it is; no call of
/// its own. Codex: only with the user's opt-in (<see cref="CodexOptedIn"/>), read on demand («Consultar cuota de Codex»)
/// and once after each Codex run, never twice within <see cref="CodexReadInterval"/>; without the opt-in nothing is
/// launched. Gemini: «no expuesto» (ADR-0009). A utilization is always the provider's own figure.
/// </summary>
public sealed partial class QuotaViewModel : ObservableObject
{
    /// <summary>The shortest time between two reads after Codex runs (docs/14 section 11, open question 2).</summary>
    public static readonly TimeSpan CodexReadInterval = TimeSpan.FromMinutes(5);

    private readonly IQuotaObservations? _observations;
    private readonly IProviderQuotaReader? _codex;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _ui;
    private IReadOnlyList<QuotaObservation> _latest = [];
    private string? _recordProblem;
    private QuotaReadResult? _lastRead;
    private DateTimeOffset? _lastReadAt;
    private bool _codexOptedIn;

    /// <param name="observations">The user's quota record; null when the shell was composed without it.</param>
    /// <param name="codex">Reads the Codex quota from its app-server; null when it was not composed.</param>
    /// <param name="time">The clock of the ages and of the interval between reads.</param>
    /// <param name="runEnds">
    /// The ends of the agent runs of the process (docs/18 P37): after each run of Codex the quota of Codex is read, with the
    /// opt-in and never twice within <see cref="CodexReadInterval"/> (<see cref="CodexRunFinishedAsync"/>). Without it, only
    /// «Consultar cuota de Codex» reads it.
    /// </param>
    /// <param name="uiContext">Where a read after a run is made; defaults to the creating thread's context.</param>
    public QuotaViewModel(IQuotaObservations? observations, IProviderQuotaReader? codex, TimeProvider time, IAgentRunEnds? runEnds = null, SynchronizationContext? uiContext = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _observations = observations;
        _codex = codex;
        _time = time;
        _ui = uiContext ?? SynchronizationContext.Current;
        if (runEnds is not null)
        {
            runEnds.Ended += OnRunEnded;
        }

        Rebuild();
    }

    /// <summary>Completes once the read asked by the end of the last run of Codex is done (or there was none to do).</summary>
    public Task CodexReadAfterRun { get; private set; } = Task.CompletedTask;

    /// <summary>A run ended on its own thread: a run of Codex asks for a read on the UI thread; any other run asks nothing.</summary>
    private void OnRunEnded(object? sender, AgentRunEnded ended)
    {
        if (ended.Provider != ProviderId.CodexCli)
        {
            return;
        }

        if (_ui is null)
        {
            CodexReadAfterRun = CodexRunFinishedAsync();
        }
        else
        {
            _ui.Post(_ => CodexReadAfterRun = CodexRunFinishedAsync(), null);
        }
    }

    public ProviderQuota Claude { get; private set; } = null!;

    public ProviderQuota Codex { get; private set; } = null!;

    public ProviderQuota Gemini { get; private set; } = null!;

    /// <summary>A read of the Codex quota is going on.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReadCodexCommand))]
    private bool _isReadingCodex;

    /// <summary>The user opted in to reading the Codex quota (the saved setting). Off: nothing is launched and nothing of Codex is shown.</summary>
    public bool CodexOptedIn
    {
        get => _codexOptedIn;
        set
        {
            if (SetProperty(ref _codexOptedIn, value))
            {
                ReadCodexCommand.NotifyCanExecuteChanged();
                Rebuild();
            }
        }
    }

    /// <summary>Reads the latest observation of each window from the user's record.</summary>
    public async Task RefreshAsync()
    {
        _recordProblem = null;
        try
        {
            _latest = _observations is null ? [] : await _observations.LatestAsync(CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The record is behind a port: a failure is shown in the panel, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _latest = [];
            _recordProblem = Texts.Format("Observability_Quota_RecordUnreadable", ex.Message);
        }

        Rebuild();
    }

    /// <summary>
    /// Called once a run of Codex finished (D-15, docs/18 P37; the provider pipeline says it through <see cref="IAgentRunEnds"/>):
    /// with the opt-in, the quota is read (<c>account/rateLimits/read</c>, no turn of the model), unless the last read is younger
    /// than <see cref="CodexReadInterval"/>. Without the opt-in nothing happens.
    /// </summary>
    public async Task CodexRunFinishedAsync()
    {
        if (!CanReadCodex() || (_lastReadAt is { } last && _time.GetUtcNow() - last < CodexReadInterval))
        {
            return;
        }

        await ReadCodexAsync().ConfigureAwait(true);
    }

    /// <summary>«Consultar cuota de Codex»: one read of the app-server, only with the opt-in.</summary>
    [RelayCommand(CanExecute = nameof(CanReadCodex))]
    private async Task ReadCodexAsync()
    {
        if (!CodexOptedIn || _codex is not { } reader)
        {
            return;
        }

        IsReadingCodex = true;
        try
        {
            // On the thread pool: the reader loads the settings, looks the executable up and starts a process before its first
            // real wait, and none of that runs on the UI thread (the read after a run of Codex starts on it).
            var result = await Task.Run(() => reader.ReadAsync(CancellationToken.None), CancellationToken.None).ConfigureAwait(true);
            _lastRead = result;
            if (result is { Status: QuotaReadStatus.Read, Observations.Count: > 0 } && _observations is { } record)
            {
                await record.AppendAsync(result.Observations, CancellationToken.None).ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // The reader is behind a port: whatever it throws is shown as «sin dato», never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _lastRead = new QuotaReadResult(QuotaReadStatus.Missing, [], Texts.Format("Observability_Quota_CodexReadFailed", ex.Message));
        }
        finally
        {
            _lastReadAt = _time.GetUtcNow();
            IsReadingCodex = false;
        }

        Rebuild();
    }

    private bool CanReadCodex() => CodexOptedIn && _codex is not null && !IsReadingCodex;

    private void Rebuild()
    {
        Claude = ClaudeQuota();
        Codex = CodexQuota();
        Gemini = new ProviderQuota("Gemini CLI", Texts.Get("Observability_Quota_GeminiNotExposed"), [], []);
        OnPropertyChanged(nameof(Claude));
        OnPropertyChanged(nameof(Codex));
        OnPropertyChanged(nameof(Gemini));
    }

    private ProviderQuota ClaudeQuota()
    {
        var windows = Rows(_latest.Where(o => o.Provider == ProviderId.ClaudeCode));
        var status = _observations is null ? Texts.Get("Observability_Quota_NoRecord")
            : _recordProblem is { } problem ? problem
            : windows.Count == 0 ? Texts.Get("Observability_Quota_ClaudeNone")
            : Texts.Get("Observability_Quota_ClaudeSeen");
        return new ProviderQuota("Claude Code", status, windows, []);
    }

    private ProviderQuota CodexQuota()
    {
        const string Name = "Codex CLI";
        if (!CodexOptedIn)
        {
            return new ProviderQuota(Name, Texts.Get("Observability_Quota_CodexOff"), [], []);
        }

        if (_codex is null)
        {
            return new ProviderQuota(Name, Texts.Get("Observability_Quota_CodexUnavailable"), [], []);
        }

        if (_lastRead is not { } read)
        {
            var kept = Rows(_latest.Where(o => o.Provider == ProviderId.CodexCli));
            return new ProviderQuota(
                Name,
                kept.Count == 0 ? Texts.Get("Observability_Quota_CodexNotRead") : Texts.Get("Observability_Quota_CodexKept"),
                kept,
                []);
        }

        List<string> notes = read.PlanType is { Length: > 0 } plan ? [Texts.Format("Observability_Quota_Plan", plan)] : [];
        var warnings = new List<string>();
        if (read.OrdinaryUsageAllowed is false)
        {
            warnings.Add(Texts.Get("Observability_Quota_CodexNotAllowed"));
        }

        warnings.AddRange(read.Warnings);
        var quota = read.Status switch
        {
            QuotaReadStatus.Read => new ProviderQuota(Name, Texts.Get("Observability_Quota_CodexRead"), Rows(read.Observations), notes),
            QuotaReadStatus.NotExposed => new ProviderQuota(Name, Texts.Format("Observability_Quota_NotExposed", read.Reason ?? Texts.Get("Observability_Quota_NotExposedDefault")), [], notes),
            QuotaReadStatus.NotOptedIn => new ProviderQuota(Name, Texts.Get("Observability_Quota_NoPermission"), [], notes),
            _ => new ProviderQuota(Name, Texts.Format("Observability_Quota_Missing", read.Reason ?? Texts.Get("Observability_Quota_MissingDefault")), [], notes),
        };
        return quota with { Warnings = warnings };
    }

    private List<QuotaWindowRow> Rows(IEnumerable<QuotaObservation> observations) =>
    [
        .. observations
            .OrderBy(o => o.Window, StringComparer.Ordinal)
            .Select(o =>
            {
                var name = WindowName(o);
                var seen = Seen(o.ObservedAt);

                // A window whose reset has passed since it was seen: its figure is of before the reset, never shown as now. Its
                // bar says when it reset, so the line under the bar does not say it again (V36).
                var reset = o.ResetsAt is { } at && at <= _time.GetUtcNow() ? at : (DateTimeOffset?)null;
                var resets = reset is null && o.ResetsAt is { } when ? Resets(when) : null;
                var reached = reset is null && !string.IsNullOrWhiteSpace(o.Reached) ? Texts.Format("Observability_Quota_Reached", o.Reached) : null;
                return new QuotaWindowRow(name, MetricText.Percent(o.Utilization), seen, resets, reached)
                {
                    Window = o.Window,
                    ShortName = ShortName(o),
                    Bar = reset is { } past
                        ? MetricBars.QuotaAfterReset(name, ResetClock(past), o.Utilization, seen)
                        : MetricBars.Quota(name, o.Utilization, seen, resets, reached),
                };
            }),
    ];

    /// <summary>The window in a few words for the status bar: «5 h» and «7 días» for Claude, the length of any other, or its name.</summary>
    private static string ShortName(QuotaObservation observation) => observation.Window switch
    {
        "five_hour" => Texts.Get("Observability_Window_FiveHourShort"),
        "seven_day" => Texts.Get("Observability_Window_SevenDayShort"),
        _ when observation.Length is { } length => Length(length),
        _ => observation.Label ?? WindowLabel(observation.Window),
    };

    /// <summary>
    /// The name of a window of the subscription as every screen says it: «5 horas» and «7 días» (with the model when the
    /// window is of one) for Claude's <c>five_hour</c> and <c>seven_day</c>, or the provider's own name for any other.
    /// </summary>
    public static string WindowLabel(string window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window switch
        {
            "five_hour" => Texts.Get("Observability_Window_FiveHour"),
            "seven_day" => Texts.Get("Observability_Window_SevenDay"),
            "seven_day_opus" => Texts.Get("Observability_Window_SevenDayOpus"),
            "seven_day_sonnet" => Texts.Get("Observability_Window_SevenDaySonnet"),
            var raw => raw,
        };
    }

    private static string WindowName(QuotaObservation observation)
    {
        var name = observation.Label ?? WindowLabel(observation.Window);
        return observation.Length is { } length ? Texts.Format("Observability_Window_WithLength", name, Length(length)) : name;
    }

    private static string Length(TimeSpan length) =>
        length.TotalHours < 1 ? Texts.Format("Observability_Length_Minutes", (int)length.TotalMinutes)
        : length.TotalDays >= 1 && length.Ticks % TimeSpan.TicksPerDay == 0 ? ((int)length.TotalDays == 1 ? Texts.Get("Observability_Length_Day") : Texts.Format("Observability_Length_Days", (int)length.TotalDays))
        : Texts.Format("Observability_Length_Hours", (int)length.TotalHours);

    private string Seen(DateTimeOffset observedAt)
    {
        var age = _time.GetUtcNow() - observedAt;
        return age.TotalMinutes < 1 ? Texts.Get("Observability_Seen_Now")
            : age.TotalHours < 1 ? Texts.Format("Observability_Seen_Minutes", (int)age.TotalMinutes)
            : age.TotalHours < 48 ? Texts.Format("Observability_Seen_Hours", (int)age.TotalHours)
            : Texts.Format("Observability_Seen_Days", (int)age.TotalDays);
    }

    /// <summary>When the window resets, a moment still to come: «se reinicia hoy a las 15:00», «se reinicia el 04/11 a las 10:00».</summary>
    private string Resets(DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone);
        return Texts.Format(local.Date == _time.GetLocalNow().Date ? "Observability_Reset_Today" : "Observability_Reset_Later", ResetClock(at));
    }

    /// <summary>The moment of a reset as people say it: «a las 15:00» today, «el 04/11 a las 10:00» another day.</summary>
    private string ResetClock(DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone);
        var clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.Date == _time.GetLocalNow().Date
            ? Texts.Format("Observability_Clock_At", clock)
            : Texts.Format("Observability_Clock_OnDate", local.ToString(Texts.Get("Observability_Clock_DateFormat"), CultureInfo.InvariantCulture), clock);
    }
}
