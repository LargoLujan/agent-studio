using System.IO;

using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Desktop.Controls.Editor;

/// <summary>Line ending used by a file.</summary>
public enum LineEnding
{
    CrLf,
    Lf,
}

/// <summary>Encoding details that must survive an edit/save round trip: UTF-8 BOM and line endings.</summary>
/// <param name="HasMixedLineEndings">
/// The file mixes CRLF, LF or lone CR. Monaco cannot keep that, so saving unifies them to <paramref name="LineEnding"/>.
/// </param>
public sealed record TextFileFormat(bool HasBom, LineEnding LineEnding, bool HasMixedLineEndings = false)
{
    /// <summary>Format for a file that does not exist yet: UTF-8 without BOM and the platform line ending.</summary>
    public static TextFileFormat Default { get; } =
        new(false, Environment.NewLine == "\n" ? LineEnding.Lf : LineEnding.CrLf);

    internal string NewLine => LineEnding == LineEnding.CrLf ? "\r\n" : "\n";
}

/// <summary>Reads and writes UTF-8 text files keeping their BOM and line endings, writing atomically.</summary>
public static partial class TextFileStore
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    [GeneratedRegex(@"\r\n|\r|\n")]
    private static partial Regex LineBreak();

    /// <summary>Decodes UTF-8 bytes (BOM optional) and detects the predominant line ending.</summary>
    /// <exception cref="NotSupportedException">The bytes start with a UTF-16/UTF-32 byte order mark.</exception>
    /// <exception cref="InvalidDataException">The bytes are not valid UTF-8.</exception>
    public static (string Text, TextFileFormat Format) Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith<byte>([0xFF, 0xFE]) || bytes.StartsWith<byte>([0xFE, 0xFF]))
        {
            throw new NotSupportedException("Only UTF-8 files are supported (UTF-16/UTF-32 byte order mark found).");
        }

        var hasBom = bytes.StartsWith(Utf8Bom);
        var body = hasBom ? bytes[Utf8Bom.Length..] : bytes;

        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(body);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("The file is not valid UTF-8.", ex);
        }

        return (text, new TextFileFormat(hasBom, DetectLineEnding(text), HasMixedLineEndings(text)));
    }

    /// <summary>The predominant line ending of <paramref name="text"/>; the platform default when it has none.</summary>
    public static LineEnding DetectLineEnding(string text)
    {
        int crlf = 0, lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            if (i > 0 && text[i - 1] == '\r')
            {
                crlf++;
            }
            else
            {
                lf++;
            }
        }

        if (crlf == 0 && lf == 0)
        {
            return TextFileFormat.Default.LineEnding;
        }

        return crlf >= lf ? LineEnding.CrLf : LineEnding.Lf;
    }

    /// <summary>True when <paramref name="text"/> uses more than one line break style (CRLF, LF, lone CR).</summary>
    public static bool HasMixedLineEndings(string text)
    {
        var crlf = false;
        var lf = false;
        var cr = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf = true;
                    i++;
                }
                else
                {
                    cr = true;
                }
            }
            else if (text[i] == '\n')
            {
                lf = true;
            }

            if ((crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0) > 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Rewrites every line break in <paramref name="text"/> to the requested one.</summary>
    public static string NormalizeLineEndings(string text, LineEnding lineEnding) =>
        LineBreak().Replace(text, lineEnding == LineEnding.CrLf ? "\r\n" : "\n");

    /// <summary>Encodes text as UTF-8 with or without BOM.</summary>
    public static byte[] Encode(string text, TextFileFormat format)
    {
        var body = new UTF8Encoding(false).GetBytes(NormalizeLineEndings(text, format.LineEnding));
        if (!format.HasBom)
        {
            return body;
        }

        var result = new byte[Utf8Bom.Length + body.Length];
        Utf8Bom.CopyTo(result, 0);
        body.CopyTo(result, Utf8Bom.Length);
        return result;
    }

    public static async Task<(string Text, TextFileFormat Format)> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var (text, format, _) = await ReadWithFingerprintAsync(path, cancellationToken).ConfigureAwait(false);
        return (text, format);
    }

    /// <summary>Like <see cref="ReadAsync"/>, also fingerprinting the very bytes that were decoded.</summary>
    public static async Task<(string Text, TextFileFormat Format, FileFingerprint Fingerprint)> ReadWithFingerprintAsync(
        string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var lastWrite = File.GetLastWriteTimeUtc(path);
        var (text, format) = Decode(bytes);
        return (text, format, FileFingerprint.FromBytes(bytes, lastWrite));
    }

    /// <summary>
    /// Writes to a temporary file next to <paramref name="path"/> and then replaces the target, so a crash never
    /// leaves a half-written file.
    /// </summary>
    public static async Task WriteAtomicAsync(string path, string text, TextFileFormat format, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("The path has no directory.", nameof(path));
        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllBytesAsync(temp, Encode(text, format), cancellationToken).ConfigureAwait(false);
            if (File.Exists(fullPath))
            {
                File.Replace(temp, fullPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
