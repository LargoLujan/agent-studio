using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Desktop.Services;

public interface IAppSettingsStore
{
    /// <summary>Loads the settings; a missing or corrupt file yields <see cref="AppSettings.Default"/>.</summary>
    AppSettings Load();

    /// <summary>
    /// Persists the settings atomically: the projects, the recents and the skills index. The preferences of the panels
    /// (<see cref="SavePreferences"/>) and the placement of the window (<see cref="SaveWindowPlacement"/>) stay as they are
    /// stored, so a copy loaded before they changed does not undo them.
    /// </summary>
    void Save(AppSettings settings);

    /// <summary>
    /// Persists where the main window is (<see cref="AppSettings.MainWindowPlacement"/>), on the real exit, and keeps everything
    /// else as it is stored. A store that keeps a single copy saves it whole.
    /// </summary>
    void SaveWindowPlacement(WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        Save(Load() with { MainWindowPlacement = placement });
    }

    /// <summary>
    /// Persists the preferences of the panels (the context thresholds and the Codex quota opt-in, docs/14 D-11 and D-15, and
    /// the settings of Phase 8, docs/16 D-13, D-14 and D-40) and keeps everything else as it is stored. A store that keeps a
    /// single copy saves it whole.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The thresholds break the rule of <see cref="AppSettings.ContextThresholds"/>, or the time of the idle lock is out of
    /// range (<see cref="AppSettings.IsValidIdleLockMinutes"/>); nothing is saved.
    /// </exception>
    void SavePreferences(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.ValidatePreferences();
        Save(settings);
    }
}

