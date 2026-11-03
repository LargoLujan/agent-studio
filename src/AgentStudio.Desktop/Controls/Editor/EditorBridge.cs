using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Desktop.Controls.Editor;

/// <summary>
/// Transport-independent half of the WebView2 bridge: serializes host messages, queues them until the page says
/// "ready", parses what the page sends and correlates <c>requestContent</c> with <c>content</c>.
/// </summary>
public sealed class EditorBridge
{
    private readonly Action<string> _post;
    private ILogger _logger;
    private readonly Queue<string> _pending = new();
    private readonly Dictionary<string, TaskCompletionSource<ContentMessage>> _requests = [];

    public EditorBridge(Action<string> postJson, ILogger? logger = null)
    {
        _post = postJson ?? throw new ArgumentNullException(nameof(postJson));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Logger for ignored or failed messages; can be replaced after construction.</summary>
    public ILogger Logger
    {
        get => _logger;
        set => _logger = value ?? NullLogger.Instance;
    }

    /// <summary>True once the page has sent <c>ready</c>.</summary>
    public bool IsReady { get; private set; }

    public event EventHandler? Ready;

    public event EventHandler<ChangedMessage>? Changed;

    public event EventHandler? SaveRequested;

    public event EventHandler<string>? Failed;

    public void Open(string content, string language, bool readOnly) =>
        Send(new OpenMessage(content, language, readOnly));

    public void ShowDiff(string original, string modified, string language) =>
        Send(new ShowDiffMessage(original, modified, language));

    public void HideDiff() => Send(new HideDiffMessage());

    public void Focus() => Send(new FocusMessage());

    public void MarkSaved(long versionId) => Send(new MarkSavedMessage(versionId));

    /// <summary>Asks the page for its current text.</summary>
    /// <exception cref="TimeoutException">The page did not answer in time.</exception>
    public async Task<ContentMessage> RequestContentAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[requestId] = completion;

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        using var registration = timeoutSource.Token.Register(() => completion.TrySetCanceled(timeoutSource.Token));

        try
        {
            Send(new RequestContentMessage(requestId));
            return await completion.Task.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The editor did not return its content in time.");
        }
        finally
        {
            _requests.Remove(requestId);
        }
    }

    /// <summary>Handles one raw JSON message from the page. Unknown or malformed messages are logged and ignored.</summary>
    public void HandleWebMessage(string? json)
    {
        if (!EditorMessageCodec.TryParse(json, out var message, out var error))
        {
            _logger.LogWarning("Ignoring editor message: {Reason}", error);
            return;
        }

        switch (message)
        {
            case ReadyMessage:
                IsReady = true;
                while (_pending.Count > 0)
                {
                    _post(_pending.Dequeue());
                }

                Ready?.Invoke(this, EventArgs.Empty);
                break;

            case ChangedMessage changed:
                Changed?.Invoke(this, changed);
                break;

            case ContentMessage content:
                if (_requests.TryGetValue(content.RequestId, out var completion))
                {
                    completion.TrySetResult(content);
                }
                else
                {
                    _logger.LogWarning("Ignoring editor content for unknown request {RequestId}", content.RequestId);
                }

                break;

            case SaveRequestedMessage:
                SaveRequested?.Invoke(this, EventArgs.Empty);
                break;

            case ErrorMessage failure:
                _logger.LogError("Editor page reported an error: {Message}", failure.Message);
                Failed?.Invoke(this, failure.Message);
                break;
        }
    }

    /// <summary>Cancels outstanding content requests (the page is going away).</summary>
    public void CancelPending()
    {
        foreach (var request in _requests.Values.ToArray())
        {
            request.TrySetCanceled();
        }
    }

    private void Send(EditorHostMessage message)
    {
        var json = EditorMessageCodec.Serialize(message);
        if (IsReady)
        {
            _post(json);
        }
        else
        {
            _pending.Enqueue(json);
        }
    }
}
