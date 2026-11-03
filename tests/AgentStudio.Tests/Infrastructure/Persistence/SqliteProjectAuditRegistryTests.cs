using System.IO;
using AgentStudio.Application.Debate;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Agents;
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
/// docs/15 D-28 and section 6: the immutable header of a whole-project audit in <c>project_audits</c>, with the rules of the
/// debates: found after reopening as it was created, its hash (the one <c>ProjectAuditStarted</c> anchors) makes the round
/// trip and is the one the row keeps, the same header twice writes nothing, another one under its id is refused, and a
/// header changed behind the store's back is never read.
/// </summary>
public sealed class SqliteProjectAuditRegistryTests : IDisposable
{
    private const string Id = "aud-20261019-0a1b2c";

    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AgentDefinition Judge = new(
        Aid("juez-revision"),
        "Juez «revisión»",
        AgentRole.Judge,
        new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-x"),
        "Juzgas el proyecto completo\ncon evidencias.",
        ["objetivo"],
        ["Read", "Grep", "Glob"],
        [new PathGrant("docs/", FileAccessLevel.Read)],
        new ExecutionBudget(30, TimeSpan.FromMinutes(15.25), MaxTotalTokens: 500_000),
        1,
        MemoryPolicy.None,
        new ReviewPolicy(false, false));

    private static SqliteProjectAuditRegistry Registry(ProjectInstance instance) => new(instance.Databases);

    /// <summary>Stores the requirements and the request (evidence with the audit as its run) and builds a header with every member set.</summary>
    private static async Task<ProjectAuditRecord> SeedAuditAsync(ProjectInstance instance, string id = Id)
    {
        var requirements = PlanFixtures.Requirements();
        await instance.Requirements.SaveAsync(instance.ProjectId, requirements, Ct);
        using var request = new MemoryStream(Bytes("Audita el proyecto completo contra los requisitos de la Fase 5."));
        var evidence = await instance.Evidence.CaptureAsync(
            instance.ProjectId,
            new EvidenceDraft(EvidenceKind.Document, EvidenceOrigin.ProvidedByHuman, "Petición de la auditoría", "manuel", RunId: id),
            request,
            Ct);
        var steps = Profile.Steps;
        return new ProjectAuditRecord(
            new RunId(id),
            instance.ProjectId,
            T0.AddTicks(1_234_567),
            CommitA,
            true,
            requirements.ContentHash,
            evidence.Id,
            Profile with { Steps = [steps[0] with { Isolation = ProcessIsolation.None }, .. steps.Skip(1)] },
            Judge,
            [VendorId.Anthropic],
            [VendorId.Anthropic, new VendorId("otro")]);
    }

    [Fact]
    public async Task An_audit_header_is_found_after_reopening_exactly_as_it_was_created()
    {
        ProjectAuditRecord audit;
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            audit = await SeedAuditAsync(first);
            await Registry(first).CreateAsync(audit, Ct);
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var found = await Registry(second).FindAsync(second.ProjectId, audit.Id, Ct);

        // The uncommitted changes, the profile (a step without isolation included), the whole judge and both vendor lists.
        Assert.NotNull(found);
        Assert.Equal(Shape(audit), Shape(found));
        Assert.Equal(audit.Verification.ContentHash, found.Verification.ContentHash);
    }

    [Fact]
    public async Task The_hash_ProjectAuditStarted_anchors_makes_the_round_trip_and_is_the_hash_the_row_keeps()
    {
        ProjectAuditRecord audit;
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            audit = await SeedAuditAsync(first);
            await Registry(first).CreateAsync(audit, Ct);
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var found = await Registry(second).FindAsync(second.ProjectId, audit.Id, Ct);

        Assert.NotNull(found);
        Assert.Equal(DebateHeaderHash.Of(audit), DebateHeaderHash.Of(found));
        using var connection = second.Connect();
        Assert.Equal(DebateHeaderHash.Of(audit), Text(connection, $"SELECT header_hash FROM project_audits WHERE id = '{Id}'"));
        Assert.Equal(1, Scalar(connection, $"""
            SELECT COUNT(*) FROM project_audits
            WHERE id = '{Id}' AND project_id = '{second.ProjectId}' AND head_commit = '{CommitA}'
              AND requirement_set_hash = '{audit.RequirementSetHash}' AND request_evidence_id = '{audit.Request.Value}'
            """));
    }

    [Fact]
    public async Task An_audit_of_a_project_without_commits_is_found_without_a_head()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var audit = await SeedAuditAsync(instance) with { HeadCommit = null, UncommittedChanges = false, ProducerVendors = [], DeclaredAuthors = [] };

        await Registry(instance).CreateAsync(audit, Ct);
        var found = await Registry(instance).FindAsync(instance.ProjectId, audit.Id, Ct);

