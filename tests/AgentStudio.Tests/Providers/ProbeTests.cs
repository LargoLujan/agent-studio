using System.Diagnostics;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Providers.Cli;

namespace AgentStudio.Tests.Providers;

public sealed class ProbeTests
{
    private const string ClaudeJson =
        """{"loggedIn": true, "authMethod": "claude.ai", "apiProvider": "firstParty", "projectsDirectory": "C:\\Users\\x\\.claude\\projects", "email": "persona@example.com", "orgId": "org-123", "orgName": "Mi Organizacion", "subscriptionType": "max"}""";

    private sealed class FakeLocator(string? path) : IExecutableLocator
    {
        public string? Find(string command) => path;
    }

    private sealed class FakeRunner(Func<ProcessSpec, ProcessResult> respond) : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
        {
            Calls.Add(spec);
            return Task.FromResult(respond(spec));
        }
    }

    private static ProcessResult Ok(string stdout, int exit = 0) => new(exit, stdout, "", TimeSpan.FromMilliseconds(5), false);

    [Fact]
    public async Task Claude_probe_reports_version_and_auth_without_personal_data()
    {
        var runner = new FakeRunner(spec => spec.Arguments[0] == "--version" ? Ok("2.1.290 (Claude Code)") : Ok(ClaudeJson));
        var probe = new ClaudeCodeProbe(new FakeLocator("C:/bin/claude.exe"), runner);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(InstallationState.Installed, result.Installation);
        Assert.Equal("2.1.290", result.Version);
        Assert.Equal(AuthenticationState.Authenticated, result.Authentication);
        Assert.Equal("claude.ai (suscripción max)", result.AuthenticationMethod);
        Assert.Equal(ProviderId.ClaudeCode, result.Provider);
        Assert.Equal(VendorId.Anthropic, probe.Vendor);
        Assert.Equal(["--version"], runner.Calls[0].Arguments);
        Assert.Equal(["auth", "status"], runner.Calls[1].Arguments);
        Assert.All(runner.Calls, c => Assert.Equal(TimeSpan.FromSeconds(20), c.Timeout));

        var everything = string.Join('|', result.Notes) + result.AuthenticationMethod + result.ExecutablePath + result.Version + result.DisplayName;
        Assert.DoesNotContain("persona@example.com", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("org-123", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Mi Organizacion", everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Claude_probe_logged_out_exit_code_one_is_not_authenticated()
    {
        var runner = new FakeRunner(spec => spec.Arguments[0] == "--version"
            ? Ok("2.1.290 (Claude Code)")
            : Ok("""{"loggedIn": false, "authMethod": "none"}""", exit: 1));
        var probe = new ClaudeCodeProbe(new FakeLocator("C:/bin/claude.exe"), runner);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(InstallationState.Installed, result.Installation);
        Assert.Equal(AuthenticationState.NotAuthenticated, result.Authentication);
    }

    [Fact]
    public async Task Version_with_nonzero_exit_code_is_an_error_even_if_output_looks_like_a_version()
    {
        var runner = new FakeRunner(_ => Ok("2.1.290 (Claude Code)", exit: 3));
        var probe = new ClaudeCodeProbe(new FakeLocator("C:/bin/claude.exe"), runner);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(InstallationState.Error, result.Installation);
        Assert.Contains("«--version» terminó con código 3.", result.Notes);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Missing_executable_is_not_installed_and_runs_nothing()
    {
        var runner = new FakeRunner(_ => throw new InvalidOperationException("no debe ejecutarse"));

        foreach (var probe in new IProviderProbe[]
        {
            new ClaudeCodeProbe(new FakeLocator(null), runner),
            new CodexCliProbe(new FakeLocator(null), runner),
            new GeminiCliProbe(new FakeLocator(null), runner),
        })
        {
            var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

            Assert.Equal(InstallationState.NotInstalled, result.Installation);
            Assert.Null(result.ExecutablePath);
            Assert.Null(result.Version);
            Assert.NotEmpty(result.Notes);
        }

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Version_timeout_is_an_error_with_a_note()
    {
        var runner = new FakeRunner(_ => new ProcessResult(-1, "", "", TimeSpan.FromSeconds(20), TimedOut: true));
        var probe = new ClaudeCodeProbe(new FakeLocator("C:/bin/claude.exe"), runner);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(InstallationState.Error, result.Installation);
        Assert.Null(result.Version);
        Assert.Contains(result.Notes, n => n.Contains("tiempo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Auth_timeout_leaves_authentication_unknown()
    {
        var runner = new FakeRunner(spec => spec.Arguments[0] == "--version"
            ? Ok("codex-cli 0.160.0")
            : new ProcessResult(-1, "", "", TimeSpan.FromSeconds(20), TimedOut: true));
        var probe = new CodexCliProbe(new FakeLocator("C:/bin/codex.cmd"), runner);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(InstallationState.Installed, result.Installation);
        Assert.Equal("0.160.0", result.Version);
        Assert.Equal(AuthenticationState.Unknown, result.Authentication);
        Assert.NotEmpty(result.Notes);
    }

    [Fact]
    public async Task Runner_exception_becomes_an_error_result()
    {
        var runner = new FakeRunner(_ => throw new System.ComponentModel.Win32Exception("no se pudo iniciar"));
        var probe = new CodexCliProbe(new FakeLocator("C:/bin/codex.cmd"), runner);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(InstallationState.Error, result.Installation);
        Assert.NotEmpty(result.Notes);
    }

    [Fact]
    public async Task Codex_probe_reads_login_status()
    {
        var runner = new FakeRunner(spec => spec.Arguments[0] == "--version" ? Ok("codex-cli 0.160.0") : Ok("Logged in using ChatGPT"));
        var probe = new CodexCliProbe(new FakeLocator("C:/bin/codex.cmd"), runner);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("0.160.0", result.Version);
        Assert.Equal(AuthenticationState.Authenticated, result.Authentication);
        Assert.Equal("ChatGPT", result.AuthenticationMethod);
        Assert.Equal(VendorId.OpenAI, probe.Vendor);
        Assert.Equal(["login", "status"], runner.Calls[1].Arguments);
    }

    [Fact]
    public async Task Gemini_probe_only_runs_version_and_notes_variable_names_without_values()
    {
        var runner = new FakeRunner(_ => Ok("0.9.0"));
        var env = new Dictionary<string, string> { ["GEMINI_API_KEY"] = "valor-secreto-123", ["GOOGLE_API_KEY"] = "" };
        var probe = new GeminiCliProbe(new FakeLocator("C:/bin/gemini.cmd"), runner, name => env.GetValueOrDefault(name));

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(InstallationState.Installed, result.Installation);
        Assert.Equal("0.9.0", result.Version);
        Assert.Equal(AuthenticationState.Unknown, result.Authentication);
        Assert.Single(runner.Calls);
        Assert.Equal(["--version"], runner.Calls[0].Arguments);
        Assert.Contains("Gemini CLI no documenta un comando de estado de sesión que no consuma cuota.", result.Notes);
        Assert.Contains(GeminiCliProbe.PersonalAccountsNote, result.Notes);
        Assert.Contains("Variables de entorno de autenticación definidas: GEMINI_API_KEY.", result.Notes);
        var notes = string.Join('|', result.Notes);
        Assert.DoesNotContain("GOOGLE_API_KEY", notes, StringComparison.Ordinal);
        Assert.DoesNotContain("valor-secreto-123", notes, StringComparison.Ordinal);
        Assert.Equal(VendorId.Google, probe.Vendor);

        // The method names the variable, never its value: with it, Auto Doctor no longer shows Gemini as parked (ADR-0009).
        Assert.Equal("Variables de entorno (GEMINI_API_KEY)", result.AuthenticationMethod);
    }

    [Fact]
    public async Task Gemini_probe_without_authentication_variables_reports_no_method()
    {
        var probe = new GeminiCliProbe(new FakeLocator("C:/bin/gemini.cmd"), new FakeRunner(_ => Ok("0.9.0")), _ => null);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AuthenticationState.Unknown, result.Authentication);
        Assert.Null(result.AuthenticationMethod);
    }

    [Fact]
    [Trait("Category", "LocalTools")]
    public async Task Real_probes_run_without_throwing_and_stay_consistent()
    {
        var runner = new ProcessRunner();
        var locator = new ExecutableLocator();
        IProviderProbe[] probes =
        [
            new ClaudeCodeProbe(locator, runner),
            new CodexCliProbe(locator, runner),
            new GeminiCliProbe(locator, runner),
        ];
        var watch = Stopwatch.StartNew();

        var results = await Task.WhenAll(probes.Select(p => p.ProbeAsync(TestContext.Current.CancellationToken)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"Tardó {watch.Elapsed}");
        Assert.Equal(3, results.Length);
        foreach (var result in results)
        {
            if (result.Installation == InstallationState.Installed)
            {
                Assert.NotNull(result.Version);
                Assert.NotNull(result.ExecutablePath);
            }
            else
            {
                Assert.NotEmpty(result.Notes);
            }

            Console.WriteLine($"{result.Provider}: {result.Installation}, version={result.Version}, auth={result.Authentication}, metodo={result.AuthenticationMethod}, notas={result.Notes.Count}");
        }
    }
}
