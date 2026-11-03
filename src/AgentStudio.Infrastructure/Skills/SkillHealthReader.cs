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
/// The messages are in Spanish: the catalog screen shows them as they are.
/// </summary>
public sealed class SkillHealthReader : ISkillHealthReader
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
            new(SkillHealthCodes.Upstream, SkillHealthStatus.Unknown, "Sin comprobar (Fase 6): si el repositorio de origen cambió desde el commit fijado."),
            new(SkillHealthCodes.Archived, SkillHealthStatus.Unknown, "Sin comprobar (Fase 6): si el repositorio de origen está archivado o borrado."),
            new(SkillHealthCodes.Scripts, SkillHealthStatus.Unknown, "Sin comprobar (Fase 6): si trae scripts sin revisar."),
        };
        return new SkillHealthReport(checks);
    }

    /// <summary>The frontmatter and size checks of the local copy, or both unknown with the reason.</summary>
    private static async Task<IReadOnlyList<SkillHealthCheck>> ReadSkillFileAsync(string? localPath, CancellationToken ct)
    {
        if (localPath is null)
        {
            return Unread("Sin copia local que comprobar: la skill está enlazada o su copia no se puede usar.");
        }

        try
        {
            var folder = new DirectoryInfo(localPath);
            if (!folder.Exists)
            {
                return Unread($"No existe la carpeta de la skill, así que no se pudo leer su {SkillFile}.");
            }

            if (IsLink(folder))
            {
                return Unread($"La carpeta de la skill es un enlace: no se sigue y su {SkillFile} queda sin comprobar.");
            }

            var file = new FileInfo(Path.Combine(folder.FullName, SkillFile));
            if (!file.Exists)
            {
                return Unread($"No se encuentra su {SkillFile}: quizá se borró después de leer el catálogo.");
            }

            if (IsLink(file))
            {
                return Unread($"Su {SkillFile} es un enlace: no se sigue y queda sin comprobar.");
            }

            if (file.Length > MaxSkillFileBytes)
            {
                var size = (file.Length / 1024).ToString(CultureInfo.InvariantCulture);
                return
                [
                    new(SkillHealthCodes.Frontmatter, SkillHealthStatus.Warning, $"Su {SkillFile} es demasiado grande para una skill ({size} KiB): no se lee."),
                    new(SkillHealthCodes.Size, SkillHealthStatus.Warning, $"Su {SkillFile} ocupa {size} KiB."),
                ];
            }

            string text;
            var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true);
                text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }

            return SkillFrontmatter.Parse(text).Validate(folder.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unread($"No se pudo leer su {SkillFile}: {ex.Message}");
        }
    }

    private static SkillHealthCheck[] Unread(string reason) =>
    [
        new(SkillHealthCodes.Frontmatter, SkillHealthStatus.Unknown, reason),
        new(SkillHealthCodes.Size, SkillHealthStatus.Unknown, $"Sin comprobar: no se leyó su {SkillFile}."),
    ];

    private static bool IsLink(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;
}
