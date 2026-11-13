namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>
/// The screens of Phase 10 (docs/21 section 7): «Repositorio y cambios › PR», the cards of publication and merge, and the new parts of «Ramas», «Actividad» and
/// «Trabajo». The P0 gives the file with its list empty, already added up in <see cref="UiScreens"/>, so WP5 only fills it and no other package touches
/// <c>UiScreens.cs</c>: every view WP5 creates is added here, and the guard of the inventory fails if one is missing.
/// </summary>
internal static class UiScreensHosting
{
    public static IReadOnlyList<UiScreen> All { get; } = [];
}
