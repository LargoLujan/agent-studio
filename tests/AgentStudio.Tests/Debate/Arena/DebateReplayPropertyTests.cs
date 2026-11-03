using AgentStudio.Application.Coordination;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Debate.Fakes;
using static AgentStudio.Tests.Debate.Fakes.ScriptedDebateWorld;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>
/// The replay (docs/15 §8): about 200 debates drawn by seed (modes, participants on both CLIs, rounds, limits, failures,
/// verdicts, a person who retries, asks for judgment, decides or aborts, and processes that end and are resumed). Whatever
/// happened live, the replay of the log gives the same state, and the budget never counts less than the CLIs reported nor
/// leaves a launch out.
/// </summary>
public sealed class DebateReplayPropertyTests
{
    private const int Seeds = 200;
    private const int Parallel = 8;

    private static readonly (AgentId Agent, VendorId Vendor)[] Candidates =
    [
        (Arquitecto, VendorId.Anthropic),
        (Revisor, VendorId.Anthropic),
        (Critico, VendorId.OpenAI),
        (Analista, VendorId.OpenAI),
    ];

    [Fact]
    public async Task The_replay_of_the_log_gives_the_live_state_and_the_budget_never_counts_less()
    {
        var seen = new List<string>();
        for (var batch = 0; batch < Seeds; batch += Parallel)
        {
            var ended = await Task.WhenAll(Enumerable.Range(batch, Math.Min(Parallel, Seeds - batch)).Select(seed => Task.Run(() => OneAsync(seed))));
            seen.AddRange(ended.SelectMany(e => e));
        }

        // The seeds reach every way a debate ends, so the property is about all of them.
        string[] ways =
        [
            "resumed", "deadline", "requested", "converged", "turn-missing", "tribunal", "limit-rounds", "limit-tokens",
            "limit-duration", "single", "agree", "user", "aborted", "failed-turn", "provider-limit", "human-resolution",
        ];
        Assert.All(ways, way => Assert.True(seen.Contains(way), $"no seed reached '{way}'"));
    }

