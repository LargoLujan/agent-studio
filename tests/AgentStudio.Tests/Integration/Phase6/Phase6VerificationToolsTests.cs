using System.IO;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Tests.Orchestration.Verification;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Integration.Phase6;

/// <summary>
/// F6-8 of docs/20 end to end (section 9.8, D-12): a verification of two steps whose evidence carries the <c>tools:</c> block of its header, written by the runner with the
/// reader of versions the app's composition registers (<c>StepToolVersionReader</c>: it reads the version resource of the executable and starts nothing) and read back by
/// <see cref="EvidenceToolVersions.Parse"/>. The steps themselves go through a scripted process runner, so no tool of this computer is started.
/// </summary>
public sealed class Phase6VerificationToolsTests : IDisposable
{
    private static readonly EvidenceScope Scope = new("proyecto-1", new RunId("20261115-a1b2c3"), new TaskId("T1"), 1, "0123456789abcdef0123456789abcdef01234567", @"C:\proyectos\calc");

    private readonly string _workingDirectory = Directory.CreateTempSubdirectory("as-p6-wd-").FullName;
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "as-p6-scratch-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly string _areas = Path.Combine(Path.GetTempPath(), "as-p6-areas-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly InMemoryEvidenceStore _store = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var folder in new[] { _workingDirectory, _scratch, _areas })
        {
            TempFolders.Delete(folder);
        }
    }

    private static VerificationStep Step(string id, string executable) => new(
        id, id, executable, ["--version"], EvidenceKind.CommandOutput, TestReportFormat.None, null, TimeSpan.FromMinutes(1));

    [Fact]
    public async Task A_verification_of_two_steps_leaves_the_tools_block_in_each_evidence_and_the_reader_of_the_composition_reads_the_versions_without_starting_anything()
    {
        using var host = Phase6Host.Create();
        var reader = Assert.IsAssignableFrom<IStepToolVersionReader>(host.Provider.GetRequiredService<IStepToolVersionReader>());
        var read = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var missing = Path.Combine(_workingDirectory, "no-such-tool.exe");
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0, "ok"), ProcessScript.Exit(0, "ok"));
        var runner = new VerificationRunner(
            processes,
            _store,
            new VerificationRunnerOptions(_scratch, new SemaphoreSlim(1, 1)),
            new FakeTimeProvider(),
            new FakeIsolatedWorkAreaFactory(_areas),
            new FakeCommitArchive(),
            reader);

        var results = await runner.RunAsync(_workingDirectory, [Step("build", read), Step("test", missing)], Scope, null, Ct);

        Assert.All(results, r => Assert.True(r.Succeeded));

        // Step one: the version is that of the version resource of the executable, the same the system reports for the file, with the note of where it came from.
        var build = Assert.Single(EvidenceToolVersions.Parse(_store.Text(results[0].Evidence.Id)));
        Assert.Equal("cmd", build.Tool);
        Assert.False(string.IsNullOrWhiteSpace(build.Version));
        Assert.StartsWith("version resource of cmd.exe", build.Note, StringComparison.Ordinal);

        // Step two: a tool that is not there is «unknown» with its reason, and the step still ran.
        var test = Assert.Single(EvidenceToolVersions.Parse(_store.Text(results[1].Evidence.Id)));
        Assert.Equal("no-such-tool", test.Tool);
        Assert.Null(test.Version);
        Assert.Equal("the file was not found", test.Note);

        // The block is in the header, before the working directory, and each step names only its own tool.
        var lines = _store.Text(results[0].Evidence.Id).Split('\n');
        var block = Array.IndexOf(lines, EvidenceToolVersions.BlockLine);
        Assert.True(block > 0);
        Assert.StartsWith("# working directory:", lines[block + 2], StringComparison.Ordinal);

        // Reading the versions started nothing: the process runner only ever saw the two steps.
        Assert.Equal([read, missing], processes.Started.Select(s => s.FileName));
    }
}
