using System.Text;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Skills;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Infrastructure.Persistence.Json;
using Microsoft.Data.Sqlite;
using static AgentStudio.Infrastructure.Persistence.SqliteValues;

namespace AgentStudio.Infrastructure.Skills;

/// <summary>Where the global activation decisions are kept: a JSONL file of the user, not of any project.</summary>
public sealed record SkillActivationStoreOptions
{
    public SkillActivationStoreOptions(string globalActivationsFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalActivationsFile);
        GlobalActivationsFile = Path.GetFullPath(globalActivationsFile);
    }

    public string GlobalActivationsFile { get; }

    /// <summary><c>%APPDATA%\AgentStudio\skill-activations.jsonl</c>.</summary>
    public static SkillActivationStoreOptions ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentStudio", "skill-activations.jsonl"));
}

/// <summary>
/// The history of activation decisions (docs/13 section 3.6), never rewritten: for one skill and scope the last
/// decision wins (<see cref="SkillActivationResolver"/>).
/// <list type="bullet">
/// <item>Project and agent decisions go to the project's <c>skill_activations</c> table (append-only by trigger). A
/// project decision names its own project; an agent decision names its agent.</item>
/// <item>Global decisions belong to the user, not to a project: one JSON object per line in
/// <see cref="SkillActivationStoreOptions.GlobalActivationsFile"/>. A line counts once it ends with a newline, which is
/// written together with it. A last fragment without one is an append that never finished: reading ignores it and the
/// next append cuts it off. A complete line that does not read is a damaged history and an
/// <see cref="InvalidDataException"/>, never silently skipped.</item>
/// </list>
/// <see cref="ListAsync"/> returns the global decisions and then the project's, each in the order they were made.
/// </summary>
public sealed class SkillActivationStore(ProjectDatabases databases, TimeProvider timeProvider, SkillActivationStoreOptions options) : ISkillActivationStore
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int Attempts = 10;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SemaphoreSlim _globalGate = new(1, 1);

    public async Task<IReadOnlyList<SkillActivation>> ListAsync(string projectId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var global = await ReadGlobalAsync(ct).ConfigureAwait(false);
        var scoped = await ReadProjectAsync(projectId, ct).ConfigureAwait(false);
        return [.. global, .. scoped];
    }

    public async Task AppendAsync(string projectId, SkillActivation activation, string decidedBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        if (string.IsNullOrWhiteSpace(activation.Skill.Value))
        {
            throw new ArgumentException("The activation names no skill.", nameof(activation));
        }

        switch (activation.Scope)
        {
            case SkillScopeKind.Global when activation.ScopeId is not null:
                throw new ArgumentException("A global activation applies to every project and names none.", nameof(activation));
            case SkillScopeKind.Global:
                await AppendGlobalAsync(activation, decidedBy, ct).ConfigureAwait(false);
                break;
            case SkillScopeKind.Project when !string.Equals(activation.ScopeId, projectId, StringComparison.Ordinal):
                throw new ArgumentException($"A project activation names its own project ('{projectId}'), not '{activation.ScopeId}'.", nameof(activation));
            case SkillScopeKind.Agent when string.IsNullOrWhiteSpace(activation.ScopeId):
                throw new ArgumentException("An agent activation names its agent.", nameof(activation));
            case SkillScopeKind.Project or SkillScopeKind.Agent:
                await InsertAsync(projectId, activation, decidedBy, ct).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(activation), activation.Scope, "Unknown activation scope.");
        }
    }

    private async Task InsertAsync(string projectId, SkillActivation activation, string decidedBy, CancellationToken ct)
    {
        await using var connection = databases.Connect(projectId);
        await using var insert = Command(connection, """
            INSERT INTO skill_activations(project_id, skill_id, scope, scope_id, enabled, decided_by, decided_at)
            VALUES ($project, $skill, $scope, $scopeId, $enabled, $decidedBy, $at);
            """);
        insert.Parameters.AddWithValue("$project", projectId);
        insert.Parameters.AddWithValue("$skill", activation.Skill.Value);
        insert.Parameters.AddWithValue("$scope", activation.Scope.ToString());
        insert.Parameters.AddWithValue("$scopeId", activation.ScopeId);
        insert.Parameters.AddWithValue("$enabled", activation.Enabled ? 1 : 0);
        insert.Parameters.AddWithValue("$decidedBy", decidedBy);
        insert.Parameters.AddWithValue("$at", Time(timeProvider.GetUtcNow()));
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SkillActivation>> ReadProjectAsync(string projectId, CancellationToken ct)
    {
        await using var connection = databases.Connect(projectId);
        await using var select = Command(connection, "SELECT id, skill_id, scope, scope_id, enabled FROM skill_activations WHERE project_id = $project ORDER BY id;");
        select.Parameters.AddWithValue("$project", projectId);

        var activations = new List<SkillActivation>();
        await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            activations.Add(ReadRow(reader));
        }

        return activations;
    }

    private static SkillActivation ReadRow(SqliteDataReader reader)
    {
        var scopeText = reader.GetString(2);
        if (!TryParseEnum<SkillScopeKind>(scopeText, out var scope) || scope == SkillScopeKind.Global)
        {
            throw new InvalidDataException($"Skill activation {reader.GetInt64(0)} has an unknown scope '{scopeText}'.");
        }

        return new SkillActivation(new SkillId(reader.GetString(1)), scope, reader.GetString(3), reader.GetInt64(4) == 1);
    }

    private async Task AppendGlobalAsync(SkillActivation activation, string decidedBy, CancellationToken ct)
    {
        var line = PersistenceJson.Serialize(new GlobalActivationLine(activation.Skill.Value, activation.Enabled, decidedBy, timeProvider.GetUtcNow())) + "\n";
        var path = options.GlobalActivationsFile;

        await _globalGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    AppendLine(path, Utf8.GetBytes(line));
                    return;
                }
                catch (IOException ex) when (IsSharingViolation(ex) && attempt < Attempts)
                {
                    // Another instance of the app is appending its own decision.
                    await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _globalGate.Release();
        }
    }

    // One writer at a time (no write sharing), and the line goes in one write together with its newline.
    private static void AppendLine(string path, byte[] line)
    {
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        var complete = CompleteLength(stream);
        if (complete < stream.Length)
        {
            // What follows the last newline was never a decision: the append that wrote it did not finish.
            stream.SetLength(complete);
        }

        stream.Seek(0, SeekOrigin.End);
        stream.Write(line);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>The length of the file up to and including its last newline.</summary>
    private static long CompleteLength(FileStream stream)
    {
        var buffer = new byte[4096];
        var end = stream.Length;
        while (end > 0)
        {
            var start = Math.Max(0, end - buffer.Length);
            stream.Seek(start, SeekOrigin.Begin);
            stream.ReadExactly(buffer, 0, (int)(end - start));
            var newline = Array.LastIndexOf(buffer, (byte)'\n', (int)(end - start - 1));
            if (newline >= 0)
            {
                return start + newline + 1;
            }

            end = start;
        }

        return 0;
    }

    private async Task<IReadOnlyList<SkillActivation>> ReadGlobalAsync(CancellationToken ct)
    {
        var path = options.GlobalActivationsFile;
        if (!File.Exists(path))
        {
            return [];
        }

        string text;
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Utf8);
            text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        var lines = text.Split('\n');
        var activations = new List<SkillActivation>();

        // The last piece follows the last newline: empty in a complete file, or an append that never finished.
        for (var i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length > 0)
            {
                activations.Add(ReadLine(path, i + 1, line));
            }
        }

        return activations;
    }

    private static SkillActivation ReadLine(string path, int number, string line)
    {
        var what = $"'{path}', line {number}";
        var decision = PersistenceJson.Deserialize<GlobalActivationLine>(line, what);
        if (string.IsNullOrWhiteSpace(decision.Skill) || string.IsNullOrWhiteSpace(decision.DecidedBy))
        {
            throw new InvalidDataException($"{what} is not a complete activation decision.");
        }

        return new SkillActivation(new SkillId(decision.Skill), SkillScopeKind.Global, null, decision.Enabled);
    }

    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    /// <summary>One line of the global file.</summary>
    private sealed record GlobalActivationLine(string Skill, bool Enabled, string DecidedBy, DateTimeOffset DecidedAt);
}
