using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Help;

// Phase 14 contracts of the help (docs/17 §5.4, D-54 to D-57; ADR-0056). Who does what:
// - WP-14C, Desktop/Help: the corpus (IHelpCorpus, from Help/<language>/*.md), its local search (IHelpSearch: FTS5, plus the
//   vectors of the semantic index when its model is installed), the panel, F1, the contents of the ⓘ and the catalog of the
//   actions (IHelpActionCatalog).
// - WP-14C, Application/Help: the agent of the help (IHelpAgent), over AuxiliaryAgentRunner with the purpose Help.
// Only contracts and their pure rules are here: no service, no file, no process.

/// <summary>The limits of the help (docs/17 D-55 and D-56), which a strict schema of the answer cannot say: the system checks them.</summary>
public static class HelpLimits
{
    /// <summary>The longest title of an article.</summary>
    public const int MaxTitleLength = 80;

    /// <summary>The longest text of a search.</summary>
    public const int MaxQueryLength = 200;

    /// <summary>The most results of a search.</summary>
    public const int MaxSearchResults = 10;

    /// <summary>The longest question the person asks the agent.</summary>
    public const int MaxQuestionLength = 1000;

    /// <summary>The longest text of the answer of the agent (D-56): 1 200 characters.</summary>
    public const int MaxAnswerLength = 1200;

    /// <summary>The most articles sent to the agent with the question: the closest to it (D-56).</summary>
    public const int MaxSentArticles = 4;

    /// <summary>The most articles an answer cites; each one is among the articles that were sent.</summary>
    public const int MaxCitedArticles = 4;

    /// <summary>The most actions an answer offers (D-56 says «una acción»; the buttons are of a closed list).</summary>
    public const int MaxActions = 2;

    /// <summary>The most facts about the state of the screen the card carries.</summary>
    public const int MaxStateFacts = 6;

    /// <summary>The longest text of one part of the card: the name of the screen, a fact, the last notice or the name of the project.</summary>
    public const int MaxFactLength = 200;
}

// ---- The corpus and its search (D-55): local, free, and always the first answer ----

/// <summary>
/// An article of the help, in one language (docs/17 D-55): written in plain language, one per screen or section, in
/// <c>Help/&lt;language&gt;/&lt;id&gt;.md</c>. <paramref name="Screens"/> are the screens where <c>F1</c> opens it and
/// <paramref name="InfoKeys"/> the ⓘ buttons whose «Más ayuda» opens it (the <c>InfoKey</c> of the control, D-54). The documents
/// of <c>docs/</c> are for development and are not help.
/// </summary>
/// <param name="Id">A code (<see cref="UserText.IsCode"/>), the same in every language: <c>orchestrator.team</c>.</param>
/// <param name="Language">The name of the language of this version (<see cref="LanguageTags"/>): <c>es-ES</c>, <c>en</c>.</param>
/// <param name="Title">The title, shown in the results and at the top of the article.</param>
/// <param name="Markdown">The article, in Markdown.</param>
/// <param name="Screens">The codes of the screens whose <c>F1</c> opens it.</param>
/// <param name="InfoKeys">The keys of the ⓘ buttons whose «Más ayuda» opens it.</param>
public sealed record HelpArticle(
    string Id,
    string Language,
    string Title,
    string Markdown,
    IReadOnlyList<string> Screens,
    IReadOnlyList<string> InfoKeys);

/// <summary>The rules an article of the corpus keeps, checked by the loader and by a test over the whole corpus (F14-7).</summary>
public static class HelpArticleRules
{
    /// <summary>
    /// Every rule the article breaks, as stable codes, in a fixed order; an empty list means it may be published. No text of the
    /// article is in a code.
    /// </summary>
    public static IReadOnlyList<string> Validate(HelpArticle article)
    {
        ArgumentNullException.ThrowIfNull(article);
        var problems = new List<string>();
        void Add(string code)
        {
            if (!problems.Contains(code))
            {
                problems.Add(code);
            }
        }

        if (!UserText.IsCode(article.Id))
        {
            Add("help.article-id-invalid");
        }

        if (!LanguageTags.IsValid(article.Language))
        {
            Add("help.article-language-invalid");
        }

        if (string.IsNullOrWhiteSpace(article.Title))
        {
            Add("help.article-title-required");
        }
        else if (article.Title.Length > HelpLimits.MaxTitleLength)
        {
            Add("help.article-title-too-long");
        }

        if (string.IsNullOrWhiteSpace(article.Markdown))
        {
            Add("help.article-body-required");
        }

        var screens = article.Screens ?? [];
        if (screens.Any(s => !UserText.IsCode(s)))
        {
            Add("help.article-screen-invalid");
        }

        var infoKeys = article.InfoKeys ?? [];
        if (infoKeys.Any(k => !IsInfoKey(k)))
        {
            Add("help.article-info-key-invalid");
        }

        if (screens.Distinct(StringComparer.Ordinal).Count() != screens.Count || infoKeys.Distinct(StringComparer.Ordinal).Count() != infoKeys.Count)
        {
            Add("help.article-link-duplicate");
        }

        return problems;
    }

