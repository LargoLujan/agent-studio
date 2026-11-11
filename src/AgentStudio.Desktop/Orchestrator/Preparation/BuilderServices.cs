using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Models;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Domain.Agents;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Orchestrator.Preparation;

/// <summary>
/// The services the visual builder reads, all optional (docs/04 section 21.8): the composition of the app registers them and
/// passes them to the document; without one, what needs it is shown as not available, never as a button that does nothing.
/// None of them is created here and none launches an agent by itself: «Revisar el orden» and the chat spend quota only with the
/// OK of the person in each use.
/// </summary>
/// <param name="OrderReview">«Revisar el orden» (D-20): an auxiliary run of the orchestrator, read only.</param>
/// <param name="Chat">The chat of preparation (D-44): one auxiliary run per message, read only.</param>
/// <param name="Ledger">The only register of the OKs of quota spent in the app: the services spend them, not the screen.</param>
/// <param name="Catalog">The catalog of models (D-23), without prices.</param>
/// <param name="Learning">What the verified log taught of each model of this project.</param>
/// <param name="Estimator">The estimate shown before an OK (D-32): intervals with their origin, never an invented number.</param>
/// <param name="Repository">The hardened git layer, to read the node of the repository without network.</param>
/// <param name="Live">The grouped live state of the run followed (D-14 and D-16); the monitor implements it.</param>
/// <param name="Quota">The record of the quota of the account, for the header of the strip (D-12).</param>
/// <param name="Brief">The brief of the project, kept as memory of the person (D-10).</param>
/// <param name="WindowsAnimates">Whether Windows animates its windows; null reads it from Windows
/// (<see cref="MotionPolicy.WindowsAnimates"/>). A test gives it, so it does not depend on the machine that runs it.</param>
/// <param name="ContextPacker">The packer of the engine (D-13, P49): the analyzer asks it, off the UI thread and once per agent and set of requirements, how much memory a task would start with. Without it, memory is not counted.</param>
/// <param name="ProjectFiles">Counts the files of a project up to a cap (<see cref="ProjectFileCounter"/>), off the UI thread; the builder asks it once when the project opens. Without it the size of the project is unknown, and a test gives it so it never reads the disk.</param>
/// <param name="SkillCharacters">What the CLI presents of the skills that are on for an agent (<see cref="SkillPresentationSource"/>), asked off the UI thread (it reads the catalog and files) and once for each agent. Without it the skills are not counted.</param>
public sealed record BuilderServices(
    IOrderReview? OrderReview = null,
    IPreparationChat? Chat = null,
    IQuotaConsentLedger? Ledger = null,
    IModelCatalogSource? Catalog = null,
    IModelLearning? Learning = null,
    IUsageEstimator? Estimator = null,
    IRepositoryStateReader? Repository = null,
    IRunLiveProjection? Live = null,
    IQuotaObservations? Quota = null,
    IProjectBrief? Brief = null,
    Func<bool>? WindowsAnimates = null,
    IContextPacker? ContextPacker = null,
    Func<string, CancellationToken, Task<ProjectFileCount>>? ProjectFiles = null,
    Func<ProjectContext, AgentDefinition, CancellationToken, Task<int>>? SkillCharacters = null)
{
    /// <summary>Nothing composed: the canvas works with what it always had.</summary>
    public static BuilderServices None { get; } = new();
}

/// <summary>
/// The OK of the person for one use of quota (docs/17 D-20 and D-44): the estimate in front of them, and the consent that the
/// service spends once. It is the same card for «Revisar el orden» and for each message of the chat.
/// </summary>
public sealed partial class QuotaConsentCardViewModel : ObservableObject
{
    private readonly CanvasText _text;
    private readonly TimeProvider _time;

    public QuotaConsentCardViewModel(CanvasText text, TimeProvider time)
    {
        _text = text;
        _time = time;
    }

    /// <summary>The card is open: the person has to say yes or no.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>The estimate in words with its origin, or that there is not enough to estimate (never a number made up).</summary>
    [ObservableProperty]
    private string _estimateText = string.Empty;

    /// <summary>What the OK is for, as the service records it.</summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>Opens the card with the estimate in front of the person.</summary>
    public async Task OpenAsync(string description, Func<CancellationToken, Task<UsageEstimate?>>? estimate)
    {
        Description = description;
        EstimateText = _text.Get("Canvas_Consent_Estimating");
        IsOpen = true;
        UsageEstimate? known = null;
        if (estimate is not null)
        {
            try
            {
                known = await estimate(CancellationToken.None).ConfigureAwait(true);
            }
#pragma warning disable CA1031 // The estimator is behind a port: no estimate is said as such, never thrown into the UI.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                EstimateText = _text.Format("Canvas_Consent_EstimateFailed", ex.Message);
                return;
            }
        }

        EstimateText = Describe(known);
    }

    public void Close() => IsOpen = false;

    /// <summary>The consent of the person, now: who decided, when and what it is for.</summary>
    public QuotaConsent Grant() => new(ApprovalPromptService.DecidedBy, _time.GetUtcNow(), Description);

    private string Describe(UsageEstimate? estimate)
    {
        if (estimate is null)
        {
            return _text.Get("Canvas_Consent_NoEstimator");
        }

        var total = estimate.TotalTokens;
        if (!estimate.IsKnown || total is not { IsKnown: true })
        {
            return _text.Format("Canvas_Consent_NoEstimate", estimate.References, UsageEstimate.MinimumReferences);
        }

        var culture = _text.Provider.Culture;
        return _text.Format(
            "Canvas_Consent_Estimate",
            total.Low.Value!.Value.ToString("N0", culture),
            total.High.Value!.Value.ToString("N0", culture),
            estimate.References);
    }
}

/// <summary>The agent that answers a conversation of preparation or reviews the order: the orchestrator of the template, if there is one.</summary>
public static class OrchestratorOf
{
    public static AgentDefinition? In(IReadOnlyList<AgentDefinition>? roster) => roster?.FirstOrDefault(a => a.Role == AgentRole.Orchestrator);
}
