using System.IO;
using System.Text;
using AgentStudio.Application.Git;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Domain.Team;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Tests.Infrastructure.Persistence;
using static AgentStudio.Tests.Desktop.Orchestrator.Team.TeamTestSupport;

namespace AgentStudio.Tests.Desktop.Orchestrator.Team;

/// <summary>
/// F13-3 (docs/17 D-10): the node of the repository shows the branch, the short HEAD, the remotes clean, the changes not
/// committed, the stack and the main folders, without network; and a URL with credentials is never shown nor saved with them.
/// The tests use temporary repositories (a <c>.git</c> folder with its configuration) and a fake of the hardened git layer.
/// </summary>
public sealed class RepositoryNodeTests : IDisposable
{
    private const string Secret = "ghp_S3cretT0ken";
    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private sealed class FakeState(RepositoryState? state = null, Exception? failure = null) : IRepositoryStateReader
    {
        public int Calls { get; private set; }

        public string? Root { get; private set; }

        public Task<RepositoryState> InspectAsync(string repoRoot, CancellationToken cancellationToken)
        {
            Calls++;
            Root = repoRoot;
            return failure is null ? Task.FromResult(state!) : Task.FromException<RepositoryState>(failure);
        }
    }

    private string Repo(string config)
    {
        Directory.CreateDirectory(Path.Combine(_folder.Root, ".git"));
        File.WriteAllText(Path.Combine(_folder.Root, ".git", "config"), config, Encoding.UTF8);
        File.WriteAllText(Path.Combine(_folder.Root, "Calc.slnx"), "<Solution/>");
        foreach (var (folder, files) in new[] { ("src", 5), ("tests", 3), ("docs", 1), ("bin", 9), ("obj", 9), ("node_modules", 9), (".github", 2) })
        {
            Directory.CreateDirectory(Path.Combine(_folder.Root, folder));
            for (var i = 0; i < files; i++)
            {
                File.WriteAllText(Path.Combine(_folder.Root, folder, $"f{i}.cs"), "x");
            }
        }

        return _folder.Root;
    }

    private static RepositoryState State(string root, bool dirty = false, string? branch = "main") =>
        new(root, "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678", branch, dirty, new Version(2, 45, 0));

    private const string ConfigWithCredentials = """
        [core]
        	repositoryformatversion = 0
        [remote "origin"]
        	url = https://usuario:ghp_S3cretT0ken@github.com/org/calc.git
        	fetch = +refs/heads/*:refs/remotes/origin/*
        [remote "upstream"]
        	url = git@github.com:otro/calc.git
        [remote "mirror"]
        	url = https://tokenSolo@gitlab.com/org/calc.git
        [branch "main"]
        	remote = origin
        """;

    [Fact]
    public async Task It_reads_the_branch_the_head_the_remotes_the_stack_and_the_main_folders_without_the_network()
    {
        var root = Repo(ConfigWithCredentials);
        var state = new FakeState(State(root, dirty: true));

        var reading = await new RepositoryReader(state).ReadAsync(root, CancellationToken.None);

        var node = reading.Node!;
        Assert.Equal(1, state.Calls);
        Assert.Equal("main", node.Branch);
        Assert.Equal("a1b2c3d", node.HeadShort);
        Assert.True(node.HasUncommittedChanges);
        Assert.Equal("csharp, dotnet", node.Stack);
        Assert.Equal(["docs/", "src/", "tests/"], node.MainFolders);
        Assert.DoesNotContain(node.MainFolders, f => f.StartsWith("bin", StringComparison.Ordinal) || f.StartsWith("obj", StringComparison.Ordinal) || f.StartsWith("node_modules", StringComparison.Ordinal) || f.StartsWith('.'));
        Assert.Equal(["origin", "upstream", "mirror"], node.Remotes.Select(r => r.Name));
    }

