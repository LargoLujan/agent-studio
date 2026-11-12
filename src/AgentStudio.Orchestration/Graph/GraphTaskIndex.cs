using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Orchestration.Graph;

/// <summary>
/// The tasks that touched a folder (docs/20 D-22, F6-5): where the person goes from a project of Auto Graph to the work. It reads the records the engine
/// anchored in the decision log at the end of each attempt (<c>AttemptRecord</c>, with the files the attempt changed), as the engine itself reads them to resume:
/// each record is read only as the log anchored it (its hash must be the one registered), the latest attempt of each task is the one that counts, and the title
/// is the one the plan gave the task. A log that does not verify gives no tasks: nothing is read from a log whose chain is broken.
/// </summary>
public sealed class GraphTaskIndex(IDecisionRecorder decisions, IEvidenceStore evidence, IPlanStore plans) : IGraphTaskIndex
{
    /// <summary>The most attempt records read: the newest ones. A project with more has a long history that the panel does not need.</summary>
    public const int MaxRecords = 500;

    /// <summary>The most tasks returned for a folder.</summary>
    public const int MaxTasks = 100;

    private const int MaxRecordBytes = 1024 * 1024;

    public async Task<IReadOnlyList<GraphTaskRef>> TasksUnderAsync(ProjectContext project, string folder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!RepositoryPaths.IsSafeRelativeOrEmpty(folder))
        {
            throw new ArgumentException("The folder is relative to the project and never leads out of it.", nameof(folder));
        }

        var log = await decisions.LoadAsync(project.ProjectId, ct).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            return [];
        }

        var planOfRun = new Dictionary<string, string>(StringComparer.Ordinal);
        var registrations = new List<EvidenceRegisteredPayload>();
        foreach (var entry in log.Entries)
        {
            switch (RunLog.Payload(entry))
            {
                case RunStartedPayload started:
                    planOfRun[started.RunId] = started.PlanHash;
                    break;
                case EvidenceRegisteredPayload { Kind: EvidenceKind.Document, Origin: EvidenceOrigin.CapturedBySystem, Task: not null, Title: { } title } registered
                    when title.StartsWith(AttemptRecord.TitlePrefix, StringComparison.Ordinal):
                    registrations.Add(registered);
                    break;
            }
        }

        var latest = new Dictionary<(string Run, string Task), AttemptRecord>();
        foreach (var registration in registrations.Skip(Math.Max(0, registrations.Count - MaxRecords)))
        {
            ct.ThrowIfCancellationRequested();
            var record = await ReadAsync(project, registration, ct).ConfigureAwait(false);
            if (record is null || record.Run != registration.RunId || record.Task != registration.Task!.Value.Value)
            {
                continue;
            }

            var key = (record.Run, record.Task);
            if (!latest.TryGetValue(key, out var current) || record.Attempt >= current.Attempt)
            {
                latest[key] = record;
            }
        }

        var plansLoaded = new Dictionary<string, PlanDefinition?>(StringComparer.Ordinal);
        var result = new List<GraphTaskRef>();
        foreach (var ((run, task), record) in latest.OrderBy(e => e.Key.Run, StringComparer.Ordinal).ThenBy(e => e.Key.Task, StringComparer.Ordinal))
        {
            var under = record.ChangedPaths
                .Where(p => RepositoryPaths.IsSafeRelative(p) && IsUnder(p, folder))
                .Order(StringComparer.Ordinal)
                .ToList();
            if (under.Count == 0)
            {
                continue;
            }

            var title = await TitleAsync(project, run, task, planOfRun, plansLoaded, ct).ConfigureAwait(false);
            result.Add(new GraphTaskRef(new RunId(run), new TaskId(task), title, under));
            if (result.Count >= MaxTasks)
            {
                break;
            }
        }

        return result;
    }

    private static bool IsUnder(string path, string folder) =>
        folder.Length == 0 || path.StartsWith(folder + "/", StringComparison.Ordinal) || string.Equals(path, folder, StringComparison.Ordinal);

    private async Task<AttemptRecord?> ReadAsync(ProjectContext project, EvidenceRegisteredPayload registration, CancellationToken ct)
    {
        try
        {
            await using var stream = await evidence.OpenContentAsync(project.ProjectId, registration.Evidence, ct).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + read > MaxRecordBytes)
                {
                    return null;
                }

                memory.Write(buffer, 0, read);
            }

            // Read only as the log anchored it: a record rewritten after it was registered is not read.
            var bytes = memory.ToArray();
            return string.Equals(ContentHash.Sha256Hex(bytes), registration.Sha256, StringComparison.OrdinalIgnoreCase)
                ? AttemptRecord.TryParse(Encoding.UTF8.GetString(bytes))
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<string> TitleAsync(
        ProjectContext project,
        string run,
        string task,
        Dictionary<string, string> planOfRun,
        Dictionary<string, PlanDefinition?> plansLoaded,
        CancellationToken ct)
    {
        string? title = null;
        if (planOfRun.TryGetValue(run, out var hash))
        {
            if (!plansLoaded.TryGetValue(hash, out var plan))
            {
                try
                {
                    plan = await plans.FindAsync(project.ProjectId, hash, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
                {
                    plan = null;
                }

                plansLoaded[hash] = plan;
            }

            title = plan?.Tasks.FirstOrDefault(t => t.Id.Value == task)?.Title;
        }

        var clean = UntrustedText.Clean(string.IsNullOrWhiteSpace(title) ? task : title).Trim();
        if (clean.Length == 0)
        {
            clean = task;
        }

        return clean.Length > 200 ? clean[..200].TrimEnd() : clean;
    }
}
