using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;

namespace AgentStudio.Infrastructure.Skills.GitHub;

/// <summary>
/// The only way the doctor of skills reaches the token of GitHub (docs/20 D-15), and the way the screen stores and deletes it. The order is fixed:
/// <list type="number">
/// <item>is there a token? (<see cref="ICredentialVaultProbe"/>, which decrypts nothing) With none there is no secret and no presence is asked;</item>
/// <item>the presence of the person (<see cref="ISkillsDoctorPresence"/>: Windows Hello, <c>RemoteOrSecretOperation</c>, «siempre»), once per batch;</item>
/// <item>only then <see cref="ICredentialVault.ReadAsync"/>. With the presence denied, or failed to ask, the vault gets no reading at all and the batch goes
/// without a token.</item>
/// </list>
/// Storing, replacing and deleting also ask for presence first. Nothing here writes the token in any text: an error says what failed, never the value.
/// </summary>
public sealed class SkillsDoctorSecrets(ICredentialVault vault, ICredentialVaultProbe probe, ISkillsDoctorPresence presence) : ISkillsDoctorSecrets, ISkillsDoctorTokenManager
{
    /// <summary>The name of the token in the vault.</summary>
    public const string TokenName = "github-skills-doctor-token";

    public async Task<SkillsDoctorTokenUse> UseTokenAsync(CancellationToken ct)
    {
        if (!await probe.ContainsAsync(TokenName, ct).ConfigureAwait(false))
        {
            return SkillsDoctorTokenUse.None;
        }

        if (!await ConfirmAsync(SkillsDoctorPresenceReason.UseToken, ct).ConfigureAwait(false))
        {
            return SkillsDoctorTokenUse.Denied;
        }

        string? text;
        try
        {
            text = await vault.ReadAsync(TokenName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // A vault file that cannot be read is the same as no token: the batch goes without one.
            return SkillsDoctorTokenUse.None;
        }

        if (text is null)
        {
            return SkillsDoctorTokenUse.None;
        }

        try
        {
            return new SkillsDoctorTokenUse(SkillsDoctorTokenState.Granted, new GitHubToken(text));
        }
        catch (ArgumentException)
        {
            return SkillsDoctorTokenUse.None;
        }
    }

    public Task<bool> HasTokenAsync(CancellationToken ct) => probe.ContainsAsync(TokenName, ct);

    public async Task<SkillsDoctorTokenChange> StoreAsync(GitHubToken token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!await ConfirmAsync(SkillsDoctorPresenceReason.StoreToken, ct).ConfigureAwait(false))
        {
            return SkillsDoctorTokenChange.PresenceDenied;
        }

        await vault.StoreAsync(TokenName, token.Reveal(), ct).ConfigureAwait(false);
        return SkillsDoctorTokenChange.Done;
    }

    public async Task<SkillsDoctorTokenChange> DeleteAsync(CancellationToken ct)
    {
        if (!await probe.ContainsAsync(TokenName, ct).ConfigureAwait(false))
        {
            return SkillsDoctorTokenChange.NothingStored;
        }

        if (!await ConfirmAsync(SkillsDoctorPresenceReason.DeleteToken, ct).ConfigureAwait(false))
        {
            return SkillsDoctorTokenChange.PresenceDenied;
        }

        await vault.DeleteAsync(TokenName, ct).ConfigureAwait(false);
        return SkillsDoctorTokenChange.Done;
    }

    /// <summary>A confirmation that fails to be asked is a no (never a yes), except when the caller cancelled.</summary>
    private async Task<bool> ConfirmAsync(SkillsDoctorPresenceReason reason, CancellationToken ct)
    {
        try
        {
            return await presence.ConfirmAsync(reason, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
