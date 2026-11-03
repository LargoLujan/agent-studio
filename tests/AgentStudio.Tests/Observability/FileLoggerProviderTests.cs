using System.IO;
using System.Text.Json;
using AgentStudio.Observability.Logging;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Tests.Observability;

/// <summary>The provider writing real files: the fields, the daily name, the size roll, the retention and the level.</summary>
public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly LoggerFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Each_entry_is_one_json_object_per_line_with_the_required_fields()
    {
        var logger = _fx.Logger("Tests.Fields");

        logger.LogWarning(new EventId(12, "Opened"), new InvalidOperationException("boom"), "Opened {Path} in {Millis} ms", "a.cs", 42);
        logger.LogInformation("Second entry");
        _fx.Flush();

        Assert.Equal(["agentstudio-20261015.log"], _fx.Logs.FileNames());
        var bytes = _fx.Logs.ReadBytes("agentstudio-20261015.log");
        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]); // no byte-order mark
        Assert.Equal((byte)'\n', bytes[^1]);

        var records = _fx.Logs.Records();
        Assert.Equal(2, records.Count);
        var first = records[0];
        Assert.Equal("2026-10-15T10:00:00.000Z", first.GetProperty("timestamp").GetString());
        Assert.Equal("Warning", first.GetProperty("level").GetString());
        Assert.Equal("Tests.Fields", first.GetProperty("category").GetString());
        Assert.Equal(12, first.GetProperty("eventId").GetInt32());
        Assert.Equal("Opened", first.GetProperty("eventName").GetString());
        Assert.Equal("Opened a.cs in 42 ms", first.GetProperty("message").GetString());
        Assert.Equal("a.cs", first.GetProperty("properties").GetProperty("Path").GetString());
        Assert.Equal(42, first.GetProperty("properties").GetProperty("Millis").GetInt32());
        Assert.Equal("System.InvalidOperationException", first.GetProperty("exception").GetProperty("type").GetString());
        Assert.Equal("boom", first.GetProperty("exception").GetProperty("message").GetString());
        Assert.Equal("Second entry", records[1].GetProperty("message").GetString());
        Assert.False(records[1].TryGetProperty("exception", out _));
    }

    [Fact]
    public void The_file_is_named_after_the_local_day_while_the_timestamps_stay_utc()
    {
        // 23:30 UTC on the 15th is 01:30 on the 16th at +02:00.
        _fx.Clock.Set(new DateTimeOffset(2026, 10, 15, 23, 30, 0, TimeSpan.Zero));

        _fx.Logger().LogInformation("Late");
        _fx.Flush();

        Assert.Equal(["agentstudio-20261016.log"], _fx.Logs.FileNames());
        Assert.Equal("2026-10-15T23:30:00.000Z", _fx.Logs.Records().Single().GetProperty("timestamp").GetString());
    }

    [Fact]
    public void A_new_file_starts_when_the_day_changes()
    {
        var logger = _fx.Logger();

        logger.LogInformation("Day one");
        _fx.Clock.Set(new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero));
        logger.LogInformation("Day two");
        _fx.Flush();

        Assert.Equal(["agentstudio-20261015.log", "agentstudio-20261016.log"], _fx.Logs.FileNames());
        Assert.Equal("Day one", JsonDocument.Parse(_fx.Logs.ReadLines("agentstudio-20261015.log").Single()).RootElement.GetProperty("message").GetString());
        Assert.Equal("Day two", JsonDocument.Parse(_fx.Logs.ReadLines("agentstudio-20261016.log").Single()).RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void Files_roll_by_size_without_splitting_or_losing_a_line()
    {
        _fx.Options.MaxFileSizeBytes = 400;
        var logger = _fx.Logger();

        for (var i = 0; i < 12; i++)
        {
            logger.LogInformation("entry {N}", i);
        }

        _fx.Flush();

        var names = _fx.Logs.FileNames();
        Assert.True(names.Length >= 4, "Expected the entries to spread over several files, got " + string.Join(", ", names));
        Assert.Equal("agentstudio-20261015.log", names[0]);
        Assert.Equal("agentstudio-20261015-1.log", names[1]);
        Assert.All(names, name => Assert.True(_fx.Logs.ReadBytes(name).Length <= 400, name + " is over the limit"));
        var numbers = _fx.Logs.Records().Select(r => r.GetProperty("properties").GetProperty("N").GetInt32()).ToArray();
        Assert.Equal(Enumerable.Range(0, 12), numbers);
    }

    [Fact]
    public void Logs_older_than_the_retention_window_are_deleted_when_the_provider_starts()
    {
        _fx.Logs.WriteFile("agentstudio-20260901.log", "old");
        _fx.Logs.WriteFile("agentstudio-20260930.log", "just too old");
        _fx.Logs.WriteFile("agentstudio-20261001.log", "kept: 14 days old");
        _fx.Logs.WriteFile("notes.txt", "foreign file");

        _fx.Logger().LogInformation("now");
        _fx.Flush();

        Assert.Equal(["agentstudio-20261001.log", "agentstudio-20261015.log", "notes.txt"], _fx.Logs.FileNames());
    }

    [Fact]
    public void Information_is_the_default_minimum_level()
    {
        var logger = _fx.Logger();

        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
        Assert.False(logger.IsEnabled(LogLevel.None));

        logger.LogDebug("not written");
        logger.LogInformation("written");
        _fx.Flush();

        Assert.Equal(["written"], _fx.Logs.Records().Select(r => r.GetProperty("message").GetString()));
    }

    [Fact]
    public void The_minimum_level_comes_from_the_options()
    {
        _fx.Options.MinLevel = LogLevel.Debug;
        var debug = _fx.Logger();
        Assert.True(debug.IsEnabled(LogLevel.Debug));
        Assert.False(debug.IsEnabled(LogLevel.Trace));
        debug.LogDebug("debug is written");
        _fx.Flush();
        Assert.Equal(["debug is written"], _fx.Logs.Records().Select(r => r.GetProperty("message").GetString()));

        using var errors = new LoggerFixture();
        errors.Options.MinLevel = LogLevel.Error;
        var logger = errors.Logger();
        Assert.False(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));

        using var off = new LoggerFixture();
        off.Options.MinLevel = LogLevel.None;
        Assert.False(off.Logger().IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void Scopes_are_accepted_and_ignored()
    {
        var logger = _fx.Logger();

        using (logger.BeginScope("scope {Id}", 7))
        {
            logger.LogInformation("inside");
        }

        _fx.Flush();

        var record = _fx.Logs.Records().Single();
        Assert.Equal(["timestamp", "level", "category", "eventId", "message"], record.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Many_threads_can_log_at_once_without_corrupting_or_losing_lines()
    {
        _fx.Options.QueueCapacity = 4096;
        var logger = _fx.Logger();
        const int Threads = 8;
        const int PerThread = 250;

        await Task.WhenAll(Enumerable.Range(0, Threads).Select(thread => Task.Run(
            () =>
            {
                for (var i = 0; i < PerThread; i++)
                {
                    logger.LogInformation("thread {Thread} entry {N}", thread, i);
                }
            },
            TestContext.Current.CancellationToken)));
        _fx.Flush();

        var records = _fx.Logs.Records();
        Assert.Equal(Threads * PerThread, records.Count);
        Assert.Equal(0L, _fx.Provider!.DroppedCount);
        var pairs = records.Select(r => (r.GetProperty("properties").GetProperty("Thread").GetInt32(), r.GetProperty("properties").GetProperty("N").GetInt32())).ToArray();
        Assert.Equal(Threads * PerThread, pairs.Distinct().Count());
        foreach (var group in pairs.GroupBy(p => p.Item1))
        {
            Assert.Equal(Enumerable.Range(0, PerThread), group.Select(p => p.Item2)); // each thread's entries keep their order
        }
    }

    [Fact]
    public void The_options_are_validated_when_the_provider_is_created()
    {
        var redactor = _fx.Redactor;

        Assert.Throws<ArgumentOutOfRangeException>(() => new FileLoggerProvider(new FileLoggerOptions { QueueCapacity = 0 }, redactor));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileLoggerProvider(new FileLoggerOptions { MaxFileSizeBytes = 0 }, redactor));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileLoggerProvider(new FileLoggerOptions { RetentionDays = -1 }, redactor));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileLoggerProvider(new FileLoggerOptions { MaxValueLength = 0 }, redactor));
        Assert.Throws<ArgumentException>(() => new FileLoggerProvider(new FileLoggerOptions { LogDirectory = " " }, redactor));
        Assert.Throws<ArgumentException>(() => new FileLoggerProvider(new FileLoggerOptions { FileNamePrefix = string.Empty }, redactor));
        Assert.Throws<ArgumentNullException>(() => new FileLoggerProvider(null!, redactor));
        Assert.Throws<ArgumentNullException>(() => new FileLoggerProvider(new FileLoggerOptions(), null!));
    }

    [Fact]
    public void By_default_the_logs_go_to_the_local_application_data_folder()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(local), "This machine has no LocalApplicationData folder.");

        Assert.Equal(Path.Combine(local, "AgentStudio", "logs"), new FileLoggerOptions().LogDirectory);
        Assert.Equal(LogLevel.Information, new FileLoggerOptions().MinLevel);
        Assert.Equal(10L * 1024 * 1024, new FileLoggerOptions().MaxFileSizeBytes);
        Assert.Equal(14, new FileLoggerOptions().RetentionDays);
    }
}
