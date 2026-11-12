using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Tests.Desktop;

public sealed class ProviderDiagnosticsViewModelTests
{
    private static ProviderDiagnosticsViewModel Create(TimeProvider? time, params IProviderProbe[] probes) =>
        new(new ProviderDiagnostics(probes), time ?? TimeProvider.System);

    private static ProviderProbeResult Result(
        string name,
        InstallationState installation,
        AuthenticationState authentication,
        string? version = "1.2.3",
        string? method = "oauth",
        string? path = @"C:\tools\x.exe",
        string[]? notes = null) =>
        new(new ProviderId(name), name, installation, path, version, authentication, method, notes ?? []);

    [Theory]
    [InlineData(InstallationState.Installed, AuthenticationState.Authenticated, StatusKind.Ok, "Instalado", "Sesión iniciada")]
    [InlineData(InstallationState.Installed, AuthenticationState.Unknown, StatusKind.Warning, "Instalado", "Desconocida")]
    [InlineData(InstallationState.Installed, AuthenticationState.NotAuthenticated, StatusKind.Error, "Instalado", "Sin sesión")]
    [InlineData(InstallationState.NotInstalled, AuthenticationState.Unknown, StatusKind.Warning, "No instalado", "Desconocida")]
    [InlineData(InstallationState.Error, AuthenticationState.Unknown, StatusKind.Error, "Error", "Desconocida")]
    public async Task Refresh_MapsTextsAndStatusKind(
        InstallationState installation,
        AuthenticationState authentication,
        StatusKind expectedKind,
        string expectedInstallation,
        string expectedAuthentication)
    {
        var vm = Create(null, new FakeProbe(Result("p", installation, authentication, notes: ["nota"])));

        await vm.RefreshCommand.ExecuteAsync(null);

        var item = Assert.Single(vm.Providers);
        Assert.Equal("p", item.Name);
        Assert.Equal(expectedKind, item.Status);
        Assert.Equal(expectedInstallation, item.Installation);
        Assert.Equal(expectedAuthentication, item.Authentication);
        Assert.Equal("1.2.3", item.Version);
        Assert.Equal("oauth", item.Method);
        Assert.Equal(@"C:\tools\x.exe", item.ExecutablePath);
        Assert.Equal(["nota"], item.Notes);
        Assert.False(vm.IsEmpty);
        Assert.Null(vm.ErrorMessage);
    }

    /// <summary>
    /// Gemini is parked by a decision (ADR-0009): its row is neutral and says so in one line, and what its check found is still
    /// there, folded under its details.
    /// </summary>
    [Fact]
    public async Task Refresh_Gemini_IsParked_NeutralAndWithWhatItsCheckFound()
    {
        // As the probe of Gemini CLI reports it without an API key: the session unknown and no method.
        var vm = Create(null, new FakeProbe(Result(ProviderId.GeminiCli.Value, InstallationState.Installed, AuthenticationState.Unknown, method: null, notes: ["nota"])));

        await vm.RefreshCommand.ExecuteAsync(null);

        var item = Assert.Single(vm.Providers);
        Assert.True(item.IsParked);
        Assert.Equal(StatusKind.Parked, item.Status);
        Assert.Equal(ProviderStatusItem.GeminiParkedNote, item.ParkedNote);
        Assert.Contains("ADR-0009", item.ParkedNote, StringComparison.Ordinal);
        Assert.Equal("Aparcado", item.Summary);
        Assert.Equal(("Instalado", "1.2.3", "Desconocida", "—"), (item.Installation, item.Version, item.Authentication, item.Method));
        Assert.Equal(["nota"], item.Notes);
        Assert.Equal($"{ProviderId.GeminiCli.Value} (aparcado)", item.ToString());
    }

    /// <summary>
    /// ADR-0009: Gemini comes back with an API key. When its probe names a way to authenticate (the variables it found defined,
    /// never their values), it is a provider like the others: not parked, and with its session unknown, a warning.
    /// </summary>
    [Fact]
    public async Task Refresh_Gemini_WithAnApiKeyItsProbeFound_IsAProviderLikeTheOthers()
    {
        var vm = Create(null, new FakeProbe(Result(
            ProviderId.GeminiCli.Value,
            InstallationState.Installed,
            AuthenticationState.Unknown,
            method: "Variables de entorno (GEMINI_API_KEY)")));

        await vm.RefreshCommand.ExecuteAsync(null);

        var item = Assert.Single(vm.Providers);
        Assert.False(item.IsParked);
        Assert.Null(item.ParkedNote);
        Assert.Equal(StatusKind.Warning, item.Status);
        Assert.Equal("1.2.3 · Desconocida", item.Summary);
        Assert.Equal("Variables de entorno (GEMINI_API_KEY)", item.Method);
        Assert.Equal(ProviderSummary.From(vm), new ProviderSummary("Programas: 0 de 1 listos", StatusKind.Warning));
    }

