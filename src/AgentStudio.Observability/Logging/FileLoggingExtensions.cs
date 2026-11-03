using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Observability.Logging;

public static class FileLoggingExtensions
{
    /// <summary>
    /// Adds the structured file logger (<see cref="FileLoggerProvider"/>). It needs an
    /// <see cref="AgentStudio.Application.Security.ISecretRedactor"/> in the container and uses the container's
    /// <see cref="TimeProvider"/> when there is one. Calling it twice registers one provider; the last
    /// <paramref name="configure"/> wins. The container disposes the provider when the host is disposed, which writes
    /// what is still queued.
    /// </summary>
    /// <remarks>
    /// The framework applies its own level filters before the provider sees an entry. To let
    /// <see cref="FileLoggerOptions.MinLevel"/> go below that default (Information), also add a filter for the
    /// provider: <c>logging.AddFilter&lt;FileLoggerProvider&gt;(null, minLevel)</c>.
    /// </remarks>
    public static ILoggingBuilder AddAgentStudioFileLogging(this ILoggingBuilder builder, Action<FileLoggerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new FileLoggerOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, FileLoggerProvider>());
        return builder;
    }
}
