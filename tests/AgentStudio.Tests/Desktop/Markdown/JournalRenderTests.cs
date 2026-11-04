using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Desktop.Views;
using AgentStudio.Tests.Desktop.Orchestrator;

namespace AgentStudio.Tests.Desktop.Markdown;

/// <summary>
/// V31, seen live: «Bitácora de hoy» showed its raw Markdown («\(intento 1\)», «**00:01:27**»). The view shows it read: the
/// title, the lines under it, each entry with its time in bold, code in the monospaced font and the escapes resolved; the file
/// is not changed (the view model keeps its text as read). After the review: it keeps the reading caret of the plain viewer,
/// it neither prints nor finds (Ctrl+P is «Ir al fichero» in the window), and a long day draws only its latest entries. Drawn
/// without ever being shown (<see cref="OffscreenRender"/>).
/// </summary>
public sealed class JournalRenderTests
{
    private static string Sample => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Memory", "Fixtures", "bitacora-ejemplo.md"));

    [Fact]
    public void The_journal_is_shown_read_with_its_title_bold_times_code_and_no_escapes()
    {
        var sample = Sample;
        var (text, kept, title, titleWeight, firstBold, codeFonts, entries, subEntries) = WpfHost.Run(() =>
        {
            var journal = new JournalViewModel(TimeProvider.System) { Text = sample, StatusMessage = null };
            var view = new JournalView { DataContext = journal };
            OffscreenRender.SaveIfRequested(OffscreenRender.Render(view, 760, 900), "bitacora.png");

            var document = Reader(view).Document;
            var heading = (Paragraph)document.Blocks.FirstBlock;
            var lists = document.Blocks.OfType<List>().ToList();
            var items = lists.SelectMany(l => l.ListItems).ToList();
            var bold = items.SelectMany(i => i.Blocks.OfType<Paragraph>()).SelectMany(p => p.Inlines.OfType<Bold>()).First();
            var mono = (FontFamily)System.Windows.Application.Current.Resources["MonoFont"];
            var code = items.SelectMany(i => i.Blocks.OfType<Paragraph>()).SelectMany(p => p.Inlines.OfType<Run>()).Where(r => Equals(r.FontFamily, mono)).Select(r => r.Text).ToList();
            return (
                new TextRange(document.ContentStart, document.ContentEnd).Text,
                journal.Text,
                new TextRange(heading.ContentStart, heading.ContentEnd).Text,
                heading.FontWeight,
                string.Concat(bold.Inlines.OfType<Run>().Select(r => r.Text)),
                code,
                items.Count,
                items.SelectMany(i => i.Blocks.OfType<List>()).Sum(l => l.ListItems.Count));
        });

        Assert.Equal(sample, kept);
        Assert.Equal("Bitácora · 2026-03-10", title);
        Assert.Equal(FontWeights.SemiBold, titleWeight);
        Assert.Equal("08:59:58", firstBold);
        Assert.Contains("eb1c6494", codeFonts);
        Assert.Contains("«Implementar el almacén [T-12]»", text, StringComparison.Ordinal);
        Assert.Contains("«Resumen *final*»", text, StringComparison.Ordinal);
        Assert.DoesNotContain("**", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\[", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\*", text, StringComparison.Ordinal);
        Assert.DoesNotContain("# ", text, StringComparison.Ordinal);
        Assert.True(entries >= 10, $"Only {entries} entries were shown.");
        Assert.True(subEntries >= 9, $"Only {subEntries} sub-entries were shown under their entries.");
    }

    /// <summary>
    /// The review of V31: a document viewer prints on Ctrl+P, and the window's Ctrl+P is «Ir al fichero». The journal neither
    /// prints nor finds, and its answer lets the key go on to the window.
    /// </summary>
    [Fact]
    public void The_journal_neither_prints_nor_finds_and_lets_the_shortcut_go_on_to_the_window()
    {
        var answers = WpfHost.Run(() =>
        {
            var view = new JournalView { DataContext = new JournalViewModel(TimeProvider.System) { Text = Sample, StatusMessage = null } };
            OffscreenRender.Render(view, 600, 400);
            var reader = Reader(view);
            return new[] { ApplicationCommands.Print, ApplicationCommands.Find }
                .Select(command => (command.Name, Plain: command.CanExecute(null, reader), Routed: CanExecute(command, reader)))
                .ToList();
        });

        Assert.All(answers, a =>
        {
            Assert.False(a.Plain, $"{a.Name} can run in the journal.");
            Assert.False(a.Routed.CanExecute, $"{a.Name} can run in the journal.");
            Assert.True(a.Routed.ContinueRouting, $"{a.Name} stops the key in the journal: it never reaches the window.");
        });
    }

    /// <summary>The review of V31: the plain viewer had a reading caret (the keyboard moves and selects with it); the journal keeps it.</summary>
    [Fact]
    public void The_journal_keeps_a_reading_caret_and_cannot_be_edited()
    {
        var (readOnly, caret, undo, name) = WpfHost.Run(() =>
        {
            var view = new JournalView { DataContext = new JournalViewModel(TimeProvider.System) { Text = Sample, StatusMessage = null } };
            OffscreenRender.Render(view, 600, 400);
            var reader = Reader(view);
            return (reader.IsReadOnly, reader.IsReadOnlyCaretVisible, reader.IsUndoEnabled, System.Windows.Automation.AutomationProperties.GetName(reader));
        });

        Assert.True(readOnly);
        Assert.True(caret);
        Assert.False(undo);
        Assert.Equal("Bitácora de hoy", name);
    }

    /// <summary>
    /// The review of V31: 5,000 lines took 6.3 s to lay out, on the UI thread, when a project opened. The view draws the
    /// latest entries only; the whole day is the plain text, which is shown on demand.
    /// </summary>
    [Fact]
    public void A_long_day_draws_only_its_latest_entries()
    {
        var day = JournalViewModelTests.LongDay(entries: 2500);
        var (paragraphs, plainVisible, readerVisible, elapsed) = WpfHost.Run(() =>
        {
            var journal = new JournalViewModel(TimeProvider.System) { Text = day, StatusMessage = null };
            var view = new JournalView { DataContext = journal };
            var clock = Stopwatch.StartNew();
            OffscreenRender.SaveIfRequested(OffscreenRender.Render(view, 760, 600), "bitacora-larga.png");
            clock.Stop();
            var document = Reader(view).Document;
            var count = document.Blocks.OfType<List>().SelectMany(l => l.ListItems).Count();
            return (count, ((TextBox)view.FindName("JournalText")).Visibility, Reader(view).Visibility, clock.Elapsed);
        });

        Assert.InRange(paragraphs, 1, JournalViewModel.ReadableLines / 2);
        Assert.Equal(Visibility.Collapsed, plainVisible);
        Assert.Equal(Visibility.Visible, readerVisible);
        TestContext.Current.SendDiagnosticMessage(string.Create(CultureInfo.InvariantCulture, $"A day of 5,000 lines drawn in {elapsed.TotalMilliseconds:0} ms ({paragraphs} entries)."));
    }

    /// <summary>
    /// The review of V32: each read of the day by itself put a new document and took the reader back to the top. Whoever reads
    /// the end follows the new entries; whoever reads further up stays where they were.
    /// </summary>
    [Fact]
    public void A_day_read_again_keeps_the_place_of_whoever_reads_it()
    {
        var (followsEnd, endGap, keptAt, wanted) = WpfHost.Run(() =>
        {
            var journal = new JournalViewModel(TimeProvider.System) { ReadScope = Scope, Text = JournalViewModelTests.LongDay(entries: 60), StatusMessage = null };
            var (_, reader) = Show(journal);

            reader.ScrollToEnd();
            reader.UpdateLayout();
            journal.Text = JournalViewModelTests.LongDay(entries: 70);
            WpfHost.FlushBindings();
            reader.UpdateLayout();
            var gap = reader.ExtentHeight - reader.ViewportHeight - reader.VerticalOffset;
            var atEnd = reader.VerticalOffset > 0 && gap <= 1;

            reader.ScrollToVerticalOffset(120);
            reader.UpdateLayout();
            var offset = reader.VerticalOffset;
            journal.Text = JournalViewModelTests.LongDay(entries: 80);
            WpfHost.FlushBindings();
            reader.UpdateLayout();
            return (atEnd, gap, reader.VerticalOffset, offset);
        });

        Assert.True(followsEnd, $"After a read the end was {endGap:0} px away: it did not follow the new entries ({keptAt}, {wanted}).");
        Assert.Equal(120, wanted, 0.5);
        Assert.Equal(wanted, keptAt, 0.5);
    }

    /// <summary>
    /// The re-review of V32: the first day opened at its end (the empty document before it already had a height), and another
    /// project took the place of the one before. A first day, and a day of another project or another date, start at the top.
    /// </summary>
    [Fact]
    public void A_first_day_and_a_day_of_another_project_or_date_start_at_the_top()
    {
        var (first, otherProject, otherDay) = WpfHost.Run(() =>
        {
            var journal = new JournalViewModel(TimeProvider.System) { StatusMessage = null };
            var (_, reader) = Show(journal);

            // As the panel reads it: the scope first, then the text.
            journal.ReadScope = Scope;
            journal.Text = JournalViewModelTests.LongDay(entries: 60);
            WpfHost.FlushBindings();
            reader.UpdateLayout();
            var atFirst = reader.VerticalOffset;

            reader.ScrollToEnd();
            reader.UpdateLayout();
            journal.ReadScope = @"C:\proyectos\otro|2026-10-15.md";
            journal.Text = JournalViewModelTests.LongDay(entries: 61);
            WpfHost.FlushBindings();
            reader.UpdateLayout();
            var atOtherProject = reader.VerticalOffset;

            reader.ScrollToEnd();
            reader.UpdateLayout();
            journal.ReadScope = @"C:\proyectos\otro|2026-10-16.md";
            journal.Text = JournalViewModelTests.LongDay(entries: 62);
            WpfHost.FlushBindings();
            reader.UpdateLayout();
            return (atFirst, atOtherProject, reader.VerticalOffset);
        });

        Assert.Equal(0, first, 0.5);
        Assert.Equal(0, otherProject, 0.5);
        Assert.Equal(0, otherDay, 0.5);
    }

    /// <summary>
    /// The re-review of V32: a long day draws its latest entries, so each new entry drops the oldest drawn and the text went up
    /// under whoever read it. The entry at the top of the view stays at the top.
    /// </summary>
    [Fact]
    public void In_a_long_day_the_entry_at_the_top_stays_at_the_top_when_new_entries_come()
    {
        var (before, after, shift) = WpfHost.Run(() =>
        {
            var journal = new JournalViewModel(TimeProvider.System) { ReadScope = Scope, Text = JournalViewModelTests.LongDay(entries: 2500), StatusMessage = null };
            var (_, reader) = Show(journal);
            Assert.True(journal.IsTrimmed);

            reader.ScrollToVerticalOffset(reader.ExtentHeight / 2);
            reader.UpdateLayout();
            var top = TopText(reader);
            var offset = reader.VerticalOffset;

            journal.Text = JournalViewModelTests.LongDay(entries: 2503);
            WpfHost.FlushBindings();
            reader.UpdateLayout();
            return (top, TopText(reader), reader.VerticalOffset - offset);
        });

        Assert.Equal(before, after);
        Assert.True(shift < -1, $"The view moved by {shift:0} px: it should have moved up by the entries dropped above.");
    }

    private const string Scope = @"C:\proyectos\calc|2026-10-15.md";

    /// <summary>The panel in a host of a fixed size, as it is on screen: a later layout pass keeps the height, so the day scrolls.</summary>
    private static (JournalView View, RichTextBox Reader) Show(JournalViewModel journal)
    {
        var view = new JournalView { DataContext = journal };
        var host = new Grid { Width = 600, Height = 300, Children = { view } };
        OffscreenRender.Render(host, 600, 300);
        return (view, Reader(view));
    }

    /// <summary>The text of the paragraph at the top of the view.</summary>
    private static string TopText(RichTextBox reader) =>
        reader.GetPositionFromPoint(new Point(reader.Padding.Left + 4, reader.Padding.Top + 2), snapToText: true)?.Paragraph is { } top
            ? new TextRange(top.ContentStart, top.ContentEnd).Text
            : string.Empty;

    private static RichTextBox Reader(JournalView view) => (RichTextBox)view.FindName("JournalDocument");

    /// <summary>
    /// Asks <paramref name="target"/> whether <paramref name="command"/> can run, as the key routing does: whether it can, and
    /// whether the key goes on to the elements above. The arguments have no public constructor; the routing is WPF's own.
    /// </summary>
    private static (bool CanExecute, bool ContinueRouting) CanExecute(RoutedCommand command, UIElement target)
    {
        var args = (CanExecuteRoutedEventArgs)Activator.CreateInstance(
            typeof(CanExecuteRoutedEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null, [command, null], CultureInfo.InvariantCulture)!;
        args.RoutedEvent = CommandManager.CanExecuteEvent;
        target.RaiseEvent(args);
        return (args.CanExecute, args.ContinueRouting);
    }
}
