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

        // Four sectors and their five skills, all expanded; five switches on the card plus the one of the assisted recommendation and the one of «Comprobar en GitHub».
        Assert.Equal(9, treeItems);
        Assert.Equal(7, switches);
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

    /// <summary>
    /// V29, seen live: with the window at 1280×768 the column of the recommendations was cut on the right («Permitir la
    /// recomendación asistida (» and nothing more). At the width that window leaves to a document (970 px, with the Explorer
    /// open) everything fits side by side; narrower, the recommendations go under the card. Never cut, never a horizontal
    /// scroll bar.
    /// </summary>
    [Theory]
    [InlineData(970, 640, "skills-1280x768.png", false)]
    [InlineData(600, 640, "skills-estrecho.png", true)]
    public async Task The_columns_fit_the_width_and_the_recommendations_go_under_the_card_when_it_is_narrow(double width, double height, string picture, bool stacked)
    {
        var (toggleRight, cardRight, recommendationsRight, recommendationsTop, cardBottom, horizontalBars, tallestScope) = await WpfHost.RunAsync(async () =>
        {
            var world = new SkillsWorld();
            world.Activations.Decisions.Add(new SkillActivation(new SkillId("spark-optimization"), SkillScopeKind.Project, ProjectId, true));
            var document = world.Document();
            await document.OpenAsync(CancellationToken.None);
            document.Sectors.SelectMany(s => s.Skills).Single(s => s.Id == "spark-optimization").IsSelected = true;
            document.TaskText = "Optimizar un trabajo de Spark que escribe tablas Delta";
            document.RecommendCommand.Execute(null);

            var view = new SkillsView { DataContext = document };
            OffscreenRender.SaveIfRequested(OffscreenRender.Render(view, width, height), picture);
            var toggle = Descendants<CheckBox>(view).Single(c => AutomationName(c) == "Permitir la recomendación asistida");
            var recommendations = (FrameworkElement)view.FindName("RecommendationsCard");
            var card = Descendants<ContentControl>(view).Single(c => c.Content is SkillDetailViewModel);
            var cardBorder = Ancestor<Border>(card, b => b.Style == (Style)view.FindResource("Card"));
            var scopes = Descendants<ItemsControl>(view).Single(i => AutomationName(i) == "Activación por ámbito");

            // The tree keeps its own horizontal scroll for a long name, as it always had: the columns are what were cut.
            var tree = Descendants<ScrollViewer>(Descendants<TreeView>(view).Single()).ToHashSet();
            return (
                Right(toggle, view),
                Right(cardBorder, view),
                Right(recommendations, view),
                recommendations.TranslatePoint(new Point(0, 0), view).Y,
                cardBorder.TranslatePoint(new Point(0, cardBorder.ActualHeight), view).Y,
                Descendants<ScrollViewer>(view).Count(s => !tree.Contains(s) && s.ComputedHorizontalScrollBarVisibility == Visibility.Visible),
                Descendants<WrapPanel>(scopes).Max(p => p.ActualHeight));
        });

        // What is in effect goes under its scope when the card is narrow, never in a sliver that wraps letter by letter.
        Assert.True(tallestScope < 60, $"A row of «Activación» is {tallestScope:0} px high.");
        Assert.True(toggleRight <= width - 16, $"«Permitir la recomendación asistida» ends at {toggleRight:0} px in a view of {width:0} px: it is cut.");
        Assert.True(recommendationsRight <= width - 16 + 0.5, $"The recommendations end at {recommendationsRight:0} px in a view of {width:0} px.");
        Assert.True(cardRight <= width - 16 + 0.5, $"The card ends at {cardRight:0} px in a view of {width:0} px.");
        Assert.Equal(0, horizontalBars);
        Assert.Equal(stacked, recommendationsTop >= cardBottom);
    }

    private static double Right(FrameworkElement element, UIElement view) => element.TranslatePoint(new Point(element.ActualWidth, 0), view).X;

    private static T Ancestor<T>(DependencyObject element, Func<T, bool> match)
        where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T found && match(found))
            {
                return found;
            }
        }

        throw new InvalidOperationException($"No {typeof(T).Name} above the element matches.");
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
