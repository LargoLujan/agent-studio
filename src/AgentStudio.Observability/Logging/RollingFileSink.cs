using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentStudio.Observability.Logging;

/// <summary>
/// Appends lines to <c>{prefix}-YYYYMMDD.log</c>, where the day is the LOCAL calendar day of the entry's own timestamp
/// (not of the moment it is written, so an entry queued at 23:59:59 and written after midnight still lands in its
/// day). When appending a line would take a file over <c>MaxFileSizeBytes</c> the sink moves on to
/// <c>{prefix}-YYYYMMDD-1.log</c>, then <c>-2</c> and so on; a line is never split, and a line that is larger than the
/// limit by itself is written alone in a file. When it starts it resumes in the highest existing suffix of the day.
/// </summary>
/// <remarks>
/// Each batch opens the file, appends and closes it, so the data is in the operating system as soon as the batch is
/// written (a process that is killed loses at most the batch in flight) and other programs can read or delete the file
/// between batches. The file is opened with <see cref="FileShare.Read"/>: other readers are welcome, but a second
/// Agent Studio process cannot write at the same moment (it retries briefly), because .NET appends at a remembered
/// offset and two concurrent writers could overwrite each other. Not thread-safe; the provider's single writer thread
/// is its only caller.
/// </remarks>
internal sealed class RollingFileSink : ILogLineSink
{
    private const int OpenAttempts = 20;
    private const int OpenRetryDelayMilliseconds = 10;
    private const int SharingViolation = 32;
    private const int LockViolation = 33;
    private const int BufferSize = 16 * 1024;
    private const int MaxCachedDays = 8;

    private readonly string _directory;
    private readonly string _prefix;
    private readonly long _maxFileSizeBytes;
    private readonly int _retentionDays;
    private readonly TimeProvider _clock;
    private readonly Regex _fileName;
    private readonly Dictionary<DateOnly, int> _indexByDay = [];

    public RollingFileSink(FileLoggerOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _directory = options.LogDirectory;
        _prefix = options.FileNamePrefix;
        _maxFileSizeBytes = options.MaxFileSizeBytes;
        _retentionDays = options.RetentionDays;
        _clock = clock;
        _fileName = new Regex(
            "^" + Regex.Escape(_prefix) + @"-(?<day>\d{8})(?:-(?<index>\d+))?\.log$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
    }

    /// <summary>The local calendar day of a UTC instant; it names the file the entry belongs to.</summary>
    public static DateOnly DayOf(TimeProvider clock, DateTimeOffset utcInstant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcInstant, clock.LocalTimeZone).DateTime);

    public int DeleteExpired()
    {
        if (!Directory.Exists(_directory))
        {
            return 0;
        }

        var today = DayOf(_clock, _clock.GetUtcNow());
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(_directory, _prefix + "-*.log"))
        {
            var match = _fileName.Match(Path.GetFileName(path));
            if (!match.Success
                || !DateOnly.TryParseExact(match.Groups["day"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                || today.DayNumber - day.DayNumber <= _retentionDays)
            {
                continue;
            }

            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (IOException)
            {
                // In use or already gone: it will be collected next time.
            }
            catch (UnauthorizedAccessException)
            {
                // Not ours to delete: leave it.
            }
        }

        return deleted;
    }

    public void Append(IReadOnlyList<LogLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(_directory);

        FileStream? stream = null;
        var streamDay = default(DateOnly);
        try
        {
            foreach (var line in lines)
            {
                if (stream is null || streamDay != line.Day)
                {
                    stream?.Dispose();
                    stream = OpenForAppend(line.Day, line.Bytes.Length);
                    streamDay = line.Day;
                }
                else if (stream.Length > 0 && stream.Length + line.Bytes.Length > _maxFileSizeBytes)
                {
                    stream.Dispose();
                    _indexByDay[line.Day]++;
                    stream = OpenForAppend(line.Day, line.Bytes.Length);
                }

                stream.Write(line.Bytes);
            }
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public void Dispose()
    {
        // Nothing is kept open between batches.
    }

    private string PathFor(DateOnly day, int index) =>
        Path.Combine(
            _directory,
            _prefix + "-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + (index == 0 ? string.Empty : "-" + index.ToString(CultureInfo.InvariantCulture)) + ".log");

    // Opens the current file of the day, moving on to the next suffix while the file has no room for the line.
    private FileStream OpenForAppend(DateOnly day, int incomingBytes)
    {
        var index = _indexByDay.TryGetValue(day, out var known) ? known : LastExistingIndex(day);
        while (true)
        {
            var stream = Open(PathFor(day, index));
            if (stream.Length == 0 || stream.Length + incomingBytes <= _maxFileSizeBytes)
            {
                _indexByDay[day] = index;
                ForgetOldDays();
                return stream;
            }

            stream.Dispose();
            index++;
        }
    }

    private int LastExistingIndex(DateOnly day)
    {
        var index = 0;
        while (File.Exists(PathFor(day, index + 1)))
        {
            index++;
        }

        return index;
    }

    private void ForgetOldDays()
    {
        if (_indexByDay.Count <= MaxCachedDays)
        {
            return;
        }

        foreach (var day in _indexByDay.Keys.Order().Take(_indexByDay.Count - MaxCachedDays / 2).ToArray())
        {
            _indexByDay.Remove(day);
        }
    }

    private static FileStream Open(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.None);
            }
            catch (IOException failure) when (attempt < OpenAttempts && IsSharingProblem(failure))
            {
                Thread.Sleep(OpenRetryDelayMilliseconds);
            }
        }
    }

    // The low 16 bits of the HRESULT carry the Win32 error code.
    private static bool IsSharingProblem(IOException failure) =>
        (failure.HResult & 0xFFFF) is SharingViolation or LockViolation;
}
