using System.IO;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Debate;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Tests.Domain.Orchestration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// docs/15 D-01 and section 6: the immutable header of a debate in <c>debate_sessions</c>. It is found after reopening exactly
/// as it was created, its hash (the one <c>DebateOpened</c> anchors) makes the round trip and is the one the row keeps, the
/// same header twice writes nothing, another one under its id is refused, and a header changed behind the store's back is
/// never read.
/// </summary>
public sealed class SqliteDebateRegistryTests : IDisposable
{
    private const string Id = "deb-20261019-a1b2c3";

    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An agent with every member of a definition set, so the store has to keep all of them.</summary>
    private static AgentDefinition Agent(string id, AgentRole role, ProviderId provider, VendorId vendor, string? model) => new(
        Aid(id),
        "Agente «" + id + "»",
        role,
        new ModelBinding(provider, vendor, model),
        "Instrucciones de " + id + "\ncon dos líneas y \"comillas\"",
        ["objetivo 1", "objetivo 2"],
        ["Read", "Grep", "Glob"],
        [new PathGrant("src/", FileAccessLevel.Read)],
        new ExecutionBudget(12, TimeSpan.FromMinutes(4.5), MaxTotalTokens: 90_000, MaxCostUsd: 0.25m),
        3,
        MemoryPolicy.None,
        new ReviewPolicy(false, true));

    private static readonly AgentDefinition Architect = Agent("arquitecto", AgentRole.Architect, ProviderId.ClaudeCode, VendorId.Anthropic, "haiku");
    private static readonly AgentDefinition Critic = Agent("critico", AgentRole.Critic, ProviderId.CodexCli, VendorId.OpenAI, null);
    private static readonly AgentDefinition ClaudeJudge = Agent("juez-claude", AgentRole.Judge, ProviderId.ClaudeCode, VendorId.Anthropic, "sonnet");
    private static readonly AgentDefinition CodexJudge = Agent("juez-revision", AgentRole.Judge, ProviderId.CodexCli, VendorId.OpenAI, "gpt-x");

    private static SqliteDebateRegistry Registry(ProjectInstance instance) => new(instance.Databases);

    /// <summary>
    /// Stores what the header points at, in the order of docs/15 section 6 (the requirements, then the question as evidence
    /// with the debate as its run), and builds a header of a cross-vendor panel on a commit: every kind of member set.
    /// </summary>
    private static async Task<DebateRecord> SeedDebateAsync(ProjectInstance instance, string id = Id)
    {
        var requirements = PlanFixtures.Requirements();
        await instance.Requirements.SaveAsync(instance.ProjectId, requirements, Ct);
        using var question = new MemoryStream(Bytes("¿Qué caché adoptamos para el índice de skills?"));
        var evidence = await instance.Evidence.CaptureAsync(
            instance.ProjectId,
            new EvidenceDraft(EvidenceKind.Document, EvidenceOrigin.ProvidedByHuman, "Pregunta del debate", "manuel", RunId: id),
            question,
            Ct);
        return new DebateRecord(
            new RunId(id),
            instance.ProjectId,
            T0.AddTicks(9_876_543),
            DebateMode.ArchitectureReview,
            requirements.ContentHash,
            evidence.Id,
            new DebateSubject(DebateSubjectKind.Commit, CommitA),
            CommitA,
            [new DebateParticipantDefinition(Architect, DebateRole.Proponent, "P1"), new DebateParticipantDefinition(Critic, DebateRole.Critic, "P2")],
            [ClaudeJudge, CodexJudge],
            DebateJudgingKind.CrossVendorPanel,
            [],
            [VendorId.OpenAI, VendorId.Anthropic],
            new DebateLimits(3, TimeSpan.FromMinutes(12.5), 250_000),
            8,
            null);
    }

    [Fact]
    public async Task A_debate_header_is_found_after_reopening_exactly_as_it_was_created()
    {
        DebateRecord debate;
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            debate = await SeedDebateAsync(first);
            await Registry(first).CreateAsync(debate, Ct);
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var found = await Registry(second).FindAsync(second.ProjectId, debate.Id, Ct);

        // Every member of every participant and judge, the disposition, the vendors, the limits and the launches allowed.
        Assert.NotNull(found);
        Assert.Equal(Shape(debate), Shape(found));
    }

