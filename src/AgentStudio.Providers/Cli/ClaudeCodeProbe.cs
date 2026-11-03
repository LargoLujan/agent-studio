using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Providers.Cli;

/// <summary>Checks Claude Code with <c>claude --version</c> and <c>claude auth status</c> (no prompts, no quota).</summary>
public sealed class ClaudeCodeProbe(IExecutableLocator locator, IProcessRunner runner) : CliProbe(locator, runner)
{
    public override ProviderId Provider => ProviderId.ClaudeCode;

    public override VendorId Vendor => VendorId.Anthropic;

    public override string DisplayName => "Claude Code";

    protected override string Command => "claude";

    protected override async Task<CliAuthStatus> CheckAuthenticationAsync(string executablePath, List<string> notes, CancellationToken cancellationToken)
    {
        var run = await RunAsync(executablePath, ["auth", "status"], cancellationToken).ConfigureAwait(false);
        if (run.Failure is not null)
        {
            notes.Add($"No se pudo comprobar la sesión: {run.Failure}");
            return new CliAuthStatus(AuthenticationState.Unknown, null);
        }

        // Exit code 1 means "not signed in" but the JSON is still printed, so the output decides.
        var status = ClaudeAuthStatusParser.Parse(run.Result?.StandardOutput);
        if (status.State == AuthenticationState.Unknown)
        {
            notes.Add("La salida de «claude auth status» no se pudo interpretar.");
        }

        return status;
    }
}
