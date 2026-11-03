using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Tests.Providers.Codex;

internal static class CodexTestSupport
{
    public const string FixtureVersion = "0.160.0";

    /// <summary>The folder of the synthetic captures of the ends of an attempt (Phase 8, D-41), next to the real ones.</summary>
    public const string LimitsFolder = "limits";

    private static string FixtureFolder => Path.Combine(AppContext.BaseDirectory, "Providers", "Fixtures", "codex-cli", FixtureVersion);

    public static string[] FixtureLines(string name) =>
        File.ReadAllLines(Path.Combine(FixtureFolder, name))
            .Where(l => l.Length > 0)
            .ToArray();

    /// <summary>The lines of a synthetic capture of <see cref="LimitsFolder"/> (see its README: none comes from a real run).</summary>
    public static string[] LimitsLines(string name) => FixtureLines(Path.Combine(LimitsFolder, name));

    /// <summary>
    /// The text of every capture of this version, real or synthetic, never a README: each one with the spaces between a key
    /// and its value removed, because some real captures write <c>"key": value</c>.
    /// </summary>
    public static IReadOnlyList<(string Name, string Text)> CaptureTexts() =>
    [
        .. Directory.EnumerateFiles(FixtureFolder, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".jsonl" or ".json" or ".stderr")
            .Select(path => (Path.GetRelativePath(FixtureFolder, path), Regex.Replace(File.ReadAllText(path), "\"\\s*:\\s*", "\":"))),
    ];

    public static PermissionProfile Permissions(bool writes = false, bool network = false, params string[] extraDirectories) =>
        new(writes, AllowShell: true, network, [], [], extraDirectories);

    public static AgentRunRequest Request(
        PermissionProfile? permissions = null,
        string workingDirectory = "C:/work",
        string prompt = "Haz esto",
        string? schema = null,
        string? resume = null,
        string? model = "gpt-x",
        string instructions = "",
        TimeSpan? maxDuration = null) =>
        new(
            new AgentDefinition(
                new AgentId("Exec-1"),
                "Ejecutor",
                AgentRole.Executor,
                new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, model),
                instructions,
                [],
                [],
                [],
                new ExecutionBudget(10, maxDuration ?? TimeSpan.FromMinutes(5)),
                3,
                MemoryPolicy.None,
                new ReviewPolicy(false, false)),
            prompt,
            workingDirectory,
            permissions ?? Permissions(),
            schema,
            resume);

    public static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "agentstudio-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
