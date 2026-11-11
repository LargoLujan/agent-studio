using System.Collections.ObjectModel;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Providers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator.Preparation;

/// <summary>What «Revisar el orden» needs of the document: the planning run of the proposal, the plan and the template as drawn now, and who plans.</summary>
public sealed record OrderReviewSubject(
    ProjectContext Project,
    RunId PlanningRun,
    PlanDefinition Plan,
    IReadOnlyList<AgentDefinition> Roster,
    AgentId Orchestrator);

/// <summary>One suggestion of «Revisar el orden», checked again by the system: what it says, whether the plan already does it, and its fix.</summary>
public sealed partial class OrderSuggestionItemViewModel : ObservableObject
{
    private readonly OrderReviewViewModel _owner;

    internal OrderSuggestionItemViewModel(OrderSuggestion suggestion, string text, string? buttonText, OrderReviewViewModel owner)
    {
        Suggestion = suggestion;
        Text = text;
        ButtonText = buttonText;
        _owner = owner;
    }

    public OrderSuggestion Suggestion { get; }

    /// <summary>What the suggestion says, written by the system from its kind and its two tasks: «T3 necesita T2».</summary>
    public string Text { get; }

    /// <summary>The reason the orchestrator gave: text of an agent, shown apart as a note of the orchestrator and never as a sentence of the system.</summary>
    public string Reason => Suggestion.Reason;

    public string? ButtonText { get; }

    public bool CanApply => Suggestion.Fix is not null && ButtonText is not null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private Task ApplyAsync() => _owner.ApplyAsync(Suggestion);

    public string AutomationName => $"{Text}. {ButtonText}".TrimEnd('.', ' ');

    public override string ToString() => AutomationName;
}

/// <summary>
/// «Revisar el orden con el orquestador» (docs/17 D-20, F13-10): offered when the person changed the plan or the team after the
/// proposal, so its notes of order may be old. It is an auxiliary run of the orchestrator, read only, that spends quota: it
/// needs the OK of the person each time, with the estimate in front. Its answer comes back as suggestions the system checked
/// again (<see cref="IOrderReview"/>); nothing is applied by itself, and each one is a fix of one click that can be undone.
/// </summary>
public sealed partial class OrderReviewViewModel : ObservableObject
{
    private readonly BuilderServices _services;
    private readonly Func<OrderReviewSubject?> _subject;
    private readonly Func<Fix, Task> _applyFix;
    private readonly CanvasText _text;

    public OrderReviewViewModel(BuilderServices services, Func<OrderReviewSubject?> subject, Func<Fix, Task> applyFix, CanvasText text, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(applyFix);
        _services = services;
        _subject = subject;
        _applyFix = applyFix;
        _text = text;
        Consent = new QuotaConsentCardViewModel(text, time);
    }

    /// <summary>The OK of the person for this use of quota, with the estimate.</summary>
    public QuotaConsentCardViewModel Consent { get; }

    public ObservableCollection<OrderSuggestionItemViewModel> Suggestions { get; } = [];

    public ObservableCollection<string> Discarded { get; } = [];

    /// <summary>The plan or the team changed after the proposal: its notes of order may be old, and the canvas says so (D-20).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfferText))]
    private bool _notesAreStale;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _status;

    /// <summary>The review can be asked for: the service is composed, the plan came from a planning run, and nothing else is going on.</summary>
    public bool IsAvailable => _services.OrderReview is not null && _subject() is not null;

    public string OfferText => NotesAreStale ? _text.Get("Canvas_Order_Stale") : _text.Get("Canvas_Order_Offer");

    public string UnavailableText => _services.OrderReview is null ? _text.Get("Canvas_Order_NotComposed") : _text.Get("Canvas_Order_NoPlanningRun");

