using System.Collections.ObjectModel;
using AgentStudio.Application.Analysis;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Questions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis.Rules;
using ObservabilityConsumptionTexts = AgentStudio.Observability.Analysis.ConsumptionTexts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Observability.Analysis;

/// <summary>
/// One finding of the deterministic analysis as the screen says it (docs/17 D-34): its sentence, from the resources by its code
/// (<c>consumption.</c> and the code), what it suggests, and what it cost with its origin. Never a saving made up: only what was spent.
/// </summary>
/// <param name="Code">The code of the finding (<see cref="ConsumptionFindingCodes"/>).</param>
/// <param name="Sentence">What was found, in the language of the interface.</param>
/// <param name="Suggestion">What it suggests; null for a finding that only informs (<c>work.discarded</c>). It is a suggestion: nothing is applied by itself.</param>
/// <param name="Cost">«Costó ~42.000 tokens · exacto · informado por el CLI», or why the cost is not known.</param>
/// <param name="About">What it is about: «el agente exec1», «el intento T1#2», «la ejecución».</param>
public sealed record ConsumptionFindingRow(string Code, string Sentence, string? Suggestion, string Cost, string About)
{
    /// <summary>What a screen reader says of a finding: what it is about, the sentence, what it cost and what it suggests.</summary>
    public string AccessibleName => string.Join(" ", new[] { About + ".", Sentence, Cost + ".", Suggestion }.Where(p => !string.IsNullOrWhiteSpace(p)));

    public override string ToString() => AccessibleName;
}

/// <summary>
/// One suggestion of the agent that analysed the consumption (docs/17 D-35): its text is data of the agent, shown framed as said by
/// the agent; its typed fix, when it has one, is only described; and the saving it claims is «lo dice el agente», never measured.
/// </summary>
/// <param name="Frame">«Lo dice el agente (es un dato, no una orden)».</param>
/// <param name="Text">What the agent wrote, plain text.</param>
/// <param name="About">What it is about.</param>
/// <param name="Fix">The fix the agent proposes, described as the button of the analyzer would say it; null when it proposes none. Never applied from here.</param>
/// <param name="Saving">«El agente estima un ahorro de ~12.000 tokens (lo dice el agente; no está medido)»; null when it claims none.</param>
public sealed record AssistedSuggestionRow(string Frame, string Text, string About, string? Fix, string? Saving)
{
    public string AccessibleName => string.Join(" ", new[] { Frame + ".", About + ".", Text, Fix, Saving }.Where(p => !string.IsNullOrWhiteSpace(p)));

    public override string ToString() => AccessibleName;
}

/// <summary>
/// The analysis of the consumption of a run on screen (docs/17 D-34 to D-36, F13-15). Two analyses, never applied by themselves:
/// <list type="bullet">
/// <item><b>The deterministic one</b> is free and instant: it runs when each run ends and when the person asks, over the verified log
/// (<see cref="IConsumptionAnalyzer"/>). Its sentences come from the resources (<see cref="ObservabilityConsumptionTexts"/>
/// is in <c>AgentStudio.Observability</c>: the code and the arguments), each with what it cost and where the figure comes from.</item>
/// <item><b>The one with an agent</b> (<see cref="IAssistedConsumptionAnalysis"/>) is optional and spends quota: the person gives
/// an OK each time, with the estimate in front, and the agent is one of the vendor that did not produce the work, which the service
/// chooses. Nothing is launched without that OK. The suggestions of the agent are framed as its own.</item>
/// </list>
/// A suggestion, of either, is a text: applying a fix goes through the usual gates of the canvas, never from here.
/// </summary>
public sealed partial class ConsumptionAnalysisViewModel : ObservableObject
{
    /// <summary>Who gives the OK of quota: the person (the log says <c>user</c>).</summary>
    public const string ConsentedBy = "user";

    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _ui;
    private readonly IUiTextProvider _texts;
    private ProjectContext? _project;
    private RunId? _run;
    private ConsumptionReport? _report;
    private int _generation;
    private CancellationTokenSource? _estimating;

    public ConsumptionAnalysisViewModel(TimeProvider time, SynchronizationContext? uiContext = null, IUiTextProvider? texts = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _ui = uiContext ?? SynchronizationContext.Current;
        _texts = texts ?? Texts.Current;
    }

