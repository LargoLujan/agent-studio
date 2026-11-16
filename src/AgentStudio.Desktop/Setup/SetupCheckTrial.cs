using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Desktop.Setup;

/// <summary>How one command of the check went in «Probarla ahora».</summary>
/// <param name="StepId">The id of the step of the profile.</param>
/// <param name="Tests">The counters of its report, for a step that runs tests; null otherwise or when it could not be read.</param>
/// <param name="FailedTests">The tests its report names as failed (the first ones, bounded by the verifier). Untrusted: shown as text.</param>
public sealed record SetupCheckTrialStep(string StepId, bool Passed, bool TimedOut, int ExitCode, TestRunSummary? Tests, IReadOnlyList<FailedTest> FailedTests);

/// <summary>What «Probarla ahora» found.</summary>
/// <param name="Steps">One entry per command that ran, in order; the verifier stops at the first one that fails.</param>
/// <param name="NotIsolated">The commands the profile runs without isolation: they are never run from here.</param>
/// <param name="NotReached">The isolated commands that did not run because one before them failed.</param>
/// <param name="Output">The last lines the commands wrote, cleaned of hidden characters and cut; untrusted, shown as text.</param>
public sealed record SetupCheckTrialResult(
    IReadOnlyList<SetupCheckTrialStep> Steps,
    IReadOnlyList<string> NotIsolated,
    IReadOnlyList<string> NotReached,
    IReadOnlyList<string> Output)
{
    /// <summary>Every command that runs from here ran and ended well.</summary>
    public bool Passed => Steps.Count > 0 && NotReached.Count == 0 && Steps.All(s => s.Passed);
}

/// <summary>
/// «Probarla ahora» of the check step of the configurator (docs/24 §3.4, wave 1): the commands the person is editing, before they are
/// saved, run once with the verifier of the runs and with its confinement, exactly as the tests of HEAD run the saved profile
/// (<c>HeadTestsService</c>): only the isolated steps (low integrity on a copy of the last commit taken from the objects of the
/// repository, ADR-0017), with the project held as an audit holds it, within the sum of the timeouts of the steps plus a minute,
/// and without quota. A step the profile runs without isolation is never run from here, so nothing runs with the rights of the
/// person and their working tree is never written.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>What the isolation does not do.</b> Low integrity only stops writes: the code the commands build and run can read
/// whatever the person can read and can use the network (<c>ProcessContracts</c>, ADR-0017). The screen says so in those words, and
/// the configurator asks the person before running commands that are not the profile they already accepted.</item>
/// <item>The commands are the person's own, typed on this screen, and they have not been confirmed with Windows Hello: that
/// confirmation guards <b>saving</b> them, because a saved profile runs by itself in every run. Here one click runs them once,
/// confined, and nothing is saved.</item>
/// <item>The verifier stores what it captures as the system's evidence bound to the commit, as it does for the tests of HEAD; nothing
/// is announced in the decision log, so no approval can rest on it: it belongs to no run.</item>
/// <item>The output is untrusted: the last <see cref="MaxOutputLines"/> lines, each cleaned of hidden characters and cut.</item>
/// </list>
/// </remarks>
public sealed class SetupCheckTrial
{
    /// <summary>The lines of output kept for the screen.</summary>
    public const int MaxOutputLines = 20;

    /// <summary>The longest line of output kept, in characters.</summary>
    public const int MaxOutputLineLength = 240;

    /// <summary>What is added to the sum of the timeouts of the steps: the copy of the commit and the reading of the reports.</summary>
    public static readonly TimeSpan BudgetSlack = TimeSpan.FromMinutes(1);

    private readonly IVerificationRunner _runner;
    private readonly IWorktreeManager _git;
    private readonly IProjectActivity _activity;
    private readonly TimeProvider _time;

