using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Infrastructure.Hosting.State;

/// <summary>Where the local state of the remote lives: <c>%LOCALAPPDATA%\AgentStudio\hosting</c> by default; a test gives a folder of its own.</summary>
public sealed record RemoteSyncStoreOptions(string Root)
{
    public static RemoteSyncStoreOptions Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio", "hosting"));
}

/// <summary>
/// The local state of the remote that can be deleted (docs/21 §6): <c>sync.json</c> (the repository seen, the followed pull requests with their cursors and texts, the wait of the
/// platform and how the last call ended) and <c>pending.json</c> (what is parked or waits to synchronise). It is a cache: deleting it costs one more reading and duplicates nothing.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>It is read as not trusted.</b> The file is bounded in size and depth; a link (a reparse point) is never followed; every field is validated one by one (identifiers by the rules
/// of <see cref="RemoteRepository"/>, snapshots by <see cref="RemoteSnapshot.EnsureValid"/>, texts cleaned and cut, dates inside a sane range, enums by name). What does not validate is left out
/// and the rest is kept; a file that cannot be read at all is the same as no file.</item>
/// <item><b>It authorises nothing.</b> It holds no grant, no approval and no credential: what it returns are facts to look at, which whoever uses them checks again against the verified log.</item>
/// <item><b>It is written atomically:</b> a temporary file beside the target and a move over it; a reader finds the old state or the new one, never a mix.</item>
/// </list>
/// </remarks>
public sealed class RemoteSyncStore(RemoteSyncStoreOptions options) : IRemoteSyncStore
{
    /// <summary>The most a file is allowed to weigh to be read.</summary>
    public const int MaxFileBytes = 2 * 1024 * 1024;

    public const int MaxFollowed = 100;
    public const int MaxPending = 100;
    public const int MaxRevocations = 50;

    private const int MaxText = 256;
    private const int MaxFailingNames = 20;
    private const int MaxETag = 200;

