using System.Globalization;
using System.Text;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Application.Documents;

// Phase 14 contracts of the documentation for clients (docs/17 §5.5, D-58 to D-60; ADR-0057). Who does what:
// - WP-14D, Memory/Documents: the builder (IClientDocumentBuilder) that makes the model from the verified log, the memory of trust
//   and the reports, deterministically, and the writers of Markdown and HTML (IClientDocumentFormatWriter, no new library).
// - WP-14D, Infrastructure/Documents/Docx: the writer of Word, with the Open XML SDK (DocumentFormat.OpenXml, MIT).
// - WP-14D, Desktop/Documents: the preview, the writer of PDF (WebView2 PrintToPdfAsync), IUserTextRenderer over the resources of
//   the desktop, the exporter's composition and the «Aprobar y exportar» screen.
// - WP-14C or WP-14D, Application/Documents: IClientDocumentExporter and IClientDocumentDrafter over AuxiliaryAgentRunner.
// Only contracts and their pure rules are here: no builder, no writer, no file.

/// <summary>The four formats of a document for clients (docs/17 D-59), all from the same model.</summary>
public enum ClientDocumentFormat
{
    /// <summary>Markdown, written by Agent Studio from the model. It is also what stays as evidence of every export.</summary>
    Markdown,

    /// <summary>HTML, written by Agent Studio from the model.</summary>
    Html,

    /// <summary>PDF: the HTML printed with WebView2, one document at a time.</summary>
    Pdf,

    /// <summary>Word (.docx), written with the Open XML SDK.</summary>
    Word,
}

/// <summary>What each format is as a file.</summary>
public static class ClientDocumentFormats
{
    /// <summary>The four formats, in the order the screen lists them.</summary>
    public static IReadOnlyList<ClientDocumentFormat> All { get; } = [ClientDocumentFormat.Markdown, ClientDocumentFormat.Html, ClientDocumentFormat.Pdf, ClientDocumentFormat.Word];

    /// <summary>The extension of the file, with its dot.</summary>
    public static string Extension(ClientDocumentFormat format) => format switch
    {
        ClientDocumentFormat.Markdown => ".md",
        ClientDocumentFormat.Html => ".html",
        ClientDocumentFormat.Pdf => ".pdf",
        ClientDocumentFormat.Word => ".docx",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown format of a document for clients."),
    };

    /// <summary>The media type of the file.</summary>
    public static string MediaType(ClientDocumentFormat format) => format switch
    {
        ClientDocumentFormat.Markdown => "text/markdown",
        ClientDocumentFormat.Html => "text/html",
        ClientDocumentFormat.Pdf => "application/pdf",
        ClientDocumentFormat.Word => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown format of a document for clients."),
    };
}

/// <summary>
/// The sections of the document after its cover (docs/17 D-58), in the order they appear. The cover is structured (<see cref="ClientDocumentCover"/>),
/// and «Lo que no va» is not a section but a rule (<see cref="ClientDocumentRules"/>).
/// </summary>
public enum ClientDocumentSectionKind
{
    /// <summary>A summary in plain language: a template with the figures of the system or, if asked, written by an agent and marked.</summary>
    Summary,

    /// <summary>The goal and each requirement with its state (implemented, reviewed, integrated).</summary>
    Scope,

    /// <summary>The tests (the counters the system captured from the TRX, their commands and date) and the verdict of the judge requirement by requirement.</summary>
    Verification,

    /// <summary>The decisions of the memory of trust and the ADR of the repository, if there are any.</summary>
    Decisions,

    /// <summary>The commits and, from Phase 10, the pull requests; the files changed by area.</summary>
    Changes,

    /// <summary>Requirements not satisfied, open issues, known errors, alerts of the auditor, challenges and residuals that apply.</summary>
    RisksAndPending,

    /// <summary>The index of evidence with its fingerprint, a glossary and, if asked, an explanation of use written by an agent.</summary>
    Annexes,
}

/// <summary>The sections of a document, in order, and the code of each title.</summary>
public static class ClientDocumentSections
{
    /// <summary>The sections in the order they appear.</summary>
    public static IReadOnlyList<ClientDocumentSectionKind> Order { get; } =
    [
        ClientDocumentSectionKind.Summary,
        ClientDocumentSectionKind.Scope,
        ClientDocumentSectionKind.Verification,
        ClientDocumentSectionKind.Decisions,
        ClientDocumentSectionKind.Changes,
        ClientDocumentSectionKind.RisksAndPending,
        ClientDocumentSectionKind.Annexes,
    ];

