using System.Globalization;
using System.Text;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
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
/// <item><b>When.</b> Never while a run or a planning goes on (<see cref="IRunActivity"/>): the check of the user's
/// repository compares everything under <c>.agentstudio/</c>, so the export would fail the task. It is refused before the
/// first file, and stops before the next one if a run starts meanwhile; the next export rewrites everything.</item>
/// <item>On a log that does not verify nothing is exported (<see cref="Application.Decisions.DecisionLogIntegrityException"/>).</item>
/// </list>
/// </summary>
public sealed class MemoryMarkdownExporter(ProjectMemory memory, IProjectStorageFiles files, IRunActivity activity, ISecretRedactor redactor, TimeProvider timeProvider) : IMemoryExporter
{
    /// <summary>The folder of the export, under <c>.agentstudio</c>; its file names are <see cref="AgentStudioFiles"/>'s, the one list every reader of the repository shares.</summary>
    public const string Folder = AgentStudioFiles.MemoryExportFolder;

    private const string BusyReason = "Hay una ejecución, una planificación, un debate o una auditoría en curso en el proyecto: la memoria se exporta cuando termine.";

    private const string Header = "<!-- Generado por Agent Studio a partir de la memoria del proyecto. Editar este fichero no cambia la memoria: la próxima exportación lo sustituye. -->";

    private const string ChronologicalIntro = "Memoria de confianza (del sistema, del usuario y la aceptada) en orden cronológico y en hora local.";

    private const string ThematicIntro = "Memoria de confianza vigente (del sistema, del usuario y la aceptada, sin lo sustituido), por tema y en hora local.";

    private static readonly string[] Months = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<MemoryExportResult> ExportAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (activity.IsBusy(project.ProjectId))
        {
            return MemoryExportResult.Refused(BusyReason);
        }

        var snapshot = await memory.SnapshotAsync(project.ProjectId, ct).ConfigureAwait(false);
        var zone = timeProvider.LocalTimeZone;
        // The journal entries stay out (docs/17 D-02): their source already is the journal, Markdown in the repository.
        var trusted = snapshot.Entries.Where(e => e.Trust == MemoryTrust.Trusted && MemoryKinds.IsExported(e.Kind)).ToList();
        var announced = snapshot.Announced.Where(a => MemoryKinds.IsExported(a.Payload.Kind)).ToList();
        var documents = new List<(string Name, string Text)>();
        foreach (var month in announced.Select(a => MonthOf(a.At, zone)).Distinct().Order())
        {
            documents.Add((AgentStudioFiles.ChronologicalMemoryFile(month), Chronological(month, [.. trusted.Where(e => MonthOf(e.CreatedAt, zone) == month)], zone)));
        }

        foreach (var kind in Enum.GetValues<MemoryKind>().Where(k => announced.Any(a => a.Payload.Kind == k)))
        {
            documents.Add((AgentStudioFiles.ThematicMemoryFile(kind), Thematic(kind, [.. trusted.Where(e => e.Kind == kind && e.SupersededBy is null)], zone)));
        }

        var written = new List<string>();
        foreach (var (name, text) in documents)
        {
            ct.ThrowIfCancellationRequested();
            if (activity.IsBusy(project.ProjectId))
            {
                return new MemoryExportResult(false, written, 0, BusyReason);
            }

            var path = Folder + "/" + name;
            files.Replace(project.RootPath, path, Utf8.GetBytes(text));
            written.Add(path);
        }

        return new MemoryExportResult(true, written, trusted.Count);
    }

    private string Chronological(DateOnly month, IReadOnlyList<MemoryEntry> entries, TimeZoneInfo zone)
    {
        var text = new StringBuilder();
        text.Append(Header).Append('\n')
            .Append("# Memoria del proyecto · ").Append(Months[month.Month - 1]).Append(" de ").Append(month.Year.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append('\n').Append(ChronologicalIntro).Append('\n');
        if (entries.Count == 0)
        {
            text.Append('\n').Append("_No queda memoria de confianza de este mes._").Append('\n');
        }

        foreach (var entry in entries.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            text.Append('\n').Append("## ").Append(Local(entry.CreatedAt, zone)).Append(" · ").Append(Capitalized(MemoryLabels.Kind(entry.Kind))).Append(" · ").Append(Data(entry.Topic)).Append('\n');
            AppendBody(text, entry);
        }

        return text.ToString();
    }

    private string Thematic(MemoryKind kind, IReadOnlyList<MemoryEntry> entries, TimeZoneInfo zone)
    {
        var text = new StringBuilder();
        text.Append(Header).Append('\n')
            .Append("# Memoria del proyecto · ").Append(Title(kind)).Append('\n')
            .Append('\n').Append(ThematicIntro).Append('\n');
        if (entries.Count == 0)
        {
            text.Append('\n').Append("_No hay memoria de confianza vigente en esta categoría._").Append('\n');
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
                AppendBody(text, entry);
            }
        }

        return text.ToString();
    }

    /// <summary>The content as a quote, line by line, and what identifies the entry: its id, origin, source, hash and supersession.</summary>
    private void AppendBody(StringBuilder text, MemoryEntry entry)
    {
        text.Append('\n');
        foreach (var line in redactor.Redact(entry.Content).TrimEnd('\r', '\n').Split('\n'))
        {
            var clean = line.TrimEnd('\r');
            text.Append(clean.Length == 0 ? ">" : "> " + MarkdownText.Escape(clean)).Append('\n');
        }

        text.Append('\n')
            .Append("- Entrada: ").Append(MarkdownText.Escape(entry.Id)).Append(" (").Append(MemoryLabels.Origin(entry.Origin)).Append(")\n")
            .Append("- Fuente: ").Append(Data(entry.Source)).Append('\n')
            .Append("- SHA-256: ").Append(MarkdownText.Escape(entry.ContentSha256)).Append('\n');
        if (entry.Supersedes is { } supersedes)
        {
            text.Append("- Sustituye a: ").Append(MarkdownText.Escape(supersedes)).Append('\n');
        }

        if (entry.SupersededBy is { } supersededBy)
        {
            text.Append("- Sustituida por: ").Append(MarkdownText.Escape(supersededBy)).Append('\n');
        }
    }

    /// <summary>Text of an entry on one line: redacted, with its line breaks as spaces, and escaped.</summary>
    private string Data(string text) => MarkdownText.Escape(redactor.Redact(text).ReplaceLineEndings(" "));

    private static DateOnly MonthOf(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return new DateOnly(local.Year, local.Month, 1);
    }

    private static string Local(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Capitalized(string label) => char.ToUpper(label[0], CultureInfo.InvariantCulture) + label[1..];

    /// <summary>The title of the file of a category that is exported (<see cref="MemoryKinds.IsExported"/>); the journal is not.</summary>
    internal static string Title(MemoryKind kind) => kind switch
    {
        MemoryKind.Architecture => "Arquitectura",
        MemoryKind.Convention => "Convenciones",
        MemoryKind.Decision => "Decisiones",
        MemoryKind.KnownError => "Errores conocidos",
        MemoryKind.Solution => "Soluciones",
        MemoryKind.PendingTask => "Tareas pendientes",
        MemoryKind.Change => "Cambios",
        MemoryKind.DebateSummary => "Resúmenes de debate",
        MemoryKind.Verdict => "Veredictos",
        MemoryKind.DailySummary => "Resúmenes diarios",
        MemoryKind.ProjectBrief => "Ficha del proyecto",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown memory category."),
    };
}
