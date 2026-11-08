using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Orchestration.Analysis.Rules;

/// <summary>
/// <see cref="FindingCodes.OrderNoteUnapplied"/> and <see cref="FindingCodes.OrderHarmful"/> (docs/17 D-19): the plan, as the
/// person edited it, no longer keeps a note of order of the orchestrator that can be checked. The notes are text of an agent:
/// they never rule, and their reason is never an argument of a sentence of the system (it is shown apart, framed as «nota del
/// orquestador»). Only on a sound graph, and only for notes whose two tasks are in the plan. Warning, with the dependency that
/// keeps the note (<see cref="AddDependencyFix"/>) when <see cref="Work.TaskGraph"/> admits it; none when it would make a cycle.
/// <list type="bullet">
/// <item><see cref="FindingCodes.OrderNoteUnapplied"/>: a note «<c>To</c> needs <c>From</c>» and <c>To</c> no longer comes after
/// <c>From</c>. Nodes: <c>To</c>, <c>From</c>. Arguments: the id of <c>To</c>, the id of <c>From</c>.</item>
/// <item><see cref="FindingCodes.OrderHarmful"/>: a note «<c>First</c> before <c>Then</c> would be harmful» and the plan lets
/// <c>First</c> run without <c>Then</c> before it. Nodes: <c>First</c>, <c>Then</c>. Arguments: the id of <c>First</c>, the id of
/// <c>Then</c>.</item>
/// </list>
/// The pairs that may run at once (<see cref="OrderNotes.ParallelOk"/>) say nothing the plan can break: a plan that orders them
/// is only slower.
/// </summary>
public sealed class OrderNoteRule : IMontageRule
{
    public IReadOnlyList<string> Codes { get; } = [FindingCodes.OrderNoteUnapplied, FindingCodes.OrderHarmful];

    public IEnumerable<Finding> Check(MontageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var notes = draft.OrderNotes ?? OrderNotes.None;
        if (draft.Plan is not { } plan || DraftFacts.Ancestors(plan) is not { } ancestors)
        {
            return [];
        }

        bool After(TaskId later, TaskId earlier) => ancestors.TryGetValue(later, out var before) && before.Contains(earlier);
        bool Known(TaskId task) => ancestors.ContainsKey(task);

        var findings = new List<Finding>();
        foreach (var note in (notes.Dependencies ?? []).Where(n => n is not null && n.From != n.To && Known(n.From) && Known(n.To)).DistinctBy(n => (n.From, n.To)))
        {
            if (!After(note.To, note.From))
            {
                findings.Add(Finding.Of(FindingCodes.OrderNoteUnapplied, FindingSeverity.Warning, [note.To.Value, note.From.Value], DraftFacts.DependencyFix(plan, note.To, note.From), note.To.Value, note.From.Value));
            }
        }

        foreach (var note in (notes.Harmful ?? []).Where(n => n is not null && n.First != n.Then && Known(n.First) && Known(n.Then)).DistinctBy(n => (n.First, n.Then)))
        {
            if (!After(note.First, note.Then))
            {
                findings.Add(Finding.Of(FindingCodes.OrderHarmful, FindingSeverity.Warning, [note.First.Value, note.Then.Value], DraftFacts.DependencyFix(plan, note.First, note.Then), note.First.Value, note.Then.Value));
            }
        }

        return findings;
    }
}
