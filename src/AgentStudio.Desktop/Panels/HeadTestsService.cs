using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Session;

namespace AgentStudio.Desktop.Panels;

/// <summary>One test that failed in the tests of HEAD: its step, its name and what its report says, as the report gives them (cut).</summary>
/// <param name="Step">The step of the profile whose report names it.</param>
/// <param name="Name">The name of the test.</param>
/// <param name="Message">The first line of what the report says went wrong; null when it says nothing. Untrusted: shown as text.</param>
public sealed record FailedTestRow(string Step, string Name, string? Message)
{
    /// <summary>What a screen reader says of a failed test: its name, its step and its message.</summary>
    public string AccessibleName => string.Join(" · ", new[] { Name, Step, Message }.Where(p => !string.IsNullOrWhiteSpace(p)));

    public override string ToString() => AccessibleName;
}

/// <summary>
/// Why the tests of HEAD were not run, said in the language of the person: the key of the text of the panel and its arguments. The
/// message of the exception stays in English, for the log.
/// </summary>
public sealed class HeadTestsRefusedException : InvalidOperationException
{
    public HeadTestsRefusedException(string textKey, string message, params object[] arguments)
        : base(message)
    {
        TextKey = textKey;
        Arguments = arguments;
    }

    /// <summary>The key of the text in the resources of the panels.</summary>
    public string TextKey { get; }

    public IReadOnlyList<object> Arguments { get; }
}

/// <summary>
/// What the shell needs to compose the tests of HEAD (docs/17 D-45): the isolated verifier, the profile of the project, the git
/// layer, the workspace and the activity of the process. Any of them missing and there is no service: the button is not shown.
/// </summary>
public sealed class HeadTestsServices(
    IVerificationRunner? runner,
    IVerificationProfileSource? profiles,
    IWorktreeManager? git,
    IProjectWorkspace? workspace,
    IProjectActivity? activity,
    TimeProvider? time = null)
{
    /// <summary>The service of the project <paramref name="projectRoot"/> names at the moment it runs; null when something is missing here.</summary>
    public IHeadTestsService? For(Func<string?> projectRoot)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);
        return runner is null || profiles is null || git is null || workspace is null || activity is null
            ? null
            : new HeadTestsService(runner, profiles, git, workspace, activity, projectRoot, time: time);
    }
}

/// <summary>
/// «Ejecutar las pruebas de HEAD» (docs/17 D-45, P50): the steps of the verification profile of the project that are isolated, on a
/// copy of the commit HEAD points at, with the verifier of the runs (ADR-0017: copy read from the objects of the repository, low
/// integrity, no network) and without quota. It is what the debates and the audit do on a commit: it asks for no approval, because
/// nothing runs with the rights of the person (a step the profile turns isolation off for is not run, and the result says which),
/// and the working tree of the person is never read or written.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The project is held while the tests run, as an audit holds it (nothing else with agents goes on at the same time), and it is
/// refused, saying what holds it, when something does.</item>
/// <item>The verifier stores what it captures as the system's own evidence («prueba guardada»), bound to the commit; the service does
/// not announce it in the decision log, so no approval can rest on it: it belongs to no run.</item>
/// <item>The time is bounded: the sum of the timeouts of the steps, plus a minute, for the whole of them.</item>
/// <item>The failed tests come from the report of the step (TRX or JUnit), which is untrusted: the verifier reads them bounded.</item>
/// </list>
/// </remarks>
public sealed class HeadTestsService : IHeadTestsService
{
    /// <summary>What is added to the sum of the timeouts of the steps: the copy of the commit and the reading of the reports.</summary>
    public static readonly TimeSpan DefaultBudgetSlack = TimeSpan.FromMinutes(1);

    private readonly TimeSpan _slack;
    private readonly TimeProvider _time;

    private readonly IVerificationRunner _runner;
    private readonly IVerificationProfileSource _profiles;
    private readonly IWorktreeManager _git;
    private readonly IProjectWorkspace _workspace;
    private readonly IProjectActivity _activity;
    private readonly Func<string?> _projectRoot;
    private readonly IUiTextProvider _texts;

    public HeadTestsService(
        IVerificationRunner runner,
        IVerificationProfileSource profiles,
        IWorktreeManager git,
        IProjectWorkspace workspace,
        IProjectActivity activity,
        Func<string?> projectRoot,
        IUiTextProvider? texts = null,
        TimeSpan? budgetSlack = null,
        TimeProvider? time = null)
    {
        _slack = budgetSlack ?? DefaultBudgetSlack;
        _time = time ?? TimeProvider.System;
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(projectRoot);
        _runner = runner;
        _profiles = profiles;
        _git = git;
        _workspace = workspace;
        _activity = activity;
        _projectRoot = projectRoot;
        _texts = texts ?? Texts.Current;
    }