    /// <summary>One debate of the seed, played to its end by a scripted person; what it went through, for the coverage.</summary>
    private static async Task<IReadOnlyList<string>> OneAsync(int seed)
    {
        var ways = new List<string>();
        var random = new Random(seed);
        using var world = new ScriptedDebateWorld();
        var plan = await PlanAsync(world, random);
        var dossiers = new ScriptedDebateDossiers(world);
        var scripted = new Random(seed * 7919);
        var hangs = random.Next(4) == 0;
        world.Claude.Reply = (r, _) => Script(world.Claude, scripted, hangs, r);
        world.Codex.Reply = (r, _) => Script(world.Codex, scripted, hangs, r);
        var judges = plan.Judging == DebateJudgingKind.CrossVendorPanel ? 2 : 1;
        world.Judgment.Decisions.Clear();
        world.Judgment.Chosen.Clear();
        for (var i = 0; i < judges; i++)
        {
            world.Judgment.Decisions.Add(Pick(random, VerdictDecision.Approved, VerdictDecision.Approved, VerdictDecision.Approved, VerdictDecision.Rejected, VerdictDecision.ChangesRequested, VerdictDecision.EscalateToHuman, VerdictDecision.Unresolved));
            world.Judgment.Chosen.Add(Pick<string?>(random, "C1", "C1", null));
        }

        world.Judgment.Valid = random.Next(10) != 0;

        var proposal = await world.Coordinator.PrepareAsync(plan, CancellationToken.None);
        Assert.True(
            proposal.Problems.Count == 0,
            $"seed {seed} ({plan.Mode}, {string.Join(", ", plan.Participants.Select(p => $"{p.Agent}:{p.Role}"))}, {plan.Judging} {string.Join(", ", plan.Judges)}): " + string.Join(" | ", proposal.Problems));
        var run = await world.Coordinator.OpenAsync(proposal, CancellationToken.None);
        var person = new Random(seed * 104_729);
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        var handlingHang = false;
        DebateAwaiting? seen = null;
        var waited = 0;
        var resumes = 0;
        try
        {
            while (!run.Completion.IsCompleted)
            {
                Assert.True(DateTime.UtcNow < limit, $"seed {seed}: the debate did not end in time ({run.Snapshot().Phase}, {run.Snapshot().Awaiting?.Reason}).");
                var running = world.Claude.Runs.Any(r => r.Running) || world.Codex.Runs.Any(r => r.Running);
                var snapshot = run.Snapshot();
                if (running && !handlingHang)
                {
                    handlingHang = true;
                    var choice = person.Next(3);
                    if (choice == 0 && resumes < 2)
                    {
                        // The process ends with a turn in course; another one resumes the debate.
                        await run.DisposeAsync();
                        world.Claude.Hold = false;
                        world.Codex.Hold = false;
                        resumes++;
                        ways.Add("resumed");
                        run = await world.Coordinator.ResumeAsync(world.Context, run.Id, CancellationToken.None);
                    }
                    else if (choice == 1 && snapshot.Contributions.Count > 0)
                    {
                        // «Pedir juicio ahora» while the turn runs: it is cancelled and what answered is published.
                        ways.Add("requested");
                        world.Claude.Hold = false;
                        world.Codex.Hold = false;
                        try
                        {
                            await run.RequestJudgmentAsync();
                        }
                        catch (InvalidOperationException)
                        {
                            // The round ended meanwhile.
                        }
                    }
                    else
                    {
                        // The deadline of the system clock passes with the turn in course.
                        ways.Add("deadline");
                        world.Clock.Advance(plan.Limits.MaxDuration + TimeSpan.FromMinutes(1));
                        world.Claude.Hold = false;
                        world.Codex.Hold = false;
                    }
                }
                else if (!running)
                {
                    handlingHang = false;
                }

                if (snapshot.Awaiting is { } awaiting && !running && !ReferenceEquals(awaiting, seen))
                {
                    seen = awaiting;
                    ways.Add(awaiting.Reason switch
                    {
                        DebateAwaitReason.FailedTurn => "failed-turn",
                        DebateAwaitReason.ProviderLimit => "provider-limit",
                        _ => "human-resolution",
                    });
                    await ActAsync(run, snapshot, awaiting, person, ++waited > 4);
                }

                await Task.Delay(5);
            }
        }
        finally
        {
            await run.DisposeAsync();
        }

        var result = await run.Completion;
        await CheckReplayAsync(world, run, result, seed, crashed: resumes > 0);

        // Every judgment found a log that backs the debate: the approved header, and every row of its evidence announced. (A
        // debate about a run also gets the dossier of that run, which this world does not register: it is not checked here.)
        Assert.All(plan.Subject.Kind == DebateSubjectKind.Run ? [] : dossiers.Built, built =>
        {
            Assert.True(built.HeaderVerified, $"seed {seed}: the header did not verify");
            Assert.True(built.Checks.Count == 0, $"seed {seed} ({plan.Mode}): " + string.Join(" | ", built.Checks));
        });
        ways.Add(result.Outcome == DebateOutcome.Aborted ? "aborted" : result.Panel!.Rule);
        ways.Add(result.JudgmentReason switch
        {
            DebateJudgmentReason.Converged => "converged",
            DebateJudgmentReason.TurnMissing => "turn-missing",
            DebateJudgmentReason.Tribunal => "tribunal",
            DebateJudgmentReason.Requested => "requested",
            DebateJudgmentReason.Limit => "limit-" + result.LimitReached.ToString().ToLowerInvariant(),
            _ => "no-judgment",
        });
        return ways;
    }

