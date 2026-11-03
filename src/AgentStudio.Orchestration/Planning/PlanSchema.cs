using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using static AgentStudio.Orchestration.Planning.StrictJsonSchema;

namespace AgentStudio.Orchestration.Planning;

/// <summary>
/// The JSON schema the orchestrator agent answers with (ADR-0013). It is built in code for each planning request, with
/// the agents of the roster (but the orchestrator), the requirements of the frozen set and the steps of the trusted
/// verification profile as enums, so the agent cannot name anything that does not exist. The schema stays inside the
/// subset that is valid for Claude's <c>--json-schema</c> and for OpenAI's strict mode (see <see cref="StrictJsonSchema"/>),
/// and must fit in <see cref="MaxBytes"/>, because Claude Code only accepts it inline in the command line.
/// </summary>
/// <remarks>
/// The schema is a first line of defence that makes well-formed answers likely; it proves nothing. The answer is still
/// parsed strictly (<see cref="PlanParser"/>) and judged by <see cref="PlanValidator"/>, which is the authority.
/// </remarks>
public static class PlanSchema
{
    /// <summary>The inline limit of <c>--json-schema</c> (the same as <c>ClaudeCodeArguments.MaxJsonSchemaBytes</c>).</summary>
    public const int MaxBytes = 16 * 1024;

    /// <summary>The schema as compact JSON (no line breaks, which the command line would reject).</summary>
    public static string Build(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var agents = context.Roster
            .Where(a => a.Role != AgentRole.Orchestrator)
            .Select(a => a.Id.Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var requirements = context.Requirements.Requirements.Select(r => r.Id.Value).Order(StringComparer.Ordinal).ToList();
        var steps = context.VerificationSteps.Order(StringComparer.Ordinal).ToList();
        var tools = context.ToolIds.Order(StringComparer.Ordinal).ToList();
        var kinds = NamesOf<WorkTaskKind>()
            .Where(k => k != nameof(WorkTaskKind.Debate) || context.Limits.DebateNodesAvailable)
            .ToList();

        var predicate = Object(
            ("type", Enum([nameof(TaskSucceeded), nameof(TestsPassed), nameof(OutputEquals), nameof(PathsChanged)])),
            ("task", String("Id de una tarea antecesora de la condición.")),
            ("key", Nullable(String("Solo OutputEquals."))),
            ("value", Nullable(String("Solo OutputEquals."))),
            ("pathPrefix", Nullable(String("Solo PathsChanged."))));

        var dependency = Object(
            ("prerequisite", String()),
            ("branch", NullableEnum(ConditionBranches.All.ToList())));

        var task = Object(
            ("id", String("Letra inicial y hasta 32 letras, dígitos, '_' o '-'.")),
            ("title", String()),
            ("kind", Enum(kinds)),
            ("agent", NullableEnum(agents)),
            ("objective", String()),
            ("instructions", String()),
            ("dependsOn", Array(dependency)),
            ("join", Enum(NamesOf<JoinPolicy>())),
            ("covers", Array(Enum(requirements))),
            ("writeScope", Array(String(), "Carpetas relativas que acaban en '/' o ficheros exactos.")),
            ("verificationSteps", Array(Enum(steps))),
            ("condition", Nullable(predicate)),
            ("toolId", NullableEnum(tools)),
            ("maxRetries", Integer()),
            ("timeoutMinutes", Nullable(Integer())),
            ("workspace", Enum(NamesOf<WorkspaceMode>())));

        var plan = Object(
            ("summary", String()),
            ("tasks", Array(task)),
            ("risks", Array(String())));

        return ToCompactJson(plan);
    }

    /// <summary>True if the schema can travel inline to the CLI.</summary>
    public static bool Fits(string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return Utf8Bytes(schema) <= MaxBytes;
    }

    private static IReadOnlyList<string> NamesOf<T>()
        where T : struct, Enum => System.Enum.GetNames<T>();
}
