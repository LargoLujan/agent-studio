using System.Collections.ObjectModel;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Help;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Help;

/// <summary>What the help reads: the corpus and its search (always), and, to ask an agent, the services that need a project and quota (each optional).</summary>
/// <param name="Corpus">The articles of the help.</param>
/// <param name="Search">The local search, free.</param>
/// <param name="Catalog">The closed list of actions the agent may offer.</param>
/// <param name="Agent">The agent of the help; without it «Preguntar a un agente» says it is not available.</param>
/// <param name="Estimator">The estimate shown before the OK; without it the OK says there is none.</param>
/// <param name="Workspace">Opens the project the question is counted in; without it there is no agent.</param>
/// <param name="Redactor">Cleans the question and the card before they are shown and sent.</param>
public sealed record HelpBackend(
    IHelpCorpus Corpus,
    IHelpSearch Search,
    IHelpActionCatalog Catalog,
    IHelpAgent? Agent = null,
    IUsageEstimator? Estimator = null,
    IProjectWorkspace? Workspace = null,
    ISecretRedactor? Redactor = null);

/// <summary>An article in a list: a result of the search, a topic or an article the agent cited. Opens its article.</summary>
public sealed class HelpArticleItemViewModel(string id, string title, string excerpt, Action<string> open)
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    /// <summary>A piece of the article around the words found; empty for a topic of the list.</summary>
    public string Excerpt { get; } = excerpt;

    public bool HasExcerpt => Excerpt.Length > 0;

    public IRelayCommand OpenCommand { get; } = new RelayCommand(() => open(id));

    public override string ToString() => Title;
}

/// <summary>A button of the answer of the agent: an action of the closed list, which opens a place of the app only when the person presses it.</summary>
public sealed class HelpActionItemViewModel(string id, string label, Action<string> run)
{
    public string Id { get; } = id;

    public string Label { get; } = label;

    public IRelayCommand RunCommand { get; } = new RelayCommand(() => run(id));

    public override string ToString() => Label;
}

/// <summary>
/// The «Ayuda» tab (docs/17 D-55 to D-57, ADR-0056): the topics of the help, a search that is local and free, the article chosen, and
/// below them «Preguntar a un agente», which spends quota only with the OK of the person for each question, with the card of what is
/// sent in front of them. <c>F1</c> and the «Más ayuda» of each ⓘ open it on the article of the screen or of the section. What the
/// agent answers is shown framed as text of an agent, with buttons for the actions of the closed list.
/// </summary>
public sealed partial class HelpDocumentViewModel : DocumentViewModel
{
    public const string DocumentId = "document:help";

    private readonly HelpBackend _backend;
    private readonly CanvasText _text;
    private readonly TimeProvider _time;
    private readonly ISecretRedactor _redactor;
    private int _searchGeneration;
    private string? _projectRoot;
    private string? _projectName;
    private HelpScreenFacts? _facts;
    private HelpContextCard? _pendingCard;
    private string? _pendingQuestion;
    private HelpAgentChoice? _pendingChoice;
    private ProjectContext? _pendingProject;

    /// <param name="redactor">Cleans what is shown and sent; the one of the backend by default, or the pattern-based one.</param>
    public HelpDocumentViewModel(HelpBackend backend, TimeProvider? time = null, IUiTextProvider? texts = null)
        : base(DocumentId, (texts ?? Texts.Current).Get("Help_Title"))
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _text = new CanvasText(texts);
        _time = time ?? TimeProvider.System;
        _redactor = backend.Redactor ?? new AgentStudio.Security.Redaction.SecretRedactor();
        Consent = new QuotaConsentCardViewModel(_text, _time);
        foreach (var article in backend.Corpus.Articles(Language))
        {
            Topics.Add(new HelpArticleItemViewModel(article.Id, article.Title, string.Empty, id => OpenArticle(id)));
        }

