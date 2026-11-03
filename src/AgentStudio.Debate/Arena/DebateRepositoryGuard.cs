using System.Globalization;
using System.Security.Cryptography;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Decisions;

namespace AgentStudio.Debate.Arena;

/// <summary>
/// The check of the user's repository around each round and each judgment of a debate (docs/15 §7, ADR-0013 decision 7): its
/// state (HEAD, branches, tags, stash, index, status and the content of what the status lists) is captured once, before the
/// first turn, and every later capture is compared with that baseline, never with the previous one. Any difference stops the
/// debate with an alert: no participant writes, so a difference means something escaped its sandbox (or someone touched the
/// repository meanwhile).
/// </summary>
/// <remarks>
/// <para>It follows the check of the engine (<c>UserRepositoryGuard</c>, which ADR-0002 keeps out of this module):</para>
/// <list type="bullet">
/// <item>What Agent Studio itself writes under <c>.agentstudio/</c> while the debate goes on is left out, and only those exact
/// paths: the evidence and temporary folders, the database files, the canvas layout, the verification profile and the days
/// of the prompt journal. The journal is only ever appended to, so its content is checked instead of its status line: a day
/// that no longer starts with what it held, or is gone, is a difference.</item>
/// <item>What the capture could not verify blocks, with one exception (ADR-0012): an ignored folder whose metadata did not
/// change. Only a process at medium integrity can swap a file there for one of the same length and restore its timestamp,
/// and no participant of a debate writes at all, so such a folder is recorded once with an alert instead of blocking.</item>
/// </list>
/// </remarks>
public sealed class DebateRepositoryGuard(IWorktreeManager worktrees, string repositoryRoot, DebateTurnRecorder recorder)
{
    /// <summary>The code of the alert of a difference found around a round or a judgment.</summary>
    public const string ChangedAlert = "user-repository.changed";

    /// <summary>The code of the alert that records the ignored folders a check let through without verifying them.</summary>
    public const string UnverifiedAlert = "user-repository.capture-indeterminate";

    // The names are the ones Agent Studio gives its own files (AgentStudioFiles); which may change around a round, the policy here.
    private const string AgentStudioFolder = AgentStudioFiles.DataFolder + "/";
    private const string JournalFolder = AgentStudioFiles.JournalFolder + "/";
    private const string IgnoredPrefix = "!! ";

    private readonly Lock _journalGate = new();
    private readonly Dictionary<string, (int Length, string Sha256)> _journal = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _alertGate = new(1, 1);
    private readonly HashSet<string> _alerted = new(StringComparer.Ordinal);
    private UserRepositoryState? _baseline;

