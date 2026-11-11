using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Tests.Desktop.Orchestrator;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Lock;

/// <summary>
/// F8-10: the lock screen says since when it is locked, what goes on and what waits for the person, and never shows the
/// content it hides; it unlocks with Windows Hello (a double), and without Hello it offers «Bloquear Windows».
/// </summary>
public sealed class LockScreenViewModelTests
{
    private static readonly ApprovalRequest Delivery = new(
        "deliver-integration", "Entregar la ejecución 20261021-abc123 en main.", RiskLevel.Critical, null, [@"src\Secreto\Calc.cs"], "p-secreto", "20261021-abc123");

    private sealed class Work : IWorkInProgress
    {
        public List<ProjectActivityKind> Kinds { get; set; } = [];

        public IReadOnlyList<ProjectActivityKind> Current() => Kinds;
    }

    private sealed class Harness
    {
        public Harness(FakeUserPresenceVerifier? hello = null, bool withWork = true, SynchronizationContext? context = null)
        {
            Hello = hello ?? new FakeUserPresenceVerifier();
            Lock = new InterfaceLock(Hello, Session, Clock);
            Screen = new LockScreenViewModel(Lock, Clock, context ?? new InlineSynchronizationContext(), withWork ? Work : null, TimeSpan.FromSeconds(15));
        }

        public FakeTimeProvider Clock { get; } = new(T0);

        public FakeUserPresenceVerifier Hello { get; }

        public FakeWindowsSession Session { get; } = new();

        public Work Work { get; } = new();

        public InterfaceLock Lock { get; }

