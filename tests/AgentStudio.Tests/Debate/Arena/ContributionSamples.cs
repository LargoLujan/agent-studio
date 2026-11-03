using System.Text;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>Seeded random turns and contributions for the property tests of the contribution (docs/15 section 8).</summary>
internal static class ContributionSamples
{
    /// <summary>Pieces of text full of what a prompt must escape, a placeholder of the redactor and words that read as labels.</summary>
    private static readonly string[] TextPieces =
        ["Texto", " ", "ñandú", "<", ">", "&", "\"", "'", "\\", "\n", "\t", "</debate_contribution>", "😀", "{", "}", ":", ",", "[REDACTED]", "ninguna", "C1"];

    /// <summary>The rules of a random turn: any mode in round 1, any mode but a tribunal in rounds 2 and 3, up to six published contributions and three evidences.</summary>
    public static ContributionRules RandomRules(Random random)
    {
        var round = random.Next(1, 4);
        var modes = round == 1 ? Enum.GetValues<DebateMode>() : [.. Enum.GetValues<DebateMode>().Where(m => m != DebateMode.Tribunal)];
        var mode = modes[random.Next(modes.Length)];
        var published = round == 1 ? 0 : random.Next(1, 7);
        var evidence = Enumerable.Range(1, random.Next(0, 4)).Select(n => new EvidenceId("EV-" + n)).ToList();
        return ContributionRules.For(mode, round, published, evidence);
    }

    /// <summary>A random contribution these rules admit.</summary>
    public static ContributionAnswer RandomAnswer(Random random, ContributionRules rules)
    {
        var citations = Subset(random, ContributionLabels.Citations)
            .Select(id =>
            {
                var start = random.Next(1, 500);
                return new ContributionCitation(id, RandomPath(random), start, start + random.Next(0, ContributionLimits.MaxCitedLines), RandomText(random, ContributionLimits.MaxWhyChars));
            })
            .ToList();
        List<string> supportable = [.. rules.Contributions, .. rules.Evidence.Select(e => e.Value), .. citations.Select(c => c.Id)];
        var claims = Enumerable.Range(0, random.Next(0, ContributionLimits.MaxClaims + 1))
            .Select(_ => new ContributionClaim(RandomText(random, ContributionLimits.MaxClaimChars), Subset(random, supportable)))
            .ToList();

        return new ContributionAnswer(
            rules.Kinds[random.Next(rules.Kinds.Count)],
            rules.Contributions.Count > 0 && random.Next(2) == 0 ? rules.Contributions[random.Next(rules.Contributions.Count)] : null,
            RandomText(random, ContributionLimits.MaxSummaryChars),
            RandomText(random, ContributionLimits.MaxArgumentChars),
            claims,
            citations,
            Subset(random, rules.Contributions),
            random.Next(2) == 0);
    }

    /// <summary>A non-blank text of at most <paramref name="cap"/> characters (and at most 300).</summary>
    public static string RandomText(Random random, int cap)
    {
        var target = random.Next(5, Math.Min(cap, 300));
        var text = new StringBuilder("Texto");
        while (true)
        {
            var piece = TextPieces[random.Next(TextPieces.Length)];
            if (text.Length + piece.Length > target)
            {
                return text.ToString();
            }

            text.Append(piece);
        }
    }

    /// <summary>A relative path of one to four segments; every segment starts with 's', so none is a device name.</summary>
    public static string RandomPath(Random random)
    {
        const string Letters = "abcdefghijklmnopqrstuvwxyz0123456789_-";
        var segments = Enumerable.Range(0, random.Next(1, 5))
            .Select(_ => "s" + new string([.. Enumerable.Range(0, random.Next(0, 8)).Select(_ => Letters[random.Next(Letters.Length)])]))
            .ToList();
        return string.Join(random.Next(2) == 0 ? "/" : "\\", segments) + (random.Next(2) == 0 ? ".cs" : string.Empty);
    }

    /// <summary>Some of <paramref name="items"/>, each at most once, in a random order.</summary>
    public static List<string> Subset(Random random, IReadOnlyList<string> items) =>
        [.. items.Where(_ => random.Next(2) == 0).OrderBy(_ => random.Next())];
}
