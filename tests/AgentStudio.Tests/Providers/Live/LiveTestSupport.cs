using System.Globalization;
using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Providers.Live;

/// <summary>Shared plumbing of the live provider tests (the ones that spend subscription quota).</summary>
internal static class LiveTestSupport
{
    /// <summary>Opt-in switch for every test that launches a real CLI with a prompt.</summary>
    public const string OptInVariable = "AGENTSTUDIO_PROVIDER_TESTS";

    /// <summary>When set to a folder, live tests write the raw capture of the CLI they ran there (used to refresh the fixtures).</summary>
    public const string CaptureVariable = "AGENTSTUDIO_CAPTURE_DIR";

    public static void RequireOptIn() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(OptInVariable) == "1",
            $"Set {OptInVariable}=1 to run real provider tests (they spend subscription quota).");

    /// <summary>Reads the event stream of a run to its end. <c>Events</c> can be enumerated only once.</summary>
    public static async Task<List<AgentEvent>> DrainAsync(IAgentRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var events = new List<AgentEvent>();
        await foreach (var evt in run.Events.WithCancellation(cancellationToken))
        {
            events.Add(evt);
        }

        return events;
    }

    private static readonly Lock EvidenceGate = new();

    /// <summary>
    /// Writes a line to the test output and, when <see cref="CaptureVariable"/> is defined, appends it to
    /// <c>evidence.log</c> there: the timings and costs of a live run, for the report.
    /// </summary>
    public static void Note(string message)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        var directory = Environment.GetEnvironmentVariable(CaptureVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        lock (EvidenceGate)
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "evidence.log"), message + Environment.NewLine);
        }
    }

    /// <summary>
    /// Writes the raw capture of <paramref name="process"/> (stdout, stderr, exit code and command line) as
    /// <c>&lt;name&gt;.jsonl/.stderr/.exit/.cmd.txt</c> when <see cref="CaptureVariable"/> is defined; otherwise does nothing.
    /// </summary>
    public static void SaveCapture(RecordedProcess process, string name)
    {
        ArgumentNullException.ThrowIfNull(process);
        var directory = Environment.GetEnvironmentVariable(CaptureVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, name + ".jsonl"), process.StandardOutput);
        File.WriteAllLines(Path.Combine(directory, name + ".stderr"), process.StandardError);
        File.WriteAllText(
            Path.Combine(directory, name + ".exit"),
            "exit=" + (process.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "running") + Environment.NewLine);
        File.WriteAllText(Path.Combine(directory, name + ".cmd.txt"), CommandLine(process.Spec) + Environment.NewLine);
    }

    /// <summary>The executable and its arguments, one token per argument, for documenting how a capture was made.</summary>
    public static string CommandLine(ProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var tokens = new[] { Path.GetFileName(spec.FileName) }.Concat(spec.Arguments).Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a);
        return string.Join(' ', tokens) + $"   (cwd: {spec.WorkingDirectory}; prompt through stdin)";
    }

    /// <summary>Creates a real, empty git repository (<c>git init</c>); skips the test when git is not installed.</summary>
    public static async Task InitGitRepositoryAsync(string directory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var git = new ExecutableLocator().Find("git");
        Assert.SkipUnless(git is not null, "git is not installed.");

        var result = await new ProcessRunner().RunAsync(
            new ProcessSpec(git!, ["init", "--quiet", directory], Timeout: TimeSpan.FromSeconds(30)),
            cancellationToken);
        Assert.True(result.ExitCode == 0, "git init failed: " + result.StandardError);
    }

    /// <summary>Output of <c>git status --porcelain</c> in <paramref name="directory"/>: empty when nothing is untracked or modified.</summary>
    public static async Task<string> GitStatusAsync(string directory, CancellationToken cancellationToken)
    {
        var git = new ExecutableLocator().Find("git");
        Assert.SkipUnless(git is not null, "git is not installed.");

        var result = await new ProcessRunner().RunAsync(
            new ProcessSpec(git!, ["status", "--porcelain"], directory, Timeout: TimeSpan.FromSeconds(30)),
            cancellationToken);
        Assert.True(result.ExitCode == 0, "git status failed: " + result.StandardError);
        return result.StandardOutput.Trim();
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or lies below it (Windows path comparison).</summary>
    public static bool IsUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