        public LockScreenViewModel Screen { get; }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5, Ct);
        }

        Assert.True(condition(), "The condition never held.");
    }

    [Fact]
    public void Unlocked_there_is_nothing_to_hide_and_it_can_be_locked()
    {
        var harness = new Harness();

        Assert.False(harness.Screen.IsLocked);
        Assert.True(harness.Screen.LockCommand.CanExecute(null));
        Assert.False(harness.Screen.UnlockCommand.CanExecute(null));
        Assert.False(harness.Screen.LockWindowsCommand.CanExecute(null));
    }

    [Fact]
    public async Task Locked_it_says_since_when_what_goes_on_and_what_waits_and_nothing_of_the_content()
    {
        var harness = new Harness();
        harness.Work.Kinds = [ProjectActivityKind.Run, ProjectActivityKind.Run, ProjectActivityKind.Debate];
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.Screen.LockCommand.ExecuteAsync(null);
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        var waiting = harness.Lock.WaitUntilUnlockedAsync(Delivery, Ct);

        Assert.True(harness.Screen.IsLocked);
        Assert.Equal("Agent Studio está bloqueado", LockScreenViewModel.Title);
        Assert.Equal("Bloqueado por ti a las 10:05.", harness.Screen.LockedText);
        Assert.Equal("Sigue en marcha: 2 ejecuciones y 1 debate.", harness.Screen.RunningText);
        Assert.Equal("Hay 1 aprobación pendiente.", harness.Screen.WaitingText);
        Assert.Equal(["deliver-integration · riesgo crítico · desde las 10:07"], harness.Screen.WaitingItems);
        Assert.True(harness.Screen.UnlockCommand.CanExecute(null));
        Assert.True(harness.Screen.LockWindowsCommand.CanExecute(null));
        Assert.False(harness.Screen.LockCommand.CanExecute(null));

        // Locking hides the content: nothing of the question but its action, nor of the projects.
        var shown = string.Join("\n", [harness.Screen.LockedText, harness.Screen.RunningText, harness.Screen.WaitingText, .. harness.Screen.WaitingItems, LockScreenViewModel.Assurance]);
        Assert.DoesNotContain("Entregar", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("Secreto", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("p-secreto", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("20261021-abc123", shown, StringComparison.Ordinal);

        await harness.Screen.UnlockCommand.ExecuteAsync(null);
        await waiting.WaitAsync(Ct);

        Assert.False(harness.Screen.IsLocked);
        Assert.Empty(harness.Screen.WaitingItems);
        Assert.Null(harness.Screen.Message);
    }

    [Fact]
    public async Task Locked_by_the_idle_lock_it_says_so()
    {
        var harness = new Harness();

        await harness.Lock.LockAsync(LockReason.Idle, Ct);

        Assert.Equal("Bloqueado a las 10:00 por inactividad.", harness.Screen.LockedText);
        Assert.Equal("No hay trabajo en marcha.", harness.Screen.RunningText);
        Assert.Equal("No hay nada pendiente.", harness.Screen.WaitingText);
    }

    [Fact]
    public async Task Without_a_source_of_the_work_it_says_there_is_no_data()
    {
        var harness = new Harness(withWork: false);

        await harness.Lock.LockAsync(LockReason.Manual, Ct);

        Assert.Equal("Lo que sigue en marcha: sin dato.", harness.Screen.RunningText);
    }

    [Theory]
    [InlineData(new[] { ProjectActivityKind.Run }, "Sigue en marcha: 1 ejecución.")]
    [InlineData(new[] { ProjectActivityKind.Planning, ProjectActivityKind.Planning }, "Sigue en marcha: 2 planificaciones.")]
    [InlineData(new[] { ProjectActivityKind.Audit, ProjectActivityKind.Run, ProjectActivityKind.Debate, ProjectActivityKind.Planning }, "Sigue en marcha: 1 ejecución, 1 planificación, 1 debate y 1 auditoría.")]
    [InlineData(new[] { ProjectActivityKind.Audit, ProjectActivityKind.Audit, ProjectActivityKind.Debate, ProjectActivityKind.Debate }, "Sigue en marcha: 2 debates y 2 auditorías.")]
    public void What_goes_on_is_counted_by_kind(ProjectActivityKind[] kinds, string expected) =>
        Assert.Equal(expected, LockScreenViewModel.Running(kinds));

    [Fact]
    public async Task Several_questions_waiting_are_listed_oldest_first()
    {
        var harness = new Harness();
        await harness.Lock.LockAsync(LockReason.Manual, Ct);

        _ = harness.Lock.WaitUntilUnlockedAsync(Delivery, Ct);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        _ = harness.Lock.WaitUntilUnlockedAsync(Delivery with { Action = "discard-run-work", Risk = RiskLevel.Destructive }, Ct);

        Assert.Equal("Hay 2 aprobaciones pendientes.", harness.Screen.WaitingText);
        Assert.Equal(["deliver-integration · riesgo crítico · desde las 10:00", "discard-run-work · acción destructiva · desde las 10:01"], harness.Screen.WaitingItems);
    }

    [Theory]
    [InlineData(UserPresenceResult.Canceled, "se canceló la verificación")]
    [InlineData(UserPresenceResult.RetriesExhausted, "demasiados intentos fallidos")]
    public async Task A_yes_windows_hello_did_not_confirm_leaves_it_locked_and_says_why(UserPresenceResult result, string why)
    {
        var harness = new Harness(FakeUserPresenceVerifier.Answering(result));
        await harness.Lock.LockAsync(LockReason.Manual, Ct);

        await harness.Screen.UnlockCommand.ExecuteAsync(null);

        Assert.True(harness.Screen.IsLocked);
        Assert.Equal($"Windows Hello no confirmó que eres tú ({why}). Vuelve a intentarlo o usa «Bloquear Windows».", harness.Screen.Message);
        Assert.Equal(Tone.Warning, harness.Screen.MessageTone);
    }

    [Fact]
    public async Task Windows_hello_that_cannot_be_asked_leaves_it_locked_and_says_why()
    {
        var harness = new Harness(new FakeUserPresenceVerifier { Verify = (_, _) => throw new InvalidOperationException("No window.") });
        await harness.Lock.LockAsync(LockReason.Manual, Ct);

        await harness.Screen.UnlockCommand.ExecuteAsync(null);

        Assert.True(harness.Screen.IsLocked);
        Assert.Equal("No se pudo pedir Windows Hello (No window.). Vuelve a intentarlo o usa «Bloquear Windows».", harness.Screen.Message);
    }

    [Fact]
    public async Task Without_windows_hello_it_offers_bloquear_windows_and_the_unlock_of_windows_unlocks_it()
    {
        var harness = new Harness();
        await harness.Lock.LockAsync(LockReason.Manual, Ct);
        harness.Hello.Availability = _ => Task.FromResult(UserPresenceAvailability.NotConfiguredForUser);

        await harness.Screen.UnlockCommand.ExecuteAsync(null);

        Assert.True(harness.Screen.IsLocked);
        Assert.Equal(
            "Windows Hello no está disponible (no está configurado para tu usuario). Usa «Bloquear Windows»: al desbloquear Windows, se desbloquea Agent Studio.",
            harness.Screen.HelloAbsentText);
        Assert.Empty(harness.Hello.Messages);

        harness.Screen.LockWindowsCommand.Execute(null);
        Assert.Equal(1, harness.Session.Locks);
        Assert.Equal("Windows está bloqueado: al desbloquearlo, se desbloquea Agent Studio.", harness.Screen.Message);
        harness.Session.RaiseUnlocked();

        Assert.False(harness.Screen.IsLocked);
        Assert.Null(harness.Screen.Message);
        Assert.Null(harness.Screen.HelloAbsentText);
    }

    [Fact]
    public async Task Bloquear_without_windows_hello_locks_windows_too_and_says_it()
    {
        var harness = new Harness(FakeUserPresenceVerifier.Without(UserPresenceAvailability.DeviceNotPresent));

        await harness.Screen.LockCommand.ExecuteAsync(null);

        Assert.True(harness.Screen.IsLocked);
        Assert.Equal(1, harness.Session.Locks);
        Assert.Equal("Windows está bloqueado: al desbloquearlo, se desbloquea Agent Studio.", harness.Screen.Message);
    }

    [Fact]
    public async Task When_windows_refuses_to_lock_it_says_so()
    {
        var harness = new Harness();
        harness.Session.Refuses = true;
        await harness.Lock.LockAsync(LockReason.Manual, Ct);

        harness.Screen.LockWindowsCommand.Execute(null);

        Assert.True(harness.Screen.IsLocked);
        Assert.Equal("Windows no se pudo bloquear. Vuelve a intentarlo.", harness.Screen.Message);
        Assert.Equal(Tone.Error, harness.Screen.MessageTone);
    }

    [Fact]
    public async Task The_screen_follows_the_lock_on_the_UI_context_whatever_thread_locks()
    {
        var context = new QueueSynchronizationContext();
        var harness = new Harness(context: context);

        await Task.Run(() => harness.Lock.LockAsync(LockReason.Idle, CancellationToken.None), Ct);
        await UntilAsync(() => context.Pending > 0);

        Assert.False(harness.Screen.IsLocked);
        context.RunAll();
        Assert.True(harness.Screen.IsLocked);
    }

    [Fact]
    public async Task While_locked_what_goes_on_is_read_again()
    {
        var harness = new Harness();
        harness.Work.Kinds = [ProjectActivityKind.Run];
        await harness.Lock.LockAsync(LockReason.Manual, Ct);
        Assert.Equal("Sigue en marcha: 1 ejecución.", harness.Screen.RunningText);

        harness.Work.Kinds = [];
        harness.Clock.Advance(TimeSpan.FromSeconds(15));

        await UntilAsync(() => harness.Screen.RunningText == "No hay trabajo en marcha.");
    }

    [Fact]
    public async Task Disposed_it_follows_the_lock_no_more()
    {
        var harness = new Harness();

        harness.Screen.Dispose();
        await harness.Lock.LockAsync(LockReason.Manual, Ct);

        Assert.False(harness.Screen.IsLocked);
    }
}
