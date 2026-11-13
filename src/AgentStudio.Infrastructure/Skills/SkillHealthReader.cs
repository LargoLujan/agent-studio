using System.Globalization;
using System.Text;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Skills;

namespace AgentStudio.Infrastructure.Skills;

/// <summary>
/// The local health of a skill (docs/14 D-26), with no network:
/// <list type="bullet">
/// <item>the frontmatter and the size of its <c>SKILL.md</c> (<see cref="SkillFrontmatter.Validate"/>), read from its local
/// copy. A skill without one (linked, or a copy the catalog could not use) gets them as unknown. The folder and the file
/// are checked again before the read: a link planted after the catalog listed it is not followed, and a file larger than
/// <see cref="MaxSkillFileBytes"/> is not read;</item>
/// <item>its license and its pinned commit (<see cref="SkillHealthChecks"/>);</item>
/// <item>upstream, archived and scripts, «sin comprobar (Fase 6)»: they need the network or a review. Unknown weighs more
/// than healthy (<see cref="SkillHealthReport.Status"/>), so no skill looks healthy for what was never checked.</item>
/// </list>
/// Every text is a code with its arguments: the desktop paints it in the language of the person (ADR-0054).
/// </summary>
public sealed class SkillHealthReader(ISkillScriptsInspector? scripts = null, ISkillScriptReviews? reviews = null) : ISkillHealthReader
{
    /// <summary>The largest <c>SKILL.md</c> read: far above any real skill (the specification recommends under 500 lines).</summary>
    public const long MaxSkillFileBytes = 1024 * 1024;

    private const string SkillFile = "SKILL.md";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<SkillHealthReport> ReadLocalAsync(CatalogSkill skill, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var entry = skill.Entry;
        var checks = new List<SkillHealthCheck>(await ReadSkillFileAsync(skill.LocalPath, ct).ConfigureAwait(false))
        {
            SkillHealthChecks.CheckLicense(entry.Source.License),
            SkillHealthChecks.CheckPinned(entry.Source.PinnedCommit),
            SkillHealthCheck.Coded(SkillHealthCodes.Upstream, SkillHealthStatus.Unknown, SkillsUserTexts.ReaderUpstreamUnchecked),
            SkillHealthCheck.Coded(SkillHealthCodes.Archived, SkillHealthStatus.Unknown, SkillsUserTexts.ReaderArchivedUnchecked),
            await ReadScriptsAsync(skill, ct).ConfigureAwait(false),
        };
        return new SkillHealthReport(checks);
    }

    /// <summary>
    /// The <c>scripts</c> check, local (docs/20 D-14): a skill without a <c>scripts/</c> folder is healthy; one with it is a warning until the person reviews
    /// those scripts, and again whenever their fingerprint changes. Nothing is ever run.
    /// </summary>
    private async Task<SkillHealthCheck> ReadScriptsAsync(CatalogSkill skill, CancellationToken ct)
    {
        if (skill.LocalPath is null)
        {
            return SkillHealthCheck.Coded(SkillHealthCodes.Scripts, SkillHealthStatus.Unknown, SkillsUserTexts.ReaderScriptsNoCopy);
        }

        if (scripts is null)
        {
            return SkillHealthCheck.Coded(SkillHealthCodes.Scripts, SkillHealthStatus.Unknown, SkillsUserTexts.ReaderScriptsNoReader);
        }

        try
        {
            var info = await scripts.InspectAsync(skill.LocalPath, ct).ConfigureAwait(false);
            if (info is null)
            {
                return SkillHealthCheck.Coded(SkillHealthCodes.Scripts, SkillHealthStatus.Healthy, SkillsUserTexts.ReaderScriptsNone);
            }

            var count = info.RelativePaths.Count;

            // Too big to be read: it is not «sin revisar» nor «cambiada» (nothing can be reviewed), and the card of the skill says the same.
            if (!info.Reviewable)
            {
                return SkillHealthCheck.Coded(SkillHealthCodes.Scripts, SkillHealthStatus.Warning, SkillsUserTexts.ReaderScriptsTooBig);
            }

            var reviewed = reviews is null ? null : await reviews.ReviewedSha256Async(skill.Entry.Id, ct).ConfigureAwait(false);
            if (string.Equals(reviewed, info.Sha256, StringComparison.Ordinal))
            {
                return SkillHealthCheck.Coded(SkillHealthCodes.Scripts, SkillHealthStatus.Healthy, SkillsUserTexts.ReaderScriptsReviewed, count);
            }

            return reviewed is null
                ? SkillHealthCheck.Coded(SkillHealthCodes.Scripts, SkillHealthStatus.Warning, SkillsUserTexts.ReaderScriptsUnreviewed, count)
                : SkillHealthCheck.Coded(SkillHealthCodes.Scripts, SkillHealthStatus.Warning, SkillsUserTexts.ReaderScriptsChanged, count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return SkillHealthCheck.Coded(SkillHealthCodes.Scripts, SkillHealthStatus.Unknown, SkillsUserTexts.ReaderScriptsUnreadable);
        }
    }

    /// <summary>The frontmatter and size checks of the local copy, or both unknown with the reason.</summary>
    private static async Task<IReadOnlyList<SkillHealthCheck>> ReadSkillFileAsync(string? localPath, CancellationToken ct)
    {
        if (localPath is null)
        {
            return Unread(SkillsUserTexts.ReaderUnreadNoCopy);
        }

        try
        {
            var folder = new DirectoryInfo(localPath);
            if (!folder.Exists)
            {
                return Unread(SkillsUserTexts.ReaderUnreadNoFolder);
            }

            if (IsLink(folder))
            {
                return Unread(SkillsUserTexts.ReaderUnreadFolderLink);
            }

            var file = new FileInfo(Path.Combine(folder.FullName, SkillFile));
            if (!file.Exists)
            {
                return Unread(SkillsUserTexts.ReaderUnreadFileMissing);
            }

            if (IsLink(file))
            {
                return Unread(SkillsUserTexts.ReaderUnreadFileLink);
            }

            if (file.Length > MaxSkillFileBytes)
            {
                var size = file.Length / 1024;
                return
                [
                    SkillHealthCheck.Coded(SkillHealthCodes.Frontmatter, SkillHealthStatus.Warning, SkillsUserTexts.ReaderTooLargeFrontmatter, size),
                    SkillHealthCheck.Coded(SkillHealthCodes.Size, SkillHealthStatus.Warning, SkillsUserTexts.ReaderTooLargeSize, size),
                ];
            }

            string text;
            var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true);
                text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }

            // The size check also says what the whole SKILL.md costs in context once the skill is used (docs/20 D-14).
            return SkillFrontmatter.Parse(text).Validate(folder.Name, SkillContextCost.OfText(text.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unread(SkillsUserTexts.ReaderUnreadFailed);
        }
    }

    private static SkillHealthCheck[] Unread(string reason) =>
    [
        SkillHealthCheck.Coded(SkillHealthCodes.Frontmatter, SkillHealthStatus.Unknown, reason),
        SkillHealthCheck.Coded(SkillHealthCodes.Size, SkillHealthStatus.Unknown, SkillsUserTexts.ReaderSizeUnread),
    ];

    private static bool IsLink(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;
}
