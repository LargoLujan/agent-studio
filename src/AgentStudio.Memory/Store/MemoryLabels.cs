using AgentStudio.Application.Knowledge;

namespace AgentStudio.Memory.Store;

/// <summary>How the memory names its categories, origins and reviews in Spanish: in the log summaries and the Markdown.</summary>
internal static class MemoryLabels
{
    public static string Kind(MemoryKind kind) => kind switch
    {
        MemoryKind.Architecture => "arquitectura",
        MemoryKind.Convention => "convención",
        MemoryKind.Decision => "decisión",
        MemoryKind.KnownError => "error conocido",
        MemoryKind.Solution => "solución",
        MemoryKind.PendingTask => "tarea pendiente",
        MemoryKind.Change => "cambio",
        MemoryKind.DebateSummary => "resumen de debate",
        MemoryKind.Verdict => "veredicto",
        MemoryKind.DailySummary => "resumen diario",
        MemoryKind.JournalEntry => "entrada de la bitácora",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown memory category."),
    };

    public static string Origin(MemoryOrigin origin) => origin switch
    {
        MemoryOrigin.System => "del sistema",
        MemoryOrigin.User => "del usuario",
        MemoryOrigin.Agent => "de un agente",
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown memory origin."),
    };

    public static string Review(MemoryReview review) => review switch
    {
        MemoryReview.Accepted => "aceptada",
        MemoryReview.Rejected => "rechazada",
        MemoryReview.Retracted => "retirada",
        _ => throw new ArgumentOutOfRangeException(nameof(review), review, "Unknown memory review."),
    };
}
