using System.Reflection;
using System.Runtime.Versioning;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Decisions;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/16 §4.5 (P0 of Phase 8): the contracts the packages WP1 to WP4 build against in parallel. These tests fail if one of
/// them drifts from what docs/16 and docs/04 §18 say.
/// </summary>
public sealed class Phase8ContractTests
{
    private static IEnumerable<string> MethodNames(Type type) => type.GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal);

    private static IEnumerable<Type> Parameters(Type type, string method) => type.GetMethod(method)!.GetParameters().Select(p => p.ParameterType);

    // ---- the class of the end of an attempt (docs/16 D-41, F8-1) ----

    /// <summary>An outcome built as the adapters of the earlier phases build it has no class: the one its status implies is all that is known.</summary>
    [Theory]
    [InlineData(AgentRunStatus.Succeeded, AttemptEndClass.Completed)]
    [InlineData(AgentRunStatus.Failed, AttemptEndClass.Other)]
    [InlineData(AgentRunStatus.Cancelled, AttemptEndClass.Cancelled)]
    [InlineData(AgentRunStatus.LimitReached, AttemptEndClass.AttemptBudget)]
    [InlineData(AgentRunStatus.RateLimited, AttemptEndClass.Other)]
    public void An_outcome_an_adapter_did_not_classify_has_the_class_its_status_implies(AgentRunStatus status, AttemptEndClass expected)
    {
        var outcome = new AgentRunOutcome(status, 0, null, null, "sess-1", null);

        Assert.Null(outcome.End);
        Assert.Equal(new AgentRunEnd(expected), outcome.Classified());
        Assert.Equal(new AgentRunEnd(expected), AgentRunEnd.Implied(status));
    }

    [Fact]
    public void An_outcome_an_adapter_classified_keeps_its_class_the_window_and_the_reset()
    {
        var resets = T0.AddHours(5);
        var outcome = new AgentRunOutcome(AgentRunStatus.RateLimited, 1, null, null, "sess-1", "límite", new AgentRunEnd(AttemptEndClass.WindowQuota, "five_hour", resets));

        Assert.Equal(new AgentRunEnd(AttemptEndClass.WindowQuota, "five_hour", resets), outcome.Classified());
        Assert.Equal(new AttemptEndSnapshot(AttemptEndClass.WindowQuota, "five_hour", resets), AttemptEndSnapshot.From(outcome.Classified()));
    }

    [Fact]
    public void A_rate_limit_event_names_its_window_only_when_the_cli_does()
    {
        var unnamed = new RateLimitSignaled(T0, "rejected", null);
        var named = new RateLimitSignaled(T0, "rejected", T0.AddHours(5), "seven_day");

        Assert.Null(unnamed.LimitType);
        Assert.Equal("seven_day", named.LimitType);
    }

    // ---- the options of the header of a run (D-36, D-37 and D-39) ----

    [Fact]
    public void A_run_has_no_consent_to_resume_by_itself_and_waits_with_the_engine_defaults_unless_the_plan_was_approved_otherwise()
    {
        var options = new RunOptions();

        Assert.Null(options.QuotaResetMargin);
        Assert.Null(options.MaxEstimatedQuotaWait);
        Assert.False(options.AutoResumeConsent);
        Assert.Equal(TimeSpan.FromMinutes(3), RunOptions.DefaultQuotaResetMargin);
        Assert.Equal(TimeSpan.FromMinutes(60), RunOptions.DefaultMaxEstimatedQuotaWait);

        // The six options of the earlier phases keep their places: a header built by position means what it meant.
        Assert.Equal(new RunOptions(), new RunOptions(3, 12, null, null, null, null));
    }

    /// <summary>
    /// The waits of a new run must make sense (<see cref="RunOptions.EnsureValid"/>), but a stored header with odd ones is still
    /// read as it was: it builds, and the engine uses the nearest sensible values (no margin, and a cap of a minute).
    /// </summary>
    [Fact]
    public void Odd_waits_are_refused_for_a_new_run_and_resolved_to_sensible_ones_for_a_stored_header()
    {
        var odd = new RunOptions(QuotaResetMargin: TimeSpan.FromMinutes(-2), MaxEstimatedQuotaWait: TimeSpan.Zero, AutoResumeConsent: true);
        var given = new RunOptions(QuotaResetMargin: TimeSpan.Zero, MaxEstimatedQuotaWait: TimeSpan.FromMinutes(15));
        var defaults = new RunOptions();

        Assert.Equal(TimeSpan.FromMinutes(1), RunOptions.MinEstimatedQuotaWait);
        Assert.Equal((TimeSpan.Zero, RunOptions.MinEstimatedQuotaWait), (odd.ResolvedQuotaResetMargin(), odd.ResolvedMaxEstimatedQuotaWait()));
        Assert.Equal((TimeSpan.Zero, TimeSpan.FromMinutes(15)), (given.ResolvedQuotaResetMargin(), given.ResolvedMaxEstimatedQuotaWait()));
        Assert.Equal((RunOptions.DefaultQuotaResetMargin, RunOptions.DefaultMaxEstimatedQuotaWait), (defaults.ResolvedQuotaResetMargin(), defaults.ResolvedMaxEstimatedQuotaWait()));
        Assert.Contains("QuotaResetMargin", Assert.Throws<ArgumentException>(() => (odd with { MaxEstimatedQuotaWait = null }).EnsureValid()).Message, StringComparison.Ordinal);
        Assert.Contains("MaxEstimatedQuotaWait", Assert.Throws<ArgumentException>(() => (odd with { QuotaResetMargin = null }).EnsureValid()).Message, StringComparison.Ordinal);
        given.EnsureValid();
        defaults.EnsureValid();
    }

    // ---- the presence of the user and the approvals (D-13, F8-10 and F8-11) ----

    [Fact]
    public void An_approval_built_as_before_was_given_in_the_dialog()
    {
        var decision = new ApprovalDecision(true, "user", T0, "ok");

        Assert.Equal(ApprovalVerification.Dialog, decision.Verification);
        Assert.Equal(ApprovalVerification.WindowsHello, (decision with { Verification = ApprovalVerification.WindowsHello }).Verification);
    }

    [Fact]
    public void The_verifier_of_the_presence_of_the_user_checks_without_asking_and_asks_with_a_message()
    {
        Assert.Equal(["CheckAvailabilityAsync", "VerifyAsync"], MethodNames(typeof(IUserPresenceVerifier)));
        Assert.Equal([typeof(CancellationToken)], Parameters(typeof(IUserPresenceVerifier), nameof(IUserPresenceVerifier.CheckAvailabilityAsync)));
        Assert.Equal([typeof(string), typeof(CancellationToken)], Parameters(typeof(IUserPresenceVerifier), nameof(IUserPresenceVerifier.VerifyAsync)));
        Assert.Equal(typeof(Task<UserPresenceAvailability>), typeof(IUserPresenceVerifier).GetMethod(nameof(IUserPresenceVerifier.CheckAvailabilityAsync))!.ReturnType);
        Assert.Equal(typeof(Task<UserPresenceResult>), typeof(IUserPresenceVerifier).GetMethod(nameof(IUserPresenceVerifier.VerifyAsync))!.ReturnType);
    }

    /// <summary>
    /// The availability and the results mirror those of Windows one to one, names and values, so the implementation translates
    /// them without guessing: only a result ever reaches the app, never biometric data nor the PIN.
    /// </summary>
    [Fact]
    public void The_availability_and_the_results_of_the_verifier_are_those_of_windows_one_to_one()
    {
        Assert.Equal(Enum.GetNames<Windows.Security.Credentials.UI.UserConsentVerifierAvailability>(), Enum.GetNames<UserPresenceAvailability>());
        Assert.Equal(
            Enum.GetValues<Windows.Security.Credentials.UI.UserConsentVerifierAvailability>().Select(v => (int)v),
            Enum.GetValues<UserPresenceAvailability>().Select(v => (int)v));
        Assert.Equal(Enum.GetNames<Windows.Security.Credentials.UI.UserConsentVerificationResult>(), Enum.GetNames<UserPresenceResult>());
        Assert.Equal(
            Enum.GetValues<Windows.Security.Credentials.UI.UserConsentVerificationResult>().Select(v => (int)v),
            Enum.GetValues<UserPresenceResult>().Select(v => (int)v));
    }

    // ---- the power of the computer (D-40, F8-5) ----

    [Fact]
    public void Power_transitions_are_seen_as_events_and_keeping_awake_is_held_and_released()
    {
        Assert.Equal(["Suspending", "Resumed"], Enum.GetNames<PowerTransition>());
        Assert.Equal(typeof(EventHandler<PowerEvent>), typeof(IPowerEvents).GetEvent(nameof(IPowerEvents.Changed))!.EventHandlerType);
        Assert.Equal(["Hold", "get_IsHeld"], MethodNames(typeof(IKeepAwake)));
        Assert.Equal(typeof(IDisposable), typeof(IKeepAwake).GetMethod(nameof(IKeepAwake.Hold))!.ReturnType);
        Assert.Equal([typeof(string)], Parameters(typeof(IKeepAwake), nameof(IKeepAwake.Hold)));
        Assert.Null(new PowerEvent(PowerTransition.Suspending, T0).Slept);
    }

    // ---- the log (D-16, D-38 and D-39) ----

    [Fact]
    public void The_kinds_of_phase_8_are_in_the_log_and_read_their_payloads()
    {
        Assert.Equal(typeof(RunExitChosenPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.RunExitChosen));
        Assert.Equal(typeof(RunDispatchChangedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.RunDispatchChanged));
        Assert.Equal(typeof(AccessCheckedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.AccessChecked));
    }

    // ---- the target framework (docs/16 D-13; ADR-0001, ADR-0037) ----

    /// <summary>
    /// Only the desktop app and this test project target a version of the Windows SDK, the one whose interop lets a desktop
    /// window ask Windows Hello (build 22000). The domain, the contracts and the modules stay in <c>net10.0</c>.
    /// </summary>
    [Fact]
    public void Only_the_desktop_and_its_tests_target_the_windows_sdk_of_build_22000()
    {
        var desktop = typeof(AgentStudio.Desktop.App).Assembly;
        var tests = typeof(Phase8ContractTests).Assembly;
        var application = typeof(IOrchestrator).Assembly;

        Assert.Equal("Windows10.0.22000.0", desktop.GetCustomAttribute<TargetPlatformAttribute>()?.PlatformName);
        Assert.Equal("Windows10.0.22000.0", desktop.GetCustomAttribute<SupportedOSPlatformAttribute>()?.PlatformName);
        Assert.Equal("Windows10.0.22000.0", tests.GetCustomAttribute<TargetPlatformAttribute>()?.PlatformName);
        Assert.Equal(".NETCoreApp,Version=v10.0", application.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName);
        Assert.Null(application.GetCustomAttribute<TargetPlatformAttribute>());
        Assert.DoesNotContain(application.GetReferencedAssemblies().Select(a => a.Name), name => name is "WinRT.Runtime" or "Microsoft.Windows.SDK.NET");
    }

    /// <summary>
    /// The projection of that SDK brings the interop class a desktop app calls with its window (docs/16 D-13): only its shape
    /// is checked here, Windows Hello is never asked by a test.
    /// </summary>
    [Fact]
    public void The_windows_sdk_projection_brings_the_interop_that_asks_windows_hello_for_a_window()
    {
        var request = typeof(Windows.Security.Credentials.UI.UserConsentVerifierInterop).GetMethod("RequestVerificationForWindowAsync");

        Assert.NotNull(request);
        Assert.True(request.IsStatic);
        Assert.Equal([typeof(nint), typeof(string)], request.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(
            typeof(Windows.Foundation.IAsyncOperation<Windows.Security.Credentials.UI.UserConsentVerificationResult>),
            request.ReturnType);
    }
}
