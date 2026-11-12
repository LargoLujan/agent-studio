using AgentStudio.Application.Localization;
using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// How long a task its provider limited waits, and whether it waits by itself (F8-2; docs/16 D-36 and D-37, ADR-0034).
/// With the consent of its header (<c>AutoResumeConsent</c>), the task waits for the reset its provider announced plus the
/// margin, however long that is; without a reset time the waits are progressive, capped, and the system estimates them.
/// Without the consent nothing changes from before Phase 8: until the reset, or the default cooldown, and by itself only up
/// to <c>MaxAutoWaitForQuota</c>. Waiting is all the engine does: the task never goes to another provider, account or model.
/// </summary>
internal static class QuotaWaitPolicy
{
    /// <summary>The waits without a reset time, in order: a task's n-th in a row waits the n-th step, and the last from then on (D-37).</summary>
    public static readonly IReadOnlyList<TimeSpan> EstimatedSteps =
        [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(60)];

    /// <summary>
    /// The waits of a provider that is overloaded or limits the rate (class <c>RateOrOverload</c>) without saying until when:
    /// short and progressive, in the same session (D-41).
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> ShortSteps =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(15)];

    /// <summary>
    /// The reset the provider announced, <paramref name="resetsAt"/>, when it is still ahead at <paramref name="now"/> (with the
    /// margin, under the consent); null when there is none or it already passed (a window read stale, or a clock that differs
    /// from the provider's). A reset that passed is no reset to wait for: waiting until it would be no wait at all, and the
    /// check after it would come at once, and again.
    /// </summary>
    public static DateTimeOffset? Announced(RunContext run, DateTimeOffset now, DateTimeOffset? resetsAt) =>
        resetsAt is { } reset && (run.Options.AutoResumeConsent ? reset + run.QuotaResetMargin : reset) > now ? reset : null;

    /// <summary>
    /// The block of a task its provider limited at <paramref name="now"/>, with the reset the provider announced, or null when
    /// it did not say; one that already passed counts as not said (<see cref="Announced"/>), so the block always ends ahead.
    /// <paramref name="estimatedBefore"/> is how many waits without a reset time the task had in a row before this one; each
    /// is capped by <see cref="RunContext.MaxEstimatedQuotaWait"/>. <paramref name="overload"/>: the provider is overloaded or
    /// limits the rate, which takes the short steps.
    /// </summary>
    public static BlockReason Block(RunContext run, DateTimeOffset now, DateTimeOffset? resetsAt, int estimatedBefore, bool overload = false)
    {
        var announced = Announced(run, now, resetsAt);
        if (!run.Options.AutoResumeConsent)
        {
            return BlockReason.RateLimited(announced ?? now + RunContext.DefaultRateLimitCooldown);
        }

        if (announced is { } reset)
        {
            return BlockReason.RateLimited(reset + run.QuotaResetMargin);
        }

        var steps = overload ? ShortSteps : EstimatedSteps;
        var step = steps[Math.Clamp(estimatedBefore, 0, steps.Count - 1)];
        return BlockReason.EstimatedRateLimit(now + (step < run.MaxEstimatedQuotaWait ? step : run.MaxEstimatedQuotaWait));
    }

    /// <summary>
    /// Whether a task blocked by a limit waits by itself until its block ends; otherwise the run rests in pause until the user
    /// retries it. <paramref name="stoppedChecks"/> is how many checks in a row found its limit still on and launched nothing
    /// (<see cref="RunLedger.StoppedChecks"/>): a check spends no launch, but a task is never checked more times in a row than
    /// the run may launch agents (<c>MaxAgentRuns</c>), so the waits and their checks cannot go round (ADR-0034, decision 9).
    /// </summary>
    public static bool WaitsByItself(RunContext run, BlockReason block, DateTimeOffset blockedAt, int stoppedChecks) =>
        block is { Kind: BlockKind.RateLimitedUntil, Until: { } until }
        && !ChecksExhausted(run, stoppedChecks)
        && (run.Options.AutoResumeConsent || until - blockedAt <= run.MaxQuotaWait);

    /// <summary>Whether <paramref name="stoppedChecks"/> checks in a row are as many as the run allows before a person decides.</summary>
    public static bool ChecksExhausted(RunContext run, int stoppedChecks) => stoppedChecks >= run.Options.MaxAgentRuns;

    /// <summary>Whether ready work held by the cooldown of its provider waits for it by itself, by the same rule.</summary>
    public static bool WaitsForCooldown(RunContext run, DateTimeOffset until, DateTimeOffset now) =>
        until > now && (run.Options.AutoResumeConsent || until - now <= run.MaxQuotaWait);

    /// <summary>
    /// Why a task is blocked by a limit whose reset its provider did not announce, in Spanish. <paramref name="overload"/>: the
    /// provider is overloaded or limits the rate. <paramref name="passed"/>: the reset it gave, which had already passed.
    /// </summary>
    public static UserText UnannouncedText(TaskSpec spec, BlockReason block, bool overload = false, DateTimeOffset? passed = null)
    {
        var until = block.Until!.Value;
        var judge = spec.Kind == WorkTaskKind.Review;
        var who = UserText.Of(judge ? OrchestrationUserTexts.QuotaWhoJudge : overload ? OrchestrationUserTexts.QuotaWhoOverload : OrchestrationUserTexts.QuotaWhoPlain);
        var wait = UserText.Of(block.Estimated ? OrchestrationUserTexts.QuotaWaitEstimated : OrchestrationUserTexts.QuotaWait);
        if (passed is { } reset)
        {
            return UserText.Of(OrchestrationUserTexts.QuotaPassedReset, who, reset, wait, until);
        }

        if (block.Estimated)
        {
            return UserText.Of(OrchestrationUserTexts.QuotaNoReset, who, wait, until);
        }

        // The same sentences the workers write when the provider announced the reset: the same codes.
        return judge ? UserText.Of(OrchestrationUserTexts.WorkerJudgeLimitUntil, until)
            : !overload ? UserText.Of(OrchestrationUserTexts.WorkerRateLimitedUntil, until)
            : UserText.Of(OrchestrationUserTexts.QuotaUntil, who, until);
    }

    public static string Unannounced(TaskSpec spec, BlockReason block, bool overload = false, DateTimeOffset? passed = null)
    {
        var until = block.Until!.Value.ToString("O", CultureInfo.InvariantCulture);
        var who = spec.Kind == WorkTaskKind.Review ? "El juez alcanzó el límite de uso de su proveedor"
            : overload ? "El proveedor está sobrecargado o limita la velocidad"
            : "Límite de uso del proveedor";
        var wait = block.Estimated ? "espera estimada" : "espera";
        return passed is { } reset
            ? $"{who}, con una hora de reinicio que ya pasó ({reset.ToString("O", CultureInfo.InvariantCulture)}): {wait} hasta {until}."
            : block.Estimated ? $"{who} sin hora de reinicio: {wait} hasta {until}." : $"{who} hasta {until}.";
    }
}

