using System.Globalization;
using AgentStudio.Application.Knowledge;

namespace AgentStudio.Application.Projects;

/// <summary>
/// The files Agent Studio itself keeps in a project's <c>.agentstudio</c> folder, by the exact names its writers give them:
/// the prompt journal (ADR-0008), the Markdown export of the memory (ADR-0022), the database, the evidence and temporary
/// folders, the canvas layout and the verification profile. One closed list for the writers and for whoever reads the
/// user's repository, so a name never drifts from the writer that gives it, and a file of the user's that only looks like
/// one of them (<c>memory/tema-notas.md</c>, say) is never taken for one.
/// </summary>
public static class AgentStudioFiles
{
    /// <summary>The folder of a project's data, in its root.</summary>
    public const string DataFolder = ".agentstudio";

    /// <summary>The folder of the prompt journal, in <see cref="DataFolder"/>: one file a day (<see cref="JournalDayFile"/>).</summary>
    public const string JournalFolder = "bitacora";

    /// <summary>The folder of the Markdown export of the memory, in <see cref="DataFolder"/>.</summary>
    public const string MemoryExportFolder = "memory";

    /// <summary>The folder of the evidence store, in <see cref="DataFolder"/>.</summary>
    public const string EvidenceFolder = "evidence";

    /// <summary>The temporary folder of the storage, in <see cref="DataFolder"/>.</summary>
    public const string TempFolder = "tmp";

    /// <summary>Where the orchestrator canvas placed its nodes on this machine.</summary>
    public const string CanvasLayoutFile = "canvas-layout.json";

    /// <summary>The trusted verification profile (ADR-0016).</summary>
    public const string VerificationProfileFile = "verification.json";

    private const string JournalDayFormat = "yyyy-MM-dd";
    private const string MonthFormat = "yyyy-MM";
    private const string ChronologicalPrefix = "cronologico-";
    private const string ThematicPrefix = "tema-";
    private const string Markdown = ".md";

    /// <summary>The database files of the project (<c>agentstudio.db</c>, its <c>-wal</c> and <c>-shm</c> and its <c>.lock</c>), and nothing else.</summary>
    public static IReadOnlyList<string> DatabaseFiles { get; } = ["agentstudio.db", "agentstudio.db-wal", "agentstudio.db-shm", "agentstudio.db.lock"];

    /// <summary>
    /// The file of the memory export of every category that is exported (<see cref="MemoryKinds.IsExported"/>), in the order
    /// of <see cref="MemoryKind"/>: ten, no more. The journal entries have none: their source already is the journal.
    /// </summary>
    public static IReadOnlyList<string> ThematicMemoryFiles { get; } = [.. Enum.GetValues<MemoryKind>().Where(MemoryKinds.IsExported).Select(ThematicMemoryFile)];

    /// <summary>The day of the journal of <paramref name="day"/>: <c>AAAA-MM-DD.md</c>.</summary>
    public static string JournalDayFile(DateOnly day) => day.ToString(JournalDayFormat, CultureInfo.InvariantCulture) + Markdown;