    /// <summary>Whether <paramref name="key"/> is the key of an ⓘ: letters, digits and <c>_</c>, starting with a letter (<c>Info_Canvas_Team</c>).</summary>
    public static bool IsInfoKey(string? key) =>
        key is { Length: > 0 and <= 64 } && char.IsAsciiLetter(key[0]) && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}

/// <summary>The articles the app has, by language (docs/17 D-55): <c>Help/&lt;language&gt;/*.md</c> in the desktop. It reads files shipped with the app and nothing else.</summary>
public interface IHelpCorpus
{
    /// <summary>
    /// The articles in <paramref name="language"/>, by id. An article not written in that language comes in Spanish of Spain, the
    /// source (the neutral resources), as a text missing from the interface does (ADR-0054).
    /// </summary>
    IReadOnlyList<HelpArticle> Articles(string language);

    /// <summary>One article by its id, or null when there is none.</summary>
    HelpArticle? Find(string id, string language);

    /// <summary>The article <c>F1</c> opens on <paramref name="screen"/> (a code), or null when the screen has none.</summary>
    HelpArticle? ForScreen(string screen, string language);

    /// <summary>The article the «Más ayuda» of the ⓘ with <paramref name="infoKey"/> opens, or null when it has none.</summary>
    HelpArticle? ForInfoKey(string infoKey, string language);
}

/// <summary>How a result of the search was found.</summary>
public enum HelpMatchKind
{
    /// <summary>By its words (FTS5).</summary>
    Lexical,

    /// <summary>By its meaning (the vectors of the semantic index, when its model is installed).</summary>
    Semantic,

    /// <summary>By both.</summary>
    Both,
}

/// <summary>One result of the search of the help: the article it opens, its title, a piece of it and how well it matches (higher is better; only comparable within one list).</summary>
public sealed record HelpSearchHit(string ArticleId, string Title, string Excerpt, double Score, HelpMatchKind Match);

/// <summary>The rules a search of the help keeps.</summary>
public static class HelpSearchRules
{
    /// <summary>The rules the search breaks, as stable codes; an empty list means it may run.</summary>
    public static IReadOnlyList<string> Validate(string? query, int limit)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(query))
        {
            problems.Add("help.search-query-required");
        }
        else
        {
            if (query.Length > HelpLimits.MaxQueryLength)
            {
                problems.Add("help.search-query-too-long");
            }

            if (PlainText.HasHiddenCharacter(query, allowLineBreaks: false))
            {
                problems.Add("help.search-query-hidden-character");
            }
        }

        if (limit is < 1 or > HelpLimits.MaxSearchResults)
        {
            problems.Add("help.search-limit-invalid");
        }

        return problems;
    }
}

/// <summary>
/// The local search of the help (docs/17 D-55): free and without network. FTS5 over the corpus and, when the semantic index has its
/// model installed (ADR-0047), the vectors too, fused like the search of the memory. It never spends quota, and it is the first
/// answer to any question: «Preguntar a un agente» comes after it.
/// </summary>
public interface IHelpSearch
{
    /// <exception cref="ArgumentException">The query or the limit break <see cref="HelpSearchRules"/>.</exception>
    Task<IReadOnlyList<HelpSearchHit>> SearchAsync(string query, string language, int limit, CancellationToken ct);
}

// ---- The agent of the help (D-56) and what it sees (D-57) ----

