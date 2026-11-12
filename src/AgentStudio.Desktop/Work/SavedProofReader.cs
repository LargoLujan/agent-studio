using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Common;

namespace AgentStudio.Desktop.Work;

/// <summary>
/// Opens a saved proof (a diff, a test run, the report of a milestone) from the evidence store, as the room of a debate does: its
/// content is read whole, compared with the fingerprint the store keeps, and shown only when it is the same; a proof that does not
/// match is said so and its text is never shown. What it returns is the <see cref="EvidenceViewer"/> the rest of the app already knows.
/// </summary>
public static class SavedProofReader
{
    /// <summary>The most bytes of a proof the screen shows; a bigger one says so.</summary>
    public const int MaxBytes = 256 * 1024;

    public static async Task<EvidenceViewer> ReadAsync(IEvidenceStore store, ProjectContext project, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(project);
        EvidenceId evidenceId;
        try
        {
            evidenceId = new EvidenceId(id);
        }
        catch (ArgumentException)
        {
            return Missing(id);
        }

        var records = await store.ListAsync(project.ProjectId, cancellationToken).ConfigureAwait(false);
        if (records.FirstOrDefault(r => r.Id == evidenceId) is not { } record)
        {
            return Missing(id);
        }

        var description = $"{EvidenceText.Kind(record.Kind)} · {EvidenceText.Origin(record.Origin)}{(record.SubjectCommit is { } commit ? " · " + Texts.Format("Debate_Evidence_Commit", DebateTexts.Short(commit)) : string.Empty)}";
        byte[] bytes;
        await using (var stream = await store.OpenContentAsync(project.ProjectId, evidenceId, cancellationToken).ConfigureAwait(false))
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            bytes = copy.ToArray();
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actual, record.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new EvidenceViewer(id, record.Title, description, Texts.Format("Debate_Evidence_HashMismatch", DebateTexts.Short(actual), DebateTexts.Short(record.ContentSha256)), Tone.Error, null);
        }

        var same = Texts.Format("Debate_Evidence_HashOk", DebateTexts.Short(actual));
        return new EvidenceViewer(id, record.Title, description, same, Tone.Success, bytes.Length > MaxBytes ? Texts.Get("Debate_Evidence_TooBig") : Encoding.UTF8.GetString(bytes));
    }

    private static EvidenceViewer Missing(string id) => new(id, id, Texts.Get("Debate_Evidence_Missing"), string.Empty, Tone.Warning, null);
}
