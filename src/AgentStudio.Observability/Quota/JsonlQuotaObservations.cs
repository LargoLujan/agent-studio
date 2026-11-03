using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Observability.Quota;

/// <summary>
/// The user's record of quota observations (docs/14 D-14): one JSON line per observation in
/// <see cref="QuotaLogOptions.FilePath"/>, only ever appended to. When appending would take the file over
/// <see cref="QuotaLogOptions.MaxFileBytes"/> it rotates: the older files move down (<c>quota.1.jsonl</c> is the newest,
/// and those beyond <see cref="QuotaLogOptions.RetainedFiles"/> are deleted) and the new file starts with the latest
/// observation of every window, so <see cref="LatestAsync"/> reads only the current file and never loses a window that
/// was seen long ago. The quota belongs to the account, not to a project, so the record lives in the user's profile.
/// <list type="bullet">
/// <item>The utilization is the provider's own figure: one that Agent Studio measured or estimated is refused, and so is
/// the whole batch it came in.</item>
/// <item>A torn last line (the process died while writing) and lines that are not observations are skipped when
/// reading, and the next append starts on a line of its own.</item>
/// <item>The latest observation of a window is the one observed last; one without utilization never hides an earlier one
/// that has it, so the panel keeps the last figure the provider gave with its age.</item>
/// </list>
/// The record is only shown: nothing decides on it. One writer per process; another process that writes at the same
/// moment is waited for briefly.
/// </summary>
public sealed class JsonlQuotaObservations : IQuotaObservations
{
    private const int OpenAttempts = 20;
    private const int SharingViolation = 32;
    private const int LockViolation = 33;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly QuotaLogOptions _options;

