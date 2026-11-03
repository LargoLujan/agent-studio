using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentStudio.Application.Skills;
using AgentStudio.Desktop.Skills;
using AgentStudio.Desktop.Views.Skills;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Skills;
using AgentStudio.Tests.Desktop.Orchestrator;
using static AgentStudio.Tests.Desktop.Skills.SkillsScreenSamples;

namespace AgentStudio.Tests.Desktop.Skills;

/// <summary>
/// The Skills screen drawn without ever being shown (<see cref="OffscreenRender"/>): no window, no HWND, nothing on the
/// user's screen. It proves that the view, the theme and the bindings produce a picture of the tree, the card with its
/// switches and health, and both recommendations. With <c>AGENTSTUDIO_RENDER_EVIDENCE</c> set, the picture is saved for
/// docs/evidencias.
/// </summary>
public sealed class SkillsRenderTests
{
    private static readonly Color Background = Color.FromRgb(0x0A, 0x10, 0x20);
    private static readonly Color Accent = Color.FromRgb(0x2E, 0x68, 0xF9);
    private static readonly Color Success = Color.FromRgb(0x2F, 0xD0, 0x8A);
    private static readonly Color Warning = Color.FromRgb(0xF5, 0xB5, 0x44);
    private static readonly Color Error = Color.FromRgb(0xFF, 0x5C, 0x6C);

    [Fact]
    public async Task The_whole_screen_renders_offscreen_with_its_tree_card_and_both_recommendations()
    {
        var (drawn, accent, success, warning, error, treeItems, switches) = await WpfHost.RunAsync(async () =>
        {
            var world = new SkillsWorld();
            world.Health.Statuses["sql-optimization"] = SkillHealthStatus.Critical;
            world.Health.Statuses["wpf-mvvm"] = SkillHealthStatus.Warning;
            world.Activations.Decisions.Add(new SkillActivation(new SkillId("spark-optimization"), SkillScopeKind.Project, ProjectId, true));
            world.Recommendations.Answer = new AssistedRecommendationResult(
                [
                    new AssistedRecommendation(new SkillId("spark-optimization"), SkillScopeKind.Project, null, "El proyecto escribe tablas Delta con Spark."),
                    new AssistedRecommendation(new SkillId("test-driven-development"), SkillScopeKind.Agent, new AgentId("ejecutor"), "Que el ejecutor escriba la prueba primero."),
                ],
                ["Se descarta «no-existe»: no está en el catálogo."],
                new RunId("aux-20260101-abcdef"));
            var document = world.Document();
            await document.OpenAsync(CancellationToken.None);
            document.Sectors.SelectMany(s => s.Skills).Single(s => s.Id == "spark-optimization").IsSelected = true;
            document.TaskText = "Optimizar un trabajo de Spark que escribe tablas Delta";
            document.RecommendCommand.Execute(null);
            document.IsAssistedAllowed = true;
            await document.AskAgentCommand.ExecuteAsync(null);

            var view = new SkillsView { DataContext = document };
            var bitmap = OffscreenRender.Render(view, 1440, 900);
            OffscreenRender.SaveIfRequested(bitmap, "fase4-skills.png");
            return (
                OffscreenRender.CountUnlike(bitmap, Background),
                OffscreenRender.Count(bitmap, Accent),
                OffscreenRender.Count(bitmap, Success),
                OffscreenRender.Count(bitmap, Warning),
                OffscreenRender.Count(bitmap, Error),
                Descendants<TreeViewItem>(view).Count(),
                Descendants<CheckBox>(view).Count());
        });

        Assert.True(drawn > 80_000, $"Only {drawn} pixels differ from the background.");
        Assert.True(accent > 300, $"Only {accent} pixels have the accent colour: «Pedir a un agente» or the checked switch was not drawn.");
        Assert.True(success > 20, $"Only {success} pixels have the success colour: the healthy checks were not drawn.");
        Assert.True(warning > 20, $"Only {warning} pixels have the warning colour: the warnings and the amber dot were not drawn.");
        Assert.True(error > 10, $"Only {error} pixels have the error colour: the critical dot was not drawn.");

        // Four sectors and their five skills, all expanded; five switches on the card plus the one of the assisted recommendation.
        Assert.Equal(9, treeItems);
        Assert.Equal(6, switches);
    }

    [Fact]
    public async Task By_default_the_assisted_recommendation_is_off_and_its_button_disabled()
    {
        var (allowed, enabled) = await WpfHost.RunAsync(async () =>
        {
            var document = new SkillsWorld().Document();
            await document.OpenAsync(CancellationToken.None);
            document.TaskText = "Optimizar Spark";
            var view = new SkillsView { DataContext = document };
            OffscreenRender.Render(view, 1440, 900);

            var toggle = Descendants<CheckBox>(view).Single(c => AutomationName(c) == "Permitir la recomendación asistida");
            var ask = Descendants<Button>(view).Single(b => AutomationName(b) == "Pedir a un agente");
            return (toggle.IsChecked, ask.IsEnabled);
        });

        Assert.False(allowed);
        Assert.False(enabled);
    }

    [Fact]
    public async Task With_dozens_of_warnings_of_the_index_the_catalog_stays_in_view_and_their_detail_has_a_limited_height()
    {
        var (summary, treeTop, treeHeight, detailHeight, treeTopExpanded) = await WpfHost.RunAsync(async () =>
        {
            var world = new SkillsWorld();
            for (var i = 1; i <= 27; i++)
            {
                var name = $"enlazada-{i:00}";
                world.Catalog.Add(Skill(name, "Una skill que el índice solo enlaza.", SkillSectors.IngenieriaSoftware, ["enlazada"], linked: true));
                world.CatalogWarnings.Add($"«{name}» está enlazada: el índice no guarda una copia local y no se puede dar a un agente.");
            }

            // design-tokens of the samples is linked too, as the catalog says.
            world.CatalogWarnings.Add("«design-tokens» está enlazada: el índice no guarda una copia local y no se puede dar a un agente.");
            var document = world.Document();
            await document.OpenAsync(CancellationToken.None);
            var view = new SkillsView { DataContext = document };

            var bitmap = OffscreenRender.Render(view, 1440, 900);
            OffscreenRender.SaveIfRequested(bitmap, "skills-avisos.png");
            var tree = Descendants<TreeView>(view).Single();
            var top = tree.TranslatePoint(new Point(0, 0), view).Y;
            var height = tree.ActualHeight;

            // The detail open, in a view of its own (a view drawn twice keeps the bounds of its first picture).
            document.ShowCatalogWarnings = true;
            var open = new SkillsView { DataContext = document };
            var expanded = OffscreenRender.Render(open, 1440, 900);
            OffscreenRender.SaveIfRequested(expanded, "skills-avisos-detalle.png");
            var detail = Descendants<ScrollViewer>(open).Single(s => s.MaxHeight == 160);
            var openTree = Descendants<TreeView>(open).Single();
            return (document.CatalogWarningsSummary, top, height, detail.ActualHeight, openTree.TranslatePoint(new Point(0, 0), open).Y);
        });

        Assert.Equal("28 skills enlazadas no se pueden dar a un agente.", summary);
        Assert.True(treeTop < 200, $"The tree starts at {treeTop:0} px: the warnings push the catalog down.");
        Assert.True(treeHeight > 600, $"The tree is only {treeHeight:0} px high.");
        Assert.InRange(detailHeight, 100, 160);
        Assert.True(treeTopExpanded < 400, $"With the detail open the tree starts at {treeTopExpanded:0} px.");
    }

    private static string AutomationName(DependencyObject element) => System.Windows.Automation.AutomationProperties.GetName(element);

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
