namespace NodifySpike.ViewModels;

/// <summary>
/// A wire between an output connector (the prerequisite) and an input connector (the dependent). Creating it marks
/// both connectors as connected; <see cref="Detach"/> undoes that and is the only way to remove it from the canvas.
/// </summary>
public sealed class ConnectionViewModel
{
    public ConnectionViewModel(ConnectorViewModel source, ConnectorViewModel target)
    {
        if (source.Flow != ConnectorFlow.Output || target.Flow != ConnectorFlow.Input)
        {
            throw new ArgumentException("A connection goes from an output connector to an input connector.");
        }

        Source = source;
        Target = target;
        Source.AddConnection();
        Target.AddConnection();
    }

    /// <summary>The output connector: its node is the prerequisite.</summary>
    public ConnectorViewModel Source { get; }

    /// <summary>The input connector: its node depends on <see cref="Source"/>'s node.</summary>
    public ConnectorViewModel Target { get; }

    public string? Branch => Source.Branch;

    internal void Detach()
    {
        Source.RemoveConnection();
        Target.RemoveConnection();
    }
}
