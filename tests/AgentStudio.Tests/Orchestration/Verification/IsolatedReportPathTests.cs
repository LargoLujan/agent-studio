using System.Diagnostics;
using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Infrastructure.Processes.Isolation;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Tests.Orchestration.Verification;

/// <summary>
/// Where the report of an isolated step may be (ADR-0017), over the real Low work areas: only in the fresh
/// <c>{results}</c> folder of the step. A fixed path of the copy may be a link that an earlier step of the run left, and the
/// runner, which has the rights of the user, never clears or reads such a path. The copy is empty (no git): only the
/// folders of the area matter here.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class IsolatedReportPathTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string UsersReport = "informe del usuario\n";

    private readonly string _container = Directory.CreateTempSubdirectory("as-isoreport-").FullName;
    private readonly InMemoryEvidenceStore _store = new();

    // A gate of these tests alone: the one the process shares is held by the slow isolated runs of other test classes.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string WorkingDirectory => Directory.CreateDirectory(Path.Combine(_container, "wt")).FullName;

    private EvidenceScope Scope => new("proyecto-1", new RunId("20261017-a1b2c3"), new TaskId("T1"), 1, Commit, Path.Combine(_container, "repo"));

    private VerificationRunner Runner(IStreamingProcessRunner processes) => new(
        processes,
        _store,
        new VerificationRunnerOptions(Path.Combine(_container, "scratch"), _gate),
        isolation: new LowIntegrityWorkAreaFactory(new LowIntegrityWorkAreaOptions(Path.Combine(_container, "verify"), LockFile: Path.Combine(_container, "verify.lock"))),
        git: new FakeCommitArchive());

    /// <summary>A folder of the user (Medium) with a report of its own, which no verification may delete.</summary>
    private string Victim()
    {
        var victim = Directory.CreateDirectory(Path.Combine(_container, "usuario")).FullName;
        File.WriteAllText(Path.Combine(victim, "results.trx"), UsersReport);
        return victim;
    }

    private static VerificationStep Plant(string target) => new(
        "plant", "Plantar un enlace", "cmd.exe", ["/d", "/c", "mklink", "/J", "reports", target], EvidenceKind.CommandOutput, TestReportFormat.None, null, TimeSpan.FromMinutes(1));

    private static VerificationStep TestWithFixedReport() => new(
        "test", "Pruebas", "cmd.exe", ["/d", "/c", "exit", "0"], EvidenceKind.TestRun, TestReportFormat.Trx, "reports/results.trx", TimeSpan.FromMinutes(1));

    [Fact]
    public async Task A_link_an_earlier_step_left_in_the_copy_never_lets_the_runner_delete_a_file_of_the_user_through_it()
    {
        var victim = Victim();

        // Windows refuses a Low process a junction to a folder it cannot write (test below), so the earlier step's link is
        // made here, with the rights of the test: what a step would leave if that refusal were ever bypassed.
        var processes = new ScriptedProcessRunner(
            ProcessScript.Exit(0) with { OnStart = spec => MakeJunction(Path.Combine(spec.WorkingDirectory!, "reports"), victim) },
            ProcessScript.Exit(0));

        var results = await Runner(processes).RunAsync(WorkingDirectory, [Plant(victim), TestWithFixedReport()], Scope, null, Ct);

        Assert.True(File.Exists(Path.Combine(victim, "results.trx")), "The runner deleted a file of the user through a link of the copy.");
        Assert.Equal(UsersReport, File.ReadAllText(Path.Combine(victim, "results.trx")));
        Assert.Equal(["plant", "test"], results.Select(r => r.StepId));
        Assert.False(results[1].Succeeded);
        Assert.Single(processes.Started);
    }

    [Fact]
    public async Task Windows_refuses_a_Low_step_a_junction_to_a_folder_of_the_user()
    {
        // Characterization, measured on Windows 11 (2026-10-17): the mount point mitigation for sandboxed tokens only lets a
        // Low process make a junction to a folder it can write. The runner does not rely on it (test above).
        var victim = Victim();

        var result = Assert.Single(await Runner(new StreamingProcessRunner()).RunAsync(WorkingDirectory, [Plant(victim)], Scope, null, Ct));

        Assert.False(result.Succeeded, _store.Text(result.Evidence.Id));
        Assert.Equal(UsersReport, File.ReadAllText(Path.Combine(victim, "results.trx")));
    }

    private static void MakeJunction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe", ["/d", "/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output);
    }

    public void Dispose()
    {
        _gate.Dispose();
        if (Directory.Exists(_container))
        {
            Directory.Delete(_container, recursive: true);
        }
    }
}