    [Fact]
    public async Task A_url_with_credentials_is_never_held_shown_or_saved_with_them()
    {
        var root = Repo(ConfigWithCredentials);
        var node = (await new RepositoryReader(new FakeState(State(root))).ReadAsync(root, CancellationToken.None)).Node!;

        Assert.Equal("https://github.com/org/calc.git", node.Remotes.Single(r => r.Name == "origin").Url);
        Assert.Equal("github.com:otro/calc.git", node.Remotes.Single(r => r.Name == "upstream").Url);
        Assert.Equal("https://gitlab.com/org/calc.git", node.Remotes.Single(r => r.Name == "mirror").Url);
        Assert.All(node.Remotes, r => Assert.False(RepositoryRemote.HasCredentials(r.Url)));
        Assert.Throws<ArgumentException>(() => new RepositoryRemote("origin", "https://usuario:" + Secret + "@github.com/org/calc.git"));

        // Nothing the node says, in either language, carries the secret.
        foreach (var text in new[] { Text(), Text(English) })
        {
            var view = new RepositoryNodeViewModel(node, null, new System.Windows.Point(0, 0), text);
            var everything = string.Join('\n', view.Title, view.BranchText, view.HeadText, view.RemotesText, view.ChangesText, view.StackText, view.AutomationName, string.Join('\n', view.RemoteLines));
            Assert.DoesNotContain(Secret, everything, StringComparison.Ordinal);
            Assert.DoesNotContain("usuario", everything, StringComparison.Ordinal);
            Assert.Contains("https://github.com/org/calc.git", everything, StringComparison.Ordinal);
        }

        // Nor the layout file the canvas saves with the node on it.
        var team = new TeamCanvasViewModel(Text());
        team.Load(Roster(), RepositoryReading.Of(node), CanvasLayout.Empty);
        var store = new FileCanvasLayoutStore(new ProjectStorageFiles());
        store.Save(_folder.Root, team.CaptureLayout());
        var saved = File.ReadAllText(store.GetLayoutPath(_folder.Root));
        Assert.DoesNotContain(Secret, saved, StringComparison.Ordinal);
        Assert.DoesNotContain("usuario", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("github.com", saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_changes_not_committed_come_in_the_colour_of_a_warning_with_what_happens()
    {
        var root = Repo(ConfigWithCredentials);
        var dirty = (await new RepositoryReader(new FakeState(State(root, dirty: true))).ReadAsync(root, CancellationToken.None)).Node!;
        var clean = (await new RepositoryReader(new FakeState(State(root, dirty: false))).ReadAsync(root, CancellationToken.None)).Node!;

        var warned = new RepositoryNodeViewModel(dirty, null, default, Text());
        var calm = new RepositoryNodeViewModel(clean, null, default, Text());

        Assert.True(warned.HasUncommittedChanges);
        Assert.Equal("Hay cambios sin guardar como versión: si trabajas en tu repositorio durante una ejecución, la tarea falla.", warned.ChangesText);
        Assert.False(calm.HasUncommittedChanges);
        Assert.Equal("sin cambios sin confirmar", calm.ChangesText);
    }

    [Fact]
    public async Task A_detached_head_and_a_repository_without_commits_are_said_in_words()
    {
        var root = Repo(ConfigWithCredentials);
        var detached = (await new RepositoryReader(new FakeState(State(root, branch: null))).ReadAsync(root, CancellationToken.None)).Node!;

        var view = new RepositoryNodeViewModel(detached, null, default, Text());
        Assert.Equal("sin rama (versión suelta)", view.BranchText);

        var none = new RepositoryNodeViewModel(detached with { HeadShort = null }, null, default, Text());
        Assert.Equal("sin versiones guardadas", none.HeadText);
        Assert.Equal("no branch (loose version)", new RepositoryNodeViewModel(detached, null, default, Text(English)).BranchText);
    }

    [Fact]
    public async Task A_remote_of_a_worktree_is_read_from_the_common_configuration()
    {
        var main = Path.Combine(_folder.Root, "principal");
        var worktree = Path.Combine(_folder.Root, "copia");
        Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "copia"));
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(main, ".git", "config"), "[remote \"origin\"]\n\turl = https://u:" + Secret + "@host.example/r.git\n");
        File.WriteAllText(Path.Combine(main, ".git", "worktrees", "copia", "commondir"), "../..");
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + Path.Combine(main, ".git", "worktrees", "copia"));

        var remotes = RepositoryReader.ReadRemotes(worktree);

        Assert.Equal("https://host.example/r.git", remotes.Single().Url);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Without_the_git_layer_or_when_it_fails_the_node_says_why_instead_of_guessing()
    {
        var root = Repo(ConfigWithCredentials);

        var missing = await new RepositoryReader(null).ReadAsync(root, CancellationToken.None);
        var failed = await new RepositoryReader(new FakeState(failure: new InvalidOperationException("not a git repository"))).ReadAsync(root, CancellationToken.None);

        Assert.Null(missing.Node);
        Assert.Equal(RepositoryProblems.NotComposed, missing.Problem);
        Assert.Null(failed.Node);
        Assert.Equal("not a git repository", failed.Problem);
        var view = new RepositoryNodeViewModel(null, failed.Problem, default, Text());
        Assert.Equal("No se pudo leer el repositorio (not a git repository).", view.ProblemText);
    }

