using AgentStudio.Desktop.Controls;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Observability.Context;

namespace AgentStudio.Desktop.Observability;

/// <summary>
/// The readings of the bars of every screen (docs/18 P3, tanda 2), built from the figures the screens already show, with the
/// words of <see cref="MetricText"/> and <see cref="ContextText"/>: an estimate carries «~» and never says «exacto», a figure
/// that is not there says «no expuesto» or «sin dato» and is never a bar at zero, and the level of the context is the one of
/// the thresholds of the settings.
/// </summary>
public static class MetricBars
{
    /// <summary>What a context bar says before the first step of its agent arrives.</summary>
    public const string NoObservationYet = "sin observaciones todavía";

    /// <summary>
    /// Whether the agents of <paramref name="provider"/> may report the context of each step, for a screen that follows an
    /// attempt without the provider itself: <c>codex exec</c> and Gemini do not (docs/14 D-09), so theirs is «no expuesto» from
    /// the start; Claude Code does, and any other provider (or one not known yet) is followed as one that may, so it says «sin
    /// observaciones todavía» until a step arrives, never «no expuesto» by a guess.
    /// </summary>
    public static bool ReportsContext(ProviderId? provider) => provider != ProviderId.CodexCli && provider != ProviderId.GeminiCli;

    /// <summary>
    /// The context of one step (docs/14 D-09 to D-12): the tokens of its prompt against the window of its model, judged with
    /// <paramref name="thresholds"/>. With a percentage, a bar with its origin and level; with the tokens alone (no window
    /// known), an empty track with the tokens; what the CLI does not expose, an empty track with «no expuesto».
    /// </summary>
    public static MetricBarReading Context(string label, string? model, MetricValue<long> used, MetricValue<long> window, ContextHealthThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(thresholds);
        var reading = ContextText.Read(model, used, window, thresholds);
        IReadOnlyList<double> marks = [thresholds.Warning, thresholds.Compress, thresholds.Critical];
        var report = new ContextHealthEvaluator(thresholds).Evaluate(used, window);
        if (report.Utilization is { Value: { } fraction } utilization)
        {
            return new MetricBarReading
            {
                Label = label,
                Value = fraction * 100,
                Maximum = 100,
                ValueText = reading.Occupancy.Shown,
                Origin = reading.Occupancy.Origin,
                OriginKind = utilization.Origin,
                IsEstimate = utilization.Provenance == MetricProvenance.Estimated,
                Level = LevelOf(report.Level),
                LevelText = reading.LevelText,
                Detail = Join(reading.Tokens, model, WindowSource(window)),
                Marks = marks,
            };
        }

        if (used.HasValue)
        {
            // D-10: the tokens without a percentage until a window of the model is known; nothing is judged.
            return new MetricBarReading
            {
                Label = label,
                ValueText = reading.Occupancy.Shown,
                Origin = reading.Occupancy.Origin,
                OriginKind = used.Origin,
                IsEstimate = used.Provenance == MetricProvenance.Estimated,
                LevelText = reading.LevelText,
                Detail = Join("sin porcentaje: la ventana no se conoce todavía", model),
                Marks = marks,
            };
        }

        var origin = MetricText.Tokens(used).Origin;
        return Empty(label, origin, used.Origin, used.Source);
    }

    /// <summary>The context of an attempt as its tracker follows it (<see cref="ContextHealthTracker"/>), judged with the thresholds of now.</summary>
    public static MetricBarReading Context(string label, ContextHealthState state, ContextHealthThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Steps == 0 && state.UsedTokens.Origin == MetricOrigin.Missing
            ? NoObservation(label)
            : Context(label, state.Model, state.UsedTokens, state.ContextWindow, thresholds);
    }

    /// <summary>A context bar of an agent that has started without any step yet: an empty track, «sin observaciones todavía».</summary>
    public static MetricBarReading NoObservation(string label) =>
        Empty(label, NoObservationYet, MetricOrigin.Missing, "el agente aún no ha informado de ningún paso");

    /// <summary>
    /// An empty track that says why there is no value: «no expuesto», «sin dato», «sin tareas en curso»… What the CLI does not
    /// expose says «no expuesto» whatever the reason given.
    /// </summary>
    public static MetricBarReading Empty(string label, string why, MetricOrigin origin = MetricOrigin.Missing, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
        return new MetricBarReading
        {
            Label = label,
            ValueText = why,
            Origin = origin == MetricOrigin.NotExposed ? MetricText.Origin(MetricOrigin.NotExposed) : why,
            OriginKind = origin,
            Detail = string.Equals(detail, why, StringComparison.Ordinal) ? null : detail,
        };
    }

    /// <summary>
    /// A window of the quota of the account (docs/14 D-14 and D-15): the utilization the provider gives (0 to 1), always its
    /// own figure. A limit the provider says was reached is drawn as critical and said in words.
    /// </summary>
    public static MetricBarReading Quota(string label, MetricValue<double> utilization, string? seen = null, string? resets = null, string? reached = null)
    {
        ArgumentNullException.ThrowIfNull(label);
        var figure = MetricText.Percent(utilization);
        if (utilization.Value is not { } fraction || !utilization.Origin.CarriesValue())
        {
            return Empty(label, figure.Origin, utilization.Origin, Join(resets, seen) ?? utilization.Source);
        }

        // The age goes next to the figure, in every form of the bar: the quota is what the provider said at that moment.
        return new MetricBarReading
        {
            Label = label,
            Value = fraction * 100,
            Maximum = 100,
            ValueText = figure.Shown,
            Origin = figure.Origin,
            OriginKind = utilization.Origin,
            IsEstimate = utilization.Provenance == MetricProvenance.Estimated,
            Level = reached is null ? MetricBarLevel.Neutral : MetricBarLevel.Critical,
            LevelText = reached,
            Detail = resets,
            Age = seen,
        };
    }

