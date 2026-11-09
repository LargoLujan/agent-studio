using System.Threading.Channels;

namespace AgentStudio.Desktop.Lock;

/// <summary>
/// One confirmation of the person at a time (WP5, re-review of 2026-11-09): a channel of capacity one with a single consumer.
/// Taking the slot writes into it; a second confirmation that finds it taken is refused at once, never queued, so one unlock of
/// Windows, or one Windows Hello, never confirms two approvals. The one that holds it reads its token back when it ends.
/// </summary>
/// <remarks>
/// Refused, not queued: a question that waited for the slot would be confirmed by whatever the person does for the first one,
/// and the lock screen of Windows does not say which approval it is for.
/// </remarks>
internal sealed class PresenceConfirmationSlot
{
    private readonly Channel<byte> _slot = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
    });

    /// <summary>Takes the slot; null when another confirmation holds it. Disposing what it returns frees it, once.</summary>
    public IDisposable? TryTake() => _slot.Writer.TryWrite(0) ? new Taken(_slot.Reader) : null;

    private sealed class Taken(ChannelReader<byte> reader) : IDisposable
    {
        private int _freed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _freed, 1) == 0)
            {
                reader.TryRead(out _);
            }
        }
    }
}