/// <summary>The agent of the help as the system names it: the id the estimator and the consumption use for it.</summary>
public static class HelpAgent
{
    /// <summary>The id of the agent of the help (<c>UsageEstimateRequest.Agent</c>, the auxiliary run).</summary>
    public static readonly AgentId Id = new("ayuda");
}

/// <summary>
/// Which agent answers (docs/17 D-56): Claude by default, with a light model (<see cref="Model"/> null means the lightest one
/// the catalog classifies for that CLI). With Codex it is the person's choice and the card says so: its read-only mode reads the
/// whole disk (docs/15 §3.2), which is why it is not the default.
/// </summary>
public sealed record HelpAgentChoice(ProviderId Provider, string? Model)
{
    /// <summary>Claude Code with its light model.</summary>
    public static HelpAgentChoice Default { get; } = new(ProviderId.ClaudeCode, null);

    /// <summary>Whether this is the choice made when the person chooses nothing.</summary>
    public bool IsDefault => Equals(Default);

    /// <summary>Whether the read-only mode of this CLI can read the whole disk (Codex): the person is told before the OK.</summary>
    public bool ReadsWholeDisk => Provider == ProviderId.CodexCli;
}

/// <summary>What a part of the context card is about (docs/17 D-57).</summary>
public enum HelpContextKind
{
    /// <summary>The screen where the person is («Orquestador»).</summary>
    Screen,

    /// <summary>A fact about its state («plan sin aprobar», «1 problema»).</summary>
    State,

    /// <summary>The last notice the person could see.</summary>
    LastNotice,

    /// <summary>The name of the project: only with «Incluir datos del proyecto».</summary>
    ProjectName,
}

/// <summary>What the screen knows about itself, before the card is built: the screen, its name, its state, its last notice and the project.</summary>
/// <param name="Screen">The code of the screen (<see cref="UserText.IsCode"/>): <c>orchestrator</c>.</param>
/// <param name="ScreenName">The name the person sees, in their language.</param>
/// <param name="State">Facts about its state, as the person reads them.</param>
/// <param name="LastNotice">The last notice visible, or null.</param>
/// <param name="ProjectName">The name of the project, or null.</param>
public sealed record HelpScreenFacts(string Screen, string ScreenName, IReadOnlyList<string> State, string? LastNotice, string? ProjectName);

/// <summary>One thing the card says will be sent, with its exact text.</summary>
public sealed record HelpContextPart(HelpContextKind Kind, string Text);

/// <summary>
/// What is sent to the agent of the help besides the question, and what is not (docs/17 D-57): the card the person sees before
/// they say yes. It is the exact text of each part, after redaction, so what is shown is what travels. The screen, its state and
/// the last notice go; the name of the project and any text with a path of the machine go only when the person ticks «Incluir
/// datos del proyecto» (<see cref="IncludesProjectData"/>), and what was left out for lack of that mark is in
/// <see cref="Withheld"/>, so the card can say so. Never: credentials, files, code, the memory, the log, the prompts of other agents,
/// nor anything of the folders of the CLIs; the card passes through <see cref="ISecretRedactor"/>.
/// </summary>
/// <param name="Screen">The code of the screen.</param>
/// <param name="Parts">What is sent, in order: the screen, the facts, the notice and, if marked, the project.</param>
/// <param name="IncludesProjectData">The person ticked «Incluir datos del proyecto».</param>
/// <param name="Withheld">The kinds of part that exist but were left out because the mark is not ticked.</param>
public sealed record HelpContextCard(string Screen, IReadOnlyList<HelpContextPart> Parts, bool IncludesProjectData, IReadOnlyList<HelpContextKind> Withheld)
{
    /// <summary>
    /// The card of a screen: every text cleaned of hidden characters, redacted and cut to <see cref="HelpLimits.MaxFactLength"/>; the
    /// project and the texts with a path of the machine only with the mark. At most <see cref="HelpLimits.MaxStateFacts"/> facts.
    /// </summary>
    /// <exception cref="ArgumentException">The screen is not a code, or it has no name.</exception>
    public static HelpContextCard Build(HelpScreenFacts facts, bool includeProjectData, ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(redactor);
        if (!UserText.IsCode(facts.Screen))
        {
            throw new ArgumentException($"'{facts.Screen}' is not the code of a screen.", nameof(facts));
        }

        var name = Clean(facts.ScreenName, redactor);
        if (name.Length == 0)
        {
            throw new ArgumentException("A screen has a name the person knows it by.", nameof(facts));
        }

        if (!includeProjectData && LocalPaths.Contains(name))
        {
            throw new ArgumentException("The name of a screen is never a path of the machine.", nameof(facts));
        }

        var parts = new List<HelpContextPart> { new(HelpContextKind.Screen, name) };
        var withheld = new List<HelpContextKind>();
        void Offer(HelpContextKind kind, string? raw)
        {
            var text = Clean(raw, redactor);
            if (text.Length == 0)
            {
                return;
            }

            if (!includeProjectData && (LocalPaths.Contains(raw) || LocalPaths.Contains(text)))
            {
                if (!withheld.Contains(kind))
                {
                    withheld.Add(kind);
                }

                return;
            }

            parts.Add(new HelpContextPart(kind, text));
        }

        foreach (var fact in (facts.State ?? []).Take(HelpLimits.MaxStateFacts))
        {
            Offer(HelpContextKind.State, fact);
        }

        Offer(HelpContextKind.LastNotice, facts.LastNotice);
        if (!string.IsNullOrWhiteSpace(facts.ProjectName))
        {
            if (includeProjectData)
            {
                Offer(HelpContextKind.ProjectName, facts.ProjectName);
            }
            else
            {
                withheld.Add(HelpContextKind.ProjectName);
            }
        }

        return new HelpContextCard(facts.Screen, parts, includeProjectData, withheld);
    }

