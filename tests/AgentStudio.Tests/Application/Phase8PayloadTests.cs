using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/16 §4.5 (P0 of Phase 8): the end of an attempt with its class (<c>AgentRunFinished</c> version 3, D-41), the choice
/// made when the app is left with a run going (<c>RunExitChosen</c>, D-16), a pause or a resume of the dispatch of a run
/// (<c>RunDispatchChanged</c>, D-39), the check before going on after a wait (<c>AccessChecked</c>, D-38 and F8-3) and how the
/// person who answered an approval was confirmed (<c>ApprovalGranted</c> and <c>ApprovalDenied</c> version 2, D-13). The log
/// hashes the text of every payload, so each is pinned here as text; versions 1 and 2 are still read and written back byte
/// for byte; and the hash of an entry that carries a new one is computed by an independent implementation of the canonical
/// form of the log.
/// </summary>
public sealed class Phase8PayloadTests
{
    private const string Run = "20261015-a1b2c3";

    private static readonly DateTimeOffset Resets = new(2026, 1, 1, 15, 0, 0, TimeSpan.Zero);

    private static readonly RunExitChosenPayload ExitChosen = new(RunExitChoice.PauseAndExitWhenIdle, "user") { RunId = Run };

    private static readonly RunDispatchChangedPayload DispatchPaused = new(RunDispatchChange.Paused, "user", T0.AddMinutes(2)) { RunId = Run };

    /// <summary>The read of the Codex quota after a wait, with the opt-in: the window is still reached, and when it resets.</summary>
    private static readonly AccessCheckedPayload QuotaStillLimited = new(
        Tid("T1"), ProviderId.CodexCli, AccessCheckSource.QuotaRead, AccessCheckResult.StillLimited, T0.AddMinutes(1), Resets)
    {
        RunId = Run,
    };

    /// <summary>A run that was waiting for quota when the app closed, resumed by itself after the restart by the consent of its header.</summary>
    private static readonly RunResumedPayload ResumedByConsent = new(RunResumeCause.ConsentAfterQuotaWait, "agent-studio", T0.AddMinutes(4), new string('a', 64)) { RunId = Run };

    /// <summary>One sample of every payload type Phase 8 adds; <see cref="DecisionPayloadTests"/> adds them to the samples of every payload.</summary>
    internal static readonly DecisionPayload[] Samples = [ExitChosen, DispatchPaused, QuotaStillLimited, ResumedByConsent];

    /// <summary>A Claude attempt that ran out of its five-hour window, as version 3 records it.</summary>
    private static readonly AgentRunFinishedPayload WindowQuotaEnd = new(
        Tid("T1"), 2, AttemptOutcome.RateLimited, 1, "sess-1", "Límite de uso", null, null,
        LaunchedAt: T0.AddSeconds(3), Turns: 1, End: new AttemptEndSnapshot(AttemptEndClass.WindowQuota, "five_hour", Resets))
    {
        RunId = Run,
        V = AgentRunFinishedPayload.EndClassVersion,
    };

    /// <summary>The text version 2 writes for the end of an attempt (no class): it never changes.</summary>
    private const string FinishedV2 =
        "{\"task\":\"T1\",\"attempt\":1,\"status\":\"Failed\",\"exitCode\":1,\"sessionId\":\"sess-1\",\"error\":\"boom\","
        + "\"launchedAt\":\"2026-01-01T10:00:03+00:00\",\"turns\":3,\"v\":2,\"runId\":\"20261015-a1b2c3\"}";

    // ---- AgentRunFinished version 3: the class of the end (D-41, F8-1) ----

    [Fact]
    public void The_end_of_an_attempt_is_written_in_version_3_with_its_class_the_window_that_ran_out_and_when_it_resets()
    {
        var json = DecisionPayloadSerializer.Serialize(WindowQuotaEnd);

        Assert.Equal(
            "{\"task\":\"T1\",\"attempt\":2,\"status\":\"RateLimited\",\"exitCode\":1,\"sessionId\":\"sess-1\",\"error\":\"Límite de uso\","
            + "\"launchedAt\":\"2026-01-01T10:00:03+00:00\",\"turns\":1,"
            + "\"end\":{\"class\":\"WindowQuota\",\"limitType\":\"five_hour\",\"resetsAt\":\"2026-01-01T15:00:00+00:00\"},"
            + "\"v\":3,\"runId\":\"20261015-a1b2c3\"}",
            json);
        Assert.Equal(3, AgentRunFinishedPayload.EndClassVersion);
        Assert.True(WindowQuotaEnd.RecordsEndClass());
        Assert.True(WindowQuotaEnd.RecordsUsageWithOrigins());
    }

