using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Settings;
using AgentStudio.Desktop.Views.Settings;
using AgentStudio.Tests.Desktop.Orchestrator;

namespace AgentStudio.Tests.Desktop.Settings;

/// <summary>
/// «Ajustes» drawn without ever being shown (<see cref="OffscreenRender"/>): no window, no HWND, nothing on the user's screen;
/// synthetic settings only. Every long label wraps inside a narrow tab instead of being cut, and a save with a wrong section
/// shows why next to its fields and in the bar of «Guardar». With <c>AGENTSTUDIO_RENDER_EVIDENCE</c> set, the pictures are saved.
/// </summary>
public sealed class SettingsRenderTests
{
    private static readonly Color Background = Color.FromRgb(0x0A, 0x10, 0x20);
    private static readonly Color Error = Color.FromRgb(0xFF, 0x5C, 0x6C);

    private static SettingsDocumentViewModel Document(InMemorySettingsStore store) =>
        new(store, new ObservabilitySettingsViewModel(store), new UnattendedSettingsViewModel(store, new LockSettingsViewModel(store)), memory: new MemorySettingsViewModel(store));

    [Fact]
    public void In_a_narrow_tab_every_long_label_wraps_inside_it_instead_of_being_cut()
    {
        const double Width = 380;
        var (drawn, labels, outside, wrapped) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new SettingsView { DataContext = Document(new InMemorySettingsStore(AppSettings.Default with { IdleLockEnabled = true })) };

            var bitmap = OffscreenRender.Render(view, Width, 2200);
            OffscreenRender.SaveIfRequested(bitmap, "ajustes-estrecho.png");

            // Check boxes and radio buttons carry their label as a wrapping TextBlock.
            // The ⓘ of a section (InfoButton) is a toggle with no label of its own: its card is checked by its own tests.
            var texts = Descendants<ToggleButton>(view).Where(t => t.Name != "Toggle").Select(t => t.Content).OfType<TextBlock>().ToList();
            return (
                OffscreenRender.CountUnlike(bitmap, Background),
                texts.Select(t => t.Text).ToList(),
                texts.Count(t => t.TranslatePoint(new Point(t.ActualWidth, 0), view).X > Width + 0.5),
                texts.Where(t => t.ActualHeight > t.FontSize * 2).Select(t => t.Text).ToList());
        });

        Assert.True(drawn > 40_000, $"Only {drawn} pixels differ from the background: the document was not drawn.");
        Assert.Equal(
            [
                "Leer la cuota de Codex",
                "Preguntar si hay trabajo en marcha (sin trabajo, sale)",
                "Seguir siempre en la bandeja",
                "Salir de Agent Studio",
                "Impedir la suspensión mientras hay trabajo",
                HelloTexts.SettingLabel,
                "Bloquear la interfaz tras un tiempo sin actividad",
                "Búsqueda en el historial para el orquestador",
                "Reducir movimiento",
            ],
            labels);
        Assert.Equal(0, outside);
        Assert.Contains("Preguntar si hay trabajo en marcha (sin trabajo, sale)", wrapped);
        Assert.Contains(HelloTexts.SettingLabel, wrapped);
    }

    [Fact]
    public void A_save_with_wrong_sections_draws_why_next_to_their_fields_and_in_the_bar_of_guardar()
    {
        var (error, document) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var document = Document(new InMemorySettingsStore());
            document.Context.CompressText = "60";
            document.Lock!.IdleLockMinutesText = "0";
            document.SaveCommand.Execute(null);
            var view = new SettingsView { DataContext = document };

            var bitmap = OffscreenRender.Render(view, 900, 1300);
            OffscreenRender.SaveIfRequested(bitmap, "ajustes-validacion.png");
            return (OffscreenRender.Count(bitmap, Error), document);
        });

        Assert.Equal("No se guardó nada. Corrige «Contexto y cuota» y «Windows Hello y bloqueo».", document.ErrorMessage);
        Assert.NotNull(document.Context.ErrorMessage);
        Assert.NotNull(document.Lock!.ErrorMessage);
        Assert.True(error > 300, $"Only {error} pixels have the error colour: the reasons were not drawn.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
