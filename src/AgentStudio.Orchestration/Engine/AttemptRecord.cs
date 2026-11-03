using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// What the system recorded at the end of an attempt, as system-captured evidence (JSON). It is the engine's own memory
/// of facts the decision log does not carry: the commit the attempt started from and the one it left (so a resumed run
/// knows the base of every worktree and the result of every completed task), the changed paths, the outputs of the
/// report and the latest test summary (the facts conditions are evaluated on). A completed task cites its record first
/// among its completion evidence.
/// </summary>
internal sealed record AttemptRecord(
    int V,
    string Kind,
    string Run,
    string Task,
    int Attempt,
    string Outcome,
    string? AttemptBase,
    string? Head,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<ExecutorOutput> Outputs,
    TestRunSummary? Tests,
    string? ReportEvidence,
    string? ReportSha256)
{
    public const string RecordKind = "agent-studio/attempt-record";
    public const string MediaType = "application/vnd.agentstudio.attempt+json";
    public const string TitlePrefix = "Registro del intento";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static AttemptRecord? TryParse(string json)
    {
        try
        {
            var record = JsonSerializer.Deserialize<AttemptRecord>(json, Options);
            return record is { V: 1, Kind: RecordKind } ? record : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Title(string task, int attempt) => $"{TitlePrefix} {task} #{attempt}";
}
