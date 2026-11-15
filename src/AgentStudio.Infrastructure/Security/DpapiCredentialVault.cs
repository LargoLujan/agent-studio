using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Security;

namespace AgentStudio.Infrastructure.Security;

/// <summary>
/// The minimal vault of the secrets Agent Studio owns (docs/20 D-15, ADR-0063): one file per secret in a folder of the app, encrypted with DPAPI for the
/// user of Windows (<see cref="DataProtectionScope.CurrentUser"/>) with a fixed entropy of the app. Project storage uses
/// <see cref="ProjectCredentialVaults"/> with separate entropy and folders. It is not part of the contract of the CLIs: it never
/// reads, copies or stores their credentials.
/// <para>
/// What DPAPI protects, said honestly: the file is of no use on another computer or for another user. It does <b>not</b> protect from a process that runs as
/// the same user, which can ask Windows to decrypt it. The presence (Windows Hello) is asked by whoever uses the vault, not here: this class decides nothing
/// about who may read. <see cref="ContainsAsync"/> only looks for the file; it decrypts nothing.
/// </para>
/// A name is <c>[a-z0-9._-]</c>, 1 to 64 characters, so it cannot leave the folder. A file is written next to its final name and moved into place, so a
/// failure never leaves half a secret, and it is read with a limit of <see cref="MaxFileBytes"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialVault : ICredentialVault, ICredentialVaultProbe
{
    public const int MaxNameLength = 64;

    public const int MaxFileBytes = 64 * 1024;

    private const string Extension = ".dpapi";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AgentStudio.Vault.v1");

    private readonly string _folder;
    private readonly byte[] _entropy;
    private readonly string? _projectRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="folder">Where the files go; <c>%LOCALAPPDATA%\AgentStudio\vault</c> by default. A test passes a temporary folder: nothing here touches the real one by itself.</param>
    public DpapiCredentialVault(string folder)
        : this(folder, Entropy, projectRoot: null)
    {
    }

    internal DpapiCredentialVault(string folder, byte[] entropy, string? projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = Path.GetFullPath(folder);
        _entropy = entropy.ToArray();
        _projectRoot = projectRoot;
    }

    /// <summary><c>%LOCALAPPDATA%\AgentStudio\vault</c>.</summary>
    public static string DefaultFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio", "vault");

    public static bool IsValidName(string? name) =>
        name is { Length: >= 1 and <= MaxNameLength } && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-');

    public async Task StoreAsync(string name, string secret, CancellationToken cancellationToken)
    {
        var path = PathOf(name);
        ArgumentException.ThrowIfNullOrEmpty(secret);
        cancellationToken.ThrowIfCancellationRequested();
        if (Encoding.UTF8.GetByteCount(secret) > MaxFileBytes)
        {
            throw new ArgumentException("The secret exceeds the vault size limit.", nameof(secret));
        }

        var plain = Encoding.UTF8.GetBytes(secret);
        try
        {
            var sealedBytes = ProtectedData.Protect(plain, _entropy, DataProtectionScope.CurrentUser);
            if (sealedBytes.Length > MaxFileBytes)
            {
                throw new ArgumentException("The encrypted secret exceeds the vault size limit.", nameof(secret));
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureProjectPathSafe(path);
                Directory.CreateDirectory(_folder);
                EnsureProjectPathSafe(path);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                    {
                        await output.WriteAsync(sealedBytes, cancellationToken).ConfigureAwait(false);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureProjectPathSafe(path);
                    File.Move(temporary, path, overwrite: true);
                }
                catch
                {
                    // A failed or cancelled store leaves no half-written copy of the secret behind.
                    TryDelete(temporary);
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public async Task<string?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        var path = PathOf(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureProjectPathSafe(path);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            if (info.Length > MaxFileBytes || info.LinkTarget is not null)
            {
                throw new InvalidDataException("The vault file is not usable.");
            }

            // Bound the read itself as well: a file growing after the metadata check must not allocate without limit.
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var buffer = new byte[MaxFileBytes + 1];
            var length = await input.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (length > MaxFileBytes)
            {
                throw new InvalidDataException("The vault file is not usable.");
            }

            byte[] plain;
            try
            {
                plain = ProtectedData.Unprotect(buffer.AsSpan(0, length).ToArray(), _entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                // Another user or another computer: the message of the system is not repeated.
                throw new InvalidDataException("The vault file cannot be decrypted for this user.");
            }

            try
            {
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ContainsAsync(string name, CancellationToken cancellationToken)
    {
        var path = PathOf(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureProjectPathSafe(path);
            return File.Exists(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        var path = PathOf(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureProjectPathSafe(path);
            File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureProjectPathSafe(string path)
    {
        if (_projectRoot is null)
        {
            return; // The existing global doctor's layout and entropy are preserved.
        }

        // The root is app-owned, not a project path. Reject planted links in this store before touching their targets.
        CheckEntry(_projectRoot, isDirectory: true);
        CheckEntry(_folder, isDirectory: true);
        CheckEntry(path, isDirectory: false);
    }

    private static void CheckEntry(string path, bool isDirectory)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != isDirectory)
        {
            throw new InvalidDataException("The project vault path is not usable.");
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done for it.
        }
    }

    private string PathOf(string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException("A secret name is 1 to 64 characters of a-z, 0-9, '.', '_' and '-'.", nameof(name));
        }

        return Path.Combine(_folder, name + Extension);
    }
}
