using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Providers;
using AgentStudio.Debate.Judging;
using AgentStudio.Debate.Judging.Debates;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Providers.Cli.Claude;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Debate.Judging;

/// <summary>
/// docs/15 D-25: the schema the judge of a debate answers with is the verdict schema of Phase 3 plus the resolution (the
/// comparison of the proposals and the one chosen, by label, or «ninguna»), in the portable subset both CLIs accept and in
/// the 16 KB Claude takes on its command line.
/// </summary>
public sealed class DebateVerdictSchemaTests
{
    private static readonly FrozenRequirementSet Requirements = FrozenRequirementSet.Freeze(
        [
            Req("RQ-01", RequirementPriority.Mandatory, "a", Crit("c1", VerificationMethod.JudgeAssessment)),
            Req("RQ-02", RequirementPriority.Desirable, "b", Crit("c2", VerificationMethod.HumanReview)),
        ],
        T0);

    private static readonly JudgeIdentity SystemJudge = new(new AgentId("juez"), new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5"));

    private static JsonElement Parse(string schema) => JsonDocument.Parse(schema).RootElement.Clone();

    private static IEnumerable<string?> EnumValues(JsonElement node) => node.GetProperty("enum").EnumerateArray().Select(e => e.GetString());

    private static JsonElement Resolution(JsonElement root) => root.GetProperty("properties").GetProperty("resolution");

    [Fact]
    public void The_schema_of_a_debate_keeps_to_the_strict_mode_rules_both_clis_accept()
    {
        var root = Parse(DebateVerdictSchema.Build(Requirements, ["P1", "P2", "P3"]));

        Assert.Equal("object", root.GetProperty("type").GetString());
        VerdictSchemaTests.AssertStrictShape(root, "$");
    }

    [Fact]
    public void The_judge_chooses_one_of_the_proposals_of_the_debate_or_none_and_compares_only_those()
    {
        var root = Parse(DebateVerdictSchema.Build(Requirements, ["P1", "P2", "P3"]));

        var resolution = Resolution(root).GetProperty("properties");
        Assert.Equal(["P1", "P2", "P3", "ninguna"], EnumValues(resolution.GetProperty("chosenProposal")));
        Assert.Equal(["P1", "P2", "P3"], EnumValues(resolution.GetProperty("comparison").GetProperty("items").GetProperty("properties").GetProperty("proposal")));
        var requirement = root.GetProperty("properties").GetProperty("assessments").GetProperty("items").GetProperty("properties").GetProperty("requirement");
        Assert.Equal(["RQ-01", "RQ-02"], EnumValues(requirement));
    }

    /// <summary>The second judge of a panel reads the proposals in reverse (D-14): the enum keeps the order it is given.</summary>
    [Fact]
    public void The_proposals_are_offered_in_the_order_the_judge_reads_them()
    {
        var root = Parse(DebateVerdictSchema.Build(Requirements, ["P3", "P2", "P1"]));

        Assert.Equal(["P3", "P2", "P1", "ninguna"], EnumValues(Resolution(root).GetProperty("properties").GetProperty("chosenProposal")));
    }

    [Fact]
    public void The_judge_compares_the_proposals_before_it_chooses_and_chooses_before_it_decides()
    {
        var root = Parse(DebateVerdictSchema.Build(Requirements, ["P1", "P2"]));

        var order = root.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        Assert.True(order.IndexOf("assessments") < order.IndexOf("resolution"), string.Join(",", order));
        Assert.True(order.IndexOf("findings") < order.IndexOf("resolution"), string.Join(",", order));
        Assert.True(order.IndexOf("resolution") < order.IndexOf("decision"), string.Join(",", order));
        Assert.Equal(["comparison", "chosenProposal"], Resolution(root).GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    /// <summary>A debate that reviews work (or a tribunal) has no proposal to choose: the schema is the verdict schema of Phase 3, without a resolution.</summary>
    [Fact]
    public void Without_proposals_there_is_no_resolution_and_the_members_are_those_of_phase_3()
    {
        var root = Parse(DebateVerdictSchema.Build(Requirements, []));
        var phase3 = Parse(VerdictSchema.Build(Requirements));

        Assert.False(root.GetProperty("properties").TryGetProperty("resolution", out _));
        Assert.Equal(phase3.GetProperty("properties").EnumerateObject().Select(p => p.Name), root.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        VerdictSchemaTests.AssertStrictShape(root, "$");
    }

    [Fact]
    public void The_schema_with_50_requirements_and_6_proposals_fits_in_the_16_KB_that_claude_accepts()
    {
        Assert.Equal(ClaudeCodeArguments.MaxJsonSchemaBytes, DebateVerdictSchema.MaxBytes);
        var requirements = FrozenRequirementSet.Freeze(
            Enumerable.Range(0, 50).Select(i => Req(("RQ-" + i.ToString("D5", CultureInfo.InvariantCulture)).PadRight(32, 'x'))),
            T0);

        var schema = DebateVerdictSchema.Build(requirements, ["P1", "P2", "P3", "P4", "P5", "P6"]);

        Assert.InRange(Encoding.UTF8.GetByteCount(schema), 1, DebateVerdictSchema.MaxBytes);
        var judge = new AgentDefinition(new AgentId("juez"), "Juez", AgentRole.Judge, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic), "", [], [],
            [], new ExecutionBudget(5, TimeSpan.FromMinutes(5)), 1, MemoryPolicy.None, new ReviewPolicy(false, false));
        var arguments = ClaudeCodeArguments.Build(
            new AgentRunRequest(judge, "prompt", "C:\\work", new PermissionProfile(false, false, false, [], [], []), schema),
            Guid.NewGuid().ToString(),
            null);
        Assert.Contains("--json-schema", arguments);
        VerdictSchemaTests.AssertStrictShape(Parse(schema), "$");
    }

    [Fact]
    public void A_debate_too_large_for_a_16_KB_schema_is_refused_instead_of_truncated()
    {
        var requirements = FrozenRequirementSet.Freeze(
            Enumerable.Range(0, 600).Select(i => Req(("RQ-" + i.ToString("D5", CultureInfo.InvariantCulture)).PadRight(32, 'x'))),
            T0);

        var ex = Assert.Throws<InvalidOperationException>(() => DebateVerdictSchema.Build(requirements, ["P1", "P2"]));

        Assert.Contains("16384", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("P1", "P1")]
    [InlineData("P1", " ")]
    [InlineData("P1", "ninguna")]
    public void Proposals_are_distinct_labels_and_never_the_sentinel(string first, string second)
    {
        Assert.Throws<ArgumentException>(() => DebateVerdictSchema.Build(Requirements, [first, second]));
    }

    [Fact]
    public void Whatever_the_schema_accepts_the_parser_of_a_debate_turns_into_a_well_formed_verdict()
    {
        var root = Parse(DebateVerdictSchema.Build(Requirements, ["P1", "P2"]));
        var dossier = Dossier(Requirements.Requirements) with { Debate = new DebateDossierContext(DebateMode.Free, ["P1", "P2"], Choice: true) };

        // Every value of every enum, one sample per value: the schema and the parser must agree on all of them.
        var enums = new List<(string Path, string Value)>();
        VerdictSchemaTests.Sample(root, (path, values) =>
        {
            enums.AddRange(values.Select(v => (path, v)));
            return values[0];
        });

        Assert.Contains(enums, e => e.Path == "$.resolution.chosenProposal" && e.Value == "ninguna");
        foreach (var (path, value) in enums)
        {
            var json = VerdictSchemaTests.Sample(root, (p, values) => p == path ? value : values[0])!.ToJsonString();

            var verdict = VerdictJson.ParseDebate(json, SystemJudge, T0);

            Assert.NotNull(verdict.Resolution);
            Assert.DoesNotContain(VerdictValidator.Validate(verdict, dossier), v => v.Code is VerdictViolationCode.MalformedVerdict or VerdictViolationCode.DebateResolutionUnknown);
        }
    }
}
