using System.Globalization;
using System.Text.Json;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Models;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Infrastructure.Models;

/// <summary>
/// Reads the local catalog of models (docs/17 D-23) from its JSON (<see cref="ModelCatalogData"/>) and checks it: its own
/// shape, the rules of the domain (<see cref="ModelCatalog.Validate"/>) and that every entry names its sources. It fails
/// closed: a catalog with any problem is not used, and the result is an empty catalog with the problems as stable codes
/// (<see cref="ModelCatalogDocument.Unreadable"/>), so nothing is classified and nothing breaks.
/// </summary>
/// <remarks>
/// The format, version <see cref="SupportedVersion"/>: <c>version</c>, <c>checkedOn</c> (<c>yyyy-MM-dd</c>), <c>sources</c>
/// (<c>id</c>, <c>description</c>, <c>location</c>, <c>checkedOn</c>), <c>rules</c> (the fields of
/// <see cref="ModelRecommendationRules"/>, efforts by their token) and <c>models</c> (<c>provider</c>, <c>testedCliVersion</c>,
/// <c>names</c>, <c>tier</c> as <c>light</c>, <c>balanced</c> or <c>maximum</c>, <c>efforts</c> by their token, <c>note</c> as a
/// coded text or null, and <c>sources</c> by id). No other member is allowed.
/// </remarks>
public static class ModelCatalogReader
{
    /// <summary>The version of the format this reader understands.</summary>
    public const int SupportedVersion = 1;

    private static readonly string[] RootMembers = ["version", "checkedOn", "sources", "rules", "models"];
    private static readonly string[] SourceMembers = ["id", "description", "location", "checkedOn"];
    private static readonly string[] RuleMembers =
        ["orchestratorMaximumFromMandatoryRequirements", "orchestratorMaximumFromFiles", "orchestratorEffort", "executorEffort", "judgeEffort"];

    private static readonly string[] EntryMembers = ["provider", "testedCliVersion", "names", "tier", "efforts", "note", "sources"];

    private static readonly ProviderId[] KnownProviders = [ProviderId.ClaudeCode, ProviderId.CodexCli, ProviderId.GeminiCli];

    /// <summary>The catalog in <paramref name="json"/>, or an empty one with the problems that made it unusable.</summary>
    public static ModelCatalogDocument Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var problems = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var read = ReadRoot(document.RootElement, problems);
            if (read is null || problems.Count > 0)
            {
                return ModelCatalogDocument.Unreadable(Distinct(problems));
            }