        ShowHome();
    }

    /// <summary>The language the help is read and asked in: the one of the interface.</summary>
    public string Language => _text.Provider.Culture.Name;

    /// <summary>The articles of the help, by name: where the person starts when they do not know what to search for.</summary>
    public ObservableCollection<HelpArticleItemViewModel> Topics { get; } = [];

    /// <summary>What the search found, best first.</summary>
    public ObservableCollection<HelpArticleItemViewModel> Results { get; } = [];

    /// <summary>The OK of the person for the question they are about to ask, with the estimate in front of them.</summary>
    public QuotaConsentCardViewModel Consent { get; }

    /// <summary>What the card says will be sent, as lines the person reads (the screen, its state, the last notice, the project and the question).</summary>
    public ObservableCollection<string> CardLines { get; } = [];

    public ObservableCollection<HelpArticleItemViewModel> ReplyArticles { get; } = [];

    public ObservableCollection<HelpActionItemViewModel> ReplyActions { get; } = [];

    public ObservableCollection<string> ReplyDiscarded { get; } = [];

    /// <summary>What the person types to search.</summary>
    [ObservableProperty]
    private string _query = string.Empty;

    /// <summary>The search was made and found nothing.</summary>
    [ObservableProperty]
    private string? _searchNotice;

    /// <summary>The id of the article shown; null when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArticle))]
    private string? _articleId;

    [ObservableProperty]
    private string _articleTitle = string.Empty;

    /// <summary>The Markdown of the article shown.</summary>
    [ObservableProperty]
    private string _articleMarkdown = string.Empty;

    /// <summary>What to say when no article is shown (a screen without one, nothing chosen yet).</summary>
    [ObservableProperty]
    private string? _articleNotice;

    public bool HasArticle => ArticleId is not null;

    /// <summary>What the person is asking the agent.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    [NotifyPropertyChangedFor(nameof(QuestionNotice))]
    private string _question = string.Empty;

    /// <summary>«Incluir datos del proyecto» (D-57): without the mark, the name of the project and what names a path of the machine stay out of the card.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    [NotifyPropertyChangedFor(nameof(QuestionNotice))]
    private bool _includeProjectData;

    /// <summary>The person chose Codex: its read-only mode reads the whole disk, and the panel says so before the OK.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodexWarning))]
    private bool _useCodex;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _status;

    /// <summary>The answer of the agent, as text of an agent; empty until there is one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReply))]
    private string _replyText = string.Empty;

    public bool HasReply => ReplyText.Length > 0;

    /// <summary>Whether the agent of the help is composed (the services it needs are).</summary>
    public bool AgentComposed => _backend.Agent is not null && _backend.Workspace is not null;

    /// <summary>Whether a question can be asked now: the agent is composed and a project is open (without one there is no log to count it in).</summary>
    public bool AgentAvailable => AgentComposed && _projectRoot is not null;

    /// <summary>Why the agent cannot be asked, or null when it can.</summary>
    public string? AgentUnavailableText => !AgentComposed ? _text.Get("Help_Ask_NotComposed") : _projectRoot is null ? _text.Get("Help_Ask_NoProject") : null;

    /// <summary>Said when the question names a path of the machine without the mark: what would travel is not what the person agreed to send.</summary>
    public string? QuestionNotice => !IncludeProjectData && LocalPaths.Contains(Question) ? _text.Get("Help_Ask_PathInQuestion") : null;

    public string? CodexWarning => UseCodex ? _text.Get("Help_Ask_CodexWarning") : null;

    /// <summary>What the actions of the answer do: opens a place of the app; set by the shell. Never run for an id that is not in the closed list.</summary>
    public Func<string, bool>? Navigate { get; set; }

    /// <summary>Whether an article exists for the ⓘ with this key, in the language in use: «Más ayuda» is shown only then.</summary>
    public bool HasInfoArticle(string? infoKey) =>
        !string.IsNullOrWhiteSpace(infoKey) && _backend.Corpus.ForInfoKey(infoKey, Language) is not null;

    /// <summary>Shows the article of the ⓘ with this key; false when there is none.</summary>
    public bool OpenInfoKey(string? infoKey)
    {
        if (string.IsNullOrWhiteSpace(infoKey) || _backend.Corpus.ForInfoKey(infoKey, Language) is not { } article)
        {
            return false;
        }

        ShowArticle(article);
        return true;
    }

    /// <summary>
    /// <c>F1</c> and the «Ayuda» of the bar (D-55): the article of the screen the person was in. A screen without one shows the topics
    /// with a note. The facts are what the card will say of that screen, taken now, before this tab takes the focus.
    /// </summary>
    /// <param name="screen">The code of the screen (<see cref="HelpScreens"/>), or null when it has no help.</param>
    /// <param name="content">What the dock showed: the screen reads its state for the card.</param>
    /// <param name="notice">The last notice the person could see.</param>
    /// <param name="projectRoot">The folder of the project that is open, or null.</param>
    public void ShowFor(string? screen, object? content, string? notice, string? projectRoot)
    {
        SetProject(projectRoot);
        _facts = screen is null ? null : HelpScreens.FactsOf(screen, content, notice, _projectName, _text.Provider);
        if (screen is not null && _backend.Corpus.ForScreen(screen, Language) is { } article)
        {
            ShowArticle(article);
            return;
        }

        ShowHome();
        if (screen is not null)
        {
            ArticleNotice = _text.Get("Help_Article_NoScreen");
        }
    }

    /// <summary>Shows the article with this id; false when there is none.</summary>
    public bool OpenArticle(string id)
    {
        if (_backend.Corpus.Find(id, Language) is not { } article)
        {
            return false;
        }

        ShowArticle(article);
        return true;
    }

    private void ShowArticle(HelpArticle article)
    {
        ArticleId = article.Id;
        ArticleTitle = article.Title;
        ArticleMarkdown = article.Markdown;
        ArticleNotice = null;
    }

    private void ShowHome()
    {
        ArticleId = null;
        ArticleTitle = string.Empty;
        ArticleMarkdown = string.Empty;
        ArticleNotice = _text.Get("Help_Article_None");
    }

    private void SetProject(string? projectRoot)
    {
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : projectRoot;
        _projectName = _projectRoot is null ? null : System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(_projectRoot));
        OnPropertyChanged(nameof(AgentAvailable));
        OnPropertyChanged(nameof(AgentUnavailableText));
        AskCommand.NotifyCanExecuteChanged();
    }

    // ---- the search: local, free, as the person types ----

    partial void OnQueryChanged(string value) => _ = SearchAsync(value);

    private async Task SearchAsync(string query)
    {
        var generation = ++_searchGeneration;
        if (string.IsNullOrWhiteSpace(query) || query.Length > HelpLimits.MaxQueryLength)
        {
            Results.Clear();
            SearchNotice = null;
            return;
        }

        IReadOnlyList<HelpSearchHit> hits;
        try
        {
            hits = await _backend.Search.SearchAsync(query.Trim(), Language, HelpLimits.MaxSearchResults, CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A search that fails shows no result, never an error thrown into the UI.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            hits = [];
        }

        if (generation != _searchGeneration)
        {
            return;
        }

        Results.Clear();
        foreach (var hit in hits)
        {
            Results.Add(new HelpArticleItemViewModel(hit.ArticleId, hit.Title, hit.Excerpt, id => OpenArticle(id)));
        }

        SearchNotice = hits.Count == 0 ? _text.Format("Help_Search_NoResults", query.Trim()) : null;
    }

    // ---- «Preguntar a un agente»: the card first, then the OK, then the question ----

    private HelpAgentChoice Choice => UseCodex ? new HelpAgentChoice(ProviderId.CodexCli, null) : HelpAgentChoice.Default;

    private bool CanAsk() => AgentAvailable && !IsBusy && !Consent.IsOpen && !string.IsNullOrWhiteSpace(Question) && QuestionNotice is null && Question.Length <= HelpLimits.MaxQuestionLength;

    /// <summary>«Preguntar a un agente»: shows the card with what will be sent and the estimate; nothing is sent until the person says yes.</summary>
    [RelayCommand(CanExecute = nameof(CanAsk))]
    private async Task AskAsync()
    {
        if (_backend.Workspace is not { } workspace || _projectRoot is not { } root)
        {
            return;
        }

        try
        {
            _pendingProject = await workspace.OpenAsync(root, CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The project store is behind a port: why it failed is shown, never thrown into the UI.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Status = _text.Format("Help_Failed", ex.Message);
            return;
        }

        var facts = _facts ?? HelpScreens.FactsOf(HelpScreens.Canvas, null, null, _projectName, _text.Provider);
        _pendingQuestion = HelpAskRules.CleanQuestion(Question, _redactor);
        _pendingChoice = Choice;
        try
        {
            _pendingCard = HelpContextCard.Build(facts with { ProjectName = _projectName }, IncludeProjectData, _redactor);
        }
        catch (ArgumentException ex)
        {
            Status = _text.Format("Help_Failed", ex.Message);
            return;
        }

        ShowCard(_pendingCard, _pendingQuestion, _pendingChoice);
        var project = _pendingProject;
        var choice = _pendingChoice;
        Func<CancellationToken, Task<UsageEstimate?>>? estimate = null;
        if (_backend.Estimator is { } estimator)
        {
            estimate = async ct => await estimator.EstimateAsync(
                new UsageEstimateRequest(project, HelpAgent.Id, choice.Provider, choice.Model, AgentRunPurpose.Help),
                ct).ConfigureAwait(false);
        }

        await Consent.OpenAsync(_text.Get("Help_Ask_ConsentDescription"), estimate).ConfigureAwait(true);
        AskCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The lines of the card, exactly as they travel (the texts are already redacted), and what was left out for lack of the mark.</summary>
    private void ShowCard(HelpContextCard card, string question, HelpAgentChoice choice)
    {
        CardLines.Clear();
        foreach (var part in card.Parts)
        {
            CardLines.Add(_text.Format("Help_Card_Kind_" + part.Kind, part.Text));
        }

        CardLines.Add(_text.Format("Help_Card_Question", question));
        CardLines.Add(_text.Format("Help_Card_Agent", _text.Get(choice.IsDefault ? "Help_Agent_Claude" : "Help_Agent_Codex")));
        if (card.Withheld.Count > 0)
        {
            CardLines.Add(_text.Format("Help_Card_Withheld", string.Join(", ", card.Withheld.Select(k => _text.Get("Help_Card_Withheld_" + k)))));
        }

        CardLines.Add(_text.Get("Help_Card_Never"));
    }

    [RelayCommand]
    private void Cancel()
    {
        _pendingCard = null;
        _pendingQuestion = null;
        Consent.Close();
        AskCommand.NotifyCanExecuteChanged();
    }

    /// <summary>«Dar mi OK y enviar»: the OK of this question, which the service spends once. The question goes as the card showed it.</summary>
    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (_backend.Agent is not { } agent || _pendingCard is not { } card || _pendingProject is not { } project
            || _pendingQuestion is not { Length: > 0 } question || _pendingChoice is not { } choice || !Consent.IsOpen)
        {
            return;
        }

        var consent = Consent.Grant();
        Consent.Close();
        _pendingCard = null;
        _pendingQuestion = null;
        IsBusy = true;
        Status = _text.Get("Help_Waiting");
        ClearReply();
        try
        {
            var reply = await agent.AskAsync(new HelpAskRequest(project, question, Language, card, choice, consent), null, CancellationToken.None).ConfigureAwait(true);
            Show(reply);
        }
#pragma warning disable CA1031 // The service is behind a port: why it failed is shown, never thrown into the UI.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Status = _text.Format("Help_Failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearReply()
    {
        ReplyText = string.Empty;
        ReplyArticles.Clear();
        ReplyActions.Clear();
        ReplyDiscarded.Clear();
    }

    private void Show(HelpAskReply reply)
    {
        Status = reply.Error is { Length: > 0 } error ? _text.Format("Help_Failed", error) : null;
        ReplyText = reply.Text;
        foreach (var id in reply.Articles)
        {
            if (_backend.Corpus.Find(id, Language) is { } article)
            {
                ReplyArticles.Add(new HelpArticleItemViewModel(article.Id, article.Title, string.Empty, i => OpenArticle(i)));
            }
        }

        foreach (var id in reply.Actions)
        {
            if (_backend.Catalog.Actions.FirstOrDefault(a => a.Id == id) is { } action)
            {
                ReplyActions.Add(new HelpActionItemViewModel(action.Id, _text.Provider.Render(action.Label), RunAction));
            }
        }

        foreach (var note in reply.Discarded)
        {
            ReplyDiscarded.Add(note);
        }
    }

    /// <summary>Runs an action of the answer when the person presses its button, and only if it is in the closed list.</summary>
    private void RunAction(string id)
    {
        if (_backend.Catalog.Actions.Any(a => a.Id == id))
        {
            Navigate?.Invoke(id);
        }
    }
}
