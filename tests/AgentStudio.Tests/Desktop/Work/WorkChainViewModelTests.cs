using System.IO;
using System.Text;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Work;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Tests.Orchestration.WorkTracking;

namespace AgentStudio.Tests.Desktop.Work;

/// <summary>A chain the test fixes, to open nodes the real reader would give from ids the log knows.</summary>
internal sealed class FixedChain : ITraceChainReader
{
    public IReadOnlyList<TraceChainStep> Steps { get; set; } = [];

    public Task<TraceChain> ChainAsync(ProjectContext project, TraceRef start, CancellationToken cancellationToken) => Task.FromResult(new TraceChain(start, Steps));
}

/// <summary>
/// docs/16 F9-4 on the screen: the chain requirement, milestone, task, attempt, change, verification, verdict and delivery is walked from a
/// card of the board, read from the verified log; each node opens what the app already has, and the node it has no way to open is shown
/// without a link.
/// </summary>
public sealed class WorkChainViewModelTests
{
    private static readonly string CommitA = new('a', 40);
    private static readonly string CommitB = new('b', 40);

    private static CancellationToken Ct => WorkTestWorld.Ct;

    private static async Task<WorkTestWorld> CompleteAsync()
    {
        var test = await WorkTestWorld.OpenAsync();
        var world = test.World;
        var view = await world.OpenMilestoneAsync();
        await world.StartRunAsync();
        await world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, WorkWorld.Run1), "manuel", Ct);
        await world.LaunchAsync(WorkWorld.Run1, "T1", 1);
        await world.EvidenceAsync(WorkWorld.Run1, "T1", 1, EvidenceKind.CodeDiff, "diff1");
        await world.EvidenceAsync(WorkWorld.Run1, "T1", 1, EvidenceKind.TestRun, "test1", CommitB);
        await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T1");
        await world.DeliverAsync(WorkWorld.Run1, CommitA);
        await test.Document.ReloadAsync();
        test.Document.Milestones.SelectedItem = test.Document.Milestones.Items.Single();
        await test.Document.Board.ReloadAsync();
        return test;
    }

    [Fact]
    public async Task The_chain_is_walked_from_a_card_of_the_board_through_every_stage_to_the_delivery()
    {
        using var test = await CompleteAsync();
        var card = test.Document.Board.Columns.SelectMany(c => c.Cards).Single(c => c.Card.Kind == BoardCardKind.Requirement && c.Card.Id == "R1");

        card.ViewChainCommand.Execute(null);
        await test.Document.Chain.ReloadAsync();

        // «Ver cadena» takes the person to the chain, centred on the card.
        Assert.Same(test.Document.Chain, test.Document.SelectedSection);
        Assert.Equal(TraceRef.Requirement("R1"), test.Document.Context.Focus);
        Assert.Equal(
            [TraceChainStage.Requirement, TraceChainStage.Milestone, TraceChainStage.Task, TraceChainStage.Attempt, TraceChainStage.Change, TraceChainStage.Change, TraceChainStage.Verification, TraceChainStage.Verdict, TraceChainStage.Delivery],
            test.Document.Chain.Steps.Select(s => s.Step.Stage));
        Assert.Equal(Texts.Get("Work_Stage_Verdict"), test.Document.Chain.Steps.Single(s => s.Step.Stage == TraceChainStage.Verdict).StageText);
        Assert.Equal("Add Calc.Multiply", test.Document.Chain.Steps[0].Title);
        Assert.All(test.Document.Chain.Steps, s => Assert.Null(s.SuggestedText));
    }

    [Fact]
    public async Task A_node_with_no_way_to_be_opened_is_shown_without_a_link_and_a_proof_or_a_verdict_has_one()
    {
        using var test = await CompleteAsync();
        test.Document.Context.LookAt(TraceRef.Requirement("R1"));
        await test.Document.Chain.ReloadAsync();

        var steps = test.Document.Chain.Steps;
        Assert.All(steps.Where(s => s.Step.Node.Kind is TraceNodeKind.Commit or TraceNodeKind.Task or TraceNodeKind.Attempt or TraceNodeKind.Requirement or TraceNodeKind.Milestone),
            s => Assert.True(s.HasNoLink && !s.CanOpen, s.ToString()));
        Assert.All(steps.Where(s => s.Step.Node.Kind is TraceNodeKind.Evidence or TraceNodeKind.Verdict), s => Assert.True(s.CanOpen, s.ToString()));
    }

    [Fact]
    public async Task A_saved_proof_opens_with_its_text_and_a_verdict_with_its_decision()
    {
        var chain = new FixedChain();
        using var test = await WorkTestWorld.OpenAsync((_, backend) => backend with { Chain = chain });
        var world = test.World;
        await world.StartRunAsync();
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T1");
        var record = await world.Instance.Evidence.CaptureAsync(
            world.ProjectId,
            new EvidenceDraft(EvidenceKind.CodeDiff, EvidenceOrigin.CapturedBySystem, "Diff de T1", "agent-studio"),
            new MemoryStream(Encoding.UTF8.GetBytes("--- a/Calc.cs\n+++ b/Calc.cs\n")),
            Ct);
        chain.Steps =
        [
            new(TraceChainStage.Change, TraceRef.Evidence(record.Id.Value), "Diff de T1", false),
            new(TraceChainStage.Verdict, TraceRef.Verdict("v1"), null, false),
            new(TraceChainStage.Delivery, TraceRef.Commit(CommitA), null, false),
        ];
        test.Document.Context.LookAt(TraceRef.Requirement("R1"));
        await test.Document.Chain.ReloadAsync();

        var steps = test.Document.Chain.Steps;
        steps[0].OpenCommand.Execute(null);
        await WaitAsync(() => test.Document.Chain.HasOpenedProof);
        Assert.Equal("Diff de T1", test.Document.Chain.OpenedProof!.Title);
        Assert.Contains("+++ b/Calc.cs", test.Document.Chain.OpenedProof.Text, StringComparison.Ordinal);
        Assert.Equal(Texts.Format("Debate_Evidence_HashOk", record.ContentSha256[..12]), test.Document.Chain.OpenedProof.HashText);

        steps[1].OpenCommand.Execute(null);
        await WaitAsync(() => test.Document.Chain.HasOpenedVerdict);
        Assert.Equal(AgentStudio.Desktop.Debate.VerdictTexts.Decision(VerdictDecision.Approved), test.Document.Chain.OpenedVerdict!.Decision);
        Assert.Equal(Texts.Get("Work_Verdict_Valid"), test.Document.Chain.OpenedVerdict.Validity);

        // The delivery is a commit, and the app has nowhere to open a commit: no link, said in words.
        Assert.True(steps[2].HasNoLink);
        Assert.False(steps[2].OpenCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_proof_that_the_store_does_not_have_says_so_instead_of_showing_nothing()
    {
        var chain = new FixedChain { Steps = [new(TraceChainStage.Verification, TraceRef.Evidence("no-such-proof"), null, false)] };
        using var test = await WorkTestWorld.OpenAsync((_, backend) => backend with { Chain = chain });
        test.Document.Context.LookAt(TraceRef.Requirement("R1"));
        await test.Document.Chain.ReloadAsync();

        test.Document.Chain.Steps[0].OpenCommand.Execute(null);
        await WaitAsync(() => test.Document.Chain.HasOpenedProof);

        Assert.Null(test.Document.Chain.OpenedProof!.Text);
        Assert.Equal(Texts.Get("Debate_Evidence_Missing"), test.Document.Chain.OpenedProof.Description);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, Ct);
        }

        Assert.True(condition(), "The node did not open.");
    }
}
