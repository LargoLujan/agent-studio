using System.Windows;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Views.Orchestrator.Questions;

namespace AgentStudio.Desktop.Orchestrator.Questions;

/// <summary>Where the windows of the questions are shown; the app's is <see cref="WpfQuestionWindowHost"/>, and tests use a double, so no window is ever shown by them.</summary>
public interface IQuestionWindowHost
{
    /// <summary>Whether the app is in view: its main window is visible, not minimized and not hidden in the tray.</summary>
    bool IsAppInView { get; }

    /// <summary>
    /// Shows the window of a question, or brings it forward when it is open already. Never modal. With <paramref name="activate"/> false
    /// it opens without taking the focus from what the person was doing (the window that opens by itself): a stray key cannot answer it.
    /// </summary>
    void Show(QuestionViewModel question, bool activate = true);

    /// <summary>Closes the window of a question, if it is open.</summary>
    void Close(QuestionViewModel question);

    /// <summary>Hides every open window (the interface is locked). They come back with <see cref="Restore"/>.</summary>
    void Withdraw();

    /// <summary>Shows again the windows <see cref="Withdraw"/> hid.</summary>
    void Restore();
}

/// <summary>
/// What the tray needs to say that an agent asks something (docs/17 D-31 and D-41): a notice with no data of the project, so it
/// can be read by anyone who sees the screen of the computer. The text is the same for every question.
/// </summary>
public sealed class QuestionAttentionEventArgs(string notice) : EventArgs
{
    /// <summary>«Un agente te pregunta algo».</summary>
    public string Notice { get; } = notice;
}

/// <summary>
/// Opens the window of each question of the run that the monitor follows (docs/17 D-31, F13-14), and keeps the rules around it:
/// <list type="bullet">
/// <item>The window opens by itself if the app is in view; otherwise <see cref="AttentionRequested"/> asks for the notice of the
/// tray «Un agente te pregunta algo», which carries no data of the project (the shell shows it, D-41).</item>
/// <item>It is not modal. «Decidir más tarde» closes it and keeps the card of the monitor; «Responder» on that card opens it again.
/// Neither cancels the task.</item>
/// <item>With the interface locked the windows are withdrawn and come back when it unlocks, like an approval (ADR-0037); a question
/// that arrives while it is locked waits for the unlock and asks for the notice of the tray.</item>
/// <item>A question that was answered, by the person or by the consent of the run, or whose task ended, closes its window.</item>
/// </list>
/// </summary>
public sealed class QuestionPromptService : IDisposable
{
    private readonly RunMonitorViewModel _monitor;
    private readonly IQuestionWindowHost _host;
    private readonly IInterfaceLock? _lock;
    private readonly HashSet<string> _deferred = new(StringComparer.Ordinal);

    public QuestionPromptService(RunMonitorViewModel monitor, IQuestionWindowHost host, IInterfaceLock? interfaceLock = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(host);
        _monitor = monitor;
        _host = host;
        _lock = interfaceLock;
        _monitor.QuestionRaised += OnRaised;
        _monitor.QuestionResolved += OnResolved;
        _monitor.QuestionOpenRequested += OnOpenRequested;
        if (_lock is not null)
        {
            _lock.Changed += OnLockChanged;
        }

        // The questions that were already waiting when the service was built (a run attached before, or one rebuilt after a restart)
        // get the same treatment as the ones that arrive later.
        foreach (var waiting in _monitor.PendingQuestions.ToList())
        {
            OnRaised(_monitor, waiting);
        }
    }

    /// <summary>The app is out of view or locked while an agent asks: the shell shows the notice of the tray (D-41). It carries no data.</summary>
    public event EventHandler<QuestionAttentionEventArgs>? AttentionRequested;

    private bool IsLocked => _lock?.IsLocked == true;

