using System.IO;
using AgentStudio.Desktop.Services;
using AgentStudio.Tests.Infrastructure.Persistence;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// V32: the watch of the journal folder of a project (<see cref="JournalFolderWatcher"/>), over temporary folders. It says
/// when a day is written; it follows the folders as they appear (the journal comes with the first entry of a project); it
/// never watches through a link; and once let go it says nothing more. It reads nothing.
/// </summary>
public sealed class JournalFolderWatcherTests
{
    [Fact]
    public async Task Says_when_a_day_of_the_journal_is_written()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("calc");
        temp.CreateDirectory("calc/.agentstudio/bitacora");
        var calls = new Calls();
        using var watch = new JournalFolderWatcher().Watch(root, calls.Add);

        temp.WriteFile("calc/.agentstudio/bitacora/2026-10-15.md", "- primera\n");

        await calls.WaitAboveAsync(0);
    }

    [Fact]
    public async Task Follows_the_folders_of_the_journal_as_they_appear()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("calc");
        var calls = new Calls();
        using var watch = new JournalFolderWatcher().Watch(root, calls.Add);

        temp.CreateDirectory("calc/.agentstudio");
        await calls.WaitAboveAsync(0);
        temp.CreateDirectory("calc/.agentstudio/bitacora");
        var before = await calls.QuietAsync();

        temp.WriteFile("calc/.agentstudio/bitacora/2026-10-15.md", "- primera\n");
        await calls.WaitAboveAsync(before);

        // Another file of .agentstudio is not the journal.
        var journal = await calls.QuietAsync();
        temp.WriteFile("calc/.agentstudio/canvas-layout.json", "{}");
        await Task.Delay(700, TestContext.Current.CancellationToken);
        Assert.Equal(journal, calls.Count);
    }

    [Fact]
    public async Task Never_watches_through_a_link_planted_as_the_journal_folder()
    {
        using var temp = new TempDirectory();
        using var links = new PlantedLinks();
        var root = temp.CreateDirectory("calc");
        temp.CreateDirectory("calc/.agentstudio");
        temp.CreateDirectory("fuera");
        links.Junction(temp.Combine("calc", ".agentstudio", "bitacora"), temp.Combine("fuera"));
        var calls = new Calls();
        using var watch = new JournalFolderWatcher().Watch(root, calls.Add);

        temp.WriteFile("fuera/2026-10-15.md", "- texto de fuera del proyecto\n");
        await Task.Delay(700, TestContext.Current.CancellationToken);

        Assert.Equal(0, calls.Count);
    }

    [Fact]
    public async Task Once_let_go_it_says_nothing_more()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("calc");
        temp.CreateDirectory("calc/.agentstudio/bitacora");
        var calls = new Calls();
        var watch = new JournalFolderWatcher().Watch(root, calls.Add);

        watch.Dispose();
        temp.WriteFile("calc/.agentstudio/bitacora/2026-10-15.md", "- primera\n");
        await Task.Delay(700, TestContext.Current.CancellationToken);

        Assert.Equal(0, calls.Count);
    }

    [Fact]
    public void A_project_folder_that_is_not_there_is_not_an_error()
    {
        using var temp = new TempDirectory();
        var watch = new JournalFolderWatcher().Watch(temp.Combine("no-existe"), () => { });

        watch.Dispose();
    }

    /// <summary>
    /// The review of V32: deleting <c>.agentstudio</c> and making it again (a checkout of another branch, the demo made again)
    /// left the watch dead, and the writes after it were lost. Five times over, every write after the folders came back is
    /// said.
    /// </summary>
    [Fact]
    public async Task Deleting_and_making_again_the_folders_of_the_journal_never_loses_a_later_write()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("calc");
        temp.CreateDirectory("calc/.agentstudio/bitacora");
        var calls = new Calls();
        using var watch = new JournalFolderWatcher().Watch(root, calls.Add);
        var data = temp.Combine("calc", ".agentstudio");

        for (var cycle = 1; cycle <= 5; cycle++)
        {
            await TryAsync(() => Directory.Delete(data, recursive: true), () => !Directory.Exists(data));
            await TryAsync(() => Directory.CreateDirectory(Path.Combine(data, "bitacora")), () => Directory.Exists(Path.Combine(data, "bitacora")));
            var before = await calls.QuietAsync();

            File.AppendAllText(Path.Combine(data, "bitacora", "2026-10-15.md"), $"- ciclo {cycle}\n");

            await calls.WaitAboveAsync(before);
        }
    }

    /// <summary>
    /// The re-review: when <c>bitacora</c> was made between measuring the folders and watching <c>.agentstudio</c>, nothing
    /// said it, and only the retry 5 s later found it (5 of 15 times). The folders and the journal made at once, fifteen
    /// times, with a clock that never retries: every write after them is said all the same.
    /// </summary>
    [Fact]
    public async Task Folders_made_at_once_are_all_watched_without_waiting_for_a_retry()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("calc");
        var calls = new Calls();
        using var watch = new JournalFolderWatcher(new Microsoft.Extensions.Time.Testing.FakeTimeProvider()).Watch(root, calls.Add);
        var data = temp.Combine("calc", ".agentstudio");

        for (var cycle = 1; cycle <= 15; cycle++)
        {
            await TryAsync(() => Directory.Delete(data, recursive: true), () => !Directory.Exists(data));
            await TryAsync(() => Directory.CreateDirectory(Path.Combine(data, "bitacora")), () => Directory.Exists(Path.Combine(data, "bitacora")));
            var before = await calls.QuietAsync();

            File.AppendAllText(Path.Combine(data, "bitacora", "2026-10-15.md"), $"- ciclo {cycle}\n");

            await calls.WaitAboveAsync(before);
        }
    }

    /// <summary>
    /// A project whose folder is not there yet (or could not be watched) is tried again every
    /// <see cref="JournalFolderWatcher.RetryInterval"/>: once it is, its journal is watched.
    /// </summary>
    [Fact]
    public async Task A_watch_that_could_not_start_is_tried_again_until_it_can()
    {
        using var temp = new TempDirectory();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var calls = new Calls();
        using var watch = new JournalFolderWatcher(clock).Watch(temp.Combine("calc"), calls.Add);

        temp.CreateDirectory("calc/.agentstudio/bitacora");
        clock.Advance(JournalFolderWatcher.RetryInterval);

        // Once the watch is complete again the journal is read again (it may have been written meanwhile).
        await calls.WaitAboveAsync(0);
        var before = await calls.QuietAsync();
        temp.WriteFile("calc/.agentstudio/bitacora/2026-10-15.md", "- primera\n");
        await calls.WaitAboveAsync(before);
    }

    /// <summary>Retries <paramref name="act"/> until <paramref name="done"/>: a folder that is being deleted cannot be deleted or made at once.</summary>
    private static async Task TryAsync(Action act, Func<bool> done)
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

    /// <summary>How many times the watch said the journal changed; it is called on any thread.</summary>
    private sealed class Calls
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Add() => Interlocked.Increment(ref _count);

        public async Task WaitAboveAsync(int count)
        {
            for (var i = 0; i < 500 && Count <= count; i++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.True(Count > count, "The watch did not say the journal changed.");
        }

        /// <summary>Waits until no call came for a while, and says how many there were.</summary>
        public async Task<int> QuietAsync()
        {
            var last = Count;
            for (var i = 0; i < 50; i++)
            {
                await Task.Delay(300, TestContext.Current.CancellationToken);
                if (Count == last)
                {
                    return last;
                }

                last = Count;
            }

            return last;
        }
    }
}
