using System.Globalization;
using System.Runtime.CompilerServices;

namespace AgentStudio.Tests.TestSupport;

/// <summary>
/// The tests read the texts of the interface in Spanish of Spain, the language the resources are written in (ADR-0054),
/// whatever the language of the Windows that runs them: the runner of the CI is in English, and the texts of the desktop
/// follow the culture of the interface. Only that culture is fixed, never the one of numbers and dates. A test that checks
/// another language takes a provider of its own (<c>new ResourceTextProvider(culture)</c>).
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void UseSpanishInterface()
    {
        var spanish = CultureInfo.GetCultureInfo("es-ES");
        CultureInfo.DefaultThreadCurrentUICulture = spanish;
        CultureInfo.CurrentUICulture = spanish;
    }
}
