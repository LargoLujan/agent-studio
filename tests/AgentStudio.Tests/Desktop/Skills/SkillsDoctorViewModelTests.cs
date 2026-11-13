using System.IO;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Skills;
using AgentStudio.Desktop.Skills;
using AgentStudio.Domain.Skills;
using FakeSecrets = AgentStudio.Tests.Security.FakeSecrets;

namespace AgentStudio.Tests.Desktop.Skills;

/// <summary>
/// The doctor of skills in the screen (docs/20 D-13 to D-15, F6-6 and F6-13): nothing is asked when it opens, the button needs the permission, a batch
/// is laid over the local health, no text of an error reaches the screen, the access key goes in as an argument and is never shown, and the scripts are
/// marked as reviewed. Doubles only: no request leaves the process.
/// </summary>
public sealed class SkillsDoctorViewModelTests
{
    private const string Sentinel = "sentinel-text-that-must-not-reach-the-screen";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<SkillsDocumentViewModel> OpenAsync(SkillsWorld world, SkillsBackend backend)
    {
        var document = world.Document(backend);
        await document.OpenAsync(Ct);
        return document;
    }

    private static SkillsBackend WithDoctor(SkillsWorld world, FakeDoctor doctor, FakeNetworkSetting network, FakeTokens? tokens = null) =>
        world.Backend() with { Doctor = doctor, NetworkSetting = network, Tokens = tokens };

