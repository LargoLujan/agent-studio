using System.Collections.ObjectModel;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Orchestration.Analysis.Rules;
using AgentStudio.Domain.Requirements;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator.Preparation;

/// <summary>One turn of the conversation: what the person wrote, or what the orchestrator answered (text of an agent, shown as such).</summary>
public sealed record ChatTurn(bool IsPerson, string Author, string Text)
{
    public override string ToString() => $"{Author}: {Text}";
}

/// <summary>A draft of a requirement of the answer: «Copiar al editor» puts it in the editor of the requirements as a draft of the person.</summary>
public sealed partial class ChatRequirementItemViewModel : ObservableObject
{
    private readonly Func<RequirementDraft, bool> _copy;

    internal ChatRequirementItemViewModel(RequirementDraft draft, string priorityText, Func<RequirementDraft, bool> copy)
    {
        Draft = draft;
        PriorityText = priorityText;
        _copy = copy;
    }

    public RequirementDraft Draft { get; }

    public string PriorityText { get; }

    public string Statement => Draft.Statement;

    public IReadOnlyList<string> Criteria => Draft.Criteria;

    [ObservableProperty]
    private bool _isCopied;

    [RelayCommand]
    private void Copy() => IsCopied = _copy(Draft) || IsCopied;

    public string AutomationName => $"{PriorityText}: {Statement}";

    public override string ToString() => AutomationName;
}

/// <summary>A suggestion for the team of the answer, checked against the template: never applied by itself, only by the click of the person.</summary>
public sealed partial class ChatTeamItemViewModel : ObservableObject
{
    private readonly Func<Fix, Task> _apply;

    internal ChatTeamItemViewModel(TeamSuggestion suggestion, string buttonText, Func<Fix, Task> apply)
    {
        Suggestion = suggestion;
        ButtonText = buttonText;
        _apply = apply;
    }

    public TeamSuggestion Suggestion { get; }

    /// <summary>The button of the fix, written by the system: «Cambiar a sonnet».</summary>
    public string ButtonText { get; }

    /// <summary>Why the orchestrator suggests it: text of an agent, shown apart.</summary>
    public string Reason => Suggestion.Reason;

    public bool WithdrawsApproval => Suggestion.Fix.ChangesRoster;

    public bool AsksHello => Suggestion.Fix.GrantsHighRiskCapability;

    [RelayCommand]
    private Task ApplyAsync() => _apply(Suggestion.Fix);

    public string AutomationName => ButtonText;

    public override string ToString() => AutomationName;
}

/// <summary>
/// The chat with the orchestrator to prepare the work (docs/17 D-44, F13-18): the person writes freely what they want to do and the
/// orchestrator answers read only, in an auxiliary run (<see cref="IPreparationChat"/>) that spends quota. Each message needs the OK
/// of the person, with the estimate in front, and the same conversation is resumed from one message to the next. What comes back
/// are suggestions, and none is applied by itself: a draft of a requirement is copied to the editor by a click and frozen by the
/// person; a suggestion for the team is a fix of one click that can be undone.
/// </summary>
public sealed partial class PreparationChatViewModel : ObservableObject
{
    private readonly BuilderServices _services;
    private readonly Func<PreparationChatSubject?> _subject;
    private readonly Func<RequirementDraft, bool> _copy;
    private readonly Func<Fix, Task> _applyFix;
    private readonly CanvasText _text;
    private PreparationChatSession? _session;

    public PreparationChatViewModel(BuilderServices services, Func<PreparationChatSubject?> subject, Func<RequirementDraft, bool> copy, Func<Fix, Task> applyFix, CanvasText text, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(applyFix);
        _services = services;
        _subject = subject;
        _copy = copy;
        _applyFix = applyFix;
        _text = text;
        Consent = new QuotaConsentCardViewModel(text, time);
    }

    /// <summary>The OK of the person for the message they are about to send, with the estimate.</summary>
    public QuotaConsentCardViewModel Consent { get; }

    public ObservableCollection<ChatTurn> Turns { get; } = [];

    public ObservableCollection<ChatRequirementItemViewModel> Requirements { get; } = [];

    public ObservableCollection<ChatTeamItemViewModel> Team { get; } = [];

    public ObservableCollection<string> Risks { get; } = [];

    public ObservableCollection<string> Questions { get; } = [];

    public ObservableCollection<string> Discarded { get; } = [];

    /// <summary>What the person is writing.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    private string _draft = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _status;

    /// <summary>The message waiting for the OK: it is sent, as written then, only when the person says yes.</summary>
    private string? _pending;

    /// <summary>The chat can be used: the service is composed and the project has an orchestrator.</summary>
    public bool IsAvailable => _services.Chat is not null && _subject() is not null;

    public string UnavailableText => _services.Chat is null ? _text.Get("Canvas_Chat_NotComposed") : _text.Get("Canvas_Chat_NoOrchestrator");

