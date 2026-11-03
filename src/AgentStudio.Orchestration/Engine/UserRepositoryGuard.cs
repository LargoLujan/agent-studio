using System.Globalization;
using System.Security.Cryptography;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// Defence in depth around every step that runs code an agent influenced (ADR-0013): each agent run (planner, executor,
/// judge), and each verification, tool run and integration, which build and test the agents' code. The state of the
/// user's own repository (HEAD, branches, tags, stash, index and working tree status) is checked before the step and
/// after it, and any difference fails the step. Agents work in worktrees outside the repository and the system makes
/// every commit, so a difference means something escaped its worktree (or someone touched the repository meanwhile).
/// </summary>
/// <remarks>
/// <para>
/// Every check compares with one baseline, captured once before anything runs (<see cref="EstablishBaselineAsync"/>),
/// never with the previous check: a change made between two steps is seen by the first check of the next one instead
/// of becoming its new normal. A run is guarded from where this process starts or resumes it.
/// </para>
/// <para>
/// The one known exception is what Agent Studio itself writes under <c>.agentstudio/</c> while a run goes on, and only
/// those exact paths: the prompt journal of ADR-0008 (<c>bitacora/yyyy-MM-dd.md</c>), the database files (exactly
/// <c>agentstudio.db</c>, its <c>-wal</c> and <c>-shm</c> and its <c>.lock</c>), the evidence and temporary folders,
/// the canvas layout and the verification profile. A status line is left out of the captures only when every path it
/// names is one of those; anything else under <c>.agentstudio/</c> is compared.
/// </para>
/// <para>
/// The journal is only ever appended to, so its status line says nothing about what changed in it: its content is
/// checked instead. Every day of the journal on disk when the baseline is captured, and every one that appears later,
/// must keep what it held the last time it was seen as an exact prefix; one that is rewritten, cut short or deleted is
/// a difference.
/// </para>
/// <para>
/// What the capture could not verify (indeterminate: past the per-file cap or the capture budget, or unreadable) blocks
/// like a difference, with one exception (ADR-0012): an ignored folder (status line <c>!! path/</c>) in a run that cannot
/// write to the user's repository at medium integrity (<see cref="CanWriteAtMediumIntegrity"/>). Only a process at
/// medium integrity can swap a file there for one of the same length and restore its timestamp, which is what hashing
/// the content defends against. An entry is only indeterminate when neither capture verified the folder and both read
/// the same metadata for every entry in it (<see cref="UserRepositoryState.CompareInDetail"/>): a file added, removed,
/// resized or touched is a difference and blocks. For such a run, the indeterminate ignored folders are recorded once
/// per path, with an audit alert, instead of blocking. Files, untracked folders and git files the capture could not
/// verify always block.
/// </para>
/// </remarks>
/// <param name="canWriteAtMediumIntegrity">
/// Whether a step of the run can write to the user's repository at medium integrity. True, the default, fails closed:
/// an unverified ignored folder blocks. Only the engine, which knows the run, relaxes it.
/// </param>
/// <param name="recorder">The run's log, where the ignored folders let through unverified are recorded; required to relax.</param>
internal sealed class UserRepositoryGuard(IWorktreeManager worktrees, string repositoryRoot, bool canWriteAtMediumIntegrity = true, RunRecorder? recorder = null)
{
    /// <summary>The code of the audit alert that records the ignored folders a check let through without verifying them.</summary>
    internal const string UnverifiedAlert = "user-repository.capture-indeterminate";

    // The names are the ones Agent Studio gives its own files (AgentStudioFiles); which of them may change during a run, and
    // how, is this guard's own policy.
    private const string AgentStudioFolder = AgentStudioFiles.DataFolder + "/";
    private const string JournalFolder = AgentStudioFiles.JournalFolder + "/";

    /// <summary>How the capture's status lists an ignored path (an ignored folder ends with '/').</summary>
    private const string IgnoredPrefix = "!! ";

    private readonly Lock _journalGate = new();

    /// <summary>What each day of the journal held the last time it was seen: its length and the SHA-256 of those bytes.</summary>
    private readonly Dictionary<string, (int Length, string Sha256)> _journal = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the ignored folders let through unverified are recorded; null when they block.</summary>
    private readonly RunRecorder? _alerts = canWriteAtMediumIntegrity
        ? null
        : recorder ?? throw new ArgumentException("A guard that lets unverified ignored folders through must record them: it needs the run's recorder.", nameof(recorder));

    /// <summary>One check records at a time, so each path is recorded once even when parallel workers check together.</summary>
    private readonly SemaphoreSlim _alertGate = new(1, 1);

    /// <summary>The ignored folders whose alert is already in the log.</summary>
    private readonly HashSet<string> _alerted = new(StringComparer.Ordinal);

