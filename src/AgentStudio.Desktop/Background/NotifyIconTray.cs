using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Media = System.Windows.Media;

namespace AgentStudio.Desktop.Background;

/// <summary>
/// The tray icon of the app (docs/16 D-14, ADR-0036): <see cref="Forms.NotifyIcon"/> of Windows Forms, part of the desktop
/// shared framework the app already runs on, with its menu (<see cref="Forms.ContextMenuStrip"/>) painted with the colours
/// of the theme. Windows Forms is used here and nowhere else.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Create it on the UI thread: its hidden window receives the messages of the icon through the WPF message loop.</item>
/// <item>Nothing shows until <see cref="Show"/>: before that the icon is not in the notification area, which is what lets
/// the tests build it without anything appearing on the screen.</item>
/// <item>The tooltip is cut to <see cref="TrayToolTip.MaxLength"/> characters (Windows Forms refuses a longer one).</item>
/// <item>Windows keeps the text an icon is added with as its name: the one «Configuración» lists among the icons of the
/// notification area, and the one UI Automation reads before every later tooltip. So the icon is added with
/// <see cref="Name"/> alone, and the tooltip set before <see cref="Show"/> follows once the icon is there.</item>
/// <item>When Explorer starts again (<c>TaskbarCreated</c>), Windows Forms adds the icon again with the tooltip it holds; a
/// hidden window of this class, made when the icon is first shown, hears the same message and adds the icon again with its
/// name (<see cref="OnTaskbarCreated"/>). Windows Forms shows the menu after bringing its window to the front, so the menu
/// closes when the person clicks elsewhere.</item>
/// </list>
/// </remarks>
public sealed class NotifyIconTray : ITrayIcon
{
    /// <summary>The name the icon is added to the notification area with.</summary>
    public const string Name = "Agent Studio";

    private const int NotificationMilliseconds = 6000;

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Drawing.Icon _image;
    private Func<IReadOnlyList<TrayMenuItem>>? _build;
    private string _toolTip = Name;

    /// <summary>Hears Explorer starting again; null until the icon is first shown (or when Windows gave no id for the message).</summary>
    private TaskbarRestarts? _restarts;
    private bool _disposed;

    public NotifyIconTray()
    {
        _image = TrayIconImage.Create(Forms.SystemInformation.SmallIconSize.Width);
        _menu = new Forms.ContextMenuStrip { Renderer = new ThemeRenderer(), ShowImageMargin = false, ShowCheckMargin = false };
        _menu.Opening += OnMenuOpening;
        _icon = new Forms.NotifyIcon { Icon = _image, Text = Name, ContextMenuStrip = _menu, Visible = false };
        _icon.MouseClick += OnMouseClick;
        _icon.BalloonTipClicked += OnBalloonTipClicked;
    }

    public event EventHandler? Clicked;

    public event EventHandler? NotificationClicked;

    /// <summary>Whether the icon is in the notification area.</summary>
    public bool IsVisible => !_disposed && _icon.Visible;

    /// <summary>The tooltip, as the icon says it once it is in the notification area.</summary>
    public string ToolTip => _toolTip;

