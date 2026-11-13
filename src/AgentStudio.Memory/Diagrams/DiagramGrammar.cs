using System.Globalization;
using System.Text;
using AgentStudio.Application.Diagrams;
using AgentStudio.Domain.Common;

namespace AgentStudio.Memory.Diagrams;

/// <summary>
/// The bounded grammar of the generated diagrams (docs/20 D-24): what the generators may write and the caps they keep. A diagram is made of fixed words of this
/// file, identifiers of its own (<c>n1</c>, <c>p1</c>…) and texts that went through <see cref="MermaidText"/>; nothing else reaches it. No directive
/// (<c>%%{init}%%</c>), no <c>click</c>, no link and no style that comes from data. The text ends its lines with <c>\n</c> and has no byte-order mark.
/// </summary>
internal static class DiagramGrammar
{
    /// <summary>The version of the generators: part of what makes a diagram «desactualizado» and written in its header (docs/20 D-25). Raise it when a form changes.</summary>
    public const int GeneratorVersion = 1;

    /// <summary>The most nodes of a flowchart (docs/20 D-24).</summary>
    public const int MaxNodes = 200;

    /// <summary>The most relations of a flowchart.</summary>
    public const int MaxEdges = 400;

    /// <summary>The most messages of a sequence diagram.</summary>
    public const int MaxMessages = 300;

    /// <summary>The fixed vocabulary of the sequence diagram, in Spanish and without parameters (docs/20 D-24): the message of an entry of the log is never free text.</summary>
    public static class Words
    {
        public const string Person = "Persona";
        public const string Orchestrator = "Orquestador";
        public const string System = "Sistema";
        public const string Judge = "Juez";

        public const string StartsRun = "inicia la ejecución";
        public const string LaunchesTask = "lanza la tarea";
        public const string DeliversReport = "entrega el informe";
        public const string Fails = "falla";
        public const string IsCancelled = "se cancela";
        public const string IsInterrupted = "se interrumpe";
        public const string Verifies = "verifica";
        public const string Integrates = "integra";
        public const string IntegratesWithConflict = "integra con conflicto";
        public const string Delivers = "entrega al repositorio";
        public const string AsksVerdict = "pide el veredicto";
        public const string VerdictApproved = "veredicto aprobado";
        public const string VerdictChanges = "veredicto con cambios";
        public const string VerdictRejected = "veredicto rechazado";
        public const string VerdictEscalated = "veredicto a la persona";
        public const string VerdictUnresolved = "veredicto sin resolver";
        public const string VerdictInvalid = "veredicto no válido";
        public const string AsksApproval = "pide aprobación";
        public const string Approves = "aprueba";
        public const string Denies = "deniega";
        public const string EndsRun = "termina la ejecución";

        public const string ProjectSuffix = " (parcial)";
    }

    /// <summary>The header every diagram starts with: <c>%%</c> comments, so the text is still one Mermaid text. Everything in it is code: fixed words, hexadecimal and a date.</summary>
    /// <param name="diagram">The kind in one fixed word: <c>solucion</c>, <c>proyecto</c>, <c>plan</c> or <c>secuencia</c>.</param>
    /// <param name="origin">The commit (<c>commit: …</c>) or the run (<c>ejecución: …</c>) the diagram describes, already written.</param>
    /// <param name="modelHash">The fingerprint of the model.</param>
    public static string Header(string diagram, string origin, string modelHash, DateTimeOffset at)
    {
        if (!ContentHash.IsSha256Hex(modelHash))
        {
            throw new ArgumentException("The model is named by its SHA-256.", nameof(modelHash));
        }

        var builder = new StringBuilder();
        builder.Append("%% Generado por Agent Studio. Editarlo no cambia nada: se regenera.\n");
        builder.Append(CultureInfo.InvariantCulture, $"%% diagrama: {diagram} · generador: {GeneratorVersion}\n");
        builder.Append(CultureInfo.InvariantCulture, $"%% {origin} · modelo: sha256:{modelHash} · fecha: {at.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}\n");
        return builder.ToString();
    }

    /// <summary>The origin of a diagram of a commit.</summary>
    public static string CommitOrigin(string commit) => "commit: " + commit;

    /// <summary>The origin of a diagram of a run; the id of a run has only letters, digits, <c>_</c> and <c>-</c>.</summary>
    public static string RunOrigin(string run) => "ejecución: " + run;

    /// <summary>Whether <paramref name="run"/> is the id of a run (<c>RunId</c>): 1 to 64 letters, digits, <c>_</c> and <c>-</c>. It is the only data that reaches a header, so the header is checked once more here.</summary>
    public static bool IsRunId(string? run) =>
        !string.IsNullOrEmpty(run) && run.Length <= 64 && run.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-');

    /// <summary>The fixed word of a kind, for the header and for the names of the cache.</summary>
    public static string Word(DiagramKind kind) => kind switch
    {
        DiagramKind.Solution => "solucion",
        DiagramKind.ProjectNamespaces => "proyecto",
        DiagramKind.RunPlan => "plan",
        DiagramKind.RunSequence => "secuencia",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of diagram."),
    };

    /// <summary>The fingerprint of a text: the SHA-256 of its UTF-8 bytes.</summary>
    public static string Sha256(string text) => ContentHash.Sha256Hex(Encoding.UTF8.GetBytes(text));
}