    /// <summary>The conversation has begun: it keeps its read-only copy until it ends.</summary>
    public bool HasConversation => _session is not null;

    /// <summary>«Enviar»: opens the card with the estimate; nothing is sent until the person says yes (one OK per message).</summary>
    [RelayCommand(CanExecute = nameof(CanAsk))]
    private async Task AskAsync()
    {
        if (_subject() is not { } subject)
        {
            return;
        }

        _pending = Draft.Trim();
        Func<CancellationToken, Task<UsageEstimate?>>? estimate = null;
        if (_services.Estimator is { } estimator)
        {
            estimate = async ct => await estimator.EstimateAsync(
                new UsageEstimateRequest(subject.Project, subject.Orchestrator.Id, subject.Orchestrator.Model.Provider, subject.Orchestrator.Model.Model, AgentRunPurpose.PreparationChat, Effort: subject.Orchestrator.Effort),
                ct).ConfigureAwait(false);
        }

        await Consent.OpenAsync(_text.Get("Canvas_Chat_ConsentDescription"), estimate).ConfigureAwait(true);
        AskCommand.NotifyCanExecuteChanged();
    }

    private bool CanAsk() => IsAvailable && !IsBusy && !Consent.IsOpen && !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand]
    private void Cancel()
    {
        _pending = null;
        Consent.Close();
        AskCommand.NotifyCanExecuteChanged();
    }

    /// <summary>«Dar mi OK y enviar»: the OK of this message, which the service spends once.</summary>
    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (_services.Chat is not { } chat || _subject() is not { } subject || !Consent.IsOpen || _pending is not { Length: > 0 } message)
        {
            return;
        }

        var consent = Consent.Grant();
        Consent.Close();
        _pending = null;
        Draft = string.Empty;
        IsBusy = true;
        Status = _text.Get("Canvas_Chat_Waiting");
        Turns.Add(new ChatTurn(true, _text.Get("Canvas_Chat_You"), message));
        try
        {
            var reply = await chat.SendAsync(
                new PreparationChatRequest(subject.Project, subject.Roster, subject.Orchestrator.Id, message, _session, consent) { Catalog = subject.Catalog },
                null,
                CancellationToken.None).ConfigureAwait(true);
            Show(reply);
        }
#pragma warning disable CA1031 // The service is behind a port: why it failed is shown in the chat, never thrown into the UI.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Status = _text.Format("Canvas_Chat_Failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(PreparationChatReply reply)
    {
        _session = reply.Session;
        OnPropertyChanged(nameof(HasConversation));
        Status = reply.Error is { Length: > 0 } error ? _text.Format("Canvas_Chat_Failed", error) : null;
        if (reply.Text.Length > 0)
        {
            Turns.Add(new ChatTurn(false, _text.Get("Canvas_Chat_Orchestrator"), reply.Text));
        }

        Requirements.Clear();
        foreach (var draft in reply.Requirements)
        {
            Requirements.Add(new ChatRequirementItemViewModel(draft, _text.Get(draft.Priority == RequirementPriority.Mandatory ? "Canvas_Chat_Mandatory" : "Canvas_Chat_Desirable"), _copy));
        }

        Team.Clear();
        foreach (var suggestion in reply.Team)
        {
            var button = MontageTexts.ButtonOf(suggestion.Fix);
            Team.Add(new ChatTeamItemViewModel(suggestion, _text.Render(button.Code, button.Arguments), _applyFix));
        }

        Replace(Risks, reply.Risks);
        Replace(Questions, reply.Questions);
        Replace(Discarded, reply.Discarded);
    }

    private static void Replace(ObservableCollection<string> target, IReadOnlyList<string> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    /// <summary>Ends the conversation: its read-only copy is deleted and the next message starts another.</summary>
    [RelayCommand]
    private async Task EndAsync()
    {
        if (_services.Chat is { } chat && _session is { } session && _subject() is { } subject)
        {
            try
            {
                await chat.EndAsync(subject.Project, session, CancellationToken.None).ConfigureAwait(true);
            }
#pragma warning disable CA1031 // The copy is cleaned best effort: a failure is said, never thrown into the UI.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                Status = _text.Format("Canvas_Chat_Failed", ex.Message);
                return;
            }
        }

        _session = null;
        Turns.Clear();
        Requirements.Clear();
        Team.Clear();
        Risks.Clear();
        Questions.Clear();
        Discarded.Clear();
        Status = null;
        OnPropertyChanged(nameof(HasConversation));
    }
}

/// <summary>What the chat needs of the document: the project, the template, who answers and the catalog of models for the suggestions.</summary>
public sealed record PreparationChatSubject(ProjectContext Project, IReadOnlyList<AgentDefinition> Roster, AgentDefinition Orchestrator, ModelCatalog? Catalog);
