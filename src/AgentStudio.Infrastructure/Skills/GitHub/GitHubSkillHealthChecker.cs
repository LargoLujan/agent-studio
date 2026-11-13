using System.Globalization;
using System.Text.Json;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Skills;

namespace AgentStudio.Infrastructure.Skills.GitHub;

/// <summary>
/// The doctor of skills against GitHub (docs/20 D-13 to D-15, F6-6 and F6-13): for every skill of the catalog that has a pinned origin, whether the
/// repository is archived (<c>archived</c>) and whether its folder changed upstream since the pinned commit (<c>upstream</c>). What the person types
/// and what an index says is never trusted: the order of a batch is fixed.
/// <list type="number">
/// <item>The permission of Settings (<see cref="ISkillsDoctorPolicy"/>). Off: no request is built and the secrets are not touched.</item>
/// <item>Every origin is validated (<see cref="GitHubSkillOrigin"/>). What does not pass is «origen no válido» and nothing is asked for it.</item>
/// <item>If there is nothing to ask, the batch ends: no presence is requested for a batch that sends nothing.</item>
/// <item>The token, once (<see cref="ISkillsDoctorSecrets.UseTokenAsync"/>: it asks for the presence, and with it denied the vault is not read).</item>
/// <item>Each skill: <c>GET /repos/{owner}/{repo}</c>, and two requests of <c>commits</c> (the pinned commit and the branch). In series, through
/// <see cref="GitHubApiClient"/>, whose guard fails closed on any other address.</item>
/// </list>
/// At the limit, or without a connection, the skills that are left are not asked and say why. The answers are untrusted data: only the fields of the
/// table of D-14 are read, with their types, and no text of GitHub reaches a message. The messages are fixed, in Spanish like those of
/// <see cref="SkillHealthReader"/>, and carry no address, header, body or token.
/// </summary>
public sealed class GitHubSkillHealthChecker(
    ISkillsDoctorPolicy policy,
    ISkillsDoctorSecrets secrets,
    GitHubApiClient client,
    GitHubResponseCache cache,
    TimeProvider? time = null) : ISkillHealthBatchChecker
{
    private static readonly JsonDocumentOptions JsonLimits = new() { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<SkillHealthBatchResult> CheckAsync(IReadOnlyList<SkillEntry> skills, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(skills);
        if (!policy.NetworkAllowed)
        {
            return new SkillHealthBatchResult([.. skills.Select(s => Failed(s.Id, SkillHealthFailure.NetworkNotAllowed, null))], false, null);
        }

        var origins = skills.Select(s => (Skill: s, Origin: OriginOf(s.Source))).ToList();
        if (origins.All(o => o.Origin is null))
        {
            return new SkillHealthBatchResult([.. origins.Select(o => Failed(o.Skill.Id, SkillHealthFailure.InvalidOrigin, null))], false, null);
        }

        SkillsDoctorTokenUse use;
        try
        {
            use = await secrets.UseTokenAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            use = SkillsDoctorTokenUse.Denied;
        }

        var token = use.Token;
        var batch = new BatchState();
        var entries = new List<SkillHealthBatchEntry>(origins.Count);
        foreach (var (skill, origin) in origins)
        {
            ct.ThrowIfCancellationRequested();
            if (origin is null)
            {
                entries.Add(Failed(skill.Id, SkillHealthFailure.InvalidOrigin, null));
            }
            else if (batch.Stopped is { } stop)
            {
                entries.Add(Failed(skill.Id, stop.Failure, stop.ResetsAt));
            }
            else
            {
                entries.Add(await CheckOneAsync(skill, origin, token, batch, ct).ConfigureAwait(false));
            }
        }

        return new SkillHealthBatchResult(entries, token is not null, batch.Stopped?.ResetsAt ?? batch.LimitUntil, use.State);
    }

    private async Task<SkillHealthBatchEntry> CheckOneAsync(SkillEntry skill, GitHubSkillOrigin origin, GitHubToken? token, BatchState batch, CancellationToken ct)
    {
        try
        {
            var repository = await AskAsync(GitHubApiAddress.Repository(origin), token, batch, ParseRepository, ct).ConfigureAwait(false);
            if (repository.Answer is null)
            {
                return new SkillHealthBatchEntry(
                    skill.Id,
                    new SkillHealthReport(
                    [
                        new(SkillHealthCodes.Archived, SkillHealthStatus.Warning, Message(SkillHealthFailure.NotFound, null)),
                        new(SkillHealthCodes.Upstream, SkillHealthStatus.Unknown, "Sin comprobar: el repositorio de origen no se encuentra."),
                    ]),
                    SkillHealthFailure.NotFound);
            }

            var archived = new SkillHealthCheck(
                SkillHealthCodes.Archived,
                repository.Answer.Archived == true ? SkillHealthStatus.Critical : SkillHealthStatus.Healthy,
                repository.Answer.Archived == true
                    ? "El repositorio de origen está archivado: ya no recibirá cambios."
                    : "El repositorio de origen no está archivado.");

            var pinned = await AskAsync(GitHubApiAddress.Commits(origin, origin.Commit), token, batch, ParseCommits, ct).ConfigureAwait(false);
            var branch = await AskAsync(GitHubApiAddress.Commits(origin, origin.Branch), token, batch, ParseCommits, ct).ConfigureAwait(false);
            return new SkillHealthBatchEntry(skill.Id, new SkillHealthReport([archived, Upstream(pinned.Answer?.CommitSha, branch.Answer?.CommitSha, pinned.Answer is not null, branch.Answer is not null)]), null);
        }
        catch (GitHubApiException ex)
        {
            if (ex.Failure is SkillHealthFailure.RateLimited or SkillHealthFailure.NoNetwork)
            {
                batch.Stopped = (ex.Failure, ex.ResetsAt);
            }

            return Failed(skill.Id, ex.Failure, ex.ResetsAt);
        }
    }

    private static SkillHealthCheck Upstream(string? pinnedSha, string? branchSha, bool pinnedFound, bool branchFound)
    {
        if (!pinnedFound)
        {
            return new(SkillHealthCodes.Upstream, SkillHealthStatus.Warning, "El commit fijado ya no existe en el origen: revisa la skill antes de usarla.");
        }

        if (!branchFound)
        {
            return new(SkillHealthCodes.Upstream, SkillHealthStatus.Warning, "La rama de origen ya no existe: revisa la skill antes de usarla.");
        }

        if (pinnedSha is null && branchSha is null)
        {
            // Both answers are empty: nothing touched the folder, so it is not there. Two empty answers are not «sin novedades».
            return new(SkillHealthCodes.Upstream, SkillHealthStatus.Warning, "No se encontró la carpeta de la skill en el origen: revisa si cambió de sitio.");
        }

        return string.Equals(pinnedSha, branchSha, StringComparison.Ordinal)
            ? new(SkillHealthCodes.Upstream, SkillHealthStatus.Healthy, "Sin novedades en su carpeta desde el commit fijado.")
            : new(SkillHealthCodes.Upstream, SkillHealthStatus.Warning, "Actualización por revisar: su carpeta cambió en el origen desde el commit fijado.");
    }

    private async Task<(GitHubCachedAnswer? Answer, int Status)> AskAsync(
        Uri uri,
        GitHubToken? token,
        BatchState batch,
        Func<byte[], GitHubCachedAnswer?> parse,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (batch.LimitUntil is { } until && now < until)
        {
            // The last answer said there are no requests left: none is sent.
            throw new GitHubApiException(SkillHealthFailure.RateLimited, until);
        }

        var key = GitHubResponseCache.KeyOf(uri, token is not null);
        var cached = cache.Get(key);
        var response = await client.GetAsync(uri, cached?.ETag, token, ct).ConfigureAwait(false);
        if (response.RateLimitRemaining == 0)
        {
            batch.LimitUntil = response.RateLimitResetsAt ?? now.AddMinutes(1);
        }

        switch (response.Status)
        {
            case 404:
                return (null, 404);
            case 304 when cached is not null:
                return (cached, 304);
            case 200:
                var answer = parse(response.Body) ?? throw new GitHubApiException(SkillHealthFailure.UnreadableResponse);
                if (!response.Redirected && response.ETag is not null)
                {
                    answer = answer with { ETag = response.ETag };
                    cache.Set(key, answer);
                }

                return (answer, 200);
            default:
                throw new GitHubApiException(SkillHealthFailure.UnreadableResponse);
        }
    }

    // The ETag is filled in by the caller; here only the fields of the table of D-14, with their types.
    private static GitHubCachedAnswer? ParseRepository(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, JsonLimits);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("archived", out var archived)
                && archived.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? new GitHubCachedAnswer("-", archived.GetBoolean(), null)
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static GitHubCachedAnswer? ParseCommits(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, JsonLimits);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            if (root.GetArrayLength() == 0)
            {
                return new GitHubCachedAnswer("-", null, null);
            }

            var first = root[0];
            return first.ValueKind == JsonValueKind.Object
                && first.TryGetProperty("sha", out var sha)
                && sha.ValueKind == JsonValueKind.String
                && GitHubSkillOrigin.IsCommit(sha.GetString())
                    ? new GitHubCachedAnswer("-", null, sha.GetString())
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>owner/repo</c> and the rest of the pinning, checked; null (never an exception) when anything is not valid, or the skill is not pinned.</summary>
    private static GitHubSkillOrigin? OriginOf(SkillSource source)
    {
        var parts = source.Repository.Split('/');
        return parts.Length == 2 && GitHubSkillOrigin.TryCreate(parts[0], parts[1], source.Ref, source.PinnedCommit, source.Path, out var origin, out _)
            ? origin
            : null;
    }

    private static SkillHealthBatchEntry Failed(SkillId skill, SkillHealthFailure failure, DateTimeOffset? resetsAt) =>
        new(
            skill,
            new SkillHealthReport(
            [
                new(SkillHealthCodes.Upstream, SkillHealthStatus.Unknown, Message(failure, resetsAt)),
                new(SkillHealthCodes.Archived, SkillHealthStatus.Unknown, Message(failure, resetsAt)),
            ]),
            failure);

    private static string Message(SkillHealthFailure failure, DateTimeOffset? resetsAt) => failure switch
    {
        SkillHealthFailure.NetworkNotAllowed => "Sin comprobar: la comprobación en GitHub está apagada (se enciende en la pantalla de Skills).",
        SkillHealthFailure.InvalidOrigin => "Sin comprobar: origen no válido (o sin commit fijado). No se preguntó nada a GitHub.",
        SkillHealthFailure.NoNetwork => "Sin comprobar: no hay conexión con GitHub.",
        SkillHealthFailure.Forbidden => "Sin comprobar: GitHub no dejó leer este repositorio.",
        SkillHealthFailure.NotFound => "El repositorio de origen no se encuentra: está borrado o es privado.",
        SkillHealthFailure.RateLimited => resetsAt is { } at
            ? "Sin comprobar: límite de GitHub, se reinicia a las " + at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + "."
            : "Sin comprobar: límite de GitHub, se reinicia en breve.",
        SkillHealthFailure.RedirectRefused => "Sin comprobar: GitHub redirigió a otro sitio y no se siguió.",
        _ => "Sin comprobar: la respuesta de GitHub no se entiende.",
    };

    private sealed class BatchState
    {
        /// <summary>The batch stops here: the limit or no connection. The skills that are left are not asked.</summary>
        public (SkillHealthFailure Failure, DateTimeOffset? ResetsAt)? Stopped { get; set; }

        /// <summary>No request until this moment (the last answer said there were none left).</summary>
        public DateTimeOffset? LimitUntil { get; set; }
    }
}
