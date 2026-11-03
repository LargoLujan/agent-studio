using System.Globalization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Providers;
using HashFunctions = AgentStudio.Domain.Common.ContentHash;

namespace AgentStudio.Application.Debate;

/// <summary>
/// The canonical SHA-256 of the immutable header of a debate (<see cref="DebateRecord"/>) and of a whole-project audit
/// (<see cref="ProjectAuditRecord"/>), with the pattern of <see cref="RunHeaderHash"/> (docs/15 D-01 and D-06).
/// <c>DebateOpened</c> and <c>ProjectAuditStarted</c> anchor <see cref="Of(DebateRecord)"/>; resuming a debate and
/// launching a judge recompute it from the stored row and refuse any other (N-05), so a header rewritten behind the
/// store's back (a participant given <c>Bash</c>, a judge of the producers' vendor, a looser limit) is never used.
/// <see cref="Proposal(DebateRecord, string)"/> is the hash of what the user was shown before anything was written, which
/// the approval binds (<c>ApprovalRequest.Binding</c>) and <c>DebateOpened</c> repeats.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The header covers the time it was created (as an instant), the mode, the requirements, the evidence of the
/// question, the subject and the commit of the copy, every participant with its label, role and every member of its
/// definition, every judge with every member of its definition, the disposition, the producers and the waivers, the limits,
/// the launches allowed and the verdict a revision challenges. The id and the project are the keys it is found by, not part
/// of it.</item>
/// <item>Each agent is written with the encoding of <see cref="RosterHash"/>, as a roster of one. The participants and the
/// judges keep their order (it decides the order of publication and which judge reads the proposals reversed); the vendor
/// sets are sets, written in ordinal order.</item>
/// <item>Fields are length-prefixed (<see cref="HashFunctions.Canonical"/>); a value that may be missing is written as
/// <c>-</c> when missing and with a leading <c>=</c> otherwise, so a missing value is never an empty one.</item>
/// <item>The proposal leaves out what only exists once the debate opens (its id, its project, the time it is created and
/// the evidence of the question) and takes the SHA-256 of the question instead.</item>
/// </list>
/// </remarks>
public static class DebateHeaderHash
{
    private const string DebateVersion = "debate-header/1";
    private const string DebateProposalVersion = "debate-proposal/1";
    private const string AuditVersion = "audit-header/1";
    private const string AuditProposalVersion = "audit-proposal/1";

    /// <summary>The hash of the header of a debate, as <c>DebateOpened</c> anchors it.</summary>
    public static string Of(DebateRecord debate)
    {
        ArgumentNullException.ThrowIfNull(debate);
        var fields = new List<string?> { DebateVersion, Number(debate.CreatedAt.UtcTicks), debate.Mode.ToString(), debate.RequirementSetHash, debate.Question.Value };
        AddDebate(fields, debate);
        return Hash(fields);
    }

    /// <summary>The hash of the debate the user is shown and approves, before it opens.</summary>
    /// <exception cref="ArgumentException"><paramref name="questionSha256"/> is not a SHA-256.</exception>
    public static string Proposal(DebateRecord debate, string questionSha256)
    {
        ArgumentNullException.ThrowIfNull(debate);
        RequireSha256(questionSha256, nameof(questionSha256));
        var fields = new List<string?> { DebateProposalVersion, debate.Mode.ToString(), debate.RequirementSetHash, questionSha256 };
        AddDebate(fields, debate);
        return Hash(fields);
    }

    /// <summary>The hash of the header of a whole-project audit, as <c>ProjectAuditStarted</c> anchors it.</summary>
    public static string Of(ProjectAuditRecord audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        var fields = new List<string?>
        {
            AuditVersion,
            Number(audit.CreatedAt.UtcTicks),
            Optional(audit.HeadCommit),
            Uncommitted(audit.UncommittedChanges),
            audit.RequirementSetHash,
            audit.Request.Value,
        };
        AddAudit(fields, audit);
        return Hash(fields);
    }

    /// <summary>The hash of the audit the user is shown and approves, before it starts.</summary>
    /// <exception cref="ArgumentException"><paramref name="requestSha256"/> is not a SHA-256.</exception>
    public static string Proposal(ProjectAuditRecord audit, string requestSha256)
    {
        ArgumentNullException.ThrowIfNull(audit);
        RequireSha256(requestSha256, nameof(requestSha256));
        var fields = new List<string?> { AuditProposalVersion, Optional(audit.HeadCommit), Uncommitted(audit.UncommittedChanges), audit.RequirementSetHash, requestSha256 };
        AddAudit(fields, audit);
        return Hash(fields);
    }

    private static void AddDebate(List<string?> fields, DebateRecord debate)
    {
        ArgumentNullException.ThrowIfNull(debate.Subject);
        ArgumentNullException.ThrowIfNull(debate.Limits);
        fields.Add(debate.Subject.Kind.ToString());
        fields.Add(Optional(debate.Subject.Reference));
        fields.Add(Optional(debate.SnapshotCommit));

        var participants = debate.Participants ?? [];
        fields.Add(Number(participants.Count));
        foreach (var participant in participants)
        {
            fields.Add(participant.Label);
            fields.Add(participant.Role.ToString());
            AddAgent(fields, participant.Agent);
        }

        var judges = debate.Judges ?? [];
        fields.Add(Number(judges.Count));
        foreach (var judge in judges)
        {
            AddAgent(fields, judge);
        }

        fields.Add(debate.Judging.ToString());
        AddVendors(fields, debate.ProducerVendors);
        AddVendors(fields, debate.WaivedParticipantVendors);
        fields.Add(Number(debate.Limits.MaxRounds));
        fields.Add(Number(debate.Limits.MaxDuration.Ticks));
        fields.Add(Optional(debate.Limits.MaxTotalTokens?.ToString(CultureInfo.InvariantCulture)));
        fields.Add(Number(debate.MaxAgentRuns));
        fields.Add(Optional(debate.Challenges));
    }

    private static void AddAudit(List<string?> fields, ProjectAuditRecord audit)
    {
        ArgumentNullException.ThrowIfNull(audit.Verification);
        fields.Add(audit.Verification.ContentHash);
        AddAgent(fields, audit.Judge);
        AddVendors(fields, audit.ProducerVendors);
        AddVendors(fields, audit.DeclaredAuthors);
    }

    /// <summary>One agent with the encoding of <see cref="RosterHash"/>, as a roster of one (how many, then every member).</summary>
    private static void AddAgent(List<string?> fields, AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        RosterHash.Append(fields, [agent]);
    }

    private static void AddVendors(List<string?> fields, IReadOnlyList<VendorId>? vendors)
    {
        var distinct = (vendors ?? []).Select(v => v.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        fields.Add(Number(distinct.Count));
        fields.AddRange(distinct);
    }

    private static string Hash(List<string?> fields) => HashFunctions.Sha256Hex(HashFunctions.Canonical([.. fields]));

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Optional(string? value) => value is null ? "-" : "=" + value;

    private static string Uncommitted(bool uncommitted) => uncommitted ? "uncommitted-changes" : "no-uncommitted-changes";

    private static void RequireSha256(string value, string name)
    {
        if (!HashFunctions.IsSha256Hex(value))
        {
            throw new ArgumentException($"'{value}' is not a SHA-256 in lowercase hexadecimal.", name);
        }
    }
}
