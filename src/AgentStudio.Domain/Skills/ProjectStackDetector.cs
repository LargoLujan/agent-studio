namespace AgentStudio.Domain.Skills;

/// <summary>
/// The stack of a project as context tags for the deterministic recommender (docs/14 D-28), from the names of its files
/// only: the paths the explorer indexed, never their content, so nothing a repository holds can steer it beyond a tag.
/// Only the last segment of each path counts, compared without case:
/// <list type="bullet">
/// <item><c>*.csproj</c>, <c>*.sln</c> and <c>*.slnx</c> give <c>dotnet</c> and <c>csharp</c>;</item>
/// <item><c>package.json</c> gives <c>node</c>;</item>
/// <item><c>pyproject.toml</c> and <c>requirements.txt</c> give <c>python</c>;</item>
/// <item><c>*.sql</c> gives <c>sql</c>;</item>
/// <item><c>Dockerfile</c> gives <c>docker</c>;</item>
/// <item><c>*.tf</c> gives <c>terraform</c>;</item>
/// <item><c>*.pbip</c> and <c>*.tmdl</c> give <c>power-bi</c>.</item>
/// </list>
/// The tags are normalized (<see cref="SkillTags"/>) and come out in ordinal order, each once.
/// </summary>
public static class ProjectStackDetector
{
    private static readonly (string[] Extensions, string[] Names, string[] Tags)[] Rules =
    [
        ([".csproj", ".sln", ".slnx"], [], ["dotnet", "csharp"]),
        ([], ["package.json"], ["node"]),
        ([], ["pyproject.toml", "requirements.txt"], ["python"]),
        ([".sql"], [], ["sql"]),
        ([], ["Dockerfile"], ["docker"]),
        ([".tf"], [], ["terraform"]),
        ([".pbip", ".tmdl"], [], ["power-bi"]),
    ];

    /// <param name="relativePaths">File paths relative to the project root, with <c>/</c> or <c>\</c>; blank ones are skipped.</param>
    public static IReadOnlySet<string> Detect(IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        var tags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in relativePaths)
        {
            var name = FileName(path);
            if (name.Length == 0)
            {
                continue;
            }

            foreach (var (extensions, names, ruleTags) in Rules)
            {
                if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    || extensions.Any(e => name.Length > e.Length && name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                {
                    tags.UnionWith(ruleTags);
                }
            }
        }

        return tags;
    }

    private static string FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var separator = path.AsSpan().LastIndexOfAny('/', '\\');
        return path[(separator + 1)..].Trim();
    }
}
