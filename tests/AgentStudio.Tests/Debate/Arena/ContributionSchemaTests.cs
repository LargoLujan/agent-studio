using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentStudio.Application.Providers;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Providers;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Tests.Orchestration.Planning;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>
/// The schema of a turn (docs/15 D-10): the closed sets of <see cref="ContributionRules"/> as enums, in the portable subset
/// both CLIs accept, and small enough for Claude's command line.
/// </summary>
public sealed class ContributionSchemaTests
{
    private static ContributionRules Deliberation() => ContributionRules.For(DebateMode.Free, round: 2, published: 3, [new EvidenceId("EV-1"), new EvidenceId("EV-2")]);

    private static ContributionRules Proposals(DebateMode mode = DebateMode.Free) => ContributionRules.For(mode, round: 1, published: 0, [new EvidenceId("EV-1")]);

    /// <summary>Ids like the evidence store's: 32 hexadecimal characters.</summary>
    private static List<EvidenceId> StoreIds(int count) =>
        [.. Enumerable.Range(0, count).Select(n => new EvidenceId(n.ToString("x32", CultureInfo.InvariantCulture)))];

    private static JsonElement Root(ContributionRules rules) => JsonDocument.Parse(ContributionSchema.Build(rules)).RootElement.Clone();

    /// <summary>The schema at <paramref name="path"/>: member names, and <c>[]</c> for the items of an array.</summary>
    private static JsonElement At(JsonElement schema, string path)
    {
        foreach (var step in path.Split('/'))
        {
            schema = step == "[]" ? schema.GetProperty("items") : schema.GetProperty("properties").GetProperty(step);
        }

        return schema;
    }

    private static List<string>? Enum(JsonElement schema) =>
        schema.TryGetProperty("enum", out var values) ? [.. values.EnumerateArray().Select(v => v.GetString()!)] : null;

    private static List<string> Names(JsonElement schema) => [.. schema.GetProperty("properties").EnumerateObject().Select(p => p.Name)];

    private static string? Type(JsonElement schema) => schema.GetProperty("type").GetString();

    [Fact]
    public void The_schema_keeps_to_the_portable_subset_both_clis_accept()
    {
        ContributionRules[] turns =
        [
            Proposals(),
            Proposals(DebateMode.Tribunal),
            Deliberation(),
            ContributionRules.For(DebateMode.OrchestratorReview, round: 4, published: 24, StoreIds(200)),
            ContributionRules.For(DebateMode.Free, round: 2, published: 3, StoreIds(600)),
        ];

        foreach (var rules in turns)
        {
            var schema = ContributionSchema.Build(rules);

            Assert.Empty(StrictSchemaChecker.Problems(schema));
            Assert.Equal("object", Type(Root(rules)));
        }
    }

    [Fact]
    public void Its_members_are_those_the_parser_reads_in_the_order_of_the_canonical_form()
    {
        var root = Root(Deliberation());

        Assert.Equal(["kind", "repliesTo", "summary", "argument", "claims", "citations", "concedes", "finished"], Names(root));
        Assert.Equal(["claim", "support"], Names(At(root, "claims/[]")));
        Assert.Equal(["id", "path", "startLine", "endLine", "why"], Names(At(root, "citations/[]")));
        Assert.Equal(
            ["string", "string", "string", "string", "array", "array", "array", "boolean"],
            Names(root).Select(name => Type(At(root, name))));
        Assert.Equal(("object", "object"), (Type(At(root, "claims/[]")), Type(At(root, "citations/[]"))));
        Assert.Equal(("integer", "integer"), (Type(At(root, "citations/[]/startLine")), Type(At(root, "citations/[]/endLine"))));
        Assert.Equal(("string", "string"), (Type(At(root, "claims/[]/support/[]")), Type(At(root, "concedes/[]"))));
    }

    [Fact]
    public void Each_closed_set_offers_exactly_what_the_turn_may_write()
    {
        var root = Root(Deliberation());

        Assert.Equal(["Objection", "Rebuttal", "Evidence"], Enum(At(root, "kind")));
        Assert.Equal(["C1", "C2", "C3", "ninguna"], Enum(At(root, "repliesTo")));
        Assert.Equal(["C1", "C2", "C3", "EV-1", "EV-2", .. ContributionLabels.Citations], Enum(At(root, "claims/[]/support/[]")));
        Assert.Equal(ContributionLabels.Citations, Enum(At(root, "citations/[]/id")));
        Assert.Equal(["C1", "C2", "C3"], Enum(At(root, "concedes/[]")));
        Assert.Null(Enum(At(root, "summary")));
        Assert.Null(Enum(At(root, "citations/[]/path")));
    }

