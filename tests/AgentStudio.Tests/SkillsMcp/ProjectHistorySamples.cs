using AgentStudio.Application.Semantic;

namespace AgentStudio.Tests.SkillsMcp;

/// <summary>
/// A small trusted history of a project, as Agent Studio would write it for the planner's run (docs/17 D-06): fragments of
/// memory, of the log and of the journal, with hand-made unit vectors of four dimensions. The vectors put the fragments
/// about tests close together (<see cref="Plan"/>, <see cref="DivideByZero"/> and <see cref="Verdict"/>), and the dark
/// theme and the worktrees far from them, so a widening by meaning can be checked by hand.
/// </summary>
internal static class ProjectHistorySamples
{
    /// <summary>The fingerprint of the model of the vectors: 64 lowercase hex characters, as the format requires.</summary>
    public static readonly string Fingerprint = new('c', 64);

    public static readonly DateTimeOffset Day1 = new(2026, 10, 18, 9, 30, 0, TimeSpan.FromHours(2));

    public static HistoryFragment Plan { get; } = new(
        "mem:m-41#0",
        "decisión",
        Day1,
        "Plan aprobado: añadir Calc.Multiply con pruebas unitarias (3 tareas).",
        Unit(1, 0, 0, 0));

    public static HistoryFragment DivideByZero { get; } = new(
        "mem:m-42#0",
        "error conocido",
        Day1.AddDays(1),
        "CalcTests.Divide_by_zero falla en la verificación aislada: la división entre cero lanza DivideByZeroException.",
        Unit(0.9f, 0.436f, 0, 0));

    public static HistoryFragment Verdict { get; } = new(
        "log:57#0",
        "registro · veredicto",
        Day1.AddDays(2),
        "Veredicto del juez: R1 cumplido y R2 cumplido; la suite queda en verde.",
        Unit(0.8f, 0, 0.6f, 0));

    public static HistoryFragment DarkTheme { get; } = new(
        "mem:j-7#0",
        "bitácora",
        Day1.AddDays(3),
        "Hecho: tema oscuro completo en la ventana principal y en los diálogos.",
        Unit(0, 0, 0, 1));

    public static HistoryFragment Worktrees { get; } = new(
        "mem:m-50#0",
        "solución",
        Day1.AddDays(4),
        "Las copias de trabajo (worktrees) de cada tarea viven fuera del repositorio, en la carpeta local de la aplicación.",
        Unit(0, 1, 0, 0));

    public static IReadOnlyList<HistoryFragment> Fragments { get; } = [Worktrees, DarkTheme, Verdict, DivideByZero, Plan];

    /// <summary>The history with the vectors of its model, newest first, as the index writes it.</summary>
    public static ProjectHistorySnapshot WithVectors() => new(Day1.AddDays(5), Fingerprint, 4, Fragments);

    /// <summary>The same history without vectors: the model was never downloaded.</summary>
    public static ProjectHistorySnapshot WithoutVectors() =>
        new(Day1.AddDays(5), null, 0, [.. Fragments.Select(f => f with { Vector = null })]);

    /// <summary><paramref name="values"/> scaled to length 1.</summary>
    public static float[] Unit(params float[] values)
    {
        var length = Math.Sqrt(values.Sum(v => (double)v * v));
        return [.. values.Select(v => (float)(v / length))];
    }
}
