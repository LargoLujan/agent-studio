using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace NodifySpike.Harness;

/// <summary>
/// Mouse and keyboard input delivered as window messages (<c>SendMessage</c>) to the harness window. WPF handles them
/// exactly like device input (HwndMouseInputProvider, MouseDevice, routed events, Nodify's own gesture state machines), but
/// nothing touches the user's session: the cursor is not moved, the foreground window does not change and no keystroke can
/// reach another application, because the messages are addressed to one window handle of this process.
/// <para>
/// Three details make WPF accept them (read from the WPF sources):
/// </para>
/// <list type="number">
///   <item>WPF ignores a mouse message as "spurious" unless the window holds the Win32 mouse capture or the real cursor is
///   over it, so <c>SetCapture</c> is called before every message. A background window only receives the mouse events that
///   happen over itself, so the user's own clicks are not redirected here.</item>
///   <item><c>Mouse.LeftButton</c>, <c>Keyboard.Modifiers</c> and <c>KeyEventArgs.IsDown</c> read <c>GetKeyState</c>, which
///   is the calling thread's key-state table. The harness writes the simulated buttons and modifiers into that table
///   (<c>SetKeyboardState</c>) instead of pressing real keys.</item>
///   <item>The window must not be <c>WS_EX_NOACTIVATE</c>: WPF then takes Win32 focus inside this process, which it needs to
///   route keys to <c>Keyboard.FocusedElement</c>. Windows may turn that first <c>SetFocus</c> into a foreground change (see
///   <c>HarnessRunner.EnsureKeyboardFocusAsync</c>).</item>
/// </list>
/// Coordinates are physical pixels of the window's client area.
/// </summary>
internal sealed class WindowInput
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftDown = 0x0201;
    private const uint WmLeftUp = 0x0202;
    private const uint WmRightDown = 0x0204;
    private const uint WmRightUp = 0x0205;
    private const uint WmMouseWheel = 0x020A;

    private const int MkLeft = 0x0001;
    private const int MkRight = 0x0002;
    private const int MkShift = 0x0004;
    private const int MkControl = 0x0008;

    private const int VkLeftButton = 0x01;
    private const int VkRightButton = 0x02;

    private readonly nint _window;
    private readonly byte[] _keyState = new byte[256];
    private readonly HashSet<int> _heldKeys = [];
    private bool _leftDown;
    private bool _rightDown;

    public WindowInput(Window window)
    {
        _window = new WindowInteropHelper(window).Handle;
        if (_window == 0)
        {
            throw new InvalidOperationException("The window has no handle yet.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint window);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] keyState);

    [DllImport("user32.dll")]
    private static extern bool SetKeyboardState(byte[] keyState);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(nint window);

    /// <summary>The window with the keyboard focus <i>of the calling thread's input queue</i> (not necessarily the foreground window).</summary>
    [DllImport("user32.dll")]
    public static extern nint GetFocus();

    /// <summary>The window holding the mouse capture of the calling thread's input queue.</summary>
    [DllImport("user32.dll")]
    public static extern nint GetCapture();

    [DllImport("user32.dll")]
    public static extern nint GetActiveWindow();

    /// <summary>Two presses of the same button at the same spot within this time are a double click to WPF (milliseconds).</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDoubleClickTime();

    /// <summary>A window handle for the log. Only the number: neither titles nor class names, which would say what the user is working on.</summary>
    public static string Describe(nint window) => window == 0 ? "none" : $"0x{window:X}";

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint window, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public bool LeftDown => _leftDown;

    /// <summary>Gives the mouse stream back (the window is then inert); called when the run ends.</summary>
    public static void ReleaseMouse() => ReleaseCapture();

    public void Move(int x, int y)
    {
        Prepare();
        SendMessage(_window, WmMouseMove, MouseKeys(), Pack(x, y));
    }

    // WPF drops the mouse device when an element releases the capture (WM_CAPTURECHANGED while the real cursor is elsewhere),
    // and a press that arrives first has no element under the pointer yet: it is lost. Every button message is therefore
    // preceded by a move to the same spot, in the same synchronous burst, which re-activates the device and hit-tests.
    public void ButtonDown(MouseButton button, int x, int y)
    {
        SetButton(button, true);
        Prepare();
        SendMessage(_window, WmMouseMove, MouseKeys(), Pack(x, y));
        SendMessage(_window, button == MouseButton.Left ? WmLeftDown : WmRightDown, MouseKeys(), Pack(x, y));
    }

    public void ButtonUp(MouseButton button, int x, int y)
    {
        Prepare();
        SendMessage(_window, WmMouseMove, MouseKeys(), Pack(x, y));
        SetButton(button, false);
        ApplyKeyState();
        SendMessage(_window, button == MouseButton.Left ? WmLeftUp : WmRightUp, MouseKeys(), Pack(x, y));
    }

    /// <summary>Press and release at one spot in a single synchronous burst, so nothing can interleave between the messages.</summary>
    public void Click(MouseButton button, int x, int y)
    {
        ButtonDown(button, x, y);
        ButtonUp(button, x, y);
    }

    /// <summary>One wheel notch is 120; positive scrolls up (zooms in on the editor).</summary>
    public void Wheel(int delta, int x, int y)
    {
        Prepare();
        SendMessage(_window, WmMouseMove, MouseKeys(), Pack(x, y));
        var point = new NativePoint { X = x, Y = y };
        ClientToScreen(_window, ref point); // WM_MOUSEWHEEL carries screen coordinates; WPF converts them back
        SendMessage(_window, WmMouseWheel, (nint)((delta << 16) | MouseKeys()), Pack(point.X, point.Y));
    }

    /// <summary>Holds a modifier (or any key) down in this thread's key-state table until <see cref="Release"/>.</summary>
    public void Hold(Key key)
    {
        _heldKeys.Add(KeyInterop.VirtualKeyFromKey(key));
        ApplyKeyState();
    }

    public void Release(Key key)
    {
        _heldKeys.Remove(KeyInterop.VirtualKeyFromKey(key));
        ApplyKeyState();
    }

    public void KeyDown(Key key)
    {
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        _heldKeys.Add(virtualKey);
        ApplyKeyState();
        SendMessage(_window, WmKeyDown, virtualKey, KeyData(virtualKey, up: false));
    }

    public void KeyUp(Key key)
    {
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        _heldKeys.Remove(virtualKey);
        ApplyKeyState();
        SendMessage(_window, WmKeyUp, virtualKey, KeyData(virtualKey, up: true));
    }

    public void Press(Key key)
    {
        KeyDown(key);
        KeyUp(key);
    }

    private void Prepare()
    {
        SetCapture(_window);
        ApplyKeyState();
    }

    private void SetButton(MouseButton button, bool down)
    {
        if (button == MouseButton.Left)
        {
            _leftDown = down;
        }
        else
        {
            _rightDown = down;
        }
    }

    private int MouseKeys() =>
        (_leftDown ? MkLeft : 0) | (_rightDown ? MkRight : 0)
        | (Held(Key.LeftShift) || Held(Key.RightShift) ? MkShift : 0)
        | (Held(Key.LeftCtrl) || Held(Key.RightCtrl) ? MkControl : 0);

    private bool Held(Key key) => _heldKeys.Contains(KeyInterop.VirtualKeyFromKey(key));

    // GetKeyState (behind Mouse.LeftButton, Keyboard.Modifiers and Keyboard.IsKeyDown) reads this thread's table, so that is
    // where the simulated buttons, modifiers and keys live. The table is rebuilt from scratch on every call: a key left "down" in
    // it would not only look pressed, it would also stop Nodify's mouse gestures from matching (MouseGesture.Matches requires
    // that no other key is down), which is how a stuck Delete once made every later click and drag do nothing.
    private void ApplyKeyState()
    {
        GetKeyboardState(_keyState);
        for (var i = 0; i < _keyState.Length; i++)
        {
            _keyState[i] &= 0x01; // keep the toggle bit (Caps Lock...), drop every "down" bit
        }

        if (_leftDown)
        {
            _keyState[VkLeftButton] |= 0x80;
        }

        if (_rightDown)
        {
            _keyState[VkRightButton] |= 0x80;
        }

        foreach (var virtualKey in _heldKeys)
        {
            _keyState[virtualKey] |= 0x80;
            switch (virtualKey)
            {
                // The generic Shift/Ctrl/Alt entries mirror their left and right keys.
                case 0xA0 or 0xA1:
                    _keyState[0x10] |= 0x80;
                    break;
                case 0xA2 or 0xA3:
                    _keyState[0x11] |= 0x80;
                    break;
                case 0xA4 or 0xA5:
                    _keyState[0x12] |= 0x80;
                    break;
            }
        }

        SetKeyboardState(_keyState);
    }

    private static nint Pack(int x, int y) => (nint)((x & 0xFFFF) | ((y & 0xFFFF) << 16));

    private static nint KeyData(int virtualKey, bool up)
    {
        var scan = (int)MapVirtualKey((uint)virtualKey, 0);
        var extended = virtualKey is 0x25 or 0x26 or 0x27 or 0x28 or 0x2E or 0x24 or 0x23 or 0x2D or 0x21 or 0x22;
        var data = 1 | (scan << 16) | (extended ? 1 << 24 : 0);
        if (up)
        {
            data |= (1 << 30) | unchecked((int)(1u << 31));
        }

        return data;
    }
}