    /// <summary>The size in pixels of the image of the icon (the small icon of the system).</summary>
    public int ImageSize => _image.Width;

    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_icon.Visible)
        {
            return;
        }

        AddWithName();

        // Explorer starting again (after a crash or an update) adds every icon again: this one, with its name too.
        _restarts ??= TaskbarRestarts.Listen(OnTaskbarCreated);
    }

    public void SetToolTip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_disposed)
        {
            return;
        }

        _toolTip = text.Length <= TrayToolTip.MaxLength ? text : text[..(TrayToolTip.MaxLength - 1)] + "…";

        // Before the icon is in the notification area, Windows Forms keeps the name: the tooltip goes in once it is added.
        if (_icon.Visible)
        {
            _icon.Text = _toolTip;
        }
    }

    public void SetMenu(Func<IReadOnlyList<TrayMenuItem>> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        _build = build;
    }

    public void Notify(TrayNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (IsVisible)
        {
            _icon.ShowBalloonTip(NotificationMilliseconds, notification.Title, notification.Text, Forms.ToolTipIcon.Info);
        }
    }

    /// <summary>
    /// The menu as it would open now, built exactly as when the person opens it, as indented lines («» for a separator,
    /// «(desactivado)» after an entry that cannot be chosen). It is never shown here: for the tests and for diagnosing.
    /// </summary>
    public IReadOnlyList<string> DescribeMenu()
    {
        Rebuild();
        var lines = new List<string>();
        Describe(_menu.Items, 0, lines);
        return lines;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _restarts?.DestroyHandle();
        _restarts = null;
        _icon.Visible = false;
        _icon.MouseClick -= OnMouseClick;
        _icon.BalloonTipClicked -= OnBalloonTipClicked;
        _menu.Opening -= OnMenuOpening;
        _icon.Dispose();
        _menu.Dispose();
        _image.Dispose();
    }

    /// <summary>
    /// Puts the icon in the notification area with its name alone (what Windows keeps as the name of the icon), and then the
    /// tooltip.
    /// </summary>
    private void AddWithName()
    {
        _icon.Text = Name;
        _icon.Visible = true;
        _icon.Text = _toolTip;
    }

    /// <summary>
    /// Explorer started again: Windows Forms adds the icon again with the tooltip it holds, which Windows would then keep as its
    /// name. The icon is taken out and added again with its name, whichever of the two hidden windows hears the message first:
    /// when this one is first, the add of Windows Forms finds the icon already there and changes nothing. This path needs an
    /// icon in the notification area and Explorer starting again, so the tests do not run it.
    /// </summary>
    private void OnTaskbarCreated()
    {
        if (_disposed || !_icon.Visible)
        {
            return;
        }

        try
        {
            _icon.Visible = false;
            AddWithName();
        }
#pragma warning disable CA1031 // A message of Explorer must never bring the app down: the icon stays as Windows Forms left it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Debug.WriteLine($"Tray icon after Explorer started again: {ex.Message}");
        }
    }

    private static void Describe(Forms.ToolStripItemCollection items, int depth, List<string> lines)
    {
        foreach (Forms.ToolStripItem item in items)
        {
            if (item is Forms.ToolStripSeparator)
            {
                lines.Add(new string(' ', depth * 2));
                continue;
            }

            lines.Add(new string(' ', depth * 2) + item.Text + (item.Enabled ? string.Empty : " (desactivado)"));
            if (item is Forms.ToolStripMenuItem { HasDropDownItems: true } parent)
            {
                Describe(parent.DropDownItems, depth + 1, lines);
            }
        }
    }

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Rebuild();
        e.Cancel = _menu.Items.Count == 0;
    }

    private void Rebuild()
    {
        foreach (var old in _menu.Items.Cast<Forms.ToolStripItem>().ToList())
        {
            old.Dispose();
        }

        _menu.Items.Clear();
        foreach (var item in _build?.Invoke() ?? [])
        {
            _menu.Items.Add(Item(item));
        }
    }

    private static Forms.ToolStripItem Item(TrayMenuItem item)
    {
        if (item.IsSeparator)
        {
            return new Forms.ToolStripSeparator();
        }

        var menuItem = new Forms.ToolStripMenuItem(item.Text) { Enabled = item.IsEnabled };
        if (item.Children is { Count: > 0 } children)
        {
            foreach (var child in children)
            {
                menuItem.DropDownItems.Add(Item(child));
            }

            menuItem.DropDown.Renderer = new ThemeRenderer();
        }
        else if (item.Action is { } action)
        {
            menuItem.Click += (_, _) => _ = RunAsync(action);
        }

        return menuItem;
    }

    /// <summary>Runs what an entry does; its failure never reaches the message loop.</summary>
    private static async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A menu entry of the tray must never bring the app down; the actions log their own failures.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Debug.WriteLine($"Tray menu: {ex.Message}");
        }
    }

    private void OnMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            Clicked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnBalloonTipClicked(object? sender, EventArgs e) => NotificationClicked?.Invoke(this, EventArgs.Empty);

    /// <summary>The colours of the theme (Themes/Dark.xaml), read from the resources of the app, or its values without them.</summary>
    internal static class ThemeColours
    {
        public static Drawing.Color Surface => Read("SurfaceColor", 0x10, 0x1A, 0x30);

        public static Drawing.Color Raised => Read("SurfaceRaisedColor", 0x16, 0x22, 0x3D);

        public static Drawing.Color Border => Read("BorderColor", 0x24, 0x35, 0x58);

        public static Drawing.Color Text => Read("TextPrimaryColor", 0xE8, 0xEE, 0xF9);

        public static Drawing.Color SecondaryText => Read("TextSecondaryColor", 0x9F, 0xB0, 0xCB);

        public static Drawing.Color Accent => Read("AccentColor", 0x2E, 0x68, 0xF9);

        private static Drawing.Color Read(string key, byte r, byte g, byte b) =>
            System.Windows.Application.Current?.TryFindResource(key) is Media.Color colour
                ? Drawing.Color.FromArgb(colour.A, colour.R, colour.G, colour.B)
                : Drawing.Color.FromArgb(r, g, b);
    }

    /// <summary>The menu with the colours of the theme; disabled entries keep the secondary text colour (readable on the panel).</summary>
    private sealed class ThemeRenderer() : Forms.ToolStripProfessionalRenderer(new ThemeTable())
    {
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? ThemeColours.Text : ThemeColours.SecondaryText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = ThemeColours.Text;
            base.OnRenderArrow(e);
        }
    }

    private sealed class ThemeTable : Forms.ProfessionalColorTable
    {
        public override Drawing.Color ToolStripDropDownBackground => ThemeColours.Surface;

        public override Drawing.Color MenuBorder => ThemeColours.Border;

        public override Drawing.Color MenuItemBorder => ThemeColours.Accent;

        public override Drawing.Color MenuItemSelected => ThemeColours.Raised;

        public override Drawing.Color MenuItemSelectedGradientBegin => ThemeColours.Raised;

        public override Drawing.Color MenuItemSelectedGradientEnd => ThemeColours.Raised;

        public override Drawing.Color MenuItemPressedGradientBegin => ThemeColours.Raised;

        public override Drawing.Color MenuItemPressedGradientEnd => ThemeColours.Raised;

        public override Drawing.Color ImageMarginGradientBegin => ThemeColours.Surface;

        public override Drawing.Color ImageMarginGradientMiddle => ThemeColours.Surface;

        public override Drawing.Color ImageMarginGradientEnd => ThemeColours.Surface;

        public override Drawing.Color SeparatorDark => ThemeColours.Border;

        public override Drawing.Color SeparatorLight => ThemeColours.Border;
    }

    /// <summary>
    /// A hidden window, never shown, that hears «TaskbarCreated»: the message Explorer sends every top-level window when it
    /// starts again, the same one the hidden window of <see cref="Forms.NotifyIcon"/> hears. On the UI thread, whose message
    /// loop (WPF's) brings it the messages.
    /// </summary>
    private sealed class TaskbarRestarts : Forms.NativeWindow
    {
        private readonly int _message;
        private readonly Action _restarted;

        private TaskbarRestarts(int message, Action restarted)
        {
            _message = message;
            _restarted = restarted;
            CreateHandle(new Forms.CreateParams());
        }

        /// <summary>Starts listening; null when Windows gives no id for the message, and then nothing is heard.</summary>
        public static TaskbarRestarts? Listen(Action restarted)
        {
            var message = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            return message == 0 ? null : new TaskbarRestarts(unchecked((int)message), restarted);
        }

        protected override void WndProc(ref Forms.Message m)
        {
            if (m.Msg == _message)
            {
                _restarted();
            }

            base.WndProc(ref m);
        }
    }

    /// <summary>The user32 function that gives the id of «TaskbarCreated» (the same id for every process of the session).</summary>
    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern uint RegisterWindowMessage(string message);
    }
}

