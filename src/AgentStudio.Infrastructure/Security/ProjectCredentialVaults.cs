using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Infrastructure.Security;

/// <summary>
/// Project-bound DPAPI storage (docs/16 D-03). Opaque directory names and different entropy prevent accidental reuse
/// across projects. Neither is a security boundary against the same Windows user or an administrator. The configured
/// root belongs to the app, outside project folders; no existing global token is read, copied or migrated.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProjectCredentialVaults : IProjectCredentialVaults
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly string _root;
    private readonly ConcurrentDictionary<string, IProjectCredentialVault> _vaults = new(StringComparer.Ordinal);

    public ProjectCredentialVaults(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public IProjectCredentialVault ForProject(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (projectId.Length > 512 || projectId.Any(char.IsControl))
        {
            throw new ArgumentException("A project id must contain at most 512 characters and no control characters.", nameof(projectId));
        }

        try
        {
            _ = StrictUtf8.GetByteCount(projectId);
        }
        catch (EncoderFallbackException)
        {
            // Replacement characters could otherwise make two distinct, malformed ids share a vault.
            throw new ArgumentException("A project id must contain valid Unicode.", nameof(projectId));
        }

        return _vaults.GetOrAdd(projectId, id => new ProjectVault(_root, id));
    }

    private static string OpaqueName(string name) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)));

    private sealed class ProjectVault : IProjectCredentialVault
    {
        private readonly DpapiCredentialVault _vault;

        internal ProjectVault(string root, string projectId)
        {
            ProjectId = projectId;
            var entropy = SHA256.HashData(Encoding.UTF8.GetBytes("AgentStudio.ProjectVault.v1\0" + projectId));
            _vault = new DpapiCredentialVault(Path.Combine(root, OpaqueName(projectId)), entropy, root);
        }

        public string ProjectId { get; }

        public Task StoreAsync(string name, string secret, CancellationToken cancellationToken) =>
            _vault.StoreAsync(SecretFileName(name), secret, cancellationToken);

        public Task<string?> ReadAsync(string name, CancellationToken cancellationToken) =>
            _vault.ReadAsync(SecretFileName(name), cancellationToken);

        public Task DeleteAsync(string name, CancellationToken cancellationToken) =>
            _vault.DeleteAsync(SecretFileName(name), cancellationToken);

        public Task<bool> ContainsAsync(string name, CancellationToken cancellationToken) =>
            _vault.ContainsAsync(SecretFileName(name), cancellationToken);

        private static string SecretFileName(string name)
        {
            if (!DpapiCredentialVault.IsValidName(name))
            {
                throw new ArgumentException("A secret name is 1 to 64 characters of a-z, 0-9, '.', '_' and '-'.", nameof(name));
            }

            // Hash even valid names: Windows device names such as 'con' must never become paths.
            return OpaqueName(name);
        }
    }
}

public static class ProjectCredentialVaultServices
{
    /// <summary>
    /// Registers only project storage, leaving the global doctor's vault and its Hello flow unchanged. The desktop
    /// supplies its roaming app folder; tests supply a temporary folder. Resolving the service performs no disk I/O.
    /// </summary>
    public static IServiceCollection AddAgentStudioProjectCredentialVaults(this IServiceCollection services, string root)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (OperatingSystem.IsWindows())
        {
            services.TryAddSingleton<IProjectCredentialVaults>(new ProjectCredentialVaults(root));
        }

        return services;
    }
}
