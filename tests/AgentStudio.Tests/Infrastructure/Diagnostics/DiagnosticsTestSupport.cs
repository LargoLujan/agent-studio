using System.IO;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Execution;

namespace AgentStudio.Tests.Infrastructure.Diagnostics;

/// <summary>The recorded outputs of the tools (<c>Fixtures/</c>; README.md says which are real captures and which are written by hand).</summary>
internal static class DoctorFixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Diagnostics", "Fixtures", name));
}

/// <summary>A process runner that starts nothing: it records every <see cref="ProcessSpec"/> and answers with what the test scripts.</summary>
internal sealed class RecordingProcessRunner(Func<ProcessSpec, ProcessResult>? answer = null) : IProcessRunner
{
    private int _running;
    private int _highest;

    public List<ProcessSpec> Specs { get; } = [];

    /// <summary>The most processes that were running at the same time.</summary>
    public int MostAtOnce => _highest;

    /// <summary>How long each fake process takes, to give the queue a chance to overlap two of them.</summary>
    public TimeSpan Delay { get; set; }

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        lock (Specs)
        {
            Specs.Add(spec);
        }

        var now = Interlocked.Increment(ref _running);
        InterlockedMax(ref _highest, now);
        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return answer?.Invoke(spec) ?? Ok("");
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    public static ProcessResult Ok(string output, string error = "") => new(0, output, error, TimeSpan.FromMilliseconds(5), false);

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

/// <summary>A locator over a fixed table: the program, or null, and the one it discarded for lying inside a project.</summary>
internal sealed class FakeLocator(Func<string, string?> find, Func<string, string?>? inside = null, Func<string, bool>? untrusted = null) : IExecutableLocator
{
    public string? Find(string command) => find(command);

    public string? FindInUntrustedRoot(string command) => inside?.Invoke(command);

    public bool IsUntrusted(string path) => untrusted?.Invoke(path) ?? false;
}

internal sealed class FixedRoots(params string[] roots) : IUntrustedRoots
{
    public IReadOnlyList<string> Roots { get; } = roots;
}

/// <summary>The door with scripted answers: what each tool says, and how many times it was asked.</summary>
internal sealed class ScriptedDoor : IDoctorToolRunner
{
    private readonly Dictionary<DoctorTool, DoctorToolResult> _answers = [];

    public List<(DoctorTool Tool, string? ProjectRoot)> Asked { get; } = [];

    public ScriptedDoor Says(DoctorTool tool, string output, int exit = 0, string? path = null)
    {
        _answers[tool] = new DoctorToolResult(DoctorToolOutcome.Completed, exit, output, false, path ?? @"C:\tools\" + tool);
        return this;
    }

    public ScriptedDoor Fails(DoctorTool tool, DoctorToolOutcome outcome, string? path = null)
    {
        _answers[tool] = DoctorToolResult.Without(outcome, path);
        return this;
    }

    public Task<DoctorToolResult> RunAsync(DoctorTool tool, string? projectRoot, CancellationToken ct)
    {
        lock (Asked)
        {
            Asked.Add((tool, projectRoot));
        }

        return Task.FromResult(_answers.TryGetValue(tool, out var answer) ? answer : DoctorToolResult.Without(DoctorToolOutcome.NotInstalled));
    }

    /// <summary>A computer like the development machine, as the real captures say it (Docker with the engine stopped, WSL with only the distribution of Docker).</summary>
    public static ScriptedDoor LikeTheDevelopmentMachine() => new ScriptedDoor()
        .Says(DoctorTool.DotNetListSdks, DoctorFixtures.Read("real-dotnet-list-sdks.txt"))
        .Says(DoctorTool.DotNetListRuntimes, DoctorFixtures.Read("real-dotnet-list-runtimes.txt"))
        .Says(DoctorTool.NodeVersion, DoctorFixtures.Read("real-node-version.txt"))
        .Says(DoctorTool.NpmVersion, "10.8.2\r\n")
        .Says(DoctorTool.PythonVersion, DoctorFixtures.Read("real-python-version.txt"))
        .Says(DoctorTool.GitVersion, DoctorFixtures.Read("real-git-version.txt"))
        .Says(DoctorTool.DockerVersionJson, DoctorFixtures.Read("real-docker-version-engine-stopped.json"), exit: 1)
        .Says(DoctorTool.WslStatus, DoctorFixtures.Read("real-wsl-status-es-docker-desktop-only.txt"));
}

internal sealed class FixedWebView2(string? version) : AgentStudio.Infrastructure.Diagnostics.IWebView2Probe
{
    public AgentStudio.Domain.Environments.ToolVersion? Read() => version is null ? null : AgentStudio.Domain.Environments.ToolVersion.Parse(version);
}
