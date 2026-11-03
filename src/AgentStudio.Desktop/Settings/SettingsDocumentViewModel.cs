using System.Globalization;
using System.IO;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Settings;

/// <summary>
/// The «Ajustes» document (docs/03 §5.2 and §2.1): every preference of the app in one tab, one section each, and one «Guardar».
/// <list type="bullet">
/// <item>«Contexto y cuota» (<see cref="Context"/>: the context thresholds and the Codex opt-in, docs/14 D-11 and D-15),
/// «Trabajo desatendido» (<see cref="Unattended"/>: closing the window and keeping the computer awake, docs/16 D-14 and D-40)
/// and «Windows Hello y bloqueo» (<see cref="Lock"/>, docs/16 D-13). A section that was not composed is not shown.</item>
/// <item>«Guardar» checks every section first, with the rules of each, and saves nothing while one is wrong: each says why
/// next to its fields, and the document says which. Then it writes all the preferences at once, with one
/// <see cref="IAppSettingsStore.SavePreferences"/> (atomic), and only then each section takes what was saved and says so (its
/// <c>Saved</c> event): the orchestrators get the thresholds, «Consumo» the opt-in, and «mantener despierto» reads its setting
/// again.</item>
/// <item>A change on screen that is not saved marks the document as dirty, so closing its tab or leaving the app asks first,
/// as with a file; a tab closed without saving drops the change, and opening it again shows what is in force.</item>
/// <item>«Guardar» chosen in that question and not done (a section is wrong, the file could not be written) keeps the change,
/// and the document asks to be shown (<see cref="ShowRequested"/>): leaving the app with the window in the tray, the shell
/// shows it, and the person sees why instead of an app that just did not exit.</item>
/// </list>
/// One document for the session, like «Consumo».
/// </summary>
public sealed partial class SettingsDocumentViewModel : DocumentViewModel
{
    public const string DocumentId = "document:settings";

    /// <summary>What the preferences cover and where they live, as the screen says it.</summary>
    public const string ScopeText = "Valen para todos los proyectos y se guardan en tu perfil de Windows (%APPDATA%\\AgentStudio\\settings.json).";

    /// <summary>What «Guardar» does, as the screen says it.</summary>
    public const string SaveExplanation =
        "«Guardar» comprueba todas las secciones y las guarda a la vez: si una no es válida, no se guarda nada. Si cierras la pestaña sin guardar, te pregunta.";

    private readonly IAppSettingsStore _store;
    private readonly TimeProvider _time;

    /// <param name="store">Where the preferences are saved.</param>
    /// <param name="context">«Contexto y cuota»: the same view model «Consumo» judges its report with.</param>
    /// <param name="unattended">«Trabajo desatendido»; null when Phase 8 was not composed.</param>
    /// <param name="lockSettings">«Windows Hello y bloqueo»; by default the one of <paramref name="unattended"/>, and null when neither has it.</param>
    /// <param name="time">The clock of «Ajustes guardados a las …»; the system's by default.</param>
    public SettingsDocumentViewModel(
        IAppSettingsStore store,
        ObservabilitySettingsViewModel context,
        UnattendedSettingsViewModel? unattended = null,
        LockSettingsViewModel? lockSettings = null,
        TimeProvider? time = null)
        : base(DocumentId, "Ajustes")
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(context);
        _store = store;
        _time = time ?? TimeProvider.System;
        Context = context;
        Unattended = unattended;
        Lock = lockSettings ?? unattended?.Lock;

        // A change on screen, or a save (which changes what is in force, often without changing what is on screen).
        Context.PropertyChanged += OnSectionChanged;
        Context.Saved += OnSectionChanged;
        if (Unattended is not null)
        {
            Unattended.PropertyChanged += OnSectionChanged;
            Unattended.Saved += OnSectionChanged;
        }

        if (Lock is not null)
        {
            Lock.PropertyChanged += OnSectionChanged;
            Lock.Saved += OnSectionChanged;
        }

        Closed += (_, _) => Revert();
        IsDirty = HasChanges();
    }

    /// <summary>«Contexto y cuota».</summary>
    public ObservabilitySettingsViewModel Context { get; }

    /// <summary>«Trabajo desatendido»; null when it was not composed.</summary>
    public UnattendedSettingsViewModel? Unattended { get; }

    /// <summary>«Windows Hello y bloqueo»; null when it was not composed.</summary>
    public LockSettingsViewModel? Lock { get; }

    /// <summary>«Ajustes guardados a las 14:05:12.»; null until a save succeeds, and after the next try.</summary>
    [ObservableProperty]
    private string? _notice;

    /// <summary>Why nothing was saved: which sections are wrong, or why the file could not be written; null otherwise.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Raised when «Guardar», chosen in the question about unsaved changes (closing the tab, leaving the app), could not save:
    /// the document must be shown, because its sections say why and the window may be hidden in the tray.
    /// </summary>
    public event EventHandler? ShowRequested;

    /// <summary>A change on screen can always be saved from here (closing the tab or leaving the app offers «Guardar»).</summary>
    public override bool CanSave => true;

    /// <summary>
    /// «Guardar» asked from the question about unsaved changes: the same as the button. When a section is wrong or the file
    /// could not be written, the document stays dirty and says why, so the tab or the app stays open, and it asks to be shown
    /// (<see cref="ShowRequested"/>): the change is never left behind unseen.
    /// </summary>
    public override Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Save();
        if (ErrorMessage is not null)
        {
            ShowRequested?.Invoke(this, EventArgs.Empty);
        }

        return Task.CompletedTask;
    }

    /// <summary>«Guardar»: checks every section and saves all the preferences at once, or says why nothing was saved.</summary>
    [RelayCommand]
    private void Save()
    {
        Notice = null;
        ErrorMessage = null;
        var wrong = new List<string>();
        if (Context.Validate() is not null)
        {
            wrong.Add(ObservabilitySettingsViewModel.Title);
        }

        if (Lock?.Validate() is not null)
        {
            wrong.Add(LockSettingsViewModel.Title);
        }

        if (wrong.Count > 0)
        {
            ErrorMessage = $"No se guardó nada: corrige {string.Join(" y ", wrong.Select(title => $"«{title}»"))}.";
            return;
        }

        AppSettings saved;
        try
        {
            var preferences = Context.Apply(_store.Load());
            if (Unattended is not null)
            {
                preferences = Unattended.Apply(preferences);
            }

            if (Lock is not null)
            {
                preferences = Lock.Apply(preferences);
            }

            _store.SavePreferences(preferences);
            saved = preferences;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"No se pudieron guardar los ajustes: {ex.Message}";
            return;
        }

        Context.Accept(saved);
        Unattended?.Accept(saved);
        Lock?.Accept(saved);
        Notice = $"Ajustes guardados a las {_time.GetLocalNow().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}.";
    }

    /// <summary>The tab was closed: what was not saved goes, so opening it again shows what is in force.</summary>
    private void Revert()
    {
        Context.Revert();
        Unattended?.Revert();
        Lock?.Revert();
        Notice = null;
        ErrorMessage = null;
        IsDirty = HasChanges();
    }

    private void OnSectionChanged(object? sender, EventArgs e) => IsDirty = HasChanges();

    private bool HasChanges() => Context.HasChanges || Unattended?.HasChanges == true || Lock?.HasChanges == true;
}
