using System.Collections.ObjectModel;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Work;

/// <summary>
/// A card of the board as the screen draws it. Its column is the one <c>BoardProjector</c> gave: the screen never moves a card, and a
/// card that is in review says why when its column alone does not (a challenged verdict, a verdict that leaves the decision to the person).
/// </summary>
public sealed class BoardCardViewModel
{
    private readonly Action<TraceRef> _viewChain;

    internal BoardCardViewModel(BoardCard card, Action<TraceRef> viewChain)
    {
        Card = card;
        _viewChain = viewChain;
        ViewChainCommand = new RelayCommand(ViewChain);
    }

    public BoardCard Card { get; }

    public string Title => Card.Title;

    public string KindText => WorkTexts.CardKind(Card.Kind);

    /// <summary>Why the card is where it is; null when its column says it all.</summary>
    public string? ReasonText => WorkTexts.Reason(Card.Reason);

    public bool HasReason => ReasonText is not null;

    public string? RunText => Card.Run is { } run ? Texts.Format("Work_Board_Run", run) : null;

    public bool HasRun => Card.Run is not null;

    /// <summary>The node of the chain this card starts: the requirement, or the task of its run.</summary>
    public TraceRef? Node => Card.Kind == BoardCardKind.Task
        ? (Card.Run is { } run ? TraceRef.Task(run, Card.Id) : null)
        : TraceRef.Requirement(Card.Id);

    public IRelayCommand ViewChainCommand { get; }

    private void ViewChain()
    {
        if (Node is { } node)
        {
            _viewChain(node);
        }
    }

    /// <summary>What a screen reader says of the card (A1-bis): its kind and title.</summary>
    public override string ToString() => $"{KindText}: {Title}";
}

/// <summary>A column of the board with the cards the verified log puts in it.</summary>
public sealed class BoardColumnViewModel
{
    internal BoardColumnViewModel(BoardColumn column, IReadOnlyList<BoardCardViewModel> cards)
    {
        Column = column;
        Cards = new ObservableCollection<BoardCardViewModel>(cards);
    }

    public BoardColumn Column { get; }

    public string Title => WorkTexts.Column(Column);

    public ObservableCollection<BoardCardViewModel> Cards { get; }

    public string CountText => Texts.Format("Work_Board_Count", Cards.Count);

    public override string ToString() => Title;
}

/// <summary>
/// «Tablero» (docs/16 D-19, F9-2): the seven columns of the selected milestone, drawn from what <c>BoardProjector</c> read in the verified
/// log. No card is dragged and none reaches «Hecho» or «Integrado» without a valid, approved verdict: that is the rule of the domain
/// (<c>BoardRules</c>), and the screen only groups by the column it is given. The rules the person has not decided yet stay as the code has
/// them, and the screen says what the code does.
/// </summary>
public sealed class BoardSection : WorkSection
{
    public BoardSection(WorkContext context)
        : base(context, "Info_Work_Board")
    {
        Context.MilestoneSelected += (_, _) => _ = ReloadAsync();
        Columns = [.. Enum.GetValues<BoardColumn>().Select(c => new BoardColumnViewModel(c, []))];
    }

    private IReadOnlyList<BoardColumnViewModel> _columns = [];

    public IReadOnlyList<BoardColumnViewModel> Columns
    {
        get => _columns;
        private set
        {
            _columns = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCards));
        }
    }

    public bool HasCards => Columns.Any(c => c.Cards.Count > 0);

    /// <summary>The items of the backlog in force: the cards of the «Backlog» column of the project, not yet in a milestone's board.</summary>
    public ObservableCollection<BoardCardViewModel> ProjectBacklog { get; } = [];

    public bool HasProjectBacklog => ProjectBacklog.Count > 0;

    public string MilestoneText => Context.SelectedMilestone is { } id ? Texts.Format("Work_Board_Milestone", id.Value) : Texts.Get("Work_Board_NoMilestone");

    public string? ReadText { get; private set; }

    /// <summary>What the code decides today, for the screen to say (docs/16 D-19): the four rules still open are shown as they are.</summary>
    public IReadOnlyList<string> Rules { get; } =
    [
        Texts.Get("Work_Rule_NeedsVerdict"),
        Texts.Get("Work_Rule_IntegratedNeedsDone"),
        Texts.Get("Work_Rule_ApprovedNotMet"),
        Texts.Get("Work_Rule_CloseNeedsRun"),
        Texts.Get("Work_Rule_TestsAndDocs"),
    ];

    public override async Task ReloadAsync()
    {
        var read = BeginRead();
        OnPropertyChanged(nameof(MilestoneText));
        Notice = Context.Backend.Board is null ? Texts.Get("Work_Notice_NoBoard") : null;
        var columns = Enum.GetValues<BoardColumn>().Select(c => new BoardColumnViewModel(c, [])).ToList();
        var backlog = new List<BoardCardViewModel>();
        string? readText = null;
        if (Context.Backend.Board is { } board && Context.Project is { } project)
        {
            await GuardAsync(async () =>
            {
                foreach (var card in await board.BacklogAsync(project, CancellationToken.None).ConfigureAwait(true))
                {
                    backlog.Add(new BoardCardViewModel(card, Context.LookAt));
                }

                if (Context.SelectedMilestone is { } milestone)
                {
                    var projected = await board.ProjectAsync(project, milestone, CancellationToken.None).ConfigureAwait(true);
                    var cards = projected.Requirements.Concat(projected.Tasks).Select(c => new BoardCardViewModel(c, Context.LookAt)).ToList();
                    columns = [.. Enum.GetValues<BoardColumn>().Select(c => new BoardColumnViewModel(c, [.. cards.Where(card => card.Card.Column == c)]))];
                    readText = Texts.Format("Work_Board_Read", projected.LogEntries);
                }
            }).ConfigureAwait(true);
        }

        if (!IsLatest(read))
        {
            return;
        }

        ProjectBacklog.Clear();
        foreach (var card in backlog)
        {
            ProjectBacklog.Add(card);
        }

        ReadText = readText;
        OnPropertyChanged(nameof(ReadText));
        Columns = columns;
        OnPropertyChanged(nameof(HasProjectBacklog));
    }

    protected override void ProjectChanged()
    {
        Columns = [.. Enum.GetValues<BoardColumn>().Select(c => new BoardColumnViewModel(c, []))];
        ProjectBacklog.Clear();
        OnPropertyChanged(nameof(HasProjectBacklog));
        OnPropertyChanged(nameof(MilestoneText));
    }
}
