using System.IO;
using System.Security.Cryptography;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Tests.Domain.Orchestration;
using AgentStudio.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// A throwaway project folder (with a space and an accent in its path) that is deleted at the end, read-only files
/// included. Nothing here is a git repository unless a test makes it one.
/// </summary>
internal sealed class TempProjectFolder : IDisposable
{
    private readonly string _container;

    public TempProjectFolder()
    {
        _container = Path.Combine(Path.GetTempPath(), "as persistencia " + Guid.NewGuid().ToString("N")[..12]);
        Root = Path.Combine(_container, "proyecto ñ");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string DataDirectory => Path.Combine(Root, ".agentstudio");

    public string DatabasePath => Path.Combine(DataDirectory, "agentstudio.db");

    public string EvidenceDirectory => Path.Combine(DataDirectory, "evidence");

    public void Dispose() => ForceDelete(_container);

    /// <summary>Best effort, never through a link (see <see cref="TempFolders"/>).</summary>
    internal static void ForceDelete(string directory) => TempFolders.Delete(directory);
}

/// <summary>
/// One "instance of the app" over a project folder: its own registry, workspace, stores and recorder, as the
/// composition root builds them. Disposing it is closing the app: locks and database handles are released, so a new
/// instance over the same folder sees exactly what was persisted and nothing else.
/// </summary>
internal sealed class ProjectInstance : IDisposable
{
    private ProjectInstance(string root, FakeTimeProvider clock, string? anchorFolder)
    {
        Root = root;
        Clock = clock;
        Databases = new ProjectDatabases();
        Workspace = new ProjectWorkspace(Databases, clock);
        DecisionLog = new SqliteDecisionLogStore(Databases);
        Evidence = new SqliteEvidenceStore(Databases, clock);
        Requirements = new SqliteRequirementStore(Databases);
        Plans = new SqlitePlanStore(Databases, clock);
        Runs = new SqliteRunRegistry(Databases);
        Verdicts = new SqliteVerdictStore(Databases);
        Roster = new SqliteAgentRoster(Databases, clock);
        Recorder = new DecisionRecorder(DecisionLog, clock, anchorFolder is null ? null : new FileDecisionLogAnchorStore(new DecisionLogAnchorOptions(anchorFolder), Databases));
    }

    public string Root { get; }

    public FakeTimeProvider Clock { get; }

    public ProjectDatabases Databases { get; }

    public ProjectWorkspace Workspace { get; }

    public SqliteDecisionLogStore DecisionLog { get; }

    public SqliteEvidenceStore Evidence { get; }

    public SqliteRequirementStore Requirements { get; }

    public SqlitePlanStore Plans { get; }

    public SqliteRunRegistry Runs { get; }

    public SqliteVerdictStore Verdicts { get; }

    public SqliteAgentRoster Roster { get; }

    public DecisionRecorder Recorder { get; }

    public ProjectContext Project { get; private set; } = null!;

    public string ProjectId => Project.ProjectId;

    /// <param name="anchorFolder">Where the recorder anchors the head of the log (outside the project), or null for no anchor.</param>
    public static async Task<ProjectInstance> OpenAsync(string root, FakeTimeProvider? clock = null, string? anchorFolder = null)
    {
        var instance = new ProjectInstance(root, clock ?? new FakeTimeProvider(T0), anchorFolder);
        try
        {
            instance.Project = await instance.Workspace.OpenAsync(root, TestContext.Current.CancellationToken);
            return instance;
        }
        catch
        {
            instance.Dispose();
            throw;
        }
    }

    /// <summary>A connection of the test's own to the project database, for looking behind the stores' back.</summary>
    public SqliteConnection Connect() => SqliteSchema.Open(new SqliteConnectionStringBuilder
    {
        DataSource = Databases.Get(ProjectId).DatabasePath,
        Pooling = false,
    }.ToString());

    public void Dispose() => Databases.Dispose();
}

internal static class PersistenceTestData
{
    public static readonly VerificationProfile Profile = new(
        "dotnet",
        [
            new VerificationStep("build", "Compilar", "dotnet", ["build", "--no-restore"], EvidenceKind.BuildLog, TestReportFormat.None, null, TimeSpan.FromMinutes(10)),
            new VerificationStep(
                "test",
                "Tests",
                "dotnet",
                ["test", "--no-build", "--", "--report-xunit-trx", "--report-xunit-trx-filename", "r.trx"],
                EvidenceKind.TestRun,
                TestReportFormat.Trx,
                "TestResults/r.trx",
                TimeSpan.FromMinutes(20)),
        ]);

    /// <summary>The fixture roster plus an agent that uses every member of a definition.</summary>
    public static IReadOnlyList<AgentDefinition> FullRoster() =>
    [
        .. PlanFixtures.Roster(),
        new AgentDefinition(
            Aid("ejecutor-completo"),
            "Ejecutor «completo»",
            AgentRole.Executor,
            new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "claude-opus"),
            "Instrucciones del sistema\ncon dos líneas",
            ["objetivo 1", "objetivo 2"],
            ["Read", "Edit"],
            [new PathGrant("src/", FileAccessLevel.ReadWrite), new PathGrant("docs/", FileAccessLevel.Read)],
            new ExecutionBudget(40, TimeSpan.FromMinutes(20.5), MaxTotalTokens: 500_000, MaxCostUsd: 1.25m),
            2,
            MemoryPolicy.ReadSelected,
            new ReviewPolicy(true, true)),
    ];

    /// <summary>Stores what a run header points at (plan, requirements and the evidence of the request) and builds the header.</summary>
    public static async Task<RunRecord> SeedRunAsync(ProjectInstance instance, string id = "20261015-a1b2c3", RunId? follows = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = PlanFixtures.Plan();
        var requirements = PlanFixtures.Requirements();
        await instance.Plans.SaveAsync(instance.ProjectId, plan, ct);
        await instance.Requirements.SaveAsync(instance.ProjectId, requirements, ct);
        using var request = new MemoryStream(Bytes("Añade Calc.Multiply(a, b) con sus tests."));
        var evidence = await instance.Evidence.CaptureAsync(
            instance.ProjectId,
            new EvidenceDraft(EvidenceKind.Document, EvidenceOrigin.ProvidedByHuman, "Petición original", "manuel", RunId: id),
            request,
            ct);
        return new RunRecord(
            new RunId(id),
            instance.ProjectId,
            T0.AddTicks(9_876_543),
            plan.ContentHash,
            requirements.ContentHash,
            evidence.Id,
            "0123456789abcdef0123456789abcdef01234567",
            "main",
            Profile,
            FullRoster(),
            new RunOptions(MaxParallelTasks: 2, StallThreshold: TimeSpan.FromMinutes(7), MaxAutoWaitForQuota: TimeSpan.FromSeconds(90.5)),
            follows);
    }

    /// <summary>A structural fingerprint of a value (every public member, recursively) to compare records that hold lists.</summary>
    public static string Shape(object? value) => System.Text.Json.JsonSerializer.Serialize(value);

    public static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    public static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string? Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    public static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
