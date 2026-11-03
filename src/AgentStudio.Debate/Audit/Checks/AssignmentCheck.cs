using AgentStudio.Application.Decisions;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;

namespace AgentStudio.Debate.Audit.Checks;

/// <summary>
/// Tasks assigned to agents with the right role and permissions (D-33, <c>orchestrator.assignment</c>, high). From
/// <c>TaskAssigned</c>, <c>TaskCreated</c> and the roster <c>RunStarted</c> anchored, as <c>PlanValidator</c> checks a plan:
/// <list type="bullet">
/// <item>every assignment is of a created task, the one the approved plan makes, to an agent of the roster;</item>
/// <item>the orchestrator only plans; judges and auditors only review, and only they review;</item>
/// <item>a reviewer does not run on a vendor that produced the work of the run (<see cref="RunLog.ProducerVendors"/>);</item>
/// <item>the assigned agent's definition, from the trusted header, is valid (a judge is read-only and gets no memory...);</item>
/// <item>every executor launched for a task is the agent it was assigned to, on the provider and vendor of the roster;</item>
/// <item>every task of the plan with an agent has its assignment in the log.</item>
/// </list>
/// </summary>
internal static class AssignmentCheck
{
    public static void Run(AuditedRun run, AuditFindings findings)
    {
        var producers = RunLog.ProducerVendors(run.Entries);
        var created = new HashSet<TaskId>();
        var assigned = new Dictionary<TaskId, AgentId>();
        foreach (var item in run.Items)
        {
            switch (item.Payload)
            {
                case TaskCreatedPayload task:
                    created.Add(task.Task);
                    break;
                case TaskAssignedPayload assignment:
                    Assignment(run, item, assignment, created, producers, findings);
                    assigned[assignment.Task] = assignment.Agent;
                    break;
                case AgentRunStartedPayload launch when AuditedRun.IsExecutorLaunch(launch):
                    Launch(run, item, launch, assigned, findings);
                    break;
            }
        }

        foreach (var spec in run.Specs.Values.Where(s => s.Agent is not null && !assigned.ContainsKey(s.Id)).OrderBy(s => s.Id.Value, StringComparer.Ordinal))
        {
            findings.Add(OrchestratorAudit.AssignmentCode, Severity.High, [spec.Id], [], FormattableString.Invariant(
                $"Task '{spec.Id}' of the approved plan is assigned to '{spec.Agent}', and the log never recorded its TaskAssigned."));
        }
    }

    private static void Assignment(AuditedRun run, RunItem item, TaskAssignedPayload assignment, HashSet<TaskId> created, IReadOnlySet<VendorId> producers, AuditFindings findings)
    {
        var (task, agent) = (assignment.Task, assignment.Agent);
        var problems = new List<string>();
        if (!created.Contains(task))
        {
            problems.Add("the run had not created that task (no TaskCreated before it)");
        }

        if (run.Plan is not null)
        {
            if (!run.Specs.TryGetValue(task, out var spec))
            {
                problems.Add("the task is not in the approved plan");
            }
            else if (spec.Agent != agent)
            {
                problems.Add(spec.Agent is { } planned ? $"the approved plan assigns it to '{planned}'" : "the approved plan assigns it to no agent");
            }
        }

        if (!run.Roster.TryGetValue(agent, out var member))
        {
            problems.Add($"'{agent}' is not in the roster RunStarted anchored");
        }
        else
        {
            var kind = run.KindOf(task);
            if (member.Role == AgentRole.Orchestrator)
            {
                problems.Add($"'{agent}' is the orchestrator, which only plans");
            }
            else if (member.Role is AgentRole.Judge or AgentRole.Auditor && kind is { } notReview && notReview != WorkTaskKind.Review)
            {
                problems.Add($"'{agent}' is a {member.Role}: judges and auditors only take review tasks, and '{task}' is {notReview}");
            }
            else if (kind == WorkTaskKind.Review && member.Role is not (AgentRole.Judge or AgentRole.Auditor))
            {
                problems.Add($"'{task}' is a review, and '{agent}' is a {member.Role}: only a judge or an auditor reviews");
            }

            if (kind == WorkTaskKind.Review && producers.Contains(member.Vendor))
            {
                var vendors = string.Join(", ", producers.Select(v => v.Value).Order(StringComparer.Ordinal));
                problems.Add($"'{agent}' reviews on vendor '{member.Vendor}', which also produced the work of the run ({vendors})");
            }
        }

        if (run.Definitions.TryGetValue(agent, out var definition) && definition.Validate() is { Count: > 0 } broken)
        {
            problems.Add($"the definition of '{agent}' breaks {string.Join(", ", broken)}");
        }

        if (problems.Count > 0)
        {
            findings.Add(OrchestratorAudit.AssignmentCode, Severity.High, [task], [item.Sequence], FormattableString.Invariant(
                $"Entry {item.Sequence} assigns task '{task}' to '{agent}': {string.Join("; ", problems)}."));
        }
    }

    private static void Launch(AuditedRun run, RunItem item, AgentRunStartedPayload launch, Dictionary<TaskId, AgentId> assigned, AuditFindings findings)
    {
        var task = launch.Task!.Value;
        var problems = new List<string>();
        if (!assigned.TryGetValue(task, out var agent))
        {
            problems.Add("the task was never assigned");
        }
        else if (agent != launch.Agent)
        {
            problems.Add($"the task was assigned to '{agent}'");
        }

        if (!run.Roster.TryGetValue(launch.Agent, out var member))
        {
            problems.Add($"'{launch.Agent}' is not in the roster RunStarted anchored");
        }
        else if (member.Provider != launch.Provider || member.Vendor != launch.Vendor)
        {
            problems.Add($"it runs on {launch.Provider}/{launch.Vendor}, and the roster RunStarted anchored has '{launch.Agent}' on {member.Provider}/{member.Vendor}");
        }

        if (problems.Count > 0)
        {
            findings.Add(OrchestratorAudit.AssignmentCode, Severity.High, [task], [item.Sequence], FormattableString.Invariant(
                $"Entry {item.Sequence} launches '{launch.Agent}' for task '{task}' (attempt {launch.Attempt}): {string.Join("; ", problems)}."));
        }
    }
}
