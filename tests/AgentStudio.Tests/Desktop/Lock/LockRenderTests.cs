using System.Windows.Controls;
using System.Windows.Media;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Views.Lock;
using AgentStudio.Tests.Desktop.Orchestrator;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Lock;

/// <summary>
/// The lock screen and the notice of Windows Hello of an approval, drawn without ever being shown (<see cref="OffscreenRender"/>):
/// no window, no HWND, nothing on the user's screen. With <c>AGENTSTUDIO_RENDER_EVIDENCE</c> set, the pictures are saved.
/// </summary>
public sealed class LockRenderTests
{
    private static readonly Color Background = Color.FromRgb(0x0A, 0x10, 0x20);
    private static readonly Color Surface = Color.FromRgb(0x10, 0x1A, 0x30);
    private static readonly Color Accent = Color.FromRgb(0x2E, 0x68, 0xF9);
    private static readonly Color Warning = Color.FromRgb(0xF5, 0xB5, 0x44);

    private static readonly ApprovalRequest Delivery = new(
        "deliver-integration", "Entregar la ejecución 20261021-abc123 en main.", RiskLevel.Critical, null, [@"src\Calc.cs"], "p-1", "20261021-abc123");

    private sealed class Work : IWorkInProgress
    {
        public IReadOnlyList<ProjectActivityKind> Current() => [ProjectActivityKind.Run, ProjectActivityKind.Debate];
    }

    [Fact]
    public async Task The_lock_screen_renders_offscreen_opaque_with_its_unlock_button_and_the_warning_without_hello()
    {
        var clock = new FakeTimeProvider(T0);
        var hello = new FakeUserPresenceVerifier();
        var interfaceLock = new InterfaceLock(hello, new FakeWindowsSession(), clock);
        await interfaceLock.LockAsync(LockReason.Idle, TestContext.Current.CancellationToken);
        _ = interfaceLock.WaitUntilUnlockedAsync(Delivery, TestContext.Current.CancellationToken);

        // Windows Hello went missing while locked: the screen warns and offers «Bloquear Windows».
        hello.Availability = _ => Task.FromResult(UserPresenceAvailability.NotConfiguredForUser);
        await interfaceLock.UnlockWithHelloAsync(TestContext.Current.CancellationToken);

        var (drawn, background, accent, warning, waiting) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            using var screen = new LockScreenViewModel(interfaceLock, clock, new InlineSynchronizationContext(), new Work());
            var view = new LockScreenView { DataContext = screen };

            var bitmap = OffscreenRender.Render(view, 1200, 760);
            OffscreenRender.SaveIfRequested(bitmap, "fase8-bloqueo.png");
            return (
                OffscreenRender.CountUnlike(bitmap, Background),
                OffscreenRender.Count(bitmap, Background),
                OffscreenRender.Count(bitmap, Accent),
                OffscreenRender.Count(bitmap, Warning),
                screen.WaitingItems.Count);
        });

        Assert.Equal(1, waiting);
        Assert.True(drawn > 20_000, $"Only {drawn} pixels differ from the background: the card was not drawn.");
        Assert.True(background > 500_000, $"Only {background} pixels have the background colour: the screen is not opaque.");
        Assert.True(accent > 1_500, $"Only {accent} pixels have the accent colour: the unlock button was not drawn.");
        Assert.True(warning > 300, $"Only {warning} pixels have the warning colour: the warning without Hello was not drawn.");
    }

    [Fact]
    public void The_notice_of_windows_hello_renders_offscreen_as_a_warning_without_hello_and_not_at_all_without_a_part_for_it()
    {
        var (unavailable, asked, none) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var withoutHello = OffscreenRender.Render(
                new HelloNoticeView { DataContext = new ApprovalDialogViewModel(Delivery, ApprovalVerification.WindowsHelloUnavailable, UserPresenceAvailability.NotConfiguredForUser) },
                560,
                90);
            OffscreenRender.SaveIfRequested(withoutHello, "fase8-aprobacion-sin-hello.png");
            var withHello = OffscreenRender.Render(
                new HelloNoticeView { DataContext = new ApprovalDialogViewModel(Delivery, ApprovalVerification.WindowsHello, UserPresenceAvailability.Available) },
                560,
                90);
            OffscreenRender.SaveIfRequested(withHello, "fase8-aprobacion-con-hello.png");
            var dialogOnly = OffscreenRender.Render(new HelloNoticeView { DataContext = new ApprovalDialogViewModel(Delivery) }, 560, 90);

            return (
                OffscreenRender.Count(withoutHello, Warning),
                OffscreenRender.Count(withHello, Warning),
                OffscreenRender.CountUnlike(dialogOnly, Background));
        });

        Assert.True(unavailable > 100, $"Only {unavailable} pixels have the warning colour: the warning was not drawn.");
        Assert.Equal(0, asked);
        Assert.Equal(0, none);
    }

    [Fact]
    public void The_settings_of_windows_hello_and_the_lock_render_offscreen_as_a_section_without_a_save_of_its_own()
    {
        var (drawn, boxes, buttons) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new Border
            {
                Background = (Brush)System.Windows.Application.Current.Resources["SurfaceBrush"],
                Padding = new System.Windows.Thickness(16),
                Child = new LockSettingsView { DataContext = new LockSettingsViewModel(new InMemorySettingsStore()) { IdleLockEnabled = true } },
            };

            var bitmap = OffscreenRender.Render(view, 640, 400);
            OffscreenRender.SaveIfRequested(bitmap, "fase8-ajustes-bloqueo.png");
            return (OffscreenRender.CountUnlike(bitmap, Surface), Descendants<CheckBox>(view).Count(), Descendants<Button>(view).Count());
        });

        Assert.True(drawn > 3_000, $"Only {drawn} pixels differ from the panel: the settings were not drawn.");
        Assert.Equal(2, boxes);

        // «Guardar» is the one of «Ajustes», for every section at once (SettingsRenderTests).
        Assert.Equal(0, buttons);
    }

    private static IEnumerable<T> Descendants<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
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
