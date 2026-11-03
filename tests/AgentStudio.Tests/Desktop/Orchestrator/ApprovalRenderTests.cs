using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Views.Orchestrator;
using AgentStudio.Orchestration.Integration;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// V25, seen live: the question of a delivery was titled «deliver-integration», named the project by its GUID and said
/// «fichero(s)». Drawn without ever being shown (<see cref="OffscreenRender"/>); with <c>AGENTSTUDIO_RENDER_EVIDENCE</c> set,
/// the picture is saved.
/// </summary>
public sealed class ApprovalRenderTests
{
    private const string ProjectGuid = "0688cd79-3afe-4ff1-9536-962396f6853f";

    [Fact]
    public void The_question_of_a_delivery_has_a_plain_title_and_names_the_project_not_its_id()
    {
        var texts = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var request = new ApprovalRequest(
                DeliveryService.ApprovalAction,
                "Entregar la ejecución 20261030-2340ab: avanzar tu rama main de 1a2b3c4d5e a 9f8e7d6c5b (agentstudio/run/20261030-2340ab) con --ff-only. Cambian 2 ficheros.",
                RiskLevel.Critical,
                null,
                ["src/Calc/Calc.cs", "tests/Calc.Tests/CalcTests.cs"],
                ProjectGuid,
                "20261030-2340ab");
            var view = new ApprovalView
            {
                DataContext = new ApprovalDialogViewModel(request, ApprovalVerification.WindowsHello, UserPresenceAvailability.Available, "calc"),
            };

            var bitmap = OffscreenRender.Render(view, 600, 640);
            OffscreenRender.SaveIfRequested(bitmap, "aprobacion-entrega.png");
            return Descendants<TextBlock>(view).Where(t => t.Visibility == Visibility.Visible).Select(t => t.Text).ToList();
        });

        Assert.Contains("Entregar a tu rama", texts);
        Assert.Contains("Origen: proyecto calc · ejecución 20261030-2340ab", texts);
        Assert.Contains("Acción en el registro: deliver-integration", texts);
        Assert.DoesNotContain("deliver-integration", texts);
        Assert.DoesNotContain(texts, t => t.Contains(ProjectGuid, StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("(s)", StringComparison.Ordinal));
    }

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

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
