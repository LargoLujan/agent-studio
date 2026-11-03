using AgentStudio.Domain.Requirements;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain;

public sealed class FrozenRequirementSetTests
{
    private static Requirement[] Baseline() =>
    [
        Req("R1", RequirementPriority.Mandatory, "login works", Crit("c1", VerificationMethod.AutomatedTest, "tests pass"), Crit("c2", VerificationMethod.HumanReview, "ux ok")),
        Req("R2", RequirementPriority.Desirable, "fast", Crit("c3", VerificationMethod.CommandCheck, "bench")),
    ];

    private static string HashOf(IEnumerable<Requirement> requirements, DateTimeOffset? at = null) =>
        FrozenRequirementSet.Freeze(requirements, at ?? T0).ContentHash;

    [Fact]
    public void Freezing_nothing_is_rejected()
    {
        Violation(() => FrozenRequirementSet.Freeze([], T0));
    }

    [Fact]
    public void Duplicate_requirement_ids_are_rejected()
    {
        Violation(() => FrozenRequirementSet.Freeze([Req("R1"), Req("R1")], T0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_statement_is_rejected(string statement)
    {
        Violation(() => FrozenRequirementSet.Freeze([Req("R1", RequirementPriority.Desirable, statement)], T0));
    }

    [Fact]
    public void A_mandatory_requirement_without_criteria_is_rejected_but_a_desirable_one_is_not()
    {
        var mandatory = new Requirement(Rid("R1"), "x", RequirementPriority.Mandatory, []);
        var desirable = new Requirement(Rid("R2"), "x", RequirementPriority.Desirable, []);

        Violation(() => FrozenRequirementSet.Freeze([mandatory], T0));
        var set = FrozenRequirementSet.Freeze([desirable], T0);
        Assert.Single(set.Requirements);
    }

    [Fact]
    public void The_hash_is_64_lowercase_hex()
    {
        Assert.Matches("^[0-9a-f]{64}$", HashOf(Baseline()));
    }

    [Fact]
    public void The_hash_does_not_depend_on_input_order()
    {
        var forward = HashOf(Baseline());
        var backward = HashOf(Baseline().Reverse());

        Assert.Equal(forward, backward);
    }

    [Fact]
    public void The_hash_does_not_depend_on_criteria_order()
    {
        var swapped = Baseline();
        swapped[0] = swapped[0] with { Criteria = swapped[0].Criteria.Reverse().ToList() };

        Assert.Equal(HashOf(Baseline()), HashOf(swapped));
    }

    [Fact]
    public void The_hash_does_not_depend_on_FrozenAt()
    {
        Assert.Equal(HashOf(Baseline(), T0), HashOf(Baseline(), T0.AddYears(3)));
        Assert.Equal(T0.AddYears(3), FrozenRequirementSet.Freeze(Baseline(), T0.AddYears(3)).FrozenAt);
    }

    [Fact]
    public void Random_input_orders_always_give_the_same_hash()
    {
        var random = new Random(7);
        var expected = HashOf(Baseline());
        for (var i = 0; i < 100; i++)
        {
            var shuffled = Baseline().OrderBy(_ => random.Next()).ToList();
            Assert.Equal(expected, HashOf(shuffled));
        }
    }

    public static TheoryData<string> Mutations() =>
    [
        "statement",
        "priority",
        "criterion-description",
        "criterion-method",
        "criterion-id",
        "criterion-removed",
        "criterion-added",
        "requirement-id",
        "requirement-added",
    ];

    [Theory]
    [MemberData(nameof(Mutations))]
    public void The_hash_changes_when_any_field_changes(string mutation)
    {
        var reqs = Baseline();
        var r1 = reqs[0];
        reqs[0] = mutation switch
        {
            "statement" => r1 with { Statement = "login works!" },
            "priority" => r1 with { Priority = RequirementPriority.Desirable },
            "criterion-description" => r1 with { Criteria = [r1.Criteria[0] with { Description = "tests pass." }, r1.Criteria[1]] },
            "criterion-method" => r1 with { Criteria = [r1.Criteria[0] with { Method = VerificationMethod.CommandCheck }, r1.Criteria[1]] },
            "criterion-id" => r1 with { Criteria = [r1.Criteria[0] with { Id = "c1b" }, r1.Criteria[1]] },
            "criterion-removed" => r1 with { Criteria = [r1.Criteria[0]] },
            "criterion-added" => r1 with { Criteria = [.. r1.Criteria, Crit("c9")] },
            "requirement-id" => r1 with { Id = Rid("R1b") },
            _ => r1,
        };
        IEnumerable<Requirement> changed = mutation == "requirement-added" ? [.. reqs, Req("R3")] : reqs;

        Assert.NotEqual(HashOf(Baseline()), HashOf(changed));
    }

    [Fact]
    public void Text_moved_between_adjacent_fields_does_not_collide()
    {
        var a = Req("R1", RequirementPriority.Desirable, "ab");
        var b = Req("R1b", RequirementPriority.Desirable, "b");
        var c = Req("R1", RequirementPriority.Desirable, "a");
        var d = Req("R1bb", RequirementPriority.Desirable, "b");

        Assert.NotEqual(HashOf([a]), HashOf([b]));
        Assert.NotEqual(HashOf([c, b]), HashOf([d, c]));
    }

    [Fact]
    public void Find_and_Mandatory_expose_the_frozen_content()
    {
        var set = FrozenRequirementSet.Freeze(Baseline(), T0);

        Assert.Equal("login works", set.Find(Rid("R1"))!.Statement);
        Assert.Null(set.Find(Rid("nope")));
        Assert.Equal([Rid("R1")], set.Mandatory.Select(r => r.Id));
    }
}
