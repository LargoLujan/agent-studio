using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Debate.Judging;

/// <summary>
/// Builds the dossier of an orchestration run straight from Agent Studio's stores, through their ports, and writes its
/// package of files under the dossier root. Callers pass identifiers only, so neither the engine nor the orchestrator can
/// choose or filter what the judge sees.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The decision log is the source of truth: the entries of the run, the head of the whole log (which the verdict
/// must echo), the hashes of the plan and of the frozen requirements (from <c>RunStarted</c>), the producers' vendors
/// (<see cref="RunLog.ProducerVendors"/>) and the state of the tasks, replayed with <see cref="RunStateReplayer"/>.</item>
/// <item>The run header (<see cref="IRunRegistry"/>) gives the evidence that holds the literal original request and the
/// instant the run started; a header that disagrees with the log is reported, and the log wins. The header is a row of
/// the store, so it must also hash to what <c>RunStarted</c> anchored (<see cref="RunHeaderHash"/>, version 2): one that
/// does not, or a run whose <c>RunStarted</c> anchors no hash (version 1), is an integrity problem.</item>
/// <item>The commit under review is the result of the run's last clean integration in the log
/// (<see cref="RunLog.CandidateCommit"/>): only test runs bound to it can support an approval.</item>
/// <item>Every problem found (the checks below) is an integrity problem of the dossier
/// (<see cref="JudgeDossier.IntegrityProblems"/>): the judge sees it and no approval can pass the validator.</item>
/// <item>Only the evidence the verified log registered is consumed (<see cref="RunLog.RegisteredEvidence"/>), and as the
/// log anchored it: a row of the store that no <c>EvidenceRegistered</c> entry announced with its id, SHA-256, kind and
/// origin (and, from version 2, its title, author, capture time, test counters and commit) is left out and reported, and
/// so is the original request when the log did not register it. A row announced only by version 1 is reported too: its
/// metadata is unanchored, so it can never support an approval.</item>
/// <item>Every evidence of the dossier is copied into the package and checked against the SHA-256 recorded when it was
/// captured. A mismatch, or content that cannot be read, is a system check the judge sees: never silently left out.</item>
/// <item>The original request is read only against the SHA-256 its <c>EvidenceRegistered</c> entry anchored, never against
/// the row of the store (R3-08): content rewritten with the hash of its row, or that the log never anchored, is withheld
/// from the judge, with a system check.</item>
/// <item>The package goes to <c>&lt;root&gt;/&lt;project&gt;/&lt;run&gt;/&lt;first 16 characters of the head&gt;</c>, so
/// building again at the same head rewrites the same package and a later state of the log gets a new one. Nothing is
/// deleted nor written through a link (junction or symbolic link) from the root down, and the files of the package
/// are always new files.</item>
/// </list>
/// Phase 3 only reviews runs: a request without a run id is refused (the whole-project audit is Phase 5).
/// </remarks>
public sealed class JudgeDossierBuilder(
    IDecisionRecorder decisions,
    IRunRegistry runs,
    IPlanStore plans,
    IRequirementStore requirements,
    IEvidenceStore evidence,
    JudgingOptions options) : IJudgeDossierBuilder, IJudgeDossierPackageBuilder
{
    private const int HeadPrefixLength = 16;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<JudgeDossier> BuildAsync(DossierRequest request, CancellationToken cancellationToken) =>
        (await BuildPackageAsync(request, cancellationToken).ConfigureAwait(false)).Dossier;

    /// <exception cref="ArgumentException">The request names no run, or the run id is not valid.</exception>
    /// <exception cref="InvalidOperationException">The run never started, is not registered, or its plan, requirements or original request are missing.</exception>
    public async Task<JudgeDossierPackage> BuildPackageAsync(DossierRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);
        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            throw new ArgumentException("Phase 3 builds the dossier of one orchestration run: the request must name the run.", nameof(request));
        }

        var projectId = request.ProjectId;
        var run = new RunId(request.RunId);
        var checks = new List<string>();

        // A critical moment: the judge rests on the whole log, verified from its first entry (ADR-0010, addendum of 2026-11-04).
        var log = await decisions.LoadAsync(projectId, LogLoad.Full, cancellationToken).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            checks.Add($"The decision log does not verify (entry {log.Verification.FirstInvalidSequence}): {log.Verification.Problem}");
        }

        var runEntries = RunLog.EntriesOf(log.Entries, run.Value);
        var started = RunLog.RunStarted(runEntries)
            ?? throw new InvalidOperationException($"The decision log of project '{projectId}' has no RunStarted for run '{run}'.");
        var header = await runs.FindAsync(projectId, run, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Run '{run}' of project '{projectId}' is not registered.");
        if (!string.Equals(header.PlanHash, started.PlanHash, StringComparison.Ordinal))
        {
            checks.Add($"The run header names plan {header.PlanHash}, but RunStarted in the log names {started.PlanHash}; the log is used.");
        }

        if (!string.Equals(header.RequirementSetHash, started.RequirementSetHash, StringComparison.Ordinal))
        {
            checks.Add($"The run header names requirement set {header.RequirementSetHash}, but RunStarted in the log names {started.RequirementSetHash}; the log is used.");
        }

        if (HeaderProblem(run, header, started) is { } unverified)
        {
            checks.Add(unverified);
        }

        var frozen = await requirements.FindAsync(projectId, started.RequirementSetHash, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The frozen requirement set {started.RequirementSetHash} of run '{run}' is not stored.");
        var plan = await plans.FindAsync(projectId, started.PlanHash, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The plan {started.PlanHash} of run '{run}' is not stored.");
        var originalRequest = await ReadOriginalRequestAsync(projectId, header.OriginalRequest, RunLog.AnchoredSha256(runEntries, header.OriginalRequest), checks, cancellationToken).ConfigureAwait(false);
        var stored = await evidence.ListByRunAsync(projectId, run.Value, cancellationToken).ConfigureAwait(false);
        var runEvidence = RunLog.RegisteredEvidence(runEntries, stored, checks);
        if (!runEvidence.Any(e => e.Id == header.OriginalRequest))
        {
            checks.Add($"The original request (evidence '{header.OriginalRequest}') named by the run header is not evidence the decision log registered for this run.");
        }

        var replayProblems = new List<string>();
        var events = RunLog.ReplayEvents(runEntries, replayProblems);
        var replay = RunStateReplayer.Replay(plan, header.CreatedAt, events, started.PlanHash);
        checks.AddRange(replayProblems.Concat(replay.Problems).Select(p => "Replay: " + p));

        var dossier = new JudgeDossier(
            originalRequest,
            frozen,
            PlanJson.Render(plan),
            runEntries,
            log.HeadHash,
            runEvidence,
            [.. replay.Graph.Tasks.Select(t => t.Snapshot())],
            RunLog.ProducerVendors(runEntries))
        {
            CandidateCommit = RunLog.CandidateCommit(runEntries),
        };

        var folder = PackageFolder(projectId, run, log.HeadHash);
        return await WritePackageAsync(folder, projectId, run, dossier, checks, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Why the stored header is not the one the run started with, or null when it hashes to what <c>RunStarted</c>
    /// anchored (<see cref="RunHeaderHash"/>, version 2). A version-1 <c>RunStarted</c> anchors no hash, so its header
    /// cannot be checked at all.
    /// </summary>
    private static string? HeaderProblem(RunId run, RunRecord header, RunStartedPayload started)
    {
        if (started.HeaderHash is not { } anchored)
        {
            return string.Create(CultureInfo.InvariantCulture, $"RunStarted of run '{run}' is version {started.V}, which does not anchor the hash of the run header: the header (the roster with the judge, the options, the base commit, the original request and the start of the run) cannot be checked against what was approved.");
        }

        var stored = RunHeaderHash.Of(header);
        return string.Equals(stored, anchored, StringComparison.Ordinal)
            ? null
            : $"The header stored for run '{run}' hashes to {stored}, but RunStarted anchored {anchored}: its roster (the judge included), options, base commit, original request or start changed after the run started, so what the dossier takes from it is not what was approved.";
    }

    /// <summary>
    /// The literal original request, only as the decision log anchored it (R3-08): its bytes must hash to
    /// <paramref name="anchoredSha256"/>, the SHA-256 of its <c>EvidenceRegistered</c> entry, and the text is decoded from
    /// those very bytes. Content the log anchored nothing for, or that hashes to anything else (rewritten together with
    /// the hash of its row, say), is withheld: the judge gets a note in its place and the dossier an integrity problem.
    /// </summary>
    /// <exception cref="InvalidOperationException">The content cannot be read at all.</exception>
    private async Task<string> ReadOriginalRequestAsync(string projectId, EvidenceId id, string? anchoredSha256, List<string> checks, CancellationToken cancellationToken)
    {
        if (anchoredSha256 is null)
        {
            return Withheld(id); // the check that it is not registered evidence of the run comes with the evidence of the run
        }

        byte[] content;
        try
        {
            var stream = await evidence.OpenContentAsync(projectId, id, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                content = buffer.ToArray();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"The original request (evidence '{id}') cannot be read: {ex.Message}", ex);
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(content));
        if (!string.Equals(actual, anchoredSha256, StringComparison.OrdinalIgnoreCase))
        {
            checks.Add($"The original request (evidence '{id}') hashes to {actual}, not to {anchoredSha256}, the SHA-256 the decision log anchored for it: it was rewritten after it was registered, so it is withheld from the judge and proves nothing.");
            return Withheld(id);
        }

        using var reader = new StreamReader(new MemoryStream(content, writable: false), Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What the judge gets in place of an original request whose content the log does not vouch for.</summary>
    private static string Withheld(EvidenceId id) =>
        $"[Withheld by Agent Studio: the content of the original request (evidence '{id}') is not the one the decision log anchored, or the log anchored none. The system checks say why.]";

    private string PackageFolder(string projectId, RunId run, string headHash)
    {
        var root = Path.GetFullPath(options.DossierRoot);
        var folder = Path.GetFullPath(Path.Combine(root, Segment(projectId, 64), run.Value, headHash[..Math.Min(HeadPrefixLength, headHash.Length)]));
        if (!folder.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The dossier folder '{folder}' would fall outside the dossier root '{root}'.");
        }

        return folder;
    }

    /// <summary>
    /// Copies and checks every evidence first, so the problems found while copying are part of the dossier the validator
    /// checks (<see cref="JudgeDossier.IntegrityProblems"/>), and then writes the request and the dossier itself.
    /// </summary>
    private async Task<JudgeDossierPackage> WritePackageAsync(string folder, string projectId, RunId run, JudgeDossier dossier, List<string> checks, CancellationToken cancellationToken)
    {
        // An agent with a shell runs as the same user and can plant a junction on the way to the package: nothing is
        // deleted nor written through one, so the package can never land on (or wipe) a folder elsewhere.
        RefuseLinks(folder);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true); // Same head, same content: the package is rebuilt, never merged with an old one.
        }

        var evidenceFolder = Path.Combine(folder, "evidence");
        Directory.CreateDirectory(evidenceFolder);
        RefuseLinks(evidenceFolder); // again, now that the folders exist: one could have been swapped meanwhile

        var copies = new List<DossierEvidenceFile>();
        for (var i = 0; i < dossier.Evidence.Count; i++)
        {
            var record = dossier.Evidence[i];
            var name = $"{i + 1:D3}-{Segment(record.Id.Value, 48)}{(record.Kind == EvidenceKind.CodeDiff ? ".diff" : ".txt")}";
            copies.Add(await CopyEvidenceAsync(projectId, record, evidenceFolder, name, checks, cancellationToken).ConfigureAwait(false));
        }

        IReadOnlyList<string> problems = [.. checks];
        var checkedDossier = dossier with { IntegrityProblems = problems };
        var files = new DossierFiles(folder, projectId, run.Value, copies, problems);
        await WriteNewTextAsync(Path.Combine(folder, "request.txt"), checkedDossier.OriginalRequest, cancellationToken).ConfigureAwait(false);
        await WriteNewTextAsync(Path.Combine(folder, "dossier.json"), DossierDocument.ToJson(checkedDossier, files), cancellationToken).ConfigureAwait(false);
        return new JudgeDossierPackage(checkedDossier, files);
    }

    /// <summary>
    /// Refuses a path that goes through a link (a junction or a symbolic link) anywhere from the dossier root, included,
    /// down to <paramref name="path"/>, included. The folders above the root are the user's own and are not checked.
    /// </summary>
    /// <exception cref="InvalidOperationException">A folder on the way is a link.</exception>
    private void RefuseLinks(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.DossierRoot));
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (IsLink(current))
            {
                throw new InvalidOperationException(
                    $"The dossier folder '{path}' goes through '{current.FullName}', which is a link (a junction or a symbolic link): the package is never written nor deleted through one.");
            }

            if (string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

    /// <summary>A link, even one whose target is gone; a folder that does not exist is not one.</summary>
    private static bool IsLink(DirectoryInfo folder) =>
        folder.LinkTarget is not null || (folder.Exists && folder.Attributes.HasFlag(FileAttributes.ReparsePoint));

    /// <summary>Writes a file that must not exist yet, so a link planted in its place is never followed.</summary>
    private static async Task WriteNewTextAsync(string path, string text, CancellationToken cancellationToken)
    {
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await using (file.ConfigureAwait(false))
        {
            await file.WriteAsync(Utf8NoBom.GetBytes(text), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DossierEvidenceFile> CopyEvidenceAsync(
        string projectId,
        EvidenceRecord record,
        string evidenceFolder,
        string name,
        List<string> checks,
        CancellationToken cancellationToken)
    {
        try
        {
            var source = await evidence.OpenContentAsync(projectId, record.Id, cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                var (size, written, sha256) = await CopyAsync(source, Path.Combine(evidenceFolder, name), options.MaxEvidenceFileBytes, cancellationToken).ConfigureAwait(false);
                var verified = string.Equals(sha256, record.ContentSha256, StringComparison.Ordinal);
                if (!verified)
                {
                    checks.Add($"Evidence '{record.Id}': its content does not match the SHA-256 recorded at capture; it changed afterwards and proves nothing.");
                }

                return new DossierEvidenceFile(record.Id, "evidence/" + name, verified, written < size, size);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            checks.Add($"Evidence '{record.Id}': its content cannot be read ({ex.Message}); it proves nothing.");
            return new DossierEvidenceFile(record.Id, null, false, false, 0);
        }
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes, but hashes the whole content.</summary>
    private static async Task<(long Size, long Written, string Sha256)> CopyAsync(Stream source, string path, long limit, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using (target.ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            long size = 0;
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                var room = (int)Math.Min(read, limit - written);
                if (room > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, room), cancellationToken).ConfigureAwait(false);
                    written += room;
                }

                size += read;
            }

            return (size, written, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
    }

    /// <summary>A safe folder or file name part: <c>[A-Za-z0-9_-]</c>, at most <paramref name="maxLength"/> characters.</summary>
    private static string Segment(string text, int maxLength)
    {
        var safe = new string([.. text.Select(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' ? c : '_')]);
        safe = safe.Length > maxLength ? safe[..maxLength] : safe;
        return safe.Length == 0 ? "_" : safe;
    }
}
