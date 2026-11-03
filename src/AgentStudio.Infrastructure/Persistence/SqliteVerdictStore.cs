using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Infrastructure.Persistence.Json;
using Microsoft.Data.Sqlite;
using static AgentStudio.Infrastructure.Persistence.SqliteValues;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// Verdicts in <c>verdicts</c>, append-only. Each one is stored exactly as the judge gave it, valid or not, with the
/// violations the validator found; <c>payload_json</c> holds the whole verdict and is what is read back, while the
/// other columns are there to filter (a member the judge left out gets an empty value in them). A correction points at
/// the verdict it supersedes, which stays. Appending the same verdict again does nothing; the same id with other
/// content is refused.
/// <para>
/// Phase 5 (docs/15 section 6) changes no column: the verdict of a debate keeps the debate in <c>run_id</c>, the judge's
/// task in <c>task_id</c> and <c>Debate</c> in <c>scope</c>, and its resolution goes in <c>payload_json</c>, as the last
/// member and only when there is one, so the text of every other verdict, and of those stored before, does not change.
/// <c>debate_id</c>, which points at the unused debate tables of 0001, stays NULL (D-02).
/// </para>
/// </summary>
public sealed class SqliteVerdictStore(ProjectDatabases databases) : IVerdictStore
{
    private const string Columns = """
        id, project_id, debate_id, decision, summary, confidence_level, confidence_justification, requires_human,
        judge_agent_id, judge_provider_id, judge_vendor_id, judge_model, requirement_set_hash, decision_log_head_hash,
        payload_json, issued_at, run_id, task_id, scope, valid, validation_json, supersedes_verdict_id
        """;

    /// <returns>The id the verdict is stored under: its own, or a new one when it has none.</returns>
    public async Task<string> AppendAsync(string projectId, StoredVerdict verdict, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(verdict.Verdict);
        var id = string.IsNullOrWhiteSpace(verdict.Id) ? Guid.NewGuid().ToString("N") : verdict.Id;
        var v = verdict.Verdict;
        var payload = PersistenceJson.Serialize(VerdictJson.From(v));
        var validation = PersistenceJson.Serialize((verdict.Issues ?? []).Select(ViolationJson.From).ToList());

        await using var connection = databases.Connect(projectId);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var select = Command(connection, "SELECT run_id, task_id, scope, valid, validation_json, supersedes_verdict_id, payload_json FROM verdicts WHERE id = $id;", transaction))
        {
            select.Parameters.AddWithValue("$id", id);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var same = NullableString(reader, 0) == verdict.RunId
                    && NullableString(reader, 1) == verdict.TaskId
                    && NullableString(reader, 2) == verdict.Scope.ToString()
                    && NullableInt32(reader, 3) == (verdict.Valid ? 1 : 0)
                    && NullableString(reader, 4) == validation
                    && NullableString(reader, 5) == verdict.Supersedes
                    && reader.GetString(6) == payload;
                return same
                    ? id
                    : throw new InvalidOperationException($"Verdict '{id}' is already stored with other content; a verdict is never rewritten.");
            }
        }

        await using (var insert = Command(connection, $"""
            INSERT INTO verdicts({Columns})
            VALUES ($id, $project, NULL, $decision, $summary, $confidence, $justification, $requiresHuman,
                    $judgeAgent, $judgeProvider, $judgeVendor, $judgeModel, $requirements, $head,
                    $payload, $issuedAt, $run, $task, $scope, $valid, $validation, $supersedes);
            """, transaction))
        {
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$project", projectId);
            insert.Parameters.AddWithValue("$decision", v.Decision.ToString());
            insert.Parameters.AddWithValue("$summary", v.Summary ?? string.Empty);
            insert.Parameters.AddWithValue("$confidence", v.Confidence?.Level.ToString() ?? string.Empty);
            insert.Parameters.AddWithValue("$justification", v.Confidence?.Justification ?? string.Empty);
            insert.Parameters.AddWithValue("$requiresHuman", v.RequiresHumanIntervention ? 1 : 0);
            insert.Parameters.AddWithValue("$judgeAgent", v.Judge?.Agent.Value ?? string.Empty);
            insert.Parameters.AddWithValue("$judgeProvider", v.Judge?.Model?.Provider.Value ?? string.Empty);
            insert.Parameters.AddWithValue("$judgeVendor", v.Judge?.Model?.Vendor.Value ?? string.Empty);
            insert.Parameters.AddWithValue("$judgeModel", OrNull(v.Judge?.Model?.Model));
            insert.Parameters.AddWithValue("$requirements", v.RequirementSetHash ?? string.Empty);
            insert.Parameters.AddWithValue("$head", v.DecisionLogHeadHash ?? string.Empty);
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$issuedAt", Time(v.IssuedAt));
            insert.Parameters.AddWithValue("$run", OrNull(verdict.RunId));
            insert.Parameters.AddWithValue("$task", OrNull(verdict.TaskId));
            insert.Parameters.AddWithValue("$scope", verdict.Scope.ToString());
            insert.Parameters.AddWithValue("$valid", verdict.Valid ? 1 : 0);
            insert.Parameters.AddWithValue("$validation", validation);
            insert.Parameters.AddWithValue("$supersedes", OrNull(verdict.Supersedes));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task<IReadOnlyList<StoredVerdict>> ListAsync(string projectId, string? runId, CancellationToken cancellationToken)
    {
        await using var connection = databases.Connect(projectId);
        var filter = runId is null ? string.Empty : " AND run_id = $run";
        await using var select = Command(connection, $"""
            SELECT id, payload_json, run_id, task_id, scope, valid, validation_json, supersedes_verdict_id
            FROM verdicts
            WHERE project_id = $project{filter}
            ORDER BY rowid;
            """);
        select.Parameters.AddWithValue("$project", projectId);
        select.Parameters.AddWithValue("$run", OrNull(runId));

        var verdicts = new List<StoredVerdict>();
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            verdicts.Add(Read(reader));
        }

        return verdicts;
    }

    private static StoredVerdict Read(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var what = $"Verdict '{id}'";
        var verdict = PersistenceJson.Deserialize<VerdictJson>(reader.GetString(1), what).ToDomain();

        var scopeText = NullableString(reader, 4);
        if (scopeText is null || !TryParseEnum<AuditScope>(scopeText, out var scope))
        {
            throw new InvalidDataException($"{what} has no valid audit scope ('{scopeText}'): it was not stored by this store.");
        }

        var valid = NullableInt32(reader, 5) ?? throw new InvalidDataException($"{what} does not say whether it is valid.");
        var issues = PersistenceJson.Deserialize<List<ViolationJson>>(NullableString(reader, 6) ?? "[]", $"{what}: validation");
        return new StoredVerdict(
            id,
            verdict,
            NullableString(reader, 2),
            NullableString(reader, 3),
            scope,
            valid == 1,
            [.. issues.Select(i => i.ToDomain())],
            NullableString(reader, 7));
    }
}
