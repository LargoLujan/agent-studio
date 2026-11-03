using System.Globalization;
using System.IO;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;

namespace AgentStudio.Desktop.Memory;

/// <summary>
/// Keeps the memory of a project up to date from its log (docs/14 D-18 and D-22). When a run of the project ends, the
/// memory is derived from the verified log and then exported to Markdown. When a project is opened, it is derived only if
/// the project already has its storage: opening a folder never creates <c>.agentstudio/</c>. The derivation and the export
/// refuse on their own while a run or a planning of the project goes on, and say why.
/// </summary>
public sealed class ProjectMemoryUpkeep
{
    private readonly IProjectWorkspace? _workspace;
    private readonly IMemoryDerivation? _derivation;
    private readonly IMemoryExporter? _exporter;

    /// <param name="workspace">Opens the project to know its id; null when the shell was composed without it.</param>
    /// <param name="derivation">Derives the memory from the log; null when it was not composed (nothing is done).</param>
    /// <param name="exporter">The Markdown export after a run; null when it was not composed (only the derivation runs).</param>
    public ProjectMemoryUpkeep(IProjectWorkspace? workspace, IMemoryDerivation? derivation, IMemoryExporter? exporter)
    {
        _workspace = workspace;
        _derivation = derivation;
        _exporter = exporter;
    }

    /// <summary>Whether there is anything to keep: a workspace and a derivation were composed.</summary>
    public bool IsAvailable => _workspace is not null && _derivation is not null;

    /// <summary>A run of the project ended: derives, then exports. Returns a line for the status bar, or null when nothing changed.</summary>
    public async Task<string?> AfterRunAsync(string projectRoot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (!IsAvailable)
        {
            return null;
        }

        var project = await _workspace!.OpenAsync(projectRoot, ct).ConfigureAwait(true);
        var derived = await _derivation!.DeriveAsync(project, ct).ConfigureAwait(true);
        var line = Describe(derived);
        if (!derived.Completed || _exporter is null)
        {
            return line;
        }

        var exported = await _exporter.ExportAsync(project, ct).ConfigureAwait(true);
        var export = exported.Exported
            ? string.Create(CultureInfo.InvariantCulture, $"exportada a .agentstudio/memory/ ({exported.Files.Count} ficheros, {exported.Entries} entradas)")
            : $"sin exportar: {exported.Reason}";
        return line is null ? $"Memoria {export}." : $"{line.TrimEnd('.')}; {export}.";
    }

    /// <summary>A project was opened: derives its memory when it already has its storage. Returns a line for the status bar, or null.</summary>
    public async Task<string?> OnProjectOpenedAsync(string projectRoot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (!IsAvailable || !HasStorage(projectRoot))
        {
            return null;
        }

        var project = await _workspace!.OpenAsync(projectRoot, ct).ConfigureAwait(true);
        return Describe(await _derivation!.DeriveAsync(project, ct).ConfigureAwait(true));
    }

    /// <summary>The project already has its database (<c>.agentstudio/agentstudio.db</c>): Agent Studio opened it before.</summary>
    private static bool HasStorage(string projectRoot) => File.Exists(Path.Combine(projectRoot, ".agentstudio", "agentstudio.db"));

    private static string? Describe(MemoryDerivationResult derived)
    {
        if (!derived.Completed)
        {
            return $"Memoria sin derivar: {derived.Reason}";
        }

        var parts = new List<string>();
        if (derived.Recorded.Count > 0)
        {
            parts.Add(derived.Recorded.Count == 1
                ? "1 entrada nueva derivada del registro"
                : string.Create(CultureInfo.InvariantCulture, $"{derived.Recorded.Count} entradas nuevas derivadas del registro"));
        }

        // A problem is a text of an agent whose evidence is not what the log anchored, or a day of the journal that could
        // not be read safely (docs/17 D-02): nothing of either went to the memory.
        if (derived.Problems.Count > 0)
        {
            parts.Add(derived.Problems.Count == 1
                ? "1 texto sin guardar en la memoria (no cuadra con el registro o es un día de la bitácora que no se pudo leer)"
                : string.Create(CultureInfo.InvariantCulture, $"{derived.Problems.Count} textos sin guardar en la memoria (no cuadran con el registro o son días de la bitácora que no se pudieron leer)"));
        }

        return parts.Count == 0 ? null : $"Memoria: {string.Join("; ", parts)}.";
    }
}
