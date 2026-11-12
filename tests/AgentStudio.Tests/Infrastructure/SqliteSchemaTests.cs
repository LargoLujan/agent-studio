using System.Globalization;
using System.IO;
using AgentStudio.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AgentStudio.Tests.Infrastructure;

public sealed class SqliteSchemaTests : IDisposable
{
    private const string Now = "2026-10-15T10:00:00.0000000+00:00";
    private readonly SqliteConnection _connection = SqliteSchema.Open("Data Source=:memory:");

    public SqliteSchemaTests() => SqliteSchema.Apply(_connection);

    public void Dispose() => _connection.Dispose();

    private void Exec(string sql) => Exec(_connection, sql);

    private long Scalar(string sql) => Scalar(_connection, sql);

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void SeedProject() =>
        Exec($"INSERT INTO projects(id, name, root_path, created_at) VALUES ('p1', 'Proyecto', 'C:/x', '{Now}')");

    /// <summary>The number of the last migration: 0007, the replace triggers closed against the rowid (P45).</summary>
    private const int LastVersion = 7;

    [Fact]
    public void Apply_is_idempotent()
    {
        SqliteSchema.Apply(_connection);
        SqliteSchema.Apply(_connection);

        Assert.Equal(LastVersion, Scalar("SELECT COUNT(*) FROM schema_migrations"));
        Assert.Equal(LastVersion, Scalar("SELECT MAX(version) FROM schema_migrations"));
    }

    [Theory]
    [InlineData("projects")]
    [InlineData("agents")]
    [InlineData("requirement_sets")]
    [InlineData("tasks")]
    [InlineData("task_dependencies")]
    [InlineData("task_status_history")]
    [InlineData("decision_log")]
    [InlineData("evidence")]
    [InlineData("evidence_requirements")]
    [InlineData("debates")]
    [InlineData("debate_participants")]
    [InlineData("debate_contributions")]
    [InlineData("debate_contribution_evidence")]
    [InlineData("verdicts")]
    [InlineData("agent_runs")]
    [InlineData("usage_records")]
    [InlineData("memory_entries")]
    [InlineData("memory_entries_fts")]
    [InlineData("schema_migrations")]
    [InlineData("plans")]
    [InlineData("orchestration_runs")]
    [InlineData("skill_activations")]
    [InlineData("debate_sessions")]
    [InlineData("project_audits")]
    [InlineData("milestones")]
    [InlineData("backlog_items")]
    public void Creates_table(string table) =>
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM sqlite_master WHERE name = '{table}'"));