    /// <summary>
    /// Every rule the card breaks, as stable codes, in a fixed order; an empty list means it may be sent. The service checks the
    /// card it is about to send with this, whatever built it: a part that changes when it is redacted again still has a secret.
    /// </summary>
    public IReadOnlyList<string> Validate(ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        var problems = new List<string>();
        void Add(string code)
        {
            if (!problems.Contains(code))
            {
                problems.Add(code);
            }
        }

        if (!UserText.IsCode(Screen))
        {
            Add("help.card-screen-invalid");
        }

        var parts = Parts ?? [];
        if (parts.Count(p => p is { Kind: HelpContextKind.Screen }) != 1)
        {
            Add("help.card-screen-part");
        }

        if (parts.Count(p => p is { Kind: HelpContextKind.State }) > HelpLimits.MaxStateFacts)
        {
            Add("help.card-too-many-facts");
        }

        if (parts.Count(p => p is { Kind: HelpContextKind.LastNotice }) > 1 || parts.Count(p => p is { Kind: HelpContextKind.ProjectName }) > 1)
        {
            Add("help.card-part-duplicate");
        }

        foreach (var part in parts)
        {
            if (part is null || !Enum.IsDefined(part.Kind))
            {
                Add("help.card-part-invalid");
                continue;
            }

            if (string.IsNullOrWhiteSpace(part.Text))
            {
                Add("help.card-part-empty");
                continue;
            }

            if (part.Text.Length > HelpLimits.MaxFactLength)
            {
                Add("help.card-part-too-long");
            }

            if (PlainText.HasHiddenCharacter(part.Text, allowLineBreaks: false))
            {
                Add("help.card-hidden-character");
            }

            if (!string.Equals(redactor.Redact(part.Text), part.Text, StringComparison.Ordinal))
            {
                Add("help.card-not-redacted");
            }

            if (!IncludesProjectData && (part.Kind == HelpContextKind.ProjectName || LocalPaths.Contains(part.Text)))
            {
                Add("help.card-project-data-not-marked");
            }
        }

        return problems;
    }

    private static string Clean(string? text, ISecretRedactor redactor)
    {
        var plain = PlainText.Strip(text, keepLineBreaks: false).Trim();
        if (plain.Length == 0)
        {
            return string.Empty;
        }

        var redacted = redactor.Redact(plain).Trim();
        return redacted.Length > HelpLimits.MaxFactLength ? redacted[..HelpLimits.MaxFactLength].TrimEnd() : redacted;
    }
}

/// <summary>
/// One action of the closed list the agent may offer (docs/17 D-56): «Abrir Consumo», «Abrir Ajustes → Trabajo desatendido». The
/// app shows it as a button with its <see cref="Label"/>; the agent only names its <see cref="Id"/>, and nothing runs until the person
/// presses it. An action opens a place of the app; it never changes anything.
/// </summary>
public sealed record HelpAction
{
    /// <exception cref="ArgumentException">The id is not a code.</exception>
    public HelpAction(string id, UserText label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (!UserText.IsCode(id))
        {
            throw new ArgumentException($"'{id}' is not the code of an action.", nameof(id));
        }

        Id = id;
        Label = label;
    }

