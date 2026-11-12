namespace AgentStudio.Providers.Cli;

/// <summary>What is added to «the program was not found» when the locator did find it, but only inside the open project (ADR-0018, second addendum of 2026-11-12).</summary>
internal static class InsideProjectNote
{
    /// <summary>In Spanish, for the messages of the adapters that are written that way.</summary>
    internal static string Spanish(string? path) =>
        path is null ? string.Empty : $" Se encontró dentro de la carpeta del proyecto ({path}) y no se usa: instálalo fuera de ella.";

    /// <summary>In English, for the technical messages.</summary>
    internal static string English(string? path) =>
        path is null ? string.Empty : $" It was found only inside the project folder ('{path}'), which is not trusted to provide programs, so it was not used.";
}
