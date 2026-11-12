using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Providers.Cli.Codex;
using ClaudeSupport = AgentStudio.Tests.Providers.Claude.ClaudeTestSupport;
using CodexSupport = AgentStudio.Tests.Providers.Codex.CodexTestSupport;

namespace AgentStudio.Tests.Providers;

/// <summary>ADR-0018 (second addendum of 2026-11-12): a program that exists only inside the open project is not a silent «not installed».</summary>
public sealed class ProviderNotFoundInsideProjectTests
{
    private const string Planted = @"C:\proyecto\bin\claude.cmd";

    private sealed class InsideOnly(string path) : IExecutableLocator
    {
        public string? Find(string command) => null;

        public string? FindInUntrustedRoot(string command) => path;
    }

    private sealed class Nothing : IExecutableLocator
    {
        public string? Find(string command) => null;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Claude_says_where_it_found_the_program_and_why_it_is_not_used()
    {
        var provider = new ClaudeCodeAgentProvider(new InsideOnly(Planted), new StreamingProcessRunner(), TimeProvider.System);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.StartAsync(ClaudeSupport.Request(), Token));

        Assert.Contains(Planted, error.Message, StringComparison.Ordinal);
        Assert.Contains("carpeta del proyecto", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Codex_says_where_it_found_the_program_and_why_it_is_not_used()
    {
        var provider = new CodexCliAgentProvider(new InsideOnly(Planted), new StreamingProcessRunner(), TimeProvider.System);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.StartAsync(CodexSupport.Request(), Token));

        Assert.Contains(Planted, error.Message, StringComparison.Ordinal);
        Assert.Contains("project folder", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_that_is_simply_missing_keeps_the_plain_message()
    {
        var claude = new ClaudeCodeAgentProvider(new Nothing(), new StreamingProcessRunner(), TimeProvider.System);
        var codex = new CodexCliAgentProvider(new Nothing(), new StreamingProcessRunner(), TimeProvider.System);

        var a = await Assert.ThrowsAsync<InvalidOperationException>(() => claude.StartAsync(ClaudeSupport.Request(), Token));
        var b = await Assert.ThrowsAsync<InvalidOperationException>(() => codex.StartAsync(CodexSupport.Request(), Token));

        Assert.DoesNotContain("carpeta del proyecto", a.Message, StringComparison.Ordinal);
        Assert.Equal("Codex CLI was not found on the PATH.", b.Message);
    }
}
