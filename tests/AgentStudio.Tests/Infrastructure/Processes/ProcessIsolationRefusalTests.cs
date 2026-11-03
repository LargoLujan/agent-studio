using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Processes;

/// <summary>An isolation that cannot be applied stops the start: it never degrades into running without isolation.</summary>
public sealed class ProcessIsolationRefusalTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("as-isolation-refusal-").FullName;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // If this ever ran, it would leave a file behind.
    private ProcessSpec Leaves(string marker, ProcessIsolation isolation) =>
        new("cmd.exe", ["/c", "copy", "/y", "NUL", Path.Combine(_folder, marker)], Isolation: isolation);

    [Fact]
    public async Task An_isolation_the_runners_do_not_know_is_refused_and_nothing_starts()
    {
        const ProcessIsolation Unknown = (ProcessIsolation)42;

        await Assert.ThrowsAsync<ProcessIsolationException>(() => new StreamingProcessRunner().StartAsync(Leaves("streaming.txt", Unknown), Token));
        await Assert.ThrowsAsync<ProcessIsolationException>(() => new ProcessRunner().RunAsync(Leaves("waiting.txt", Unknown), Token));

        await Task.Delay(500, Token);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder));
    }

    [Fact]
    public async Task The_argument_rules_still_apply_to_an_isolated_process()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new StreamingProcessRunner().StartAsync(new ProcessSpec("cmd.exe", ["/c", "a\nb"], Isolation: ProcessIsolation.LowIntegrity), Token));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new StreamingProcessRunner().StartAsync(new ProcessSpec("tool.cmd", ["a&b"], Isolation: ProcessIsolation.LowIntegrity), Token));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