    /// <summary>What a person does when the debate waits: retry, judgment with what there is, a decision or an abort.</summary>
    private static async Task ActAsync(IDebateRun run, DebateSnapshot snapshot, DebateAwaiting awaiting, Random person, bool insist)
    {
        try
        {
            if (awaiting.Reason == DebateAwaitReason.HumanResolution)
            {
                if (insist || person.Next(4) == 0)
                {
                    await run.AbortAsync("La persona lo deja.");
                    return;
                }

                await run.DecideAsync(Pick<VerdictDecision?>(person, VerdictDecision.Approved, VerdictDecision.Rejected, VerdictDecision.Unresolved), Pick<string?>(person, "C1", null), "Decido yo.");
                return;
            }

            switch (insist ? 2 : person.Next(3))
            {
                case 0:
                    await run.RetryTurnAsync(snapshot.Participants.First(p => p.Label == awaiting.Participant).Agent);
                    break;
                case 1:
                    await run.RequestJudgmentAsync();
                    break;
                default:
                    await run.AbortAsync("La persona aborta.");
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or DomainRuleViolationException)
        {
            // The debate moved on meanwhile, or refused what was asked: it says why and the person looks again.
        }
    }

    /// <summary>One launch of a participant: an answer, one that does not read, a failure, a usage limit, no usage, or a turn that hangs.</summary>
    private static ScriptedDebateReply Script(ScriptedDebateProvider provider, Random scripted, bool hangs, AgentRunRequest request)
    {
        int roll;
        bool finished;
        lock (scripted)
        {
            roll = scripted.Next(100);
            finished = scripted.Next(3) == 0;
        }

        return roll switch
        {
            < 8 => ScriptedDebateProvider.Valid(request, customize: a => a["kind"] = "Nada"),
            < 16 => new ScriptedDebateReply(AgentRunStatus.Failed, null, null, "fallo guionizado"),
            < 20 => new ScriptedDebateReply(AgentRunStatus.RateLimited, null, null, "límite de uso"),
            < 26 => ScriptedDebateProvider.Valid(request, finished) with { Usage = null },
            < 34 when hangs => Hang(provider, request, finished),
            _ => ScriptedDebateProvider.Valid(request, finished),
        };

        static ScriptedDebateReply Hang(ScriptedDebateProvider provider, AgentRunRequest request, bool finished)
        {
            provider.Hold = true;
            return ScriptedDebateProvider.Valid(request, finished);
        }
    }

    /// <summary>A debate drawn from the seed: its mode, two or three participants with the roles it needs, the disposition and limits.</summary>
    private static async Task<DebateRequest> PlanAsync(ScriptedDebateWorld world, Random random)
    {
        var mode = Pick(random, Enum.GetValues<DebateMode>());
        var count = random.Next(2, 4);
        var seated = Candidates.OrderBy(_ => random.Next()).Take(count).ToList();
        var roles = mode switch
        {
            DebateMode.Free or DebateMode.Adversarial or DebateMode.ImplementationAudit => seated.Select((_, i) => i == 0 ? DebateRole.Proponent : DebateRole.Critic),
            DebateMode.ArchitectureReview => seated.Select((_, i) => i == 0 ? DebateRole.Proponent : DebateRole.Panelist),
            DebateMode.OrchestratorReview => seated.Select((_, i) => i == 0 ? DebateRole.Critic : DebateRole.Panelist),
            _ => seated.Select(_ => DebateRole.Panelist),
        };
        var participants = seated.Zip(roles, (s, r) => (s.Agent, r)).ToList();
        var vendors = seated.Select(s => s.Vendor).Distinct().ToList();
        var limits = new DebateLimits(random.Next(1, 5), TimeSpan.FromMinutes(30), random.Next(2) == 0 ? null : random.Next(2_000, 16_000));
        int? maxAgentRuns = random.Next(3) == 0 ? random.Next(count, (count * limits.MaxRounds) + 3) : null;

        if (mode is DebateMode.ImplementationAudit or DebateMode.OrchestratorReview)
        {
            // The run under review is Anthropic's, so its judge is of OpenAI: independent when no participant is of OpenAI,
            // and not a producer (with OpenAI waived) when one is.
            await world.RecordFinishedRunAsync();
            return world.Request(
                mode,
                participants,
                [JuezCodex],
                vendors.Contains(VendorId.OpenAI) ? DebateJudgingKind.NonProducerJudge : DebateJudgingKind.IndependentJudge,
                new DebateSubject(DebateSubjectKind.Run, FinishedRun.Value),
                limits,
                mode == DebateMode.OrchestratorReview ? DebateModes.OrchestratorReview(T0) : null,
                maxAgentRuns);
        }

        var subject = random.Next(2) == 0 ? new DebateSubject(DebateSubjectKind.Commit, null) : null;
        return vendors.Count == 2
            ? world.Request(mode, participants, [JuezClaude, JuezCodex], DebateJudgingKind.CrossVendorPanel, subject, limits, maxAgentRuns: maxAgentRuns)
            : world.Request(mode, participants, [vendors[0] == VendorId.Anthropic ? JuezCodex : JuezClaude], DebateJudgingKind.IndependentJudge, subject, limits, maxAgentRuns: maxAgentRuns);
    }

    private static async Task CheckReplayAsync(ScriptedDebateWorld world, IDebateRun run, DebateResult result, int seed, bool crashed)
    {
        var ct = CancellationToken.None;
        var record = (await world.Debates.FindAsync(Project, run.Id, ct))!;
        var log = await world.DebateLogAsync(run.Id);
        var stored = await world.Verdicts.ListAsync(Project, run.Id.Value, ct);
        var requirements = (await world.Requirements.FindAsync(Project, record.RequirementSetHash, ct))!;
        var header = new DebateHeader(
            new DebateId(record.Id.Value),
            record.Mode,
            record.Subject.Kind,
            world.Evidence.ContentText(record.Question),
            requirements,
            [.. record.Participants.Select(p => new DebateParticipant(p.Agent.Id, p.Role, p.Agent.Model))],
            new JudgingArrangement(record.Judging, [.. record.Judges.Select(j => new JudgeIdentity(j.Id, j.Model))], new HashSet<VendorId>(record.ProducerVendors), new HashSet<VendorId>(record.WaivedParticipantVendors)),
            record.Limits,
            record.CreatedAt);
        var replay = DebateStateReplayer.Replay(header, log.Events(record.CreatedAt, ids => [.. ids.Select(id => stored.First(v => v.Id == id)).Select(v => (v.Verdict, v.Valid))]));
        var live = run.Snapshot();
        var why = $"seed {seed} ({record.Mode}, {result.Phase}/{result.Outcome}, {result.JudgmentReason}/{result.LimitReached}): ";

        Assert.True(replay.Problems.Count == 0, why + string.Join(" | ", replay.Problems));
        Assert.DoesNotContain(live.Problems, p => p.StartsWith("El debate se detuvo", StringComparison.Ordinal));
        Assert.True(log.Problems.Count == 0, why + string.Join(" | ", log.Problems));
        var session = replay.Session!;
        Assert.Equal((result.Phase, result.Outcome, result.LimitReached, result.JudgmentReason), (session.Phase, session.Outcome, session.LimitReached, session.JudgmentReason));
        Assert.Equal(live.Round, session.CurrentRound);
        Assert.True(result.Phase == DebatePhase.Closed, why + "every debate ends closed");
        Assert.Equal(
            live.Contributions.Select(c => $"{c.Index}|{c.Round}|{c.Author}|{c.Kind}|{c.RepliesTo}|{c.Evidence}|{c.AnsweredAt:O}|{string.Join(",", c.Cites)}"),
            session.Contributions.Select((c, i) => $"{i}|{c.Round}|{Label(record, c.Author)}|{c.Kind}|{(c.RepliesTo is { } r ? ContributionLabels.Of(r) : null)}|{c.Content}|{c.At:O}|{string.Join(",", c.Evidence)}"));
        if (result.Panel is { } panel)
        {
            var replayed = session.Panel!;
            Assert.Equal((panel.Outcome, panel.ChosenProposal, panel.Rule), (replayed.Outcome, replayed.ChosenProposal, replayed.Rule));
            if (panel.Rule != JudgingPanel.User)
            {
                Assert.Equal(panel.Decision, replayed.Decision);
            }
        }
        else
        {
            Assert.Null(session.Panel);
        }

        // The transcript the history shows comes from the same replay, with nothing to report.
        var transcript = await world.Coordinator.ReadAsync(world.Context, run.Id, ct);
        Assert.True(transcript.Problems.Count == 0, why + string.Join(" | ", transcript.Problems));
        Assert.Equal((result.Phase, result.Outcome), (transcript.Summary.Phase, transcript.Summary.Outcome));
        Assert.Equal(live.Contributions.Select(c => c.Text), transcript.Contributions.Select(c => c.Text));

        // The budget: every launch of the log counted, the figures each CLI reported counted exactly, an estimate for each
        // missing figure, and the room shows the figure of the log.
        var usages = log.Usages(id => world.Evidence.ContentText(id).Length);
        Assert.Equal(log.Turns.Count(), usages.Count);
        Assert.True(world.Claude.Runs.Count + world.Codex.Runs.Count <= usages.Count, why + "a launch is not in the log");
        var missing = usages.Where(u => DebateBudget.ReportedTurns([u]) == 0).ToList();
        Assert.All(missing, u => Assert.True(u.Estimate > 0, why + "a missing figure counts nothing"));
        var used = DebateBudget.Used(usages);
        var recorded = usages.Except(missing).Sum(u => u.InputTokens.Value!.Value + u.OutputTokens.Value!.Value);
        Assert.Equal(recorded + missing.Sum(u => u.Estimate), used.Sum ?? 0);
        Assert.Equal(used.Sum, live.Tokens.Sum);
        if (!crashed)
        {
            // Without a process that ended, every figure a CLI reported reached the log.
            var reported = world.Claude.Runs.Concat(world.Codex.Runs)
                .Where(r => r.Completion.Result.Status != AgentRunStatus.Cancelled && r.Reply.Usage is not null)
                .Sum(r => r.Reply.Usage!.InputTokens.Value!.Value + r.Reply.Usage.OutputTokens.Value!.Value);
            Assert.Equal(reported, recorded);
        }
    }

    private static string Label(DebateRecord record, AgentId agent) => record.Participants.First(p => p.Agent.Id == agent).Label;

    private static T Pick<T>(Random random, params T[] values) => values[random.Next(values.Length)];
}
