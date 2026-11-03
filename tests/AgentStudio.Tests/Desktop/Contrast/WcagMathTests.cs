namespace AgentStudio.Tests.Desktop.Contrast;

/// <summary>The contrast maths itself, against published reference values, so the rest of the tests can be trusted.</summary>
public sealed class WcagMathTests
{
    private static readonly Rgba White = Rgba.ParseCss("#FFFFFF");

    [Fact]
    public void Black_on_white_is_21_to_1()
    {
        Assert.Equal(21.0, Wcag.Ratio(Rgba.ParseCss("#000000"), White), 6);
    }

    [Fact]
    public void Ratio_is_symmetric_and_one_for_equal_colours()
    {
        var a = Rgba.ParseCss("#0A1020");
        var b = Rgba.ParseCss("#E8EEF9");

        Assert.Equal(Wcag.Ratio(a, b), Wcag.Ratio(b, a), 12);
        Assert.Equal(1.0, Wcag.Ratio(a, a), 12);
    }

    [Theory]
    [InlineData("#767676", 4.54)] // The lightest grey that reaches AA on white (the W3C reference example).
    [InlineData("#777777", 4.48)] // One step lighter fails.
    [InlineData("#595959", 7.0)] // AAA on white.
    public void Grey_on_white_matches_the_published_values(string grey, double expected)
    {
        Assert.Equal(expected, Wcag.Ratio(Rgba.ParseCss(grey), White), 2);
    }

    [Fact]
    public void A_ratio_just_below_the_threshold_does_not_pass_even_if_it_rounds_to_it()
    {
        // The accent of the first theme: white on #2F6BFF is 4.4987:1. Rounded it reads 4.5, but it is not AA.
        var ratio = Wcag.Ratio(White, Rgba.ParseCss("#2F6BFF"));

        Assert.Equal(4.5, Math.Round(ratio, 1));
        Assert.True(ratio < Wcag.NormalText);
    }

    [Fact]
    public void Over_composites_a_translucent_colour_with_its_alpha()
    {
        var composite = Wcag.Over(Rgba.ParseCss("#FF000080"), Rgba.ParseCss("#000000"));

        Assert.Equal(new Rgba(128, 0, 0), composite);
        Assert.Equal(Rgba.ParseCss("#FF0000"), Wcag.Over(Rgba.ParseCss("#FF0000"), Rgba.ParseCss("#00FF00")));
    }

    [Fact]
    public void Css_hex_has_the_alpha_last_and_wpf_hex_has_it_first()
    {
        Assert.Equal(new Rgba(0x2F, 0x6B, 0xFF, 0x55), Rgba.ParseCss("#2F6BFF55"));
        Assert.Equal(new Rgba(0x2F, 0x6B, 0xFF, 0x55), Rgba.ParseWpf("#552F6BFF"));
        Assert.Equal(new Rgba(0x2F, 0x6B, 0xFF), Rgba.ParseWpf("#FF2F6BFF"));
        Assert.Throws<FormatException>(() => Rgba.ParseCss("#FFF"));
    }

    [Theory]
    [InlineData(ContrastKind.NormalText, 4.5)]
    [InlineData(ContrastKind.LargeText, 3.0)]
    [InlineData(ContrastKind.UiComponent, 3.0)]
    [InlineData(ContrastKind.FocusIndicator, 3.0)]
    [InlineData(ContrastKind.TextOnHighlight, 3.0)]
    public void Each_kind_has_its_WCAG_threshold(ContrastKind kind, double minimum)
    {
        Assert.Equal(minimum, kind.Minimum());
    }
}
