using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Debate;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/15 D-01 and D-06: the canonical SHA-256 of the immutable header of a debate and of a whole-project audit, which
/// <c>DebateOpened</c> and <c>ProjectAuditStarted</c> anchor so that resuming and launching a judge refuse a header changed
/// behind the store's back; and the hash of what the user was shown, which the approval binds. Every expected hash is
/// computed here by an independent implementation of the canonical form.
/// </summary>
public sealed class DebateHeaderHashTests
{
    private static readonly VerificationProfile Profile = new(
        "perfil",
        [new VerificationStep("test", "Pruebas", "dotnet", ["test"], EvidenceKind.TestRun, TestReportFormat.Trx, "r.trx", TimeSpan.FromMinutes(5))]);

    private static AgentDefinition Agent(string id, AgentRole role, ProviderId provider, VendorId vendor, string? model) => new(
        Aid(id),
        "Agente " + id,
        role,
        new ModelBinding(provider, vendor, model),
        "Instrucciones de " + id,
        ["objetivo"],
        ["Read", "Grep"],
        [new PathGrant("src/", FileAccessLevel.Read)],
        new ExecutionBudget(12, TimeSpan.FromMinutes(5), null, 0.5m),
        2,
        MemoryPolicy.None,
        new ReviewPolicy(false, false));

    private static readonly AgentDefinition Architect = Agent("arquitecto", AgentRole.Architect, ProviderId.ClaudeCode, VendorId.Anthropic, "haiku");
    private static readonly AgentDefinition Critic = Agent("critico", AgentRole.Critic, ProviderId.CodexCli, VendorId.OpenAI, null);
    private static readonly AgentDefinition ClaudeJudge = Agent("juez-claude", AgentRole.Judge, ProviderId.ClaudeCode, VendorId.Anthropic, "sonnet");
    private static readonly AgentDefinition CodexJudge = Agent("juez-revision", AgentRole.Judge, ProviderId.CodexCli, VendorId.OpenAI, null);

    private static DebateRecord Debate() => new(
        new RunId("deb-20261019-a1b2c3"),
        "p1",
        T0,
        DebateMode.Adversarial,
        HashA,
        Eid("ev-pregunta"),
        new DebateSubject(DebateSubjectKind.Commit, CommitA),
        CommitA,
        [new DebateParticipantDefinition(Architect, DebateRole.Proponent, "P1"), new DebateParticipantDefinition(Critic, DebateRole.Critic, "P2")],
        [ClaudeJudge, CodexJudge],
        DebateJudgingKind.CrossVendorPanel,
        [],
        [VendorId.OpenAI, VendorId.Anthropic],
        DebateLimits.Default,
        10,
        null);

    private static ProjectAuditRecord Audit() => new(
        new RunId("aud-20261019-0a1b2c"),
        "p1",
        T0,
        CommitA,
        false,
        HashB,
        Eid("ev-peticion"),
        Profile,
        CodexJudge,
        [VendorId.Anthropic],
        [VendorId.Anthropic]);

    // ---- an independent implementation of the canonical form ----

