using System.Windows.Input;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>
/// An entry of the left activity bar: it toggles a panel or opens a screen. An entry can still be offered disabled until the
/// phase that delivers its feature (<see cref="AvailableInPhase"/>); none is today, and the bar shows only what works.
/// </summary>
public sealed class ActivityItemViewModel
{
    public ActivityItemViewModel(string id, string title, string glyph, int? availableInPhase, string? panelId, ICommand command)
    {
        Id = id;
        Title = title;
        Glyph = glyph;
        AvailableInPhase = availableInPhase;
        PanelId = panelId;
        Command = command;
    }

    public string Id { get; }

    public string Title { get; }

    /// <summary>Segoe MDL2 Assets character.</summary>
    public string Glyph { get; }

    /// <summary>Phase that delivers the feature; null when it is already available.</summary>
    public int? AvailableInPhase { get; }

    /// <summary>ContentId of the panel it toggles.</summary>
    public string? PanelId { get; }

    /// <summary>The keyboard shortcut that does the same, as the tooltip says it (for example «Ctrl+Ñ»); null when there is none.</summary>
    public string? Shortcut { get; init; }

    public bool IsEnabled => AvailableInPhase is null;

    public string ToolTip => !IsEnabled ? $"{Title}: Disponible en la Fase {AvailableInPhase}"
        : Shortcut is null ? Title
        : $"{Title} ({Shortcut})";

    public ICommand Command { get; }

    /// <summary>The title: what a screen reader says for the entry of the bar, instead of the name of this type.</summary>
    public override string ToString() => Title;
}