    [Fact]
    public async Task Refresh_Gemini_WithAVerifiedSession_IsNotParkedEither()
    {
        var vm = Create(null, new FakeProbe(Result(ProviderId.GeminiCli.Value, InstallationState.Installed, AuthenticationState.Authenticated, method: null)));

        await vm.RefreshCommand.ExecuteAsync(null);

        var item = Assert.Single(vm.Providers);
        Assert.False(item.IsParked);
        Assert.Equal(StatusKind.Ok, item.Status);
    }

    [Theory]
    [InlineData(InstallationState.Installed, AuthenticationState.Authenticated, "1.2.3 · Sesión iniciada")]
    [InlineData(InstallationState.Installed, AuthenticationState.NotAuthenticated, "1.2.3 · Sin sesión")]
    [InlineData(InstallationState.NotInstalled, AuthenticationState.Unknown, "No instalado")]
    [InlineData(InstallationState.Error, AuthenticationState.Unknown, "Error")]
    public async Task Refresh_AProviderInUse_SaysItsVersionAndSessionInOneLine(InstallationState installation, AuthenticationState authentication, string expected)
    {
        var vm = Create(null, new FakeProbe(Result("claude-code", installation, authentication)));

        await vm.RefreshCommand.ExecuteAsync(null);

        var item = Assert.Single(vm.Providers);
        Assert.False(item.IsParked);
        Assert.Null(item.ParkedNote);
        Assert.Equal(expected, item.Summary);
        Assert.Equal("claude-code", item.ToString());
    }

    [Fact]
    public async Task Refresh_MissingVersionAndMethod_ShowDash()
    {
        var vm = Create(null, new FakeProbe(Result("p", InstallationState.NotInstalled, AuthenticationState.Unknown, null, null, null)));

        await vm.RefreshCommand.ExecuteAsync(null);

        var item = Assert.Single(vm.Providers);
        Assert.Equal("—", item.Version);
        Assert.Equal("—", item.Method);
        Assert.Equal(string.Empty, item.ExecutablePath);
    }

    [Fact]
    public async Task Refresh_IsBusyAndNotExecutableWhileProbeRuns()
    {
        var gate = new TaskCompletionSource<ProviderProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(null, new FakeProbe(gate.Task));

        var running = vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.IsBusy);
        Assert.False(vm.RefreshCommand.CanExecute(null));

        gate.SetResult(Result("p", InstallationState.Installed, AuthenticationState.Authenticated));
        await running;

        Assert.False(vm.IsBusy);
        Assert.True(vm.RefreshCommand.CanExecute(null));
        Assert.Single(vm.Providers);
    }

    [Fact]
    public async Task Refresh_ThrowingProbe_DoesNotHideOthers()
    {
        var vm = Create(
            null,
            new FakeProbe(Result("ok", InstallationState.Installed, AuthenticationState.Authenticated)),
            new FakeProbe(Task.FromException<ProviderProbeResult>(new InvalidOperationException("boom"))));

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Providers.Count);
        Assert.Equal(StatusKind.Ok, vm.Providers[0].Status);
        Assert.Equal(StatusKind.Error, vm.Providers[1].Status);
        Assert.Contains("boom", Assert.Single(vm.Providers[1].Notes));
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Refresh_NoProbes_ShowsEmptyState()
    {
        var vm = Create(null);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(vm.Providers);
        Assert.True(vm.IsEmpty);
        Assert.NotNull(vm.LastCheckedAt);
    }

    [Fact]
    public async Task Refresh_UsesInjectedTimeProvider()
    {
        var now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var vm = Create(new FixedTimeProvider(now));

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(now, vm.LastCheckedAt);
    }

    [Fact]
    public async Task Refresh_ReplacesPreviousResults()
    {
        var vm = Create(null, new FakeProbe(Result("p", InstallationState.Installed, AuthenticationState.Authenticated)));

        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Single(vm.Providers);
    }

    private sealed class FakeProbe : IProviderProbe
    {
        private readonly Task<ProviderProbeResult> _result;

        public FakeProbe(ProviderProbeResult result)
            : this(Task.FromResult(result))
        {
            Provider = result.Provider;
            DisplayName = result.DisplayName;
        }

        public FakeProbe(Task<ProviderProbeResult> result)
        {
            _result = result;
            Provider = new ProviderId("fake");
            DisplayName = "fake";
        }

        public ProviderId Provider { get; }

        public VendorId Vendor => VendorId.Anthropic;

        public string DisplayName { get; }

        public Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken) => _result;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
