using System.Collections.Concurrent;
using System.Numerics;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Observability.Usage;

// docs/14 D-02: the CLIs report the total of the session, so the own usage of an attempt is a difference of snapshots.
public sealed partial class UsageProjector
{
    private const string ResetReason = "la sesión no acumuló (una cifra bajó respecto al intento anterior): se cuenta la instantánea entera";
    private const string NoReasoningPerModelReason = "modelUsage no da el razonamiento";
    private const string LessPreviousReason = "menos el intento anterior de la sesión";
    private const string UntoldReason = "acumulado de la sesión: el intento anterior no da esta cifra";
    private const int MaxDerivedSources = 1024;

    private static readonly ConcurrentDictionary<(string Reason, string Source), string> DerivedSources = new();

    /// <summary>
    /// Gives an attempt that just ended its own usage: its snapshot minus the last one of its session (the attempts end
    /// in log order, so every earlier attempt of the session is already known). The total of the session is only known
    /// once the whole log is read, so it is added afterwards (<see cref="WithSessionTotals"/>).
    /// </summary>
    private static (UsageAttempt Attempt, Session? Session) Close(string projectId, Closed attempt, Dictionary<(ProviderId Provider, string Session), Session> sessions, List<string> warnings)
    {
        var raw = Snapshot.Of(attempt, warnings);
        var session = SessionOf(sessions, attempt.Started.Provider, attempt.Finished);
        var previous = session?.LastWithResult;
        var built = raw is null
            ? Build(projectId, attempt, MissingFigures(attempt.Finished.Usage is null ? NoResultReason : CodexZeroReason), [])
            : Build(projectId, attempt, raw.Minus(previous), raw.ModelsMinus(previous));
        if (session is not null)
        {
            session.Members++;
            session.LastWithResult = raw ?? session.LastWithResult;
        }

        return (built, session);
    }

    /// <summary>Every attempt of a session shared by more than one carries the total of the session: its last snapshot.</summary>
    private static void WithSessionTotals(List<UsageAttempt> attempts, List<Session?> sessions)
    {
        for (var i = 0; i < attempts.Count; i++)
        {
            if (sessions[i] is { Members: > 1, LastWithResult: { } last })
            {
                attempts[i] = attempts[i] with { SessionTotal = last.AsIs() };
            }
        }
    }

    /// <summary>
    /// The session an attempt ran in: the one it resumed when known, else the one of its id. An attempt that resumed a
    /// session under a new id joins the session it resumed, and later attempts find that session by either id.
    /// </summary>
    private static Session? SessionOf(Dictionary<(ProviderId, string), Session> sessions, ProviderId provider, AgentRunFinishedPayload finished)
    {
        var resumed = finished.ResumedSession is { Length: > 0 } r ? r : null;
        var own = finished.SessionId is { Length: > 0 } s ? s : null;
        if (resumed is null && own is null)
        {
            return null;
        }

        Session? session = null;
        if (resumed is not null)
        {
            sessions.TryGetValue((provider, resumed), out session);
        }

        if (session is null && own is not null)
        {
            sessions.TryGetValue((provider, own), out session);
        }

        session ??= new Session();
        if (resumed is not null)
        {
            sessions.TryAdd((provider, resumed), session);
        }

        if (own is not null)
        {
            sessions.TryAdd((provider, own), session);
        }

        return session;
    }

    private static UsageFigures MissingFigures(string reason) => new(
        MetricValue<long>.Missing(reason),
        MetricValue<long>.Missing(reason),
        MetricValue<long>.Missing(reason),
        MetricValue<long>.Missing(reason),
        MetricValue<long>.Missing(reason),
        MetricValue<decimal>.Missing(reason));

    private static bool WentDown<T>(MetricValue<T> current, MetricValue<T> previous)
        where T : struct, INumber<T> =>
        current.Value is { } now && previous.Value is { } before && now < before;

    /// <summary>A figure counted whole because the session did not accumulate: an estimate of Agent Studio.</summary>
    private static MetricValue<T> Whole<T>(MetricValue<T> figure, string reason)
        where T : struct =>
        figure.Value is { } value ? MetricValue<T>.EstimatedBySystem(value, Derived(reason, figure.Source)) : figure;

