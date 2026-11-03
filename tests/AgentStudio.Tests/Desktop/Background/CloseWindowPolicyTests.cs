using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Services;

namespace AgentStudio.Tests.Desktop.Background;

/// <summary>
/// F8-7 and docs/16 D-14: closing the main window, for the three settings with and without work going on. «Seguir en la
/// bandeja» always hides it; «salir» exits, through the managed exit when there is work; «preguntar» asks only with work.
/// </summary>
public sealed class CloseWindowPolicyTests
{
    [Theory]
    [InlineData(CloseWindowAction.Ask, false, CloseWindowDecision.Exit)]
    [InlineData(CloseWindowAction.Ask, true, CloseWindowDecision.AskKeepOrExit)]
    [InlineData(CloseWindowAction.KeepInTray, false, CloseWindowDecision.HideToTray)]
    [InlineData(CloseWindowAction.KeepInTray, true, CloseWindowDecision.HideToTray)]
    [InlineData(CloseWindowAction.Exit, false, CloseWindowDecision.Exit)]
    [InlineData(CloseWindowAction.Exit, true, CloseWindowDecision.ManagedExit)]
    public void Each_setting_with_and_without_work_decides_what_closing_the_window_does(CloseWindowAction setting, bool hasWork, CloseWindowDecision expected) =>
        Assert.Equal(expected, CloseWindowPolicy.Decide(setting, hasWork));

    [Fact]
    public void A_setting_that_is_not_one_of_the_three_behaves_as_asking()
    {
        Assert.Equal(CloseWindowDecision.AskKeepOrExit, CloseWindowPolicy.Decide((CloseWindowAction)42, hasActiveWork: true));
        Assert.Equal(CloseWindowDecision.Exit, CloseWindowPolicy.Decide((CloseWindowAction)42, hasActiveWork: false));
    }

    [Fact]
    public void The_default_setting_asks_when_there_is_work_and_exits_when_there_is_none()
    {
        var setting = AppSettings.Default.CloseWindow;

        Assert.Equal(CloseWindowAction.Ask, setting);
        Assert.Equal(CloseWindowDecision.AskKeepOrExit, CloseWindowPolicy.Decide(setting, hasActiveWork: true));
        Assert.Equal(CloseWindowDecision.Exit, CloseWindowPolicy.Decide(setting, hasActiveWork: false));
    }
}
