using System.Text.Json;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Skills;

namespace AgentStudio.Infrastructure.Skills;

/// <summary>Where the reviews of scripts are kept: a JSON file of the user, next to the global activation decisions (they go with the skill, not with a project).</summary>
public sealed record SkillScriptReviewOptions
{
    public SkillScriptReviewOptions(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        File = Path.GetFullPath(file);
    }

    public string File { get; }

    /// <summary><c>%APPDATA%\AgentStudio\skill-script-reviews.json</c>.</summary>
    public static SkillScriptReviewOptions ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentStudio", "skill-script-reviews.json"));
}

/// <summary>
/// The SHA-256 of the scripts of each skill that the person marked as reviewed (docs/20 D-14). If a script changes, the stored hash no longer matches and the skill
/// is «sin revisar» again. The file is read as untrusted (size, shape, a 64-character lowercase hexadecimal hash); a file that cannot be read means no reviews.
/// </summary>
public sealed class SkillScriptReviewStore(SkillScriptReviewOptions options) : ISkillScriptReviews
{
    public const int MaxFileBytes = 1024 * 1024;

    public const int MaxEntries = 5000;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> ReviewedSha256Async(SkillId skill, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Read().GetValueOrDefault(skill.Value);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkReviewedAsync(SkillId skill, string scriptsSha256, CancellationToken ct)
    {
        if (!IsSha256(scriptsSha256))
        {
            throw new ArgumentException("A review is the SHA-256 of the scripts: 64 lowercase hexadecimal characters.", nameof(scriptsSha256));
        }

        if (skill.Value.Length > 200)
        {
            throw new ArgumentException("The skill identifier is too long.", nameof(skill));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var reviews = Read();
            reviews[skill.Value] = scriptsSha256;
            Directory.CreateDirectory(Path.GetDirectoryName(options.File)!);
            var temporary = options.File + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, new Document(1, reviews), cancellationToken: ct).ConfigureAwait(false);
            }

            File.Move(temporary, options.File, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private Dictionary<string, string> Read()
    {
        var reviews = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var info = new FileInfo(options.File);
            if (!info.Exists || info.Length > MaxFileBytes || info.LinkTarget is not null)
            {
                return reviews;
            }

            var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(options.File));
            foreach (var (id, hash) in document?.Reviews ?? [])
            {
                if (reviews.Count < MaxEntries && id is { Length: > 0 and <= 200 } && IsSha256(hash))
                {
                    reviews[id] = hash;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            reviews.Clear();
        }

        return reviews;
    }

    private sealed record Document(int Version, Dictionary<string, string> Reviews);
}
