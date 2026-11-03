using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Debate.Audit.Checks;

/// <summary>
/// Complete instructions (D-33, <c>orchestrator.instructions</c>, medium): every launch of an executor has its
/// <c>InstructionsIssued</c> before it, for the same task and attempt and with the same prompt; the instructions carry the
/// requirements (<c>Covers</c>) and the write scope of the approved plan; and the prompt, read only as the log anchored it,
/// carries the objective of the task and quotes literally the statement and every criterion of each requirement it covers
/// (a desirable one the memory pack left out for size is named instead, as <c>ExecutorPrompt</c> does). Requirements that
/// are not the ones <c>RunStarted</c> anchored are a finding of their own, and their text is not checked.
/// </summary>
internal static class InstructionsCheck
{
    public static void Run(AuditedRun run, AuditFindings findings)
    {
        if (run.RequirementsProblem is { } problem)
        {
            findings.Add(OrchestratorAudit.InstructionsCode, Severity.Medium, [], [run.StartedItem.Sequence], problem);
        }

        var issued = new List<(long Sequence, InstructionsIssuedPayload Payload)>();
        foreach (var item in run.Items)
        {
            switch (item.Payload)
            {
                case InstructionsIssuedPayload instructions:
                    issued.Add((item.Sequence, instructions));
                    break;
                case AgentRunStartedPayload launch when AuditedRun.IsExecutorLaunch(launch):
                    Launch(run, item, launch, issued, findings);
                    break;
            }
        }
    }

    private static void Launch(AuditedRun run, RunItem item, AgentRunStartedPayload launch, List<(long Sequence, InstructionsIssuedPayload Payload)> issued, AuditFindings findings)
    {
        var task = launch.Task!.Value;
        var head = FormattableString.Invariant($"Entry {item.Sequence} launches '{launch.Agent}' for task '{task}' (attempt {launch.Attempt})");
        var match = issued.LastOrDefault(i => i.Payload.Task == task && i.Payload.Attempt == launch.Attempt);
        if (match.Payload is not { } instructions)
        {
            findings.Add(OrchestratorAudit.InstructionsCode, Severity.Medium, [task], [item.Sequence],
                head + " with no InstructionsIssued before it: nothing in the log says what it was told.");
            return;
        }

        var at = FormattableString.Invariant($"InstructionsIssued (entry {match.Sequence})");
        var problems = new List<string>();
        if (instructions.PromptEvidenceId != launch.PromptEvidenceId || !string.Equals(instructions.PromptSha256, launch.PromptSha256, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"it was launched with the prompt {launch.PromptEvidenceId} ({launch.PromptSha256}), not with {instructions.PromptEvidenceId} ({instructions.PromptSha256}), the one {at} announced");
        }

        if (run.Specs.TryGetValue(task, out var spec))
        {
            if (!SameSet(instructions.Covers.Select(r => r.Value), spec.Covers.Select(r => r.Value)))
            {
                problems.Add($"{at} covers {List(instructions.Covers.Select(r => r.Value))}, but the plan's task covers {List(spec.Covers.Select(r => r.Value))}");
            }

            if (!SameSet(instructions.WriteScope, spec.WriteScope))
            {
                problems.Add($"{at} gives the write scope {List(instructions.WriteScope)}, but the plan's task has {List(spec.WriteScope)}");
            }
        }

        var prompt = instructions.PromptEvidenceId;
        if (!run.TryText(prompt, instructions.PromptSha256, out var text, out var unreadable))
        {
            problems.Add($"its prompt {prompt} {unreadable}");
        }
        else if (spec is not null)
        {
            if (!string.IsNullOrWhiteSpace(spec.Objective) && !text.Contains(spec.Objective, StringComparison.Ordinal))
            {
                problems.Add($"its prompt {prompt} does not carry the objective of the plan's task");
            }

            if (run.Requirements is { } requirements)
            {
                foreach (var requirement in spec.Covers.Select(requirements.Find).OfType<Requirement>())
                {
                    if (Quoting(requirement, text, launch) is { } missing)
                    {
                        problems.Add($"its prompt {prompt} {missing}");
                    }
                }
            }
        }

        if (problems.Count > 0)
        {
            findings.Add(OrchestratorAudit.InstructionsCode, Severity.Medium, [task], [item.Sequence], $"{head}: {string.Join("; ", problems)}.");
        }
    }

    /// <summary>What the prompt fails to quote of a covered requirement, or null when it quotes it all.</summary>
    private static string? Quoting(Requirement requirement, string prompt, AgentRunStartedPayload launch)
    {
        var id = requirement.Id.Value;
        var leftOut = requirement.Priority == RequirementPriority.Desirable
            && (launch.ContextPack?.Omitted ?? []).Contains(id, StringComparer.Ordinal);
        if (leftOut)
        {
            return prompt.Contains(id, StringComparison.Ordinal) ? null : $"neither quotes nor names requirement {id}, which its memory pack left out";
        }

        var missing = new List<string>();
        if (!prompt.Contains(requirement.Statement, StringComparison.Ordinal))
        {
            missing.Add($"the statement of {id}");
        }

        missing.AddRange(requirement.Criteria.Where(c => !prompt.Contains(c.Description, StringComparison.Ordinal)).Select(c => $"criterion {c.Id} of {id}"));
        return missing.Count == 0 ? null : $"does not quote {string.Join(" nor ", missing)} literally";
    }

    private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) => a.ToHashSet(StringComparer.Ordinal).SetEquals(b);

    private static string List(IEnumerable<string> values)
    {
        var text = string.Join(", ", values);
        return text.Length == 0 ? "nothing" : text;
    }
}