    [Theory]
    [InlineData(DebateMode.Free, "Proposal")]
    [InlineData(DebateMode.Adversarial, "Proposal")]
    [InlineData(DebateMode.ArchitectureReview, "Proposal")]
    [InlineData(DebateMode.ImplementationAudit, "Proposal")]
    [InlineData(DebateMode.OrchestratorReview, "Proposal")]
    [InlineData(DebateMode.Tribunal, "Assessment")]
    public void Round_one_offers_the_independent_kind_and_nothing_of_another_participant(DebateMode mode, string kind)
    {
        var rules = Proposals(mode);

        var schema = ContributionSchema.Build(rules);

        var root = Root(rules);
        Assert.Equal([kind], Enum(At(root, "kind")));
        Assert.Equal(["ninguna"], Enum(At(root, "repliesTo")));
        Assert.Equal(["EV-1", .. ContributionLabels.Citations], Enum(At(root, "claims/[]/support/[]")));
        Assert.Null(Enum(At(root, "concedes/[]"))); // the subset has no empty enum: the parser closes the set
        Assert.DoesNotMatch(new Regex("\"C[0-9]+\""), schema); // no label of a contribution anywhere (D-17)
    }

    [Fact]
    public void The_schema_travels_on_one_line()
    {
        var schema = ContributionSchema.Build(Deliberation());

        Assert.DoesNotContain('\n', schema);
        Assert.DoesNotContain('\r', schema);
    }

    [Fact]
    public void A_long_debate_with_a_large_material_fits_the_16_KB_claude_takes_on_its_command_line()
    {
        Assert.Equal(ClaudeCodeArguments.MaxJsonSchemaBytes, ContributionSchema.MaxBytes);
        var rules = ContributionRules.For(DebateMode.Adversarial, round: 4, published: 24, StoreIds(200));

        var schema = ContributionSchema.Build(rules);

        Assert.InRange(Encoding.UTF8.GetByteCount(schema), 1, ContributionSchema.MaxBytes);
        Assert.Equal(24 + 200 + ContributionLimits.MaxCitations, Enum(At(Root(rules), "claims/[]/support/[]"))!.Count);
        var participant = new AgentDefinition(new AgentId("arquitecto"), "Arquitecto", AgentRole.Architect, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"), "", [], [],
            [], new ExecutionBudget(12, TimeSpan.FromMinutes(5)), 1, MemoryPolicy.None, new ReviewPolicy(false, false));
        var arguments = ClaudeCodeArguments.Build(
            new AgentRunRequest(participant, "prompt", @"C:\copia", new PermissionProfile(false, false, false, [], [], []), schema),
            Guid.NewGuid().ToString(),
            null);
        Assert.Contains("--json-schema", arguments);
    }

    [Theory]
    [InlineData(600, 32)] // more than 16 KB with the enum
    [InlineData(260, 32)] // more than 250 values of more than 7 500 characters in one enum (OpenAI strict mode)
    [InlineData(1_000, 4)] // more than 1 000 enum values in the schema (OpenAI strict mode)
    public void A_material_too_large_for_the_support_enum_leaves_the_support_to_the_parser(int evidences, int idLength)
    {
        var evidence = Enumerable.Range(0, evidences).Select(n => new EvidenceId(n.ToString(CultureInfo.InvariantCulture).PadLeft(idLength, 'e'))).ToList();
        var rules = ContributionRules.For(DebateMode.Free, round: 2, published: 3, evidence);

        var schema = ContributionSchema.Build(rules);

        var root = Root(rules);
        Assert.InRange(Encoding.UTF8.GetByteCount(schema), 1, ContributionSchema.MaxBytes);
        Assert.Empty(StrictSchemaChecker.Problems(schema));
        Assert.Null(Enum(At(root, "claims/[]/support/[]")));
        Assert.Equal(["C1", "C2", "C3", "ninguna"], Enum(At(root, "repliesTo")));
        Assert.Equal(["C1", "C2", "C3"], Enum(At(root, "concedes/[]")));

        var answer = JsonNode.Parse(ContributionParser.ToJson(new ContributionAnswer(
            ContributionKind.Evidence, null, "Resumen.", "Argumento.", [new ContributionClaim("Afirmación.", [evidence[^1].Value])], [], [], false)))!.AsObject();
        Assert.Empty(ContributionParser.Parse(answer.ToJsonString(), rules).Problems);
        answer["claims"]![0]!["support"] = new JsonArray("EV-404");
        Assert.Contains(ContributionParser.Parse(answer.ToJsonString(), rules).Problems, p => p.StartsWith("'claims[0].support[0]' is not", StringComparison.Ordinal));
    }

    [Fact]
    public void A_material_of_many_short_ids_keeps_its_enum_while_it_fits()
    {
        var evidence = Enumerable.Range(1, 300).Select(n => new EvidenceId("EV-" + n.ToString(CultureInfo.InvariantCulture))).ToList();
        var rules = ContributionRules.For(DebateMode.Free, round: 2, published: 3, evidence);

        var support = Enum(At(Root(rules), "claims/[]/support/[]"));

        Assert.Equal(3 + 300 + ContributionLimits.MaxCitations, support!.Count);
    }

    [Fact]
    public void A_turn_too_large_even_without_the_support_enum_is_refused_instead_of_truncated()
    {
        var rules = ContributionRules.For(DebateMode.Free, round: 2, published: 3_000, []);

        var refusal = Assert.Throws<InvalidOperationException>(() => ContributionSchema.Build(rules));

        Assert.Contains("16384", refusal.Message, StringComparison.Ordinal);
    }
}
