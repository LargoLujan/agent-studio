using System.Diagnostics;
using System.IO;
using System.Reflection;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Environments;
using AgentStudio.Infrastructure.Environments;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Infrastructure.Environments;

/// <summary>
/// docs/20 D-07 and D-08, and F6-7 (WP-E): the source of checks of the declared environment with a double for the inventory of WP-A and a NuGet cache of its own, and
/// the proof that reading the environment starts no process: not by a promise, by structure (no type of the package can start one) and by a tripwire (a repository
/// with <c>dotnet.exe</c>, <c>node.exe</c>, <c>git.exe</c>, <c>claude.cmd</c> and a <c>.venv</c> python planted, none of which runs).
/// </summary>
public sealed class DeclaredEnvironmentCheckSourceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "as fuente " + Guid.NewGuid().ToString("N")[..12]);

    public DeclaredEnvironmentCheckSourceTests() => Directory.CreateDirectory(_temp);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => TempFolders.Delete(_temp);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Environments", "Fixtures", name);

    private string Copy(string fixture)
    {
        var root = Path.Combine(_temp, "repositorio " + Guid.NewGuid().ToString("N")[..6]);
        foreach (var file in Directory.EnumerateFiles(Fixture(fixture), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(root, Path.GetRelativePath(Fixture(fixture), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return root;
    }

    private sealed class FakeInventory(InstalledTools tools) : IInstalledToolInventory
    {
        public List<string?> Asked { get; } = [];

        public Task<InstalledTools> ReadAsync(string? projectRoot, CancellationToken ct)
        {
            Asked.Add(projectRoot);
            return Task.FromResult(tools);
        }
    }

    private static ToolVersion V(string text) => ToolVersion.Parse(text);

    private static InstalledTools Machine() =>
        new(
            [new InstalledSdk(V("10.0.103"), null), new InstalledSdk(V("10.0.303"), null)],
            [],
            new InstalledTool(V("22.11.0"), null),
            new InstalledTool(V("10.9.0"), null),
            new InstalledTool(V("3.12.5"), null),
            false,
            null,
            new DockerStatus(DockerState.Running, V("27.0.1"), V("27.0.1")),
            WslStatus.NotInstalled,
            null);

    private DeclaredEnvironmentCheckSource Source(FakeInventory inventory, string? cache = null) =>
        new(new DeclaredEnvironmentReader(), inventory, () => cache ?? Path.Combine(_temp, "nuget"));

    [Fact]
    public async Task Without_an_open_project_the_source_has_nothing_to_say_and_asks_nobody()
    {
        var inventory = new FakeInventory(Machine());

        var checks = await Source(inventory).CheckAsync(null, Ct);

        Assert.Empty(checks);
        Assert.Empty(inventory.Asked);
        Assert.Equal(DoctorSection.DeclaredEnvironment, Source(inventory).Section);
    }

    [Fact]
    public async Task A_project_with_everything_gets_one_check_per_row_set_against_the_inventory_of_its_own_root()
    {
        var root = Copy("everything");
        var nuget = Path.Combine(_temp, "nuget");
        Directory.CreateDirectory(Path.Combine(nuget, "dotnet-ef", "10.0.0"));
        var inventory = new FakeInventory(Machine());

        var checks = await Source(inventory).CheckAsync(new ProjectContext("p1", root), Ct);

        Assert.Equal([root], inventory.Asked);
        Assert.All(checks, c => Assert.Equal(DoctorSection.DeclaredEnvironment, c.Section));
        Assert.Equal(checks.Count, checks.Select(c => c.Code).Distinct().Count());
        DoctorCheck Of(string code) => Assert.Single(checks, c => c.Code == code);
        Assert.Equal(DoctorStatus.Ok, Of(EnvironmentCheckCodes.DotNetSdk).Status);
        Assert.Equal("10.0.303", Of(EnvironmentCheckCodes.DotNetSdk).Fact("resolved"));
        Assert.Equal(DoctorStatus.Warning, Of(EnvironmentCheckCodes.DotNetLocalTools).Status);
        Assert.Equal(DoctorStatus.Ok, Of(EnvironmentCheckCodes.NodeNvmrc).Status);
        Assert.Equal(DoctorStatus.Ok, Of(EnvironmentCheckCodes.NodeVersionFile).Status);
        Assert.Equal(DoctorStatus.Ok, Of(EnvironmentCheckCodes.NodeEngine).Status);
        Assert.Equal(DoctorStatus.Ok, Of(EnvironmentCheckCodes.NpmEngine).Status);
        Assert.Equal(DoctorStatus.Ok, Of(EnvironmentCheckCodes.PythonVersionFile).Status);
        Assert.Equal(DoctorStatus.Ok, Of(EnvironmentCheckCodes.PythonRequires).Status);
        Assert.Equal(DoctorStatus.Ok, Of(EnvironmentCheckCodes.PythonVenv).Status);
        Assert.Equal(DoctorStatus.Ok, Of(EnvironmentCheckCodes.Containers).Status);
        Assert.DoesNotContain(checks, c => c.Code == EnvironmentCheckCodes.NotRead);
    }

    [Fact]
    public async Task The_local_tools_are_looked_up_in_the_cache_by_package_and_version_and_a_hostile_id_is_never_made_into_a_path()
    {
        var root = Path.Combine(_temp, "hostil");
        Directory.CreateDirectory(Path.Combine(root, ".config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, ".config", "dotnet-tools.json"),
            "{\"tools\":{\"dotnet-ef\":{\"version\":\"10.0.0\"},\"..\\\\planted\":{\"version\":\"1.0.0\"},\"../planted\":{\"version\":\"1.0.0\"},\"c:\\\\x\":{\"version\":\"1.0.0\"},\"ok\":{\"version\":\"1.0.0/..\"}}}",
            Ct);
        var nuget = Path.Combine(_temp, "nuget");
        Directory.CreateDirectory(Path.Combine(nuget, "dotnet-ef", "10.0.0"));
        // What a traversal would reach if the id were put in a path as it is.
        Directory.CreateDirectory(Path.Combine(_temp, "planted", "1.0.0"));
        Directory.CreateDirectory(Path.Combine(nuget, "ok", "1.0.0"));

        var checks = await Source(new FakeInventory(Machine())).CheckAsync(new ProjectContext("p1", root), Ct);

        var tools = Assert.Single(checks, c => c.Code == EnvironmentCheckCodes.DotNetLocalTools);
        Assert.Equal(DoctorStatus.Warning, tools.Status);
        var installed = tools.Facts.Where(f => f.Name == "installed").Select(f => f.Value).ToList();
        Assert.Equal("in the NuGet cache", installed[0]);
        Assert.All(installed.Skip(1), v => Assert.Equal("not in the NuGet cache", v));
        Assert.Equal(5, installed.Count);
    }

    [Fact]
    public async Task Without_a_folder_for_the_cache_the_local_tools_are_not_checked()
    {
        var root = Copy("dotnet-repo");
        var source = new DeclaredEnvironmentCheckSource(new DeclaredEnvironmentReader(), new FakeInventory(Machine()), () => null);

        var checks = await source.CheckAsync(new ProjectContext("p1", root), Ct);

        Assert.Equal(DoctorStatus.Unknown, Assert.Single(checks, c => c.Code == EnvironmentCheckCodes.DotNetLocalTools).Status);
    }

    [Fact]
    public async Task A_global_json_with_paths_inside_the_project_is_a_warning_and_the_sdk_is_not_a_yes()
    {
        var root = Path.Combine(_temp, "con rutas");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "global.json"), "{\"sdk\":{\"version\":\"10.0.100\",\"paths\":[\".dotnet\",\"$host$\"]}}", Ct);

        var checks = await Source(new FakeInventory(Machine())).CheckAsync(new ProjectContext("p1", root), Ct);

        Assert.Equal(DoctorStatus.Unknown, Assert.Single(checks, c => c.Code == EnvironmentCheckCodes.DotNetSdk).Status);
        Assert.Equal(DoctorStatus.Warning, Assert.Single(checks, c => c.Code == EnvironmentCheckCodes.DotNetSdkInRepo).Status);
    }

    [Fact]
    public async Task What_could_not_be_read_comes_out_as_one_warning_with_the_file_and_the_reason()
    {
        var root = Path.Combine(_temp, "roto");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "global.json"), "{ \"sdk\": ", Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "package.json"), "{\"engines\":{\"node\":\"1\",\"node\":\"2\"}}", Ct);

        var checks = await Source(new FakeInventory(Machine())).CheckAsync(new ProjectContext("p1", root), Ct);

        var check = Assert.Single(checks);
        Assert.Equal(EnvironmentCheckCodes.NotRead, check.Code);
        Assert.Equal("global.json", check.Fact("malformed"));
        Assert.Equal("package.json", check.Fact("duplicate-key"));
    }

    // ---- F6-7: nothing is started ----

    [Fact]
    public void No_type_of_the_package_can_start_a_process()
    {
        string[] forbidden =
        [
            nameof(Process), nameof(ProcessStartInfo), nameof(IProcessRunner), nameof(IStreamingProcessRunner), nameof(IDoctorToolRunner), nameof(IExecutableLocator),
        ];
        var types = typeof(DeclaredEnvironmentReader).Assembly.GetTypes().Where(t => t.Namespace == "AgentStudio.Infrastructure.Environments").ToList();
        Assert.NotEmpty(types);
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (var type in types)
        {
            var used = new List<Type>();
            used.AddRange(type.GetFields(all).Select(f => f.FieldType));
            used.AddRange(type.GetConstructors(all).SelectMany(c => c.GetParameters().Select(p => p.ParameterType)));
            foreach (var method in type.GetMethods(all))
            {
                used.Add(method.ReturnType);
                used.AddRange(method.GetParameters().Select(p => p.ParameterType));
                used.AddRange(method.GetMethodBody()?.LocalVariables.Select(l => l.LocalType) ?? []);
            }

            Assert.DoesNotContain(used, u => forbidden.Contains(u.Name, StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task Reading_a_repository_with_planted_tools_starts_nothing_of_it()
    {
        var root = Copy("everything");
        var marker = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        foreach (var planted in new[] { "dotnet.exe", "node.exe", "git.exe", "python.exe", "claude.exe", Path.Combine(".venv", "Scripts", "python.exe") })
        {
            var target = Path.Combine(root, planted);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(marker, target);
        }

        await File.WriteAllTextAsync(Path.Combine(root, "claude.cmd"), "@echo planted", Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "global.json"), "{\"sdk\":{\"version\":\"10.0.100\",\"paths\":[\".dotnet\"]}}", Ct);

        try
        {
            var checks = await Source(new FakeInventory(Machine())).CheckAsync(new ProjectContext("p1", root), Ct);
            Assert.NotEmpty(checks);
            Assert.Empty(RunningFrom(root));
        }
        finally
        {
            foreach (var process in RunningFrom(root))
            {
                process.Kill(entireProcessTree: true);
                process.Dispose();
            }
        }
    }

    /// <summary>The processes whose executable lies inside <paramref name="folder"/> (the ones that cannot be asked about are not).</summary>
    private static List<Process> RunningFrom(string folder)
    {
        var found = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainModule?.FileName is { } file && file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(process);
                    continue;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // A process that cannot be inspected (protected, or gone) is not one of ours.
            }

            process.Dispose();
        }

        return found;
    }
}
