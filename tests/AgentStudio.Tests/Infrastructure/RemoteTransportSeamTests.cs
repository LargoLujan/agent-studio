using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using AgentStudio.Domain.Hosting;
using AgentStudio.Infrastructure.Git.Remote;

namespace AgentStudio.Tests.Infrastructure;

/// <summary>
/// The test seam of the remote executor of git (docs/21 D-06, P0 of Phase 10): <c>RemoteTransport</c> is internal, the only instance production names is
/// <c>GitHubHttps</c>, and the tests of WP1 see the internals of the assembly through <c>InternalsVisibleTo</c>. The executor itself is WP1's.
/// </summary>
public sealed class RemoteTransportSeamTests
{
    private static readonly RemoteRepository Repository = RemoteRepository.TryCreateGitHub("LargoLujan", "agent-studio")!;

    [Fact]
    public void The_transport_is_internal_and_the_tests_see_it_through_the_attribute_of_the_assembly()
    {
        Assert.False(typeof(RemoteTransport).IsPublic);
        Assert.True(typeof(RemoteTransport).IsNotPublic);
        var visible = typeof(RemoteTransport).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>().Select(a => a.AssemblyName);
        Assert.Contains("AgentStudio.Tests", visible);
    }

    [Fact]
    public void The_one_instance_of_production_is_https_to_the_address_the_repository_writes()
    {
        var transport = RemoteTransport.GitHubHttps;

        Assert.Equal("https", transport.Scheme);
        Assert.Equal(RemoteTransport.HttpsScheme, transport.Scheme);
        Assert.Equal("https://github.com/LargoLujan/agent-studio.git", transport.AddressOf(Repository));
        Assert.Same(transport, RemoteTransport.GitHubHttps);
    }

    [Fact]
    public void A_transport_of_the_tests_takes_any_scheme_of_lowercase_letters_and_an_address_of_its_own()
    {
        var local = RemoteTransport.ForTests("file", repository => $@"C:\bare\{repository.Owner}\{repository.Name}.git");

        Assert.Equal("file", local.Scheme);
        Assert.Equal(@"C:\bare\LargoLujan\agent-studio.git", local.AddressOf(Repository));
    }

    [Theory]
    [InlineData("")]
    [InlineData("HTTPS")]
    [InlineData("ht tps")]
    [InlineData("https:")]
    [InlineData("a,b")]
    [InlineData("ssh2")]
    public void A_scheme_that_is_not_lowercase_letters_is_refused_because_it_goes_into_a_list_of_protocols(string scheme) =>
        Assert.ThrowsAny<ArgumentException>(() => RemoteTransport.ForTests(scheme, r => r.PushUrl));

    [Fact]
    public void A_transport_needs_a_function_and_a_repository()
    {
        Assert.Throws<ArgumentNullException>(() => RemoteTransport.ForTests("https", null!));
        Assert.Throws<ArgumentNullException>(() => RemoteTransport.GitHubHttps.AddressOf(null!));
    }

    [Fact]
    public void The_constructor_is_private_and_the_test_factory_is_called_by_nothing_under_src()
    {
        Assert.DoesNotContain(typeof(RemoteTransport).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), c => !c.IsPrivate);

        var src = Path.Combine(AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport.RepositoryRoot, "src");
        var callers = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            .Where(f => Path.GetFileName(f) is not ("RemoteTransport.ForTests.cs" or "RemoteTransport.cs"))
            .Where(f => File.ReadAllText(f).Contains("RemoteTransport.ForTests", StringComparison.Ordinal) || File.ReadAllText(f).Contains("new RemoteTransport(", StringComparison.Ordinal))
            .ToList();

        Assert.True(callers.Count == 0, "Production code must use RemoteTransport.GitHubHttps only:\n" + string.Join('\n', callers));
    }
}