    [Fact]
    public void A_version_3_end_survives_a_round_trip_with_its_class()
    {
        var json = DecisionPayloadSerializer.Serialize(WindowQuotaEnd);

        var read = DecisionPayloadSerializer.Deserialize<AgentRunFinishedPayload>(json);

        Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
        Assert.Equal(new AttemptEndSnapshot(AttemptEndClass.WindowQuota, "five_hour", Resets), read.End);
        Assert.Equal(read.End, read.ResolvedEnd());
        Assert.Equal(new AgentRunEnd(AttemptEndClass.WindowQuota, "five_hour", Resets), read.End!.ToEnd());
    }

    [Fact]
    public void A_budget_names_which_one_ended_the_attempt_and_an_unknown_window_or_reset_is_left_out()
    {
        var turns = WindowQuotaEnd with { Status = AttemptOutcome.LimitReached, End = AttemptEndSnapshot.From(new AgentRunEnd(AttemptEndClass.AttemptBudget, Budget: AttemptBudgetKind.Turns)) };
        var codex = WindowQuotaEnd with { End = AttemptEndSnapshot.From(new AgentRunEnd(AttemptEndClass.WindowQuota)) };

        Assert.Contains(",\"end\":{\"class\":\"AttemptBudget\",\"budget\":\"Turns\"},", DecisionPayloadSerializer.Serialize(turns), StringComparison.Ordinal);
        Assert.Contains(",\"end\":{\"class\":\"WindowQuota\"},", DecisionPayloadSerializer.Serialize(codex), StringComparison.Ordinal);
    }

    [Fact]
    public void A_version_2_end_is_read_and_written_back_byte_for_byte_and_its_class_is_the_one_its_status_implies()
    {
        var read = DecisionPayloadSerializer.Deserialize<AgentRunFinishedPayload>(FinishedV2);

        Assert.Equal(FinishedV2, DecisionPayloadSerializer.Serialize(read));
        Assert.Equal(2, read.V);
        Assert.Null(read.End);
        Assert.False(read.RecordsEndClass());
        Assert.True(read.RecordsUsageWithOrigins());
        Assert.Equal(new AttemptEndSnapshot(AttemptEndClass.Other), read.ResolvedEnd());
    }

    /// <summary>
    /// An end recorded before version 3 has no class: the one its status alone implies is all that is known. A rate limit of
    /// version 2 could be a window or a throttling, so it is not recognised (Other): the status still says it was a limit.
    /// </summary>
    [Theory]
    [InlineData(AttemptOutcome.Succeeded, AttemptEndClass.Completed)]
    [InlineData(AttemptOutcome.Failed, AttemptEndClass.Other)]
    [InlineData(AttemptOutcome.Cancelled, AttemptEndClass.Cancelled)]
    [InlineData(AttemptOutcome.LimitReached, AttemptEndClass.AttemptBudget)]
    [InlineData(AttemptOutcome.RateLimited, AttemptEndClass.Other)]
    [InlineData(AttemptOutcome.Interrupted, AttemptEndClass.Interrupted)]
    public void An_end_without_a_class_resolves_to_the_one_its_status_implies(AttemptOutcome status, AttemptEndClass expected)
    {
        var v1 = new AgentRunFinishedPayload(Tid("T1"), 1, status, null, null, null, null, null) { RunId = Run };

        Assert.Equal(new AttemptEndSnapshot(expected), v1.ResolvedEnd());
        Assert.Equal(new AttemptEndSnapshot(expected), AttemptEndSnapshot.ImpliedBy(status));
        Assert.Null(v1.ResolvedEnd().Budget);
    }

    [Fact]
    public void Every_outcome_of_an_attempt_implies_a_class()
    {
        foreach (var status in Enum.GetValues<AttemptOutcome>())
        {
            Assert.True(Enum.IsDefined(AttemptEndSnapshot.ImpliedBy(status).Class));
        }
    }