/// <summary>
/// The check before going on after a wait for quota (F8-3; docs/16 D-38, ADR-0034 decision 7). Before a task its provider
/// limited is launched again, the session of the provider's CLI is asked with its probe (<c>claude auth status</c>,
/// <c>codex login status</c>) and, for Codex, its quota is read from the app-server, which launches nothing without the
/// user's opt-in. None of them is a turn of the model. Each source asked leaves one <c>AccessChecked</c> entry with what it
/// said, and nothing of what the CLI printed (a probe may print the account). A CLI signed out stops the task for a person,
/// with the command that fixes it and no retry; a quota still limited sends it back to wait. Claude has no read of its
/// quota without a turn: there the check is the attempt itself, and its end says whether the limit is still on.
/// </summary>
internal static class AccessCheck
{
    /// <summary>
    /// Asks the sources of <paramref name="provider"/> before <paramref name="task"/> is launched again; returns how the attempt
    /// ends instead of launching, or null to launch it. A source with no probe or reader registered is not asked.
    /// </summary>
    public static async Task<AttemptEnd?> BeforeRelaunchAsync(WorkerContext w, TaskId? task, ProviderId provider)
    {
        if (w.Ports.Probes.FirstOrDefault(p => p.Provider == provider) is { } probe)
        {
            var session = await ProbeAsync(probe, w.Abort).ConfigureAwait(false);
            await RecordAsync(w, task, provider, AccessCheckSource.SessionProbe, session, null).ConfigureAwait(false);
            if (session == AccessCheckResult.SignedOut)
            {
                return new AttemptEnd(
                    EndKind.AwaitingHuman,
                    $"La sesión de {probe.DisplayName} no está iniciada: inicia sesión con «{LoginCommand(provider)}» y reintenta la tarea.")
                {
                    Text = UserText.Of(OrchestrationUserTexts.WorkerNotSignedIn, probe.DisplayName, LoginCommand(provider)),
                    Unlaunched = true,
                };
            }
        }

        if (provider == ProviderId.CodexCli && w.Ports.QuotaReaders.FirstOrDefault(r => r.Provider == provider) is { } reader)
        {
            var (quota, resetsAt) = await ReadAsync(reader, w.Abort).ConfigureAwait(false);
            await RecordAsync(w, task, provider, AccessCheckSource.QuotaRead, quota, resetsAt).ConfigureAwait(false);
            if (quota == AccessCheckResult.StillLimited)
            {
                return new AttemptEnd(
                    EndKind.RateLimited,
                    resetsAt is { } at
                        ? $"La cuota de {provider} sigue agotada según su app-server, hasta {at.ToString("O", CultureInfo.InvariantCulture)}."
                        : $"La cuota de {provider} sigue agotada según su app-server, sin hora de reinicio.")
                {
                    Text = resetsAt is { } stillUntil
                        ? UserText.Of(OrchestrationUserTexts.WorkerLimitStillOnUntil, provider.ToString(), stillUntil)
                        : UserText.Of(OrchestrationUserTexts.WorkerLimitStillOnNoReset, provider.ToString()),
                    RetryAt = resetsAt,
                    Unlaunched = true,
                };
            }
        }

        return null;
    }

