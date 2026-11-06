using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Semantic;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Views.Memory;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Memory;
using static AgentStudio.Tests.Desktop.Memory.MemorySearchSamples;

namespace AgentStudio.Tests.Desktop.Memory;

/// <summary>
/// «Memoria» in Phase 12 drawn without ever being shown (<see cref="OffscreenRender"/>; docs/17 D-02 to D-06, F12-6): a
/// search by meaning and by words with the origin and the trust of each result and the panel of the journal; the model
/// missing, with the confirmation of its download open; and the download going, with its progress and its cancel button.
/// Synthetic data only, no window, no network. With <c>AGENTSTUDIO_RENDER_EVIDENCE</c> set, the pictures are saved.
/// </summary>
public sealed class MemorySearchRenderTests
{
    private static readonly Color Surface = Color.FromRgb(0x10, 0x1A, 0x30);
    private static readonly Color Raised = Color.FromRgb(0x16, 0x22, 0x3D);
    private static readonly Color Warning = Color.FromRgb(0xF5, 0xB5, 0x44);
    private static readonly Color Success = Color.FromRgb(0x2F, 0xD0, 0x8A);

    private const string Root = @"C:\proyectos\calc";

    private static readonly DateOnly Tuesday = new(2026, 10, 27);
    private static readonly DateOnly Wednesday = new(2026, 10, 28);

    private static MemoryViewModel Create(FakeSemanticIndex index, FakeJournalTrust trust, FakeModelInstaller installer, params MemoryEntry[] entries)
    {
        var store = new FakeProjectMemory();
        store.Entries.AddRange(entries);
        var summaries = new FakeDailySummaryService(day => new DailySummaryResult(day, false, new string('a', 64), "## Prompts\n- 4 de user (claude-code/sonnet)\n\n## Hecho\n- «Hecho: búsqueda por significado en «Memoria»»", 4, 0, null));
        return new MemoryViewModel(
            new FakeProjectWorkspace(),
            store,
            new FakeMemoryExporter(),
            new FakeRunActivity(),
            new DailySummaryViewModel(summaries, new FakeClock(At)),
            new FakeClock(At),
            new MemorySearchBackend(index, trust, installer));
    }