    /// <summary>
    /// What a figure added since <paramref name="previous"/>, with the weakest origin of the two. A figure the previous
    /// snapshot lacks cannot be told apart from the total of the session: it is counted whole, as an estimate.
    /// </summary>
    private static MetricValue<T> Difference<T>(MetricValue<T> current, MetricValue<T> previous)
        where T : struct, INumber<T>
    {
        if (current.Value is not { } now)
        {
            return current;
        }

        return previous.Value is { } before
            ? MetricValue<T>.Of(MetricOrigins.Weakest(current.Origin, previous.Origin), now - before, Derived(LessPreviousReason, current.Source))
            : MetricValue<T>.EstimatedBySystem(now, Derived(UntoldReason, current.Source));
    }

    /// <summary>
    /// The source of a figure worked out from another: the reason and the source it comes from. The sources of the CLIs
    /// are a handful of constants, so each text is made once (up to a bound, beyond which it is made every time).
    /// </summary>
    private static string Derived(string reason, string source)
    {
        if (DerivedSources.TryGetValue((reason, source), out var text))
        {
            return text;
        }

        text = reason == LessPreviousReason ? $"{source}, {reason}" : $"{reason} ({source})";
        if (DerivedSources.Count < MaxDerivedSources)
        {
            DerivedSources.TryAdd((reason, source), text);
        }

        return text;
    }

    /// <summary>A session of a CLI: how many attempts ran in it and the last snapshot one of them ended with.</summary>
    private sealed class Session
    {
        public int Members { get; set; }

        public Snapshot? LastWithResult { get; set; }
    }

    /// <summary>The snapshot an attempt ended with, as the CLI reported it: the total of its session at that moment.</summary>
    private sealed record Snapshot(
        MetricValue<long> Input,
        MetricValue<long> CacheRead,
        MetricValue<long> CacheWrite,
        MetricValue<long> Output,
        MetricValue<long> Reasoning,
        MetricValue<decimal> Cost,
        IReadOnlyList<ModelSnapshot>? PerModel)
    {
        /// <summary>The snapshot of an end; null when it has none, or when it is a Codex usage of all zeros.</summary>
        public static Snapshot? Of(Closed attempt, List<string> warnings)
        {
            if (attempt.Finished.Usage is not { } usage)
            {
                return null;
            }

            var snapshot = new Snapshot(
                Long(usage.InputTokens, attempt, warnings),
                Long(usage.CachedInputTokens, attempt, warnings),
                Long(usage.CacheWriteInputTokens, attempt, warnings),
                Long(usage.OutputTokens, attempt, warnings),
                Long(usage.ReasoningOutputTokens, attempt, warnings),
                Decimal(usage.CostUsd, attempt, warnings),
                usage.PerModel is { } models ? [.. models.Select(m => ModelSnapshot.Of(m, attempt, warnings))] : null);

            MetricValue<long>[] tokens = [snapshot.Input, snapshot.CacheRead, snapshot.CacheWrite, snapshot.Output, snapshot.Reasoning];
            var zero = attempt.Started.Provider == ProviderId.CodexCli && tokens.Any(t => t.HasValue) && tokens.All(t => t.Value is null or 0);
            return zero ? null : snapshot;
        }

        /// <summary>The figures as the CLI reported them: the total of the session at the end of this attempt.</summary>
        public UsageFigures AsIs() => new(Input, CacheRead, CacheWrite, Output, Reasoning, Cost);

        /// <summary>
        /// What this attempt added to its session, figure by figure, since <paramref name="previous"/> (the last earlier
        /// attempt of the session with a result). When a figure went down the CLI did not accumulate: the whole
        /// snapshot counts, as an estimate.
        /// </summary>
        public UsageFigures Minus(Snapshot? previous)
        {
            if (previous is null)
            {
                return AsIs();
            }

            if (WentDownFrom(previous))
            {
                return new UsageFigures(Whole(Input, ResetReason), Whole(CacheRead, ResetReason), Whole(CacheWrite, ResetReason), Whole(Output, ResetReason), Whole(Reasoning, ResetReason), Whole(Cost, ResetReason));
            }

            return new UsageFigures(
                Difference(Input, previous.Input),
                Difference(CacheRead, previous.CacheRead),
                Difference(CacheWrite, previous.CacheWrite),
                Difference(Output, previous.Output),
                Difference(Reasoning, previous.Reasoning),
                Difference(Cost, previous.Cost));
        }

        /// <summary>
        /// The same for each model of the breakdown. A model the previous attempt did not use starts from zero; when the
        /// previous attempt has no breakdown, or the session did not accumulate, each model counts whole.
        /// </summary>
        public IReadOnlyList<ModelUsageFigures> ModelsMinus(Snapshot? previous)
        {
            if (PerModel is null)
            {
                return [];
            }

            var reset = previous is not null && WentDownFrom(previous);
            return [.. PerModel.Select(model =>
            {
                if (previous is null)
                {
                    return model.Figures(model.AsIs());
                }

                if (reset)
                {
                    return model.Figures(model.Whole(ResetReason));
                }

                if (previous.PerModel is null)
                {
                    return model.Figures(model.Whole("acumulado de la sesión: el intento anterior no tiene desglose por modelo"));
                }

                var before = previous.PerModel.FirstOrDefault(m => string.Equals(m.Model, model.Model, StringComparison.Ordinal));
                return model.Figures(before is null ? model.AsIs() : model.Minus(before));
            })];
        }

        private bool WentDownFrom(Snapshot previous) =>
            WentDown(Input, previous.Input) || WentDown(CacheRead, previous.CacheRead) || WentDown(CacheWrite, previous.CacheWrite)
            || WentDown(Output, previous.Output) || WentDown(Reasoning, previous.Reasoning) || WentDown(Cost, previous.Cost);
    }

