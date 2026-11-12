using System.Text;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Diagnostics;

namespace AgentStudio.Application.Diagnostics;

/// <summary>What the adapter of the providers cannot know from a probe: which versions were not tested, and which provider is parked (ADR-0009).</summary>
/// <param name="IsUntested">Whether the version of an installed program is one Agent Studio did not test; none means every version counts as tested.</param>
/// <param name="IsParked">Whether the provider is parked by a decision: neutral, neither ready nor a problem (its checks are «no aplica»).</param>
public sealed record ProviderCheckOptions(Func<ProviderProbeResult, bool>? IsUntested = null, Func<ProviderProbeResult, bool>? IsParked = null);

/// <summary>
/// The sources of providers of the panel (docs/20 D-01): turns what the probes of today say into checks, without rewriting them. For each
/// provider: <c>doctor.provider.&lt;id&gt;.install</c>, and, when it is installed, <c>.version</c> and <c>.session</c>. The probes run once per
/// run (<see cref="ProviderDiagnostics"/>); the results are kept in <see cref="LastResults"/> so the panel can paint the rows it paints
/// today without running them again.
/// </summary>
public sealed class ProviderCheckSource(ProviderDiagnostics diagnostics, ProviderCheckOptions? options = null) : IDoctorCheckSource
{
    private readonly ProviderCheckOptions _options = options ?? new ProviderCheckOptions();
    private IReadOnlyList<ProviderProbeResult> _lastResults = [];

    public DoctorSection Section => DoctorSection.Providers;

    /// <summary>Results of the probes younger than this are used as they are (the panel sets it when it opens just after the start-up check of the top bar); zero runs them again.</summary>
    public TimeSpan ReuseWithin { get; set; }

    /// <summary>The results of the last run of the probes.</summary>
    public IReadOnlyList<ProviderProbeResult> LastResults => _lastResults;

    public async Task<IReadOnlyList<DoctorCheck>> CheckAsync(ProjectContext? project, CancellationToken ct)
    {
        var results = await diagnostics.RunAsync(ReuseWithin, ct).ConfigureAwait(false);
        _lastResults = results;
        return [.. results.SelectMany(Checks)];
    }

    /// <summary>The checks of one result.</summary>
    public IEnumerable<DoctorCheck> Checks(ProviderProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var id = Id(result);
        var parked = _options.IsParked?.Invoke(result) ?? false;
        var name = DoctorFact.Of("name", string.IsNullOrWhiteSpace(result.DisplayName) ? id : result.DisplayName);
        var path = string.IsNullOrWhiteSpace(result.ExecutablePath) ? null : DoctorFact.Of(DoctorFactNames.Path, result.ExecutablePath);
        var version = string.IsNullOrWhiteSpace(result.Version) ? null : DoctorFact.Of(DoctorFactNames.Version, result.Version);

        var installed = result.Installation == InstallationState.Installed;
        var installFacts = new List<DoctorFact> { name };
        if (path is not null)
        {
            installFacts.Add(path);
        }

        if (!installed && !string.IsNullOrWhiteSpace(result.FoundInsideProject))
        {
            installFacts.Add(DoctorFact.Of("inside-project", result.FoundInsideProject));
        }

        yield return new DoctorCheck(
            $"doctor.provider.{id}.install",
            DoctorSection.Providers,
            parked ? DoctorStatus.NotApplicable : result.Installation switch
            {
                InstallationState.Installed => DoctorStatus.Ok,
                InstallationState.NotInstalled => DoctorStatus.Warning,
                _ => DoctorStatus.Error,
            },
            installFacts,
            installed || parked ? null : DoctorFixCatalog.ProviderInstallFixCode(id));

        if (!installed)
        {
            yield break;
        }

        var versionFacts = new List<DoctorFact> { name };
        if (version is not null)
        {
            versionFacts.Add(version);
        }

        var untested = version is not null && (_options.IsUntested?.Invoke(result) ?? false);
        yield return new DoctorCheck(
            $"doctor.provider.{id}.version",
            DoctorSection.Providers,
            parked ? DoctorStatus.NotApplicable : untested ? DoctorStatus.Warning : version is null ? DoctorStatus.Unknown : DoctorStatus.Ok,
            versionFacts);

        var sessionFacts = new List<DoctorFact> { name };
        if (!string.IsNullOrWhiteSpace(result.AuthenticationMethod))
        {
            sessionFacts.Add(DoctorFact.Of("method", result.AuthenticationMethod));
        }

        yield return new DoctorCheck(
            $"doctor.provider.{id}.session",
            DoctorSection.Providers,
            parked ? DoctorStatus.NotApplicable : result.Authentication switch
            {
                AuthenticationState.Authenticated => DoctorStatus.Ok,
                AuthenticationState.NotAuthenticated => DoctorStatus.Error,
                _ => DoctorStatus.Warning,
            },
            sessionFacts,
            !parked && result.Authentication == AuthenticationState.NotAuthenticated ? DoctorFixCatalog.ProviderLoginFixCode(id) : null);
    }

    /// <summary>The provider as a piece of a code: lowercase letters and digits joined by <c>-</c>.</summary>
    public static string Id(ProviderProbeResult result)
    {
        var text = new StringBuilder();
        foreach (var c in result.Provider.Value.ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            {
                text.Append(c);
            }
            else if (text.Length > 0 && text[^1] != '-')
            {
                text.Append('-');
            }
        }

        var id = text.ToString().Trim('-');
        return id.Length == 0 ? "provider" : id.Length > 30 ? id[..30].Trim('-') : id;
    }
}
