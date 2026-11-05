using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using AgentStudio.Application.Semantic;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Semantic;
using AgentStudio.Infrastructure.Embeddings;
using AgentStudio.Infrastructure.Embeddings.Onnx;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Tests.Infrastructure.Persistence;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Infrastructure.Embeddings;

/// <summary>
/// F12-5 with the pieces of WP2 (docs/17 §9.2): one query in Spanish against an index of 10 000 fragments, with its
/// embedding by the real model, the lexical list (FTS5 and <c>bm25</c> over <see cref="LexicalTerms"/>), the semantic one
/// (cosine with every vector, in memory) and the fusion (<see cref="HybridRanking"/>), under 200 ms at the 95th
/// percentile. Like the spike, the vectors of the index are random (seeded): the cost of the scan does not depend on them.
/// Skipped unless <see cref="MeasureVariable"/> is 1, the model is on disk and the build is optimised (<c>-c Release</c>: in
/// Debug the scan of the vectors is not optimised and the figures mean nothing); the report goes to the test output and,
/// when <see cref="ReportVariable"/> names a file, is appended to it.
/// </summary>
[Trait("Category", "Measurement")]
public sealed class SemanticSearchMeasurementTests
{
    public const string MeasureVariable = "AGENTSTUDIO_SEMANTIC_MEASURE";

    public const string ReportVariable = "AGENTSTUDIO_SEMANTIC_MEASURE_REPORT";

    private const int Fragments = 10_000;
    private const int WarmUp = 30;
    private const int Measured = 300;
    private const int ListLength = 50;

    private static readonly string[] Sentences =
    [
        "Hecho: la verificación aislada pasa tras dar permiso de lectura a la carpeta de trabajo.",
        "Decisión: el juez es siempre de otro vendor que el ejecutor de la tarea.",
        "Error conocido: dotnet test con -nologo no ejecuta ninguna prueba.",
        "Convención: el código y los comentarios en inglés; la interfaz y la documentación en castellano.",
        "El orquestador planifica, los ejecutores trabajan en worktrees y el juez revisa las evidencias.",
        "Ventana de contexto al 87 %: conviene renovar la sesión con un traspaso.",
        "Cuota de cinco horas agotada; la ejecución se reanuda a las 17:20.",
        "Windows Hello pide la huella o el PIN antes de una aprobación crítica.",
        "La memoria es append-only y cada entrada se ancla en el registro de decisiones.",
        "El plan aprobado tiene tres tareas: modelo, almacén y búsqueda híbrida.",
        "El veredicto rechaza la entrega porque falta la prueba de reconstrucción del índice.",
        "El auditor avisa de que una tarea tocó ficheros fuera de su permiso.",
        "Las pruebas de integración fallan en la máquina del usuario por un bloqueo de SQLite.",
        "El tema oscuro cumple el contraste AA en todos los paneles y en la barra de estado.",
        "La bitácora registra cada petición con su resultado y la línea Hecho al cerrarla.",
        "El instalador descarga el modelo una vez, con el OK de la persona, y comprueba su huella.",
        "La entrega a la rama del usuario se aprueba en un diálogo con el resumen de los cambios.",
        "El lienzo del constructor permite deshacer y rehacer los cambios del flujo.",
        "Una condición de carrera en el motor se resolvió con una cola de un solo consumidor.",
        "El panel de consumo muestra los tokens por modelo y la cuota que queda en cada proveedor.",
    ];

    /// <summary>The queries of the measure, as a person writes them; the end-to-end measure of the index asks them too.</summary>
    internal static readonly string[] Queries =
    [
        "¿Por qué fallan las pruebas en la verificación aislada?",
        "¿Quién revisa lo que entregan los ejecutores?",
        "¿Qué pasa cuando se acaba la cuota?",
        "¿Cómo se descarga el modelo de búsqueda?",
        "¿En qué idioma van los comentarios del código?",
        "¿Qué hacer cuando el contexto está casi lleno?",
        "¿Por qué se rechazó la última entrega?",
        "¿Cómo se resolvió la condición de carrera?",
        "¿Qué pide Windows Hello antes de aprobar?",
        "¿Dónde se ve el consumo de tokens?",
        "modo nocturno de la interfaz",
        "copias de trabajo de cada tarea",
        "test en rojo tras el cambio",
        "permisos de la carpeta de trabajo",
        "deshacer en el editor de flujo",
    ];

