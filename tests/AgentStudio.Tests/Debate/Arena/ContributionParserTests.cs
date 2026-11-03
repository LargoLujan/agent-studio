using System.Text.Json.Nodes;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;

namespace AgentStudio.Tests.Debate.Arena;

public sealed class ContributionParserTests
{
    private static readonly EvidenceId Report = new("EV-1");
    private static readonly EvidenceId TestRun = new("EV-2");

    /// <summary>Round 2 of a free debate: three contributions published (C1 to C3) and two evidences of the material.</summary>
    private static ContributionRules Deliberation() => ContributionRules.For(DebateMode.Free, round: 2, published: 3, [Report, TestRun]);

    /// <summary>Round 1: nothing is published, one evidence of the material.</summary>
    private static ContributionRules Proposals(DebateMode mode = DebateMode.Free) => ContributionRules.For(mode, round: 1, published: 0, [Report]);

    /// <summary>An objection to C2 that concedes C1, with two claims and two citations, valid for <see cref="Deliberation"/>.</summary>
    private static JsonObject Objection() => JsonNode.Parse("""
        {
          "kind": "Objection",
          "repliesTo": "C2",
          "summary": "La propuesta C2 no valida la entrada.",
          "argument": "C2 acepta valores negativos sin comprobarlos y el test EV-2 no cubre ese caso.",
          "claims": [
            { "claim": "Multiply no comprueba el desbordamiento.", "support": ["C2", "CITA-1"] },
            { "claim": "Los tests no cubren los negativos.", "support": ["EV-2", "CITA-2"] }
          ],
          "citations": [
            { "id": "CITA-1", "path": "src/Calc/Calc.cs", "startLine": 10, "endLine": 14, "why": "Aquí se multiplica sin checked." },
            { "id": "CITA-2", "path": "tests/Calc.Tests/CalcTests.cs", "startLine": 1, "endLine": 80, "why": "Ningún caso negativo." }
          ],
          "concedes": ["C1"],
          "finished": false
        }
        """)!.AsObject();

    /// <summary>An independent proposal, valid for <see cref="Proposals"/>: it can only cite the material and its own citations.</summary>
    private static JsonObject Proposal(string kind = "Proposal") => JsonNode.Parse($$"""
        {
          "kind": "{{kind}}",
          "repliesTo": "ninguna",
          "summary": "Usar aritmética comprobada.",
          "argument": "Multiplicar con checked detecta el desbordamiento en el sitio.",
          "claims": [
            { "claim": "El informe EV-1 ya señala el desbordamiento.", "support": ["EV-1", "CITA-1"] }
          ],
          "citations": [
            { "id": "CITA-1", "path": "src\\Calc\\Calc.cs", "startLine": 3, "endLine": 3, "why": "La firma de Multiply." }
          ],
          "concedes": [],
          "finished": true
        }
        """)!.AsObject();

    private static ContributionParseResult Parse(JsonNode answer, ContributionRules? rules = null) =>
        ContributionParser.Parse(answer.ToJsonString(), rules ?? Deliberation());

    private static void AssertRejected(ContributionParseResult result, string? mentions = null)
    {
        Assert.Null(result.Answer);
        Assert.NotEmpty(result.Problems);
        if (mentions is not null)
        {
            Assert.Contains(result.Problems, p => p.Contains(mentions, StringComparison.Ordinal));
        }
    }

