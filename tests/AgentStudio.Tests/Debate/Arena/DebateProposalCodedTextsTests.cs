using AgentStudio.Application.Coordination;
using AgentStudio.Application.Localization;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Debate.Fakes;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>
/// The problems and the notes of the proposal of a debate carry a code with each sentence (docs/17 D-47, ADR-0054 addendum of
/// 2026-11-12): the desktop paints the code in the language in use, and the sentences stay as the layer wrote them.
/// </summary>
public sealed class DebateProposalCodedTextsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_participant_that_is_not_in_the_team_is_a_problem_with_its_code_and_a_sentence_for_each_code()
    {
        using var world = new ScriptedDebateWorld();
        var request = world.FreeOnAnthropic() with
        {
            Participants = [new DebateParticipantRequest(new AgentId("fantasma"), DebateRole.Proponent), new DebateParticipantRequest(ScriptedDebateWorld.Revisor, DebateRole.Critic)],
        };

        var proposal = await world.Coordinator.PrepareAsync(request, Ct);

        Assert.NotEmpty(proposal.Problems);
        Assert.NotNull(proposal.ProblemTexts);
        Assert.Equal(proposal.Problems.Count, proposal.ProblemTexts.Count);
        Assert.Contains(UserText.Of(DebateUserTexts.ProblemParticipantMissing, "fantasma"), proposal.ProblemTexts);
        Assert.Contains(proposal.Problems, p => p.Contains("fantasma", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_note_of_the_estimate_has_its_code()
    {
        using var world = new ScriptedDebateWorld();

        var proposal = await world.Coordinator.PrepareAsync(world.FreeOnAnthropic(), Ct);

        Assert.NotNull(proposal.Estimate.NoteTexts);
        Assert.Equal(proposal.Estimate.Notes.Count, proposal.Estimate.NoteTexts.Count);
        Assert.Contains(UserText.Of(DebateUserTexts.NoteEstimated), proposal.Estimate.NoteTexts);
        Assert.Contains(UserText.Of(DebateUserTexts.NoteMaxRuns, 10, 2, 4), proposal.Estimate.NoteTexts);
    }

    [Fact]
    public async Task A_refused_opening_keeps_the_english_message_and_gives_the_code()
    {
        using var world = new ScriptedDebateWorld();
        var request = world.FreeOnAnthropic() with
        {
            Participants = [new DebateParticipantRequest(new AgentId("fantasma"), DebateRole.Proponent), new DebateParticipantRequest(ScriptedDebateWorld.Revisor, DebateRole.Critic)],
        };
        var proposal = await world.Coordinator.PrepareAsync(request, Ct);

        var refusal = await Assert.ThrowsAsync<UserTextException>(() => world.Coordinator.OpenAsync(proposal, Ct));

        Assert.Equal(DebateUserTexts.CannotOpen, refusal.Text.Code);
        Assert.StartsWith("The debate cannot be opened", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_provider_is_named_as_the_person_knows_it_never_by_its_id()
    {
        Assert.Equal("Claude Code", ProviderNames.Of(ProviderId.ClaudeCode));
        Assert.Equal("Codex CLI", ProviderNames.Of(ProviderId.CodexCli));
        Assert.Equal("Gemini CLI", ProviderNames.Of(ProviderId.GeminiCli));
        Assert.Equal("otro-cli", ProviderNames.Of(new ProviderId("otro-cli")));
    }
}
