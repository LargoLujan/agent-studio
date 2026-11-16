using System.Windows.Input;
using AgentStudio.Desktop.Localization;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>The groups of the activity bar (docs/24 §3.1), in the order the bar shows them; the footer sits at its bottom.</summary>
public enum ActivityGroup
{
    /// <summary>«Proyecto»: home, set-up, the orchestrator and the runs resumed by themselves.</summary>
    Project,

    /// <summary>«El proyecto por dentro»: its files, its repository, its work, its memory, its skills and its agents.</summary>
    Inside,

    /// <summary>«Cuentas»: what the runs used.</summary>
    Accounts,

    /// <summary>«Más»: the rest of the screens and panels.</summary>
    More,

    /// <summary>The bottom of the bar: help and settings.</summary>
    Footer,
}

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

    /// <summary>The group of the bar the entry is shown in.</summary>
    public ActivityGroup Group { get; init; } = ActivityGroup.More;

    public bool IsEnabled => AvailableInPhase is null;

    public string ToolTip => !IsEnabled ? Texts.Format("Main_Activity_Phase", Title, AvailableInPhase)
        : Shortcut is null ? Title
        : $"{Title} ({Shortcut})";

    public ICommand Command { get; }

    /// <summary>The title: what a screen reader says for the entry of the bar, instead of the name of this type.</summary>
    public override string ToString() => Title;
}

/// <summary>A group of the activity bar with its caption and its entries, in the order of the bar.</summary>
public sealed class ActivitySection(ActivityGroup group, string caption, IReadOnlyList<ActivityItemViewModel> items)
{
    public ActivityGroup Group { get; } = group;

    /// <summary>The small caption above the entries; hidden while the bar shows only icons.</summary>
    public string Caption { get; } = caption;

    public IReadOnlyList<ActivityItemViewModel> Items { get; } = items;

    /// <summary>The caption: what a screen reader says for the group, instead of the name of this type.</summary>
    public override string ToString() => Caption;
}
