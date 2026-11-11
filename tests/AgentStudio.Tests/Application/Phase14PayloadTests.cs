using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Documents;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/17 §8.2 and §9.4 (P0 of Phase 14, D-60, ADR-0057): <c>ClientDocumentExported</c>, the export of a document for clients. The log
/// hashes the text of every payload, so it is pinned here as text, read back byte for byte, and the hash of an entry that carries it is
/// computed by an independent implementation of the canonical form of the log. The payload carries no text of the document, no path of
/// the machine and nothing of what an agent wrote: only hashes, names of files and counts.
/// </summary>
public sealed class Phase14PayloadTests
{
    private const string Commit = "9c4da5d1f2e3a4b5c6d7e8f9a0b1c2d3e4f5a6b7";

    private static readonly string HashC = new('c', 64);

    private static readonly RunId DraftRun = new("aux-20261111-a1b2c3");

    private static readonly DateTimeOffset At = new(2026, 11, 11, 10, 0, 0, TimeSpan.FromHours(1));

    private static readonly ClientDocumentFileSnapshot MarkdownFile = new(ClientDocumentFormat.Markdown, "mi-tienda-hito-1-v2.md", HashB, 4210);

    private static readonly ClientDocumentFileSnapshot WordFile = new(ClientDocumentFormat.Word, "mi-tienda-hito-1-v2.docx", HashC, 18234);

    private static readonly ClientDocumentAgentPartSnapshot SummaryByAgent = new(ClientDocumentSectionKind.Summary, DraftRun, false);

    /// <summary>The second export of «Hito 1»: Markdown and Word, one secret redacted, three paths removed, two risks, a summary by an agent not reviewed.</summary>
    private static readonly ClientDocumentExportedPayload Exported = ClientDocumentExportedPayload.Exporting(
        2, "Hito 1", Commit, "es-ES", HashA, Eid("ev-markdown"), HashB, [MarkdownFile, WordFile], 1, 3, 2, false, [SummaryByAgent], "user");

    /// <summary>One sample of every payload type Phase 14 adds; <see cref="DecisionPayloadTests"/> adds them to the samples of every payload.</summary>
    internal static readonly DecisionPayload[] Samples = [Exported];

    private static ClientDocumentExportedPayload With(
        int version = 2,
        string? commit = Commit,
        string language = "es-ES",
        string? modelSha256 = null,
        string? markdownSha256 = null,
        IReadOnlyList<ClientDocumentFileSnapshot>? files = null,
        int secrets = 1,
        int paths = 3,
        int risks = 2,
        string decidedBy = "user") =>
        ClientDocumentExportedPayload.Exporting(
            version, "Hito 1", commit, language, modelSha256 ?? HashA, Eid("ev-markdown"), markdownSha256 ?? HashB, files ?? [MarkdownFile], secrets, paths, risks, false, [], decidedBy);

    // ---- the text of the payload ----