/// <summary>
/// The image of the tray icon: an «A» on the accent colour, drawn when the app starts at the size of the small icons of
/// the system, and handed to Windows as an icon file in memory (a 32-bit DIB with its alpha), so the app ships no image
/// and leaves no handle behind.
/// </summary>
public static class TrayIconImage
{
    /// <summary>Draws the icon at <paramref name="size"/> × <paramref name="size"/> pixels.</summary>
    public static Drawing.Icon Create(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 256);
        using var bitmap = Draw(size);
        using var file = new MemoryStream(IconFile(bitmap));
        return new Drawing.Icon(file, size, size);
    }

    /// <summary>The image of the icon as a bitmap: the rounded square in the accent colour with the letter.</summary>
    public static Drawing.Bitmap Draw(int size)
    {
        var bitmap = new Drawing.Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Drawing.Color.Transparent);
        var radius = Math.Max(3, size / 4f);
        using (var square = Rounded(new Drawing.RectangleF(0.5f, 0.5f, size - 1f, size - 1f), radius))
        using (var fill = new Drawing.SolidBrush(NotifyIconTray.ThemeColours.Accent))
        {
            graphics.FillPath(fill, square);
        }

        using var font = new Drawing.Font("Segoe UI", size * 0.62f, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
        using var letter = new Drawing.SolidBrush(Drawing.Color.White);
        using var centre = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
        graphics.DrawString("A", font, letter, new Drawing.RectangleF(0, size * 0.04f, size, size), centre);
        return bitmap;
    }

    /// <summary>An icon file with one 32-bit image: the directory, the header of the DIB, its rows from the bottom and an empty mask.</summary>
    private static byte[] IconFile(Drawing.Bitmap bitmap)
    {
        var size = bitmap.Width;
        var maskRow = (size + 31) / 32 * 4;
        var imageBytes = 40 + (size * size * 4) + (maskRow * size);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            // ICONDIR and its one ICONDIRENTRY.
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(imageBytes);
            writer.Write(6 + 16);

            // BITMAPINFOHEADER: the height counts the image and its mask.
            writer.Write(40);
            writer.Write(size);
            writer.Write(size * 2);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(0);
            writer.Write((size * size * 4) + (maskRow * size));
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);

            for (var y = size - 1; y >= 0; y--)
            {
                for (var x = 0; x < size; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    writer.Write(pixel.B);
                    writer.Write(pixel.G);
                    writer.Write(pixel.R);
                    writer.Write(pixel.A);
                }
            }

            // The mask is empty: the alpha of each pixel says what is transparent.
            writer.Write(new byte[maskRow * size]);
        }

        return stream.ToArray();
    }

    private static GraphicsPath Rounded(Drawing.RectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
