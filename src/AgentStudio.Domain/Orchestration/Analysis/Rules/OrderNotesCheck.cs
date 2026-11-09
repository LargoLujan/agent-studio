using AgentStudio.Domain.Common;
using AgentStudio.Domain.Questions;

namespace AgentStudio.Domain.Orchestration.Analysis.Rules;

/// <summary>
/// What the system keeps of the notes of order an agent wrote (docs/17 D-19 and D-20), in the plan or in the answer of «Revisar
/// el orden»: only the notes about two different tasks of the plan, with a reason of one sentence (<see cref="OrderNotes.MaxReasonLength"/>
/// characters at most) without control or direction characters, once per pair, and <see cref="MaxNotesPerKind"/> of each kind
/// at most. The rest is left out, and why is said in English for the logs. The notes are text of an agent: kept, they still
/// never rule (the analyzer and <see cref="Work.TaskGraph"/> decide).
/// </summary>
public static class OrderNotesCheck
{
    /// <summary>The most notes of each kind kept: twice the tasks a plan may have.</summary>
    public const int MaxNotesPerKind = 40;

    public static (OrderNotes Kept, IReadOnlyList<string> Discarded) Check(OrderNotes notes, IEnumerable<TaskId> tasks)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(tasks);
        var known = tasks.ToHashSet();
        var discarded = new List<string>();

        List<T> Keep<T>(IReadOnlyList<T>? list, string kind, Func<T, (TaskId Left, TaskId Right, string Reason)> parts)
        {
            var kept = new List<T>();
            var seen = new HashSet<(TaskId, TaskId)>();
            foreach (var note in list ?? [])
            {
                if (note is null)
                {
                    discarded.Add($"A {kind} note is null.");
                    continue;
                }

                var (left, right, reason) = parts(note);
                var why = left == right ? "it names the same task twice"
                    : !known.Contains(left) || !known.Contains(right) ? "it names a task that is not in the plan"
                    : string.IsNullOrWhiteSpace(reason) ? "it gives no reason"
                    : reason.Length > OrderNotes.MaxReasonLength ? $"its reason has more than {OrderNotes.MaxReasonLength} characters"
                    : QuestionRules.HasControlCharacter(reason) || QuestionRules.HasBidiCharacter(reason) ? "its reason has control or direction characters"
                    : !seen.Add((left, right)) ? "it repeats a note"
                    : kept.Count >= MaxNotesPerKind ? $"there are more than {MaxNotesPerKind} of its kind"
                    : null;
                if (why is null)
                {
                    kept.Add(note);
                }
                else
                {
                    discarded.Add($"The {kind} note about '{left}' and '{right}' is left out: {why}.");
                }
            }

            return kept;
        }

        var dependencies = Keep(notes.Dependencies, "dependency", n => (n.From, n.To, n.Reason));
        var parallel = Keep(notes.ParallelOk, "parallel", n => (n.A, n.B, n.Reason));
        var harmful = Keep(notes.Harmful, "harmful", n => (n.First, n.Then, n.Reason));
        return (new OrderNotes(dependencies, parallel, harmful), discarded);
    }
}