    /// <summary>The code of the title of a section, painted in the language of the document (<see cref="UserText.ResourceKey"/>).</summary>
    public static string TitleCode(ClientDocumentSectionKind kind) => kind switch
    {
        ClientDocumentSectionKind.Summary => "document.section-summary",
        ClientDocumentSectionKind.Scope => "document.section-scope",
        ClientDocumentSectionKind.Verification => "document.section-verification",
        ClientDocumentSectionKind.Decisions => "document.section-decisions",
        ClientDocumentSectionKind.Changes => "document.section-changes",
        ClientDocumentSectionKind.RisksAndPending => "document.section-risks",
        ClientDocumentSectionKind.Annexes => "document.section-annexes",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown section of a document for clients."),
    };
}

/// <summary>Who wrote a text of the document.</summary>
public enum ClientTextSource
{
    /// <summary>The system: a code with arguments, painted in the language chosen at export (the headings, the states, the glossary, the sentences of the template).</summary>
    System,

    /// <summary>A person, or the anchored records quoted as they are (a requirement, the subject of a commit, the title of a decision): it goes in its own language, never translated.</summary>
    Written,

    /// <summary>An agent, with the run that wrote it and whether the person reviewed it: the generators show it marked «Redactado por un agente · revisado por ti: sí o no».</summary>
    Agent,
}

/// <summary>The mark of what an agent wrote (docs/17 D-59): the auxiliary run (or the judge's run) it comes from, and whether the person reviewed it.</summary>
public sealed record ClientAgentMark(RunId Run, bool ReviewedByPerson);

/// <summary>
/// A text of the document, by who wrote it (docs/17 D-58 and D-59). Text of the system is a code with arguments, so the model has no
/// language: the same model gives its Markdown, HTML, PDF and Word in any language chosen at export, and the only thing that varies
/// is the rendering of the codes (<see cref="IUserTextRenderer"/>). Text of a person or of an agent is verbatim. An agent's text
/// cannot lose its mark: the factory is the only way to make one.
/// </summary>
public sealed record ClientText
{
    private ClientText(ClientTextSource source, UserText? system, string? value, ClientAgentMark? mark)
    {
        Source = source;
        System = system;
        Value = value;
        Mark = mark;
    }

    /// <summary>Who wrote it.</summary>
    public ClientTextSource Source { get; }

    /// <summary>The code and arguments of the sentence, for <see cref="ClientTextSource.System"/>; otherwise null.</summary>
    public UserText? System { get; }

    /// <summary>The text itself, for <see cref="ClientTextSource.Written"/> and <see cref="ClientTextSource.Agent"/>; otherwise null.</summary>
    public string? Value { get; }

    /// <summary>The mark of an agent's text; otherwise null.</summary>
    public ClientAgentMark? Mark { get; }

    /// <summary>A sentence of the system, as a code.</summary>
    public static ClientText OfSystem(UserText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ClientText(ClientTextSource.System, text, null, null);
    }

    /// <summary>A sentence of the system, from its code and arguments.</summary>
    public static ClientText OfSystem(string code, params object[] arguments) => OfSystem(new UserText(code, arguments));

    /// <summary>Text of a person, or taken as it is from the anchored records.</summary>
    /// <exception cref="ArgumentException">The text is blank.</exception>
    public static ClientText Written(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new ClientText(ClientTextSource.Written, null, value, null);
    }

