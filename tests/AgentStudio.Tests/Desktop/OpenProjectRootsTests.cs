using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Desktop.Services;

namespace AgentStudio.Tests.Desktop;

/// <summary>ADR-0018 (second addendum of 2026-11-12): what the list of untrusted roots says about a project opened too wide.</summary>
public sealed class OpenProjectRootsTests
{
    [Fact]
    public void A_whole_profile_or_drive_opened_as_a_project_is_reported_as_broad_and_a_repository_is_not()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using var temp = new TempDirectory();
        var repository = temp.CreateDirectory("repositorio");
        var roots = new OpenProjectRoots();

        roots.Open(repository);
        Assert.Empty(roots.BroadRoots);

        roots.Open(home);
        roots.Open(Path.GetPathRoot(home)!);
        roots.Open(home + Path.DirectorySeparatorChar);

        Assert.Equal(2, roots.BroadRoots.Count);
        Assert.Contains(roots.BroadRoots, r => string.Equals(r, home, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(repository, roots.Roots);
        Assert.DoesNotContain(repository, roots.BroadRoots);
    }

    [Fact]
    public void Roots_opened_from_several_threads_are_all_kept_once()
    {
        using var temp = new TempDirectory();
        var roots = new OpenProjectRoots();
        var folders = Enumerable.Range(0, 40).Select(i => temp.CreateDirectory("p" + i)).ToList();

        Parallel.ForEach(folders.Concat(folders), f => roots.Open(f));

        Assert.Equal(40, roots.Roots.Count);
    }

    [Fact]
    public void The_text_of_the_warning_exists_in_both_languages_with_the_folder_in_it()
    {
        var es = new AgentStudio.Desktop.Localization.ResourceTextProvider(System.Globalization.CultureInfo.GetCultureInfo("es-ES"))
            .Format("Providers_ProjectTooBroad", @"C:\Users\ana");
        var en = new AgentStudio.Desktop.Localization.ResourceTextProvider(System.Globalization.CultureInfo.GetCultureInfo("en"))
            .Format("Providers_ProjectTooBroad", @"C:\Users\ana");

        Assert.Contains(@"C:\Users\ana", es, StringComparison.Ordinal);
        Assert.Contains("Abre la carpeta del repositorio", es, StringComparison.Ordinal);
        Assert.Contains("too broad", en, StringComparison.Ordinal);
        Assert.Contains("Open the folder of the repository", en, StringComparison.Ordinal);
    }

    [Fact]
    public void An_item_of_the_diagnosis_carries_the_warning_first()
    {
        var result = new AgentStudio.Application.Providers.ProviderProbeResult(
            AgentStudio.Domain.Providers.ProviderId.ClaudeCode, "Claude Code", AgentStudio.Application.Providers.InstallationState.NotInstalled,
            null, null, AgentStudio.Application.Providers.AuthenticationState.Unknown, null, ["nota"]);

        var item = AgentStudio.Desktop.ViewModels.ProviderStatusItem.From(result).WithProjectTooBroad(@"C:\Users\ana");

        Assert.Equal(2, item.Notes.Count);
        Assert.Contains(@"C:\Users\ana", item.Notes[0], StringComparison.Ordinal);
        Assert.Equal("nota", item.Notes[1]);
    }
}
