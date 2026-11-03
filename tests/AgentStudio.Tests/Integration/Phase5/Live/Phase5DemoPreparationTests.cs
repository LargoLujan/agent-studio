using AgentStudio.Application.Coordination;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Tests.Integration.Phase5.Live;

/// <summary>
/// What every demonstration would show the user to approve, without spending anything (D-06 and the checklist of ADR-0030,
/// steps 1 to 6): the workspace and its repository, the composition of Phase 5 on SQLite with the real providers (the probes
/// run <c>--version</c>, <c>claude auth status</c> and <c>codex login status</c>, never a prompt), the proposal of each debate
/// that can be prepared now (the review modes and the challenge need the runs before them), and the audit of the copy of
/// Agent Studio with both judges: rule P refuses the Claude judge and lets Codex judge. Preparing writes nothing in the log,
/// launches no agent and spends no quota, so it needs only <c>AGENTSTUDIO_PHASE5_DEMOS=1</c>; it is the first thing to run.
/// </summary>
[Trait("Category", "ProviderLive")]
public sealed class Phase5DemoPreparationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Prepares_every_demonstration_without_spending_quota()
    {
        DemoSettings.RequireDemos();
        var started = DateTimeOffset.UtcNow;
        var workspace = DemoWorkspace.Resolve();
        var head = DemoRepository.EnsureCalc(workspace.Repository);
        var prepared = new List<DemoPreparedFacts>();
        var notes = new List<string> { $"Espacio de trabajo de las demostraciones preparado; repositorio de los debates en {head[..12]}." };
        IReadOnlyList<DemoTool> tools;

        await using (var app = await DemoApp.OpenAsync(workspace.Repository, workspace, DemoRoster.Debates(), DotNetVerificationProfile.Propose(), Ct))
        {
            tools = await DemoFacts.ToolsAsync(app, Ct);
            var coordinator = app.Get<IDebateCoordinator>();
            foreach (var demo in new[] { DebateDemo.Free, DebateDemo.Adversarial, DebateDemo.Architecture, DebateDemo.Tribunal })
            {
                var request = new DebateRequest(app.Project, demo.Mode, demo.Question, demo.Requirements!(DateTimeOffset.UtcNow), new DebateSubject(DebateSubjectKind.Commit, null), demo.Participants, demo.Judges, demo.Judging, DebateDemo.Limits);
                prepared.Add(Facts(demo.Block, await coordinator.PrepareAsync(request, Ct)));
            }

            if (await Phase5DebateDemoLiveTests.SubjectRunAsync(app) is { } subject)
            {
                foreach (var demo in new[] { DebateDemo.Implementation, DebateDemo.Orchestrator })
                {
                    var requirements = demo.Mode == DebateMode.OrchestratorReview
                        ? DebateModes.OrchestratorReview(DateTimeOffset.UtcNow)
                        : await app.Get<IRequirementStore>().FindAsync(app.Project.ProjectId, subject.RequirementSetHash, Ct);
                    if (requirements is null)
                    {
                        notes.Add($"{demo.Title}: los requisitos de la ejecución {subject.Run} no están guardados.");
                        continue;
                    }

                    var request = new DebateRequest(app.Project, demo.Mode, demo.QuestionFor(subject.Run), requirements, new DebateSubject(DebateSubjectKind.Run, subject.Run), demo.Participants, demo.Judges, demo.Judging, DebateDemo.Limits);
                    prepared.Add(Facts(demo.Block, await coordinator.PrepareAsync(request, Ct)));
                }
            }
            else
            {
                notes.Add("Todavía no hay ejecución objeto: los dos modos de revisión se preparan cuando exista (primero la prueba de la ejecución objeto).");
            }
        }

        // The audit: the copy of HEAD as its own project, the frozen requirements, the profile, and both judges.
        var copy = AuditDemo.Copy(workspace);
        notes.AddRange(copy.Notes);
        var profile = AuditDemo.Profile(DemoSettings.AuditExclusions);
        ProjectAuditProposal codex;
        ProjectAuditProposal claude;
        await using (var app = await DemoApp.OpenAsync(copy.Root, workspace, DemoRoster.Audit(), profile, Ct))
        {
            var requirements = await AuditDemo.FreezeAsync(app, Ct);
            var audits = app.Get<IProjectAuditService>();
            codex = await audits.PrepareAsync(new ProjectAuditRequest(app.Project, requirements.ContentHash, DemoRoster.Judge, AuditDemo.DeclaredAuthors), Ct);
            claude = await audits.PrepareAsync(new ProjectAuditRequest(app.Project, requirements.ContentHash, DemoRoster.ClaudeJudge, AuditDemo.DeclaredAuthors), Ct);
            prepared.Add(new DemoPreparedFacts(
                DemoPlan.Audit,
                "WholeProject",
                codex.CanJudge ? "juez de un vendor que no produjo el código" : "sin juez posible",
                codex.Judge is { } judge ? [new DemoJudge(judge.Id.Value, judge.Model.Provider.Value, judge.Model.Vendor.Value, judge.Model.Model)] : [],
                [],
                codex.CanJudge ? 2 : 0,
                codex.EstimatedTokens.Value,
                codex.Problems,
                codex.ApprovalText));
            notes.Add($"Auditoría: requisitos {requirements.ContentHash[..12]} congelados ({requirements.Requirements.Count}); HEAD {codex.HeadCommit}; {(codex.UncommittedChanges ? "con cambios sin confirmar" : "sin cambios sin confirmar")}; con el juez de Claude {(claude.CanJudge ? "PODRÍA juzgar" : "no puede juzgar (regla P)")}.");
        }

        var capture = new DemoCapture
        {
            Block = DemoPlan.Preparation,
            Title = "Preparación sin cuota",
            Test = nameof(Phase5DemoPreparationTests) + "." + nameof(Prepares_every_demonstration_without_spending_quota),
            StartedAt = started,
            FinishedAt = DateTimeOffset.UtcNow,
            Result = prepared.All(p => p.Problems.Count == 0) && !claude.CanJudge
                ? $"{prepared.Count} propuestas sin problemas; la regla P rechaza al juez de Claude en la auditoría."
                : "Alguna propuesta tiene problemas: ver la tabla.",
            Completed = prepared.All(p => p.Problems.Count == 0) && !claude.CanJudge,
            Tools = tools,
            Notes = notes,
            Prepared = prepared,
        };
        if (DemoSettings.CaptureDirectory is { } folder)
        {
            DemoCaptures.Write(folder, capture);
        }

        TestContext.Current.TestOutputHelper?.WriteLine(DemoScrub.Text(string.Join("\n", notes.Concat(prepared.Select(p => $"{p.Block}: {p.Judging}, {p.MaxAgentRuns} ejecuciones, {p.EstimatedTokens} tokens; {string.Join(" ", p.Problems)}")))));

        Assert.All(prepared, p => Assert.True(p.Problems.Count == 0, $"{p.Block}: {string.Join(" ", p.Problems)}"));
        Assert.All(prepared.Where(p => p.Block != DemoPlan.Audit), p => Assert.Contains(VendorId.OpenAI.Value, p.Judges.Select(j => j.Vendor)));
        Assert.True(codex.CanJudge, "The Codex judge cannot audit: " + string.Join(" ", codex.Problems));
        Assert.False(claude.CanJudge, "Rule P must refuse the Claude judge on code declared Anthropic's.");
        Assert.Empty(codex.ProducerVendors);
        Assert.Empty(codex.SkippedSteps);
    }

    private static DemoPreparedFacts Facts(string block, DebateProposal proposal) => new(
        block,
        proposal.Request.Mode.ToString(),
        proposal.Judging.Kind.ToString(),
        [.. proposal.Judging.Judges.Select(Judge)],
        [.. (proposal.Judging.WaivedParticipantVendors ?? new HashSet<VendorId>()).Select(v => v.Value).Order(StringComparer.Ordinal)],
        proposal.Estimate.MaxAgentRuns,
        proposal.Estimate.Tokens.Value,
        proposal.Problems,
        proposal.ApprovalText);

    private static DemoJudge Judge(JudgeIdentity judge) => new(judge.Agent.Value, judge.Model.Provider.Value, judge.Model.Vendor.Value, judge.Model.Model);
}
