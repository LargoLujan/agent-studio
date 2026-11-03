using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// No test may show a window or take the focus: the input source <see cref="TestKeys"/> raises its key events from is
/// a real window, so it must be one that is never visible and never becomes the foreground window.
/// </summary>
public sealed class TestKeysTests
{
    private const int GwlStyle = -16;
    private const long WsVisible = 0x10000000;

    [Fact]
    public void Key_events_come_from_a_window_that_is_never_shown_nor_activated()
    {
        var (handle, visible, style, foreground) = WpfHost.Run(() =>
        {
            var target = new Border();
            (IntPtr Handle, bool Visible, long Style, IntPtr Foreground) seen = default;
            target.PreviewKeyDown += (_, e) =>
            {
                var source = (HwndSource)e.InputSource;
                seen = (source.Handle, IsWindowVisible(source.Handle), (long)GetWindowLongPtr(source.Handle, GwlStyle), GetForegroundWindow());
            };

            TestKeys.PreviewKeyDown(target, Key.A);
            return seen;
        });

        Assert.NotEqual(IntPtr.Zero, handle);
        Assert.False(visible, "the window behind the key events was visible");
        Assert.Equal(0, style & WsVisible);
        Assert.NotEqual(handle, foreground);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
