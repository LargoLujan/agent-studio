using System.Globalization;
using System.IO;
using AgentStudio.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// P45 and ADR-0010 (addendum of 2026-11-12): a connection without <c>recursive_triggers</c> (another program's: the app turns
/// them on) used <c>INSERT OR REPLACE</c> with the rowid of an existing row to change it with the triggers intact. Migration 0007
/// makes every <c>*_no_replace</c> trigger reject an insert whose rowid is taken. The connections here are raw ones, as another
/// program would open them: recursive triggers stay off.
/// </summary>
public sealed class ReplaceByRowidTests
{
    private const string Now = "2026-10-15T10:00:00.0000000+00:00";

    private static readonly string[] UpTo0006 =
    [
        "0001_initial.sql", "0002_orchestration.sql", "0003_evidence_subject_commit.sql", "0004_memory.sql", "0005_debates.sql", "0006_milestones.sql",
    ];

    /// <summary>The tables whose key is not the rowid, the column that is their key and an expression for a key no row has.</summary>
    public static TheoryData<string, string, string, string> Protected => new()
    {
        { "decision_log", "sequence", "99", "summary" },
        { "evidence", "id", "'x-' || id", "title" },
        { "verdicts", "id", "'x-' || id", "summary" },
        { "requirement_sets", "content_hash", "'x-' || content_hash", "payload_json" },
        { "plans", "content_hash", "'x-' || content_hash", "created_at" },
        { "orchestration_runs", "id", "'x-' || id", "base_ref" },
        { "memory_entries", "id", "'x-' || id", "content" },
        { "debate_sessions", "id", "'x-' || id", "header_hash" },
        { "project_audits", "id", "'x-' || id", "header_hash" },
        { "milestones", "id", "'x-' || id", "name" },
        { "backlog_items", "version", "99", "statement" },
    };

    /// <summary>A raw connection (recursive triggers off) on an in-memory database with the first six migrations applied by hand.</summary>
    private static SqliteConnection At0006()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        connection.Open();
        foreach (var migration in UpTo0006)
        {
            Exec(connection, Embedded(migration));
        }

        Exec(connection, "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)");
        for (var version = 1; version <= 6; version++)
        {
            Exec(connection, $"INSERT INTO schema_migrations(version, applied_at) VALUES ({version}, '{Now}')");
        }

