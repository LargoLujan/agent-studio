using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Services;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Security;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Security.Approvals;
using AgentStudio.Tests.Debate.Fakes;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Desktop.Lock;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Domain.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Integration.Phase13;

/// <summary>
/// F13-16 (docs/17 §4.9, ADR-0053) across every path that approves something critical, over ONE container composed as the app
/// composes it (<see cref="LockServices.AddWindowsHelloAndLock"/>, so one <see cref="ReinforcedConfirmation"/> for all of them)
/// and the real settings store reading a file the test edits by hand: the approval gate (delivering), accepting a history as a
/// new origin and «Aprobar plan» with writers. Windows Hello, the lock of Windows and the dialog are doubles: nothing here asks
/// the real Windows Hello nor locks the session, and no <c>App</c> is created.
/// </summary>
public sealed class HelloBarrierTests
{
    private const string Project = "p-barrera";
    private const string Root = @"C:\proyectos\calc";

    public enum Via
    {
        Gate,
        History,
        Plan,
    }

    /// <summary>What a settings file edited by hand can say, and how the setting reads it (<c>null</c>: the file does not exist).</summary>
    public static TheoryData<string, string?> ManipulatedFiles => new()
    {
        { "the setting off", """{"windowsHelloForImportantApprovals":false}""" },
        { "the setting off under its old name", """{"windowsHelloForCriticalApprovals":false}""" },
        { "unknown keys", """{"disableHello":true,"presencePolicy":{"deliver-integration":"Never"},"allow":["Bash"],"windowsHelloForImportantApprovals":false}""" },
        { "a boolean as text", """{"windowsHelloForImportantApprovals":"false"}""" },
        { "a word that means nothing", """{"windowsHelloForImportantApprovals":"nunca"}""" },
        { "a number", """{"windowsHelloForImportantApprovals":0}""" },
        { "a null", """{"windowsHelloForImportantApprovals":null}""" },
        { "a repeated key, last one off", """{"windowsHelloForImportantApprovals":true,"windowsHelloForImportantApprovals":false}""" },
        { "a repeated key under its old name", """{"windowsHelloForCriticalApprovals":false,"windowsHelloForCriticalApprovals":false}""" },
        { "both names, the old one off", """{"windowsHelloForCriticalApprovals":false,"windowsHelloForImportantApprovals":"no"}""" },
        { "broken JSON", """{"windowsHelloForImportantApprovals":false""" },
        { "text that is not JSON", "windowsHelloForImportantApprovals=false" },
        { "an array", "[false]" },
        { "a bare null", "null" },
        { "an empty file", string.Empty },
        { "the file deleted", null },
    };

    private sealed class FakeDialog(bool approved) : IApprovalDialog
    {
        public Task<ApprovalAnswer?> ShowAsync(ApprovalDialogViewModel question, CancellationToken cancellationToken) =>
            Task.FromResult<ApprovalAnswer?>(new ApprovalAnswer(approved, null));
    }

    private sealed class ZeroIdle : IIdleInput
    {
        public TimeSpan IdleFor() => TimeSpan.Zero;
    }

    private sealed class MemoryLayoutStore : ICanvasLayoutStore
    {
        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => CanvasLayout.Empty;

        public void Save(string projectRoot, CanvasLayout layout)
        {
        }
    }

    /// <summary>The result of asking one path to approve: whether it did, the method its record carries, and what it says.</summary>
    private sealed record Outcome(bool Approved, ApprovalVerification? Method, string? Said);

    /// <summary>
    /// The three paths of the app over one confirmation: the gate over the prompt, «Aceptar este historial» of the session over
    /// the same prompt and a real recorder, and the document of the plan with writers. Everything the barrier decides comes from
    /// the container and the settings file in the temporary folder.
    /// </summary>
    private sealed class World : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly ServiceProvider _container;
        private readonly HistoryBackend _history = new();
        private OrchestrationSession? _session;
        private SessionContext? _historyContext;
        private OrchestratorDocumentViewModel? _document;

        public World(FakeUserPresenceVerifier hello, FakeWindowsLockConfirmation? windowsLock = null, bool approved = true)
        {
            Hello = hello;
            Paths = new AppDataPaths(_temp.Path);
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
            Prompt = new ApprovalPromptService(new FakeDialog(approved), Clock, new InlineSynchronizationContext(), Confirmation);
            Gate = new ApprovalGate(Prompt, new DecisionRecorder(Store, Clock), Clock, new ApprovalGateOptions());
            Engine = new FakeOrchestrator { Start = _ => new FakeRun(new RunSnapshot(new RunId("20260101-abc123"), RunPhase.Running, [], FakeRun.Healthy, 0)) };
        }

