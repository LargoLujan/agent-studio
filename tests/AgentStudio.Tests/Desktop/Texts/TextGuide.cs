using System.Text.RegularExpressions;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.TextsGuide;

/// <summary>
/// The guide of texts of docs/17 D-52 and D-53 (ADR-0054 point 9) as data for the tests of this folder: which resource files
/// are checked, which texts are first level and which are detail, the terms of the jargon and of the glossary, and the exceptions.
/// </summary>
internal static partial class TextGuide
{
    /// <summary>D-52: no sentence has more than 20 words (what <c>DebateStringsTests</c> already asked of the debates).</summary>
    public const int MaxWordsPerSentence = 20;

    /// <summary>D-53: the INFLESZ index of an explanation in Spanish is 55 or more.</summary>
    public const double MinInflesz = SpanishReadability.NormalLevel;

    /// <summary>
    /// A Spanish text is an explanation (and gets the INFLESZ test) when it is the «Qué es esto» or the actions of an ⓘ (key <c>Info_*_What</c> or <c>Info_*_Actions</c>), has this many
    /// words or more, or has two sentences and at least <see cref="ExplanationMinWordsInTwoSentences"/> words. Shorter texts are
    /// labels, names and one-line messages, where the index says nothing (a short sentence with a placeholder scores wildly).
    /// </summary>
    public const int ExplanationMinWords = 12;

    /// <summary>The words a text of two or more sentences needs to be an explanation.</summary>
    public const int ExplanationMinWordsInTwoSentences = 8;

    /// <summary>
    /// The resource files (by base name) the guide is checked on: the areas whose texts are already extracted and reviewed.
    /// </summary>
    public static readonly string[] ClosedAreas =
    [
        "Strings", "CanvasStrings", "MonitorStrings", "SetupStrings", "ModelsStrings", "EstimatesStrings", "ConsumptionStrings",
        "HelloStrings", "IntegrationStrings", "MontageStrings", "PanelsStrings", "OrchestratorStrings", "DebateStrings", "AuditStrings",
        "BackgroundStrings", "LockStrings", "ObservabilityStrings", "MemoryStrings", "SkillsStrings", "ShellStrings",
        "ControlsStrings", "ServicesStrings", "WorkStrings",
        // HelpStrings and DocumentsStrings (Desktop/Help, Desktop/Documents) are written by other packages of Phase 14: they join
        // this list when they are integrated.
    ];

    /// <summary>
    /// Areas whose resource files other packages of Phase 14 are creating while this guide is written: their texts are not
    /// checked yet. The orchestrator takes each one out of this list and adds it to <see cref="ClosedAreas"/> when it integrates the
    /// package, so from then on the guide is checked on its texts too. Any other area that appears in the folder and is in neither
    /// list is skipped in the same way (and listed by <c>Every_resource_area_is_checked_or_pending_its_extraction</c>).
    /// </summary>
    public static readonly string[] PendingExtraction =
    [
        "HelpStrings", "DocumentsStrings",
    ];

    /// <summary>
    /// Key convention: a key whose last segment (after the last «_») is one of these is a text of «Detalles», the folded part of a
    /// screen, where the technical terms of <see cref="Jargon"/> may appear. Every other key is first level: titles, labels, buttons,
    /// hints, warnings, the three texts of an ⓘ (<c>Info_*_What</c>, <c>_Actions</c>…) and the messages.
    /// </summary>
    public static readonly string[] DetailSuffixes = ["Detail", "Details"];

    /// <summary>A term of the jargon, in Spanish and English: it may not appear in a first-level text (D-52, D-53).</summary>
    public sealed record Term(string Name, Regex Pattern, string Say);

