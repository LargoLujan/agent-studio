using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Doctor;

/// <summary>What a fix of a guide may ask of the desktop: copy the command and open the official page. Nothing else: the app never runs a command it shows.</summary>
public interface IDoctorActions
{
    void Copy(string text);

    void OpenPage(Uri page);
}

/// <summary>The real actions: the clipboard of Windows and the browser of the person, for an <c>https</c> page the guide named.</summary>
public sealed class SystemDoctorActions : IDoctorActions
{
    public void Copy(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard is held by another program: the person can select the command and copy it by hand.
        }
    }

    public void OpenPage(Uri page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser registered: nothing to open, and the page is shown as text anyway.
        }
    }
}

/// <summary>Where «Ir a…» can go: the codes of the screens the shell opens, and how it opens one.</summary>
public sealed record DoctorNavigation(IReadOnlySet<string> Targets, Action<string> Go);

/// <summary>
/// The panel of Auto Doctor (docs/20 D-04 and section 6.1): five folded sections over one run of every source, with the rows of the providers as they were.
/// It runs when the panel is opened and when the person presses «Volver a comprobar»; the result is not kept (docs/20 section 5). The probes of the providers
/// run once per run and their results go to the rows of today (<see cref="ProviderDiagnosticsViewModel.Apply"/>).
/// </summary>
public sealed partial class DoctorViewModel : ObservableObject
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    private readonly IDoctorService? _service;
    private readonly ProviderCheckSource? _providerSource;
    private readonly IDoctorFixRunner? _fixRunner;
    private readonly TimeProvider _time;
    private readonly IProjectWorkspace? _workspace;
    private readonly IDoctorActions _actions;
    private readonly Func<string?, DoctorFix?> _findFix;
    private readonly IUiTextProvider? _texts;
    private ProjectContext? _project;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private DateTimeOffset? _lastCheckedAt;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _notice;

    /// <param name="service">Runs every source; without it the panel shows only the providers, as before Phase 6 (a shell built by hand).</param>
    /// <param name="providers">The rows of the providers, which this panel contains and feeds.</param>
    /// <param name="providerSource">Gives the results of the probes of the run, to hand them to <paramref name="providers"/>.</param>
    /// <param name="workspace">Opens the open project to give its context to the sources of the project; without it, no project is checked.</param>
    /// <param name="findFix">Finds the fix a check names by code; the closed list of <see cref="DoctorFixCatalog"/> by default.</param>
    /// <param name="texts">The texts of the language in use at the time the panel runs; <see cref="Texts.Current"/> when null.</param>
    public DoctorViewModel(
        IDoctorService? service,
        ProviderDiagnosticsViewModel providers,
        ProviderCheckSource? providerSource,
        IDoctorFixRunner? fixRunner,
        TimeProvider time,
        IProjectWorkspace? workspace = null,
        IDoctorActions? actions = null,
        Func<string?, DoctorFix?>? findFix = null,
        IUiTextProvider? texts = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(time);
        _service = service;
        Providers = providers;
        _providerSource = providerSource;
        _fixRunner = fixRunner;
        _time = time;
        _workspace = workspace;
        _actions = actions ?? new SystemDoctorActions();
        _findFix = findFix ?? DoctorFixCatalog.Find;
        _texts = texts;
    }

    /// <summary>The rows of the providers of today, with their status and the top bar that follows them.</summary>
    public ProviderDiagnosticsViewModel Providers { get; }

    public ObservableCollection<DoctorSectionViewModel> Sections { get; } = [];

    /// <summary>The root of the open project, or null; the shell sets it.</summary>
    public Func<string?>? ProjectRoot { get; set; }

    /// <summary>Where «Ir a…» can go; without it that fix is not offered.</summary>
    public DoctorNavigation? Navigation { get; set; }

    /// <summary>True when the panel has run and found no section to show: nothing registered.</summary>
    public bool IsEmpty => Sections.Count == 0 && LastCheckedAt is not null;

    /// <summary>True until the panel ran for the first time, so the view can say nothing was checked yet.</summary>
    public bool NeverChecked => LastCheckedAt is null;

    private IUiTextProvider TextsNow => _texts ?? Texts.Current;

    /// <summary>The panel was shown: it checks if it never did or the last time was a while ago.</summary>
    public async Task OpenedAsync()
    {
        if (!IsBusy && (LastCheckedAt is null || _time.GetLocalNow() - LastCheckedAt > StaleAfter))
        {
            await RefreshAsync();
        }
    }

    internal bool CanNavigate(string target) => Navigation?.Targets.Contains(target) == true;

    internal void NavigateTo(string target)
    {
        if (CanNavigate(target))
        {
            Navigation!.Go(target);
        }
    }

    internal void Copy(string text) => _actions.Copy(text);

    internal void OpenPage(Uri page) => _actions.OpenPage(page);

    internal async Task<DoctorFixResult> ApplyAsync(DoctorFix fix, string checkCode)
    {
        if (_fixRunner is null)
        {
            return new DoctorFixResult(DoctorFixOutcome.Failed);
        }

        try
        {
            return await _fixRunner.ApplyAsync(fix, checkCode, _project, CancellationToken.None);
        }
#pragma warning disable CA1031 // The panel shows that the fix failed; it never breaks.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new DoctorFixResult(DoctorFixOutcome.Failed);
        }
    }

    internal async Task UndoAsync(DoctorCheckViewModel check, long sequence)
    {
        if (_fixRunner is null)
        {
            return;
        }

        try
        {
            await _fixRunner.UndoAsync(sequence, _project, CancellationToken.None);
            check.Undone();
        }
#pragma warning disable CA1031 // The panel shows the failure; it never breaks.
        catch (Exception)
#pragma warning restore CA1031
        {
            check.FixMessage = Texts.Get("Doctor_Fix_Failed");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        Notice = null;
        try
        {
            var root = ProjectRoot?.Invoke();
            _project = await OpenProjectAsync(root);
            if (_service is null)
            {
                // A shell built by hand: only the providers, as before.
                await Providers.RefreshCommand.ExecuteAsync(null);
                Sections.Clear();
                Sections.Add(ProvidersOnlySection());
                LastCheckedAt = Providers.LastCheckedAt ?? _time.GetLocalNow();
                return;
            }

            var report = await _service.RunAsync(_project, CancellationToken.None);
            if (_providerSource is not null)
            {
                Providers.Apply(_providerSource.LastResults);
            }

            Show(report);
            LastCheckedAt = _time.GetLocalNow();
        }
#pragma warning disable CA1031 // The UI must show any failure instead of crashing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = Texts.Format("Providers_CheckFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(NeverChecked));
        }
    }

    private bool CanRefresh() => !IsBusy;

    /// <summary>The context of the open project, or null: opening it creates nothing the orchestrator would not, and a project that cannot be opened is said, not hidden.</summary>
    private async Task<ProjectContext?> OpenProjectAsync(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || _workspace is null)
        {
            return null;
        }

        try
        {
            return await _workspace.OpenAsync(root, CancellationToken.None);
        }
