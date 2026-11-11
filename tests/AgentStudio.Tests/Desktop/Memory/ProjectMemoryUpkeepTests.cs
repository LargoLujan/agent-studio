using System.IO;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Memory;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Desktop.Memory;

/// <summary>
/// The upkeep of the memory (docs/14 D-18 and D-22): after a run it derives from the log and then exports; when a project
/// is opened it derives only if the project already has its storage, so opening a folder never creates <c>.agentstudio/</c>.
/// </summary>
public sealed class ProjectMemoryUpkeepTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 21, 0, 0, TimeSpan.FromHours(2));

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static MemoryEntry Entry(string id) =>
        new(id, MemoryKind.Change, "Integración", "Commit abc", MemoryOrigin.System, "registro:r1#" + id.Length, At, new string('a', 64), MemoryTrust.Trusted, null, null);

    [Fact]
    public async Task After_a_run_it_derives_then_exports_and_says_both()
    {
        var workspace = new FakeProjectWorkspace();
        var derivation = new FakeMemoryDerivation(new MemoryDerivationResult([Entry("m1"), Entry("m22")], ["registro:r1#3"], []));
        var exporter = new FakeMemoryExporter();
        var upkeep = new ProjectMemoryUpkeep(workspace, derivation, exporter);

        var line = await upkeep.AfterRunAsync(@"C:\proyectos\calc", TestContext.Current.CancellationToken);

        Assert.Equal([@"C:\proyectos\calc"], workspace.Opened);
        Assert.Single(derivation.Derived);
        Assert.Single(exporter.Exports);
        Assert.Equal("Memoria: 2 entradas nuevas derivadas del registro; exportada a .agentstudio/memory/ (2 ficheros, 7 entradas).", line);
    }

    [Fact]
    public async Task A_derivation_that_was_refused_is_not_exported_and_says_why()
    {
        var derivation = new FakeMemoryDerivation(MemoryDerivationResult.NotDerived("hay una ejecución o una planificación en curso"));
        var exporter = new FakeMemoryExporter();
        var upkeep = new ProjectMemoryUpkeep(new FakeProjectWorkspace(), derivation, exporter);

        var line = await upkeep.AfterRunAsync(@"C:\proyectos\calc", TestContext.Current.CancellationToken);

        Assert.Empty(exporter.Exports);
        Assert.Equal("Memoria sin derivar: hay una ejecución o una planificación en curso", line);
    }

    [Fact]
    public async Task An_export_that_was_refused_says_why_and_agent_texts_left_out_are_counted()
    {
        var derivation = new FakeMemoryDerivation(new MemoryDerivationResult([], [], ["El resumen del plan no cuadra con su evidencia."]));
        var exporter = new FakeMemoryExporter(MemoryExportResult.Refused("hay una ejecución en curso"));
        var upkeep = new ProjectMemoryUpkeep(new FakeProjectWorkspace(), derivation, exporter);

        var line = await upkeep.AfterRunAsync(@"C:\proyectos\calc", TestContext.Current.CancellationToken);

        Assert.Equal("Memoria: 1 texto sin guardar en la memoria: no cuadra con el registro o es un día de la bitácora que no se pudo leer; sin exportar: hay una ejecución en curso.", line);
    }

    [Fact]
    public async Task Opening_a_folder_without_storage_derives_nothing_and_creates_nothing()
    {
        var root = _temp.CreateDirectory("nuevo");
        var workspace = new FakeProjectWorkspace();
        var derivation = new FakeMemoryDerivation(new MemoryDerivationResult([Entry("m1")], [], []));
        var upkeep = new ProjectMemoryUpkeep(workspace, derivation, new FakeMemoryExporter());

        var line = await upkeep.OnProjectOpenedAsync(root, TestContext.Current.CancellationToken);

        Assert.Null(line);
        Assert.Empty(workspace.Opened);
        Assert.Empty(derivation.Derived);
        Assert.False(Directory.Exists(Path.Combine(root, ".agentstudio")));
    }

    [Fact]
    public async Task Opening_a_project_with_storage_derives_and_does_not_export()
    {
        var root = _temp.CreateDirectory("calc");
        Directory.CreateDirectory(Path.Combine(root, ".agentstudio"));
        await File.WriteAllTextAsync(Path.Combine(root, ".agentstudio", "agentstudio.db"), string.Empty, TestContext.Current.CancellationToken);
        var derivation = new FakeMemoryDerivation(new MemoryDerivationResult([Entry("m1")], [], []));
        var exporter = new FakeMemoryExporter();
        var upkeep = new ProjectMemoryUpkeep(new FakeProjectWorkspace(), derivation, exporter);

        var line = await upkeep.OnProjectOpenedAsync(root, TestContext.Current.CancellationToken);

        Assert.Equal("Memoria: 1 entrada nueva derivada del registro.", line);
        Assert.Single(derivation.Derived);
        Assert.Empty(exporter.Exports);
    }

    [Fact]
    public async Task Without_a_derivation_nothing_is_opened()
    {
        var workspace = new FakeProjectWorkspace();
        var upkeep = new ProjectMemoryUpkeep(workspace, null, new FakeMemoryExporter());

        Assert.False(upkeep.IsAvailable);
        Assert.Null(await upkeep.AfterRunAsync(@"C:\proyectos\calc", TestContext.Current.CancellationToken));
        Assert.Empty(workspace.Opened);
    }
}

/// <summary>Answers the derivation the test sets and records the projects it was asked for.</summary>
internal sealed class FakeMemoryDerivation(MemoryDerivationResult result) : IMemoryDerivation
{
    public List<ProjectContext> Derived { get; } = [];

    public MemoryDerivationResult Result { get; set; } = result;

    public Task<MemoryDerivationResult> DeriveAsync(ProjectContext project, CancellationToken ct)
    {
        Derived.Add(project);
        return Task.FromResult(Result);
    }
}
