using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Setup;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Security;
using AgentStudio.Domain.Skills;
using AgentStudio.Infrastructure.Models;

namespace AgentStudio.Tests.Application.Setup;

/// <summary>
/// F13-12 (docs/17 D-26 and D-27): the configurator proposes without writing and applies through the usual gates. The service is
/// tested over fakes of its ports: what each one holds says what was written, and the log says what was recorded.
/// </summary>
public sealed class ProjectSetupServiceTests
{
    private static readonly string[] DotNetFiles = ["App.slnx", "src/App/App.csproj", "src/App/Program.cs", "docs/README.md"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig
    {
        public InMemoryRoster Roster { get; } = new(SetupTeams.Default());

        public FakeProfiles Profiles { get; } = new();

        public FakeOptionsStore Options { get; } = new();

        public RecordingRecorder Recorder { get; } = new();

        public FakeBrief Brief { get; } = new();

        public FakeActivations Activations { get; } = new();

        public FakeGate Gate { get; set; } = new(true);

        public bool GateComposed { get; set; } = true;

        public FakeSkillCatalog Catalog { get; set; } = new(
            FakeSkillCatalog.Skill("dotnet-testing", ["dotnet", "testing"]),
            FakeSkillCatalog.Skill("dotnet-linked", ["dotnet"], linked: true),
            FakeSkillCatalog.Skill("planner", ["planning"]));

        public FakeRepositoryReader Repository { get; set; } = new(SetupFakes.Facts(DotNetFiles));

        public ProjectSetupService Service() => new(
            Repository,
            Roster,
            Profiles,
            Options,
            Recorder,
            Brief,
            Catalog,
            Activations,
            requirements: null,
            models: new BuiltInModelCatalog(),
            learning: null,
            gate: GateComposed ? Gate : null);
    }

    // ---- the proposal: it reads, it never writes ---------------------------------------------------------------

    [Fact]
    public async Task The_proposal_covers_the_seven_steps_and_writes_nothing()
    {
        var rig = new Rig();
        var proposal = await rig.Service().ProposeAsync(SetupFakes.Project, null, Ct);

        Assert.Equal(SetupStacks.DotNet, proposal.Project.Stack);
        Assert.Equal(SetupSize.Small, proposal.Project.Size);
        Assert.Contains("setup.brief.section.stack-dotnet", proposal.Brief.SkeletonSections);
        Assert.Equal(["orquestador", "ejecutor", "juez"], proposal.Team.Changes.Select(c => c.After.Id.Value));
        Assert.Equal(3, proposal.Models.Count);
        Assert.Equal("dotnet", proposal.Verification.Proposed!.Name);
        Assert.Equal(ProjectRunOptions.Defaults, proposal.Budget);
        Assert.Contains(proposal.Skills, s => s.Skill.Value == "dotnet-testing");

        Assert.Equal(0, rig.Roster.Saves);
        Assert.Equal(0, rig.Profiles.Saves);
        Assert.Equal(0, rig.Options.Saves);
        Assert.Equal(0, rig.Brief.Saves);
        Assert.Empty(rig.Activations.Appended);
        Assert.Empty(rig.Recorder.Entries);
        Assert.Empty(rig.Gate.Asked);
    }

    [Fact]
    public async Task The_models_come_with_the_recommendation_of_the_catalog_and_its_reason()
    {
        var proposal = await new Rig().Service().ProposeAsync(SetupFakes.Project, null, Ct);

        var orchestrator = proposal.Models.Single(m => m.Agent.Id.Value == "orquestador");
        var judge = proposal.Models.Single(m => m.Agent.Id.Value == "juez");
        Assert.NotNull(orchestrator.Recommendation);
        Assert.Equal("model.reason.role-default", orchestrator.Reason!.Code);
        Assert.Equal(ProviderId.CodexCli, judge.Recommendation!.Provider);
        Assert.Equal("model.reason.judge-highest-other-vendor", judge.Reason!.Code);
        Assert.All(proposal.Models, m => Assert.False(m.Follows));
    }

    [Fact]
    public async Task A_medium_project_gets_the_lighter_executor_with_its_own_reason()
    {
        var rig = new Rig { Repository = new FakeRepositoryReader(SetupFakes.Facts(Enumerable.Range(0, 400).Select(i => $"src/f{i}.cs").Append("App.slnx"))) };

        var proposal = await rig.Service().ProposeAsync(SetupFakes.Project, null, Ct);

        Assert.Equal(SetupSize.Medium, proposal.Project.Size);
        Assert.Contains("ejecutor-ligero", proposal.Team.Changes.Select(c => c.After.Id.Value));
        var light = proposal.Models.Single(m => m.Agent.Id.Value == "ejecutor-ligero");
        Assert.Equal("setup.models.light-executor", light.Reason!.Code);
        Assert.Equal(AgentStudio.Domain.Models.ModelTier.Light, light.Recommendation!.Tier);
    }

    [Fact]
    public async Task The_size_can_be_chosen_over_the_one_of_the_project()
    {
        var proposal = await new Rig().Service().ProposeAsync(SetupFakes.Project, SetupSize.Large, Ct);

        Assert.Equal(SetupSize.Large, proposal.Team.Size);
        Assert.Contains("revisor", proposal.Team.Changes.Select(c => c.After.Id.Value));
        Assert.Contains("ejecutor-ligero", proposal.Team.Changes.Select(c => c.After.Id.Value));
    }

    [Fact]
    public async Task A_profile_file_that_cannot_be_used_is_reported_and_nothing_is_proposed_over_it()
    {
        var rig = new Rig();
        rig.Profiles.Unreadable = true;

        var proposal = await rig.Service().ProposeAsync(SetupFakes.Project, null, Ct);

        Assert.True(proposal.Verification.CurrentUnreadable);
        Assert.False(proposal.Verification.WouldChange);
        Assert.Contains(proposal.Problems, p => p.Code == "setup.problem.profile-unreadable");
    }

    [Fact]
    public async Task Without_a_stack_there_is_no_profile_proposed()
    {
        var rig = new Rig { Repository = new FakeRepositoryReader(SetupFakes.Facts(["notes.txt"])) };
        var service = new ProjectSetupService(rig.Repository, rig.Roster, new FakeProfiles(null), rig.Options, rig.Recorder);

        var proposal = await service.ProposeAsync(SetupFakes.Project, null, Ct);

        Assert.Null(proposal.Verification.Proposed);
        Assert.Equal("setup.verification.no-stack", proposal.Verification.NoProposalCode);
        Assert.Null(proposal.Project.Stack);
    }

    // ---- applying: each part by its usual gate -----------------------------------------------------------------

    [Fact]
    public async Task Nothing_selected_writes_nothing_and_records_nothing()
    {
        var rig = new Rig();

        var result = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection(null, null, null, null, null), Ct);

        Assert.All(result.Parts, p => Assert.Equal(SetupPartOutcome.Skipped, p.Outcome));
        Assert.False(result.Recorded);
        Assert.Empty(rig.Recorder.Entries);
        Assert.Empty(rig.Gate.Asked);
    }

