using System.Globalization;
using System.Text;

namespace AgentStudio.Desktop.Help;

/// <summary>
/// The synonyms of the help search (ADR-0056 addendum, P59): the articles say «límite de uso» and never «cuota», as the guide of texts
/// asks (ADR-0054 point 9), so a person who searches the technical word would find nothing. Each entry maps a technical word, as the
/// person may type it in either language, to how the articles say it in Spanish and in English. The search adds those phrases to the
/// terms of the query (see <see cref="HelpSearch"/>); the articles and the texts of the screens do not change. This table is the only
/// place with the synonyms, and a test checks that it covers every entry of the glossary of the guide of texts.
/// </summary>
public static class HelpSynonyms
{
    private sealed record Entry(string[] Words, string[] Spanish, string[] English);

    private static readonly Entry[] Entries =
    [
        new(["cuota", "cuotas", "quota", "quotas"], ["límite de uso"], ["usage limit"]),
        new(["evidencia", "evidencias", "evidence"], ["prueba guardada", "pruebas guardadas"], ["saved test run"]),
        new(["cli", "clis", "proveedor", "proveedores", "provider", "providers"], ["programa del agente"], ["agent program"]),
        new(["ventana", "ventanas", "contexto", "context"], ["memoria de trabajo"], ["working memory"]),
        new(["commit", "commits"], ["versión guardada", "versiones guardadas"], ["saved version"]),
        new(["worktree", "worktrees"], ["copia de trabajo aislada"], ["isolated working copy"]),
        new(["head"], ["última versión guardada"], ["latest saved version"]),
        new(["hash", "hashes", "sha-256", "sha256"], ["huella"], ["fingerprint"]),
        new(["vendor", "vendors"], ["empresa del modelo"], ["model company"]),
        new(["mcp"], ["herramientas externas"], ["external tools"]),
        new(["ff-only"], ["sin mezclar"], ["no merge"]),
        new(["dossier", "dossiers"], ["expediente para el juez"], ["file for the judge"]),
        new(["sandbox", "sandboxes"], ["zona aislada"], []),
        new(["append-only"], ["historial que no se puede borrar"], ["history that cannot be deleted"]),
        new(["integridad", "integrity"], ["zona aislada"], ["sandbox"]),
    ];

    /// <summary>The names of the glossary the table intentionally does not cover: the guide says they do not appear at all.</summary>
    public static readonly IReadOnlyList<string> NeverShown = ["payload", "RunId", "GUID"];

    /// <summary>The words of every entry, folded: what a test compares with the names of the glossary.</summary>
    public static IReadOnlyList<string> Words() => [.. Entries.SelectMany(e => e.Words).Select(Fold)];

    /// <summary>The phrases that say <paramref name="term"/> in the articles of the language, or empty when it has no synonym.</summary>
    public static IReadOnlyList<string> For(string term, bool english)
    {
        string key;
        try
        {
            key = Fold(term);
        }
        catch (ArgumentException)
        {
            // A lone surrogate pasted into the search cannot be normalized: it has no synonym.
            return [];
        }

        foreach (var entry in Entries)
        {
            if (entry.Words.Any(w => Fold(w) == key))
            {
                return english ? entry.English : entry.Spanish;
            }
        }

        return [];
    }

    /// <summary>Lower case, without accents and without what is not a letter or a digit, as <c>unicode61</c> folds a word.</summary>
    public static string Fold(string word)
    {
        var decomposed = word.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
            {
                folded.Append(c);
            }
        }

        return folded.ToString();
    }
}