    /// <summary>Text of an agent, with its mark.</summary>
    /// <exception cref="ArgumentException">The text is blank.</exception>
    public static ClientText ByAgent(string value, ClientAgentMark mark)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentNullException.ThrowIfNull(mark);
        return new ClientText(ClientTextSource.Agent, null, value, mark);
    }

    /// <summary>The fields of the canonical form of this text (<see cref="ClientDocument.Sha256"/>): its source, then its content.</summary>
    internal IEnumerable<string> CanonicalFields()
    {
        yield return Source.ToString();
        if (System is { } system)
        {
            yield return system.Code;
            yield return system.Arguments.Count.ToString(CultureInfo.InvariantCulture);
            foreach (var argument in system.Arguments)
            {
                yield return Argument(argument);
            }
        }
        else
        {
            yield return Value ?? string.Empty;
            if (Mark is { } mark)
            {
                yield return mark.Run.Value;
                yield return mark.ReviewedByPerson ? "reviewed" : "not-reviewed";
            }
        }
    }

    private static string Argument(object argument) => argument switch
    {
        string text => "s:" + text,
        int number => "n:" + number.ToString(CultureInfo.InvariantCulture),
        long number => "n:" + number.ToString(CultureInfo.InvariantCulture),
        decimal number => "m:" + number.ToString(CultureInfo.InvariantCulture),
        double number => "d:" + number.ToString("R", CultureInfo.InvariantCulture),
        DateTimeOffset instant => "t:" + instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        TimeSpan span => "u:" + span.ToString("c", CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException("A coded text carries only text, numbers, instants and durations."),
    };
}

/// <summary>A block of a section (docs/17 D-58: paragraphs, tables, lists and references to evidence).</summary>
public abstract record ClientDocumentBlock;

/// <summary>A paragraph.</summary>
public sealed record ClientParagraph(ClientText Text) : ClientDocumentBlock;

/// <summary>A list, bulleted or numbered.</summary>
public sealed record ClientList(bool Ordered, IReadOnlyList<ClientText> Items) : ClientDocumentBlock;

/// <summary>A table: a header row and rows with as many cells as the header.</summary>
public sealed record ClientTable(IReadOnlyList<ClientText> Header, IReadOnlyList<IReadOnlyList<ClientText>> Rows) : ClientDocumentBlock;

/// <summary>A reference to a piece of evidence: its id, its SHA-256 (the «huella» of the annex) and what it is.</summary>
public sealed record ClientEvidenceReference(EvidenceId Evidence, string Sha256, ClientText Label);

/// <summary>A list of references to evidence, with their fingerprints.</summary>
public sealed record ClientEvidenceList(IReadOnlyList<ClientEvidenceReference> References) : ClientDocumentBlock;

/// <summary>A section of the document: its kind (its title is <see cref="ClientDocumentSections.TitleCode"/>) and its blocks.</summary>
public sealed record ClientDocumentSection(ClientDocumentSectionKind Kind, IReadOnlyList<ClientDocumentBlock> Blocks);

/// <summary>
/// The cover (docs/17 D-58): the project, the milestone (its name, docs/16 D-49; null for a delivery without one), the date it was
/// made, the version of the document (each export is one: <see cref="Decisions.ClientDocumentExportedPayload"/>) and the commit.
/// </summary>
public sealed record ClientDocumentCover(string Project, string? Milestone, DateTimeOffset GeneratedAt, int Version, string? Commit);

/// <summary>
/// The model of a document for clients (docs/17 D-58): a cover and sections of paragraphs, lists, tables and references to evidence,
/// built by the system and in no language (system text is a code). The same inputs always give the same model, hence the same
/// <see cref="Sha256"/>; the four formats are written from it. What an agent wrote is marked (<see cref="ClientText.Mark"/>).
/// </summary>
public sealed record ClientDocument(ClientDocumentCover Cover, IReadOnlyList<ClientDocumentSection> Sections)
{
    /// <summary>The identifier of the canonical form: a change in how it is written bumps it, so an old hash is never mistaken for a new one.</summary>
    public const string CanonicalVersion = "agent-studio/client-document/1";

    /// <summary>Every text of the document, with the section it is in, in reading order (the cover has none).</summary>
    public IEnumerable<(ClientDocumentSectionKind Section, ClientText Text)> Texts()
    {
        foreach (var section in Sections ?? [])
        {
            foreach (var block in section.Blocks ?? [])
            {
                foreach (var text in TextsOf(block))
                {
                    yield return (section.Kind, text);
                }
            }
        }
    }

