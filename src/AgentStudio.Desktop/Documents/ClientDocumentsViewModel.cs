using System.Collections.ObjectModel;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Documents;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Memory.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Documents;

/// <summary>
/// The «Documentación para clientes» document (docs/17 D-58 to D-60; ADR-0057): the person picks the milestone or delivery, the
/// language and the formats, generates the preview (the Markdown of the model the system builds), reads the list of what goes out (the
/// secrets redacted, the paths of the computer removed, the parts agents wrote and the risks) and, only after approving it, exports
/// to a folder. Nothing is exported without the approval, and a change that alters the document (the milestone, the language) takes
/// the preview and the approval back. The export never overwrites (a file that exists gets a suffix) and the log records every one.
/// For a milestone that has just been closed, <see cref="GenerateForMilestoneAsync"/> is the entry point.
/// </summary>
public sealed partial class ClientDocumentsViewModel : DocumentViewModel
{
    public const string DocumentId = "documents:clients";

    private readonly IProjectWorkspace? _workspace;
    private readonly IClientDocumentBuilder? _builder;
    private readonly IClientDocumentExporter? _exporter;
    private readonly IClientDocumentFormatWriter? _markdownWriter;
    private readonly IReadOnlySet<ClientDocumentFormat> _available;
    private readonly IFolderPicker? _folders;
    private ProjectContext? _project;
    private ClientDocumentBuild? _build;
    private string _builtLanguage = UiLanguages.SourceCulture;
    private int _generation;

    /// <param name="workspace">Opens the project to know its id.</param>
    /// <param name="builder">Builds the model; null when it was not composed (the screen says so).</param>
    /// <param name="exporter">Writes the files and records the export; null when it was not composed.</param>
    /// <param name="writers">The writers of the formats this installation has; the one of Markdown also paints the preview.</param>
    /// <param name="folders">Asks for the destination folder.</param>
    public ClientDocumentsViewModel(
        IProjectWorkspace? workspace,
        IClientDocumentBuilder? builder,
        IClientDocumentExporter? exporter,
        IEnumerable<IClientDocumentFormatWriter>? writers,
        IFolderPicker? folders)
        : base(DocumentId, Texts.Get("Documents_Title"))
    {
        _workspace = workspace;
        _builder = builder;
        _exporter = exporter;
        _folders = folders;
        var list = writers?.ToList() ?? [];
        _markdownWriter = list.FirstOrDefault(w => w.Format == ClientDocumentFormat.Markdown);
        _available = list.Select(w => w.Format).ToHashSet();
        _selectedLanguage = UiLanguages.Find(Texts.Current.Culture.Name) ?? UiLanguages.All[0];
        _exportMarkdown = _available.Contains(ClientDocumentFormat.Markdown);
        _exportHtml = false;
        _exportPdf = false;
        _exportWord = _available.Contains(ClientDocumentFormat.Word);
        StatusMessage = Texts.Get("Documents_Status_OpenFolder");
    }

    /// <summary>The languages the document can be written in: the languages of the interface.</summary>
    public IReadOnlyList<UiLanguage> Languages => UiLanguages.All;

    /// <summary>The paths of the computer that were taken out, as the person is shown them.</summary>
    public ObservableCollection<string> PathsRemoved { get; } = [];

    /// <summary>The parts of the document an agent wrote, with the run and whether the person reviewed them.</summary>
    public ObservableCollection<string> AgentParts { get; } = [];

    /// <summary>The files the last export wrote, by name.</summary>
    public ObservableCollection<string> ExportedFiles { get; } = [];

    public bool HasPdf => _available.Contains(ClientDocumentFormat.Pdf);

    public bool HasWord => _available.Contains(ClientDocumentFormat.Word);

    public bool HasHtml => _available.Contains(ClientDocumentFormat.Html);

    public bool HasMarkdown => _available.Contains(ClientDocumentFormat.Markdown);

    /// <summary>The name of the milestone, or empty for the delivery of the project.</summary>
    [ObservableProperty]
    private string _milestone = string.Empty;

    [ObservableProperty]
    private UiLanguage _selectedLanguage;

    [ObservableProperty]
    private bool _exportMarkdown;

    [ObservableProperty]
    private bool _exportHtml;

    [ObservableProperty]
    private bool _exportPdf;

    [ObservableProperty]
    private bool _exportWord;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private string _destination = string.Empty;

    /// <summary>The Markdown of the document, as the preview.</summary>
    [ObservableProperty]
    private string _preview = string.Empty;

    [ObservableProperty]
    private string _secretsText = string.Empty;