    /// <summary>The code of the action, the same in every language: <c>open.consumption</c>.</summary>
    public string Id { get; }

    /// <summary>The id: what a list or a screen reader shows for the action instead of its record.</summary>
    public override string ToString() => Id;

    /// <summary>The text of its button, as a code: the desktop paints it in the language of the person.</summary>
    public UserText Label { get; }
}

/// <summary>The closed list of actions the agent of the help may offer (docs/17 D-56), written in the code of the desktop; neither the agent nor a file can add one.</summary>
public interface IHelpActionCatalog
{
    /// <summary>The actions, with unique ids; the strict schema of the answer lists exactly these ids.</summary>
    IReadOnlyList<HelpAction> Actions { get; }
}

/// <summary>
/// One question of the person to the agent of the help (docs/17 D-56): the open project (without one there is only the local help,
/// because there is no log to count it in), the question, the language of the answer, the card they saw, the agent and their OK for
/// this question with the estimate in front (decision 8 (a): one OK per question, spent once). The question is a new run each time:
/// the earlier turns are not sent again, so what the card showed is all that travels.
/// </summary>
public sealed record HelpAskRequest(
    ProjectContext Project,
    string Question,
    string Language,
    HelpContextCard Context,
    HelpAgentChoice Agent,
    QuotaConsent Consent);

/// <summary>The rules a question to the agent of the help keeps, and how the question is cleaned.</summary>
public static class HelpAskRules
{
    /// <summary>
    /// The question as it is sent: hidden characters out, redacted, trimmed. The card of the question shows this text, so what the
    /// person sees is what travels.
    /// </summary>
    public static string CleanQuestion(string? question, ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        var plain = PlainText.Strip(question, keepLineBreaks: true).Trim();
        return plain.Length == 0 ? string.Empty : redactor.Redact(plain).Trim();
    }

    /// <summary>
    /// Every rule the request breaks, as stable codes, in a fixed order; an empty list means it may be launched (the consent is
    /// checked by the service, which spends it).
    /// </summary>
    public static IReadOnlyList<string> Validate(HelpAskRequest request, ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(redactor);
        var problems = new List<string>();
        void Add(string code)
        {
            if (!problems.Contains(code))
            {
                problems.Add(code);
            }
        }

        if (string.IsNullOrWhiteSpace(request.Question))
        {
            Add("help.question-required");
        }
        else
        {
            if (request.Question.Length > HelpLimits.MaxQuestionLength)
            {
                Add("help.question-too-long");
            }

            if (PlainText.HasHiddenCharacter(request.Question, allowLineBreaks: true))
            {
                Add("help.question-hidden-character");
            }

            if (!string.Equals(redactor.Redact(request.Question), request.Question, StringComparison.Ordinal))
            {
                Add("help.question-not-redacted");
            }
        }

        if (!LanguageTags.IsValid(request.Language))
        {
            Add("help.language-invalid");
        }

        if (request.Context is null)
        {
            Add("help.card-required");
        }
        else
        {
            foreach (var problem in request.Context.Validate(redactor))
            {
                Add(problem);
            }
        }

        if (request.Agent is null || string.IsNullOrWhiteSpace(request.Agent.Provider.Value))
        {
            Add("help.agent-required");
        }

        return problems;
    }
}

/// <summary>
/// The answer of the agent of the help once the system checked it (<see cref="HelpAnswerRules.Check"/>): its text (at most
/// <see cref="HelpLimits.MaxAnswerLength"/> characters, text of an agent that the screen shows framed as such), the articles it
/// cites, which exist among those sent, the actions it offers, which are of the closed list, and what was left out and why (in English).
/// </summary>
public sealed record HelpAnswerCheck(string Text, IReadOnlyList<string> Articles, IReadOnlyList<string> Actions, IReadOnlyList<string> Discarded);

