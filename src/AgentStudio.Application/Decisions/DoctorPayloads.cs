using AgentStudio.Domain.Common;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Graph;
using AgentStudio.Domain.Tracing;

namespace AgentStudio.Application.Decisions;

/// <summary>
/// A fix of Auto Doctor was applied, or undone (docs/20 D-10 and D-11, ADR-0061). Undoing is another entry of this kind whose
/// <paramref name="Undoes"/> is the sequence of the first. It belongs to no run (<see cref="DecisionPayload.NoRun"/>) and carries no free text, no
/// path of the machine and nothing of a token: only the two codes.
/// </summary>
/// <param name="FixCode">The code of the fix (<see cref="Application.Diagnostics.DoctorFix.Code"/>).</param>
/// <param name="CheckCode">The code of the check or the discrepancy it answers (<c>doctor.…</c> or <c>doc.…</c>).</param>
/// <param name="Undoes">The sequence of the entry this one undoes, or null when it applies.</param>
public sealed record DoctorFixAppliedPayload(string FixCode, string CheckCode, long? Undoes) : DecisionPayload
{
    /// <summary>A fix applied, outside every run.</summary>
    /// <exception cref="ArgumentException">A code that is not a code.</exception>
    public static DoctorFixAppliedPayload Applying(string fixCode, string checkCode) => Of(fixCode, checkCode, null);

    /// <summary>The undo of the entry <paramref name="undoes"/>, outside every run.</summary>
    /// <exception cref="ArgumentException">A code that is not a code, or a sequence below 1.</exception>
    public static DoctorFixAppliedPayload Undoing(string fixCode, string checkCode, long undoes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(undoes, 1);
        return Of(fixCode, checkCode, undoes);
    }

    private static DoctorFixAppliedPayload Of(string fixCode, string checkCode, long? undoes)
    {
        if (!DoctorCodes.IsCode(fixCode) || !DoctorCodes.IsCode(checkCode))
        {
            throw new ArgumentException("A fix and a check are named by their codes: lowercase words joined by '.' and '-'.", nameof(fixCode));
        }

        return new DoctorFixAppliedPayload(fixCode, checkCode, undoes) { RunId = NoRun };
    }
}

/// <summary>
/// A relation that a person accepted from the proposal of an AI for Auto Graph (docs/20 D-21): the two nodes by their ids in the model and the kind.
/// The reason the agent gave is not here: it is text of an agent, so it stays in the evidence of the proposal and in the store of relations.
/// </summary>
public sealed record ProposedRelation
{
    public ProposedRelation(string from, string to, GraphEdgeKind kind)
    {
        // The same rules as the ends of a relation of the model: one line, bounded, never a node related to itself.
        _ = new GraphEdge(from, to, kind, GraphEdgeOrigin.Inferred, new GraphInference("agent", "model", DateTimeOffset.UnixEpoch, "reason"));
        From = from;
        To = to;
        Kind = kind;
    }

    public string From { get; }

    public string To { get; }

    public GraphEdgeKind Kind { get; }
}

/// <summary>
/// The relations an AI proposed for Auto Graph that the person approved (docs/20 D-21, ADR-0062): the commit and the fingerprint of the model they
/// refer to, the text of the agent as evidence (<paramref name="Proposal"/>), the relations kept, how many were discarded (an end that is not in the
/// model, or a repeat) and who proposed them. It belongs to no run. It exists only with the action «Proponer relaciones» (decision 7 (a)).
/// </summary>
public sealed record GraphRelationsProposedPayload(
    string Commit,
    string ModelHash,
    EvidenceId Proposal,
    IReadOnlyList<ProposedRelation> Accepted,
    int Discarded,
    string Agent,
    string Model) : DecisionPayload
{
    /// <summary>The most relations an agent may propose in one launch (docs/20 D-21); accepted and discarded together.</summary>
    public const int MaxRelations = 50;

    /// <summary>The proposal as the log records it, outside every run.</summary>
    /// <exception cref="ArgumentException">A commit that is not a full id, a model that is not a SHA-256, a relation repeated, no relation at all, more than <see cref="MaxRelations"/>, a count below 0, or an agent or a model that is not one line.</exception>
    public static GraphRelationsProposedPayload Proposing(
        string commit,
        string modelHash,
        EvidenceId proposal,
        IReadOnlyList<ProposedRelation> accepted,
        int discarded,
        string agent,
        string model)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        if (!TraceRef.IsFullCommitId(commit))
        {
            throw new ArgumentException("The commit is a full id (40 or 64 lowercase hexadecimal characters).", nameof(commit));
        }

        if (!ContentHash.IsSha256Hex(modelHash))
        {
            throw new ArgumentException("The model is named by its SHA-256 (64 lowercase hexadecimal characters).", nameof(modelHash));
        }

        if (accepted.Any(r => r is null) || accepted.Distinct().Count() != accepted.Count)
        {
            throw new ArgumentException("Each accepted relation is there once.", nameof(accepted));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(discarded);
        if (accepted.Count + discarded is < 1 or > MaxRelations)
        {
            throw new ArgumentException($"A proposal holds 1 to {MaxRelations} relations between those accepted and those discarded.", nameof(accepted));
        }

        if (!IsName(agent) || !IsName(model))
        {
            throw new ArgumentException("The agent and the model are names of one line.", nameof(agent));
        }

        return new GraphRelationsProposedPayload(commit, modelHash, proposal, [.. accepted], discarded, agent, model) { RunId = NoRun };
    }

    private static bool IsName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 100 && value == value.Trim() && !value.Any(char.IsControl);
}
