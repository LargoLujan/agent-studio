using System.Globalization;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;

namespace AgentStudio.Memory.Summary;

/// <summary>
/// The deterministic summary of a day of the journal (docs/14 D-31; ADR-0025). No agent is launched and no quota is spent.
/// <list type="bullet">
/// <item>The day is read through the checked storage (<see cref="JournalReader"/>), redacted
/// (<see cref="ISecretRedactor"/>) and summarized by <see cref="DailySummaryBuilder"/>: the same bytes always give the
/// same summary.</item>
/// <item>The day is the local one (<see cref="TimeProvider.LocalTimeZone"/>). Today, or a day still to come, is
/// <b>provisional</b> and goes nowhere.</item>
/// <item>A closed day with a journal goes to the memory as <see cref="MemoryKind.DailySummary"/>, from the system, with
/// <c>bitacora/AAAA-MM-DD.md#sha256:&lt;hash of its bytes&gt;</c> as its source: once per content. If the journal of a
/// closed day changed (a «Hecho» line added after midnight), the new summary supersedes the one before. A day without a
/// journal leaves nothing. The memory writes wait while a run or a planning goes on (<see cref="IProjectMemory"/>).</item>
/// </list>
/// </summary>
public sealed class DailySummaryService(IProjectStorageReader reader, IProjectMemory memory, ISecretRedactor redactor, TimeProvider timeProvider) : IDailySummaryService
{
    public async Task<DailySummaryResult> SummarizeAsync(ProjectContext project, DateOnly day, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var journal = JournalReader.Read(reader, project.RootPath, day);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeProvider.LocalTimeZone).DateTime);
        var provisional = day >= today;
        var summary = DailySummaryBuilder.Build(day, provisional, journal.Text is { } text ? redactor.Redact(text) : null);

        MemoryEntry? recorded = null;
        if (!provisional && journal.Text is not null)
        {
            var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var topic = "Resumen del " + date;
            var source = $"bitacora/{date}.md#sha256:{journal.Sha256}";
            var current = (await memory.RecallAsync(project, new MemoryQuery(Kinds: [MemoryKind.DailySummary], Topic: topic, MaxEntries: 1, TrustedOnly: false), ct).ConfigureAwait(false))
                .FirstOrDefault();
            if (current is null || !string.Equals(current.Source, source, StringComparison.Ordinal))
            {
                recorded = await memory.RecordAsync(project, new MemoryDraft(MemoryKind.DailySummary, topic, summary.Text, MemoryOrigin.System, source, current?.Id), ct).ConfigureAwait(false);
            }
        }

        return new DailySummaryResult(day, provisional, journal.Sha256, summary.Text, summary.Prompts, summary.UnrecognizedLines, recorded);
    }
}
