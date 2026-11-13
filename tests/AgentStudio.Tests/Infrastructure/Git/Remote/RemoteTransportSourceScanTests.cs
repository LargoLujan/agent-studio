using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Tests.Desktop.Localization;

namespace AgentStudio.Tests.Infrastructure.Git.Remote;

/// <summary>
/// The seam of the tests of the remote executor must open nothing in production (docs/21 D-06, ADR-0064): a scan of <c>src/</c>, in the style of the architecture tests, that no code (comments are not code)
/// names the factory of the tests, builds a transport by hand, writes a <c>file://</c> address in the layer of infrastructure, or reads a setting or a variable that could open it. It complements
/// <c>RemoteTransportSeamTests</c> of the P0, which stays as it is.
/// </summary>
public sealed partial class RemoteTransportSourceScanTests
{
    private static readonly string Src = Path.Combine(LocalizationTestSupport.RepositoryRoot, "src");

    [GeneratedRegex(@"(?<!:)//.*$", RegexOptions.CultureInvariant)]
    private static partial Regex LineComment();

    /// <summary>The code of a file without its line comments (<c>//</c>, <c>///</c>); an address such as <c>https://</c> keeps its slashes because a colon comes before them.</summary>
    private static string Code(string file) => string.Join('\n', File.ReadAllLines(file).Select(line => LineComment().Replace(line, string.Empty)));

    private static IEnumerable<string> Sources(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"));

    private static string Names(IEnumerable<string> files) => string.Join('\n', files.Select(f => Path.GetRelativePath(Src, f)));

    [Fact]
    public void No_code_of_src_names_the_factory_of_the_tests_or_builds_a_transport_by_hand()
    {
        var offenders = Sources(Src)
            .Where(f => Path.GetFileName(f) is not "RemoteTransport.ForTests.cs")
            .Where(f => Code(f).Contains("ForTests", StringComparison.Ordinal)
                || (Path.GetFileName(f) is not "RemoteTransport.cs" && Regex.IsMatch(Code(f), @"new\s+RemoteTransport\s*\(")))
            .ToList();

        Assert.True(offenders.Count == 0, "Only RemoteTransport.GitHubHttps may exist in production:\n" + Names(offenders));
    }

    [Fact]
    public void The_transport_of_production_is_made_once_and_is_https()
    {
        var code = Code(Path.Combine(Src, "AgentStudio.Infrastructure", "Git", "Remote", "RemoteTransport.cs"));

        Assert.Single(Regex.Matches(code, @"\bnew\("));
        Assert.Contains("GitHubHttps { get; } = new(HttpsScheme,", code, StringComparison.Ordinal);
        Assert.Contains("HttpsScheme = \"https\"", code, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(Code(Path.Combine(Src, "AgentStudio.Infrastructure", "Git", "Remote", "RemoteTransport.ForTests.cs")), @"\bnew\("));
    }

    [Fact]
    public void No_code_of_the_layer_of_infrastructure_writes_a_file_address_or_gives_the_file_scheme_to_a_transport()
    {
        var infrastructure = Path.Combine(Src, "AgentStudio.Infrastructure");
        var offenders = Sources(infrastructure)
            .Where(f => Code(f).Contains("file://", StringComparison.OrdinalIgnoreCase)
                || Code(f).Split('\n').Any(line => line.Contains("RemoteTransport", StringComparison.Ordinal) && line.Contains("\"file\"", StringComparison.Ordinal)))
            .ToList();

        Assert.True(offenders.Count == 0, "A file:// address or the file scheme given to a transport:\n" + Names(offenders));
    }

    [Fact]
    public void The_folder_of_the_executor_reads_no_setting_and_no_variable_of_the_environment_that_could_open_the_seam()
    {
        var remote = Path.Combine(Src, "AgentStudio.Infrastructure", "Git", "Remote");
        var forbidden = new[] { "GetEnvironmentVariable", "AppSettings", "IConfiguration", "IOptions", "Registry", "ConfigurationManager" };
        var offenders = Sources(remote)
            .SelectMany(f => forbidden.Where(word => Code(f).Contains(word, StringComparison.Ordinal)).Select(word => Path.GetFileName(f) + " reads " + word))
            .ToList();

        Assert.True(offenders.Count == 0, string.Join('\n', offenders));
    }

    [Fact]
    public void The_variable_of_git_that_injects_configuration_is_only_ever_named_to_be_removed()
    {
        var remote = Path.Combine(Src, "AgentStudio.Infrastructure", "Git", "Remote");
        var files = Sources(remote).Where(f => Code(f).Contains("GIT_CONFIG_PARAMETERS", StringComparison.Ordinal)).Select(Path.GetFileName).ToList();

        Assert.Equal(["RemoteGitEnvironment.cs"], files);
    }

    [Fact]
    public void The_registration_of_the_executor_is_the_one_composition_that_names_a_transport()
    {
        var remote = Path.Combine(Src, "AgentStudio.Infrastructure", "Git", "Remote");
        var namingGitHubHttps = Sources(Src).Where(f => Code(f).Contains("RemoteTransport.GitHubHttps", StringComparison.Ordinal)).Select(Path.GetFileName).Order().ToList();

        Assert.Equal(["RemoteGitDependencyInjection.cs"], namingGitHubHttps);
        Assert.True(Directory.Exists(remote));
    }
}