    [Fact]
    public void An_export_is_written_in_one_fixed_text_outside_every_run_and_read_back_byte_for_byte()
    {
        var json = DecisionPayloadSerializer.Serialize(Exported);

        Assert.Equal(
            $$"""{"version":2,"milestone":"Hito 1","commit":"{{Commit}}","language":"es-ES","modelSha256":"{{HashA}}","markdownEvidence":"ev-markdown","markdownSha256":"{{HashB}}","files":[{"format":"Markdown","fileName":"mi-tienda-hito-1-v2.md","sha256":"{{HashB}}","bytes":4210},{"format":"Word","fileName":"mi-tienda-hito-1-v2.docx","sha256":"{{HashC}}","bytes":18234}],"secretsRedacted":1,"pathsRemoved":3,"risksIncluded":2,"includesConsumption":false,"agentParts":[{"section":"Summary","run":"aux-20261111-a1b2c3","reviewedByPerson":false}],"decidedBy":"user","v":1,"runId":""}""",
            json);
        var read = DecisionPayloadSerializer.Deserialize<ClientDocumentExportedPayload>(json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
        Assert.Equal(typeof(ClientDocumentExportedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.ClientDocumentExported));
        Assert.Equal(DecisionPayload.NoRun, Exported.RunId);
        Assert.Equal(1, Exported.V);
        Assert.Equal(["mi-tienda-hito-1-v2.md", "mi-tienda-hito-1-v2.docx"], read.Files.Select(f => f.FileName));
        Assert.Equal(DraftRun, read.AgentParts.Single().Run);
    }

    [Fact]
    public void A_delivery_without_a_milestone_or_commit_leaves_both_out_and_an_export_without_agent_parts_writes_an_empty_list()
    {
        var payload = ClientDocumentExportedPayload.Exporting(1, null, null, "en", HashA, Eid("ev-markdown"), HashB, [MarkdownFile], 0, 0, 0, true, [], "user");

        var json = DecisionPayloadSerializer.Serialize(payload);

        Assert.DoesNotContain("milestone", json, StringComparison.Ordinal);
        Assert.DoesNotContain("commit", json, StringComparison.Ordinal);
        Assert.Contains("\"includesConsumption\":true,\"agentParts\":[]", json, StringComparison.Ordinal);
        var read = DecisionPayloadSerializer.Deserialize<ClientDocumentExportedPayload>(json);
        Assert.Null(read.Milestone);
        Assert.Null(read.Commit);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
    }

    [Fact]
    public void The_formats_and_sections_are_written_by_name_so_the_order_of_the_enums_never_matters()
    {
        var json = DecisionPayloadSerializer.Serialize(Exported);

        Assert.Contains("\"format\":\"Markdown\"", json, StringComparison.Ordinal);
        Assert.Contains("\"format\":\"Word\"", json, StringComparison.Ordinal);
        Assert.Contains("\"section\":\"Summary\"", json, StringComparison.Ordinal);
        Assert.Throws<System.Text.Json.JsonException>(() => DecisionPayloadSerializer.Deserialize<ClientDocumentExportedPayload>(json.Replace("\"Word\"", "3", StringComparison.Ordinal)));
    }

    /// <summary>No text of the document, no path of the machine and nothing an agent wrote goes in the log (ADR-0010, point 5): hashes, names and counts.</summary>
    [Fact]
    public void An_export_carries_no_text_of_the_document_and_the_paths_removed_are_only_counted()
    {
        var names = typeof(ClientDocumentExportedPayload).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(names, n => n is "Text" or "Content" or "Body" or "Paragraphs" or "Summary" or "Output" or "Prompt" or "Path" or "Paths" or "Directory" or "Destination" or "Transcript");
        Assert.Equal(typeof(int), typeof(ClientDocumentExportedPayload).GetProperty(nameof(ClientDocumentExportedPayload.PathsRemoved))!.PropertyType);
        Assert.Equal(typeof(string), typeof(ClientDocumentFileSnapshot).GetProperty(nameof(ClientDocumentFileSnapshot.FileName))!.PropertyType);
        Assert.DoesNotContain(typeof(ClientDocumentAgentPartSnapshot).GetProperties(), p => p.PropertyType == typeof(string));
    }

    // ---- what the log refuses ----

    [Fact]
    public void An_export_names_everything_by_a_valid_value_and_refuses_the_rest()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => With(version: 0));
        Assert.Throws<ArgumentException>(() => With(commit: "HEAD"));
        Assert.Throws<ArgumentException>(() => With(language: "castellano"));
        Assert.Throws<ArgumentException>(() => With(modelSha256: "abc"));
        Assert.Throws<ArgumentException>(() => With(markdownSha256: HashA.ToUpperInvariant()));
        Assert.Throws<ArgumentException>(() => With(files: []));
        Assert.Throws<ArgumentOutOfRangeException>(() => With(secrets: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => With(paths: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => With(risks: -1));
        Assert.Throws<ArgumentException>(() => With(decidedBy: " "));
        Assert.Throws<ArgumentNullException>(() => ClientDocumentExportedPayload.Exporting(1, null, null, "es-ES", HashA, Eid("e"), HashB, null!, 0, 0, 0, false, [], "user"));
        Assert.Throws<ArgumentNullException>(() => ClientDocumentExportedPayload.Exporting(1, null, null, "es-ES", HashA, Eid("e"), HashB, [MarkdownFile], 0, 0, 0, false, null!, "user"));
    }

    [Fact]
    public void The_milestone_is_held_to_the_rules_of_the_rest_of_the_text()
    {
        ClientDocumentExportedPayload Milestone(string? name) =>
            ClientDocumentExportedPayload.Exporting(1, name, null, "es-ES", HashA, Eid("ev-markdown"), HashB, [MarkdownFile], 0, 0, 0, false, [], "user");

        Assert.Equal("Hito 1", Milestone("Hito 1").Milestone);
        Assert.Null(Milestone(null).Milestone);
        Assert.Throws<ArgumentException>(() => Milestone(" "));
        Assert.Throws<ArgumentException>(() => Milestone(@"Hito en C:\Users\ana\tienda"));
        Assert.Throws<ArgumentException>(() => Milestone("Hito\u202E1"));
    }

    [Fact]
    public void The_agent_parts_of_the_record_come_from_the_document_whatever_the_list_of_what_goes_out_says()
    {
        var hidden = Request() with { Build = new ClientDocumentBuild(Document(), new ClientDocumentDisclosure(1, [], [], 2, false)) };
        var result = new ClientDocumentExportResult(2, [new ExportedClientFile(ClientDocumentFormat.Markdown, "mi-tienda-hito-1-v2.md", HashB, 10)], Eid("ev-markdown"), HashB);

        Assert.Equal([SummaryByAgent], ClientDocumentExportedPayload.Of(hidden, result).AgentParts);
    }

    [Fact]
    public void Each_file_is_a_plain_name_with_the_extension_of_its_format_a_fingerprint_and_a_size_and_there_is_one_per_format()
    {
        Assert.Throws<ArgumentException>(() => With(files: [MarkdownFile with { FileName = @"C:\Entregas\mi-tienda-v2.md" }]));
        Assert.Throws<ArgumentException>(() => With(files: [MarkdownFile with { FileName = "../mi-tienda-v2.md" }]));
        Assert.Throws<ArgumentException>(() => With(files: [MarkdownFile with { FileName = "mi-tienda-v2.docx" }]));
        Assert.Throws<ArgumentException>(() => With(files: [MarkdownFile with { Sha256 = "abc" }]));
        Assert.Throws<ArgumentException>(() => With(files: [MarkdownFile with { Bytes = 0 }]));
        Assert.Throws<ArgumentException>(() => With(files: [MarkdownFile with { Format = (ClientDocumentFormat)9 }]));
        Assert.Throws<ArgumentException>(() => With(files: [MarkdownFile, MarkdownFile with { FileName = "otro-v2.md" }]));
        Assert.Equal(2, With(files: [MarkdownFile, WordFile]).Files.Count);
    }

    // ---- from the request the person approved and what the exporter did ----

    private static readonly ProjectContext Project = new("6f1c2b0e-0000-4000-8000-0000000000ab", @"C:\repo");

    private static ClientDocument Document() => new(
        new ClientDocumentCover("Mi tienda", "Hito 1", At, 2, Commit),
        [
            new(ClientDocumentSectionKind.Summary, [new ClientParagraph(ClientText.ByAgent("Resumen.", new ClientAgentMark(DraftRun, false)))]),
            new(ClientDocumentSectionKind.RisksAndPending, [new ClientList(false, [ClientText.Written("R4: el redondeo queda sin probar"), ClientText.Written("R5: sin datos de carga")])]),
        ]);

    private static ClientDocumentExportRequest Request(ClientDocument? document = null) =>
        new(
            Project,
            new ClientDocumentBuild(document ?? Document(), ClientDocumentDisclosure.Of(document ?? Document(), 1, [@"C:\Users\ana\repo", @"C:\Users\ana\otro", "~/x"], 2, false)),
            [ClientDocumentFormat.Markdown, ClientDocumentFormat.Word],
            "es-ES",
            @"C:\Entregas\tienda",
            "user");

    [Fact]
    public void The_payload_of_an_export_is_made_from_the_request_and_the_result_so_the_two_cannot_disagree()
    {
        var request = Request();
        var result = new ClientDocumentExportResult(
            2,
            [new ExportedClientFile(ClientDocumentFormat.Markdown, "mi-tienda-hito-1-v2.md", HashB, 4210), new ExportedClientFile(ClientDocumentFormat.Word, "mi-tienda-hito-1-v2.docx", HashC, 18234)],
            Eid("ev-markdown"),
            HashB);

        var payload = ClientDocumentExportedPayload.Of(request, result);

        Assert.Equal(request.Build.Document.Sha256(), payload.ModelSha256);
        Assert.Equal(DecisionPayloadSerializer.Serialize(Exported with { ModelSha256 = request.Build.Document.Sha256() }), DecisionPayloadSerializer.Serialize(payload));
        Assert.Equal(3, payload.PathsRemoved);
        Assert.DoesNotContain("Users", DecisionPayloadSerializer.Serialize(payload), StringComparison.Ordinal);
        Assert.Equal(DecisionPayload.NoRun, payload.RunId);
    }

    [Fact]
    public void The_version_exported_is_the_one_of_the_cover()
    {
        var result = new ClientDocumentExportResult(3, [new ExportedClientFile(ClientDocumentFormat.Markdown, "mi-tienda-hito-1-v3.md", HashB, 10)], Eid("ev-markdown"), HashB);

        Assert.Throws<ArgumentException>(() => ClientDocumentExportedPayload.Of(Request(), result));
        Assert.Throws<ArgumentNullException>(() => ClientDocumentExportedPayload.Of(null!, result));
        Assert.Throws<ArgumentNullException>(() => ClientDocumentExportedPayload.Of(Request(), null!));
    }

    // ---- the hash of the entry, by an independent implementation of the canonical form ----

    /// <summary>The hash of a <c>ClientDocumentExported</c> entry against an independent implementation of the canonical form, and a literal computed outside the repository.</summary>
    [Fact]
    public void The_entry_of_an_export_hashes_as_the_canonical_form_of_the_log_says()
    {
        var log = new DecisionLog();
        var payload = DecisionPayloadSerializer.Serialize(Exported);
        const string Summary = "Exporta el documento para clientes: versión 2 de «Hito 1» (Markdown, Word)";

        var exported = log.Append(T0, new Actor(ActorKind.User, "user"), DecisionKind.ClientDocumentExported, Summary, payload);

        Assert.Equal(Independent(1, T0, "User", "user", "ClientDocumentExported", Summary, payload, DecisionLog.GenesisHash), exported.Hash);
        Assert.Equal("0cb23de13652112f8a1fd20840ab56fb64754f7de2e815acbc1e56829b374028", exported.Hash);
        Assert.True(DecisionLog.Load(log.Entries).Verify().IsValid);
    }

    private static string Independent(long sequence, DateTimeOffset at, string actorKind, string actorId, string kind, string summary, string payload, string previous)
    {
        var text = new StringBuilder();
        foreach (var field in new[] { sequence.ToString(CultureInfo.InvariantCulture), at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), actorKind, actorId, kind, summary, payload, string.Empty, previous })
        {
            text.Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field).Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
