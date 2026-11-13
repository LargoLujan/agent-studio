using System.Text.RegularExpressions;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Orchestration.Verification;

/// <summary>
/// The rules of a trusted verification profile (docs/13 section 5, ADR-0016). The profile file is checked against them
/// when it is loaded or saved, and <see cref="VerificationRunner"/> checks the steps it is given again before it runs
/// any of them. Every rule returns a message instead of throwing, so a user editing the profile sees all the problems
/// at once.
/// </summary>
public static partial class VerificationRules
{
    /// <summary>The longest timeout a step may have.</summary>
    public static TimeSpan MaxTimeout { get; } = TimeSpan.FromHours(24);

    /// <summary>The kinds of evidence the output of a step can be captured as.</summary>
    public static IReadOnlyList<EvidenceKind> AllowedKinds { get; } = [EvidenceKind.BuildLog, EvidenceKind.TestRun, EvidenceKind.CommandOutput];

    // Plans name steps by id, so ids follow the shape of task ids. \A ... \z: `$` would accept a trailing newline.
    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex StepIdPattern();

    /// <summary>The problems of a whole profile: its name, its steps and their ids. Empty means valid.</summary>
    public static IReadOnlyList<string> Check(VerificationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            problems.Add("The profile has no name.");
        }

        if (profile.Steps is null || profile.Steps.Count == 0)
        {
            problems.Add("The profile has no steps.");
            return problems;
        }

