using System.IO;
using System.IO.Pipelines;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Skills;
using AgentStudio.SkillsMcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.SkillsMcp;

/// <summary>A small catalog and its snapshot, as the service would write it for one run.</summary>
internal static class SkillsMcpSamples
{
    public const string ProjectId = "6f1c2b0e-0000-4000-8000-0000000000cc";

    public const string Sha = "8a1541c4a3ffa5a20a5a91de0dcf3f0bab1d1ef4";

    /// <summary>A path and a source that must never reach the answers of the server.</summary>
    public const string LocalRoot = @"C:\skills-index\domains";

    public static IReadOnlyList<AgentId> Roster { get; } = [Aid("orquestador"), Aid("ejecutor"), Aid("juez")];

    public static CatalogSkill Skill(string name, string summary, SkillSector sector, string[] tags, bool linked = false) =>
        new(
            new SkillEntry(
                new SkillId(name),
                name,
                summary,
                sector,
                tags,
                new SkillSource("owner/private-repo", "skills/" + name, "main", Sha, "MIT", linked ? SkillDistribution.Linked : SkillDistribution.Vendored)),
            linked ? null : Path.Combine(LocalRoot, sector.Id, "skills", name));

    public static IReadOnlyList<CatalogSkill> Catalog { get; } =
    [
        Skill("spark-optimization", "Optimiza trabajos de Spark y tablas Delta.", SkillSectors.IngenieriaDatos, ["spark", "delta", "ingenieria-datos"]),
        Skill("sql-optimization", "Ajusta consultas SQL lentas.", SkillSectors.IngenieriaDatos, ["sql", "ingenieria-datos"]),
        Skill("wpf-mvvm", "Patrones MVVM para aplicaciones de escritorio en WPF.", SkillSectors.Escritorio, ["wpf", "dotnet", "escritorio"]),
        Skill("test-driven-development", "Escribe la prueba primero y mírala fallar.", SkillSectors.IngenieriaSoftware, ["tdd", "ingenieria-software"]),
        Skill("design-tokens", "Tokens de diseño y contraste accesible.", SkillSectors.UxUi, ["accessibility", "design-system", "ux-ui"], linked: true),
    ];

    public static SkillCatalogSnapshot Snapshot(IReadOnlyList<SkillActivation>? activations = null) =>
        SkillCatalogSnapshotBuilder.Build(
            T0,
            ProjectId,
            Catalog,
            activations ?? [new SkillActivation(new SkillId("wpf-mvvm"), SkillScopeKind.Agent, "ejecutor", true)],
            Roster,
            new Dictionary<SkillId, SkillHealthStatus> { [new SkillId("spark-optimization")] = SkillHealthStatus.Healthy });
}

/// <summary>
/// The server and an MCP client over two in-memory pipes: no process, no socket and no file. Disposing it closes the
/// client, completes both pipes and waits for the server to stop.
/// </summary>
internal sealed class InMemoryMcp : IAsyncDisposable
{
    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();
    private readonly CancellationTokenSource _stop = new();
    private McpServer? _server;
    private Task? _running;

    public McpClient Client { get; private set; } = null!;

    public static async Task<InMemoryMcp> StartAsync(SkillCatalogSnapshot snapshot, CancellationToken cancellationToken)
    {
        var session = new InMemoryMcp();
        var options = SkillsMcpServer.Options(snapshot);
        var transport = new StreamServerTransport(session._toServer.Reader.AsStream(), session._toClient.Writer.AsStream(), SkillsMcpServer.ServerName);
        session._server = McpServer.Create(transport, options);
        session._running = session._server.RunAsync(session._stop.Token);
        session.Client = await McpClient.CreateAsync(
            new StreamClientTransport(session._toServer.Writer.AsStream(), session._toClient.Reader.AsStream()),
            cancellationToken: cancellationToken);
        return session;
    }

    public async Task<CallToolResult> CallAsync(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken) =>
        await Client.CallToolAsync(tool, arguments, cancellationToken: cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _stop.CancelAsync();
        await _toServer.Writer.CompleteAsync();
        await _toClient.Writer.CompleteAsync();
        if (_running is not null)
        {
            try
            {
                await _running.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
                // The server stops by cancellation.
            }
        }

        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        _stop.Dispose();
    }
}
