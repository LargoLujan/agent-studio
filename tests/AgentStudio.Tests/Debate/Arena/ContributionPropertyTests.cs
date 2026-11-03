using System.Globalization;
using System.Text.Json.Nodes;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using static AgentStudio.Tests.Debate.Arena.ContributionSamples;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>Seeded properties of the contribution (docs/15 section 8: <see cref="Random"/> with a seed, no FsCheck).</summary>
public sealed class ContributionPropertyTests
{
    private const int Cases = 200;

    private static readonly string[] Members = ["kind", "repliesTo", "summary", "argument", "claims", "citations", "concedes", "finished"];

    [Fact]
    public void Any_valid_contribution_reads_back_from_its_canonical_form_which_is_stable()
    {
        var random = new Random(20261019);
        for (var i = 0; i < Cases; i++)
        {
            var rules = RandomRules(random);
            var answer = RandomAnswer(random, rules);
            var json = ContributionParser.ToJson(answer);

            var result = ContributionParser.Parse(json, rules);

            Assert.True(result.Problems.Count == 0, $"Caso {i}: {string.Join(" | ", result.Problems)}");
            Assert.Equal(json, ContributionParser.ToJson(result.Answer!));
            Assert.Equal(answer.RepliesTo, result.Answer!.RepliesTo);
            Assert.Equal(answer.Finished, result.Answer.Finished);
        }
    }

    [Fact]
    public void Any_valid_contribution_with_a_member_missing_or_one_more_is_rejected()
    {
        var random = new Random(20261020);
        for (var i = 0; i < Cases; i++)
        {
            var rules = RandomRules(random);
            var answer = RandomAnswer(random, rules);
            var missing = JsonNode.Parse(ContributionParser.ToJson(answer))!.AsObject();
            var removed = Members[random.Next(Members.Length)];
            Assert.True(missing.Remove(removed));
            var extra = JsonNode.Parse(ContributionParser.ToJson(answer))!.AsObject();
            extra["vote"] = "P" + random.Next(1, 4);

            var withoutMember = ContributionParser.Parse(missing.ToJsonString(), rules);
            var withExtraMember = ContributionParser.Parse(extra.ToJsonString(), rules);

            Assert.Null(withoutMember.Answer);
            Assert.Contains(withoutMember.Problems, p => p.Contains(removed, StringComparison.Ordinal));
            Assert.Null(withExtraMember.Answer);
            Assert.Contains(withExtraMember.Problems, p => p.StartsWith("The contribution has 1 member the schema does not have", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_longest_contribution_the_caps_allow_fits_the_answer_cap_even_with_every_character_escaped()
    {
        // 40 published contributions and 60 evidences with the 32-character ids of the real store, every text at its cap
        // and made of a character the encoder always escapes, into six characters ('<'), every claim citing everything.
        var evidence = Enumerable.Range(0, 60).Select(n => new EvidenceId(n.ToString("x32", CultureInfo.InvariantCulture))).ToList();
        var rules = ContributionRules.For(DebateMode.Free, round: 4, published: 40, evidence);
        var citations = ContributionLabels.Citations
            .Select((id, n) => new ContributionCitation(id, "src/" + new string('a', ContributionLimits.MaxPathChars - 4), (n * 100) + 1, (n * 100) + ContributionLimits.MaxCitedLines, new string('<', ContributionLimits.MaxWhyChars)))
            .ToList();
        List<string> everything = [.. rules.Contributions, .. evidence.Select(e => e.Value), .. citations.Select(c => c.Id)];
        var answer = new ContributionAnswer(
            ContributionKind.Rebuttal,
            "C40",
            new string('<', ContributionLimits.MaxSummaryChars),
            new string('<', ContributionLimits.MaxArgumentChars),
            [.. Enumerable.Range(0, ContributionLimits.MaxClaims).Select(_ => new ContributionClaim(new string('<', ContributionLimits.MaxClaimChars), everything))],
            citations,
            rules.Contributions,
            Finished: false);

        var json = ContributionParser.ToJson(answer);

        Assert.True(json.Length <= ContributionLimits.MaxAnswerChars, $"{json.Length} characters");
        Assert.Empty(ContributionParser.Parse(json, rules).Problems);
    }
}