    /// <summary>
    /// Whether <paramref name="name"/> is a day of the journal exactly as its writers name it (<c>MarkdownDailyJournal</c> and
    /// the development hook of ADR-0008): <c>AAAA-MM-DD.md</c>, with a real date. Any other name there is not the journal.
    /// </summary>
    public static bool IsJournalDay(string name) =>
        name is not null
        && name.Length == JournalDayFormat.Length + Markdown.Length
        && name.EndsWith(Markdown, StringComparison.Ordinal)
        && DateOnly.TryParseExact(name.AsSpan(0, JournalDayFormat.Length), JournalDayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>The file of the memory export of one local month: <c>cronologico-AAAA-MM.md</c>.</summary>
    public static string ChronologicalMemoryFile(DateOnly month) => ChronologicalPrefix + month.ToString(MonthFormat, CultureInfo.InvariantCulture) + Markdown;

    /// <summary>The file of the memory export of one category: <c>tema-&lt;categoría&gt;.md</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The category is not one of <see cref="MemoryKind"/>, or it is not exported (<see cref="MemoryKind.JournalEntry"/>).</exception>
    public static string ThematicMemoryFile(MemoryKind kind) => ThematicPrefix + kind switch
    {
        MemoryKind.Architecture => "arquitectura",
        MemoryKind.Convention => "convenciones",
        MemoryKind.Decision => "decisiones",
        MemoryKind.KnownError => "errores-conocidos",
        MemoryKind.Solution => "soluciones",
        MemoryKind.PendingTask => "tareas-pendientes",
        MemoryKind.Change => "cambios",
        MemoryKind.DebateSummary => "resumenes-de-debate",
        MemoryKind.Verdict => "veredictos",
        MemoryKind.DailySummary => "resumenes-diarios",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown memory category."),
    } + Markdown;

    /// <summary>
    /// Whether <paramref name="name"/> is a file the memory export writes: the file of a month (<see cref="ChronologicalMemoryFile"/>,
    /// with a real month) or of one of the categories (<see cref="ThematicMemoryFiles"/>), exactly, and nothing else.
    /// </summary>
    public static bool IsMemoryExportFile(string name)
    {
        if (name is null)
        {
            return false;
        }

        if (ThematicMemoryFiles.Contains(name, StringComparer.Ordinal))
        {
            return true;
        }

        return name.Length == ChronologicalPrefix.Length + MonthFormat.Length + Markdown.Length
            && name.StartsWith(ChronologicalPrefix, StringComparison.Ordinal)
            && name.EndsWith(Markdown, StringComparison.Ordinal)
            && DateOnly.TryParseExact(name.AsSpan(ChronologicalPrefix.Length, MonthFormat.Length), MonthFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    /// <summary>
    /// Whether a line of <c>git status --porcelain</c> (<c>XY path</c>, or <c>XY from -&gt; to</c> for a rename or a copy),
    /// with paths relative to the root of the repository, names a file Agent Studio keeps in the data folder of the project,
    /// new or changed: a day of the journal, a file of the memory export, a database file, anything in the evidence or the
    /// temporary folder, the canvas layout or the verification profile. A file deleted, renamed or copied there is not
    /// (Agent Studio never does that), nor a line that cannot be read: those count as changes of the user.
    /// </summary>
    /// <param name="projectPrefix">
    /// Where the project is in the repository, relative to its root with <c>/</c> at the end (<c>apps/calc/</c>); empty when
    /// the project is the repository.
    /// </param>
    public static bool IsAgentStudioStatusLine(string line, string projectPrefix = "")
    {
        ArgumentNullException.ThrowIfNull(projectPrefix);
        if (line is null || line.Length < 4 || line[2] != ' ' || !line[..2].All(c => c is ' ' or 'M' or 'A' or '?'))
        {
            return false;
        }

        var path = line[3..];
        if (path.Contains(" -> ", StringComparison.Ordinal))
        {
            return false;
        }

        path = Unquote(path).Replace('\\', '/');
        var folder = projectPrefix + DataFolder + "/";
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var inside = path[folder.Length..];
        if (Under(inside, EvidenceFolder) || Under(inside, TempFolder))
        {
            return true;
        }

        var slash = inside.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return DatabaseFiles.Contains(inside, StringComparer.OrdinalIgnoreCase)
                || inside.Equals(CanvasLayoutFile, StringComparison.OrdinalIgnoreCase)
                || inside.Equals(VerificationProfileFile, StringComparison.OrdinalIgnoreCase);
        }

        var subfolder = inside[..slash];
        var name = inside[(slash + 1)..];
        return !name.Contains('/', StringComparison.Ordinal)
            && ((subfolder.Equals(JournalFolder, StringComparison.OrdinalIgnoreCase) && IsJournalDay(name))
                || (subfolder.Equals(MemoryExportFolder, StringComparison.OrdinalIgnoreCase) && IsMemoryExportFile(name)));
    }

    /// <summary>The folder itself (listed once, with its trailing <c>/</c>) or anything in it.</summary>
    private static bool Under(string inside, string folder) =>
        inside.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    private static string Unquote(string path)
    {
        var text = path.Trim();
        return text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
    }
}