    private static readonly DateTimeOffset EarliestDate = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LatestDate = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Regex ProjectIdRule = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex TicketRule = new("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex StageRule = new("^[a-z0-9][a-z0-9.:=;,_|-]{0,199}$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 32,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<RemoteSyncState> LoadAsync(string projectId, CancellationToken ct)
    {
        var folder = FolderOf(projectId);
        var sync = await ReadAsync<SyncFile>(Path.Combine(folder, "sync.json"), ct).ConfigureAwait(false);
        var pending = await ReadAsync<PendingFile>(Path.Combine(folder, "pending.json"), ct).ConfigureAwait(false);
        var followed = (sync?.Followed ?? []).Select(f => Follow(f)).OfType<FollowedPullRequest>()
            .GroupBy(f => f.PullRequest.TraceId, StringComparer.Ordinal).Select(g => g.Last()).Take(MaxFollowed).ToList();
        var parked = (pending?.Pending ?? []).Select(p => Park(projectId, p)).OfType<PendingRemoteWork>()
            .GroupBy(p => p.Ticket.Id, StringComparer.Ordinal).Select(g => g.Last()).Take(MaxPending).ToList();
        return new RemoteSyncState(
            sync?.Repository is { } id ? RemoteRepository.TryParseId(Bounded(id, 200)) : null,
            followed,
            parked,
            SaneDate(sync?.RateLimitedUntil),
            SaneDate(sync?.LastGoodSync),
            sync?.LastOutcome is { } outcome && Enum.TryParse<HostingOutcome>(outcome, false, out var parsed) && Enum.IsDefined(parsed) ? parsed : null,
            SaneDate(sync?.LastOutcomeAt),
            [.. (sync?.PendingRevocations ?? []).Where(id => id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')).Distinct(StringComparer.Ordinal).Take(MaxRevocations)]);
    }

    public async Task SaveAsync(string projectId, RemoteSyncState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        var folder = FolderOf(projectId);
        Directory.CreateDirectory(folder);
        if (IsLink(new DirectoryInfo(folder)))
        {
            throw new IOException("The folder of the local state of the remote is a link: it is not written.");
        }

        var sync = new SyncFile(
            1,
            state.Repository?.Id,
            [.. state.Followed.Take(MaxFollowed).Select(f => new FollowedDto(
                f.PullRequest.TraceId,
                f.Run,
                f.Cursor.ETags.ToDictionary(e => e.Key.ToString(), e => e.Value),
                f.Cursor.LastSnapshot is null ? null : RemoteSnapshotPayload.From(f.Cursor.LastSnapshot),
                new TextsDto(f.Display.Title, [.. f.Display.FailingCheckNames], f.Display.DeploymentEnvironment),
                f.PendingConfirmation is null ? null : RemoteSnapshotPayload.From(f.PendingConfirmation)))],
            state.RateLimitedUntil,
            state.LastGoodSync,
            state.LastOutcome?.ToString(),
            state.LastOutcomeAt,
            [.. (state.PendingRevocations ?? []).Take(MaxRevocations)]);
        var pending = new PendingFile(
            1,
            [.. state.Pending.Take(MaxPending).Select(p => new PendingDto(
                p.Ticket.Id,
                p.Ticket.Operation?.ToString(),
                p.Ticket.PullRequest?.TraceId,
                p.Operation is null ? null : new OperationDto(p.Operation.Run.Value, p.Operation.Kind.ToString(), p.Operation.Repository.Id, p.Operation.BaseBranch?.Name, p.Operation.PullRequest?.TraceId, p.Operation.Commit, p.Operation.Method?.ToString()),
                p.Observation is null ? null : RemoteSnapshotPayload.From(p.Observation),
                p.Observation?.PullRequest.TraceId,
                p.Stage,
                p.Reason.ToString(),
                p.Since,
                p.RetryAt))]);
        await WriteAsync(Path.Combine(folder, "sync.json"), JsonSerializer.SerializeToUtf8Bytes(sync, Json), ct).ConfigureAwait(false);
        await WriteAsync(Path.Combine(folder, "pending.json"), JsonSerializer.SerializeToUtf8Bytes(pending, Json), ct).ConfigureAwait(false);
    }

    private string FolderOf(string projectId)
    {
        if (projectId is null || !ProjectIdRule.IsMatch(projectId))
        {
            throw new ArgumentException("A project id for the local state of the remote is up to 64 letters, digits, '-' and '_'.", nameof(projectId));
        }

        return Path.Combine(options.Root, projectId);
    }

    private static async Task WriteAsync(string path, byte[] content, CancellationToken ct)
    {
        if (File.Exists(path) && IsLink(new FileInfo(path)))
        {
            throw new IOException("The local state of the remote is a link: it is not written.");
        }

        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(content, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || IsLink(info) || info.Length > MaxFileBytes || (info.Directory is { } folder && IsLink(folder)))
            {
                return null;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[(int)Math.Min(stream.Length, MaxFileBytes)];
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(buffer, Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsLink(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static DateTimeOffset? SaneDate(DateTimeOffset? date) => date is { } d && d >= EarliestDate && d <= LatestDate ? d : null;

    private static string? Bounded(string? text, int max) => text is not null && text.Length <= max ? text : null;

    /// <summary>A text of the remote kept for the screen: control and format characters out, cut to <paramref name="max"/>.</summary>
    private static string? Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            if (builder.Length + rune.Utf16SequenceLength > max)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static PullRequestRef? ParseRequest(string? traceId) => traceId is { Length: <= 300 } ? PullRequestRef.TryParseTraceId(traceId) : null;

    private static RemoteSnapshot? Snapshot(RemoteSnapshotPayload? payload, PullRequestRef pullRequest)
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            return payload.ToSnapshot(pullRequest);
        }
        catch (Exception ex) when (RemoteLogReading.IsInvalidData(ex))
        {
            return null;
        }
    }

    private static FollowedPullRequest? Follow(FollowedDto? dto)
    {
        if (dto is null || ParseRequest(dto.PullRequest) is not { } pullRequest)
        {
            return null;
        }

        string? run = null;
        if (dto.Run is not null)
        {
            if (!IsRun(dto.Run))
            {
                return null;
            }

            run = dto.Run;
        }

        var etags = new Dictionary<ObservedResource, string>();
        foreach (var (name, value) in (dto.ETags ?? []).Take(Enum.GetValues<ObservedResource>().Length))
        {
            if (Enum.TryParse<ObservedResource>(name, false, out var resource) && Enum.IsDefined(resource) && value is { Length: > 0 and <= MaxETag } && value.All(c => c is >= ' ' and <= '~'))
            {
                etags[resource] = value;
            }
        }

        var display = new RemoteDisplayTexts(
            Clean(dto.Display?.Title, MaxText),
            [.. (dto.Display?.FailingCheckNames ?? []).Take(MaxFailingNames).Select(n => Clean(n, 120)).OfType<string>()],
            Clean(dto.Display?.DeploymentEnvironment, 120));
        return new FollowedPullRequest(pullRequest, run, new ObservationCursor(etags, Snapshot(dto.LastSnapshot, pullRequest)), display, Snapshot(dto.PendingConfirmation, pullRequest));
    }

    private static bool IsRun(string run)
    {
        try
        {
            _ = new RunId(run);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static PendingRemoteWork? Park(string projectId, PendingDto? dto)
    {
        if (dto is null || dto.Id is null || !TicketRule.IsMatch(dto.Id) || dto.Stage is null || !StageRule.IsMatch(dto.Stage)
            || !Enum.TryParse<PendingReason>(dto.Reason, false, out var reason) || !Enum.IsDefined(reason)
            || SaneDate(dto.Since) is not { } since)
        {
            return null;
        }

        HostingOperationKind? ticketKind = null;
        if (dto.Operation is not null)
        {
            if (!Enum.TryParse<HostingOperationKind>(dto.Operation, false, out var kind) || !Enum.IsDefined(kind))
            {
                return null;
            }

            ticketKind = kind;
        }

        PullRequestRef? ticketRequest = null;
        if (dto.PullRequest is not null)
        {
            ticketRequest = ParseRequest(dto.PullRequest);
            if (ticketRequest is null)
            {
                return null;
            }
        }

        RemoteSnapshot? observation = null;
        if (dto.Observation is not null)
        {
            if (ParseRequest(dto.ObservationPullRequest) is not { } observed || Snapshot(dto.Observation, observed) is not { } snapshot)
            {
                return null;
            }

            observation = snapshot;
        }

        HostingOperation? operation = null;
        if (dto.Details is { } details)
        {
            operation = Operation(projectId, details);
            if (operation is null)
            {
                return null;
            }
        }

        return new PendingRemoteWork(new RemoteWorkTicket(dto.Id, ticketKind, ticketRequest), operation, observation, dto.Stage, reason, since, SaneDate(dto.RetryAt));
    }

    private static HostingOperation? Operation(string projectId, OperationDto dto)
    {
        if (!Enum.TryParse<HostingOperationKind>(dto.Kind, false, out var kind) || !Enum.IsDefined(kind) || dto.Run is null || !IsRun(dto.Run)
            || RemoteRepository.TryParseId(Bounded(dto.Repository, 200)) is not { } repository)
        {
            return null;
        }

        RemoteBranch? baseBranch = null;
        if (dto.BaseBranch is not null && (baseBranch = RemoteBranch.TryParse(dto.BaseBranch)) is null)
        {
            return null;
        }

        PullRequestRef? pullRequest = null;
        if (dto.PullRequest is not null && (pullRequest = ParseRequest(dto.PullRequest)) is null)
        {
            return null;
        }

        MergeMethod? method = null;
        if (dto.Method is not null)
        {
            if (!Enum.TryParse<MergeMethod>(dto.Method, false, out var parsed) || !Enum.IsDefined(parsed))
            {
                return null;
            }

            method = parsed;
        }

        if (dto.Commit is not null && !AgentStudio.Domain.Tracing.TraceRef.IsFullCommitId(dto.Commit))
        {
            return null;
        }

        // The folder of the project is not in the file: whoever uses the operation sets the project it belongs to.
        return new HostingOperation(new ProjectContext(projectId, string.Empty), new RunId(dto.Run), kind, repository, baseBranch, pullRequest, dto.Commit, method);
    }

    private sealed record SyncFile(int V, string? Repository, List<FollowedDto>? Followed, DateTimeOffset? RateLimitedUntil, DateTimeOffset? LastGoodSync, string? LastOutcome, DateTimeOffset? LastOutcomeAt, List<string>? PendingRevocations = null);

    private sealed record FollowedDto(string? PullRequest, string? Run, Dictionary<string, string>? ETags, RemoteSnapshotPayload? LastSnapshot, TextsDto? Display, RemoteSnapshotPayload? PendingConfirmation);

    private sealed record TextsDto(string? Title, List<string>? FailingCheckNames, string? DeploymentEnvironment);

    private sealed record PendingFile(int V, List<PendingDto>? Pending);

    private sealed record PendingDto(
        string? Id, string? Operation, string? PullRequest, OperationDto? Details, RemoteSnapshotPayload? Observation, string? ObservationPullRequest, string? Stage, string? Reason, DateTimeOffset Since, DateTimeOffset? RetryAt);

    private sealed record OperationDto(string? Run, string? Kind, string? Repository, string? BaseBranch, string? PullRequest, string? Commit, string? Method);
}

/// <summary>The registration of the local state of the remote. The lead calls it from the composition root.</summary>
public static class RemoteSyncStoreServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IRemoteSyncStore"/> over <c>%LOCALAPPDATA%\AgentStudio\hosting</c>, or over the options already registered.</summary>
    public static IServiceCollection AddAgentStudioRemoteSyncStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(RemoteSyncStoreOptions.Default());
        services.TryAddSingleton<IRemoteSyncStore>(provider => new RemoteSyncStore(provider.GetRequiredService<RemoteSyncStoreOptions>()));
        return services;
    }
}
