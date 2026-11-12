using System.Collections.ObjectModel;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.ViewModels;

public partial class ProviderDiagnosticsViewModel : ObservableObject
{
    private readonly ProviderDiagnostics _diagnostics;
    private readonly TimeProvider _timeProvider;

    private readonly OpenProjectRoots? _roots;

    public ProviderDiagnosticsViewModel(ProviderDiagnostics diagnostics, TimeProvider timeProvider, OpenProjectRoots? roots = null)
    {
        _roots = roots;
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _diagnostics = diagnostics;
        _timeProvider = timeProvider;
    }

    public ObservableCollection<ProviderStatusItem> Providers { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private DateTimeOffset? _lastCheckedAt;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>True after a successful check that found no registered providers.</summary>
    [ObservableProperty]
    private bool _isEmpty;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            Apply(await _diagnostics.RunAsync(CancellationToken.None));
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
        }
    }

    /// <summary>
    /// Shows the results of a run of the probes that someone else made: Auto Doctor (docs/20 D-04) runs them once for all its checks and hands the same results
    /// to the rows of the providers, so opening the panel does not launch every program twice. The summary of the top bar follows this list as it follows its own.
    /// </summary>
    public void Apply(IReadOnlyList<ProviderProbeResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        Providers.Clear();
        foreach (var result in results)
        {
            var item = ProviderStatusItem.From(result);
            Providers.Add(_roots?.BroadRoots is { Count: > 0 } broad ? item.WithProjectTooBroad(broad[0]) : item);
        }

        IsEmpty = Providers.Count == 0;
        LastCheckedAt = _timeProvider.GetLocalNow();
    }

    private bool CanRefresh() => !IsBusy;
}