    [ObservableProperty]
    private string _pathsText = string.Empty;

    [ObservableProperty]
    private string _agentsText = string.Empty;

    [ObservableProperty]
    private string _risksText = string.Empty;

    [ObservableProperty]
    private string _consumptionText = string.Empty;

    /// <summary>The person reviewed the preview and the list of what goes out, and approves exporting.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _approved;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Whether there is a preview, and so a list of what goes out to review.</summary>
    public bool HasPreview => _build is not null;

    /// <summary>The project open in the shell changed (null: none): the preview of the other one is dropped.</summary>
    public async Task SetProjectAsync(string? projectRoot)
    {
        Forget();
        ErrorMessage = null;
        _project = null;
        ExportedFiles.Clear();
        if (projectRoot is null)
        {
            StatusMessage = Texts.Get("Documents_Status_OpenFolder");
            GenerateCommand.NotifyCanExecuteChanged();
            return;
        }

        if (_workspace is null || _builder is null || _exporter is null || _markdownWriter is null)
        {
            StatusMessage = Texts.Get("Documents_Status_Unavailable");
            return;
        }

        try
        {
            _project = await _workspace.OpenAsync(projectRoot, CancellationToken.None).ConfigureAwait(true);
            StatusMessage = null;
        }
#pragma warning disable CA1031 // The workspace is behind a port: a failure is said on the screen, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = Texts.Format("Documents_Error_Generate", ex.Message);
        }

        GenerateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The entry point for a milestone that was just closed (docs/16 D-49): sets its name and builds the preview. The closing of a
    /// milestone calls it once the work package of the milestones exists; the screen reaches it today by typing the name.
    /// </summary>
    public Task GenerateForMilestoneAsync(string milestone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(milestone);
        Milestone = milestone;
        return GenerateAsync();
    }

    /// <summary>A milestone was closed and its document is offered (docs/16 D-49): the shell opens this tab. Nothing was generated or exported.</summary>
    public event EventHandler? MilestoneOffered;

    /// <summary>
    /// A milestone of <paramref name="projectRoot"/> was closed: the screen follows that project, takes the name of the milestone and says
    /// that its document can be generated. It builds nothing and exports nothing: the person generates, reviews and approves.
    /// </summary>
    public async Task OfferForClosedMilestoneAsync(string projectRoot, string milestone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(milestone);
        await SetProjectAsync(projectRoot).ConfigureAwait(true);
        Milestone = milestone;
        StatusMessage = Texts.Format("Documents_MilestoneClosed", milestone);
        MilestoneOffered?.Invoke(this, EventArgs.Empty);
    }

    partial void OnMilestoneChanged(string value) => Forget();

    partial void OnSelectedLanguageChanged(UiLanguage value) => Forget();

    private bool CanGenerate() => !IsBusy && _project is not null && _builder is not null;

    /// <summary>Builds the model and its preview, and the list of what goes out; the approval starts again.</summary>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        if (_project is not { } project || _builder is null || _markdownWriter is null)
        {
            return;
        }

        IsBusy = true;
        Forget();