    [Fact]
    public async Task The_template_is_saved_through_the_roster_and_its_new_hash_is_what_the_log_names()
    {
        var rig = new Rig();
        var proposal = await rig.Service().ProposeAsync(SetupFakes.Project, SetupSize.Medium, Ct);
        var roster = proposal.Team.Changes.Select(c => c.After).ToList();

        var result = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection(null, roster, null, null, null), Ct);

        Assert.Equal(SetupPartOutcome.Applied, result.Parts.Single(p => p.Step == SetupStep.Team).Outcome);
        Assert.True(result.RosterChanged);
        Assert.NotEqual(result.RosterHashBefore, result.RosterHashAfter);
        var stored = await rig.Roster.ListAsync(SetupFakes.Project.ProjectId, Ct);
        Assert.Equal(RosterHash.Of(stored), result.RosterHashAfter);
        Assert.Contains(stored, a => a.Id.Value == "ejecutor-ligero");
        var payload = Assert.IsType<ProjectSetupAppliedPayload>(Assert.Single(rig.Recorder.Entries).Payload);
        Assert.Equal(result.RosterHashAfter, payload.RosterHash);
        Assert.Null(payload.VerificationProfileHash);
        Assert.Empty(rig.Gate.Asked);
    }

    [Fact]
    public async Task A_template_that_is_the_one_stored_changes_nothing_so_the_hash_and_the_approval_stay()
    {
        var rig = new Rig();
        var stored = await rig.Roster.ListAsync(SetupFakes.Project.ProjectId, Ct);

        var result = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection(null, stored, null, null, null), Ct);

        Assert.Equal(SetupPartOutcome.Unchanged, result.Parts.Single(p => p.Step == SetupStep.Team).Outcome);
        Assert.False(result.RosterChanged);
        Assert.Equal(result.RosterHashBefore, result.RosterHashAfter);
        Assert.Equal(0, rig.Roster.Saves);
        Assert.Empty(rig.Recorder.Entries);
    }

    [Fact]
    public async Task Changing_the_model_or_the_effort_changes_the_hash()
    {
        var rig = new Rig();
        var proposal = await rig.Service().ProposeAsync(SetupFakes.Project, null, Ct);
        var executor = proposal.Models.Single(m => m.Agent.Id.Value == "ejecutor");
        var changed = SetupProposer.WithRecommendation(executor.Agent, executor.Recommendation!);

        var result = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection(null, [changed], null, null, null), Ct);

        Assert.True(result.RosterChanged);
        var stored = (await rig.Roster.ListAsync(SetupFakes.Project.ProjectId, Ct)).Single(a => a.Id.Value == "ejecutor");
        Assert.Equal(executor.Recommendation!.Model, stored.Model.Model);
        Assert.Equal(executor.Recommendation.Effort, stored.Effort);
    }

    [Fact]
    public async Task A_template_that_is_not_valid_leaves_the_stored_one_as_it_was()
    {
        var rig = new Rig();
        var good = SetupProposer.Template(SetupSize.Large).Single(a => a.Id.Value == "revisor");
        var bad = good with { Name = " " };

        var result = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection(null, [good, bad], null, null, null), Ct);

        Assert.Equal(SetupPartOutcome.Failed, result.Parts.Single(p => p.Step == SetupStep.Team).Outcome);
        Assert.Equal(0, rig.Roster.Saves);
        Assert.False(result.RosterChanged);
    }

    [Fact]
    public async Task An_agent_given_a_shell_asks_the_barrier_first_and_is_not_stored_without_it()
    {
        var rig = new Rig { Gate = new FakeGate(false) };
        var executor = SetupTeams.Default().Single(a => a.Id.Value == "ejecutor");
        var withShell = executor with { AllowedTools = [.. executor.AllowedTools, "Bash"] };

        var result = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection(null, [withShell], null, null, null), Ct);

        Assert.Equal(PresenceAction.GrantHighRiskCapability, Assert.Single(rig.Gate.Asked).Action);
        Assert.Equal(SetupPartOutcome.Denied, result.Parts.Single(p => p.Step == SetupStep.Team).Outcome);
        Assert.Equal(0, rig.Roster.Saves);
        Assert.Empty(rig.Recorder.Entries);

        rig.Gate = new FakeGate(true);
        var confirmed = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection(null, [withShell], null, null, null), Ct);

        Assert.Equal(SetupPartOutcome.Applied, confirmed.Parts.Single(p => p.Step == SetupStep.Team).Outcome);
        Assert.Equal(1, rig.Roster.Saves);
    }

    [Fact]
    public void The_capabilities_of_the_barrier_are_read_from_the_tools_the_cli_and_the_role()
    {
        var claudeExecutor = SetupTeams.Agent("e", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Edit", "Write"]);
        var codexExecutor = SetupTeams.Agent("c", AgentRole.Executor, ProviderId.CodexCli, VendorId.OpenAI);
        var geminiTester = SetupTeams.Agent("g", AgentRole.Tester, ProviderId.GeminiCli, VendorId.Google);
        var web = SetupTeams.Agent("w", AgentRole.Orchestrator, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "WebFetch", "websearch"]);
        var grants = SetupTeams.Agent("f", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic) with { PathGrants = [new PathGrant("src/", FileAccessLevel.ReadWrite)] };

        Assert.Empty(SetupCapabilities.Of(claudeExecutor));
        Assert.Equal([SetupCapabilities.CodexWrites], SetupCapabilities.Of(codexExecutor));
        Assert.Equal([SetupCapabilities.UnmeasuredCliWrites], SetupCapabilities.Of(geminiTester));
        Assert.Equal([SetupCapabilities.Network], SetupCapabilities.Of(web));
        Assert.Equal([SetupCapabilities.MoreFolders], SetupCapabilities.Of(grants));

        // A capability the agent already had is not granted again; one it did not have is.
        Assert.Empty(SetupCapabilities.Granted([codexExecutor], [codexExecutor]));
        Assert.Equal([(codexExecutor.Id, SetupCapabilities.CodexWrites)], SetupCapabilities.Granted([], [codexExecutor]));
    }

    [Fact]
    public async Task The_profile_asks_the_barrier_and_without_a_gate_composed_it_is_denied()
    {
        var rig = new Rig { GateComposed = false };
        var profile = (await rig.Service().ProposeAsync(SetupFakes.Project, null, Ct)).Verification.Proposed!;

        var result = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection(null, null, profile, null, null), Ct);

        var part = result.Parts.Single(p => p.Step == SetupStep.Verification);
        Assert.Equal(SetupPartOutcome.Denied, part.Outcome);
        Assert.Equal("setup.hello.not-composed", part.Detail!.Code);
        Assert.Equal(0, rig.Profiles.Saves);
        Assert.Empty(rig.Recorder.Entries);
    }

    [Fact]
    public async Task The_profile_is_written_only_when_the_person_is_confirmed_and_the_log_names_its_hash()
    {
        var rig = new Rig { Gate = new FakeGate(false) };
        var profile = (await rig.Service().ProposeAsync(SetupFakes.Project, null, Ct)).Verification.Proposed!;

        var denied = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection(null, null, profile, null, null), Ct);

        Assert.Equal(SetupPartOutcome.Denied, denied.Parts.Single(p => p.Step == SetupStep.Verification).Outcome);
        Assert.Equal(PresenceAction.ChangeVerificationProfile, Assert.Single(rig.Gate.Asked).Action);
        Assert.Equal(profile.ContentHash, rig.Gate.Asked[0].Subject);
        Assert.Null(rig.Profiles.Stored);

        rig.Gate = new FakeGate(true);
        var applied = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection(null, null, profile, null, null), Ct);

        Assert.Equal(SetupPartOutcome.Applied, applied.Parts.Single(p => p.Step == SetupStep.Verification).Outcome);
        Assert.Equal(profile.ContentHash, rig.Profiles.Stored!.ContentHash);
        var payload = Assert.IsType<ProjectSetupAppliedPayload>(Assert.Single(rig.Recorder.Entries).Payload);
        Assert.Equal(profile.ContentHash, payload.VerificationProfileHash);
    }

    [Fact]
    public async Task A_profile_that_is_the_one_in_force_asks_nothing_and_a_file_that_cannot_be_used_is_never_overwritten()
    {
        var rig = new Rig();
        var profile = SetupFakes.Profile();
        rig.Profiles.Stored = profile;

        var same = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection(null, null, profile, null, null), Ct);

        Assert.Equal(SetupPartOutcome.Unchanged, same.Parts.Single(p => p.Step == SetupStep.Verification).Outcome);
        Assert.Empty(rig.Gate.Asked);

        rig.Profiles.Unreadable = true;
        var broken = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection(null, null, SetupFakes.Profile("other"), null, null), Ct);

        Assert.Equal(SetupPartOutcome.Failed, broken.Parts.Single(p => p.Step == SetupStep.Verification).Outcome);
        Assert.Equal(0, rig.Profiles.Saves);
        Assert.Empty(rig.Gate.Asked);
    }

    [Fact]
    public async Task The_brief_is_a_new_version_of_the_person_and_an_identical_text_writes_nothing()
    {
        var rig = new Rig();

        var first = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection("  ## Qué es\nUna calculadora.  ", null, null, null, null), Ct);

        Assert.Equal(SetupPartOutcome.Applied, first.Parts.Single(p => p.Step == SetupStep.Brief).Outcome);
        var payload = Assert.IsType<ProjectSetupAppliedPayload>(Assert.Single(rig.Recorder.Entries).Payload);
        Assert.Equal(rig.Brief.Stored!.Sha256, payload.BriefSha256);

        var again = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection("## Qué es\nUna calculadora.", null, null, null, null), Ct);

        Assert.Equal(SetupPartOutcome.Unchanged, again.Parts.Single(p => p.Step == SetupStep.Brief).Outcome);
        Assert.Equal(1, rig.Brief.Saves);
        Assert.Single(rig.Recorder.Entries);
    }

    [Fact]
    public async Task The_skills_chosen_are_decisions_of_the_person_one_per_agent_and_what_is_active_is_not_repeated()
    {
        var rig = new Rig();
        var choices = new[]
        {
            new SetupSkillChoice(new AgentId("ejecutor"), new SkillId("dotnet-testing")),
            new SetupSkillChoice(new AgentId("orquestador"), new SkillId("planner")),
            new SetupSkillChoice(new AgentId("ejecutor"), new SkillId("dotnet-testing")),
        };

        var result = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection(null, null, null, choices, null), Ct);

        Assert.Equal(SetupPartOutcome.Applied, result.Parts.Single(p => p.Step == SetupStep.Skills).Outcome);
        Assert.Equal(2, rig.Activations.Appended.Count);
        Assert.All(rig.Activations.Appended, a =>
        {
            Assert.Equal(SkillScopeKind.Agent, a.Activation.Scope);
            Assert.True(a.Activation.Enabled);
            Assert.Equal("user", a.DecidedBy);
        });
        var payload = Assert.IsType<ProjectSetupAppliedPayload>(Assert.Single(rig.Recorder.Entries).Payload);
        Assert.True(ContentHash.IsSha256Hex(payload.SkillActivationsSha256));

        var again = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection(null, null, null, choices, null), Ct);

        Assert.Equal(SetupPartOutcome.Unchanged, again.Parts.Single(p => p.Step == SetupStep.Skills).Outcome);
        Assert.Equal(2, rig.Activations.Appended.Count);
    }

    [Fact]
    public async Task The_options_are_checked_saved_and_named_by_their_hash()
    {
        var rig = new Rig();

        var invalid = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection(null, null, null, null, new ProjectRunOptions(0, 3, null)), Ct);

        Assert.Equal(SetupPartOutcome.Failed, invalid.Parts.Single(p => p.Step == SetupStep.Budget).Outcome);
        Assert.Equal(0, rig.Options.Saves);

        var options = new ProjectRunOptions(20, 2, TimeSpan.FromMinutes(30));
        var applied = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection(null, null, null, null, options), Ct);

        Assert.Equal(SetupPartOutcome.Applied, applied.Parts.Single(p => p.Step == SetupStep.Budget).Outcome);
        Assert.Equal(options, rig.Options.Stored);
        var payload = Assert.IsType<ProjectSetupAppliedPayload>(Assert.Single(rig.Recorder.Entries).Payload);
        Assert.Equal(ProjectSetupService.OptionsHash(options), payload.OptionsSha256);

        var run = options.ToRunOptions();
        Assert.Equal((2, 20, TimeSpan.FromMinutes(30)), (run.MaxParallelTasks, run.MaxAgentRuns, run.DefaultTaskTimeout));
    }

    [Fact]
    public async Task One_entry_names_everything_applied_and_a_part_that_was_denied_does_not_stop_the_others()
    {
        var rig = new Rig { Gate = new FakeGate(false) };
        var proposal = await rig.Service().ProposeAsync(SetupFakes.Project, SetupSize.Medium, Ct);

        var result = await rig.Service().ApplyAsync(
            SetupFakes.Project,
            new SetupSelection("Una calculadora.", [.. proposal.Team.Changes.Select(c => c.After)], proposal.Verification.Proposed, null, new ProjectRunOptions(10, 3, null)),
            Ct);

        Assert.Equal(
            [SetupPartOutcome.Applied, SetupPartOutcome.Applied, SetupPartOutcome.Denied, SetupPartOutcome.Skipped, SetupPartOutcome.Applied],
            result.Parts.Select(p => p.Outcome));
        Assert.True(result.Recorded);
        var entry = Assert.Single(rig.Recorder.Entries);
        Assert.Equal(DecisionKind.ProjectSetupApplied, entry.Kind);
        Assert.Equal(ActorKind.User, entry.Actor.Kind);
        var payload = Assert.IsType<ProjectSetupAppliedPayload>(entry.Payload);
        Assert.NotNull(payload.BriefSha256);
        Assert.NotNull(payload.RosterHash);
        Assert.Null(payload.VerificationProfileHash);
        Assert.Null(payload.SkillActivationsSha256);
        Assert.NotNull(payload.OptionsSha256);
        Assert.Equal(DecisionPayload.NoRun, payload.RunId);

        // The payload goes to the log in the serialization of every other one, and reads back the same.
        var json = DecisionPayloadSerializer.Serialize(payload);
        Assert.Equal(payload.RosterHash, JsonDocument.Parse(json).RootElement.GetProperty("rosterHash").GetString());
    }

    [Fact]
    public async Task A_log_that_does_not_verify_leaves_the_writes_done_and_says_the_entry_is_missing()
    {
        var rig = new Rig();
        rig.Recorder.FailWithIntegrity = true;

        var result = await rig.Service().ApplyAsync(SetupFakes.Project, new SetupSelection(null, null, null, null, new ProjectRunOptions(10, 3, null)), Ct);

        Assert.False(result.Recorded);
        Assert.Equal("setup.problem.record-failed", result.RecordProblem!.Code);
        Assert.Equal(1, rig.Options.Saves);
    }
}
