using System.Collections.Concurrent;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Providers.Cli.Codex;
using AgentStudio.Providers.Cli.Gemini;

namespace AgentStudio.Providers.Composition;

/// <summary>
/// Builds the Claude Code, Codex CLI and Gemini CLI providers of a project through <see cref="AgentProviderPipeline"/>,
/// once per project root. The adapters are created here and nowhere else, so none is reachable without the journal.
/// Concurrency limits therefore apply per project; the IDE has one project open at a time.
/// </summary>
/// <param name="quota">
/// The user's record of quota observations (docs/14 D-14), shared by every project: the quota belongs to the account. When
/// the composition registers none, the providers are composed without it.
/// </param>
public sealed class ProjectAgentProviders(
    IExecutableLocator locator,
    IStreamingProcessRunner runner,
    IPromptJournalFactory journals,
    TimeProvider time,
    IQuotaObservations? quota = null) : IProjectAgentProviders
{
    private readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<IAgentProvider>>> _byProject =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IAgentProvider> For(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        return _byProject.GetOrAdd(root, key => new Lazy<IReadOnlyList<IAgentProvider>>(() => Create(key))).Value;
    }

    private IReadOnlyList<IAgentProvider> Create(string root)
    {
        var journal = journals.ForProject(root);
        IAgentProvider[] adapters =
        [
            new ClaudeCodeAgentProvider(locator, runner, time),
            new CodexCliAgentProvider(locator, runner, time),
            new GeminiCliAgentProvider(locator, runner),
        ];
        return [.. adapters.Select(adapter => AgentProviderPipeline.Compose(adapter, journal, time, quota: quota))];
    }
}