    private static JsonObject Member(JsonObject answer, string level) => level switch
    {
        "root" => answer,
        "claim" => answer["claims"]![0]!.AsObject(),
        "citation" => answer["citations"]![0]!.AsObject(),
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    // ---- what a valid answer becomes ----

    [Fact]
    public void A_valid_answer_becomes_the_contribution_it_describes()
    {
        var result = Parse(Objection());

        Assert.Empty(result.Problems);
        var answer = Assert.IsType<ContributionAnswer>(result.Answer);
        Assert.Equal(ContributionKind.Objection, answer.Kind);
        Assert.Equal("C2", answer.RepliesTo);
        Assert.Equal(1, answer.RepliesToIndex);
        Assert.Equal("La propuesta C2 no valida la entrada.", answer.Summary);
        Assert.Equal("C2 acepta valores negativos sin comprobarlos y el test EV-2 no cubre ese caso.", answer.Argument);
        Assert.Equal(["Multiply no comprueba el desbordamiento.", "Los tests no cubren los negativos."], answer.Claims.Select(c => c.Claim));
        Assert.Equal(["C2", "CITA-1"], answer.Claims[0].Support);
        Assert.Equal(["EV-2", "CITA-2"], answer.Claims[1].Support);
        Assert.Equal(
            [("CITA-1", "src/Calc/Calc.cs", 10, 14, "Aquí se multiplica sin checked."), ("CITA-2", "tests/Calc.Tests/CalcTests.cs", 1, 80, "Ningún caso negativo.")],
            answer.Citations.Select(c => (c.Id, c.Path, c.StartLine, c.EndLine, c.Why)));
        Assert.Equal("src/Calc/Calc.cs:10-14", answer.Citations[0].Locator);
        Assert.Equal(["C1"], answer.Concedes);
        Assert.False(answer.Finished);
    }

    [Fact]
    public void An_independent_proposal_replies_to_nothing_and_ninguna_is_the_sentinel_for_it()
    {
        var result = Parse(Proposal(), Proposals());

        Assert.Empty(result.Problems);
        var answer = Assert.IsType<ContributionAnswer>(result.Answer);
        Assert.Equal(ContributionKind.Proposal, answer.Kind);
        Assert.Null(answer.RepliesTo);
        Assert.Null(answer.RepliesToIndex);
        Assert.Empty(answer.Concedes);
        Assert.True(answer.Finished);
        Assert.Equal(@"src\Calc\Calc.cs", answer.Citations[0].Path);
    }

    // ---- the kinds each round admits ----

    [Theory]
    [InlineData(DebateMode.Free)]
    [InlineData(DebateMode.Adversarial)]
    [InlineData(DebateMode.ArchitectureReview)]
    [InlineData(DebateMode.ImplementationAudit)]
    [InlineData(DebateMode.OrchestratorReview)]
    public void Round_one_only_admits_a_proposal(DebateMode mode)
    {
        Assert.Equal([ContributionKind.Proposal], ContributionRules.KindsFor(mode, 1));
        Assert.NotNull(Parse(Proposal(), Proposals(mode)).Answer);

        foreach (var kind in new[] { "Assessment", "Objection", "Rebuttal", "Evidence" })
        {
            AssertRejected(Parse(Proposal(kind), Proposals(mode)), kind);
        }
    }

    [Fact]
    public void A_tribunal_only_admits_independent_assessments()
    {
        Assert.Equal([ContributionKind.Assessment], ContributionRules.KindsFor(DebateMode.Tribunal, 1));
        Assert.NotNull(Parse(Proposal("Assessment"), Proposals(DebateMode.Tribunal)).Answer);

        AssertRejected(Parse(Proposal("Proposal"), Proposals(DebateMode.Tribunal)), "Proposal");
    }

    [Theory]
    [InlineData("Objection", true)]
    [InlineData("Rebuttal", true)]
    [InlineData("Evidence", true)]
    [InlineData("Proposal", false)]
    [InlineData("Assessment", false)]
    public void Later_rounds_admit_objections_rebuttals_and_evidence(string kind, bool admitted)
    {
        var answer = Objection();
        answer["kind"] = kind;

        var result = Parse(answer);

        if (admitted)
        {
            Assert.Empty(result.Problems);
            Assert.Equal(Enum.Parse<ContributionKind>(kind), result.Answer!.Kind);
        }
        else
        {
            AssertRejected(result, kind);
        }
    }

    [Theory]
    [InlineData("objection")]
    [InlineData("OBJECTION")]
    [InlineData("2")]
    [InlineData("Vote")]
    [InlineData("")]
    public void A_kind_outside_the_closed_set_is_rejected(string kind)
    {
        var answer = Objection();
        answer["kind"] = kind;

        AssertRejected(Parse(answer), "kind");
    }

    [Fact]
    public void A_tribunal_has_no_later_rounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ContributionRules.KindsFor(DebateMode.Tribunal, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContributionRules.For(DebateMode.Tribunal, 2, 2, []));
    }

    // ---- the turn rules themselves ----

