using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Security;
using AgentStudio.Observability.Logging;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Tests.Observability;

/// <summary>The queue and the writer: never block, drop with a counter, flush on dispose, never throw into a caller.</summary>
public sealed class FileLoggerResilienceTests : IDisposable
{
    private const string Prompt = "Please refactor the payroll module for TENANT-42 and email everyone";

    // How long a test waits for the writer thread to reach a point it always reaches within milliseconds.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly LoggerFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    /// <summary>A sink the test controls: the writer thread stops inside Append until the test releases it.</summary>
    private sealed class GateSink : ILogLineSink
    {
        private readonly ManualResetEventSlim _release = new(false);

        public ManualResetEventSlim Entered { get; } = new(false);

        public ConcurrentQueue<string> Lines { get; } = new();

        public int DeleteCalls { get; private set; }

        public void Release() => _release.Set();

        public int DeleteExpired()
        {
            DeleteCalls++;
            return 0;
        }

        public void Append(IReadOnlyList<LogLine> lines)
        {
            Entered.Set();
            _release.Wait();
            foreach (var line in lines)
            {
                Lines.Enqueue(Encoding.UTF8.GetString(line.Bytes).TrimEnd('\n'));
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingSink : ILogLineSink
    {
        public int DeleteExpired() => throw new IOException("retention failed");

        public void Append(IReadOnlyList<LogLine> lines) => throw new IOException("disk full");

        public void Dispose()
        {
        }
    }

    private sealed class SlowSink : ILogLineSink
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public int DeleteExpired() => 0;

        public void Append(IReadOnlyList<LogLine> lines)
        {
            Thread.Sleep(300);
            foreach (var line in lines)
            {
                Lines.Enqueue(Encoding.UTF8.GetString(line.Bytes).TrimEnd('\n'));
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingRedactor : ISecretRedactor
    {
        public string Redact(string text) => throw new InvalidOperationException("redactor is down");
    }

    private FileLoggerProvider WithSink(ILogLineSink sink) => new(_fx.Options, _fx.Redactor, _fx.Clock, sink);

    // ---- the queue ----

    [Fact]
    public void When_the_queue_is_full_new_entries_are_dropped_and_counted_without_blocking()
    {
        var sink = new GateSink();
        _fx.Options.QueueCapacity = 4;
        using var provider = WithSink(sink);
        var logger = provider.CreateLogger("Tests.Drop");

        logger.LogInformation("held {N}", 0);
        Assert.True(sink.Entered.Wait(Patience, TestContext.Current.CancellationToken)); // the writer took it and is stuck inside the sink

        var stopwatch = Stopwatch.StartNew();
        for (var i = 1; i <= 20; i++)
        {
            logger.LogInformation("entry {N}", i);
        }

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Logging blocked for {stopwatch.Elapsed}.");
        Assert.Equal(16L, provider.DroppedCount); // 4 fit in the queue, the other 16 were dropped

        sink.Release();
        provider.Dispose();

        var written = sink.Lines.Select(line => JsonDocument.Parse(line).RootElement).ToArray();
        Assert.Equal(6, written.Length); // the held entry, a notice and the 4 queued entries
        var messages = written.Select(r => r.GetProperty("message").GetString()).ToArray();
        Assert.Equal("held 0", messages[0]);
        Assert.Equal(["entry 1", "entry 2", "entry 3", "entry 4"], messages[2..]);
        var notice = written[1];
        Assert.Equal("Warning", notice.GetProperty("level").GetString());
        Assert.Contains("16 log entries were dropped because the queue was full", notice.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(16, notice.GetProperty("properties").GetProperty("Dropped").GetInt64());
    }

    [Fact]
    public void One_notice_reports_all_the_drops_since_the_previous_notice()
    {
        var sink = new GateSink();
        _fx.Options.QueueCapacity = 1;
        using var provider = WithSink(sink);
        var logger = provider.CreateLogger("Tests.Drop");

        logger.LogInformation("held");
        Assert.True(sink.Entered.Wait(Patience, TestContext.Current.CancellationToken));
        logger.LogInformation("queued");
        logger.LogInformation("dropped 1");
        logger.LogInformation("dropped 2");
        Assert.Equal(2L, provider.DroppedCount);

        sink.Release();
        provider.Dispose();

        Assert.Equal(2L, provider.DroppedCount);
        var notices = sink.Lines.Select(line => JsonDocument.Parse(line).RootElement).Where(r => r.GetProperty("level").GetString() == "Warning").ToArray();
        Assert.Equal(2, notices.Single().GetProperty("properties").GetProperty("Dropped").GetInt64());
    }

    [Fact]
    public void Disposing_the_provider_writes_everything_that_was_queued()
    {
        _fx.Options.QueueCapacity = 5000;
        var logger = _fx.Logger("Tests.Flush");

        for (var i = 0; i < 2000; i++)
        {
            logger.LogInformation("entry {N}", i);
        }

        _fx.Provider!.Dispose(); // straight away, with most of the entries still in the queue

        var numbers = _fx.Logs.Records().Select(r => r.GetProperty("properties").GetProperty("N").GetInt32()).ToArray();
        Assert.Equal(Enumerable.Range(0, 2000), numbers);
        Assert.Equal(0L, _fx.Provider.DroppedCount);
    }

    [Fact]
    public void Disposing_waits_for_a_slow_writer()
    {
        var sink = new SlowSink();
        var provider = WithSink(sink);
        var logger = provider.CreateLogger("Tests.Slow");

        logger.LogInformation("one");
        logger.LogInformation("two");
        logger.LogInformation("three");
        provider.Dispose();

        Assert.Equal(3, sink.Lines.Count);
    }

    [Fact]
    public void Disposing_gives_up_after_the_shutdown_timeout_instead_of_hanging()
    {
        var sink = new GateSink();
        _fx.Options.ShutdownTimeout = TimeSpan.FromMilliseconds(200);
        var provider = WithSink(sink);
        provider.CreateLogger("Tests.Stuck").LogInformation("stuck in the sink");
        Assert.True(sink.Entered.Wait(Patience, TestContext.Current.CancellationToken));

        var stopwatch = Stopwatch.StartNew();
        provider.Dispose();
        stopwatch.Stop();
        sink.Release(); // let the writer thread go

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Dispose took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void Retention_runs_once_when_the_writer_starts()
    {
        var sink = new GateSink();
        var provider = WithSink(sink);
        provider.CreateLogger("Tests.Retention").LogInformation("hello");
        Assert.True(sink.Entered.Wait(Patience, TestContext.Current.CancellationToken));
        sink.Release();
        provider.Dispose();

        Assert.Equal(1, sink.DeleteCalls);
    }

    [Fact]
    public void Disposing_twice_is_harmless_and_logging_afterwards_is_ignored_silently()
    {
        var logger = _fx.Logger("Tests.Late");
        logger.LogInformation("before");
        _fx.Provider!.Dispose();
        _fx.Provider.Dispose();

        logger.LogInformation("after");
        logger.LogError(new InvalidOperationException("late"), "after again");

        Assert.False(logger.IsEnabled(LogLevel.Critical));
        Assert.Equal(["before"], _fx.Logs.Records().Select(r => r.GetProperty("message").GetString()));
        Assert.Equal(0L, _fx.Provider.DroppedCount);
    }

    // ---- never throw into the caller ----

    [Fact]
    public void A_folder_that_cannot_be_used_costs_entries_but_never_throws()
    {
        _fx.Logs.WriteFile("blocker", "a file where the folder should be");
        _fx.Options.LogDirectory = _fx.Logs.Combine("blocker");
        var logger = _fx.Logger("Tests.Broken");

        for (var i = 0; i < 10; i++)
        {
            logger.LogError(new InvalidOperationException("x"), "entry {N}", i);
        }

        _fx.Provider!.Dispose();

        Assert.True(_fx.Provider.WriteFailureCount > 0);
        Assert.Equal(["blocker"], _fx.Logs.FileNames());
    }

    [Fact]
    public void A_sink_that_fails_is_counted_and_the_logger_keeps_going()
    {
        using var provider = WithSink(new ThrowingSink());
        var logger = provider.CreateLogger("Tests.Failing");

        for (var i = 0; i < 10; i++)
        {
            logger.LogInformation("entry {N}", i);
        }

        provider.Dispose();

        Assert.Equal(10L, provider.WriteFailureCount);
        Assert.Equal(0L, provider.DroppedCount);
    }

    [Fact]
    public void A_formatter_that_throws_still_leaves_a_record()
    {
        var logger = _fx.Logger("Tests.Formatter");

        logger.Log(LogLevel.Information, new EventId(3), "state", null, (_, _) => throw new InvalidOperationException("bad formatter"));
        logger.Log(LogLevel.Information, new EventId(4), "state", null, null!);
        _fx.Flush();

        var messages = _fx.Logs.Records().Select(r => r.GetProperty("message").GetString()).ToArray();
        Assert.Equal(["[message unavailable: InvalidOperationException]", "[message unavailable: NullReferenceException]"], messages);
    }

    [Fact]
    public void A_redactor_that_throws_leaves_a_record_without_any_content()
    {
        _fx.Redactor = new ThrowingRedactor();
        var logger = _fx.Logger("Tests.Redactor");

        logger.LogError(new InvalidOperationException(Prompt), "Running {Request} with {Detail}", Prompt, "short");
        _fx.Flush();

        var record = _fx.Logs.Records().Single();
        Assert.Equal("[log entry could not be formatted: InvalidOperationException]", record.GetProperty("message").GetString());
        Assert.Equal("Error", record.GetProperty("level").GetString());
        Assert.DoesNotContain("payroll", _fx.Logs.AllText(), StringComparison.Ordinal);
        Assert.DoesNotContain("short", _fx.Logs.AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Values_and_exceptions_that_misbehave_do_not_throw()
    {
        var logger = _fx.Logger("Tests.Misbehaving");

        logger.LogInformation("Saw {Item}", new MisbehavingValue());
        logger.LogError(new MisbehavingException(), "Failed");
        logger.LogInformation("Odd {A} {B}", "lone surrogate \uD800", "tail");
        _fx.Flush();

        var messages = _fx.Logs.Records().Select(r => r.GetProperty("message").GetString()).ToArray();
        Assert.Equal(3, messages.Length);
        Assert.Equal("Saw [ToString failed: InvalidOperationException]", messages[0]);
        Assert.Equal("Failed", messages[1]);
        Assert.StartsWith("Odd lone surrogate", messages[2], StringComparison.Ordinal);
    }

    [Fact]
    public void A_category_that_the_redactor_cannot_clean_does_not_throw()
    {
        _fx.Redactor = new ThrowingRedactor();

        var logger = _fx.Logger("Tests.Category");
        logger.LogInformation("hello");
        _fx.Flush();

        Assert.Equal("[category unavailable]", _fx.Logs.Records().Single().GetProperty("category").GetString());
    }

    private readonly struct MisbehavingValue
    {
        public override string ToString() => throw new InvalidOperationException("no text");
    }

    private sealed class MisbehavingException : Exception
    {
        public override string Message => throw new InvalidOperationException("no message");

        public override string? StackTrace => throw new InvalidOperationException("no stack");
    }
}