    [Fact]
    public async Task A_search_by_meaning_and_by_words_renders_each_result_with_its_origin_and_trust_and_the_journal_days()
    {
        var trusted = Entry("m-41", MemoryTrust.Trusted, content: "Plan aprobado: añadir Calc.Multiply con pruebas unitarias (3 tareas).");
        var proposed = Entry("m-42", MemoryTrust.Proposed, MemoryKind.KnownError, MemoryOrigin.Agent, "CalcTests.Divide_by_zero falla en la verificación aislada.");
        var journal = JournalEntry("j-7", Wednesday, MemoryTrust.Quarantined, content: "- **04:22:31** · sesión `eb1c6494` · #prompt · «las pruebas de Calc se quedan en rojo al dividir entre cero \\(intento 1\\)»");
        var index = new FakeSemanticIndex
        {
            Status = Ready(1500, 1500),
            Answer = _ => new SemanticSearchResult(
            [
                Hit(proposed, 1, 1),
                Hit(journal, null, 2),
                LogHit(57, "registro · veredicto", "Veredicto del juez: R1 cumplido; R2 incumplido: CalcTests.Divide_by_zero sigue en rojo.", 2, 3),
                Hit(trusted, 3, null),
            ],
            SemanticSearchMode.Hybrid),
        };
        var trust = new FakeJournalTrust { State = new JournalTrustState(false, [Tuesday], 3) };
        var memory = await WpfHost.RunAsync(async () =>
        {
            var model = Create(index, trust, new FakeModelInstaller(), trusted, proposed, journal, JournalEntry("j-8", Wednesday, MemoryTrust.Quarantined), JournalEntry("j-9", Wednesday, MemoryTrust.Quarantined));
            await model.SetProjectAsync(Root);
            model.SearchText = "test en rojo";
            await model.SearchCommand.ExecuteAsync(null);
            return model;
        });

        var (drawn, cards, warning, success, label, matches, dayButtons, projectButton, trustEnd, acceptStart, journalText, rawMarks) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new MemoryView { DataContext = memory };

            var bitmap = OffscreenRender.Render(view, 1500, 1000);
            OffscreenRender.SaveIfRequested(bitmap, "fase12-memoria-busqueda-hibrida.png");
            var texts = Descendants<TextBlock>(view).ToList();
            var buttons = Descendants<Button>(view).ToList();
            var quarantined = texts.Single(t => t.Text.StartsWith("En cuarentena: no se puede comprobar quién la escribió", StringComparison.Ordinal));
            return (
                OffscreenRender.CountUnlike(bitmap, Surface),
                OffscreenRender.Count(bitmap, Raised),
                OffscreenRender.Count(bitmap, Warning),
                OffscreenRender.Count(bitmap, Success),
                texts.Single(t => t.Text == "Buscar por significado y por palabras").ActualWidth,
                texts.Count(t => t.Text.StartsWith("Coincide por", StringComparison.Ordinal) && t.ActualHeight > 0),
                buttons.Count(b => b.Content as string == "Confiar en este día" && b.ActualWidth > 0),
                buttons.Single(b => b.Content as string == "Confiar en la bitácora de este proyecto").ActualWidth,
                quarantined.TransformToAncestor(view).Transform(new Point(quarantined.ActualWidth, 0)).X,
                buttons.Where(b => b.Content as string == "Aceptar").Min(b => b.TransformToAncestor(view).Transform(new Point(0, 0)).X),
                texts.Single(t => t.Text.Contains("las pruebas de Calc", StringComparison.Ordinal)).Text,
                texts.Count(t => t.Text.Contains("**", StringComparison.Ordinal) || t.Text.Contains("\\(", StringComparison.Ordinal)));
        });

        Assert.Equal(4, memory.Results.Count);
        Assert.Equal("Por significado y por palabras: 4 resultados, del más cercano al menos.", memory.SearchNotice);
        Assert.True(label > 0, "The label of the search by meaning was not laid out.");
        Assert.Equal(4, matches);
        Assert.Equal(1, dayButtons);
        Assert.True(projectButton > 0, "«Confiar en la bitácora de este proyecto» was not laid out.");
        Assert.True(trustEnd <= acceptStart, $"The trust of the journal entry ends at {trustEnd} and runs under «Aceptar», which starts at {acceptStart}.");
        Assert.Equal("04:22:31 · sesión eb1c6494 · #prompt · «las pruebas de Calc se quedan en rojo al dividir entre cero (intento 1)»", journalText); // V41
        Assert.Equal(0, rawMarks);
        Assert.True(drawn > 50_000, $"Only {drawn} pixels differ from the panel: the memory was not drawn.");
        Assert.True(cards > 20_000, $"Only {cards} pixels have the card colour: the results were not drawn.");
        Assert.True(warning > 100, $"Only {warning} pixels have the warning colour (what waits for you).");
        Assert.True(success > 20, $"Only {success} pixels have the success colour (the trusted results and the model).");
    }

    [Fact]
    public async Task Without_the_model_the_search_says_it_went_by_words_and_the_download_asks_for_confirmation()
    {
        var index = new FakeSemanticIndex
        {
            Status = Missing(),
            Answer = _ => new SemanticSearchResult([Hit(Entry("m-41", MemoryTrust.Trusted), 1, null)], SemanticSearchMode.WordsOnly, WordsOnlyReason.ModelMissing),
        };
        var installer = new FakeModelInstaller();
        var memory = await WpfHost.RunAsync(async () =>
        {
            var model = Create(index, new FakeJournalTrust { State = new JournalTrustState(false, [], 0) }, installer, Entry("m-41", MemoryTrust.Trusted));
            await model.SetProjectAsync(Root);
            model.SearchText = "pruebas";
            await model.SearchCommand.ExecuteAsync(null);
            model.Model.AskDownloadCommand.Execute(null);
            return model;
        });

        var (confirm, decline, files, notice, label) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new MemoryView { DataContext = memory };

            var bitmap = OffscreenRender.Render(view, 1500, 1100);
            OffscreenRender.SaveIfRequested(bitmap, "fase12-memoria-confirmar-descarga.png");
            var texts = Descendants<TextBlock>(view).ToList();
            var buttons = Descendants<Button>(view).ToList();
            return (
                buttons.Single(b => b.Content as string == "Descargar 123,4 MB").ActualWidth,
                buttons.Single(b => b.Content as string == "No descargar").ActualWidth,
                texts.Count(t => t.Text.StartsWith("https://huggingface.co/intfloat/multilingual-e5-small/resolve/", StringComparison.Ordinal) && t.ActualHeight > 0),
                texts.Single(t => t.Text == "Solo por palabras: el modelo de búsqueda por significado no está descargado.").ActualWidth,
                texts.Single(t => t.Text == "Buscar por palabras").ActualWidth);
        });

        Assert.True(memory.Model.IsConfirming);
        Assert.Empty(installer.Consents);
        Assert.True(confirm > 0, "«Descargar 123,4 MB» was not laid out.");
        Assert.True(decline > 0, "«No descargar» was not laid out.");
        Assert.Equal(2, files);
        Assert.True(notice > 0, "The search does not say it went by words only.");
        Assert.True(label > 0, "The label of the search by words was not laid out.");
    }

    [Fact]
    public async Task The_download_renders_its_progress_and_its_cancel_button_and_cancelling_leaves_nothing()
    {
        var installer = new FakeModelInstaller();
        var gate = new TaskCompletionSource<EmbeddingModelStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        installer.Install = async (progress, ct) =>
        {
            progress!.Report(new EmbeddingModelDownloadProgress("model_qint8_avx512_vnni.onnx", 59_173_412, 118_346_824));
            using var cancelled = ct.Register(() => gate.TrySetCanceled(ct));
            return await gate.Task;
        };
        var (memory, download) = await WpfHost.RunAsync(async () =>
        {
            var model = Create(new FakeSemanticIndex { Status = Missing() }, new FakeJournalTrust(), installer);
            await model.SetProjectAsync(Root);
            model.Model.AskDownloadCommand.Execute(null);
            var pending = model.Model.ConfirmDownloadCommand.ExecuteAsync(null);
            return (model, pending);
        });

        var (value, cancel, text) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new MemoryView { DataContext = memory };

            var bitmap = OffscreenRender.Render(view, 1500, 900);
            OffscreenRender.SaveIfRequested(bitmap, "fase12-memoria-descargando.png");
            return (
                Descendants<ProgressBar>(view).Single().Value,
                Descendants<Button>(view).Single(b => b.Content as string == "Cancelar la descarga").ActualWidth,
                Descendants<TextBlock>(view).Single(t => t.Text.StartsWith("Descargando ", StringComparison.Ordinal)).Text);
        });

        Assert.Equal(47.9, value, 1);
        Assert.True(cancel > 0, "«Cancelar la descarga» was not laid out.");
        Assert.Equal("Descargando model_qint8_avx512_vnni.onnx: 59,2 de 118,3 MB · 48 % del total", text);

        await WpfHost.RunAsync(async () =>
        {
            memory.Model.CancelDownloadCommand.Execute(null);
            await download;
            return true;
        });

        Assert.False(memory.Model.IsDownloading);
        Assert.Equal("Descarga cancelada: no se ha guardado nada a medias.", memory.Model.Outcome);
        Assert.Single(installer.Consents);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
