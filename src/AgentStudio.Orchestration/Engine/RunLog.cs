using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Decisions;

namespace AgentStudio.Orchestration.Engine;

/// <summary>Reads the entries of one run out of a project's decision log (payloads carry the run id).</summary>
internal static class RunLog
{
    /// <summary>The typed payload of an entry, or null for kinds without one (or a payload that cannot be read).</summary>
    public static DecisionPayload? Payload(DecisionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            return DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The entries of <paramref name="run"/>, in log order, with their payloads.</summary>
    public static IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> Of(IEnumerable<DecisionEntry> entries, string run)
    {
        var result = new List<(DecisionEntry, DecisionPayload)>();
        foreach (var entry in entries)
        {
            if (Payload(entry) is { } payload && string.Equals(payload.RunId, run, StringComparison.Ordinal))
            {
                result.Add((entry, payload));
            }
        }

        return result;
    }

    /// <summary>Every run id that appears in the log.</summary>
    public static IReadOnlySet<string> RunIds(IEnumerable<DecisionEntry> entries) =>
        entries.Select(Payload).Where(p => p is not null).Select(p => p!.RunId).ToHashSet(StringComparer.Ordinal);
}
