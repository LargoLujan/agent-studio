using System.Globalization;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Providers.Cli.Codex.AppServer;

/// <summary>
/// Reads the subscription quota of Codex from its app-server (docs/14 D-15), only when the user opted in: the one read the
/// probe rule of <c>CLAUDE.md</c> allows besides the version and session checks (user's OK, 2026-10-18). It is a GET of
/// the backend with the CLI's own credentials, never a turn of the model.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Without the opt-in nothing is looked up nor launched (<see cref="QuotaReadStatus.NotOptedIn"/>).</item>
/// <item><c>codex --version</c> says whether the answer of this version was tested (<see cref="SupportedVersions.QuotaReadTested"/>);
/// another one is read with the warning «versión de Codex no probada», as Auto Doctor does.</item>
/// <item>The native <c>codex.exe</c> (<see cref="CodexExecutableResolver"/>) runs <see cref="Arguments"/> inside the Job
/// Object of the process runner, in a new empty folder of the run (<see cref="RunTempFolder"/>), never a repository, so no
/// <c>.codex/</c> layer of a project is loaded. It still reads the user's <c>~/.codex/config.toml</c>: the app-server has
/// no <c>--ignore-user-config</c>. Agent Studio never reads that folder.</item>
/// <item>Its standard input gets the closed list of <see cref="Messages"/>, constant text: <c>initialize</c>,
/// <c>initialized</c> and <c>account/rateLimits/read</c> with <c>excludeResetCreditDetails</c> (no second request to the
/// backend) and never <c>supportsLunaReserve</c>. Nothing else is ever sent: never
/// <c>account/rateLimitResetCredit/consume</c>, which spends a stored reset, nor <c>account/sendAddCreditsNudgeEmail</c>,
/// which sends an email. The input is not closed after them: measured on 0.160.0, the app-server exits at the end of its
/// input before the read answers (<see cref="ProcessSpec.KeepStandardInputOpen"/>).</item>
/// <item>The answer (id 2) is read by <see cref="CodexRateLimitsParser"/>'s whitelist within <see cref="ResponseTimeout"/>,
/// and the process tree is ended as soon as it arrives. No answer in time is «sin dato» with its reason. Its standard
/// error is never kept: it can carry paths of the user's profile.</item>
/// </list>
/// The runner closes the standard input once the three lines are written. The app-server of 0.160.0 processes requests in
/// order, awaiting each one inline, so the read is answered before the end of the input closes the connection: read in its
/// code, not measured (the live test <c>CodexQuotaLiveTests</c> is the measurement).
/// </remarks>
public sealed class CodexAppServerQuotaReader : IProviderQuotaReader
{
    /// <summary>How long the answer of the read is awaited (D-15).</summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The name Agent Studio gives itself in <c>clientInfo</c>.</summary>
    public const string ClientName = "agent_studio";

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(20);

    /// <summary>A margin after which the runner itself ends the tree, should the reader not.</summary>
    private static readonly TimeSpan ProcessGrace = TimeSpan.FromSeconds(10);

    private readonly IExecutableLocator _locator;
    private readonly IStreamingProcessRunner _runner;
    private readonly IProcessRunner _probes;
    private readonly Func<bool> _optedIn;
    private readonly TimeProvider _time;
    private readonly string _clientVersion;

