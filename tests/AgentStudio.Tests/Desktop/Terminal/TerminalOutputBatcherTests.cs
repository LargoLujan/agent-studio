using AgentStudio.Desktop.Controls.Terminal;

namespace AgentStudio.Tests.Desktop.Terminal;

public sealed class TerminalOutputBatcherTests
{
    [Fact]
    public void Only_the_first_append_asks_for_a_flush_and_chunks_are_joined()
    {
        var batcher = new TerminalOutputBatcher();

        Assert.True(batcher.Append("ab"));
        Assert.False(batcher.Append("cd"));

        Assert.Equal("abcd", batcher.Take(100));
        Assert.Null(batcher.Take(100));
        Assert.True(batcher.Append("x"));
    }

    [Fact]
    public void Take_respects_the_limit_and_never_splits_a_surrogate_pair()
    {
        var batcher = new TerminalOutputBatcher();
        batcher.Append("ab😀cd"); // the emoji is a surrogate pair at index 2-3

        Assert.Equal("ab", batcher.Take(3));
        Assert.Equal("😀c", batcher.Take(3));
        Assert.Equal("d", batcher.Take(3));
        Assert.False(batcher.HasPending);
    }

    [Fact]
    public void Clear_discards_pending_text()
    {
        var batcher = new TerminalOutputBatcher();
        batcher.Append("zzz");

        batcher.Clear();

        Assert.False(batcher.HasPending);
        Assert.Null(batcher.Take(10));
    }
}
