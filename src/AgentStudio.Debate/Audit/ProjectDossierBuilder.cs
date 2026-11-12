using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Debate.Audit;

/// <summary>What the dossier of a whole-project audit is built from: the verified log, the audit's unit in it and what the service knows.</summary>
/// <param name="Log">The whole verified log, read after <c>ProjectAuditStarted</c> and the verification were recorded.</param>
/// <param name="Snapshot">The read-only copy of the commit audited, or null when it could not be extracted.</param>
/// <param name="NotExercised">What the profile's test steps leave out, with the reason; empty when they run every test.</param>
public sealed record ProjectDossierRequest(
    string ProjectId,
    RunId Audit,
    string RequestText,
    FrozenRequirementSet Requirements,
    string HeadCommit,
    IReadOnlyList<DecisionEntry> Log,
    IReadOnlyList<VendorId> ProducerVendors,
    IReadOnlyList<VendorId> DeclaredAuthors,
    IReadOnlyList<string> SkippedSteps,
    string? Snapshot,
    IReadOnlyList<PendingProblem> Pending,
    IReadOnlyList<string> Checks,
    IReadOnlyList<ExcludedTests> NotExercised,
    AuditMilestoneScope? Milestone = null);

/// <summary>
/// Builds the dossier of a whole-project audit (docs/15 D-30): an index, because a repository does not fit in a prompt (R8).
/// It holds the frozen requirements, the verification of HEAD (the evidence the log registered for the audit, as it anchored
/// it), each run with its start, end, verdicts and alerts, each debate with its mode, question, outcome and the verdict it
/// challenges, the documents of the copy (<c>docs/**</c> and the README) with their size and SHA-256, the pending problems and
/// the entries of the log that matter, summarised. The judge reads the code in the copy of the commit.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Caps: <see cref="MaxEntries"/> entries of the log and <see cref="MaxBytes"/> of JSON. What does not fit is cut in a
/// fixed order (documents, then entries, then debates, then runs; the last ones of each are kept for entries, debates
/// and runs, the first ones for documents) and the document says how many items of each list were left out.</item>
/// <item>The dossier the validator checks has the requirements, the evidence of the audit, the head of the log that the
/// verdict must echo, the vendors that may not judge, HEAD as the commit under review (only its test runs count) and every
/// problem found in the material (<see cref="JudgeDossier.IntegrityProblems"/>).</item>
/// <item>The package goes to <c>&lt;dossier root&gt;\&lt;project&gt;\&lt;audit&gt;\&lt;16 first characters of the head&gt;</c>,
/// with the discipline of the dossier of a run: nothing is written nor deleted through a link, and its files are new.</item>
/// <item>The text of the index is escaped so that nothing in it (a question, an alert) can close the delimiter it travels in.</item>
/// </list>
/// </remarks>
public sealed class ProjectDossierBuilder(IEvidenceStore evidence, JudgingOptions options)
{
    public const string Format = "agent-studio/project-dossier/1";

    public const int MaxEntries = 200;

    public const int MaxBytes = 256 * 1024;

    private const int MaxDocuments = 2000;
    private const int HeadPrefixLength = 16;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly DecisionKind[] SummarizedKinds =
    [
        DecisionKind.RunStarted, DecisionKind.RunFinished, DecisionKind.IntegrationPerformed, DecisionKind.VerdictIssued,
        DecisionKind.AuditAlertRaised, DecisionKind.ReplanRequested, DecisionKind.ApprovalGranted, DecisionKind.ApprovalDenied,
        DecisionKind.DebateOpened, DecisionKind.DebateClosed, DecisionKind.VerdictChallenged, DecisionKind.ProjectAuditStarted,
        DecisionKind.ProjectAuditFinished,
    ];