    /// <summary>
    /// The SHA-256 of the canonical form of the model (length-prefixed fields, the instant in UTC, numbers with the invariant
    /// culture): the same model always has the same hash, on any machine and in any culture. <c>ClientDocumentExported</c> records it.
    /// </summary>
    public string Sha256()
    {
        var fields = new List<string?>
        {
            CanonicalVersion,
            Cover.Project,
            Cover.Milestone,
            Cover.GeneratedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Cover.Version.ToString(CultureInfo.InvariantCulture),
            Cover.Commit,
        };
        foreach (var section in Sections)
        {
            fields.Add("section");
            fields.Add(section.Kind.ToString());
            foreach (var block in section.Blocks)
            {
                AddBlock(fields, block);
            }
        }

        return ContentHash.Sha256Hex(ContentHash.Canonical([.. fields]));
    }

    private static IEnumerable<ClientText> TextsOf(ClientDocumentBlock block)
    {
        switch (block)
        {
            case ClientParagraph paragraph:
                yield return paragraph.Text;
                break;
            case ClientList list:
                foreach (var item in list.Items)
                {
                    yield return item;
                }

                break;
            case ClientTable table:
                foreach (var cell in table.Header.Concat(table.Rows.SelectMany(r => r)))
                {
                    yield return cell;
                }

                break;
            case ClientEvidenceList evidence:
                foreach (var reference in evidence.References)
                {
                    yield return reference.Label;
                }

                break;
        }
    }

    private static void AddBlock(List<string?> fields, ClientDocumentBlock block)
    {
        void Text(ClientText text)
        {
            fields.AddRange(text.CanonicalFields());
        }

        switch (block)
        {
            case ClientParagraph paragraph:
                fields.Add("paragraph");
                Text(paragraph.Text);
                break;
            case ClientList list:
                fields.Add(list.Ordered ? "ordered-list" : "list");
                fields.Add(list.Items.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var item in list.Items)
                {
                    Text(item);
                }

                break;
            case ClientTable table:
                fields.Add("table");
                fields.Add(table.Header.Count.ToString(CultureInfo.InvariantCulture));
                fields.Add(table.Rows.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var cell in table.Header.Concat(table.Rows.SelectMany(r => r)))
                {
                    Text(cell);
                }

                break;
            case ClientEvidenceList evidence:
                fields.Add("evidence");
                fields.Add(evidence.References.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var reference in evidence.References)
                {
                    fields.Add(reference.Evidence.Value);
                    fields.Add(reference.Sha256);
                    Text(reference.Label);
                }

                break;
            default:
                throw new InvalidOperationException($"{block.GetType().Name} is not a block of a document for clients.");
        }
    }
}

/// <summary>
/// The rules the model of a document keeps before it is shown or written (docs/17 D-58, «Lo que no va»). Prompts, transcripts of the
/// agents, the journal and the consumption (unless ticked) are never put in the model by the builder; what these rules check is
/// what can be told from the model alone: no path of the machine and no hidden character in what people or agents wrote, sections in
/// order, well-formed blocks and fingerprints that are SHA-256.
/// </summary>
public static class ClientDocumentRules
{
    /// <summary>
    /// Every rule the model breaks, as stable codes, in a fixed order; an empty list means it may be shown to the person for review.
    /// No text of the document is in a code.
    /// </summary>
    public static IReadOnlyList<string> Validate(ClientDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var problems = new List<string>();
        void Add(string code)
        {
            if (!problems.Contains(code))
            {
                problems.Add(code);
            }
        }

        var cover = document.Cover;
        if (cover is null)
        {
            Add("document.cover-required");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(cover.Project))
            {
                Add("document.cover-project-required");
            }

            if (cover.Version < 1)
            {
                Add("document.cover-version-invalid");
            }

            if (cover.Commit is not null && !IsCommit(cover.Commit))
            {
                Add("document.cover-commit-invalid");
            }

            foreach (var text in new[] { cover.Project, cover.Milestone })
            {
                if (LocalPaths.Contains(text))
                {
                    Add("document.local-path");
                }

                if (PlainText.HasHiddenCharacter(text, allowLineBreaks: false))
                {
                    Add("document.hidden-character");
                }
            }
        }

        var sections = document.Sections ?? [];
        var positions = sections.Select(s => ClientDocumentSections.Order.ToList().IndexOf(s.Kind)).ToList();
        if (positions.Any(p => p < 0) || positions.Distinct().Count() != positions.Count || !positions.SequenceEqual(positions.Order()))
        {
            Add("document.sections-order");
        }

        foreach (var section in sections)
        {
            if ((section.Blocks ?? []).Count == 0)
            {
                Add("document.section-empty");
            }

            foreach (var block in section.Blocks ?? [])
            {
                CheckBlock(block, Add);
            }
        }

