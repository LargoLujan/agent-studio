using System.Threading.Channels;
using AgentStudio.Application.Security;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Observability.Logging;

/// <summary>
/// Structured logging to a file, as one JSON object per line, for <c>Microsoft.Extensions.Logging</c>:
/// <c>%LOCALAPPDATA%\AgentStudio\logs\agentstudio-YYYYMMDD.log</c> by default (see <see cref="FileLoggerOptions"/>).
/// Each line has <c>timestamp</c> (ISO-8601 UTC), <c>level</c>, <c>category</c>, <c>eventId</c>, <c>message</c>,
/// <c>properties</c> (the structured state) and, when there is one, an <c>exception</c> summary.
/// </summary>
/// <remarks>
/// <para><b>What reaches the disk.</b> Nothing is written as the caller supplied it. Every string passes through the
/// <see cref="ISecretRedactor"/> (API keys, tokens, passwords, connection strings; the user profile path becomes
/// <c>~</c>), and free text is kept out of the file by this rule, applied to every property value:</para>
/// <list type="bullet">
/// <item>Numbers, booleans, enum names, GUIDs and dates are written as they are.</item>
/// <item>A text is written (redacted) only if its property NAME has none of the sensitive words (see
/// <see cref="FileLoggerOptions.SensitiveNameWords"/>: <c>Prompt</c>, <c>SystemInstructions</c>, <c>FinalText</c>,
/// <c>Response</c>, <c>Content</c>, <c>Output</c>, <c>Password</c>, <c>ApiKey</c>...) AND it has at most
/// <see cref="FileLoggerOptions.MaxValueLength"/> characters. Otherwise only <c>{"length": n, "sha256": "8 hex digits"}</c>
/// is written. The message shows the same <c>[text len=n sha256=...]</c> in place of the text.</item>
/// <item>An object, record or collection is never printed: only its type name (and item count).</item>
/// </list>
/// <para>That is the rule to follow when logging: put agent prompts and model output in properties, never in the
/// template, and never format them into the message yourself (<c>$"..."</c> turns the text into the template).</para>
/// <para><b>Resilience.</b> Formatting happens on the calling thread; writing happens on one background thread fed by a
/// bounded queue. A full queue never blocks the caller: the entry is dropped and counted
/// (<see cref="DroppedCount"/>), and the writer leaves a warning line with the number of lost entries. No logging
/// call throws, whatever the state, the formatter, the redactor or the disk do. Disposing the provider (the host does
/// it when it shuts down) writes what is queued before returning, waiting at most
/// <see cref="FileLoggerOptions.ShutdownTimeout"/>.</para>
/// <para>Files roll daily (the day is the local day of the entry) and by size, and files older than the retention
/// window are deleted when the provider starts. The provider is registered under the alias <c>File</c>, so the
/// standard <c>Logging:File:LogLevel</c> configuration also applies to it.</para>
/// </remarks>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const string UnavailableCategory = "[category unavailable]";
    private const string NoticeCategory = "AgentStudio.Observability.FileLogger";
    private const int MaxBatch = 256;

    private readonly Channel<LogLine> _queue;
    private readonly ILogLineSink _sink;
    private readonly LogRecordFormatter _formatter;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _shutdownTimeout;
    private readonly Task _writer;
    private long _dropped;
    private long _failed;
    private long _reportedDropped;
    private long _reportedFailed;
    private int _disposed;

    /// <summary>Creates the provider and starts its writer thread. Nothing is written to the disk by this call.</summary>
    /// <param name="options">Settings; read once, here.</param>
    /// <param name="redactor">Cleans every string before it is written.</param>
    /// <param name="timeProvider">Source of the timestamps and of the local day; the system clock by default.</param>
    public FileLoggerProvider(FileLoggerOptions options, ISecretRedactor redactor, TimeProvider? timeProvider = null)
        : this(options, redactor, timeProvider ?? TimeProvider.System, sink: null)
    {
    }

    internal FileLoggerProvider(FileLoggerOptions options, ISecretRedactor redactor, TimeProvider timeProvider, ILogLineSink? sink)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options.Validate();

        _clock = timeProvider;
        MinLevel = options.MinLevel;
        _shutdownTimeout = options.ShutdownTimeout;
        _formatter = new LogRecordFormatter(options, redactor);
        _sink = sink ?? new RollingFileSink(options, timeProvider);
        _queue = Channel.CreateBounded<LogLine>(new BoundedChannelOptions(options.QueueCapacity)
        {
            // Wait + TryWrite: a full queue makes TryWrite return false at once, which is what is counted as a drop.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _writer = Task.Run(RunWriterAsync);
    }

    /// <summary>Entries that were dropped because the queue was full.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Entries that were lost because they could not be formatted at all or could not be written to the disk.</summary>
    public long WriteFailureCount => Interlocked.Read(ref _failed);

    internal LogLevel MinLevel { get; }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        string label;
        try
        {
            label = _formatter.SafeLabel(categoryName ?? string.Empty);
        }
#pragma warning disable CA1031 // A category that cannot be cleaned must not stop the logger.
        catch (Exception)
#pragma warning restore CA1031
        {
            label = UnavailableCategory;
        }

        return new FileLogger(this, label);
    }

    /// <summary>Stops accepting entries, writes what is queued (waiting at most the shutdown timeout) and returns. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        try
        {
            _writer.Wait(_shutdownTimeout);
        }
