using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Debate.Audit;

/// <summary>
/// The trusted verification profile of a project (ADR-0016), as the audit of the whole project needs it: the one the user
/// saved (<c>&lt;root&gt;\.agentstudio\verification.json</c>), or null when the project has none. The desktop's
/// <c>IVerificationProfileSource.LoadAsync</c> has this very shape, so the composition adapts it as it is.
/// </summary>
public interface IAuditVerificationProfiles
{
    /// <summary>The project's profile, or null when it has none. A file that cannot be used throws, saying why.</summary>
    Task<VerificationProfile?> LoadAsync(ProjectContext project, CancellationToken cancellationToken);
}

/// <summary>
/// Where the audit of the whole project puts the read-only copy of the commit it audits:
/// <c>&lt;SnapshotRoot&gt;\&lt;project&gt;\&lt;audit&gt;\snapshot</c> (the composition gives a folder under
/// <c>%LOCALAPPDATA%\AgentStudio</c>). The copy is removed when the audit ends.
/// </summary>
public sealed record AuditOptions
{
    public AuditOptions(string snapshotRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotRoot);
        SnapshotRoot = snapshotRoot;
    }

    public string SnapshotRoot { get; }
}

/// <summary>The ids of the audits of the whole project (docs/15 D-01): <c>aud-yyyyMMdd-xxxxxx</c>, the day in UTC and six random hexadecimal digits.</summary>
public static class AuditIds
{
    public const string Prefix = "aud-";

    /// <summary>A new id that no entry of <paramref name="log"/> uses as its run.</summary>
    public static RunId New(TimeProvider time, IReadOnlyList<DecisionEntry> log)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        var taken = RunIdsOf(log);
        var day = time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        while (true)
        {
            var id = $"{Prefix}{day}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";
            if (!taken.Contains(id))
            {
                return new RunId(id);
            }
        }
    }

    /// <summary>Every run id an entry of the log names (its payload's <c>runId</c>), whatever the kind of unit.</summary>
    public static IReadOnlySet<string> RunIdsOf(IReadOnlyList<DecisionEntry> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in log)
        {
            try
            {
                using var document = JsonDocument.Parse(entry.PayloadJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("runId", out var run)
                    && run.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(run.GetString()))
                {
                    ids.Add(run.GetString()!);
                }
            }
            catch (JsonException)
            {
                // An entry whose payload is not JSON names no run.
            }
        }

        return ids;
    }
}