        foreach (var (_, text) in document.Texts())
        {
            if (text.Source == ClientTextSource.System)
            {
                continue;
            }

            if (LocalPaths.Contains(text.Value))
            {
                Add("document.local-path");
            }

            if (PlainText.HasHiddenCharacter(text.Value, allowLineBreaks: true))
            {
                Add("document.hidden-character");
            }
        }

        return problems;
    }

    /// <summary>Whether <paramref name="value"/> is a commit id: 7 to 64 hexadecimal characters, lowercase.</summary>
    public static bool IsCommit(string? value) =>
        value is { Length: >= 7 and <= 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static void CheckBlock(ClientDocumentBlock block, Action<string> add)
    {
        switch (block)
        {
            case ClientParagraph:
                break;
            case ClientList list:
                if ((list.Items ?? []).Count == 0)
                {
                    add("document.list-empty");
                }

                break;
            case ClientTable table:
                var header = table.Header ?? [];
                if (header.Count == 0 || (table.Rows ?? []).Any(r => r.Count != header.Count))
                {
                    add("document.table-malformed");
                }

                break;
            case ClientEvidenceList evidence:
                if ((evidence.References ?? []).Any(r => !ContentHash.IsSha256Hex(r.Sha256)))
                {
                    add("document.evidence-fingerprint-invalid");
                }

                break;
            default:
                add("document.block-unknown");
                break;
        }
    }
}

/// <summary>An optional part written by an agent (docs/17 D-59): the summary, or an explanation of use.</summary>
public enum ClientDraftKind
{
    /// <summary>The summary of the first section, written by an agent instead of the template.</summary>
    Summary,

    /// <summary>An explanation of use, in the annexes.</summary>
    UsageExplanation,
}

/// <summary>Where each kind of draft goes in the document.</summary>
public static class ClientDraftKinds
{
    /// <summary>The section a draft of <paramref name="kind"/> goes in.</summary>
    public static ClientDocumentSectionKind SectionOf(ClientDraftKind kind) => kind switch
    {
        ClientDraftKind.Summary => ClientDocumentSectionKind.Summary,
        ClientDraftKind.UsageExplanation => ClientDocumentSectionKind.Annexes,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of draft of a document for clients."),
    };
}

/// <summary>The limits of what an agent drafts for a document (docs/17 D-59), which a strict schema cannot say.</summary>
public static class ClientDocumentLimits
{
    /// <summary>The most paragraphs of one draft.</summary>
    public const int MaxDraftParagraphs = 6;

    /// <summary>The longest paragraph of a draft.</summary>
    public const int MaxDraftParagraphLength = 800;
}

/// <summary>What the system keeps of what an agent drafted: the paragraphs that pass, and what was left out and why (in English).</summary>
public sealed record ClientDraftCheck(IReadOnlyList<string> Paragraphs, IReadOnlyList<string> Discarded);

/// <summary>What the system keeps of the draft of an agent (docs/17 D-59): never an error for the agent's text, only what is left out.</summary>
public static class ClientDraftRules
{
    /// <summary>
    /// The paragraphs cleaned of hidden characters, trimmed, cut to <see cref="ClientDocumentLimits.MaxDraftParagraphLength"/>, at
    /// most <see cref="ClientDocumentLimits.MaxDraftParagraphs"/>; a paragraph with a path of the machine is left out whole, because
    /// a document for clients never has one.
    /// </summary>
    public static ClientDraftCheck Check(IEnumerable<string?>? paragraphs)
    {
        var kept = new List<string>();
        var discarded = new List<string>();
        foreach (var paragraph in paragraphs ?? [])
        {
            var clean = PlainText.Strip(paragraph, keepLineBreaks: true).Trim();
            if (clean.Length == 0)
            {
                continue;
            }

            if (kept.Count >= ClientDocumentLimits.MaxDraftParagraphs)
            {
                discarded.Add($"a paragraph is one more than {ClientDocumentLimits.MaxDraftParagraphs}.");
                continue;
            }

            if (LocalPaths.Contains(clean))
            {
                discarded.Add("a paragraph names a path of the machine.");
                continue;
            }

            if (clean.Length > ClientDocumentLimits.MaxDraftParagraphLength)
            {
                clean = clean[..ClientDocumentLimits.MaxDraftParagraphLength].TrimEnd();
                discarded.Add($"a paragraph is longer than {ClientDocumentLimits.MaxDraftParagraphLength} characters: it was cut.");
            }

            kept.Add(clean);
        }

        return new ClientDraftCheck(kept, discarded);
    }
}

