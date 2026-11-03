using System.IO;
using System.Text;
using AgentStudio.Application.Projects;
using AgentStudio.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// The checked read of a project's <c>.agentstudio</c> (docs/14 D-31; ADR-0010, addendum H-01): the same links the
/// writes refuse are refused when reading, so a link planted as a day of the journal can never feed a file from outside
/// the project into what Agent Studio writes (the daily summary, the versioned Markdown).
/// </summary>
public sealed class ProjectStorageFilesReadTests : IDisposable
{
    private const string Day = "bitacora/2026-03-10.md";

    private readonly TempProjectFolder _folder = new();
    private readonly PlantedLinks _links = new();
    private readonly IProjectStorageReader _reader = new ProjectStorageFiles();

    public ProjectStorageFilesReadTests()
    {
        Outside = Path.Combine(Path.GetDirectoryName(_folder.Root)!, "fuera del proyecto");
        Directory.CreateDirectory(Outside);
    }

    /// <summary>A folder next to the project, outside it: where a planted link points.</summary>
    private string Outside { get; }

    private string JournalDirectory => Path.Combine(_folder.DataDirectory, "bitacora");

    private string DayPath => Path.Combine(JournalDirectory, "2026-03-10.md");

    public void Dispose()
    {
        // The links go first, as links: the cleanup never walks into what they point to.
        _links.Dispose();
        _folder.Dispose();
    }

    private static string ReadAll(Stream stream)
    {
        using (stream)
        {
            return new StreamReader(stream, Encoding.UTF8).ReadToEnd();
        }
    }

    /// <summary>A journal day outside the project, with text that must never be read as the project's.</summary>
    private string ForeignDay()
    {
        var foreign = Path.Combine(Outside, "bitacora");
        Directory.CreateDirectory(foreign);
        var path = Path.Combine(foreign, "2026-03-10.md");
        File.WriteAllText(path, "- **09:00:00** · sesión `ajena` · #prompt · «texto de fuera»");
        return path;
    }

    [Fact]
    public void A_day_of_the_journal_is_read_whole()
    {
        Directory.CreateDirectory(JournalDirectory);
        File.WriteAllText(DayPath, "# Bitácora · 2026-03-10\n- línea\n");

        Assert.Equal("# Bitácora · 2026-03-10\n- línea\n", ReadAll(_reader.OpenForRead(_folder.Root, Day)));
    }

    [Fact]
    public void A_missing_day_is_not_found_and_reading_creates_nothing()
    {
        Assert.Throws<FileNotFoundException>(() => _reader.OpenForRead(_folder.Root, Day));

        Assert.False(Directory.Exists(_folder.DataDirectory));
    }

    [Fact]
    public void A_missing_journal_folder_is_not_found_and_is_not_created()
    {
        Directory.CreateDirectory(_folder.DataDirectory);

        Assert.Throws<FileNotFoundException>(() => _reader.OpenForRead(_folder.Root, Day));

        Assert.False(Directory.Exists(JournalDirectory));
    }

    [Theory]
    [InlineData("../fuera.json")]
    [InlineData("bitacora/../../fuera.md")]
    [InlineData("./verification.json")]
    [InlineData("bitacora//2026-03-10.md")]
    [InlineData("a/b/c.json")]
    [InlineData(@"C:\fuera.json")]
    [InlineData("verification.json:oculto")]
    public void A_path_that_is_not_a_file_of_the_storage_is_refused(string relativePath)
    {
        Assert.Throws<ArgumentException>(() => _reader.OpenForRead(_folder.Root, relativePath));
    }

    [Fact]
    public void A_junction_at_the_data_folder_is_refused_and_nothing_outside_is_read()
    {
        ForeignDay();
        _links.Junction(_folder.DataDirectory, Outside);

        var refusal = Assert.Throws<ProjectStorageIntegrityException>(() => _reader.OpenForRead(_folder.Root, Day));

        Assert.Contains(".agentstudio", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_junction_at_the_journal_folder_is_refused_and_nothing_outside_is_read()
    {
        ForeignDay();
        Directory.CreateDirectory(_folder.DataDirectory);
        _links.Junction(JournalDirectory, Path.Combine(Outside, "bitacora"));

        var refusal = Assert.Throws<ProjectStorageIntegrityException>(() => _reader.OpenForRead(_folder.Root, Day));

        Assert.Contains("bitacora", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_junction_where_the_day_should_be_is_refused()
    {
        Directory.CreateDirectory(JournalDirectory);
        _links.Junction(DayPath, Outside);

        var refusal = Assert.Throws<ProjectStorageIntegrityException>(() => _reader.OpenForRead(_folder.Root, Day));

        Assert.Contains("2026-03-10.md", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_day_that_is_a_second_name_of_another_file_is_refused()
    {
        var victim = ForeignDay();
        Directory.CreateDirectory(JournalDirectory);
        _links.HardLink(DayPath, victim);

        var refusal = Assert.Throws<ProjectStorageIntegrityException>(() => _reader.OpenForRead(_folder.Root, Day));

        Assert.Contains("names", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_where_the_day_should_be_is_refused()
    {
        Directory.CreateDirectory(DayPath);

        Assert.Throws<ProjectStorageIntegrityException>(() => _reader.OpenForRead(_folder.Root, Day));
    }

    [Fact]
    public void A_day_the_journal_is_appending_to_can_be_read_meanwhile()
    {
        var writer = new ProjectStorageFiles();
        using var appending = writer.OpenForAppend(_folder.Root, Day);
        appending.Write("- **09:00:00** · sesión `abc` · #prompt · «hola»\n"u8);
        appending.Flush();

        Assert.Equal("- **09:00:00** · sesión `abc` · #prompt · «hola»\n", ReadAll(_reader.OpenForRead(_folder.Root, Day)));
    }

    [Fact]
    public void The_persistence_of_the_app_reads_the_storage_through_the_same_checks()
    {
        var services = new ServiceCollection();
        services.AddAgentStudioPersistence(new DecisionLogAnchorOptions(Path.Combine(Outside, "anclas")));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Directory.CreateDirectory(_folder.DataDirectory);
        _links.Junction(JournalDirectory, Outside);

        Assert.Throws<ProjectStorageIntegrityException>(() => provider.GetRequiredService<IProjectStorageReader>().OpenForRead(_folder.Root, Day));
    }

    [Fact]
    public void A_project_reached_through_a_junction_above_its_root_is_read_from_its_real_folder()
    {
        // C:\dev → D:\dev, say: the folders above the project are the user's to arrange.
        var container = Path.GetDirectoryName(_folder.Root)!;
        var project = Path.Combine(container, "real", "proyecto");
        Directory.CreateDirectory(Path.Combine(project, ".agentstudio", "bitacora"));
        File.WriteAllText(Path.Combine(project, ".agentstudio", "bitacora", "2026-03-10.md"), "real");
        _links.Junction(Path.Combine(container, "enlace"), Path.Combine(container, "real"));

        Assert.Equal("real", ReadAll(_reader.OpenForRead(Path.Combine(container, "enlace", "proyecto"), Day)));
    }
}