    /// <summary>
    /// The technical terms that stay out of the first level (they go in «Detalles»). «token» is not here: the glossary keeps the
    /// word and says it is explained in its ⓘ. «commit» is allowed only as «versión guardada (commit)», the first time (see <see cref="Commit"/>).
    /// </summary>
    public static readonly IReadOnlyList<Term> Jargon =
    [
        new("hash", Rx(@"\bhash(es)?\b", RegexOptions.IgnoreCase), "huella / fingerprint"),
        new("SHA-256", Rx(@"\bSHA-?(1|256|512)\b", RegexOptions.IgnoreCase), "huella / fingerprint"),
        new("vendor", Rx(@"\bvendors?\b", RegexOptions.IgnoreCase), "empresa del modelo / model company"),
        new("CLI", Rx(@"\bCLIs?\b", RegexOptions.None), "programa del agente / agent program"),
        new("worktree", Rx(@"\bworktrees?\b", RegexOptions.IgnoreCase), "copia de trabajo aislada / isolated working copy"),
        new("HEAD", Rx(@"\bHEAD\b", RegexOptions.None), "última versión guardada / latest saved version"),
        new("MCP", Rx(@"\bMCP\b", RegexOptions.None), "herramientas externas / external tools"),
        new("ff-only", Rx(@"\bff-only\b", RegexOptions.IgnoreCase), "sin mezclar / no merge"),
        new("payload", Rx(@"\bpayload\b", RegexOptions.IgnoreCase), "no aparece / not shown"),
        new("RunId", Rx(@"\bRunId\b", RegexOptions.None), "no aparece / not shown"),
        new("GUID", Rx(@"\bGUID\b", RegexOptions.None), "no aparece / not shown"),
        new("integrity level", Rx(@"integridad (baja|media|Low|Medium)|\b(Low|Medium)[- ]integrity\b|\b(low|medium)[- ]integrity\b", RegexOptions.IgnoreCase), "zona aislada con menos permisos / isolated zone with fewer permissions"),
    ];

    /// <summary>The «commit» of the glossary: «versión guardada (commit)» is the one form in which the word may appear, in any text.</summary>
    public static readonly Term Commit = new("commit", Rx(@"\bcommit(s|ted)?\b", RegexOptions.IgnoreCase), "versión guardada (commit) / saved version (commit)");

    /// <summary>
    /// The words the glossary translates, in the old form: they may not appear in any text of the interface, detail or not
    /// (D-52: «evidencia» → «prueba guardada», «cuota» → «límite de uso de tu plan»). Spanish only unless the term says otherwise.
    /// </summary>
    public sealed record Replaced(string Name, Regex Spanish, Regex? English, string Say);

    /// <summary>The glossary of D-52 in its old forms.</summary>
    public static readonly IReadOnlyList<Replaced> Glossary =
    [
        new("cuota", Rx(@"\bcuotas?\b", RegexOptions.IgnoreCase), Rx(@"\bquotas?\b", RegexOptions.IgnoreCase), "límite de uso de tu plan / usage limit of your plan"),
        new("evidencia", Rx(@"\bevidencias?\b", RegexOptions.IgnoreCase), null, "prueba(s) guardada(s)"),
        new("dossier", Rx(@"\bdossier(s)?\b", RegexOptions.IgnoreCase), Rx(@"\bdossiers?\b", RegexOptions.IgnoreCase), "expediente para el juez / file for the judge"),
        new("sandbox", Rx(@"\bsandbox(es)?\b", RegexOptions.IgnoreCase), null, "zona aislada (in English «sandbox» is the natural word)"),
        new("append-only", Rx(@"\bappend-only\b", RegexOptions.IgnoreCase), Rx(@"\bappend-only\b", RegexOptions.IgnoreCase), "historial que no se puede borrar / history that cannot be deleted"),
        new("ventana de contexto", Rx(@"\bventanas? de contexto\b", RegexOptions.IgnoreCase), Rx(@"\bcontext windows?\b", RegexOptions.IgnoreCase), "memoria de trabajo del modelo / working memory of the model"),
    ];

    /// <summary>
    /// Calques of the Spanish glossary that the English texts may not use: in English «sandbox» and «evidence» are the natural
    /// words, so the literal translations of «zona aislada» and «prueba guardada» are the mistake.
    /// </summary>
    public static readonly IReadOnlyList<Term> EnglishCalques =
    [
        new("isolated zone", Rx(@"\bisolated zones?\b", RegexOptions.IgnoreCase), "sandbox"),
        new("saved evidence", Rx(@"\bsaved evidence\b|\bpieces of saved evidence\b", RegexOptions.IgnoreCase), "evidence"),
    ];

