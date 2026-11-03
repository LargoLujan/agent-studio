using CommunityToolkit.Mvvm.ComponentModel;

namespace NodifySpike.ViewModels;

/// <summary>
/// State of the wire the user is dragging. The view binds <see cref="Source"/> and <see cref="PreviewTarget"/> one way to
/// source from Nodify's PendingConnection; on every hover the same rules that will decide the drop produce the text
/// shown next to the cursor, so a rejected connection explains itself before the mouse button is released.
/// </summary>
public sealed partial class PendingConnectionViewModel : ObservableObject
{
    private readonly CanvasViewModel _canvas;

    public PendingConnectionViewModel(CanvasViewModel canvas)
    {
        _canvas = canvas;
    }

    /// <summary>The connector the drag started from (Nodify writes its DataContext here).</summary>
    [ObservableProperty]
    private object? _source;

    /// <summary>The connector under the cursor, or null (Nodify writes its DataContext here while dragging).</summary>
    [ObservableProperty]
    private object? _previewTarget;

    [ObservableProperty]
    private string? _previewText;

    [ObservableProperty]
    private bool _isPreviewError;

    partial void OnPreviewTargetChanged(object? value)
    {
        if (Source is not ConnectorViewModel source || value is not ConnectorViewModel target || ReferenceEquals(source, target))
        {
            PreviewText = null;
            IsPreviewError = false;
            return;
        }

        var check = _canvas.Check(source, target);
        IsPreviewError = check.Verdict != ConnectionVerdict.Allowed;
        PreviewText = check.Verdict == ConnectionVerdict.Allowed ? $"Conectar con {target.Node.Id}" : check.Message;
    }
}
