using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Debate.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// WP-14C, the reinforcements of the guard of <c>AuxiliaryAgentRunner</c> that the review of the P0 asked for (docs/17 D-56): a working
/// folder that is a link (or inside one) is refused, the folder is checked again just before the provider starts, and the read-only
/// purposes (<c>Help</c>, <c>ClientDocumentDraft</c>) cannot be launched with an empty list of allowed tools.
/// </summary>
public sealed class AuxiliaryAgentRunnerGuardTests
{
    private static readonly ProjectContext Project = new("6f1c2b0e-0000-4000-8000-0000000000ab", "C:\\repo");

    private static readonly PermissionProfile ReadOnly = new(false, false, false, ["Read", "Grep", "Glob"], [], []);

    private static readonly AgentDefinition Helper = new(
        Aid("ayuda"),
        "Ayuda",
        AgentRole.Architect,
        new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"),
        string.Empty,
        [],
        ["Read", "Grep", "Glob"],
        [],
        new ExecutionBudget(3, TimeSpan.FromMinutes(5)),
        1,
        MemoryPolicy.None,
        new ReviewPolicy(false, false));

    [Theory]
    [InlineData(AgentRunPurpose.Help)]
    [InlineData(AgentRunPurpose.ClientDocumentDraft)]
    [InlineData(AgentRunPurpose.GraphRelations)]
    public async Task An_empty_list_of_allowed_tools_is_refused_before_anything_is_recorded(AgentRunPurpose purpose)
    {
        using var world = new World();
        using var folder = new TempFolder();
        var launch = Launch(folder.Path, ReadOnly with { AllowedTools = [] });

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => world.Runner.RunAsync(Project, new AuxiliaryRunRequest(purpose, launch), null, TestContext.Current.CancellationToken));

