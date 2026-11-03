using System.Text.Json;
using AgentStudio.Desktop.Controls.Editor;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Tests.Desktop.Editor;

public sealed class EditorBridgeTests
{
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static string TypeOf(string json) => JsonDocument.Parse(json).RootElement.GetProperty("type").GetString()!;

    [Fact]
    public void Messages_are_queued_until_the_page_is_ready_and_then_flushed_in_order()
    {
        var sent = new List<string>();
        var bridge = new EditorBridge(sent.Add);

        bridge.Open("text", "csharp", false);
        bridge.Focus();
        Assert.Empty(sent);

        bridge.HandleWebMessage("{\"type\":\"ready\"}");

        Assert.True(bridge.IsReady);
        Assert.Equal(["open", "focus"], sent.Select(TypeOf));

        bridge.HideDiff();
        Assert.Equal(3, sent.Count);
    }

    [Fact]
    public void Page_events_are_raised()
    {
        var bridge = new EditorBridge(_ => { });
        ChangedMessage? changed = null;
        var ready = 0;
        var save = 0;
        string? failure = null;
        bridge.Ready += (_, _) => ready++;
        bridge.Changed += (_, m) => changed = m;
        bridge.SaveRequested += (_, _) => save++;
        bridge.Failed += (_, m) => failure = m;

        bridge.HandleWebMessage("{\"type\":\"ready\"}");
        bridge.HandleWebMessage("{\"type\":\"changed\",\"versionId\":4,\"isDirty\":true}");
        bridge.HandleWebMessage("{\"type\":\"saveRequested\"}");
        bridge.HandleWebMessage("{\"type\":\"error\",\"message\":\"no monaco\"}");

        Assert.Equal(1, ready);
        Assert.Equal(new ChangedMessage(4, true), changed);
        Assert.Equal(1, save);
        Assert.Equal("no monaco", failure);
    }

    [Fact]
    public async Task Request_content_is_answered_by_the_matching_request_id()
    {
        var sent = new List<string>();
        var bridge = new EditorBridge(sent.Add);
        bridge.HandleWebMessage("{\"type\":\"ready\"}");

        var pending = bridge.RequestContentAsync(cancellationToken: TestContext.Current.CancellationToken);
        var requestId = JsonDocument.Parse(Assert.Single(sent)).RootElement.GetProperty("requestId").GetString()!;

        bridge.HandleWebMessage("{\"type\":\"content\",\"requestId\":\"other\",\"text\":\"wrong\",\"versionId\":1}");
        Assert.False(pending.IsCompleted);

        bridge.HandleWebMessage($"{{\"type\":\"content\",\"requestId\":\"{requestId}\",\"text\":\"right\",\"versionId\":9}}");
        var content = await pending;

        Assert.Equal("right", content.Text);
        Assert.Equal(9, content.VersionId);
    }

    [Fact]
    public async Task Request_content_times_out_when_the_page_never_answers()
    {
        var bridge = new EditorBridge(_ => { });
        bridge.HandleWebMessage("{\"type\":\"ready\"}");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            bridge.RequestContentAsync(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancelling_pending_requests_unblocks_waiters()
    {
        var bridge = new EditorBridge(_ => { });
        bridge.HandleWebMessage("{\"type\":\"ready\"}");
        var pending = bridge.RequestContentAsync(cancellationToken: TestContext.Current.CancellationToken);

        bridge.CancelPending();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void Unknown_and_malformed_messages_are_logged_and_ignored()
    {
        var logger = new RecordingLogger();
        var bridge = new EditorBridge(_ => { }, logger);
        var events = 0;
        bridge.Ready += (_, _) => events++;
        bridge.Changed += (_, _) => events++;
        bridge.SaveRequested += (_, _) => events++;

        bridge.HandleWebMessage("{\"type\":\"explode\"}");
        bridge.HandleWebMessage("garbage");
        bridge.HandleWebMessage("{\"type\":\"content\",\"requestId\":\"nobody\",\"text\":\"t\",\"versionId\":1}");

        Assert.Equal(0, events);
        Assert.False(bridge.IsReady);
        Assert.Equal(3, logger.Entries.Count);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Warning, entry.Level));
        Assert.Contains("explode", logger.Entries[0].Message, StringComparison.Ordinal);
    }
}
