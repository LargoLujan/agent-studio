using AgentStudio.Application.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Security.Approvals;

public static class ApprovalsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the approval gate (F3-7) as the one <see cref="IApprovalGate"/> of the app. It needs the
    /// <see cref="IApprovalPrompt"/> of the desktop app, the <c>IDecisionRecorder</c> and a <see cref="TimeProvider"/>.
    /// Options registered earlier are kept; otherwise <paramref name="options"/>, or a 30-minute timeout.
    /// </summary>
    public static IServiceCollection AddAgentStudioApprovals(this IServiceCollection services, ApprovalGateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(options ?? new ApprovalGateOptions());
        services.TryAddSingleton<IApprovalGate, ApprovalGate>();
        return services;
    }
}
