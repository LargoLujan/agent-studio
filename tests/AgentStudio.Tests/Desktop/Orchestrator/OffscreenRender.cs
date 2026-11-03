using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// Renders a view that is never shown: it is measured, arranged and laid out on the WPF test thread and drawn into a
/// <see cref="RenderTargetBitmap"/>. No window, no HWND, nothing on the user's screen. Call inside <c>WpfHost.Run</c>.
/// </summary>
internal static class OffscreenRender
{
    /// <summary>When set to a folder, the renders are also saved there as PNG (docs/evidencias, run once by hand).</summary>
    public const string EvidenceVariable = "AGENTSTUDIO_RENDER_EVIDENCE";

    /// <summary>120 dpi, the 125 % display the spike captures used, so the PNGs match the earlier evidence.</summary>
    public const double Dpi = 120;

    public static BitmapSource Render(FrameworkElement element, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(element);
        var size = new Size(width, height);
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();

        // Bindings, triggers and the connector anchors settle over a few passes. Routed commands (zoom, fit) are asked
        // again whether they can run: with no window there is no input to make WPF ask after the bindings resolved.
        for (var pass = 0; pass < 3; pass++)
        {
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            WpfHost.FlushBindings();
            element.UpdateLayout();
        }

        var scale = Dpi / 96d;
        var bitmap = new RenderTargetBitmap((int)Math.Round(width * scale), (int)Math.Round(height * scale), Dpi, Dpi, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(element), null, new Rect(size));
        }

        bitmap.Render(drawing);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>How many pixels are within <paramref name="tolerance"/> of <paramref name="colour"/> on every channel.</summary>
    public static int Count(BitmapSource bitmap, Color colour, int tolerance = 6) => Pixels(bitmap).Count(p => Near(p, colour, tolerance));

    /// <summary>How many pixels differ from <paramref name="colour"/> by more than <paramref name="tolerance"/>.</summary>
    public static int CountUnlike(BitmapSource bitmap, Color colour, int tolerance = 6) => Pixels(bitmap).Count(p => !Near(p, colour, tolerance));

    /// <summary>Saves the bitmap as <paramref name="fileName"/> in the folder named by <see cref="EvidenceVariable"/>, if any.</summary>
    public static string? SaveIfRequested(BitmapSource bitmap, string fileName)
    {
        var folder = Environment.GetEnvironmentVariable(EvidenceVariable);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static bool Near(Color a, Color b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private static IEnumerable<Color> Pixels(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0)
            {
                continue; // Nothing drawn here.
            }

            yield return Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]);
        }
    }
}
