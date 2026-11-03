using System.Globalization;
using AgentStudio.Application.Execution;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using HashFunctions = AgentStudio.Domain.Common.ContentHash;

namespace AgentStudio.Application.Verification;

// Phase 3 contracts (docs/13 section 3.4).

/// <summary>How the machine-readable test report of a step is read. Never the console output of the test runner.</summary>
public enum TestReportFormat
{
    None,
    Trx,
    JUnit,
}

/// <summary>
/// One step of the trusted verification profile (build, test...). The profile is frozen when a run starts and is never
/// read from a worktree, so neither a plan nor an agent can change what is run.
/// </summary>
/// <remarks>
/// For the test project of this repository (xUnit v3 on Microsoft.Testing.Platform, spike S4 in
/// <c>docs/investigacion/2026-10-15-spikes-fase-3.md</c>) a test step is <c>dotnet</c> with the arguments
/// <c>test --no-build --results-directory {folder} -- --report-xunit-trx --report-xunit-trx-filename {name}.trx
/// --zero-tests-policy strict</c>, <see cref="Report"/> = <see cref="TestReportFormat.Trx"/> and
/// <see cref="ReportPath"/> = <c>{folder}\{name}.trx</c>. For JUnit use <c>--report-xunit-junit</c> with
/// <c>--report-xunit-junit-filename {name}.xml</c>. <c>--results-directory</c> goes before <c>--</c>; each
/// <c>-filename</c> option needs its report option, or the run ends with code 5 without running a test; and
/// <c>--report-trx</c> belongs to a package this repository does not use. The exit code alone proves nothing (a run in
/// which every test is skipped exits with 0), which is why the report is read and why a run needs at least one passed
/// test to count as a success. A project on another framework needs other flags: the profile is written by the user.
/// </remarks>
/// <param name="Id">The name plans use to refer to the step (<c>build</c>, <c>test</c>).</param>
/// <param name="Kind">The kind of evidence its output is captured as.</param>
/// <param name="Report">The format of the machine-readable test report the step writes, or None.</param>
/// <param name="ReportPath">Where the report is written when <paramref name="Report"/> is not None.</param>
/// <param name="Isolation">
/// How the step is kept from writing the user's files (ADR-0017). By default it runs at Low integrity on a copy of the
/// commit it verifies, never in the working directory. Only the profile can turn that off, step by step, with
/// <see cref="ProcessIsolation.None"/>; the step then runs as before, with the rights of the user, and its evidence says so.
/// </param>
/// <param name="Requires">
/// The ids of the steps of the same profile that must run, and pass, before this one in the same verification: a
/// <c>dotnet test --no-build</c> needs the <c>dotnet build</c> of the same copy, because an isolated verification starts
/// from a fresh copy of the commit where nothing was built (ADR-0017). Whenever the step runs, the engine runs them first,
/// even when a task names only this step (<see cref="VerificationProfile.StepsToRun(IEnumerable{string})"/>). Null or
/// empty: it requires nothing, as every step did before the field existed. A step only requires steps above it in the
/// profile and with its own isolation (<c>VerificationRules</c>), so they share its working directory and no cycle exists.
/// </param>
public sealed record VerificationStep(
    string Id,
    string Title,
    string Executable,
    IReadOnlyList<string> Arguments,
    EvidenceKind Kind,
    TestReportFormat Report,
    string? ReportPath,
    TimeSpan Timeout,
    ProcessIsolation Isolation = ProcessIsolation.LowIntegrity,
    IReadOnlyList<string>? Requires = null);

/// <summary>The verification steps of a project. <see cref="ContentHash"/> identifies the exact commands that will run.</summary>
public sealed record VerificationProfile(string Name, IReadOnlyList<VerificationStep> Steps)
{
    /// <summary>
    /// SHA-256 of the canonical content. Independent of the order of the steps, which plans refer to by id, unless a step
    /// requires others: the order of the profile is then the order the required steps run in, and the hash covers it.
    /// </summary>
    public string ContentHash => Hash(this);

    public VerificationStep? Find(string id) => Steps.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// The steps that run when a task names <paramref name="stepIds"/> (its verification steps, or the step of a tool run),
    /// in the order they run: each named step in the order given, preceded by the steps it requires
    /// (<see cref="VerificationStep.Requires"/>, and the ones those require) that are not there yet, in the order of the
    /// profile; no step twice. Without requirements these are just the named steps, in their order, as they always ran.
    /// </summary>
    /// <exception cref="InvalidOperationException">A named step, or one it requires, is not in the profile.</exception>
    public IReadOnlyList<VerificationStep> StepsToRun(IEnumerable<string> stepIds)
    {
        var steps = StepsToRun(stepIds, out var missing);
        return missing.Count == 0
            ? steps
            : throw new InvalidOperationException($"The verification profile '{Name}' has no step {string.Join(", ", missing.Select(m => $"'{m}'"))}, which the steps to run name or require.");
    }