    [Fact]
    public void Foreign_keys_are_enforced()
    {
        var ex = Assert.Throws<SqliteException>(() =>
            Exec($"INSERT INTO agents(id, project_id, name, role, provider_id, vendor_id, definition_json, created_at) VALUES ('a', 'missing', 'n', 'r', 'p', 'v', '{{}}', '{Now}')"));

        Assert.Contains("FOREIGN KEY", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Task_cannot_depend_on_itself()
    {
        SeedProject();
        Exec($"INSERT INTO tasks(id, project_id, title, kind, status, max_retries, retry_count, requires_evidence, created_at, last_activity_at) VALUES ('t1', 'p1', 't', 'k', 'Pending', 3, 0, 0, '{Now}', '{Now}')");

        Assert.Throws<SqliteException>(() => Exec("INSERT INTO task_dependencies(task_id, prerequisite_id) VALUES ('t1', 't1')"));
    }

    [Fact]
    public void Usage_provenance_is_restricted()
    {
        SeedProject();
        Exec($"INSERT INTO agent_runs(id, project_id, agent_id, provider_id, started_at, status) VALUES ('r1', 'p1', 'a', 'claude-code', '{Now}', 'Running')");

        Exec($"INSERT INTO usage_records(run_id, metric, value, provenance, source, recorded_at) VALUES ('r1', 'tokens', 1, 'Exact', 's', '{Now}')");
        Assert.Throws<SqliteException>(() =>
            Exec($"INSERT INTO usage_records(run_id, metric, value, provenance, source, recorded_at) VALUES ('r1', 'tokens', 1, 'Guess', 's', '{Now}')"));
    }

    [Theory]
    [InlineData("decision_log", "UPDATE decision_log SET summary = 'x'")]
    [InlineData("decision_log", "DELETE FROM decision_log")]
    [InlineData("evidence", "UPDATE evidence SET title = 'x'")]
    [InlineData("evidence", "DELETE FROM evidence")]
    [InlineData("verdicts", "UPDATE verdicts SET summary = 'x'")]
    [InlineData("verdicts", "DELETE FROM verdicts")]
    [InlineData("debate_contributions", "UPDATE debate_contributions SET content = 'x'")]
    [InlineData("debate_contributions", "DELETE FROM debate_contributions")]
    [InlineData("requirement_sets", "UPDATE requirement_sets SET payload_json = '{\"x\":1}'")]
    [InlineData("requirement_sets", "DELETE FROM requirement_sets")]
    [InlineData("plans", "UPDATE plans SET payload_json = '{\"x\":1}'")]
    [InlineData("plans", "DELETE FROM plans")]
    [InlineData("orchestration_runs", "UPDATE orchestration_runs SET base_commit = 'forged'")]
    [InlineData("orchestration_runs", "DELETE FROM orchestration_runs")]
    [InlineData("skill_activations", "UPDATE skill_activations SET enabled = 0")]
    [InlineData("skill_activations", "DELETE FROM skill_activations")]
    [InlineData("memory_entries", "UPDATE memory_entries SET content = 'reescrita'")]
    [InlineData("memory_entries", "UPDATE memory_entries SET superseded_by = 'm9'")]
    [InlineData("memory_entries", "DELETE FROM memory_entries")]
    [InlineData("debate_sessions", "UPDATE debate_sessions SET header_json = '{\"judges\":[]}'")]
    [InlineData("debate_sessions", "UPDATE debate_sessions SET header_hash = 'forged'")]
    [InlineData("debate_sessions", "DELETE FROM debate_sessions")]
    [InlineData("project_audits", "UPDATE project_audits SET head_commit = 'forged'")]
    [InlineData("project_audits", "DELETE FROM project_audits")]
    [InlineData("milestones", "UPDATE milestones SET objective = 'otro'")]
    [InlineData("milestones", "UPDATE milestones SET header_hash = 'forged'")]
    [InlineData("milestones", "DELETE FROM milestones")]
    [InlineData("backlog_items", "UPDATE backlog_items SET statement = 'reescrito'")]
    [InlineData("backlog_items", "DELETE FROM backlog_items")]
    public void Append_only_tables_reject_update_and_delete(string table, string mutation)
    {
        SeedAppendOnlyRows();

        var ex = Assert.Throws<SqliteException>(() => Exec(mutation));

        Assert.Contains($"{table} is append-only", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("decision_log", "INSERT OR REPLACE INTO decision_log(project_id, sequence, recorded_at, actor_kind, actor_id, kind, summary, payload_json, previous_hash, hash) VALUES ('p1', 1, '2026-10-15T10:00:00.0000000+00:00', 'Human', 'u', 'k', 'FORGED', '{}', '', 'h2')", "SELECT summary FROM decision_log WHERE sequence = 1", "s")]
    [InlineData("evidence", "INSERT OR REPLACE INTO evidence(id, project_id, kind, origin, title, produced_by, captured_at, content_sha256) VALUES ('e1', 'p1', 'k', 'o', 'FORGED', 'a', '2026-10-15T10:00:00.0000000+00:00', 'abc')", "SELECT title FROM evidence WHERE id = 'e1'", "t")]
    [InlineData("verdicts", "INSERT OR REPLACE INTO verdicts(id, project_id, debate_id, decision, summary, confidence_level, confidence_justification, requires_human, judge_agent_id, judge_provider_id, judge_vendor_id, requirement_set_hash, decision_log_head_hash, payload_json, issued_at) VALUES ('v1', 'p1', 'd1', 'd', 'FORGED', 'High', 'j', 0, 'j', 'p', 'v', 'rh', 'h', '{}', '2026-10-15T10:00:00.0000000+00:00')", "SELECT summary FROM verdicts WHERE id = 'v1'", "s")]
    [InlineData("debate_contributions", "INSERT OR REPLACE INTO debate_contributions(id, debate_id, round, author_agent_id, kind, content, at) VALUES (1, 'd1', 1, 'a', 'k', 'FORGED', '2026-10-15T10:00:00.0000000+00:00')", "SELECT content FROM debate_contributions WHERE id = 1", "c")]
    [InlineData("requirement_sets", "INSERT OR REPLACE INTO requirement_sets(content_hash, project_id, frozen_at, payload_json) VALUES ('rh', 'p1', '2026-10-15T10:00:00.0000000+00:00', '{\"x\":1}')", "SELECT payload_json FROM requirement_sets WHERE content_hash = 'rh'", "{}")]
    [InlineData("plans", "INSERT OR REPLACE INTO plans(content_hash, project_id, source, author_agent_id, schema_version, created_at, payload_json) VALUES ('ph', 'p1', 'User', NULL, 1, '2026-10-15T10:00:00.0000000+00:00', 'FORGED')", "SELECT payload_json FROM plans WHERE content_hash = 'ph'", "{}")]
    [InlineData("orchestration_runs", "INSERT OR REPLACE INTO orchestration_runs(id, project_id, created_at, plan_hash, requirement_set_hash, original_request_evidence_id, base_commit, verification_profile_json, roster_json, options_json) VALUES ('r1', 'p1', '2026-10-15T10:00:00.0000000+00:00', 'ph', 'rh', 'e1', 'forged', '{}', '[]', '{}')", "SELECT base_commit FROM orchestration_runs WHERE id = 'r1'", "abc123")]
    [InlineData("skill_activations", "INSERT OR REPLACE INTO skill_activations(id, project_id, skill_id, scope, scope_id, enabled, decided_by, decided_at) VALUES (1, 'p1', 'tdd', 'Project', 'p1', 0, 'forged', '2026-10-15T10:00:00.0000000+00:00')", "SELECT decided_by FROM skill_activations WHERE id = 1", "manuel")]
    [InlineData("memory_entries", "INSERT OR REPLACE INTO memory_entries(id, project_id, kind, topic, content, source, created_at, origin, content_sha256) VALUES ('m1', 'p1', 'Decision', 'Despliegue', 'FORGED', 's', '2026-10-15T10:00:00.0000000+00:00', 'User', 'h')", "SELECT content FROM memory_entries WHERE id = 'm1'", "Se usa blue green")]
    [InlineData("debate_sessions", "INSERT OR REPLACE INTO debate_sessions(id, project_id, created_at, mode, requirement_set_hash, question_evidence_id, subject_kind, header_json, header_hash) VALUES ('deb-1', 'p1', '2026-10-15T10:00:00.0000000+00:00', 'Free', 'rh', 'e1', 'None', 'FORGED', 'h')", "SELECT header_json FROM debate_sessions WHERE id = 'deb-1'", "{}")]
    [InlineData("project_audits", "INSERT OR REPLACE INTO project_audits(id, project_id, created_at, head_commit, requirement_set_hash, request_evidence_id, header_json, header_hash) VALUES ('aud-1', 'p1', '2026-10-15T10:00:00.0000000+00:00', 'forged', 'rh', 'e1', '{}', 'h')", "SELECT head_commit FROM project_audits WHERE id = 'aud-1'", "abc123")]
    [InlineData("milestones", "INSERT OR REPLACE INTO milestones(id, project_id, created_at, name, objective, requirement_set_hash, header_hash) VALUES ('mil-1', 'p1', '2026-10-15T10:00:00.0000000+00:00', 'FORGED', 'o', 'rh', 'h')", "SELECT name FROM milestones WHERE id = 'mil-1'", "Hito")]
    [InlineData("backlog_items", "INSERT OR REPLACE INTO backlog_items(item_id, version, project_id, created_at, statement, priority, criteria_json, content_sha256) VALUES ('bk-1', 1, 'p1', '2026-10-15T10:00:00.0000000+00:00', 'FORGED', 'Desirable', '[]', 'h')", "SELECT statement FROM backlog_items WHERE item_id = 'bk-1' AND version = 1", "Sumar")]
    public void Append_only_tables_reject_insert_or_replace_and_keep_original_row(string table, string replace, string select, string original)
    {
        SeedAppendOnlyRows();

        var ex = Assert.Throws<SqliteException>(() => Exec(replace));

        Assert.Contains($"{table} is append-only", ex.Message, StringComparison.Ordinal);
        using var command = _connection.CreateCommand();
        command.CommandText = select;
        Assert.Equal(original, (string?)command.ExecuteScalar());
    }

    private void SeedAppendOnlyRows()
    {
        SeedProject();
        Exec($"INSERT INTO decision_log(project_id, sequence, recorded_at, actor_kind, actor_id, kind, summary, payload_json, previous_hash, hash) VALUES ('p1', 1, '{Now}', 'Human', 'u', 'k', 's', '{{}}', '', 'h')");
        Exec($"INSERT INTO evidence(id, project_id, kind, origin, title, produced_by, captured_at, content_sha256) VALUES ('e1', 'p1', 'k', 'o', 't', 'a', '{Now}', 'abc')");
        Exec($"INSERT INTO requirement_sets(content_hash, project_id, frozen_at, payload_json) VALUES ('rh', 'p1', '{Now}', '{{}}')");
        Exec($"INSERT INTO debates(id, project_id, mode, question, phase, outcome, requirement_set_hash, limits_json, judge_agent_id, started_at) VALUES ('d1', 'p1', 'm', 'q', 'Open', 'None', 'rh', '{{}}', 'j', '{Now}')");
        Exec($"INSERT INTO debate_contributions(debate_id, round, author_agent_id, kind, content, at) VALUES ('d1', 1, 'a', 'k', 'c', '{Now}')");
        Exec($"INSERT INTO verdicts(id, project_id, debate_id, decision, summary, confidence_level, confidence_justification, requires_human, judge_agent_id, judge_provider_id, judge_vendor_id, requirement_set_hash, decision_log_head_hash, payload_json, issued_at) VALUES ('v1', 'p1', 'd1', 'd', 's', 'High', 'j', 0, 'j', 'p', 'v', 'rh', 'h', '{{}}', '{Now}')");
        Exec($"INSERT INTO plans(content_hash, project_id, source, author_agent_id, schema_version, created_at, payload_json) VALUES ('ph', 'p1', 'OrchestratorAgent', 'orquestador', 1, '{Now}', '{{}}')");
        Exec($"INSERT INTO orchestration_runs(id, project_id, created_at, plan_hash, requirement_set_hash, original_request_evidence_id, base_commit, base_ref, verification_profile_json, roster_json, options_json) VALUES ('r1', 'p1', '{Now}', 'ph', 'rh', 'e1', 'abc123', 'main', '{{}}', '[]', '{{}}')");
        Exec($"INSERT INTO skill_activations(project_id, skill_id, scope, scope_id, enabled, decided_by, decided_at) VALUES ('p1', 'tdd', 'Project', 'p1', 1, 'manuel', '{Now}')");
        Exec($"INSERT INTO memory_entries(id, project_id, kind, topic, content, source, created_at, origin, content_sha256) VALUES ('m1', 'p1', 'Decision', 'Despliegue', 'Se usa blue green', 'registro:r1#3', '{Now}', 'System', '{new string('a', 64)}')");
        Exec($"INSERT INTO debate_sessions(id, project_id, created_at, mode, requirement_set_hash, question_evidence_id, subject_kind, subject_ref, snapshot_commit, header_json, header_hash) VALUES ('deb-1', 'p1', '{Now}', 'Free', 'rh', 'e1', 'Commit', 'abc123', 'abc123', '{{}}', 'h')");
        Exec($"INSERT INTO project_audits(id, project_id, created_at, head_commit, requirement_set_hash, request_evidence_id, header_json, header_hash) VALUES ('aud-1', 'p1', '{Now}', 'abc123', 'rh', 'e1', '{{}}', 'h')");
        Exec($"INSERT INTO milestones(id, project_id, created_at, name, objective, requirement_set_hash, header_hash) VALUES ('mil-1', 'p1', '{Now}', 'Hito', 'Objetivo', 'rh', 'h')");
        Exec($"INSERT INTO backlog_items(item_id, version, project_id, created_at, statement, priority, criteria_json, content_sha256) VALUES ('bk-1', 1, 'p1', '{Now}', 'Sumar', 'Desirable', '[]', 'h')");
    }

    // ---- migration 0004 (memory, docs/14 section 6): append-only rows, every state derived from the log ----

    /// <summary>The rows are indexed when they are inserted, and then never change: the index stays in step with them.</summary>
    [Fact]
    public void Memory_is_found_by_full_text_once_inserted_and_can_be_neither_updated_nor_deleted()
    {
        SeedAppendOnlyRows();

        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM memory_entries_fts WHERE memory_entries_fts MATCH 'blue'"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM memory_entries_fts WHERE memory_entries_fts MATCH 'topic:despliegue'"));

        Assert.Contains("memory_entries is append-only", Assert.Throws<SqliteException>(() => Exec("UPDATE memory_entries SET content = 'ahora canary' WHERE id = 'm1'")).Message, StringComparison.Ordinal);
        Assert.Contains("memory_entries is append-only", Assert.Throws<SqliteException>(() => Exec("DELETE FROM memory_entries WHERE id = 'm1'")).Message, StringComparison.Ordinal);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM memory_entries_fts WHERE memory_entries_fts MATCH 'blue'"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM memory_entries_fts WHERE memory_entries_fts MATCH 'canary'"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM memory_entries"));
    }

    [Fact]
    public void The_index_triggers_of_update_and_delete_are_gone_and_the_one_of_insert_stays()
    {
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name IN ('memory_entries_au', 'memory_entries_ad')"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = 'memory_entries_ai' AND tbl_name = 'memory_entries'"));
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("content_sha256")]
    [InlineData("supersedes_entry_id")]
    public void Memory_gains_nullable_columns_for_its_origin_hash_and_the_entry_it_supersedes(string column)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT type, \"notnull\" FROM pragma_table_info('memory_entries') WHERE name = '{column}'";
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read(), $"memory_entries.{column} does not exist.");
        Assert.Equal(("TEXT", 0), (reader.GetString(0), reader.GetInt32(1)));
    }

    [Fact]
    public void A_new_entry_supersedes_an_older_one_without_touching_it()
    {
        SeedAppendOnlyRows();

        Exec($"INSERT INTO memory_entries(id, project_id, kind, topic, content, source, created_at, origin, content_sha256, supersedes_entry_id) VALUES ('m2', 'p1', 'Decision', 'Despliegue', 'Ahora canary', 'registro:r2#5', '{Now}', 'User', '{new string('b', 64)}', 'm1')");

        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM memory_entries WHERE id = 'm2' AND supersedes_entry_id = 'm1'"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM memory_entries WHERE id = 'm1' AND superseded_by IS NULL AND content = 'Se usa blue green'"));
    }

    [Fact]
    public void Memory_can_be_listed_by_project_category_and_date_through_an_index()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_index_info('memory_by_project') ORDER BY seqno";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        Assert.Equal(["project_id", "kind", "created_at"], columns);
    }

    /// <summary>A database with the embedded <paramref name="migrations"/> applied by hand, and the table of versions empty.</summary>
    private static SqliteConnection At(params string[] migrations)
    {
        var connection = SqliteSchema.Open("Data Source=:memory:");
        foreach (var migration in migrations)
        {
            Exec(connection, EmbeddedMigration(migration));
        }

        Exec(connection, "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)");
        return connection;
    }

    private static readonly string[] UpTo0003 = ["0001_initial.sql", "0002_orchestration.sql", "0003_evidence_subject_commit.sql"];

    private static readonly string[] UpTo0004 = [.. UpTo0003, "0004_memory.sql"];

    private static readonly string[] UpTo0005 = [.. UpTo0004, "0005_debates.sql"];

    private static List<string> Tables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>docs/14 D-01 and D-27: usage is a projection of the log and the skills catalog lives in the profile, so 0004 adds no table.</summary>
    [Fact]
    public void Phase_4_adds_no_table_neither_for_usage_nor_for_skills()
    {
        using var at0003 = At(UpTo0003);
        using var at0004 = At(UpTo0004);

        Assert.Equal(Tables(at0003), Tables(at0004));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('skills', 'skill_health_checks')"));
        Assert.Equal(["usage_records"], Tables(_connection).Where(t => t.Contains("usage", StringComparison.Ordinal)));
    }

    // ---- migration 0005 (debates and audits, docs/15 section 6): two immutable headers, every state derived from the log ----

    /// <summary>D-01: the debate tables of 0001 (with their mutable state) stay, unused; 0005 adds only the two headers.</summary>
    [Fact]
    public void Phase_5_adds_only_the_headers_of_debates_and_audits_and_keeps_the_debate_tables_of_0001()
    {
        using var at0004 = At(UpTo0004);

        using var at0005 = At(UpTo0005);

        Assert.Equal(Tables(at0004).Concat(["debate_sessions", "project_audits"]).Order(StringComparer.Ordinal), Tables(at0005));
        Assert.Equal(4, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('debates', 'debate_participants', 'debate_contributions', 'debate_contribution_evidence')"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM pragma_table_info('verdicts') WHERE name = 'debate_id'"));
    }

    [Fact]
    public void Upgrading_a_database_at_0004_adds_the_headers_keeps_its_rows_and_is_idempotent()
    {
        using var old = At(UpTo0004);
        Exec(old, $"INSERT INTO schema_migrations(version, applied_at) VALUES (1, '{Now}'), (2, '{Now}'), (3, '{Now}'), (4, '{Now}')");
        Exec(old, $"INSERT INTO projects(id, name, root_path, created_at) VALUES ('p1', 'Proyecto', 'C:/x', '{Now}')");
        Exec(old, $"INSERT INTO evidence(id, project_id, kind, origin, title, produced_by, captured_at, content_sha256, run_id) VALUES ('e1', 'p1', 'Document', 'ProvidedByHuman', 'pregunta', 'manuel', '{Now}', 'abc', 'deb-1')");
        Exec(old, $"INSERT INTO requirement_sets(content_hash, project_id, frozen_at, payload_json) VALUES ('rh', 'p1', '{Now}', '{{}}')");
        Exec(old, $"INSERT INTO memory_entries(id, project_id, kind, topic, content, source, created_at, origin, content_sha256) VALUES ('m1', 'p1', 'Decision', 't', 'c', 's', '{Now}', 'System', 'h')");

        SqliteSchema.Apply(old);
        SqliteSchema.Apply(old);

        Assert.Equal(((long)LastVersion, (long)LastVersion), (Scalar(old, "SELECT COUNT(*) FROM schema_migrations"), Scalar(old, "SELECT MAX(version) FROM schema_migrations")));
        Assert.Equal(1, Scalar(old, "SELECT COUNT(*) FROM evidence WHERE id = 'e1' AND run_id = 'deb-1'"));
        Assert.Equal(1, Scalar(old, "SELECT COUNT(*) FROM memory_entries WHERE id = 'm1'"));
        Exec(old, $"INSERT INTO debate_sessions(id, project_id, created_at, mode, requirement_set_hash, question_evidence_id, subject_kind, header_json, header_hash) VALUES ('deb-1', 'p1', '{Now}', 'Adversarial', 'rh', 'e1', 'None', '{{}}', 'h')");
        Assert.Contains("debate_sessions is append-only", Assert.Throws<SqliteException>(() => Exec(old, "UPDATE debate_sessions SET mode = 'Free'")).Message, StringComparison.Ordinal);
        Assert.Contains("debate_sessions is append-only", Assert.Throws<SqliteException>(() => Exec(old, "DELETE FROM debate_sessions")).Message, StringComparison.Ordinal);
        Assert.Contains("memory_entries is append-only", Assert.Throws<SqliteException>(() => Exec(old, "DELETE FROM memory_entries")).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("debate_sessions_by_project", "debate_sessions")]
    [InlineData("project_audits_by_project", "project_audits")]
    public void The_headers_are_listed_by_project_and_date_through_an_index(string index, string table)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_index_info('{index}') ORDER BY seqno";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        Assert.Equal(["project_id", "created_at"], columns);
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = '{index}' AND tbl_name = '{table}'"));
    }

    // ---- migration 0006 (milestones and backlog, docs/16 section 4.6): two append-only tables, every state derived from the log ----

    [Fact]
    public void Phase_9_adds_only_the_milestones_and_the_backlog_and_no_table_for_links_or_the_board()
    {
        using var at0005 = At(UpTo0005);

        Assert.Equal(Tables(at0005).Concat(["backlog_items", "milestones"]).Order(StringComparer.Ordinal), Tables(_connection));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND (name LIKE '%link%' OR name LIKE '%board%')"));
    }

    [Fact]
    public void Upgrading_a_database_at_0005_adds_the_tables_keeps_its_rows_and_is_idempotent()
    {
        using var old = At(UpTo0005);
        Exec(old, $"INSERT INTO schema_migrations(version, applied_at) VALUES (1, '{Now}'), (2, '{Now}'), (3, '{Now}'), (4, '{Now}'), (5, '{Now}')");
        Exec(old, $"INSERT INTO projects(id, name, root_path, created_at) VALUES ('p1', 'Proyecto', 'C:/x', '{Now}')");
        Exec(old, $"INSERT INTO requirement_sets(content_hash, project_id, frozen_at, payload_json) VALUES ('rh', 'p1', '{Now}', '{{}}')");
        Exec(old, $"INSERT INTO evidence(id, project_id, kind, origin, title, produced_by, captured_at, content_sha256, run_id) VALUES ('e1', 'p1', 'Document', 'ProvidedByHuman', 'pregunta', 'manuel', '{Now}', 'abc', 'deb-1')");
        Exec(old, $"INSERT INTO debate_sessions(id, project_id, created_at, mode, requirement_set_hash, question_evidence_id, subject_kind, header_json, header_hash) VALUES ('deb-1', 'p1', '{Now}', 'Free', 'rh', 'e1', 'None', '{{}}', 'h')");

        SqliteSchema.Apply(old);
        SqliteSchema.Apply(old);

        Assert.Equal(((long)LastVersion, (long)LastVersion), (Scalar(old, "SELECT COUNT(*) FROM schema_migrations"), Scalar(old, "SELECT MAX(version) FROM schema_migrations")));
        Assert.Equal(1, Scalar(old, "SELECT COUNT(*) FROM debate_sessions WHERE id = 'deb-1'"));
        Exec(old, $"INSERT INTO milestones(id, project_id, created_at, name, objective, requirement_set_hash, budget_json, header_hash) VALUES ('mil-1', 'p1', '{Now}', 'Hito', 'Objetivo', 'rh', '{{\"maxTokens\":1000,\"period\":\"Total\"}}', 'h')");
        Assert.Contains("milestones is append-only", Assert.Throws<SqliteException>(() => Exec(old, "UPDATE milestones SET name = 'x'")).Message, StringComparison.Ordinal);
        Assert.Contains("milestones is append-only", Assert.Throws<SqliteException>(() => Exec(old, "DELETE FROM milestones")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_backlog_item_has_a_row_per_version_which_is_never_replaced()
    {
        SeedAppendOnlyRows();

        Exec($"INSERT INTO backlog_items(item_id, version, project_id, created_at, statement, priority, criteria_json, content_sha256) VALUES ('bk-1', 2, 'p1', '{Now}', 'Sumar y restar', 'Mandatory', '[]', 'h2')");

        Assert.Equal(2, Scalar("SELECT COUNT(*) FROM backlog_items WHERE item_id = 'bk-1'"));
        Assert.Contains("backlog_items is append-only", Assert.Throws<SqliteException>(() =>
            Exec($"INSERT INTO backlog_items(item_id, version, project_id, created_at, statement, priority, criteria_json, content_sha256) VALUES ('bk-1', 2, 'p1', '{Now}', 'otra', 'Mandatory', '[]', 'h3')")).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("INSERT INTO milestones(id, project_id, created_at, name, objective, requirement_set_hash, header_hash) VALUES ('mx', 'ghost', '2026-10-15T10:00:00.0000000+00:00', 'n', 'o', 'rh', 'h')")]
    [InlineData("INSERT INTO milestones(id, project_id, created_at, name, objective, requirement_set_hash, header_hash) VALUES ('mx', 'p1', '2026-10-15T10:00:00.0000000+00:00', 'n', 'o', 'no-requirements', 'h')")]
    [InlineData("INSERT INTO milestones(id, project_id, created_at, name, objective, requirement_set_hash, header_hash) VALUES ('mx', 'p1', '2026-10-15T10:00:00.0000000+00:00', ' ', 'o', 'rh', 'h')")]
    [InlineData("INSERT INTO milestones(id, project_id, created_at, name, objective, requirement_set_hash, header_hash) VALUES ('mx', 'p1', '2026-10-15T10:00:00.0000000+00:00', 'n', '', 'rh', 'h')")]
    [InlineData("INSERT INTO backlog_items(item_id, version, project_id, created_at, statement, priority, criteria_json, content_sha256) VALUES ('bk-9', 0, 'p1', '2026-10-15T10:00:00.0000000+00:00', 's', 'Desirable', '[]', 'h')")]
    [InlineData("INSERT INTO backlog_items(item_id, version, project_id, created_at, statement, priority, criteria_json, content_sha256) VALUES ('bk-9', 1, 'p1', '2026-10-15T10:00:00.0000000+00:00', 's', 'Urgent', '[]', 'h')")]
    [InlineData("INSERT INTO backlog_items(item_id, version, project_id, created_at, statement, priority, criteria_json, content_sha256) VALUES ('bk-9', 1, 'ghost', '2026-10-15T10:00:00.0000000+00:00', 's', 'Desirable', '[]', 'h')")]
    [InlineData("INSERT INTO backlog_items(item_id, version, project_id, created_at, statement, priority, criteria_json, content_sha256) VALUES ('bk-9', 1, 'p1', '2026-10-15T10:00:00.0000000+00:00', ' ', 'Desirable', '[]', 'h')")]
    public void The_new_tables_refuse_rows_that_break_their_checks_or_their_keys(string insert)
    {
        SeedAppendOnlyRows();

        Assert.Throws<SqliteException>(() => Exec(insert));
    }

    [Theory]
    [InlineData("milestones_by_project", "milestones")]
    [InlineData("backlog_items_by_project", "backlog_items")]
    public void The_new_tables_are_listed_by_project_and_date_through_an_index(string index, string table)
    {
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = '{index}' AND tbl_name = '{table}'"));
        Assert.Equal(2, Scalar($"SELECT COUNT(*) FROM pragma_index_info('{index}') WHERE name IN ('project_id', 'created_at')"));
    }

    [Fact]
    public void A_revision_debate_points_at_the_verdict_it_challenges()
    {
        SeedAppendOnlyRows();

        Exec($"INSERT INTO debate_sessions(id, project_id, created_at, mode, requirement_set_hash, question_evidence_id, subject_kind, subject_ref, challenges_verdict_id, header_json, header_hash) VALUES ('deb-2', 'p1', '{Now}', 'Adversarial', 'rh', 'e1', 'Verdict', 'v1', 'v1', '{{}}', 'h2')");

        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM debate_sessions WHERE id = 'deb-2' AND challenges_verdict_id = 'v1'"));
    }

    [Fact]
    public void Upgrading_a_database_at_0003_keeps_its_rows_and_is_idempotent()
    {
        using var old = SqliteSchema.Open("Data Source=:memory:");
        Exec(old, EmbeddedMigration("0001_initial.sql"));
        Exec(old, EmbeddedMigration("0002_orchestration.sql"));
        Exec(old, EmbeddedMigration("0003_evidence_subject_commit.sql"));
        Exec(old, "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)");
        Exec(old, $"INSERT INTO schema_migrations(version, applied_at) VALUES (1, '{Now}'), (2, '{Now}'), (3, '{Now}')");
        Exec(old, $"INSERT INTO projects(id, name, root_path, created_at) VALUES ('p1', 'Proyecto', 'C:/x', '{Now}')");
        Exec(old, $"INSERT INTO evidence(id, project_id, kind, origin, title, produced_by, captured_at, content_sha256, subject_commit) VALUES ('e1', 'p1', 'TestRun', 'CapturedBySystem', 't', 'a', '{Now}', 'abc', 'c1')");
        Exec(old, $"INSERT INTO memory_entries(id, project_id, kind, topic, content, source, created_at) VALUES ('m0', 'p1', 'Decision', 'Antiguo', 'Una entrada anterior a la Fase 4', 'test', '{Now}')");

        SqliteSchema.Apply(old);
        SqliteSchema.Apply(old);

        Assert.Equal(((long)LastVersion, (long)LastVersion), (Scalar(old, "SELECT COUNT(*) FROM schema_migrations"), Scalar(old, "SELECT MAX(version) FROM schema_migrations")));
        Assert.Equal(1, Scalar(old, "SELECT COUNT(*) FROM evidence WHERE id = 'e1' AND subject_commit = 'c1'"));
        Assert.Equal(1, Scalar(old, "SELECT COUNT(*) FROM memory_entries WHERE id = 'm0' AND origin IS NULL AND content_sha256 IS NULL AND supersedes_entry_id IS NULL"));
        Assert.Equal(1, Scalar(old, "SELECT COUNT(*) FROM memory_entries_fts WHERE memory_entries_fts MATCH 'anterior'"));
        Assert.Contains("memory_entries is append-only", Assert.Throws<SqliteException>(() => Exec(old, "UPDATE memory_entries SET content = 'x'")).Message, StringComparison.Ordinal);
        Assert.Contains("memory_entries is append-only", Assert.Throws<SqliteException>(() => Exec(old, "DELETE FROM memory_entries")).Message, StringComparison.Ordinal);
    }

    // ---- migration 0002 (orchestration) ----

    [Theory]
    [InlineData("plans")]
    [InlineData("orchestration_runs")]
    [InlineData("skill_activations")]
    [InlineData("memory_entries")]
    [InlineData("debate_sessions")]
    [InlineData("project_audits")]
    [InlineData("milestones")]
    [InlineData("backlog_items")]
    public void Each_new_append_only_table_has_its_update_delete_and_replace_triggers(string table)
    {
        foreach (var suffix in new[] { "no_update", "no_delete", "no_replace" })
        {
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = '{table}_{suffix}' AND tbl_name = '{table}'"));
        }
    }

    [Fact]
    public void A_plan_cannot_be_saved_twice_under_the_same_hash()
    {
        SeedAppendOnlyRows();

        var ex = Assert.Throws<SqliteException>(() =>
            Exec($"INSERT INTO plans(content_hash, project_id, source, author_agent_id, schema_version, created_at, payload_json) VALUES ('ph', 'p1', 'User', NULL, 1, '{Now}', '{{}}')"));

        Assert.Contains("plans is append-only", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Activation_decisions_are_a_history_so_the_same_skill_and_scope_can_appear_many_times()
    {
        SeedProject();
        Exec($"INSERT INTO skill_activations(project_id, skill_id, scope, scope_id, enabled, decided_by, decided_at) VALUES ('p1', 'tdd', 'Project', 'p1', 1, 'manuel', '{Now}')");
        Exec($"INSERT INTO skill_activations(project_id, skill_id, scope, scope_id, enabled, decided_by, decided_at) VALUES ('p1', 'tdd', 'Project', 'p1', 0, 'manuel', '{Now}')");

        Assert.Equal(2, Scalar("SELECT COUNT(*) FROM skill_activations WHERE skill_id = 'tdd'"));
        Assert.Equal(0, Scalar("SELECT enabled FROM skill_activations ORDER BY id DESC LIMIT 1"));
    }

    [Theory]
    [InlineData("INSERT INTO plans(content_hash, project_id, source, author_agent_id, schema_version, created_at, payload_json) VALUES ('p2', 'p1', 'Robot', NULL, 1, '2026-10-15T10:00:00.0000000+00:00', '{}')")]
    [InlineData("INSERT INTO skill_activations(project_id, skill_id, scope, scope_id, enabled, decided_by, decided_at) VALUES ('p1', 'tdd', 'Global', 'x', 1, 'manuel', '2026-10-15T10:00:00.0000000+00:00')")]
    [InlineData("INSERT INTO skill_activations(project_id, skill_id, scope, scope_id, enabled, decided_by, decided_at) VALUES ('p1', 'tdd', 'Agent', 'a', 2, 'manuel', '2026-10-15T10:00:00.0000000+00:00')")]
    [InlineData("INSERT INTO verdicts(id, project_id, decision, summary, confidence_level, confidence_justification, requires_human, judge_agent_id, judge_provider_id, judge_vendor_id, requirement_set_hash, decision_log_head_hash, payload_json, issued_at, valid) VALUES ('vx', 'p1', 'd', 's', 'High', 'j', 0, 'j', 'p', 'v', 'rh', 'h', '{}', '2026-10-15T10:00:00.0000000+00:00', 2)")]
    [InlineData("INSERT INTO memory_entries(id, project_id, kind, topic, content, source, created_at, origin) VALUES ('mx', 'p1', 'Decision', 't', 'c', 's', '2026-10-15T10:00:00.0000000+00:00', 'Robot')")]
    [InlineData("INSERT INTO memory_entries(id, project_id, kind, topic, content, source, created_at, origin) VALUES ('mx', 'p1', 'Decision', 't', 'c', 's', '2026-10-15T10:00:00.0000000+00:00', 'system')")]
    [InlineData("INSERT INTO debate_sessions(id, project_id, created_at, mode, requirement_set_hash, question_evidence_id, subject_kind, header_json, header_hash) VALUES ('dx', 'p1', '2026-10-15T10:00:00.0000000+00:00', 'Majority', 'rh', 'e1', 'None', '{}', 'h')")]
    [InlineData("INSERT INTO debate_sessions(id, project_id, created_at, mode, requirement_set_hash, question_evidence_id, subject_kind, header_json, header_hash) VALUES ('dx', 'p1', '2026-10-15T10:00:00.0000000+00:00', 'Free', 'rh', 'e1', 'Branch', '{}', 'h')")]
    public void The_new_columns_and_tables_restrict_their_values(string insert)
    {
        SeedProject();

        var ex = Assert.Throws<SqliteException>(() => Exec(insert));

        Assert.Contains("CHECK", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("INSERT INTO plans(content_hash, project_id, source, schema_version, created_at, payload_json) VALUES ('p2', 'ghost', 'User', 1, '2026-10-15T10:00:00.0000000+00:00', '{}')")]
    [InlineData("INSERT INTO orchestration_runs(id, project_id, created_at, plan_hash, requirement_set_hash, original_request_evidence_id, base_commit, verification_profile_json, roster_json, options_json) VALUES ('r2', 'p1', '2026-10-15T10:00:00.0000000+00:00', 'no-plan', 'rh', 'e1', 'c', '{}', '[]', '{}')")]
    [InlineData("INSERT INTO orchestration_runs(id, project_id, created_at, plan_hash, requirement_set_hash, original_request_evidence_id, base_commit, verification_profile_json, roster_json, options_json) VALUES ('r2', 'p1', '2026-10-15T10:00:00.0000000+00:00', 'ph', 'no-requirements', 'e1', 'c', '{}', '[]', '{}')")]
    [InlineData("INSERT INTO orchestration_runs(id, project_id, created_at, plan_hash, requirement_set_hash, original_request_evidence_id, base_commit, verification_profile_json, roster_json, options_json) VALUES ('r2', 'p1', '2026-10-15T10:00:00.0000000+00:00', 'ph', 'rh', 'no-evidence', 'c', '{}', '[]', '{}')")]
    [InlineData("INSERT INTO orchestration_runs(id, project_id, created_at, plan_hash, requirement_set_hash, original_request_evidence_id, base_commit, verification_profile_json, roster_json, options_json, follows_run_id) VALUES ('r2', 'p1', '2026-10-15T10:00:00.0000000+00:00', 'ph', 'rh', 'e1', 'c', '{}', '[]', '{}', 'no-run')")]
    [InlineData("INSERT INTO skill_activations(project_id, skill_id, scope, scope_id, enabled, decided_by, decided_at) VALUES ('ghost', 'tdd', 'Project', 'x', 1, 'manuel', '2026-10-15T10:00:00.0000000+00:00')")]
    [InlineData("INSERT INTO memory_entries(id, project_id, kind, topic, content, source, created_at, origin, supersedes_entry_id) VALUES ('mx', 'p1', 'Decision', 't', 'c', 's', '2026-10-15T10:00:00.0000000+00:00', 'User', 'no-entry')")]
    [InlineData("INSERT INTO debate_sessions(id, project_id, created_at, mode, requirement_set_hash, question_evidence_id, subject_kind, header_json, header_hash) VALUES ('dx', 'ghost', '2026-10-15T10:00:00.0000000+00:00', 'Free', 'rh', 'e1', 'None', '{}', 'h')")]
    [InlineData("INSERT INTO debate_sessions(id, project_id, created_at, mode, requirement_set_hash, question_evidence_id, subject_kind, header_json, header_hash) VALUES ('dx', 'p1', '2026-10-15T10:00:00.0000000+00:00', 'Free', 'no-requirements', 'e1', 'None', '{}', 'h')")]
    [InlineData("INSERT INTO debate_sessions(id, project_id, created_at, mode, requirement_set_hash, question_evidence_id, subject_kind, header_json, header_hash) VALUES ('dx', 'p1', '2026-10-15T10:00:00.0000000+00:00', 'Free', 'rh', 'no-evidence', 'None', '{}', 'h')")]
    [InlineData("INSERT INTO debate_sessions(id, project_id, created_at, mode, requirement_set_hash, question_evidence_id, subject_kind, challenges_verdict_id, header_json, header_hash) VALUES ('dx', 'p1', '2026-10-15T10:00:00.0000000+00:00', 'Adversarial', 'rh', 'e1', 'Verdict', 'no-verdict', '{}', 'h')")]
    [InlineData("INSERT INTO project_audits(id, project_id, created_at, requirement_set_hash, request_evidence_id, header_json, header_hash) VALUES ('ax', 'ghost', '2026-10-15T10:00:00.0000000+00:00', 'rh', 'e1', '{}', 'h')")]
    [InlineData("INSERT INTO project_audits(id, project_id, created_at, requirement_set_hash, request_evidence_id, header_json, header_hash) VALUES ('ax', 'p1', '2026-10-15T10:00:00.0000000+00:00', 'no-requirements', 'e1', '{}', 'h')")]
    [InlineData("INSERT INTO project_audits(id, project_id, created_at, requirement_set_hash, request_evidence_id, header_json, header_hash) VALUES ('ax', 'p1', '2026-10-15T10:00:00.0000000+00:00', 'rh', 'no-evidence', '{}', 'h')")]
    public void The_new_tables_reference_what_they_depend_on(string insert)
    {
        SeedAppendOnlyRows();

        var ex = Assert.Throws<SqliteException>(() => Exec(insert));

        Assert.Contains("FOREIGN KEY", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_follow_up_run_can_point_at_the_run_it_continues()
    {
        SeedAppendOnlyRows();

        Exec($"INSERT INTO orchestration_runs(id, project_id, created_at, plan_hash, requirement_set_hash, original_request_evidence_id, base_commit, verification_profile_json, roster_json, options_json, follows_run_id) VALUES ('r2', 'p1', '{Now}', 'ph', 'rh', 'e1', 'def456', '{{}}', '[]', '{{}}', 'r1')");

        Assert.Equal(2, Scalar("SELECT COUNT(*) FROM orchestration_runs"));
    }

    [Theory]
    [InlineData("evidence", "run_id", "TEXT")]
    [InlineData("evidence", "task_id", "TEXT")]
    [InlineData("evidence", "attempt", "INTEGER")]
    [InlineData("evidence", "media_type", "TEXT")]
    [InlineData("evidence", "size_bytes", "INTEGER")]
    [InlineData("verdicts", "run_id", "TEXT")]
    [InlineData("verdicts", "task_id", "TEXT")]
    [InlineData("verdicts", "scope", "TEXT")]
    [InlineData("verdicts", "valid", "INTEGER")]
    [InlineData("verdicts", "validation_json", "TEXT")]
    [InlineData("verdicts", "supersedes_verdict_id", "TEXT")]
    public void Evidence_and_verdicts_gain_nullable_columns_for_the_run_they_belong_to(string table, string column, string type)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT type, \"notnull\" FROM pragma_table_info('{table}') WHERE name = '{column}'";
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read(), $"{table}.{column} does not exist.");
        Assert.Equal(type, reader.GetString(0));
        Assert.Equal(0, reader.GetInt32(1));
    }

    [Theory]
    [InlineData("evidence_by_run", "evidence")]
    [InlineData("verdicts_by_run", "verdicts")]
    public void Evidence_and_verdicts_can_be_listed_by_run_through_an_index(string index, string table)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_index_info('{index}') ORDER BY seqno";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        Assert.Equal(["project_id", "run_id"], columns);
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = '{index}' AND tbl_name = '{table}'"));
    }

    [Fact]
    public void Evidence_and_verdicts_store_their_run_columns_and_a_correction_points_at_the_verdict_it_replaces()
    {
        SeedAppendOnlyRows();
        Exec($"INSERT INTO evidence(id, project_id, kind, origin, title, produced_by, captured_at, content_sha256, run_id, task_id, attempt, media_type, size_bytes) VALUES ('e2', 'p1', 'TestRun', 'CapturedBySystem', 't', 'agent-studio', '{Now}', 'def', '20261015-a1b2c3', 'T1', 2, 'text/plain', 1234)");
        Exec($"INSERT INTO verdicts(id, project_id, decision, summary, confidence_level, confidence_justification, requires_human, judge_agent_id, judge_provider_id, judge_vendor_id, requirement_set_hash, decision_log_head_hash, payload_json, issued_at, run_id, task_id, scope, valid, validation_json, supersedes_verdict_id) VALUES ('v2', 'p1', 'Approved', 's', 'High', 'j', 0, 'j', 'p', 'v', 'rh', 'h', '{{}}', '{Now}', '20261015-a1b2c3', 'T7', 'CurrentPlan', 0, '[]', 'v1')");

        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM evidence WHERE project_id = 'p1' AND run_id = '20261015-a1b2c3' AND task_id = 'T1' AND attempt = 2 AND media_type = 'text/plain' AND size_bytes = 1234"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM verdicts WHERE supersedes_verdict_id = 'v1' AND valid = 0 AND scope = 'CurrentPlan'"));
        Assert.Throws<SqliteException>(() =>
            Exec($"INSERT INTO verdicts(id, project_id, decision, summary, confidence_level, confidence_justification, requires_human, judge_agent_id, judge_provider_id, judge_vendor_id, requirement_set_hash, decision_log_head_hash, payload_json, issued_at, supersedes_verdict_id) VALUES ('v3', 'p1', 'Approved', 's', 'High', 'j', 0, 'j', 'p', 'v', 'rh', 'h', '{{}}', '{Now}', 'no-verdict')"));
    }

    [Fact]
    public void Upgrading_a_database_that_has_only_the_initial_schema_keeps_its_rows_and_its_protections()
    {
        using var old = SqliteSchema.Open("Data Source=:memory:");
        Exec(old, EmbeddedMigration("0001_initial.sql"));
        Exec(old, "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)");
        Exec(old, $"INSERT INTO schema_migrations(version, applied_at) VALUES (1, '{Now}')");
        Exec(old, $"INSERT INTO projects(id, name, root_path, created_at) VALUES ('p1', 'Proyecto', 'C:/x', '{Now}')");
        Exec(old, $"INSERT INTO evidence(id, project_id, kind, origin, title, produced_by, captured_at, content_sha256) VALUES ('e1', 'p1', 'k', 'o', 't', 'a', '{Now}', 'abc')");
        Exec(old, $"INSERT INTO requirement_sets(content_hash, project_id, frozen_at, payload_json) VALUES ('rh', 'p1', '{Now}', '{{}}')");
        Exec(old, $"INSERT INTO verdicts(id, project_id, decision, summary, confidence_level, confidence_justification, requires_human, judge_agent_id, judge_provider_id, judge_vendor_id, requirement_set_hash, decision_log_head_hash, payload_json, issued_at) VALUES ('v1', 'p1', 'd', 's', 'High', 'j', 0, 'j', 'p', 'v', 'rh', 'h', '{{}}', '{Now}')");
        Assert.Equal(0, Scalar(old, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'plans'"));

        SqliteSchema.Apply(old);

        Assert.Equal(LastVersion, Scalar(old, "SELECT COUNT(*) FROM schema_migrations"));
        Assert.Equal(1, Scalar(old, "SELECT COUNT(*) FROM evidence WHERE id = 'e1' AND title = 't' AND run_id IS NULL AND task_id IS NULL AND attempt IS NULL AND media_type IS NULL AND size_bytes IS NULL AND subject_commit IS NULL"));
        Assert.Equal(1, Scalar(old, "SELECT COUNT(*) FROM verdicts WHERE id = 'v1' AND summary = 's' AND run_id IS NULL AND valid IS NULL AND supersedes_verdict_id IS NULL"));
        Assert.Equal(1, Scalar(old, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'plans'"));
        var tampering = Assert.Throws<SqliteException>(() => Exec(old, "UPDATE evidence SET title = 'x'"));
        Assert.Contains("evidence is append-only", tampering.Message, StringComparison.Ordinal);

        SqliteSchema.Apply(old);
        Assert.Equal(LastVersion, Scalar(old, "SELECT COUNT(*) FROM schema_migrations"));
    }

    // ---- migration 0003 (the commit an evidence verified) ----

    [Fact]
    public void Evidence_keeps_the_commit_it_verified_and_it_is_as_immutable_as_the_rest_of_the_row()
    {
        SeedProject();
        Exec($"INSERT INTO evidence(id, project_id, kind, origin, title, produced_by, captured_at, content_sha256, subject_commit) VALUES ('e9', 'p1', 'TestRun', 'CapturedBySystem', 't', 'agent-studio', '{Now}', 'abc', '{new string('a', 40)}')");

        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM evidence WHERE id = 'e9' AND subject_commit = '{new string('a', 40)}'"));
        var rebinding = Assert.Throws<SqliteException>(() => Exec($"UPDATE evidence SET subject_commit = '{new string('b', 40)}' WHERE id = 'e9'"));
        Assert.Contains("evidence is append-only", rebinding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Upgrading_a_database_at_0002_adds_the_subject_commit_and_keeps_its_rows()
    {
        using var old = SqliteSchema.Open("Data Source=:memory:");
        Exec(old, EmbeddedMigration("0001_initial.sql"));
        Exec(old, EmbeddedMigration("0002_orchestration.sql"));
        Exec(old, "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)");
        Exec(old, $"INSERT INTO schema_migrations(version, applied_at) VALUES (1, '{Now}'), (2, '{Now}')");
        Exec(old, $"INSERT INTO projects(id, name, root_path, created_at) VALUES ('p1', 'Proyecto', 'C:/x', '{Now}')");
        Exec(old, $"INSERT INTO evidence(id, project_id, kind, origin, title, produced_by, captured_at, content_sha256, run_id) VALUES ('e1', 'p1', 'TestRun', 'CapturedBySystem', 't', 'a', '{Now}', 'abc', 'r1')");

        SqliteSchema.Apply(old);

        Assert.Equal(LastVersion, Scalar(old, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(1, Scalar(old, "SELECT COUNT(*) FROM evidence WHERE id = 'e1' AND run_id = 'r1' AND subject_commit IS NULL"));
        Assert.Contains("evidence is append-only", Assert.Throws<SqliteException>(() => Exec(old, "UPDATE evidence SET subject_commit = 'x'")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_migration_that_fails_half_way_leaves_nothing_behind_and_the_version_alone()
    {
        using var old = SqliteSchema.Open("Data Source=:memory:");
        Exec(old, EmbeddedMigration("0001_initial.sql"));
        Exec(old, "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)");
        Exec(old, $"INSERT INTO schema_migrations(version, applied_at) VALUES (1, '{Now}')");
        Exec(old, "CREATE TABLE skill_activations(clash TEXT)"); // 0002 creates plans and runs first, then fails here

        Assert.Throws<SqliteException>(() => SqliteSchema.Apply(old));

        Assert.Equal(1, Scalar(old, "SELECT COUNT(*) FROM schema_migrations"));
        Assert.Equal(0, Scalar(old, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('plans', 'orchestration_runs')"));
        Assert.Equal(0, Scalar(old, "SELECT COUNT(*) FROM pragma_table_info('evidence') WHERE name = 'run_id'"));
    }

    private static string EmbeddedMigration(string fileName)
    {
        var assembly = typeof(SqliteSchema).Assembly;
        using var stream = assembly.GetManifestResourceStream("AgentStudio.Schema." + fileName)
            ?? throw new InvalidOperationException($"Migration {fileName} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
