using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Common;

namespace AgentStudio.Debate.Arena;

/// <summary>What the screens of a debate show (docs/04 §17.3), built from what the log says; every text is read against its anchored hash elsewhere.</summary>
public static class DebateViews
{
    /// <summary>The participants as <c>DebateOpened</c> recorded them, with the agents behind their labels.</summary>
    public static IReadOnlyList<DebateParticipantView> Participants(DebateOpenedPayload opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        return [.. opened.Participants.Select(p => new DebateParticipantView(p.Label, p.Role, p.Agent, p.Provider, p.Vendor, p.Model))];
    }

    /// <summary>The label of the participant <paramref name="agent"/> (<c>P1</c>…), or the agent itself when the opening does not name it.</summary>
    public static string LabelOf(DebateOpenedPayload opened, AgentId agent)
    {
        ArgumentNullException.ThrowIfNull(opened);
        return opened.Participants.FirstOrDefault(p => p.Agent == agent)?.Label ?? agent.Value;
    }

    /// <summary>A published contribution, with its text when it could be read against its hash.</summary>
    public static DebateContributionView Contribution(DebateOpenedPayload opened, DebateContributionRecordedPayload payload, string? text)
    {
        ArgumentNullException.ThrowIfNull(opened);
        ArgumentNullException.ThrowIfNull(payload);
        return new DebateContributionView(
            ContributionLabels.Of(payload.Index),
            payload.Index,
            payload.Round,
            LabelOf(opened, payload.Author),
            payload.Kind,
            payload.RepliesTo is { } target ? ContributionLabels.Of(target) : null,
            payload.Evidence,
            payload.Sha256,
            text,
            payload.Cites ?? [],
            payload.UnresolvedCitations ?? [],
            payload.Redactions,
            payload.Finished,
            payload.AnsweredAt);
    }
}
