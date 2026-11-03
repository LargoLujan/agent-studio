using System.IO;

namespace AgentStudio.Desktop.Controls.Editor;

/// <summary>Maps a file extension to a Monaco language id.</summary>
public static class LanguageMap
{
    public const string PlainText = "plaintext";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".py"] = "python",
        [".sql"] = "sql",
        [".json"] = "json",
        [".yaml"] = "yaml",
        [".yml"] = "yaml",
        [".md"] = "markdown",
        [".xaml"] = "xml",
        [".xml"] = "xml",
        [".csproj"] = "xml",
        [".props"] = "xml",
        [".ps1"] = "powershell",
        [".ts"] = "typescript",
        [".js"] = "javascript",
        [".html"] = "html",
        [".css"] = "css",
    };

    /// <summary>Language id for a file path or bare file name; <see cref="PlainText"/> when unknown.</summary>
    public static string FromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return PlainText;
        }

        return FromExtension(Path.GetExtension(path));
    }

    /// <summary>Language id for an extension with or without the leading dot.</summary>
    public static string FromExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return PlainText;
        }

        var key = extension.StartsWith('.') ? extension : "." + extension;
        return ByExtension.TryGetValue(key, out var language) ? language : PlainText;
    }
}
