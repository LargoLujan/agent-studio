using System.Text.Json;

namespace AgentStudio.Debate.Audit.Checks;

/// <summary>What the audit reads of an attempt record: its task, attempt, outcome and the paths the attempt changed.</summary>
internal sealed record AttemptFacts(string Task, int Attempt, string Outcome, IReadOnlyList<string> ChangedPaths);

/// <summary>
/// Reads the attempt record the engine captures at the end of each attempt (<c>AttemptRecord</c>, internal to the
/// Orchestration module, which this one cannot see): JSON in camelCase, version 1, of kind
/// <see cref="OrchestratorAudit.AttemptRecordKind"/>. Only what the checks need is read; anything else in it is ignored.
/// </summary>
internal static class AttemptRecordReader
{
    public static bool TryRead(ReadOnlyMemory<byte> content, out AttemptFacts? facts)
    {
        facts = null;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1
                || !Text(root, "kind", out var kind) || !string.Equals(kind, OrchestratorAudit.AttemptRecordKind, StringComparison.Ordinal)
                || !Text(root, "task", out var task)
                || !root.TryGetProperty("attempt", out var attemptElement) || attemptElement.ValueKind != JsonValueKind.Number || !attemptElement.TryGetInt32(out var attempt)
                || !Text(root, "outcome", out var outcome)
                || !root.TryGetProperty("changedPaths", out var paths) || paths.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var changed = new List<string>();
            foreach (var path in paths.EnumerateArray())
            {
                if (path.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                changed.Add(path.GetString()!);
            }

            facts = new AttemptFacts(task, attempt, outcome, changed);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Text(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
        {
            return false;
        }

        value = element.GetString()!;
        return true;
    }
}
