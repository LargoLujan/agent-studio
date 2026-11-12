using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Desktop.Repository;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Memory;

namespace AgentStudio.Tests.Desktop.RepositoryScreen;

/// <summary>
/// «Repositorio y cambios» over the real hardened history reader and a temporary repository (no network): the screen reads the history
/// through the layer and never starts git itself, and a folder the layer does not accept says why.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class RepositoryViewModelGitTests : IDisposable
{
    private readonly TempGitRepo _repo = TempGitRepo.Create();
    private readonly List<string> _folders = [];

    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.FromHours(2));

    public void Dispose()
    {
        _repo.Dispose();
        foreach (var folder in _folders)
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private RepositoryViewModel Model(FakeAnnotator? annotator = null) => new(
        new FakeWorkspace(),
        new RepositoryHistoryReader(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(_repo.WorktreeRoot)),
        annotator ?? new FakeAnnotator(),
        new FakeActivity(),
        new FakeRemotes(),
        new FakeClock(Now));

    [Fact]
    public async Task The_summary_the_branches_and_the_graph_come_through_the_hardened_layer()
    {
        _repo.Write("a.txt", "a\n");
        _repo.Run("add", "-A");
        _repo.Run("commit", "-q", "-m", "segundo cambio");
        _repo.Run("branch", "agentstudio/run-20261112-a1b2c3");
        _repo.Write("nuevo.txt", "sin confirmar\n");
        var model = Model();

        await model.SetProjectAsync(_repo.Root);

        Assert.Null(model.RefusalText);
        Assert.Equal("main", model.BranchText);
        Assert.Equal(_repo.Head[..8], model.HeadText);
        Assert.Equal("Sí, hay cambios sin guardar.", model.ChangesText);
        Assert.Equal(["agentstudio/run-20261112-a1b2c3", "main"], model.Branches.Select(b => b.Name).Order(StringComparer.Ordinal));
        Assert.True(model.Branches.Single(b => b.Name == "agentstudio/run-20261112-a1b2c3").IsRun);
        Assert.Equal(2, model.Commits.Count);
        Assert.Equal("segundo cambio", model.Commits[0].Subject);
        Assert.Equal("Agent Studio Tests", model.Commits[0].Author);
        Assert.All(model.Commits, c => Assert.Equal(40, c.Id.Length));
    }

    [Fact]
    public async Task A_folder_that_is_not_a_repository_is_refused_with_its_reason_and_not_shown_as_empty()
    {
        var folder = Path.Combine(Path.GetTempPath(), "as repo vacio " + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(folder);
        _folders.Add(folder);
        var model = Model();

        await model.SetProjectAsync(folder);

        Assert.Equal("Agent Studio no lee este repositorio: la carpeta no es un repositorio de git.", model.RefusalText);
        Assert.Empty(model.Commits);
        Assert.Null(model.CommitsNote);
        Assert.Null(model.BranchesNote);
    }

    [Fact]
    public async Task A_repository_whose_configuration_could_run_a_program_is_refused_by_the_layer()
    {
        _repo.Run("config", "filter.format.clean", "sh tools/filter.sh");
        _repo.Run("config", "filter.format.smudge", "sh tools/filter.sh");
        var model = Model();

        await model.SetProjectAsync(_repo.Root);

        Assert.Equal("Agent Studio no lee este repositorio: puede lanzar un programa del repositorio. Por seguridad, no se lee.", model.RefusalText);
        Assert.Empty(model.Commits);
    }
}
