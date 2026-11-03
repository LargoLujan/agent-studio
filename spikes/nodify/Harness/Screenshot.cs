using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NodifySpike.Harness;

/// <summary>A captured bitmap, its pixels and how to map a point of the UI onto them.</summary>
internal sealed class CapturedImage
{
    private readonly byte[] _pixels;
    private readonly int _stride;
    private readonly Visual _root;
    private readonly double _scale;

    public CapturedImage(BitmapSource bitmap, Visual root, double scale)
    {
        Bitmap = bitmap;
        _root = root;
        _scale = scale;
        _stride = bitmap.PixelWidth * 4;
        _pixels = new byte[_stride * bitmap.PixelHeight];
        bitmap.CopyPixels(_pixels, _stride, 0);
    }

    public BitmapSource Bitmap { get; }

    public int Width => Bitmap.PixelWidth;

    public int Height => Bitmap.PixelHeight;

    /// <summary>Pixel of an element-local point (device-independent units), or null outside the capture.</summary>
    public (int X, int Y)? PixelOf(FrameworkElement element, double localX, double localY)
    {
        var point = element.TransformToAncestor(_root).Transform(new Point(localX, localY));
        var x = (int)Math.Round(point.X * _scale);
        var y = (int)Math.Round(point.Y * _scale);
        return x >= 0 && y >= 0 && x < Width && y < Height ? (x, y) : null;
    }

    public Color? PixelAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return null;
        }

        var i = (y * _stride) + (x * 4);
        return Color.FromRgb(_pixels[i + 2], _pixels[i + 1], _pixels[i]); // Pbgra32 over an opaque background
    }

    /// <summary>The most frequent colour of a pixel rectangle (grid lines and text are rare, so this is the surface colour).</summary>
    public Color ModeColour(int x, int y, int width, int height, int step = 3)
    {
        var histogram = new Dictionary<int, int>();
        for (var py = y; py < y + height; py += step)
        {
            for (var px = x; px < x + width; px += step)
            {
                if (PixelAt(px, py) is { } pixel)
                {
                    var key = (pixel.R << 16) | (pixel.G << 8) | pixel.B;
                    histogram[key] = histogram.GetValueOrDefault(key) + 1;
                }
            }
        }

        var best = histogram.MaxBy(kv => kv.Value).Key;
        return Color.FromRgb((byte)(best >> 16), (byte)(best >> 8), (byte)best);
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

/// <summary>
/// Off-screen capture: the UI is rendered into a <see cref="RenderTargetBitmap"/> and never needs to be on a display, so no
/// window has to be shown on the user's screen. The bitmap is rendered at an explicit DPI (120 = a 125 % display), which makes
/// the PNG physical-pixel exact whatever monitor the hosting window happens to be nearest to. The AdornerDecorator that
/// holds the area is rendered, not the area alone, so adorners (the keyboard focus ring, hot keys) are part of the picture.
/// </summary>
internal static class Screenshot
{
    public const double Dpi = 120;

    public static CapturedImage Capture(FrameworkElement area)
    {
        var root = VisualTree.Ancestor<AdornerDecorator>(area) ?? (FrameworkElement)area;
        var width = root.ActualWidth;
        var height = root.ActualHeight;
        var scale = Dpi / 96d;

        var bitmap = new RenderTargetBitmap((int)Math.Round(width * scale), (int)Math.Round(height * scale), Dpi, Dpi, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush((Color)Application.Current.Resources["BackgroundColor"]), null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }

        bitmap.Render(drawing);
        bitmap.Freeze();
        return new CapturedImage(bitmap, root, scale);
    }
}
