using AgentStudio.Application.Security;
using AgentStudio.Security.Redaction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Security;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the pattern-based <see cref="ISecretRedactor"/>, for the user profile of the current process. A
    /// redactor registered earlier is kept, so tests and hosts can substitute their own.
    /// </summary>
    public static IServiceCollection AddAgentStudioSecurity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISecretRedactor>(_ => new SecretRedactor());
        return services;
    }
}