/// <summary>What the system keeps of what the agent of the help answered (docs/17 D-56): the strict schema cannot say lengths or lists of ids, so the system checks them.</summary>
public static class HelpAnswerRules
{
    /// <summary>
    /// The answer, cleaned: hidden characters out, trimmed and cut to <see cref="HelpLimits.MaxAnswerLength"/>; only the cited
    /// articles among <paramref name="sentArticles"/> (at most <see cref="HelpLimits.MaxCitedArticles"/>); only the actions among
    /// <paramref name="knownActions"/> (at most <see cref="HelpLimits.MaxActions"/>). What does not pass is not an error: it is left
    /// out and said in <see cref="HelpAnswerCheck.Discarded"/>.
    /// </summary>
    public static HelpAnswerCheck Check(
        string? text,
        IEnumerable<string?>? citedArticles,
        IEnumerable<string?>? actions,
        IReadOnlyCollection<string> sentArticles,
        IReadOnlyCollection<string> knownActions)
    {
        ArgumentNullException.ThrowIfNull(sentArticles);
        ArgumentNullException.ThrowIfNull(knownActions);
        var discarded = new List<string>();

        var clean = PlainText.Strip(text, keepLineBreaks: true).Trim();
        if (clean.Length == 0)
        {
            discarded.Add("the answer has no text.");
        }
        else if (clean.Length > HelpLimits.MaxAnswerLength)
        {
            clean = clean[..HelpLimits.MaxAnswerLength].TrimEnd();
            discarded.Add($"the text is longer than {HelpLimits.MaxAnswerLength} characters: it was cut.");
        }

        var articles = Keep(citedArticles, sentArticles, HelpLimits.MaxCitedArticles, "article", "is not one of the articles sent", discarded);
        var kept = Keep(actions, knownActions, HelpLimits.MaxActions, "action", "is not in the closed list", discarded);
        return new HelpAnswerCheck(clean, articles, kept, discarded);
    }

    private static List<string> Keep(IEnumerable<string?>? offered, IReadOnlyCollection<string> allowed, int max, string what, string notAllowed, List<string> discarded)
    {
        var kept = new List<string>();
        foreach (var id in offered ?? [])
        {
            if (string.IsNullOrWhiteSpace(id) || kept.Contains(id, StringComparer.Ordinal))
            {
                continue;
            }

            if (!allowed.Contains(id, StringComparer.Ordinal))
            {
                discarded.Add($"the {what} '{Shorten(id)}' {notAllowed}.");
            }
            else if (kept.Count >= max)
            {
                discarded.Add($"the {what} '{Shorten(id)}' is one more than {max}.");
            }
            else
            {
                kept.Add(id);
            }
        }

        return kept;
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "…";
}

/// <summary>
/// How a question to the agent of the help went (docs/17 D-56): the auxiliary run it was recorded under (purpose <c>Help</c>), how
/// it ended, the evidence of the answer as it came, and what the system kept of it (<see cref="HelpAnswerCheck"/>: the text, the
/// cited articles, the actions of the closed list, and what was left out and why). <see cref="Error"/> says why there is no answer
/// when the run failed or its answer could not be read.
/// </summary>
public sealed record HelpAskReply(
    RunId Run,
    AgentRunStatus Status,
    EvidenceId? AnswerEvidence,
    string Text,
    IReadOnlyList<string> Articles,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Discarded,
    string? Error);

/// <summary>
/// The agent of the help (docs/17 D-56, ADR-0056): an auxiliary run (<c>AuxiliaryAgentRunner</c>, purpose <c>Help</c>) that reads
/// and writes nothing: an empty working folder, Read, Grep and Glob confined as the measured profile of Claude says (ADR-0018), no
/// shell, no network and no memory. It receives, as delimited data, the card, the closest articles of the help and the question,
/// and answers with a schema; it executes nothing. Without a consent that is recent and not spent, nothing is launched. It counts
/// in the consumption like any run (ADR-0019) and its conversation stays as evidence in the open project, outside git.
/// </summary>
public interface IHelpAgent
{
    /// <exception cref="ArgumentNullException">There is no request, or no project, card or consent in it.</exception>
    /// <exception cref="ArgumentException">The request breaks <see cref="HelpAskRules.Validate"/>: nothing is launched.</exception>
    /// <exception cref="InvalidOperationException">The consent is too old, dated in the future or spent already: nothing is launched.</exception>
    Task<HelpAskReply> AskAsync(HelpAskRequest request, IProgress<AgentEvent>? progress, CancellationToken ct);
}