    [Fact]
    public async Task A_query_in_Spanish_against_ten_thousand_fragments_with_its_embedding()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(MeasureVariable) == "1", $"Set {MeasureVariable}=1 to measure the semantic search (F12-5).");
        Assert.SkipWhen(
            typeof(SemanticIndexStore).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled == true,
            "Measure an optimised build: dotnet test -c Release.");
        var modelFolder = LocalE5Model.Require();
        var ct = TestContext.Current.CancellationToken;
        using var project = new TempProjectFolder();
        var localAppData = Path.Combine(Path.GetTempPath(), "as medida " + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            using var instance = await ProjectInstance.OpenAsync(project.Root);
            using var store = new SemanticIndexStore(instance.Databases, new SemanticIndexOptions(localAppData));
            using var source = new OnnxEmbedderSource(new EmbeddingModelLocation(modelFolder, LocalE5Model.Descriptor));
            var load = Stopwatch.StartNew();
            var embedder = (await source.OpenAsync(ct)).Embedder!;
            var loadTime = load.Elapsed;
            var model = LocalE5Model.Descriptor.Fingerprint();

            var fill = Stopwatch.StartNew();
            var random = new Random(20261103);
            var characters = 0L;
            for (var start = 0; start < Fragments; start += 500)
            {
                var batch = new List<IndexedFragment>(500);
                for (var i = start; i < start + 500; i++)
                {
                    var text = Text(random, 300 + random.Next(1200));
                    characters += text.Length;
                    var fragment = new SemanticFragment(FragmentSource.Memory($"mem-{i:D5}", ContentHash.Sha256Hex(text)), 0, text, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i));
                    batch.Add(new IndexedFragment(fragment, RandomUnitVector(random, LocalE5Model.Descriptor.Dimensions), model));
                }

                await store.UpsertAsync(instance.Project, batch, ct);
            }

            var fillTime = fill.Elapsed;
            var total = new List<double>();
            var embedding = new List<double>();
            var words = new List<double>();
            var vectors = new List<double>();
            var reads = new List<double>();
            var singleReads = new List<double>();
            for (var i = 0; i < WarmUp + Measured; i++)
            {
                var query = Queries[i % Queries.Length];
                var all = Stopwatch.StartNew();
                var step = Stopwatch.StartNew();
                var vector = (await embedder.EmbedAsync([query], EmbeddingRole.Query, ct))[0];
                var tEmbedding = step.Elapsed.TotalMilliseconds;
                step.Restart();
                var lexical = await store.SearchWordsAsync(instance.Project, LexicalTerms.From(query), ListLength, ct);
                var tWords = step.Elapsed.TotalMilliseconds;
                step.Restart();
                var semantic = await store.SearchVectorsAsync(instance.Project, vector, model, ListLength, ct);
                var tVectors = step.Elapsed.TotalMilliseconds;
                var fused = HybridRanking.Fuse(lexical, [.. semantic.Select(candidate => new RankedCandidate(candidate.Id, candidate.At))]);
                var tTotal = all.Elapsed.TotalMilliseconds;
                Assert.NotEmpty(fused);

                // As many rows as each search reads to leave out one it could not have written, and a single one (what any
                // operation costs: its turn in the queue and its connection), each as a read of its own: not in the total.
                step.Restart();
                _ = await store.ReadIndexedAsync(instance.Project, [.. semantic.Select(c => c.Id).Concat(lexical.Select(c => c.Id)).Distinct().Take(66)], ct);
                var tRead = step.Elapsed.TotalMilliseconds;
                step.Restart();
                _ = await store.ReadIndexedAsync(instance.Project, [semantic[0].Id], ct);
                var tSingleRead = step.Elapsed.TotalMilliseconds;
                if (i >= WarmUp)
                {
                    total.Add(tTotal);
                    embedding.Add(tEmbedding);
                    words.Add(tWords);
                    vectors.Add(tVectors);
                    reads.Add(tRead);
                    singleReads.Add(tSingleRead);
                }
            }

            var report = new StringBuilder()
                .AppendLine(CultureInfo.InvariantCulture, $"Semantic search, F12-5 ({DateTimeOffset.Now:yyyy-MM-dd HH:mm}, {Environment.ProcessorCount} logical processors, .NET {Environment.Version})")
                .AppendLine(CultureInfo.InvariantCulture, $"model loaded in {loadTime.TotalMilliseconds:F0} ms; {Fragments} fragments ({characters / 1_000_000.0:F1} M characters) written in {fillTime.TotalSeconds:F1} s")
                .AppendLine(CultureInfo.InvariantCulture, $"query total: {Summary(total)}")
                .AppendLine(CultureInfo.InvariantCulture, $"  embedding: {Summary(embedding)}")
                .AppendLine(CultureInfo.InvariantCulture, $"  words (FTS5, top {ListLength}): {Summary(words)}")
                .AppendLine(CultureInfo.InvariantCulture, $"  vectors (cosine, top {ListLength}): {Summary(vectors)}")
                .AppendLine(CultureInfo.InvariantCulture, $"  read of up to 66 fragments by id with their vectors, apart (a search checks as many, without vectors): {Summary(reads)}")
                .AppendLine(CultureInfo.InvariantCulture, $"  read of 1 fragment by id, apart (the cost of any operation): {Summary(singleReads)}")
                .ToString();
            TestContext.Current.TestOutputHelper?.WriteLine(report);
            if (Environment.GetEnvironmentVariable(ReportVariable) is { Length: > 0 } reportFile)
            {
                await File.AppendAllTextAsync(reportFile, report, ct);
            }

            Assert.True(Percentile(total, 0.95) < 200, report);
        }
        finally
        {
            TempFolders.Delete(localAppData);
        }
    }

    private static string Text(Random random, int length)
    {
        var text = new StringBuilder(length + 120);
        while (text.Length < length)
        {
            text.Append(Sentences[random.Next(Sentences.Length)]).Append(' ');
        }

        return text.ToString(0, length).Trim();
    }

    private static float[] RandomUnitVector(Random random, int dimensions)
    {
        var vector = new float[dimensions];
        var sum = 0.0;
        for (var d = 0; d < dimensions; d++)
        {
            vector[d] = (float)((random.NextDouble() * 2) - 1);
            sum += vector[d] * (double)vector[d];
        }

        var scale = (float)(1 / Math.Sqrt(sum));
        for (var d = 0; d < dimensions; d++)
        {
            vector[d] *= scale;
        }

        return vector;
    }

    private static double Percentile(List<double> values, double q)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Max(0, (int)Math.Ceiling(q * sorted.Length) - 1)];
    }

    private static string Summary(List<double> ms) => string.Create(
        CultureInfo.InvariantCulture,
        $"p50 {Percentile(ms, 0.50):F1} ms · p95 {Percentile(ms, 0.95):F1} ms · max {ms.Max():F1} ms (n={ms.Count})");
}
