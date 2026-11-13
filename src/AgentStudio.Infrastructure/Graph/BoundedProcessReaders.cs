using System.Text;
using AgentStudio.Application.Graph;
using AgentStudio.Domain.Common;

namespace AgentStudio.Infrastructure.Graph;

/// <summary>The standard output of the analysis process, read up to its cap.</summary>
/// <param name="Text">What was read: everything when the cap was not passed, and what came before it otherwise.</param>
/// <param name="Exceeded">The process wrote more than the cap allows.</param>
public sealed record BoundedText(string Text, bool Exceeded);

/// <summary>What the standard error of the analysis process said: the last file it started on (a safe path only), a short tail of other lines, and whether it passed its cap.</summary>
public sealed record ErrorSummary(string? LastProgress, IReadOnlyList<string> Tail, bool Exceeded);

/// <summary>
/// How the host reads the two streams of the analysis process, which is not trusted (docs/20 D-18, ADR-0062): the cap is applied <b>while</b> the stream is
/// read (past it the reading stops and the caller kills the process), never after; a line of the standard error is bounded and nothing of it is believed but a
/// <c>progress:</c> line whose path is safe, whose trailing <c>\r</c> is trimmed before it is compared.
/// </summary>
public static class BoundedProcessReaders
{
    /// <summary>The longest line of the standard error that is kept; the rest of a longer line is dropped.</summary>
    public const int MaxErrorLineCharacters = 1024;

    /// <summary>How many lines of the standard error that are not progress are kept (the last ones).</summary>
    public const int MaxTailLines = 10;

    /// <summary>The most the host reads from the standard error before it kills the process; a progress line per file of a large solution is a few megabytes.</summary>
    public const long MaxErrorBytes = 64L * 1024 * 1024;

    /// <summary>Reads <paramref name="reader"/> to its end, or until it holds more than <paramref name="maxBytes"/> of UTF-8, and then calls <paramref name="onExceeded"/> and stops.</summary>
    public static async Task<BoundedText> ReadOutputAsync(TextReader reader, long maxBytes, Action onExceeded, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(onExceeded);
        var text = new StringBuilder();
        var buffer = new char[8192];
        long bytes = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            bytes += Encoding.UTF8.GetByteCount(buffer, 0, read);
            if (bytes > maxBytes)
            {
                onExceeded();
                return new BoundedText(string.Empty, true);
            }

            text.Append(buffer, 0, read);
        }

        return new BoundedText(text.ToString(), false);
    }

    /// <summary>Reads the standard error line by line with every bound described in the summary of the type.</summary>
    public static async Task<ErrorSummary> ReadErrorAsync(TextReader reader, long maxBytes, Action onExceeded, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(onExceeded);
        var line = new StringBuilder();
        var dropped = false;
        var tail = new Queue<string>();
        string? lastProgress = null;
        var buffer = new char[4096];
        long bytes = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            bytes += Encoding.UTF8.GetByteCount(buffer, 0, read);
            if (bytes > maxBytes)
            {
                onExceeded();
                return new ErrorSummary(lastProgress, [.. tail], true);
            }

            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c != '\n')
                {
                    if (line.Length < MaxErrorLineCharacters)
                    {
                        line.Append(c);
                    }
                    else
                    {
                        dropped = true;
                    }

                    continue;
                }

                var text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (!dropped && GraphWorkerProtocol.TryParseProgress(text, out var path))
                {
                    lastProgress = path;
                }
                else if (text.Length > 0)
                {
                    tail.Enqueue(UntrustedText.Clean(text.Length > 200 ? text[..200] : text));
                    if (tail.Count > MaxTailLines)
                    {
                        tail.Dequeue();
                    }
                }

                dropped = false;
            }
        }

        // A last line with no line end was cut by the end of the process: it is not believed.
        return new ErrorSummary(lastProgress, [.. tail], false);
    }
}
