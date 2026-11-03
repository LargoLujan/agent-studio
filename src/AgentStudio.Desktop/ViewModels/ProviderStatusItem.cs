using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>Semantic colour of a provider card.</summary>
public enum StatusKind
{
    Ok,
    Warning,
    Error,

    /// <summary>Parked by a decision (ADR-0009): neither ready nor a problem, so neutral, and left out of the summary.</summary>
    Parked,
}

/// <summary>Display-ready (Spanish) projection of a <see cref="ProviderProbeResult"/>.</summary>
public sealed record ProviderStatusItem(
    string Name,
    StatusKind Status,
    string Installation,
    string Version,
    string Authentication,
    string Method,
    string ExecutablePath,
    IReadOnlyList<string> Notes)
{
    /// <summary>What the row of a parked provider says in its details, above what its check found (ADR-0009).</summary>
    public const string GeminiParkedNote =
        "Aparcado (ADR-0009): sin una clave de API (GEMINI_API_KEY), Agent Studio trabaja con Claude Code y Codex, y Gemini no cuenta en el estado de los proveedores.";

    private const string InstalledText = "Instalado";

    /// <summary>
    /// The providers parked by a decision, with what their row says: their check still runs and is shown. One comes back as a
    /// provider like the others as soon as its check finds a way to authenticate (ADR-0009: Gemini, with an API key).
    /// </summary>
    private static readonly Dictionary<ProviderId, string> Parked = new()
    {
        [ProviderId.GeminiCli] = GeminiParkedNote,
    };

    /// <summary>Why the provider is parked; null when it is in use. A parked provider is neutral and does not count in the summary.</summary>
    public string? ParkedNote { get; init; }

    public bool IsParked => ParkedNote is not null;

    /// <summary>
    /// The one line of its row in Auto Doctor: «Aparcado» when it is; the version and the session when it is installed; what
    /// went wrong otherwise. The rest (installation, method, path and notes) is in the details of the row.
    /// </summary>
    public string Summary => IsParked ? "Aparcado"
        : Installation == InstalledText ? $"{Version} · {Authentication}"
        : Installation;

    public static ProviderStatusItem From(ProviderProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var installation = result.Installation switch
        {
            InstallationState.Installed => InstalledText,
            InstallationState.NotInstalled => "No instalado",
            _ => "Error",
        };

        var authentication = result.Authentication switch
        {
            AuthenticationState.Authenticated => "Sesión iniciada",
            AuthenticationState.NotAuthenticated => "Sin sesión",
            _ => "Desconocida",
        };

        var parked = HasUsableAuthentication(result) ? null : Parked.GetValueOrDefault(result.Provider);
        var status = parked is not null ? StatusKind.Parked : result.Installation switch
        {
            InstallationState.Error => StatusKind.Error,
            InstallationState.NotInstalled => StatusKind.Warning,
            _ => result.Authentication switch
            {
                AuthenticationState.Authenticated => StatusKind.Ok,
                AuthenticationState.NotAuthenticated => StatusKind.Error,
                _ => StatusKind.Warning,
            },
        };

        return new ProviderStatusItem(
            result.DisplayName,
            status,
            installation,
            string.IsNullOrWhiteSpace(result.Version) ? "—" : result.Version,
            authentication,
            string.IsNullOrWhiteSpace(result.AuthenticationMethod) ? "—" : result.AuthenticationMethod,
            result.ExecutablePath is null ? string.Empty : PathDisplay.CollapseHome(result.ExecutablePath),
            result.Notes ?? [])
        {
            ParkedNote = parked,
        };
    }

    /// <summary>
    /// Whether the check found a way to authenticate: a session it verified, or a method it names (for Gemini, the API key
    /// variables it found defined, by name). Only what the check says counts: no value of a key is read here.
    /// </summary>
    private static bool HasUsableAuthentication(ProviderProbeResult result) =>
        result.Authentication == AuthenticationState.Authenticated || !string.IsNullOrWhiteSpace(result.AuthenticationMethod);

    /// <summary>The name and, when parked, that it is: what a screen reader says for the card, instead of every field of the record.</summary>
    public override string ToString() => IsParked ? $"{Name} (aparcado)" : Name;
}