    /// <summary>The figures of one model of a snapshot (Claude <c>modelUsage</c>), also totals of the session, and its window.</summary>
    private sealed record ModelSnapshot(
        string Model,
        MetricValue<long> Input,
        MetricValue<long> CacheRead,
        MetricValue<long> CacheWrite,
        MetricValue<long> Output,
        MetricValue<decimal> Cost,
        MetricValue<long> Window,
        string? CostBasis)
    {
        private static readonly MetricValue<long> NoReasoning = MetricValue<long>.NotExposed(NoReasoningPerModelReason);

        public static ModelSnapshot Of(ModelUsageSnapshot model, Closed attempt, List<string> warnings) => new(
            model.Model,
            Long(model.InputTokens, attempt, warnings),
            Long(model.CacheReadInputTokens, attempt, warnings),
            Long(model.CacheWriteInputTokens, attempt, warnings),
            Long(model.OutputTokens, attempt, warnings),
            Decimal(model.CostUsd, attempt, warnings),
            Long(model.ContextWindow, attempt, warnings),
            model.CostBasis);

        public ModelUsageFigures Figures(UsageFigures figures) => new(Model, figures, Window, CostBasis);

        public UsageFigures AsIs() => new(Input, CacheRead, CacheWrite, Output, NoReasoning, Cost);

        public UsageFigures Whole(string reason) =>
            new(UsageProjector.Whole(Input, reason), UsageProjector.Whole(CacheRead, reason), UsageProjector.Whole(CacheWrite, reason), UsageProjector.Whole(Output, reason), NoReasoning, UsageProjector.Whole(Cost, reason));

        public UsageFigures Minus(ModelSnapshot before)
        {
            if (WentDown(Input, before.Input) || WentDown(CacheRead, before.CacheRead) || WentDown(CacheWrite, before.CacheWrite)
                || WentDown(Output, before.Output) || WentDown(Cost, before.Cost))
            {
                return Whole(ResetReason);
            }

            return new UsageFigures(
                Difference(Input, before.Input),
                Difference(CacheRead, before.CacheRead),
                Difference(CacheWrite, before.CacheWrite),
                Difference(Output, before.Output),
                NoReasoning,
                Difference(Cost, before.Cost));
        }
    }
}
