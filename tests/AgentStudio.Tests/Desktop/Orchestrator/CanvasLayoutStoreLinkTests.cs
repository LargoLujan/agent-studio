using System.IO;
using System.Windows;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Tests.Infrastructure.Persistence;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// Links left in a project before the canvas saves its layout there (ADR-0018: in the model). A junction at
/// <c>.agentstudio</c> or at <c>tmp</c>, where the new file is written before it replaces the old one, or a layout file
/// that is a second name of another file: the save is refused and nothing is written through the link.
/// </summary>
public sealed class CanvasLayoutStoreLinkTests : IDisposable
{
    private readonly TempProjectFolder _folder = new();
    private readonly PlantedLinks _links = new();

    public CanvasLayoutStoreLinkTests()
    {
        Outside = Path.Combine(Path.GetDirectoryName(_folder.Root)!, "fuera del proyecto");
        Directory.CreateDirectory(Outside);
    }

    /// <summary>A folder next to the project, outside it: where a planted link points.</summary>
    private string Outside { get; }

    private string LayoutFile => Path.Combine(_folder.DataDirectory, FileCanvasLayoutStore.FileName);

    public void Dispose()
    {
        // The links go first, as links: the cleanup never walks into what they point to.
        _links.Dispose();
        _folder.Dispose();
    }

    private static FileCanvasLayoutStore NewStore() => new(new ProjectStorageFiles());

    private static CanvasLayout Layout() => new(new Dictionary<string, Point>(StringComparer.Ordinal) { ["T1"] = new Point(320, 160) });

    private string[] OutsideEntries() => Directory.GetFileSystemEntries(Outside, "*", SearchOption.AllDirectories);

    [Fact]
    public void A_junction_at_the_data_folder_is_refused_and_nothing_is_saved_outside()
    {
        _links.Junction(_folder.DataDirectory, Outside);

        var refusal = Assert.Throws<ProjectStorageIntegrityException>(() => NewStore().Save(_folder.Root, Layout()));

        Assert.Contains(".agentstudio", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(OutsideEntries());
    }

    [Fact]
    public void A_junction_at_the_temporary_folder_is_refused_and_nothing_is_saved_outside()
    {
        Directory.CreateDirectory(_folder.DataDirectory);
        _links.Junction(Path.Combine(_folder.DataDirectory, "tmp"), Outside);

        Assert.Throws<ProjectStorageIntegrityException>(() => NewStore().Save(_folder.Root, Layout()));

        Assert.Empty(OutsideEntries());
        Assert.False(File.Exists(LayoutFile));
    }

    [Fact]
    public void A_layout_file_that_is_a_second_name_of_another_file_is_refused_and_that_file_is_not_written()
    {
        var victim = Path.Combine(Outside, "importante.json");
        File.WriteAllText(victim, "no se toca");
        Directory.CreateDirectory(_folder.DataDirectory);
        _links.HardLink(LayoutFile, victim);

        Assert.Throws<ProjectStorageIntegrityException>(() => NewStore().Save(_folder.Root, Layout()));

        Assert.Equal("no se toca", File.ReadAllText(victim));
        Assert.Equal("no se toca", File.ReadAllText(LayoutFile));
    }

    [Fact]
    public void A_project_without_links_saves_and_reads_its_layout_and_leaves_no_temporary_file()
    {
        var store = NewStore();

        store.Save(_folder.Root, Layout());
        store.Save(_folder.Root, Layout());

        Assert.Equal(new Point(320, 160), store.Load(_folder.Root).Positions["T1"]);
        Assert.Equal([FileCanvasLayoutStore.FileName], Directory.EnumerateFiles(_folder.DataDirectory).Select(Path.GetFileName));
        var temporary = Path.Combine(_folder.DataDirectory, "tmp");
        Assert.True(!Directory.Exists(temporary) || Directory.EnumerateFileSystemEntries(temporary).Count() == 0);
        Assert.Empty(OutsideEntries());
    }
}