/// <summary>A draft the person accepted into the document: its kind, its paragraphs (already checked), the run that wrote it and whether the person reviewed it.</summary>
public sealed record ClientAgentDraft(ClientDraftKind Kind, IReadOnlyList<string> Paragraphs, RunId Run, bool ReviewedByPerson);

/// <summary>
/// What the system builds a document from (docs/17 D-58): the open project and, for a milestone, its name; «el consumo» only when
/// ticked, and the drafts of agents the person accepted. The language is not here: the model has none.
/// </summary>
public sealed record ClientDocumentBuildRequest(
    ProjectContext Project,
    string? Milestone,
    bool IncludeConsumption,
    IReadOnlyList<ClientAgentDraft> AgentDrafts);

/// <summary>One part of the document an agent wrote, as the list of what goes out says it (docs/17 D-60).</summary>
public sealed record ClientAgentPart(ClientDocumentSectionKind Section, RunId Run, bool ReviewedByPerson);

/// <summary>
/// The list of what goes out (docs/17 D-60), shown to the person next to the preview: how many secrets the redactor took out, which
/// paths of the machine were removed (the person's own, shown to them and never written to the log: <c>ClientDocumentExported</c>
/// keeps only how many), which parts agents wrote and whether the person reviewed them, how many risks go, and whether the
/// consumption goes (it never does unless the person ticked it).
/// </summary>
public sealed record ClientDocumentDisclosure(
    int SecretsRedacted,
    IReadOnlyList<string> PathsRemoved,
    IReadOnlyList<ClientAgentPart> AgentWrittenParts,
    int RisksIncluded,
    bool IncludesConsumption)
{
    /// <summary>
    /// The list for <paramref name="document"/>: the agent-written parts come from the document itself, so the list can never
    /// disagree with it; the secrets, the paths, the risks and the consumption are what the builder counted and decided.
    /// </summary>
    public static ClientDocumentDisclosure Of(ClientDocument document, int secretsRedacted, IReadOnlyList<string> pathsRemoved, int risksIncluded, bool includesConsumption)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pathsRemoved);
        var parts = document.Texts()
            .Where(t => t.Text.Source == ClientTextSource.Agent)
            .Select(t => new ClientAgentPart(t.Section, t.Text.Mark!.Run, t.Text.Mark.ReviewedByPerson))
            .Distinct()
            .ToList();
        return new ClientDocumentDisclosure(secretsRedacted, pathsRemoved, parts, risksIncluded, includesConsumption);
    }

    /// <summary>The rules the list keeps: no negative count.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (SecretsRedacted < 0 || RisksIncluded < 0)
        {
            problems.Add("document.disclosure-count-negative");
        }

        return problems;
    }
}

/// <summary>What the builder gives for the review (docs/17 D-60): the model, and the list of what goes out.</summary>
public sealed record ClientDocumentBuild(ClientDocument Document, ClientDocumentDisclosure Disclosure);

/// <summary>
/// Builds the model of the document (docs/17 D-58) from the verified log, the memory of trust and the reports, deterministically: the
/// same project in the same state gives the same model. The version is one more than the highest the log records for the same
/// milestone (or for the project's deliveries without one). A log that does not verify fails closed
/// (<see cref="Decisions.DecisionLogIntegrityException"/>), as the memory does.
/// </summary>
public interface IClientDocumentBuilder
{
    Task<ClientDocumentBuild> BuildAsync(ClientDocumentBuildRequest request, CancellationToken ct);
}

/// <summary>
/// Paints the sentences of the system (codes with arguments) in a language (docs/17 D-47 and D-59). The writers of Markdown, HTML and
/// Word live below the desktop and cannot read its resources: this port is how they get the text, with numbers and dates in the
/// culture of the language. A missing text comes in Spanish of Spain, the source.
/// </summary>
public interface IUserTextRenderer
{
    /// <param name="text">The sentence, as a code with arguments.</param>
    /// <param name="language">The language (<see cref="LanguageTags"/>): the one chosen at export, not necessarily the one of the interface.</param>
    string Render(UserText text, string language);
}

