using System.IO;
using System.Text;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Tests.Infrastructure.Persistence;
using AgentStudio.Tests.Memory;
using Microsoft.Extensions.DependencyInjection;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// «Bitácora de hoy» reads the day through the checked storage reader (docs/14 D-31, H-01): a link planted as a day of
/// the journal is refused by the reader and nothing is shown, and without that reader the journal is not read at all.
/// </summary>
public sealed class JournalViewModelTests
{
    private const string Root = @"C:\proyectos\calc";

    // 2026-10-15 23:30 UTC is already the 16th in the +02:00 local zone of FakeClock.
    private static FakeClock Clock(int hour = 10, int day = 15) =>
        new(new DateTimeOffset(2026, 10, day, hour, 0, 0, TimeSpan.FromHours(2)));

    [Fact]
    public async Task WithoutProject_SaysToOpenAFolder()
    {
        var reader = new FakeStorageReader();
        var journal = new JournalViewModel(Clock(), reader);

        await journal.SetProjectAsync(null);

        Assert.Null(journal.CurrentFilePath);
        Assert.Equal(string.Empty, journal.Text);
        Assert.Contains("Abre una carpeta", journal.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(reader.Reads);
    }

    [Fact]
    public async Task TodaysFile_IsReadThroughTheCheckedReader()
    {
        var reader = new FakeStorageReader { Files = { ["bitacora/2026-10-15.md"] = "# 2026-10-15\n- Hecho: algo\n" } };
        var journal = new JournalViewModel(Clock(), reader);

        await journal.SetProjectAsync(Root);

        Assert.Equal("# 2026-10-15\n- Hecho: algo\n", journal.Text);
        Assert.Null(journal.StatusMessage);
        Assert.Equal([(Root, "bitacora/2026-10-15.md")], reader.Reads);
    }

    [Fact]
    public async Task MissingFile_ShowsAnHonestEmptyState()
    {
        var reader = new FakeStorageReader { Files = { ["bitacora/2026-10-14.md"] = "ayer" } };
        var journal = new JournalViewModel(Clock(), reader);

        await journal.SetProjectAsync(Root);

        Assert.Equal(string.Empty, journal.Text);
        Assert.Contains("No hay bitácora de hoy", journal.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("2026-10-15.md", journal.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingJournalFolder_IsTheSameHonestEmptyState()
    {
        var reader = new FakeStorageReader { Failure = new DirectoryNotFoundException("No existe .agentstudio/bitacora.") };
        var journal = new JournalViewModel(Clock(), reader);

        await journal.SetProjectAsync(Root);

        Assert.Equal(string.Empty, journal.Text);
        Assert.Contains("No hay bitácora de hoy", journal.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsesTheLocalDayNotTheUtcDay()
    {
        var reader = new FakeStorageReader { Files = { ["bitacora/2026-10-16.md"] = "hoy local" } };
        var journal = new JournalViewModel(new FakeClock(new DateTimeOffset(2026, 10, 15, 23, 30, 0, TimeSpan.Zero)), reader);

        await journal.SetProjectAsync(Root);

        Assert.Equal("hoy local", journal.Text);
        Assert.Equal([(Root, "bitacora/2026-10-16.md")], reader.Reads);
    }

    [Fact]
    public async Task Refresh_ReadsNewContentAndNoticesWhenTheFileAppears()
    {
        var reader = new FakeStorageReader();
        var journal = new JournalViewModel(Clock(), reader);
        await journal.SetProjectAsync(Root);
        Assert.NotNull(journal.StatusMessage);

        reader.Files["bitacora/2026-10-15.md"] = "primera";
        await journal.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("primera", journal.Text);
        Assert.Null(journal.StatusMessage);

        reader.Files["bitacora/2026-10-15.md"] = "primera y segunda";
        await journal.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("primera y segunda", journal.Text);
    }

    /// <summary>The reader refuses a day of the journal that is a link (or reaches another place): nothing of it is shown.</summary>
    [Fact]
    public async Task ADayTheReaderRefuses_ShowsWhyAndNothingOfIt()
    {
        var reader = new FakeStorageReader
        {
            Files = { ["bitacora/2026-10-15.md"] = "contenido de fuera del proyecto" },
            Failure = new IOException(@"'.agentstudio\bitacora\2026-10-15.md' is a link: Agent Studio never follows one."),
        };
        var journal = new JournalViewModel(Clock(), reader);

        await journal.SetProjectAsync(Root);

        Assert.Equal(string.Empty, journal.Text);
        Assert.Equal(@"No se pudo leer la bitácora: '.agentstudio\bitacora\2026-10-15.md' is a link: Agent Studio never follows one.", journal.StatusMessage);
    }

    /// <summary>Without the checked reader the day is never opened directly, even when the file is there.</summary>
    [Fact]
    public async Task WithoutTheCheckedReader_TheJournalIsNotReadAtAll()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".agentstudio/bitacora/2026-10-15.md", "no se debe leer");
        var journal = new JournalViewModel(Clock());

        await journal.SetProjectAsync(temp.Path);

        Assert.Equal(string.Empty, journal.Text);
        Assert.Equal("La bitácora no se puede leer: falta la lectura comprobada de .agentstudio (sin ella no se abre ningún fichero del proyecto).", journal.StatusMessage);
    }

    /// <summary>
    /// In the app as composed (<see cref="AppComposition.AddAgentStudioApp"/>), the panel gets the checked reader of the
    /// persistence, so today's day is shown. Nothing is shown on screen: only the composed objects are touched.
    /// </summary>
    [Fact]
    public async Task In_the_composed_app_the_journal_reads_through_the_checked_reader_of_the_persistence()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("calc");
        temp.WriteFile("calc/.agentstudio/bitacora/2026-10-15.md", "# 2026-10-15\n- Hecho: algo\n");
        using var provider = ComposedApp(temp);
        var journal = provider.GetRequiredService<JournalViewModel>();

        await journal.SetProjectAsync(project);

        Assert.Equal("# 2026-10-15\n- Hecho: algo\n", journal.Text);
        Assert.Null(journal.StatusMessage);
    }

    /// <summary>
    /// A junction planted as the journal folder of the project is refused by the reader of the composed app, and nothing
    /// of what it points to is shown (the junction is made with <c>mklink /J</c>, no window, and removed as a link).
    /// </summary>
    [Fact]
    public async Task In_the_composed_app_a_link_planted_as_the_journal_folder_is_refused_and_nothing_of_it_is_shown()
    {
        using var temp = new TempDirectory();
        using var links = new PlantedLinks();
        var project = temp.CreateDirectory("calc");
        temp.WriteFile("fuera/2026-10-15.md", "- texto de fuera del proyecto");
        temp.CreateDirectory("calc/.agentstudio");
        links.Junction(temp.Combine("calc", ".agentstudio", "bitacora"), temp.Combine("fuera"));
        using var provider = ComposedApp(temp);
        var journal = provider.GetRequiredService<JournalViewModel>();

        await journal.SetProjectAsync(project);

        Assert.Equal(string.Empty, journal.Text);
        Assert.StartsWith("No se pudo leer la bitácora: ", journal.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("bitacora", journal.StatusMessage, StringComparison.Ordinal);
    }

    /// <summary>The app's composition over temporary folders, with the clock of these tests.</summary>
    private static ServiceProvider ComposedApp(TempDirectory temp)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock());
        services.AddWindowsDoubles();
        services.AddAgentStudioApp(new AppFolders(new AppDataPaths(temp.Combine("roaming")), new LocalDataPaths(temp.Combine("local")), null));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>The days of the journal by their path under <c>.agentstudio</c>; what is opened is recorded.</summary>
    private sealed class FakeStorageReader : IProjectStorageReader
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public List<(string Root, string Path)> Reads { get; } = [];

        /// <summary>When set, every read throws it (a refused link, a folder that is not there).</summary>
        public Exception? Failure { get; set; }

        public Stream OpenForRead(string projectRoot, string relativePath)
        {
            Reads.Add((projectRoot, relativePath));
            if (Failure is not null)
            {
                throw Failure;
            }

            return Files.TryGetValue(relativePath, out var content)
                ? new MemoryStream(Encoding.UTF8.GetBytes(content))
                : throw new FileNotFoundException("The file does not exist.", relativePath);
        }
    }
}
