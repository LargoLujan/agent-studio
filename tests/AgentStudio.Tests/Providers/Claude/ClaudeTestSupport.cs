using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Tests.Providers.Claude;

internal static class ClaudeTestSupport
{
    public const string FixtureVersion = "2.1.290";

    /// <summary>The folder of the synthetic captures of the ends of an attempt (Phase 8, D-41), next to the real ones.</summary>
    public const string LimitsFolder = "limits";

    public static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Providers", "Fixtures", "claude-code", FixtureVersion, name);

    public static string[] FixtureLines(string name) => File.ReadAllLines(FixturePath(name));

    /// <summary>The lines of a synthetic capture of <see cref="LimitsFolder"/> (see its README: none comes from a real run).</summary>
    public static string[] LimitsLines(string name) => FixtureLines(Path.Combine(LimitsFolder, name));

    /// <summary>
    /// The text of every capture of this version, real or synthetic, never a README: each one with the spaces between a key
    /// and its value removed, because some real captures write <c>"key": value</c>.
    /// </summary>
    public static IReadOnlyList<(string Name, string Text)> CaptureTexts() =>
    [
        .. Directory.EnumerateFiles(FixturePath(string.Empty), "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".jsonl" or ".json" or ".stderr")
            .Select(path => (Path.GetRelativePath(FixturePath(string.Empty), path), Regex.Replace(File.ReadAllText(path), "\"\\s*:\\s*", "\":"))),
    ];

    public static PermissionProfile Permissions(
        bool writes = false,
        bool shell = false,
        bool network = false,
        string[]? allowed = null,
        string[]? denied = null,
        string[]? directories = null) =>
        new(writes, shell, network, allowed ?? [], denied ?? [], directories ?? []);

    public static AgentRunRequest Request(
        PermissionProfile? permissions = null,
        string prompt = "Responde exactamente OK",
        string? model = "haiku",
        string instructions = "",
        ExecutionBudget? budget = null,
        string? schema = null,
        string? resume = null,
        string? skills = null,
        string workingDirectory = "C:/work") =>
        new(
            new AgentDefinition(
                new AgentId("Exec-1"),
                "Ejecutor",
                AgentRole.Executor,
                new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, model),
                instructions,
                [],
                [],
                [],
                budget ?? new ExecutionBudget(4, TimeSpan.FromMinutes(5)),
                3,
                MemoryPolicy.None,
                new ReviewPolicy(false, false)),
            prompt,
            workingDirectory,
            permissions ?? Permissions(),
            OutputJsonSchema: schema,
            ResumeSessionId: resume,
            SkillsDirectory: skills);
}
