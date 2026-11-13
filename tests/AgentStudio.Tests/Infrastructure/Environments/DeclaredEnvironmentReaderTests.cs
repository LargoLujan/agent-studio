using System.IO;
using System.Text;
using AgentStudio.Domain.Environments;
using AgentStudio.Infrastructure.Environments;
using AgentStudio.Tests.Infrastructure.Persistence;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Infrastructure.Environments;

/// <summary>
/// docs/20 D-05 and D-06 (WP-E, F6-7): the reader of the declared environment over sample repositories of the Fixtures folder and over hostile ones made in a
/// temporary folder: a manifest that is huge, a link that leads out, JSON that is too deep, files outside the closed list. Nothing here touches the profile of the person.
/// </summary>
public sealed class DeclaredEnvironmentReaderTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "as entorno " + Guid.NewGuid().ToString("N")[..12]);
    private readonly PlantedLinks _links = new();
    private readonly DeclaredEnvironmentReader _reader = new();

    public DeclaredEnvironmentReaderTests() => Directory.CreateDirectory(_temp);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _links.Dispose();
        TempFolders.Delete(_temp);
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Environments", "Fixtures", name);

    /// <summary>A new project folder in the temporary one, empty or with a copy of a fixture.</summary>
    private string Project(string? fixture = null)
    {
        var root = Path.Combine(_temp, "proyecto ñ " + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        if (fixture is not null)
        {
            foreach (var file in Directory.EnumerateFiles(Fixture(fixture), "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(root, Path.GetRelativePath(Fixture(fixture), file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        return root;
    }

    private static string Value(DeclaredEnvironment environment, DeclaredKind kind) => Assert.Single(environment.Of(kind)).Value;

    // ---- sample repositories ----

    [Fact]
    public async Task A_dotnet_repository_gives_its_sdk_rule_and_local_tools()
    {
        var environment = await _reader.ReadAsync(Fixture("dotnet-repo"), Ct);

        Assert.Empty(environment.Issues);
        Assert.Equal(["global.json", ".config/dotnet-tools.json"], environment.FilesFound);
        Assert.Equal("10.0.100", Value(environment, DeclaredKind.DotNetSdkVersion));
        Assert.Equal("latestFeature", Value(environment, DeclaredKind.DotNetRollForward));
        Assert.Equal("false", Value(environment, DeclaredKind.DotNetAllowPrerelease));
        Assert.Equal([("dotnet-ef", "10.0.0"), ("cake.tool", "5.0.0")], environment.Of(DeclaredKind.DotNetLocalTool).Select(e => (e.Name, e.Value)));
        Assert.False(environment.DeclaresContainers);
    }

    [Fact]
    public async Task A_node_repository_gives_nvmrc_and_the_engines_and_nothing_of_the_scripts()
    {
        var environment = await _reader.ReadAsync(Fixture("node-repo"), Ct);

        Assert.Empty(environment.Issues);
        Assert.Equal("v22.11.0", Value(environment, DeclaredKind.NodeVersion));
        Assert.Equal(">=22.11.0 <23 || ^24", Value(environment, DeclaredKind.NodeEngine));
        Assert.Equal("^10", Value(environment, DeclaredKind.NpmEngine));
        Assert.Equal(3, environment.Entries.Count);
    }

    [Fact]
    public async Task A_python_repository_gives_the_version_file_requires_python_and_the_venv()
    {
        var environment = await _reader.ReadAsync(Fixture("python-repo"), Ct);

        Assert.Empty(environment.Issues);
        Assert.Equal("3.12.5", Value(environment, DeclaredKind.PythonVersion));
        Assert.Equal(">=3.10,<3.14", Value(environment, DeclaredKind.PythonRequires));
        Assert.Equal("3.12.5", Value(environment, DeclaredKind.VenvVersion));
        Assert.Equal(".venv/pyvenv.cfg", Assert.Single(environment.Of(DeclaredKind.VenvVersion)).Source.File);
    }

    [Fact]
    public async Task A_container_repository_names_its_images_and_features_and_only_says_that_compose_exists()
    {
        var environment = await _reader.ReadAsync(Fixture("container-repo"), Ct);

        Assert.Empty(environment.Issues);
        Assert.True(environment.DeclaresContainers);
        Assert.Equal(
            ["mcr.microsoft.com/devcontainers/dotnet:10.0", "mcr.microsoft.com/dotnet/sdk:10.0", "mcr.microsoft.com/dotnet/aspnet:10.0"],
            environment.Of(DeclaredKind.ContainerImage).Select(e => e.Value));
        Assert.Equal("build", environment.Of(DeclaredKind.ContainerImage).Skip(1).First().Name);
        Assert.Equal("ghcr.io/devcontainers/features/node:1", Value(environment, DeclaredKind.DevContainerFeature));
        var compose = Assert.Single(environment.Of(DeclaredKind.ComposeFile));
        Assert.Equal(new FileInfo(Path.Combine(Fixture("container-repo"), "compose.yaml")).Length.ToString(System.Globalization.CultureInfo.InvariantCulture), compose.Value);
        Assert.Equal("compose.yaml", compose.Source.File);
        Assert.DoesNotContain(environment.Entries, e => e.Value.Contains("8080", StringComparison.Ordinal) || e.Value == "web");
    }

    [Fact]
    public async Task A_repository_with_everything_gives_all_the_files_of_the_list_that_exist_in_the_order_of_the_design()
    {
        var environment = await _reader.ReadAsync(Fixture("everything"), Ct);

        Assert.Empty(environment.Issues);
        Assert.Equal(
            ["global.json", ".config/dotnet-tools.json", ".nvmrc", ".node-version", "package.json", ".python-version", "pyproject.toml", ".venv/pyvenv.cfg", ".devcontainer/devcontainer.json", "compose.yaml", "Dockerfile"],
            environment.FilesFound);
        Assert.Equal(2, environment.Of(DeclaredKind.NodeVersion).Count());
        Assert.True(environment.Entries.Count > 15);
    }

    [Fact]
    public async Task A_folder_without_any_of_the_files_declares_nothing_and_a_missing_folder_too()
    {
        Assert.Equal(DeclaredEnvironment.Empty, await _reader.ReadAsync(Project(), Ct));
        Assert.Equal(DeclaredEnvironment.Empty, await _reader.ReadAsync(Path.Combine(_temp, "no existe"), Ct));
    }

    [Fact]
    public async Task What_is_outside_the_closed_list_is_never_read_not_even_a_global_json_in_a_subfolder_or_an_env_file()
    {
        var root = Project();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "global.json"), "{\"sdk\":{\"version\":\"9.9.9\"}}", Ct);
        await File.WriteAllTextAsync(Path.Combine(root, ".env"), "SECRET=hunter2", Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "package-lock.json"), "{}", Ct);

        var environment = await _reader.ReadAsync(root, Ct);

        Assert.Empty(environment.FilesFound);
        Assert.Empty(environment.Entries);
        Assert.Empty(environment.Issues);
    }

    // ---- hostile files ----

    [Fact]
    public async Task A_manifest_over_256_KB_is_not_read_and_one_exactly_at_the_cap_is()
    {
        var over = Project();
        await File.WriteAllTextAsync(Path.Combine(over, "global.json"), "{\"sdk\":{\"version\":\"1.0.0\"}}" + new string(' ', DeclaredEnvironmentReader.MaxFileBytes), Ct);
        var exact = Project();
        const string body = "{\"sdk\":{\"version\":\"1.0.0\"}}";
        await File.WriteAllTextAsync(Path.Combine(exact, "global.json"), body + new string(' ', DeclaredEnvironmentReader.MaxFileBytes - body.Length), Ct);

        var tooBig = await _reader.ReadAsync(over, Ct);
        var atCap = await _reader.ReadAsync(exact, Ct);

        Assert.Equal(["global.json"], tooBig.FilesFound);
        Assert.Equal(DeclaredIssueKind.TooLarge, Assert.Single(tooBig.Issues).Kind);
        Assert.Empty(tooBig.Entries);
        Assert.Empty(atCap.Issues);
        Assert.Equal("1.0.0", Value(atCap, DeclaredKind.DotNetSdkVersion));
    }

    [Fact]
    public async Task A_folder_that_is_a_junction_leading_out_is_not_read_through_and_what_is_there_does_not_count()
    {
        var root = Project();
        var outside = Path.Combine(_temp, "fuera");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "dotnet-tools.json"), "{\"tools\":{\"evil\":{\"version\":\"1.0.0\"}}}", Ct);
        _links.Junction(Path.Combine(root, ".config"), outside);

        var environment = await _reader.ReadAsync(root, Ct);

        var issue = Assert.Single(environment.Issues);
        Assert.Equal((DeclaredFiles.DotNetTools, DeclaredIssueKind.IsLink), (issue.Source.File, issue.Kind));
        Assert.Empty(environment.Entries);
    }

    [Fact]
    public async Task A_venv_that_is_a_junction_is_not_read_through()
    {
        var root = Project();
        var outside = Path.Combine(_temp, "otro venv");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "pyvenv.cfg"), "version = 9.9.9\n", Ct);
        _links.Junction(Path.Combine(root, ".venv"), outside);

        var environment = await _reader.ReadAsync(root, Ct);

        Assert.Equal(DeclaredIssueKind.IsLink, Assert.Single(environment.Issues).Kind);
        Assert.Empty(environment.Of(DeclaredKind.VenvVersion));
    }

    [Fact]
    public async Task A_file_that_is_a_symbolic_link_is_not_read_through()
    {
        var root = Project();
        var victim = Path.Combine(_temp, "victima.json");
        await File.WriteAllTextAsync(victim, "{\"sdk\":{\"version\":\"9.9.9\"}}", Ct);
        try
        {
            File.CreateSymbolicLink(Path.Combine(root, "global.json"), victim);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("This account cannot create symbolic links (it needs developer mode or elevation): " + ex.Message);
        }

        var environment = await _reader.ReadAsync(root, Ct);

        Assert.Equal(DeclaredIssueKind.IsLink, Assert.Single(environment.Issues).Kind);
        Assert.Empty(environment.Entries);
    }

    [Fact]
    public async Task A_project_opened_through_a_junction_is_read_because_the_link_is_the_root_the_person_chose()
    {
        var real = Project("dotnet-repo");
        var junction = Path.Combine(_temp, "atajo");
        _links.Junction(junction, real);

        var environment = await _reader.ReadAsync(junction, Ct);

        Assert.Empty(environment.Issues);
        Assert.Equal("10.0.100", Value(environment, DeclaredKind.DotNetSdkVersion));
    }

    [Theory]
    [InlineData(FileAttributes.ReparsePoint, null, false)]
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Archive, null, false)]
    [InlineData(FileAttributes.ReparsePoint, @"C:\fuera", true)]
    [InlineData(FileAttributes.Normal, @"C:\fuera", false)]
    [InlineData(FileAttributes.Archive, null, false)]
    public void A_reparse_point_without_a_target_such_as_a_sync_placeholder_is_a_regular_file_and_one_with_a_target_is_a_link(FileAttributes attributes, string? target, bool link)
    {
        Assert.Equal(link, DeclaredEnvironmentReader.IsLink(attributes, target));
    }

    [Fact]
    public async Task Json_nested_too_deep_and_with_repeated_keys_and_not_utf8_are_issues_never_exceptions()
    {
        var deep = Project();
        await File.WriteAllTextAsync(Path.Combine(deep, "global.json"), "{\"x\":" + new string('[', 5000) + new string(']', 5000) + "}", Ct);
        var repeated = Project();
        await File.WriteAllTextAsync(Path.Combine(repeated, "package.json"), "{\"engines\":{\"node\":\"1\",\"node\":\"2\"}}", Ct);
        var binary = Project();
        await File.WriteAllBytesAsync(Path.Combine(binary, "pyproject.toml"), [0xFF, 0xFE, 0x00, 0x80, 0xC3, 0x28], Ct);
        var bom = Project();
        await File.WriteAllBytesAsync(Path.Combine(bom, ".nvmrc"), [.. Encoding.UTF8.GetPreamble(), .. "v20\n"u8.ToArray()], Ct);

        Assert.Equal(DeclaredIssueKind.TooDeep, Assert.Single((await _reader.ReadAsync(deep, Ct)).Issues).Kind);
        Assert.Equal(DeclaredIssueKind.DuplicateKey, Assert.Single((await _reader.ReadAsync(repeated, Ct)).Issues).Kind);
        Assert.Equal(DeclaredIssueKind.Malformed, Assert.Single((await _reader.ReadAsync(binary, Ct)).Issues).Kind);
        Assert.Equal("v20", Value(await _reader.ReadAsync(bom, Ct), DeclaredKind.NodeVersion));
    }

    [Fact]
    public async Task A_line_of_more_than_4096_characters_cuts_the_reading_and_hidden_characters_are_cleaned_from_what_is_shown()
    {
        var root = Project();
        await File.WriteAllTextAsync(Path.Combine(root, ".node-version"), new string('2', 5000), Ct);
        await File.WriteAllTextAsync(Path.Combine(root, ".nvmrc"), "22\u202E.1\u200B\n", Ct);

        var environment = await _reader.ReadAsync(root, Ct);

        Assert.Equal(DeclaredIssueKind.LineTooLong, Assert.Single(environment.Issues).Kind);
        Assert.Equal("22.1", Value(environment, DeclaredKind.NodeVersion));
    }

    [Fact]
    public async Task A_file_name_that_is_a_folder_or_is_locked_is_an_issue_and_not_an_exception()
    {
        var root = Project();
        Directory.CreateDirectory(Path.Combine(root, "package.json"));
        var locked = Path.Combine(root, ".nvmrc");
        await File.WriteAllTextAsync(locked, "22", Ct);

        // A writer that excludes sharing makes the read fail with an IOException: it is said, not thrown.
        using var holder = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var environment = await _reader.ReadAsync(root, Ct);

        Assert.Contains(environment.Issues, i => i.Source.File == ".nvmrc" && i.Kind == DeclaredIssueKind.Unreadable);
        Assert.DoesNotContain(environment.Entries, e => e.Source.File == DeclaredFiles.PackageJson);
    }

    [Fact]
    public async Task A_cancelled_reading_is_cancelled()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.ReadAsync(Fixture("everything"), source.Token));
    }

    [Fact]
    public async Task A_relative_or_blank_root_is_refused_as_an_argument_and_never_read_from_the_current_folder()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _reader.ReadAsync(" ", Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _reader.ReadAsync(string.Empty, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _reader.ReadAsync(@"..\proyecto", Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _reader.ReadAsync(".", Ct));
    }
}
