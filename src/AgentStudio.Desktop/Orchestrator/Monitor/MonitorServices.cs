using AgentStudio.Application.Analysis;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator.Questions;

namespace AgentStudio.Desktop.Orchestrator.Monitor;

/// <summary>
/// What every monitor of the app is given (docs/04 section 21.10, «Lo que la integración tiene que hacer»): the sources of the
/// parts of the prompt, the quota, the analysis of consumption and the window of the questions of its run. The composition root
/// creates one of these; the places that create a <see cref="RunMonitorViewModel"/> (the document of the orchestrator, the shell
/// for a run nobody else follows) call <see cref="Equip"/> on it, so no monitor is left without them. Every part is optional: a
/// monitor equipped by an empty instance behaves as it did before Phase 13.
/// </summary>
public sealed class MonitorServices
{
    private readonly IContextPartsSource? _parts;
    private readonly QuotaViewModel? _quota;
    private readonly IConsumptionAnalyzer? _analyzer;
    private readonly IAssistedConsumptionAnalysis? _assisted;
    private readonly Func<ProjectContext, CancellationToken, Task<UsageEstimate>>? _estimateAssisted;
    private readonly IInterfaceLock? _lock;
    private readonly Func<IQuestionWindowHost> _hostFactory;
    private readonly IUiTextProvider _texts;
    private IQuestionWindowHost? _plannerHost;

    public MonitorServices(
        IContextPartsSource? parts = null,
        QuotaViewModel? quota = null,
        IConsumptionAnalyzer? analyzer = null,
        IAssistedConsumptionAnalysis? assisted = null,
        Func<ProjectContext, CancellationToken, Task<UsageEstimate>>? estimateAssisted = null,
        IInterfaceLock? interfaceLock = null,
        Func<IQuestionWindowHost>? hostFactory = null,
        IUiTextProvider? texts = null)
    {
        _parts = parts;
        _quota = quota;
        _analyzer = analyzer;
        _assisted = assisted;
        _estimateAssisted = estimateAssisted;
        _lock = interfaceLock;
        _hostFactory = hostFactory ?? (() => new WpfQuestionWindowHost());
        _texts = texts ?? Texts.Current;
        if (_lock is not null)
        {
            // The windows of the questions of the orchestrator go with the lock, like those of the questions of a run (ADR-0037).
            _lock.Changed += OnLockChanged;
        }
    }

    private void OnLockChanged(object? sender, EventArgs e)
    {
        if (_plannerHost is not { } host)
        {
            return;
        }

        if (_lock?.IsLocked == true)
        {
            host.Withdraw();
        }
        else
        {
            host.Restore();
        }
    }

    /// <summary>
    /// An agent asks something and the app is out of view or locked (docs/17 D-31 and D-41): the shell shows the notice in the tray.
    /// It carries no data of the project. Raised for the questions of the runs and for those of the orchestrator before it plans.
    /// </summary>
    public event EventHandler<QuestionAttentionEventArgs>? AttentionRequested;

    /// <summary>
    /// Gives <paramref name="monitor"/> its sources and the service of the windows of its questions (one per monitor, with its own
    /// host). Dispose the result when the monitor is let go; the monitor itself stays usable.
    /// </summary>
    public IDisposable Equip(RunMonitorViewModel monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        monitor.ContextPartsSource ??= _parts;
        monitor.Quota ??= _quota;
        var consumption = monitor.Consumption;
        consumption.Analyzer ??= _analyzer;
        consumption.Assisted ??= _assisted;
        consumption.EstimateAssisted ??= _estimateAssisted;

        var prompts = new QuestionPromptService(monitor, _hostFactory(), _lock);
        prompts.AttentionRequested += OnAttention;
        return new Release(prompts, this);
    }

    /// <summary>
    /// Shows the questions of the orchestrator before it plans (docs/17 D-28): the window of each one that is not answered, if the app
    /// is in view; otherwise the tray says that an agent asks. With <paramref name="activate"/> false the windows do not take the
    /// focus (they open by themselves); «Responder» passes true.
    /// </summary>
    public void PresentPlannerQuestions(PlannerQuestionSet set, bool activate)
    {
        ArgumentNullException.ThrowIfNull(set);
        var host = _plannerHost ??= _hostFactory();
        foreach (var question in set.Questions.Where(q => !q.IsAnswered))
        {
            if (_lock?.IsLocked == true || !host.IsAppInView)
            {
                AttentionRequested?.Invoke(this, new QuestionAttentionEventArgs(_texts.Get("Monitor_Question_Notice")));
                continue;
            }

            Closing(question, host);
            host.Show(question, activate);
        }
    }

    /// <summary>The window of the question closes when it is answered or deferred (once per question).</summary>
    private readonly HashSet<QuestionViewModel> _watched = [];

    private void Closing(QuestionViewModel question, IQuestionWindowHost host)
    {
        if (!_watched.Add(question))
        {
            return;
        }

        question.Answered += (_, _) => host.Close(question);
        question.DeferRequested += (_, _) => host.Close(question);
    }

    /// <summary>Closes the windows of the questions of a planning that was rejected or cancelled.</summary>
    public void WithdrawPlannerQuestions(PlannerQuestionSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (_plannerHost is not { } host)
        {
            return;
        }

        foreach (var question in set.Questions)
        {
            host.Close(question);
        }
    }

    private void OnAttention(object? sender, QuestionAttentionEventArgs e) => AttentionRequested?.Invoke(this, e);

    private sealed class Release(QuestionPromptService prompts, MonitorServices owner) : IDisposable
    {
        public void Dispose()
        {
            prompts.AttentionRequested -= owner.OnAttention;
            prompts.Dispose();
        }
    }
}
