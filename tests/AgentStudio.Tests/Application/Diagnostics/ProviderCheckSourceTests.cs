using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Tests.Application.Diagnostics;

/// <summary>The adapter of the probes of the providers to checks (docs/20 D-01, F6-1): installation, version, untested version and session.</summary>
public sealed class ProviderCheckSourceTests
{
    private sealed class Probe(string id, ProviderProbeResult result) : IProviderProbe
    {
        public int Runs { get; private set; }

        public ProviderId Provider { get; } = new(id);

        public VendorId Vendor => VendorId.Anthropic;

        public string DisplayName => result.DisplayName;

        public Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            Runs++;
            return Task.FromResult(result);
        }
    }

    private static ProviderProbeResult Result(
        string id,
        InstallationState installation,
        AuthenticationState authentication = AuthenticationState.Authenticated,
        string? version = "2.1.290",
        string? method = "claude.ai",
        string? insideProject = null) =>
        new(new ProviderId(id), "Claude Code", installation, installation == InstallationState.Installed ? @"C:\Users\x\AppData\Roaming\npm\claude.cmd" : null, version, authentication, method, [], insideProject);

    private static async Task<IReadOnlyDictionary<string, DoctorCheck>> ChecksAsync(ProviderProbeResult result, ProviderCheckOptions? options = null)
    {
        var source = new ProviderCheckSource(new ProviderDiagnostics([new Probe(result.Provider.Value, result)]), options);
        return (await source.CheckAsync(null, CancellationToken.None)).ToDictionary(c => c.Code);
    }

    [Fact]
    public async Task An_installed_provider_with_a_session_gives_three_correct_checks_with_its_version_and_path()
    {
        var checks = await ChecksAsync(Result("claude-code", InstallationState.Installed));

        Assert.Equal(["doctor.provider.claude-code.install", "doctor.provider.claude-code.version", "doctor.provider.claude-code.session"], checks.Keys.ToArray());
        Assert.All(checks.Values, c => Assert.Equal(DoctorStatus.Ok, c.Status));
        Assert.All(checks.Values, c => Assert.Equal(DoctorSection.Providers, c.Section));
        Assert.All(checks.Values, c => Assert.Equal("Claude Code", c.Fact("name")));
        Assert.Equal("2.1.290", checks["doctor.provider.claude-code.version"].Fact(DoctorFactNames.Version));
        Assert.Equal(@"C:\Users\x\AppData\Roaming\npm\claude.cmd", checks["doctor.provider.claude-code.install"].Fact(DoctorFactNames.Path));
        Assert.Equal("claude.ai", checks["doctor.provider.claude-code.session"].Fact("method"));
    }

    [Fact]
    public async Task A_provider_that_is_not_installed_is_a_warning_with_its_guide_and_has_no_version_or_session_check()
    {
        var checks = await ChecksAsync(Result("codex-cli", InstallationState.NotInstalled, AuthenticationState.Unknown, version: null, method: null));

        var install = Assert.Single(checks.Values);
        Assert.Equal(DoctorStatus.Warning, install.Status);
        Assert.Equal("doctor.fix.install-codex-cli", install.FixCode);
        Assert.IsType<DoctorFix.Guide>(DoctorFixCatalog.Find(install.FixCode));
    }

    [Fact]
    public async Task A_provider_found_only_inside_the_project_says_where()
    {
        var checks = await ChecksAsync(Result("claude-code", InstallationState.NotInstalled, AuthenticationState.Unknown, null, null, insideProject: @"C:\work\calc\claude.cmd"));

        Assert.Equal(@"C:\work\calc\claude.cmd", checks["doctor.provider.claude-code.install"].Fact("inside-project"));
    }

    [Fact]
    public async Task A_provider_that_fails_to_answer_its_version_is_an_error()
    {
        var checks = await ChecksAsync(Result("claude-code", InstallationState.Error, AuthenticationState.Unknown, null, null));

        Assert.Equal(DoctorStatus.Error, checks["doctor.provider.claude-code.install"].Status);
        Assert.Single(checks);
    }

    [Fact]
    public async Task An_untested_version_is_a_warning_and_a_missing_version_is_not_checked()
    {
        var untested = await ChecksAsync(Result("claude-code", InstallationState.Installed, version: "9.9.9"), new ProviderCheckOptions(IsUntested: r => r.Version == "9.9.9"));
        var none = await ChecksAsync(Result("claude-code", InstallationState.Installed, version: null));

        Assert.Equal(DoctorStatus.Warning, untested["doctor.provider.claude-code.version"].Status);
        Assert.Equal(DoctorStatus.Unknown, none["doctor.provider.claude-code.version"].Status);
    }

    [Theory]
    [InlineData(AuthenticationState.Authenticated, DoctorStatus.Ok)]
    [InlineData(AuthenticationState.NotAuthenticated, DoctorStatus.Error)]
    [InlineData(AuthenticationState.Unknown, DoctorStatus.Warning)]
    public async Task The_session_has_the_state_the_row_of_today_gives_it(AuthenticationState authentication, DoctorStatus expected)
    {
        var checks = await ChecksAsync(Result("claude-code", InstallationState.Installed, authentication));

        Assert.Equal(expected, checks["doctor.provider.claude-code.session"].Status);
    }

    [Fact]
    public async Task A_parked_provider_is_neutral_in_every_check_so_the_section_does_not_warn_for_it()
    {
        var checks = await ChecksAsync(
            Result("gemini-cli", InstallationState.Installed, AuthenticationState.Unknown, method: null),
            new ProviderCheckOptions(IsParked: r => r.Provider == ProviderId.GeminiCli));

        Assert.All(checks.Values, c => Assert.Equal(DoctorStatus.NotApplicable, c.Status));
        Assert.Equal(DoctorStatus.NotApplicable, new DoctorReport([.. checks.Values]).Worst);
    }

    [Fact]
    public void An_unusual_id_of_provider_still_gives_a_valid_code()
    {
        var source = new ProviderCheckSource(new ProviderDiagnostics([]));
        var odd = Result("My Provider_2!", InstallationState.Installed);

        var codes = source.Checks(odd).Select(c => c.Code).ToArray();

        Assert.Equal("doctor.provider.my-provider-2.install", codes[0]);
        Assert.All(codes, c => Assert.True(DoctorCodes.IsCode(c)));
    }

    [Fact]
    public async Task Two_runs_that_overlap_share_one_launch_of_the_probes_and_a_later_one_launches_again_unless_it_accepts_recent_results()
    {
        var probe = new SlowProbe();
        var diagnostics = new ProviderDiagnostics([probe]);

        var first = diagnostics.RunAsync(CancellationToken.None);
        var second = diagnostics.RunAsync(CancellationToken.None);
        await Task.WhenAll(first, second);
        Assert.Equal(1, probe.Runs);

        await diagnostics.RunAsync(CancellationToken.None);
        Assert.Equal(2, probe.Runs);

        await diagnostics.RunAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(2, probe.Runs);
    }

    [Fact]
    public async Task A_caller_that_cancels_leaves_the_shared_run_to_the_others()
    {
        var probe = new SlowProbe();
        var diagnostics = new ProviderDiagnostics([probe]);
        using var cancel = new CancellationTokenSource();

        var leaving = diagnostics.RunAsync(cancel.Token);
        var staying = diagnostics.RunAsync(CancellationToken.None);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);
        Assert.Single(await staying);
    }

    private sealed class SlowProbe : IProviderProbe
    {
        public int Runs;

        public ProviderId Provider { get; } = new("claude-code");

        public VendorId Vendor => VendorId.Anthropic;

        public string DisplayName => "Agent";

        public async Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Runs);
            await Task.Delay(100, CancellationToken.None);
            return new ProviderProbeResult(Provider, "Agent", InstallationState.Installed, null, "1.0", AuthenticationState.Authenticated, null, []);
        }
    }

    [Fact]
    public async Task The_probes_run_once_per_run_and_their_results_are_kept_for_the_rows_of_today()
    {
        var probe = new Probe("claude-code", Result("claude-code", InstallationState.Installed));
        var source = new ProviderCheckSource(new ProviderDiagnostics([probe]));

        await new DoctorService([source]).RunAsync(null, CancellationToken.None);

        Assert.Equal(1, probe.Runs);
        Assert.Single(source.LastResults);
    }
}
