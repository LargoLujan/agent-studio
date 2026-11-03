using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Debate.Arena;
using AgentStudio.Debate.Audit;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Integration.Phase4;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase5;

/// <summary>
/// F5-7 up to the delivery (docs/15 D-38 to D-40 and decision 6(a); ADR-0032 and the addendum of ADR-0012): a real run of
/// the engine whose integration Codex's judge reviewed, the challenge of that verdict through the real service, its
/// revision debate in the real arena judged by the real judgment, and <see cref="IDeliveryService"/> reading the log of
/// SQLite. The revision inherits who may not judge (Claude produced the run) and changes the judge; while it is open the
/// delivery waits; a revision that confirms the approval lets it through, and the run is delivered to the user's branch; one
/// that withdraws it blocks it; and one that turns an escalated review towards an approval only counts once the person
/// accepts it (<c>verdict.revision.accept</c>).
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class ChallengeToDeliveryTests
{
    private const string Reasons = "La prueba solo comprueba que el fichero existe, no lo que hace la función.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_challenged_approval_blocks_the_delivery_while_its_revision_is_open_and_one_that_confirms_it_delivers_the_run_to_the_users_branch()
    {
        using var repo = Phase5Runs.Repo();
        var person = new ScriptedApprovalPrompt();
        var clis = new Phase5Clis();
        await using var app = await Phase4App.OpenAsync(repo.Root, Phase5Composition.Services(Phase4Paths.Beside(repo.Root), person, clis.Providers), Phase5Roster.Agents());
        var project = app.Project;
        var delivery = app.Get<IDeliveryService>();
        var challenges = app.Get<IVerdictChallenges>();
        var run = await Phase5Runs.SucceededAsync(app);
        var verdict = run.VerdictId!;
        var original = await StoredAsync(app, run, verdict);
        Assert.Equal((true, VerdictDecision.Approved, Phase5Roster.RunJudge), (original.Valid, original.Verdict.Decision, original.Verdict.Judge.Agent));
        var approved = await delivery.CheckAsync(project, run.Id, Ct);
        Assert.True(approved.Ready, approved.Reason);
        var userHead = repo.Head;

        // The revision is held in its round 1, so the challenge stays open while the delivery is asked.
        var proposal = await PrepareAsync(app, verdict);
        var turns = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clis.Hold = r => r.Agent.Role != AgentRole.Judge && DebateIds.IsDebate(r.DebateId) ? turns : null;
        await using var revision = await challenges.ChallengeAsync(proposal, Ct);

        // What the challenge anchored: the reasons as a person's note announced in the revision, and VerdictChallenged in the run.
        var (_, challenged) = Assert.Single(await app.PayloadsAsync<VerdictChallengedPayload>(run.Id.Value));
        Assert.Equal((verdict, revision.Id.Value, ScriptedApprovalPrompt.Person), (challenged.Verdict, challenged.RevisionDebate, challenged.ChallengedBy));
        Assert.Equal(Reasons, await Phase5Checks.TextAsync(app, challenged.Reasons, challenged.ReasonsSha256));
        var note = Assert.Single(await app.Get<IEvidenceStore>().ListByRunAsync(project.ProjectId, revision.Id.Value, Ct), e => e.Id == challenged.Reasons);
        Assert.Equal((EvidenceKind.HumanNote, EvidenceOrigin.ProvidedByHuman), (note.Kind, note.Origin));
        var (_, opened) = Assert.Single(await app.PayloadsAsync<DebateOpenedPayload>(revision.Id.Value));
        Assert.Equal((DebateMode.Adversarial, DebateSubjectKind.Verdict, verdict, run.IntegrationCommit), (opened.Mode, opened.Subject, opened.Challenges, opened.SnapshotCommit));
        Assert.Contains(await app.PayloadsAsync<ApprovalAnsweredPayload>(revision.Id.Value), a => a.Entry.Kind == DecisionKind.ApprovalGranted && a.Payload.Action == DeliveryService.DebateOpenAction);

        // While the revision is open the delivery waits: nothing is asked and the user's branch does not move.
        Assert.Equal(ChallengeStatus.Pending, await challenges.StatusAsync(project, verdict, Ct));
        var pending = await delivery.CheckAsync(project, run.Id, Ct);
        AssertNotReady(pending, verdict, revision.Id.Value, "no ha terminado");
        await AssertNothingDeliveredAsync(app, run, person, repo, userHead);

        turns.SetResult();
        var revised = await Phase5Checks.EndAsync(revision);
        Assert.True(revised is { Phase: DebatePhase.Closed, Outcome: DebateOutcome.Resolved, Panel.Rule: JudgingPanel.Single }, revised.Summary);

        // The verdict of the revision is kept in the run, superseding the challenged one, by another judge of the vendor that did not produce it.
        var kept = await RevisionVerdictAsync(app, run, verdict, revision, original);
        Assert.Equal(VerdictDecision.Approved, kept.Verdict.Decision);

        // It confirms the approval: the delivery goes through, and the run reaches the user's branch.
        Assert.Equal(ChallengeStatus.Confirmed, await challenges.StatusAsync(project, verdict, Ct));
        var ready = await delivery.CheckAsync(project, run.Id, Ct);
        Assert.True(ready.Ready, ready.Reason);
        Assert.Equal(run.IntegrationCommit, ready.Commit);
        Assert.Contains("confirmó la aprobación", ready.Reason, StringComparison.Ordinal);
        Assert.Contains(revision.Id.Value, ready.Reason, StringComparison.Ordinal);

        var delivered = await delivery.DeliverAsync(project, run.Id, DeliveryMode.FastForwardOnly, Ct);
        Assert.True(delivered.Delivered, delivered.Message);
        Assert.Equal(DeliveryService.ApprovalAction, person.Requests[^1].Action);
        Assert.Equal(run.IntegrationCommit, repo.Head);
        var log = await app.LoadLogAsync();
        Assert.True(log.Verification.IsValid, log.Verification.Problem);
    }

    [Fact]
    public async Task A_revision_that_withdraws_the_approval_blocks_the_delivery()
    {
        using var repo = Phase5Runs.Repo();
        var person = new ScriptedApprovalPrompt();
        var clis = new Phase5Clis
        {
            // The judge of the run approves; the judge of the revision asks for changes.
            Decision = r => DebateIds.IsDebate(r.DebateId) ? VerdictDecision.ChangesRequested : VerdictDecision.Approved,
        };
        await using var app = await Phase4App.OpenAsync(repo.Root, Phase5Composition.Services(Phase4Paths.Beside(repo.Root), person, clis.Providers), Phase5Roster.Agents());
        var project = app.Project;
        var delivery = app.Get<IDeliveryService>();
        var challenges = app.Get<IVerdictChallenges>();
        var run = await Phase5Runs.SucceededAsync(app);
        var verdict = run.VerdictId!;
        var original = await StoredAsync(app, run, verdict);
        Assert.True((await delivery.CheckAsync(project, run.Id, Ct)).Ready);
        var userHead = repo.Head;

        await using var revision = await challenges.ChallengeAsync(await PrepareAsync(app, verdict), Ct);
        var revised = await Phase5Checks.EndAsync(revision);
        Assert.True(revised is { Phase: DebatePhase.Closed, Outcome: DebateOutcome.Resolved, Panel.Rule: JudgingPanel.Single }, revised.Summary);
        var kept = await RevisionVerdictAsync(app, run, verdict, revision, original);
        Assert.Equal(VerdictDecision.ChangesRequested, kept.Verdict.Decision);
        Assert.NotEmpty(kept.Verdict.CorrectiveActions);

        Assert.Equal(ChallengeStatus.Revoked, await challenges.StatusAsync(project, verdict, Ct));
        AssertNotReady(await delivery.CheckAsync(project, run.Id, Ct), revision.Id.Value, verdict, "pide cambios", "retira la aprobación");
        await AssertNothingDeliveredAsync(app, run, person, repo, userHead);
    }

    [Fact]
    public async Task A_revision_that_turns_an_escalated_review_towards_an_approval_counts_only_once_the_user_accepts_it()
    {
        using var repo = Phase5Runs.Repo();
        var person = new ScriptedApprovalPrompt();
        var clis = new Phase5Clis
        {
            // The judge of the run leaves it to a person; the judge of the revision approves.
            Decision = r => DebateIds.IsDebate(r.DebateId) ? VerdictDecision.Approved : VerdictDecision.EscalateToHuman,
        };
        await using var app = await Phase4App.OpenAsync(repo.Root, Phase5Composition.Services(Phase4Paths.Beside(repo.Root), person, clis.Providers), Phase5Roster.Agents());
        var project = app.Project;
        var delivery = app.Get<IDeliveryService>();
        var challenges = app.Get<IVerdictChallenges>();

        // The review escalated, the person approved the escalation, and the run ended Succeeded with that verdict: no judge approved it.
        var run = await Phase5Runs.SucceededAsync(app);
        var verdict = run.VerdictId!;
        var original = await StoredAsync(app, run, verdict);
        Assert.Equal((true, VerdictDecision.EscalateToHuman), (original.Valid, original.Verdict.Decision));
        var escalation = Assert.Single(await app.PayloadsAsync<ApprovalAnsweredPayload>(run.Id.Value), a => a.Payload.Action == OrchestratorAudit.ReviewEscalationAction);
        Assert.Equal((DecisionKind.ApprovalGranted, ScriptedApprovalPrompt.Person), (escalation.Entry.Kind, escalation.Payload.DecidedBy));
        AssertNotReady(await delivery.CheckAsync(project, run.Id, Ct), nameof(VerdictDecision.EscalateToHuman));
        var userHead = repo.Head;

        await using var revision = await challenges.ChallengeAsync(await PrepareAsync(app, verdict), Ct);
        var revised = await Phase5Checks.EndAsync(revision);
        Assert.True(revised is { Phase: DebatePhase.Closed, Outcome: DebateOutcome.Resolved }, revised.Summary);
        var kept = await RevisionVerdictAsync(app, run, verdict, revision, original);
        Assert.Equal(VerdictDecision.Approved, kept.Verdict.Decision);

        // The revision turns it towards an approval: it does not count until the person accepts it.
        Assert.Equal(ChallengeStatus.AwaitingAcceptance, await challenges.StatusAsync(project, verdict, Ct));
        AssertNotReady(await delivery.CheckAsync(project, run.Id, Ct), revision.Id.Value, kept.Id, "da la vuelta hacia una aprobación", "la aceptas expresamente");
        await AssertNothingDeliveredAsync(app, run, person, repo, userHead);

        // Accepted through the gate, in the revision debate and bound to its verdict: then the delivery is ready.
        await challenges.AcceptRevisionAsync(project, kept.Id, Ct);
        var acceptance = person.Requests[^1];
        Assert.Equal((DeliveryService.RevisionAcceptanceAction, revision.Id.Value), (acceptance.Action, acceptance.RunId));
        Assert.False(string.IsNullOrEmpty(acceptance.Binding));
        Assert.Contains(await app.PayloadsAsync<ApprovalAnsweredPayload>(revision.Id.Value), a => a.Entry.Kind == DecisionKind.ApprovalGranted && a.Payload.Action == DeliveryService.RevisionAcceptanceAction);
        Assert.Equal(ChallengeStatus.Accepted, await challenges.StatusAsync(project, verdict, Ct));
        var ready = await delivery.CheckAsync(project, run.Id, Ct);
        Assert.True(ready.Ready, ready.Reason);
        Assert.Equal(run.IntegrationCommit, ready.Commit);
        Assert.Contains("aceptaste", ready.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The challenge of <paramref name="verdict"/> as «Impugnar» prepares it: an adversarial revision with a participant of each
    /// vendor and the judge chosen by the service. It inherits who may not judge the run (Anthropic produced it, rule P),
    /// waives only the vendor that did not produce it, and changes the judge: never Codex's <c>juez</c>, which issued the verdict.
    /// </summary>
    private static async Task<DebateProposal> PrepareAsync(Phase4App app, string verdict)
    {
        var revision = new DebateRequest(
            app.Project,
            DebateMode.Adversarial,
            Reasons,
            Requirements(),
            new DebateSubject(DebateSubjectKind.Verdict, verdict),
            [new DebateParticipantRequest(Phase5Roster.Architect, DebateRole.Proponent), new DebateParticipantRequest(Phase5Roster.GreenCritic, DebateRole.Critic)],
            [],
            DebateJudgingKind.IndependentJudge,
            new DebateLimits(1, TimeSpan.FromMinutes(30), 400_000));
        var proposal = await app.Get<IVerdictChallenges>().PrepareAsync(new VerdictChallengeRequest(app.Project, verdict, Reasons, revision), Ct);
        Assert.True(proposal.Problems.Count == 0, string.Join(" | ", proposal.Problems));
        Assert.Equal((DebateMode.Adversarial, Reasons, Requirements().ContentHash), (proposal.Request.Mode, proposal.Request.Question, proposal.Request.Requirements.ContentHash));
        Assert.Equal(DebateJudgingKind.NonProducerJudge, proposal.Judging.Kind);
        var judge = Assert.Single(proposal.Judging.Judges);
        Assert.NotEqual(Phase5Roster.RunJudge, judge.Agent);
        Assert.Equal(VendorId.OpenAI, judge.Model.Vendor);
        Assert.Contains(VendorId.Anthropic, proposal.Judging.ProducerVendors);
        Assert.Equal([VendorId.OpenAI], proposal.Judging.WaivedParticipantVendors);
        return proposal;
    }

    /// <summary>The verdict of the run, as the store keeps it.</summary>
    private static async Task<StoredVerdict> StoredAsync(Phase4App app, RunResult run, string verdict) =>
        Assert.Single(await app.Get<IVerdictStore>().ListAsync(app.Project.ProjectId, run.Id.Value, Ct), v => v.Id == verdict);

    /// <summary>
    /// D-40: the verdict of the revision is kept in the unit and the scope of the challenged one, superseding it, announced
    /// there with <c>VerdictIssued</c>, valid, and named by the close of the revision; its judge is not the one that issued
    /// the challenged verdict and runs on the vendor that did not produce the run.
    /// </summary>
    private static async Task<StoredVerdict> RevisionVerdictAsync(Phase4App app, RunResult run, string verdict, IDebateRun revision, StoredVerdict original)
    {
        var kept = Assert.Single(await app.Get<IVerdictStore>().ListAsync(app.Project.ProjectId, run.Id.Value, Ct), v => v.Supersedes == verdict);
        Assert.Equal((true, run.Id.Value, original.Scope), (kept.Valid, kept.RunId, kept.Scope));
        Assert.NotEqual(Phase5Roster.RunJudge, kept.Verdict.Judge.Agent);
        Assert.Equal(VendorId.OpenAI, kept.Verdict.Judge.Model.Vendor);
        var issued = Assert.Single(await app.PayloadsAsync<VerdictIssuedPayload>(run.Id.Value), i => i.Payload.VerdictId == kept.Id).Payload;
        Assert.Equal((verdict, true, kept.Verdict.Decision), (issued.Supersedes, issued.Valid, issued.Decision));
        var (_, closed) = Assert.Single(await app.PayloadsAsync<DebateClosedPayload>(revision.Id.Value));
        Assert.Equal([kept.Id], closed.Verdicts);
        Assert.Empty(await app.Get<IVerdictStore>().ListAsync(app.Project.ProjectId, revision.Id.Value, Ct));
        return kept;
    }

    private static void AssertNotReady(DeliveryReadiness readiness, params string[] mentions)
    {
        Assert.False(readiness.Ready, readiness.Reason);
        Assert.Null(readiness.Commit);
        foreach (var mention in mentions)
        {
            Assert.Contains(mention, readiness.Reason, StringComparison.Ordinal);
        }
    }

    /// <summary>A delivery that is not ready asks nobody and leaves the user's branch where it was.</summary>
    private static async Task AssertNothingDeliveredAsync(Phase4App app, RunResult run, ScriptedApprovalPrompt person, TempGitRepo repo, string userHead)
    {
        var asked = person.Requests.Count;
        var result = await app.Get<IDeliveryService>().DeliverAsync(app.Project, run.Id, DeliveryMode.FastForwardOnly, Ct);
        Assert.False(result.Delivered, result.Message);
        Assert.Equal(asked, person.Requests.Count);
        Assert.Equal(userHead, repo.Head);
    }
}
