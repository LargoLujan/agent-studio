using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Setup;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Setup;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Security;
using AgentStudio.Infrastructure.Models;
using AgentStudio.Tests.Application.Setup;

namespace AgentStudio.Tests.Desktop.Setup;

/// <summary>
/// F13-12 (docs/17 D-26 and D-27): the configurator of a project as the person drives it. The view model is built over the real
/// service and fakes of its ports, in Spanish of Spain and in English; nothing is written until «Aplicar», and then each part goes
/// through its gate.
/// </summary>
public sealed class SetupViewModelTests
{
    private static readonly ResourceTextProvider Spanish = new(CultureInfo.GetCultureInfo("es-ES"));

    private static readonly ResourceTextProvider English = new(CultureInfo.GetCultureInfo("en"));

    private static readonly string[] DotNetFiles = ["App.slnx", "src/App/App.csproj", "src/App/Program.cs", "docs/README.md"];

    private sealed class Rig
    {
        public InMemoryRoster Roster { get; } = new(SetupTeams.Default());

        public FakeProfiles Profiles { get; } = new();

        public FakeOptionsStore Options { get; } = new();

        public RecordingRecorder Recorder { get; } = new();

        public FakeBrief Brief { get; } = new();

        public FakeActivations Activations { get; } = new();

        public FakeGate Gate { get; set; } = new(true);

        public IReadOnlyList<string> Files { get; set; } = DotNetFiles;

        public ProjectSetupService Service() => new(
            new FakeRepositoryReader(SetupFakes.Facts(Files)),
            Roster,
            Profiles,
            Options,
            Recorder,
            Brief,
            new FakeSkillCatalog(
                FakeSkillCatalog.Skill("dotnet-testing", ["dotnet", "testing"]),
                FakeSkillCatalog.Skill("planner", ["planning"])),
            Activations,
            requirements: null,
            models: new BuiltInModelCatalog(),
            learning: null,
            gate: Gate);

        public async Task<SetupViewModel> OpenAsync(IUiTextProvider? texts = null)
        {
            var vm = new SetupViewModel(Service(), texts ?? Spanish);
            await vm.LoadAsync(SetupFakes.Project);
            return vm;
        }
    }

    private static SetupStepViewModel Step(SetupViewModel vm, SetupStep step) => vm.Steps[(int)step];

    // ---- loading: seven steps and a summary, nothing written ------------------------------------------------------

    [Fact]
    public async Task Loading_fills_the_seven_steps_and_the_summary_and_writes_nothing()
    {
        var rig = new Rig();

        var vm = await rig.OpenAsync();

        Assert.Equal(8, vm.Steps.Count);
        Assert.Equal(Enum.GetValues<SetupStep>(), vm.Steps.Select(s => s.Step));
        Assert.Equal(Spanish.Get("Setup_Step_Project"), vm.Steps[0].Title);
        Assert.Contains(vm.ProjectLines, l => l.Contains(".NET", StringComparison.Ordinal));
        Assert.Contains(vm.ProjectLines, l => l.Contains("main", StringComparison.Ordinal));
        Assert.Equal(["Orquestador", "Ejecutor", "Juez"], vm.TeamRows.Select(r => r.Name));
        Assert.Equal(3, vm.ModelRows.Count);
        Assert.Contains(vm.VerificationLines, l => l.Contains("dotnet", StringComparison.Ordinal));
        Assert.Contains(vm.SkillRows, r => r.SkillName == "dotnet-testing");
        Assert.DoesNotContain(vm.SkillRows, r => r.SkillName == "dotnet-linked");
        Assert.Equal("12", vm.MaxAgentRunsText);
        Assert.Equal("3", vm.MaxParallelTasksText);
        Assert.Equal(string.Empty, vm.TimeMinutesText);
        Assert.Equal(0, rig.Roster.Saves);
        Assert.Equal(0, rig.Profiles.Saves);
        Assert.Equal(0, rig.Options.Saves);
        Assert.Equal(0, rig.Brief.Saves);
        Assert.Empty(rig.Activations.Appended);
        Assert.Empty(rig.Recorder.Entries);
        Assert.Empty(rig.Gate.Asked);
    }

