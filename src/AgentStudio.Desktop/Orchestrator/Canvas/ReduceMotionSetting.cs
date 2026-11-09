using AgentStudio.Desktop.Services;

namespace AgentStudio.Desktop.Orchestrator.Canvas;

/// <summary>
/// «Reducir movimiento» as the canvases read it (docs/17 D-15): the saved setting, read once and again each time it is saved
/// (<see cref="MotionPolicy.SettingChanged"/>), because an animation asks for it every time it would start and the settings file
/// must not be read for each one. A store that cannot be read means the animations are not reduced.
/// </summary>
public sealed class ReduceMotionSetting : IDisposable
{
    private readonly IAppSettingsStore _store;
    private readonly System.Threading.Lock _gate = new();
    private bool? _value;

    public ReduceMotionSetting(IAppSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        MotionPolicy.SettingChanged += OnChanged;
    }

    /// <summary>The setting now: what the document passes as <c>reduceMotion</c>.</summary>
    public bool Current
    {
        get
        {
            lock (_gate)
            {
                _value ??= Read();
                return _value.Value;
            }
        }
    }

    private bool Read()
    {
        try
        {
            return _store.Load().ReduceMotion;
        }
#pragma warning disable CA1031 // A settings file that cannot be read must not stop an animation from deciding: they run.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _value = null;
        }
    }

    public void Dispose() => MotionPolicy.SettingChanged -= OnChanged;
}