        Assert.NotNull(found);
        Assert.Null(found.HeadCommit);
        Assert.False(found.UncommittedChanges);
        Assert.Equal(Shape(audit), Shape(found));
    }

    [Fact]
    public async Task Audits_are_listed_in_the_order_they_were_created_and_creating_one_again_is_harmless()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var first = await SeedAuditAsync(instance, "aud-20261019-ffffff");
        var second = await SeedAuditAsync(instance, "aud-20261018-000000");
        await Registry(instance).CreateAsync(first, Ct);
        await Registry(instance).CreateAsync(second, Ct);
        await Registry(instance).CreateAsync(first, Ct);

        var audits = await Registry(instance).ListAsync(instance.ProjectId, Ct);

        Assert.Equal([first.Id, second.Id], audits.Select(a => a.Id));
    }

    public static TheoryData<string> OtherHeaders => ["other-judge", "no-uncommitted-changes", "other-profile", "other-authors"];

    [Theory]
    [MemberData(nameof(OtherHeaders))]
    public async Task The_same_id_with_another_header_is_refused_and_the_first_header_stays(string change)
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var audit = await SeedAuditAsync(instance);
        await Registry(instance).CreateAsync(audit, Ct);
        var other = change switch
        {
            "other-judge" => audit with { Judge = Judge with { Model = new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "opus") } },
            "no-uncommitted-changes" => audit with { UncommittedChanges = false },
            "other-profile" => audit with { Verification = Profile },
            "other-authors" => audit with { DeclaredAuthors = [] },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Registry(instance).CreateAsync(other, Ct));

        Assert.Equal(Shape(audit), Shape(await Registry(instance).FindAsync(instance.ProjectId, audit.Id, Ct)));
    }

    [Theory]
    [InlineData("requirements")]
    [InlineData("request")]
    public async Task An_audit_must_point_at_stored_requirements_and_request(string missing)
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var audit = await SeedAuditAsync(instance);
        var broken = missing == "requirements" ? audit with { RequirementSetHash = new string('c', 64) } : audit with { Request = Eid("no-existe") };

        var ex = await Assert.ThrowsAsync<SqliteException>(() => Registry(instance).CreateAsync(broken, Ct));

        Assert.Contains("FOREIGN KEY", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string> Tamperings => new()
    {
        // The judge moved to the vendor that produced the code, the old hash left in place.
        """UPDATE project_audits SET header_json = replace(header_json, '"vendor":"openai"', '"vendor":"anthropic"')""",
        // The declared authors forgotten.
        """UPDATE project_audits SET header_json = replace(header_json, '"declaredAuthors":["anthropic","otro"]', '"declaredAuthors":[]')""",
        // Another commit audited.
        "UPDATE project_audits SET head_commit = '2222222222222222222222222222222222222222'",
        // A hash forged alone.
        "UPDATE project_audits SET header_hash = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'",
        // Something this store never writes.
        """UPDATE project_audits SET header_json = '{"uncommittedChanges":true}'""",
    };

    [Theory]
    [MemberData(nameof(Tamperings))]
    public async Task A_header_changed_behind_the_stores_back_is_never_read(string tampering)
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var audit = await SeedAuditAsync(instance);
        await Registry(instance).CreateAsync(audit, Ct);
        using (var connection = instance.Connect())
        {
            Exec(connection, "DROP TRIGGER project_audits_no_update");
            Exec(connection, tampering);
        }

        var find = await Assert.ThrowsAsync<InvalidDataException>(() => Registry(instance).FindAsync(instance.ProjectId, audit.Id, Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => Registry(instance).ListAsync(instance.ProjectId, Ct));
        Assert.Contains(Id, find.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("no-profile")]
    [InlineData("no-judge")]
    [InlineData("no-authors")]
    [InlineData("blank-project")]
    public async Task A_header_with_a_missing_member_is_refused_before_anything_is_written(string shape)
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var audit = await SeedAuditAsync(instance);
        var invalid = shape switch
        {
            "no-profile" => audit with { Verification = null! },
            "no-judge" => audit with { Judge = null! },
            "no-authors" => audit with { DeclaredAuthors = null! },
            "blank-project" => audit with { ProjectId = string.Empty },
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Registry(instance).CreateAsync(invalid, Ct));

        Assert.Empty(await Registry(instance).ListAsync(instance.ProjectId, Ct));
    }

    [Fact]
    public async Task An_unknown_audit_finds_nothing()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);

        Assert.Null(await Registry(instance).FindAsync(instance.ProjectId, new RunId("aud-20261019-nadaaa"), Ct));
        Assert.Empty(await Registry(instance).ListAsync(instance.ProjectId, Ct));
    }

    [Fact]
    public async Task The_container_resolves_the_registry_of_audits_to_the_sqlite_one()
    {
        var services = new ServiceCollection();
        services.AddAgentStudioPersistence(new DecisionLogAnchorOptions(Path.Combine(_folder.DataDirectory, "anclas")));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.IsType<SqliteProjectAuditRegistry>(provider.GetRequiredService<IProjectAuditRegistry>());
        Assert.Same(provider.GetRequiredService<IProjectAuditRegistry>(), provider.GetRequiredService<IProjectAuditRegistry>());
    }
}
