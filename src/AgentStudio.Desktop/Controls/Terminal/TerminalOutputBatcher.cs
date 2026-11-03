using System.Text;

namespace AgentStudio.Desktop.Controls.Terminal;

/// <summary>
/// Accumulates terminal output written from any thread so the UI thread can forward it to xterm.js in few, large
/// messages instead of one per read. Chunks are never cut in the middle of a surrogate pair.
/// </summary>
public sealed class TerminalOutputBatcher
{
    private readonly object _gate = new();
    private readonly StringBuilder _pending = new();

    /// <summary>Adds text; returns true when the buffer was empty before, i.e. the caller must schedule a flush.</summary>
    public bool Append(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            var wasEmpty = _pending.Length == 0;
            _pending.Append(text);
            return wasEmpty;
        }
    }

    /// <summary>Removes and returns up to <paramref name="maxChars"/> characters, or null when nothing is pending.</summary>
    public string? Take(int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 2);
        lock (_gate)
        {
            if (_pending.Length == 0)
            {
                return null;
            }

            var count = Math.Min(maxChars, _pending.Length);
            if (count < _pending.Length && char.IsHighSurrogate(_pending[count - 1]))
            {
                count--;
            }

            var chunk = _pending.ToString(0, count);
            _pending.Remove(0, count);
            return chunk;
        }
    }

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _pending.Length > 0;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }
}
