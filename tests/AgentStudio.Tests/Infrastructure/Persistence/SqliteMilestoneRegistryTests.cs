using System.IO;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Application.Work;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Tests.Domain.Orchestration;
using Microsoft.Data.Sqlite;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// docs/16 D-17 and ADR-0038: the immutable header of a milestone in <c>milestones</c>. It is found after reopening exactly as it
/// was created, its hash (the one <c>MilestoneOpened</c> anchors) makes the round trip and is the one the row keeps, the same header
/// twice writes nothing, another one under its id is refused, and a header changed behind the store's back is never read.
/// </summary>
public sealed class SqliteMilestoneRegistryTests : IDisposable
{
    private const string Id = "mil-20261111-a1b2c3";

    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SqliteMilestoneRegistry Registry(ProjectInstance instance) => new(instance.Databases);

    /// <summary>Stores the first set of requirements the header points at and builds a header with every member set.</summary>
    private static async Task<MilestoneHeader> SeedAsync(ProjectInstance instance, string id = Id, WorkBudget? budget = null)
    {
        var requirements = PlanFixtures.Requirements();
        await instance.Requirements.SaveAsync(instance.ProjectId, requirements, Ct);
        return new MilestoneHeader(
            new MilestoneId(id),
            "Primera entrega \"alfa\"",
            "Que la calculadora multiplique,\ncon sus tests.",
            requirements.ContentHash,
            budget ?? new WorkBudget(2_000_000, null, BudgetPeriod.Total),
            T0.AddTicks(9_876_543));
    }

    [Fact]
    public async Task A_milestone_header_is_found_after_reopening_exactly_as_it_was_created()
    {
        MilestoneHeader header;
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            header = await SeedAsync(first);
            await Registry(first).CreateAsync(first.ProjectId, header, Ct);
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var found = await Registry(second).FindAsync(second.ProjectId, new MilestoneId(Id), Ct);

        Assert.NotNull(found);
        Assert.Equal(Shape(header), Shape(found));
        Assert.Null(found.Budget!.MaxAgentRuns);
    }

    [Fact]
    public async Task The_hash_MilestoneOpened_anchors_makes_the_round_trip_and_is_the_hash_the_row_keeps()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var header = await SeedAsync(instance);
        await Registry(instance).CreateAsync(instance.ProjectId, header, Ct);

        var found = await Registry(instance).FindAsync(instance.ProjectId, header.Id, Ct);

