using AgentStudio.Desktop.Background;

namespace AgentStudio.Tests.Desktop.Background;

/// <summary>
/// F8-9 and docs/16 D-16: the question of the managed exit says what goes on and what each answer does before it is chosen;
/// «Seguir en la bandeja» only when the window is being closed; no answer is «Cancelar».
/// </summary>
public sealed class ExitPromptViewModelTests
{
    private static ExitQuestion Question(bool keepInTray, bool withDebate = false) => new(
        [
            new ExitWorkLine("monorepo", "Ejecución 20261021-c4a1f2", "En ejecución · 2 intentos en curso", CanPause: true),
            .. withDebate ? new[] { new ExitWorkLine("docs-internas", "Debate deb-20261019-a1b2c3", "En deliberación · ronda 2", CanPause: false) } : [],
        ],
        keepInTray);

    [Fact]
    public void Closing_the_window_offers_keeping_it_in_the_tray_and_each_answer_says_what_it_does()
    {
        var question = new ExitPromptViewModel(Question(keepInTray: true));

        Assert.True(question.OffersKeepInTray);
        Assert.Equal("Vas a cerrar la ventana con 1 trabajo en marcha. ¿Qué hago con ello?", question.Intro);
        Assert.Equal("Pausar y salir cuando terminen los intentos en curso", question.PauseText);
        Assert.Equal("Salir ahora y dejarlas interrumpidas", question.ExitNowText);
        Assert.Contains("no se reanudan solas", question.PauseHelp, StringComparison.Ordinal);
        Assert.Contains("No se reanuda solo", question.ExitNowHelp, StringComparison.Ordinal);
        Assert.Contains("todo sigue en marcha", question.KeepInTrayHelp, StringComparison.Ordinal);
        Assert.DoesNotContain("debates", question.PauseHelp, StringComparison.Ordinal);
        Assert.Contains("registro de cada ejecución", question.RecordNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Exiting_from_the_tray_does_not_offer_keeping_it_and_says_what_cannot_be_paused()
    {
        var question = new ExitPromptViewModel(Question(keepInTray: false, withDebate: true));

        Assert.False(question.OffersKeepInTray);
        Assert.Equal("Vas a salir de Agent Studio con 2 trabajos en marcha. ¿Qué hago con ello?", question.Intro);
        Assert.EndsWith("Los debates y las auditorías no se pueden pausar: espera a que terminen.", question.PauseHelp, StringComparison.Ordinal);

        question.ChooseCommand.Execute(ExitChoice.KeepInTray);
        Assert.Null(question.Answer);
    }

    [Theory]
    [InlineData(ExitChoice.KeepInTray)]
    [InlineData(ExitChoice.PauseAndExitWhenIdle)]
    [InlineData(ExitChoice.ExitNow)]
    [InlineData(ExitChoice.Cancel)]
    public void Choosing_an_answer_records_it_and_asks_the_window_to_close(ExitChoice choice)
    {
        var question = new ExitPromptViewModel(Question(keepInTray: true));
        var closed = 0;
        question.CloseRequested += (_, _) => closed++;

        question.ChooseCommand.Execute(choice);

        Assert.Equal(choice, question.Answer);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Nothing_answered_is_no_answer()
    {
        var question = new ExitPromptViewModel(Question(keepInTray: true));

        Assert.Null(question.Answer);
    }
}
