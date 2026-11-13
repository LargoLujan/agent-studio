using System.Collections.ObjectModel;
using System.IO;
using AgentStudio.Application.Diagrams;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Graph;
using AgentStudio.Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Docs;

/// <summary>A diagram of the list: its name, its state and the state of its copy in the repository, with the name a screen reader says.</summary>
public sealed class DiagramRowItem(DiagramStatus status, IUiTextProvider texts)
{
    public DiagramStatus Status { get; } = status;

    public DiagramRef Diagram => Status.Diagram;

    public string Name { get; } = DocsTexts.Name(texts, status.Diagram);

    public string StateText { get; } = DocsTexts.State(texts, status);

    public string ExportText { get; } = DocsTexts.Export(texts, status.Export);

    public bool IsGenerated => Status.State != DiagramState.NotGenerated;

    public bool IsStale => Status.State == DiagramState.Stale;

    public bool HasPrevious => Status.HasPrevious;

    /// <summary>The state is said in words and with a sign: never only a colour.</summary>
    public string StateSign => Status.State switch
    {
        DiagramState.Current => "✓",
        DiagramState.Stale => "⚠",
        _ => "○",
    };

    public string AutomationName { get; } = texts.Format("Docs_Row_Name", DocsTexts.Name(texts, status.Diagram), DocsTexts.State(texts, status), DocsTexts.Export(texts, status.Export));
}

/// <summary>A file the export did not write because it exists and is not the one Agent Studio wrote: what it is and why, for the question to the person.</summary>
public sealed class ExportConflictItem(DiagramExportConflict conflict, IUiTextProvider texts)
{
    public DiagramExportConflict Conflict { get; } = conflict;

    public string Text { get; } = texts.Format("Docs_Conflict_Item", conflict.Path, DocsTexts.Export(texts, conflict.State));
}

/// <summary>
/// «Diagramas» (docs/20 D-25 and D-26, F6-10): the diagrams of the project as text, each with its state («sin generar», «al día», «desactualizado») and the one of its copy in
/// the repository; the text itself, read-only; and «Generar», «Regenerar», «Deshacer», «Copiar» and «Exportar al repositorio». The text lives in a cache on this computer: it goes to
/// the repository only when the person asks, and a file that was changed outside, or that this computer did not write, is never overwritten without asking. The diagram is
/// not drawn here (Phase 16): the solution is drawn in «Grafo» and the plan on the canvas.
/// </summary>
public sealed partial class DocsDiagramsSection : GraphSection
{
    private readonly DocsBackend _backend;
    private readonly IUiTextProvider? _texts;
    private int _generation;
    private string? _sourceRaw;
    private Task _sourceTask = Task.CompletedTask;

