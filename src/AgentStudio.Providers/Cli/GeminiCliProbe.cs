using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Providers.Cli;

/// <summary>
/// Checks Gemini CLI with <c>gemini --version</c> only: it has no documented session-status command that spends no
/// quota, so authentication is always unknown. Google stopped serving personal accounts in Gemini CLI on 2026-06-18,
/// so for them it only works with an API key; the provider is parked until there is one (ADR-0009).
/// </summary>
public sealed class GeminiCliProbe : CliProbe
{
    private static readonly string[] AuthVariables = ["GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_GENAI_USE_VERTEXAI"];

    /// <summary>Shown in Auto Doctor: why signing in with a personal Google account no longer works.</summary>
    public const string PersonalAccountsNote =
        "Google dejó de admitir cuentas personales en Gemini CLI el 18-06-2026: con una cuenta personal solo funciona con una API key (GEMINI_API_KEY).";

    private readonly Func<string, string?> _environment;

    public GeminiCliProbe(IExecutableLocator locator, IProcessRunner runner, Func<string, string?>? environment = null)
        : base(locator, runner)
    {
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    public override ProviderId Provider => ProviderId.GeminiCli;

    public override VendorId Vendor => VendorId.Google;

    public override string DisplayName => "Gemini CLI";

    protected override string Command => "gemini";

    protected override Task<CliAuthStatus> CheckAuthenticationAsync(string executablePath, List<string> notes, CancellationToken cancellationToken)
    {
        notes.Add("Gemini CLI no documenta un comando de estado de sesión que no consuma cuota.");
        notes.Add(PersonalAccountsNote);

        // Only the names are reported, never the values.
        var present = AuthVariables.Where(name => !string.IsNullOrEmpty(_environment(name))).ToList();
        if (present.Count > 0)
        {
            notes.Add($"Variables de entorno de autenticación definidas: {string.Join(", ", present)}.");
        }

        // With one of them defined there is a way to authenticate (ADR-0009: the provider comes back with an API key), and the
        // method says which variables, by name. The session itself stays unknown: checking it would spend quota.
        var method = present.Count > 0 ? $"Variables de entorno ({string.Join(", ", present)})" : null;
        return Task.FromResult(new CliAuthStatus(AuthenticationState.Unknown, method));
    }
}
