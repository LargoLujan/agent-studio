using AgentStudio.Application.Decisions;
using AgentStudio.Application.Documents;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Memory.Documents;

/// <summary>
/// «Aprobar y exportar» (docs/17 D-60; ADR-0057). In this order, and nothing is recorded if any step fails:
/// <list type="number">
/// <item>The request is validated (<see cref="ClientDocumentExportRules"/>), the project's log is read and verified whole (a log that does not
/// verify writes nothing), and a folder inside the project is refused while a run goes on (<see cref="IRunActivity"/>).</item>
/// <item>Every format is rendered before the first file is written, so a writer that fails leaves nothing behind. The Markdown is always
/// rendered, whichever formats were chosen: it is what stays as evidence.</item>
/// <item>Each file is written whole to a temporary file in the folder and moved to its name without overwriting: a name that is taken (an
/// earlier version, or a file the person had) gets a suffix (<see cref="ClientDocumentFiles.WithCollisionSuffix"/>) and the name used is the
/// one recorded. If a later step fails, the files this export created are removed.</item>
/// <item>The Markdown is captured as evidence, and last <c>ClientDocumentExported</c> is recorded, with the fingerprint of each file.</item>
/// </list>
/// </summary>
public sealed class ClientDocumentExporter(
    IEnumerable<IClientDocumentFormatWriter> writers,
    IEvidenceStore evidence,
    IDecisionRecorder recorder,
    IRunActivity activity,
    ISecretRedactor redactor) : IClientDocumentExporter
{
    private const int MaxNameAttempts = 1000;

    private readonly Dictionary<ClientDocumentFormat, IClientDocumentFormatWriter> _writers = writers.ToDictionary(w => w.Format);

    public async Task<ClientDocumentExportResult> ExportAsync(ClientDocumentExportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var problems = ClientDocumentExportRules.Validate(request, redactor);
        if (problems.Count > 0)
        {
            throw new ArgumentException("The export breaks its rules: " + string.Join(", ", problems), nameof(request));
        }

        var projectId = request.Project.ProjectId;
        var state = await recorder.LoadAsync(projectId, LogLoad.Full, ct).ConfigureAwait(false);
        if (!state.Verification.IsValid)
        {
            throw new DecisionLogIntegrityException(projectId, state.Verification);
        }

        var document = request.Build.Document;

        // The person's approval is the request's, not the screen's: it names who approved and the fingerprints of the document and of the
        // list of what goes out that are about to be written, so a caller that skips the screen cannot skip it.
        var approval = request.Approval ?? throw new ArgumentException("document.export-approval-required: the export needs the approval of the person.", nameof(request));
        if (string.IsNullOrWhiteSpace(approval.ApprovedBy)
            || !string.Equals(approval.ModelSha256, document.Sha256(), StringComparison.Ordinal)
            || !string.Equals(approval.DisclosureSha256, request.Build.Disclosure.Sha256(), StringComparison.Ordinal))
        {
            throw new ArgumentException("document.export-approval-mismatch: the approval is not about this document and this list of what goes out.", nameof(request));
        }

        // Links and junctions are followed (also in the folders above): the folder written is the real one, so a link to the project cannot
        // dodge the check of the run, nor point at its repository or its data.
        var destination = ResolveReal(request.DestinationDirectory);
        var root = ResolveReal(request.Project.RootPath);
        var inside = IsInside(root, destination);
        if (inside && IsProtected(root, destination))
        {
            throw new ArgumentException("document.export-destination-protected: a document is never written into .git or .agentstudio.", nameof(request));
        }

        void EnsureNotBusy()
        {
            if (inside && activity.IsBusy(projectId))
            {
                throw new ClientDocumentRunBusyException();
            }
        }

        EnsureNotBusy();
        var needed = request.Formats.Append(ClientDocumentFormat.Markdown).Distinct().ToList();
        foreach (var format in needed.Where(f => !_writers.ContainsKey(f)))
        {
            throw new InvalidOperationException($"There is no writer for the format {format}.");
        }

        var rendered = new Dictionary<ClientDocumentFormat, byte[]>();
        foreach (var format in needed)
        {
            ct.ThrowIfCancellationRequested();
            rendered[format] = await _writers[format].RenderAsync(document, request.Language, ct).ConfigureAwait(false);
        }

        var cover = document.Cover;
        var created = new List<string>();
        var files = new List<ExportedClientFile>();

        // The folders this export makes (the destination and the parents that did not exist), to take them away if it fails and they are empty.
        var made = new List<string>();
        for (var folder = destination; !Directory.Exists(folder) && Path.GetDirectoryName(folder) is not null; folder = Path.GetDirectoryName(folder)!)
        {
            made.Add(folder);
        }

        try
        {
            Directory.CreateDirectory(destination);
            foreach (var format in request.Formats)
            {
                ct.ThrowIfCancellationRequested();

                // Rendering a PDF can take a minute: a run may have started since the check above.
                EnsureNotBusy();
                var bytes = rendered[format];
                var name = WriteWithoutOverwriting(destination, ClientDocumentFiles.FileName(cover.Project, cover.Milestone, cover.Version, format), bytes, created);
                files.Add(new ExportedClientFile(format, name, ContentHash.Sha256Hex(bytes), bytes.Length));
            }

            var markdown = rendered[ClientDocumentFormat.Markdown];
            EvidenceRecord record;
            using (var content = new MemoryStream(markdown, writable: false))
            {
                record = await evidence.CaptureAsync(
                    projectId,
                    new EvidenceDraft(EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, $"Documento para clientes · versión {cover.Version}", "agent-studio", MediaType: "text/markdown"),
                    content,
                    ct).ConfigureAwait(false);
            }

            var result = new ClientDocumentExportResult(cover.Version, files, record.Id, record.ContentSha256);
            await recorder.RecordAsync(
                projectId,
                new Actor(ActorKind.User, request.DecidedBy),
                DecisionKind.ClientDocumentExported,
                $"Documentación para clientes exportada: versión {cover.Version}",
                ClientDocumentExportedPayload.Of(request, result),
                cancellationToken: ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            foreach (var path in created)
            {
                TryDelete(path);
            }

            foreach (var folder in made)
            {
                TryDeleteEmptyDirectory(folder);
            }

            throw;
        }
    }

    private static bool IsInside(string root, string directory)
    {
        var folder = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return (Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar).StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether <paramref name="directory"/>, inside <paramref name="root"/>, is the repository's (<c>.git</c>) or the data's (<c>.agentstudio</c>) folder or under one.</summary>
    private static bool IsProtected(string root, string directory) =>
        Path.GetRelativePath(root, directory).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase) || part.Equals(".agentstudio", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The full path with every link and junction on it followed, also the ones in the folders above, and also when the folder does not
    /// exist yet (what is missing is added as it is). A link that loops, or is too deep, is refused.
    /// </summary>
    internal static string ResolveReal(string path)
    {
        var current = Path.GetFullPath(path);
        for (var round = 0; round < 16; round++)
        {
            var parts = current.Substring(Path.GetPathRoot(current)!.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var resolved = Path.GetPathRoot(current)!;
            var changed = false;
            foreach (var part in parts)
            {
                var next = Path.Combine(resolved, part);
                if (!changed && Directory.Exists(next) && new DirectoryInfo(next).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    resolved = Path.GetFullPath(target.FullName);
                    changed = true;
                }
                else
                {
                    resolved = next;
                }
            }

            if (!changed)
            {
                return resolved;
            }

            current = resolved;
        }

        throw new IOException("The folder has too many links to be resolved.");
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a folder that cannot be removed stays, empty.
        }
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> whole to a temporary file next to the destination and moves it to <paramref name="fileName"/>, or to the
    /// same name with a suffix when it is taken; it never overwrites. Returns the name used.
    /// </summary>
    private static string WriteWithoutOverwriting(string directory, string fileName, byte[] bytes, List<string> created)
    {
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            for (var attempt = 1; attempt <= MaxNameAttempts; attempt++)
            {
                var name = attempt == 1 ? fileName : ClientDocumentFiles.WithCollisionSuffix(fileName, attempt);
                var path = Path.Combine(directory, name);
                try
                {
                    File.Move(temporary, path, overwrite: false);
                    created.Add(path);
                    return name;
                }
                catch (IOException) when (File.Exists(path) || Directory.Exists(path))
                {
                    // The name is taken: the next one.
                }
            }

            throw new IOException("No free name was found for the file.");
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a file that cannot be removed is left, and nothing was recorded for it.
        }
    }
}
