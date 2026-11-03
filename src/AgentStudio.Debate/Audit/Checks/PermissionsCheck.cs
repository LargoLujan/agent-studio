using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;

namespace AgentStudio.Debate.Audit.Checks;

/// <summary>
/// Permissions respected (D-33, <c>orchestrator.permissions</c>, high):
/// <list type="bullet">
/// <item>every <c>PermissionDenied</c> of the run is an agent asking for what its permissions do not allow;</item>
/// <item>a task the approved plan does not let write (anything but agent work in a writable worktree) that has a diff or an
/// attempt record with changed paths changed files without permission: its agent had no write permission at all, so it is
/// a finding even when the engine failed the attempt;</item>
/// <item>an attempt record of a writing task with paths outside its write scope (<see cref="WriteScope.Contains"/>) is a
/// finding when the run accepted that attempt (its outcome is completed, or the task completed citing it). The engine
/// fails an attempt that wrote outside its scope, and then nothing was accepted.</item>
/// </list>
/// An attempt record is read only as the log anchored it; one that cannot be read leaves the paths of its attempt unchecked,
/// which is a finding too.
/// </summary>
internal static class PermissionsCheck
{
    public static void Run(AuditedRun run, AuditFindings findings)
    {
        foreach (var item in run.Items.Where(i => i.Kind == DecisionKind.PermissionDenied))
        {
            findings.Add(OrchestratorAudit.PermissionsCode, Severity.High, AuditedRun.FreeFormTasks(item.Entry), [item.Sequence], FormattableString.Invariant(
                $"Entry {item.Sequence} is a PermissionDenied: an agent of the run asked for something its permissions do not allow ({item.Entry.Summary})."));
        }

        if (run.Plan is null)
        {
            return;
        }

        var accepted = run.Items
            .Select(i => i.Payload)
            .OfType<TaskStatusChangedPayload>()
            .Where(c => c.To == WorkTaskStatus.Completed)
            .SelectMany(c => (c.Evidence ?? []).Select(e => (c.Task, Evidence: e)))
            .ToHashSet();
        var readOnly = new Dictionary<(TaskId Task, int Attempt), ReadOnlyChanges>();
        foreach (var item in run.Items)
        {
            if (item.Payload is not EvidenceRegisteredPayload { Task: { } task } evidence || !run.Specs.TryGetValue(task, out var spec))
            {
                continue;
            }

            var writes = spec is { Kind: WorkTaskKind.AgentWork, Workspace: WorkspaceMode.Write };
            if (evidence.Kind == EvidenceKind.CodeDiff && !writes)
            {
                Changes(readOnly, task, evidence.Attempt ?? 0).Add(item.Sequence, $"diff {evidence.Evidence}", []);
            }

            if (!AuditedRun.MayBeAttemptRecord(evidence))
            {
                continue;
            }

            if (!run.TryContent(evidence.Evidence, evidence.Sha256, out var content, out var problem) || !AttemptRecordReader.TryRead(content, out var record))
            {
                if (evidence.Title is not null)
                {
                    var why = problem.Length > 0 ? problem : $"cannot be read as an attempt record ({OrchestratorAudit.AttemptRecordKind}, version 1)";
                    findings.Add(OrchestratorAudit.PermissionsCode, Severity.High, [task], [item.Sequence], FormattableString.Invariant(
                        $"The attempt record {evidence.Evidence} of task '{task}' (entry {item.Sequence}) {why}: the paths its attempt changed cannot be checked."));
                }

                continue;
            }

            if (!writes)
            {
                if (record!.ChangedPaths.Count > 0)
                {
                    Changes(readOnly, task, record.Attempt).Add(item.Sequence, $"attempt record {evidence.Evidence}", record.ChangedPaths);
                }

                continue;
            }

            var outside = record!.ChangedPaths.Where(path => !WriteScope.Contains(spec.WriteScope, path)).ToList();
            var taken = string.Equals(record.Outcome, "Completed", StringComparison.Ordinal) || accepted.Contains((task, evidence.Evidence));
            if (outside.Count > 0 && taken)
            {
                findings.Add(OrchestratorAudit.PermissionsCode, Severity.High, [task], [item.Sequence], FormattableString.Invariant(
                    $"The attempt record {evidence.Evidence} of task '{task}' (attempt {record.Attempt}, entry {item.Sequence}) lists {string.Join(", ", outside)} outside the write scope {string.Join(", ", spec.WriteScope)}, and the run accepted that attempt."));
            }
        }

        foreach (var ((task, attempt), changes) in readOnly.OrderBy(c => c.Value.First))
        {
            var spec = run.Specs[task];
            var paths = changes.Paths.Count > 0 ? string.Join(", ", changes.Paths) : "files";
            findings.Add(OrchestratorAudit.PermissionsCode, Severity.High, [task], changes.Entries, FormattableString.Invariant(
                $"Task '{task}' may not change files ({spec.Kind}, workspace {spec.Workspace}), and its attempt {attempt} changed {paths} ({string.Join(", ", changes.Sources)})."));
        }
    }

    private static ReadOnlyChanges Changes(Dictionary<(TaskId Task, int Attempt), ReadOnlyChanges> changes, TaskId task, int attempt)
    {
        if (!changes.TryGetValue((task, attempt), out var found))
        {
            changes[(task, attempt)] = found = new ReadOnlyChanges();
        }

        return found;
    }

    /// <summary>What a task that may not write left behind in one attempt: the entries, the evidence and the paths.</summary>
    private sealed class ReadOnlyChanges
    {
        public long First { get; private set; } = long.MaxValue;

        public List<long> Entries { get; } = [];

        public List<string> Sources { get; } = [];

        public SortedSet<string> Paths { get; } = new(StringComparer.Ordinal);

        public void Add(long entry, string source, IEnumerable<string> paths)
        {
            First = Math.Min(First, entry);
            Entries.Add(entry);
            Sources.Add(source);
            Paths.UnionWith(paths);
        }
    }
}
