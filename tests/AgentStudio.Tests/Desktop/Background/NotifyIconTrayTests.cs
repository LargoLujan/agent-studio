using AgentStudio.Desktop.Background;

namespace AgentStudio.Tests.Desktop.Background;

/// <summary>
/// The real tray icon (<c>NotifyIcon</c> of Windows Forms), built on the WPF test thread and never shown: it is not put in
/// the notification area (<see cref="NotifyIconTray.Show"/> is never called), its menu is built but never opened, and a
/// notification without the icon shows nothing.
/// </summary>
public sealed class NotifyIconTrayTests
{
    [Fact]
    public void The_real_icon_is_built_with_its_tooltip_and_its_menu_without_ever_showing_up()
    {
        var (visible, toolTip, size, menu, visibleAfterNotifying) = WpfHost.Run(() =>
        {
            using var tray = new NotifyIconTray();
            tray.SetToolTip(new string('x', 300));
            tray.SetMenu(() =>
            [
                new TrayMenuItem("Abrir Agent Studio", () => Task.CompletedTask),
                TrayMenuItem.Separator,
                new TrayMenuItem("Abrir proyecto", Children: [new TrayMenuItem("monorepo", () => Task.CompletedTask), TrayMenuItem.Separator, new TrayMenuItem("Abrir carpeta…", () => Task.CompletedTask)]),
                new TrayMenuItem("Bloquear", IsEnabled: false),
            ]);
            var described = tray.DescribeMenu();
            var before = tray.IsVisible;
            tray.Notify(new TrayNotification("Ejecución terminada", "Una ejecución terminó: completada."));
            return (before, tray.ToolTip, tray.ImageSize, described, tray.IsVisible);
        });

        Assert.False(visible);
        Assert.False(visibleAfterNotifying);
        Assert.Equal(TrayToolTip.MaxLength, toolTip.Length);
        Assert.EndsWith("…", toolTip, StringComparison.Ordinal);
        Assert.InRange(size, 16, 64);
        Assert.Equal(["Abrir Agent Studio", "", "Abrir proyecto", "  monorepo", "  ", "  Abrir carpeta…", "Bloquear (desactivado)"], menu);
    }

    [Fact]
    public void A_tooltip_that_fits_is_kept_as_it_is_and_the_menu_is_built_again_each_time()
    {
        var (toolTip, first, second) = WpfHost.Run(() =>
        {
            using var tray = new NotifyIconTray();
            tray.SetToolTip("Agent Studio · 2 ejecuciones · 1 espera cuota hasta las 14:05");
            var count = 0;
            tray.SetMenu(() => [new TrayMenuItem($"Entrada {++count}", () => Task.CompletedTask)]);
            return (tray.ToolTip, tray.DescribeMenu(), tray.DescribeMenu());
        });

        Assert.Equal("Agent Studio · 2 ejecuciones · 1 espera cuota hasta las 14:05", toolTip);
        Assert.Equal(["Entrada 1"], first);
        Assert.Equal(["Entrada 2"], second);
    }

    [Fact]
    public void A_tooltip_set_before_the_icon_is_shown_waits_so_the_icon_is_added_with_its_name_alone()
    {
        var (toolTip, addedWith, visible) = WpfHost.Run(() =>
        {
            using var tray = new NotifyIconTray();
            tray.SetToolTip("Agent Studio · sin trabajo en marcha");

            // What Windows Forms would add the icon with (it is never added here: Show is not called).
            var icon = AgentStudio.Tests.Desktop.Phase8.Wiring.Field(tray, "_icon")!;
            return (tray.ToolTip, (string?)AgentStudio.Tests.Desktop.Phase8.Wiring.Property(icon, "Text"), tray.IsVisible);
        });

        Assert.Equal("Agent Studio · sin trabajo en marcha", toolTip);
        Assert.Equal(NotifyIconTray.Name, addedWith);
        Assert.Equal("Agent Studio", NotifyIconTray.Name);
        Assert.False(visible);
    }

    [Fact]
    public void Disposing_twice_is_harmless_and_a_disposed_icon_cannot_be_shown()
    {
        var refused = WpfHost.Run(() =>
        {
            var tray = new NotifyIconTray();
            tray.Dispose();
            tray.Dispose();
            tray.SetToolTip("Agent Studio");
            try
            {
                tray.Show();
                return false;
            }
            catch (ObjectDisposedException)
            {
                return !tray.IsVisible;
            }
        });

        Assert.True(refused);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(32)]
    public void The_image_is_the_accent_square_with_a_white_letter_and_transparent_corners(int size)
    {
        using var bitmap = TrayIconImage.Draw(size);
        using var icon = TrayIconImage.Create(size);

        Assert.Equal((size, size), (icon.Width, icon.Height));
        var accent = 0;
        var light = 0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A > 200 && Math.Abs(pixel.R - 0x2E) < 12 && Math.Abs(pixel.G - 0x68) < 12 && Math.Abs(pixel.B - 0xF9) < 12)
                {
                    accent++;
                }
                else if (pixel.A > 200 && pixel.R > 200 && pixel.G > 200 && pixel.B > 200)
                {
                    light++;
                }
            }
        }

        // The letter is smoothed against the square, so at 16 pixels only its core is near white.
        Assert.True(accent > size * size / 3, $"Only {accent} pixels of {size * size} have the accent colour.");
        Assert.True(light >= size / 2, $"Only {light} pixels are near white: the letter was not drawn.");
        Assert.Equal(0, bitmap.GetPixel(0, 0).A);
        Assert.Equal(0, bitmap.GetPixel(size - 1, size - 1).A);
    }

    [Fact]
    public void An_image_out_of_the_sizes_of_an_icon_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrayIconImage.Create(8));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrayIconImage.Create(512));
    }
}