            problems.AddRange(read.Catalog.Validate());
            return problems.Count > 0 ? ModelCatalogDocument.Unreadable(Distinct(problems)) : read;
        }
        catch (JsonException)
        {
            return ModelCatalogDocument.Unreadable(["model-catalog.json-invalid"]);
        }
    }

    private static ModelCatalogDocument? ReadRoot(JsonElement root, List<string> problems)
    {
        if (root.ValueKind != JsonValueKind.Object || !OnlyMembers(root, RootMembers))
        {
            problems.Add("model-catalog.shape-invalid");
            return null;
        }

        var version = Int(root, "version");
        if (version is null)
        {
            problems.Add("model-catalog.version-required");
            return null;
        }

        if (version > SupportedVersion)
        {
            problems.Add("model-catalog.version-unsupported");
            return null;
        }

        var checkedOn = Date(root, "checkedOn");
        if (checkedOn is null)
        {
            problems.Add("model-catalog.checked-on-invalid");
        }

        var sources = ReadSources(root, problems);
        var rules = ReadRules(root, problems);
        var entries = new List<ModelCatalogEntry>();
        var entrySources = new List<IReadOnlyList<string>>();
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            problems.Add("model-catalog.models-required");
            return null;
        }

        var known = sources.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var model in models.EnumerateArray())
        {
            if (ReadEntry(model, known, problems) is { } read)
            {
                entries.Add(read.Entry);
                entrySources.Add(read.Sources);
            }
        }

        return new ModelCatalogDocument(new ModelCatalog(version.Value, entries, rules), checkedOn ?? DateOnly.MinValue, sources, entrySources, []);
    }

    private static List<ModelCatalogSource> ReadSources(JsonElement root, List<string> problems)
    {
        var sources = new List<ModelCatalogSource>();
        if (!root.TryGetProperty("sources", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
        {
            problems.Add("model-catalog.sources-required");
            return sources;
        }

        foreach (var item in array.EnumerateArray())
        {
            var id = Text(item, "id");
            var description = Text(item, "description");
            var location = Text(item, "location");
            var checkedOn = Date(item, "checkedOn");
            if (item.ValueKind != JsonValueKind.Object || !OnlyMembers(item, SourceMembers) || id is null || description is null || location is null || checkedOn is null)
            {
                problems.Add("model-catalog.source-invalid");
                continue;
            }

            if (sources.Any(s => s.Id == id))
            {
                problems.Add("model-catalog.source-duplicate");
                continue;
            }

            sources.Add(new ModelCatalogSource(id, description, location, checkedOn.Value));
        }

        return sources;
    }

    private static ModelRecommendationRules ReadRules(JsonElement root, List<string> problems)
    {
        if (!root.TryGetProperty("rules", out var rules))
        {
            return ModelRecommendationRules.Default;
        }

        var mandatory = Int(rules, "orchestratorMaximumFromMandatoryRequirements");
        var files = Int(rules, "orchestratorMaximumFromFiles");
        var orchestrator = Effort(rules, "orchestratorEffort");
        var executor = Effort(rules, "executorEffort");
        var judge = Effort(rules, "judgeEffort");
        if (rules.ValueKind != JsonValueKind.Object || !OnlyMembers(rules, RuleMembers)
            || mandatory is not > 0 || files is not > 0 || orchestrator is null || executor is null || judge is null)
        {
            problems.Add("model-catalog.rules-invalid");
            return ModelRecommendationRules.Default;
        }

        return new ModelRecommendationRules(mandatory.Value, files.Value, orchestrator.Value, executor.Value, judge.Value);
    }

    private static (ModelCatalogEntry Entry, IReadOnlyList<string> Sources)? ReadEntry(JsonElement item, HashSet<string> knownSources, List<string> problems)
    {
        if (item.ValueKind != JsonValueKind.Object || !OnlyMembers(item, EntryMembers))
        {
            problems.Add("model-catalog.entry-invalid");
            return null;
        }

        var provider = Text(item, "provider") is { } providerText ? KnownProviders.FirstOrDefault(p => p.Value == providerText) : default;
        if (provider == default)
        {
            problems.Add("model-catalog.provider-unknown");
            return null;
        }

        var tier = Text(item, "tier") switch
        {
            "light" => ModelTier.Light,
            "balanced" => ModelTier.Balanced,
            "maximum" => ModelTier.Maximum,
            _ => (ModelTier?)null,
        };
        if (tier is null)
        {
            problems.Add("model-catalog.tier-unknown");
            return null;
        }

        var names = Texts(item, "names");
        var efforts = new List<ReasoningEffort>();
        if (!item.TryGetProperty("efforts", out var effortArray) || effortArray.ValueKind != JsonValueKind.Array)
        {
            problems.Add("model-catalog.entry-invalid");
            return null;
        }

        foreach (var token in effortArray.EnumerateArray())
        {
            if (token.ValueKind == JsonValueKind.String && EffortOf(token.GetString()) is { } effort)
            {
                efforts.Add(effort);
            }
            else
            {
                problems.Add("model-catalog.effort-unknown");
            }
        }

        // That every effort is one its CLI takes is the domain's rule (model-catalog.effort-not-for-cli, ModelCatalogEntry.Validate).
        string? note = null;
        if (item.TryGetProperty("note", out var noteElement) && noteElement.ValueKind != JsonValueKind.Null)
        {
            note = noteElement.ValueKind == JsonValueKind.String ? noteElement.GetString() : null;
            if (!UserText.IsCode(note))
            {
                problems.Add("model-catalog.note-not-a-code");
            }
        }

        var sources = Texts(item, "sources");
        if (sources is null || sources.Count == 0)
        {
            problems.Add("model-catalog.entry-sources-required");
        }
        else if (sources.Any(s => !knownSources.Contains(s)))
        {
            problems.Add("model-catalog.entry-source-unknown");
        }

        if (names is null)
        {
            problems.Add("model-catalog.entry-invalid");
            return null;
        }

        // A missing version is left blank, so the domain's own rule (model-catalog.cli-version-required) names it.
        var version = Text(item, "testedCliVersion") ?? string.Empty;
        return (new ModelCatalogEntry(provider, version, names, tier.Value, efforts, note), sources ?? []);
    }

    /// <summary>The effort spelt <paramref name="token"/> (<see cref="ReasoningEfforts.Token"/>), or null.</summary>
    public static ReasoningEffort? EffortOf(string? token) =>
        Enum.GetValues<ReasoningEffort>().Cast<ReasoningEffort?>().FirstOrDefault(e => string.Equals(ReasoningEfforts.Token(e!.Value), token, StringComparison.Ordinal));

    private static bool OnlyMembers(JsonElement element, string[] allowed) =>
        element.EnumerateObject().All(p => allowed.Contains(p.Name, StringComparer.Ordinal))
        && element.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == element.EnumerateObject().Count();

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static List<string>? Texts(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var texts = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            // A blank or non-text name is kept as blank, so the domain's own rule (model-catalog.name-blank) names it.
            texts.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty);
        }

        return texts;
    }

    private static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static DateOnly? Date(JsonElement element, string name) =>
        Text(element, name) is { } text && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static ReasoningEffort? Effort(JsonElement element, string name) => EffortOf(Text(element, name));

    private static List<string> Distinct(List<string> problems) => [.. problems.Distinct(StringComparer.Ordinal)];
}
