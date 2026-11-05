using AgentStudio.Application.Knowledge;
using AgentStudio.Memory.Store;

namespace AgentStudio.Memory.Semantic;

/// <summary>What a result of the semantic index is, in Spanish, as the «Memoria» document and the planner's tool show it.</summary>
internal static class SemanticLabels
{
    /// <summary>The label of a fragment of a memory entry of <paramref name="kind"/>: its category, and «bitácora» for a journal entry.</summary>
    public static string Of(MemoryKind kind) => kind == MemoryKind.JournalEntry ? "bitácora" : MemoryLabels.Kind(kind);
}