    /// <summary>Captures the state every later check is compared with. Only the first call captures.</summary>
    public async Task EstablishBaselineAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _baseline) is not null)
        {
            return;
        }

        TakeJournal();
        var state = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.CompareExchange(ref _baseline, state, null);
    }

    /// <summary>
    /// How the user's repository differs from the baseline, with what the capture could not verify; empty when it is as it was.
    /// The ignored folders let through unverified are recorded before this returns.
    /// </summary>
    /// <exception cref="InvalidOperationException">No baseline was established.</exception>
    public async Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken)
    {
        var baseline = Volatile.Read(ref _baseline)
            ?? throw new InvalidOperationException("The user's repository has no baseline to be checked against.");
        var now = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        var (differences, indeterminate) = UserRepositoryState.CompareInDetail(baseline, now);
        IReadOnlyList<UnverifiedPath> blocking = indeterminate;
        if (indeterminate.Count > 0)
        {
            var folders = IgnoredFolders(baseline, now);
            var letThrough = indeterminate.Where(u => !u.IsGitFile && folders.Contains(u.Path)).ToList();
            if (letThrough.Count > 0)
            {
                await RecordUnverifiedAsync(letThrough, cancellationToken).ConfigureAwait(false);
                blocking = [.. indeterminate.Except(letThrough)];
            }
        }

        return [.. differences, .. blocking.Select(u => u.Description), .. JournalDifferences()];
    }

    /// <summary>The alert of a difference, which stops the debate.</summary>
    public Task<DecisionEntry> RecordChangedAsync(string when, IReadOnlyList<string> differences, CancellationToken cancellationToken) =>
        recorder.RecordAsync(
            DecisionKind.AuditAlertRaised,
            "El repositorio del usuario cambió durante el debate",
            new AuditAlertPayload(ChangedAlert, [], $"The user's repository changed {when}: " + string.Join(" ", differences)) { RunId = recorder.Debate.Value },
            cancellationToken);

    /// <summary>The ignored folders of two captures: the paths both list with <c>!! path/</c> and no other line.</summary>
    private static HashSet<string> IgnoredFolders(UserRepositoryState before, UserRepositoryState after)
    {
        var kept = before.PorcelainStatus.Intersect(after.PorcelainStatus, StringComparer.Ordinal).ToList();
        var named = kept.SelectMany(StatusPaths).CountBy(path => path, StringComparer.Ordinal).ToDictionary(StringComparer.Ordinal);
        return kept
            .Where(l => l.StartsWith(IgnoredPrefix, StringComparison.Ordinal) && l.EndsWith('/'))
            .Select(l => l[IgnoredPrefix.Length..])
            .Where(path => named.GetValueOrDefault(path) == 1)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<string> StatusPaths(string line)
    {
        if (line.Length < 4)
        {
            yield break;
        }

        var paths = line[3..];
        var arrow = paths.IndexOf(" -> ", StringComparison.Ordinal);
        if (line[..2].IndexOfAny(['R', 'C']) >= 0 && arrow > 0)
        {
            yield return paths[..arrow];
            yield return paths[(arrow + 4)..];
        }
        else
        {
            yield return paths;
        }
    }

    private async Task RecordUnverifiedAsync(IReadOnlyList<UnverifiedPath> unverified, CancellationToken cancellationToken)
    {
        await _alertGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fresh = unverified.Where(u => !_alerted.Contains(u.Path)).ToList();
            if (fresh.Count == 0)
            {
                return;
            }

            await recorder.RecordAsync(
                DecisionKind.AuditAlertRaised,
                "Carpetas ignoradas del repositorio del usuario sin verificar",
                new AuditAlertPayload(
                    UnverifiedAlert,
                    [],
                    string.Create(CultureInfo.InvariantCulture, $"The capture of the user's repository could not verify the content of {fresh.Count} ignored folder(s): ")
                        + string.Join("; ", fresh.Select(f => $"'{f.Path}' ({f.Reason})"))
                        + ". No participant of a debate writes, so a same-length swap there cannot come from it: they do not block. Any change of their sizes or modification times still does.")
                {
                    RunId = recorder.Debate.Value,
                },
                cancellationToken).ConfigureAwait(false);
            _alerted.UnionWith(fresh.Select(u => u.Path));
        }
        finally
        {
            _alertGate.Release();
        }
    }

    private async Task<UserRepositoryState> CaptureAsync(CancellationToken cancellationToken)
    {
        var state = await worktrees.CaptureUserRepositoryStateAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        return state with { PorcelainStatus = [.. state.PorcelainStatus.Where(line => !WrittenByAgentStudio(line))] };
    }

    /// <summary>True for a status line whose paths are all files Agent Studio writes under <c>.agentstudio/</c> during a debate.</summary>
    internal static bool WrittenByAgentStudio(string line)
    {
        if (line is null || line.Length < 4 || line[2] != ' ')
        {
            return false;
        }

        var code = line[..2];
        var paths = line[3..].Split(" -> ", StringSplitOptions.None);
        return paths.All(path => Trusted(code, Unquote(path), renamed: paths.Length > 1));
    }

    private static string Unquote(string path)
    {
        var text = path.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1];
        }

        return text.Replace('\\', '/');
    }

    private static bool Trusted(string code, string path, bool renamed)
    {
        if (!path.StartsWith(AgentStudioFolder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var inside = path[AgentStudioFolder.Length..];
        if (inside.StartsWith(AgentStudioFiles.EvidenceFolder + "/", StringComparison.OrdinalIgnoreCase)
            || inside.StartsWith(AgentStudioFiles.TempFolder + "/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (inside.Contains('/', StringComparison.Ordinal))
        {
            return !renamed
                && inside.Length > JournalFolder.Length
                && inside.StartsWith(JournalFolder, StringComparison.OrdinalIgnoreCase)
                && AgentStudioFiles.IsJournalDay(inside[JournalFolder.Length..])
                && code.All(c => c is ' ' or 'M' or 'A' or '?');
        }

        return inside.Equals(AgentStudioFiles.CanvasLayoutFile, StringComparison.OrdinalIgnoreCase)
            || inside.Equals(AgentStudioFiles.VerificationProfileFile, StringComparison.OrdinalIgnoreCase)
            || AgentStudioFiles.DatabaseFiles.Contains(inside, StringComparer.OrdinalIgnoreCase);
    }

    private string JournalDirectory => Path.Combine(repositoryRoot, AgentStudioFiles.DataFolder, AgentStudioFiles.JournalFolder);

    private void TakeJournal()
    {
        lock (_journalGate)
        {
            var days = JournalDays()
                ?? throw new InvalidOperationException($"The folder of the prompt journal ({JournalDirectory}) cannot be listed, so the debate has no baseline for it.");
            foreach (var name in days)
            {
                if (Read(name) is { } content)
                {
                    _journal[name] = (content.Length, Hash(content, content.Length));
                }
            }
        }
    }

    private List<string> JournalDifferences()
    {
        var differences = new List<string>();
        lock (_journalGate)
        {
            foreach (var (name, seen) in _journal.ToList())
            {
                var shown = AgentStudioFolder + JournalFolder + name;
                var content = Read(name);
                if (content is null)
                {
                    differences.Add($"The prompt journal '{shown}' was deleted or cannot be read (it may only grow).");
                }
                else if (content.Length < seen.Length || !string.Equals(Hash(content, seen.Length), seen.Sha256, StringComparison.Ordinal))
                {
                    differences.Add(string.Create(CultureInfo.InvariantCulture, $"The prompt journal '{shown}' no longer starts with what it held ({seen.Length} bytes): it was rewritten or cut short (it may only grow)."));
                }
                else
                {
                    _journal[name] = (content.Length, Hash(content, content.Length));
                }
            }

            var days = JournalDays();
            if (days is null)
            {
                differences.Add($"The folder of the prompt journal '{AgentStudioFolder}{JournalFolder}' cannot be listed any more.");
                return differences;
            }

            foreach (var name in days.Where(n => !_journal.ContainsKey(n)))
            {
                if (Read(name) is { } content)
                {
                    _journal[name] = (content.Length, Hash(content, content.Length));
                }
            }
        }

        return differences;
    }

    private List<string>? JournalDays()
    {
        try
        {
            if (!Directory.Exists(JournalDirectory))
            {
                return [];
            }

            return [.. Directory.EnumerateFiles(JournalDirectory, "*.md", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(AgentStudioFiles.IsJournalDay)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private byte[]? Read(string name)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(JournalDirectory, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Hash(byte[] content, int length) => Convert.ToHexStringLower(SHA256.HashData(content.AsSpan(0, length)));
}