    public DocsDiagramsSection(DocsBackend backend, IUiTextProvider? texts = null)
        : base("Info_Docs_Diagrams")
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _texts = texts;
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        Conflicts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasConflicts));
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy))
            {
                NotifyCommands();
            }
        };
    }

    private IUiTextProvider T => _texts ?? Texts.Current;

    public ObservableCollection<DiagramRowItem> Rows { get; } = [];

    public ObservableCollection<ExportConflictItem> Conflicts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GenerateLabel))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private DiagramRowItem? _selectedRow;

    /// <summary>The text of the diagram chosen, as the cache keeps it; empty when there is none to show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    private string _sourceText = string.Empty;

    /// <summary>What to say instead of the text: nothing chosen, not generated yet, or the cache lost it.</summary>
    [ObservableProperty]
    private string? _sourceNotice;

    public bool IsAvailable => _backend.Diagrams is not null && _backend.Reader is not null;

    public string UnavailableText => T.Get("Docs_Unavailable");

    public bool IsEmpty => IsAvailable && HasProject && Rows.Count == 0;

    public bool HasSelection => SelectedRow is not null;

    public bool HasSource => SourceText.Length > 0;

    public bool HasConflicts => Conflicts.Count > 0;

    /// <summary>«Generar» for a diagram that is not generated, «Regenerar» for one that is.</summary>
    public string GenerateLabel => T.Get(SelectedRow is { IsGenerated: true } ? "Docs_Regenerate" : "Docs_Generate");

    public bool CanCopyToClipboard => _backend.Clipboard is not null;

    /// <summary>The text that is being shown has been read; for tests and for whoever waits on the selection.</summary>
    public Task SourceShown => _sourceTask;

    protected override void ProjectChanged()
    {
        _generation++;
        IsBusy = false;
        Rows.Clear();
        Conflicts.Clear();
        SelectedRow = null;
        Message = null;
        SourceText = string.Empty;
        SourceNotice = null;
        _sourceRaw = null;
        OnPropertyChanged(nameof(IsEmpty));
        NotifyCommands();
    }

    public override async Task ReloadAsync(CancellationToken ct)
    {
        if (Project is not { } project || _backend.Diagrams is not { } catalog)
        {
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        var generation = ++_generation;
        IsBusy = true;
        Error = null;
        try
        {
            var list = await catalog.ListAsync(project, ct).ConfigureAwait(true);
            if (generation == _generation)
            {
                Apply(list, SelectedRow?.Diagram);
            }
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }
        }
#pragma warning disable CA1031 // The catalog is behind a port: why it failed is said, never thrown into the UI.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            if (generation == _generation)
            {
                Error = T.Format("Docs_Error_List", ex.Message);
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsBusy = false;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }

        await _sourceTask.ConfigureAwait(true);
    }

    private void Apply(IReadOnlyList<DiagramStatus> list, DiagramRef? keep)
    {
        Rows.Clear();
        foreach (var status in list)
        {
            Rows.Add(new DiagramRowItem(status, T));
        }

        SelectedRow = Rows.FirstOrDefault(r => keep is not null && r.Diagram == keep) ?? Rows.FirstOrDefault();

        // Every row is a new object, so choosing the same diagram again raises the change of the selection and reads its text; with nothing to choose it does not.
        if (SelectedRow is null)
        {
            _sourceTask = ShowSourceAsync(null);
        }

        NotifyCommands();
    }

    partial void OnSelectedRowChanged(DiagramRowItem? value)
    {
        _sourceTask = ShowSourceAsync(value);
        NotifyCommands();
    }

    private async Task ShowSourceAsync(DiagramRowItem? row)
    {
        _sourceRaw = null;
        SourceText = string.Empty;
        CopyCommand.NotifyCanExecuteChanged();
        if (row is null)
        {
            SourceNotice = T.Get("Docs_Source_Choose");
            return;
        }

        if (!row.IsGenerated)
        {
            SourceNotice = T.Get("Docs_Source_NotGenerated");
            return;
        }

        if (Project is not { } project || _backend.Reader is not { } reader)
        {
            return;
        }

        try
        {
            var text = await reader.ReadAsync(project, row.Diagram, CancellationToken.None).ConfigureAwait(true);
            if (!ReferenceEquals(row, SelectedRow))
            {
                return;
            }

            _sourceRaw = text;
            SourceText = text ?? string.Empty;
            SourceNotice = text is null ? T.Get("Docs_Source_Lost") : null;
        }
#pragma warning disable CA1031 // The reader is behind a port: why it failed is said, never thrown into the UI.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            SourceNotice = T.Format("Docs_Error_Read", ex.Message);
        }

        CopyCommand.NotifyCanExecuteChanged();
    }

    private bool CanAct() => Project is not null && _backend.Diagrams is not null && SelectedRow is not null && !IsBusy;

    private bool CanUndo() => CanAct() && SelectedRow!.HasPrevious;

    private bool CanExport() => CanAct() && SelectedRow!.IsGenerated;

    private bool CanCopy() => _sourceRaw is not null && _backend.Clipboard is not null;

    private void NotifyCommands()
    {
        GenerateCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    /// <summary>«Generar» and «Regenerar»: one command. On a diagram that is up to date it changes nothing and says so.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task GenerateAsync(CancellationToken ct) => RunAsync(
        async (project, catalog, row) =>
        {
            var after = await catalog.GenerateAsync(project, row.Diagram.Kind, row.Diagram.Subject, ct).ConfigureAwait(true);
            Message = T.Get(row.Status.State == DiagramState.Current && after.GeneratedAt == row.Status.GeneratedAt ? "Docs_Message_Unchanged" : "Docs_Message_Generated");
        },
        ct);

    /// <summary>«Deshacer»: puts the previous text of the diagram back, byte by byte.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private Task UndoAsync(CancellationToken ct) => RunAsync(
        async (project, catalog, row) =>
        {
            await catalog.RestorePreviousAsync(project, row.Diagram.Kind, row.Diagram.Subject, ct).ConfigureAwait(true);
            Message = T.Get("Docs_Message_Undone");
        },
        ct);

    /// <summary>«Exportar al repositorio»: writes the diagram in the default folder. A file that is not the one this computer wrote is not overwritten: the person is asked.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportAsync(CancellationToken ct) => RunAsync(
        async (project, catalog, row) =>
        {
            Conflicts.Clear();
            var result = await catalog.ExportAsync(
                project,
                new DiagramExportRequest([row.Diagram], DiagramExportRequest.DefaultFolder, DiagramExportConflictChoice.Ask),
                ct).ConfigureAwait(true);
            foreach (var conflict in result.Conflicts)
            {
                Conflicts.Add(new ExportConflictItem(conflict, T));
            }

            Message = result.Written.Count > 0
                ? T.Format("Docs_Message_Exported", result.Written.Count, result.Written[0].Path)
                : Conflicts.Count > 0 ? null : T.Get("Docs_Message_ExportedNone");
        },
        ct);

    /// <summary>The answer of the person to the question about the files that were changed outside: <c>overwrite</c>, <c>new</c> (another name) or <c>skip</c>.</summary>
    [RelayCommand]
    private async Task ResolveConflictAsync(string? choice, CancellationToken ct)
    {
        if (Project is not { } project || _backend.Diagrams is not { } catalog || Conflicts.Count == 0 || IsBusy)
        {
            return;
        }

        var pending = Conflicts.Select(c => c.Conflict.Diagram).ToList();
        Conflicts.Clear();
        var decision = choice switch
        {
            "overwrite" => DiagramExportConflictChoice.Overwrite,
            "new" => DiagramExportConflictChoice.SaveAsNew,
            _ => DiagramExportConflictChoice.Skip,
        };
        if (decision == DiagramExportConflictChoice.Skip)
        {
            Message = T.Get("Docs_Message_ExportedNone");
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            var result = await catalog.ExportAsync(project, new DiagramExportRequest(pending, DiagramExportRequest.DefaultFolder, decision), ct).ConfigureAwait(true);
            foreach (var conflict in result.Conflicts)
            {
                Conflicts.Add(new ExportConflictItem(conflict, T));
            }

            Message = result.Written.Count > 0
                ? T.Format("Docs_Message_Exported", result.Written.Count, result.Written[0].Path)
                : T.Get("Docs_Message_ExportedNone");
            Apply(await catalog.ListAsync(project, ct).ConfigureAwait(true), SelectedRow?.Diagram);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // The catalog is behind a port: why it failed is said, never thrown into the UI.
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
#pragma warning restore CA1031
        {
            Error = T.Format("Docs_Error_Export", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>«Copiar»: the text of the diagram on the clipboard.</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void Copy()
    {
        if (_sourceRaw is { } text && _backend.Clipboard is { } clipboard)
        {
            clipboard.Copy(text);
            Message = T.Get("Docs_Message_Copied");
        }
    }

    /// <summary>«Actualizar la lista»: reads the states again.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync(CancellationToken ct) => ReloadAsync(ct);

    private bool CanRefresh() => Project is not null && _backend.Diagrams is not null && !IsBusy;

    private async Task RunAsync(Func<ProjectContext, IDiagramCatalog, DiagramRowItem, Task> action, CancellationToken ct)
    {
        if (Project is not { } project || _backend.Diagrams is not { } catalog || SelectedRow is not { } row)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        Message = null;
        try
        {
            await action(project, catalog, row).ConfigureAwait(true);
            Apply(await catalog.ListAsync(project, ct).ConfigureAwait(true), row.Diagram);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // The catalog is behind a port: why it failed is said, never thrown into the UI.
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or AgentStudio.Application.Decisions.DecisionLogIntegrityException)
#pragma warning restore CA1031
        {
            Error = T.Format("Docs_Error_Action", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        await _sourceTask.ConfigureAwait(true);
    }
}