    /// <summary>
    /// As <see cref="StepsToRun(IEnumerable{string})"/>, but the ids the profile does not have, named or required, are left
    /// out of the steps and given in <paramref name="missing"/>, in the order they were found, so a caller that fails
    /// closed on them (the warning about writers at medium integrity) can name them.
    /// </summary>
    public IReadOnlyList<VerificationStep> StepsToRun(IEnumerable<string> stepIds, out IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(stepIds);
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i]?.Id is { } id)
            {
                position.TryAdd(id, i);
            }
        }

        var absent = new List<string>();
        var result = new List<VerificationStep>();
        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in stepIds)
        {
            if (id is null || !position.TryGetValue(id, out var at))
            {
                Missing(absent, id);
                continue;
            }

            foreach (var required in RequiredBy(at, position, absent).Order())
            {
                if (added.Add(Steps[required].Id))
                {
                    result.Add(Steps[required]);
                }
            }

            if (added.Add(Steps[at].Id))
            {
                result.Add(Steps[at]);
            }
        }

        missing = absent;
        return result;
    }

    /// <summary>The positions of the steps the step at <paramref name="at"/> requires, directly or through others; never its own.</summary>
    private HashSet<int> RequiredBy(int at, Dictionary<string, int> position, List<string> absent)
    {
        var found = new HashSet<int>();
        var pending = new Stack<string>(Steps[at].Requires ?? []);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (id is null || !position.TryGetValue(id, out var required))
            {
                Missing(absent, id);
                continue;
            }

            if (required != at && found.Add(required))
            {
                foreach (var next in Steps[required].Requires ?? [])
                {
                    pending.Push(next);
                }
            }
        }

        return found;
    }

    private static void Missing(List<string> absent, string? id)
    {
        var shown = id ?? "(null)";
        if (!absent.Contains(shown, StringComparer.Ordinal))
        {
            absent.Add(shown);
        }
    }

    private static string Hash(VerificationProfile profile)
    {
        var fields = new List<string?> { "verification-profile/1", profile.Name, profile.Steps.Count.ToString(CultureInfo.InvariantCulture) };
        foreach (var step in profile.Steps.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            fields.Add(step.Id);
            fields.Add(step.Title);
            fields.Add(step.Executable);
            fields.Add(step.Arguments.Count.ToString(CultureInfo.InvariantCulture));
            fields.AddRange(step.Arguments);
            fields.Add(step.Kind.ToString());
            fields.Add(step.Report.ToString());
            fields.Add(step.ReportPath);
            fields.Add(step.Timeout.Ticks.ToString(CultureInfo.InvariantCulture));

            // Only a step that is not isolated adds a field, so the hash of a profile that never turns isolation off is
            // the one it had before isolation existed. Its form can never be a step id, so steps cannot shift into it.
            if (step.Isolation != ProcessIsolation.LowIntegrity)
            {
                fields.Add("isolation=" + step.Isolation);
            }

            // The same for a step that requires others (profile file version 2): a profile without requirements keeps the
            // hash it had before they existed. Their order does not change what runs, so they go sorted; ids hold no ','.
            if (step.Requires is { Count: > 0 } requires)
            {
                fields.Add("requires=" + string.Join(',', requires.Order(StringComparer.Ordinal)));
            }
        }

        // With requirements the order of the steps decides the order the required ones run in (StepsToRun), so it is
        // part of what runs: one field more, after every step, in a form no step id can take.
        if (profile.Steps.Any(s => s.Requires is { Count: > 0 }))
        {
            fields.Add("order=" + string.Join(',', profile.Steps.Select(s => s.Id)));
        }

        return HashFunctions.Sha256Hex(HashFunctions.Canonical([.. fields]));
    }
}

/// <summary>Where a piece of evidence comes from inside a run.</summary>
/// <param name="SubjectCommit">
/// The commit checked out in the working directory, which the steps verify (a full git object id). Every evidence the
/// runner captures is bound to it (<c>EvidenceRecord.SubjectCommit</c>), so a test run can only support the approval of
/// that exact commit. Null when the caller does not know it: such evidence verifies no commit.
/// </param>
/// <param name="RepositoryRoot">
/// The root of the project's repository, whose objects hold <paramref name="SubjectCommit"/>. An isolated step runs on a
/// copy of the commit that the git layer takes from there (ADR-0017), never through the <c>.git</c> of the working
/// directory, which an agent can rewrite. Without it, or without the commit, an isolated step is not run.
/// </param>
public sealed record EvidenceScope(string ProjectId, RunId? Run, TaskId? Task, int? Attempt, string? SubjectCommit = null, string? RepositoryRoot = null);

/// <summary>The outcome of one verification step. <see cref="Evidence"/> is what the runner captured, with its own hash.</summary>
public sealed record VerificationResult(string StepId, EvidenceRecord Evidence, bool Succeeded, bool TimedOut, int ExitCode);

/// <summary>
/// Runs verification steps in a working directory and captures their output as evidence. A step that runs no tests
/// does not succeed (<c>TestRunSummary.Succeeded</c> needs at least one passed test); a timeout kills the process tree.
/// </summary>
public interface IVerificationRunner
{
    Task<IReadOnlyList<VerificationResult>> RunAsync(
        string workingDirectory,
        IReadOnlyList<VerificationStep> steps,
        EvidenceScope scope,
        IProgress<ProcessOutputLine>? output,
        CancellationToken cancellationToken);
}