        public FakeTimeProvider Clock { get; } = new(T0);

        public AppDataPaths Paths { get; }

        public FakeUserPresenceVerifier Hello { get; }

        public ReinforcedConfirmation Confirmation { get; }

        public ApprovalPromptService Prompt { get; }

        public ApprovalGate Gate { get; }

        public FakeOrchestrator Engine { get; }

        public InMemoryDecisionLogStore Store { get; } = new();

        public IReadOnlyList<DecisionEntry> GateLog => Store.Of(Project);

        public IReadOnlyList<DecisionEntry> HistoryLog => _history.Entries;

        public OrchestratorDocumentViewModel Document => _document!;

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

        /// <summary>Gets the history to accept and the plan to approve ready, as the screens have them when the person clicks.</summary>
        public async Task PrepareAsync()
        {
            await _history.WriteUnanchoredAsync(2);
            _session = new OrchestrationSession(_history.On(Backends.Complete(new FakeOrchestrator())) with { Prompt = Prompt });
            _historyContext = await _session.OpenAsync(Root, CancellationToken.None);
            Assert.NotNull(_historyContext.Unanchored);

            _document = new OrchestratorDocumentViewModel(
                Root, new OrchestrationSession(Backends.Complete(Engine)), new MemoryLayoutStore(), Clock, new InlineSynchronizationContext(), Confirmation);
            await _document.OpenAsync();
            _document.RequestText = "Añadir Calc.Multiply";
            _document.Canvas.LoadTasks(PlanFixtures.Plan().Tasks, CanvasLayout.Empty);
            Assert.True(_document.ApproveCommand.CanExecute(null));
        }

        public async Task<Outcome> ApproveAsync(Via via, string gateAction = "deliver-integration")
        {
            switch (via)
            {
                case Via.Gate:
                    var decision = await Gate.RequestAsync(new ApprovalRequest(gateAction, "Lo que se pide.", RiskLevel.Critical, null, [], Project, "20261109-abc123"), TestContext.Current.CancellationToken);
                    return new Outcome(decision.Approved, decision.Verification, decision.Comment);
                case Via.History:
                    var accepted = await _session!.AcceptHistoryAsync(_historyContext!.Project!, _historyContext.Unanchored!, CancellationToken.None);
                    var granted = _history.Entries.LastOrDefault(e => e.Kind == DecisionKind.ApprovalGranted);
                    var method = granted is null ? (ApprovalVerification?)null : MethodOf(granted.PayloadJson);
                    return new Outcome(accepted, method, null);
                default:
                    await _document!.ApproveCommand.ExecuteAsync(null);
                    if (!_document.IsApproved)
                    {
                        return new Outcome(false, null, _document.ErrorMessage);
                    }

                    await _document.ExecuteCommand.ExecuteAsync(null);
                    return new Outcome(true, Engine.RunRequests.Single().PlanApproval.Verification, null);
            }
        }

        public void Dispose()
        {
            _container.Dispose();
            _temp.Dispose();
        }

