using System.Globalization;
using System.IO;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;

namespace AgentStudio.Desktop.Memory;

/// <summary>
/// Keeps the memory of a project up to date from its log (docs/14 D-18 and D-22). When a run of the project ends, the
/// memory is derived from the verified log and then exported to Markdown. When a project is opened, it is derived only if
/// the project already has its storage: opening a folder never creates <c>.agentstudio/</c>. The derivation and the export
/// refuse on their own while a run or a planning of the project goes on, and say why.
/// <para>
/// Phase 12 (docs/17 D-04; ADR-0047), with a <see cref="SemanticIndexUpkeep"/>: each derivation that completes asks for a
/// refresh of the semantic index of the project, in the background (the index lives outside the project, so it does not
/// wait for anything, and nothing here waits for it), and opening a project opens the local model ahead when it is already
/// in place, so the first search does not pay its load. Nothing is downloaded.
/// </para>
/// </summary>
public sealed class ProjectMemoryUpkeep
{
    private readonly IProjectWorkspace? _workspace;
    private readonly IMemoryDerivation? _derivation;
    private readonly IMemoryExporter? _exporter;
    private readonly SemanticIndexUpkeep? _index;

    /// <param name="workspace">Opens the project to know its id; null when the shell was composed without it.</param>
    /// <param name="derivation">Derives the memory from the log; null when it was not composed (nothing is done).</param>
    /// <param name="exporter">The Markdown export after a run; null when it was not composed (only the derivation runs).</param>
    /// <param name="index">Refreshes the semantic index after each derivation; null when it was not composed.</param>
    public ProjectMemoryUpkeep(IProjectWorkspace? workspace, IMemoryDerivation? derivation, IMemoryExporter? exporter, SemanticIndexUpkeep? index = null)
    {
        _workspace = workspace;
        _derivation = derivation;
        _exporter = exporter;
        _index = index;
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
        RefreshIndex(project, derived);
        var line = Describe(derived);
        if (!derived.Completed || _exporter is null)
        {
            return line;
        }

        var exported = await _exporter.ExportAsync(project, MemoryReasonTexts.ExportLanguage, ct).ConfigureAwait(true);
        var export = exported.Exported
            ? Texts.Format("Memory_Upkeep_Exported", exported.Files.Count, exported.Entries)
            : Texts.Format("Memory_Upkeep_NotExported", MemoryReasonTexts.Say(exported.ReasonText, exported.Reason));
        return line is null ? Texts.Format("Memory_Upkeep_Alone", export) : Texts.Format("Memory_Upkeep_Joined", line.TrimEnd('.'), export);
    }

    /// <summary>
    /// A project was opened: the local model is opened ahead when it is already in place, and the memory is derived when the
    /// project already has its storage. Returns a line for the status bar, or null.
    /// </summary>
    public async Task<string?> OnProjectOpenedAsync(string projectRoot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (_index is { } index)
        {
            // In the background: it reads only the folder of the model, never the project, and never downloads (F12-6).
            _ = index.WarmUpAsync();
        }

        if (!IsAvailable || !HasStorage(projectRoot))
        {
            return null;
        }

        var project = await _workspace!.OpenAsync(projectRoot, ct).ConfigureAwait(true);
        var derived = await _derivation!.DeriveAsync(project, ct).ConfigureAwait(true);
        RefreshIndex(project, derived);
        return Describe(derived);
    }

    /// <summary>
    /// A derivation that completed asks for a refresh of the index, which runs in the background and never fails its caller;
    /// one that did not (the log does not verify, a run goes on) asks for none: the refresh would write nothing.
    /// </summary>
    private void RefreshIndex(ProjectContext project, MemoryDerivationResult derived)
    {
        if (derived.Completed && _index is { IsAvailable: true } index)
        {
            _ = index.RefreshAsync(project);
        }
    }

    /// <summary>The project already has its database (<c>.agentstudio/agentstudio.db</c>): Agent Studio opened it before.</summary>
    private static bool HasStorage(string projectRoot) => File.Exists(Path.Combine(projectRoot, ".agentstudio", "agentstudio.db"));

    private static string? Describe(MemoryDerivationResult derived)
    {
        if (!derived.Completed)
        {
            return Texts.Format("Memory_Upkeep_NotDerived", MemoryReasonTexts.Say(derived.ReasonText, derived.Reason));
        }

        var parts = new List<string>();
        if (derived.Recorded.Count > 0)
        {
            parts.Add(derived.Recorded.Count == 1
                ? Texts.Get("Memory_Upkeep_New_One")
                : Texts.Format("Memory_Upkeep_New_Other", derived.Recorded.Count));
        }

        // A problem is a text of an agent whose evidence is not what the log anchored, or a day of the journal that could
        // not be read safely (docs/17 D-02): nothing of either went to the memory.
        if (derived.Problems.Count > 0)
        {
            parts.Add(derived.Problems.Count == 1
                ? Texts.Get("Memory_Upkeep_Problems_One")
                : Texts.Format("Memory_Upkeep_Problems_Other", derived.Problems.Count));
        }

        return parts.Count == 0 ? null : Texts.Format("Memory_Upkeep_Summary", string.Join("; ", parts));
    }
}