        Assert.NotNull(found);
        var anchored = MilestoneHeaderHash.Of(header);
        Assert.Equal(anchored, MilestoneHeaderHash.Of(found));
        using var connection = instance.Connect();
        Assert.Equal(anchored, Text(connection, $"SELECT header_hash FROM milestones WHERE id = '{Id}'"));
        Assert.Equal("""{"maxTokens":2000000,"period":"Total"}""", Text(connection, $"SELECT budget_json FROM milestones WHERE id = '{Id}'"));
    }

    [Fact]
    public async Task A_milestone_without_a_budget_is_found_without_one()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var header = (await SeedAsync(instance)) with { Budget = null };

        await Registry(instance).CreateAsync(instance.ProjectId, header, Ct);
        var found = await Registry(instance).FindAsync(instance.ProjectId, header.Id, Ct);

        Assert.NotNull(found);
        Assert.Null(found.Budget);
        using var connection = instance.Connect();
        Assert.Equal(1, Scalar(connection, $"SELECT COUNT(*) FROM milestones WHERE id = '{Id}' AND budget_json IS NULL"));
    }

    [Fact]
    public async Task Milestones_are_listed_in_the_order_they_were_created()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var first = await SeedAsync(instance, "mil-20261111-ffffff");
        var second = (await SeedAsync(instance, "mil-20261110-000000")) with { CreatedAt = T0.AddDays(-1) };
        await Registry(instance).CreateAsync(instance.ProjectId, first, Ct);
        await Registry(instance).CreateAsync(instance.ProjectId, second, Ct);

        var headers = await Registry(instance).ListAsync(instance.ProjectId, Ct);

        Assert.Equal([first.Id, second.Id], headers.Select(h => h.Id));
        Assert.Null(await Registry(instance).FindAsync(instance.ProjectId, new MilestoneId("mil-20260101-000000"), Ct));
    }

    [Fact]
    public async Task Creating_the_same_milestone_again_is_harmless()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var header = await SeedAsync(instance);

        await Registry(instance).CreateAsync(instance.ProjectId, header, Ct);
        await Registry(instance).CreateAsync(instance.ProjectId, header with { CreatedAt = header.CreatedAt.ToOffset(TimeSpan.FromHours(2)) }, Ct);

        Assert.Single(await Registry(instance).ListAsync(instance.ProjectId, Ct));
    }

    public static TheoryData<string> OtherHeaders => ["objective", "name", "looser-budget", "no-budget", "time"];

    [Theory]
    [MemberData(nameof(OtherHeaders))]
    public async Task The_same_id_with_another_header_is_refused_and_the_first_header_stays(string change)
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var header = await SeedAsync(instance);
        await Registry(instance).CreateAsync(instance.ProjectId, header, Ct);
        var other = change switch
        {
            "objective" => header with { Objective = "Otro objetivo" },
            "name" => header with { Name = "Otro nombre" },
            "looser-budget" => header with { Budget = new WorkBudget(9_000_000, null, BudgetPeriod.Total) },
            "no-budget" => header with { Budget = null },
            "time" => header with { CreatedAt = header.CreatedAt.AddSeconds(1) },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Registry(instance).CreateAsync(instance.ProjectId, other, Ct));

        Assert.Equal(Shape(header), Shape(await Registry(instance).FindAsync(instance.ProjectId, header.Id, Ct)));
    }

    [Fact]
    public async Task A_milestone_must_point_at_stored_requirements()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var header = (await SeedAsync(instance)) with { RequirementSetHash = new string('c', 64) };

        var ex = await Assert.ThrowsAsync<SqliteException>(() => Registry(instance).CreateAsync(instance.ProjectId, header, Ct));

        Assert.Contains("FOREIGN KEY", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await Registry(instance).ListAsync(instance.ProjectId, Ct));
    }

    [Fact]
    public async Task A_header_the_domain_does_not_allow_is_refused_before_it_is_written()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var header = await SeedAsync(instance);

        await Assert.ThrowsAsync<ArgumentException>(() => Registry(instance).CreateAsync(instance.ProjectId, header with { Name = "  " }, Ct));
        await Assert.ThrowsAsync<DomainRuleViolationException>(() => Registry(instance).CreateAsync(instance.ProjectId, header with { Budget = new WorkBudget(null, null) }, Ct));

        Assert.Empty(await Registry(instance).ListAsync(instance.ProjectId, Ct));
    }

    public static TheoryData<string> Tamperings => new()
    {
        // A looser budget, with the old hash left in place.
        """UPDATE milestones SET budget_json = '{"maxTokens":90000000,"period":"Total"}'""",
        // Another objective.
        "UPDATE milestones SET objective = 'Haz otra cosa'",
        "UPDATE milestones SET name = 'Otro'",
        // A hash forged alone.
        "UPDATE milestones SET header_hash = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'",
    };

    [Theory]
    [MemberData(nameof(Tamperings))]
    public async Task A_header_changed_behind_the_stores_back_is_never_read(string tampering)
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var header = await SeedAsync(instance);
        await Registry(instance).CreateAsync(instance.ProjectId, header, Ct);
        using (var connection = instance.Connect())
        {
            Exec(connection, "DROP TRIGGER milestones_no_update");
            Exec(connection, tampering);
        }

        var find = await Assert.ThrowsAsync<InvalidDataException>(() => Registry(instance).FindAsync(instance.ProjectId, header.Id, Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => Registry(instance).ListAsync(instance.ProjectId, Ct));
        Assert.Contains(Id, find.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> ForeignRows => new()
    {
        """UPDATE milestones SET budget_json = '{"maxTokens":"mucho"}'""",
        """UPDATE milestones SET budget_json = '{"period":"Siempre"}'""",
        "UPDATE milestones SET budget_json = 'no es json'",
        "UPDATE milestones SET created_at = 'ayer'",
    };

    [Theory]
    [MemberData(nameof(ForeignRows))]
    public async Task A_row_this_store_did_not_write_is_invalid_data(string tampering)
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var header = await SeedAsync(instance);
        await Registry(instance).CreateAsync(instance.ProjectId, header, Ct);
        using (var connection = instance.Connect())
        {
            Exec(connection, "DROP TRIGGER milestones_no_update");
            Exec(connection, tampering);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => Registry(instance).FindAsync(instance.ProjectId, header.Id, Ct));
    }

    [Fact]
    public async Task The_table_refuses_update_delete_and_replace()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var header = await SeedAsync(instance);
        await Registry(instance).CreateAsync(instance.ProjectId, header, Ct);

        using var connection = instance.Connect();
        Assert.Contains("append-only", Assert.Throws<SqliteException>(() => Exec(connection, "UPDATE milestones SET name = 'x'")).Message, StringComparison.Ordinal);
        Assert.Contains("append-only", Assert.Throws<SqliteException>(() => Exec(connection, "DELETE FROM milestones")).Message, StringComparison.Ordinal);
        Assert.Contains("append-only", Assert.Throws<SqliteException>(() => Exec(connection, $"""
            INSERT OR REPLACE INTO milestones(id, project_id, created_at, name, objective, requirement_set_hash, header_hash)
            VALUES ('{Id}', '{instance.ProjectId}', '2026-11-11T00:00:00.0000000+00:00', 'x', 'y', '{header.RequirementSetHash}', 'h')
            """)).Message, StringComparison.Ordinal);
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM milestones"));
    }
}
