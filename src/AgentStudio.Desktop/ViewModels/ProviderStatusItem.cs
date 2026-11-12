using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Localization;
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

/// <summary>Display-ready projection of a <see cref="ProviderProbeResult"/>, in the language of the interface.</summary>
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
    public static string GeminiParkedNote => Texts.Get("Providers_GeminiParked");

    private static string InstalledText => Texts.Get("Providers_Installed");

    /// <summary>
    /// The providers parked by a decision, with the key of what their row says: their check still runs and is shown. One comes
    /// back as a provider like the others as soon as its check finds a way to authenticate (ADR-0009: Gemini, with an API key).
    /// </summary>
    private static readonly Dictionary<ProviderId, string> Parked = new()
    {
        [ProviderId.GeminiCli] = "Providers_GeminiParked",
    };

    /// <summary>Why the provider is parked; null when it is in use. A parked provider is neutral and does not count in the summary.</summary>
    public string? ParkedNote { get; init; }

    public bool IsParked => ParkedNote is not null;

    /// <summary>
    /// The one line of its row in Auto Doctor: «Aparcado» when it is; the version and the session when it is installed; what
    /// went wrong otherwise. The rest (installation, method, path and notes) is in the details of the row.
    /// </summary>
    public string Summary => IsParked ? Texts.Get("Providers_Parked")
        : Installation == InstalledText ? $"{Version} · {Authentication}"
        : Installation;

    /// <summary>This item with the warning that the open folder is too wide for the protection of programs to apply.</summary>
    public ProviderStatusItem WithProjectTooBroad(string folder) =>
        this with { Notes = [Texts.Format("Providers_ProjectTooBroad", PathDisplay.CollapseHome(folder)), .. Notes] };

    public static ProviderStatusItem From(ProviderProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var installation = result.Installation switch
        {
            InstallationState.Installed => InstalledText,
            InstallationState.NotInstalled => Texts.Get("Providers_NotInstalled"),
            _ => Texts.Get("Providers_InstallationError"),
        };

        var authentication = result.Authentication switch
        {
            AuthenticationState.Authenticated => Texts.Get("Providers_SignedIn"),
            AuthenticationState.NotAuthenticated => Texts.Get("Providers_NotSignedIn"),
            _ => Texts.Get("Providers_SessionUnknown"),
        };

        var parkedKey = IsParkedResult(result) ? Parked[result.Provider] : null;
        var parked = parkedKey is null ? null : Texts.Get(parkedKey);
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
            WithInsideProject(result))
        {
            ParkedNote = parked,
        };
    }

    /// <summary>
    /// The notes of the check, and first of them, in the language in use, that the program was found only inside the open project and
    /// so is not used (ADR-0018): the person would otherwise see only «No instalado» for something they did install.
    /// </summary>
    private static IReadOnlyList<string> WithInsideProject(ProviderProbeResult result)
    {
        var notes = result.Notes ?? [];
        return result.FoundInsideProject is { } path
            ? [Texts.Format("Providers_InsideProject", PathDisplay.CollapseHome(path)), .. notes]
            : notes;
    }

    /// <summary>Whether this result is of a provider parked by a decision (ADR-0009) that has found no way to authenticate: Auto Doctor treats its checks as neutral too.</summary>
    public static bool IsParkedResult(ProviderProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return !HasUsableAuthentication(result) && Parked.ContainsKey(result.Provider);
    }

    /// <summary>
    /// Whether the check found a way to authenticate: a session it verified, or a method it names (for Gemini, the API key
    /// variables it found defined, by name). Only what the check says counts: no value of a key is read here.
    /// </summary>
    private static bool HasUsableAuthentication(ProviderProbeResult result) =>
        result.Authentication == AuthenticationState.Authenticated || !string.IsNullOrWhiteSpace(result.AuthenticationMethod);

    /// <summary>The name and, when parked, that it is: what a screen reader says for the card, instead of every field of the record.</summary>
    public override string ToString() => IsParked ? Texts.Format("Providers_ParkedName", Name) : Name;
}
