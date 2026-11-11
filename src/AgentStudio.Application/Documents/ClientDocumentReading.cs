using System.Globalization;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Security;

namespace AgentStudio.Application.Documents;

/// <summary>A text of the document as a writer puts it down: the words, and the mark of an agent's text when it has one.</summary>
public readonly record struct RenderedClientText(string Value, string? Mark);

/// <summary>
/// What every writer of a document for clients (Markdown, HTML, Word and, from the HTML, PDF) reads the model through, so the four
/// say the same thing: the sentences of the system painted in the language of the document, the mark of an agent's text, the title,
/// the lines of the cover and the titles of the sections (docs/17 D-58 and D-59). Writers differ only in how they lay it out.
/// </summary>
public sealed class ClientDocumentReading
{
    private readonly IUserTextRenderer _texts;

    /// <exception cref="ArgumentException">The language is not the name of a culture.</exception>
    public ClientDocumentReading(IUserTextRenderer texts, string language)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (!LanguageTags.IsValid(language))
        {
            throw new ArgumentException("The language is the name of a culture, such as es-ES or en.", nameof(language));
        }

        _texts = texts;
        Language = language;
    }

    /// <summary>The language the system's sentences are painted in.</summary>
    public string Language { get; }

    /// <summary>
    /// Checks the model before a writer lays it out: it breaks no rule of <see cref="ClientDocumentRules"/> (no secret, no path of the
    /// machine, no hidden character, well-formed), so a model that was not cleaned is never written.
    /// </summary>
    /// <exception cref="ArgumentException">The model breaks a rule; the message lists the codes.</exception>
    public static void EnsureWritable(ClientDocument document, ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(document);
        var problems = ClientDocumentRules.Validate(document, redactor);
        if (problems.Count > 0)
        {
            throw new ArgumentException("The model of the document breaks its rules: " + string.Join(", ", problems), nameof(document));
        }
    }

    /// <summary>A sentence of the system in the language of the document.</summary>
    public string System(string code, params object[] arguments) => _texts.Render(new UserText(code, arguments), Language);

    /// <summary>A text of the model: the system's painted, a person's or an agent's as it is, with the mark of an agent's.</summary>
    public RenderedClientText Text(ClientText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Source switch
        {
            ClientTextSource.System => new(_texts.Render(text.System!, Language), null),
            ClientTextSource.Written => new(text.Value!, null),
            _ => new(text.Value!, Mark(text.Mark!)),
        };
    }

    /// <summary>«Redactado por un agente · revisado por la persona responsable: sí o no», in the language of the document.</summary>
    public string Mark(ClientAgentMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        return System(mark.ReviewedByPerson ? ClientDocumentTexts.AgentReviewed : ClientDocumentTexts.AgentNotReviewed, mark.Run.Value);
    }

    /// <summary>The title of the document.</summary>
    public string Title(ClientDocument document) => System(ClientDocumentTexts.Title, document.Cover.Project);

    /// <summary>The title of a section.</summary>
    public string SectionTitle(ClientDocumentSectionKind kind) => System(ClientDocumentSections.TitleCode(kind));

    /// <summary>The lines of the cover as label and value: project, milestone (when there is one), date, version and commit (when there is one).</summary>
    public IReadOnlyList<(string Label, string Value)> CoverLines(ClientDocument document)
    {
        var cover = document.Cover;
        var lines = new List<(string, string)> { (System(ClientDocumentTexts.CoverProject), cover.Project) };
        if (cover.Milestone is not null)
        {
            lines.Add((System(ClientDocumentTexts.CoverMilestone), cover.Milestone));
        }

        lines.Add((System(ClientDocumentTexts.CoverDate), System(ClientDocumentTexts.DateValue, cover.GeneratedAt.ToUniversalTime())));
        lines.Add((System(ClientDocumentTexts.CoverVersion), cover.Version.ToString(CultureInfo.InvariantCulture)));
        if (cover.Commit is not null)
        {
            lines.Add((System(ClientDocumentTexts.CoverCommit), cover.Commit));
        }

        return lines;
    }
}
