using AgentStudio.Desktop.Localization;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>Display-ready summary of the provider diagnostics. <see cref="Status"/> lets the StatusDot style colour it.</summary>
public sealed record ProviderSummary(string Text, StatusKind Status)
{
    /// <summary>
    /// The summary of the providers in use: a parked one (Gemini, ADR-0009) is neither counted nor a reason for a warning, and
    /// its row in Auto Doctor says why. With every provider in use ready, it is green.
    /// </summary>
    public static ProviderSummary From(ProviderDiagnosticsViewModel diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var providers = diagnostics.Providers;

        if (diagnostics.IsBusy)
        {
            return new ProviderSummary(Texts.Get("Providers_Summary_Checking"), StatusKind.Warning);
        }

        if (diagnostics.ErrorMessage is not null)
        {
            return new ProviderSummary(Texts.Get("Providers_Summary_Failed"), StatusKind.Error);
        }

        if (providers.Count == 0)
        {
            return new ProviderSummary(
                Texts.Get(diagnostics.LastCheckedAt is null ? "Providers_Summary_NotChecked" : "Providers_Summary_NoneRegistered"),
                StatusKind.Warning);
        }

        var inUse = providers.Where(p => !p.IsParked).ToList();
        if (inUse.Count == 0)
        {
            return new ProviderSummary(Texts.Get("Providers_Summary_AllParked"), StatusKind.Warning);
        }

        var ready = inUse.Count(p => p.Status == StatusKind.Ok);
        var status = inUse.Any(p => p.Status == StatusKind.Error) ? StatusKind.Error
            : ready == inUse.Count ? StatusKind.Ok
            : StatusKind.Warning;
        return new ProviderSummary(Texts.Format("Providers_Summary_Ready", ready, inUse.Count), status);
    }
}
