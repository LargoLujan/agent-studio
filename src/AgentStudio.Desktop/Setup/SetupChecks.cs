using System.Text;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Evidence;
using AgentStudio.Orchestration.Verification;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Setup;

/// <summary>
/// A command of the check as the one line the person edits, written as the process receives it: the program, then its arguments,
/// separated by spaces, with the rules of the Windows command line that <c>ProcessStartInfo.ArgumentList</c> and the isolated launch
/// (<c>WindowsCommandLine</c>) apply. A part that is empty or has a space or a double quote goes between double quotes; inside them a
/// double quote is <c>\"</c>, and the backslashes just before a quote are doubled. So <c>a" "b</c>, one argument, shows as
/// <c>"a\" \"b"</c> and never as two. Nothing here goes through a shell: the parts become the executable and the arguments of the step,
/// each passed on its own.
/// </summary>
public static class SetupCommandLine
{
    private const char Quote = '"';
    private const char Backslash = '\\';

    /// <summary>The line of a program and its arguments, each part written as the command line of the process carries it.</summary>
    public static string Render(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        var line = new StringBuilder();
        foreach (var part in new[] { executable }.Concat(arguments))
        {
            if (line.Length > 0)
            {
                line.Append(' ');
            }

            Append(line, part);
        }

        return line.ToString();
    }

    /// <summary>
    /// The parts of <paramref name="line"/>, the program first, read as the C runtime reads a command line: 2N backslashes before a
    /// double quote are N backslashes and the quote opens or closes a quoted part, 2N+1 are N backslashes and a literal quote, and
    /// backslashes before anything else are themselves. Null when a double quote is not closed.
    /// </summary>
    public static IReadOnlyList<string>? Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;
        var index = 0;
        while (index < line.Length)
        {
            var character = line[index];
            if (character == Backslash)
            {
                var backslashes = 0;
                while (index < line.Length && line[index] == Backslash)
                {
                    backslashes++;
                    index++;
                }

                if (index < line.Length && line[index] == Quote)
                {
                    current.Append(Backslash, backslashes / 2);
                    if (backslashes % 2 == 1)
                    {
                        current.Append(Quote);
                    }
                    else
                    {
                        quoted = !quoted;
                    }

                    index++;
                }
                else
                {
                    current.Append(Backslash, backslashes);
                }

                started = true;
                continue;
            }

            if (character == Quote)
            {
                quoted = !quoted;
                started = true;
            }
            else if (!quoted && char.IsWhiteSpace(character))
            {
                if (started)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(character);
                started = true;
            }

            index++;
        }

        if (quoted)
        {
            return null;
        }

        if (started)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    // The algorithm of System.PasteArguments.AppendArgument (dotnet/runtime), the one WindowsCommandLine follows.
    private static void Append(StringBuilder line, string part)
    {
        if (part.Length != 0 && !part.Any(c => char.IsWhiteSpace(c) || c == Quote))
        {
            line.Append(part);
            return;
        }

        line.Append(Quote);
        var index = 0;
        while (index < part.Length)
        {
            var character = part[index++];
            if (character == Backslash)
            {
                var backslashes = 1;
                while (index < part.Length && part[index] == Backslash)
                {
                    index++;
                    backslashes++;
                }

                if (index == part.Length)
                {
                    line.Append(Backslash, backslashes * 2);
                }
                else if (part[index] == Quote)
                {
                    line.Append(Backslash, (backslashes * 2) + 1).Append(Quote);
                    index++;
                }
                else
                {
                    line.Append(Backslash, backslashes);
                }

                continue;
            }

            if (character == Quote)
            {
                line.Append(Backslash).Append(Quote);
                continue;
            }

            line.Append(character);
        }

        line.Append(Quote);
    }
}

/// <summary>
/// One command of the check (step 5) as the person edits it: what it is for, the line of the command and why it cannot be used.
/// The rest of the step (its id, the kind of what it captures, its report, its time and what it requires) stays as the proposal
/// has it; an empty line leaves the command out.
/// </summary>
public sealed partial class SetupCheckRowViewModel : ObservableObject
{
    private readonly IUiTextProvider _text;
    private readonly string _original;

    /// <param name="step">The step the row edits; a blank one (no executable) for a project whose technology was not recognised.</param>
    internal SetupCheckRowViewModel(VerificationStep step, IUiTextProvider text)
    {
        Step = step;
        _text = text;
        Label = SetupChecks.Label(text, step);
        _original = string.IsNullOrWhiteSpace(step.Executable) ? string.Empty : SetupCommandLine.Render(step.Executable, step.Arguments);
        Example = _original.Length == 0 && text.Has("Setup_Verification_Example_" + step.Id) ? text.Get("Setup_Verification_Example_" + step.Id) : string.Empty;
        _commandText = _original;
    }

    /// <summary>Raised when the person changes the line.</summary>
    public event EventHandler? Changed;

    /// <summary>The step the row edits.</summary>
    internal VerificationStep Step { get; }

    /// <summary>What the command is for, in words: «Compilar», «Lanzar las pruebas».</summary>
    public string Label { get; }

    /// <summary>An example for a command nothing was proposed for, or empty.</summary>
    public string Example { get; }

    /// <summary>The program and its arguments, as one line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(IsEmpty))]
    private string _commandText;

    /// <summary>The line is empty: the command is left out.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(CommandText);

    /// <summary>The person changed the line from the command the row was read with.</summary>
    public bool IsChanged => !string.Equals(CommandText, _original, StringComparison.Ordinal);

