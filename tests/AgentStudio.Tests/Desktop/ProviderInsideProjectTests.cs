using System.Globalization;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// WP-0 of Phase 6 (ADR-0018, addendum of 2026-11-12): when a program of the agent is found only inside the open project, Auto Doctor
/// says so, with the path and what to do, in both languages and with the words of the glossary of ADR-0054 («programa del agente»).
/// </summary>
public sealed class ProviderInsideProjectTests
{
    private static readonly IUiTextProvider Spanish = new ResourceTextProvider(CultureInfo.GetCultureInfo("es-ES"));
    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    private static ProviderProbeResult NotInstalled(string? foundInsideProject) =>
        new(ProviderId.ClaudeCode, "Claude Code", InstallationState.NotInstalled, null, null, AuthenticationState.Unknown, null,
            ["No se encontró «claude» en el PATH."], foundInsideProject);

    [Fact]
    public void The_note_comes_first_and_names_the_file()
    {
        var item = ProviderStatusItem.From(NotInstalled(@"C:\proyectos\mio\bin\claude.cmd"));

        Assert.Equal(2, item.Notes.Count);
        Assert.Contains(@"C:\proyectos\mio\bin\claude.cmd", item.Notes[0], StringComparison.Ordinal);
        Assert.Equal("No se encontró «claude» en el PATH.", item.Notes[1]);
    }

    [Fact]
    public void Without_the_case_there_is_no_extra_note()
    {
        var item = ProviderStatusItem.From(NotInstalled(null));

        Assert.Equal(["No se encontró «claude» en el PATH."], item.Notes);
    }

    [Fact]
    public void The_text_exists_in_both_languages_with_the_path_in_it_and_says_what_to_do()
    {
        var es = Spanish.Format("Providers_InsideProject", @"C:\x\claude.cmd");
        var en = English.Format("Providers_InsideProject", @"C:\x\claude.cmd");

        Assert.Contains(@"C:\x\claude.cmd", es, StringComparison.Ordinal);
        Assert.Contains(@"C:\x\claude.cmd", en, StringComparison.Ordinal);
        Assert.Contains("programa del agente", es, StringComparison.Ordinal);
        Assert.DoesNotContain("CLI", es, StringComparison.Ordinal);
        Assert.Contains("Instálalo fuera del proyecto", es, StringComparison.Ordinal);
        Assert.Contains("agent program", en, StringComparison.Ordinal);
        Assert.Contains("Install it outside the project", en, StringComparison.Ordinal);
        Assert.NotEqual(es, en);
    }
}