    public async Task<HeadTestsResult> RunAsync(CancellationToken cancellationToken)
    {
        var root = _projectRoot() ?? throw new HeadTestsRefusedException("Panels_Tests_HeadNoProject", "No project is open.");
        var project = await _workspace.OpenAsync(root, cancellationToken).ConfigureAwait(false);

        using var lease = await EnterAsync(project.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new HeadTestsRefusedException(
                "Panels_Tests_HeadBusy",
                $"Project '{project.ProjectId}' is busy ({_activity.Current(project.ProjectId)}): the tests of HEAD share the project with no other activity.",
                BusyWith(_activity.Current(project.ProjectId)));

        var profile = await _profiles.LoadAsync(project, cancellationToken).ConfigureAwait(false)
            ?? throw new HeadTestsRefusedException("Panels_Tests_HeadNoProfile", "The project has no verification profile.");
        var steps = profile.Steps.Where(s => s.Isolation != ProcessIsolation.None).ToList();
        if (steps.Count == 0)
        {
            throw new HeadTestsRefusedException("Panels_Tests_HeadNoIsolatedStep", "The verification profile has no isolated step: nothing runs with the permissions of the person.");
        }

        var notRun = profile.Steps.Where(s => s.Isolation == ProcessIsolation.None).Select(s => s.Id).ToList();
        var head = (await _git.InspectAsync(project.RootPath, cancellationToken).ConfigureAwait(false)).HeadCommit;

        var budget = steps.Aggregate(_slack,(total, step) => total + step.Timeout);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(budget);
        IReadOnlyList<VerificationResult> results;
        try
        {
            results = await _runner.RunAsync(project.RootPath, steps, new EvidenceScope(project.ProjectId, null, null, null, head, project.RootPath), null, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HeadTestsRefusedException(
                "Panels_Tests_HeadTimeBudget",
                $"The tests of HEAD passed their time budget ({budget}) and were stopped.",
                Math.Ceiling(budget.TotalMinutes));
        }

        // The verifier stops at the first step that does not succeed: the steps after it were not run, and the panel says so.
        var stopped = steps.Skip(results.Count).Select(s => s.Id).ToList();
        return Present(results, notRun, stopped, root);
    }

    /// <summary>
    /// Holds the project as an audit does. While what holds it is a remote operation of Agent Studio (docs/21 D-25) it waits, up to the cap of
    /// <see cref="ProjectActivityEntry"/>, and past it the panel says so (<c>Panels_Tests_HeadBusyRemote</c>); with anything else it answers null at once, as before.
    /// </summary>
    private async Task<IDisposable?> EnterAsync(string projectId, CancellationToken cancellationToken)
    {
        try
        {
            return await ProjectActivityEntry.EnterAsync(_activity, projectId, ProjectActivityKind.Audit, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (UserTextException busy) when (busy.Text.Code == ProjectActivityEntry.BusyRemoteCode)
        {
            throw new HeadTestsRefusedException("Panels_Tests_HeadBusyRemote", busy.Message);
        }
    }

    private HeadTestsResult Present(IReadOnlyList<VerificationResult> results, IReadOnlyList<string> notRun, IReadOnlyList<string> stopped, string projectRoot)
    {
        var lines = new List<TestRunRow>(results.Count);
        var failed = new List<FailedTestRow>();
        long totalFailed = 0;
        foreach (var result in results)
        {
            var evidence = result.Evidence;
            lines.Add(TestRunRows.Build(null, evidence.Title, evidence.TestRun, evidence.ContentSha256[..12], _texts, forceFailed: !result.Succeeded));
            totalFailed += evidence.TestRun?.Failed ?? 0;
            failed.AddRange((result.FailedTests ?? []).Select(f => new FailedTestRow(result.StepId, f.Name, f.Message)));
        }

        return new HeadTestsResult(lines, failed, notRun, (int)Math.Clamp(totalFailed - failed.Count, 0, int.MaxValue), stopped, projectRoot);
    }

    private string BusyWith(ProjectActivityKind? kind) => _texts.Get(kind switch
    {
        ProjectActivityKind.Run => "Panels_Tests_BusyRun",
        ProjectActivityKind.Planning => "Panels_Tests_BusyPlanning",
        ProjectActivityKind.Debate => "Panels_Tests_BusyDebate",
        _ => "Panels_Tests_BusyAudit",
    });
}