    /// <summary>Marks the notes of order as old or as fresh, from the plan and the template of the proposal against those drawn now.</summary>
    public void Refresh(bool stale)
    {
        NotesAreStale = stale;
        AskCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(UnavailableText));
    }

    /// <summary>«Revisar el orden»: opens the card with the estimate. Nothing is read nor launched until the person says yes.</summary>
    [RelayCommand(CanExecute = nameof(CanAsk))]
    private async Task AskAsync()
    {
        if (_subject() is not { } subject)
        {
            return;
        }

        await Consent.OpenAsync(
            _text.Get("Canvas_Order_ConsentDescription"),
            _services.Estimator is { } estimator
                ? ct => EstimateAsync(estimator, subject, ct)
                : null).ConfigureAwait(true);
    }

    private bool CanAsk() => IsAvailable && !IsBusy && !Consent.IsOpen;

    private static async Task<UsageEstimate?> EstimateAsync(IUsageEstimator estimator, OrderReviewSubject subject, CancellationToken ct)
    {
        var orchestrator = subject.Roster.First(a => a.Id == subject.Orchestrator);
        return await estimator.EstimateAsync(
            new UsageEstimateRequest(subject.Project, orchestrator.Id, orchestrator.Model.Provider, orchestrator.Model.Model, AgentRunPurpose.OrderReview, Effort: orchestrator.Effort),
            ct).ConfigureAwait(false);
    }

    [RelayCommand]
    private void Cancel()
    {
        Consent.Close();
        AskCommand.NotifyCanExecuteChanged();
    }

    /// <summary>«Dar mi OK y revisar»: the OK of this use, which the service spends once.</summary>
    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (_services.OrderReview is not { } service || _subject() is not { } subject || !Consent.IsOpen)
        {
            return;
        }

        var consent = Consent.Grant();
        Consent.Close();
        IsBusy = true;
        Status = _text.Get("Canvas_Order_Running");
        Suggestions.Clear();
        Discarded.Clear();
        try
        {
            var result = await service.ReviewAsync(
                new OrderReviewRequest(subject.Project, subject.PlanningRun, subject.Plan, subject.Roster, subject.Orchestrator, consent),
                null,
                CancellationToken.None).ConfigureAwait(true);
            Show(result);
        }
#pragma warning disable CA1031 // The service is behind a port: why it failed is shown, never thrown into the UI.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Status = _text.Format("Canvas_Order_Failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            AskCommand.NotifyCanExecuteChanged();
        }
    }

    private void Show(OrderReviewResult result)
    {
        if (result.Error is { Length: > 0 } error)
        {
            Status = _text.Format("Canvas_Order_Failed", error);
        }
        else
        {
            Status = result.Suggestions.Count == 0 ? _text.Get("Canvas_Order_NothingToSuggest") : _text.Format("Canvas_Order_Suggestions", result.Suggestions.Count);
            NotesAreStale = false;
        }

        foreach (var suggestion in result.Suggestions)
        {
            Suggestions.Add(Item(suggestion));
        }

        foreach (var reason in result.Discarded)
        {
            Discarded.Add(reason);
        }
    }

    private OrderSuggestionItemViewModel Item(OrderSuggestion suggestion)
    {
        var text = suggestion.Kind switch
        {
            OrderSuggestionKind.Dependency => _text.Format("Canvas_Order_Dependency", suggestion.Second.Value, suggestion.First.Value),
            OrderSuggestionKind.ParallelOk => _text.Format("Canvas_Order_ParallelOk", suggestion.First.Value, suggestion.Second.Value),
            _ => _text.Format("Canvas_Order_Harmful", suggestion.First.Value, suggestion.Second.Value),
        };
        if (suggestion.Kept && suggestion.Fix is null && suggestion.Kind != OrderSuggestionKind.ParallelOk)
        {
            text += " " + _text.Get("Canvas_Order_AlreadyDone");
        }

        var button = suggestion.Fix is { } fix
            ? _text.Render(Domain.Orchestration.Analysis.Rules.MontageTexts.ButtonOf(fix).Code, Domain.Orchestration.Analysis.Rules.MontageTexts.ButtonOf(fix).Arguments)
            : null;
        return new OrderSuggestionItemViewModel(suggestion, text, button, this);
    }

    internal async Task ApplyAsync(OrderSuggestion suggestion)
    {
        if (suggestion.Fix is { } fix)
        {
            await _applyFix(fix).ConfigureAwait(true);
        }
    }
}