    /// <summary>
    /// What the guide lets pass, with the reason of each. The key is <c>Area/Key/Rule</c>: the rule is <c>length</c>, <c>inflesz</c>, a
    /// term of <see cref="Jargon"/> or <see cref="Commit"/> or of <see cref="Glossary"/> by name. Each one is a text that the rule
    /// would flag and that the guide itself accepts; none is there «to pass the test».
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Exceptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CanvasStrings/Canvas_Provider_codex_cli/CLI"] = "«Codex CLI» is the name of the product the person installed, as «Gemini CLI»; D-52 says «programa del agente (Claude Code, Codex)» for the concept, and these are the names of the programs.",
        ["CanvasStrings/Canvas_Provider_gemini_cli/CLI"] = "«Gemini CLI» is the name of the product the person installed; see the previous one.",
        ["ObservabilityStrings/Observability_Quota_CodexName/CLI"] = "«Codex CLI» is the name of the product whose usage limit the bar shows; see the exceptions of CanvasStrings.",
        ["ObservabilityStrings/Observability_Quota_GeminiName/CLI"] = "«Gemini CLI» is the name of the product whose usage limit the bar shows; see the exceptions of CanvasStrings.",
        ["ControlsStrings/Terminal_AssetsMissing/inflesz"] = "The text is two short sentences, but the script path the person has to run (tools\\assets\\fetch-xterm.ps1) is one very long «word» that the formula counts as such.",
        ["ControlsStrings/Editor_MonacoMissing/inflesz"] = "The script path the person has to run (tools/assets/fetch-monaco.ps1) is one very long «word» for the formula; the sentences are short.",
    };

    /// <summary>The text of a resource file, by language: <c>es</c> for the neutral file and <c>en</c> for its English satellite.</summary>
    public sealed record Text(string Area, string Key, string Language, string Value)
    {
        /// <summary>A text of the folded «Detalles» (see <see cref="DetailSuffixes"/>).</summary>
        public bool IsDetail => DetailSuffixes.Contains(Key[(Key.LastIndexOf('_') + 1)..], StringComparer.Ordinal);

        /// <summary>An explanation in Spanish (see <see cref="ExplanationMinWords"/>); only these get the INFLESZ test.</summary>
        public bool IsExplanation
        {
            get
            {
                if (Language != "es")
                {
                    return false;
                }

                var sentences = SpanishReadability.Sentences(Value);
                var words = sentences.Sum(s => SpanishReadability.Words(s).Count);
                return (Key.StartsWith("Info_", StringComparison.Ordinal)
                        && (Key.EndsWith("_What", StringComparison.Ordinal) || Key.EndsWith("_Actions", StringComparison.Ordinal)))
                    || words >= ExplanationMinWords
                    || (sentences.Count >= 2 && words >= ExplanationMinWordsInTwoSentences);
            }
        }

        public string Id => $"{Area}/{Key}";
    }

    /// <summary>The texts of the closed areas, in Spanish and English, that are not <c>{0}</c>-only or empty.</summary>
    public static IReadOnlyList<Text> Texts()
    {
        var result = new List<Text>();
        foreach (var (area, (neutral, satellites)) in ResourceFiles().OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!ClosedAreas.Contains(area, StringComparer.Ordinal))
            {
                continue;
            }

            result.AddRange(Read(neutral).Select(p => new Text(area, p.Key, "es", p.Value)));
            if (satellites.TryGetValue("en", out var english))
            {
                result.AddRange(Read(english).Select(p => new Text(area, p.Key, "en", p.Value)));
            }
        }

        return [.. result.Where(t => SpanishReadability.Words(t.Value).Count > 0)];
    }

    /// <summary>Whether a rule that flags a text is excused by the list of exceptions.</summary>
    public static bool IsExcused(Text text, string rule) => Exceptions.ContainsKey($"{text.Area}/{text.Key}/{rule}");

    /// <summary>The text without its «(commit)» glosses, where the word «commit» is the glossary's own.</summary>
    public static string WithoutCommitGloss(string value) => CommitGloss().Replace(value, string.Empty);

    [GeneratedRegex(@"\(commit\)", RegexOptions.IgnoreCase)]
    private static partial Regex CommitGloss();

    private static Regex Rx(string pattern, RegexOptions options) => new(pattern, options | RegexOptions.CultureInvariant);
}