    /// <summary>
    /// When a window of <paramref name="provider"/> ran out and its CLI did not say when it resets (Codex <c>exec</c>), the read
    /// of its quota may: for Codex, with the opt-in, the reset of the window still reached, recorded as one
    /// <c>AccessChecked</c>; null when there is no reader, no opt-in or no reset.
    /// </summary>
    public static async Task<DateTimeOffset?> ReadResetAsync(WorkerContext w, TaskId? task, ProviderId provider)
    {
        if (provider != ProviderId.CodexCli || w.Ports.QuotaReaders.FirstOrDefault(r => r.Provider == provider) is not { } reader)
        {
            return null;
        }

        var (quota, resetsAt) = await ReadAsync(reader, w.Abort).ConfigureAwait(false);
        await RecordAsync(w, task, provider, AccessCheckSource.QuotaRead, quota, resetsAt).ConfigureAwait(false);
        return quota == AccessCheckResult.StillLimited ? resetsAt : null;
    }

    /// <summary>The command that signs a CLI in, as docs/16 D-41 names it.</summary>
    internal static string LoginCommand(ProviderId provider) =>
        provider == ProviderId.ClaudeCode ? "claude auth login"
        : provider == ProviderId.CodexCli ? "codex login"
        : $"el inicio de sesión de {provider}";

    /// <summary>A probe gives <c>Ready</c>, <c>SignedOut</c> or <c>Missing</c> (not installed, unknown, or the probe failed).</summary>
    private static async Task<AccessCheckResult> ProbeAsync(IProviderProbe probe, CancellationToken cancellationToken)
    {
        try
        {
            var result = await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return result.Installation != InstallationState.Installed ? AccessCheckResult.Missing
                : result.Authentication switch
                {
                    AuthenticationState.Authenticated => AccessCheckResult.Ready,
                    AuthenticationState.NotAuthenticated => AccessCheckResult.SignedOut,
                    _ => AccessCheckResult.Missing,
                };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return AccessCheckResult.Missing;
        }
    }

    /// <summary>
    /// A read of the quota: still limited when the provider says ordinary use is not allowed, or names a limit reached, or a
    /// window is full; then the latest reset of those windows is when it ends (null when none says).
    /// </summary>
    private static async Task<(AccessCheckResult Result, DateTimeOffset? ResetsAt)> ReadAsync(IProviderQuotaReader reader, CancellationToken cancellationToken)
    {
        QuotaReadResult read;
        try
        {
            read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (AccessCheckResult.Missing, null);
        }

        switch (read.Status)
        {
            case QuotaReadStatus.NotOptedIn:
                return (AccessCheckResult.NotOptedIn, null);
            case QuotaReadStatus.NotExposed:
                return (AccessCheckResult.NotExposed, null);
            case QuotaReadStatus.Read:
                var limiting = read.Observations.Where(o => !string.IsNullOrEmpty(o.Reached) || o.Utilization.Value >= 1).ToList();
                if (read.OrdinaryUsageAllowed == false || limiting.Count > 0)
                {
                    var resets = (limiting.Count > 0 ? limiting : read.Observations).Select(o => o.ResetsAt).OfType<DateTimeOffset>().ToList();
                    return (AccessCheckResult.StillLimited, resets.Count > 0 ? resets.Max() : null);
                }

                return (AccessCheckResult.Ready, null);
            default:
                return (AccessCheckResult.Missing, null);
        }
    }

    private static Task<DecisionEntry> RecordAsync(WorkerContext w, TaskId? task, ProviderId provider, AccessCheckSource source, AccessCheckResult result, DateTimeOffset? resetsAt) =>
        w.Recorder.RecordAsync(
            DecisionKind.AccessChecked,
            $"Comprobación antes de relanzar {task?.Value ?? provider.ToString()}: {source} de {provider}, {result}",
            new AccessCheckedPayload(task, provider, source, result, w.Now, resetsAt) { RunId = w.Recorder.RunValue },
            w.Abort);
}