#pragma warning disable CA1031 // A project in use by another instance or with a storage that is refused is a finding of its own, not a failure of the panel.
        catch (Exception)
#pragma warning restore CA1031
        {
            Notice = Texts.Get("Doctor_ProjectUnavailable");
            return null;
        }
    }

    /// <summary>The section of the providers over the rows alone, for a panel with no service of checks: the worst row decides its state, and a parked provider is neutral.</summary>
    private DoctorSectionViewModel ProvidersOnlySection()
    {
        var worst = DoctorStatusRules.Worst(Providers.Providers.Select(p => p.Status switch
        {
            StatusKind.Ok => DoctorStatus.Ok,
            StatusKind.Warning => DoctorStatus.Warning,
            StatusKind.Error => DoctorStatus.Error,
            _ => DoctorStatus.NotApplicable,
        }));
        var inUse = Providers.Providers.Where(p => !p.IsParked).ToList();
        var summary = inUse.Count == 0 ? TextsNow.Get("Doctor_Section_NoneApply") : TextsNow.Format("Doctor_Section_Count", inUse.Count(p => p.Status == StatusKind.Ok), inUse.Count);
        return new DoctorSectionViewModel(DoctorSection.Providers, [], worst, TextsNow, summary);
    }

    private void Show(DoctorReport report)
    {
        var texts = TextsNow;
        Sections.Clear();
        foreach (var section in Enum.GetValues<DoctorSection>())
        {
            var checks = report.Checks.Where(c => c.Section == section).ToList();
            if (checks.Count == 0)
            {
                // A section with no source registered (the declared environment until it is composed) is not shown as «no aplica»: it is not there.
                continue;
            }

            var items = checks.Select(c => new DoctorCheckViewModel(c, _findFix(c.FixCode), this, texts)).ToList();
            Sections.Add(new DoctorSectionViewModel(section, items, report.WorstOf(section), texts));
        }
    }
}
