using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Application.Git;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Tests.Desktop.Localization;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// F10-10 (docs/21 §9): the internal git layer stays without network and without credentials, and the remote executor is the only place that opens a transport. Two tests: one of behaviour, over a battery of the real
/// layer with a runner that records, and one of architecture, over the sources.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed partial class InternalLayerHasNoNetworkTests : GitManagerTestBase
{
    private static readonly string[] Subcommands = ["push", "fetch", "pull", "clone", "ls-remote"];

    [GeneratedRegex(@"(?<!:)//.*$", RegexOptions.CultureInvariant)]
    private static partial Regex LineComment();

    private static string Code(string file) => string.Join('\n', File.ReadAllLines(file).Select(line => LineComment().Replace(line, string.Empty)));

    [Fact]
    public async Task Every_command_of_the_internal_layer_closes_every_transport_and_blanks_the_credential_helpers()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var options = new GitWorktreeOptions(Repo.WorktreeRoot);
        var history = new RepositoryHistoryReader(recorder, new ExecutableLocator(), options);
        var remotes = new RepositoryRemoteReader(recorder, new ExecutableLocator(), options);
        Repo.Run("remote", "add", "origin", "https://github.com/someone/else.git");

        // The battery: state, capture, a worktree with a commit and a checkout, the readers of history and of remotes, and the removal.
        await manager.InspectAsync(Repo.Root, Ct);
        await manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct);
        var lease = await EnsureAsync(manager, "T1");
        await CommitInAsync(manager, lease, "nuevo.txt", "x\n");
        await manager.CheckoutAsync(lease, Repo.BaseCommit, Ct);
        await manager.ListOwnedAsync(Project, Ct);
        await history.SummaryAsync(Project, Ct);
        await history.BranchesAsync(Project, Ct);
        await remotes.ListRemotesAsync(Repo.Root, Ct);
        await manager.RemoveAsync(lease, discardChanges: false, Ct);

        var specs = recorder.Specs;
        Assert.True(specs.Count >= 10, "The battery launched too little to prove anything: " + specs.Count);
        foreach (var spec in specs)
        {
            var command = RecordingProcessRunner.SubCommand(spec);
            Assert.DoesNotContain(command, Subcommands);
            Assert.Equal("-", spec.Environment!["GIT_ALLOW_PROTOCOL"]);
            Assert.Contains("credential.helper=", spec.Arguments);
            Assert.Contains("core.askPass=", spec.Arguments);
            Assert.Contains("core.sshCommand=", spec.Arguments);
            Assert.Contains("protocol.https.allow=never", spec.Arguments);
            Assert.Contains("protocol.allow=never", spec.Arguments);
        }
    }

    [Fact]
    public void No_file_of_the_internal_layer_names_a_command_that_opens_a_transport_and_the_executor_does_not_use_the_internal_runner()
    {
        var git = Path.Combine(LocalizationTestSupport.RepositoryRoot, "src", "AgentStudio.Infrastructure", "Git");
        var remote = Path.Combine(git, "Remote");
        bool IsRemote(string file) => file.StartsWith(remote + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(git, "*.cs", SearchOption.AllDirectories).Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj")).ToList();

        Assert.Contains(files, IsRemote);
        var offenders = files.Where(f => !IsRemote(f))
            .SelectMany(f => Subcommands.Where(name => Code(f).Contains($"\"{name}\"", StringComparison.Ordinal)).Select(name => $"{Path.GetFileName(f)} names \"{name}\""))
            .ToList();
        Assert.True(offenders.Count == 0, string.Join('\n', offenders));

        var usingTheInternalRunner = files.Where(IsRemote).Where(f => Code(f).Contains("GitCommandRunner", StringComparison.Ordinal)).Select(Path.GetFileName).ToList();
        Assert.True(usingTheInternalRunner.Count == 0, "The remote executor must not use GitCommandRunner: " + string.Join(", ", usingTheInternalRunner));

        // And the executor has no fetch, pull or clone of its own: its commands are config, ls-remote and push.
        var closed = files.Where(IsRemote).SelectMany(f => new[] { "fetch", "pull", "clone" }.Where(name => Code(f).Contains($"\"{name}\"", StringComparison.Ordinal)).Select(name => $"{Path.GetFileName(f)} names \"{name}\"")).ToList();
        Assert.True(closed.Count == 0, string.Join('\n', closed));
    }
}