        // What the document is made with is fixed now: the language and the milestone of the screen can change while it builds, and the
        // result is then dropped (Forget moves the generation), never exported with the ones of that moment.
        var generation = _generation;
        var language = SelectedLanguage.CultureName;
        var milestone = string.IsNullOrWhiteSpace(Milestone) ? null : Milestone.Trim();
        ErrorMessage = null;
        StatusMessage = Texts.Get("Documents_Status_Generating");
        try
        {
            var build = await _builder.BuildAsync(new ClientDocumentBuildRequest(project, milestone, IncludeConsumption: false, []), CancellationToken.None).ConfigureAwait(true);
            var markdown = await _markdownWriter.RenderAsync(build.Document, language, CancellationToken.None).ConfigureAwait(true);
            if (generation != _generation)
            {
                StatusMessage = null;
                return;
            }

            _build = build;
            _builtLanguage = language;
            Preview = System.Text.Encoding.UTF8.GetString(markdown);
            ShowDisclosure(build.Disclosure);
            StatusMessage = Texts.Format("Documents_Status_Ready", build.Document.Cover.Version);
        }
        catch (DecisionLogIntegrityException)
        {
            StatusMessage = null;
            ErrorMessage = Texts.Get("Documents_Error_LogIntegrity");
        }
#pragma warning disable CA1031 // The builder reads stores: a failure is said on the screen, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusMessage = null;
            ErrorMessage = Texts.Format("Documents_Error_Generate", ex.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasPreview));
        }
    }

    [RelayCommand]
    private void ChooseFolder()
    {
        if (_folders?.PickFolder(Texts.Get("Documents_Destination_Title")) is { } folder)
        {
            Destination = folder;
        }
    }

    private bool CanExport() => !IsBusy && Approved && _build is not null && !string.IsNullOrWhiteSpace(Destination);

    /// <summary>«Aprobar y exportar»: writes the formats chosen where the person chose, and the log records it.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (_project is not { } project || _exporter is null || _build is not { } build)
        {
            return;
        }

        var formats = ChosenFormats();
        if (formats.Count == 0)
        {
            ErrorMessage = Texts.Get("Documents_Error_NeedFormat");
            return;
        }

        if (!System.IO.Path.IsPathFullyQualified(Destination))
        {
            ErrorMessage = Texts.Get("Documents_Error_NeedFolder");
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = Texts.Get("Documents_Status_Exporting");
        ExportedFiles.Clear();
        try
        {
            // The language and the document are those of the preview the person approved, not the ones the screen shows now.
            var request = new ClientDocumentExportRequest(
                project, build, formats, _builtLanguage, Destination, ApprovalPromptService.DecidedBy, ClientDocumentApproval.Of(build, ApprovalPromptService.DecidedBy));
            var result = await _exporter.ExportAsync(request, CancellationToken.None).ConfigureAwait(true);
            foreach (var file in result.Files)
            {
                ExportedFiles.Add(file.FileName);
            }

            StatusMessage = Texts.Format("Documents_Status_Exported", result.Files.Count, result.Version);
            Forget();
        }
        catch (DecisionLogIntegrityException)
        {
            StatusMessage = null;
            ErrorMessage = Texts.Get("Documents_Error_LogIntegrity");
        }
        catch (ClientDocumentRunBusyException)
        {
            StatusMessage = null;
            ErrorMessage = Texts.Get("Documents_Error_RunBusy");
        }
#pragma warning disable CA1031 // The exporter writes files and the log: a failure is said on the screen, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusMessage = null;
            ErrorMessage = Texts.Format("Documents_Error_Export", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private List<ClientDocumentFormat> ChosenFormats()
    {
        var formats = new List<ClientDocumentFormat>();
        if (ExportMarkdown && HasMarkdown)
        {
            formats.Add(ClientDocumentFormat.Markdown);
        }

        if (ExportHtml && HasHtml)
        {
            formats.Add(ClientDocumentFormat.Html);
        }

        if (ExportPdf && HasPdf)
        {
            formats.Add(ClientDocumentFormat.Pdf);
        }

        if (ExportWord && HasWord)
        {
            formats.Add(ClientDocumentFormat.Word);
        }

        return formats;
    }

    /// <summary>Drops the preview, the list and the approval: what the person reviewed is no longer what would be exported.</summary>
    private void Forget()
    {
        _generation++;
        _build = null;
        Approved = false;
        Preview = string.Empty;
        SecretsText = string.Empty;
        PathsText = string.Empty;
        AgentsText = string.Empty;
        RisksText = string.Empty;
        ConsumptionText = string.Empty;
        PathsRemoved.Clear();
        AgentParts.Clear();
        OnPropertyChanged(nameof(HasPreview));
        ExportCommand.NotifyCanExecuteChanged();
    }

    private void ShowDisclosure(ClientDocumentDisclosure disclosure)
    {
        SecretsText = Texts.Format("Documents_Disclosure_Secrets", disclosure.SecretsRedacted);
        PathsText = Texts.Format("Documents_Disclosure_Paths", disclosure.PathsRemoved.Count);
        PathsRemoved.Clear();
        foreach (var path in disclosure.PathsRemoved)
        {
            PathsRemoved.Add(path);
        }

        AgentsText = Texts.Format("Documents_Disclosure_Agents", disclosure.AgentWrittenParts.Count);
        AgentParts.Clear();
        foreach (var part in disclosure.AgentWrittenParts)
        {
            AgentParts.Add(Texts.Format(
                "Documents_Disclosure_AgentPart",
                Texts.Render(UserText.Of(ClientDocumentSections.TitleCode(part.Section))),
                part.Run.Value,
                Texts.Get(part.ReviewedByPerson ? "Documents_Disclosure_Yes" : "Documents_Disclosure_No")));
        }

        RisksText = Texts.Format("Documents_Disclosure_Risks", disclosure.RisksIncluded);
        ConsumptionText = disclosure.IncludesConsumption ? string.Empty : Texts.Get("Documents_Disclosure_NoConsumption");
    }
}
