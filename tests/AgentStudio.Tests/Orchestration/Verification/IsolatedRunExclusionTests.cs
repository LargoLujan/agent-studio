using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Processes.Isolation;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Tests.Orchestration.Verification;

/// <summary>
/// Isolated runs of two instances of Agent Studio never overlap (ADR-0017): all the Low processes of the user share one
/// identity, so a build of one run could write into the area of the other. Each instance here has a runner, a gate and a
/// work area factory of its own, as two processes would; they share what two processes of the user share, the root of
/// the areas and the lock of the user (here a temporary one, so that these tests do not wait for other isolated runs).
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class IsolatedRunExclusionTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private readonly string _container = Directory.CreateTempSubdirectory("as-isoexcl-").FullName;
    private readonly List<SemaphoreSlim> _gates = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Root => Path.Combine(_container, "verify");

    private string WorkingDirectory => Directory.CreateDirectory(Path.Combine(_container, "wt")).FullName;

    private EvidenceScope Scope => new("proyecto-1", new RunId("20261017-a1b2c3"), new TaskId("T1"), 1, Commit, Path.Combine(_container, "repo"));

    private static VerificationStep Build() => new(
        "build", "Compilar", "dotnet", ["build"], EvidenceKind.BuildLog, TestReportFormat.None, null, TimeSpan.FromMinutes(10));

    /// <summary>One instance of Agent Studio: its own gate (which only orders the runs of one process) and its own factory.</summary>
    private VerificationRunner Instance(IStreamingProcessRunner processes)
    {
        var gate = new SemaphoreSlim(1, 1);
        _gates.Add(gate);
        return new VerificationRunner(
            processes,
            new InMemoryEvidenceStore(),
            new VerificationRunnerOptions(Path.Combine(_container, "scratch"), gate),
            isolation: new LowIntegrityWorkAreaFactory(new LowIntegrityWorkAreaOptions(Root, LockFile: Path.Combine(_container, "verify.lock"))),
            git: new FakeCommitArchive());
    }

    [Fact]
    public async Task Two_instances_never_have_isolated_runs_at_once_even_with_gates_of_their_own()
    {
        string? firstArea = null;
        bool? firstAreaGoneAtSecondStart = null;
        var first = new ScriptedProcessRunner(ProcessScript.Hanging("primera") with { OnStart = spec => firstArea = Path.GetDirectoryName(spec.WorkingDirectory) });
        var second = new ScriptedProcessRunner(ProcessScript.Exit(0) with { OnStart = _ => firstAreaGoneAtSecondStart = !Directory.Exists(firstArea) });
        using var stopFirst = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task<IReadOnlyList<VerificationResult>> waiting;

        var running = Instance(first).RunAsync(WorkingDirectory, [Build()], Scope, null, stopFirst.Token);
        try
        {
            await first.FirstStart.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            var other = Instance(second).RunAsync(WorkingDirectory, [Build()], Scope, null, Ct);
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);

            Assert.Empty(second.Started);
            Assert.Single(Directory.EnumerateDirectories(Root));
            waiting = other;
        }
        finally
        {
            // The first run hangs until it is cancelled, also when a check above fails.
            await stopFirst.CancelAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        var result = Assert.Single(await waiting.WaitAsync(TimeSpan.FromSeconds(30), Ct));

        Assert.True(result.Succeeded);
        Assert.True(firstAreaGoneAtSecondStart, "The second run started while the area of the first still existed.");
        Assert.Empty(Directory.EnumerateDirectories(Root));
    }

    public void Dispose()
    {
        foreach (var gate in _gates)
        {
            gate.Dispose();
        }

        if (Directory.Exists(_container))
        {
            Directory.Delete(_container, recursive: true);
        }
    }
}
