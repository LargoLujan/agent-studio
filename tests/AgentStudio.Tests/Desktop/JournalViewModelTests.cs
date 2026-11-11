using System.IO;
using System.Text;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Tests.Infrastructure.Persistence;
using AgentStudio.Tests.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
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

    /// <summary>
    /// The review of V31: laying out the readable view takes seconds for a long day (5,000 lines), on the UI thread, when a
    /// project opens. A long day draws its title and its latest entries, whole; all of it is there as plain text, on demand.
    /// </summary>
    [Fact]
    public async Task A_long_day_draws_its_latest_entries_and_shows_all_of_it_as_plain_text_on_demand()
    {
        var day = LongDay(entries: 2500);
        var reader = new FakeStorageReader { Files = { ["bitacora/2026-10-15.md"] = day } };
        var journal = new JournalViewModel(Clock(), reader);

        await journal.SetProjectAsync(Root);

        Assert.Equal(day, journal.Text);
        Assert.True(journal.IsTrimmed);
        Assert.True(journal.ShowReadable);
        var lines = journal.RenderedText.Split('\n');
        Assert.True(lines.Length <= JournalViewModel.ReadableLines, $"The readable view draws {lines.Length} lines.");
        Assert.Equal("# Bitácora · 2026-10-15", lines[0]);
        Assert.StartsWith("- **09:00:00** · P-", lines[5], StringComparison.Ordinal);
        Assert.Contains("«petición 2500»", journal.RenderedText, StringComparison.Ordinal);
        Assert.DoesNotContain("«petición 1»", journal.RenderedText, StringComparison.Ordinal);
        var shown = lines.Count(l => l.StartsWith("- ", StringComparison.Ordinal));
        Assert.Equal($"Bitácora larga: se ven las {shown} entradas más recientes de 2500.", journal.TrimNote);
        Assert.Equal("Ver todo", journal.ToggleText);

        // The plain viewer takes the whole day only when it is asked for: a text box pays for its text even when hidden.
        Assert.Equal(string.Empty, journal.PlainText);

        journal.TogglePlainTextCommand.Execute(null);

        Assert.True(journal.ShowPlainText);
        Assert.False(journal.ShowReadable);
        Assert.Equal(day, journal.PlainText);
        Assert.Equal("Ver legible", journal.ToggleText);
        Assert.Equal("Se ve el día completo como texto, sin formato.", journal.TrimNote);

        // Another project starts readable again; a short day is drawn whole, with nothing to toggle.
        reader.Files["bitacora/2026-10-15.md"] = LongDay(entries: 3);
        await journal.SetProjectAsync(Root);
        Assert.False(journal.ShowPlainText);
        Assert.False(journal.IsTrimmed);
        Assert.Null(journal.TrimNote);
        Assert.Equal(journal.Text, journal.RenderedText);
    }

    // ---- V32: the panel follows the day by itself ---------------------------------------------------------------------

    private const string Day = "bitacora/2026-10-15.md";

    /// <summary>
    /// V32, seen live: after a run that wrote the day, the panel still said «No hay bitácora de hoy» until «Actualizar». A
    /// change of the journal folder is read again by itself, through the checked reader.
    /// </summary>
    [Fact]
    public async Task A_change_of_the_journal_folder_is_shown_without_pressing_actualizar()
    {
        var reader = new FakeStorageReader();
        var watcher = new FakeJournalWatcher();
        using var journal = new JournalViewModel(Clock(), reader, watcher);
        await journal.SetProjectAsync(Root);
        Assert.Contains("No hay bitácora de hoy", journal.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(Root, Assert.Single(watcher.Watched).Root);

        reader.Files[Day] = "# 2026-10-15\n- Ejecutor · empezó\n";
        watcher.Change();

        await WaitUntilAsync(() => journal.Text == "# 2026-10-15\n- Ejecutor · empezó\n");
        Assert.Null(journal.StatusMessage);
        Assert.All(reader.Reads, r => Assert.Equal((Root, Day), r));
    }

    /// <summary>
    /// One read at a time: a burst of changes is one read once it settles, and the changes that come while a read is in course
    /// are one read more when it ends, never one each.
    /// </summary>
    [Fact]
    public async Task A_burst_of_changes_is_one_read_and_the_changes_during_a_read_are_one_more()
    {
        var reader = new FakeStorageReader { Files = { [Day] = "1" } };
        var watcher = new FakeJournalWatcher();
        using var journal = new JournalViewModel(Clock(), reader, watcher);
        await journal.SetProjectAsync(Root);
        Assert.Single(reader.Reads);

        for (var i = 0; i < 10; i++)
        {
            watcher.Change();
        }

        await WaitUntilAsync(() => reader.Reads.Count == 2);

        using var gate = new SemaphoreSlim(0);
        reader.Gate = gate;
        reader.Files[Day] = "2";
        watcher.Change();
        await WaitUntilAsync(() => reader.Reads.Count == 3);
        reader.Gate = null;
        reader.Files[Day] = "3";
        for (var i = 0; i < 5; i++)
        {
            watcher.Change();
        }

        gate.Release();
        await WaitUntilAsync(() => journal.Text == "3");
        await Task.Delay(JournalViewModel.SettleTime * 3, TestContext.Current.CancellationToken);

        Assert.Equal(4, reader.Reads.Count);
        Assert.Equal("3", journal.Text);
    }

    /// <summary>The day is read off the UI thread, and shown on it.</summary>
    [Fact]
    public async Task The_day_is_read_off_the_ui_thread_and_shown_on_it()
    {
        var reader = new FakeStorageReader { Files = { [Day] = "hoy" } };

        var (ui, readOn, shownOn) = await WpfHost.RunAsync(async () =>
        {
            using var journal = new JournalViewModel(Clock(), reader);
            var shown = 0;
            journal.PropertyChanged += (_, e) => shown = e.PropertyName == nameof(JournalViewModel.Text) ? Environment.CurrentManagedThreadId : shown;
            await journal.SetProjectAsync(Root);
            Assert.Equal("hoy", journal.Text);
            return (Environment.CurrentManagedThreadId, Assert.Single(reader.ReadThreads), shown);
        });

        Assert.NotEqual(ui, readOn);
        Assert.Equal(ui, shownOn);
    }

    /// <summary>A read of the project left, still in course when another one opened, is never shown for the one open.</summary>
    [Fact]
    public async Task A_read_of_the_project_left_is_never_shown_for_the_one_open()
    {
        const string Other = @"C:\proyectos\otro";
        var reader = new FakeStorageReader { ByRoot = { [Root] = "de calc", [Other] = "de otro" } };
        using var gate = new SemaphoreSlim(0);
        reader.Gate = gate;
        using var journal = new JournalViewModel(Clock(), reader);
        var texts = new List<string>();
        journal.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JournalViewModel.Text))
            {
                lock (texts)
                {
                    texts.Add(journal.Text);
                }
            }
        };

        var first = journal.SetProjectAsync(Root);
        await WaitUntilAsync(() => reader.Reads.Count == 1);
        reader.Gate = null;
        var second = journal.SetProjectAsync(Other);
        gate.Release();
        await Task.WhenAll(first, second);

        Assert.Equal("de otro", journal.Text);
        Assert.DoesNotContain("de calc", texts);
    }

    /// <summary>Opening another project lets go of the watch of the one left; closing the panel (the shell) lets go of all.</summary>
    [Fact]
    public async Task Opening_another_project_or_closing_lets_go_of_the_watch_of_the_one_left()
    {
        var watcher = new FakeJournalWatcher();
        var journal = new JournalViewModel(Clock(), new FakeStorageReader(), watcher);

        await journal.SetProjectAsync(Root);
        await journal.SetProjectAsync(@"C:\proyectos\otro");
        Assert.Equal([Root, @"C:\proyectos\otro"], watcher.Watched.Select(w => w.Root));
        Assert.Equal([true, false], watcher.Watched.Select(w => w.Disposed));

        await journal.SetProjectAsync(null);
        Assert.Equal([true, true], watcher.Watched.Select(w => w.Disposed));

        await journal.SetProjectAsync(Root);
        journal.Dispose();
        Assert.Equal(3, watcher.Watched.Count);
        Assert.All(watcher.Watched, w => Assert.True(w.Disposed));

        // Without the checked reader nothing is read, so nothing is watched either.
        var unread = new FakeJournalWatcher();
        using var withoutReader = new JournalViewModel(Clock(), null, unread);
        await withoutReader.SetProjectAsync(Root);
        Assert.Empty(unread.Watched);
    }

    /// <summary>At midnight the panel goes to the new day by itself.</summary>
    [Fact]
    public async Task At_midnight_the_panel_goes_to_the_new_day()
    {
        // 21:59:30 UTC is 23:59:30 of the 15th in the +02:00 zone (the fake clock takes its start as UTC).
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 21, 59, 30, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test-plus-2", TimeSpan.FromHours(2), "test", "test"));
        var reader = new FakeStorageReader { Files = { [Day] = "el 15", ["bitacora/2026-10-16.md"] = "el 16" } };
        using var journal = new JournalViewModel(clock, reader);
        await journal.SetProjectAsync(Root);
        Assert.Equal("el 15", journal.Text);

        clock.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal("el 15", journal.Text);

        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => journal.Text == "el 16");
        Assert.Equal([Day, "bitacora/2026-10-16.md"], reader.Reads.Select(r => r.Path));
    }

    /// <summary>
    /// In the app as composed, with the real watch of the folder and the checked reader of the persistence: a day the app
    /// writes after the project opened, folders and all, is shown by itself, and so is a line more.
    /// </summary>
    [Fact]
    public async Task In_the_composed_app_a_day_written_after_the_project_opened_is_shown_by_itself()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("calc");
        using var provider = ComposedApp(temp);
        var journal = provider.GetRequiredService<JournalViewModel>();
        await journal.SetProjectAsync(project);
        Assert.Contains("No hay bitácora de hoy", journal.StatusMessage, StringComparison.Ordinal);

        temp.WriteFile("calc/.agentstudio/bitacora/2026-10-15.md", "# 2026-10-15\n- Ejecutor · empezó\n");
        await WaitUntilAsync(() => journal.Text == "# 2026-10-15\n- Ejecutor · empezó\n", seconds: 15);

        File.AppendAllText(temp.Combine("calc", ".agentstudio", "bitacora", "2026-10-15.md"), "  - Resultado · Succeeded\n");
        await WaitUntilAsync(() => journal.Text.EndsWith("  - Resultado · Succeeded\n", StringComparison.Ordinal), seconds: 15);
        Assert.Null(journal.StatusMessage);
    }

    /// <summary>
    /// The review of V32, in the composed app: <c>.agentstudio</c> deleted and made again three times (a checkout, the demo
    /// made again), and after each the day the app writes is shown by itself.
    /// </summary>
    [Fact]
    public async Task In_the_composed_app_the_day_is_still_shown_after_its_folders_are_deleted_and_made_again()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("calc");
        temp.CreateDirectory("calc/.agentstudio/bitacora");
        using var provider = ComposedApp(temp);
        var journal = provider.GetRequiredService<JournalViewModel>();
        await journal.SetProjectAsync(project);
        var data = temp.Combine("calc", ".agentstudio");

        for (var cycle = 1; cycle <= 3; cycle++)
        {
            await RetryAsync(() => Directory.Delete(data, recursive: true), () => !Directory.Exists(data));
            await RetryAsync(() => Directory.CreateDirectory(Path.Combine(data, "bitacora")), () => Directory.Exists(Path.Combine(data, "bitacora")));
            await Task.Delay(300, TestContext.Current.CancellationToken);

            File.WriteAllText(Path.Combine(data, "bitacora", "2026-10-15.md"), $"# 2026-10-15\n- ciclo {cycle}\n");

            await WaitUntilAsync(() => journal.Text.Contains($"ciclo {cycle}", StringComparison.Ordinal), seconds: 15);
        }
    }

    /// <summary>«Actualizar» and the check of the day also set again a watch that could not be completed.</summary>
    [Fact]
    public async Task Actualizar_and_the_check_of_the_day_set_the_watch_again()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));
        var watcher = new FakeJournalWatcher();
        using var journal = new JournalViewModel(clock, new FakeStorageReader(), watcher);
        await journal.SetProjectAsync(Root);
        var watch = Assert.Single(watcher.Watched);
        Assert.Equal(0, watch.Rearms);

        await journal.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(1, watch.Rearms);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, watch.Rearms);
    }

    /// <summary>
    /// The review of V32: the day was read half a second after the first change of a burst. It is read once the changes stop
    /// for <see cref="JournalViewModel.SettleTime"/>: every read comes at least that long after the change before it, and a
    /// burst of changes 150 ms apart is read once it ends, with its last change. The times are measured, not assumed, so a
    /// loaded machine that spaces the changes more only gives longer quiet spells.
    /// </summary>
    [Fact]
    public async Task A_burst_of_changes_is_read_once_after_the_last_change()
    {
        var reader = new FakeStorageReader { Files = { [Day] = "0" } };
        var watcher = new FakeJournalWatcher();
        using var journal = new JournalViewModel(Clock(), reader, watcher);
        await journal.SetProjectAsync(Root);
        var changes = new List<long>();

        for (var i = 1; i <= 6; i++)
        {
            reader.Files[Day] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            changes.Add(System.Diagnostics.Stopwatch.GetTimestamp());
            watcher.Change();
            await Task.Delay(150, TestContext.Current.CancellationToken);
        }

        await WaitUntilAsync(() => journal.Text == "6");
        await Task.Delay(JournalViewModel.SettleTime * 2, TestContext.Current.CancellationToken);

        // The first read is the opening of the project; every later one waited for the changes before it to settle.
        long[] reads;
        lock (reader.Reads)
        {
            reads = [.. reader.ReadStamps.Skip(1)];
        }

        Assert.NotEmpty(reads);
        foreach (var read in reads)
        {
            var before = changes.Where(c => c < read).Max();
            var quiet = System.Diagnostics.Stopwatch.GetElapsedTime(before, read);
            Assert.True(quiet >= JournalViewModel.SettleTime - TimeSpan.FromMilliseconds(50), $"A read came {quiet.TotalMilliseconds:0} ms after a change: the changes had not settled.");
        }

        // One read after the last change; a loaded machine may space two changes by a whole quiet spell (a read between them
        // is right then), or stretch the burst past the longest wait (one read more): each is allowed, as measured.
        var quietGaps = changes.Zip(changes.Skip(1), (a, b) => System.Diagnostics.Stopwatch.GetElapsedTime(a, b))
            .Count(gap => gap >= JournalViewModel.SettleTime - TimeSpan.FromMilliseconds(50));
        var stretched = System.Diagnostics.Stopwatch.GetElapsedTime(changes[0], changes[^1]) >= JournalViewModel.LongestSettle ? 1 : 0;
        Assert.True(reads.Length <= 1 + quietGaps + stretched, $"A burst of 6 changes was read {reads.Length} times ({quietGaps} quiet spells within it).");
    }

    /// <summary>A burst that never ends is still read while it goes on (at most <see cref="JournalViewModel.LongestSettle"/> of spells after its first change).</summary>
    [Fact]
    public async Task A_burst_without_end_is_still_read()
    {
        var reader = new FakeStorageReader { Files = { [Day] = "0" } };
        var watcher = new FakeJournalWatcher();
        using var journal = new JournalViewModel(Clock(), reader, watcher);
        await journal.SetProjectAsync(Root);
        var started = System.Diagnostics.Stopwatch.StartNew();

        while (started.Elapsed < TimeSpan.FromSeconds(10) && reader.Reads.Count < 2)
        {
            watcher.Change();
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.True(reader.Reads.Count >= 2, "A burst of changes without end was never read.");
    }

    /// <summary>
    /// The review of V32: with «Ver todo» on, a day read again by itself does not reload the plain viewer (a second for 5,000
    /// lines): the panel says there are new entries, «Recargar» brings them, and back in the readable view they are there.
    /// </summary>
    [Fact]
    public async Task With_ver_todo_on_a_day_read_again_by_itself_waits_for_recargar()
    {
        var first = LongDay(entries: 400);
        var reader = new FakeStorageReader { Files = { [Day] = first } };
        var watcher = new FakeJournalWatcher();
        using var journal = new JournalViewModel(Clock(), reader, watcher);
        await journal.SetProjectAsync(Root);
        journal.TogglePlainTextCommand.Execute(null);
        Assert.Equal(first, journal.PlainText);
        Assert.False(journal.HasNewEntries);
        var plainChanges = 0;
        journal.PropertyChanged += (_, e) => plainChanges += e.PropertyName == nameof(JournalViewModel.PlainText) && journal.PlainText != first ? 1 : 0;

        var second = first + "- **09:00:00** · P-0401 · Ejecutor · claude-code/haiku · #prompt · «petición 401»\n";
        reader.Files[Day] = second;
        watcher.Change();
        await WaitUntilAsync(() => journal.Text == second);

        Assert.Equal(first, journal.PlainText);
        Assert.Equal(0, plainChanges);
        Assert.True(journal.HasNewEntries);
        Assert.Equal("Hay entradas nuevas: «Recargar» las enseña aquí; la vista legible ya las tiene.", journal.NewEntriesNote);

        journal.ReloadPlainTextCommand.Execute(null);
        Assert.Equal(second, journal.PlainText);
        Assert.False(journal.HasNewEntries);
        Assert.Null(journal.NewEntriesNote);

        // «Actualizar» is a person asking: the plain viewer takes the day too.
        var third = second + "- **09:00:00** · P-0402 · Ejecutor · claude-code/haiku · #prompt · «petición 402»\n";
        reader.Files[Day] = third;
        await journal.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(third, journal.PlainText);

        // Back in the readable view, the latest entries are there.
        var fourth = third + "- **09:00:00** · P-0403 · Ejecutor · claude-code/haiku · #prompt · «petición 403»\n";
        reader.Files[Day] = fourth;
        watcher.Change();
        await WaitUntilAsync(() => journal.Text == fourth);
        journal.TogglePlainTextCommand.Execute(null);

        // Without a UI thread the day is shown on the thread that read it: its readable part follows its text there.
        await WaitUntilAsync(() => journal.RenderedText.Contains("«petición 403»", StringComparison.Ordinal));
        Assert.False(journal.HasNewEntries);
    }

    /// <summary>Retries <paramref name="act"/> until <paramref name="done"/>: a folder being deleted cannot be deleted or made at once.</summary>
    private static async Task RetryAsync(Action act, Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            try
            {
                act();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still held: the watch lets go of it once it sees it go.
            }

            if (!done())
            {
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
        }

        Assert.True(done(), "The folder could not be deleted or made again.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int seconds = 5)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "The panel did not read the day in time.");
    }

    /// <summary>A day as the app writes it: the title, the two lines under it and one entry with its result per request.</summary>
    internal static string LongDay(int entries)
    {
        var text = new StringBuilder("# Bitácora · 2026-10-15\n\nRegistro automático de Agent Studio.\nCuando el agente termina, se añade una línea «Resultado».\n\n");
        for (var i = 1; i <= entries; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"- **09:00:00** · P-{i:0000} · Ejecutor · claude-code/haiku · #prompt · «petición {i}»\n")
                .Append(System.Globalization.CultureInfo.InvariantCulture, $"  - Resultado P-{i:0000} · 09:00:01 · Succeeded · 1,0 s · tokens no disponibles\n");
        }

        return text.ToString();
    }

    /// <summary>The app's composition over temporary folders, with the clock of these tests.</summary>
    private static ServiceProvider ComposedApp(TempDirectory temp)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock());
        services.AddWindowsDoubles();
        services.AddTestLocalData(temp.Combine("local"));
        services.AddAgentStudioApp(new AppFolders(new AppDataPaths(temp.Combine("roaming")), new LocalDataPaths(temp.Combine("local")), null));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>The days of the journal by their path under <c>.agentstudio</c>; what is opened is recorded.</summary>
    private sealed class FakeStorageReader : IProjectStorageReader
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        /// <summary>The text of the day of a given project, before <see cref="Files"/>.</summary>
        public Dictionary<string, string> ByRoot { get; } = new(StringComparer.Ordinal);

        public List<(string Root, string Path)> Reads { get; } = [];

        /// <summary>The thread of each read.</summary>
        public List<int> ReadThreads { get; } = [];

        /// <summary>When each read started (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>).</summary>
        public List<long> ReadStamps { get; } = [];

        /// <summary>When set, every read throws it (a refused link, a folder that is not there).</summary>
        public Exception? Failure { get; set; }

        /// <summary>When set, a read waits on it after it was recorded (a read in course).</summary>
        public SemaphoreSlim? Gate { get; set; }

        public Stream OpenForRead(string projectRoot, string relativePath)
        {
            var gate = Gate;
            lock (Reads)
            {
                Reads.Add((projectRoot, relativePath));
                ReadThreads.Add(Environment.CurrentManagedThreadId);
                ReadStamps.Add(System.Diagnostics.Stopwatch.GetTimestamp());
            }

            gate?.Wait(TimeSpan.FromSeconds(10));
            if (Failure is not null)
            {
                throw Failure;
            }

            return ByRoot.TryGetValue(projectRoot, out var own) ? new MemoryStream(Encoding.UTF8.GetBytes(own))
                : Files.TryGetValue(relativePath, out var content) ? new MemoryStream(Encoding.UTF8.GetBytes(content))
                : throw new FileNotFoundException("The file does not exist.", relativePath);
        }
    }

    /// <summary>A watch of the journal folder that changes when the test says so; each watch is recorded with whether it was let go.</summary>
    private sealed class FakeJournalWatcher : IJournalWatcher
    {
        public List<Watching> Watched { get; } = [];

        public IJournalWatch Watch(string projectRoot, Action changed)
        {
            var watch = new Watching(projectRoot, changed);
            Watched.Add(watch);
            return watch;
        }

        /// <summary>The folder of the project watched now changed.</summary>
        public void Change() => Watched.Last(w => !w.Disposed).Changed();

        public sealed class Watching(string root, Action changed) : IJournalWatch
        {
            public string Root { get; } = root;

            public Action Changed { get; } = changed;

            public bool Disposed { get; private set; }

            /// <summary>How many times the panel asked to set the watch again.</summary>
            public int Rearms { get; private set; }

            public void Dispose() => Disposed = true;

            public void Rearm() => Rearms++;
        }
    }
}
