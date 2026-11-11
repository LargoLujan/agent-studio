using System.Collections.ObjectModel;
using System.Globalization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Lock;

/// <summary>
/// What goes on in this process, as the lock screen tells it (docs/16 D-13): the kind of activity of each project that has one.
/// It never names a project: locking hides the content.
/// </summary>
public interface IWorkInProgress
{
    IReadOnlyList<ProjectActivityKind> Current();
}

/// <summary>
/// The lock screen (docs/16 D-13, F8-10): over the interface while it is locked, it says since when, what goes on and what
/// waits for the person (the action of each question and since when, never its description nor its paths), unlocks with
/// Windows Hello and offers «Bloquear Windows». It follows <see cref="InterfaceLock"/> on the UI thread, whatever thread
/// changes it, and reads again what goes on every <c>refreshInterval</c>.
/// </summary>
public sealed partial class LockScreenViewModel : ObservableObject, IDisposable
{
    public static string Title => Texts.Get("Lock_Screen_Title");

    /// <summary>What the lock does and does not do, as the screen says it.</summary>
    public static string Assurance => Texts.Get("Lock_Screen_Assurance");

    /// <summary>How often what goes on is read again while locked.</summary>
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly InterfaceLock _lock;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _ui;
    private readonly IWorkInProgress? _work;
    private readonly ITimer _refresh;
    private bool _disposed;

    /// <param name="uiContext">The UI thread's context; defaults to the creating thread's context.</param>
    /// <param name="work">What goes on in this process; without it the screen says «sin dato».</param>
    /// <param name="refreshInterval">How often what goes on is read again; <see cref="DefaultRefreshInterval"/> by default.</param>
    public LockScreenViewModel(InterfaceLock interfaceLock, TimeProvider time, SynchronizationContext? uiContext = null, IWorkInProgress? work = null, TimeSpan? refreshInterval = null)
    {
        ArgumentNullException.ThrowIfNull(interfaceLock);
        ArgumentNullException.ThrowIfNull(time);
        _lock = interfaceLock;
        _time = time;
        _ui = uiContext ?? SynchronizationContext.Current;
        _work = work;
        var every = refreshInterval ?? DefaultRefreshInterval;
        _refresh = time.CreateTimer(_ => OnRefreshDue(), null, every, every);
        interfaceLock.Changed += OnLockChanged;
        Refresh();
    }

    /// <summary>Whether the interface is locked: the lock screen is over it, and its content is hidden.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    [NotifyCanExecuteChangedFor(nameof(LockWindowsCommand))]
    [NotifyCanExecuteChangedFor(nameof(LockCommand))]
    private bool _isLocked;

    /// <summary>«Bloqueado a mano a las 14:05.» or «Bloqueado a las 14:05 por inactividad.»</summary>
    [ObservableProperty]
    private string _lockedText = string.Empty;

    /// <summary>What goes on, counted by kind, or «sin dato».</summary>
    [ObservableProperty]
    private string _runningText = string.Empty;

    /// <summary>How many questions wait for the person.</summary>
    [ObservableProperty]
    private string _waitingText = string.Empty;

    /// <summary>Why Windows Hello is not there, and what to do instead; null while it is there or nobody asked.</summary>
    [ObservableProperty]
    private string? _helloAbsentText;

    /// <summary>What came of the last action on the screen; null when there is nothing to say.</summary>
    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private Tone _messageTone = Tone.Neutral;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    private bool _isUnlocking;

    /// <summary>Each question waiting: its action, its risk and since when.</summary>
    public ObservableCollection<string> WaitingItems { get; } = [];

    /// <summary>What goes on, counted by kind of activity, as the screen says it.</summary>
    public static string Running(IReadOnlyCollection<ProjectActivityKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Count == 0)
        {
            return Texts.Get("Lock_Screen_NoWork");
        }

        var parts = new List<string>();
        foreach (var kind in Kinds)
        {
            var count = kinds.Count(k => k == kind);
            if (count > 0)
            {
                parts.Add(Texts.Format("Lock_Kind_" + kind + (count == 1 ? "_One" : "_Other"), count));
            }
        }