#pragma warning disable CA1031 // The writer loop observes its own errors; whatever is left must not stop the shutdown.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Nothing useful can be done while shutting down.
        }

        _sink.Dispose();
    }

    internal void Write<TState>(string category, LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            var utcNow = _clock.GetUtcNow();
            byte[] bytes;
            try
            {
                bytes = _formatter.Format(utcNow, category, level, eventId, state, exception, formatter);
            }
#pragma warning disable CA1031 // Whatever went wrong, the call must not throw: write a record without any content instead.
            catch (Exception failure)
#pragma warning restore CA1031
            {
                bytes = _formatter.FormatFailure(utcNow, category, level, eventId, failure);
            }

            if (!_queue.Writer.TryWrite(new LogLine(RollingFileSink.DayOf(_clock, utcNow), bytes)))
            {
                Interlocked.Increment(ref _dropped);
            }
        }
#pragma warning disable CA1031 // Logging never throws into its callers.
        catch (Exception)
#pragma warning restore CA1031
        {
            Interlocked.Increment(ref _failed);
        }
    }

    private async Task RunWriterAsync()
    {
        try
        {
            try
            {
                _sink.DeleteExpired();
            }
#pragma warning disable CA1031 // Retention is best effort.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Old files stay until the next start.
            }

            var reader = _queue.Reader;
            var batch = new List<LogLine>(MaxBatch);
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                var notice = AddNotice(batch);
                while (batch.Count < MaxBatch && reader.TryRead(out var line))
                {
                    batch.Add(line);
                }

                WriteBatch(batch, notice);
            }

            batch.Clear();
            WriteBatch(batch, AddNotice(batch));
        }
#pragma warning disable CA1031 // The writer must never die with an unobserved exception.
        catch (Exception)
#pragma warning restore CA1031
        {
            Interlocked.Increment(ref _failed);
        }
    }

    // When entries were lost since the last notice, the first line of the batch says so.
    private bool AddNotice(List<LogLine> batch)
    {
        var dropped = Interlocked.Read(ref _dropped);
        var failed = Interlocked.Read(ref _failed);
        var newDropped = dropped - _reportedDropped;
        var newFailed = failed - _reportedFailed;
        if (newDropped <= 0 && newFailed <= 0)
        {
            return false;
        }

        _reportedDropped = dropped;
        _reportedFailed = failed;
        var reasons = new List<string>(2);
        if (newDropped > 0)
        {
            reasons.Add($"{newDropped} log entries were dropped because the queue was full");
        }

        if (newFailed > 0)
        {
            reasons.Add($"{newFailed} log entries could not be written");
        }

        var utcNow = _clock.GetUtcNow();
        var bytes = _formatter.FormatNotice(utcNow, LogLevel.Warning, NoticeCategory, string.Join("; ", reasons), ("Dropped", newDropped), ("WriteFailures", newFailed));
        batch.Add(new LogLine(RollingFileSink.DayOf(_clock, utcNow), bytes));
        return true;
    }

    private void WriteBatch(List<LogLine> batch, bool startsWithNotice)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            _sink.Append(batch);
        }
#pragma warning disable CA1031 // A disk that fails costs entries, never the application.
        catch (Exception)
#pragma warning restore CA1031
        {
            Interlocked.Add(ref _failed, batch.Count - (startsWithNotice ? 1 : 0));
        }
    }
}
