using System.Diagnostics;
using System.IO;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Infrastructure.Git;

namespace AgentStudio.Tests.Integration.Phase3;

/// <summary>Checks the Phase 3 integration tests share: evidence against its hashes, the user's repository, processes.</summary>
internal static class Phase3Checks
{
    private const string AgentStudioFolder = ".agentstudio/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Every evidence of the run, read back from the store: its SHA-256 is computed again from the content the store
    /// returns and from the content-addressed file on disk, and both match what the store recorded and what
    /// <c>EvidenceRegistered</c> announced in the log. With <paramref name="everyOneRegistered"/>, nothing of the run
    /// is in the store without being announced in the log.
    /// </summary>
    public static async Task<IReadOnlyList<EvidenceRecord>> AssertEvidenceIntactAsync(Phase3App app, RunId run, bool everyOneRegistered = true)
    {
        var records = await app.Evidence.ListByRunAsync(app.Project.ProjectId, run.Value, Ct);
        Assert.NotEmpty(records);
        foreach (var record in records)
        {
            byte[] content;
            await using (var stream = await app.Evidence.OpenContentAsync(app.Project.ProjectId, record.Id, Ct))
            {
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy, Ct);
                content = copy.ToArray();
            }

            Assert.Equal(record.ContentSha256, ContentHash.Sha256Hex(content));
            Assert.True(record.Matches(content), $"Evidence {record.Id} ({record.Title}) does not match its hash.");
            var file = Path.Combine(app.Storage.EvidenceDirectory, record.ContentSha256);
            Assert.Equal(record.ContentSha256, ContentHash.Sha256Hex(await File.ReadAllBytesAsync(file, Ct)));
        }

        var byId = records.ToDictionary(r => r.Id);
        var registered = await app.PayloadsAsync<EvidenceRegisteredPayload>(run);
        Assert.NotEmpty(registered);
        foreach (var (_, payload) in registered)
        {
            var record = byId.GetValueOrDefault(payload.Evidence);
            Assert.True(record is not null, $"Evidence {payload.Evidence} is announced in the log but not in the store.");
            Assert.Equal(record.ContentSha256, payload.Sha256);
            Assert.Equal(record.Kind, payload.Kind);
            Assert.Equal(record.Origin, payload.Origin);
        }

        if (everyOneRegistered)
        {
            Assert.Equal(
                records.Select(r => r.Id.Value).Order(StringComparer.Ordinal),
                registered.Select(p => p.Payload.Evidence.Value).Distinct().Order(StringComparer.Ordinal));
        }

        return records;
    }

    /// <summary>
    /// The capture without the status lines under <c>.agentstudio/</c>: the prompt journal of ADR-0008 is versioned in the
    /// user's repository, and Agent Studio writes it during every run (the known exception of docs/13 section 5).
    /// </summary>
    public static UserRepositoryState WithoutAgentStudio(UserRepositoryState state) =>
        state with { PorcelainStatus = [.. state.PorcelainStatus.Where(line => !UnderAgentStudio(line))] };

    /// <summary>The entries of <c>git status --porcelain -z</c> in a snapshot.</summary>
    public static IReadOnlyList<string> StatusEntries(MainTreeSnapshot snapshot) =>
        snapshot.Status.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    public static bool UnderAgentStudio(string statusLine) =>
        statusLine.Length > 3 && statusLine[3..].Trim('"').Replace('\\', '/').StartsWith(AgentStudioFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>The prompt-journal files Agent Studio wrote in the project.</summary>
    public static IReadOnlyList<string> JournalFiles(string projectRoot)
    {
        var folder = Path.Combine(projectRoot, ".agentstudio", "bitacora");
        return Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "*.md").Order(StringComparer.Ordinal)] : [];
    }

    /// <summary>The run's entries as "sequence kind summary", for assertion messages.</summary>
    public static async Task<string> DescribeAsync(Phase3App app, RunId run)
    {
        var text = new StringBuilder();
        foreach (var (entry, _) in await app.RunEntriesAsync(run))
        {
            text.Append(entry.Sequence).Append(' ').Append(entry.Kind).Append(' ').AppendLine(entry.Summary);
        }

        return text.ToString();
    }

    public static string Describe(RunResult result) =>
        $"{result.Phase}: {result.Summary} (unreachable [{string.Join(", ", result.Health.UnreachableTasks)}], retries exhausted [{string.Join(", ", result.Health.RetryExhaustedTasks)}])";

    /// <summary>Waits, in real time, until the condition holds.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > limit)
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(50, Ct);
        }
    }

    /// <summary>Whether a process is still alive.</summary>
    public static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>A short temporary folder of the test's own (an ASCII path, far from MAX_PATH), deleted at the end with its read-only files.</summary>
internal sealed class ScratchFolder : IDisposable
{
    public ScratchFolder(string prefix)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose() => AgentStudio.Tests.Infrastructure.Persistence.TempProjectFolder.ForceDelete(Path);
}