    private void OnRaised(object? sender, QuestionViewModel question)
    {
        question.DeferRequested += OnDeferRequested;
        question.Answered += OnAnswered;
        if (IsLocked || !_host.IsAppInView)
        {
            // The window waits for the app to be in view: the person is told in the tray, which shows nothing of the project.
            AttentionRequested?.Invoke(this, new QuestionAttentionEventArgs(Texts.Get("Monitor_Question_Notice")));
        }

        if (!IsLocked && _host.IsAppInView)
        {
            _host.Show(question, activate: false);
        }
    }

    private void OnResolved(object? sender, QuestionViewModel question)
    {
        question.DeferRequested -= OnDeferRequested;
        question.Answered -= OnAnswered;
        _deferred.Remove(question.Id);
        _host.Close(question);
    }

    private void OnOpenRequested(object? sender, QuestionViewModel question)
    {
        // The person asked for it: it opens even if it was deferred. Not while the interface is locked.
        _deferred.Remove(question.Id);
        if (!IsLocked)
        {
            _host.Show(question);
        }
    }

    private void OnDeferRequested(object? sender, EventArgs e)
    {
        if (sender is QuestionViewModel question)
        {
            _deferred.Add(question.Id);
            _host.Close(question);
        }
    }

    private void OnAnswered(object? sender, EventArgs e)
    {
        if (sender is QuestionViewModel question)
        {
            _host.Close(question);
        }
    }

    private void OnLockChanged(object? sender, EventArgs e)
    {
        if (IsLocked)
        {
            _host.Withdraw();
            return;
        }

        _host.Restore();

        // A question that came while it was locked, or whose window the lock withdrew before it opened, shows now.
        foreach (var question in _monitor.PendingQuestions.Where(q => !_deferred.Contains(q.Id)))
        {
            _host.Show(question, activate: false);
        }
    }

    public void Dispose()
    {
        _monitor.QuestionRaised -= OnRaised;
        _monitor.QuestionResolved -= OnResolved;
        _monitor.QuestionOpenRequested -= OnOpenRequested;
        if (_lock is not null)
        {
            _lock.Changed -= OnLockChanged;
        }
    }
}

/// <summary>
/// The windows of the questions in the app (docs/17 D-31): a themed window over the main window, shown without blocking it. It
/// closes when the question is answered, deferred or resolved, and closing it any other way is the same as deferring: the question
/// stays on its card, and the task keeps waiting.
/// </summary>
public sealed class WpfQuestionWindowHost : IQuestionWindowHost
{
    private readonly Dictionary<string, QuestionWindow> _windows = new(StringComparer.Ordinal);
    private readonly List<string> _withdrawn = [];

    public bool IsAppInView => System.Windows.Application.Current?.MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized };

    public void Show(QuestionViewModel question, bool activate = true)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (_windows.TryGetValue(question.Id, out var open))
        {
            open.Show();
            if (activate)
            {
                open.Activate();
            }

            return;
        }

        var window = new QuestionWindow { DataContext = question };
        if (System.Windows.Application.Current?.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // Nothing is chosen for the person, and a window that opens by itself does not take the focus: a key meant for what they were
        // writing cannot answer the agent.
        window.ShowActivated = activate;
        _windows[question.Id] = window;
        window.Closed += (_, _) =>
        {
            // Closed by the person with the X (not by Close below, which removes it first): the same as «Decidir más tarde».
            if (_windows.Remove(question.Id) && !question.IsAnswered)
            {
                question.DeferCommand.Execute(null);
            }
        };
        window.Show();
    }

    public void Close(QuestionViewModel question)
    {
        ArgumentNullException.ThrowIfNull(question);
        _withdrawn.Remove(question.Id);
        if (_windows.TryGetValue(question.Id, out var window))
        {
            _windows.Remove(question.Id);
            window.Close();
        }
    }

    public void Withdraw()
    {
        foreach (var (id, window) in _windows.Where(w => w.Value.IsVisible).ToList())
        {
            window.Hide();
            _withdrawn.Add(id);
        }
    }

    public void Restore()
    {
        foreach (var id in _withdrawn.ToList())
        {
            if (_windows.TryGetValue(id, out var window))
            {
                window.Show();
            }
        }

        _withdrawn.Clear();
    }
}
