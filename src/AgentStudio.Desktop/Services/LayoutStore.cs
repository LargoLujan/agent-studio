using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AgentStudio.Desktop.Services;

/// <summary>Stores the docking layout (AvalonDock XML) of each project.</summary>
public interface ILayoutStore
{
    /// <summary>Layout file of a project: layouts\&lt;sha256 of the normalized path&gt;.xml.</summary>
    string GetLayoutPath(string projectPath);

    /// <summary>The saved layout, or null when there is none or the file is not well-formed XML.</summary>
    string? Load(string projectPath);

    void Save(string projectPath, string layoutXml);
}

public sealed class FileLayoutStore(AppDataPaths paths) : ILayoutStore
{
    public string GetLayoutPath(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var normalized = AppSettings.NormalizePath(projectPath).ToUpperInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(paths.LayoutsDirectory, hash + ".xml");
    }

    public string? Load(string projectPath)
    {
        var file = GetLayoutPath(projectPath);
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var xml = File.ReadAllText(file);
            _ = XDocument.Parse(xml);
            return xml;
        }
        catch (XmlException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string projectPath, string layoutXml)
    {
        ArgumentNullException.ThrowIfNull(layoutXml);
        AtomicFile.WriteAllText(GetLayoutPath(projectPath), layoutXml);
    }
}