    /// <summary>The deterministic analysis; without it the section is not shown.</summary>
    public IConsumptionAnalyzer? Analyzer { get; set; }

    /// <summary>The analysis with an agent; without it the button is not offered.</summary>
    public IAssistedConsumptionAnalysis? Assisted { get; set; }

    /// <summary>
    /// What the analysis with an agent would spend, for the person to see before the OK (docs/17 D-35): the estimator of the system
    /// for the purpose <c>ConsumptionAnalysis</c>. Without it, or when it knows too little, the OK says the cost is unknown.
    /// </summary>
    public Func<ProjectContext, CancellationToken, Task<UsageEstimate>>? EstimateAssisted { get; set; }

    /// <summary>The findings of the deterministic analysis, in the stable order the analyzer gives.</summary>
    public ObservableCollection<ConsumptionFindingRow> Findings { get; } = [];

    /// <summary>The suggestions of the agent, framed as its own.</summary>
    public ObservableCollection<AssistedSuggestionRow> Suggestions { get; } = [];

    /// <summary>The section has something to analyse: the analyzer is composed and a run with its project is on screen.</summary>
    public bool IsAvailable => Analyzer is not null && _project is not null && _run is not null;

    /// <summary>The button of the analysis with an agent is offered.</summary>
    public bool CanAskAgent => IsAvailable && Assisted is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(AskAgentCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmAgentCommand))]
    private bool _isAnalyzing;

    /// <summary>The deterministic analysis was made: the section says what it found, or that it found nothing.</summary>
    [ObservableProperty]
    private bool _hasAnalysis;

    /// <summary>«Se encontraron 3 cosas que revisar», or «No se encontró nada»; null before the analysis.</summary>
    [ObservableProperty]
    private string? _summary;

    /// <summary>Why an analysis did not run, in words; null while there is nothing to say. It is announced.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>The OK of quota is being asked: the explanation and the estimate are on screen, and nothing is launched until the person says yes.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmAgentCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelAgentCommand))]
    private bool _isAskingConsent;

    /// <summary>What is said before the OK: what the agent does, that it spends quota, and the estimate.</summary>
    [ObservableProperty]
    private string? _consentText;

    /// <summary>The analysis with an agent is running (after the OK).</summary>
    [ObservableProperty]
    private bool _isAgentRunning;

    /// <summary>The analysis with an agent finished: its suggestions, or that it had none.</summary>
    [ObservableProperty]
    private string? _agentSummary;

    /// <summary>The framing sentence of the whole list of suggestions of the agent.</summary>
    public string NeverApplied => _texts.Get("Monitor_Consumption_NeverApplied");

    /// <summary>Completes when the last analysis asked has finished and been applied; for the tests and for whoever waits.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>A run is on screen: its analysis can be made. A previous one is forgotten.</summary>
    public void Bind(ProjectContext? project, RunId? run)
    {
        Forget();
        _project = project;
        _run = run;
        NotifyAvailability();
    }

    /// <summary>The run on screen went: what it showed goes too.</summary>
    public void Forget()
    {
        _generation++;
        _estimating?.Cancel();
        _project = null;
        _run = null;
        _report = null;
        Findings.Clear();
        Suggestions.Clear();
        HasAnalysis = false;
        Summary = null;
        ErrorMessage = null;
        IsAskingConsent = false;
        ConsentText = null;
        IsAgentRunning = false;
        AgentSummary = null;
        IsAnalyzing = false;
        Completion = Task.CompletedTask;
        NotifyAvailability();
    }

    private void NotifyAvailability()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(CanAskAgent));
        AnalyzeCommand.NotifyCanExecuteChanged();
        AskAgentCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The deterministic analysis, when a run ends and on demand (free, instant, over the verified log).</summary>
    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private Task AnalyzeAsync()
    {
        Completion = RunDeterministicAsync();
        return Completion;
    }

    private bool CanAnalyze() => IsAvailable && !IsAnalyzing;

    /// <summary>Analyses the run on screen; what it finds replaces what the section showed. It spends no quota.</summary>
    public Task RunDeterministicAsync()
    {
        if (Analyzer is not { } analyzer || _project is not { } project || _run is not { } run)
        {
            return Task.CompletedTask;
        }

        var generation = ++_generation;
        IsAnalyzing = true;
        ErrorMessage = null;
        return Core();

        async Task Core()
        {
            ConsumptionReport? report = null;
            string? failure = null;
            try
            {
                report = await analyzer.AnalyzeAsync(project, run, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The analyzer is behind a port: a log that does not verify is said in words, never thrown on a background task.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                failure = ex.Message;
            }

            Post(generation, () =>
            {
                IsAnalyzing = false;
                if (report is null)
                {
                    ErrorMessage = _texts.Format("Monitor_Consumption_Failed", failure ?? string.Empty);
                    return;
                }

                Apply(report);
            });
        }
    }

    private void Apply(ConsumptionReport report)
    {
        _report = report;
        Findings.Clear();
        foreach (var finding in report.Findings)
        {
            Findings.Add(RowOf(finding));
        }

        HasAnalysis = true;
        Summary = report.Findings.Count == 0
            ? _texts.Get("Monitor_Consumption_NoFindings")
            : _texts.Format("Monitor_Consumption_Found", report.Findings.Count.ToString(_texts.Culture));
        AskAgentCommand.NotifyCanExecuteChanged();
    }

    private ConsumptionFindingRow RowOf(ConsumptionFinding finding)
    {
        var sentence = ObservabilityConsumptionTexts.Sentence(finding);
        var suggestion = ObservabilityConsumptionTexts.Suggestion(finding);
        return new ConsumptionFindingRow(
            finding.Code,
            _texts.Render(sentence),
            suggestion is null ? null : _texts.Format("Monitor_Consumption_Suggestion", _texts.Render(suggestion)),
            CostText(finding),
            TargetText(finding.Target));
    }

    /// <summary>What a finding cost with its origin («Costó 42.000 tokens · exacto · informado por el CLI»), or why it is not known: never a made-up figure.</summary>
    private string CostText(ConsumptionFinding finding)
    {
        var tokens = MetricText.Tokens(finding.Tokens);
        return finding.Tokens.Value is null
            ? _texts.Format("Monitor_Consumption_CostUnknown", tokens.Origin)
            : _texts.Format("Monitor_Consumption_Cost", (finding.Tokens.Provenance == MetricProvenance.Estimated ? "~" : string.Empty) + finding.Tokens.Value.Value.ToString("N0", _texts.Culture), tokens.Origin);
    }

    private string TargetText(ConsumptionTarget target) => target.Kind switch
    {
        ConsumptionTargetKind.Run => _texts.Get("Monitor_Consumption_TargetRun"),
        ConsumptionTargetKind.Agent => _texts.Format("Monitor_Consumption_TargetAgent", target.Id ?? string.Empty),
        ConsumptionTargetKind.Task => _texts.Format("Monitor_Consumption_TargetTask", target.Id ?? string.Empty),
        ConsumptionTargetKind.Attempt => _texts.Format("Monitor_Consumption_TargetAttempt", target.Id ?? string.Empty),
        _ => _texts.Format("Monitor_Consumption_TargetSetting", target.Id ?? string.Empty),
    };

    // ---- the analysis with an agent: the OK first ----

    /// <summary>«Analizar con un agente»: says what it does and what it may cost, and waits for the OK. Nothing is launched yet.</summary>
    [RelayCommand(CanExecute = nameof(CanAskAgentNow))]
    private async Task AskAgentAsync()
    {
        if (_project is not { } project)
        {
            return;
        }

        ErrorMessage = null;
        AgentSummary = null;
        IsAskingConsent = true;
        var estimate = _texts.Get("Monitor_Consumption_EstimateAsking");
        ConsentText = _texts.Format("Monitor_Consumption_Consent", estimate);
        if (EstimateAssisted is not { } estimator)
        {
            ConsentText = _texts.Format("Monitor_Consumption_Consent", _texts.Get("Monitor_Consumption_EstimateNone"));
            return;
        }

        _estimating?.Cancel();
        _estimating = new CancellationTokenSource();
        var token = _estimating.Token;
        var generation = _generation;
        string text;
        try
        {
            var found = await estimator(project, token).ConfigureAwait(true);
            text = QuestionCosts.Of(found.ToSnapshot("analysis"), _texts).Text;
        }
        catch (OperationCanceledException)
        {
            return;
        }
#pragma warning disable CA1031 // The estimator is behind a port: when it cannot say, the OK says the cost is unknown.
        catch (Exception)
#pragma warning restore CA1031
        {
            text = _texts.Get("Monitor_Consumption_EstimateNone");
        }

        if (generation == _generation && IsAskingConsent)
        {
            ConsentText = _texts.Format("Monitor_Consumption_Consent", text);
        }
    }

    private bool CanAskAgentNow() => CanAskAgent && HasAnalysis && !IsAnalyzing && !IsAgentRunning;

    /// <summary>«Sí, gastar cuota»: the OK of the person, once, with what they were shown. Only now the agent is launched.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private Task ConfirmAgentAsync()
    {
        Completion = RunAssistedAsync();
        return Completion;
    }

    private bool CanConfirm() => IsAskingConsent && !IsAnalyzing && !IsAgentRunning && Assisted is not null && _report is not null;

    /// <summary>«No»: nothing was launched and nothing was spent.</summary>
    [RelayCommand(CanExecute = nameof(IsAskingConsent))]
    private void CancelAgent()
    {
        _estimating?.Cancel();
        IsAskingConsent = false;
        ConsentText = null;
    }

    private async Task RunAssistedAsync()
    {
        if (Assisted is not { } assisted || _project is not { } project || _run is not { } run || _report is not { } report)
        {
            return;
        }

        // The consent says who gave it, when and what they were shown: that is what the log keeps of the OK.
        var consent = new QuotaConsent(ConsentedBy, _time.GetUtcNow(), ConsentText ?? _texts.Get("Monitor_Consumption_Button"));
        var generation = ++_generation;
        IsAskingConsent = false;
        IsAgentRunning = true;
        ErrorMessage = null;
        Suggestions.Clear();
        AssistedConsumptionAnalysis? analysis = null;
        string? failure = null;
        try
        {
            analysis = await assisted.AnalyzeAsync(new AssistedConsumptionRequest(project, run, report, consent), null, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The service is behind a port: whatever it refuses (no consent, no agent of another vendor, a log that does not verify) is said in words.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            failure = ex.Message;
        }

        Post(generation, () =>
        {
            IsAgentRunning = false;
            ConsentText = null;
            if (analysis is null)
            {
                ErrorMessage = _texts.Format("Monitor_Consumption_AgentFailed", failure ?? string.Empty);
                return;
            }

            foreach (var suggestion in analysis.Suggestions)
            {
                Suggestions.Add(SuggestionRow(suggestion));
            }

            AgentSummary = analysis.Suggestions.Count == 0
                ? _texts.Get("Monitor_Consumption_AgentNothing")
                : _texts.Format("Monitor_Consumption_AgentFound", analysis.Suggestions.Count.ToString(_texts.Culture));
            AskAgentCommand.NotifyCanExecuteChanged();
        });
    }

    private AssistedSuggestionRow SuggestionRow(AssistedConsumptionSuggestion suggestion)
    {
        string? fix = null;
        if (suggestion.Fix is { } proposed)
        {
            var button = MontageTexts.ButtonOf(proposed);
            fix = _texts.Format("Monitor_Consumption_AgentFix", _texts.Render(new UserText(button.Code, button.Arguments)));
        }

        return new AssistedSuggestionRow(
            _texts.Get("Monitor_Consumption_AgentSays"),
            suggestion.Text,
            TargetText(suggestion.Target),
            fix,
            suggestion.SavingClaimedByAgent is { } saving
                ? _texts.Format("Monitor_Consumption_AgentSaving", saving.ToString("N0", _texts.Culture))
                : null);
    }

    private void Post(int generation, Action action)
    {
        void Run()
        {
            if (generation == _generation)
            {
                action();
            }
        }

        if (_ui is null)
        {
            Run();
        }
        else
        {
            _ui.Post(_ => Run(), null);
        }
    }
}
