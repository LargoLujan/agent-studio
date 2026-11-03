using Microsoft.Extensions.Logging;

namespace AgentStudio.Observability.Logging;

/// <summary>The logger of one category. It only decides whether to log; the provider formats and queues.</summary>
internal sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
{
    /// <summary>Scopes are not written: their state can carry free text, and nothing in the app uses them yet.</summary>
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel != LogLevel.None && logLevel >= provider.MinLevel && !provider.IsDisposed;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        provider.Write(category, logLevel, eventId, state, exception, formatter);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
