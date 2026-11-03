using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Tests.Providers.Gemini;

internal sealed class FakeLocator(Dictionary<string, string?> map) : IExecutableLocator
{
    public string? Find(string command) => map.GetValueOrDefault(command);
}

/// <summary>A scripted process: replays lines, then exits with the given code; Kill ends it with -1.</summary>
internal sealed class FakeSession : IProcessSession
{
    private readonly List<ProcessOutputLine> _lines;
    private readonly int _exitCode;
    private readonly bool _blockUntilKilled;
    private readonly TaskCompletionSource _killed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeSession(IEnumerable<ProcessOutputLine> lines, int exitCode, bool blockUntilKilled = false)
    {
        _lines = [.. lines];
        _exitCode = exitCode;
        _blockUntilKilled = blockUntilKilled;
        Exited = Task.Run(async () =>
        {
            if (_blockUntilKilled)
            {
                await _killed.Task;
                return -1;
            }

            return _exitCode;
        });
    }

    public int ProcessId => 1234;

    public Task<int> Exited { get; }

    public IAsyncEnumerable<ProcessOutputLine> Output => Replay();

    private async IAsyncEnumerable<ProcessOutputLine> Replay()
    {
        foreach (var line in _lines)
        {
            yield return line;
        }

        if (_blockUntilKilled)
        {
            await _killed.Task;
        }
    }

    public Task KillAsync()
    {
        _killed.TrySetResult();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _killed.TrySetResult();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeStreamingRunner(Func<ProcessSpec, IProcessSession> create) : IStreamingProcessRunner
{
    public List<ProcessSpec> Specs { get; } = [];

    public Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        Specs.Add(spec);
        return Task.FromResult(create(spec));
    }
}

internal static class GeminiTestData
{
    public static AgentRunRequest Request(
        string prompt = "Responde OK",
        string? instructions = "Eres un ejecutor.",
        bool allowWrites = false,
        string? model = null,
        string? resume = null,
        IReadOnlyList<string>? extraDirectories = null,
        string workingDirectory = "C:/work",
        TimeSpan? maxDuration = null) =>
        new(
            new AgentDefinition(
                new AgentId("Exec-1"),
                "Ejecutor",
                AgentRole.Executor,
                new ModelBinding(ProviderId.GeminiCli, VendorId.Google, model),
                instructions ?? string.Empty,
                [],
                [],
                [],
                new ExecutionBudget(10, maxDuration ?? TimeSpan.FromMinutes(5)),
                3,
                MemoryPolicy.None,
                new ReviewPolicy(false, false)),
            prompt,
            workingDirectory,
            new PermissionProfile(allowWrites, false, false, [], [], extraDirectories ?? []),
            ResumeSessionId: resume);

    public static IEnumerable<ProcessOutputLine> StdoutLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => new ProcessOutputLine(ProcessStream.StandardOutput, l.TrimEnd('\r'), DateTimeOffset.UtcNow));

    public static IEnumerable<ProcessOutputLine> StderrLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => new ProcessOutputLine(ProcessStream.StandardError, l.TrimEnd('\r'), DateTimeOffset.UtcNow));

    /// <summary>Synthetic fixtures are read from the source tree (they are not copied to the output).</summary>
    public static string ReadSynthetic(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Providers", "Gemini", "Synthetic", name);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            candidate = Path.Combine(directory.FullName, "tests", "AgentStudio.Tests", "Providers", "Gemini", "Synthetic", name);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Synthetic fixture not found: {name}");
    }

    public static string ReadReal(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Providers", "Fixtures", "gemini-cli", "0.64.0", name));
}
