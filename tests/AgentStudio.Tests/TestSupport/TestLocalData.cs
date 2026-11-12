using System.IO;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Providers.Cli;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.TestSupport;

/// <summary>
/// The folders of the user in <c>%LOCALAPPDATA%\AgentStudio</c> that the composition and the adapters would write, moved
/// into a temporary folder of the test. A test that composes the app (<c>AddAgentStudioApp</c>) or starts an adapter must
/// use it: otherwise it writes the real folders of the person (Windows denies that at Low integrity, which is how the audit
/// of the repository runs the suite) and leaves its files there (docs/08, F5-5, ADR-0017 addendum).
/// </summary>
internal static class TestLocalData
{
    private static readonly Lazy<string> SharedRoot = new(CreateSharedRoot);

    /// <summary>
    /// A temporary folder shared by every test of the process that does not need a folder of its own, deleted when the
    /// process ends. A test that looks at the contents of a folder uses its own (<c>TempDirectory</c>) instead.
    /// </summary>
    public static string Shared => SharedRoot.Value;

    /// <summary>The per-run folders of the adapters (<see cref="RunTempFolderOptions"/>) in the shared folder.</summary>
    public static RunTempFolderOptions RunFolders => new(Path.Combine(Shared, "runs"));

    /// <summary>The anchors of the decision logs (<see cref="DecisionLogAnchorOptions"/>) in <paramref name="root"/>.</summary>
    public static DecisionLogAnchorOptions Anchors(string root) => new(Path.Combine(root, "anchors"));

    /// <summary>
    /// Registers, before <c>AddAgentStudioApp</c> (the composition only adds what is not registered), every folder of the
    /// composition that lives in the real <c>%LOCALAPPDATA%</c>, inside <paramref name="root"/>: the anchors of the decision
    /// logs and the per-run folders of the adapters. Returns the collection to chain.
    /// </summary>
    public static IServiceCollection AddTestLocalData(this IServiceCollection services, string root)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(Anchors(root));
        services.AddSingleton(new RunTempFolderOptions(Path.Combine(root, "runs")));
        return services;
    }

    private static string CreateSharedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentstudio-localdata-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: it is a temporary folder.
            }
        };
        return root;
    }
}
