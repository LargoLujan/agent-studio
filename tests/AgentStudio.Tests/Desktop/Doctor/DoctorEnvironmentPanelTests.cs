using System.IO;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Doctor;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Environments;
using AgentStudio.Infrastructure.Environments;
using AgentStudio.Tests.Application.Diagnostics;

namespace AgentStudio.Tests.Desktop.Doctor;

/// <summary>
/// The panel of Auto Doctor over a project of test (a folder in the temp folder, with its <c>global.json</c> and <c>dotnet-tools.json</c>) read by the real reader of the
/// declared environment and set against a computer of test: what the project asks for and does not get is painted in plain Spanish, with its guide to copy and no button that does nothing.
/// No process is started and no application object is created.
/// </summary>
public sealed class DoctorEnvironmentPanelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "as panel " + Guid.NewGuid().ToString("N")[..12]);

    public DoctorEnvironmentPanelTests() => Directory.CreateDirectory(_root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that stays is not a failure of the test.
        }
    }

    private sealed class OpenEverything : IOpenProjects
    {
        public ProjectContext? Find(string projectRoot) => new("p1", projectRoot);
    }

    private static InstalledTools Machine() =>
        new(
            [new InstalledSdk(ToolVersion.Parse("10.0.103"), null)],
            [],
            null,
            null,
            null,
            false,
            null,
            DockerStatus.NotInstalled,
            WslStatus.NotInstalled,
            null);

    private async Task<DoctorViewModel> PanelAsync(string globalJson, string? toolsJson = null)
    {
        File.WriteAllText(Path.Combine(_root, "global.json"), globalJson);
        if (toolsJson is not null)
        {
            Directory.CreateDirectory(Path.Combine(_root, ".config"));
            File.WriteAllText(Path.Combine(_root, ".config", "dotnet-tools.json"), toolsJson);
        }

        // The NuGet cache is a folder of this test, empty: the tools of the project are not in it.
        var cache = Path.Combine(_root, "empty-cache");
        Directory.CreateDirectory(cache);
        var source = new DeclaredEnvironmentCheckSource(new DeclaredEnvironmentReader(), new FixedInventory(Machine()), () => cache);
        var doctor = new DoctorViewModel(
            new DoctorService([source]),
            new ProviderDiagnosticsViewModel(new ProviderDiagnostics([]), TimeProvider.System),
            null,
            new DoctorFixRunner(),
            TimeProvider.System,
            new OpenEverything(),
            new NoActions())
        {
            ProjectRoot = () => _root,
            Navigation = new DoctorNavigation(new HashSet<string> { "setup" }, _ => { }),
        };
        await doctor.RefreshCommand.ExecuteAsync(null);
        return doctor;
    }

    private sealed class NoActions : IDoctorActions
    {
        public void Copy(string text)
        {
        }

        public void OpenPage(Uri page)
        {
        }
    }

    [Fact]
    public async Task A_global_json_that_asks_for_an_sdk_the_computer_does_not_have_is_painted_as_an_error_with_a_guide()
    {
        var doctor = await PanelAsync("""{ "sdk": { "version": "9.0.100", "rollForward": "disable" } }""");

        var section = Assert.Single(doctor.Sections);
        Assert.Equal(DoctorSection.DeclaredEnvironment, section.Section);
        Assert.True(section.IsTable);
        Assert.Equal(DoctorStatus.Error, section.Status);
        var row = Assert.Single(section.Checks);
        Assert.Equal(EnvironmentCheckCodes.DotNetSdk, row.Code);
        Assert.Equal("SDK de .NET que pide el proyecto", row.Title);
        Assert.Equal("El proyecto pide un SDK de .NET que no tienes instalado.", row.Explanation);
        Assert.Equal("✕", row.Glyph);
        Assert.Equal("sdk 9.0.100", row.Declared);
        Assert.StartsWith("global.json", row.Source, StringComparison.Ordinal);
        Assert.Equal("10.0.103", row.Installed);
        Assert.Contains(row.Facts, f => f.Label == "Origen" && f.Value.StartsWith("global.json", StringComparison.Ordinal));

        // The fix is the guide of the closed list: a command to copy and a page, never run by the app.
        Assert.Equal(DoctorFixKind.Guide, row.FixKind);
        Assert.Equal("Cómo arreglarlo", row.FixLabel);
        Assert.Equal("winget install --id Microsoft.DotNet.SDK.10 --exact", row.GuideCommand);
        Assert.False(string.IsNullOrWhiteSpace(row.FixSentence));
    }

    [Fact]
    public async Task What_cannot_be_compared_is_painted_as_not_checked_and_never_as_met()
    {
        var doctor = await PanelAsync("""{ "sdk": { "version": "10.0.100", "paths": [ "./sdk", "$host$" ] } }""");

        var rows = Assert.Single(doctor.Sections).Checks;
        var row = Assert.Single(rows, r => r.Code == EnvironmentCheckCodes.DotNetSdk);
        Assert.Equal(DoctorStatus.Unknown, row.Status);
        Assert.Equal("Sin comprobar", row.StatusName);
        Assert.Equal("?", row.Glyph);
        Assert.Contains("no se puede comparar", row.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rows, r => r.Code == EnvironmentCheckCodes.DotNetSdk && r.Status == DoctorStatus.Ok);
        Assert.Contains(rows, r => r.Code == EnvironmentCheckCodes.DotNetSdkInRepo && r.Status == DoctorStatus.Warning);
    }

    [Fact]
    public async Task The_tools_of_the_project_that_are_not_downloaded_say_so_in_words_and_offer_the_guide_to_restore_them()
    {
        var doctor = await PanelAsync(
            """{ "sdk": { "version": "10.0.100" } }""",
            """{ "version": 1, "isRoot": true, "tools": { "dotnet-ef": { "version": "10.0.0", "commands": [ "dotnet-ef" ] } } }""");

        var row = Assert.Single(Assert.Single(doctor.Sections).Checks, r => r.Code == EnvironmentCheckCodes.DotNetLocalTools);
        Assert.Equal(DoctorStatus.Warning, row.Status);
        Assert.Equal("sin descargar", row.Installed);
        Assert.Contains(row.Facts, f => f.Label == "Instalado" && f.Value == "sin descargar");
        Assert.DoesNotContain(row.Facts, f => f.Value.Contains("NuGet", StringComparison.Ordinal));
        Assert.Equal(DoctorFixKind.Guide, row.FixKind);
        Assert.Equal("dotnet tool restore", row.GuideCommand);
        Assert.Contains("dotnet-tools.json", row.FixSentence, StringComparison.Ordinal);
    }
}
