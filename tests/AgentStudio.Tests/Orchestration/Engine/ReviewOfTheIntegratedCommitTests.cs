using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Debate.Fakes;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// The engine with the real independent review behind it: the dossier comes from the run's stores, the verdict goes
/// through the real validator (with its one correction), and only the judge is scripted. The commit under review is the
/// last clean integration in the log, and an approval needs a passing system-captured test run of that very commit.
/// </summary>
public sealed class ReviewOfTheIntegratedCommitTests
{
    [Fact]
    public async Task A_run_with_an_integration_and_a_review_ends_approved_on_the_tests_of_the_integrated_commit()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/calc.cs", "Multiply")));
        JudgeDossier? reviewed = null;
        Verdict Approve(JudgeIdentity judge, JudgeDossier dossier)
        {
            reviewed = dossier;

            // A judge that read its dossier cites the test run of the commit under review (any test run, if there is none).
            var proof = dossier.Evidence
                .Where(e => e.Kind == EvidenceKind.TestRun)
                .OrderBy(e => e.SubjectCommit == dossier.CandidateCommit ? 0 : 1)
                .First();
            return ScriptedJudge.From(JudgeAnswers.Approval(dossier, proof.Id), judge);
        }

        var judges = new FakeJudgeFactory(launch =>
        {
            var judge = new JudgeIdentity(launch.Agent.Id, launch.Agent.Model with { Model = "gpt-5" });
            return new ScriptedJudge(judge, dossier => Approve(judge, dossier), (dossier, _, _) => Approve(judge, dossier));
        });
        var verdicts = new FakeVerdictStore();
        harness.ReviewPort = new IndependentReview(
            harness.Decisions,
            new JudgeDossierBuilder(harness.Decisions, harness.Runs, harness.Plans, harness.RequirementStore, harness.Evidence, new JudgingOptions(harness.Paths.DossierRoot)),
            harness.Runs,
            judges,
            verdicts,
            harness.Evidence,
            harness.Time);
        var plan = Plan(
            Work("T1").Verified("build", "test"),
            Node("I1", WorkTaskKind.Integration).After(On("T1")) with { Covers = [] },
            Node("R1", WorkTaskKind.Review).After(On("I1")));

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        var verdict = verdicts.Stored[0];
        Assert.True(verdict.Valid, "The verdict broke: " + string.Join("; ", verdict.Issues.Select(i => $"{i.Code} {i.Message}")));
        Assert.Single(verdicts.Stored);
        Assert.Equal(VerdictDecision.Approved, verdict.Verdict.Decision);
        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(verdict.Id, result.VerdictId);
        Assert.Empty(harness.Approvals.Requests); // the judge approved; nobody had to
        Assert.NotNull(result.IntegrationCommit);
        Assert.Equal(result.IntegrationCommit, reviewed!.CandidateCommit);
        Assert.Empty(reviewed.IntegrityProblems);
    }
}
