using System.IO;
using System.IO.Compression;
using System.Text;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Infrastructure.Processes.Isolation;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Tests.Infrastructure.Git;

namespace AgentStudio.Tests.Orchestration.Verification;

/// <summary>
/// What an isolated step verifies is the commit under review as the project's repository holds it, whatever an agent did
/// to the <c>.git</c> file of its worktree (ADR-0017). An agent can point that file at a repository it planted, whose object
/// store serves another tree under the very id of the commit: git trusts the file name of a loose object and never hashes
/// it again on reading. Real git, a real worktree of a temporary repository and the real Low work areas.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class IsolatedCopyGitFileTests : GitManagerTestBase
{
    private const string Reviewed = "contenido revisado";
    private const string Planted = "contenido plantado";

    // Next to the repository and the worktree root, inside the folder the repository deletes when the test ends.
    private string Beside(string name) => Path.Combine(Path.GetDirectoryName(Repo.Root)!, name);

    private static VerificationStep Show() =>
        new("show", "Mostrar la copia", "cmd.exe", ["/d", "/c", "type", "marca.txt"], EvidenceKind.CommandOutput, TestReportFormat.None, null, TimeSpan.FromMinutes(2));

    [Fact]
    public async Task A_gitfile_rewritten_to_a_planted_repository_does_not_change_what_an_isolated_step_verifies()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        var commit = await CommitInAsync(manager, lease, "marca.txt", Reviewed + "\n");
        var planted = PlantAnotherTreeUnder(commit, "marca.txt", Planted + "\n");
        RedirectGitFile(lease, planted);

        // Control: through the rewritten .git file, git itself now serves the planted tree for the reviewed commit.
        Assert.Equal(Planted + "\n", TempGitRepo.Git(lease.Path, "show", commit + ":marca.txt"));

        // As the engine runs it: the working directory is the worktree, the scope names the project's repository.
        var store = new InMemoryEvidenceStore();
        var runner = new VerificationRunner(new StreamingProcessRunner(), store, new VerificationRunnerOptions(Beside("scratch")), isolation: new LowIntegrityWorkAreaFactory(), git: manager);
        var result = Assert.Single(await runner.RunAsync(lease.Path, [Show()], new EvidenceScope(ProjectId, TestRun, new TaskId("T1"), 1, commit, Repo.Root), null, Ct));

        var log = store.Text(result.Evidence.Id);
        Assert.True(result.Succeeded, log);
        Assert.Contains("# source: commit " + commit, log, StringComparison.Ordinal);
        Assert.DoesNotContain(Planted, log, StringComparison.Ordinal);
        Assert.Contains("stdout| " + Reviewed, log, StringComparison.Ordinal);
        Assert.Equal(commit, result.Evidence.SubjectCommit);
    }

    /// <summary>
    /// A repository of its own that serves the real commit and its real tree (an alternate of the project's objects) but,
    /// under the id of the blob of <paramref name="file"/> in <paramref name="commit"/>, another content: a loose object
    /// whose file name is that id. git checks the hash of the commit it archives, not that of every blob it reads. Returns
    /// its git directory.
    /// </summary>
    private string PlantAnotherTreeUnder(string commit, string file, string content)
    {
        var root = Beside("plantado");
        Directory.CreateDirectory(root);
        TempGitRepo.Git(root, "init", "-q");
        var objects = Path.Combine(root, ".git", "objects");
        File.WriteAllText(Path.Combine(objects, "info", "alternates"), Path.Combine(Repo.Root, ".git", "objects").Replace('\\', '/') + "\n");

        var blob = TempGitRepo.Git(Repo.Root, "rev-parse", commit + ":" + file).Trim();
        var forged = Encoding.UTF8.GetBytes(content);
        var loose = Path.Combine(objects, blob[..2], blob[2..]);
        Directory.CreateDirectory(Path.GetDirectoryName(loose)!);
        using (var output = File.Create(loose))
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
        {
            zlib.Write(Encoding.ASCII.GetBytes($"blob {forged.Length}\0"));
            zlib.Write(forged);
        }

        return Path.Combine(root, ".git");
    }
}