    [Fact]
    public async Task The_reason_a_repository_could_not_be_read_never_carries_the_credentials_of_an_address()
    {
        var root = Repo(ConfigWithCredentials);
        var failure = new InvalidOperationException("fatal: unable to access 'https://usuario:" + Secret + "@github.com/org/calc.git/': could not resolve host");

        var reading = await new RepositoryReader(new FakeState(failure: failure)).ReadAsync(root, CancellationToken.None);

        Assert.DoesNotContain(Secret, reading.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("usuario", reading.Problem, StringComparison.Ordinal);
        Assert.Contains("https://github.com/org/calc.git", reading.Problem, StringComparison.Ordinal);
    }

    /// <summary>The five ways a secret hides in an address (F13-3): none of them reaches a node, a text, the layout or a reason.</summary>
    [Fact]
    public async Task No_form_of_address_leaks_its_secret_to_the_node_the_layout_the_accessible_name_or_the_errors()
    {
        string[] secrets = ["S3CRETO1", "S3CRETO2", "S3CRETO3", "S3CRETO4", "S3CRETO5"];
        var config = string.Join(
            '\n',
            "[remote \"a\"]\n\turl = https://usuario:" + secrets[0] + "@github.com/o/a.git",
            "[remote \"b\"]\n\turl = usuario" + secrets[1] + "@github.com:o/b.git",
            "[remote \"c\"]\n\turl = https://user:pa?" + secrets[2] + "@host.example/o/c.git",
            "[remote \"d\"]\n\turl = https://github.com/o/d.git?token=" + secrets[3] + "#frag" + secrets[3],
            "[remote \"e\"]\n\turl = ssh://git:" + secrets[4] + "@host.example:2222/o/e.git");
        var root = Repo(config);
        var node = (await new RepositoryReader(new FakeState(State(root))).ReadAsync(root, CancellationToken.None)).Node!;
        Assert.Equal(5, node.Remotes.Count);
        Assert.All(node.Remotes, r => Assert.False(RepositoryRemote.HasCredentials(r.Url)));

        var team = new TeamCanvasViewModel(Text());
        team.Load(Roster(), RepositoryReading.Of(node), CanvasLayout.Empty);
        var store = new FileCanvasLayoutStore(new ProjectStorageFiles());
        store.Save(_folder.Root, team.CaptureLayout());
        var view = team.Repository!;
        var everything = string.Join(
            '\n',
            view.Title,
            view.RemotesText,
            view.AutomationName,
            string.Join('\n', view.RemoteLines),
            view.ProblemText,
            File.ReadAllText(store.GetLayoutPath(_folder.Root)));

        // The reason of an error that quotes an address is cleaned too.
        var failure = new InvalidOperationException("fatal: unable to access 'https://usuario:" + secrets[0] + "@github.com/o/a.git' and 'usuario" + secrets[1] + "@github.com:o/b.git?token=" + secrets[3] + "'");
        var reading = await new RepositoryReader(new FakeState(failure: failure)).ReadAsync(root, CancellationToken.None);
        everything += "\n" + reading.Problem + "\n" + new RepositoryNodeViewModel(null, reading.Problem, default, Text()).AutomationName;

        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_configuration_file_past_the_cap_is_not_read_and_the_node_says_so()
    {
        var root = Repo(ConfigWithCredentials);
        File.AppendAllText(Path.Combine(root, ".git", "config"), "\n#" + new string('x', (int)RepositoryReader.MaxConfigBytes));

        var reading = await new RepositoryReader(new FakeState(State(root))).ReadAsync(root, CancellationToken.None);

        Assert.Empty(reading.Node!.Remotes);
        Assert.Equal(RepositoryProblems.ConfigurationTooBig, reading.RemotesNote);
        var view = new RepositoryNodeViewModel(reading.Node, null, default, Text(), reading.RemotesNote);
        Assert.Equal("No se leyeron los remotos. El fichero de configuración del repositorio es demasiado grande.", view.RemotesText);
    }

    [Fact]
    public async Task A_configuration_that_cannot_be_read_gives_no_remotes_never_an_error()
    {
        Directory.CreateDirectory(Path.Combine(_folder.Root, ".git"));
        Assert.Empty(RepositoryReader.ReadRemotes(_folder.Root));
        Assert.Empty(RepositoryReader.ReadRemotes(Path.Combine(_folder.Root, "no existe")));
        await Task.CompletedTask;
    }
}