    /// <summary>Why the line cannot be used, or empty.</summary>
    public string Problem
    {
        get
        {
            if (IsEmpty)
            {
                return string.Empty;
            }

            if (SetupCommandLine.Parse(CommandText) is not { } parts)
            {
                return _text.Get("Setup_Verification_Quotes");
            }

            if (parts.Count == 0 || string.IsNullOrWhiteSpace(parts[0]))
            {
                return _text.Get("Setup_Verification_No_Program");
            }

            // No command line can name a program with a double quote in it: the launch would refuse it.
            if (parts[0].Contains('"', StringComparison.Ordinal))
            {
                return _text.Get("Setup_Verification_Quote_In_Program");
            }

            // A mark of direction or a zero-width character would make the line show one command and run another.
            if (parts.Any(AgentStudio.Domain.Common.UntrustedText.HasHidden))
            {
                return _text.Get("Setup_Verification_Hidden");
            }

            // A report is only read from the fresh folder of the step: a command that does not write it there can never pass.
            if (Step.ReportPath is { } report && report.Contains(VerificationRunner.ResultsFolderToken, StringComparison.Ordinal)
                && !parts.Skip(1).Any(p => p.Contains(VerificationRunner.ResultsFolderToken, StringComparison.Ordinal)))
            {
                return _text.Get("Setup_Verification_Report");
            }

            return VerificationRules.Check(Build(parts)).Count > 0 ? _text.Get("Setup_Verification_Invalid") : string.Empty;
        }
    }

    /// <summary>The step with the command of the line; the same step when the line was not touched. Null when the line is empty or cannot be used.</summary>
    internal VerificationStep? ToStep()
    {
        if (IsEmpty || Problem.Length > 0)
        {
            return null;
        }

        return string.Equals(CommandText, _original, StringComparison.Ordinal) ? Step : Build(SetupCommandLine.Parse(CommandText)!);
    }

    private VerificationStep Build(IReadOnlyList<string> parts) => Step with { Executable = parts[0], Arguments = [.. parts.Skip(1)] };

    partial void OnCommandTextChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>What a screen reader says of the row: what the command is for.</summary>
    public override string ToString() => Label;
}

/// <summary>The commands of the check (step 5): the profile they make, from the rows the person edits.</summary>
public static class SetupChecks
{
    /// <summary>The name of a profile written for a project whose technology was not recognised.</summary>
    public const string OwnProfileName = "custom";

    /// <summary>What a command is for, in words: «Compilar», «Lanzar las pruebas»; the title of the profile for an id the screen does not know.</summary>
    public static string Label(IUiTextProvider text, VerificationStep step)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(step);
        return text.Has("Setup_Verification_Step_" + step.Id) ? text.Get("Setup_Verification_Step_" + step.Id) : step.Title;
    }

    /// <summary>
    /// The blank commands for a project whose technology was not recognised: one that compiles and one that runs the tests and
    /// leaves a JUnit report in the fresh folder of the step, which is how the system reads tests (never from their console).
    /// </summary>
    public static IReadOnlyList<VerificationStep> Blank(string buildTitle, string testTitle) =>
    [
        new("build", buildTitle, string.Empty, [], EvidenceKind.BuildLog, TestReportFormat.None, null, TimeSpan.FromMinutes(10)),
        new("test", testTitle, string.Empty, [], EvidenceKind.TestRun, TestReportFormat.JUnit, VerificationRunner.ResultsFolderToken + "/test.xml", TimeSpan.FromMinutes(20), Requires: ["build"]),
    ];

    /// <summary>
    /// The profile the rows make: each command that is not empty, in order. A command that required one left out requires what
    /// that one required, so nothing runs before what it needs. Null when every line is empty. Once the person changes a line the
    /// commands are theirs and the profile is named <see cref="OwnProfileName"/>: it no longer is the one of a stack, and Windows
    /// Hello does not say it is.
    /// </summary>
    /// <param name="problem">Why it cannot be used, from the first row that cannot or from the rules of a profile; null when it can.</param>
    public static VerificationProfile? Profile(string name, IReadOnlyList<SetupCheckRowViewModel> rows, IUiTextProvider text, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        problem = null;
        var kept = new List<VerificationStep>();
        var dropped = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.IsEmpty)
            {
                dropped[row.Step.Id] = row.Step.Requires ?? [];
                continue;
            }

            if (row.ToStep() is not { } step)
            {
                problem = row.Problem;
                return null;
            }

            kept.Add(step);
        }

        if (kept.Count == 0)
        {
            return null;
        }

        var ids = kept.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var steps = kept.Select(s => s.Requires is { Count: > 0 } requires && requires.Any(dropped.ContainsKey)
            ? s with { Requires = Resolve(requires, dropped, ids) }
            : s).ToList();
        var profile = new VerificationProfile(rows.Any(r => r.IsChanged) ? OwnProfileName : name, steps);
        if (VerificationRules.Check(profile).Count > 0)
        {
            problem = text.Get("Setup_Verification_Invalid");
            return null;
        }

        return profile;
    }

    private static IReadOnlyList<string>? Resolve(IReadOnlyList<string> requires, Dictionary<string, IReadOnlyList<string>> dropped, HashSet<string> kept)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(requires);
        while (pending.Count > 0)
        {
            var id = pending.Dequeue();
            if (!seen.Add(id))
            {
                continue;
            }

            if (dropped.TryGetValue(id, out var instead))
            {
                foreach (var next in instead)
                {
                    pending.Enqueue(next);
                }
            }
            else if (kept.Contains(id))
            {
                result.Add(id);
            }
        }

        return result.Count > 0 ? result : null;
    }
}
