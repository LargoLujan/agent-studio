using System.IO;

namespace AgentStudio.Desktop.Controls.Editor;

/// <summary>Locates the editor page and Monaco on disk and describes how they are served to WebView2.</summary>
public sealed class EditorAssets(string assetsFolder)
{
    /// <summary>Virtual host name mapped to the Assets folder.</summary>
    public const string HostName = "agentstudio.editor";

    public const string MissingMonacoMessage =
        "Editor no disponible: faltan los recursos de Monaco (ejecuta tools/assets/fetch-monaco.ps1)";

    public static EditorAssets Default { get; } = new(Path.Combine(AppContext.BaseDirectory, "Assets"));

    public string Folder { get; } = assetsFolder;

    public string PageUrl => $"https://{HostName}/editor/editor.html";

    public bool IsPageAvailable => File.Exists(Path.Combine(Folder, "editor", "editor.html"));

    public bool IsMonacoAvailable => File.Exists(Path.Combine(Folder, "monaco", "vs", "loader.js"));

    /// <summary>The reason the editor cannot start, or null when every resource is present.</summary>
    public string? GetUnavailableReason()
    {
        if (!IsMonacoAvailable)
        {
            return MissingMonacoMessage;
        }

        return IsPageAvailable ? null : "Editor no disponible: falta Assets/editor/editor.html";
    }

    /// <summary>True when a navigation target stays on the virtual host (everything else is blocked).</summary>
    public static bool IsAllowedNavigation(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps
        && string.Equals(parsed.Host, HostName, StringComparison.OrdinalIgnoreCase);
}
