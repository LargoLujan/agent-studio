namespace AgentStudio.Domain.Skills;

/// <summary>
/// Derives tags from a skill's name, summary and source domain with a dictionary of technologies. Matching is
/// done on whole words/phrases of the accent-free lower-case text ("Dockerfile" does not match "docker").
/// The sector id (and the source domain id, when given) are always included.
/// </summary>
public static class SkillTagger
{
    // tag -> phrases (already in normalized form: lower-case words separated by single spaces).
    private static readonly (string Tag, string[] Phrases)[] Dictionary =
    [
        ("spark", ["spark", "apache spark"]),
        ("pyspark", ["pyspark"]),
        ("sql", ["sql"]),
        ("t-sql", ["t sql", "tsql", "transact sql"]),
        ("dax", ["dax"]),
        ("power-bi", ["power bi", "powerbi"]),
        ("tmdl", ["tmdl"]),
        ("fabric", ["fabric", "microsoft fabric"]),
        ("delta", ["delta", "delta lake"]),
        ("dbt", ["dbt"]),
        ("airflow", ["airflow"]),
        ("pandas", ["pandas"]),
        ("polars", ["polars"]),
        ("scikit-learn", ["scikit learn", "sklearn"]),
        ("statistics", ["statistics", "statistical", "estadistica", "estadistico"]),
        ("duckdb", ["duckdb"]),
        ("kafka", ["kafka"]),
        ("databricks", ["databricks"]),
        ("lakehouse", ["lakehouse"]),
        ("medallion", ["medallion"]),
        ("python", ["python"]),
        ("typescript", ["typescript"]),
        ("react", ["react"]),
        ("react-native", ["react native"]),
        ("nextjs", ["nextjs", "next js"]),
        ("vue", ["vue", "vuejs", "vue js"]),
        ("svelte", ["svelte", "sveltekit"]),
        ("tailwind", ["tailwind", "tailwindcss"]),
        ("wpf", ["wpf"]),
        ("winui", ["winui"]),
        ("maui", ["maui"]),
        ("dotnet", ["dotnet", "net maui", "csharp"]),
        ("tauri", ["tauri"]),
        ("electron", ["electron"]),
        ("flutter", ["flutter"]),
        ("swift", ["swift", "swiftui"]),
        ("kotlin", ["kotlin"]),
        ("android", ["android"]),
        ("qt", ["qt"]),
        ("playwright", ["playwright"]),
        ("tdd", ["tdd", "test driven"]),
        ("debugging", ["debugging", "debug", "diagnosing"]),
        ("git", ["git"]),
        ("mcp", ["mcp"]),
        ("rag", ["rag"]),
        ("evals", ["eval", "evals", "evaluation", "evaluations"]),
        ("llm", ["llm", "llms"]),
        ("prompt", ["prompt", "prompts"]),
        ("langchain", ["langchain"]),
        ("mlflow", ["mlflow"]),
        ("azure", ["azure"]),
        ("terraform", ["terraform"]),
        ("docker", ["docker"]),
        ("kubernetes", ["kubernetes", "k8s"]),
        ("ci-cd", ["ci cd", "cicd", "continuous integration"]),
        ("observability", ["observability", "observabilidad"]),
        ("security", ["security", "seguridad"]),
        ("owasp", ["owasp"]),
        ("threat-model", ["threat model", "threat modeling", "threat modelling"]),
        ("accessibility", ["accessibility", "a11y", "wcag", "accesibilidad"]),
        ("design-system", ["design system", "design systems"]),
        ("ux-writing", ["ux writing"]),
        ("figma", ["figma"]),
        ("fastapi", ["fastapi"]),
        ("node", ["node", "nodejs", "node js", "fastify"]),
        ("api", ["api", "apis", "rest"]),
    ];

    public static IReadOnlySet<string> Derive(string name, string summary, SkillSector sector, string? domain)
    {
        ArgumentNullException.ThrowIfNull(sector);
        var text = " " + SkillText.Normalize(name + " " + summary) + " ";
        var tags = new HashSet<string>(StringComparer.Ordinal) { sector.Id };

        foreach (var (tag, phrases) in Dictionary)
        {
            if (phrases.Any(p => text.Contains(" " + p + " ", StringComparison.Ordinal)))
            {
                tags.Add(tag);
            }
        }

        if (SkillTags.TryNormalize(domain, out var domainTag))
        {
            tags.Add(domainTag);
        }

        return tags;
    }
}
