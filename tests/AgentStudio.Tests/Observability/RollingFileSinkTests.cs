using System.IO;
using System.Text;
using AgentStudio.Observability.Logging;

namespace AgentStudio.Tests.Observability;

public sealed class RollingFileSinkTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    private readonly TempLogDirectory _logs = new();

    // 10:00 UTC is 12:00 local (+02:00), so the local day is 2026-10-15.
    private readonly LoggingClock _clock = new(new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero));

    private readonly FileLoggerOptions _options = new();

    public RollingFileSinkTests()
    {
        _options.LogDirectory = _logs.Path;
    }

    public void Dispose() => _logs.Dispose();

    private RollingFileSink Sink() => new(_options, _clock);

    private static LogLine Line(string text, DateOnly? day = null) => new(day ?? Today, Encoding.UTF8.GetBytes(text + "\n"));

    private static string Name(DateOnly day, int index = 0) =>
        "agentstudio-" + day.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + (index == 0 ? string.Empty : "-" + index) + ".log";

    [Fact]
    public void Lines_are_appended_to_the_file_of_their_day_and_the_folder_is_created_on_demand()
    {
        Assert.False(_logs.Exists);
        using var sink = Sink();

        sink.Append([Line("first"), Line("second")]);
        sink.Append([Line("third")]);

        Assert.Equal(["agentstudio-20261015.log"], _logs.FileNames());
        Assert.Equal(["first", "second", "third"], _logs.ReadLines("agentstudio-20261015.log"));
    }

    [Fact]
    public void Each_line_goes_to_the_file_of_its_own_day_not_to_the_day_of_the_write()
    {
        var tomorrow = Today.AddDays(1);
        _clock.Set(new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero)); // the write happens "after midnight"
        using var sink = Sink();

        sink.Append([Line("late yesterday", Today), Line("early today", tomorrow), Line("late yesterday again", Today)]);

        Assert.Equal(["agentstudio-20261015.log", "agentstudio-20261016.log"], _logs.FileNames());
        Assert.Equal(["late yesterday", "late yesterday again"], _logs.ReadLines("agentstudio-20261015.log"));
        Assert.Equal(["early today"], _logs.ReadLines("agentstudio-20261016.log"));
    }

    [Fact]
    public void A_file_that_would_exceed_the_limit_rolls_to_the_next_suffix_and_no_line_is_split()
    {
        _options.MaxFileSizeBytes = 100;
        var line = new string('x', 39); // 40 bytes with the newline: two fit in 100 bytes, three do not
        using var sink = Sink();

        sink.Append([Line(line + "1"), Line(line + "2"), Line(line + "3"), Line(line + "4"), Line(line + "5")]);

        Assert.Equal([Name(Today), Name(Today, 1), Name(Today, 2)], _logs.FileNames());
        Assert.Equal([line + "1", line + "2"], _logs.ReadLines(Name(Today)));
        Assert.Equal([line + "3", line + "4"], _logs.ReadLines(Name(Today, 1)));
        Assert.Equal([line + "5"], _logs.ReadLines(Name(Today, 2)));
        Assert.All(_logs.FileNames(), name => Assert.True(_logs.ReadBytes(name).Length <= 100));
    }

    [Fact]
    public void The_roll_position_is_kept_between_batches()
    {
        _options.MaxFileSizeBytes = 100;
        var line = new string('x', 39);
        using var sink = Sink();

        sink.Append([Line(line + "1"), Line(line + "2")]);
        sink.Append([Line(line + "3")]);
        sink.Append([Line(line + "4")]);

        Assert.Equal([line + "1", line + "2"], _logs.ReadLines(Name(Today)));
        Assert.Equal([line + "3", line + "4"], _logs.ReadLines(Name(Today, 1)));
    }

    [Fact]
    public void A_line_larger_than_the_limit_is_written_alone_in_its_file()
    {
        _options.MaxFileSizeBytes = 50;
        var big = new string('b', 120);
        using var sink = Sink();

        sink.Append([Line(big), Line("small")]);

        Assert.Equal([big], _logs.ReadLines(Name(Today)));
        Assert.Equal(["small"], _logs.ReadLines(Name(Today, 1)));
    }

    [Fact]
    public void A_new_sink_resumes_in_the_last_file_of_the_day_while_it_has_room()
    {
        _options.MaxFileSizeBytes = 100;
        var line = new string('x', 39);
        using (var first = Sink())
        {
            first.Append([Line(line + "1"), Line(line + "2"), Line(line + "3")]);
        }

        using (var second = Sink())
        {
            second.Append([Line("tail")]);
        }

        Assert.Equal([Name(Today), Name(Today, 1)], _logs.FileNames());
        Assert.Equal([line + "3", "tail"], _logs.ReadLines(Name(Today, 1)));
    }

    [Fact]
    public void A_new_sink_moves_on_to_the_next_suffix_when_the_last_file_is_full()
    {
        _options.MaxFileSizeBytes = 100;
        var line = new string('x', 39);
        using (var first = Sink())
        {
            first.Append([Line(line + "1"), Line(line + "2")]);
        }

        using (var second = Sink())
        {
            second.Append([Line(line + "3")]);
        }

        Assert.Equal([line + "1", line + "2"], _logs.ReadLines(Name(Today)));
        Assert.Equal([line + "3"], _logs.ReadLines(Name(Today, 1)));
    }

    [Fact]
    public void Files_older_than_the_retention_window_are_deleted_and_everything_else_is_kept()
    {
        var keep = new[]
        {
            Name(Today),
            Name(Today.AddDays(-1)),
            Name(Today.AddDays(-14)), // exactly 14 days old: kept
            Name(Today.AddDays(1)), // a day in the future: not ours to judge
            "other.log",
            "agentstudio-notes.txt",
            "agentstudio-2026101.log", // malformed day
            "agentstudio-20261099.log", // not a date
            "agentstudio-20260101.log.bak", // not a log file
        };
        var delete = new[] { Name(Today.AddDays(-15)), Name(new DateOnly(2026, 1, 1), 3), Name(Today.AddDays(-100)) };
        foreach (var name in keep.Concat(delete))
        {
            _logs.WriteFile(name, "content");
        }

        using var sink = Sink();
        var deleted = sink.DeleteExpired();

        Assert.Equal(3, deleted);
        Assert.Equal(keep.Order(StringComparer.Ordinal), _logs.FileNames().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_retention_of_zero_days_keeps_only_today_and_newer()
    {
        _options.RetentionDays = 0;
        _logs.WriteFile(Name(Today), "today");
        _logs.WriteFile(Name(Today.AddDays(-1)), "yesterday");

        using var sink = Sink();
        var deleted = sink.DeleteExpired();

        Assert.Equal(1, deleted);
        Assert.Equal([Name(Today)], _logs.FileNames());
    }

    [Fact]
    public void Retention_uses_the_local_day_of_the_clock()
    {
        // 23:30 UTC on the 14th is already the 15th at +02:00: the file of the 1st is 14 days old (kept), the 30th of September is 15 (deleted).
        _clock.Set(new DateTimeOffset(2026, 10, 14, 23, 30, 0, TimeSpan.Zero));
        _logs.WriteFile(Name(new DateOnly(2026, 10, 1)), "kept");
        _logs.WriteFile(Name(new DateOnly(2026, 9, 30)), "deleted");

        using var sink = Sink();
        sink.DeleteExpired();

        Assert.Equal([Name(new DateOnly(2026, 10, 1))], _logs.FileNames());
    }

    [Fact]
    public void Deleting_when_the_folder_does_not_exist_is_a_no_op()
    {
        using var sink = Sink();

        Assert.Equal(0, sink.DeleteExpired());
        Assert.False(_logs.Exists);
    }

    [Fact]
    public async Task Two_sinks_can_write_the_same_file_at_the_same_time_without_losing_or_mixing_lines()
    {
        using var left = Sink();
        using var right = Sink();
        const int Batches = 60;
        const int LinesPerBatch = 5;

        void Work(RollingFileSink sink, string who)
        {
            for (var b = 0; b < Batches; b++)
            {
                sink.Append(Enumerable.Range(0, LinesPerBatch).Select(i => Line($"{who}-{b:D3}-{i}-" + new string('z', 60))).ToArray());
            }
        }

        await Task.WhenAll(
            Task.Run(() => Work(left, "L"), TestContext.Current.CancellationToken),
            Task.Run(() => Work(right, "R"), TestContext.Current.CancellationToken));

        var lines = _logs.ReadLines(Name(Today));
        Assert.Equal(2 * Batches * LinesPerBatch, lines.Length);
        Assert.Equal(lines.Length, lines.Distinct().Count());
        Assert.All(lines, line => Assert.Matches("^[LR]-\\d{3}-\\d-z{60}$", line));
    }

    [Fact]
    public void A_folder_that_cannot_be_created_raises_an_io_exception_for_the_caller_to_count()
    {
        _logs.WriteFile("blocker", "this is a file where the folder should be");
        _options.LogDirectory = _logs.Combine("blocker");
        using var sink = Sink();

        Assert.ThrowsAny<IOException>(() => sink.Append([Line("lost")]));
    }
}
