using System.Globalization;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Observability;

/// <summary>One window of a subscription as the panel shows it: its name, the utilization with its origin and how old it is.</summary>
/// <param name="Name">«5 horas», «7 días», or the provider's name of the limit with the length of its window.</param>
/// <param name="Seen">How old the observation is: «vista hace 12 min».</param>
/// <param name="Resets">When the window resets, in local time; null when the provider does not say.</param>
/// <param name="Reached">The limit the provider says was reached; null when it says none.</param>
public sealed record QuotaWindowRow(string Name, Figure Utilization, string Seen, string? Resets, string? Reached = null);

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
    private IReadOnlyList<QuotaObservation> _latest = [];
    private string? _recordProblem;
    private QuotaReadResult? _lastRead;
    private DateTimeOffset? _lastReadAt;
    private bool _codexOptedIn;

    /// <param name="observations">The user's quota record; null when the shell was composed without it.</param>
    /// <param name="codex">Reads the Codex quota from its app-server; null when it was not composed.</param>
    /// <param name="time">The clock of the ages and of the interval between reads.</param>
    public QuotaViewModel(IQuotaObservations? observations, IProviderQuotaReader? codex, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _observations = observations;
        _codex = codex;
        _time = time;
        Rebuild();
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
            _recordProblem = $"sin dato: no se pudo leer el registro de cuota: {ex.Message}";
        }

        Rebuild();
    }

    /// <summary>
    /// Called once a Codex run finished (D-15): with the opt-in, the quota is read, unless the last read is younger than
    /// <see cref="CodexReadInterval"/>. Without the opt-in nothing happens.
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
            var result = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(true);
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
            _lastRead = new QuotaReadResult(QuotaReadStatus.Missing, [], $"no se pudo leer la cuota de Codex: {ex.Message}");
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
        Gemini = new ProviderQuota("Gemini CLI", "no expuesto: Gemini está aparcado (ADR-0009) y su cuota de plan no tiene interfaz.", [], []);
        OnPropertyChanged(nameof(Claude));
        OnPropertyChanged(nameof(Codex));
        OnPropertyChanged(nameof(Gemini));
    }

    private ProviderQuota ClaudeQuota()
    {
        var windows = Rows(_latest.Where(o => o.Provider == ProviderId.ClaudeCode));
        var status = _observations is null ? "Sin dato: el registro de cuota no está disponible en esta composición."
            : _recordProblem is { } problem ? problem
            : windows.Count == 0 ? "Sin dato todavía: Claude anuncia su cuota al empezar cada ejecución."
            : "Del último anuncio de Claude al empezar una ejecución (rate_limit_event): sin llamadas extra.";
        return new ProviderQuota("Claude Code", status, windows, []);
    }

    private ProviderQuota CodexQuota()
    {
        const string Name = "Codex CLI";
        if (!CodexOptedIn)
        {
            return new ProviderQuota(Name, "Desactivado: activa «Leer la cuota de Codex» en «Ajustes». Sin tu permiso no se lanza nada.", [], []);
        }

        if (_codex is null)
        {
            return new ProviderQuota(Name, "La lectura de la cuota de Codex no está disponible en esta composición.", [], []);
        }

        if (_lastRead is not { } read)
        {
            var kept = Rows(_latest.Where(o => o.Provider == ProviderId.CodexCli));
            return new ProviderQuota(
                Name,
                kept.Count == 0 ? "Sin leer todavía: pulsa «Consultar cuota de Codex»." : "Última lectura guardada del app-server de Codex (account/rateLimits/read).",
                kept,
                []);
        }

        List<string> notes = read.PlanType is { Length: > 0 } plan ? [$"Plan: {plan}"] : [];
        var warnings = new List<string>();
        if (read.OrdinaryUsageAllowed is false)
        {
            warnings.Add("Codex dice que el uso ordinario no está permitido ahora.");
        }

        warnings.AddRange(read.Warnings);
        var quota = read.Status switch
        {
            QuotaReadStatus.Read => new ProviderQuota(Name, "Leída del app-server de Codex (account/rateLimits/read), sin turno del modelo.", Rows(read.Observations), notes),
            QuotaReadStatus.NotExposed => new ProviderQuota(Name, $"no expuesto: {read.Reason ?? "Codex no la ofrece aquí"}", [], notes),
            QuotaReadStatus.NotOptedIn => new ProviderQuota(Name, "Sin tu permiso no se lanzó nada.", [], notes),
            _ => new ProviderQuota(Name, $"sin dato: {read.Reason ?? "la lectura no llegó"}", [], notes),
        };
        return quota with { Warnings = warnings };
    }

    private List<QuotaWindowRow> Rows(IEnumerable<QuotaObservation> observations) =>
    [
        .. observations
            .OrderBy(o => o.Window, StringComparer.Ordinal)
            .Select(o => new QuotaWindowRow(
                WindowName(o),
                MetricText.Percent(o.Utilization),
                Seen(o.ObservedAt),
                o.ResetsAt is { } at ? Resets(at) : null,
                string.IsNullOrWhiteSpace(o.Reached) ? null : $"límite alcanzado: {o.Reached}")),
    ];

    /// <summary>
    /// The name of a window of the subscription as every screen says it: «5 horas» and «7 días» (with the model when the
    /// window is of one) for Claude's <c>five_hour</c> and <c>seven_day</c>, or the provider's own name for any other.
    /// </summary>
    public static string WindowLabel(string window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window switch
        {
            "five_hour" => "5 horas",
            "seven_day" => "7 días",
            "seven_day_opus" => "7 días · Opus",
            "seven_day_sonnet" => "7 días · Sonnet",
            var raw => raw,
        };
    }

    private static string WindowName(QuotaObservation observation)
    {
        var name = observation.Label ?? WindowLabel(observation.Window);
        return observation.Length is { } length ? $"{name} · ventana de {Length(length)}" : name;
    }

    private static string Length(TimeSpan length) =>
        length.TotalHours < 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)length.TotalMinutes} min")
        : length.TotalDays >= 1 && length.Ticks % TimeSpan.TicksPerDay == 0 ? ((int)length.TotalDays == 1 ? "1 día" : string.Create(CultureInfo.InvariantCulture, $"{(int)length.TotalDays} días"))
        : string.Create(CultureInfo.InvariantCulture, $"{(int)length.TotalHours} h");

    private string Seen(DateTimeOffset observedAt)
    {
        var age = _time.GetUtcNow() - observedAt;
        return age.TotalMinutes < 1 ? "vista hace menos de 1 min"
            : age.TotalHours < 1 ? string.Create(CultureInfo.InvariantCulture, $"vista hace {(int)age.TotalMinutes} min")
            : age.TotalHours < 48 ? string.Create(CultureInfo.InvariantCulture, $"vista hace {(int)age.TotalHours} h")
            : string.Create(CultureInfo.InvariantCulture, $"vista hace {(int)age.TotalDays} días");
    }

    private string Resets(DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone);
        var today = _time.GetLocalNow().Date;
        return local.Date == today
            ? $"se reinicia hoy a las {local.ToString("HH:mm", CultureInfo.InvariantCulture)}"
            : $"se reinicia el {local.ToString("dd/MM", CultureInfo.InvariantCulture)} a las {local.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }
}
