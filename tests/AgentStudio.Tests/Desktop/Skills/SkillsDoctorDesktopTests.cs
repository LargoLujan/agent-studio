using System.Globalization;
using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Skills;
using AgentStudio.Infrastructure.Skills.GitHub;
using AgentStudio.Tests.Desktop.Lock;
using AgentStudio.Tests.Infrastructure.Skills.GitHub;
using FakeSecrets = AgentStudio.Tests.Security.FakeSecrets;

namespace AgentStudio.Tests.Desktop.Skills;

/// <summary>
/// The pieces the desktop adds to the doctor of skills (docs/20 D-13 to D-15): Windows Hello over the one barrier of the approvals, with the
/// real <see cref="ReinforcedConfirmation"/> over a scripted Windows Hello, and the permission that lives in the settings of the app.
/// </summary>
public sealed class SkillsDoctorDesktopTests
{
    private static readonly ResourceTextProvider Spanish = new(CultureInfo.GetCultureInfo("es-ES"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HelloSkillsDoctorPresence Presence(FakeUserPresenceVerifier verifier) =>
        new(new ReinforcedConfirmation(verifier, () => false), Spanish);

    [Theory]
    [InlineData(SkillsDoctorPresenceReason.UseToken, "Skills_Hello_UseToken")]
    [InlineData(SkillsDoctorPresenceReason.StoreToken, "Skills_Hello_StoreToken")]
    [InlineData(SkillsDoctorPresenceReason.DeleteToken, "Skills_Hello_DeleteToken")]
    public async Task A_verified_person_confirms_and_windows_hello_is_asked_with_its_reason(SkillsDoctorPresenceReason reason, string key)
    {
        var verifier = FakeUserPresenceVerifier.Answering(UserPresenceResult.Verified);

        var confirmed = await Presence(verifier).ConfirmAsync(reason, Ct);

        Assert.True(confirmed);
        Assert.Equal([Spanish.Get(key)], verifier.Messages);
    }

    [Theory]
    [InlineData(UserPresenceResult.Canceled)]
    [InlineData(UserPresenceResult.RetriesExhausted)]
    [InlineData(UserPresenceResult.DeviceBusy)]
    public async Task Any_answer_but_verified_is_a_no(UserPresenceResult answer)
    {
        var confirmed = await Presence(FakeUserPresenceVerifier.Answering(answer)).ConfirmAsync(SkillsDoctorPresenceReason.UseToken, Ct);

        Assert.False(confirmed);
    }

    [Fact]
    public async Task Without_windows_hello_and_without_the_lock_of_windows_nothing_confirms_and_nothing_is_asked()
    {
        var verifier = FakeUserPresenceVerifier.Without(UserPresenceAvailability.NotConfiguredForUser);

        var confirmed = await Presence(verifier).ConfirmAsync(SkillsDoctorPresenceReason.UseToken, Ct);

        Assert.False(confirmed);
        Assert.Empty(verifier.Messages);
    }

    [Fact]
    public async Task A_cancelled_windows_hello_leaves_the_vault_unread_and_a_verified_one_reads_it_once()
    {
        var vault = new CountingVault();
        vault.Secrets[SkillsDoctorSecrets.TokenName] = FakeSecrets.GitHubFineGrainedToken();

        var denied = await new SkillsDoctorSecrets(vault, vault, Presence(FakeUserPresenceVerifier.Answering(UserPresenceResult.Canceled))).UseTokenAsync(Ct);
        Assert.Equal(SkillsDoctorTokenState.PresenceDenied, denied.State);
        Assert.Equal(0, vault.Reads);

        var granted = await new SkillsDoctorSecrets(vault, vault, Presence(FakeUserPresenceVerifier.Answering(UserPresenceResult.Verified))).UseTokenAsync(Ct);
        Assert.Equal(SkillsDoctorTokenState.Granted, granted.State);
        Assert.Equal(1, vault.Reads);
    }

    [Fact]
    public async Task Windows_hello_is_asked_before_the_token_is_stored_or_deleted()
    {
        var vault = new CountingVault();
        var cancelled = new SkillsDoctorSecrets(vault, vault, Presence(FakeUserPresenceVerifier.Answering(UserPresenceResult.Canceled)));
        var verified = new SkillsDoctorSecrets(vault, vault, Presence(FakeUserPresenceVerifier.Answering(UserPresenceResult.Verified)));
        var token = new GitHubToken(FakeSecrets.GitHubToken());

        Assert.Equal(SkillsDoctorTokenChange.PresenceDenied, await cancelled.StoreAsync(token, Ct));
        Assert.Equal(0, vault.Stores);
        Assert.Equal(SkillsDoctorTokenChange.Done, await verified.StoreAsync(token, Ct));
        Assert.Equal(SkillsDoctorTokenChange.PresenceDenied, await cancelled.DeleteAsync(Ct));
        Assert.Equal(0, vault.Deletes);
        Assert.Equal(SkillsDoctorTokenChange.Done, await verified.DeleteAsync(Ct));
    }

    [Fact]
    public void The_permission_is_off_by_default_and_survives_saving_and_loading_the_settings()
    {
        using var temp = new TempDirectory();
        var store = new JsonAppSettingsStore(new AppDataPaths(temp.Combine("settings")));
        var setting = new AppSettingsSkillsDoctorSetting(store);

        Assert.False(setting.NetworkAllowed);
        Assert.False(AppSettings.Default.SkillsDoctorNetworkEnabled);

        setting.Set(true);
        Assert.True(new AppSettingsSkillsDoctorSetting(new JsonAppSettingsStore(new AppDataPaths(temp.Combine("settings")))).NetworkAllowed);

        // Saving the rest of the settings (the shell does it with every project it opens) does not turn it off.
        store.Save(store.Load().WithProjectOpened(temp.Combine("project")));
        Assert.True(setting.NetworkAllowed);

        setting.Set(false);
        Assert.False(setting.NetworkAllowed);
    }
}
