using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Domain.Models;

/// <summary>
/// The stable codes of what is said about the model and the effort of an agent (docs/17 D-23 and D-25): a model or an effort
/// that is not known never breaks a launch, it goes with the default of the CLI or as it was written, and one of these says so.
/// The desktop paints each with its resource (<c>Text_</c> and the code, ADR-0054).
/// </summary>
public static class ModelNoticeCodes
{
    /// <summary>«Sin clasificar»: the catalog does not know the model; the CLI receives it as written. Argument: the model.</summary>
    public const string Unclassified = "model.unclassified";

    /// <summary>The CLI does not take the effort (or it is not one of the list): none is passed and the CLI uses its own. Argument: the effort.</summary>
    public const string EffortCliDefault = "model.effort-cli-default";

    /// <summary>The catalog does not list the effort for the model: it is passed, and what the model does with it is not checked. Arguments: the model and the effort.</summary>
    public const string EffortNotInCatalog = "model.effort-not-in-catalog";

    /// <summary>An effort with the default model of the CLI: which model that is, and whether it takes the effort, is not known. Argument: the effort.</summary>
    public const string EffortForDefaultModel = "model.effort-default-model";

    /// <summary>Every code, for the tests of the resources.</summary>
    public static IReadOnlyList<string> All { get; } = [Unclassified, EffortCliDefault, EffortNotInCatalog, EffortForDefaultModel];
}

/// <summary>One thing to say about the model or the effort of an agent: a code of <see cref="ModelNoticeCodes"/> and its arguments.</summary>
public sealed record ModelNotice(string Code, IReadOnlyList<object> Arguments)
{
    /// <summary>A notice with its arguments checked (<see cref="TextArguments"/>).</summary>
    public static ModelNotice Of(string code, params object[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return new ModelNotice(code, TextArguments.Checked(arguments, nameof(arguments)));
    }

    /// <summary>Two notices are equal when they have the same code and the same arguments, in order.</summary>
    public bool Equals(ModelNotice? other) => other is not null && Code == other.Code && Arguments.SequenceEqual(other.Arguments);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Code, Arguments.Count);
}

/// <summary>
/// What is said about the model and the effort an agent would be launched with (docs/17 D-23 and D-25), with the same rule the
/// launch follows (<see cref="ReasoningEfforts.Accepts"/>: an effort its CLI does not take is not passed): pure, deterministic and
/// without quota.
/// </summary>
public static class ModelNotices
{
    /// <summary>The notices of <paramref name="agent"/>, against <paramref name="catalog"/> (null when there is none: nothing is classified).</summary>
    public static IReadOnlyList<ModelNotice> Of(ModelCatalog? catalog, AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return Of(catalog, agent.Model.Provider, agent.Model.Model, agent.Effort);
    }

    /// <summary>The notices of a model of <paramref name="provider"/> (null or blank: the default of the CLI) with an effort (null: none).</summary>
    public static IReadOnlyList<ModelNotice> Of(ModelCatalog? catalog, ProviderId provider, string? model, ReasoningEffort? effort)
    {
        var notices = new List<ModelNotice>();
        var named = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        var entry = named is null ? null : catalog?.Find(provider, named);
        if (named is not null && entry is null)
        {
            notices.Add(ModelNotice.Of(ModelNoticeCodes.Unclassified, named));
        }

        if (effort is not { } value)
        {
            return notices;
        }

        if (!ReasoningEfforts.Accepts(provider, value))
        {
            notices.Add(ModelNotice.Of(ModelNoticeCodes.EffortCliDefault, Name(value)));
        }
        else if (named is null)
        {
            notices.Add(ModelNotice.Of(ModelNoticeCodes.EffortForDefaultModel, Name(value)));
        }
        else if (entry is not null && !entry.Accepts(value))
        {
            notices.Add(ModelNotice.Of(ModelNoticeCodes.EffortNotInCatalog, named, Name(value)));
        }

        return notices;
    }

    /// <summary>The token of an effort of the list, or its number for one outside it (never an exception).</summary>
    private static string Name(ReasoningEffort effort) =>
        Enum.IsDefined(effort) ? ReasoningEfforts.Token(effort) : ((int)effort).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
