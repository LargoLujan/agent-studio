using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Environments;
using AgentStudio.Domain.Team;
using AgentStudio.Tests.Application.Setup;
using AgentStudio.Tests.Debate.Audit.Fixtures;
using AgentStudio.Tests.Desktop.RepositoryScreen;
using AgentStudio.Tests.Infrastructure.Diagnostics;

namespace AgentStudio.Tests.Application.Diagnostics;

internal sealed class LogRecorder : IDecisionRecorder
{
    public List<DecisionEntry> Entries { get; } = [];

    public DecisionLogVerification Verification { get; set; } = DecisionLogVerification.Valid;

    public Exception? LoadFailure { get; set; }

    public List<(DecisionKind Kind, DecisionPayload Payload, string Summary)> Recorded { get; } = [];

    public int Loads { get; private set; }

    public Task<DecisionLogState> LoadAsync(string projectId, CancellationToken cancellationToken)
    {
        Loads++;
        return LoadFailure is { } failure ? Task.FromException<DecisionLogState>(failure) : Task.FromResult(new DecisionLogState(Entries, Verification));
    }

    public Task<DecisionLogState> LoadAsync(string projectId, LogLoad load, CancellationToken cancellationToken) => LoadAsync(projectId, cancellationToken);

    public Task<DecisionEntry> RecordAsync(string projectId, Actor actor, DecisionKind kind, string summary, DecisionPayload payload, long? corrects = null, CancellationToken cancellationToken = default)
    {
        Recorded.Add((kind, payload, summary));
        var entry = new DecisionEntry(Entries.Count + 1, DateTimeOffset.UnixEpoch, actor, kind, summary, "{}", corrects, new string('0', 64), new string('1', 64));
        Entries.Add(entry);
        return Task.FromResult(entry);
    }

    public static DecisionEntry Entry(DecisionKind kind, long sequence = 1) =>
        new(sequence, DateTimeOffset.UnixEpoch, new Actor(ActorKind.User, "person"), kind, "entry", "{}", null, new string('0', 64), new string('1', 64));
}

internal sealed class FixedInventory(InstalledTools tools) : IInstalledToolInventory
{
    public Task<InstalledTools> ReadAsync(string? projectRoot, CancellationToken ct) => Task.FromResult(tools);
}

/// <summary>The section «Proyecto» (docs/20 D-03, F6-1): one test per check, over fakes of the ports.</summary>
public sealed class ProjectCheckSourceTests
{
    private static readonly ProjectContext Project = new("p1", @"C:\work\calc");

    private sealed class Rig
    {
        public AuditRepository Git { get; } = new("a".PadRight(40, 'a'));

        public FakeRemotes Remotes { get; } = new();

        public LogRecorder Log { get; } = new();

        public FakeProfiles Profiles { get; } = new();

        public FakeEnvironmentProbe Environment { get; } = new();

        public InstalledTools Installed { get; set; } = InstalledTools.None;

        public async Task<IReadOnlyDictionary<string, DoctorCheck>> RunAsync() =>
            (await new ProjectCheckSource(Git, Remotes, Log, Profiles, new FixedInventory(Installed), Environment).CheckAsync(Project, CancellationToken.None))
            .ToDictionary(c => c.Code);
    }

    private static InstalledTool Tool(string version) => new(ToolVersion.Parse(version), null);

    private static Rig Healthy()
    {
        var rig = new Rig();
        rig.Log.Entries.Add(LogRecorder.Entry(DecisionKind.ProjectSetupApplied));
        rig.Profiles.Stored = SetupFakes.Profile("dotnet");
        rig.Installed = new InstalledTools([new InstalledSdk(ToolVersion.Parse("10.0.303"), null)], [], null, null, null, false, Tool("2.45.2"), DockerStatus.NotInstalled, WslStatus.NotInstalled, null);
        return rig;
    }

    [Fact]
    public async Task A_healthy_project_has_every_check_correct()
    {
        var checks = await Healthy().RunAsync();

        Assert.Equal(
            ["doctor.project.repository", "doctor.project.git-layer", "doctor.project.hooks", "doctor.project.remotes", "doctor.project.decision-log", "doctor.project.setup", "doctor.project.verification-profile", "doctor.project.profile-tools", "doctor.project.path-inside"],
            checks.Keys.ToArray());
        Assert.All(checks.Values, c => Assert.Equal(DoctorStatus.Ok, c.Status));
        Assert.All(checks.Values, c => Assert.Equal(DoctorSection.Project, c.Section));
        Assert.Equal("main", checks["doctor.project.git-layer"].Fact("branch"));
        Assert.Equal("1", checks["doctor.project.decision-log"].Fact("entries"));
        Assert.Equal("0", checks["doctor.project.remotes"].Fact("count"));
    }

