using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Orchestrator.Preparation;
using static AgentStudio.Tests.Desktop.Orchestrator.Team.TeamTestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator.Preparation;

/// <summary>
/// The brief of the project (docs/17 D-10, F13-17): a text of the person that is kept as their memory, each version replacing
/// the one in force. The memory is a port: here it is a double in memory, and nothing is written anywhere.
/// </summary>
public sealed class ProjectBriefViewModelTests
{
    private sealed class MemoryBrief : IProjectBrief
    {
        public ProjectBrief? Current { get; set; }

        public List<(string Text, string DecidedBy)> Saves { get; } = [];

        public Task<ProjectBrief?> ReadAsync(ProjectContext project, CancellationToken ct) => Task.FromResult(Current);

        public Task<ProjectBrief> SaveAsync(ProjectContext project, string text, string decidedBy, CancellationToken ct)
        {
            Saves.Add((text, decidedBy));
            Current = new ProjectBrief("e1", text, new string('a', 64), T0);
            return Task.FromResult(Current);
        }
    }

    [Fact]
    public async Task It_reads_the_brief_in_force_and_saves_a_new_version_as_a_decision_of_the_person()
    {
        var store = new MemoryBrief { Current = new ProjectBrief("e0", "Calculadora en C#.", new string('b', 64), T0) };
        var brief = new ProjectBriefViewModel(store, Project, Text());

        await brief.LoadAsync();
        Assert.Equal("Calculadora en C#.", brief.Brief);
        Assert.StartsWith("Versión vigente, huella bbbbbbbbbbbb", brief.VersionText, StringComparison.Ordinal);
        Assert.False(brief.SaveCommand.CanExecute(null));

        brief.Brief = "Calculadora en C#. Documentación: https://docs.example/calc";
        Assert.True(brief.SaveCommand.CanExecute(null));
        await brief.SaveCommand.ExecuteAsync(null);

        var saved = Assert.Single(store.Saves);
        Assert.Equal("Calculadora en C#. Documentación: https://docs.example/calc", saved.Text);
        Assert.Equal(ApprovalPromptService.DecidedBy, saved.DecidedBy);
        Assert.Equal("Ficha guardada: es una versión nueva que sustituye a la anterior.", brief.Status);
        Assert.StartsWith("Versión vigente, huella aaaaaaaaaaaa", brief.VersionText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_brief_or_one_past_the_limit_is_not_saved_and_says_why()
    {
        var store = new MemoryBrief();
        var brief = new ProjectBriefViewModel(store, Project, Text());

        brief.Brief = new string('x', ProjectBriefs.MaxCharacters + 1);
        await brief.SaveCommand.ExecuteAsync(null);

        Assert.Empty(store.Saves);
        Assert.StartsWith("La ficha pasa del máximo de", brief.Status, StringComparison.Ordinal);
        Assert.Equal("Todavía no hay ficha.", brief.VersionText);
    }

    [Fact]
    public async Task Without_the_memory_it_says_so_and_saves_nothing()
    {
        var brief = new ProjectBriefViewModel(null, Project, Text());
        brief.Brief = "algo";

        Assert.False(brief.IsAvailable);
        Assert.False(brief.SaveCommand.CanExecute(null));
        Assert.Equal("La ficha no está disponible: falta la memoria del proyecto.", brief.UnavailableText);
        await brief.LoadAsync();
        Assert.Equal("algo", brief.Brief);
    }
}