    /// <summary>
    /// A window of the quota whose reset has passed since the provider announced it: its figure is of before the reset and
    /// says nothing of now, so the track is empty and the words say it reset with no new figure; the old one goes to the detail.
    /// </summary>
    /// <param name="resetAt">When it reset, as people say it: «a las 15:00», «el 04/11 a las 10:00».</param>
    public static MetricBarReading QuotaAfterReset(string label, string resetAt, MetricValue<double> before, string? seen = null)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(resetAt);
        var figure = MetricText.Percent(before);

        // The status bar says it short (V38); the moment of the reset stays in the line, the name and the tooltip.
        return Empty(label, $"se reinició {resetAt}, sin dato nuevo", MetricOrigin.Missing, Join("antes del reinicio: " + figure.Text, seen))
            with { ShortValueText = "se reinició · sin dato" };
    }

    /// <summary>
    /// The budget of a run or a debate: the agent runs used against the maximum (measured by Agent Studio). Without the
    /// maximum, the runs used and «máximo sin dato», with an empty track.
    /// </summary>
    public static MetricBarReading Budget(string label, int used, int? maximum, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(label);
        var origin = MetricText.Origin(MetricOrigin.MeasuredBySystem);
        if (maximum is not { } max || max <= 0)
        {
            return new MetricBarReading
            {
                Label = label,
                ValueText = used == 1 ? "1 ejecución de agente usada" : string.Create(MetricText.Spanish, $"{used:N0} ejecuciones de agente usadas"),
                Origin = "máximo sin dato",
                OriginKind = MetricOrigin.Missing,
                Detail = detail,
            };
        }

        return new MetricBarReading
        {
            Label = label,
            Value = used,
            Maximum = max,
            ValueText = string.Create(MetricText.Spanish, $"{used:N0} de {max:N0}"),
            Origin = origin,
            OriginKind = MetricOrigin.MeasuredBySystem,
            Level = used >= max ? MetricBarLevel.Critical : MetricBarLevel.Neutral,
            LevelText = used >= max ? "presupuesto agotado" : null,
            Detail = detail ?? "ejecuciones de agente usadas frente al máximo de la ejecución",
        };
    }

    /// <summary>
    /// Tokens against a limit (the tokens of a debate against its maximum): a bar with the origin of the tokens when both are
    /// known; without a limit, the tokens alone with an empty track and «sin límite».
    /// </summary>
    public static MetricBarReading Tokens(string label, MetricValue<long> used, long? maximum, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(label);
        var figure = MetricText.Tokens(used);
        if (used.Value is not { } tokens || !used.Origin.CarriesValue())
        {
            return Empty(label, figure.Origin, used.Origin, detail ?? used.Source);
        }

        var text = figure.Shown + " tokens";
        if (maximum is not { } max || max <= 0)
        {
            return new MetricBarReading
            {
                Label = label,
                ValueText = text,
                Origin = figure.Origin,
                OriginKind = used.Origin,
                IsEstimate = used.Provenance == MetricProvenance.Estimated,
                LevelText = "sin límite",
                Detail = detail,
            };
        }

        return new MetricBarReading
        {
            Label = label,
            Value = tokens,
            Maximum = max,
            ValueText = string.Create(MetricText.Spanish, $"{figure.Shown} de {max:N0} tokens"),
            Origin = figure.Origin,
            OriginKind = used.Origin,
            IsEstimate = used.Provenance == MetricProvenance.Estimated,
            Level = tokens >= max ? MetricBarLevel.Critical : MetricBarLevel.Neutral,
            LevelText = tokens >= max ? "límite alcanzado" : null,
            Detail = detail,
        };
    }

    /// <summary>
    /// A sum of tokens (the turns of a debate) against a limit: the sum with its origin, «parcial (n de m)» when values are
    /// missing, and «~» when an estimate is among them.
    /// </summary>
    public static MetricBarReading Tokens(string label, MetricAggregate<long> used, long? maximum, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(used);
        var figure = MetricText.Tokens(used);
        var source = used.Sources.Count > 0 ? string.Join("; ", used.Sources) : "la suma de los turnos";
        var value = used.Sum is { } sum && used.Origin.CarriesValue()
            ? MetricValue<long>.Of(used.Origin, sum, source)
            : MetricValue<long>.Of(used.Origin.CarriesValue() ? MetricOrigin.Missing : used.Origin, null, source);
        var reading = Tokens(label, value, maximum, detail);
        return reading with { Origin = figure.Origin };
    }

    /// <summary>The level of a bar for a level of the thresholds; an unknown level is never «sano».</summary>
    public static MetricBarLevel LevelOf(ContextHealthLevel level) => level switch
    {
        ContextHealthLevel.Healthy => MetricBarLevel.Healthy,
        ContextHealthLevel.Warning => MetricBarLevel.Warning,
        ContextHealthLevel.CompressionRecommended => MetricBarLevel.Compression,
        ContextHealthLevel.Critical => MetricBarLevel.Critical,
        _ => MetricBarLevel.Neutral,
    };

    /// <summary>Where an estimated window comes from (the base table, a run before), for the detail of the bar.</summary>
    private static string? WindowSource(MetricValue<long> window) =>
        window.Origin == MetricOrigin.EstimatedBySystem ? "ventana estimada: " + window.Source : null;

    private static string? Join(params string?[] parts)
    {
        var shown = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return shown.Count == 0 ? null : string.Join(" · ", shown);
    }
}
