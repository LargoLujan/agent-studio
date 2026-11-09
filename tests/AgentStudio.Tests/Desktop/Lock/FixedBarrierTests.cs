using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Services;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Security;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Security.Approvals;
using AgentStudio.Tests.Debate.Fakes;
using AgentStudio.Tests.Desktop.Orchestrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Lock;

/// <summary>
/// F13-16 (docs/17 D-37 to D-41, ADR-0053): the fixed barrier of Windows Hello. What is on the list asks Windows Hello with any
/// setting and any file: the setting is never read for it, and editing, inventing or deleting the settings file changes
/// nothing. Without Hello on this computer it locks Windows and trusts the state of the session; with Hello, only Hello counts.
/// Windows Hello, the lock of Windows and the dialog are doubles: no test asks the real Windows Hello nor locks the session.
/// </summary>
public sealed class FixedBarrierTests
{
    private const string Project = "p-barrera";

    /// <summary>The contents of a settings file edited by hand to turn the barrier off, and none at all.</summary>
    public static TheoryData<string, string?> ManipulatedSettings => new()
    {
        { "the setting off", """{"windowsHelloForImportantApprovals":false}""" },
        { "the setting off under its old name", """{"windowsHelloForCriticalApprovals":false}""" },
        {
            "keys made up to turn it off",
            """{"windowsHelloForImportantApprovals":false,"disableHello":true,"presencePolicy":{"deliver-integration":"Never"},"approvals":{"requireHello":false,"allow":["Bash","WebFetch"]}}"""
        },
        { "the setting with a value of another type", """{"windowsHelloForImportantApprovals":"no"}""" },
        { "a file that is not JSON", "{ deliver-integration: never" },
        { "the file deleted", null },
    };

    private sealed class FakeDialog(bool approved) : IApprovalDialog
    {
        public List<ApprovalDialogViewModel> Shown { get; } = [];

        public Task<ApprovalAnswer?> ShowAsync(ApprovalDialogViewModel question, CancellationToken cancellationToken)
        {
            Shown.Add(question);
            return Task.FromResult<ApprovalAnswer?>(new ApprovalAnswer(approved, null));
        }
    }

    private sealed class ZeroIdle : IIdleInput
    {
        public TimeSpan IdleFor() => TimeSpan.Zero;
    }

    /// <summary>
    /// The barrier as the app composes it (<see cref="LockServices.AddWindowsHelloAndLock"/>) over the real settings store of a
    /// temporary folder, with the gate and the prompt of the app in front of a scripted dialog.
    /// </summary>
    private sealed class World : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly ServiceProvider _container;

        public World(FakeUserPresenceVerifier hello, FakeWindowsLockConfirmation? windowsLock = null, bool approved = true)
        {
            Hello = hello;
            Paths = new AppDataPaths(_temp.Path);
            Dialog = new FakeDialog(approved);
            var services = new ServiceCollection();
            services.AddSingleton<IAppSettingsStore>(new JsonAppSettingsStore(Paths, null));
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IUserPresenceVerifier>(hello);
            services.AddSingleton<IWindowsSession>(new FakeWindowsSession());
            services.AddSingleton<IWindowsSessionState>(new FakeWindowsSessionState());
            services.AddSingleton<IIdleInput>(new ZeroIdle());
            if (windowsLock is not null)
            {
                services.AddSingleton<IWindowsLockConfirmation>(windowsLock);
            }

            _container = services.AddWindowsHelloAndLock().BuildServiceProvider();
            Confirmation = _container.GetRequiredService<ReinforcedConfirmation>();
            Prompt = new ApprovalPromptService(Dialog, Clock, new InlineSynchronizationContext(), Confirmation);
            Gate = new ApprovalGate(Prompt, new DecisionRecorder(Store, Clock), Clock, new ApprovalGateOptions());
        }

        public FakeTimeProvider Clock { get; } = new(T0);

        public AppDataPaths Paths { get; }

        public FakeUserPresenceVerifier Hello { get; }

        public FakeDialog Dialog { get; }

        public ReinforcedConfirmation Confirmation { get; }

        public ApprovalPromptService Prompt { get; }

        public ApprovalGate Gate { get; }

        public InMemoryDecisionLogStore Store { get; } = new();

        public IReadOnlyList<DecisionEntry> Log => Store.Of(Project);

