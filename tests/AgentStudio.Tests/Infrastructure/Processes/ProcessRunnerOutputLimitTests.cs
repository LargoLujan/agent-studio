using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Processes;

/// <summary>The optional cap on the output of <see cref="ProcessRunner"/>: off by default, and a process that passes it is killed and reported.</summary>
public sealed class ProcessRunnerOutputLimitTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "as-outlimit-" + Guid.NewGuid().ToString("N")[..8]);

    public ProcessRunnerOutputLimitTests() => Directory.CreateDirectory(_folder);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string BigFile(int characters)
    {
        var path = Path.Combine(_folder, "big.txt");
        File.WriteAllText(path, new string('x', characters));
        return path;
    }

    private static ProcessSpec Type(string file, int? limit) =>
        new("cmd.exe", ["/d", "/c", "type", file], Timeout: TimeSpan.FromSeconds(60), MaxOutputCharacters: limit);

    [Fact]
    public async Task Without_a_limit_the_whole_output_is_kept_as_before()
    {
        var result = await new ProcessRunner().RunAsync(Type(BigFile(300_000), null), Ct);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.OutputLimitExceeded);
        Assert.False(result.TimedOut);
        Assert.Equal(300_000, result.StandardOutput.Length);
    }

    [Fact]
    public async Task An_output_within_the_limit_is_not_reported()
    {
        var result = await new ProcessRunner().RunAsync(Type(BigFile(1_000), 1_000), Ct);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.OutputLimitExceeded);
        Assert.Equal(1_000, result.StandardOutput.Length);
    }

    [Fact]
    public async Task An_output_past_the_limit_kills_the_process_and_is_reported_cut_at_the_limit()
    {
        var result = await new ProcessRunner().RunAsync(Type(BigFile(3_000_000), 10_000), Ct);

        Assert.True(result.OutputLimitExceeded);
        Assert.False(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(10_000, result.StandardOutput.Length);
    }
}