    [Fact]
    public async Task Opening_the_screen_asks_nothing_and_the_button_cannot_be_pressed_with_the_permission_off()
    {
        var world = new SkillsWorld();
        var doctor = new FakeDoctor();
        var document = await OpenAsync(world, WithDoctor(world, doctor, new FakeNetworkSetting(false)));

        Assert.Equal(0, doctor.Calls);
        Assert.False(document.IsNetworkAllowed);
        Assert.False(document.CheckOnGitHubCommand.CanExecute(null));
        Assert.Contains("casilla", document.DoctorUnavailable, StringComparison.Ordinal);
        Assert.Contains("Todavía no", document.DoctorStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_checker_the_screen_says_so_instead_of_pretending()
    {
        var world = new SkillsWorld();
        var document = await OpenAsync(world, world.Backend());

        Assert.False(document.CheckOnGitHubCommand.CanExecute(null));
        Assert.Contains("no trae el comprobador", document.DoctorUnavailable, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turning_the_permission_on_keeps_it_and_enables_the_button_but_asks_nothing_yet()
    {
        var world = new SkillsWorld();
        var doctor = new FakeDoctor();
        var network = new FakeNetworkSetting(false);
        var document = await OpenAsync(world, WithDoctor(world, doctor, network));

        document.IsNetworkAllowed = true;

        Assert.True(network.NetworkAllowed);
        Assert.True(document.CheckOnGitHubCommand.CanExecute(null));
        Assert.Null(document.DoctorUnavailable);
        Assert.Equal(0, doctor.Calls);
    }

    [Fact]
    public async Task Reading_the_stored_permission_does_not_write_it_back()
    {
        var world = new SkillsWorld();
        var network = new FakeNetworkSetting(true);

        var document = await OpenAsync(world, WithDoctor(world, new FakeDoctor(), network));

        Assert.True(document.IsNetworkAllowed);
        Assert.Equal(0, network.Writes);
    }

    [Fact]
    public async Task Pressing_the_button_checks_every_skill_once_and_lays_the_result_over_the_health()
    {
        var world = new SkillsWorld();
        var skill = world.Catalog[0].Entry;
        var doctor = new FakeDoctor
        {
            Answer = entries => new SkillHealthBatchResult(
                [.. entries.Select(e => new SkillHealthBatchEntry(
                    e.Id,
                    new SkillHealthReport([new(SkillHealthCodes.Upstream, SkillHealthStatus.Warning, "Actualización por revisar."), new(SkillHealthCodes.Archived, SkillHealthStatus.Healthy, "No archivado.")]),
                    null))],
                false,
                null),
        };
        var document = await OpenAsync(world, WithDoctor(world, doctor, new FakeNetworkSetting(true)));

        await document.CheckOnGitHubCommand.ExecuteAsync(null);

        Assert.Equal(1, doctor.Calls);
        Assert.Equal(world.Catalog.Count, doctor.Asked.Count);
        document.Sectors.SelectMany(s => s.Skills).Single(i => i.Id == skill.Id.Value).IsSelected = true;
        var upstream = Assert.Single(document.Detail!.Checks, c => c.Code == SkillHealthCodes.Upstream);
        Assert.Equal(SkillHealthStatus.Warning, upstream.Status);
        Assert.Equal("Actualización por revisar.", upstream.Message);
        Assert.Contains("Comprobado a las", document.DoctorStatus, StringComparison.Ordinal);
        Assert.False(document.IsChecking);
    }

    [Fact]
    public async Task A_batch_that_went_without_the_token_because_the_presence_was_denied_says_so()
    {
        var world = new SkillsWorld();
        var doctor = new FakeDoctor { Answer = entries => new SkillHealthBatchResult([.. entries.Select(e => new SkillHealthBatchEntry(e.Id, new SkillHealthReport([]), null))], false, null, SkillsDoctorTokenState.PresenceDenied) };
        var document = await OpenAsync(world, WithDoctor(world, doctor, new FakeNetworkSetting(true)));

        await document.CheckOnGitHubCommand.ExecuteAsync(null);

        Assert.Contains("No se confirmó tu presencia", document.DoctorStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task At_the_limit_the_screen_says_when_it_resets()
    {
        var world = new SkillsWorld();
        var resets = new DateTimeOffset(2026, 11, 13, 14, 30, 0, TimeSpan.Zero);
        var doctor = new FakeDoctor { Answer = entries => new SkillHealthBatchResult([.. entries.Select(e => new SkillHealthBatchEntry(e.Id, new SkillHealthReport([]), SkillHealthFailure.RateLimited))], false, resets) };
        var document = await OpenAsync(world, WithDoctor(world, doctor, new FakeNetworkSetting(true)));

        await document.CheckOnGitHubCommand.ExecuteAsync(null);

        Assert.Contains("límite de consultas", document.DoctorStatus, StringComparison.Ordinal);
        Assert.Contains(resets.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture), document.DoctorStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_text_of_an_exception_never_reaches_the_screen()
    {
        var world = new SkillsWorld();
        var doctor = new FakeDoctor { Answer = _ => throw new InvalidOperationException(Sentinel) };
        var document = await OpenAsync(world, WithDoctor(world, doctor, new FakeNetworkSetting(true)));

        await document.CheckOnGitHubCommand.ExecuteAsync(null);

        Assert.Contains("No se pudo comprobar", document.DoctorStatus, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, document.DoctorStatus, StringComparison.Ordinal);
        Assert.False(document.IsChecking);
    }

    [Fact]
    public async Task The_access_key_goes_in_as_an_argument_and_is_never_shown_again()
    {
        var key = FakeSecrets.GitHubFineGrainedToken();
        var world = new SkillsWorld();
        var tokens = new FakeTokens();
        var document = await OpenAsync(world, WithDoctor(world, new FakeDoctor(), new FakeNetworkSetting(false), tokens));
        Assert.Contains("No hay ninguna", document.TokenStoredText, StringComparison.Ordinal);

        await document.SaveTokenAsync("  " + key + "  ");

        Assert.Equal(key, tokens.Stored!.Reveal());
        Assert.True(document.HasToken);
        Assert.Contains("Hay una clave", document.TokenStoredText, StringComparison.Ordinal);
        Assert.Equal("Clave guardada.", document.TokenStatus);
        Assert.DoesNotContain(key, string.Join('\n', document.TokenStoredText, document.TokenStatus, document.DoctorStatus), StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(SkillsDocumentViewModel).GetProperties(), p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) || p.Name is "Token" or "AccessKey");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has a space")]
    [InlineData("with\nnewline")]
    public async Task A_text_that_is_not_a_key_is_refused_before_Hello_is_asked(string text)
    {
        var world = new SkillsWorld();
        var tokens = new FakeTokens();
        var document = await OpenAsync(world, WithDoctor(world, new FakeDoctor(), new FakeNetworkSetting(false), tokens));

        await document.SaveTokenAsync(text);

        Assert.Equal(0, tokens.Stores);
        Assert.Contains("no vale", document.TokenStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_presence_nothing_changes_and_it_says_so()
    {
        var world = new SkillsWorld();
        var tokens = new FakeTokens { Change = SkillsDoctorTokenChange.PresenceDenied };
        var document = await OpenAsync(world, WithDoctor(world, new FakeDoctor(), new FakeNetworkSetting(false), tokens));

        await document.SaveTokenAsync(FakeSecrets.GitHubToken());

        Assert.False(document.HasToken);
        Assert.Contains("No se confirmó tu presencia", document.TokenStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_the_key_needs_one_and_forgets_it()
    {
        var world = new SkillsWorld();
        var tokens = new FakeTokens { Has = true };
        var document = await OpenAsync(world, WithDoctor(world, new FakeDoctor(), new FakeNetworkSetting(false), tokens));
        Assert.True(document.DeleteTokenCommand.CanExecute(null));

        await document.DeleteTokenCommand.ExecuteAsync(null);

        Assert.False(document.HasToken);
        Assert.Equal("Clave borrada.", document.TokenStatus);
        Assert.False(document.DeleteTokenCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_failure_storing_the_key_does_not_show_the_text_of_the_exception()
    {
        var world = new SkillsWorld();
        var tokens = new FakeTokens { Throw = new InvalidOperationException(Sentinel) };
        var document = await OpenAsync(world, WithDoctor(world, new FakeDoctor(), new FakeNetworkSetting(false), tokens));

        await document.SaveTokenAsync(FakeSecrets.GitHubToken());

        Assert.DoesNotContain(Sentinel, document.TokenStatus, StringComparison.Ordinal);
        Assert.Contains("No se pudo", document.TokenStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_scripts_of_a_skill_are_listed_and_marking_them_reviewed_is_kept()
    {
        var world = new SkillsWorld();
        var id = world.Catalog[0].Entry.Id;
        var inspector = new FakeInspector { [world.Catalog[0].LocalPath!] = new SkillScriptsInfo(new string('a', 64), ["run.ps1", "lib/x.py"]) };
        var reviews = new FakeReviews();
        var backend = world.Backend() with { ScriptsInspector = inspector, ScriptReviews = reviews };
        var document = await OpenAsync(world, backend);
        document.Sectors.SelectMany(s => s.Skills).Single(i => i.Id == id.Value).IsSelected = true;

        Assert.Equal(["run.ps1", "lib/x.py"], document.Detail!.ScriptFiles);
        Assert.True(document.Detail.CanMarkScripts);
        Assert.Null(document.Detail.ScriptsReviewedText);

        await document.Detail.MarkScriptsReviewedCommand!.ExecuteAsync(null);

        Assert.Equal(new string('a', 64), reviews.Marked[id]);
        Assert.False(document.Detail!.CanMarkScripts);
        Assert.Contains("Ya marcaste", document.Detail.ScriptsReviewedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scripts_folder_too_big_to_read_is_listed_but_never_markable_nor_shown_as_reviewed()
    {
        var world = new SkillsWorld();
        var id = world.Catalog[0].Entry.Id;
        var inspector = new FakeInspector { [world.Catalog[0].LocalPath!] = new SkillScriptsInfo(new string('b', 64), ["big.bin"], Reviewable: false) };
        var document = await OpenAsync(world, world.Backend() with { ScriptsInspector = inspector, ScriptReviews = new FakeReviews() });
        document.Sectors.SelectMany(s => s.Skills).Single(i => i.Id == id.Value).IsSelected = true;

        Assert.Equal(["big.bin"], document.Detail!.ScriptFiles);
        Assert.False(document.Detail.CanMarkScripts);
        Assert.Contains("demasiado grande", document.Detail.ScriptsReviewedText, StringComparison.Ordinal);
        Assert.DoesNotContain("Ya marcaste", document.Detail.ScriptsReviewedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_coded_check_is_painted_from_its_resources_and_a_plain_one_keeps_its_message()
    {
        var world = new SkillsWorld();
        var doctor = new FakeDoctor
        {
            Answer = entries => new SkillHealthBatchResult(
                [.. entries.Select(e => new SkillHealthBatchEntry(e.Id, new SkillHealthReport([SkillHealthCheck.Coded(SkillHealthCodes.Upstream, SkillHealthStatus.Warning, SkillsUserTexts.DoctorUpdateToReview)]), null))],
                false,
                null),
        };
        var document = await OpenAsync(world, WithDoctor(world, doctor, new FakeNetworkSetting(true)));

        await document.CheckOnGitHubCommand.ExecuteAsync(null);
        document.Sectors.SelectMany(s => s.Skills).First().IsSelected = true;

        var upstream = Assert.Single(document.Detail!.Checks, c => c.Code == SkillHealthCodes.Upstream);
        Assert.StartsWith("Actualización por revisar", upstream.Message, StringComparison.Ordinal);
        Assert.NotEqual(SkillsUserTexts.DoctorUpdateToReview, upstream.Message);
        Assert.Contains(document.Detail.Checks, c => c.Code == SkillHealthCodes.License && c.Message == "Licencia permisiva: MIT.");
    }

    [Fact]
    public async Task The_button_cancels_the_batch_in_progress_and_the_skills_left_stay_unchecked()
    {
        var world = new SkillsWorld();
        var doctor = new BlockingDoctor();
        var document = await OpenAsync(world, world.Backend() with { Doctor = doctor, NetworkSetting = new FakeNetworkSetting(true) });
        Assert.False(document.CancelCheckCommand.CanExecute(null));

        var run = document.CheckOnGitHubCommand.ExecuteAsync(null);
        await doctor.Started.Task.WaitAsync(Ct);
        Assert.True(document.IsChecking);
        Assert.True(document.CancelCheckCommand.CanExecute(null));
        document.CancelCheckCommand.Execute(null);
        await run;

        Assert.False(document.IsChecking);
        Assert.Contains("cancelada", document.DoctorStatus, StringComparison.Ordinal);
        Assert.False(document.CancelCheckCommand.CanExecute(null));
        Assert.True(document.CheckOnGitHubCommand.CanExecute(null));
    }

    [Fact]
    public async Task Closing_the_screen_cancels_the_batch()
    {
        var world = new SkillsWorld();
        var doctor = new BlockingDoctor();
        var document = await OpenAsync(world, world.Backend() with { Doctor = doctor, NetworkSetting = new FakeNetworkSetting(true) });

        var run = document.CheckOnGitHubCommand.ExecuteAsync(null);
        await doctor.Started.Task.WaitAsync(Ct);
        var closed = (MulticastDelegate)typeof(AgentStudio.Desktop.ViewModels.DocumentViewModel).GetField("Closed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(document)!;
        closed.DynamicInvoke(document, EventArgs.Empty);
        await run;

        Assert.False(document.IsChecking);
        Assert.Contains("cancelada", document.DoctorStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_token_is_said_in_the_summary()
    {
        var world = new SkillsWorld();
        var doctor = new FakeDoctor { Answer = entries => new SkillHealthBatchResult([.. entries.Select(e => new SkillHealthBatchEntry(e.Id, new SkillHealthReport([]), SkillHealthFailure.InvalidToken))], false, null) };
        var document = await OpenAsync(world, WithDoctor(world, doctor, new FakeNetworkSetting(true)));

        await document.CheckOnGitHubCommand.ExecuteAsync(null);

        Assert.Contains("no aceptó la clave de acceso", document.DoctorStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_script_is_a_link_that_opens_it_in_the_editor_and_nothing_else_does()
    {
        var world = new SkillsWorld();
        var folder = Path.Combine(Path.GetTempPath(), "as open " + Guid.NewGuid().ToString("N")[..10], "skill");
        Directory.CreateDirectory(Path.Combine(folder, "scripts", "lib"));
        File.WriteAllText(Path.Combine(folder, "scripts", "run.ps1"), "x");
        try
        {
            world.Catalog[0] = world.Catalog[0] with { LocalPath = folder };
            var inspector = new FakeInspector { [folder] = new SkillScriptsInfo(new string('a', 64), ["run.ps1", "lib/gone.py", "../../escape.txt"]) };
            var opened = new List<string>();
            var backend = world.Backend() with { ScriptsInspector = inspector, ScriptReviews = new FakeReviews() };
            var document = new SkillsDocumentViewModel(SkillsScreenSamples.ProjectRoot, backend, TimeProvider.System, (_, _) => world.ProjectFiles, path => { opened.Add(path); return Task.CompletedTask; });
            await document.OpenAsync(Ct);
            document.Sectors.SelectMany(s => s.Skills).Single(i => i.Id == world.Catalog[0].Entry.Id.Value).IsSelected = true;

            Assert.True(document.Detail!.HasScriptLinks);
            await document.Detail.ScriptLinks[0].OpenCommand.ExecuteAsync(null);
            Assert.Equal([Path.Combine(folder, "scripts", "run.ps1")], opened);

            // A script that is gone, or a path that climbs out of scripts/, is not opened: the screen says so.
            await document.Detail.ScriptLinks[1].OpenCommand.ExecuteAsync(null);
            await document.Detail.ScriptLinks[2].OpenCommand.ExecuteAsync(null);
            Assert.Single(opened);
            Assert.Contains("No se pudo abrir", document.ActivationProblem, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    [Fact]
    public async Task Without_a_way_to_open_files_the_scripts_are_listed_but_not_linked()
    {
        var world = new SkillsWorld();
        var document = await OpenAsync(world, world.DoctorBackend());
        document.Sectors.SelectMany(s => s.Skills).First().IsSelected = true;

        Assert.True(document.Detail!.HasScripts);
        Assert.False(document.Detail.HasScriptLinks);
        Assert.Empty(document.Detail.ScriptLinks);
    }

    [Fact]
    public async Task A_skill_without_scripts_shows_no_scripts_section()
    {
        var world = new SkillsWorld();
        var document = await OpenAsync(world, world.Backend() with { ScriptsInspector = new FakeInspector(), ScriptReviews = new FakeReviews() });
        document.Sectors.SelectMany(s => s.Skills).First().IsSelected = true;

        Assert.False(document.Detail!.HasScripts);
        Assert.False(document.Detail.CanMarkScripts);
    }
}
