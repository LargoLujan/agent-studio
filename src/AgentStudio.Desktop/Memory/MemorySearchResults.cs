using System.Globalization;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Semantic;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Domain.Semantic;

namespace AgentStudio.Desktop.Memory;

/// <summary>What the search of «Memoria» says, in plain Spanish (docs/17 D-05, D-06 and F12-6).</summary>
public static class MemorySearchTexts
{
    /// <summary>The label of the search box when it goes by words only.</summary>
    public const string WordsLabel = "Buscar por palabras";

    /// <summary>The label of the search box when the model and the vectors are there.</summary>
    public const string HybridLabel = "Buscar por significado y por palabras";

    /// <summary>The accessible name of the search box by words only.</summary>
    public const string WordsName = "Buscar en la memoria por palabras";

    /// <summary>The accessible name of the search box by meaning and by words.</summary>
    public const string HybridName = "Buscar en la memoria por significado y por palabras";

    /// <summary>A search without the index composed.</summary>
    public const string NoIndex = "Solo por palabras: la búsqueda por significado no está disponible en esta composición.";

    /// <summary>A search while the index of the project has no fragments yet.</summary>
    public const string EmptyIndex = "Solo por palabras: el índice de búsqueda de este proyecto aún no tiene fragmentos; se construye en segundo plano.";

    /// <summary>A search when the state of the index could not be read.</summary>
    public const string UnreadableIndex = "Solo por palabras: no se pudo leer el índice de búsqueda de este proyecto.";

    /// <summary>What a hybrid search says about its results.</summary>
    public static string Hybrid(int results) => results switch
    {
        0 => "Por significado y por palabras: nada coincide.",
        1 => "Por significado y por palabras: 1 resultado.",
        _ => string.Create(CultureInfo.InvariantCulture, $"Por significado y por palabras: {results} resultados, del más cercano al menos."),
    };

    /// <summary>Why a result came out of a hybrid search: its words, its meaning or both.</summary>
    public static string Match(FusedCandidate rank)
    {
        ArgumentNullException.ThrowIfNull(rank);
        return rank switch
        {
            { LexicalRank: not null, SemanticRank: not null } => "Coincide por palabras y por significado",
            { LexicalRank: not null } => "Coincide por palabras",
            _ => "Coincide por significado",
        };
    }
}

/// <summary>Reads what the source of a journal entry says (<see cref="MemorySources.Journal"/>).</summary>
public static class JournalSources
{
    /// <summary>The day of the journal file an entry comes from (<c>bitacora:&lt;yyyy-MM-dd&gt;#sha256:…</c>); null for any other source.</summary>
    public static DateOnly? Day(string? source)
    {
        const int DayLength = 10;
        if (source is null || !source.StartsWith(MemorySources.JournalPrefix, StringComparison.Ordinal) || source.Length < MemorySources.JournalPrefix.Length + DayLength)
        {
            return null;
        }

        var day = source.AsSpan(MemorySources.JournalPrefix.Length, DayLength);
        return DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    }
}

/// <summary>
/// A result of the search that comes from the decision log (docs/17 D-01): a text the system wrote from an entry of the
/// verified log (the approved plan, a verdict requirement by requirement, a delivery, an alert of the auditor). It is the
/// system's and trusted, it cites its entry, and nothing is decided about it here: the log is append-only.
/// </summary>
public sealed class LogFragmentViewModel
{
    public LogFragmentViewModel(SemanticHit hit, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(hit);
        ArgumentNullException.ThrowIfNull(zone);
        Hit = hit;
        Created = TimeZoneInfo.ConvertTime(hit.Fragment.At, zone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    public SemanticHit Hit { get; }

    public string Id => Hit.Fragment.Id;

    /// <summary>What it is, as the index names it («registro · veredicto»), with a capital letter.</summary>
    public string KindLabel => string.IsNullOrEmpty(Hit.Label) ? "Registro" : char.ToUpper(Hit.Label[0], CultureInfo.GetCultureInfo("es-ES")) + Hit.Label[1..];

    public string OriginLabel => MemoryTexts.Origin(MemoryOrigin.System);

    public string TrustLabel => "De confianza: lo escribió Agent Studio a partir del registro verificado";

    public Tone TrustTone => Tone.Success;

    /// <summary>The text of the fragment that ranked.</summary>
    public string Content => Hit.Fragment.Text;

    /// <summary>When its entry was recorded, in local time.</summary>
    public string Created { get; }

    /// <summary>The entry of the log it cites, by its sequence.</summary>
    public string Source => $"Registro de decisiones, entrada {Hit.Fragment.Source.Reference}";

    /// <summary>Why it came out of the search.</summary>
    public string Match => MemorySearchTexts.Match(Hit.Rank);

    /// <summary>How a list and UI Automation name it.</summary>
    public override string ToString() => $"{KindLabel} · {Created}";
}