    public SetupCheckTrial(IVerificationRunner runner, IWorktreeManager git, IProjectActivity activity, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(activity);
        _runner = runner;
        _git = git;
        _activity = activity;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The trial when the composition has everything it needs; null otherwise, and the screen does not offer it.</summary>
    public static SetupCheckTrial? Create(IVerificationRunner? runner, IWorktreeManager? git, IProjectActivity? activity, TimeProvider? time = null) =>
        runner is null || git is null || activity is null ? null : new SetupCheckTrial(runner, git, activity, time);

    /// <summary>Runs the isolated steps of <paramref name="profile"/> once on a copy of the last commit of <paramref name="project"/>.</summary>
    /// <exception cref="UserTextException">It could not run, with the code of why (<c>setup.check.*</c>): nothing ran.</exception>
    /// <exception cref="OperationCanceledException">The person stopped it.</exception>
    public async Task<SetupCheckTrialResult> RunAsync(ProjectContext project, VerificationProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(profile);
        if (VerificationRules.Check(profile).Count > 0)
        {
            throw Refused("setup.check.invalid", "The commands break a rule of the verification profile.");
        }

        // Only what runs confined. A step without isolation would run in the working tree with the rights of the person.
        var steps = profile.Steps.Where(s => s.Isolation == ProcessIsolation.LowIntegrity).ToList();
        var notIsolated = profile.Steps.Where(s => s.Isolation != ProcessIsolation.LowIntegrity).Select(s => s.Id).ToList();
        if (steps.Count == 0)
        {
            throw Refused("setup.check.no-isolated-step", "No command of the profile is isolated: nothing runs with the rights of the person.");
        }

        IDisposable? lease;
        try
        {
            lease = await ProjectActivityEntry.EnterAsync(_activity, project.ProjectId, ProjectActivityKind.Audit, _time, ct).ConfigureAwait(false);
        }
        catch (UserTextException busy) when (busy.Text.Code == ProjectActivityEntry.BusyRemoteCode)
        {
            throw Refused("setup.check.busy", busy.Message);
        }

        using (lease ?? throw Refused("setup.check.busy", $"Project '{project.ProjectId}' is busy with {_activity.Current(project.ProjectId)}."))
        {
            var head = await HeadAsync(project, ct).ConfigureAwait(false);
            var budget = steps.Aggregate(BudgetSlack, (total, step) => total + step.Timeout);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(budget);
            var output = new LastLines(MaxOutputLines, MaxOutputLineLength);
            IReadOnlyList<VerificationResult> results;
            try
            {
                results = await _runner.RunAsync(project.RootPath, steps, new EvidenceScope(project.ProjectId, null, null, null, head, project.RootPath), output, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw Refused("setup.check.too-long", $"The trial passed its time budget ({budget}) and was stopped.", (int)Math.Ceiling(budget.TotalMinutes));
            }
            catch (DirectoryNotFoundException ex)
            {
                throw Refused("setup.check.not-run", ex.Message);
            }

            return new SetupCheckTrialResult(
                [.. results.Select(r => new SetupCheckTrialStep(r.StepId, r.Succeeded, r.TimedOut, r.ExitCode, r.Evidence.TestRun, r.FailedTests ?? []))],
                notIsolated,
                [.. steps.Skip(results.Count).Select(s => s.Id)],
                output.Lines());
        }
    }

    private async Task<string> HeadAsync(ProjectContext project, CancellationToken ct)
    {
        string head;
        try
        {
            head = (await _git.InspectAsync(project.RootPath, ct).ConfigureAwait(false)).HeadCommit;
        }
#pragma warning disable CA1031 // No git, no repository or no commit yet: the trial says it cannot run, with nothing run.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            throw Refused("setup.check.no-version", ex.Message);
        }

        return string.IsNullOrWhiteSpace(head) ? throw Refused("setup.check.no-version", "The repository has no commit.") : head;
    }

    private static UserTextException Refused(string code, string message, params object[] arguments) => new(UserText.Of(code, arguments), message);

    /// <summary>The last lines the verifier reports, kept from any thread; nothing is posted to the screen while the commands run.</summary>
    private sealed class LastLines(int max, int maxLength) : IProgress<ProcessOutputLine>
    {
        private readonly Queue<string> _lines = new();
        private readonly System.Threading.Lock _gate = new();

        public void Report(ProcessOutputLine value)
        {
            var line = UntrustedText.Clean(value.Text ?? string.Empty).TrimEnd();
            if (line.Length > maxLength)
            {
                line = line[..maxLength] + "…";
            }

            lock (_gate)
            {
                _lines.Enqueue(line);
                while (_lines.Count > max)
                {
                    _lines.Dequeue();
                }
            }
        }

        public IReadOnlyList<string> Lines()
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }
}
