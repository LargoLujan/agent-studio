using System.Windows.Controls;
using System.Windows.Media;
using AgentStudio.Desktop.Controls;

namespace AgentStudio.Tests.Desktop.Contrast;

/// <summary>
/// WCAG 2.x AA of the common bar (docs/18 P3), measured on the real resources: the theme tokens of Themes/Dark.xaml and the
/// brushes of the control's own resources (Controls/MetricBar.xaml).
/// <list type="bullet">
/// <item>The track (the darkest surface) and its outline, against every surface a bar sits on: 3:1 (1.4.11), so an empty
/// bar is seen.</item>
/// <item>The fill of every level against the track, which is what says the value: 3:1. The fill with the pattern of an
/// estimate too.</item>
/// <item>The texts of the bar on every surface: 4.5:1 (1.4.3).</item>
/// <item>Exempt and listed: the stripes of the pattern against the fill and the marks of the thresholds over the fill.
/// They repeat what the words say («~», «estimado», the level), so they are not required visual information.</item>
/// </list>
/// </summary>
public sealed class MetricBarContrastTests
{
    private static readonly (string Token, string Id, string Name)[] Surfaces =
    [
        ("BackgroundColor", "bg", "ventana"),
        ("SurfaceColor", "surface", "barras, paneles y la bandeja"),
        ("SurfaceRaisedColor", "raised", "tarjetas"),
    ];

    private static readonly MetricBarLevel[] Levels = [MetricBarLevel.Neutral, MetricBarLevel.Healthy, MetricBarLevel.Warning, MetricBarLevel.Compression, MetricBarLevel.Critical];

    [Fact]
    public void The_bar_meets_WCAG_AA_on_every_surface_and_in_every_level()
    {
        var table = WpfHost.Run(() =>
        {
            var bar = new MetricBar();
            var track = ThemeResources.Color("BackgroundBrush");
            var outline = ThemeResources.Color("BorderStrongBrush");
            var stripe = ThemeResources.ToRgba(bar.Resources[MetricBar.HatchStripeKey] as Brush, MetricBar.HatchStripeKey);
            var mark = ThemeResources.ToRgba(bar.Resources[MetricBar.MarkKey] as Brush, MetricBar.MarkKey);
            var t = new ContrastTable("barras");

            foreach (var (token, id, name) in Surfaces)
            {
                var surface = ThemeResources.Color(token);
                t.Add($"outline.{id}", $"Borde de la pista de una barra sobre {name}", ContrastKind.UiComponent, "BorderStrongBrush", outline, token, surface);
                t.Add($"value.{id}", $"Valor de una barra («62 %») sobre {name}", ContrastKind.NormalText, "TextPrimaryBrush", ThemeResources.Color("TextPrimaryBrush"), token, surface);
                t.Add($"origin.{id}", $"Origen y nivel de una barra sobre {name}", ContrastKind.NormalText, "SecondaryText.Foreground", ThemeResources.ToRgba(ThemeResources.SetterBrush(ThemeResources.Style("SecondaryText"), TextBlock.ForegroundProperty), "SecondaryText"), token, surface);
            }

            foreach (var level in Levels.Distinct())
            {
                var key = MetricBar.FillKey(level);
                var fill = ThemeResources.Color(key);
                var hatched = Wcag.Over(stripe, fill);
                var id = level.ToString().ToLowerInvariant();
                t.Add($"fill.{id}", $"Relleno de una barra «{level}» frente a la pista vacía", ContrastKind.UiComponent, key, fill, "BackgroundBrush", track);
                t.Add($"hatch.{id}", $"Relleno estimado (con su trama) «{level}» frente a la pista vacía", ContrastKind.UiComponent, key + "+trama", hatched, "BackgroundBrush", track);
                t.Exempt($"stripe.{id}", $"Raya de la trama de una estimación sobre el relleno «{level}»", "Decorativa: «~» y «estimado» dicen que es una estimación.", MetricBar.HatchStripeKey, hatched, key, fill);
                t.Exempt($"mark.fill.{id}", $"Marca de un umbral sobre el relleno «{level}»", "Decorativa: el nivel va escrito junto a la barra.", MetricBar.MarkKey, Wcag.Over(mark, fill), key, fill);
            }

            t.Add("mark.track", "Marca de un umbral sobre la pista vacía", ContrastKind.UiComponent, MetricBar.MarkKey, Wcag.Over(mark, track), "BackgroundBrush", track);
            return t;
        });

        table.AssertAllPass();
    }
}