    [Fact]
    public void Round_one_rules_cannot_carry_published_contributions_because_round_one_is_sealed()
    {
        Assert.Throws<ArgumentException>(() => ContributionRules.For(DebateMode.Free, 1, 1, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContributionRules.For(DebateMode.Free, 0, 0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContributionRules.For(DebateMode.Free, 2, -1, []));
    }

    [Fact]
    public void The_published_contributions_are_labelled_in_publication_order()
    {
        var rules = Deliberation();

        Assert.Equal(["C1", "C2", "C3"], rules.Contributions);
        Assert.Equal("C1", ContributionLabels.Of(0));
        Assert.Equal(2, ContributionLabels.IndexOf("C3"));
        Assert.Null(ContributionLabels.IndexOf("C0"));
        Assert.Null(ContributionLabels.IndexOf("c3"));
        Assert.Null(ContributionLabels.IndexOf("C03"));
        Assert.Null(ContributionLabels.IndexOf("ninguna"));
        Assert.Equal(
            ["CITA-1", "CITA-2", "CITA-3", "CITA-4", "CITA-5", "CITA-6", "CITA-7", "CITA-8"],
            ContributionLabels.Citations);
    }

    [Theory]
    [InlineData("C2")]
    [InlineData("CITA-1")]
    [InlineData("ninguna")]
    public void An_evidence_id_that_reads_as_a_label_is_refused(string id)
    {
        Assert.Throws<ArgumentException>(() => ContributionRules.For(DebateMode.Free, 2, 3, [new EvidenceId(id)]));
    }

    // ---- repliesTo, support and concedes only name what is published ----

    [Theory]
    [InlineData("C4")]
    [InlineData("C0")]
    [InlineData("EV-1")]
    [InlineData("CITA-1")]
    [InlineData("Ninguna")]
    [InlineData("")]
    public void RepliesTo_is_a_published_contribution_or_ninguna(string target)
    {
        var answer = Objection();
        answer["repliesTo"] = target;

        AssertRejected(Parse(answer), "repliesTo");
    }

    [Fact]
    public void In_round_one_repliesTo_must_be_ninguna()
    {
        var answer = Proposal();
        answer["repliesTo"] = "C1";

        AssertRejected(Parse(answer, Proposals()), "round 1");
    }

    [Theory]
    [InlineData("C4")]
    [InlineData("EV-9")]
    [InlineData("CITA-3")]
    [InlineData("ninguna")]
    public void A_support_that_is_not_published_material_nor_a_citation_of_this_contribution_is_rejected(string support)
    {
        var answer = Objection();
        answer["claims"]![0]!["support"]!.AsArray().Add(support);

        AssertRejected(Parse(answer), "claims[0].support[2]");
    }

    [Fact]
    public void A_proposal_cannot_support_a_claim_with_a_contribution_of_another_participant()
    {
        var answer = Proposal();
        answer["claims"]![0]!["support"]!.AsArray().Add("C1");

        AssertRejected(Parse(answer, Proposals()), "claims[0].support[2]");
    }

    [Fact]
    public void A_support_listed_twice_in_a_claim_is_rejected()
    {
        var answer = Objection();
        answer["claims"]![0]!["support"]!.AsArray().Add("C2");

        AssertRejected(Parse(answer), "C2");
    }

    [Fact]
    public void A_claim_without_support_is_still_a_claim()
    {
        var answer = Objection();
        answer["claims"]![0]!["support"] = new JsonArray();

        Assert.Empty(Parse(answer).Problems);
    }

    [Theory]
    [InlineData("C4")]
    [InlineData("EV-1")]
    [InlineData("ninguna")]
    public void Concedes_names_published_contributions_only(string conceded)
    {
        var answer = Objection();
        answer["concedes"]!.AsArray().Add(conceded);

        AssertRejected(Parse(answer), "concedes");
    }

    [Fact]
    public void A_contribution_conceded_twice_is_rejected()
    {
        var answer = Objection();
        answer["concedes"]!.AsArray().Add("C1");

        AssertRejected(Parse(answer), "concedes");
    }

    // ---- citations ----

    [Theory]
    [InlineData("CITA-9")]
    [InlineData("CITA-0")]
    [InlineData("cita-1")]
    [InlineData("C1")]
    [InlineData("")]
    public void A_citation_id_outside_the_closed_set_is_rejected(string id)
    {
        var answer = Objection();
        answer["citations"]![1]!["id"] = id;

        AssertRejected(Parse(answer), "citations[1].id");
    }

    [Fact]
    public void Two_citations_with_the_same_id_are_rejected()
    {
        var answer = Objection();
        answer["citations"]![1]!["id"] = "CITA-1";
        answer["claims"]![1]!["support"] = new JsonArray("EV-2");

        AssertRejected(Parse(answer), "CITA-1");
    }

    [Theory]
    [InlineData("../secretos.txt")]
    [InlineData("src/../../secretos.txt")]
    [InlineData("./src/Calc/Calc.cs")]
    [InlineData("/etc/passwd")]
    [InlineData("\\Windows\\win.ini")]
    [InlineData("C:/Users/alguien/.ssh/id_rsa")]
    [InlineData("C:\\Users\\alguien\\.ssh\\id_rsa")]
    [InlineData("\\\\servidor\\recurso\\x.cs")]
    [InlineData("src/Calc/Calc.cs:secreto")]
    [InlineData("src//Calc/Calc.cs")]
    [InlineData("src/Calc/")]
    [InlineData("src/Calc/Calc.cs.")]
    [InlineData("src/Calc/Calc.cs ")]
    [InlineData("src/NUL")]
    [InlineData("CON.txt")]
    [InlineData("src/Calc/Ca*c.cs")]
    [InlineData("src/Calc/\u0001.cs")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_citation_path_that_is_not_relative_to_the_snapshot_is_a_problem_the_participant_can_correct(string path)
    {
        var answer = Objection();
        answer["citations"]![0]!["path"] = path;

        AssertRejected(Parse(answer), "citations[0].path");
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-3, 5)]
    [InlineData(10, 9)]
    [InlineData(1, 81)]
    [InlineData(100, 180)]
    public void A_line_range_that_is_not_one_to_eighty_lines_is_rejected(int startLine, int endLine)
    {
        var answer = Objection();
        answer["citations"]![0]!["startLine"] = startLine;
        answer["citations"]![0]!["endLine"] = endLine;

        AssertRejected(Parse(answer), "citations[0]");
    }

    [Fact]
    public void Eighty_lines_is_the_longest_citation()
    {
        var answer = Objection();
        answer["citations"]![0]!["startLine"] = 101;
        answer["citations"]![0]!["endLine"] = 180;

        Assert.Empty(Parse(answer).Problems);
    }

    // ---- caps ----

    [Theory]
    [InlineData("summary", ContributionLimits.MaxSummaryChars)]
    [InlineData("argument", ContributionLimits.MaxArgumentChars)]
    public void A_text_member_may_take_its_cap_and_no_more(string member, int cap)
    {
        var atCap = Objection();
        atCap[member] = new string('a', cap);
        var overCap = Objection();
        overCap[member] = new string('a', cap + 1);

        Assert.Empty(Parse(atCap).Problems);
        AssertRejected(Parse(overCap), member);
    }

    [Theory]
    [InlineData("claim", "claim", ContributionLimits.MaxClaimChars)]
    [InlineData("citation", "why", ContributionLimits.MaxWhyChars)]
    [InlineData("citation", "path", ContributionLimits.MaxPathChars)]
    public void The_text_of_a_claim_or_citation_has_a_cap_too(string level, string member, int cap)
    {
        var text = member == "path" ? "src/" + new string('a', cap - 4) : new string('a', cap);
        var atCap = Objection();
        Member(atCap, level)[member] = text;
        var overCap = Objection();
        Member(overCap, level)[member] = text + "a";

        Assert.Empty(Parse(atCap).Problems);
        AssertRejected(Parse(overCap), member);
    }

    [Fact]
    public void At_most_ten_claims()
    {
        var answer = Objection();
        var claims = answer["claims"]!.AsArray();
        while (claims.Count < ContributionLimits.MaxClaims)
        {
            claims.Add(new JsonObject { ["claim"] = "Otra afirmación.", ["support"] = new JsonArray() });
        }

        Assert.Empty(Parse(answer).Problems);
        claims.Add(new JsonObject { ["claim"] = "Una de más.", ["support"] = new JsonArray() });
        AssertRejected(Parse(answer), "claims");
    }

    [Fact]
    public void At_most_eight_citations()
    {
        var answer = Objection();
        var citations = answer["citations"]!.AsArray();
        for (var number = 3; number <= ContributionLimits.MaxCitations; number++)
        {
            citations.Add(Citation("CITA-" + number));
        }

        Assert.Empty(Parse(answer).Problems);
        citations.Add(Citation("CITA-8"));
        AssertRejected(Parse(answer), "citations");

        static JsonObject Citation(string id) => new()
        {
            ["id"] = id,
            ["path"] = "src/Calc/Calc.cs",
            ["startLine"] = 1,
            ["endLine"] = 2,
            ["why"] = "Contexto.",
        };
    }

    [Fact]
    public void An_answer_longer_than_the_cap_is_rejected_before_it_is_read()
    {
        var text = Objection().ToJsonString();
        var huge = text[..^1] + new string(' ', ContributionLimits.MaxAnswerChars) + "}";

        AssertRejected(ContributionParser.Parse(huge, Deliberation()), "characters");
    }

    [Theory]
    [InlineData("root", "summary")]
    [InlineData("root", "argument")]
    [InlineData("claim", "claim")]
    [InlineData("citation", "why")]
    public void A_blank_text_is_rejected(string level, string member)
    {
        var answer = Objection();
        Member(answer, level)[member] = "  \n ";

        AssertRejected(Parse(answer), member);
    }

    // ---- strict reading: nothing unknown, missing, repeated or null ----

    [Theory]
    [InlineData("root", "The contribution has 1 member")]
    [InlineData("claim", "'claims[0]' has 1 member")]
    [InlineData("citation", "'citations[0]' has 1 member")]
    public void An_unknown_member_is_rejected_at_every_level_and_counted_without_its_name(string level, string where)
    {
        var answer = Objection();
        Member(answer, level)["vote"] = "P1";

        var result = Parse(answer);

        AssertRejected(result, where);
        Assert.DoesNotContain(result.Problems, p => p.Contains("vote", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("root", "kind")]
    [InlineData("root", "repliesTo")]
    [InlineData("root", "summary")]
    [InlineData("root", "argument")]
    [InlineData("root", "claims")]
    [InlineData("root", "citations")]
    [InlineData("root", "concedes")]
    [InlineData("root", "finished")]
    [InlineData("claim", "claim")]
    [InlineData("claim", "support")]
    [InlineData("citation", "id")]
    [InlineData("citation", "path")]
    [InlineData("citation", "startLine")]
    [InlineData("citation", "endLine")]
    [InlineData("citation", "why")]
    public void A_missing_member_is_rejected(string level, string member)
    {
        var answer = Objection();
        Assert.True(Member(answer, level).Remove(member));

        AssertRejected(Parse(answer), member);
    }

    [Theory]
    [InlineData("root", "kind")]
    [InlineData("root", "repliesTo")]
    [InlineData("root", "summary")]
    [InlineData("root", "argument")]
    [InlineData("root", "claims")]
    [InlineData("root", "citations")]
    [InlineData("root", "concedes")]
    [InlineData("root", "finished")]
    [InlineData("claim", "claim")]
    [InlineData("claim", "support")]
    [InlineData("citation", "id")]
    [InlineData("citation", "path")]
    [InlineData("citation", "startLine")]
    [InlineData("citation", "why")]
    public void Null_is_rejected_everywhere_because_the_schema_has_no_nullable_member(string level, string member)
    {
        var answer = Objection();
        Member(answer, level)[member] = null;

        AssertRejected(Parse(answer));
    }

    [Theory]
    [InlineData("claims")]
    [InlineData("citations")]
    [InlineData("concedes")]
    [InlineData("support")]
    public void A_null_inside_a_list_is_rejected(string list)
    {
        var answer = Objection();
        var target = list == "support" ? answer["claims"]![0]!["support"]!.AsArray() : answer[list]!.AsArray();
        target.Add(null);

        AssertRejected(Parse(answer), list);
    }

    [Theory]
    [InlineData("root", "finished", "\"false\"")]
    [InlineData("root", "finished", "0")]
    [InlineData("root", "summary", "7")]
    [InlineData("root", "claims", "{}")]
    [InlineData("root", "concedes", "\"C1\"")]
    [InlineData("claim", "support", "\"C2\"")]
    [InlineData("citation", "startLine", "\"10\"")]
    [InlineData("citation", "startLine", "10.5")]
    [InlineData("citation", "endLine", "1e1")]
    [InlineData("citation", "endLine", "99999999999")]
    public void A_value_of_the_wrong_type_is_rejected(string level, string member, string json)
    {
        var answer = Objection();
        Member(answer, level)[member] = JsonNode.Parse(json);

        AssertRejected(Parse(answer));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no es JSON")]
    [InlineData("[]")]
    [InlineData("\"Objection\"")]
    [InlineData("null")]
    public void Text_that_is_not_one_contribution_object_is_rejected(string? text)
    {
        AssertRejected(ContributionParser.Parse(text, Deliberation()));
    }

    [Fact]
    public void Trailing_content_comments_and_trailing_commas_are_rejected()
    {
        var json = Objection().ToJsonString();

        AssertRejected(ContributionParser.Parse(json + " {}", Deliberation()));
        AssertRejected(ContributionParser.Parse("// contribución\n" + json, Deliberation()));
        AssertRejected(ContributionParser.Parse(json[..^1] + ",}", Deliberation()));
    }

    [Fact]
    public void A_member_written_twice_is_rejected()
    {
        var json = Objection().ToJsonString();
        var duplicated = "{\"summary\":\"otro\"," + json[1..];

        AssertRejected(ContributionParser.Parse(duplicated, Deliberation()), "summary");
    }

    [Fact]
    public void Member_names_are_case_sensitive()
    {
        var answer = Objection();
        var summary = answer["summary"]!.GetValue<string>();
        answer.Remove("summary");
        answer["Summary"] = summary;

        AssertRejected(Parse(answer));
    }

    [Fact]
    public void Every_problem_is_reported_not_only_the_first()
    {
        var answer = Objection();
        answer["kind"] = "Proposal";
        answer["repliesTo"] = "C9";
        answer["claims"]![1]!["support"]!.AsArray().Add("EV-404");
        answer["citations"]![0]!["path"] = "../fuera.txt";
        answer["concedes"]!.AsArray().Add("C7");

        var result = Parse(answer);

        AssertRejected(result);
        Assert.Contains(result.Problems, p => p.Contains("'kind'", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Contains("'repliesTo'", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Contains("'claims[1].support[2]'", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Contains("'citations[0].path'", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Contains("'concedes[1]'", StringComparison.Ordinal));
    }

    [Fact]
    public void No_problem_repeats_what_the_participant_wrote()
    {
        // The correction prompt carries the problems and is evidence of a round that is still sealed (D-17): nothing of
        // what the participant wrote may be in it. A marker written everywhere a problem could quote it never comes back.
        const string Marker = "MARCA-PROPUESTA-SECRETA";
        var places = new Action<JsonObject>[]
        {
            a => a["kind"] = Marker,
            a => a["repliesTo"] = Marker,
            a => a["claims"]![0]!["support"]!.AsArray().Add(Marker),
            a => a["concedes"]!.AsArray().Add(Marker),
            a => a["citations"]![1]!["id"] = Marker,
            a => a["citations"]![0]!["path"] = "C:/" + Marker,
            a => a["citations"]![0]!["path"] = "src/" + Marker + "/CON.txt",
            a => a[Marker] = "P1",
            a => a["claims"]![0]![Marker] = Marker,
            a => a["summary"] = 7,
            a => a["finished"] = Marker,
        };

        foreach (var place in places)
        {
            var answer = Objection();
            place(answer);

            var result = Parse(answer);

            AssertRejected(result);
            Assert.DoesNotContain(result.Problems, p => p.Contains(Marker, StringComparison.Ordinal));
        }

        var broken = ContributionParser.Parse("{\"kind\": \"" + Marker + "\" ,,}", Deliberation());
        AssertRejected(broken);
        Assert.DoesNotContain(broken.Problems, p => p.Contains(Marker, StringComparison.Ordinal));
    }
}
