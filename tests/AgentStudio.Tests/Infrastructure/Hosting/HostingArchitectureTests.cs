using System.IO;
using System.Text.RegularExpressions;

namespace AgentStudio.Tests.Infrastructure.Hosting;

/// <summary>
/// The architecture of the adapter of GitHub (docs/21 D-09, D-10, D-21 and F10-11): by what its sources say. Only the launcher starts <c>gh</c>; no source names a vault, the configuration of <c>gh</c>, a
/// review that is sent or a flag that the design forbids; the layers of ADR-0002 hold. These scan the text of <c>src/AgentStudio.Infrastructure/Hosting</c>, so they fail when a later change slips something in.
/// </summary>
public sealed partial class HostingArchitectureTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentStudio.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The solution was not found above the test folder.");
    }

    private static IReadOnlyList<(string Name, string Text)> Sources() =>
        [.. Directory.EnumerateFiles(Path.Combine(Root, "src", "AgentStudio.Infrastructure", "Hosting"), "*.cs", SearchOption.AllDirectories)
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))];

    [GeneratedRegex("\"[^\"\\\\\\r\\n]*\"")]
    private static partial Regex StringLiteral();

    [Fact]
    public void The_sources_are_there_and_the_scan_is_not_empty()
    {
        var names = Sources().Select(s => s.Name).ToList();

        Assert.Contains("GhRunner.cs", names);
        Assert.Contains("GitHubHostingProvider.cs", names);
        Assert.Contains("HostingCheckSource.cs", names);
        Assert.True(names.Count >= 15);
    }

    [Fact]
    public void Only_the_launcher_starts_a_process_and_none_opens_a_connection_of_its_own()
    {
        foreach (var (name, text) in Sources())
        {
            Assert.DoesNotContain("Process.Start", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ProcessStartInfo", text, StringComparison.Ordinal);
            Assert.DoesNotContain("HttpClient", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Socket", text, StringComparison.Ordinal);
            if (name != "GhRunner.cs")
            {
                Assert.False(text.Contains("IProcessRunner", StringComparison.Ordinal) && name != "GitHubHostingServiceCollectionExtensions.cs", name);
                Assert.DoesNotContain("new ProcessSpec", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_word_that_sends_an_approval_is_only_in_the_reader_of_the_reviews()
    {
        var holders = Sources().Where(s => s.Text.Contains("APPROVE", StringComparison.Ordinal)).Select(s => s.Name).ToList();

        Assert.Equal(["GitHubReviewReader.cs"], holders);
    }

    [Fact]
    public void No_source_names_a_vault_the_configuration_of_gh_or_a_command_of_the_account()
    {
        foreach (var (name, text) in Sources())
        {
            Assert.DoesNotContain("ICredentialVault", text, StringComparison.Ordinal);
            Assert.DoesNotContain("hosts.yml", text, StringComparison.Ordinal);
            Assert.DoesNotContain("GitHub CLI", text, StringComparison.Ordinal);
            Assert.DoesNotContain("GitHubToken", text, StringComparison.Ordinal);
            Assert.DoesNotContain("DpapiCredentialVault", text, StringComparison.Ordinal);
            Assert.DoesNotContain("show-token", text.Replace("\"--show-token\"", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.DoesNotContain("SpecialFolder.ApplicationData", text, StringComparison.Ordinal);
            Assert.False(StringLiteral().Matches(text).Any(m => m.Value is "\"auth\"" or "\"token\"" or "\"--show-token\"" or "\"pr\"" or "\"review\""), name);
        }
    }

    [Fact]
    public void No_source_uses_a_flag_that_the_design_forbids_nor_a_high_level_command_of_gh()
    {
        foreach (var (_, text) in Sources())
        {
            var literals = StringLiteral().Matches(text).Select(m => m.Value).ToList();
            foreach (var forbidden in new[] { "-f", "-F", "--field", "--raw-field", "--jq", "-q", "--template", "-t", "--paginate", "--slurp", "--cache", "--verbose", "--preview", "pr", "issue", "run", "workflow", "release" })
            {
                Assert.DoesNotContain("\"" + forbidden + "\"", literals);
            }

            Assert.DoesNotContain("--admin", text, StringComparison.Ordinal);
            Assert.DoesNotContain("gh pr ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("gh auth", text.Replace("gh auth login", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Only_the_environment_builder_names_the_variables_of_the_token()
    {
        var holders = Sources().Where(s => s.Text.Contains("\"GH_TOKEN\"", StringComparison.Ordinal) || s.Text.Contains("\"GITHUB_TOKEN\"", StringComparison.Ordinal)).Select(s => s.Name).ToList();

        Assert.Equal(["GhEnvironment.cs"], holders);
    }

    [Fact]
    public void The_layers_hold_the_adapter_uses_domain_application_and_infrastructure_and_nothing_above_them()
    {
        foreach (var (name, text) in Sources())
        {
            foreach (var line in text.Split('\n').Where(l => l.StartsWith("using ", StringComparison.Ordinal)))
            {
                Assert.DoesNotMatch(@"^using AgentStudio\.(Orchestration|Desktop|Providers|Memory|Security|Observability|Debate)\b", line);
            }

            Assert.DoesNotContain("System.Windows", text, StringComparison.Ordinal);
            Assert.False(text.Contains("App.Current", StringComparison.Ordinal), name);
        }
    }

    [Fact]
    public void No_source_waits_by_the_clock()
    {
        foreach (var (_, text) in Sources())
        {
            Assert.DoesNotContain("Task.Delay", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_call_to_gh_goes_through_the_queue_and_only_the_queue_reads_the_runner()
    {
        var holders = Sources().Where(s => s.Text.Contains("_runner.RunAsync", StringComparison.Ordinal) || s.Text.Contains("Runner.RunAsync", StringComparison.Ordinal)).Select(s => s.Name).Order().ToList();

        // The queue runs the launcher; the launcher runs the process; nobody else.
        Assert.Equal(["GhQueue.cs", "GhRunner.cs"], holders);
        Assert.DoesNotContain(Sources().Where(s => s.Name != "GhQueue.cs" && s.Name != "GhRunner.cs"), s => s.Text.Contains("GhRunner", StringComparison.Ordinal) && !s.Name.StartsWith("GitHubHostingServiceCollection", StringComparison.Ordinal));
    }
}