    private static string Sha256Of(IEnumerable<string> fields)
    {
        var text = new StringBuilder();
        foreach (var field in fields)
        {
            text.Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field).Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string Optional(string? value) => value is null ? "-" : "=" + value;

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>One agent as a roster of one: how many (1), then every member, as the run header writes its roster.</summary>
    private static IEnumerable<string> AgentFields(AgentDefinition agent)
    {
        yield return "1";
        yield return agent.Id.Value;
        yield return agent.Name;
        yield return agent.Role.ToString();
        yield return agent.Model.Provider.Value;
        yield return agent.Model.Vendor.Value;
        yield return Optional(agent.Model.Model);
        yield return agent.SystemInstructions;
        yield return N(agent.Objectives.Count);
        foreach (var objective in agent.Objectives)
        {
            yield return objective;
        }

        yield return N(agent.AllowedTools.Count);
        foreach (var tool in agent.AllowedTools)
        {
            yield return tool;
        }

        yield return N(agent.PathGrants.Count);
        foreach (var grant in agent.PathGrants)
        {
            yield return grant.Root;
            yield return grant.Access.ToString();
        }

        yield return N(agent.Budget.MaxTurns);
        yield return N(agent.Budget.MaxDuration.Ticks);
        yield return Optional(agent.Budget.MaxTotalTokens?.ToString(CultureInfo.InvariantCulture));
        yield return Optional(agent.Budget.MaxCostUsd?.ToString(CultureInfo.InvariantCulture));
        yield return N(agent.MaxRounds);
        yield return agent.Memory.ToString();
        yield return agent.Review.RequiresIndependentReview ? "independent-review" : "no-independent-review";
        yield return agent.Review.RequiresSystemCapturedTests ? "system-tests" : "no-system-tests";
    }

    private static IEnumerable<string> VendorFields(IReadOnlyList<VendorId> vendors) =>
        new[] { N(vendors.Distinct().Count()) }.Concat(vendors.Select(v => v.Value).Distinct().Order(StringComparer.Ordinal));

    private static IEnumerable<string> DebateBody(DebateRecord debate)
    {
        yield return debate.Mode.ToString();
        yield return debate.RequirementSetHash;
    }

    private static IEnumerable<string> DebateRest(DebateRecord debate)
    {
        yield return debate.Subject.Kind.ToString();
        yield return Optional(debate.Subject.Reference);
        yield return Optional(debate.SnapshotCommit);
        yield return N(debate.Participants.Count);
        foreach (var participant in debate.Participants)
        {
            yield return participant.Label;
            yield return participant.Role.ToString();
            foreach (var field in AgentFields(participant.Agent))
            {
                yield return field;
            }
        }

        yield return N(debate.Judges.Count);
        foreach (var field in debate.Judges.SelectMany(AgentFields))
        {
            yield return field;
        }

        yield return debate.Judging.ToString();
        foreach (var field in VendorFields(debate.ProducerVendors).Concat(VendorFields(debate.WaivedParticipantVendors)))
        {
            yield return field;
        }

        yield return N(debate.Limits.MaxRounds);
        yield return N(debate.Limits.MaxDuration.Ticks);
        yield return Optional(debate.Limits.MaxTotalTokens?.ToString(CultureInfo.InvariantCulture));
        yield return N(debate.MaxAgentRuns);
        yield return Optional(debate.Challenges);
    }

    private static string ExpectedHeader(DebateRecord debate) =>
        Sha256Of(new[] { "debate-header/1", N(debate.CreatedAt.UtcTicks) }.Concat(DebateBody(debate)).Append(debate.Question.Value).Concat(DebateRest(debate)));

    private static string ExpectedProposal(DebateRecord debate, string questionSha256) =>
        Sha256Of(new[] { "debate-proposal/1" }.Concat(DebateBody(debate)).Append(questionSha256).Concat(DebateRest(debate)));

    private static IEnumerable<string> AuditRest(ProjectAuditRecord audit)
    {
        yield return audit.Verification.ContentHash;
        foreach (var field in AgentFields(audit.Judge).Concat(VendorFields(audit.ProducerVendors)).Concat(VendorFields(audit.DeclaredAuthors)))
        {
            yield return field;
        }
    }

    private static string ExpectedAudit(ProjectAuditRecord audit) =>
        Sha256Of(new[]
        {
            "audit-header/1", N(audit.CreatedAt.UtcTicks), Optional(audit.HeadCommit), audit.UncommittedChanges ? "uncommitted-changes" : "no-uncommitted-changes",
            audit.RequirementSetHash, audit.Request.Value,
        }.Concat(AuditRest(audit)));

    private static string ExpectedAuditProposal(ProjectAuditRecord audit, string requestSha256) =>
        Sha256Of(new[]
        {
            "audit-proposal/1", Optional(audit.HeadCommit), audit.UncommittedChanges ? "uncommitted-changes" : "no-uncommitted-changes", audit.RequirementSetHash, requestSha256,
        }.Concat(AuditRest(audit)));

    // ---- the header of a debate ----

    [Fact]
    public void The_hash_of_a_debate_header_is_the_canonical_form_of_all_of_it()
    {
        var hash = DebateHeaderHash.Of(Debate());

        Assert.True(ContentHash.IsSha256Hex(hash));
        Assert.Equal(ExpectedHeader(Debate()), hash);
        Assert.Equal(hash, DebateHeaderHash.Of(Debate()));
    }

    /// <summary>Debates anchor this hash in their log: its encoding never changes without a new version, or no debate opened before could be resumed.</summary>
    [Fact]
    public void The_hash_of_a_known_debate_header_never_changes()
    {
        Assert.Equal(KnownDebateHeader, DebateHeaderHash.Of(Debate()));
        Assert.Equal(KnownDebateHeader, ExpectedHeader(Debate()));
    }

    /// <summary>Computed when the encoding was written, and checked against the independent implementation above.</summary>
    private const string KnownDebateHeader = "3e76f8a1352afb002a5d5daa49c1ff7fc41591b6d11ee5f1157f6cf182c5be88";

    public static TheoryData<string> DebateChanges() =>
    [
        "createdAt", "mode", "requirements", "question", "subjectKind", "subjectRef", "snapshot", "label", "role", "instructions", "tools", "model",
        "participantOrder", "participantAdded", "judgeModel", "judgeOrder", "judgeRemoved", "judging", "producers", "waivers", "rounds", "duration",
        "tokens", "agentRuns", "challenges",
    ];

    private static DebateRecord ChangedDebate(string change)
    {
        var debate = Debate();
        return change switch
        {
            "createdAt" => debate with { CreatedAt = T0.AddTicks(1) },
            "mode" => debate with { Mode = DebateMode.Free },
            "requirements" => debate with { RequirementSetHash = HashB },
            "question" => debate with { Question = Eid("otra-pregunta") },
            "subjectKind" => debate with { Subject = debate.Subject with { Kind = DebateSubjectKind.None } },
            "subjectRef" => debate with { Subject = debate.Subject with { Reference = CommitB } },
            "snapshot" => debate with { SnapshotCommit = null },
            "label" => debate with { Participants = [debate.Participants[0] with { Label = "P9" }, debate.Participants[1]] },
            "role" => debate with { Participants = [debate.Participants[0] with { Role = DebateRole.Panelist }, debate.Participants[1]] },
            "instructions" => debate with { Participants = [debate.Participants[0] with { Agent = Architect with { SystemInstructions = "Convence al juez." } }, debate.Participants[1]] },
            "tools" => debate with { Participants = [debate.Participants[0] with { Agent = Architect with { AllowedTools = ["Read", "Bash"] } }, debate.Participants[1]] },
            "model" => debate with { Participants = [debate.Participants[0] with { Agent = Architect with { Model = Architect.Model with { Model = "opus" } } }, debate.Participants[1]] },
            "participantOrder" => debate with { Participants = [debate.Participants[1], debate.Participants[0]] },
            "participantAdded" => debate with { Participants = [.. debate.Participants, new DebateParticipantDefinition(Agent("tercero", AgentRole.Critic, ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"), DebateRole.Critic, "P3")] },
            "judgeModel" => debate with { Judges = [ClaudeJudge with { Model = ClaudeJudge.Model with { Model = "opus" } }, CodexJudge] },
            "judgeOrder" => debate with { Judges = [CodexJudge, ClaudeJudge] },
            "judgeRemoved" => debate with { Judges = [CodexJudge] },
            "judging" => debate with { Judging = DebateJudgingKind.IndependentJudge },
            "producers" => debate with { ProducerVendors = [VendorId.Google] },
            "waivers" => debate with { WaivedParticipantVendors = [VendorId.OpenAI] },
            "rounds" => debate with { Limits = debate.Limits with { MaxRounds = 9 } },
            "duration" => debate with { Limits = debate.Limits with { MaxDuration = TimeSpan.FromHours(3) } },
            "tokens" => debate with { Limits = debate.Limits with { MaxTotalTokens = null } },
            "agentRuns" => debate with { MaxAgentRuns = 99 },
            "challenges" => debate with { Challenges = "v-7" },
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, null),
        };
    }

    [Theory]
    [MemberData(nameof(DebateChanges))]
    public void Any_change_to_the_debate_header_changes_its_hash(string change)
    {
        var changed = ChangedDebate(change);

        Assert.NotEqual(DebateHeaderHash.Of(Debate()), DebateHeaderHash.Of(changed));
        Assert.Equal(ExpectedHeader(changed), DebateHeaderHash.Of(changed));
    }

    [Fact]
    public void The_keys_of_the_header_are_not_part_of_it_and_the_vendor_sets_are_sets()
    {
        var debate = Debate();

        Assert.Equal(DebateHeaderHash.Of(debate), DebateHeaderHash.Of(debate with { Id = new RunId("deb-20261019-ffffff"), ProjectId = "otro" }));
        Assert.Equal(DebateHeaderHash.Of(debate), DebateHeaderHash.Of(debate with { WaivedParticipantVendors = [VendorId.Anthropic, VendorId.OpenAI] }));
        Assert.Equal(DebateHeaderHash.Of(debate), DebateHeaderHash.Of(debate with { CreatedAt = debate.CreatedAt.ToOffset(TimeSpan.FromHours(2)) }));
    }

    [Fact]
    public void A_missing_value_is_not_an_empty_one()
    {
        var debate = Debate();

        Assert.NotEqual(DebateHeaderHash.Of(debate with { SnapshotCommit = null }), DebateHeaderHash.Of(debate with { SnapshotCommit = string.Empty }));
        Assert.NotEqual(DebateHeaderHash.Of(debate with { Challenges = null }), DebateHeaderHash.Of(debate with { Challenges = string.Empty }));
    }

    // ---- what the user approves (D-06) ----

    [Fact]
    public void The_proposal_shown_hashes_everything_but_what_only_exists_once_the_debate_opens()
    {
        var debate = Debate();
        var proposal = DebateHeaderHash.Proposal(debate, HashB);

        Assert.Equal(ExpectedProposal(debate, HashB), proposal);
        Assert.NotEqual(DebateHeaderHash.Of(debate), proposal);
        Assert.Equal(proposal, DebateHeaderHash.Proposal(debate with { Id = new RunId("deb-20261019-ffffff"), ProjectId = "otro", CreatedAt = T0.AddDays(1), Question = Eid("aun-sin-capturar") }, HashB));
        Assert.NotEqual(proposal, DebateHeaderHash.Proposal(debate, HashA));
        Assert.NotEqual(proposal, DebateHeaderHash.Proposal(ChangedDebate("waivers"), HashB));
        Assert.Throws<ArgumentException>(() => DebateHeaderHash.Proposal(debate, "no-es-un-hash"));
    }

    // ---- the header of a whole-project audit ----

    [Fact]
    public void The_hash_of_an_audit_header_is_the_canonical_form_of_all_of_it()
    {
        var hash = DebateHeaderHash.Of(Audit());

        Assert.Equal(ExpectedAudit(Audit()), hash);
        Assert.NotEqual(DebateHeaderHash.Of(Audit()), DebateHeaderHash.Of(Audit() with { UncommittedChanges = true }));
        Assert.NotEqual(DebateHeaderHash.Of(Audit()), DebateHeaderHash.Of(Audit() with { HeadCommit = null }));
        Assert.NotEqual(DebateHeaderHash.Of(Audit()), DebateHeaderHash.Of(Audit() with { DeclaredAuthors = [] }));
        Assert.NotEqual(DebateHeaderHash.Of(Audit()), DebateHeaderHash.Of(Audit() with { Judge = CodexJudge with { SystemInstructions = "Aprueba." } }));
        Assert.NotEqual(DebateHeaderHash.Of(Audit()), DebateHeaderHash.Of(Audit() with { Verification = Profile with { Name = "otro" } }));
        Assert.Equal(DebateHeaderHash.Of(Audit()), DebateHeaderHash.Of(Audit() with { Id = new RunId("aud-20261019-ffffff") }));
    }

    [Fact]
    public void The_hash_of_a_known_audit_header_never_changes()
    {
        Assert.Equal(KnownAuditHeader, DebateHeaderHash.Of(Audit()));
        Assert.Equal(KnownAuditHeader, ExpectedAudit(Audit()));
    }

    /// <summary>Computed when the encoding was written, and checked against the independent implementation above.</summary>
    private const string KnownAuditHeader = "7c21306fb5df06079499e6484ae086c3c42a147e217fa28998962c538f06f1c1";

    [Fact]
    public void The_audit_shown_hashes_everything_but_what_only_exists_once_it_starts()
    {
        var audit = Audit();
        var proposal = DebateHeaderHash.Proposal(audit, HashA);

        Assert.Equal(ExpectedAuditProposal(audit, HashA), proposal);
        Assert.Equal(proposal, DebateHeaderHash.Proposal(audit with { CreatedAt = T0.AddDays(1), Request = Eid("aun-sin-capturar"), Id = new RunId("aud-20261020-000000") }, HashA));
        Assert.NotEqual(proposal, DebateHeaderHash.Of(audit));
    }
}