    private bool _journalTaken;
    private UserRepositoryState? _baseline;

    /// <summary>Captures the state every later check is compared with. Only the first call captures; later ones keep it.</summary>
    public async Task EstablishBaselineAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _baseline) is null)
        {
            TakeJournal();
            var state = await CaptureAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.CompareExchange(ref _baseline, state, null);
        }
    }

    /// <summary>
    /// Captures the user's repository and lists how it differs from the baseline, with what the capture could not verify
    /// and must block; empty means it is as it was. In a run that cannot write there at medium integrity, the ignored
    /// folders it could not verify are not listed: they are recorded (once per path) before this returns.
    /// </summary>
    /// <exception cref="InvalidOperationException">No baseline was established.</exception>
    public async Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken)
    {
        var baseline = Volatile.Read(ref _baseline)
            ?? throw new InvalidOperationException("The user's repository has no baseline to be checked against.");
        var now = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        var (differences, indeterminate) = UserRepositoryState.CompareInDetail(baseline, now);
        IReadOnlyList<UnverifiedPath> blocking = indeterminate;
        if (_alerts is not null && indeterminate.Count > 0)
        {
            // Only what is indeterminate is weighed: every difference, metadata included, blocks whatever the run can do.
            var folders = IgnoredFolders(baseline, now);
            var letThrough = indeterminate.Where(u => !u.IsGitFile && folders.Contains(u.Path)).ToList();
            if (letThrough.Count > 0)
            {
                await RecordUnverifiedAsync(_alerts, [.. letThrough.Select(u => (u.Path, u.Reason))], cancellationToken).ConfigureAwait(false);
                blocking = [.. indeterminate.Except(letThrough)];
            }
        }

        var journal = JournalDifferences();
        return [.. differences, .. blocking.Select(u => u.Description), .. journal];
    }

    /// <summary>
    /// Whether a step of the run can write to the user's repository at medium integrity, where a same-length swap inside
    /// an ignored folder is possible (ADR-0012): whether the plan has any writer at medium integrity
    /// (<see cref="MediumIntegrityWriters.Find"/>). It is the same reading the warning of the plan names them by, so the
    /// check is strict exactly when the user was warned before approving the plan.
    /// </summary>
    /// <param name="claudeWritesConfined">
    /// Whether the installed Claude Code is a version where the confinement of an executor without a shell was measured,
    /// with the launch its adapter builds today (<see cref="Application.Providers.IWriteConfinementProbe"/>); false when
    /// nobody could say.
    /// </param>
    internal static bool CanWriteAtMediumIntegrity(PlanDefinition plan, IEnumerable<AgentDefinition> roster, VerificationProfile profile, bool claudeWritesConfined) =>
        MediumIntegrityWriters.Find(plan, roster, profile, claudeWritesConfined).Count > 0;

    /// <summary>
    /// The ignored folders of two captures: the paths both list with the status line <c>!! path/</c> and no other line.
    /// Only an indeterminate entry for one of them may be let through; a file, an untracked folder or a git file never is.
    /// </summary>
    internal static IReadOnlySet<string> IgnoredFolders(UserRepositoryState before, UserRepositoryState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var kept = before.PorcelainStatus.Intersect(after.PorcelainStatus, StringComparer.Ordinal).ToList();
        var named = kept.SelectMany(StatusPaths).CountBy(path => path, StringComparer.Ordinal).ToDictionary(StringComparer.Ordinal);
        return kept
            .Where(l => l.StartsWith(IgnoredPrefix, StringComparison.Ordinal) && l.EndsWith('/'))
            .Select(l => l[IgnoredPrefix.Length..])
            .Where(path => named.GetValueOrDefault(path) == 1)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The paths of a status line, <c>XY path</c> or, for a rename or a copy, <c>XY original -&gt; path</c>.</summary>
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

    /// <summary>
    /// Records the ignored folders let through unverified that this guard has not recorded yet (since its baseline, so a
    /// resumed run records them again). Checks of parallel workers record one at a time, and a path counts as recorded
    /// only once its alert is in the log: a check that lets a folder through never returns before the alert that says so.
    /// </summary>
    private async Task RecordUnverifiedAsync(RunRecorder alerts, IReadOnlyList<(string Path, string Reason)> unverified, CancellationToken cancellationToken)
    {
        await _alertGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fresh = unverified.Where(u => !_alerted.Contains(u.Path)).ToList();
            if (fresh.Count == 0)
            {
                return;
            }

            await alerts.RecordAsync(
                DecisionKind.AuditAlertRaised,
                "Carpetas ignoradas del repositorio del usuario sin verificar",
                new AuditAlertPayload(UnverifiedAlert, [], DescribeUnverified(fresh)) { RunId = alerts.RunValue },
                cancellationToken).ConfigureAwait(false);
            _alerted.UnionWith(fresh.Select(u => u.Path));
        }
        finally
        {
            _alertGate.Release();
        }
    }

    private static string DescribeUnverified(IReadOnlyList<(string Path, string Reason)> folders) =>
        string.Create(CultureInfo.InvariantCulture, $"The capture of the user's repository could not verify the content of {folders.Count} ignored folder(s): ")
        + string.Join("; ", folders.Select(f => $"'{f.Path}' ({f.Reason})"))
        + ". No step of this run can write to the user's repository at medium integrity (no executor with a shell in a worktree it may change, nor one that "
        + "writes files there on a CLI other than a Claude Code whose confinement was measured; no step of the profile run without isolation), "
        + "so a same-length swap there cannot come from the run: they do not block. Any change of their sizes or modification times still does.";

    private async Task<UserRepositoryState> CaptureAsync(CancellationToken cancellationToken)
    {
        var state = await worktrees.CaptureUserRepositoryStateAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        return state with { PorcelainStatus = [.. state.PorcelainStatus.Where(line => !WrittenByAgentStudio(line))] };
    }

    /// <summary>
    /// True for a <c>git status --porcelain</c> line (<c>XY path</c>, or <c>XY from -&gt; to</c> for a rename) whose paths
    /// are all files Agent Studio writes under <c>.agentstudio/</c> during a run. A line that cannot be read is kept, so
    /// it is compared.
    /// </summary>
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

        // A folder the capture lists once (an ignored folder, with its trailing '/') or anything under it.
        var inside = path[AgentStudioFolder.Length..];
        if (inside.StartsWith(AgentStudioFiles.EvidenceFolder + "/", StringComparison.OrdinalIgnoreCase)
            || inside.StartsWith(AgentStudioFiles.TempFolder + "/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (inside.Contains('/', StringComparison.Ordinal))
        {
            return Journal(code, inside, renamed);
        }

        return inside.Equals(AgentStudioFiles.CanvasLayoutFile, StringComparison.OrdinalIgnoreCase)
            || inside.Equals(AgentStudioFiles.VerificationProfileFile, StringComparison.OrdinalIgnoreCase)
            || AgentStudioFiles.DatabaseFiles.Contains(inside, StringComparer.OrdinalIgnoreCase);
    }

    private static bool Under(string inside, string folder) =>
        inside.Length > folder.Length && inside.StartsWith(folder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A day of the journal (<c>bitacora/yyyy-MM-dd.md</c>) that is new or was appended to: never deleted, renamed or
    /// copied. That it was only appended to is checked on its content (<see cref="JournalDifferences"/>).
    /// </summary>
    private static bool Journal(string code, string inside, bool renamed)
    {
        if (renamed || !Under(inside, JournalFolder))
        {
            return false;
        }

        return IsJournalDay(inside[JournalFolder.Length..]) && code.All(c => c is ' ' or 'M' or 'A' or '?');
    }

    /// <summary>
    /// A day of the journal exactly as its writers name it (<see cref="AgentStudioFiles.IsJournalDay"/>): any other name in
    /// the folder (<c>notas.md</c>, a copy of a day) is not the journal and is compared like everything else.
    /// </summary>
    private static bool IsJournalDay(string name) => AgentStudioFiles.IsJournalDay(name);

    private string JournalDirectory => Path.Combine(repositoryRoot, AgentStudioFiles.DataFolder, AgentStudioFiles.JournalFolder);

    /// <summary>Takes, once, what every day of the journal holds when the baseline is captured.</summary>
    private void TakeJournal()
    {
        lock (_journalGate)
        {
            if (_journalTaken)
            {
                return;
            }

            _journalTaken = true;
            var days = JournalDays()
                ?? throw new InvalidOperationException($"The folder of the prompt journal ({JournalDirectory}) cannot be listed, so the run has no baseline for it.");
            foreach (var name in days)
            {
                if (Read(name) is { } content)
                {
                    _journal[name] = (content.Length, Hash(content, content.Length));
                }
            }
        }
    }

    /// <summary>
    /// How the journal broke its append-only rule since it was last seen: a day that no longer starts with exactly what
    /// it held, or that is gone. A day that only grew is remembered as it is now, and a new day from the moment it
    /// appears. The files are read under one lock, so concurrent checks see the journal grow in order.
    /// </summary>
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

    /// <summary>The days of the journal on disk (none without its folder), or null when the folder cannot be listed.</summary>
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
                .Where(IsJournalDay)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The bytes of a day of the journal, or null when it is not there or cannot be read. Its writer keeps it shared for reading.</summary>
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
