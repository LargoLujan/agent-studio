using AgentStudio.Domain.Providers;
using AgentStudio.Providers.Cli.Claude;

namespace AgentStudio.Providers.Cli;

/// <summary>CLI versions on which Agent Studio's adapters have been tested; others are used but flagged as untested.</summary>
public static class SupportedVersions
{
    private static readonly Dictionary<ProviderId, string[]> Tested = new()
    {
        [ProviderId.ClaudeCode] = ["2.1.290"],
        [ProviderId.CodexCli] = ["0.160.0"],
        [ProviderId.GeminiCli] = ["0.64.0"],
    };

    /// <summary>
    /// Versions on which a real run measured that an executor without a shell, with the permissions Agent Studio gives a
    /// writing executor (<c>MeasuredWriteConfinement.Executor</c>), can neither write nor read outside its working
    /// directory, by an absolute path or through <c>../</c> (ADR-0018, «Medición»; <c>ClaudeWriteConfinementLiveTests</c>),
    /// each with the launch it was measured with. Unlike <see cref="Tested"/>, an unlisted version is not only flagged: it
    /// counts as able to write wherever the user can.
    /// <para>
    /// Claude Code 2.1.290: on 2026-10-17, in <c>acceptEdits</c> with bare file tools, it wrote outside its worktree. On
    /// 2026-10-18, in <c>dontAsk</c> with path rules for the worktree only, the CLI denied the two writes and the three
    /// reads above the worktree and allowed the write and the read inside it. The same day the launch below added
    /// <c>--tools</c> (only the tools of the profile exist) and was measured again: the same result, with exactly Read,
    /// Grep, Glob, Edit, Write and StructuredOutput offered and Monitor and Workflow unavailable. Then the launch below
    /// offered Skill with a skills folder, whose skill headers the materializer rebuilds, and was measured with a skill
    /// whose source grants Write and Bash: invoked first, it granted nothing; the ways out were denied and Bash did not
    /// exist. A version only enters this list after that test passes on it with the profile the engine gives
    /// (<c>MeasuredExecutorProfileTests</c>) and the launch the adapter builds.
    /// </para>
    /// </summary>
    private static readonly Dictionary<ProviderId, WriteConfinementMeasurement[]> WriteConfinementMeasurements = new()
    {
        [ProviderId.ClaudeCode] =
        [
            new(
                "2.1.290",
                """[-p] [--setting-sources] [] [--strict-mcp-config] [--settings] [{"disableAllHooks":true}] [--permission-mode] [dontAsk] [--tools] [Read Grep Glob Edit Write StructuredOutput Skill] [--allowedTools] [Read(<worktree>/**) Read(<skills>/**) Edit(<worktree>/**)] [--disallowedTools] [Bash WebFetch WebSearch NotebookEdit EnterWorktree ExitWorktree Agent Task] [--plugin-dir] [<skills>]"""),
        ],
    };

    /// <summary>
    /// Versions whose answer to the quota read was tested (docs/14 D-15, F4-4). Codex 0.160.0: the parser follows the
    /// response types of its source (<c>rust-v0.160.0</c>), is tested against a synthetic answer of that schema and against
    /// the capture <c>05-rate-limits</c> of the live read (2026-10-18, which also measured that the app-server needs its
    /// input open until it answers). Another version is read with the warning «versión de Codex no probada»; nothing else
    /// changes.
    /// </summary>
    private static readonly Dictionary<ProviderId, string[]> QuotaReads = new()
    {
        [ProviderId.CodexCli] = ["0.160.0"],
    };

    /// <summary>The launch each CLI adapter builds today for the measured executor, as its measurements record it.</summary>
    private static readonly Dictionary<ProviderId, string> CurrentLaunches = new()
    {
        [ProviderId.ClaudeCode] = ClaudeCodeArguments.WriteConfinementLaunch,
    };

    public static IReadOnlyList<string> For(ProviderId provider) =>
        Tested.TryGetValue(provider, out var versions) ? versions : [];

    /// <summary>The versions of <paramref name="provider"/> whose answer to the quota read was tested; none for a CLI without that read.</summary>
    public static IReadOnlyList<string> QuotaReadTested(ProviderId provider) =>
        QuotaReads.TryGetValue(provider, out var versions) ? versions : [];

    /// <summary>
    /// The versions of <paramref name="provider"/> whose confinement to the working directory was measured with the launch
    /// its adapter builds today; none for a CLI never measured, and none once the launch changed since it was measured.
    /// </summary>
    public static IReadOnlyList<string> WriteConfinementMeasured(ProviderId provider) =>
        WriteConfinementMeasured(provider, CurrentLaunches.GetValueOrDefault(provider));

    /// <summary>The versions of <paramref name="provider"/> whose confinement was measured with exactly <paramref name="launch"/>; none for a null launch.</summary>
    public static IReadOnlyList<string> WriteConfinementMeasured(ProviderId provider, string? launch) =>
        launch is not null && WriteConfinementMeasurements.TryGetValue(provider, out var measurements)
            ? [.. measurements.Where(m => string.Equals(m.Launch, launch, StringComparison.Ordinal)).Select(m => m.Version)]
            : [];

    /// <summary>Null when the version is tested or the provider has no list; otherwise the Spanish note to show.</summary>
    public static string? UntestedNote(ProviderId provider, string version)
    {
        var tested = For(provider);
        if (tested.Count == 0 || tested.Contains(version, StringComparer.Ordinal))
        {
            return null;
        }

        return $"Versión no probada con Agent Studio (probadas: {string.Join(", ", tested)}).";
    }

    /// <summary>A version whose confinement was measured, with the launch it was measured with (for Claude Code, <see cref="ClaudeCodeArguments.WriteConfinementLaunch"/>).</summary>
    private sealed record WriteConfinementMeasurement(string Version, string Launch);
}
