using System.IO;
using System.Globalization;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Doctor;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Application.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Desktop.Doctor;

/// <summary>The panel of Auto Doctor (docs/20 D-04 and section 6.1): sections, folding, the fixes and what it runs and when.</summary>
public sealed class DoctorViewModelTests
{
    private sealed class RecordingActions : IDoctorActions
    {
        public List<string> Copied { get; } = [];

        public List<Uri> Opened { get; } = [];

        public void Copy(string text) => Copied.Add(text);

        public void OpenPage(Uri page) => Opened.Add(page);
    }

    private sealed class Workspace(bool fails = false) : IProjectWorkspace
    {
        public List<string> Opened { get; } = [];

        public Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken)
        {
            Opened.Add(projectRoot);
            return fails ? throw new IOException("in use") : Task.FromResult(new ProjectContext("p1", projectRoot));
        }
    }

    private sealed class CountingProbe(string id) : IProviderProbe
    {
        public int Runs { get; private set; }

        public ProviderId Provider { get; } = new(id);

        public VendorId Vendor => VendorId.Anthropic;

        public string DisplayName => "Agent";

        public Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            Runs++;
            return Task.FromResult(new ProviderProbeResult(Provider, "Agent", InstallationState.NotInstalled, null, null, AuthenticationState.Unknown, null, []));
        }
    }

    private static DoctorCheck Check(string code, DoctorSection section, DoctorStatus status, string? fix = null, params DoctorFact[] facts) => new(code, section, status, facts, fix);

    private static DoctorViewModel Panel(
        IEnumerable<IDoctorCheckSource> sources,
        TimeProvider? time = null,
        IProjectWorkspace? workspace = null,
        IDoctorActions? actions = null,
        Func<string?, DoctorFix?>? findFix = null,
        IDoctorFixRunner? runner = null,
        ProviderCheckSource? providerSource = null,
        ProviderDiagnosticsViewModel? providers = null)
    {
        var doctor = new DoctorViewModel(
            new DoctorService(sources),
            providers ?? new ProviderDiagnosticsViewModel(new ProviderDiagnostics([]), TimeProvider.System),
            providerSource,
            runner ?? new DoctorFixRunner(),
            time ?? TimeProvider.System,
            workspace,
            actions ?? new RecordingActions(),
            findFix);
        doctor.Navigation = new DoctorNavigation(new HashSet<string> { "setup" }, _ => { });
        return doctor;
    }

    private static ScriptedSource Source(DoctorSection section, params DoctorCheck[] checks) => new(section, _ => checks);

    [Fact]
    public async Task The_sections_come_in_order_with_their_worst_state_and_the_ones_with_problems_open()
    {
        var doctor = Panel(
        [
            Source(DoctorSection.Containers, Check("doctor.docker", DoctorSection.Containers, DoctorStatus.Warning)),
            Source(DoctorSection.Tools, Check("doctor.git", DoctorSection.Tools, DoctorStatus.Ok), Check("doctor.node", DoctorSection.Tools, DoctorStatus.Ok)),
        ]);

        await doctor.RefreshCommand.ExecuteAsync(null);

        Assert.Equal([DoctorSection.Tools, DoctorSection.Containers], doctor.Sections.Select(s => s.Section).ToArray());
        var tools = doctor.Sections[0];
        Assert.Equal(DoctorStatus.Ok, tools.Status);
        Assert.False(tools.IsExpanded);
        Assert.Equal("2 de 2 correctas", tools.Summary);
        Assert.True(doctor.Sections[1].IsExpanded);
        Assert.Equal(DoctorStatus.Warning, doctor.Sections[1].Status);
        Assert.Equal("Info_Doctor_Tools", tools.InfoKey);
    }

    [Fact]
    public async Task A_section_with_no_source_is_not_shown_and_not_applicable_checks_do_not_count()
    {
        var doctor = Panel(
        [
            Source(DoctorSection.Containers, Check("doctor.docker", DoctorSection.Containers, DoctorStatus.NotApplicable), Check("doctor.wsl", DoctorSection.Containers, DoctorStatus.Ok)),
        ]);

        await doctor.RefreshCommand.ExecuteAsync(null);

        var section = Assert.Single(doctor.Sections);
        Assert.Equal("1 de 1 correctas", section.Summary);
        Assert.DoesNotContain(doctor.Sections, s => s.Section == DoctorSection.DeclaredEnvironment);
        Assert.False(doctor.IsEmpty);
    }

    [Fact]
    public async Task A_panel_with_no_check_says_there_is_nothing_after_it_ran_and_not_before()
    {
        var doctor = Panel([]);
        Assert.False(doctor.IsEmpty);
        Assert.True(doctor.NeverChecked);

        await doctor.RefreshCommand.ExecuteAsync(null);

        Assert.True(doctor.IsEmpty);
        Assert.False(doctor.NeverChecked);
    }

    [Fact]
    public async Task The_state_has_a_shape_as_well_as_a_colour()
    {
        var doctor = Panel(
        [
            Source(
                DoctorSection.Tools,
                Check("doctor.git", DoctorSection.Tools, DoctorStatus.Ok),
                Check("doctor.node", DoctorSection.Tools, DoctorStatus.Warning),
                Check("doctor.npm", DoctorSection.Tools, DoctorStatus.Unknown),
                Check("doctor.python", DoctorSection.Tools, DoctorStatus.NotApplicable)),
        ]);

        await doctor.RefreshCommand.ExecuteAsync(null);

        var glyphs = doctor.Sections[0].Checks.Select(c => c.Glyph).ToArray();
        Assert.Equal(["✓", "!", "?", "–"], glyphs);
        Assert.Equal(glyphs.Length, glyphs.Distinct().Count());
        Assert.Equal(["Correcto", "Aviso", "Sin comprobar", "No aplica"], doctor.Sections[0].Checks.Select(c => c.StatusName).ToArray());
    }

    [Fact]
    public async Task A_check_has_its_title_its_sentence_with_its_facts_and_its_details_in_the_language_in_use()
    {
        var node = Check("doctor.node", DoctorSection.Tools, DoctorStatus.Ok, null, new DoctorFact(DoctorFactNames.Version, "24.14.0"), new DoctorFact(DoctorFactNames.Path, @"C:\Program Files\nodejs\node.exe"));
        var spanish = Panel([Source(DoctorSection.Tools, node)]);
        var english = Panel([Source(DoctorSection.Tools, node)]);

        await spanish.RefreshCommand.ExecuteAsync(null);
        using (Texts.Scope(new ResourceTextProvider(CultureInfo.GetCultureInfo("en"))))
        {
            await english.RefreshCommand.ExecuteAsync(null);
        }

        var es = spanish.Sections[0].Checks[0];
        var en = english.Sections[0].Checks[0];
        Assert.Equal("Node", es.Title);
        Assert.Equal("Node 24.14.0 está instalado.", es.Explanation);
        Assert.Equal("Node 24.14.0 is installed.", en.Explanation);
        Assert.Equal(["Versión", "Ubicación"], es.Facts.Select(f => f.Label).ToArray());
        Assert.Equal(["Version", "Location"], en.Facts.Select(f => f.Label).ToArray());
        Assert.Contains("Node", es.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_check_of_another_package_with_no_text_yet_is_named_by_what_it_declares_and_never_by_a_missing_key()
    {
        var check = Check("doctor.env.sample", DoctorSection.DeclaredEnvironment, DoctorStatus.Ok, null, new DoctorFact(DoctorFactNames.Declared, "10.0.303"), new DoctorFact(DoctorFactNames.Source, "global.json:3"), new DoctorFact(DoctorFactNames.Installed, "10.0.303"));
        var withProject = Panel([Source(DoctorSection.DeclaredEnvironment, check)], workspace: new Workspace());
        withProject.ProjectRoot = () => @"C:\x";

        await withProject.RefreshCommand.ExecuteAsync(null);

        var row = withProject.Sections[0].Checks[0];
        Assert.Equal("10.0.303", row.Title);
        Assert.Equal(("10.0.303", "global.json:3", "10.0.303"), (row.Declared, row.Source, row.Installed));
        Assert.DoesNotContain("[", row.Explanation, StringComparison.Ordinal);
        Assert.True(withProject.Sections[0].IsTable);
    }

    [Fact]
    public async Task The_probes_of_the_providers_run_once_per_run_and_the_rows_of_today_get_the_same_results()
    {
        var probe = new CountingProbe("claude-code");
        var diagnostics = new ProviderDiagnostics([probe]);
        var providerSource = new ProviderCheckSource(diagnostics);
        var providers = new ProviderDiagnosticsViewModel(diagnostics, TimeProvider.System);
        var doctor = Panel([providerSource], providerSource: providerSource, providers: providers);

        await doctor.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(1, probe.Runs);
        var row = Assert.Single(providers.Providers);
        Assert.Equal("Agent", row.Name);
        Assert.NotNull(providers.LastCheckedAt);
        Assert.Equal(DoctorSection.Providers, doctor.Sections[0].Section);
        Assert.True(doctor.Sections[0].IsProviders);
    }

    [Fact]
    public async Task The_providers_alone_still_work_when_the_panel_has_no_service_of_checks()
    {
        var probe = new CountingProbe("claude-code");
        var providers = new ProviderDiagnosticsViewModel(new ProviderDiagnostics([probe]), TimeProvider.System);
        var doctor = new DoctorViewModel(null, providers, null, null, TimeProvider.System);

        await doctor.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(1, probe.Runs);
        Assert.Single(providers.Providers);
        var section = Assert.Single(doctor.Sections);
        Assert.True(section.IsProviders);
        Assert.NotNull(doctor.LastCheckedAt);
    }

    [Fact]
    public async Task The_project_is_checked_only_when_one_is_open_and_one_that_cannot_be_opened_is_said()
    {
        var project = new ScriptedSource(DoctorSection.Project, p => p is null ? [] : [Check("doctor.project.repository", DoctorSection.Project, DoctorStatus.Ok)]);
        var workspace = new Workspace();
        var doctor = Panel([project], workspace: workspace);

        await doctor.RefreshCommand.ExecuteAsync(null);
        Assert.Empty(doctor.Sections);
        Assert.Empty(workspace.Opened);

        doctor.ProjectRoot = () => @"C:\work\calc";
        await doctor.RefreshCommand.ExecuteAsync(null);
        Assert.Single(doctor.Sections);
        Assert.Equal([@"C:\work\calc"], workspace.Opened.ToArray());
        Assert.Null(doctor.Notice);

        var failing = Panel([project], workspace: new Workspace(fails: true));
        failing.ProjectRoot = () => @"C:\work\calc";
        await failing.RefreshCommand.ExecuteAsync(null);
        Assert.Empty(failing.Sections);
        Assert.NotNull(failing.Notice);
    }

    [Fact]
    public async Task A_fix_that_goes_to_a_screen_is_offered_only_when_the_shell_can_open_it_and_opens_it()
    {
        var opened = new List<string>();
        var warning = Check("doctor.project.setup", DoctorSection.Project, DoctorStatus.Warning, DoctorFixCatalog.OpenSetup);
        var doctor = Panel([Source(DoctorSection.Project, warning)], workspace: new Workspace());
        doctor.ProjectRoot = () => @"C:\x";
        doctor.Navigation = new DoctorNavigation(new HashSet<string> { "setup" }, opened.Add);
        var without = Panel([Source(DoctorSection.Project, warning)], workspace: new Workspace());
        without.ProjectRoot = () => @"C:\x";
        without.Navigation = new DoctorNavigation(new HashSet<string>(), opened.Add);

        await doctor.RefreshCommand.ExecuteAsync(null);
        await without.RefreshCommand.ExecuteAsync(null);

        var go = doctor.Sections[0].Checks[0];
        Assert.Equal(DoctorFixKind.Go, go.FixKind);
        Assert.Equal("Ir a…", go.FixLabel);
        Assert.Equal(DoctorFixKind.None, without.Sections[0].Checks[0].FixKind);
        Assert.False(without.Sections[0].Checks[0].HasFix);
        go.RunFixCommand.Execute(null);
        Assert.Equal(["setup"], opened.ToArray());
    }

    [Fact]
    public async Task A_guide_shows_its_card_copies_the_command_and_opens_the_page_but_never_runs_anything()
    {
        var actions = new RecordingActions();
        var missing = Check("doctor.git", DoctorSection.Tools, DoctorStatus.Error, DoctorFixCatalog.InstallGit);
        var doctor = Panel([Source(DoctorSection.Tools, missing)], actions: actions);

        await doctor.RefreshCommand.ExecuteAsync(null);

        var check = doctor.Sections[0].Checks[0];
        Assert.Equal(DoctorFixKind.Guide, check.FixKind);
        Assert.Equal("Cómo arreglarlo", check.FixLabel);
        Assert.False(check.IsGuideOpen);
        check.RunFixCommand.Execute(null);
        Assert.True(check.IsGuideOpen);
        Assert.Equal("winget install Git.Git", check.GuideCommand);
        Assert.Equal("Instala Git con la orden de abajo, o desde la página oficial.", check.FixSentence);
        check.CopyCommandCommand.Execute(null);
        check.OpenPageCommand.Execute(null);
        Assert.Equal(["winget install Git.Git"], actions.Copied.ToArray());
        Assert.Equal([new Uri("https://git-scm.com/downloads")], actions.Opened.ToArray());
        check.RunFixCommand.Execute(null);
        Assert.False(check.IsGuideOpen);
    }

    [Fact]
    public async Task The_guide_of_a_provider_names_the_program_in_its_sentence()
    {
        var check = Check("doctor.provider.codex-cli.install", DoctorSection.Providers, DoctorStatus.Warning, DoctorFixCatalog.ProviderInstallFixCode("codex-cli"), new DoctorFact("name", "Codex"));
        var doctor = Panel([Source(DoctorSection.Providers, check)]);

        await doctor.RefreshCommand.ExecuteAsync(null);

        var item = doctor.Sections[0].Checks[0];
        Assert.Equal("Instala Codex con la orden de abajo y vuelve a comprobar.", item.FixSentence);
        Assert.Equal("Instalación de Codex", item.Title);
        Assert.Equal("npm install -g @openai/codex", item.GuideCommand);
        Assert.Single(doctor.Sections[0].ActionChecks);
    }

    [Fact]
    public async Task A_fix_that_applies_goes_through_the_runner_shows_its_result_and_can_be_undone()
    {
        var state = new[] { 1 };
        var recorder = new LogRecorder();
        var fix = new DoctorFix.Reversible("doctor.fix.test-increment", (_, _) => { state[0]++; return Task.CompletedTask; }, (_, _) => { state[0]--; return Task.CompletedTask; });
        var check = Check("doctor.project.setup", DoctorSection.Project, DoctorStatus.Warning, "doctor.fix.test-increment");
        var doctor = Panel([Source(DoctorSection.Project, check)], workspace: new Workspace(), findFix: code => code == fix.Code ? fix : null, runner: new DoctorFixRunner(recorder));
        doctor.ProjectRoot = () => @"C:\x";
        await doctor.RefreshCommand.ExecuteAsync(null);
        var item = doctor.Sections[0].Checks[0];

        Assert.Equal(DoctorFixKind.Apply, item.FixKind);
        Assert.Equal("Aplicar", item.FixLabel);
        await item.RunFixCommand.ExecuteAsync(null);
        Assert.Equal(2, state[0]);
        Assert.Equal("Hecho.", item.FixMessage);
        Assert.True(item.CanUndo);
        await item.UndoCommand.ExecuteAsync(null);

        Assert.Equal(1, state[0]);
        Assert.Equal("Deshecho.", item.FixMessage);
        Assert.False(item.CanUndo);
        Assert.Equal(2, recorder.Recorded.Count);
    }

    [Fact]
    public async Task The_panel_checks_when_it_opens_and_again_only_after_a_minute()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
        var source = Source(DoctorSection.Tools, Check("doctor.git", DoctorSection.Tools, DoctorStatus.Ok));
        var doctor = Panel([source], time);

        await doctor.OpenedAsync();
        await doctor.OpenedAsync();
        time.Advance(TimeSpan.FromSeconds(30));
        await doctor.OpenedAsync();
        Assert.Equal(1, source.Calls);

        time.Advance(TimeSpan.FromSeconds(31));
        await doctor.OpenedAsync();

        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task A_failing_source_shows_one_error_check_and_a_failure_of_the_service_shows_its_message_without_breaking()
    {
        var doctor = Panel([new ThrowingSource(DoctorSection.Tools)]);

        await doctor.RefreshCommand.ExecuteAsync(null);

        var check = Assert.Single(doctor.Sections[0].Checks);
        Assert.Equal(DoctorStatus.Error, check.Status);
        Assert.Equal("Una comprobación falló", check.Title);
        Assert.DoesNotContain("Secret", check.Explanation, StringComparison.Ordinal);

        var broken = new DoctorViewModel(new BrokenService(), new ProviderDiagnosticsViewModel(new ProviderDiagnostics([]), TimeProvider.System), null, null, TimeProvider.System);
        await broken.RefreshCommand.ExecuteAsync(null);
        Assert.NotNull(broken.ErrorMessage);
        Assert.False(broken.IsBusy);
    }

    private sealed class BrokenService : IDoctorService
    {
        public Task<DoctorReport> RunAsync(ProjectContext? project, CancellationToken ct) => throw new InvalidOperationException("broken");
    }
}
