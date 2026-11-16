using System.Globalization;
using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Setup;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Setup;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Security;
using AgentStudio.Infrastructure.Models;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Tests.Application.Setup;
using AgentStudio.Tests.Debate.Audit.Fixtures;

namespace AgentStudio.Tests.Desktop.Setup;

/// <summary>
/// The check step of the configurator (docs/24 §3.4, wave 1): the commands are edited as one line each and become the steps of the
/// profile with the rest of each step kept; «Probarla ahora» runs them once with the verifier of the runs, confined, and saving them
/// still asks Windows Hello. Also the summary of every step and «Ir a trabajar».
/// </summary>
public sealed class SetupChecksTests
{
    private static readonly ResourceTextProvider Spanish = new(CultureInfo.GetCultureInfo("es-ES"));

    private static readonly string[] DotNetFiles = ["App.slnx", "src/App/App.csproj"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig
    {
        public InMemoryRoster Roster { get; } = new(SetupTeams.Default());

        public StackProfiles Profiles { get; init; } = new(DotNetVerificationProfile.Propose());

        public FakeOptionsStore Options { get; } = new();

        public RecordingRecorder Recorder { get; } = new();

        public FakeGate Gate { get; init; } = new(true);

        public IReadOnlyList<string> Files { get; init; } = DotNetFiles;

        public SetupCheckTrial? Trial { get; init; }

        public Func<string, IReadOnlyList<SetupProviderLimit>>? Limits { get; init; }

        public ProjectSetupService Service() => new(
            new FakeRepositoryReader(SetupFakes.Facts(Files)),
            Roster,
            Profiles,
            Options,
            Recorder,
            new FakeBrief(),
            models: new BuiltInModelCatalog(),
            gate: Gate);

        public async Task<SetupViewModel> OpenAsync()
        {
            var vm = new SetupViewModel(Service(), Spanish, Trial, Limits);
            await vm.LoadAsync(SetupFakes.Project);
            return vm;
        }
    }

    /// <summary>The stack profiles, served as the configurator gets them, so the rows edit the real proposals.</summary>
    private sealed class StackProfiles(VerificationProfile? proposed) : ISetupVerificationProfiles
    {
        public VerificationProfile? Stored { get; set; }

        public bool Unreadable { get; init; }

        public VerificationProfile? Propose(IReadOnlyList<string> relativeFiles) => proposed;

        public Task<VerificationProfile?> LoadAsync(AgentStudio.Application.Projects.ProjectContext project, CancellationToken ct) =>
            Unreadable ? throw new InvalidDataException("The profile file breaks a rule.") : Task.FromResult(Stored);

        public Task SaveAsync(AgentStudio.Application.Projects.ProjectContext project, VerificationProfile profile, CancellationToken ct)
        {
            Stored = profile;
            return Task.CompletedTask;
        }
    }

    private static SetupStepViewModel Step(SetupViewModel vm, SetupStep step) => vm.Steps[(int)step];

    private static SetupCheckRowViewModel Row(SetupViewModel vm, string id) => vm.CheckRows.Single(r => r.Label == Spanish.Get("Setup_Verification_Step_" + id));

    private static VerificationProfile Node() => NodeVerificationProfile.Propose(true, new NpmLaunch(@"C:\Program Files\nodejs\node_modules\npm\bin\npm-cli.js", null));

    // ---- the line of a command -----------------------------------------------------------------------------------

    public static TheoryData<string> Stacks() => ["dotnet", "node", "python"];

    private static VerificationProfile Proposal(string stack) => stack switch
    {
        "dotnet" => DotNetVerificationProfile.Propose(),
        "node" => Node(),
        _ => PythonVerificationProfile.Propose(),
    };

    [Theory]
    [MemberData(nameof(Stacks))]
    public void Every_command_of_the_proposals_goes_to_one_line_and_back_with_the_same_parts(string stack)
    {
        foreach (var step in Proposal(stack).Steps)
        {
            var line = SetupCommandLine.Render(step.Executable, step.Arguments);

            Assert.Equal([step.Executable, .. step.Arguments], SetupCommandLine.Parse(line));
        }
    }

    [Fact]
    public void A_part_with_spaces_goes_between_quotes_and_a_quote_left_open_is_not_a_command()
    {
        Assert.Equal("node \"C:\\Program Files\\x.js\" ci", SetupCommandLine.Render("node", [@"C:\Program Files\x.js", "ci"]));
        Assert.Equal(["node", @"C:\Program Files\x.js", "ci"], SetupCommandLine.Parse("node \"C:\\Program Files\\x.js\"   ci "));
        Assert.Equal(["echo", string.Empty], SetupCommandLine.Parse("echo \"\""));
        Assert.Null(SetupCommandLine.Parse("dotnet \"build"));
    }

    [Theory]
    [MemberData(nameof(Stacks))]
    public async Task Untouched_rows_make_the_very_same_profile_so_nothing_changes_against_the_one_in_force(string stack)
    {
        var proposal = Proposal(stack);
        var vm = await new Rig { Profiles = new StackProfiles(proposal) { Stored = Proposal(stack) } }.OpenAsync();

        Assert.Equal(proposal.Steps.Count, vm.CheckRows.Count);
        Assert.False(Step(vm, SetupStep.Verification).IsIncluded);
        Assert.Equal(Spanish.Get("Setup_Verification_Same"), vm.VerificationStatus);
        Assert.Null(vm.BuildSelection().Profile);
        Assert.Equal(proposal.ContentHash, SetupChecks.Profile(proposal.Name, vm.CheckRows, Spanish, out var problem)!.ContentHash);
        Assert.Null(problem);
    }

    [Fact]
    public async Task Editing_a_command_keeps_the_rest_of_its_step_turns_the_step_on_and_saving_it_still_asks_windows_hello()
    {
        var rig = new Rig { Profiles = new StackProfiles(DotNetVerificationProfile.Propose()) { Stored = DotNetVerificationProfile.Propose() } };
        var profiles = rig.Profiles;
        var vm = await rig.OpenAsync();
        Assert.False(Step(vm, SetupStep.Verification).IsIncluded);

        Row(vm, "build").CommandText = "dotnet build App.slnx -c Release";

        Assert.True(Step(vm, SetupStep.Verification).IsIncluded);
        Assert.Equal(Spanish.Get("Setup_Verification_Hello_Note"), vm.VerificationStatus);
        Assert.True(vm.WillAskHello);
        var edited = vm.BuildSelection().Profile!;
        var build = edited.Find("build")!;
        var original = DotNetVerificationProfile.Propose().Find("build")!;
        Assert.Equal("dotnet", build.Executable);
        Assert.Equal(["build", "App.slnx", "-c", "Release"], build.Arguments);
        Assert.Equal(original with { Arguments = build.Arguments }, build with { Arguments = build.Arguments });
        Assert.Equal(new VerificationProfile("x", [DotNetVerificationProfile.Propose().Find("test")!]).ContentHash, new VerificationProfile("x", [edited.Find("test")!]).ContentHash);
        Assert.Contains(vm.SummaryLines, l => l.StartsWith(Spanish.Get("Setup_Step_Verification") + ":", StringComparison.Ordinal));

        await vm.ApplyCommand.ExecuteAsync(null);

        var asked = Assert.Single(rig.Gate.Asked);
        Assert.Equal(PresenceAction.ChangeVerificationProfile, asked.Action);
        Assert.Equal(edited.ContentHash, profiles.Stored!.ContentHash);
    }

    [Fact]
    public async Task Without_the_barrier_the_edited_commands_are_never_saved()
    {
        var rig = new Rig { Gate = new FakeGate(false) };
        var vm = await rig.OpenAsync();

        Row(vm, "build").CommandText = "dotnet build App.slnx";
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Single(rig.Gate.Asked);
        Assert.Null(rig.Profiles.Stored);
        Assert.Contains(vm.ResultLines, l => l.Contains(Spanish.Get("Setup_Outcome_Denied"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_command_is_left_out_and_what_required_it_requires_what_it_required()
    {
        var vm = await new Rig { Profiles = new StackProfiles(Node()) }.OpenAsync();

        Row(vm, "build").CommandText = "   ";

        var profile = vm.BuildSelection().Profile!;
        Assert.Equal(["install", "test"], profile.Steps.Select(s => s.Id));
        Assert.Equal(["install"], profile.Find("test")!.Requires);
        Assert.Empty(VerificationRules.Check(profile));
    }

    [Fact]
    public async Task A_test_command_that_does_not_leave_its_report_in_the_folder_of_the_step_says_so_and_blocks_apply()
    {
        var rig = new Rig();
        var vm = await rig.OpenAsync();

        vm.CheckRows.Single(r => r.Label == Spanish.Get("Setup_Verification_Step_test")).CommandText = "dotnet test";

        Assert.Equal(Spanish.Get("Setup_Verification_Report"), vm.CheckRows.Single(r => r.Label == Spanish.Get("Setup_Verification_Step_test")).Problem);
        Assert.True(Step(vm, SetupStep.Verification).IsIncluded);
        Assert.False(vm.ApplyCommand.CanExecute(null));

        vm.CheckRows.Single(r => r.Label == Spanish.Get("Setup_Verification_Step_test")).CommandText = "dotnet \"test";

        Assert.Equal(Spanish.Get("Setup_Verification_Quotes"), vm.CheckRows.Single(r => r.Label == Spanish.Get("Setup_Verification_Step_test")).Problem);
    }

    [Fact]
    public async Task Without_a_recognised_stack_two_blank_commands_with_examples_are_offered_and_writing_them_makes_the_persons_own_profile()
    {
        var rig = new Rig { Files = ["notes.txt"], Profiles = new StackProfiles(null) };
        var vm = await rig.OpenAsync();

        Assert.Equal(2, vm.CheckRows.Count);
        Assert.All(vm.CheckRows, r => Assert.Equal(string.Empty, r.CommandText));
        Assert.Equal(Spanish.Get("Setup_Verification_Example_test"), Row(vm, "test").Example);
        Assert.Equal(Spanish.Get("Text_setup_verification_no_stack"), vm.VerificationStatus);
        Assert.False(Step(vm, SetupStep.Verification).IsIncluded);

        Row(vm, "test").CommandText = "python -m pytest --junitxml={results}/test.xml";

        var profile = vm.BuildSelection().Profile!;
        Assert.Equal(SetupChecks.OwnProfileName, profile.Name);
        var test = Assert.Single(profile.Steps);
        Assert.Equal((EvidenceKind.TestRun, TestReportFormat.JUnit, ProcessIsolation.LowIntegrity), (test.Kind, test.Report, test.Isolation));
        Assert.Null(test.Requires);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("setup.hello.subject-profile-own", Assert.Single(rig.Gate.Asked).Subject.Code);
        Assert.Equal(profile.ContentHash, rig.Profiles.Stored!.ContentHash);
    }

    [Fact]
    public async Task Over_a_file_that_cannot_be_used_the_commands_cannot_be_edited()
    {
        var rig = new Rig { Profiles = new StackProfiles(DotNetVerificationProfile.Propose()) { Unreadable = true } };

        var vm = await rig.OpenAsync();

        Assert.False(vm.CanEditChecks);
        Assert.False(vm.HasCurrentChecks);
        Assert.False(vm.HasProposedChecks);
        Assert.Null(vm.BuildSelection().Profile);
    }

    [Fact]
    public async Task The_commands_in_force_and_the_proposed_ones_can_be_taken_back()
    {
        var stored = SetupFakes.Profile("dotnet", "C:\\tools\\dotnet.exe");
        var rig = new Rig { Profiles = new StackProfiles(DotNetVerificationProfile.Propose()) { Stored = stored } };
        var vm = await rig.OpenAsync();
        Assert.True(Step(vm, SetupStep.Verification).IsIncluded);
        Assert.True(vm.HasCurrentChecks);

        vm.RestoreCurrentChecksCommand.Execute(null);

        Assert.False(Step(vm, SetupStep.Verification).IsIncluded);
        Assert.StartsWith("C:\\tools\\dotnet.exe", Assert.Single(vm.CheckRows).CommandText, StringComparison.Ordinal);

        vm.RestoreProposedChecksCommand.Execute(null);

        Assert.True(Step(vm, SetupStep.Verification).IsIncluded);
    }

    // ---- «Probarla ahora» -----------------------------------------------------------------------------------------

    private const string Head = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task The_trial_runs_only_the_isolated_commands_on_the_last_commit_holding_the_project_as_an_audit_and_saves_nothing()
    {
        var runner = new SetupTrialRunner();
        var activity = new FakeProjectActivity();
        var profile = DotNetVerificationProfile.Propose() with
        {
            Steps = [.. DotNetVerificationProfile.Propose().Steps, new VerificationStep("lint", "Lint", "tool", ["lint"], EvidenceKind.CommandOutput, TestReportFormat.None, null, TimeSpan.FromMinutes(1), ProcessIsolation.None)],
        };
        var trial = new SetupCheckTrial(runner, new AuditRepository(Head), activity);

        var result = await trial.RunAsync(SetupFakes.Project, profile, Ct);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(SetupFakes.Project.RootPath, call.WorkingDirectory);
        Assert.Equal(["build", "test"], call.Steps.Select(s => s.Id));
        Assert.All(call.Steps, s => Assert.Equal(ProcessIsolation.LowIntegrity, s.Isolation));
        Assert.Equal(new EvidenceScope(SetupFakes.Project.ProjectId, null, null, null, Head, SetupFakes.Project.RootPath), call.Scope);
        Assert.Equal([ProjectActivityKind.Audit], activity.Entered);
        Assert.Equal(1, activity.Released);
        Assert.True(result.Passed);
        Assert.Equal(["lint"], result.NotIsolated);
        Assert.Equal(SetupCheckTrial.MaxOutputLines, result.Output.Count);
        Assert.All(result.Output, l => Assert.False(UntrustedText.HasHidden(l)));
        Assert.Equal("test line 29", result.Output[^1]);
    }

    [Fact]
    public async Task The_trial_refuses_a_busy_project_one_without_a_commit_and_commands_that_are_not_isolated()
    {
        var activity = new FakeProjectActivity();
        activity.Hold(ProjectActivityKind.Run);
        var busy = await Assert.ThrowsAsync<UserTextException>(() => new SetupCheckTrial(new SetupTrialRunner(), new AuditRepository(Head), activity).RunAsync(SetupFakes.Project, DotNetVerificationProfile.Propose(), Ct));
        Assert.Equal("setup.check.busy", busy.Text.Code);

        var noCommit = new AuditRepository(Head) { InspectFailure = new InvalidOperationException("No commit yet.") };
        var none = await Assert.ThrowsAsync<UserTextException>(() => new SetupCheckTrial(new SetupTrialRunner(), noCommit, new FakeProjectActivity()).RunAsync(SetupFakes.Project, DotNetVerificationProfile.Propose(), Ct));
        Assert.Equal("setup.check.no-version", none.Text.Code);

        var runner = new SetupTrialRunner();
        var open = new VerificationProfile("x", [DotNetVerificationProfile.Propose().Steps[0] with { Isolation = ProcessIsolation.None }]);
        var notIsolated = await Assert.ThrowsAsync<UserTextException>(() => new SetupCheckTrial(runner, new AuditRepository(Head), new FakeProjectActivity()).RunAsync(SetupFakes.Project, open, Ct));
        Assert.Equal("setup.check.no-isolated-step", notIsolated.Text.Code);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Try_it_now_shows_which_command_failed_the_tests_that_failed_and_the_last_lines_and_writes_nothing()
    {
        var runner = new SetupTrialRunner();
        runner.Failing.Add("test");
        var rig = new Rig { Trial = new SetupCheckTrial(runner, new AuditRepository(Head), new FakeProjectActivity()) };
        var vm = await rig.OpenAsync();
        Assert.True(vm.HasTrial);
        Row(vm, "build").CommandText = "dotnet build App.slnx";

        await vm.TryChecksCommand.ExecuteAsync(null);

        Assert.Equal(["dotnet", "build", "App.slnx"], [runner.Calls.Single().Steps[0].Executable, .. runner.Calls.Single().Steps[0].Arguments]);
        Assert.False(vm.TrialPassed);
        Assert.Equal(Spanish.Get("Setup_Verification_Trial_Failed"), vm.TrialStatus);
        Assert.Contains(Spanish.Format("Setup_Verification_Trial_Step_Passed", Spanish.Get("Setup_Verification_Step_build")), vm.TrialLines);
        Assert.Contains(Spanish.Format("Setup_Verification_Trial_Step_Failed", Spanish.Get("Setup_Verification_Step_test"), 1), vm.TrialLines);
        Assert.Contains(Spanish.Format("Setup_Verification_Trial_Failed_Test", "Calc.Adds"), vm.TrialLines);
        Assert.Contains("test line 29", vm.TrialOutput, StringComparison.Ordinal);
        Assert.False(vm.IsTrying);
        Assert.Null(rig.Profiles.Stored);
        Assert.Empty(rig.Gate.Asked);
        Assert.Empty(rig.Recorder.Entries);
    }

    [Fact]
    public async Task Without_the_trial_composed_the_button_is_not_offered()
    {
        var vm = await new Rig().OpenAsync();

        Assert.False(vm.HasTrial);
        Assert.False(vm.TryChecksCommand.CanExecute(null));
    }

    // ---- the limits ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_limits_say_the_cap_of_each_program_of_the_team_from_the_adapters()
    {
        string? asked = null;
        var rig = new Rig
        {
            Limits = root =>
            {
                asked = root;
                return [new SetupProviderLimit(ProviderId.ClaudeCode, 2), new SetupProviderLimit(ProviderId.CodexCli, 3), new SetupProviderLimit(ProviderId.GeminiCli, 1)];
            },
        };

        var vm = await rig.OpenAsync();

        Assert.Equal(SetupFakes.Project.RootPath, asked);
        Assert.Equal(
            Spanish.Format("Setup_Budget_Providers", string.Join(" · ", Spanish.Format("Setup_Budget_Provider", "Claude Code", 2), Spanish.Format("Setup_Budget_Provider", "Codex", 3))),
            vm.ProviderLimitsText);
    }

    [Fact]
    public void The_limits_come_from_the_composed_adapters_as_the_engine_reads_them()
    {
        var limits = SetupServices.ProviderLimits(new FakeProviders())!;

        Assert.Equal([new SetupProviderLimit(ProviderId.ClaudeCode, 2), new SetupProviderLimit(ProviderId.CodexCli, 5)], limits(@"C:\x"));
        Assert.Null(SetupServices.ProviderLimits(null));
    }

    private sealed class FakeProviders : AgentStudio.Application.Providers.IProjectAgentProviders
    {
        public IReadOnlyList<AgentStudio.Application.Providers.IAgentProvider> For(string projectRoot) =>
        [
            new AgentStudio.Application.Providers.ConcurrencyLimitedAgentProvider(new Adapter(ProviderId.ClaudeCode, 2)),
            new Adapter(ProviderId.CodexCli, 5),
        ];

        private sealed class Adapter(ProviderId provider, int max) : AgentStudio.Application.Providers.IAgentProvider
        {
            public ProviderId Provider => provider;

            public VendorId Vendor => VendorId.Anthropic;

            public AgentStudio.Application.Providers.ProviderCapabilities Capabilities => new(true, true, true, false, true, false, max);

            public Task<AgentStudio.Application.Providers.IAgentRun> StartAsync(AgentStudio.Application.Providers.AgentRunRequest request, CancellationToken cancellationToken) =>
                throw new NotSupportedException("Nothing is started to read a limit.");
        }
    }

    // ---- the summary and «Ir a trabajar» ------------------------------------------------------------------------

    [Fact]
    public async Task The_summary_shows_every_step_as_it_will_be_and_marks_what_is_saved()
    {
        var vm = await new Rig().OpenAsync();
        vm.MaxParallelTasksText = "2";

        Assert.Equal(
            [SetupStep.Brief, SetupStep.Team, SetupStep.Models, SetupStep.Verification, SetupStep.Skills, SetupStep.Budget],
            vm.SummaryBlocks.Select(b => b.Step));
        var saved = vm.SummaryBlocks.Where(b => b.Saves).Select(b => b.Step).ToList();
        Assert.Equal([SetupStep.Verification, SetupStep.Budget], saved);
        Assert.All(vm.SummaryBlocks, b => Assert.Equal(Spanish.Get(b.Saves ? "Setup_Summary_Saves" : "Setup_Summary_Stays"), b.State));
        Assert.All(vm.SummaryBlocks, b => Assert.NotEmpty(b.Lines));
        Assert.Equal(3, vm.SummaryBlocks.Single(b => b.Step == SetupStep.Team).Lines.Count);
        Assert.Contains(vm.SummaryBlocks.Single(b => b.Step == SetupStep.Budget).Lines, l => l.StartsWith("2 ", StringComparison.Ordinal));
        Assert.NotEmpty(vm.SummaryBlocks.Single(b => b.Step == SetupStep.Budget).Changes);

        vm.ChangeStepCommand.Execute(SetupStep.Models);

        Assert.Equal(SetupStep.Models, vm.Step);
    }

    [Fact]
    public async Task With_nothing_to_save_it_offers_to_go_to_work_and_asks_the_shell_to_take_the_person_there()
    {
        var vm = await new Rig().OpenAsync();
        Step(vm, SetupStep.Verification).IsIncluded = false;
        SetupWorkRequestedEventArgs? asked = null;
        vm.WorkRequested += (_, e) => asked = e;

        Assert.True(vm.IsSummaryEmpty);
        Assert.False(vm.HasChanges);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.True(vm.GoToWorkCommand.CanExecute(null));

        vm.GoToWorkCommand.Execute(null);

        Assert.NotNull(asked);
        Assert.Null(asked.Confirmation);
    }

    [Fact]
    public async Task A_successful_apply_confirms_and_asks_to_go_to_work_after_the_owner_was_told_and_a_denied_one_stays()
    {
        var rig = new Rig();
        var vm = await rig.OpenAsync();
        var order = new List<string>();
        vm.Applied += (_, _) => order.Add("applied");
        vm.WorkRequested += (_, e) => order.Add("work:" + e.Confirmation);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(["applied", "work:" + Spanish.Get("Setup_Applied_Confirmation")], order);
        Assert.StartsWith(Spanish.Get("Setup_Applied_Confirmation"), vm.Notice, StringComparison.Ordinal);

        var denied = new Rig { Gate = new FakeGate(false) };
        var other = await denied.OpenAsync();
        var went = false;
        other.WorkRequested += (_, _) => went = true;

        await other.ApplyCommand.ExecuteAsync(null);

        Assert.False(went);
        Assert.Contains(other.ResultLines, l => l.Contains(Spanish.Get("Setup_Outcome_Denied"), StringComparison.Ordinal));
    }

    [Fact]
    public void Only_a_result_with_nothing_denied_or_failed_and_the_registry_written_leads_to_work()
    {
        SetupApplyResult Result(SetupPartOutcome outcome, UserText? problem = null) =>
            new([new SetupPartResult(SetupStep.Team, SetupPartOutcome.Applied), new SetupPartResult(SetupStep.Budget, outcome)], null, null, false, problem is null, problem);

        Assert.True(SetupViewModel.WentWell(Result(SetupPartOutcome.Unchanged)));
        Assert.True(SetupViewModel.WentWell(Result(SetupPartOutcome.Skipped)));
        Assert.False(SetupViewModel.WentWell(Result(SetupPartOutcome.Denied)));
        Assert.False(SetupViewModel.WentWell(Result(SetupPartOutcome.Failed)));
        Assert.False(SetupViewModel.WentWell(Result(SetupPartOutcome.Applied, UserText.Of("setup.problem.record-failed"))));
    }

    [Fact]
    public void Every_code_the_desktop_of_the_configurator_emits_has_its_text_in_both_languages()
    {
        var english = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));
        var folder = Path.Combine(AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport.RepositoryRoot, "src", "AgentStudio.Desktop", "Setup");
        var codes = Directory.GetFiles(folder, "*.cs")
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\"(setup\\.[a-z0-9\\-.]+)\"").Select(m => m.Groups[1].Value))
            .Where(UserText.IsCode)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Contains("setup.check.busy", codes);
        foreach (var code in codes)
        {
            Assert.True(Spanish.Has(UserText.KeyOf(code)), code);
            Assert.True(english.Has(UserText.KeyOf(code)), code);
        }
    }
}

/// <summary>The verifier of the runs as the trial sees it: it records what it is given and answers each step as told.</summary>
internal sealed class SetupTrialRunner : IVerificationRunner
{
    public List<(string WorkingDirectory, IReadOnlyList<VerificationStep> Steps, EvidenceScope Scope)> Calls { get; } = [];

    public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<VerificationResult>> RunAsync(string workingDirectory, IReadOnlyList<VerificationStep> steps, EvidenceScope scope, IProgress<ProcessOutputLine>? output, CancellationToken cancellationToken)
    {
        Calls.Add((workingDirectory, steps, scope));
        var results = new List<VerificationResult>();
        foreach (var step in steps)
        {
            var passed = !Failing.Contains(step.Id);
            for (var i = 0; i < 30; i++)
            {
                output?.Report(new ProcessOutputLine(ProcessStream.StandardOutput, $"{step.Id} line {i}\u202E", DateTimeOffset.UnixEpoch));
            }

            var tests = step.Kind == EvidenceKind.TestRun ? new TestRunSummary(passed ? 4 : 3, passed ? 0 : 1, 0, passed ? 0 : 1) : null;
            var evidence = new EvidenceRecord(new EvidenceId("ev-" + step.Id), step.Kind, EvidenceOrigin.CapturedBySystem, step.Title, "agent-studio", DateTimeOffset.UnixEpoch, new string('a', 64), tests, subjectCommit: scope.SubjectCommit);
            results.Add(new VerificationResult(step.Id, evidence, passed, false, passed ? 0 : 1, passed ? [] : [new FailedTest("Calc.Adds", "expected 4")]));
            if (!passed)
            {
                break;
            }
        }

        return Task.FromResult<IReadOnlyList<VerificationResult>>(results);
    }
}