    [Fact]
    public async Task The_hash_DebateOpened_anchors_makes_the_round_trip_and_is_the_hash_the_row_keeps()
    {
        DebateRecord debate;
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            debate = await SeedDebateAsync(first);
            await Registry(first).CreateAsync(debate, Ct);
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var found = await Registry(second).FindAsync(second.ProjectId, debate.Id, Ct);

        Assert.NotNull(found);
        var anchored = DebateHeaderHash.Of(debate);
        Assert.Equal(anchored, DebateHeaderHash.Of(found));
        using var connection = second.Connect();
        Assert.Equal(anchored, Text(connection, $"SELECT header_hash FROM debate_sessions WHERE id = '{Id}'"));
    }

    [Fact]
    public async Task The_columns_of_the_row_mirror_the_header()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var debate = await SeedDebateAsync(instance);

        await Registry(instance).CreateAsync(debate, Ct);

        using var connection = instance.Connect();
        Assert.Equal(1, Scalar(connection, $"""
            SELECT COUNT(*) FROM debate_sessions
            WHERE id = '{Id}' AND project_id = '{instance.ProjectId}' AND created_at = '{debate.CreatedAt.ToUniversalTime():O}'
              AND mode = 'ArchitectureReview' AND requirement_set_hash = '{debate.RequirementSetHash}'
              AND question_evidence_id = '{debate.Question.Value}' AND subject_kind = 'Commit' AND subject_ref = '{CommitA}'
              AND snapshot_commit = '{CommitA}' AND challenges_verdict_id IS NULL
            """));
        var header = Text(connection, $"SELECT header_json FROM debate_sessions WHERE id = '{Id}'");
        Assert.StartsWith("""{"participants":[{"agent":{"id":"arquitecto",""", header, StringComparison.Ordinal);
        Assert.Contains("""},"role":"Proponent","label":"P1"}""", header, StringComparison.Ordinal);
        Assert.EndsWith(""","judging":"CrossVendorPanel","producerVendors":[],"waivedParticipantVendors":["openai","anthropic"],"limits":{"maxRounds":3,"maxDuration":"00:12:30","maxTotalTokens":250000},"maxAgentRuns":8}""", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_debate_without_a_subject_snapshot_or_token_limit_is_found_as_it_was_created()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var debate = await SeedDebateAsync(instance) with
        {
            Mode = DebateMode.Free,
            Subject = new DebateSubject(DebateSubjectKind.None, null),
            SnapshotCommit = null,
            Participants = [new DebateParticipantDefinition(Architect, DebateRole.Panelist, "P1"), new DebateParticipantDefinition(Architect with { Id = Aid("arquitecto-2"), Model = Architect.Model with { Model = "sonnet" } }, DebateRole.Panelist, "P2")],
            Judges = [CodexJudge],
            Judging = DebateJudgingKind.IndependentJudge,
            ProducerVendors = [VendorId.Anthropic],
            WaivedParticipantVendors = [],
            Limits = new DebateLimits(1, TimeSpan.FromMinutes(30)),
        };

        await Registry(instance).CreateAsync(debate, Ct);
        var found = await Registry(instance).FindAsync(instance.ProjectId, debate.Id, Ct);

        Assert.NotNull(found);
        Assert.Equal(Shape(debate), Shape(found));
        Assert.Null(found.Limits.MaxTotalTokens);
        Assert.Equal(DebateHeaderHash.Of(debate), DebateHeaderHash.Of(found));
    }

    [Fact]
    public async Task A_revision_debate_names_the_verdict_it_challenges()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        await instance.Verdicts.AppendAsync(instance.ProjectId, new StoredVerdict("v-impugnado", ValidVerdict(Dossier()), "20261015-a1b2c3", "revision", AuditScope.CurrentPlan, true, [], null), Ct);
        var debate = await SeedDebateAsync(instance) with
        {
            Mode = DebateMode.Adversarial,
            Subject = new DebateSubject(DebateSubjectKind.Verdict, "v-impugnado"),
            SnapshotCommit = null,
            Challenges = "v-impugnado",
        };

        await Registry(instance).CreateAsync(debate, Ct);
        var found = await Registry(instance).FindAsync(instance.ProjectId, debate.Id, Ct);

