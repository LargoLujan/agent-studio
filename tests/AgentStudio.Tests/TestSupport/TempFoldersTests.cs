using System.IO;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Infrastructure.Persistence;

namespace AgentStudio.Tests.TestSupport;

/// <summary>
/// The cleanup of the temporary folders the tests make: Directory.Delete alone trips over a junction (it removes the link,
/// then throws and leaves the emptied parents), over read-only entries and over a handle still open, so every helper
/// deletes through <see cref="TempFolders"/>, which never walks into a link.
/// </summary>
public sealed class TempFoldersTests
{
    [Fact]
    public void A_junction_inside_is_removed_as_a_link_and_the_whole_folder_goes()
    {
        using var outside = new TempDirectory();
        var victim = outside.WriteFile("victim.txt", "keep");
        using var links = new PlantedLinks();
        var temp = new TempDirectory();
        temp.WriteFile("real/file.txt", "x");
        links.Junction(temp.Combine("out"), outside.Path);

        temp.Dispose();

        Assert.False(Directory.Exists(temp.Path), "the folder with a junction inside was left behind");
        Assert.Equal("keep", File.ReadAllText(victim));
    }

    [Fact]
    public void A_junction_loop_inside_does_not_stop_the_cleanup()
    {
        using var links = new PlantedLinks();
        var temp = new TempDirectory();
        temp.WriteFile("real/file.txt", "x");
        links.Junction(temp.Combine("link"), temp.Combine("real"));
        links.Junction(temp.Combine("real", "loop"), temp.Path);

        temp.Dispose();

        Assert.False(Directory.Exists(temp.Path), "the folder with a junction loop was left behind");
    }

    [Fact]
    public void Read_only_files_and_folders_are_deleted()
    {
        var temp = new TempDirectory();
        var file = temp.WriteFile("objects/ab/cdef", "blob");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        var folder = new DirectoryInfo(temp.CreateDirectory("locked"));
        folder.Attributes |= FileAttributes.ReadOnly;

        temp.Dispose();

        Assert.False(Directory.Exists(temp.Path), "read-only entries kept the folder alive");
    }

    [Fact]
    public void Read_only_files_behind_a_junction_keep_their_attribute()
    {
        using var outside = new TempDirectory();
        var victim = outside.WriteFile("pack.idx", "pack");
        File.SetAttributes(victim, FileAttributes.ReadOnly);
        using var links = new PlantedLinks();
        var project = new TempProjectFolder();
        links.Junction(Path.Combine(project.Root, "linked"), outside.Path);

        project.Dispose();

        Assert.False(Directory.Exists(project.Root), "the project folder was left behind");
        Assert.True(File.GetAttributes(victim).HasFlag(FileAttributes.ReadOnly), "the cleanup cleared an attribute through a junction");
        File.SetAttributes(victim, FileAttributes.Normal);
    }

    [Fact]
    public void A_folder_that_is_itself_a_junction_loses_the_link_only()
    {
        using var outside = new TempDirectory();
        var victim = outside.WriteFile("victim.txt", "keep");
        using var container = new TempDirectory();
        using var links = new PlantedLinks();
        var link = container.Combine("link");
        links.Junction(link, outside.Path);

        Assert.True(TempFolders.Delete(link));

        Assert.False(Directory.Exists(link));
        Assert.Equal("keep", File.ReadAllText(victim));
    }

    [Fact]
    public void A_file_link_inside_is_removed_as_a_link_and_its_target_keeps_its_attribute()
    {
        using var outside = new TempDirectory();
        var victim = outside.WriteFile("victim.txt", "keep");
        File.SetAttributes(victim, FileAttributes.ReadOnly);
        var temp = new TempDirectory();
        try
        {
            File.CreateSymbolicLink(temp.Combine("link.txt"), victim);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            temp.Dispose();
            Assert.Skip("Creating a symbolic link needs Developer Mode or elevation on this machine.");
        }

        temp.Dispose();

        Assert.False(Directory.Exists(temp.Path), "the folder with a file link inside was left behind");
        Assert.Equal("keep", File.ReadAllText(victim));
        Assert.True(File.GetAttributes(victim).HasFlag(FileAttributes.ReadOnly), "the cleanup cleared an attribute through a file link");
        File.SetAttributes(victim, FileAttributes.Normal);
    }

    [Fact]
    public void A_folder_still_in_use_is_reported_in_the_test_output_and_the_test_goes_on()
    {
        var temp = new TempDirectory();
        var database = temp.WriteFile(".agentstudio/agentstudio.db", "data");

        bool deleted;
        using (new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            deleted = TempFolders.Delete(temp.Path);
        }

        Assert.False(deleted);
        Assert.True(Directory.Exists(temp.Path));
        Assert.Contains(temp.Path, TestContext.Current.TestOutputHelper!.Output, StringComparison.Ordinal);
        Assert.True(TempFolders.Delete(temp.Path));
        Assert.False(Directory.Exists(temp.Path));
    }
}