    [Fact]
    public void The_classes_of_the_end_are_the_documented_ones_and_a_budget_is_turns_cost_or_time()
    {
        Assert.Equal(
            ["Completed", "WindowQuota", "RateOrOverload", "ContextExhausted", "Network", "SignedOut", "AttemptBudget", "Interrupted", "Cancelled", "Other"],
            Enum.GetNames<AttemptEndClass>());
        Assert.Equal(["Turns", "Cost", "Time"], Enum.GetNames<AttemptBudgetKind>());
    }

    // ---- RunExitChosen: the managed exit (D-16, F8-9) ----

    [Fact]
    public void The_choice_made_when_leaving_the_app_is_written_in_one_fixed_text_and_read_back_byte_for_byte()
    {
        var json = DecisionPayloadSerializer.Serialize(ExitChosen);

        Assert.Equal("{\"choice\":\"PauseAndExitWhenIdle\",\"decidedBy\":\"user\",\"v\":1,\"runId\":\"20261015-a1b2c3\"}", json);
        var read = DecisionPayloadSerializer.Deserialize<RunExitChosenPayload>(json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
        Assert.Equal(typeof(RunExitChosenPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.RunExitChosen));
        Assert.Equal(["PauseAndExitWhenIdle", "ExitNow", "SessionEnded"], Enum.GetNames<RunExitChoice>());
    }

    /// <summary>
    /// D-39: a run with the consent of its header resumes by itself only after an exit nobody chose. What the person chose
    /// (pausing and leaving, or leaving it interrupted) is what they wanted: it never resumes by itself.
    /// </summary>
    [Theory]
    [InlineData(RunExitChoice.PauseAndExitWhenIdle, false)]
    [InlineData(RunExitChoice.ExitNow, false)]
    [InlineData(RunExitChoice.SessionEnded, true)]
    public void Only_an_exit_nobody_chose_lets_a_run_resume_by_itself(RunExitChoice choice, bool resumes)
    {
        Assert.Equal(resumes, new RunExitChosenPayload(choice, "user") { RunId = Run }.AllowsAutomaticResume());
    }

    /// <summary>The kinds are appended, never inserted: the hash covers the name of the kind, so no stored hash changes.</summary>
    [Fact]
    public void The_kind_of_the_managed_exit_is_appended_after_every_kind_of_the_earlier_phases()
    {
        Assert.Equal(
            [
                "PlanCreated", "PlanRevised", "TaskCreated", "TaskAssigned", "TaskStatusChanged", "InstructionsIssued", "ReviewRequested", "ReviewSkipped",
                "ApprovalRequested", "ApprovalGranted", "ApprovalDenied", "PermissionDenied", "EvidenceRegistered", "IntegrationPerformed", "VerdictIssued",
                "AuditAlertRaised", "ReplanRequested", "Correction", "RunStarted", "RunFinished", "AgentRunStarted", "AgentRunFinished", "ConditionEvaluated",
                "WorktreeCreated", "WorktreeRemoved", "MemoryRecorded", "MemoryReviewed", "DebateOpened", "DebateContributionRecorded", "DebateRoundClosed",
                "DebateClosed", "VerdictChallenged", "ProjectAuditStarted", "ProjectAuditFinished", "RunExitChosen", "RunDispatchChanged", "AccessChecked",
                "RunResumed",
                // Phase 12 appends after the Phase 8 kinds.
                "JournalTrustChanged",
                // Phase 13 appends after the Phase 12 kind.
                "QuestionAsked", "QuestionAnswered", "ProjectSetupApplied",
                // Phase 14 appends after the Phase 13 kinds.
                "ClientDocumentExported",
            ],
            Enum.GetNames<DecisionKind>());
    }

    // ---- RunDispatchChanged: the person pauses or resumes the dispatch of a run (D-39) ----

    [Fact]
    public void A_change_of_the_dispatch_is_written_in_one_fixed_text_and_read_back_byte_for_byte()
    {
        var json = DecisionPayloadSerializer.Serialize(DispatchPaused);

        Assert.Equal("{\"change\":\"Paused\",\"decidedBy\":\"user\",\"at\":\"2026-01-01T10:02:00+00:00\",\"v\":1,\"runId\":\"20261015-a1b2c3\"}", json);
        var read = DecisionPayloadSerializer.Deserialize<RunDispatchChangedPayload>(json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
        Assert.Equal((RunDispatchChange.Paused, "user", T0.AddMinutes(2)), (read.Change, read.DecidedBy, read.At));
        Assert.Equal(typeof(RunDispatchChangedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.RunDispatchChanged));
        Assert.Equal(["Paused", "Resumed"], Enum.GetNames<RunDispatchChange>());
    }

    /// <summary>
    /// D-39: as the last change of the dispatch of its run, a pause keeps the run from resuming by itself after a close nobody
    /// asked for, whatever its consent says; a resume lets the consent decide again.
    /// </summary>
    [Theory]
    [InlineData(RunDispatchChange.Paused, false)]
    [InlineData(RunDispatchChange.Resumed, true)]
    public void Only_a_resumed_dispatch_lets_a_run_resume_by_itself(RunDispatchChange change, bool resumes)
    {
        Assert.Equal(resumes, new RunDispatchChangedPayload(change, "user", T0) { RunId = Run }.AllowsAutomaticResume());
    }

    // ---- AccessChecked: the check before going on after a wait (D-38, F8-3) ----

    [Fact]
    public void A_check_before_going_on_is_written_in_one_fixed_text_and_read_back_byte_for_byte()
    {
        var json = DecisionPayloadSerializer.Serialize(QuotaStillLimited);

        Assert.Equal(
            "{\"task\":\"T1\",\"provider\":\"codex-cli\",\"source\":\"QuotaRead\",\"result\":\"StillLimited\","
            + "\"checkedAt\":\"2026-01-01T10:01:00+00:00\",\"resetsAt\":\"2026-01-01T15:00:00+00:00\",\"v\":1,\"runId\":\"20261015-a1b2c3\"}",
            json);
        var read = DecisionPayloadSerializer.Deserialize<AccessCheckedPayload>(json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
        Assert.Equal((Tid("T1"), ProviderId.CodexCli, AccessCheckSource.QuotaRead, AccessCheckResult.StillLimited), (read.Task!.Value, read.Provider, read.Source, read.Result));
        Assert.Equal((T0.AddMinutes(1), Resets), (read.CheckedAt, read.ResetsAt!.Value));
        Assert.Equal(typeof(AccessCheckedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.AccessChecked));
    }

    /// <summary>A session probe of a provider, before any task goes on: no task and no reset, so neither is written.</summary>
    [Fact]
    public void A_session_probe_of_a_provider_names_no_task_and_no_reset()
    {
        var probe = new AccessCheckedPayload(null, ProviderId.ClaudeCode, AccessCheckSource.SessionProbe, AccessCheckResult.SignedOut, T0) { RunId = Run };

        var json = DecisionPayloadSerializer.Serialize(probe);

        Assert.Equal(
            "{\"provider\":\"claude-code\",\"source\":\"SessionProbe\",\"result\":\"SignedOut\",\"checkedAt\":\"2026-01-01T10:00:00+00:00\",\"v\":1,\"runId\":\"20261015-a1b2c3\"}",
            json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(DecisionPayloadSerializer.Deserialize<AccessCheckedPayload>(json)));
    }

    /// <summary>Only the allowed sources (docs/16 D-38): the session probes of the CLIs and, in Codex with the opt-in, the read of its quota.</summary>
    [Fact]
    public void The_sources_and_the_results_of_a_check_are_the_documented_ones()
    {
        Assert.Equal(["SessionProbe", "QuotaRead"], Enum.GetNames<AccessCheckSource>());
        Assert.Equal(["Ready", "SignedOut", "StillLimited", "NotOptedIn", "NotExposed", "Missing"], Enum.GetNames<AccessCheckResult>());
    }

    // ---- RunResumed: a run resumed after a restart, and why (D-39, F8-4; added by WP1) ----

    [Fact]
    public void A_resume_by_consent_is_written_in_one_fixed_text_and_read_back_byte_for_byte()
    {
        var json = DecisionPayloadSerializer.Serialize(ResumedByConsent);

        Assert.Equal(
            "{\"cause\":\"ConsentAfterQuotaWait\",\"decidedBy\":\"agent-studio\",\"at\":\"2026-01-01T10:04:00+00:00\","
            + "\"consentHeaderHash\":\"" + new string('a', 64) + "\",\"v\":1,\"runId\":\"20261015-a1b2c3\"}",
            json);
        var read = DecisionPayloadSerializer.Deserialize<RunResumedPayload>(json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
        Assert.Equal((RunResumeCause.ConsentAfterQuotaWait, "agent-studio", T0.AddMinutes(4), new string('a', 64)), (read.Cause, read.DecidedBy, read.At, read.ConsentHeaderHash));
        Assert.True(read.ByConsent());
        Assert.Equal(typeof(RunResumedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.RunResumed));
    }

    /// <summary>A resume a person asked for: no consent, so no header is named, and nothing is written for it.</summary>
    [Fact]
    public void A_resume_a_person_asked_for_names_no_header()
    {
        var asked = new RunResumedPayload(RunResumeCause.UserRequested, "user", T0) { RunId = Run };

        var json = DecisionPayloadSerializer.Serialize(asked);

        Assert.Equal("{\"cause\":\"UserRequested\",\"decidedBy\":\"user\",\"at\":\"2026-01-01T10:00:00+00:00\",\"v\":1,\"runId\":\"20261015-a1b2c3\"}", json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(DecisionPayloadSerializer.Deserialize<RunResumedPayload>(json)));
        Assert.False(asked.ByConsent());
    }

    /// <summary>
    /// D-39: a person's resume, or one by the consent of the header after a restart of the app, told apart by what the run
    /// was cut by: a wait for quota, a close nobody asked for, or the end of the session of Windows.
    /// </summary>
    [Fact]
    public void The_causes_of_a_resume_are_the_documented_ones()
    {
        Assert.Equal(["UserRequested", "ConsentAfterQuotaWait", "ConsentAfterUnrequestedClose", "ConsentAfterSessionEnded"], Enum.GetNames<RunResumeCause>());
    }

    /// <summary>
    /// The hash of a <c>RunResumed</c> entry against the independent implementation of the canonical form below and a
    /// PowerShell script outside the repository, which gave the literal hash.
    /// </summary>
    [Fact]
    public void The_entry_of_a_resume_hashes_as_the_canonical_form_of_the_log_says()
    {
        var log = new DecisionLog();
        var resume = DecisionPayloadSerializer.Serialize(ResumedByConsent);
        const string Summary = "Reanudada sola por el consentimiento de su cabecera: esperaba cuota";

        var resumed = log.Append(T0.AddMinutes(4), new Actor(ActorKind.System, "agent-studio"), DecisionKind.RunResumed, Summary, resume);

        Assert.Equal(Independent(1, T0.AddMinutes(4), "System", "agent-studio", "RunResumed", Summary, resume, DecisionLog.GenesisHash), resumed.Hash);
        Assert.Equal("06878661312237a7fc5feaca2230045d72e00211de41cb35a6560ca9b640728e", resumed.Hash);
        Assert.True(DecisionLog.Load(log.Entries).Verify().IsValid);
    }

    // ---- ApprovalGranted and ApprovalDenied version 2: how the person was confirmed (D-13, F8-11) ----

    [Fact]
    public void An_answer_of_version_1_is_still_written_as_it_was_and_was_given_in_the_dialog()
    {
        const string V1 = "{\"action\":\"merge\",\"approved\":true,\"decidedBy\":\"manuel\",\"comment\":\"ok\",\"v\":1,\"runId\":\"20261015-a1b2c3\"}";

        var read = DecisionPayloadSerializer.Deserialize<ApprovalAnsweredPayload>(V1);

        Assert.Equal(V1, DecisionPayloadSerializer.Serialize(read));
        Assert.Equal(V1, DecisionPayloadSerializer.Serialize(new ApprovalAnsweredPayload("merge", true, "manuel", "ok", null) { RunId = Run }));
        Assert.Null(read.Verification);
        Assert.False(read.RecordsVerification());
        Assert.Equal(ApprovalVerification.Dialog, read.ResolvedVerification());
    }

    [Fact]
    public void An_answer_of_version_2_records_how_the_person_was_confirmed()
    {
        var granted = new ApprovalAnsweredPayload("delivery", true, "user", "Entregar en main", Tid("T9"), ApprovalVerification.WindowsHello)
        {
            RunId = Run,
            V = ApprovalAnsweredPayload.VerificationVersion,
        };

        var json = DecisionPayloadSerializer.Serialize(granted);

        Assert.Equal(
            "{\"action\":\"delivery\",\"approved\":true,\"decidedBy\":\"user\",\"comment\":\"Entregar en main\",\"task\":\"T9\",\"verification\":\"WindowsHello\",\"v\":2,\"runId\":\"20261015-a1b2c3\"}",
            json);
        var read = DecisionPayloadSerializer.Deserialize<ApprovalAnsweredPayload>(json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
        Assert.True(read.RecordsVerification());
        Assert.Equal(ApprovalVerification.WindowsHello, read.ResolvedVerification());
        Assert.Equal(2, ApprovalAnsweredPayload.VerificationVersion);
        // Phase 13 (docs/17 D-40 (a)) appends the verified lock of Windows.
        Assert.Equal(["Dialog", "WindowsHello", "WindowsHelloUnavailable", "WindowsLock"], Enum.GetNames<ApprovalVerification>());
    }

    // ---- the hash of an entry, by an independent implementation of the canonical form ----

    /// <summary>
    /// The hash of the entries carrying a Phase 8 payload, against an independent implementation of the canonical form of
    /// the log (every field written as its length, a colon, the value and a semicolon; SHA-256 of the UTF-8 text): the one
    /// below, and a script outside the repository that gave the literal hashes. A change of the name of a kind, of the text of
    /// a payload or of the canonical form fails here.
    /// </summary>
    [Fact]
    public void The_entries_of_phase_8_hash_as_the_canonical_form_of_the_log_says()
    {
        var actor = new Actor(ActorKind.System, "agent-studio");
        var user = new Actor(ActorKind.User, "user");
        var log = new DecisionLog();
        var end = DecisionPayloadSerializer.Serialize(WindowQuotaEnd);
        var check = DecisionPayloadSerializer.Serialize(QuotaStillLimited);
        var pause = DecisionPayloadSerializer.Serialize(DispatchPaused);
        var exit = DecisionPayloadSerializer.Serialize(ExitChosen);

        var finished = log.Append(T0, actor, DecisionKind.AgentRunFinished, "Termina ejecutor para T1, intento 2: RateLimited", end);
        var checkedAccess = log.Append(T0.AddMinutes(1), actor, DecisionKind.AccessChecked, "Comprobada la cuota de codex-cli antes de seguir: StillLimited", check);
        var paused = log.Append(T0.AddMinutes(2), user, DecisionKind.RunDispatchChanged, "Despacho en pausa", pause);
        var chosen = log.Append(T0.AddMinutes(3), user, DecisionKind.RunExitChosen, "Pausar y salir cuando terminen los intentos en curso", exit);

        Assert.Equal(Independent(1, T0, "System", "agent-studio", "AgentRunFinished", "Termina ejecutor para T1, intento 2: RateLimited", end, DecisionLog.GenesisHash), finished.Hash);
        Assert.Equal(Independent(2, T0.AddMinutes(1), "System", "agent-studio", "AccessChecked", "Comprobada la cuota de codex-cli antes de seguir: StillLimited", check, finished.Hash), checkedAccess.Hash);
        Assert.Equal(Independent(3, T0.AddMinutes(2), "User", "user", "RunDispatchChanged", "Despacho en pausa", pause, checkedAccess.Hash), paused.Hash);
        Assert.Equal(Independent(4, T0.AddMinutes(3), "User", "user", "RunExitChosen", "Pausar y salir cuando terminen los intentos en curso", exit, paused.Hash), chosen.Hash);
        Assert.Equal(
            [
                "3a0e459d1d9723003883cde4d394daa3734c5649ec33216d8a5afb184bbeba27",
                "45598290e854dbbf2190274fbf34e7cae67ce5a0534d4122541a5aa75e8feec1",
                "c398816591edfcbca126292cceb8729da69fa2586b90ccd53f2bbd00fa679e0b",
                "9c7a120f3719024b01776f87214a4fc2c44319c373ed1600cf5677871189c3b0",
            ],
            log.Entries.Select(e => e.Hash));
        Assert.True(DecisionLog.Load(log.Entries).Verify().IsValid);
    }

    private static string Independent(long sequence, DateTimeOffset at, string actorKind, string actorId, string kind, string summary, string payload, string previous)
    {
        var text = new StringBuilder();
        foreach (var field in new[] { sequence.ToString(CultureInfo.InvariantCulture), at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), actorKind, actorId, kind, summary, payload, string.Empty, previous })
        {
            text.Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field).Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
