using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using AgentStudio.Application.Knowledge;

namespace AgentStudio.Tests.Memory.Store;

/// <summary>
/// What recording and recalling memory costs as the decision log grows: every entry recorded is announced in the log, so
/// recording 10 000 entries extends a log of 10 000 entries. With the log read and verified whole on each write, the cost
/// was quadratic (2 000 entries in 144 s); verified incrementally, each write costs what it adds. The report gives the time
/// of each tenth of the entries (a flat line is linear) and of a recall at the end. Skipped unless
/// <see cref="MeasureVariable"/> is 1 and the build is optimised (<c>-c Release</c>); <see cref="CountVariable"/> sets how
/// many entries (2 000 by default), and the report goes to the test output and, when <see cref="ReportVariable"/> names a
/// file, is appended to it.
/// </summary>
[Trait("Category", "Measurement")]
public sealed class MemoryRecordingMeasurementTests
{
    public const string MeasureVariable = "AGENTSTUDIO_MEMORY_MEASURE";

    public const string CountVariable = "AGENTSTUDIO_MEMORY_MEASURE_COUNT";

    public const string ReportVariable = "AGENTSTUDIO_MEMORY_MEASURE_REPORT";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Recording_and_recalling_memory_cost_what_they_add_not_the_whole_log()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(MeasureVariable) == "1", $"Set {MeasureVariable}=1 to measure the recording of memory.");
        Assert.SkipWhen(
            typeof(AgentStudio.Memory.Store.ProjectMemory).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled == true,
            "Measure an optimised build: dotnet test -c Release.");
        var count = int.TryParse(Environment.GetEnvironmentVariable(CountVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var asked) && asked > 0 ? asked : 2000;
        var tenth = Math.Max(1, count / 10);

        using var world = await MemoryWorld.CreateAsync();
        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Grabar {count} entradas de memoria ({DateTimeOffset.Now:yyyy-MM-dd HH:mm}, {Environment.ProcessorCount} procesadores):");
        var laps = new List<TimeSpan>();
        var total = Stopwatch.StartNew();
        var lap = Stopwatch.StartNew();
        for (var i = 1; i <= count; i++)
        {
            await world.Memory.RecordAsync(
                world.Project,
                new MemoryDraft(MemoryKind.Decision, $"Decisión {i}", $"La persona aprobó el plan {i} de la ejecución r-{i % 37} con su revisión y sus pruebas.", MemoryOrigin.System, $"registro:r-{i % 37}#{i}"),
                Ct);
            if (i % tenth == 0)
            {
                laps.Add(lap.Elapsed);
                report.AppendLine(CultureInfo.InvariantCulture, $"- hasta {i}: {lap.Elapsed.TotalMilliseconds / tenth:F2} ms por entrada");
                lap.Restart();
            }
        }

        total.Stop();
        const int Loads = 20;
        var loads = Stopwatch.StartNew();
        for (var i = 0; i < Loads; i++)
        {
            Assert.True((await world.Instance.Recorder.LoadAsync(world.Project.ProjectId, Ct)).Verification.IsValid);
        }

        loads.Stop();
        var load = loads.Elapsed / Loads;
        var recall = Stopwatch.StartNew();
        var found = await world.Memory.RecallAsync(world.Project, new MemoryQuery("plan 1234 revisión", MaxEntries: 10), Ct);
        recall.Stop();
        report
            .AppendLine(CultureInfo.InvariantCulture, $"Total: {total.Elapsed.TotalSeconds:F1} s ({total.Elapsed.TotalMilliseconds / count:F2} ms por entrada); registro con {(await world.LogAsync()).Count} entradas.")
            .AppendLine(CultureInfo.InvariantCulture, $"Cargar y verificar el registro al final: {load.TotalMilliseconds:F2} ms de media en {Loads} cargas.")
            .AppendLine(CultureInfo.InvariantCulture, $"Una búsqueda al final: {recall.Elapsed.TotalMilliseconds:F0} ms ({found.Count} resultados).");
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable(ReportVariable) is { Length: > 0 } file)
        {
            await File.AppendAllTextAsync(file, report.ToString() + Environment.NewLine, Ct);
        }

        // Generous on purpose, so a busy machine does not fail it. Linear: the last tenth costs about what the first did
        // (each write is mostly the durable writes of the anchor and of SQLite), never three times as much. And a load of
        // the log verifies what it grew: read and verified whole, 2 000 entries took some 45 ms and 10 000 a quarter of a
        // second, where reading the tail takes a few.
        Assert.True(laps[^1] < laps[0] * 3 + TimeSpan.FromSeconds(1), report.ToString());
        Assert.True(load < TimeSpan.FromMilliseconds(25), report.ToString());
        Assert.True(total.Elapsed < TimeSpan.FromMilliseconds(100.0 * count), report.ToString());
    }
}
