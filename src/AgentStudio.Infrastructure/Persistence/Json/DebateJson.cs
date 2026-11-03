using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Infrastructure.Persistence.Json;

/// <summary>
/// What <c>debate_sessions.header_json</c> holds (docs/15 section 6): every participant with its whole definition, its role
/// and its label, every judge with its whole definition, the disposition of the judgment, the vendors that produced the
/// subject and the waived ones, the limits and the launches allowed. The rest of the header (mode, requirements, question,
/// subject, commit of the copy and the challenged verdict) has columns of its own. Participants and judges keep their order
/// (it decides the order of publication and which judge reads the proposals reversed), and so do the vendors, as given.
/// </summary>
internal sealed record DebateHeaderJson(
    IReadOnlyList<DebateParticipantJson> Participants,
    IReadOnlyList<AgentJson> Judges,
    DebateJudgingKind Judging,
    IReadOnlyList<string> ProducerVendors,
    IReadOnlyList<string> WaivedParticipantVendors,
    DebateLimitsJson Limits,
    int MaxAgentRuns)
{
    public static DebateHeaderJson From(DebateRecord debate) => new(
        [.. debate.Participants.Select(p => new DebateParticipantJson(AgentJson.From(p.Agent), p.Role, p.Label))],
        [.. debate.Judges.Select(AgentJson.From)],
        debate.Judging,
        [.. debate.ProducerVendors.Select(v => v.Value)],
        [.. debate.WaivedParticipantVendors.Select(v => v.Value)],
        DebateLimitsJson.From(debate.Limits),
        debate.MaxAgentRuns);

    public IReadOnlyList<DebateParticipantDefinition> ParticipantsToDomain() =>
        [.. (Participants ?? []).Select(p => (p ?? throw new InvalidOperationException("A participant is missing.")).ToDomain())];

    public IReadOnlyList<AgentDefinition> JudgesToDomain() =>
        [.. (Judges ?? []).Select(j => (j ?? throw new InvalidOperationException("A judge is missing.")).ToDomain())];

    public DebateJudgingKind JudgingToDomain() =>
        Enum.IsDefined(Judging) ? Judging : throw new InvalidOperationException($"The disposition '{Judging}' does not exist.");

    public DebateLimits LimitsToDomain() => (Limits ?? throw new InvalidOperationException("The limits are missing.")).ToDomain();
}

/// <summary>A participant of a debate: the agent with its whole definition, its role in the debate and its label (<c>P1</c>…).</summary>
internal sealed record DebateParticipantJson(AgentJson Agent, DebateRole Role, string Label)
{
    public DebateParticipantDefinition ToDomain() => new(
        (Agent ?? throw new InvalidOperationException($"Participant '{Label}' has no agent.")).ToDomain(),
        Enum.IsDefined(Role) ? Role : throw new InvalidOperationException($"Participant '{Label}' has the role '{Role}', which does not exist."),
        Label ?? throw new InvalidOperationException("A participant has no label."));
}

/// <summary>The limits of a debate (D-21). A missing token limit means none.</summary>
internal sealed record DebateLimitsJson(int MaxRounds, TimeSpan MaxDuration, long? MaxTotalTokens)
{
    public static DebateLimitsJson From(DebateLimits limits) => new(limits.MaxRounds, limits.MaxDuration, limits.MaxTotalTokens);

    public DebateLimits ToDomain() => new(MaxRounds, MaxDuration, MaxTotalTokens);
}

/// <summary>
/// What <c>project_audits.header_json</c> holds (docs/15 section 6 and D-28): whether there were uncommitted changes, the
/// trusted verification profile frozen for the audit, the judge with its whole definition, the vendors that produced code
/// according to the log and the authors the user declared. The commit, the requirements and the request have columns.
/// </summary>
internal sealed record ProjectAuditHeaderJson(
    bool UncommittedChanges,
    VerificationProfileJson Verification,
    AgentJson Judge,
    IReadOnlyList<string> ProducerVendors,
    IReadOnlyList<string> DeclaredAuthors)
{
    public static ProjectAuditHeaderJson From(ProjectAuditRecord audit) => new(
        audit.UncommittedChanges,
        VerificationProfileJson.From(audit.Verification),
        AgentJson.From(audit.Judge),
        [.. audit.ProducerVendors.Select(v => v.Value)],
        [.. audit.DeclaredAuthors.Select(v => v.Value)]);
}

/// <summary>Vendors as they are written in a header: by their id, in the order given.</summary>
internal static class VendorsJson
{
    public static IReadOnlyList<VendorId> ToDomain(IReadOnlyList<string>? vendors) => [.. (vendors ?? []).Select(v => new VendorId(v))];
}
