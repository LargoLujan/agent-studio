using System.Text.RegularExpressions;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Environments;

namespace AgentStudio.Infrastructure.Environments;

/// <summary>
/// The source of checks of the section «declared environment» of Auto Doctor (docs/20 D-07 and D-08): reads what the project declares
/// (<see cref="IDeclaredEnvironmentReader"/>), takes what is installed from the inventory of WP-A (<see cref="IInstalledToolInventory"/>, through its port) and
/// sets one against the other with the pure <see cref="EnvironmentComparer"/>. It launches nothing: the only thing it looks at besides those two is whether a folder
/// <c>&lt;id&gt;\&lt;version&gt;</c> exists in the global NuGet cache for each local tool of <c>dotnet-tools.json</c>, which is looking at a folder, not running
/// <c>dotnet tool</c>. Without an open project it has nothing to say.
/// </summary>
public sealed partial class DeclaredEnvironmentCheckSource : IDoctorCheckSource
{
    private readonly IDeclaredEnvironmentReader _reader;
    private readonly IInstalledToolInventory _inventory;
    private readonly Func<string?> _nuGetCache;

    /// <param name="nuGetCache">The folder of the global NuGet packages; by default <c>NUGET_PACKAGES</c> or <c>%USERPROFILE%\.nuget\packages</c>. A test gives its own, so no test reads the profile of the person.</param>
    public DeclaredEnvironmentCheckSource(IDeclaredEnvironmentReader reader, IInstalledToolInventory inventory, Func<string?>? nuGetCache = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(inventory);
        _reader = reader;
        _inventory = inventory;
        _nuGetCache = nuGetCache ?? DefaultNuGetCache;
    }

    public DoctorSection Section => DoctorSection.DeclaredEnvironment;

    public async Task<IReadOnlyList<DoctorCheck>> CheckAsync(ProjectContext? project, CancellationToken ct)
    {
        if (project is null)
        {
            return [];
        }

        var declared = await _reader.ReadAsync(project.RootPath, ct).ConfigureAwait(false);
        var installed = await _inventory.ReadAsync(project.RootPath, ct).ConfigureAwait(false);
        return EnvironmentComparer.Compare(declared, installed, project.RootPath, LocalToolsInCache(declared));
    }

    /// <summary>The local tools whose package folder is in the cache, by <see cref="EnvironmentComparer.LocalToolKey"/>; null when there is no cache folder to look in.</summary>
    private HashSet<string>? LocalToolsInCache(DeclaredEnvironment declared)
    {
        var tools = declared.Of(DeclaredKind.DotNetLocalTool).ToList();
        if (tools.Count == 0)
        {
            return [];
        }

        var cache = _nuGetCache();
        if (string.IsNullOrWhiteSpace(cache) || !Path.IsPathFullyQualified(cache))
        {
            return null;
        }

        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            // The id and the version came from a file of the repository: each is checked before it becomes part of a path.
            if (tool.Name is { } id && PackageId().IsMatch(id) && ToolVersion.TryParse(tool.Value, out var version))
            {
                var folder = Path.Combine(cache, id.ToLowerInvariant(), version.Text.ToLowerInvariant());
                try
                {
                    if (Directory.Exists(folder))
                    {
                        present.Add(EnvironmentComparer.LocalToolKey(id, tool.Value));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Not found there: the check says it is missing.
                }
            }
        }

        return present;
    }

    private static string? DefaultNuGetCache()
    {
        var configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, ".nuget", "packages");
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,213}$")]
    private static partial Regex PackageId();
}
