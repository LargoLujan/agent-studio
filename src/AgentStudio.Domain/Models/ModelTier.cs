namespace AgentStudio.Domain.Models;

/// <summary>
/// The level of a model in words (docs/17 D-22): «ligero», «equilibrado» or «máximo». It comes from the local catalog
/// (<see cref="ModelCatalogEntry"/>); a model the catalog does not classify has none (null: «sin clasificar»). It is never a
/// price, and it is not the effort.
/// </summary>
public enum ModelTier
{
    /// <summary>«ligero»: the smallest and fastest of a vendor (Claude's haiku, say).</summary>
    Light,

    /// <summary>«equilibrado»: the default for most work (Claude's sonnet, say).</summary>
    Balanced,

    /// <summary>«máximo»: the most capable of a vendor (Claude's opus, say).</summary>
    Maximum,
}
