using System.Globalization;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Panels;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Debate.Fakes;

namespace AgentStudio.Tests.Desktop.Panels;

/// <summary>
/// «Ejecutar las pruebas de HEAD» (P50, docs/17 D-45): the service runs the isolated steps of the profile on the commit HEAD points at
/// with the verifier of the runs (doubles here), refuses while something else holds the project, stops at its time budget and gives
/// the names of the failed tests the reports say. The ones that run real processes are in the tests of the verifier.
/// </summary>
public sealed class HeadTestsServiceTests
{
    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));
    private static readonly ProjectContext Project = new("proyecto-1", @"C:\proyectos\calc");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VerificationStep Step(string id, ProcessIsolation isolation = ProcessIsolation.LowIntegrity, TimeSpan? timeout = null) =>
        new(id, id == "test" ? "Pruebas" : "Compilar", "dotnet", [id], id == "test" ? EvidenceKind.TestRun : EvidenceKind.BuildLog, TestReportFormat.None, null, timeout ?? TimeSpan.FromMinutes(5), isolation);

    private sealed class World
    {
        public ScriptedDebateWorktrees Git { get; } = new() { HeadCommit = new string('b', 40) };

        public OrchestrationActivity Activity { get; } = new();

        public FakeRunner Runner { get; } = new();

        public VerificationProfile? Profile { get; set; } = new("perfil", [Step("build"), Step("test")]);

        public string? Root { get; set; } = Project.RootPath;

        public HeadTestsService Service(TimeSpan? slack = null) =>
            new(Runner, new FakeProfiles(() => Profile), Git, new FakeWorkspace(), Activity, () => Root, English, slack);
    }

    private sealed class FakeRunner : IVerificationRunner
    {
        public List<(string WorkingDirectory, IReadOnlyList<VerificationStep> Steps, EvidenceScope Scope)> Calls { get; } = [];

        /// <summary>What the step says: its summary and the tests its report names; the default is a pass.</summary>
        public Func<VerificationStep, (TestRunSummary? Summary, bool Succeeded, IReadOnlyList<FailedTest> Failed)> Say { get; set; } =
            step => (step.Kind == EvidenceKind.TestRun ? new TestRunSummary(12, 0, 1, 0) : null, true, []);

        public Func<CancellationToken, Task>? Wait { get; set; }

        public Func<ProjectActivityKind?>? ObserveActivity { get; set; }

        public ProjectActivityKind? ActivityDuringRun { get; private set; }

        public async Task<IReadOnlyList<VerificationResult>> RunAsync(string workingDirectory, IReadOnlyList<VerificationStep> steps, EvidenceScope scope, IProgress<ProcessOutputLine>? output, CancellationToken cancellationToken)
        {
            Calls.Add((workingDirectory, steps, scope));
            ActivityDuringRun = ObserveActivity?.Invoke();
            if (Wait is not null)
            {
                await Wait(cancellationToken);
            }

            var results = new List<VerificationResult>();
            foreach (var step in steps)
            {
                var (summary, succeeded, failed) = Say(step);
                var record = new EvidenceRecord(new EvidenceId("E-" + step.Id), step.Kind, EvidenceOrigin.CapturedBySystem, step.Title + " (" + step.Id + ")", "agent-studio", DateTimeOffset.UnixEpoch, new string('c', 64), summary, subjectCommit: scope.SubjectCommit);
                results.Add(new VerificationResult(step.Id, record, succeeded, false, succeeded ? 0 : 1, failed));
                if (!succeeded)
                {
                    break;
                }
            }

            return results;
        }
    }

    private sealed class FakeProfiles(Func<VerificationProfile?> profile) : IVerificationProfileSource
    {
        public Task<VerificationProfile?> LoadAsync(ProjectContext project, CancellationToken cancellationToken) => Task.FromResult(profile());

        public VerificationProfile? Propose(ProjectContext project) => null;

        public Task SaveAsync(ProjectContext project, VerificationProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeWorkspace : IProjectWorkspace
    {
        public Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken) => Task.FromResult(Project);
    }

    [Fact]
    public async Task It_runs_the_isolated_steps_of_the_profile_on_the_commit_of_HEAD_with_the_repository_of_the_project()
    {
        var world = new World();

        var result = await world.Service().RunAsync(Ct);

        var call = Assert.Single(world.Runner.Calls);
        Assert.Equal(["build", "test"], call.Steps.Select(s => s.Id));
        Assert.Equal(new string('b', 40), call.Scope.SubjectCommit);
        Assert.Equal(Project.RootPath, call.Scope.RepositoryRoot);
        Assert.Equal(Project.ProjectId, call.Scope.ProjectId);
        Assert.Null(call.Scope.Run);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal("12 passed · 0 failed · 1 skipped · exit code 0", result.Lines[1].Counters);
        Assert.All(result.Lines, l => Assert.False(l.Failed));
    }

    [Fact]
    public async Task A_step_the_profile_runs_without_isolation_is_never_run_and_the_result_says_which()
    {
        var world = new World { Profile = new VerificationProfile("perfil", [Step("build"), Step("test"), Step("deploy", ProcessIsolation.None)]) };

        var result = await world.Service().RunAsync(Ct);

        Assert.Equal(["build", "test"], world.Runner.Calls.Single().Steps.Select(s => s.Id));
        Assert.Equal(["deploy"], result.NotRun);
    }

    [Fact]
    public async Task A_profile_without_an_isolated_step_is_refused_and_nothing_runs()
    {
        var world = new World { Profile = new VerificationProfile("perfil", [Step("deploy", ProcessIsolation.None)]) };

        var refused = await Assert.ThrowsAsync<HeadTestsRefusedException>(() => world.Service().RunAsync(Ct));

        Assert.Equal("Panels_Tests_HeadNoIsolatedStep", refused.TextKey);
        Assert.Empty(world.Runner.Calls);
        Assert.Null(world.Activity.Current(Project.ProjectId));
    }

    [Fact]
    public async Task A_project_without_a_profile_or_without_a_project_open_is_refused()
    {
        var world = new World { Profile = null };
        var noProfile = await Assert.ThrowsAsync<HeadTestsRefusedException>(() => world.Service().RunAsync(Ct));

        world.Root = null;
        var noProject = await Assert.ThrowsAsync<HeadTestsRefusedException>(() => world.Service().RunAsync(Ct));

        Assert.Equal("Panels_Tests_HeadNoProfile", noProfile.TextKey);
        Assert.Equal("Panels_Tests_HeadNoProject", noProject.TextKey);
        Assert.Empty(world.Runner.Calls);
    }

    [Theory]
    [InlineData(ProjectActivityKind.Run, "a run")]
    [InlineData(ProjectActivityKind.Planning, "a planning")]
    [InlineData(ProjectActivityKind.Debate, "a debate")]
    [InlineData(ProjectActivityKind.Audit, "an audit or other tests")]
    public async Task It_does_not_run_while_a_run_a_planning_a_debate_or_an_audit_holds_the_project_and_says_which(ProjectActivityKind holder, string said)
    {
        var world = new World();
        using var held = world.Activity.TryEnter(Project.ProjectId, holder);

        var refused = await Assert.ThrowsAsync<HeadTestsRefusedException>(() => world.Service().RunAsync(Ct));

        Assert.Equal("Panels_Tests_HeadBusy", refused.TextKey);
        Assert.Equal([said], refused.Arguments);
        Assert.Empty(world.Runner.Calls);
    }

    [Fact]
    public async Task It_holds_the_project_while_the_tests_run_so_nothing_else_starts_and_lets_it_go_after_even_when_they_fail()
    {
        var world = new World();
        world.Runner.ObserveActivity = () => world.Activity.Current(Project.ProjectId);
        await world.Service().RunAsync(Ct);
        var during = world.Runner.ActivityDuringRun;

        world.Runner.Wait = _ => throw new InvalidOperationException("boom");
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Service().RunAsync(Ct));

        Assert.NotNull(during);
        Assert.Null(world.Activity.Current(Project.ProjectId));
        Assert.NotNull(world.Activity.TryEnter(Project.ProjectId, ProjectActivityKind.Debate));
    }

    [Fact]
    public async Task The_names_and_the_messages_of_the_failed_tests_come_from_the_report_and_the_rest_is_counted()
    {
        var world = new World();
        world.Runner.Say = step => step.Id == "test"
            ? (new TestRunSummary(3, 5, 0, 1), false, [new FailedTest("Calc.Resta", "esperado 0"), new FailedTest("Calc.Divide", null)])
            : (null, true, []);

        var result = await world.Service().RunAsync(Ct);

        Assert.Equal([("test", "Calc.Resta", "esperado 0"), ("test", "Calc.Divide", null)], result.FailedTests!.Select(f => (f.Step, f.Name, f.Message)));
        Assert.Equal(3, result.MoreFailures);
        Assert.True(result.Lines[1].Failed);
        Assert.Equal("failed", result.Lines[1].StateText);
    }

    [Fact]
    public async Task A_step_that_failed_without_counters_is_still_shown_as_failed()
    {
        var world = new World();
        world.Runner.Say = step => (null, false, []);

        var result = await world.Service().RunAsync(Ct);

        var line = Assert.Single(result.Lines);
        Assert.True(line.Failed);
        Assert.Equal("failed", line.StateText);
    }

    [Fact]
    public async Task Cancelling_stops_the_run_and_is_not_said_as_a_refusal()
    {
        var world = new World();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        world.Runner.Wait = async token =>
        {
            await cancel.CancelAsync();
            token.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.Service().RunAsync(cancel.Token));

        Assert.Null(world.Activity.Current(Project.ProjectId));
    }

    [Fact]
    public async Task The_whole_run_has_a_time_budget_and_passing_it_is_said_as_such()
    {
        var world = new World { Profile = new VerificationProfile("perfil", [Step("test", timeout: TimeSpan.FromMilliseconds(30))]) };
        world.Runner.Wait = token => Task.Delay(Timeout.Infinite, token);

        var refused = await Assert.ThrowsAsync<HeadTestsRefusedException>(() => world.Service(slack: TimeSpan.FromMilliseconds(30)).RunAsync(Ct));

        Assert.Equal("Panels_Tests_HeadTimeBudget", refused.TextKey);
        Assert.Null(world.Activity.Current(Project.ProjectId));
    }

    [Fact]
    public void Without_one_of_the_parts_there_is_no_service_and_so_no_button()
    {
        var world = new World();
        var profiles = new FakeProfiles(() => null);
        var workspace = new FakeWorkspace();

        Assert.NotNull(new HeadTestsServices(world.Runner, profiles, world.Git, workspace, world.Activity).For(() => Project.RootPath));
        Assert.Null(new HeadTestsServices(null, profiles, world.Git, workspace, world.Activity).For(() => Project.RootPath));
        Assert.Null(new HeadTestsServices(world.Runner, profiles, null, workspace, world.Activity).For(() => Project.RootPath));
        Assert.Null(new HeadTestsServices(world.Runner, profiles, world.Git, workspace, null).For(() => Project.RootPath));
    }
}