        /// <summary>Writes the settings file as someone would by hand, or deletes it.</summary>
        public void WriteSettings(string? content)
        {
            Directory.CreateDirectory(Paths.BaseDirectory);
            if (content is null)
            {
                File.Delete(Paths.SettingsFile);
                return;
            }

            File.WriteAllText(Paths.SettingsFile, content);
        }

        public Task<ApprovalDecision> AskAsync(string action) =>
            Gate.RequestAsync(new ApprovalRequest(action, "Lo que se pide.", RiskLevel.Critical, null, [], Project, "20261109-abc123"), TestContext.Current.CancellationToken);

        public void Dispose()
        {
            _container.Dispose();
            _temp.Dispose();
        }
    }

    /// <summary>Waits, at most five seconds, until <paramref name="condition"/> holds.</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "The first confirmation never started.");
    }

    // ---- D-38: no setting and no file turns it off ----

    [Theory]
    [MemberData(nameof(ManipulatedSettings))]
    public async Task Every_action_of_the_list_asks_windows_hello_whatever_the_settings_file_says(string why, string? settings)
    {
        foreach (var action in new[] { DeliveryService.ApprovalAction, UnanchoredHistory.AcceptAction })
        {
            using var world = new World(FakeUserPresenceVerifier.Answering(UserPresenceResult.Verified));
            world.WriteSettings(settings);

            var decision = await world.AskAsync(action);

            Assert.True(decision.Approved, why);
            Assert.Equal(ApprovalVerification.WindowsHello, decision.Verification);
            Assert.Single(world.Hello.Messages);
            Assert.Equal(DecisionKind.ApprovalGranted, world.Log[1].Kind);
            Assert.Contains("\"verification\":\"WindowsHello\"", world.Log[1].PayloadJson, StringComparison.Ordinal);
            Assert.Equal(HelloTexts.AlwaysAsksHello, Assert.Single(world.Dialog.Shown).HelloNotice);
        }
    }

    /// <summary>And a person who cancels Windows Hello approves nothing, whatever the file says.</summary>
    [Theory]
    [MemberData(nameof(ManipulatedSettings))]
    public async Task With_any_settings_file_a_yes_windows_hello_does_not_confirm_approves_nothing(string why, string? settings)
    {
        using var world = new World(FakeUserPresenceVerifier.Answering(UserPresenceResult.Canceled));
        world.WriteSettings(settings);

        var decision = await world.AskAsync(DeliveryService.ApprovalAction);

        Assert.False(decision.Approved, why);
        Assert.Equal(new Actor(ActorKind.System, ApprovalGate.SystemId), world.Log[1].Actor);
    }

    /// <summary>The control: the setting does govern what is configurable. Off, a debate is decided by the dialog alone.</summary>
    [Fact]
    public async Task The_setting_off_still_governs_what_is_configurable()
    {
        using var world = new World(new FakeUserPresenceVerifier());
        world.WriteSettings("""{"windowsHelloForImportantApprovals":false}""");

        var decision = await world.AskAsync("debate.open");

        Assert.Equal((true, ApprovalVerification.Dialog), (decision.Approved, decision.Verification));
        Assert.Equal(0, world.Hello.AvailabilityChecks);
        Assert.Empty(world.Hello.Messages);
    }

    /// <summary>Evidence (a): for what is always asked, the setting is never even read; a setting that throws changes nothing.</summary>
    [Theory]
    [InlineData(PresenceAction.DeliverToBranch)]
    [InlineData(PresenceAction.AcceptHistoryAsOrigin)]
    [InlineData(PresenceAction.GrantHighRiskCapability)]
    [InlineData(PresenceAction.ApprovePlanWithHighRiskCapability)]
    [InlineData(PresenceAction.ChangeVerificationProfile)]
    [InlineData(PresenceAction.RemoveProtection)]
    [InlineData(PresenceAction.RemoteOrSecretOperation)]
    public async Task For_what_is_always_asked_the_setting_is_never_read(PresenceAction action)
    {
        var reads = 0;
        var confirmation = new ReinforcedConfirmation(new FakeUserPresenceVerifier(), () =>
        {
            reads++;
            throw new InvalidOperationException("The settings file was edited by hand.");
        });

        var plan = await confirmation.PlanAsync(action, CancellationToken.None);

        Assert.Equal(0, reads);
        Assert.Equal((ApprovalVerification.WindowsHello, PresenceRequirement.Always), (plan.Confirmation, plan.Requirement));
        Assert.True(ReinforcedConfirmation.Approves(plan, PresenceProofs.FromHello(UserPresenceResult.Verified)));
        Assert.False(ReinforcedConfirmation.Approves(plan, plan.AnswerAloneProof));
    }

    [Theory]
    [InlineData(PresenceAction.AnswerAgentQuestion)]
    [InlineData(PresenceAction.AuxiliaryQuotaConsent)]
    public async Task What_grants_nothing_asks_nothing_and_reads_no_setting(PresenceAction action)
    {
        var hello = new FakeUserPresenceVerifier();
        var confirmation = new ReinforcedConfirmation(hello, () => throw new InvalidOperationException("Not to be read."));

        var plan = await confirmation.PlanAsync(action, CancellationToken.None);

        Assert.False(plan.ConfirmsAfterYes);
        Assert.Equal(0, hello.AvailabilityChecks);
        Assert.True(ReinforcedConfirmation.Approves(plan, plan.AnswerAloneProof));
    }

    // ---- D-40 (a): without Windows Hello, the lock of Windows checked with the state of the session ----

    /// <summary>Evidence (c): without Hello, the delivery locks Windows, and the log says so with the method of its name.</summary>
    [Theory]
    [InlineData(UserPresenceAvailability.NotConfiguredForUser)]
    [InlineData(UserPresenceAvailability.DeviceNotPresent)]
    [InlineData(UserPresenceAvailability.DisabledByPolicy)]
    public async Task Without_windows_hello_an_action_of_the_list_locks_windows_and_the_log_says_windows_lock(UserPresenceAvailability absent)
    {
        var windowsLock = FakeWindowsLockConfirmation.Answering(WindowsLockResult.UnlockedAfterRequest);
        using var world = new World(FakeUserPresenceVerifier.Without(absent), windowsLock);
        world.WriteSettings("""{"windowsHelloForImportantApprovals":false}""");

        var decision = await world.AskAsync(DeliveryService.ApprovalAction);

        Assert.True(decision.Approved);
        Assert.Equal(ApprovalVerification.WindowsLock, decision.Verification);
        Assert.Equal([ReinforcedConfirmation.WindowsLockTimeout], windowsLock.Locks);
        Assert.Empty(world.Hello.Messages);
        var question = Assert.Single(world.Dialog.Shown);
        Assert.Equal((ApprovalVerification.WindowsLock, HelloTexts.LocksWindowsAfterYes, Tone.Warning), (question.Confirmation, question.HelloNotice, question.HelloNoticeTone));
        Assert.Equal(new Actor(ActorKind.User, ApprovalPromptService.DecidedBy), world.Log[1].Actor);
        Assert.Contains("\"verification\":\"WindowsLock\",\"v\":2", world.Log[1].PayloadJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without Hello, accepting a history never gets the answer of the dialog alone («Sin Hello: no disponible»), as in Phase 8:
    /// it locks Windows too, and an unlock the session does not confirm approves nothing.
    /// </summary>
    [Theory]
    [InlineData(WindowsLockResult.NotVerified)]
    [InlineData(WindowsLockResult.TimedOut)]
    [InlineData(WindowsLockResult.LockFailed)]
    public async Task Without_windows_hello_no_action_of_the_list_is_approved_by_the_dialog_alone(WindowsLockResult result)
    {
        using var world = new World(FakeUserPresenceVerifier.Without(UserPresenceAvailability.NotConfiguredForUser), FakeWindowsLockConfirmation.Answering(result));

        var decision = await world.AskAsync(UnanchoredHistory.AcceptAction);

        Assert.False(decision.Approved);
        Assert.NotEqual(ApprovalVerification.WindowsHelloUnavailable, decision.Verification);
        Assert.Equal(ApprovalVerification.WindowsLock, decision.Verification);
        Assert.Equal(HelloTexts.ApprovalLockNotConfirmed(result), decision.Comment);
        Assert.Equal(new Actor(ActorKind.System, ApprovalGate.SystemId), world.Log[1].Actor);
    }

    /// <summary>
    /// The re-review of the P0 (2026-11-08): with Windows Hello on the computer, the lock of Windows is no way around it. A
    /// confirmation by the lock, given when Hello was there, does not satisfy the one rule.
    /// </summary>
    [Fact]
    public async Task With_windows_hello_on_the_computer_the_lock_of_windows_never_approves_what_is_always_asked()
    {
        var confirmation = new ReinforcedConfirmation(new FakeUserPresenceVerifier(), () => true, WindowsLock: new FakeWindowsLockConfirmation());

        var plan = await confirmation.PlanAsync(PresenceAction.DeliverToBranch, CancellationToken.None);

        Assert.True(plan.HelloAvailable);
        Assert.True(plan.AsksHello);
        Assert.False(ReinforcedConfirmation.Approves(plan, PresenceProofs.FromWindowsLock(WindowsLockResult.UnlockedAfterRequest)));
        Assert.True(ReinforcedConfirmation.Approves(plan, PresenceProofs.FromHello(UserPresenceResult.Verified)));

        // Busy, or Windows could not say: Hello is asked, as if it were there, and the lock does not count either.
        var busy = await new ReinforcedConfirmation(FakeUserPresenceVerifier.Without(UserPresenceAvailability.DeviceBusy), () => true).PlanAsync(PresenceAction.DeliverToBranch, CancellationToken.None);
        Assert.False(ReinforcedConfirmation.Approves(busy, PresenceProofs.FromWindowsLock(WindowsLockResult.UnlockedAfterRequest)));
    }

    /// <summary>
    /// D-40 (b): on a computer without Hello where Windows does not lock (a policy forbids it), nothing can approve an action of
    /// the list, not even a delivery. The reason says what to do: set up Windows Hello, and where.
    /// </summary>
    [Fact]
    public async Task When_windows_does_not_lock_the_denial_says_how_to_set_up_windows_hello()
    {
        using var world = new World(FakeUserPresenceVerifier.Without(UserPresenceAvailability.NotConfiguredForUser), FakeWindowsLockConfirmation.Answering(WindowsLockResult.LockFailed));

        var decision = await world.AskAsync(DeliveryService.ApprovalAction);

        Assert.False(decision.Approved);
        Assert.EndsWith(HelloTexts.SetUpHello, decision.Comment, StringComparison.Ordinal);

        // Only when nothing else can work: a lock nobody unlocked in time can be tried again.
        Assert.DoesNotContain(HelloTexts.SetUpHello, HelloTexts.ApprovalLockNotConfirmed(WindowsLockResult.TimedOut), StringComparison.Ordinal);
        Assert.EndsWith(HelloTexts.SetUpHello, HelloTexts.PlanLockNotConfirmed(WindowsLockResult.LockFailed), StringComparison.Ordinal);
        Assert.EndsWith(HelloTexts.SetUpHello, HelloTexts.PlanLockNotAsked(HelloTexts.LockNotComposed), StringComparison.Ordinal);
    }

    /// <summary>Without the lock of Windows composed, nothing can confirm the person: the yes is denied, never left to the dialog.</summary>
    [Fact]
    public async Task Without_hello_and_without_the_lock_of_windows_composed_the_yes_is_denied_and_says_what_to_do()
    {
        var hello = FakeUserPresenceVerifier.Without(UserPresenceAvailability.DeviceNotPresent);
        var dialog = new FakeDialog(approved: true);
        var prompt = new ApprovalPromptService(dialog, new FakeTimeProvider(T0), new InlineSynchronizationContext(), new ReinforcedConfirmation(hello, () => true));

        var decision = await prompt.AskAsync(new ApprovalRequest(DeliveryService.ApprovalAction, "Entregar.", RiskLevel.Critical, null, []), CancellationToken.None);

        Assert.False(decision.Approved);
        Assert.Equal(ApprovalVerification.WindowsLock, decision.Verification);
        Assert.Equal(HelloTexts.ApprovalLockNotAsked(HelloTexts.LockNotComposed), decision.Comment);
    }

    /// <summary>A prompt composed without Windows Hello does not even show an action of the list: the gate denies it in its own name.</summary>
    [Fact]
    public async Task Without_a_reinforced_confirmation_an_action_of_the_list_is_not_shown_and_the_gate_denies_it()
    {
        var clock = new FakeTimeProvider(T0);
        var store = new InMemoryDecisionLogStore();
        var dialog = new FakeDialog(approved: true);
        var gate = new ApprovalGate(new ApprovalPromptService(dialog, clock, new InlineSynchronizationContext()), new DecisionRecorder(store, clock), clock, new ApprovalGateOptions());

        var decision = await gate.RequestAsync(new ApprovalRequest(DeliveryService.ApprovalAction, "Entregar.", RiskLevel.Critical, null, [], Project), TestContext.Current.CancellationToken);

        Assert.False(decision.Approved);
        Assert.Empty(dialog.Shown);
        Assert.Equal(new Actor(ActorKind.System, ApprovalGate.SystemId), store.Of(Project)[1].Actor);
    }

    /// <summary>
    /// The security review of WP5 (2026-11-09): one confirmation of the person at a time. While the delivery waits for the
    /// unlock of Windows, accepting a history is not confirmed by that same unlock: it is denied at once, and says that another
    /// confirmation is pending. The first one goes on, and the unlock approves it alone.
    /// </summary>
    [Fact]
    public async Task While_the_lock_of_windows_waits_a_second_approval_is_denied_and_says_another_is_pending()
    {
        var unlock = new TaskCompletionSource<WindowsLockResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windowsLock = new FakeWindowsLockConfirmation { Answer = (_, _) => unlock.Task };
        using var world = new World(FakeUserPresenceVerifier.Without(UserPresenceAvailability.NotConfiguredForUser), windowsLock);

        var first = world.AskAsync(DeliveryService.ApprovalAction);
        await UntilAsync(() => windowsLock.Locks.Count == 1);
        var second = await world.AskAsync(UnanchoredHistory.AcceptAction);

        Assert.False(second.Approved);
        Assert.Equal(HelloTexts.ApprovalConfirmationPending, second.Comment);
        Assert.Equal(new Actor(ActorKind.System, ApprovalGate.SystemId), world.Log.Last(e => e.Kind == DecisionKind.ApprovalDenied).Actor);
        Assert.Single(windowsLock.Locks);

        unlock.SetResult(WindowsLockResult.UnlockedAfterRequest);
        var approved = await first;
        Assert.True(approved.Approved);
        Assert.Equal(ApprovalVerification.WindowsLock, approved.Verification);
    }

    /// <summary>The same with Windows Hello: one verification never confirms two approvals.</summary>
    [Fact]
    public async Task While_windows_hello_is_asked_a_second_approval_is_denied_and_says_another_is_pending()
    {
        var verified = new TaskCompletionSource<UserPresenceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hello = new FakeUserPresenceVerifier { Verify = (_, _) => verified.Task };
        using var world = new World(hello);

        var first = world.AskAsync(DeliveryService.ApprovalAction);
        await UntilAsync(() => hello.Messages.Count == 1);
        var second = await world.AskAsync(DeliveryService.ApprovalAction);

        Assert.False(second.Approved);
        Assert.Equal(HelloTexts.ApprovalConfirmationPending, second.Comment);
        Assert.Single(hello.Messages);

        verified.SetResult(UserPresenceResult.Verified);
        Assert.True((await first).Approved);

        // Free again: the next one is asked.
        hello.Verify = (_, _) => Task.FromResult(UserPresenceResult.Verified);
        Assert.True((await world.AskAsync(DeliveryService.ApprovalAction)).Approved);
        Assert.Equal(2, hello.Messages.Count);
    }

    /// <summary>A no is never confirmed: it asks neither Hello nor the lock (ADR-0037, decision 3).</summary>
    [Fact]
    public async Task A_no_to_an_action_of_the_list_asks_neither_hello_nor_the_lock_of_windows()
    {
        var windowsLock = new FakeWindowsLockConfirmation();
        using var world = new World(FakeUserPresenceVerifier.Without(UserPresenceAvailability.NotConfiguredForUser), windowsLock, approved: false);

        var decision = await world.AskAsync(DeliveryService.ApprovalAction);

        Assert.Equal((false, ApprovalVerification.Dialog), (decision.Approved, decision.Verification));
        Assert.Empty(windowsLock.Locks);
        Assert.Equal(new Actor(ActorKind.User, ApprovalPromptService.DecidedBy), world.Log[1].Actor);
    }
}