        problems.AddRange(CheckSteps(profile.Steps));
        return problems;
    }

    /// <summary>
    /// The problems of a list of steps: each step, ids used more than once (ignoring case), and the requirements of each
    /// step (<see cref="VerificationStep.Requires"/>), which must come before it in the list and have its isolation. For a
    /// profile the list is the profile, so a step only requires the steps above it (there can be no cycle); for the runner
    /// it is what is about to run, so no step runs before what it requires (<see cref="VerificationProfile.StepsToRun(IEnumerable{string})"/>
    /// builds such a list).
    /// </summary>
    public static IReadOnlyList<string> CheckSteps(IReadOnlyList<VerificationStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var problems = new List<string>();
        foreach (var step in steps)
        {
            problems.AddRange(Check(step));
        }

        problems.AddRange(steps
            .Where(s => s is not null && !string.IsNullOrEmpty(s.Id))
            .GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"Step id '{g.Key}' is used more than once."));
        problems.AddRange(CheckRequirements(steps));
        return problems;
    }

    /// <summary>
    /// Each step a step requires must be in the list, above it, with the same isolation: isolated steps share the work area
    /// of the run and steps without isolation the working directory, so a build in one is never seen by a test in the other.
    /// </summary>
    private static IEnumerable<string> CheckRequirements(IReadOnlyList<VerificationStep> steps)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i] is not { Requires: { Count: > 0 } requires } step)
            {
                continue;
            }

            foreach (var id in requires.Where(r => !string.IsNullOrEmpty(r) && !string.Equals(r, step.Id, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal))
            {
                var at = IndexOf(steps, id);
                if (at < 0)
                {
                    yield return $"Step '{step.Id}' requires '{id}', which is not one of the steps.";
                }
                else if (at > i)
                {
                    yield return $"Step '{step.Id}' requires '{id}', which comes after it: a step only requires the steps before it, which run first.";
                }
                else if (steps[at].Isolation != step.Isolation)
                {
                    yield return $"Step '{step.Id}' requires '{id}', which runs with another isolation: an isolated step and a step without isolation run in different folders, so what '{id}' leaves is never where '{step.Id}' runs.";
                }
            }
        }
    }

    private static int IndexOf(IReadOnlyList<VerificationStep> steps, string id)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i] is not null && string.Equals(steps[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The problems of one step. Empty means valid.</summary>
    public static IReadOnlyList<string> Check(VerificationStep step)
    {
        if (step is null)
        {
            return ["A step is missing."];
        }

        var problems = new List<string>();
        var id = step.Id ?? string.Empty;
        if (!StepIdPattern().IsMatch(id))
        {
            problems.Add($"Step id '{id}' must start with a letter and use only letters, digits, '_' and '-' (32 characters at most).");
        }

        if (string.IsNullOrWhiteSpace(step.Title))
        {
            problems.Add($"Step '{id}' has no title.");
        }
        else if (HasControlCharacter(step.Title) || HasHiddenInTitle(step.Title))
        {
            problems.Add($"Step '{id}': the title contains a control character (it is written in the header of the evidence).");
        }

        if (string.IsNullOrWhiteSpace(step.Executable))
        {
            problems.Add($"Step '{id}' has no executable.");
        }
        else if (HasControlCharacter(step.Executable))
        {
            problems.Add($"Step '{id}': the executable contains a control character.");
        }

        if (step.Arguments is null)
        {
            problems.Add($"Step '{id}' has no argument list.");
        }
        else
        {
            for (var i = 0; i < step.Arguments.Count; i++)
            {
                if (step.Arguments[i] is null)
                {
                    problems.Add($"Step '{id}': argument {i} is missing.");
                }
                else if (HasControlCharacter(step.Arguments[i]))
                {
                    problems.Add($"Step '{id}': argument {i} contains a control character (free text never goes on a command line).");
                }
            }
        }

        if (!AllowedKinds.Contains(step.Kind))
        {
            problems.Add($"Step '{id}' captures its output as {step.Kind}; only BuildLog, TestRun and CommandOutput are allowed.");
        }

        problems.AddRange(CheckReport(step, id));

        if (step.Timeout <= TimeSpan.Zero || step.Timeout > MaxTimeout)
        {
            problems.Add($"Step '{id}' must have a timeout greater than zero and of 24 hours at most.");
        }

        // An isolation nobody knows is never read as "none".
        if (!Enum.IsDefined(step.Isolation))
        {
            problems.Add($"Step '{id}' has an unknown isolation; it is either LowIntegrity (the default) or None.");
        }

        problems.AddRange(CheckRequires(step, id));
        return problems;
    }

    /// <summary>What one step can tell of its requirements alone: no entry missing, none twice, and never itself.</summary>
    private static IEnumerable<string> CheckRequires(VerificationStep step, string id)
    {
        if (step.Requires is not { Count: > 0 } requires)
        {
            yield break;
        }

        for (var i = 0; i < requires.Count; i++)
        {
            if (string.IsNullOrEmpty(requires[i]))
            {
                yield return $"Step '{id}': required step {i} is missing.";
            }
            else if (string.Equals(requires[i], id, StringComparison.Ordinal))
            {
                yield return $"Step '{id}' requires itself.";
            }
        }

        foreach (var repeated in requires.Where(r => !string.IsNullOrEmpty(r)).GroupBy(r => r, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            yield return $"Step '{id}' requires '{repeated.Key}' more than once.";
        }
    }

    private static IEnumerable<string> CheckReport(VerificationStep step, string id)
    {
        if (!Enum.IsDefined(step.Report))
        {
            yield return $"Step '{id}' has an unknown report format.";
            yield break;
        }

        if (step.Kind == EvidenceKind.TestRun && step.Report == TestReportFormat.None)
        {
            yield return $"Step '{id}' is a test run but reads no report: tests are only judged by a TRX or JUnit report.";
        }

        if (step.Report != TestReportFormat.None && step.Kind != EvidenceKind.TestRun)
        {
            yield return $"Step '{id}' reads a {step.Report} report, so its kind must be TestRun.";
        }

        if (step.Report != TestReportFormat.None && string.IsNullOrWhiteSpace(step.ReportPath))
        {
            yield return $"Step '{id}' reads a {step.Report} report but has no report path.";
        }

        if (step.Report == TestReportFormat.None && step.ReportPath is not null)
        {
            yield return $"Step '{id}' has a report path but reads no report.";
        }

        if (step.ReportPath is not null && HasControlCharacter(step.ReportPath))
        {
            yield return $"Step '{id}': the report path contains a control character.";
        }
    }

    private static bool HasControlCharacter(string text) => text.AsSpan().IndexOfAnyInRange('\0', '\u001f') >= 0;

    /// <summary>
    /// A hidden character in a title (it is written on one line of the header of the evidence): the controls, the line and paragraph separators and the marks of direction are still
    /// refused, because they would end the line or reorder what the person reads. The zero-width joiner and non-joiner and the soft hyphen are admitted: they are in real titles (emoji made
    /// of several, words of several scripts) and none of them ends a line, reorders text or draws anything, so they cannot forge a header.
    /// </summary>
    private static bool HasHiddenInTitle(string title)
    {
        foreach (var rune in title.EnumerateRunes())
        {
            if (AgentStudio.Domain.Common.UntrustedText.IsHidden(rune) && rune.Value is not (0x200D or 0x200C or 0x00AD))
            {
                return true;
            }
        }

        return false;
    }
}