    [Fact]
    public async Task A_folder_that_is_not_a_repository_is_an_error_and_its_git_checks_do_not_apply()
    {
        var rig = Healthy();
        rig.Environment.Layout = new ProjectLayout(false, false, []);

        var checks = await rig.RunAsync();

        Assert.Equal(DoctorStatus.Error, checks["doctor.project.repository"].Status);
        Assert.Equal(DoctorStatus.NotApplicable, checks["doctor.project.git-layer"].Status);
        Assert.Equal(DoctorStatus.NotApplicable, checks["doctor.project.hooks"].Status);
        Assert.Equal(DoctorStatus.NotApplicable, checks["doctor.project.remotes"].Status);
        Assert.Empty(rig.Remotes.Roots);
    }

    [Fact]
    public async Task A_repository_that_the_hardened_layer_refuses_is_an_error_whose_text_is_not_kept()
    {
        var rig = Healthy();
        rig.Git.InspectFailure = new InvalidOperationException(@"A link planted in C:\work\calc\.git\objects");

        var checks = await rig.RunAsync();

        Assert.Equal(DoctorStatus.Error, checks["doctor.project.git-layer"].Status);
        Assert.Empty(checks["doctor.project.git-layer"].Facts);
    }

    [Fact]
    public async Task Hooks_are_a_warning_with_their_names_and_not_checked_when_the_folder_cannot_be_read()
    {
        var rig = Healthy();
        rig.Environment.Layout = new ProjectLayout(true, true, ["pre-push", "post-merge"]);
        var present = await rig.RunAsync();
        rig.Environment.Layout = new ProjectLayout(true, false, []);
        var unreadable = await rig.RunAsync();

        Assert.Equal(DoctorStatus.Warning, present["doctor.project.hooks"].Status);
        Assert.Equal("2", present["doctor.project.hooks"].Fact("count"));
        Assert.Equal("pre-push", present["doctor.project.hooks"].Fact("hook"));
        Assert.Equal(DoctorStatus.Unknown, unreadable["doctor.project.hooks"].Status);
    }

    [Fact]
    public async Task The_remotes_are_counted_and_a_refusal_of_the_layer_is_not_checked_with_its_code()
    {
        var rig = Healthy();
        rig.Remotes.Listing = new RemoteListing([FakeRemotes.Origin], null);
        var counted = await rig.RunAsync();
        rig.Remotes.Listing = RemoteListing.Refused("unsafe-repository");
        var refused = await rig.RunAsync();

        Assert.Equal("1", counted["doctor.project.remotes"].Fact("count"));
        Assert.Equal(DoctorStatus.Unknown, refused["doctor.project.remotes"].Status);
        Assert.Equal("unsafe-repository", refused["doctor.project.remotes"].Fact("reason"));
    }

    [Fact]
    public async Task A_log_whose_chain_is_broken_is_an_error_and_one_that_cannot_be_read_is_not_checked()
    {
        var rig = Healthy();
        rig.Log.Verification = new DecisionLogVerification(false, 7, "The hash of the entry 7 does not match.");
        var broken = await rig.RunAsync();
        rig.Log.LoadFailure = new IOException("disk");
        var unreadable = await rig.RunAsync();

        Assert.Equal(DoctorStatus.Error, broken["doctor.project.decision-log"].Status);
        Assert.Equal("7", broken["doctor.project.decision-log"].Fact("sequence"));
        Assert.Equal(DoctorStatus.Unknown, unreadable["doctor.project.decision-log"].Status);
        Assert.Equal(DoctorStatus.Unknown, unreadable["doctor.project.setup"].Status);
    }

    [Fact]
    public async Task Without_the_configurator_applied_the_setup_is_a_warning_that_goes_to_the_configurator()
    {
        var rig = Healthy();
        rig.Log.Entries.Clear();

        var setup = (await rig.RunAsync())["doctor.project.setup"];

        Assert.Equal(DoctorStatus.Warning, setup.Status);
        Assert.Equal(DoctorFixCatalog.OpenSetup, setup.FixCode);
        var fix = Assert.IsType<DoctorFix.Navigate>(DoctorFixCatalog.Find(setup.FixCode));
        Assert.Equal("setup", fix.Target);
    }

