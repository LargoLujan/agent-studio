using System.Globalization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Lock;

namespace AgentStudio.Tests.Desktop.Lock;

/// <summary>
/// The texts of the lock screen, Windows Hello and the lock setting (docs/17 D-47, D-48 and D-54; F14-1, F14-3 and F14-4;
/// ADR-0054): every name an enumeration gives to a screen exists in both languages, and the reasons of a denial, which the
/// decision log keeps, are the same whatever the language of the interface.
/// </summary>
public sealed class LockStringsTests
{
    private static readonly IUiTextProvider Spanish = new ResourceTextProvider(CultureInfo.GetCultureInfo("es-ES"));

    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    [Fact]
    public void Every_name_an_enumeration_gives_to_a_lock_screen_exists_in_both_languages()
    {
        var keys = new List<string>
        {
            "Lock_Absent_Unknown",
            "Lock_NotConfirmed_Unknown",
        };
        keys.AddRange(Enum.GetNames<UserPresenceAvailability>().Where(n => n != nameof(UserPresenceAvailability.Available)).Select(n => "Lock_Absent_" + n));
        keys.AddRange(Enum.GetNames<UserPresenceResult>().Select(n => "Lock_NotConfirmed_" + n));
        keys.AddRange(Enum.GetNames<RiskLevel>().Select(n => "Lock_Risk_" + n));
        foreach (var kind in Enum.GetNames<ProjectActivityKind>())
        {
            keys.Add($"Lock_Kind_{kind}_One");
            keys.Add($"Lock_Kind_{kind}_Other");
        }

        var missing = keys.Where(k => !Spanish.Has(k) || !English.Has(k)).ToList();
        Assert.True(missing.Count == 0, "Names without a text:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void The_screen_reads_what_goes_on_with_its_numbers_in_place()
    {
        Assert.Equal("Sigue en marcha: 2 ejecuciones y 1 debate.", LockScreenViewModel.Running([ProjectActivityKind.Run, ProjectActivityKind.Run, ProjectActivityKind.Debate]));
        Assert.Equal("No hay trabajo en marcha.", LockScreenViewModel.Running([]));
        Assert.Equal("Still running: 2 runs and 1 debate.", English.Format("Lock_Screen_Running", English.Format("Lock_Kind_Run_Other", 2) + " " + English.Get("Lock_Join_And") + " " + English.Format("Lock_Kind_Debate_One", 1)));
    }

    [Fact]
    public void The_screens_say_why_windows_hello_is_not_there_in_the_language_in_use()
    {
        Assert.Equal("lo desactiva una directiva", HelloAnswers.WhyAbsent(UserPresenceAvailability.DisabledByPolicy));
        Assert.Equal("Windows no lo pudo decir", HelloAnswers.WhyAbsent((UserPresenceAvailability)99));
        Assert.Equal("se canceló la verificación", HelloAnswers.WhyNotConfirmed(UserPresenceResult.Canceled));
        Assert.Equal("Windows dio un resultado que Agent Studio no conoce", HelloAnswers.WhyNotConfirmed((UserPresenceResult)99));
    }

    /// <summary>
    /// D-48 and F14-4: the reason of a denial is written in the decision log. Built with the interface in Spanish and in English it
    /// comes out the same, and it is the text the log already holds.
    /// </summary>
    [Fact]
    public void The_reasons_of_a_denial_in_the_decision_log_are_the_same_in_every_language()
    {
        string[] Reasons() =>
        [
            .. Enum.GetValues<UserPresenceResult>().Select(HelloAnswers.ApprovalNotConfirmed),
            HelloAnswers.ApprovalNotAsked("no window"),
            .. Enum.GetValues<WindowsLockResult>().Select(HelloTexts.ApprovalLockNotConfirmed),
            HelloTexts.ApprovalLockNotAsked("no window"),
            HelloTexts.ApprovalConfirmationPending,
        ];

        var before = (CultureInfo.CurrentUICulture, CultureInfo.DefaultThreadCurrentUICulture);
        string[] inSpanish;
        string[] inEnglish;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("es-ES");
            inSpanish = Reasons();
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en");
            inEnglish = Reasons();
        }
        finally
        {
            CultureInfo.CurrentUICulture = before.CurrentUICulture;
            CultureInfo.DefaultThreadCurrentUICulture = before.DefaultThreadCurrentUICulture;
        }

        Assert.Equal(inSpanish, inEnglish);
        Assert.Equal("Windows Hello no confirmó que eres tú (Canceled: se canceló la verificación): se deniega.", HelloAnswers.ApprovalNotConfirmed(UserPresenceResult.Canceled));
        Assert.Equal("No se pudo pedir Windows Hello (no window): se deniega.", HelloAnswers.ApprovalNotAsked("no window"));
        Assert.Equal("El bloqueo de Windows no confirmó que eres tú (TimedOut: nadie lo desbloqueó a tiempo): se deniega.", HelloTexts.ApprovalLockNotConfirmed(WindowsLockResult.TimedOut));
        Assert.StartsWith("Ya hay una confirmación pendiente", HelloTexts.ApprovalConfirmationPending, StringComparison.Ordinal);

        // None of them is read from the resources: the English of the screens is another text.
        Assert.DoesNotContain(inSpanish, reason => English.Get("Lock_NotConfirmed_Canceled") == reason);
    }

    [Fact]
    public void What_windows_hello_shows_comes_from_the_resources()
    {
        Assert.Equal("Agent Studio: confirma que eres tú para desbloquear la interfaz.", HelloAnswers.UnlockMessage);
        Assert.Equal("Agent Studio: confirm it is you to approve “Deliver”.", English.Format("Lock_Hello_ApprovalMessage", "Deliver"));
        Assert.Equal("Agent Studio: confirma que eres tú para aprobar «Entregar».", HelloAnswers.ApprovalMessage("Entregar"));
    }
}