        return connection;
    }

    private static SqliteConnection Current()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        connection.Open();
        SqliteSchema.Apply(connection);
        return connection;
    }

    private static string Embedded(string migration)
    {
        var assembly = typeof(SqliteSchema).Assembly;
        using var stream = assembly.GetManifestResourceStream($"AgentStudio.Schema.{migration}")
            ?? throw new InvalidOperationException($"No embedded migration {migration}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static long Scalar(SqliteConnection connection, string sql) => Convert.ToInt64(Text(connection, sql), CultureInfo.InvariantCulture);

    /// <summary>Rows keyed <paramref name="n"/>, with automatic rowids. A foreign program decides its own foreign keys: off here, so the attack reaches the triggers.</summary>
    private static void Seed(SqliteConnection connection, int n)
    {
        if (n == 1)
        {
            Exec(connection, $"INSERT INTO projects(id, name, root_path, created_at) VALUES ('p1', 'Proyecto', 'C:/x', '{Now}')");
        }

        Exec(connection, $"INSERT INTO decision_log(project_id, sequence, recorded_at, actor_kind, actor_id, kind, summary, payload_json, previous_hash, hash) VALUES ('p1', {n}, '{Now}', 'Human', 'u', 'k', 's{n}', '{{}}', '', 'h{n}')");
        Exec(connection, $"INSERT INTO evidence(id, project_id, kind, origin, title, produced_by, captured_at, content_sha256) VALUES ('e{n}', 'p1', 'k', 'o', 't{n}', 'a', '{Now}', 'abc')");
        Exec(connection, $"INSERT INTO requirement_sets(content_hash, project_id, frozen_at, payload_json) VALUES ('rh{n}', 'p1', '{Now}', '{{}}')");
        Exec(connection, $"INSERT INTO debates(id, project_id, mode, question, phase, outcome, requirement_set_hash, limits_json, judge_agent_id, started_at) VALUES ('d{n}', 'p1', 'm', 'q', 'Open', 'None', 'rh{n}', '{{}}', 'j', '{Now}')");
        Exec(connection, $"INSERT INTO debate_contributions(debate_id, round, author_agent_id, kind, content, at) VALUES ('d{n}', 1, 'a', 'k', 'c', '{Now}')");
        Exec(connection, $"INSERT INTO verdicts(id, project_id, debate_id, decision, summary, confidence_level, confidence_justification, requires_human, judge_agent_id, judge_provider_id, judge_vendor_id, requirement_set_hash, decision_log_head_hash, payload_json, issued_at) VALUES ('v{n}', 'p1', 'd{n}', 'd', 's{n}', 'High', 'j', 0, 'j', 'p', 'v', 'rh{n}', 'h', '{{}}', '{Now}')");
        Exec(connection, $"INSERT INTO plans(content_hash, project_id, source, author_agent_id, schema_version, created_at, payload_json) VALUES ('ph{n}', 'p1', 'OrchestratorAgent', 'orquestador', 1, '{Now}', '{{}}')");
        Exec(connection, $"INSERT INTO orchestration_runs(id, project_id, created_at, plan_hash, requirement_set_hash, original_request_evidence_id, base_commit, base_ref, verification_profile_json, roster_json, options_json) VALUES ('r{n}', 'p1', '{Now}', 'ph{n}', 'rh{n}', 'e{n}', 'abc123', 'main', '{{}}', '[]', '{{}}')");
        Exec(connection, $"INSERT INTO skill_activations(project_id, skill_id, scope, scope_id, enabled, decided_by, decided_at) VALUES ('p1', 'tdd{n}', 'Project', 'p1', 1, 'manuel', '{Now}')");
        Exec(connection, $"INSERT INTO memory_entries(id, project_id, kind, topic, content, source, created_at, origin, content_sha256) VALUES ('m{n}', 'p1', 'Decision', 'Despliegue', 'Se usa blue green {n}', 'registro:r1#3', '{Now}', 'System', '{new string('a', 64)}')");
        Exec(connection, $"INSERT INTO debate_sessions(id, project_id, created_at, mode, requirement_set_hash, question_evidence_id, subject_kind, subject_ref, snapshot_commit, header_json, header_hash) VALUES ('deb-{n}', 'p1', '{Now}', 'Free', 'rh{n}', 'e{n}', 'Commit', 'abc123', 'abc123', '{{}}', 'h{n}')");
        Exec(connection, $"INSERT INTO project_audits(id, project_id, created_at, head_commit, requirement_set_hash, request_evidence_id, header_json, header_hash) VALUES ('aud-{n}', 'p1', '{Now}', 'abc123', 'rh{n}', 'e{n}', '{{}}', 'h{n}')");
        Exec(connection, $"INSERT INTO milestones(id, project_id, created_at, name, objective, requirement_set_hash, header_hash) VALUES ('mil-{n}', 'p1', '{Now}', 'Hito {n}', 'Objetivo', 'rh{n}', 'h{n}')");
        Exec(connection, $"INSERT INTO backlog_items(item_id, version, project_id, created_at, statement, priority, criteria_json, content_sha256) VALUES ('bk-{n}', 1, 'p1', '{Now}', 'Sumar {n}', 'Desirable', '[]', 'h{n}')");
    }

    /// <summary>The attack: copies the first row of <paramref name="table"/> under its own rowid with a key no row has.</summary>
    private static string ReplaceByRowid(SqliteConnection connection, string table, string key, string newKey)
    {
        var columns = new List<string>();
        using (var info = connection.CreateCommand())
        {
            info.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(0));
            }
        }

        var names = string.Join(", ", columns);
        var values = string.Join(", ", columns.Select(c => string.Equals(c, key, StringComparison.Ordinal) ? newKey : c));
        return $"INSERT OR REPLACE INTO {table}(rowid, {names}) SELECT rowid, {values} FROM {table} WHERE rowid = (SELECT MIN(rowid) FROM {table})";
    }

    [Theory]
    [MemberData(nameof(Protected))]
    public void At_0006_a_replace_through_the_rowid_changes_the_row_with_the_triggers_intact(string table, string key, string newKey, string shown)
    {
        using var database = At0006();
        Seed(database, 1);
        var before = Text(database, $"SELECT {key} FROM {table}");
        var kept = Text(database, $"SELECT {shown} FROM {table}");

        using var replace = database.CreateCommand();
        replace.CommandText = ReplaceByRowid(database, table, key, newKey);
        Assert.Equal(1, replace.ExecuteNonQuery());

        Assert.NotEqual(before, Text(database, $"SELECT {key} FROM {table}"));
        Assert.Equal(kept, Text(database, $"SELECT {shown} FROM {table}"));
        Assert.Equal(1, Scalar(database, $"SELECT COUNT(*) FROM {table}"));
        Assert.Equal(1, Scalar(database, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = '{table}_no_replace'"));
    }

    [Theory]
    [MemberData(nameof(Protected))]
    public void With_0007_the_same_replace_is_rejected_and_the_row_stays(string table, string key, string newKey, string shown)
    {
        using var database = At0006();
        Seed(database, 1);
        SqliteSchema.Apply(database);
        var before = Text(database, $"SELECT {shown} || '|' || {key} FROM {table}");

        var ex = Assert.Throws<SqliteException>(() => Exec(database, ReplaceByRowid(database, table, key, newKey)));

        Assert.Contains($"{table} is append-only", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, Text(database, $"SELECT {shown} || '|' || {key} FROM {table}"));
        Assert.Equal(1, Scalar(database, $"SELECT COUNT(*) FROM {table}"));
    }

    [Theory]
    [MemberData(nameof(Protected))]
    public void A_new_database_is_born_with_the_replace_rejected(string table, string key, string newKey, string shown)
    {
        using var database = Current();
        Seed(database, 1);
        var before = Text(database, $"SELECT {shown} || '|' || {key} FROM {table}");

        Assert.Throws<SqliteException>(() => Exec(database, ReplaceByRowid(database, table, key, newKey)));

        Assert.Equal(before, Text(database, $"SELECT {shown} || '|' || {key} FROM {table}"));
        Assert.Contains("rowid = NEW.rowid", Text(database, $"SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = '{table}_no_replace'"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_database_at_0006_migrates_to_0007_without_losing_a_row_and_Apply_stays_idempotent()
    {
        using var database = At0006();
        Seed(database, 1);
        Seed(database, 2);
        var tables = new List<string>();
        using (var list = database.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '%_fts%' AND name <> 'schema_migrations' ORDER BY name";
            using var reader = list.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        var counts = tables.ToDictionary(t => t, t => Scalar(database, $"SELECT COUNT(*) FROM \"{t}\""));
        var triggers = Scalar(database, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger'");

        SqliteSchema.Apply(database);
        SqliteSchema.Apply(database);

        Assert.All(tables, t => Assert.Equal(counts[t], Scalar(database, $"SELECT COUNT(*) FROM \"{t}\"")));
        Assert.Equal(triggers, Scalar(database, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger'"));
        Assert.Equal(7, Scalar(database, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(7, Scalar(database, "SELECT COUNT(*) FROM schema_migrations"));
    }

    [Fact]
    public void Only_the_replace_triggers_of_the_tables_with_their_own_key_change_and_the_two_with_an_integer_key_stay()
    {
        using var database = Current();

        Assert.Equal(11, Scalar(database, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name LIKE '%_no_replace' AND sql LIKE '%rowid = NEW.rowid%'"));
        Assert.Equal(13, Scalar(database, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name LIKE '%_no_replace'"));
        foreach (var table in new[] { "debate_contributions", "skill_activations" })
        {
            Assert.Equal(1, Scalar(database, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = '{table}_no_replace' AND sql NOT LIKE '%rowid = NEW.rowid%'"));
        }
    }

    [Fact]
    public void Appending_rows_keeps_working_with_automatic_and_explicit_new_rowids_in_every_protected_table()
    {
        using var database = Current();
        Seed(database, 1);
        Seed(database, 2);

        foreach (var row in Protected)
        {
            Assert.Equal(2, Scalar(database, $"SELECT COUNT(*) FROM {row.Data.Item1}"));
        }

        // An explicit rowid nobody has is as legitimate as an automatic one.
        Exec(database, $"INSERT INTO evidence(rowid, id, project_id, kind, origin, title, produced_by, captured_at, content_sha256) VALUES (500, 'e500', 'p1', 'k', 'o', 't', 'a', '{Now}', 'abc')");
        Assert.Equal(3, Scalar(database, "SELECT COUNT(*) FROM evidence"));
    }

    [Fact]
    public void After_a_rejected_insert_the_next_legitimate_one_goes_through_and_a_repeated_key_is_still_rejected()
    {
        using var database = Current();
        Seed(database, 1);

        var again = Assert.Throws<SqliteException>(() => Exec(database, $"INSERT INTO evidence(id, project_id, kind, origin, title, produced_by, captured_at, content_sha256) VALUES ('e1', 'p1', 'k', 'o', 'otra', 'a', '{Now}', 'abc')"));
        Assert.Contains("evidence is append-only", again.Message, StringComparison.Ordinal);

        Seed(database, 2);
        Assert.Equal(2, Scalar(database, "SELECT COUNT(*) FROM decision_log"));
        Assert.Equal(2, Scalar(database, "SELECT COUNT(*) FROM milestones"));
        Assert.Equal(2, Scalar(database, "SELECT COUNT(*) FROM backlog_items"));
    }

    [Fact]
    public void A_debate_and_a_milestone_still_anchor_and_a_backlog_item_still_has_a_row_per_version()
    {
        using var database = Current();
        Seed(database, 1);

        Exec(database, $"INSERT INTO backlog_items(item_id, version, project_id, created_at, statement, priority, criteria_json, content_sha256) VALUES ('bk-1', 2, 'p1', '{Now}', 'Sumar y restar', 'Desirable', '[]', 'h2')");
        Exec(database, $"INSERT INTO debate_sessions(id, project_id, created_at, mode, requirement_set_hash, question_evidence_id, subject_kind, subject_ref, snapshot_commit, header_json, header_hash) VALUES ('deb-9', 'p1', '{Now}', 'Free', 'rh1', 'e1', 'Commit', 'abc123', 'abc123', '{{}}', 'h9')");
        Exec(database, $"INSERT INTO milestones(id, project_id, created_at, name, objective, requirement_set_hash, header_hash) VALUES ('mil-9', 'p1', '{Now}', 'Otro hito', 'Objetivo', 'rh1', 'h9')");

        Assert.Equal(2, Scalar(database, "SELECT COUNT(*) FROM backlog_items WHERE item_id = 'bk-1'"));
        Assert.Equal(2, Scalar(database, "SELECT COUNT(*) FROM debate_sessions"));
        Assert.Equal(2, Scalar(database, "SELECT COUNT(*) FROM milestones"));
        Assert.Throws<SqliteException>(() => Exec(database, $"INSERT INTO backlog_items(item_id, version, project_id, created_at, statement, priority, criteria_json, content_sha256) VALUES ('bk-1', 2, 'p1', '{Now}', 'otra', 'Desirable', '[]', 'h3')"));
    }
}