        Assert.Contains("allowed tools cannot be empty", ex.Message, StringComparison.Ordinal);
        Assert.Empty(world.Store.Of(Project.ProjectId));
        Assert.Empty(world.Provider.Starts);
    }

    [Fact]
    public async Task A_working_folder_that_is_a_link_is_refused_before_anything_is_recorded()
    {
        using var world = new World();
        using var target = new TempFolder();
        using var holder = new TempFolder();
        var link = System.IO.Path.Combine(holder.Path, "enlace");
        if (!TryLink(link, target.Path))
        {
            Assert.Skip("This machine cannot create a link (no privilege for a symbolic link and no junction).");
        }

        var launch = Launch(link);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => world.Runner.RunAsync(Project, new AuxiliaryRunRequest(AgentRunPurpose.Help, launch), null, TestContext.Current.CancellationToken));

        Assert.Contains("cannot be a link", ex.Message, StringComparison.Ordinal);
        Assert.NotEmpty(AuxiliaryAgentRunner.ReadOnlyViolations(launch));
        Assert.Empty(world.Store.Of(Project.ProjectId));
        Assert.Empty(world.Provider.Starts);
        Directory.Delete(link);
    }

    [Fact]
    public async Task A_working_folder_inside_a_link_is_refused_too()
    {
        using var world = new World();
        using var target = new TempFolder();
        using var holder = new TempFolder();
        var inside = System.IO.Path.Combine(target.Path, "dentro");
        Directory.CreateDirectory(inside);
        var link = System.IO.Path.Combine(holder.Path, "enlace");
        if (!TryLink(link, target.Path))
        {
            Assert.Skip("This machine cannot create a link (no privilege for a symbolic link and no junction).");
        }

        var launch = Launch(System.IO.Path.Combine(link, "dentro"));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => world.Runner.RunAsync(Project, new AuxiliaryRunRequest(AgentRunPurpose.Help, launch), null, TestContext.Current.CancellationToken));

        Assert.Contains("cannot be a link", ex.Message, StringComparison.Ordinal);
        Assert.Empty(world.Provider.Starts);
        Directory.Delete(link);
    }

    [Theory]
    [InlineData(AgentRunPurpose.Help)]
    [InlineData(AgentRunPurpose.ClientDocumentDraft)]
    [InlineData(AgentRunPurpose.GraphRelations)]
    public async Task A_file_that_appears_after_the_first_check_stops_the_launch_and_closes_the_attempt_as_failed(AgentRunPurpose purpose)
    {
        using var world = new World();
        using var folder = new TempFolder();

        // The prompt is captured as evidence after the first check and before the start: a file lands in the folder meanwhile.
        world.Evidence.OnCapture = () => File.WriteAllText(System.IO.Path.Combine(folder.Path, "intruso.txt"), "x");

        var result = await world.Runner.RunAsync(Project, new AuxiliaryRunRequest(purpose, Launch(folder.Path)), null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.Failed, result.Outcome.Status);
        Assert.Empty(world.Provider.Starts);
        var kinds = world.Store.Of(Project.ProjectId).Select(e => e.Kind).ToList();
        Assert.Equal([DecisionKind.AgentRunStarted, DecisionKind.AgentRunFinished], kinds);
    }

    [Fact]
    public async Task A_launch_in_an_empty_real_folder_with_the_three_tools_still_runs()
    {
        using var world = new World();
        using var folder = new TempFolder();

        var result = await world.Runner.RunAsync(Project, new AuxiliaryRunRequest(AgentRunPurpose.Help, Launch(folder.Path)), null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.Succeeded, result.Outcome.Status);
        Assert.Single(world.Provider.Starts);
        Assert.Empty(AuxiliaryAgentRunner.WorkingDirectoryProblems(folder.Path));
    }

    private static AgentRunRequest Launch(string folder, PermissionProfile? permissions = null) =>
        new(Helper, "Ayuda.", folder, permissions ?? ReadOnly);

    /// <summary>A symbolic link when the machine lets this user make one, else a junction (which needs no privilege); false when neither could be made.</summary>
    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Without the privilege of a symbolic link: a junction is a reparse point as well.
        }

        try
        {
            var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            using var process = Process.Start(start);
            process?.WaitForExit();
            return process?.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private sealed class World : IDisposable
    {
        public World()
        {
            var clock = new FakeTimeProvider(T0);
            Evidence = new HookedEvidenceStore(new FakeEvidenceStore(clock));
            Provider = new ScriptedProvider();
            Runner = new AuxiliaryAgentRunner(new DecisionRecorder(Store, clock), Evidence, new Providers(Provider), clock);
        }

        public InMemoryDecisionLogStore Store { get; } = new();

        public HookedEvidenceStore Evidence { get; }

        public ScriptedProvider Provider { get; }

        public AuxiliaryAgentRunner Runner { get; }

        public void Dispose()
        {
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public TempFolder() => Path = Directory.CreateTempSubdirectory("guard-").FullName;

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A test leaves nothing that matters.
            }
        }
    }

    /// <summary>An evidence store that calls a hook the first time it captures something.</summary>
    private sealed class HookedEvidenceStore(IEvidenceStore inner) : IEvidenceStore
    {
        public Action? OnCapture { get; set; }

        public Task<EvidenceRecord> CaptureAsync(string projectId, EvidenceDraft draft, Stream content, CancellationToken cancellationToken)
        {
            var hook = OnCapture;
            OnCapture = null;
            hook?.Invoke();
            return inner.CaptureAsync(projectId, draft, content, cancellationToken);
        }

        public Task<Stream> OpenContentAsync(string projectId, EvidenceId id, CancellationToken cancellationToken) => inner.OpenContentAsync(projectId, id, cancellationToken);

        public Task<IReadOnlyList<EvidenceRecord>> ListAsync(string projectId, CancellationToken cancellationToken) => inner.ListAsync(projectId, cancellationToken);

        public Task<IReadOnlyList<EvidenceRecord>> ListByRunAsync(string projectId, string runId, CancellationToken cancellationToken) => inner.ListByRunAsync(projectId, runId, cancellationToken);
    }

    private sealed class Providers(params IAgentProvider[] providers) : IProjectAgentProviders
    {
        public IReadOnlyList<IAgentProvider> For(string projectRoot) => providers;
    }

    private sealed class ScriptedProvider : IAgentProvider
    {
        public List<AgentRunRequest> Starts { get; } = [];

        public ProviderId Provider => ProviderId.ClaudeCode;

        public VendorId Vendor => VendorId.Anthropic;

        public ProviderCapabilities Capabilities { get; } = new(true, true, true, false, true, true, 1);

        public Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken)
        {
            Starts.Add(request);
            return Task.FromResult<IAgentRun>(new Run());
        }
    }

    private sealed class Run : IAgentRun
    {
        public string RunId => "fake";

        public IAsyncEnumerable<AgentEvent> Events => None();

        public Task<AgentRunOutcome> Completion => Task.FromResult(new AgentRunOutcome(AgentRunStatus.Succeeded, 0, null, null, "s", null));

        public Task CancelAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<AgentEvent> None([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
