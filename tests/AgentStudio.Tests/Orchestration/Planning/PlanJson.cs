using System.Text.Json.Nodes;

namespace AgentStudio.Tests.Orchestration.Planning;

/// <summary>Builds plan documents the way the orchestrator agent writes them (every field present), for tests to break.</summary>
internal static class PlanJson
{
    public static JsonObject Task(
        string id,
        string kind = "AgentWork",
        string? agent = "ejecutor",
        string workspace = "Write",
        JsonArray? dependsOn = null,
        JsonObject? condition = null,
        string? toolId = null,
        int maxRetries = 1,
        int? timeoutMinutes = null,
        string join = "AllCompleted",
        string[]? covers = null,
        string[]? writeScope = null,
        string[]? steps = null) =>
        new()
        {
            ["id"] = id,
            ["title"] = "Tarea " + id,
            ["kind"] = kind,
            ["agent"] = agent,
            ["objective"] = "Objetivo de " + id,
            ["instructions"] = "Instrucciones de " + id,
            ["dependsOn"] = dependsOn ?? [],
            ["join"] = join,
            ["covers"] = Strings(covers ?? []),
            ["writeScope"] = Strings(writeScope ?? (workspace == "Write" ? [$"src/{id}/"] : [])),
            ["verificationSteps"] = Strings(steps ?? []),
            ["condition"] = condition,
            ["toolId"] = toolId,
            ["maxRetries"] = maxRetries,
            ["timeoutMinutes"] = timeoutMinutes,
            ["workspace"] = workspace,
        };

    public static JsonObject Dependency(string prerequisite, string? branch = null) =>
        new() { ["prerequisite"] = prerequisite, ["branch"] = branch };

    public static JsonObject Predicate(string type, string task, string? key = null, string? value = null, string? pathPrefix = null) =>
        new() { ["type"] = type, ["task"] = task, ["key"] = key, ["value"] = value, ["pathPrefix"] = pathPrefix };

    public static JsonObject Plan(params JsonObject[] tasks) =>
        new()
        {
            ["summary"] = "Añadir Calc.Multiply",
            ["tasks"] = new JsonArray([.. tasks]),
            ["risks"] = Strings(["La compilación puede tardar"]),
        };

    public static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
