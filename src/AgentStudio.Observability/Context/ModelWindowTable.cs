using System.Globalization;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Observability.Context;

/// <summary>
/// The base table of context windows by model (docs/18 P3 and P36, tanda 2): the window Agent Studio assumes for a model
/// that the project's log has not seen yet, so that the first run of a new project already has a percentage while its
/// steps come in. Claude Code reports the window of a model only in the result of the attempt
/// (<c>result.modelUsage.&lt;modelo&gt;.contextWindow</c>), so without this table a step has tokens and no percentage until
/// the attempt ends (docs/14 D-10).
/// <list type="bullet">
/// <item>A window from this table is always «estimado por Agent Studio» (<see cref="MetricOrigin.EstimatedBySystem"/>),
/// never exact: the window the CLI reports in the attempt, and the one learned from an earlier run of the project, win
/// over it (<see cref="ModelWindowCatalog"/>).</item>
/// <item>A model the table does not know has no window: only its tokens are shown, as before.</item>
/// <item>Codex has no entries: <c>codex exec</c> exposes neither the window nor the occupancy of a step (docs/14 D-09), so
/// its context is «no expuesto» and a window would never be used. No window of Codex is invented.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Version <see cref="Version"/>. Sources, checked on 2026-10-30:
/// </para>
/// <list type="bullet">
/// <item>The capture of Claude Code 2.1.290 (<c>tests/AgentStudio.Tests/Providers/Fixtures/claude-code/2.1.290/01-texto.jsonl</c>,
/// <c>result.modelUsage</c>): <c>claude-haiku-4-5-20251001</c>, 200 000 tokens; <c>claude-sonnet-5-5</c>, 1 000 000.</item>
/// <item>Anthropic's catalogue of current models (the Claude API reference, cached on 2026-10-06): 1 000 000 tokens for
/// Claude Fable 5.1 and 5, Mythos 5.1, Opus 5.5, 5, 4.8, 4.7 and 4.6, Sonnet 5.5, 5 and 4.6, and Haiku 5.5; 200 000 for
/// Haiku 4.5.</item>
/// </list>
/// <para>
/// The aliases of <c>claude --model</c> name a family, and the CLI resolves them to the model of its version. <c>opus</c>,
/// <c>sonnet</c>, <c>fable</c> and <c>opusplan</c> take 1 000 000, the window of every model of those families in the
/// catalogue. <c>haiku</c> takes 200 000, the smallest of its family (Haiku 4.5; Haiku 5.5 has 1 000 000): an estimate that
/// is too small shows the context fuller than it is and warns earlier, never later. A model with the suffix <c>[1m]</c> is
/// the variant with a window of 1 000 000.
/// </para>
/// <para>
/// Update it when a new model comes out, with its source and the date; a model reported by the CLI teaches its real window
/// to the project anyway after its first attempt (<see cref="ModelWindowCatalog"/>).
/// </para>
/// </remarks>
public sealed class ModelWindowTable
{
    /// <summary>The date of the table, which is its version: it says in every estimate where the window comes from.</summary>
    public const string Version = "2026-10-30";

    private const long OneMillion = 1_000_000;
    private const long TwoHundredThousand = 200_000;
    private const string OneMillionSuffix = "[1m]";

    private readonly Dictionary<string, long> _windows;

    /// <param name="windows">The window of each model id or alias, in tokens; the ids are compared without case.</param>
    public ModelWindowTable(IReadOnlyDictionary<string, long> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        _windows = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (model, tokens) in windows)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokens);
            _windows[model.Trim()] = tokens;
        }
    }

    /// <summary>The table of this version of Agent Studio (see the remarks of the class for its sources).</summary>
    public static ModelWindowTable Current { get; } = new(new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
    {
        // Claude, full ids (the catalogue of 2026-10-06 and the capture of Claude Code 2.1.290).
        ["claude-fable-5-1"] = OneMillion,
        ["claude-mythos-5-1"] = OneMillion,
        ["claude-fable-5"] = OneMillion,
        ["claude-opus-5-5"] = OneMillion,
        ["claude-opus-5"] = OneMillion,
        ["claude-opus-4-8"] = OneMillion,
        ["claude-opus-4-7"] = OneMillion,
        ["claude-opus-4-6"] = OneMillion,
        ["claude-sonnet-5-5"] = OneMillion,
        ["claude-sonnet-5"] = OneMillion,
        ["claude-sonnet-4-6"] = OneMillion,
        ["claude-haiku-5-5"] = OneMillion,
        ["claude-haiku-4-5"] = TwoHundredThousand,

        // The aliases of `claude --model`: the window of every model of the family, or the smallest for haiku.
        ["opus"] = OneMillion,
        ["sonnet"] = OneMillion,
        ["fable"] = OneMillion,
        ["opusplan"] = OneMillion,
        ["haiku"] = TwoHundredThousand,
    });

    /// <summary>The models the table knows, with their window in tokens.</summary>
    public IReadOnlyDictionary<string, long> Windows => _windows;

    /// <summary>
    /// The window of <paramref name="model"/> as an estimate of Agent Studio that names this table and its version; «no
    /// expuesto» for a provider that does not report its context (<paramref name="provider"/> other than Claude Code, or a
    /// model of Codex); missing for a model the table does not know.
    /// </summary>
    /// <param name="model">The model of the step as the CLI names it (with or without its date), or an alias.</param>
    /// <param name="provider">The provider of the attempt, when known.</param>
    public MetricValue<long> WindowFor(string? model, ProviderId? provider = null)
    {
        if ((provider is { } known && known != ProviderId.ClaudeCode) || IsCodexModel(model))
        {
            return MetricValue<long>.NotExposed("codex exec no informa de la ventana ni de la ocupación del contexto: la tabla base no tiene ventanas suyas");
        }

        if (Normalize(model) is not { } id)
        {
            return MetricValue<long>.Missing("sin modelo del paso: la tabla base no puede dar su ventana");
        }

        if (id.EndsWith(OneMillionSuffix, StringComparison.Ordinal))
        {
            return Estimate(model!, OneMillion);
        }

        return _windows.TryGetValue(id, out var tokens)
            ? Estimate(model!, tokens)
            : MetricValue<long>.Missing(string.Create(CultureInfo.InvariantCulture, $"la tabla base de ventanas ({Version}) no conoce {model!.Trim()}"));
    }

    /// <summary>
    /// The id the table looks up: trimmed, in lower case, without the date the CLI appends (<c>-20251001</c>, or
    /// <c>@20251001</c>); null for none. The suffix <c>[1m]</c> is kept: it says the window.
    /// </summary>
    public static string? Normalize(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var id = model.Trim().ToLowerInvariant();
        var oneMillion = id.EndsWith(OneMillionSuffix, StringComparison.Ordinal);
        if (oneMillion)
        {
            id = id[..^OneMillionSuffix.Length];
        }

        if (id.Length > 9 && id[^9] is '-' or '@' && id[^8..].All(char.IsAsciiDigit))
        {
            id = id[..^9];
        }

        return oneMillion ? id + OneMillionSuffix : id;
    }

    private static bool IsCodexModel(string? model) =>
        model is not null && (model.TrimStart().StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) || model.Contains("codex", StringComparison.OrdinalIgnoreCase));

    private static MetricValue<long> Estimate(string model, long tokens) =>
        MetricValue<long>.EstimatedBySystem(tokens, string.Create(CultureInfo.InvariantCulture, $"tabla base de ventanas de Agent Studio ({Version}), {model.Trim()}"));
}