    [Fact]
    public async Task The_verification_profile_is_correct_missing_or_broken()
    {
        var rig = Healthy();
        var ok = await rig.RunAsync();
        rig.Profiles.Stored = null;
        var missing = await rig.RunAsync();
        rig.Profiles.Unreadable = true;
        var broken = await rig.RunAsync();

        Assert.Equal(DoctorStatus.Ok, ok["doctor.project.verification-profile"].Status);
        Assert.Equal(DoctorStatus.Warning, missing["doctor.project.verification-profile"].Status);
        Assert.Equal(DoctorFixCatalog.OpenSetup, missing["doctor.project.verification-profile"].FixCode);
        Assert.Equal(DoctorStatus.NotApplicable, missing["doctor.project.profile-tools"].Status);
        Assert.Equal(DoctorStatus.Error, broken["doctor.project.verification-profile"].Status);
    }

    [Fact]
    public async Task The_tools_the_steps_of_the_profile_use_are_checked_against_what_is_installed()
    {
        var rig = Healthy();
        rig.Installed = InstalledTools.None;

        var tools = (await rig.RunAsync())["doctor.project.profile-tools"];

        Assert.Equal(DoctorStatus.Warning, tools.Status);
        Assert.Equal("dotnet", tools.Fact("tools"));
        Assert.Equal("dotnet", tools.Fact("missing"));
    }

    [Fact]
    public async Task A_step_with_a_program_outside_the_closed_list_is_not_asked_and_is_not_counted()
    {
        var rig = Healthy();
        var step = rig.Profiles.Stored!.Steps[0] with { Executable = @"C:\tools\custom-runner.exe" };
        rig.Profiles.Stored = new VerificationProfile("custom", [step]);

        var tools = (await rig.RunAsync())["doctor.project.profile-tools"];

        Assert.Equal(DoctorStatus.NotApplicable, tools.Status);
    }

    [Fact]
    public async Task The_folders_of_the_path_inside_the_project_are_a_warning_that_names_them()
    {
        var rig = Healthy();
        rig.Environment.Inside = [@"C:\work\calc\node_modules\.bin"];

        var check = (await rig.RunAsync())["doctor.project.path-inside"];

        Assert.Equal(DoctorStatus.Warning, check.Status);
        Assert.Equal(@"C:\work\calc\node_modules\.bin", check.Fact("folder"));
    }

    [Fact]
    public async Task A_project_that_nothing_opened_has_no_log_to_read_so_the_history_and_the_setup_are_not_checked_and_the_rest_is()
    {
        var rig = Healthy();
        var unopened = DoctorProject.Unopened(Project.RootPath);

        var checks = (await new ProjectCheckSource(rig.Git, rig.Remotes, rig.Log, rig.Profiles, new FixedInventory(rig.Installed), rig.Environment).CheckAsync(unopened, CancellationToken.None)).ToDictionary(c => c.Code);

        Assert.Equal(DoctorStatus.Unknown, checks["doctor.project.decision-log"].Status);
        Assert.Equal(DoctorStatus.Unknown, checks["doctor.project.setup"].Status);
        Assert.Equal(DoctorStatus.Ok, checks["doctor.project.repository"].Status);
        Assert.Equal(DoctorStatus.Ok, checks["doctor.project.verification-profile"].Status);
        Assert.Equal(0, rig.Log.Loads);
    }

    [Fact]
    public async Task Without_a_project_there_is_nothing_to_check()
    {
        var rig = Healthy();

        var checks = await new ProjectCheckSource(rig.Git, rig.Remotes, rig.Log, rig.Profiles, new FixedInventory(rig.Installed), rig.Environment).CheckAsync(null, CancellationToken.None);

        Assert.Empty(checks);
    }

    [Fact]
    public async Task Every_fix_a_check_names_is_in_the_closed_list()
    {
        var rig = Healthy();
        rig.Log.Entries.Clear();
        rig.Profiles.Stored = null;

        var checks = await rig.RunAsync();

        Assert.All(checks.Values.Where(c => c.FixCode is not null), c => Assert.NotNull(DoctorFixCatalog.Find(c.FixCode)));
    }
}