    /// <summary>The dossier, its package and the index the judge reads.</summary>
    public async Task<(JudgeDossierPackage Package, string Index)> BuildAsync(ProjectDossierRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var checks = new List<string>(request.Checks);
        var head = request.Log.Count == 0 ? DecisionLog.GenesisHash : request.Log[^1].Hash;
        var auditEntries = RunLog.EntriesOf(request.Log, request.Audit.Value);
        var stored = await evidence.ListByRunAsync(request.ProjectId, request.Audit.Value, cancellationToken).ConfigureAwait(false);
        var registered = RunLog.RegisteredEvidence(auditEntries, stored, checks);
        var verification = registered.Where(e => e.Kind is not EvidenceKind.Document).ToList(); // the request is a document; the verification never is

        var folder = PackageFolder(request.ProjectId, request.Audit, head);
        RefuseLinks(folder);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true); // same head, same content: rebuilt, never merged with an old one
        }

        var evidenceFolder = Path.Combine(folder, "evidence");
        Directory.CreateDirectory(evidenceFolder);
        RefuseLinks(evidenceFolder);
        var copies = new List<DossierEvidenceFile>();
        for (var i = 0; i < registered.Count; i++)
        {
            copies.Add(await CopyAsync(request.ProjectId, registered[i], evidenceFolder, $"{i + 1:D3}-{Segment(registered[i].Id.Value, 48)}.txt", checks, cancellationToken).ConfigureAwait(false));
        }

        // A milestone narrows what the judge is told about to its own runs and debates; the head the verdict echoes is the log's.
        var scoped = request with { Log = InScope(request), Pending = PendingInScope(request) };
        var summarized = Summarize(scoped.Log);
        var index = await IndexAsync(scoped, head, verification, copies, summarized, checks, cancellationToken).ConfigureAwait(false);
        var dossier = new JudgeDossier(
            request.RequestText,
            request.Requirements,
            string.Empty,
            summarized,
            head,
            registered,
            [],
            request.ProducerVendors.Concat(request.DeclaredAuthors).ToHashSet())
        {
            CandidateCommit = request.HeadCommit,
            IntegrityProblems = [.. checks],
        };
        var files = new DossierFiles(folder, request.ProjectId, request.Audit.Value, copies, [.. checks]);
        await WriteNewTextAsync(Path.Combine(folder, "request.txt"), request.RequestText, cancellationToken).ConfigureAwait(false);
        await WriteNewTextAsync(Path.Combine(folder, "dossier.json"), index, cancellationToken).ConfigureAwait(false);
        return (new JudgeDossierPackage(dossier, files), index);
    }

    /// <summary>
    /// The log the dossier describes: all of it for the whole project; for a milestone, the entries of its runs and debates and of the
    /// audit itself (entries that belong to no run, such as the memory, are left out).
    /// </summary>
    private static IReadOnlyList<DecisionEntry> InScope(ProjectDossierRequest request)
    {
        if (request.Milestone is not { } scope)
        {
            return request.Log;
        }

        var units = UnitsOf(request, scope);
        return [.. request.Log.Where(e => AuditedRun.Parse(e).Payload?.RunId is { } run && units.Contains(run))];
    }

    private static HashSet<string> UnitsOf(ProjectDossierRequest request, AuditMilestoneScope scope) =>
        new(scope.Runs.Concat(scope.Debates), StringComparer.Ordinal) { request.Audit.Value };

    /// <summary>The pending problems the dossier lists: all of the project's, or for a milestone those whose source is one of its units.</summary>
    private static IReadOnlyList<PendingProblem> PendingInScope(ProjectDossierRequest request)
    {
        if (request.Milestone is not { } scope)
        {
            return request.Pending;
        }

        var units = UnitsOf(request, scope);
        return [.. request.Pending.Where(p => units.Contains(p.Source))];
    }

    /// <summary>The entries that matter to an audit, the last <see cref="MaxEntries"/> of them.</summary>
    private static List<DecisionEntry> Summarize(IReadOnlyList<DecisionEntry> log) =>
        [.. log.Where(e => SummarizedKinds.Contains(e.Kind)).TakeLast(MaxEntries)];

    private async Task<string> IndexAsync(
        ProjectDossierRequest request,
        string head,
        IReadOnlyList<EvidenceRecord> verification,
        IReadOnlyList<DossierEvidenceFile> copies,
        IReadOnlyList<DecisionEntry> summarized,
        List<string> checks,
        CancellationToken cancellationToken)
    {
        var items = request.Log.OrderBy(e => e.Sequence).Select(AuditedRun.Parse).ToList();
        var documents = Documents(request.Snapshot, checks);
        var runs = Runs(items);
        var debates = await DebatesAsync(request.ProjectId, items, cancellationToken).ConfigureAwait(false);
        var entries = summarized.Select(e => (JsonNode)new JsonObject
        {
            ["sequence"] = e.Sequence,
            ["kind"] = e.Kind.ToString(),
            ["actor"] = $"{e.Actor.Kind}:{e.Actor.Id}",
            ["summary"] = e.Summary,
        }).ToList();

        // The entries that matter beyond the last MaxEntries are left out too, and counted with what the shrinking drops.
        var beyondCap = request.Log.Count(e => SummarizedKinds.Contains(e.Kind)) - summarized.Count;
        var dropped = new Dictionary<string, int>(StringComparer.Ordinal) { ["documents"] = 0, ["runs"] = 0, ["debates"] = 0, ["entries"] = beyondCap };
        var documentsLeft = Math.Max(0, documents.Count - MaxDocuments);
        dropped["documents"] = documentsLeft;
        documents = [.. documents.Take(MaxDocuments)];

        // The cap is on what the judge is given: the indented rendering that goes into the prompt and the package.
        string Render() => Root(request, head, verification, copies, documents, runs, debates, entries, dropped, checks).ToJsonString(DossierDocument.Indented);

        // Deterministic shrinking: documents first (the judge lists them in the copy anyway), then the oldest entries (the runs
        // and the debates summarise them), then the oldest debates and, last, the oldest runs.
        while (Utf8NoBom.GetByteCount(Render()) > MaxBytes)
        {
            if (documents.Count > 0)
            {
                Shrink(documents, dropped, "documents", keepFirst: true);
            }
            else if (entries.Count > 0)
            {
                Shrink(entries, dropped, "entries", keepFirst: false);
            }
            else if (debates.Count > 0)
            {
                Shrink(debates, dropped, "debates", keepFirst: false);
            }
            else if (runs.Count > 0)
            {
                Shrink(runs, dropped, "runs", keepFirst: false);
            }
            else
            {
                break;
            }
        }

        return Render();
    }

    /// <summary>Halves a list (at least one item goes), keeping its first or its last items, and counts what went.</summary>
    private static void Shrink(List<JsonNode> list, Dictionary<string, int> dropped, string name, bool keepFirst)
    {
        var remove = Math.Max(1, list.Count / 2);
        if (keepFirst)
        {
            list.RemoveRange(list.Count - remove, remove);
        }
        else
        {
            list.RemoveRange(0, remove);
        }

        dropped[name] += remove;
    }

    private static JsonObject Root(
        ProjectDossierRequest request,
        string head,
        IReadOnlyList<EvidenceRecord> verification,
        IReadOnlyList<DossierEvidenceFile> copies,
        List<JsonNode> documents,
        List<JsonNode> runs,
        List<JsonNode> debates,
        List<JsonNode> entries,
        Dictionary<string, int> dropped,
        List<string> checks)
    {
        var files = copies.ToDictionary(c => c.Evidence, c => c.RelativePath);
        var root = new JsonObject
        {
            ["format"] = Format,
            ["audit"] = request.Audit.Value,
            ["headCommit"] = request.HeadCommit,
            ["requirementSetHash"] = request.Requirements.ContentHash,
            ["decisionLogHeadHash"] = head,
            ["requirements"] = new JsonArray([.. request.Requirements.Requirements.Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id.Value,
                ["priority"] = r.Priority.ToString(),
                ["statement"] = r.Statement,
                ["criteria"] = new JsonArray([.. r.Criteria.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id,
                    ["method"] = c.Method.ToString(),
                    ["description"] = c.Description,
                })]),
            })]),
            ["verification"] = new JsonArray([.. verification.Select(e => (JsonNode)new JsonObject
            {
                ["evidence"] = e.Id.Value,
                ["kind"] = e.Kind.ToString(),
                ["origin"] = e.Origin.ToString(),
                ["title"] = e.Title,
                ["subjectCommit"] = e.SubjectCommit,
                ["testRun"] = e.TestRun is { } t ? new JsonObject { ["passed"] = t.Passed, ["failed"] = t.Failed, ["skipped"] = t.Skipped, ["exitCode"] = t.ExitCode, ["succeeded"] = t.Succeeded } : null,
                ["file"] = files.GetValueOrDefault(e.Id),
            })]),
            ["skippedSteps"] = new JsonArray([.. request.SkippedSteps.Select(s => (JsonNode?)JsonValue.Create(s))]),

            // The tests the profile leaves out: the verification above does not exercise them, and the judge must not read
            // a passing test step as a check of what they cover.
            ["testsNotExercised"] = new JsonArray([.. request.NotExercised.Select(e => (JsonNode)new JsonObject { ["filter"] = e.Filter, ["selector"] = e.Selector, ["reason"] = e.Reason })]),
            ["producerVendors"] = new JsonArray([.. request.ProducerVendors.Select(v => (JsonNode?)JsonValue.Create(v.Value))]),
            ["declaredAuthors"] = new JsonArray([.. request.DeclaredAuthors.Select(v => (JsonNode?)JsonValue.Create(v.Value))]),
            ["runs"] = new JsonArray([.. runs.Select(r => r.DeepClone())]),
            ["debates"] = new JsonArray([.. debates.Select(d => d.DeepClone())]),
            ["documents"] = new JsonArray([.. documents.Select(d => d.DeepClone())]),
            ["pendingProblems"] = new JsonArray([.. request.Pending.Select(p => (JsonNode)new JsonObject { ["code"] = p.Code, ["source"] = p.Source, ["message"] = p.Message })]),
            ["entries"] = new JsonArray([.. entries.Select(e => e.DeepClone())]),
            ["truncated"] = new JsonObject
            {
                ["documents"] = dropped["documents"],
                ["runs"] = dropped["runs"],
                ["debates"] = dropped["debates"],
                ["entries"] = dropped["entries"],
            },
            ["systemChecks"] = new JsonArray([.. checks.Select(c => (JsonNode?)JsonValue.Create(c))]),
        };

        // Only an audit that closes a milestone says so: the index of a whole-project audit keeps the shape it had.
        if (request.Milestone is { } milestone)
        {
            root["milestone"] = new JsonObject
            {
                ["id"] = milestone.Milestone,
                ["runs"] = new JsonArray([.. milestone.Runs.Select(r => (JsonNode?)JsonValue.Create(r))]),
                ["debates"] = new JsonArray([.. milestone.Debates.Select(d => (JsonNode?)JsonValue.Create(d))]),
            };
        }

        return root;
    }

    /// <summary>Each run of the log: its start, its end, its verdicts and its alerts, in the order they started.</summary>
    private static List<JsonNode> Runs(List<RunItem> items)
    {
        var runs = new List<JsonNode>();
        foreach (var (item, started) in items.Where(i => i.Payload is RunStartedPayload).Select(i => (i, (RunStartedPayload)i.Payload!)).DistinctBy(s => s.Item2.RunId))
        {
            var run = started.RunId;
            var finished = items.Select(i => i.Payload).OfType<RunFinishedPayload>().LastOrDefault(p => p.RunId == run);
            runs.Add(new JsonObject
            {
                ["run"] = run,
                ["startedAt"] = item.Entry.RecordedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["baseCommit"] = started.BaseCommit,
                ["follows"] = started.Follows,
                ["finished"] = finished is null ? null : new JsonObject
                {
                    ["phase"] = finished.Phase.ToString(),
                    ["summary"] = finished.Summary,
                    ["integrationCommit"] = finished.IntegrationCommit,
                },
                ["verdicts"] = new JsonArray([.. items.Select(i => i.Payload).OfType<VerdictIssuedPayload>().Where(v => v.RunId == run).Select(v => (JsonNode)new JsonObject
                {
                    ["verdict"] = v.VerdictId,
                    ["task"] = v.Task?.Value,
                    ["decision"] = v.Decision.ToString(),
                    ["valid"] = v.Valid,
                    ["supersedes"] = v.Supersedes,
                })]),
                ["alerts"] = new JsonArray([.. items.Select(i => i.Payload).OfType<AuditAlertPayload>().Where(a => a.RunId == run).Select(a => (JsonNode)new JsonObject
                {
                    ["code"] = a.Code,
                    ["message"] = a.Message,
                })]),
            });
        }

        return runs;
    }

    /// <summary>Each debate of the log: its mode, its question (read against the hash <c>DebateOpened</c> anchored), its outcome and what it challenges.</summary>
    private async Task<List<JsonNode>> DebatesAsync(string projectId, List<RunItem> items, CancellationToken cancellationToken)
    {
        var debates = new List<JsonNode>();
        foreach (var opened in items.Select(i => i.Payload).OfType<DebateOpenedPayload>().DistinctBy(d => d.RunId))
        {
            var closed = items.Select(i => i.Payload).OfType<DebateClosedPayload>().LastOrDefault(c => c.RunId == opened.RunId);
            debates.Add(new JsonObject
            {
                ["debate"] = opened.RunId,
                ["mode"] = opened.Mode.ToString(),
                ["subject"] = opened.Subject.ToString(),
                ["question"] = await ReadTextAsync(projectId, opened.Question, opened.QuestionSha256, cancellationToken).ConfigureAwait(false),
                ["outcome"] = closed?.Outcome.ToString() ?? "Open",
                ["rule"] = closed?.Rule,
                ["chosenProposal"] = closed?.ChosenProposal,
                ["challenges"] = opened.Challenges,
            });
        }

        return debates;
    }

    /// <summary>The text of an evidence, only when it hashes to what the log anchored; null otherwise.</summary>
    private async Task<string?> ReadTextAsync(string projectId, EvidenceId id, string sha256, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await evidence.OpenContentAsync(projectId, id, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                var bytes = buffer.ToArray();
                return string.Equals(ContentHash.Sha256Hex(bytes), sha256, StringComparison.OrdinalIgnoreCase) ? Encoding.UTF8.GetString(bytes) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or KeyNotFoundException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The documents of the copy, <c>docs/**</c> and the README at its root, with their size and SHA-256, in ordinal order of their path.</summary>
    private static List<JsonNode> Documents(string? snapshot, List<string> checks)
    {
        if (snapshot is null || !Directory.Exists(snapshot))
        {
            return [];
        }

        var root = Path.GetFullPath(snapshot);
        var files = new List<string>();
        var docs = Path.Combine(root, "docs");
        if (Directory.Exists(docs))
        {
            files.AddRange(Directory.EnumerateFiles(docs, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }));
        }

        files.AddRange(Directory.EnumerateFiles(root, "README*", SearchOption.TopDirectoryOnly));
        var documents = new List<JsonNode>();
        foreach (var file in files.Select(f => (Full: f, Relative: Path.GetRelativePath(root, f).Replace('\\', '/'))).OrderBy(f => f.Relative, StringComparer.Ordinal))
        {
            try
            {
                using var stream = new FileStream(file.Full, FileMode.Open, FileAccess.Read, FileShare.Read);
                documents.Add(new JsonObject
                {
                    ["path"] = file.Relative,
                    ["bytes"] = stream.Length,
                    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(stream)),
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                checks.Add($"The document '{file.Relative}' of the copy cannot be read: {ex.Message}");
            }
        }

        return documents;
    }

    private string PackageFolder(string projectId, RunId audit, string headHash)
    {
        var root = Path.GetFullPath(options.DossierRoot);
        var folder = Path.GetFullPath(Path.Combine(root, Segment(projectId, 64), audit.Value, headHash[..Math.Min(HeadPrefixLength, headHash.Length)]));
        if (!folder.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The dossier folder '{folder}' would fall outside the dossier root '{root}'.");
        }

        return folder;
    }

    /// <summary>Refuses a path that goes through a link from the dossier root, included, down to <paramref name="path"/>.</summary>
    private void RefuseLinks(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.DossierRoot));
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (current.LinkTarget is not null || (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)))
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

    private static async Task WriteNewTextAsync(string path, string text, CancellationToken cancellationToken)
    {
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await using (file.ConfigureAwait(false))
        {
            await file.WriteAsync(Utf8NoBom.GetBytes(text), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Copies an evidence into the package, at most the configured bytes, and checks its whole content against its SHA-256.</summary>
    private async Task<DossierEvidenceFile> CopyAsync(string projectId, EvidenceRecord record, string folder, string name, List<string> checks, CancellationToken cancellationToken)
    {
        try
        {
            var source = await evidence.OpenContentAsync(projectId, record.Id, cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var target = new FileStream(Path.Combine(folder, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                long size = 0;
                long written = 0;
                await using (target.ConfigureAwait(false))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        var room = (int)Math.Min(read, options.MaxEvidenceFileBytes - written);
                        if (room > 0)
                        {
                            await target.WriteAsync(buffer.AsMemory(0, room), cancellationToken).ConfigureAwait(false);
                            written += room;
                        }

                        size += read;
                    }
                }

                var verified = string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), record.ContentSha256, StringComparison.Ordinal);
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

    private static string Segment(string text, int maxLength)
    {
        var safe = new string([.. text.Select(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' ? c : '_')]);
        safe = safe.Length > maxLength ? safe[..maxLength] : safe;
        return safe.Length == 0 ? "_" : safe;
    }
}
