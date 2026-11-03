using System.IO;
using System.Text;
using AgentStudio.Infrastructure.Persistence;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// The files other modules write in a project's <c>.agentstudio</c> (the journal, the canvas layout, the verification
/// profile) through the policy of the storage: what a caller may name, what a second writer gets, and what is refused.
/// </summary>
public sealed class ProjectStorageFilesTests : IDisposable
{
    private const int ErrorSharingViolation = 32;

    private readonly TempProjectFolder _folder = new();
    private readonly PlantedLinks _links = new();
    private readonly ProjectStorageFiles _files = new();

    public ProjectStorageFilesTests()
    {
        Outside = Path.Combine(Path.GetDirectoryName(_folder.Root)!, "fuera del proyecto");
        Directory.CreateDirectory(Outside);
    }

    /// <summary>A folder next to the project, outside it: where a planted link points.</summary>
    private string Outside { get; }

    public void Dispose()
    {
        // The links go first, as links: the cleanup never walks into what they point to.
        _links.Dispose();
        _folder.Dispose();
    }

    private string[] OutsideEntries() => Directory.GetFileSystemEntries(Outside, "*", SearchOption.AllDirectories);

    [Theory]
    [InlineData("../fuera.json")]
    [InlineData("bitacora/../../fuera.md")]
    [InlineData("./verification.json")]
    [InlineData("bitacora//2026-03-10.md")]
    [InlineData("a/b/c.json")]
    [InlineData(@"C:\fuera.json")]
    [InlineData("verification.json:oculto")]
    public void A_path_that_is_not_a_file_of_the_storage_is_refused_before_anything_is_created(string relativePath)
    {
        Assert.Throws<ArgumentException>(() => _files.Replace(_folder.Root, relativePath, "x"u8));

        Assert.False(Directory.Exists(_folder.DataDirectory));
        Assert.Empty(OutsideEntries());
    }

    [Fact]
    public void A_project_folder_that_does_not_exist_is_not_created()
    {
        var missing = Path.Combine(_folder.Root, "no existe");

        Assert.Throws<DirectoryNotFoundException>(() => _files.OpenForAppend(missing, "bitacora/2026-03-10.md"));

        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void A_second_writer_gets_a_sharing_violation_it_may_retry_not_a_refusal_of_the_storage()
    {
        using var first = _files.OpenForAppend(_folder.Root, "bitacora/2026-03-10.md");

        var second = Assert.ThrowsAny<IOException>(() => _files.OpenForAppend(_folder.Root, "bitacora/2026-03-10.md"));

        Assert.IsNotType<ProjectStorageIntegrityException>(second);
        Assert.Equal(ErrorSharingViolation, second.HResult & 0xFFFF);
    }

    [Fact]
    public void A_file_opened_to_append_can_still_be_read_by_others()
    {
        using (var stream = _files.OpenForAppend(_folder.Root, "bitacora/2026-03-10.md"))
        {
            stream.Write("línea\n"u8);
            stream.Flush();

            using var reader = new FileStream(Path.Combine(_folder.DataDirectory, "bitacora", "2026-03-10.md"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Assert.Equal("línea\n", new StreamReader(reader, Encoding.UTF8).ReadToEnd());
        }
    }

    [Fact]
    public void A_folder_where_the_file_should_be_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(_folder.DataDirectory, "bitacora", "2026-03-10.md"));

        var refusal = Assert.Throws<ProjectStorageIntegrityException>(() => _files.OpenForAppend(_folder.Root, "bitacora/2026-03-10.md"));

        Assert.Contains("2026-03-10.md", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_junction_where_the_file_should_be_is_refused_and_nothing_is_written_outside()
    {
        Directory.CreateDirectory(Path.Combine(_folder.DataDirectory, "bitacora"));
        _links.Junction(Path.Combine(_folder.DataDirectory, "bitacora", "2026-03-10.md"), Outside);

        Assert.Throws<ProjectStorageIntegrityException>(() => _files.OpenForAppend(_folder.Root, "bitacora/2026-03-10.md"));
        Assert.Throws<ProjectStorageIntegrityException>(() => _files.Replace(_folder.Root, "bitacora/2026-03-10.md", "x"u8));

        Assert.Empty(OutsideEntries());
    }

    [Fact]
    public void A_replaced_file_holds_exactly_the_new_content_and_no_partial_file_is_left()
    {
        _files.Replace(_folder.Root, "canvas-layout.json", "primero"u8);

        _files.Replace(_folder.Root, "canvas-layout.json", "segundo"u8);

        Assert.Equal("segundo", File.ReadAllText(Path.Combine(_folder.DataDirectory, "canvas-layout.json")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_folder.DataDirectory, "tmp")));
    }
}
