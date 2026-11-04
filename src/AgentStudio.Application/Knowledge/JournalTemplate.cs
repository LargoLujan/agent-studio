using System.Globalization;
using System.Text;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Knowledge;

/// <summary>
/// The fixed words of the template of the prompt journal (docs/17 D-02; ADR-0047, addenda of 2026-11-04): what the writer of
/// the product (<c>MarkdownDailyJournal</c> and <see cref="JournalResultAnchor"/>), the development hook and the derivation
/// of the journal as memory put in every entry, whatever was asked. One source for whoever reads the template: the clean view
/// the meaning of an entry is computed from leaves them out, and the lexical list of a search does not search by them,
/// since they are in almost every entry of the journal and only bring noise.
/// <code>
/// - **11:40:22** · P-0102 · Backend · codex-cli/gpt-5 · #prompt #agente-backend · «Implementar el almacén» (tarea T2)
///   - Resultado P-0102 · 12:05:10 · Succeeded · 1.488,0 s · tokens 1.200 → 340 (exactos) · ejecución `20261015-a1b2c3` · tarea `T2` · intento 1
///   - Hecho: almacén de evidencias con SHA-256.
/// </code>
/// <list type="bullet">
/// <item>The fixed words are its keywords (<see cref="Keywords"/>), the states of a run (<see cref="Statuses"/>, the names of
/// <see cref="AgentRunStatus"/>) and the CLIs (<see cref="Providers"/>). The view leaves them all out; a search, all but the
/// states (<see cref="Words"/>).</item>
/// <item>The values of its other fields are not fixed and are not here: who (the name of the agent in its team), the model,
/// the tags but <see cref="PromptTag"/>, the summary and the note. They are what a person asks the journal for.</item>
/// <item>The numbers, times, runs, tasks and days are identifiers, not words of the template: a search finds them exactly.</item>
/// </list>
/// The writer of the product still spells these words itself; a test keeps what it writes in step with this list.
/// </summary>
public static class JournalTemplate
{
    /// <summary>The topic of an entry of the journal as memory (<c>Bitácora del yyyy-MM-dd · …</c>) and the title of each day.</summary>
    public const string Journal = "Bitácora";

    /// <summary>The tag every entry of the product carries, before the tags of its prompt.</summary>
    public const string PromptTag = "#prompt";

    /// <summary>The word that opens the line of the result of an entry (<c>  - Resultado P-NNNN · HH:mm:ss · state · …</c>).</summary>
    public const string Result = "Resultado";

    /// <summary>The tokens of a result: <c>tokens 1.200 → 340 (exactos)</c>, <c>(estimados)</c>, or <c>tokens no disponibles</c>.</summary>
    public const string Tokens = "tokens";

    /// <summary>Tokens the provider gave.</summary>
    public const string ExactTokens = "exactos";

    /// <summary>Tokens Agent Studio estimated.</summary>
    public const string EstimatedTokens = "estimados";

    /// <summary>A count of tokens nobody gave.</summary>
    public const string NotAvailable = "n/d";

    /// <summary>The run of the anchor of a result line: <c>ejecución `&lt;run&gt;`</c>.</summary>
    public const string Run = "ejecución";

    /// <summary>The task of an entry line (<c>(tarea T2)</c>) and of the anchor of a result line (<c>tarea `T2`</c>).</summary>
    public const string Task = "tarea";

    /// <summary>The attempt of the anchor of a result line: <c>intento &lt;n&gt;</c>.</summary>
    public const string Attempt = "intento";

    /// <summary>The entry the anchor of a result line names: <c>entrada `yyyy-MM-dd/P-NNNN`</c>.</summary>
    public const string Entry = "entrada";

    /// <summary>The session of an entry of the development hook: <c>- **HH:mm:ss** · sesión `xxxxxxxx` · …</c>.</summary>
    public const string Session = "sesión";

    /// <summary>The label of the lines the hook adds under an entry: <c>  - Hecho: …</c>, <c>  - Hecho (en curso): …</c>.</summary>
    public const string Done = "Hecho";

    /// <summary>The keywords of the template, as it spells them.</summary>
    public static IReadOnlyList<string> Keywords { get; } =
        [Journal, PromptTag, Result, Tokens, ExactTokens, EstimatedTokens, NotAvailable, Run, Task, Attempt, Entry, Session, Done];

    /// <summary>The states of a run, as the result line writes them.</summary>
    public static IReadOnlyList<string> Statuses { get; } = Enum.GetNames<AgentRunStatus>();

    /// <summary>The CLIs, as the entry line writes them before the model (<c>codex-cli/gpt-5</c>).</summary>
    public static IReadOnlyList<string> Providers { get; } = [ProviderId.ClaudeCode.Value, ProviderId.CodexCli.Value, ProviderId.GeminiCli.Value];

    /// <summary>
    /// The fixed words a search does not search by, folded as the lexical search folds a term (<see cref="Fold"/>): the
    /// keywords and the CLIs. The states are fixed words too, but a search that names one («¿qué terminó en Failed?») asks
    /// for it: with them in the list such a search lost its answer (ADR-0047, second addendum of 2026-11-04).
    /// </summary>
    public static IReadOnlySet<string> Words { get; } = FoldAll([.. Keywords, .. Providers]);

    /// <summary>Whether <paramref name="term"/>, folded, is one of <see cref="Words"/>: «Ejecución», «#prompt» and «codex-cli» are; «Claude», «tareas» and «Failed» are not.</summary>
    public static bool IsTemplateWord(string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        return Words.Contains(Fold(term));
    }

    /// <summary>The words folded as the lexical search folds a term, without the blank ones.</summary>
    public static IReadOnlySet<string> FoldAll(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        return words.Select(Fold).Where(w => w.Length > 0).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// A term folded as the lexical search folds it (<c>unicode61</c>): lower case, without accents and without what is not
    /// a letter or a digit, so «Ejecución,» is «ejecucion» and «codex-cli» is «codexcli». A surrogate is never a letter here.
    /// </summary>
    public static string Fold(string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        var clean = new StringBuilder(term.Length);
        foreach (var c in term)
        {
            clean.Append(char.IsSurrogate(c) ? ' ' : c);
        }

        var folded = new StringBuilder(clean.Length);
        foreach (var c in clean.ToString().ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
            {
                folded.Append(c);
            }
        }

        return folded.ToString();
    }
}
