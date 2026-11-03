using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Providers.Cli;

/// <summary>Checks Codex CLI with <c>codex --version</c> and <c>codex login status</c> (no prompts, no quota).</summary>
public sealed class CodexCliProbe(IExecutableLocator locator, IProcessRunner runner) : CliProbe(locator, runner)
{
    public override ProviderId Provider => ProviderId.CodexCli;

    public override VendorId Vendor => VendorId.OpenAI;

    public override string DisplayName => "Codex CLI";

    protected override string Command => "codex";

    protected override async Task<CliAuthStatus> CheckAuthenticationAsync(string executablePath, List<string> notes, CancellationToken cancellationToken)
    {
        var run = await RunAsync(executablePath, ["login", "status"], cancellationToken).ConfigureAwait(false);
        if (run.Failure is not null)
        {
            notes.Add($"No se pudo comprobar la sesión: {run.Failure}");
            return new CliAuthStatus(AuthenticationState.Unknown, null);
        }

        var status = CodexLoginStatusParser.Parse(run.Output);
        if (status.State == AuthenticationState.Unknown)
        {
            notes.Add("La salida de «codex login status» no se pudo interpretar.");
        }

        return status;
    }
}