        private static ApprovalVerification MethodOf(string payloadJson) =>
            Enum.GetValues<ApprovalVerification>().Single(v => payloadJson.Contains($"\"verification\":\"{v}\"", StringComparison.Ordinal));
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), what);
    }

    // ---- D-37 and D-38: no setting and no file turns the list off, on any path ----

    [Theory]
    [MemberData(nameof(ManipulatedFiles))]
    public async Task Every_path_asks_windows_hello_whatever_the_settings_file_says(string why, string? file)
    {
        foreach (var via in Enum.GetValues<Via>())
        {
            using var world = new World(FakeUserPresenceVerifier.Answering(UserPresenceResult.Verified));
            world.WriteSettings(file);
            await world.PrepareAsync();

            var outcome = await world.ApproveAsync(via);

            Assert.True(outcome.Approved, $"{why} · {via}: {outcome.Said}");
            Assert.Equal(ApprovalVerification.WindowsHello, outcome.Method);
            Assert.Single(world.Hello.Messages); // one verification for one approval
        }
    }

    [Theory]
    [MemberData(nameof(ManipulatedFiles))]
    public async Task With_any_settings_file_a_yes_windows_hello_does_not_confirm_approves_nothing_on_any_path(string why, string? file)
    {
        foreach (var via in Enum.GetValues<Via>())
        {
            using var world = new World(FakeUserPresenceVerifier.Answering(UserPresenceResult.Canceled));
            world.WriteSettings(file);
            await world.PrepareAsync();

            var outcome = await world.ApproveAsync(via);

            Assert.False(outcome.Approved, $"{why} · {via}");
            Assert.DoesNotContain(world.GateLog, e => e.Kind == DecisionKind.ApprovalGranted);
            Assert.DoesNotContain(world.HistoryLog, e => e.Kind == DecisionKind.ApprovalGranted); // the history stays unaccepted
            Assert.Empty(world.Engine.RunRequests);
        }
    }

    /// <summary>The gate asks Windows Hello for what the list does not name but is critical by its level, as the engine and the dialog do.</summary>
    [Theory]
    [InlineData("deliver-integration")]
    [InlineData("decision-log.accept-origin")]
    [InlineData("verification-profile.change")]
    [InlineData("some-action-nobody-listed-yet")]
    public async Task A_critical_action_of_the_gate_asks_windows_hello_even_with_the_setting_off_and_a_file_that_allows_it(string action)
    {
        using var world = new World(FakeUserPresenceVerifier.Answering(UserPresenceResult.Verified));
        world.WriteSettings("""{"windowsHelloForImportantApprovals":false,"disableHello":true,"approvals":{"allow":["Bash"]}}""");

        var outcome = await world.ApproveAsync(Via.Gate, action);

        Assert.True(outcome.Approved, outcome.Said);
        Assert.Equal(ApprovalVerification.WindowsHello, outcome.Method);
        Assert.Single(world.Hello.Messages);
    }

    /// <summary>
    /// The control: the setting still governs what is configurable, and a file that cannot be read never lowers it. Only a plain
    /// <c>false</c> (or a repeated key whose last value is <c>false</c>) turns Windows Hello off for a debate; any other thing
    /// falls back to the default, which is on.
    /// </summary>
    [Theory]
    [MemberData(nameof(ManipulatedFiles))]
    public async Task What_is_configurable_follows_the_setting_and_an_unreadable_file_never_lowers_it(string why, string? file)
    {
        // Only these say «off» in a way the store reads: the new name, the old name (migrated), a repeated key whose last value is off,
        // and the file of invented keys that also carries the plain setting.
        var turnsOff = file is """{"windowsHelloForImportantApprovals":false}""" or """{"windowsHelloForCriticalApprovals":false}""" or """{"windowsHelloForImportantApprovals":true,"windowsHelloForImportantApprovals":false}"""
            || (file is not null && file.StartsWith("""{"disableHello":true""", StringComparison.Ordinal));
        using var world = new World(FakeUserPresenceVerifier.Answering(UserPresenceResult.Verified));
        world.WriteSettings(file);

        var decision = await world.ApproveAsync(Via.Gate, "debate.open");

        Assert.True(decision.Approved, why);
        Assert.Equal(turnsOff ? ApprovalVerification.Dialog : ApprovalVerification.WindowsHello, decision.Method);
        Assert.Equal(turnsOff ? 0 : 1, world.Hello.Messages.Count);
    }

    // ---- D-40 (a): without Windows Hello, the lock of Windows, checked ----

    [Theory]
    [InlineData(UserPresenceAvailability.NotConfiguredForUser)]
    [InlineData(UserPresenceAvailability.DeviceNotPresent)]
    [InlineData(UserPresenceAvailability.DisabledByPolicy)]
    public async Task Without_windows_hello_every_path_locks_windows_and_its_record_says_windows_lock(UserPresenceAvailability absent)
    {
        foreach (var via in Enum.GetValues<Via>())
        {
            var windowsLock = FakeWindowsLockConfirmation.Answering(WindowsLockResult.UnlockedAfterRequest);
            using var world = new World(FakeUserPresenceVerifier.Without(absent), windowsLock);
            world.WriteSettings("""{"windowsHelloForImportantApprovals":false}""");
            await world.PrepareAsync();

            var outcome = await world.ApproveAsync(via);

            Assert.True(outcome.Approved, $"{via}: {outcome.Said}");
            Assert.Equal(ApprovalVerification.WindowsLock, outcome.Method);
            Assert.Equal([ReinforcedConfirmation.WindowsLockTimeout], windowsLock.Locks);
            Assert.Empty(world.Hello.Messages);
        }
    }

    [Theory]
    [InlineData(WindowsLockResult.NotVerified)]
    [InlineData(WindowsLockResult.TimedOut)]
    [InlineData(WindowsLockResult.LockFailed)]
    public async Task Without_hello_a_lock_the_session_does_not_confirm_approves_nothing_on_any_path(WindowsLockResult result)
    {
        foreach (var via in Enum.GetValues<Via>())
        {
            using var world = new World(FakeUserPresenceVerifier.Without(UserPresenceAvailability.NotConfiguredForUser), FakeWindowsLockConfirmation.Answering(result));
            await world.PrepareAsync();

            var outcome = await world.ApproveAsync(via);

            Assert.False(outcome.Approved, $"{via}");
            Assert.DoesNotContain(world.GateLog, e => e.Kind == DecisionKind.ApprovalGranted);
            Assert.DoesNotContain(world.HistoryLog, e => e.Kind == DecisionKind.ApprovalGranted);
            Assert.Empty(world.Engine.RunRequests);
        }
    }

    /// <summary>Decision 6 (b) as the fallback: Windows does not lock (a policy), so nothing is approved, and the reason says how to set up Hello.</summary>
    [Fact]
    public async Task When_windows_does_not_lock_the_gate_and_the_plan_say_how_to_set_up_windows_hello()
    {
        using var world = new World(FakeUserPresenceVerifier.Without(UserPresenceAvailability.NotConfiguredForUser), FakeWindowsLockConfirmation.Answering(WindowsLockResult.LockFailed));
        await world.PrepareAsync();

        var gate = await world.ApproveAsync(Via.Gate);
        var plan = await world.ApproveAsync(Via.Plan);

        // The gate's reason is the comment of the decision, which the log keeps in its fixed text; the plan's is the screen's.
        Assert.False(gate.Approved);
        Assert.EndsWith(OrchestratorProtocolTexts.SetUpHello, gate.Said, StringComparison.Ordinal);
        Assert.False(plan.Approved);
        Assert.EndsWith(HelloTexts.SetUpHello, plan.Said, StringComparison.Ordinal);
    }

    // ---- one confirmation at a time, across the three paths ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Three_simultaneous_approvals_are_never_approved_with_one_verification_or_one_unlock(bool withoutHello)
    {
        var verified = new TaskCompletionSource<UserPresenceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unlocked = new TaskCompletionSource<WindowsLockResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hello = withoutHello
            ? new FakeUserPresenceVerifier { Availability = _ => Task.FromResult(UserPresenceAvailability.NotConfiguredForUser), Verify = (_, _) => verified.Task }
            : new FakeUserPresenceVerifier { Verify = (_, _) => verified.Task };
        var windowsLock = new FakeWindowsLockConfirmation { Answer = (_, _) => unlocked.Task };
        using var world = new World(hello, windowsLock);
        world.WriteSettings("""{"windowsHelloForImportantApprovals":false}""");
        await world.PrepareAsync();

        var all = Enum.GetValues<Via>().Select(via => world.ApproveAsync(via)).ToList();
        await UntilAsync(() => withoutHello ? windowsLock.Locks.Count == 1 : hello.Messages.Count == 1, "the first confirmation never started");
        await UntilAsync(() => all.Count(t => t.IsCompleted) == 2, "the other two were not refused at once");

        // The two refused ones did not wait for anything, did not ask again and approved nothing.
        var refused = all.Where(t => t.IsCompleted).Select(t => t.Result).ToList();
        Assert.All(refused, o => Assert.False(o.Approved));
        Assert.Equal(withoutHello ? 0 : 1, hello.Messages.Count);
        Assert.Equal(withoutHello ? 1 : 0, windowsLock.Locks.Count);

        if (withoutHello)
        {
            unlocked.SetResult(WindowsLockResult.UnlockedAfterRequest);
        }
        else
        {
            verified.SetResult(UserPresenceResult.Verified);
        }

        var outcomes = await Task.WhenAll(all);
        var approved = Assert.Single(outcomes, o => o.Approved);
        Assert.Equal(withoutHello ? ApprovalVerification.WindowsLock : ApprovalVerification.WindowsHello, approved.Method);
        Assert.Equal(2, outcomes.Count(o => !o.Approved));
    }

    /// <summary>Freed, the next one is asked on its own: the one confirmation is not stuck.</summary>
    [Fact]
    public async Task Once_the_confirmation_ends_the_next_approval_asks_its_own_verification()
    {
        using var world = new World(FakeUserPresenceVerifier.Answering(UserPresenceResult.Verified));
        await world.PrepareAsync();

        Assert.True((await world.ApproveAsync(Via.Gate)).Approved);
        Assert.True((await world.ApproveAsync(Via.History)).Approved);
        Assert.True((await world.ApproveAsync(Via.Plan)).Approved);

        Assert.Equal(3, world.Hello.Messages.Count);
    }
}