/// <summary>
/// The settings as JSON in <see cref="AppDataPaths.SettingsFile"/>. A skills index that is not set falls back to
/// <c>defaultSkillsIndexPath</c> when that folder exists; otherwise it stays unset. The file has three writers, each with
/// its own fields: <see cref="Save"/> (the shell: projects, recents, skills index), <see cref="SavePreferences"/> (the
/// panels: context thresholds, the Codex opt-in and the settings of Phase 8) and <see cref="SaveWindowPlacement"/> (the main
/// window, on the real exit). Each reads the file and keeps the others' fields as they are there. A preference that makes no
/// sense in a hand-edited file is read as its default, and only that one: thresholds that break the rule (70, 85 and 95), a
/// closing action that does not exist (<see cref="CloseWindowAction.Ask"/>), an idle lock time out of range
/// (<see cref="AppSettings.DefaultIdleLockMinutes"/>) and a placement of the window that is not one (none: the window opens
/// centred).
/// </summary>
/// <param name="paths">Where the file lives.</param>
/// <param name="defaultSkillsIndexPath">The folder used for an unset skills index if it exists; null for no default.</param>
public sealed class JsonAppSettingsStore(AppDataPaths paths, string? defaultSkillsIndexPath) : IAppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new TolerantEnumConverter<CloseWindowAction>(), new TolerantPlacementConverter() },
    };

    /// <summary>The store of the app: an unset skills index falls back to <see cref="AppSettings.DefaultSkillsIndexPath"/>.</summary>
    public JsonAppSettingsStore(AppDataPaths paths)
        : this(paths, AppSettings.DefaultSkillsIndexPath)
    {
    }

    public AppSettings Load() => Sanitize(Read());

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var stored = Read();
        Write(settings.WithPreferencesOf(stored) with { MainWindowPlacement = stored.MainWindowPlacement });
    }

    public void SavePreferences(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.ValidatePreferences();
        Write(Read().WithPreferencesOf(settings));
    }

    public void SaveWindowPlacement(WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        Write(Read() with { MainWindowPlacement = placement });
    }

    private void Write(AppSettings settings) => AtomicFile.WriteAllText(paths.SettingsFile, JsonSerializer.Serialize(settings, Options));

    private AppSettings Read()
    {
        try
        {
            return File.Exists(paths.SettingsFile)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.SettingsFile), Options) ?? AppSettings.Default
                : AppSettings.Default;
        }
        catch (JsonException)
        {
            return AppSettings.Default;
        }
        catch (IOException)
        {
            return AppSettings.Default;
        }
        catch (UnauthorizedAccessException)
        {
            return AppSettings.Default;
        }
    }

    // A hand-edited file may hold nulls, blanks or more entries than allowed.
    private AppSettings Sanitize(AppSettings settings) =>
        settings with
        {
            LastProjectPath = string.IsNullOrWhiteSpace(settings.LastProjectPath) ? null : settings.LastProjectPath,
            RecentProjects = (settings.RecentProjects ?? [])
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Take(AppSettings.MaxRecentProjects)
                .ToList(),
            SkillsIndexPath = string.IsNullOrWhiteSpace(settings.SkillsIndexPath) ? DefaultSkillsIndex() : settings.SkillsIndexPath.Trim(),
            ContextWarningPercent = ValidThresholds(settings) ? settings.ContextWarningPercent : AppSettings.DefaultContextWarningPercent,
            ContextCompressPercent = ValidThresholds(settings) ? settings.ContextCompressPercent : AppSettings.DefaultContextCompressPercent,
            ContextCriticalPercent = ValidThresholds(settings) ? settings.ContextCriticalPercent : AppSettings.DefaultContextCriticalPercent,
            CloseWindow = Enum.IsDefined(settings.CloseWindow) ? settings.CloseWindow : AppSettings.Default.CloseWindow,
            IdleLockMinutes = AppSettings.IsValidIdleLockMinutes(settings.IdleLockMinutes) ? settings.IdleLockMinutes : AppSettings.DefaultIdleLockMinutes,
            MainWindowPlacement = settings.MainWindowPlacement is { IsValid: true } placement ? placement : null,
        };

    private static bool ValidThresholds(AppSettings settings) =>
        AppSettings.AreValidContextThresholds(settings.ContextWarningPercent, settings.ContextCompressPercent, settings.ContextCriticalPercent);

    private string? DefaultSkillsIndex() =>
        !string.IsNullOrWhiteSpace(defaultSkillsIndexPath) && Directory.Exists(defaultSkillsIndexPath) ? defaultSkillsIndexPath : null;

    /// <summary>
    /// An enum of the settings, written by its name. A name in any case, or a number the enum defines, reads as that value;
    /// anything else (a name it does not have, another number, null, an object) reads as an undefined value, which
    /// <see cref="Sanitize"/> replaces with the default. So one value of a hand-edited file that makes no sense does not reset
    /// the rest of the file, as a <see cref="JsonException"/> would.
    /// </summary>
    private sealed class TolerantEnumConverter<T> : JsonConverter<T>
        where T : struct, Enum
    {
        private static readonly T Undefined = (T)Enum.ToObject(typeof(T), -1);

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    var text = reader.GetString();
                    var name = Enum.GetNames<T>().FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
                    return name is null ? Undefined : Enum.Parse<T>(name);
                case JsonTokenType.Number:
                    return reader.TryGetInt32(out var number) && Enum.IsDefined(typeof(T), number) ? (T)Enum.ToObject(typeof(T), number) : Undefined;
                default:
                    reader.Skip();
                    return Undefined;
            }
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    /// <summary>
    /// The placement of the window, as an object of numbers (<c>left</c>, <c>top</c>, <c>width</c>, <c>height</c>) and a flag
    /// (<c>maximized</c>). Anything else in a hand-edited file (a string, a missing number, a number too big) reads as no
    /// placement, so the window opens centred and the rest of the file stays, as a <see cref="JsonException"/> would not let it.
    /// </summary>
    private sealed class TolerantPlacementConverter : JsonConverter<WindowPlacement>
    {
        public override WindowPlacement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return Number(root, "left") is { } left && Number(root, "top") is { } top
                && Number(root, "width") is { } width && Number(root, "height") is { } height
                ? new WindowPlacement(left, top, width, height, Property(root, "maximized") is { ValueKind: JsonValueKind.True })
                : null;
        }

        public override void Write(Utf8JsonWriter writer, WindowPlacement value, JsonSerializerOptions options)
        {
            if (!value.IsValid)
            {
                // JSON has no NaN nor infinity: a placement that is not one is written as none.
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartObject();
            writer.WriteNumber("left", value.Left);
            writer.WriteNumber("top", value.Top);
            writer.WriteNumber("width", value.Width);
            writer.WriteNumber("height", value.Height);
            writer.WriteBoolean("maximized", value.Maximized);
            writer.WriteEndObject();
        }

        private static double? Number(JsonElement element, string name) =>
            Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var number) && double.IsFinite(number)
                ? number
                : null;

        private static JsonElement? Property(JsonElement element, string name)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }

            return null;
        }
    }
}
