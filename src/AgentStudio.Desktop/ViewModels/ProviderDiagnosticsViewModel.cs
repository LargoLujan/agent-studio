using System.Collections.ObjectModel;
using AgentStudio.Application.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.ViewModels;

public partial class ProviderDiagnosticsViewModel : ObservableObject
{
    private readonly ProviderDiagnostics _diagnostics;
    private readonly TimeProvider _timeProvider;

    public ProviderDiagnosticsViewModel(ProviderDiagnostics diagnostics, TimeProvider timeProvider)
    {
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
            var results = await _diagnostics.RunAsync(CancellationToken.None);
            Providers.Clear();
            foreach (var result in results)
            {
                Providers.Add(ProviderStatusItem.From(result));
            }

            IsEmpty = Providers.Count == 0;
            LastCheckedAt = _timeProvider.GetLocalNow();
        }
#pragma warning disable CA1031 // The UI must show any failure instead of crashing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo completar la comprobación: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRefresh() => !IsBusy;
}
