using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Providers;

public enum InstallationState
{
    Installed,
    NotInstalled,
    Error,
}

public enum AuthenticationState
{
    Authenticated,
    NotAuthenticated,

    /// <summary>The tool has no documented way to check it without spending quota.</summary>
    Unknown,
}

/// <summary>
/// Result of checking one provider tool. It never contains secrets or personal data (no tokens, e-mails or
/// organisation names): only what is needed to tell the user whether the tool is ready.
/// </summary>
public sealed record ProviderProbeResult(
    ProviderId Provider,
    string DisplayName,
    InstallationState Installation,
    string? ExecutablePath,
    string? Version,
    AuthenticationState Authentication,
    string? AuthenticationMethod,
    IReadOnlyList<string> Notes);

/// <summary>
/// Detects whether a provider tool is installed, which version it is and whether the user is signed in, using only
/// official commands that do not send prompts (for example <c>claude auth status</c> or <c>codex login status</c>).
/// </summary>
public interface IProviderProbe
{
    ProviderId Provider { get; }

    VendorId Vendor { get; }

    string DisplayName { get; }

    Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken);
}
