using System.Globalization;

namespace AgentStudio.Tests.Desktop.Contrast;

/// <summary>An sRGB colour with straight alpha. Channels are 0..255.</summary>
internal readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>Parses CSS hex: <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (alpha LAST, as in CSS and Monaco).</summary>
    public static Rgba ParseCss(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var digits = hex.TrimStart('#');
        if (digits.Length is not (6 or 8))
        {
            throw new FormatException($"Not a #RRGGBB or #RRGGBBAA colour: '{hex}'");
        }

        return new Rgba(Byte(digits, 0), Byte(digits, 2), Byte(digits, 4), digits.Length == 8 ? Byte(digits, 6) : (byte)255);
    }

    /// <summary>Parses WPF hex: <c>#RRGGBB</c> or <c>#AARRGGBB</c> (alpha FIRST).</summary>
    public static Rgba ParseWpf(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var digits = hex.TrimStart('#');
        return digits.Length switch
        {
            6 => new Rgba(Byte(digits, 0), Byte(digits, 2), Byte(digits, 4)),
            8 => new Rgba(Byte(digits, 2), Byte(digits, 4), Byte(digits, 6), Byte(digits, 0)),
            _ => throw new FormatException($"Not a #RRGGBB or #AARRGGBB colour: '{hex}'"),
        };
    }

    public bool IsOpaque => A == 255;

    /// <summary>Opaque CSS notation, or with alpha when translucent.</summary>
    public override string ToString() => IsOpaque ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    private static byte Byte(string digits, int start) =>
        byte.Parse(digits.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}

/// <summary>
/// WCAG 2.x contrast maths (relative luminance and contrast ratio, success criterion 1.4.3 and 1.4.11).
/// Ratios are never rounded before being compared with a threshold: 4.499 does not meet 4.5.
/// </summary>
internal static class Wcag
{
    /// <summary>1.4.3: text smaller than large text.</summary>
    public const double NormalText = 4.5;

    /// <summary>1.4.3: large text (at least 24 px, or 18.66 px bold) and 1.4.11: UI components and graphics.</summary>
    public const double ThreeToOne = 3.0;

    /// <summary>Relative luminance of an opaque colour.</summary>
    public static double Luminance(Rgba colour)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(colour.R)) + (0.7152 * Channel(colour.G)) + (0.0722 * Channel(colour.B));
    }

    /// <summary>Contrast ratio between two opaque colours, in 1..21.</summary>
    public static double Ratio(Rgba a, Rgba b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var (light, dark) = la >= lb ? (la, lb) : (lb, la);
        return (light + 0.05) / (dark + 0.05);
    }

    /// <summary>Composites <paramref name="top"/> (with its alpha) over the opaque <paramref name="below"/>.</summary>
    public static Rgba Over(Rgba top, Rgba below)
    {
        var alpha = top.A / 255.0;
        byte Mix(byte t, byte b) => (byte)Math.Round((alpha * t) + ((1 - alpha) * b), MidpointRounding.AwayFromZero);
        return new Rgba(Mix(top.R, below.R), Mix(top.G, below.G), Mix(top.B, below.B));
    }
}

/// <summary>What a pair of colours is, which decides the minimum ratio it must reach.</summary>
public enum ContrastKind
{
    /// <summary>Text below the large-text size: 4.5:1 (1.4.3).</summary>
    NormalText,

    /// <summary>Text of at least 24 px, or 18.66 px bold: 3:1 (1.4.3).</summary>
    LargeText,

    /// <summary>Boundary, glyph, graphic or state fill of a UI component against what is next to it: 3:1 (1.4.11).</summary>
    UiComponent,

    /// <summary>Keyboard focus indicator against the colours next to it: 3:1 (1.4.11, 2.4.7).</summary>
    FocusIndicator,

    /// <summary>
    /// Syntax-coloured text under a translucent highlight (selection, find match, word highlight). A transient state
    /// of text whose default colour already passes 4.5:1 on the same highlight: 3:1 is the floor enforced here.
    /// </summary>
    TextOnHighlight,
}

internal static class ContrastKindExtensions
{
    public static double Minimum(this ContrastKind kind) => kind == ContrastKind.NormalText ? Wcag.NormalText : Wcag.ThreeToOne;

    public static string Label(this ContrastKind kind) => kind switch
    {
        ContrastKind.NormalText => "Texto",
        ContrastKind.LargeText => "Texto grande",
        ContrastKind.UiComponent => "Componente",
        ContrastKind.FocusIndicator => "Foco",
        ContrastKind.TextOnHighlight => "Texto sobre resaltado",
        _ => kind.ToString(),
    };
}
