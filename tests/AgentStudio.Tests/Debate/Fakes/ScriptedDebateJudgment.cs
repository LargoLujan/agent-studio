using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Tests.Debate.Fakes;

/// <summary>
/// The judgment of a debate (WP2's port) as a double: one verdict per judge of the header, about the debate's requirements and
/// the head of the log when it is asked, stored like the real one stores them (run id = the debate, task <c>juez-&lt;agent&gt;</c>),
/// and combined with the real <see cref="JudgingPanel.Combine"/>.
/// </summary>
internal sealed class ScriptedDebateJudgment(IDecisionRecorder decisions, IDebateRegistry debates, FakeVerdictStore verdicts, TimeProvider time) : IDebateJudgment
{
    private readonly Lock _gate = new();
    private readonly List<RunId> _calls = [];

    /// <summary>The decision of each judge, by its position in the arrangement; the first one when there are fewer.</summary>
    public List<VerdictDecision> Decisions { get; } = [VerdictDecision.Approved];

    /// <summary>The proposal each judge chooses, by position; null for none.</summary>
    public List<string?> Chosen { get; } = [null];

    /// <summary>Whether the verdicts pass the validator.</summary>
    public bool Valid { get; set; } = true;

    /// <summary>What each judge writes before its agent in the summary of its verdict (the text of the judge).</summary>
    public string SummaryPrefix { get; set; } = "Veredicto de";

    /// <summary>When set, the judgment throws this instead of judging.</summary>
    public Exception? Fail { get; set; }

    /// <summary>When set, the judgment waits for it, honouring the cancellation.</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>When set, it runs first on every judgment, with the log as the judgment finds it (the real dossier, for one).</summary>
    public Func<ProjectContext, RunId, CancellationToken, Task>? Inspect { get; set; }

    public IReadOnlyList<RunId> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public async Task<DebateJudgmentResult> JudgeAsync(ProjectContext project, RunId debate, IProgress<AgentEvent>? progress, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add(debate);
        }

        if (Inspect is { } inspect)
        {
            await inspect(project, debate, ct);
        }

        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(ct);
        }

        if (Fail is { } failure)
        {
            throw failure;
        }

        var record = await debates.FindAsync(project.ProjectId, debate, ct) ?? throw new InvalidOperationException($"No header for {debate}.");
        var log = await decisions.LoadAsync(project.ProjectId, ct);
        var judges = record.Judges.Select(j => new JudgeIdentity(j.Id, new ModelBinding(j.Model.Provider, j.Model.Vendor, j.Model.Model + "-reported"))).ToList();
        var judged = new List<JudgedVerdict>();
        for (var i = 0; i < judges.Count; i++)
        {
            var decision = Decisions[Math.Min(i, Decisions.Count - 1)];
            var chosen = Chosen[Math.Min(i, Chosen.Count - 1)];
            var verdict = new Verdict(
                decision,
                $"{SummaryPrefix} {judges[i].Agent}.",
                [],
                [],
                [],
                [],
                [],
                new Confidence(ConfidenceLevel.High, "Guionizado."),
                decision is VerdictDecision.ChangesRequested or VerdictDecision.Rejected ? ["Corregir."] : [],
                decision is VerdictDecision.EscalateToHuman or VerdictDecision.Unresolved,
                judges[i],
                record.RequirementSetHash,
                log.HeadHash,
                time.GetUtcNow())
            {
                Resolution = new DebateResolution(chosen, []),
            };
            var id = await verdicts.AppendAsync(
                project.ProjectId,
                new StoredVerdict($"ver-{debate.Value}-{i + 1}", verdict, debate.Value, JudgeRunScope.JudgeTask(judges[i].Agent).Value, AuditScope.Debate, Valid, Valid ? [] : [new VerdictViolation(VerdictViolationCode.MalformedVerdict, "Guionizado inválido.")], null),
                ct);
            judged.Add(new JudgedVerdict(id, verdict, Valid ? [] : [new VerdictViolation(VerdictViolationCode.MalformedVerdict, "Guionizado inválido.")], false));
        }

        var arrangement = new JudgingArrangement(record.Judging, [.. record.Judges.Select(j => new JudgeIdentity(j.Id, j.Model))], new HashSet<VendorId>(record.ProducerVendors), new HashSet<VendorId>(record.WaivedParticipantVendors));
        return new DebateJudgmentResult(judged, JudgingPanel.Combine(arrangement, [.. judged.Select(j => (j.Verdict, j.Violations.Count == 0))]));
    }
}
