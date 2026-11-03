using System.Runtime.CompilerServices;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Debate.Challenges;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Tests.Integration.Phase5.Live;

/// <summary>
/// The challenge of a verdict of the demonstrations (docs/15 F5-7, D-38 to D-40), on the same project: by default the last
/// verdict of the latest implementation audit (the one Codex gave on the run Claude produced), or the one named in
/// <c>AGENTSTUDIO_PHASE5_CHALLENGE_VERDICT</c>. The reasons are the user's; the revision is an adversarial debate whose
/// proponent (<c>arquitecto</c>, Claude) defends the verdict and whose critic (<c>critico</c>, Codex) defends the challenge,
/// with two rounds. It inherits who may not judge the object (Anthropic produced the run) and changes the judge: another
/// agent of the template, <c>juez-revision</c> (Codex with another model). Its verdict supersedes the challenged one in the
/// unit of that verdict, and the delivery would need the user to accept a revision that turns towards an approval: the
/// demonstration never accepts it.
/// </summary>
/// <remarks>
/// It spends quota (about five runs, <see cref="DemoPlan"/>), so it needs <c>AGENTSTUDIO_PROVIDER_TESTS=1</c>,
/// <c>AGENTSTUDIO_PHASE5_DEMOS=1</c> and <c>AGENTSTUDIO_CAPTURE_DIR</c>, and it runs after the implementation audit.
/// </remarks>
[Trait("Category", "ProviderLive")]
public sealed class Phase5ChallengeDemoLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Challenge_of_a_demo_verdict_opens_a_revision_debate_with_another_judge()
    {
        DemoSettings.RequireLiveDemo();
        var started = DateTimeOffset.UtcNow;
        var workspace = DemoWorkspace.Resolve();
        DemoRepository.EnsureCalc(workspace.Repository);
        await using var app = await DemoApp.OpenAsync(workspace.Repository, workspace, DemoRoster.Debates(), DotNetVerificationProfile.Propose(), Ct);
        var target = await TargetAsync(app);
        Assert.SkipWhen(
            target is null,
            $"There is no verdict to challenge: run the implementation audit of {nameof(Phase5DebateDemoLiveTests)} first, or name a verdict in {DemoSettings.ChallengeVerdictVariable}.");
        var challenged = target!;
        var unit = challenged.RunId!;
        var tools = await DemoFacts.ToolsAsync(app, Ct);
        var reasons = Reasons(challenged);
        var requirements = await app.Get<IRequirementStore>().FindAsync(app.Project.ProjectId, challenged.Verdict.RequirementSetHash, Ct)
            ?? throw new InvalidOperationException($"The requirements {challenged.Verdict.RequirementSetHash} of the challenged verdict are not stored.");

        // The service adjusts the revision (adversarial, about the verdict, on its requirements, the reasons as the question)
        // and picks the judges; the card only offers the participants and the limits.
        var revision = new DebateRequest(
            app.Project,
            DebateMode.Adversarial,
            reasons,
            requirements,
            new DebateSubject(DebateSubjectKind.Verdict, challenged.Id),
            DebateDemo.ChallengeParticipants,
            [],
            DebateJudgingKind.IndependentJudge,
            DebateDemo.Limits);
        var challenges = app.Get<IVerdictChallenges>();
        var proposal = await challenges.PrepareAsync(new VerdictChallengeRequest(app.Project, challenged.Id, reasons, revision), Ct);
        Assert.True(proposal.Problems.Count == 0, "The challenge cannot open: " + string.Join(" ", proposal.Problems));
        Assert.DoesNotContain(proposal.Judging.Judges, j => j.Agent == challenged.Verdict.Judge.Agent);

        var log = await app.LoadLogAsync(Ct);
        var target2 = ChallengeLog.ObjectOf(log.Entries, unit, challenged.Id);
        var facts = new DemoChallengeFacts
        {
            Verdict = challenged.Id,
            Unit = unit,
            Decision = challenged.Verdict.Decision.ToString(),
            Judge = new DemoJudge(challenged.Verdict.Judge.Agent.Value, challenged.Verdict.Judge.Model.Provider.Value, challenged.Verdict.Judge.Model.Vendor.Value, challenged.Verdict.Judge.Model.Model),
            Reasons = reasons,
            ExcludedVendors = [.. target2.ExcludedVendors.Select(v => v.Value).Order(StringComparer.Ordinal)],
            Notes = [.. proposal.Estimate.Notes],
            Status = ChallengeStatus.None.ToString(),
        };

        var outcome = await DebateDemoRunner.RunAsync(
            app,
            new DemoStart(DemoPlan.Challenge, "Impugnación de un veredicto de las demostraciones", nameof(Phase5ChallengeDemoLiveTests) + "." + Method(), started, tools),
            proposal,
            () => challenges.ChallengeAsync(proposal, Ct),
            runTests: false,
            challengedUnit: unit,
            challenge: facts,
            [$"Se impugna el veredicto {challenged.Id} ({challenged.Verdict.Decision}) de {unit}, que emitió {challenged.Verdict.Judge.Agent}."],
            Ct);

        // Where the challenge stands now, as the delivery and the card read it (D-40); the capture is written again with it.
        var status = await challenges.StatusAsync(app.Project, challenged.Id, Ct);
        var capture = outcome.Capture with { Challenge = facts with { Revision = outcome.Debate.Value, Status = status.ToString() } };
        DemoCaptures.Write(DemoSettings.CaptureDirectory!, capture);
        var after = await app.LoadLogAsync(Ct);
        var challengedEntry = after.Entries
            .Where(e => e.Kind == DecisionKind.VerdictChallenged)
            .Select(e => DecisionPayloadSerializer.Deserialize(e.Kind, e.PayloadJson))
            .OfType<VerdictChallengedPayload>()
            .LastOrDefault(p => p.Verdict == challenged.Id);

        DebateDemoRunner.AssertDemonstrated(outcome with { Capture = capture }, DebateDemo.ChallengeParticipants.Count, expectWaiver: true);
        Assert.NotNull(challengedEntry);
        Assert.Equal(outcome.Debate.Value, challengedEntry.RevisionDebate);
        Assert.Equal(unit, challengedEntry.RunId);
        Assert.Contains(capture.Verdicts, v => v.Supersedes == challenged.Id);
        Assert.NotEqual(ChallengeStatus.None, status);
    }

    /// <summary>The verdict to challenge: the one named in the variable, or the last verdict the judge of the latest implementation audit gave.</summary>
    private static async Task<StoredVerdict?> TargetAsync(DemoApp app)
    {
        var verdicts = await app.Get<IVerdictStore>().ListAsync(app.Project.ProjectId, null, Ct);
        if (DemoSettings.ChallengeVerdict is { } named)
        {
            return verdicts.LastOrDefault(v => v.Id == named);
        }

        var audits = (await app.Get<IDebateRegistry>().ListAsync(app.Project.ProjectId, Ct))
            .Where(d => d.Mode == DebateMode.ImplementationAudit)
            .OrderByDescending(d => d.CreatedAt);
        foreach (var audit in audits)
        {
            // The audit's own judge, not a revision kept in the same unit: running the challenge again revises the same verdict.
            if (verdicts.LastOrDefault(v => v.RunId == audit.Id.Value && audit.Judges.Any(j => j.Id == v.Verdict.Judge.Agent)) is { } last)
            {
                return last;
            }
        }

        return null;
    }

    /// <summary>The user's reasons (D-38): what round 1 of the revision starts from.</summary>
    private static string Reasons(StoredVerdict challenged) =>
        (challenged.Verdict.Assessments ?? []).Any(a => a?.Requirement.Value == "RQ-01")
            ? $"Impugno el veredicto {challenged.Id}: da por resuelta la cuestión de RQ-01 sin discutir si una sola prueba (Multiply(2, 3) devuelve 6) basta para demostrar la operación, y no dice nada del cero, de los negativos ni del desbordamiento. Pido que se revise si las evidencias que cita sostienen esa decisión."
            : $"Impugno el veredicto {challenged.Id}: sus conclusiones no se apoyan lo bastante en las evidencias que cita. Pido que se revise con otro juez si esas evidencias sostienen la decisión.";

    private static string Method([CallerMemberName] string method = "") => method;
}