/// <summary>
/// Writes the model in one format (docs/17 D-59): Markdown and HTML in Memory, Word in Infrastructure and PDF in Desktop. A writer is
/// pure with respect to the model: the same model and language give the same bytes (the PDF, from the same HTML). What an agent wrote
/// is shown with its mark, whatever the format.
/// </summary>
public interface IClientDocumentFormatWriter
{
    /// <summary>The format it writes.</summary>
    ClientDocumentFormat Format { get; }

    /// <summary>The bytes of the file.</summary>
    /// <exception cref="ArgumentException">The model breaks <see cref="ClientDocumentRules"/> or the language is not valid.</exception>
    Task<byte[]> RenderAsync(ClientDocument document, string language, CancellationToken ct);
}

/// <summary>
/// The names of the files of an export (docs/17 D-60). Every export is a version and the earlier one stays, so the version is part
/// of the name and no export ever overwrites another.
/// </summary>
public static class ClientDocumentFiles
{
    private const int MaxSlugLength = 40;

    private const int MaxFileNameLength = 120;

    private static readonly char[] Forbidden = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// <c>&lt;project&gt;[-&lt;milestone&gt;]-v&lt;version&gt;&lt;extension&gt;</c>: the names in lowercase ASCII letters, digits and
    /// hyphens, without accents (<c>mi-tienda-hito-1-v2.docx</c>). A name that leaves nothing is <c>documento</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The version is below 1, or the format is unknown.</exception>
    public static string FileName(string project, string? milestone, int version, ClientDocumentFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        var name = Slug(project);
        if (name.Length == 0)
        {
            name = "documento";
        }

        var hito = Slug(milestone);
        if (hito.Length > 0)
        {
            name += "-" + hito;
        }

        return name + "-v" + version.ToString(CultureInfo.InvariantCulture) + ClientDocumentFormats.Extension(format);
    }

    /// <summary>Whether <paramref name="name"/> is only a file name: no folder, no drive, no <c>..</c>, no character Windows refuses, and no ending in a dot or a space.</summary>
    public static bool IsPlainFileName(string? name) =>
        name is { Length: > 0 and <= MaxFileNameLength }
        && name.IndexOfAny(Forbidden) < 0
        && !name.Any(char.IsControl)
        && !name.Contains("..", StringComparison.Ordinal)
        && name[^1] is not ('.' or ' ')
        && name[0] != ' ';

