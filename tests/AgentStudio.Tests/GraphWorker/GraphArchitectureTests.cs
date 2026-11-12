using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.GraphWorker;

/// <summary>
/// F6-12 (docs/20 section 8, P0 of Phase 6, ADR-0062): the architecture test. Auto Graph does not evaluate MSBuild and does not create a compilation, so
/// the packages that would allow it never enter: no project references <c>Microsoft.Build*</c> or <c>Microsoft.CodeAnalysis.Workspaces*</c>, Roslyn lives in
/// the analysis process alone, and that process depends only on Application (ADR-0002). The rest of F6-12 (the sentinel of a hostile
/// repository, the paths that lead out) is the test of WP-G over its sample solution.
/// </summary>
public sealed partial class GraphArchitectureTests
{
    [GeneratedRegex(@"<PackageReference\s+Include=""(?<id>[^""]+)""(?<rest>[^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex PackageReference();

    [GeneratedRegex(@"<ProjectReference\s+Include=""(?<path>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectReference();

    private static IReadOnlyList<string> Projects(string folder) =>
        [.. Directory.GetFiles(Path.Combine(RepositoryRoot, folder), "*.csproj", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];

    private static bool IsForbidden(string package) =>
        package.StartsWith("Microsoft.Build", StringComparison.OrdinalIgnoreCase)
        || package.StartsWith("Microsoft.CodeAnalysis.Workspaces", StringComparison.OrdinalIgnoreCase)
        || package.StartsWith("Microsoft.CodeAnalysis.Compilers", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void No_project_of_the_solution_references_msbuild_or_the_workspaces_of_roslyn_and_the_central_file_does_not_even_pin_them()
    {
        var projects = Projects("src").Concat(Projects("tests")).ToList();
        Assert.True(projects.Count >= 13, "The projects of the solution were not found.");

        foreach (var project in projects)
        {
            var packages = PackageReference().Matches(File.ReadAllText(project)).Select(m => m.Groups["id"].Value);
            Assert.All(packages, package => Assert.False(IsForbidden(package), $"{Path.GetFileName(project)} references {package}."));
        }

        var central = File.ReadAllText(Path.Combine(RepositoryRoot, "Directory.Packages.props"));
        Assert.All(
            Regex.Matches(central, @"<PackageVersion\s+Include=""(?<id>[^""]+)""").Select(m => m.Groups["id"].Value),
            package => Assert.False(IsForbidden(package), $"Directory.Packages.props pins {package}."));
    }

    [Fact]
    public void Roslyn_is_referenced_by_the_analysis_process_alone_and_only_its_syntax_parser_with_the_compile_assets_private()
    {
        var referencing = new List<string>();
        foreach (var project in Projects("src").Concat(Projects("tests")))
        {
            foreach (Match match in PackageReference().Matches(File.ReadAllText(project)))
            {
                if (match.Groups["id"].Value.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase))
                {
                    referencing.Add($"{Path.GetFileNameWithoutExtension(project)}:{match.Groups["id"].Value}");
                    Assert.Contains("PrivateAssets=\"compile\"", match.Groups["rest"].Value, StringComparison.Ordinal);
                }
            }
        }

        Assert.Equal(["AgentStudio.GraphWorker:Microsoft.CodeAnalysis.CSharp"], referencing);
    }

    [Fact]
    public void The_analysis_process_depends_only_on_application_and_nothing_references_it_but_the_desktop_that_ships_it_and_the_tests()
    {
        var worker = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "AgentStudio.GraphWorker", "AgentStudio.GraphWorker.csproj"));
        Assert.Equal(["..\\AgentStudio.Application\\AgentStudio.Application.csproj"], ProjectReference().Matches(worker).Select(m => m.Groups["path"].Value));

        var referencingWorker = Projects("src").Concat(Projects("tests"))
            .Where(p => ProjectReference().Matches(File.ReadAllText(p)).Any(m => m.Groups["path"].Value.Contains("AgentStudio.GraphWorker", StringComparison.Ordinal)))
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal);
        Assert.Equal(["AgentStudio.Desktop", "AgentStudio.Tests"], referencingWorker);
    }

    [Fact]
    public void The_assemblies_of_the_app_see_no_type_of_roslyn_and_none_of_msbuild_and_the_output_holds_no_workspace_or_msbuild_library()
    {
        Assembly[] assemblies =
        [
            typeof(AgentStudio.Domain.Graph.CodeGraph).Assembly,
            typeof(AgentStudio.Application.Graph.CodeGraphJson).Assembly,
            typeof(AgentStudio.Desktop.Graph.GraphDocumentViewModel).Assembly,
            typeof(AgentStudio.Infrastructure.Processes.ExecutableLocator).Assembly,
            typeof(AgentStudio.Memory.Export.MemoryMarkdownExporter).Assembly,
            typeof(AgentStudio.Orchestration.Verification.VerificationRunner).Assembly,
        ];

        foreach (var assembly in assemblies)
        {
            var references = assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();
            Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft.Build", StringComparison.OrdinalIgnoreCase));
        }

        var libraries = Directory.GetFiles(AppContext.BaseDirectory, "*.dll").Select(Path.GetFileName).ToList();
        Assert.DoesNotContain(libraries, name => name!.StartsWith("Microsoft.Build", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft.CodeAnalysis.Workspaces", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_analysis_process_references_application_and_domain_and_no_module_of_the_app_and_never_msbuild_or_the_workspaces()
    {
        var worker = typeof(AgentStudio.GraphWorker.GraphWorkerRunner).Assembly;

        var references = worker.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();

        Assert.Equal(
            ["AgentStudio.Application", "AgentStudio.Domain"],
            references.Where(n => n.StartsWith("AgentStudio.", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.Build", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft.CodeAnalysis.Workspaces", StringComparison.OrdinalIgnoreCase));
    }
}
