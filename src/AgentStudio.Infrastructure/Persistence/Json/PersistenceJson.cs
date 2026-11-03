using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Domain.Common;

namespace AgentStudio.Infrastructure.Persistence.Json;

/// <summary>
/// The JSON written into the <c>*_json</c> columns. Each stored shape is a record of this folder, mapped by hand to
/// and from the domain (ADR-0010: no Dapper, no reflection over domain types), so a change to a domain type never
/// changes what is already on disk without someone noticing. Compact camelCase, enums by name, null members left out,
/// text not escaped beyond what JSON requires (the database is not HTML).
/// </summary>
internal static class PersistenceJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Reads a stored document; anything that is not the expected shape is an <see cref="InvalidDataException"/> naming <paramref name="what"/>.</summary>
    public static T Deserialize<T>(string json, string what)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException($"{what} is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{what} is not valid JSON of the expected shape: {ex.Message}", ex);
        }
    }

    /// <summary>Maps a stored document to the domain; a domain rule it breaks means it was not written by this store.</summary>
    public static T Rebuild<T>(Func<T> build, string what)
    {
        try
        {
            return build();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DomainRuleViolationException)
        {
            throw new InvalidDataException($"{what} cannot be rebuilt: {ex.Message}", ex);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
