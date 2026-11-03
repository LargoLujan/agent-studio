using System.IO;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Skills;

namespace AgentStudio.Tests.Providers.Live;

/// <summary>
/// A throw-away skill with a unique name and two unique secrets, written in a temporary catalog folder.
/// <list type="bullet">
/// <item><see cref="Codeword"/> is in the frontmatter <c>description</c>: descriptions are what the CLIs list at startup, so
/// an agent can answer from its skill list alone, without invoking anything.</item>
/// <item><see cref="BodyCodeword"/> is only in the body: the agent can only know it by invoking (loading) the skill.</item>
/// </list>
/// </summary>
internal sealed class ProbeSkill
{
    private static readonly string[] Words =
    [
        "amber", "basalt", "cobalt", "dune", "ember", "fjord", "garnet", "harbor",
        "indigo", "juniper", "kelp", "lagoon", "marble", "nectar", "onyx", "pebble",
    ];

    private ProbeSkill(string name, string codeword, string bodyCodeword, ActivatedSkill activated)
    {
        Name = name;
        Codeword = codeword;
        BodyCodeword = bodyCodeword;
        Activated = activated;
    }

    /// <summary>Unique skill name: <c>agentstudio-probe-skill-&lt;8 hex&gt;</c>, valid for the materializer.</summary>
    public string Name { get; }

    /// <summary>Unique secret in the skill's description (visible in the skill list). Never in a prompt or an argument.</summary>
    public string Codeword { get; }

    /// <summary>Unique secret only in the skill's body (visible only once the skill is invoked).</summary>
    public string BodyCodeword { get; }

    public ActivatedSkill Activated { get; }

    /// <summary>Creates <c>&lt;catalogRoot&gt;/&lt;name&gt;/SKILL.md</c> and the matching <see cref="ActivatedSkill"/>.</summary>
    public static ProbeSkill Create(string catalogRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogRoot);
        var name = "agentstudio-probe-skill-" + Guid.NewGuid().ToString("N")[..8];
        var codeword = NewCodeword();
        var bodyCodeword = NewCodeword();

        var folder = Path.Combine(catalogRoot, name);
        Directory.CreateDirectory(folder);
        var content = $"""
            ---
            name: {name}
            description: Agent Studio live-test probe. Its listed codeword is {codeword}. Invoke the skill to learn its body codeword.
            ---

            # Agent Studio probe skill

            This skill exists only to check that Agent Studio puts skills where the CLI can see and use them.
            The body codeword is {bodyCodeword}.
            """;

        // LF whatever the line endings of this source file are: the CLIs parse the frontmatter.
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), content.ReplaceLineEndings("\n") + "\n");

        var entry = new SkillEntry(
            new SkillId(name),
            name,
            "Live-test probe",
            SkillSectors.IngenieriaSoftware,
            ["probe"],
            new SkillSource("local/agentstudio-probe", name, null, null, null, SkillDistribution.Vendored));
        return new ProbeSkill(name, codeword, bodyCodeword, new ActivatedSkill(entry, folder));
    }

    // Two words and four digits, upper case: unique per run and easy for a model to copy exactly.
    private static string NewCodeword() => $"{Pick()}-{Pick()}-{Random.Shared.Next(1000, 10000)}".ToUpperInvariant();

    private static string Pick() => Words[Random.Shared.Next(Words.Length)];
}
