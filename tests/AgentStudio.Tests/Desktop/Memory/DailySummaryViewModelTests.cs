using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Memory;
using AgentStudio.Tests.Memory;

namespace AgentStudio.Tests.Desktop.Memory;

/// <summary>
/// «Resumen del día» (docs/14 F4-9, D-31) over a fake <see cref="IDailySummaryService"/>: the deterministic summary of a
/// local day of the journal; today's is provisional and not kept, a closed day goes to the memory once.
/// </summary>
public sealed class DailySummaryViewModelTests
{
    private static readonly ProjectContext Project = new("p-1", @"C:\proyectos\calc");

    // 2026-10-15 23:30 UTC is already the 16th in the +02:00 local zone of FakeClock.
    private static FakeClock LateOnThe15thInUtc() => new(new DateTimeOffset(2026, 10, 15, 23, 30, 0, TimeSpan.Zero));

    private static MemoryEntry Recorded(string id) =>
        new(id, MemoryKind.DailySummary, "2026-10-15", "Resumen", MemoryOrigin.System, "bitacora/2026-10-15.md · sha256 aaa", DateTimeOffset.UnixEpoch, new string('c', 64), MemoryTrust.Trusted, null, null);

    [Fact]
    public void The_day_on_screen_is_todays_local_day()
    {
        var summary = new DailySummaryViewModel(new FakeDailySummaryService(), LateOnThe15thInUtc());

        Assert.Equal(new DateOnly(2026, 10, 16), summary.Day);
        Assert.Equal("viernes 16/10/2026 (hoy)", summary.DayText);
        Assert.False(summary.NextDayCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_service_is_asked_for_the_chosen_day_of_the_project()
    {
        var service = new FakeDailySummaryService();
        var summary = new DailySummaryViewModel(service, LateOnThe15thInUtc());
        summary.SetProject(Project);

        summary.PreviousDayCommand.Execute(null);
        await summary.SummarizeCommand.ExecuteAsync(null);

        Assert.Equal([(Project, new DateOnly(2026, 10, 15))], service.Asked);
        Assert.Equal("jueves 15/10/2026", summary.DayText);
        Assert.True(summary.NextDayCommand.CanExecute(null));
    }

    [Fact]
    public async Task Todays_summary_is_provisional_and_is_not_kept()
    {
        var service = new FakeDailySummaryService(day => new DailySummaryResult(day, true, new string('a', 64), "## Prompts\n- 12 de user", 12, 2, null));
        var summary = new DailySummaryViewModel(service, LateOnThe15thInUtc());
        summary.SetProject(Project);

        await summary.SummarizeCommand.ExecuteAsync(null);

        Assert.Equal("## Prompts\n- 12 de user", summary.Summary);
        Assert.Equal("Provisional: el día no ha terminado, así que no se guarda en la memoria.", summary.Status);
        Assert.Equal("12 prompts · 2 líneas sin reconocer (solo se cuentan)", summary.Counts);
    }

    [Fact]
    public async Task A_closed_day_goes_to_the_memory_once()
    {
        var service = new FakeDailySummaryService(day => new DailySummaryResult(day, false, new string('a', 64), "Resumen", 1, 0, Recorded("m-7")));
        var summary = new DailySummaryViewModel(service, LateOnThe15thInUtc());
        summary.SetProject(Project);
        summary.PreviousDayCommand.Execute(null);

        await summary.SummarizeCommand.ExecuteAsync(null);
        Assert.Equal("Guardado en la memoria como resumen diario (entrada m-7).", summary.Status);
        Assert.Equal("1 prompt · 0 líneas sin reconocer (solo se cuentan)", summary.Counts);

        service.Result = day => new DailySummaryResult(day, false, new string('a', 64), "Resumen", 1, 0, null);
        await summary.SummarizeCommand.ExecuteAsync(null);
        Assert.Equal("Ya estaba resumido: no se guarda otra vez.", summary.Status);
    }

    [Fact]
    public async Task A_summary_that_fails_is_shown_and_not_thrown()
    {
        var service = new FakeDailySummaryService(_ => throw new System.IO.IOException(@"'.agentstudio\bitacora\2026-10-16.md' is a link."));
        var summary = new DailySummaryViewModel(service, LateOnThe15thInUtc());
        summary.SetProject(Project);

        await summary.SummarizeCommand.ExecuteAsync(null);

        Assert.Equal(@"No se pudo resumir el día: '.agentstudio\bitacora\2026-10-16.md' is a link.", summary.ErrorMessage);
        Assert.Null(summary.Summary);
    }

    [Fact]
    public void Without_a_project_or_without_the_service_nothing_can_be_summarized()
    {
        var withoutProject = new DailySummaryViewModel(new FakeDailySummaryService(), LateOnThe15thInUtc());
        var withoutService = new DailySummaryViewModel(null, LateOnThe15thInUtc());
        withoutService.SetProject(Project);

        Assert.False(withoutProject.SummarizeCommand.CanExecute(null));
        Assert.Equal("Abre una carpeta para resumir su bitácora.", withoutProject.Status);
        Assert.False(withoutService.SummarizeCommand.CanExecute(null));
        Assert.Equal("El resumen diario no está disponible en esta composición.", withoutService.Status);
    }

    [Fact]
    public async Task Changing_the_day_clears_the_summary_of_the_other_day()
    {
        var summary = new DailySummaryViewModel(new FakeDailySummaryService(), LateOnThe15thInUtc());
        summary.SetProject(Project);
        await summary.SummarizeCommand.ExecuteAsync(null);
        Assert.NotNull(summary.Summary);

        summary.PreviousDayCommand.Execute(null);

        Assert.Null(summary.Summary);
        Assert.Null(summary.Counts);
    }

    [Fact]
    public async Task The_empty_box_says_what_to_do_to_fill_it_and_nothing_once_there_is_a_summary()
    {
        var summary = new DailySummaryViewModel(new FakeDailySummaryService(), LateOnThe15thInUtc());
        var changes = new List<string?>();
        summary.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.Equal("Abre una carpeta de proyecto: aquí verás el resumen de cada día de su bitácora.", summary.EmptyHint);

        summary.SetProject(Project);
        Assert.Equal(
            "Pulsa «Resumir el día» para ver aquí el resumen del viernes 16/10/2026 (hoy): los prompts de la bitácora de ese día, cómo acabaron y lo hecho. Cambia de día con «Día anterior» y «Día siguiente».",
            summary.EmptyHint);
        Assert.Contains(nameof(DailySummaryViewModel.EmptyHint), changes);

        await summary.SummarizeCommand.ExecuteAsync(null);
        Assert.Null(summary.EmptyHint);

        changes.Clear();
        summary.PreviousDayCommand.Execute(null);
        Assert.StartsWith("Pulsa «Resumir el día» para ver aquí el resumen del jueves 15/10/2026:", summary.EmptyHint, StringComparison.Ordinal);
        Assert.Contains(nameof(DailySummaryViewModel.EmptyHint), changes);
    }

    [Fact]
    public void Without_the_service_the_empty_box_says_why()
    {
        var summary = new DailySummaryViewModel(null, LateOnThe15thInUtc());
        summary.SetProject(Project);

        Assert.Equal("El resumen diario no está disponible en esta composición.", summary.EmptyHint);
    }
}
