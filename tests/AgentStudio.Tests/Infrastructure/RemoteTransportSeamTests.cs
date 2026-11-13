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
        var local = new RemoteTransport("file", repository => $@"C:\bare\{repository.Owner}\{repository.Name}.git");

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
        Assert.ThrowsAny<ArgumentException>(() => new RemoteTransport(scheme, r => r.PushUrl));

    [Fact]
    public void A_transport_needs_a_function_and_a_repository()
    {
        Assert.Throws<ArgumentNullException>(() => new RemoteTransport("https", null!));
        Assert.Throws<ArgumentNullException>(() => RemoteTransport.GitHubHttps.AddressOf(null!));
    }
}
