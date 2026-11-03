using System.Windows;

namespace AgentStudio.Desktop.Services;

/// <summary>
/// Where the main window was when the app last ended: its bounds while neither maximized nor minimized, in device-independent
/// pixels, and whether it was maximized. Saved in the settings on the real exit (never when it hides in the tray) and given
/// back at startup when it still falls on a monitor (<see cref="FitTo"/>).
/// </summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized)
{
    /// <summary>The height of the strip at the top of the window (its title bar) that must fall on a monitor to restore it there.</summary>
    public const double TitleBarHeight = 30;

    /// <summary>How much of that strip must be on the monitor, across and down, for the person to grab the window and move it.</summary>
    public const double GrabWidth = 100;

    /// <inheritdoc cref="GrabWidth"/>
    public const double GrabHeight = 20;

    /// <summary>Whether every value is a number, and the size is positive (a hand-edited file may say anything).</summary>
    public bool IsValid =>
        double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;

    /// <summary>
    /// The placement to restore on the monitors connected now (their work areas, in device-independent pixels), or null when
    /// it falls on none of them (the monitor it was on is gone, say): the window then opens centred with its default size.
    /// It falls on a monitor when enough of its title bar is on that monitor's work area to grab it. The size is kept within
    /// <paramref name="minimum"/> and that work area, and the title bar is kept below its top edge.
    /// </summary>
    public WindowPlacement? FitTo(IReadOnlyList<Rect> workAreas, Size minimum)
    {
        ArgumentNullException.ThrowIfNull(workAreas);
        if (!IsValid)
        {
            return null;
        }

        var titleBar = new Rect(Left, Top, Width, TitleBarHeight);
        var needed = Math.Min(GrabWidth, Width);
        Rect? best = null;
        var bestArea = 0d;
        foreach (var workArea in workAreas.Where(a => !a.IsEmpty && a.Width > 0 && a.Height > 0))
        {
            var visible = Rect.Intersect(titleBar, workArea);
            if (visible.IsEmpty || visible.Width < needed || visible.Height < GrabHeight)
            {
                continue;
            }

            var area = visible.Width * visible.Height;
            if (area > bestArea)
            {
                best = workArea;
                bestArea = area;
            }
        }

        if (best is not { } target)
        {
            return null;
        }

        var width = Math.Max(minimum.Width, Math.Min(Width, target.Width));
        var height = Math.Max(minimum.Height, Math.Min(Height, target.Height));
        return this with { Top = Math.Max(Top, target.Top), Width = width, Height = height };
    }
}
