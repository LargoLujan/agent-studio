using AgentStudio.Application.Models;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Common;
using AgentStudio.Observability.Usage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Observability.Estimates;

/// <summary>
/// The estimator of the system (docs/16 D-20, F9-7, brought forward to Phase 13; ADR-0050): it reads the log of the project
/// through <see cref="IProjectLogReader"/>, projects its attempts and estimates with <see cref="UsageEstimation"/>. It writes
/// nothing, launches nothing and spends no quota. A log that is not trusted (it does not verify, or no anchor recognises it)
/// fails closed: every figure is unknown, with no references.
/// </summary>
public sealed class UsageEstimator(IProjectLogReader logs, IModelCatalogSource catalog) : IUsageEstimator
{
    /// <summary>Why every figure is unknown when the log of the project is not trusted.</summary>
    public const string UntrustedLogReason = "unknown: the log of the project is not trusted";

    /// <inheritdoc />
    public async Task<UsageEstimate> EstimateAsync(UsageEstimateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var attempts = await AttemptsAsync(request.Project, ct).ConfigureAwait(false);
        return attempts is null ? UsageEstimate.Unknown(0) : UsageEstimation.Estimate(attempts, catalog.Current.Catalog, request);
    }

    /// <inheritdoc />
    public async Task<PlanEstimate> EstimatePlanAsync(PlanEstimateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var attempts = await AttemptsAsync(request.Project, ct).ConfigureAwait(false);
        if (attempts is null)
        {
            return new PlanEstimate(
                request.Plan.Tasks.Where(t => t.Agent is not null).ToDictionary(t => t.Id, _ => UsageEstimate.Unknown(0)),
                EstimateInterval<long>.Unknown(UntrustedLogReason),
                EstimateInterval<long>.Unknown(UntrustedLogReason),
                EstimateInterval<long>.Unknown(UntrustedLogReason),
                EstimateInterval<TimeSpan>.Unknown(UntrustedLogReason),
                []);
        }

        return UsageEstimation.EstimatePlan(attempts, catalog.Current.Catalog, request);
    }

    /// <summary>The attempts of the trusted log of the project; null when it is not trusted.</summary>
    private async Task<IReadOnlyList<UsageAttempt>?> AttemptsAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var snapshot = await logs.ReadAsync(project.RootPath, ct).ConfigureAwait(false);
        if (!snapshot.IsTrusted)
        {
            return null;
        }

        var id = string.IsNullOrWhiteSpace(snapshot.ProjectId) ? project.ProjectId : snapshot.ProjectId;
        return UsageProjector.Project(string.IsNullOrWhiteSpace(id) ? "project" : id, snapshot.Entries).Attempts;
    }
}

/// <summary>Registration of the estimator of the system (docs/16 D-20, WP2 of Phase 13), for the composition root to call.</summary>
public static class UsageEstimatorServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IUsageEstimator"/>. It needs <see cref="IProjectLogReader"/> (Infrastructure) and
    /// <see cref="IModelCatalogSource"/> (Infrastructure, <c>AddAgentStudioModelCatalog</c>). Registering it twice registers it once.
    /// </summary>
    public static IServiceCollection AddAgentStudioUsageEstimator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IUsageEstimator, UsageEstimator>();
        return services;
    }
}