    private static string Slug(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            if (char.IsAsciiLetterOrDigit(lower))
            {
                builder.Append(lower);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length <= MaxSlugLength ? slug : slug[..MaxSlugLength].TrimEnd('-');
    }
}

/// <summary>
/// The person's approval to export (docs/17 D-60): the build they reviewed, the formats, the language of the document (chosen at
/// export; by default the one of the interface), the folder where the files go (wherever the person chooses; inside the project's
/// folder it is never written while a run is going) and who decided. The project is the one whose log records the export.
/// </summary>
public sealed record ClientDocumentExportRequest(
    ProjectContext Project,
    ClientDocumentBuild Build,
    IReadOnlyList<ClientDocumentFormat> Formats,
    string Language,
    string DestinationDirectory,
    string DecidedBy);

/// <summary>The rules an export keeps before anything is written.</summary>
public static class ClientDocumentExportRules
{
    /// <summary>Every rule the request breaks, as stable codes, in a fixed order; an empty list means it may be exported.</summary>
    public static IReadOnlyList<string> Validate(ClientDocumentExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var problems = new List<string>();
        void Add(string code)
        {
            if (!problems.Contains(code))
            {
                problems.Add(code);
            }
        }

        if (request.Build is null)
        {
            Add("document.export-build-required");
        }
        else
        {
            foreach (var problem in ClientDocumentRules.Validate(request.Build.Document))
            {
                Add(problem);
            }

            foreach (var problem in request.Build.Disclosure.Validate())
            {
                Add(problem);
            }
        }

        var formats = request.Formats ?? [];
        if (formats.Count == 0)
        {
            Add("document.export-formats-required");
        }

        if (formats.Any(f => !Enum.IsDefined(f)))
        {
            Add("document.export-format-unknown");
        }

        if (formats.Distinct().Count() != formats.Count)
        {
            Add("document.export-format-duplicate");
        }

        if (!LanguageTags.IsValid(request.Language))
        {
            Add("document.export-language-invalid");
        }

        if (string.IsNullOrWhiteSpace(request.DestinationDirectory) || !Path.IsPathRooted(request.DestinationDirectory))
        {
            Add("document.export-destination-invalid");
        }

        if (string.IsNullOrWhiteSpace(request.DecidedBy))
        {
            Add("document.export-decider-required");
        }

        return problems;
    }
}

/// <summary>A file the export wrote: its format, its name (never a path: the folder is the person's), its SHA-256 and its size.</summary>
public sealed record ExportedClientFile(ClientDocumentFormat Format, string FileName, string Sha256, long Bytes);

/// <summary>
/// What an export did (docs/17 D-60): the version, the files with their fingerprints, and the evidence of the generated Markdown,
/// which stays whichever formats were chosen.
/// </summary>
public sealed record ClientDocumentExportResult(int Version, IReadOnlyList<ExportedClientFile> Files, EvidenceId MarkdownEvidence, string MarkdownSha256);

/// <summary>
/// «Aprobar y exportar» (docs/17 D-60): writes the files where the person chose, each atomically (a file is never left half
/// written: if the app exits before one finishes, it is not saved), keeps the earlier versions (the version is part of each name),
/// stores the generated Markdown as evidence and, last, records <c>ClientDocumentExported</c> with the fingerprint of each file. If
/// writing fails, nothing is recorded. Into a folder inside the project, never while a run is going (<c>IRunActivity</c>), like the
/// export of the memory: it would write where the agents work. Into any other folder, whenever the person wants.
/// </summary>
public interface IClientDocumentExporter
{
    /// <exception cref="ArgumentException">The request breaks <see cref="ClientDocumentExportRules"/>: nothing is written.</exception>
    /// <exception cref="InvalidOperationException">The folder is inside the project and a run is going in it.</exception>
    /// <exception cref="Decisions.DecisionLogIntegrityException">The decision log does not verify: nothing is written.</exception>
    Task<ClientDocumentExportResult> ExportAsync(ClientDocumentExportRequest request, CancellationToken ct);
}

/// <summary>
/// What the agent receives to draft a part (docs/17 D-59): the deterministic model (already without secrets or paths), the part to
/// write, the language to write it in and the person's OK for this use, with the estimate in front.
/// </summary>
public sealed record ClientDocumentDraftRequest(ProjectContext Project, ClientDocument Deterministic, ClientDraftKind Kind, string Language, QuotaConsent Consent);

/// <summary>
/// The draft of an agent (docs/17 D-59): the auxiliary run it was recorded under (purpose <c>ClientDocumentDraft</c>), how it ended, the
/// evidence of the answer as it came, the paragraphs the system kept (<see cref="ClientDraftRules"/>) and why the rest were left out
/// (in English). Nothing goes into the document until the person accepts it, and it is marked as written by an agent. <see cref="Error"/>
/// says why there are no paragraphs when the run failed or its answer could not be read.
/// </summary>
public sealed record ClientDocumentDraftReply(
    RunId Run,
    AgentRunStatus Status,
    EvidenceId? AnswerEvidence,
    IReadOnlyList<string> Paragraphs,
    IReadOnlyList<string> Discarded,
    string? Error);

/// <summary>
/// The optional part written by an agent (docs/17 D-59): a read-only auxiliary run (<c>AuxiliaryAgentRunner</c>, purpose
/// <c>ClientDocumentDraft</c>) in an empty folder, with the person's OK each time. It only sees the model, never the log, the memory or
/// the files.
/// </summary>
public interface IClientDocumentDrafter
{
    /// <exception cref="ArgumentNullException">There is no request, project, model or consent.</exception>
    /// <exception cref="ArgumentException">The model breaks <see cref="ClientDocumentRules"/> or the language is not valid: nothing is launched.</exception>
    /// <exception cref="InvalidOperationException">The consent is too old, dated in the future or spent already: nothing is launched.</exception>
    Task<ClientDocumentDraftReply> DraftAsync(ClientDocumentDraftRequest request, IProgress<AgentEvent>? progress, CancellationToken ct);
}