        Assert.NotNull(found);
        Assert.Equal("v-impugnado", found.Challenges);
        Assert.Equal(Shape(debate), Shape(found));
        using var connection = instance.Connect();
        Assert.Equal("v-impugnado", Text(connection, $"SELECT challenges_verdict_id FROM debate_sessions WHERE id = '{Id}'"));
    }

    [Fact]
    public async Task Debates_are_listed_in_the_order_they_were_created()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var first = await SeedDebateAsync(instance, "deb-20261019-ffffff");
        var second = await SeedDebateAsync(instance, "deb-20261018-000000") with { CreatedAt = T0.AddDays(-1) };
        await Registry(instance).CreateAsync(first, Ct);
        await Registry(instance).CreateAsync(second, Ct);

        var debates = await Registry(instance).ListAsync(instance.ProjectId, Ct);

        Assert.Equal([first.Id, second.Id], debates.Select(d => d.Id));
        Assert.Equal(Shape(second), Shape(debates[1]));
    }

    [Fact]
    public async Task Creating_the_same_debate_again_is_harmless()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var debate = await SeedDebateAsync(instance);

        await Registry(instance).CreateAsync(debate, Ct);
        await Registry(instance).CreateAsync(debate with { CreatedAt = debate.CreatedAt.ToOffset(TimeSpan.FromHours(2)) }, Ct);

        Assert.Single(await Registry(instance).ListAsync(instance.ProjectId, Ct));
    }

    public static TheoryData<string> OtherHeaders => ["participant-with-bash", "judge-of-another-vendor", "looser-limit", "more-launches", "no-waiver", "other-question"];

    [Theory]
    [MemberData(nameof(OtherHeaders))]
    public async Task The_same_id_with_another_header_is_refused_and_the_first_header_stays(string change)
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var debate = await SeedDebateAsync(instance);
        await Registry(instance).CreateAsync(debate, Ct);
        var other = change switch
        {
            "participant-with-bash" => debate with { Participants = [new DebateParticipantDefinition(Architect with { AllowedTools = ["Read", "Bash"] }, DebateRole.Proponent, "P1"), debate.Participants[1]] },
            "judge-of-another-vendor" => debate with { Judges = [ClaudeJudge, CodexJudge with { Model = new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "opus") }] },
            "looser-limit" => debate with { Limits = debate.Limits with { MaxTotalTokens = 4_000_000 } },
            "more-launches" => debate with { MaxAgentRuns = 80 },
            "no-waiver" => debate with { WaivedParticipantVendors = [VendorId.OpenAI] },
            "other-question" => debate with { Question = Eid("otra-pregunta") },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Registry(instance).CreateAsync(other, Ct));

        var found = await Registry(instance).FindAsync(instance.ProjectId, debate.Id, Ct);
        Assert.Equal(Shape(debate), Shape(found));
    }

    public static TheoryData<string> MissingReferences => ["requirements", "question", "challenged-verdict"];

    [Theory]
    [MemberData(nameof(MissingReferences))]
    public async Task A_debate_must_point_at_stored_requirements_question_and_challenged_verdict(string missing)
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var debate = await SeedDebateAsync(instance);
        var broken = missing switch
        {
            "requirements" => debate with { RequirementSetHash = new string('c', 64) },
            "question" => debate with { Question = Eid("no-existe") },
            "challenged-verdict" => debate with { Mode = DebateMode.Adversarial, Subject = new DebateSubject(DebateSubjectKind.Verdict, "no-existe"), Challenges = "no-existe" },
            _ => throw new ArgumentOutOfRangeException(nameof(missing)),
        };

        var ex = await Assert.ThrowsAsync<SqliteException>(() => Registry(instance).CreateAsync(broken, Ct));

        Assert.Contains("FOREIGN KEY", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await Registry(instance).ListAsync(instance.ProjectId, Ct));
    }

    public static TheoryData<string> Tamperings => new()
    {
        // A participant given Bash, as docs/15 D-01 fears, with the old hash left in place.
        """UPDATE debate_sessions SET header_json = replace(header_json, '"allowedTools":["Read","Grep","Glob"]', '"allowedTools":["Read","Bash"]')""",
        // A looser limit.
        """UPDATE debate_sessions SET header_json = replace(header_json, '"maxTotalTokens":250000', '"maxTotalTokens":4000000')""",
        // The mode in its column.
        "UPDATE debate_sessions SET mode = 'Free'",
        // A hash forged alone.
        "UPDATE debate_sessions SET header_hash = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'",
    };

    [Theory]
    [MemberData(nameof(Tamperings))]
    public async Task A_header_changed_behind_the_stores_back_is_never_read(string tampering)
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var debate = await SeedDebateAsync(instance);
        await Registry(instance).CreateAsync(debate, Ct);
        using (var connection = instance.Connect())
        {
            Exec(connection, "DROP TRIGGER debate_sessions_no_update");
            Exec(connection, tampering);
        }

        // Like a plan that no longer matches its key: the row does not give the hash it was stored with.
        var find = await Assert.ThrowsAsync<InvalidDataException>(() => Registry(instance).FindAsync(instance.ProjectId, debate.Id, Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => Registry(instance).ListAsync(instance.ProjectId, Ct));
        Assert.Contains(Id, find.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> ForeignRows => new()
    {
        """UPDATE debate_sessions SET header_json = '{"participants":[{"agent":null,"role":"Proponent","label":"P1"}]}'""",
        """UPDATE debate_sessions SET header_json = replace(header_json, '"role":"Proponent"', '"role":"Juez"')""",
        """UPDATE debate_sessions SET header_json = replace(header_json, '"role":"Proponent"', '"role":7')""",
        """UPDATE debate_sessions SET header_json = 'no es json'""",
        "UPDATE debate_sessions SET created_at = 'ayer'",
    };

    [Theory]
    [MemberData(nameof(ForeignRows))]
    public async Task A_row_this_store_did_not_write_is_invalid_data(string tampering)
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var debate = await SeedDebateAsync(instance);
        await Registry(instance).CreateAsync(debate, Ct);
        using (var connection = instance.Connect())
        {
            Exec(connection, "DROP TRIGGER debate_sessions_no_update");
            Exec(connection, tampering);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => Registry(instance).FindAsync(instance.ProjectId, debate.Id, Ct));
    }

    public static TheoryData<string> InvalidHeaders => ["no-subject", "no-limits", "null-participant", "null-judge", "undefined-mode", "undefined-role", "undefined-judging", "blank-project"];

    [Theory]
    [MemberData(nameof(InvalidHeaders))]
    public async Task A_header_the_domain_could_not_hold_is_refused_before_anything_is_written(string shape)
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var debate = await SeedDebateAsync(instance);
        var invalid = shape switch
        {
            "no-subject" => debate with { Subject = null! },
            "no-limits" => debate with { Limits = null! },
            "null-participant" => debate with { Participants = [debate.Participants[0], null!] },
            "null-judge" => debate with { Judges = [null!] },
            "undefined-mode" => debate with { Mode = (DebateMode)42 },
            "undefined-role" => debate with { Participants = [new DebateParticipantDefinition(Architect, (DebateRole)42, "P1")] },
            "undefined-judging" => debate with { Judging = (DebateJudgingKind)42 },
            "blank-project" => debate with { ProjectId = " " },
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Registry(instance).CreateAsync(invalid, Ct));

        Assert.Empty(await Registry(instance).ListAsync(instance.ProjectId, Ct));
    }

    [Fact]
    public async Task An_unknown_debate_finds_nothing()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);

        Assert.Null(await Registry(instance).FindAsync(instance.ProjectId, new RunId("deb-20261019-nadaaa"), Ct));
        Assert.Empty(await Registry(instance).ListAsync(instance.ProjectId, Ct));
    }

    [Fact]
    public async Task The_container_resolves_the_registry_of_debates_to_the_sqlite_one()
    {
        var services = new ServiceCollection();
        services.AddAgentStudioPersistence(new DecisionLogAnchorOptions(Path.Combine(_folder.DataDirectory, "anclas")));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.IsType<SqliteDebateRegistry>(provider.GetRequiredService<IDebateRegistry>());
        Assert.Same(provider.GetRequiredService<IDebateRegistry>(), provider.GetRequiredService<IDebateRegistry>());
    }
}