    public JsonlQuotaObservations(QuotaLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    /// <exception cref="ArgumentException">An observation has no window or source, or a utilization that is not the provider's; nothing is written.</exception>
    public async Task AppendAsync(IReadOnlyList<QuotaObservation> observations, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count == 0)
        {
            return;
        }

        var text = new StringBuilder();
        foreach (var observation in observations)
        {
            text.Append(Line(observation)).Append('\n');
        }

        var bytes = Utf8.GetBytes(text.ToString());
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_options.FilePath))!);
            var length = File.Exists(_options.FilePath) ? new FileInfo(_options.FilePath).Length : 0;
            if (length > 0 && length + bytes.Length > _options.MaxFileBytes)
            {
                await RotateAsync(ct).ConfigureAwait(false);
            }

            await AppendAsync(bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The latest observation of each window of each provider, by provider and window.</summary>
    public async Task<IReadOnlyList<QuotaObservation>> LatestAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Latest(await ReadAsync(_options.FilePath, ct).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<QuotaObservation> Latest(IEnumerable<QuotaObservation> observations)
    {
        var latest = new Dictionary<(string Provider, string Window), QuotaObservation>();
        foreach (var observation in observations)
        {
            var key = (observation.Provider.Value, observation.Window);
            if (!latest.TryGetValue(key, out var known) || Supersedes(observation, known))
            {
                latest[key] = observation;
            }
        }

        return [.. latest.OrderBy(p => p.Key.Provider, StringComparer.Ordinal).ThenBy(p => p.Key.Window, StringComparer.Ordinal).Select(p => p.Value)];
    }

    /// <summary>A figure beats no figure; otherwise the one observed later (or written later at the same moment) wins.</summary>
    private static bool Supersedes(QuotaObservation candidate, QuotaObservation known) =>
        candidate.Utilization.HasValue != known.Utilization.HasValue ? candidate.Utilization.HasValue : candidate.ObservedAt >= known.ObservedAt;

    /// <summary>Moves the older files down and starts the current one again with the latest observation of every window.</summary>
    private async Task RotateAsync(CancellationToken ct)
    {
        var path = _options.FilePath;
        var seed = new StringBuilder();
        foreach (var observation in Latest(await ReadAsync(path, ct).ConfigureAwait(false)))
        {
            seed.Append(Line(observation)).Append('\n');
        }

        var retained = _options.RetainedFiles;
        if (retained > 0)
        {
            File.Delete(Rotated(retained));
            for (var i = retained - 1; i >= 1; i--)
            {
                if (File.Exists(Rotated(i)))
                {
                    File.Move(Rotated(i), Rotated(i + 1), overwrite: true);
                }
            }
        }

        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, Utf8.GetBytes(seed.ToString()), ct).ConfigureAwait(false);
        File.Replace(temporary, path, retained > 0 ? Rotated(1) : null, ignoreMetadataErrors: true);
    }

    private string Rotated(int index)
    {
        var path = _options.FilePath;
        var name = Path.GetFileNameWithoutExtension(path) + "." + index.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(path);
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, name);
    }

    private async Task AppendAsync(byte[] bytes, CancellationToken ct)
    {
        var stream = await OpenAsync(_options.FilePath, ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            if (stream.Length > 0)
            {
                // A line torn by a process that died while writing must not swallow the first line of this append.
                stream.Seek(-1, SeekOrigin.End);
                var last = new byte[1];
                if (await stream.ReadAsync(last, ct).ConfigureAwait(false) == 1 && last[0] != (byte)'\n')
                {
                    stream.Seek(0, SeekOrigin.End);
                    await stream.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
                }
            }

            stream.Seek(0, SeekOrigin.End);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<FileStream> OpenAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.Asynchronous);
            }
            catch (IOException failure) when (attempt < OpenAttempts && (failure.HResult & 0xFFFF) is SharingViolation or LockViolation)
            {
                await Task.Delay(10 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<List<QuotaObservation>> ReadAsync(string path, CancellationToken ct)
    {
        var observations = new List<QuotaObservation>();
        if (!File.Exists(path))
        {
            return observations;
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (Parse(line) is { } observation)
                {
                    observations.Add(observation);
                }
            }
        }

        return observations;
    }

    private static string Line(QuotaObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (string.IsNullOrWhiteSpace(observation.Window) || string.IsNullOrWhiteSpace(observation.Source) || string.IsNullOrWhiteSpace(observation.Provider.Value))
        {
            throw new ArgumentException("A quota observation needs a provider, a window and a source.", nameof(observation));
        }

        var utilization = observation.Utilization;
        if (!IsTheProvidersOwn(utilization.Origin) || (utilization.Value is { } value && !double.IsFinite(value)))
        {
            throw new ArgumentException(
                $"The utilization of window '{observation.Window}' must be the provider's own figure, not {utilization.Origin} ({utilization.Source}).",
                nameof(observation));
        }

        return JsonSerializer.Serialize(
            new LineDto(
                observation.Provider.Value,
                observation.Window,
                new FigureDto(utilization.Value, utilization.Origin.ToString(), utilization.Source),
                observation.Length,
                observation.ResetsAt,
                observation.ObservedAt,
                observation.Source,
                observation.Label,
                observation.Reached),
            JsonOptions);
    }

    /// <summary>An observation read back, or null for a line that is not one (torn, foreign or contradictory).</summary>
    private static QuotaObservation? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        LineDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<LineDto>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (dto is not { Provider: { Length: > 0 } provider, Window: { Length: > 0 } window, Utilization: { Origin: { } originName, Source: { Length: > 0 } utilizationSource } figure, ObservedAt: { } observedAt, Source: { Length: > 0 } source }
            || string.IsNullOrWhiteSpace(provider)
            || !Enum.TryParse<MetricOrigin>(originName, ignoreCase: false, out var origin)
            || !Enum.IsDefined(origin)
            || !IsTheProvidersOwn(origin)
            || (figure.Value is { } value && !double.IsFinite(value)))
        {
            return null;
        }

        MetricValue<double> utilization;
        try
        {
            utilization = MetricValue<double>.Of(origin, figure.Value, utilizationSource);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return new QuotaObservation(new ProviderId(provider), window, utilization, dto.Length, dto.ResetsAt, observedAt, source)
        {
            Label = dto.Label,
            Reached = dto.Reached,
        };
    }

    /// <summary>The provider reported or estimated the figure, or did not give it; Agent Studio never works one out.</summary>
    private static bool IsTheProvidersOwn(MetricOrigin origin) => origin is not (MetricOrigin.MeasuredBySystem or MetricOrigin.EstimatedBySystem);

    private sealed record LineDto(
        string? Provider,
        string? Window,
        FigureDto? Utilization,
        TimeSpan? Length,
        DateTimeOffset? ResetsAt,
        DateTimeOffset? ObservedAt,
        string? Source,
        string? Label,
        string? Reached);

    private sealed record FigureDto(double? Value, string? Origin, string? Source);
}