        return Texts.Format("Lock_Screen_Running", Join(parts));
    }

    /// <summary>«Bloquear»: locks the interface; without Windows Hello on this computer, Windows too.</summary>
    [RelayCommand(CanExecute = nameof(CanLock))]
    private async Task LockAsync()
    {
        var attempt = await _lock.LockAsync(LockReason.Manual, CancellationToken.None).ConfigureAwait(true);
        Refresh();
        Say(attempt.Outcome switch
        {
            LockOutcome.LockedWithWindows => (WindowsLocked, Tone.Neutral),
            LockOutcome.WindowsRefused => (WindowsRefused, Tone.Error),
            _ => (null, Tone.Neutral),
        });
    }

    private bool CanLock() => !IsLocked;

    /// <summary>«Desbloquear con Windows Hello»: only a confirmation of Windows unlocks.</summary>
    [RelayCommand(CanExecute = nameof(CanUnlock))]
    private async Task UnlockAsync()
    {
        IsUnlocking = true;
        try
        {
            var attempt = await _lock.UnlockWithHelloAsync(CancellationToken.None).ConfigureAwait(true);
            Refresh();
            Say(attempt.Outcome switch
            {
                UnlockOutcome.NotConfirmed => (Texts.Format("Lock_Screen_NotConfirmed", HelloAnswers.WhyNotConfirmed(attempt.Result ?? UserPresenceResult.Canceled), TryAgain), Tone.Warning),
                UnlockOutcome.HelloFailed => (Texts.Format("Lock_Screen_HelloFailed", attempt.Problem, TryAgain), Tone.Warning),
                _ => (null, Tone.Neutral),
            });
        }
        finally
        {
            IsUnlocking = false;
        }
    }

    private bool CanUnlock() => IsLocked && !IsUnlocking;

    /// <summary>«Bloquear Windows»: Windows locks, and its unlock, which verifies the person, unlocks the interface.</summary>
    [RelayCommand(CanExecute = nameof(CanLockWindows))]
    private void LockWindows()
    {
        var accepted = _lock.LockWindows();
        Say(accepted ? (WindowsLocked, Tone.Neutral) : (WindowsRefused, Tone.Error));
    }

    private bool CanLockWindows() => IsLocked;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lock.Changed -= OnLockChanged;
        _refresh.Dispose();
    }

    private static string TryAgain => Texts.Get("Lock_Screen_TryAgain");

    private static string WindowsLocked => Texts.Get("Lock_Screen_WindowsLocked");

    private static string WindowsRefused => Texts.Get("Lock_Screen_WindowsRefused");

    /// <summary>The kinds of activity, in the order the screen lists them (their key is <c>Lock_Kind_&lt;Kind&gt;_One</c> or <c>_Other</c>).</summary>
    private static readonly ProjectActivityKind[] Kinds =
    [
        ProjectActivityKind.Run,
        ProjectActivityKind.Planning,
        ProjectActivityKind.Debate,
        ProjectActivityKind.Audit,
    ];

    // Only while locked is there a screen to keep up to date.
    private void OnRefreshDue()
    {
        if (_lock.IsLocked)
        {
            OnLockChanged(this, EventArgs.Empty);
        }
    }

    private void OnLockChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_ui is null || ReferenceEquals(SynchronizationContext.Current, _ui))
        {
            Refresh();
        }
        else
        {
            _ui.Post(_ => Refresh(), null);
        }
    }

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var locked = _lock.IsLocked;
        IsLocked = locked;
        if (!locked)
        {
            LockedText = string.Empty;
            RunningText = string.Empty;
            WaitingText = string.Empty;
            HelloAbsentText = null;
            WaitingItems.Clear();
            Say((null, Tone.Neutral));
            return;
        }

        LockedText = Texts.Format(_lock.Reason == LockReason.Idle ? "Lock_Screen_LockedIdle" : "Lock_Screen_LockedManual", Clock(_lock.LockedAt));
        RunningText = _work is null ? Texts.Get("Lock_Screen_RunningUnknown") : Running(_work.Current());
        HelloAbsentText = _lock.HelloAbsent is { } absent
            ? Texts.Format("Lock_Screen_HelloAbsent", HelloAnswers.WhyAbsent(absent))
            : null;

        var waiting = _lock.Waiting;
        WaitingText = waiting.Count == 0
            ? Texts.Get("Lock_Screen_Waiting_None")
            : Texts.Format(waiting.Count == 1 ? "Lock_Screen_Waiting_One" : "Lock_Screen_Waiting_Other", waiting.Count);
        var items = waiting.Select(w => Texts.Format("Lock_Screen_Item", w.Action, RiskText(w.Risk), Clock(w.Since))).ToList();
        if (!items.SequenceEqual(WaitingItems))
        {
            WaitingItems.Clear();
            foreach (var item in items)
            {
                WaitingItems.Add(item);
            }
        }
    }

    private void Say((string? Text, Tone Tone) message)
    {
        Message = message.Text;
        MessageTone = message.Tone;
    }

    private string Clock(DateTimeOffset? at) =>
        at is { } value ? TimeZoneInfo.ConvertTime(value, _time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture) : "—";

    private static string RiskText(RiskLevel risk) => Texts.Get(risk switch
    {
        RiskLevel.Critical => "Lock_Risk_Critical",
        RiskLevel.Destructive => "Lock_Risk_Destructive",
        _ => "Lock_Risk_Low",
    });

    private static string Join(List<string> parts) =>
        parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts.Take(parts.Count - 1))} {Texts.Get("Lock_Join_And")} {parts[^1]}";
}
