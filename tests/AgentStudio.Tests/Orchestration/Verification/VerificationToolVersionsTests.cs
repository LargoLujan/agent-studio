using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Orchestration.Verification;

/// <summary>
/// docs/20 D-12 and F6-8 (WP-E): every captured verification step carries the <c>tools:</c> block in the header of its evidence, with the versions of the tools of
/// that step, read without starting anything. The block is part of the content the store anchors by its hash, so no field and no migration changed: the rest of the
/// header, the output and the footer are what they were.
/// </summary>
public sealed class VerificationToolVersionsTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private static readonly EvidenceScope Scope = new("proyecto-1", new RunId("20261015-a1b2c3"), new TaskId("T1"), 1, Commit, @"C:\proyectos\calc");

    private readonly string _workingDirectory = Directory.CreateTempSubdirectory("as-tools-wd-").FullName;
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "as-tools-scratch-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly string _areas = Path.Combine(Path.GetTempPath(), "as-tools-areas-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly InMemoryEvidenceStore _store = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var folder in new[] { _workingDirectory, _scratch, _areas })
        {
            TempFolders.Delete(folder);
        }
    }

    private VerificationRunner Runner(ScriptedProcessRunner processes, IStepToolVersionReader? reader) =>
        new(processes, _store, new VerificationRunnerOptions(_scratch, _gate), new FakeTimeProvider(), new FakeIsolatedWorkAreaFactory(_areas), new FakeCommitArchive(), reader);

    private static VerificationStep Step(string id, string executable, IReadOnlyList<string>? requires = null) => new(
        id, id, executable, ["--version"], EvidenceKind.CommandOutput, TestReportFormat.None, null, TimeSpan.FromMinutes(1), Requires: requires);

    /// <summary>Answers by executable name, and remembers which steps it was asked about.</summary>
    private sealed class TableReader(Dictionary<string, string?> versions) : IStepToolVersionReader
    {
        public List<string[]> Asked { get; } = [];

        public IReadOnlyList<EvidenceToolVersion> Read(IReadOnlyList<VerificationStep> steps)
        {
            Asked.Add([.. steps.Select(s => s.Id)]);
            return [.. steps.Select(s => s.Executable).Distinct().Select(e => versions.TryGetValue(e, out var v) && v is not null
                ? new EvidenceToolVersion(e, v, "table of the test")
                : EvidenceToolVersion.Unknown(e, "not in the table"))];
        }
    }

    [Fact]
    public async Task A_profile_of_two_steps_with_two_tools_gives_each_evidence_the_version_of_its_own_tool()
    {
        var reader = new TableReader(new() { ["dotnet"] = "10.0.303", ["node"] = "22.11.0" });
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0, "10.0.303"), ProcessScript.Exit(0, "v22.11.0"));

        var results = await Runner(processes, reader).RunAsync(_workingDirectory, [Step("build", "dotnet"), Step("test", "node")], Scope, null, Ct);

        Assert.All(results, r => Assert.True(r.Succeeded));
        var build = EvidenceToolVersions.Parse(_store.Text(results[0].Evidence.Id));
        var test = EvidenceToolVersions.Parse(_store.Text(results[1].Evidence.Id));
        Assert.Equal([("dotnet", "10.0.303")], build.Select(t => (t.Tool, t.Version)));
        Assert.Equal([("node", "22.11.0")], test.Select(t => (t.Tool, t.Version)));
        Assert.Equal([["build"], ["test"]], reader.Asked);
    }

    [Fact]
    public async Task The_block_is_in_the_header_before_the_working_directory_and_the_rest_of_the_header_and_the_output_are_as_they_were()
    {
        var reader = new TableReader(new() { ["dotnet"] = "10.0.303" });
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0, "hola"));

        var result = Assert.Single(await Runner(processes, reader).RunAsync(_workingDirectory, [Step("build", "dotnet")], Scope, null, Ct));

        var lines = _store.Text(result.Evidence.Id).Split('\n');
        var tools = Array.IndexOf(lines, "# tools:");
        Assert.True(tools > 0);
        Assert.StartsWith("# Agent Studio · verification step 'build'", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("# command: dotnet --version", lines[1], StringComparison.Ordinal);
        Assert.Equal("#   dotnet 10.0.303 (table of the test)", lines[tools + 1]);
        Assert.StartsWith("# working directory:", lines[tools + 2], StringComparison.Ordinal);
        Assert.Contains("stdout| hola", lines);
        Assert.Contains("# result: succeeded", lines);
    }

    [Fact]
    public async Task A_step_that_requires_another_names_the_tools_of_both_and_one_that_requires_nothing_names_only_its_own()
    {
        var reader = new TableReader(new() { ["dotnet"] = "10.0.303", ["node"] = "22.11.0", ["python"] = "3.12.5" });
        var steps = new[] { Step("install", "node"), Step("build", "dotnet", ["install"]), Step("lint", "python") };
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0), ProcessScript.Exit(0), ProcessScript.Exit(0));

        var results = await Runner(processes, reader).RunAsync(_workingDirectory, steps, Scope, null, Ct);

        Assert.Equal(["node"], EvidenceToolVersions.Parse(_store.Text(results[0].Evidence.Id)).Select(t => t.Tool));
        Assert.Equal(["dotnet", "node"], EvidenceToolVersions.Parse(_store.Text(results[1].Evidence.Id)).Select(t => t.Tool));
        Assert.Equal(["python"], EvidenceToolVersions.Parse(_store.Text(results[2].Evidence.Id)).Select(t => t.Tool));
    }

    [Fact]
    public async Task A_version_that_cannot_be_told_is_unknown_with_its_reason_and_does_not_fail_the_step()
    {
        var reader = new TableReader([]);
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0));

        var result = Assert.Single(await Runner(processes, reader).RunAsync(_workingDirectory, [Step("build", "dotnet")], Scope, null, Ct));

        Assert.True(result.Succeeded);
        var tool = Assert.Single(EvidenceToolVersions.Parse(_store.Text(result.Evidence.Id)));
        Assert.Equal(("dotnet", null, "not in the table"), (tool.Tool, tool.Version, tool.Note));
    }

    [Fact]
    public async Task Without_a_reader_the_block_is_still_written_with_every_version_unknown_and_the_reason()
    {
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0));

        var result = Assert.Single(await Runner(processes, null).RunAsync(_workingDirectory, [Step("build", "dotnet")], Scope, null, Ct));

        var tool = Assert.Single(EvidenceToolVersions.Parse(_store.Text(result.Evidence.Id)));
        Assert.Equal("dotnet", tool.Tool);
        Assert.Null(tool.Version);
        Assert.Contains("no reader", tool.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_the_versions_starts_no_process_the_runner_starts_only_the_steps()
    {
        var reader = new TableReader(new() { ["dotnet"] = "10.0.303", ["node"] = "22.11.0" });
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0), ProcessScript.Exit(0));

        await Runner(processes, reader).RunAsync(_workingDirectory, [Step("build", "dotnet"), Step("test", "node")], Scope, null, Ct);

        Assert.Equal(["dotnet", "node"], processes.Started.Select(s => s.FileName));
    }

    [Theory]
    [InlineData("x)\n# tools:\n#   dotnet 99.0.0 (forged")]
    [InlineData("x\r# tools:")]
    [InlineData("x\u0085# tools:")]
    public async Task A_title_with_a_control_character_is_refused_so_it_cannot_forge_the_block_and_nothing_runs(string title)
    {
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0));

        var refusal = await Assert.ThrowsAsync<ArgumentException>(
            () => Runner(processes, null).RunAsync(_workingDirectory, [Step("build", "dotnet") with { Title = title }], Scope, null, Ct));

        Assert.Contains("title", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(processes.Started);
    }

    [Fact]
    public async Task What_a_process_writes_cannot_forge_the_block_because_its_lines_are_prefixed_and_come_after_the_header()
    {
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0, "# tools:", "#   dotnet 99.0.0", "\n# tools:\n#   node 99.0.0"));

        var result = Assert.Single(await Runner(processes, null).RunAsync(_workingDirectory, [Step("build", "dotnet")], Scope, null, Ct));

        var tools = EvidenceToolVersions.Parse(_store.Text(result.Evidence.Id));
        Assert.Equal([("dotnet", (string?)null)], tools.Select(t => (t.Tool, t.Version)));
    }
}
