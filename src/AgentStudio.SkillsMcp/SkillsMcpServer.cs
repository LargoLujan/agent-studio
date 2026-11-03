using System.Reflection;
using System.Text;
using AgentStudio.Application.Skills;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentStudio.SkillsMcp;

/// <summary>
/// The local MCP server of the assisted skill recommendation (docs/14 D-30, ADR-0024): exactly two tools,
/// <c>search_skills</c> and <c>recommend_skills</c>, both read-only and closed-world, over the snapshot it was given. It
/// offers no resource, prompt or completion, has no write tool and opens no socket: the only transport is the stdio of the
/// process (<see cref="Program"/>), and the tests use in-memory streams.
/// </summary>
internal static class SkillsMcpServer
{
    /// <summary>The name of the server, also its name in the session (<see cref="SkillsMcpNames.Server"/>).</summary>
    public const string ServerName = SkillsMcpNames.Server;

    /// <summary>The longest task <c>recommend_skills</c> takes.</summary>
    public const int MaxTaskLength = 2000;

    private const string Instructions =
        "Catálogo de skills de Agent Studio, de solo lectura. search_skills busca por nombre, resumen y etiquetas; "
        + "recommend_skills es el recomendador determinista con sus razones. Ninguna herramienta activa nada: activar una skill lo decide la persona.";

    /// <summary>The options of a server over <paramref name="snapshot"/>: its identity, its instructions and its two tools.</summary>
    /// <exception cref="ArgumentException">A skill of the snapshot is not a valid catalog entry.</exception>
    public static McpServerOptions Options(SkillCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var tools = new SkillCatalogTools(snapshot);
        var collection = new McpServerPrimitiveCollection<McpServerTool>
        {
            McpServerTool.Create(tools.SearchSkills, ReadOnly(
                SkillsMcpNames.SearchTool,
                "Buscar skills",
                "Busca en el catálogo de skills de Agent Studio por nombre, resumen y etiquetas, con filtros opcionales de sector y etiquetas. "
                + "Devuelve solo metadatos: id, nombre, resumen, sector, etiquetas, salud local, agentes que la tienen activa, si está enlazada "
                + "(entonces no se puede dar a un agente) y su coste estimado de contexto en tokens. Solo lectura: no activa nada.")),
            McpServerTool.Create(tools.RecommendSkills, ReadOnly(
                SkillsMcpNames.RecommendTool,
                "Recomendar skills",
                "El recomendador determinista de Agent Studio: puntúa el catálogo para una tarea y unas etiquetas de contexto, con razones "
                + "legibles (etiquetas, sector, nombre y resumen que coinciden). Nunca devuelve skills con puntuación 0. Solo lectura: no activa nada.")),
        };

        return new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = ServerName,
                Title = "Catálogo de skills de Agent Studio",
                Version = typeof(SkillsMcpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0",
            },
            ServerInstructions = Instructions,
            ToolCollection = collection,
        };
    }

    private static McpServerToolCreateOptions ReadOnly(string name, string title, string description) => new()
    {
        Name = name,
        Title = title,
        Description = description,
        ReadOnly = true,
        OpenWorld = false,
        Destructive = false,
        Idempotent = true,
    };
}

/// <summary>
/// Reads the snapshot the server serves, the only file it ever opens: given by its absolute path, never through a link,
/// at most <see cref="MaxBytes"/>, and in the shared format (<see cref="SkillCatalogSnapshotJson"/>).
/// </summary>
internal static class SnapshotFile
{
    /// <summary>Far above a snapshot of the whole index (about 120 skills take some 60 KiB).</summary>
    public const long MaxBytes = 16 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <exception cref="IOException">The file does not exist, is a link or cannot be read.</exception>
    /// <exception cref="InvalidDataException">It is too large or it is not a snapshot.</exception>
    public static SkillCatalogSnapshot Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The skills snapshot does not exist.", path);
        }

        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.LinkTarget is not null)
        {
            throw new IOException($"The skills snapshot '{path}' is a link: it is not followed.");
        }

        if (file.Length > MaxBytes)
        {
            throw new InvalidDataException($"The skills snapshot is larger than {MaxBytes} bytes.");
        }

        string text;
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("The skills snapshot is not UTF-8 text.", ex);
        }

        return SkillCatalogSnapshotJson.Read(text);
    }
}
