using System.IO;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Journal;
using AgentStudio.Tests.Infrastructure.Persistence;

namespace AgentStudio.Tests.Memory;

/// <summary>
/// Links left in a project before the prompt journal writes there (ADR-0018: in the model). A junction at
/// <c>.agentstudio</c> or at <c>bitacora</c>, or a day that is a second name of another file, would take the journal's
/// appends outside the project: the record is refused, so the agent is never launched, and nothing is written through the
/// link.
/// </summary>
public sealed class MarkdownDailyJournalLinkTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 3, 10, 9, 0, 0, TimeSpan.FromHours(2));

    private readonly TempProjectFolder _folder = new();
    private readonly PlantedLinks _links = new();

    public MarkdownDailyJournalLinkTests()
    {
        Outside = Path.Combine(Path.GetDirectoryName(_folder.Root)!, "fuera del proyecto");
        Directory.CreateDirectory(Outside);
    }

    /// <summary>A folder next to the project, outside it: where a planted link points.</summary>
    private string Outside { get; }

    private string JournalDirectory => Path.Combine(_folder.DataDirectory, "bitacora");

    private string Day => Path.Combine(JournalDirectory, "2026-03-10.md");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        // The links go first, as links: the cleanup never walks into what they point to.
        _links.Dispose();
        _folder.Dispose();
    }

    private MarkdownDailyJournal NewJournal() => new(_folder.Root, new ProjectStorageFiles(), new FakeClock(At));

    private static PromptJournalEntry Entry() => new(At, "Orquestador", ProviderId.ClaudeCode, "opus", "Resumen", ["tests"]);

    private string[] OutsideEntries() => Directory.GetFileSystemEntries(Outside, "*", SearchOption.AllDirectories);

    [Fact]
    public async Task A_junction_at_the_data_folder_is_refused_and_nothing_is_journaled_outside()
    {
        _links.Junction(_folder.DataDirectory, Outside);

        var refusal = await Assert.ThrowsAsync<ProjectStorageIntegrityException>(() => NewJournal().RecordPromptAsync(Entry(), Ct));

        Assert.Contains(".agentstudio", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(OutsideEntries());
    }

    [Fact]
    public async Task A_junction_at_the_journal_folder_is_refused_and_nothing_is_journaled_outside()
    {
        Directory.CreateDirectory(_folder.DataDirectory);
        _links.Junction(JournalDirectory, Outside);

        var refusal = await Assert.ThrowsAsync<ProjectStorageIntegrityException>(() => NewJournal().RecordPromptAsync(Entry(), Ct));

        Assert.Contains("bitacora", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(OutsideEntries());
    }

    [Fact]
    public async Task A_day_that_is_a_second_name_of_another_file_is_refused_and_that_file_is_not_written()
    {
        var victim = Path.Combine(Outside, "importante.md");
        await File.WriteAllTextAsync(victim, "no se toca", Ct);
        Directory.CreateDirectory(JournalDirectory);
        _links.HardLink(Day, victim);

        var refusal = await Assert.ThrowsAsync<ProjectStorageIntegrityException>(() => NewJournal().RecordPromptAsync(Entry(), Ct));

        Assert.Contains("2026-03-10.md", refusal.Message, StringComparison.Ordinal);
        Assert.Equal("no se toca", await File.ReadAllTextAsync(victim, Ct));
    }

    [Fact]
    public async Task An_outcome_is_refused_like_a_prompt_when_its_day_became_a_second_name_of_another_file()
    {
        var journal = NewJournal();
        var id = await journal.RecordPromptAsync(Entry(), Ct);
        var victim = Path.Combine(Outside, "importante.md");
        await File.WriteAllTextAsync(victim, "no se toca", Ct);
        File.Delete(Day);
        _links.HardLink(Day, victim);
        var unavailable = MetricValue<long>.Unavailable("no reportado");

        await Assert.ThrowsAsync<ProjectStorageIntegrityException>(() =>
            journal.RecordOutcomeAsync(id, new PromptOutcome(At, AgentRunStatus.Succeeded, TimeSpan.FromSeconds(1), unavailable, unavailable), Ct));

        Assert.Equal("no se toca", await File.ReadAllTextAsync(victim, Ct));
    }

    [Fact]
    public async Task A_project_without_links_keeps_journaling_in_its_own_folder()
    {
        var journal = NewJournal();

        var first = await journal.RecordPromptAsync(Entry(), Ct);
        var second = await journal.RecordPromptAsync(Entry(), Ct);

        Assert.Equal([1, 2], new[] { first.Sequence, second.Sequence });
        Assert.Equal(2, File.ReadAllLines(Day).Count(l => l.StartsWith("- **", StringComparison.Ordinal)));
        Assert.Empty(OutsideEntries());
    }
}
