namespace AgentStudio.Domain.Skills;

/// <summary>What the user (or an agent) asks for: free text, context tags (e.g. from the repo) and/or a sector.</summary>
public sealed record SkillQuery(string? Text, IReadOnlySet<string>? ContextTags, SkillSector? Sector);

/// <summary>A recommended skill with its score and the human-readable reasons (Spanish).</summary>
public sealed record SkillRecommendation(SkillEntry Skill, double Score, IReadOnlyList<string> Reasons);

/// <summary>
/// Deterministic, explainable ranking. Points: tag matched by context or by the text (words, or 2-3 consecutive
/// words joined with '-', so "power bi" hits #power-bi) 3 each; same sector 2; term in the name 2; term in the
/// summary 1. Entries scoring 0 are never returned; ties are ordered by name (ordinal, case-insensitive), then id.
/// </summary>
public static class SkillRecommender
{
    private const double TagPoints = 3;
    private const double SectorPoints = 2;
    private const double NamePoints = 2;
    private const double SummaryPoints = 1;

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "from", "con", "para", "por", "una", "uno", "los", "las", "del", "que", "como",
        "this", "that", "use", "usar",
    };

    public static IReadOnlyList<SkillRecommendation> Recommend(SkillQuery query, IEnumerable<SkillEntry> catalog, int top = 5)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(catalog);
        if (top <= 0)
        {
            return [];
        }

        var words = SkillText.Words(query.Text);
        var terms = words.Where(w => w.Length >= 3 && !Stopwords.Contains(w)).Distinct(StringComparer.Ordinal).ToList();
        var textTags = new HashSet<string>(words, StringComparer.Ordinal);
        for (var n = 2; n <= 3; n++)
        {
            for (var i = 0; i + n <= words.Length; i++)
            {
                textTags.Add(string.Join('-', words.Skip(i).Take(n)));
            }
        }

        // Invalid or null context tags are discarded rather than failing the whole recommendation.
        var contextTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in query.ContextTags ?? new HashSet<string>())
        {
            if (SkillTags.TryNormalize(raw, out var tag))
            {
                contextTags.Add(tag);
            }
        }

        var results = new List<SkillRecommendation>();
        foreach (var skill in catalog)
        {
            var score = 0.0;
            var reasons = new List<string>();

            foreach (var tag in skill.Tags.OrderBy(t => t, StringComparer.Ordinal))
            {
                if (contextTags.Contains(tag) || textTags.Contains(tag))
                {
                    score += TagPoints;
                    reasons.Add($"coincide con #{tag}");
                }
            }

            if (query.Sector is not null && query.Sector.Id == skill.Sector.Id)
            {
                score += SectorPoints;
                reasons.Add($"pertenece al sector {skill.Sector.Title}");
            }

            var nameWords = SkillText.Words(skill.Name);
            var summaryWords = SkillText.Words(skill.Summary);
            foreach (var term in terms)
            {
                if (nameWords.Contains(term, StringComparer.Ordinal))
                {
                    score += NamePoints;
                    reasons.Add($"«{term}» aparece en el nombre");
                }

                if (summaryWords.Contains(term, StringComparer.Ordinal))
                {
                    score += SummaryPoints;
                    reasons.Add($"«{term}» aparece en el resumen");
                }
            }

            if (score > 0)
            {
                results.Add(new SkillRecommendation(skill, score, reasons));
            }
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Skill.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Skill.Id.Value, StringComparer.Ordinal)
            .Take(top)
            .ToList();
    }
}
