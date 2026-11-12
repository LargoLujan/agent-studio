using System.Collections.ObjectModel;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Tracing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Work;

/// <summary>A verdict, opened from the chain: what it decided, who judged, and what the judge wrote (the judge's text, shown as data).</summary>
public sealed record VerdictGlance(string Title, string Decision, string Judge, string Validity, string Summary);

/// <summary>
/// One node of the chain (docs/16 F9-4): its stage, its title and what can be done with it. A node opens what the app already has:
/// a saved proof (a diff, a test run, a report) is read from the proofs, and a verdict from the verdicts. A node the app has no way to
/// open (a commit, a task, an attempt, a delivery) is shown without a link, and can always be made the centre of the chain.
/// </summary>
public sealed class ChainStepViewModel
{
    internal ChainStepViewModel(TraceChainStep step, bool canOpen, Action<TraceChainStep> open, Action<TraceRef> center)
    {
        Step = step;
        CanOpen = canOpen;
        OpenCommand = new RelayCommand(() => open(step), () => CanOpen);
        CenterCommand = new RelayCommand(() => center(step.Node));
    }

    public TraceChainStep Step { get; }

    public string StageText => WorkTexts.Stage(Step.Stage);

    public string Title => string.IsNullOrWhiteSpace(Step.Title) ? WorkTexts.Short(Step.Node.Id) : Step.Title;

    public string NodeText => Texts.Format("Work_Chain_Node", WorkTexts.Node(Step.Node.Kind), WorkTexts.Short(Step.Node.Id));

    /// <summary>A step nobody confirmed (inferred): shown as such. The reader of the log gives none today, and the screen never hides one.</summary>
    public string? SuggestedText => Step.Suggested ? Texts.Get("Work_Chain_Suggested") : null;

    public bool IsSuggested => Step.Suggested;

    /// <summary>The app has a way to open this node (a saved proof or a verdict).</summary>
    public bool CanOpen { get; }

    public bool HasNoLink => !CanOpen;

    public IRelayCommand OpenCommand { get; }

    public IRelayCommand CenterCommand { get; }

    public override string ToString() => $"{StageText}: {Title}";
}

/// <summary>
/// «Cadena» (docs/16 D-31, F9-4): from any card or node, the path requirement, milestone, task, attempt, change, verification, verdict and
/// delivery, read from the verified log in both directions. Nothing inferred enters it; each node opens what the app already shows.
/// </summary>
public sealed partial class ChainSection : WorkSection
{
    public ChainSection(WorkContext context)
        : base(context, "Info_Work_Chain")
    {
        Context.FocusChanged += (_, _) => _ = ReloadAsync();
    }

    public ObservableCollection<ChainStepViewModel> Steps { get; } = [];

    public bool HasSteps => Steps.Count > 0;

    public string StartText => Context.Focus is { } focus ? Texts.Format("Work_Chain_Start", WorkTexts.Node(focus.Kind), WorkTexts.Short(focus.Id)) : Texts.Get("Work_Chain_NoStart");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenedProof))]
    private EvidenceViewer? _openedProof;

    public bool HasOpenedProof => OpenedProof is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenedVerdict))]
    private VerdictGlance? _openedVerdict;

    public bool HasOpenedVerdict => OpenedVerdict is not null;

    public override async Task ReloadAsync()
    {
        var generation = BeginRead();
        Notice = Context.Backend.Chain is null ? Texts.Get("Work_Notice_NoChain") : null;
        OnPropertyChanged(nameof(StartText));
        var steps = new List<ChainStepViewModel>();
        if (Context.Backend.Chain is { } chain && Context.Project is { } project && Context.Focus is { } focus)
        {
            await GuardAsync(async () =>
            {
                var read = await chain.ChainAsync(project, focus, CancellationToken.None).ConfigureAwait(true);
                steps.AddRange(read.Steps.Select(step => new ChainStepViewModel(step, CanOpenNode(step.Node), s => _ = OpenAsync(s), Context.LookAt)));
            }).ConfigureAwait(true);
        }

        if (!IsLatest(generation))
        {
            return;
        }

        OpenedProof = null;
        OpenedVerdict = null;
        Steps.Clear();
        foreach (var step in steps)
        {
            Steps.Add(step);
        }

        OnPropertyChanged(nameof(HasSteps));
    }

    protected override void ProjectChanged()
    {
        Steps.Clear();
        OpenedProof = null;
        OpenedVerdict = null;
        OnPropertyChanged(nameof(HasSteps));
        OnPropertyChanged(nameof(StartText));
    }

    private bool CanOpenNode(TraceRef node) =>
        (node.Kind == TraceNodeKind.Evidence && Context.Backend.Evidence is not null)
        || (node.Kind == TraceNodeKind.Verdict && Context.Backend.Verdicts is not null);

    private async Task OpenAsync(TraceChainStep step)
    {
        if (Context.Project is not { } project)
        {
            return;
        }

        OpenedProof = null;
        OpenedVerdict = null;
        await GuardAsync(async () =>
        {
            if (step.Node.Kind == TraceNodeKind.Evidence && Context.Backend.Evidence is { } evidence)
            {
                OpenedProof = await SavedProofReader.ReadAsync(evidence, project, step.Node.Id, CancellationToken.None).ConfigureAwait(true);
            }
            else if (step.Node.Kind == TraceNodeKind.Verdict && Context.Backend.Verdicts is { } verdicts)
            {
                OpenedVerdict = await ReadVerdictAsync(verdicts, project, step.Node.Id).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    private static async Task<VerdictGlance> ReadVerdictAsync(AgentStudio.Application.Persistence.IVerdictStore store, AgentStudio.Application.Projects.ProjectContext project, string id)
    {
        var stored = (await store.ListAsync(project.ProjectId, null, CancellationToken.None).ConfigureAwait(true)).FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.Ordinal));
        if (stored is null)
        {
            return new VerdictGlance(Texts.Format("Work_Verdict_Title", WorkTexts.Short(id)), Texts.Get("Work_Verdict_Missing"), string.Empty, string.Empty, string.Empty);
        }

        var judge = stored.Verdict.Judge;
        return new VerdictGlance(
            Texts.Format("Work_Verdict_Title", WorkTexts.Short(id)),
            VerdictTexts.Decision(stored.Verdict.Decision),
            Texts.Format("Work_Verdict_Judge", judge.Agent, DebateTexts.Vendor(judge.Model.Vendor)),
            Texts.Get(stored.Valid ? "Work_Verdict_Valid" : "Work_Verdict_Invalid"),
            stored.Verdict.Summary);
    }

    [RelayCommand]
    private void CloseProof() => OpenedProof = null;

    [RelayCommand]
    private void CloseVerdict() => OpenedVerdict = null;
}
