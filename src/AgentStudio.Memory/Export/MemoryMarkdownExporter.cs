using System.Globalization;
using System.Text;
using AgentStudio.Application.Documents;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Memory.Derivation;
using AgentStudio.Memory.Store;

namespace AgentStudio.Memory.Export;

/// <summary>
/// Exports the memory of a project to Markdown in <c>.agentstudio/memory/</c> (docs/14 D-22; ADR-0022, decision 8). The
/// files are an export: Agent Studio never reads them back, and editing them changes nothing.
/// <list type="bullet">
/// <item><b>What.</b> Only trusted entries (the system's, the user's and the accepted ones). The chronological files
/// (<c>cronologico-AAAA-MM.md</c>, one per local month) keep the superseded ones, marked; the thematic ones
/// (<c>tema-&lt;categoría&gt;.md</c>, ten at most) keep only what is current, by topic.</item>
/// <item><b>Which files.</b> Every month and every category the log ever announced an entry of, so a file written by an
/// earlier export is rewritten even when nothing trusted is left in it: a retracted entry leaves the Markdown at the next
/// export.</item>
/// <item><b>How.</b> Whole files through <see cref="IProjectStorageFiles.Replace"/>, which follows no link and replaces in
/// one move; in a fixed order, UTF-8 without BOM and <c>\n</c>, with no time of export, so the same memory always gives the
/// same bytes. Topic, content and source are redacted again (<see cref="ISecretRedactor"/>) and escaped
/// (<see cref="MarkdownText"/>); times are local.</item>
/// <item><b>Which language.</b> The one the person chose, given to <see cref="ExportAsync"/> as a parameter (never the culture of the
/// thread: the export runs on a worker). The frame of the files (titles, introductions, the names of the fields, categories and origins,
/// the month) is painted from the resources by <see cref="IUserTextRenderer"/>, like the documents for clients. What each entry holds
/// (topic and content) stays as it was anchored: the export prints its SHA-256 beside it, and the memory is never translated (docs/17
/// D-48). The same memory and language always give the same bytes.</item>
/// <item><b>When.</b> Never while a run or a planning goes on (<see cref="IRunActivity"/>): the check of the user's
/// repository compares everything under <c>.agentstudio/</c>, so the export would fail the task. It is refused before the
/// first file, and stops before the next one if a run starts meanwhile; the next export rewrites everything.</item>
/// <item>On a log that does not verify nothing is exported (<see cref="Application.Decisions.DecisionLogIntegrityException"/>).</item>
/// </list>
/// </summary>
public sealed class MemoryMarkdownExporter(
    ProjectMemory memory,
    IProjectStorageFiles files,
    IRunActivity activity,
    ISecretRedactor redactor,
    TimeProvider timeProvider,
    IUserTextRenderer texts) : IMemoryExporter
{
    /// <summary>The folder of the export, under <c>.agentstudio</c>; its file names are <see cref="AgentStudioFiles"/>'s, the one list every reader of the repository shares.</summary>
    public const string Folder = AgentStudioFiles.MemoryExportFolder;

    /// <summary>Why the export waits, in Spanish, for the log and the tests; the screen says the code (<see cref="DerivationUserTexts.ExportBusyReason"/>).</summary>
    private const string BusyReason = "Hay una ejecución, una planificación, un debate o una auditoría en curso en el proyecto: la memoria se exporta cuando termine.";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<MemoryExportResult> ExportAsync(ProjectContext project, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!LanguageTags.IsValid(language))
        {
            throw new ArgumentException("The language is the name of a culture, such as es-ES or en.", nameof(language));
        }

        if (activity.IsBusy(project.ProjectId))
        {
            return MemoryExportResult.Refused(BusyReason, DerivationUserTexts.ExportBusyReason());
        }

        var snapshot = await memory.SnapshotAsync(project.ProjectId, ct).ConfigureAwait(false);
        var zone = timeProvider.LocalTimeZone;
        // The journal entries stay out (docs/17 D-02): their source already is the journal, Markdown in the repository.
        var trusted = snapshot.Entries.Where(e => e.Trust == MemoryTrust.Trusted && MemoryKinds.IsExported(e.Kind)).ToList();
        var announced = snapshot.Announced.Where(a => MemoryKinds.IsExported(a.Payload.Kind)).ToList();
        var documents = new List<(string Name, string Text)>();
        foreach (var month in announced.Select(a => MonthOf(a.At, zone)).Distinct().Order())
        {
            documents.Add((AgentStudioFiles.ChronologicalMemoryFile(month), Chronological(month, [.. trusted.Where(e => MonthOf(e.CreatedAt, zone) == month)], zone, language)));
        }

        foreach (var kind in Enum.GetValues<MemoryKind>().Where(k => announced.Any(a => a.Payload.Kind == k)))
        {
            documents.Add((AgentStudioFiles.ThematicMemoryFile(kind), Thematic(kind, [.. trusted.Where(e => e.Kind == kind && e.SupersededBy is null)], zone, language)));
        }

        var written = new List<string>();
        foreach (var (name, text) in documents)
        {
            ct.ThrowIfCancellationRequested();
            if (activity.IsBusy(project.ProjectId))
            {
                return new MemoryExportResult(false, written, 0, BusyReason, DerivationUserTexts.ExportBusyReason());
            }

            var path = Folder + "/" + name;
            files.Replace(project.RootPath, path, Utf8.GetBytes(text));
            written.Add(path);
        }

        return new MemoryExportResult(true, written, trusted.Count);
    }

    private string Chronological(DateOnly month, IReadOnlyList<MemoryEntry> entries, TimeZoneInfo zone, string language)
    {
        var text = new StringBuilder();
        text.Append(Say(language, "memory.export.header")).Append('\n')
            .Append("# ").Append(Say(language, "memory.export.chronological-title", new UserText("memory.export.month", [new DateTimeOffset(month.Year, month.Month, 1, 0, 0, 0, TimeSpan.Zero)]))).Append('\n')
            .Append('\n').Append(Say(language, "memory.export.chronological-intro")).Append('\n');
        if (entries.Count == 0)
        {
            text.Append('\n').Append(Say(language, "memory.export.no-month-memory")).Append('\n');
        }

        foreach (var entry in entries.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            text.Append('\n').Append("## ").Append(Local(entry.CreatedAt, zone)).Append(" · ").Append(Say(language, KindLabel(entry.Kind))).Append(" · ").Append(Data(entry.Topic)).Append('\n');
            AppendBody(text, entry, language);
        }

        return text.ToString();
    }

    private string Thematic(MemoryKind kind, IReadOnlyList<MemoryEntry> entries, TimeZoneInfo zone, string language)
    {
        var text = new StringBuilder();
        text.Append(Say(language, "memory.export.header")).Append('\n')
            .Append("# ").Append(Say(language, "memory.export.thematic-title", TitleOf(kind))).Append('\n')
            .Append('\n').Append(Say(language, "memory.export.thematic-intro")).Append('\n');
        if (entries.Count == 0)
        {
            text.Append('\n').Append(Say(language, "memory.export.no-kind-memory")).Append('\n');
        }

        var topics = entries
            .GroupBy(e => e.Topic, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key, StringComparer.Ordinal);
        foreach (var topic in topics)
        {
            text.Append('\n').Append("## ").Append(Data(topic.Key)).Append('\n');
            foreach (var entry in topic.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id, StringComparer.Ordinal))
            {
                text.Append('\n').Append("### ").Append(Local(entry.CreatedAt, zone)).Append('\n');
                AppendBody(text, entry, language);
            }
        }

        return text.ToString();
    }

    /// <summary>The content as a quote, line by line, and what identifies the entry: its id, origin, source, hash and supersession.</summary>
    private void AppendBody(StringBuilder text, MemoryEntry entry, string language)
    {
        text.Append('\n');
        foreach (var line in redactor.Redact(entry.Content).TrimEnd('\r', '\n').Split('\n'))
        {
            var clean = line.TrimEnd('\r');
            text.Append(clean.Length == 0 ? ">" : "> " + MarkdownText.Escape(clean)).Append('\n');
        }

        text.Append('\n')
            .Append(Say(language, "memory.export.entry-line", MarkdownText.Escape(entry.Id), new UserText(OriginCode(entry.Origin)))).Append('\n')
            .Append(Say(language, "memory.export.source-line", Data(entry.Source))).Append('\n')
            .Append(Say(language, "memory.export.sha-line", "SHA-256", MarkdownText.Escape(entry.ContentSha256))).Append('\n');
        if (entry.Supersedes is { } supersedes)
        {
            text.Append(Say(language, "memory.export.supersedes-line", MarkdownText.Escape(supersedes))).Append('\n');
        }

        if (entry.SupersededBy is { } supersededBy)
        {
            text.Append(Say(language, "memory.export.superseded-by-line", MarkdownText.Escape(supersededBy))).Append('\n');
        }
    }

    /// <summary>A sentence of the frame of the files, in <paramref name="language"/>.</summary>
    private string Say(string language, string code, params object[] arguments) => texts.Render(new UserText(code, arguments), language);

    /// <summary>Text of an entry on one line: redacted, with its line breaks as spaces, and escaped.</summary>
    private string Data(string text) => MarkdownText.Escape(redactor.Redact(text).ReplaceLineEndings(" "));

    private static DateOnly MonthOf(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return new DateOnly(local.Year, local.Month, 1);
    }

    private static string Local(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The code of the name of a category, with a capital letter, for the heading of an entry of a chronological file.</summary>
    internal static string KindLabel(MemoryKind kind) => "memory.export.kind." + DerivationUserTexts.Kebab(KnownKind(kind).ToString());

    /// <summary>The title of the file of a category that is exported (<see cref="MemoryKinds.IsExported"/>; the journal is not), as a coded text.</summary>
    internal static UserText TitleOf(MemoryKind kind) => new("memory.export.title." + DerivationUserTexts.Kebab(KnownKind(kind).ToString()));

    private static string OriginCode(MemoryOrigin origin) => origin switch
    {
        MemoryOrigin.System => "memory.export.origin.system",
        MemoryOrigin.User => "memory.export.origin.user",
        MemoryOrigin.Agent => "memory.export.origin.agent",
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown memory origin."),
    };

    private static MemoryKind KnownKind(MemoryKind kind) =>
        Enum.IsDefined(kind) ? kind : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown memory category.");
}