    [Fact]
    public async Task Only_what_is_reviewed_anyway_is_on_by_default_and_nothing_is_applied_alone()
    {
        var rig = new Rig();

        var vm = await rig.OpenAsync();

        // The profile would change; the template of the project is the default one, so the team has nothing to add.
        Assert.False(Step(vm, SetupStep.Brief).IsIncluded);
        Assert.False(Step(vm, SetupStep.Team).IsIncluded);
        Assert.False(Step(vm, SetupStep.Models).IsIncluded);
        Assert.True(Step(vm, SetupStep.Verification).IsIncluded);
        Assert.False(Step(vm, SetupStep.Skills).IsIncluded);
        Assert.False(Step(vm, SetupStep.Budget).IsIncluded);
        Assert.False(Step(vm, SetupStep.Project).CanSkip);
        Assert.False(Step(vm, SetupStep.Summary).CanSkip);
        Assert.True(vm.WillAskHello);
        Assert.Single(vm.SummaryLines);
    }

    [Fact]
    public async Task Without_a_project_it_says_so_and_applies_nothing()
    {
        var vm = new SetupViewModel(new Rig().Service(), Spanish);

        await vm.LoadAsync(null);

        Assert.False(vm.HasProject);
        Assert.Equal(Spanish.Get("Setup_No_Project"), vm.ErrorMessage);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_profile_file_that_cannot_be_used_is_said_and_never_proposed_over()
    {
        var rig = new Rig();
        rig.Profiles.Unreadable = true;

        var vm = await rig.OpenAsync();

        Assert.Equal(Spanish.Get("Setup_Verification_Unreadable"), vm.VerificationStatus);
        Assert.False(Step(vm, SetupStep.Verification).IsIncluded);
        Assert.Contains(Spanish.Get("Text_setup_problem_profile_unreadable"), vm.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_recognised_stack_it_says_there_is_no_profile_proposed()
    {
        var rig = new Rig { Files = ["notes.txt"] };
        var service = new ProjectSetupService(new FakeRepositoryReader(SetupFakes.Facts(rig.Files)), rig.Roster, new FakeProfiles(null), rig.Options, rig.Recorder);
        var vm = new SetupViewModel(service, Spanish);

        await vm.LoadAsync(SetupFakes.Project);

        Assert.Equal(Spanish.Get("Text_setup_verification_no_stack"), vm.VerificationStatus);
        Assert.False(Step(vm, SetupStep.Verification).IsIncluded);
    }

    // ---- the brief ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_brief_starts_as_a_skeleton_of_the_stack_and_is_only_included_once_the_person_writes()
    {
        var vm = await new Rig().OpenAsync();

        Assert.Contains("## " + Spanish.Get("Text_setup_brief_section_what"), vm.BriefText, StringComparison.Ordinal);
        Assert.Contains("## " + Spanish.Get("Text_setup_brief_section_stack_dotnet"), vm.BriefText, StringComparison.Ordinal);
        Assert.False(Step(vm, SetupStep.Brief).IsIncluded);

        vm.BriefText += "Una calculadora.";

        Assert.True(Step(vm, SetupStep.Brief).IsIncluded);
        Assert.Contains(vm.SummaryLines, l => l.StartsWith(Spanish.Get("Setup_Step_Brief") + ":", StringComparison.Ordinal));

        vm.BriefText = "  ";

        Assert.False(Step(vm, SetupStep.Brief).IsIncluded);
    }

    // ---- the team and the models ---------------------------------------------------------------------------------

    [Fact]
    public async Task Choosing_a_size_proposes_its_template_and_the_added_agents_turn_the_step_on()
    {
        var vm = await new Rig().OpenAsync();
        Assert.False(Step(vm, SetupStep.Team).IsIncluded);

        vm.SelectedSize = vm.Sizes.Single(s => s.Size == SetupSize.Large);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await WaitForAsync(() => vm.TeamRows.Count == 5);

        Assert.Equal(["Orquestador", "Ejecutor", "Ejecutor ligero", "Juez", "Revisor"], vm.TeamRows.Select(r => r.Name));
        Assert.Equal(Spanish.Get("Setup_Team_Added"), vm.TeamRows.Single(r => r.Name == "Revisor").Kind);
        Assert.Equal(Spanish.Get("Setup_Team_Same"), vm.TeamRows.Single(r => r.Name == "Juez").Kind);
        Assert.True(Step(vm, SetupStep.Team).IsIncluded);
        Assert.Contains(vm.SummaryLines, l => l.Contains("Revisor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Each_model_shows_what_the_agent_has_the_recommendation_and_why_and_taking_it_goes_into_the_summary()
    {
        var vm = await new Rig().OpenAsync();
        var executor = vm.ModelRows.Single(r => r.Name == "Ejecutor");

        Assert.Contains(Spanish.Get("Setup_Model_Default"), executor.Current, StringComparison.Ordinal);
        Assert.NotNull(executor.Recommended);
        Assert.StartsWith(Spanish.Get("Setup_Models_Why").Split('{')[0], executor.Why, StringComparison.Ordinal);
        Assert.True(executor.CanAccept);
        Assert.False(executor.IsAccepted);
        Assert.True(vm.HasRecommendations);
        Assert.DoesNotContain(vm.SummaryLines, l => l.Contains("Ejecutor", StringComparison.Ordinal));

        executor.IsAccepted = true;

        Assert.True(Step(vm, SetupStep.Models).IsIncluded);
        Assert.Contains(vm.SummaryLines, l => l.Contains("Ejecutor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Use_all_recommended_takes_each_recommendation_the_agents_do_not_follow_yet()
    {
        var vm = await new Rig().OpenAsync();

        vm.UseAllRecommendedCommand.Execute(null);

        Assert.All(vm.ModelRows.Where(r => r.CanAccept), r => Assert.True(r.IsAccepted));
        Assert.True(Step(vm, SetupStep.Models).IsIncluded);
        Assert.Equal(3, vm.SummaryLines.Count(l => l.StartsWith(Spanish.Get("Setup_Step_Models") + ":", StringComparison.Ordinal)));
    }

    // ---- apply: each part through its gate ------------------------------------------------------------------------

    [Fact]
    public async Task Applying_the_models_stores_them_in_the_template_tells_the_owner_and_says_the_plan_must_be_approved_again()
    {
        var rig = new Rig();
        var vm = await rig.OpenAsync();
        SetupApplyResult? told = null;
        vm.Applied += (_, result) => told = result;
        Step(vm, SetupStep.Verification).IsIncluded = false;
        vm.UseAllRecommendedCommand.Execute(null);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(told);
        Assert.True(told.RosterChanged);
        var stored = await rig.Roster.ListAsync(SetupFakes.Project.ProjectId, TestContext.Current.CancellationToken);
        Assert.All(stored, a => Assert.NotNull(a.Model.Model));
        Assert.All(stored, a => Assert.NotNull(a.Effort));
        Assert.Contains(Spanish.Get("Setup_Result_Roster_Changed"), vm.Notice, StringComparison.Ordinal);
        Assert.Contains(Spanish.Get("Setup_Result_Recorded"), vm.Notice, StringComparison.Ordinal);
        Assert.Contains(vm.ResultLines, l => l.Contains(Spanish.Get("Setup_Outcome_Applied"), StringComparison.Ordinal));
        var payload = Assert.IsType<ProjectSetupAppliedPayload>(Assert.Single(rig.Recorder.Entries).Payload);
        Assert.NotNull(payload.RosterHash);
        Assert.Empty(rig.Gate.Asked);

        // What was applied is no longer a proposal: the steps read the project again, and there is nothing left to apply.
        Assert.All(vm.ModelRows, r => Assert.False(r.CanAccept));
        Assert.DoesNotContain(vm.SummaryLines, l => l.StartsWith(Spanish.Get("Setup_Step_Models") + ":", StringComparison.Ordinal));
        Assert.True(vm.HasResults);
    }

    [Fact]
    public async Task A_part_that_fails_does_not_hide_the_ones_written_the_owner_is_told_and_the_cause_is_said()
    {
        var rig = new Rig();
        rig.Options.Failure = new UnauthorizedAccessException("Access to the options folder is denied.");
        var vm = await rig.OpenAsync();
        SetupApplyResult? told = null;
        vm.Applied += (_, result) => told = result;
        Step(vm, SetupStep.Verification).IsIncluded = false;
        vm.UseAllRecommendedCommand.Execute(null);
        vm.MaxAgentRunsText = "20";

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(told);
        Assert.True(told.RosterChanged);
        Assert.Contains(vm.ResultLines, l => l.Contains(Spanish.Get("Setup_Outcome_Failed"), StringComparison.Ordinal) && l.Contains("Access to the options folder is denied.", StringComparison.Ordinal));
        Assert.Contains(vm.ResultLines, l => l.Contains(Spanish.Get("Setup_Outcome_Applied"), StringComparison.Ordinal));
        Assert.Contains(Spanish.Get("Setup_Result_Roster_Changed"), vm.Notice, StringComparison.Ordinal);
        Assert.Equal(string.Empty, vm.ErrorMessage.Replace(Spanish.Get("Text_setup_problem_skills_catalog_empty"), string.Empty));
        var payload = Assert.IsType<ProjectSetupAppliedPayload>(Assert.Single(rig.Recorder.Entries).Payload);
        Assert.NotNull(payload.RosterHash);
        Assert.Null(payload.OptionsSha256);
    }

    [Fact]
    public async Task The_profile_asks_the_barrier_and_what_it_does_not_confirm_is_reported_with_nothing_written()
    {
        var rig = new Rig { Gate = new FakeGate(false) };
        var vm = await rig.OpenAsync();

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(PresenceAction.ChangeVerificationProfile, Assert.Single(rig.Gate.Asked).Action);
        Assert.Null(rig.Profiles.Stored);
        Assert.Contains(vm.ResultLines, l =>
            l.Contains(Spanish.Get("Setup_Outcome_Denied"), StringComparison.Ordinal) && l.Contains(Spanish.Get("Text_setup_hello_not_confirmed"), StringComparison.Ordinal));
        Assert.Empty(rig.Recorder.Entries);
        Assert.Equal(string.Empty, vm.Notice);
    }

    [Fact]
    public async Task A_confirmed_profile_is_written_and_the_log_names_its_hash()
    {
        var rig = new Rig();
        var vm = await rig.OpenAsync();

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(rig.Profiles.Stored);
        var payload = Assert.IsType<ProjectSetupAppliedPayload>(Assert.Single(rig.Recorder.Entries).Payload);
        Assert.Equal(rig.Profiles.Stored.ContentHash, payload.VerificationProfileHash);
        Assert.Equal(DecisionKind.ProjectSetupApplied, rig.Recorder.Entries[0].Kind);
        Assert.Null(payload.RosterHash);
    }

    [Fact]
    public async Task A_skipped_step_writes_nothing_even_when_it_has_a_proposal()
    {
        var rig = new Rig();
        var vm = await rig.OpenAsync();
        Step(vm, SetupStep.Verification).IsIncluded = false;

        Assert.Empty(vm.SummaryLines);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.True(vm.IsSummaryEmpty);
        Assert.Null(vm.BuildSelection().Profile);
        Assert.Equal(0, rig.Profiles.Saves);
    }

    [Fact]
    public async Task The_skills_the_person_ticks_become_their_decisions_and_none_is_on_by_itself()
    {
        var rig = new Rig();
        var vm = await rig.OpenAsync();
        Step(vm, SetupStep.Verification).IsIncluded = false;
        Assert.All(vm.SkillRows, r => Assert.False(r.IsChosen));

        vm.SkillRows.Single(r => r.SkillName == "dotnet-testing" && r.AgentName == "Ejecutor").IsChosen = true;
        Assert.True(Step(vm, SetupStep.Skills).IsIncluded);
        await vm.ApplyCommand.ExecuteAsync(null);

        var decision = Assert.Single(rig.Activations.Appended);
        Assert.Equal("ejecutor", decision.Activation.ScopeId);
        Assert.Equal("dotnet-testing", decision.Activation.Skill.Value);
        Assert.Equal("user", decision.DecidedBy);
        Assert.NotNull(((ProjectSetupAppliedPayload)Assert.Single(rig.Recorder.Entries).Payload).SkillActivationsSha256);

        // Active now: the row is there, ticked, and the person cannot decide it again from here.
        var active = vm.SkillRows.Single(r => r.SkillName == "dotnet-testing" && r.AgentName == "Ejecutor");
        Assert.True(active.AlreadyActive);
        Assert.True(active.IsChosen);
        Assert.False(active.CanChoose);
    }

    // ---- the budget ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0", "3", "", "Text_setup_budget_agent_runs_out_of_range")]
    [InlineData("201", "3", "", "Text_setup_budget_agent_runs_out_of_range")]
    [InlineData("abc", "3", "", "Text_setup_budget_agent_runs_out_of_range")]
    [InlineData("12", "9", "", "Text_setup_budget_parallel_tasks_out_of_range")]
    [InlineData("12", "3", "241", "Text_setup_budget_time_out_of_range")]
    [InlineData("12", "3", "0", "Text_setup_budget_time_out_of_range")]
    public async Task A_budget_that_cannot_be_used_says_why_and_is_not_applied(string runs, string parallel, string minutes, string key)
    {
        var vm = await new Rig().OpenAsync();
        Step(vm, SetupStep.Verification).IsIncluded = false;

        vm.MaxAgentRunsText = runs;
        vm.MaxParallelTasksText = parallel;
        vm.TimeMinutesText = minutes;

        Assert.Equal(Spanish.Get(key), vm.BudgetProblem);
        Assert.False(Step(vm, SetupStep.Budget).IsIncluded);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_valid_budget_goes_into_the_summary_and_is_saved_for_the_project()
    {
        var rig = new Rig();
        var vm = await rig.OpenAsync();
        Step(vm, SetupStep.Verification).IsIncluded = false;

        vm.MaxAgentRunsText = "20";
        vm.MaxParallelTasksText = "2";
        vm.TimeMinutesText = "30";

        Assert.True(Step(vm, SetupStep.Budget).IsIncluded);
        Assert.Equal(string.Empty, vm.BudgetProblem);
        Assert.Single(vm.SummaryLines);
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(new ProjectRunOptions(20, 2, TimeSpan.FromMinutes(30)), rig.Options.Stored);
        Assert.Equal("20", vm.MaxAgentRunsText);
    }

    // ---- navigation, language and accessibility ------------------------------------------------------------------

    [Fact]
    public async Task The_steps_go_in_order_and_back_and_the_list_goes_to_any()
    {
        var vm = await new Rig().OpenAsync();

        Assert.True(vm.IsFirst);
        Assert.False(vm.BackCommand.CanExecute(null));
        for (var i = 1; i < 8; i++)
        {
            vm.NextCommand.Execute(null);
            Assert.Equal((SetupStep)i, vm.Step);
            Assert.True(vm.Steps[i].IsCurrent);
            Assert.Equal(1, vm.Steps.Count(s => s.IsCurrent));
        }

        Assert.True(vm.IsLast);
        Assert.False(vm.NextCommand.CanExecute(null));
        vm.BackCommand.Execute(null);
        Assert.Equal(SetupStep.Budget, vm.Step);
        vm.GoToCommand.Execute(vm.Steps[(int)SetupStep.Models]);
        Assert.Equal(SetupStep.Models, vm.Step);
        Assert.Equal(Spanish.Get("Setup_Step_Models"), vm.CurrentTitle);
    }

    [Fact]
    public async Task Every_text_comes_out_in_the_language_of_the_provider()
    {
        var spanish = await new Rig().OpenAsync(Spanish);
        var english = await new Rig().OpenAsync(English);

        Assert.Equal(Spanish.Get("Setup_Step_Verification"), spanish.Steps[(int)SetupStep.Verification].Title);
        Assert.Equal("Tests", english.Steps[(int)SetupStep.Verification].Title);
        Assert.Equal(spanish.ProjectLines.Count, english.ProjectLines.Count);
        Assert.Contains(english.ProjectLines, l => l.StartsWith("Technology:", StringComparison.Ordinal));
        Assert.Contains("## What the project is", english.BriefText, StringComparison.Ordinal);
        Assert.Contains("## " + Spanish.Get("Text_setup_brief_section_what"), spanish.BriefText, StringComparison.Ordinal);
        Assert.Equal("small", english.Sizes[0].Label);
        Assert.Contains("effort", english.ModelRows[0].Current, StringComparison.Ordinal);
        Assert.Equal(Spanish.Get("Setup_Verification_Hello_Note"), spanish.VerificationStatus);
        Assert.Equal("Windows Hello will be asked when you apply.", english.VerificationStatus);
        Assert.DoesNotContain(english.ModelRows.Select(r => r.Why), w => w is not null && w.StartsWith('['));
    }

    [Fact]
    public async Task The_items_of_the_lists_are_named_for_a_screen_reader()
    {
        var vm = await new Rig().OpenAsync();
        vm.UseAllRecommendedCommand.Execute(null);

        Assert.All(vm.Steps, s => Assert.Equal(s.Title, s.ToString()));
        Assert.All(vm.Sizes, s => Assert.Equal(s.Label, s.ToString()));
        Assert.All(vm.TeamRows, r => Assert.StartsWith(r.Name, r.ToString(), StringComparison.Ordinal));
        Assert.All(vm.ModelRows, r => Assert.Equal(r.Name, r.ToString()));
        Assert.All(vm.SkillRows, r => Assert.StartsWith(r.SkillName, r.ToString(), StringComparison.Ordinal));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "The condition was not met in time.");
    }
}