    /// <param name="locator">Finds <c>codex</c> on the PATH.</param>
    /// <param name="runner">Starts the app-server (Job Object).</param>
    /// <param name="probes">Runs <c>codex --version</c>.</param>
    /// <param name="optedIn">The user's opt-in, read on every call (the setting lives in the desktop settings).</param>
    /// <param name="time">The clock of the observations and of the time limit.</param>
    /// <param name="clientVersion">The version Agent Studio announces; by default the version of this assembly.</param>
    public CodexAppServerQuotaReader(IExecutableLocator locator, IStreamingProcessRunner runner, IProcessRunner probes, Func<bool> optedIn, TimeProvider time, string? clientVersion = null)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(optedIn);
        ArgumentNullException.ThrowIfNull(time);
        _locator = locator;
        _runner = runner;
        _probes = probes;
        _optedIn = optedIn;
        _time = time;
        _clientVersion = VersionToken(clientVersion ?? typeof(CodexAppServerQuotaReader).Assembly.GetName().Version?.ToString(3));
    }

    /// <summary>The surface of the launch: the app-server over standard input and output, JSON-RPC one message per line.</summary>
    public static IReadOnlyList<string> Arguments { get; } = ["app-server", "--listen", "stdio://"];

    public ProviderId Provider => ProviderId.CodexCli;

    /// <summary>The closed list of messages, in order and as constant text (only the version of Agent Studio varies, as a token).</summary>
    public static IReadOnlyList<string> Messages(string clientVersion)
    {
        var version = VersionToken(clientVersion);
        return
        [
            "{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"" + ClientName + "\",\"title\":\"Agent Studio\",\"version\":\"" + version + "\"}}}",
            "{\"method\":\"initialized\",\"params\":{}}",
            "{\"method\":\"account/rateLimits/read\",\"id\":2,\"params\":{\"excludeResetCreditDetails\":true}}",
        ];
    }

    public async Task<QuotaReadResult> ReadAsync(CancellationToken ct)
    {
        if (!_optedIn())
        {
            return new QuotaReadResult(QuotaReadStatus.NotOptedIn, [], "La lectura de la cuota de Codex está desactivada: actívala en Ajustes.");
        }

        if (_locator.Find("codex") is not { } located)
        {
            return CodexRateLimitsParser.Missing("Codex CLI no está instalado o no está en el PATH");
        }

        var executable = CodexExecutableResolver.Resolve(located);
        var warnings = await VersionWarningsAsync(executable, ct).ConfigureAwait(false);
        var folder = RunTempFolder.Create();
        try
        {
            var spec = new ProcessSpec(
                executable,
                Arguments,
                folder,
                Timeout: ResponseTimeout + ProcessGrace,
                StandardInput: string.Join('\n', Messages(_clientVersion)) + "\n",

                // Measured on 0.160.0 (2026-10-18): at the end of its input the app-server exits before the read answers.
                // The input stays open until the answer comes; disposing the session then ends the tree.
                KeepStandardInputOpen: true);
            using var limit = new CancellationTokenSource(ResponseTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
            IProcessSession session;
            try
            {
                session = await _runner.StartAsync(spec, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return CodexRateLimitsParser.Missing("no se pudo arrancar el app-server de Codex: " + CodexRateLimitsParser.Clean(ex.Message)) with { Warnings = warnings };
            }

            await using (session.ConfigureAwait(false))
            {
                try
                {
                    await foreach (var line in session.Output.WithCancellation(linked.Token).ConfigureAwait(false))
                    {
                        if (line.Stream == ProcessStream.StandardOutput
                            && CodexRateLimitsParser.FromLine(line.Text, _time.GetUtcNow()) is { } result)
                        {
                            // Disposing the session ends the tree: the app-server is not left running after the answer.
                            return result with { Warnings = [.. warnings, .. result.Warnings] };
                        }
                    }
                }
                catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    return CodexRateLimitsParser.Missing(string.Create(CultureInfo.InvariantCulture, $"el app-server de Codex no respondió en {ResponseTimeout.TotalSeconds:0} s")) with { Warnings = warnings };
                }

                return CodexRateLimitsParser.Missing("el app-server de Codex terminó sin responder") with { Warnings = warnings };
            }
        }
        finally
        {
            RunTempFolder.DeleteQuietly(folder);
        }
    }

    /// <summary>The warning of a version whose answer was not tested (or that cannot be read); none for a tested one.</summary>
    private async Task<IReadOnlyList<string>> VersionWarningsAsync(string executable, CancellationToken ct)
    {
        var tested = SupportedVersions.QuotaReadTested(Provider);
        string? version = null;
        try
        {
            var result = await _probes.RunAsync(new ProcessSpec(executable, ["--version"], Timeout: VersionTimeout), ct).ConfigureAwait(false);
            if (!result.TimedOut && result.ExitCode == 0)
            {
                version = CliVersionParser.Parse(result.StandardOutput);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A version that cannot be read is only a warning: the read itself says whether Codex answers.
        catch (Exception)
#pragma warning restore CA1031
        {
            version = null;
        }

        return version is not null && tested.Contains(version, StringComparer.Ordinal)
            ? []
            : [$"Versión de Codex no probada para leer la cuota ({version ?? "desconocida"}; probadas: {string.Join(", ", tested)})."];
    }

    private static string VersionToken(string? version) =>
        version is { Length: > 0 and <= 32 } && version.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '+') ? version : "0.0.0";
}
