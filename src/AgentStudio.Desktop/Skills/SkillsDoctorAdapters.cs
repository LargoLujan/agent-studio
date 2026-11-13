using AgentStudio.Application.Skills;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Services;
using AgentStudio.Domain.Security;

namespace AgentStudio.Desktop.Skills;

/// <summary>
/// The permission «Permitir comprobar las skills en GitHub» seen from the screen (docs/20 D-13): the policy the checker reads and the switch the screen
/// flips. It lives in <see cref="AppSettings.SkillsDoctorNetworkEnabled"/>, off by default.
/// </summary>
public interface ISkillsDoctorNetworkSetting : ISkillsDoctorPolicy
{
    void Set(bool allowed);
}

/// <summary>The permission over the settings of the app. Read at every batch, so a change takes effect at once.</summary>
public sealed class AppSettingsSkillsDoctorSetting(IAppSettingsStore store) : ISkillsDoctorNetworkSetting
{
    public bool NetworkAllowed => store.Load().SkillsDoctorNetworkEnabled;

    public void Set(bool allowed) => store.SavePreferences(store.Load() with { SkillsDoctorNetworkEnabled = allowed });
}

/// <summary>A composition without the settings of the app: the network is never allowed and the screen cannot change it.</summary>
public sealed class NoSkillsDoctorNetworkSetting : ISkillsDoctorNetworkSetting
{
    public bool NetworkAllowed => false;

    public void Set(bool allowed)
    {
        // Nothing keeps it: the permission stays off.
    }
}

/// <summary>A composition without Windows Hello: every confirmation is a no, so the token is neither stored, deleted nor read.</summary>
public sealed class NoSkillsDoctorPresence : ISkillsDoctorPresence
{
    public Task<bool> ConfirmAsync(SkillsDoctorPresenceReason reason, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>
/// Windows Hello for the token of GitHub (docs/20 D-15, ADR-0053): the same <see cref="ReinforcedConfirmation"/> the approvals use, with
/// <see cref="PresenceAction.RemoteOrSecretOperation"/>, which is «siempre» whatever any setting or file says. Only a confirmation that
/// <see cref="ReinforcedConfirmation.Approves"/> counts; a click alone never does, and an error is a no.
/// </summary>
public sealed class HelloSkillsDoctorPresence(ReinforcedConfirmation confirmation, IUiTextProvider? texts = null) : ISkillsDoctorPresence
{
    public async Task<bool> ConfirmAsync(SkillsDoctorPresenceReason reason, CancellationToken ct)
    {
        var plan = await confirmation.PlanAsync(PresenceAction.RemoteOrSecretOperation, ct).ConfigureAwait(false);
        if (!plan.ConfirmsAfterYes)
        {
            return ReinforcedConfirmation.Approves(plan, plan.AnswerAloneProof);
        }

        var message = (texts ?? Texts.Current).Get(reason switch
        {
            SkillsDoctorPresenceReason.UseToken => "Skills_Hello_UseToken",
            SkillsDoctorPresenceReason.StoreToken => "Skills_Hello_StoreToken",
            SkillsDoctorPresenceReason.DeleteToken => "Skills_Hello_DeleteToken",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown reason."),
        });
        var outcome = await confirmation.ConfirmAsync(plan, message, ct).ConfigureAwait(false);
        return ReinforcedConfirmation.Approves(plan, outcome.Proof);
    }
}
