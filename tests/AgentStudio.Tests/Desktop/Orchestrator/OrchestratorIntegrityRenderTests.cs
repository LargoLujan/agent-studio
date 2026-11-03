using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Views.Orchestrator;
using AgentStudio.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The integrity problem of the decision log and «Aceptar este historial como nuevo origen» drawn in the orchestrator tab,
/// without ever showing a window (<see cref="OffscreenRender"/>).
/// </summary>
public sealed class OrchestratorIntegrityRenderTests
{
    private static readonly Color Error = Color.FromRgb(0xFF, 0x5C, 0x6C);

    private static Task<OrchestratorDocumentViewModel> OpenAsync(HistoryBackend history) => WpfHost.RunAsync(async () =>
    {
        OrchestratorTheme.EnsureLoaded();
        var model = new OrchestratorDocumentViewModel(
            @"C:\proyectos\calc",
            new OrchestrationSession(history.On(Backends.Complete(new FakeOrchestrator()))),
            new FileCanvasLayoutStore(new ProjectStorageFiles()),
            new FakeTimeProvider(T0),
            new InlineSynchronizationContext());
        await model.OpenAsync();
        return model;
    });

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

            foreach (var below in Descendants<T>(child))
            {
                yield return below;
            }
        }
    }

    [Fact]
    public async Task The_integrity_problem_and_the_acceptance_button_are_drawn_only_for_a_history_without_an_anchor()
    {
        var unanchored = new HistoryBackend();
        await unanchored.WriteUnanchoredAsync(2);
        var blocked = await OpenAsync(unanchored);
        var trusted = await OpenAsync(new HistoryBackend());

        var (withProblem, withoutProblem, button, noButton) = WpfHost.Run(() =>
        {
            (int Errors, Button? Accept) Draw(OrchestratorDocumentViewModel document)
            {
                document.Canvas.ProjectRoot = null; // nothing is written under C:\proyectos
                var view = new OrchestratorView { DataContext = document };
                var bitmap = OffscreenRender.Render(view, 1500, 980);
                OffscreenRender.SaveIfRequested(bitmap, document == blocked ? "fase3-historial-sin-ancla.png" : "fase3-historial-anclado.png");
                var accept = Descendants<Button>(view).FirstOrDefault(b => Equals(b.Content, "Aceptar este historial como nuevo origen"));
                return (OffscreenRender.Count(bitmap, Error), accept);
            }

            var (errors, accept) = Draw(blocked);
            var (none, hidden) = Draw(trusted);
            return (errors, none, accept, hidden);
        });

        Assert.True(withProblem - withoutProblem > 300, $"The error colour covers {withProblem} pixels with the problem and {withoutProblem} without: the problem was not drawn.");
        Assert.NotNull(button);
        Assert.Equal(Visibility.Visible, WpfHost.Run(() => button.Visibility));
        Assert.True(WpfHost.Run(() => button.IsEnabled));
        Assert.Equal(Visibility.Collapsed, WpfHost.Run(() => noButton!.Visibility));
    }
}
