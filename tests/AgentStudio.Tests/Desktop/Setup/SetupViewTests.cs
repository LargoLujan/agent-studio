using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AgentStudio.Application.Setup;
using AgentStudio.Desktop.Controls.Info;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Setup;
using AgentStudio.Desktop.Views.Setup;
using AgentStudio.Infrastructure.Models;
using AgentStudio.Tests.Application.Setup;

namespace AgentStudio.Tests.Desktop.Setup;

/// <summary>
/// The screen of the configurator built offscreen, never shown (docs/17 D-54, F13-19): it paints the step on screen and only that
/// one, its list of steps is named for a screen reader, and its ⓘ is there.
/// </summary>
public sealed class SetupViewTests
{
    private static async Task<SetupViewModel> OpenAsync()
    {
        var roster = new InMemoryRoster(SetupTeams.Default());
        var service = new ProjectSetupService(
            new FakeRepositoryReader(SetupFakes.Facts(["App.slnx", "src/App.csproj"])),
            roster,
            new FakeProfiles(),
            new FakeOptionsStore(),
            new RecordingRecorder(),
            new FakeBrief(),
            new FakeSkillCatalog(FakeSkillCatalog.Skill("dotnet-testing", ["dotnet", "testing"])),
            new FakeActivations(),
            requirements: null,
            models: new BuiltInModelCatalog(),
            learning: null,
            gate: new FakeGate(true));
        var vm = new SetupViewModel(service);
        await vm.LoadAsync(SetupFakes.Project);
        return vm;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var inner in Descendants<T>(child))
            {
                yield return inner;
            }
        }
    }

    /// <summary>Whether no element from <paramref name="element"/> up to <paramref name="root"/> is collapsed: offscreen, <c>IsVisible</c> is always false.</summary>
    private static bool Shows(FrameworkElement element, DependencyObject root)
    {
        for (DependencyObject? at = element; at is not null && !ReferenceEquals(at, root); at = VisualTreeHelper.GetParent(at))
        {
            if (at is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }

    private static SetupView Build(SetupViewModel vm)
    {
        var view = new SetupView { DataContext = vm };
        view.Measure(new Size(1000, 800));
        view.Arrange(new Rect(0, 0, 1000, 800));
        WpfHost.FlushBindings();
        view.UpdateLayout();
        return view;
    }

    private static IReadOnlyList<string> VisibleTexts(SetupView view) =>
        [.. Descendants<TextBlock>(view).Where(t => Shows(t, view) && t.Text.Length > 0).Select(t => t.Text)];

    [Fact]
    public async Task Only_the_step_on_screen_is_painted_and_each_one_has_its_texts()
    {
        var vm = await WpfHost.RunAsync(OpenAsync);

        var painted = WpfHost.Run(() =>
        {
            var view = Build(vm);
            var perStep = new Dictionary<SetupStep, IReadOnlyList<string>>();
            foreach (var step in Enum.GetValues<SetupStep>())
            {
                vm.Step = step;
                view.UpdateLayout();
                perStep[step] = VisibleTexts(view);
            }

            return perStep;
        });

        var texts = Texts.Current;
        Assert.Contains(painted[SetupStep.Project], t => t.StartsWith(texts.Get("Setup_Project_Nothing_Run"), StringComparison.Ordinal));
        Assert.Contains(texts.Get("Setup_Brief_Hint"), painted[SetupStep.Brief]);
        Assert.Contains(texts.Get("Setup_Team_Hint"), painted[SetupStep.Team]);
        Assert.Contains(texts.Get("Setup_Models_Hint"), painted[SetupStep.Models]);
        Assert.Contains(texts.Get("Setup_Verification_Hint"), painted[SetupStep.Verification]);
        Assert.Contains(texts.Get("Setup_Skills_Hint"), painted[SetupStep.Skills]);
        Assert.Contains(texts.Get("Setup_Budget_Hint"), painted[SetupStep.Budget]);
        Assert.Contains(texts.Get("Setup_Summary_Hint"), painted[SetupStep.Summary]);

        // None of the hints of the other steps is painted with it.
        var hints = new Dictionary<SetupStep, string>
        {
            [SetupStep.Brief] = texts.Get("Setup_Brief_Hint"),
            [SetupStep.Team] = texts.Get("Setup_Team_Hint"),
            [SetupStep.Models] = texts.Get("Setup_Models_Hint"),
            [SetupStep.Verification] = texts.Get("Setup_Verification_Hint"),
            [SetupStep.Skills] = texts.Get("Setup_Skills_Hint"),
            [SetupStep.Budget] = texts.Get("Setup_Budget_Hint"),
            [SetupStep.Summary] = texts.Get("Setup_Summary_Hint"),
        };
        foreach (var (step, shown) in painted)
        {
            Assert.All(hints.Where(h => h.Key != step), h => Assert.DoesNotContain(h.Value, shown));
        }
    }

    [Fact]
    public async Task The_steps_are_a_named_list_the_heading_is_there_and_so_is_the_information_button()
    {
        var vm = await WpfHost.RunAsync(OpenAsync);

        var (listName, heading, infoKeys, stepNames, buttons) = WpfHost.Run(() =>
        {
            var view = Build(vm);
            var list = Descendants<ItemsControl>(view).First(i => ReferenceEquals(i.ItemsSource, vm.Steps));
            return (
                AutomationProperties.GetName(list),
                Descendants<TextBlock>(view).Any(t => t.Text == Texts.Current.Get("Setup_Heading")),
                Descendants<InfoButton>(view).Select(b => b.InfoKey).ToList(),
                Descendants<Button>(view).Where(b => b.CommandParameter is SetupStepViewModel).Select(b => AutomationProperties.GetName(b)).ToList(),
                Descendants<Button>(view).Select(b => b.Content as string).Where(c => c is not null).ToList());
        });

        Assert.Equal(Texts.Current.Get("Setup_Steps_Label"), listName);
        Assert.True(heading);
        Assert.Equal(["Setup_Info", "Setup_Verification_Info", "Setup_Budget_Info"], infoKeys);
        Assert.Equal(vm.Steps.Select(s => s.Title), stepNames);
        Assert.Contains(Texts.Current.Get("Setup_Button_Apply"), buttons);
        Assert.Contains(Texts.Current.Get("Setup_Button_Next"), buttons);
        Assert.Contains(Texts.Current.Get("Setup_Button_Back"), buttons);
    }

    [Fact]
    public async Task The_models_are_chosen_in_boxes_the_summary_has_a_change_button_per_step_and_with_nothing_to_save_it_offers_to_go_to_work()
    {
        var vm = await WpfHost.RunAsync(OpenAsync);

        var (boxes, changes, goToWork, apply) = WpfHost.Run(() =>
        {
            var view = Build(vm);
            vm.Step = SetupStep.Models;
            view.UpdateLayout();
            var boxes = Descendants<ComboBox>(view).Where(c => Shows(c, view)).Select(c => (c.IsEditable, Name: AutomationProperties.GetName(c))).ToList();
            vm.Step = SetupStep.Summary;
            view.UpdateLayout();
            var changes = Descendants<Button>(view).Count(b => Shows(b, view) && b.Content as string == Texts.Current.Get("Setup_Summary_Change"));
            vm.StepOf(SetupStep.Verification).IsIncluded = false;
            view.UpdateLayout();
            bool Shown(string key) => Descendants<Button>(view).Any(b => Shows(b, view) && b.Content as string == Texts.Current.Get(key));
            return (boxes, changes, Shown("Setup_Button_Go_To_Work"), Shown("Setup_Button_Apply"));
        });

        Assert.Equal(vm.ModelRows.Select(r => r.ModelName), boxes.Where(b => b.IsEditable).Select(b => b.Name));
        Assert.Equal(vm.ModelRows.Select(r => r.EffortName), boxes.Where(b => !b.IsEditable).Select(b => b.Name));
        Assert.Equal(6, changes);
        Assert.True(goToWork);
        Assert.False(apply);
    }

    [Fact]
    public async Task Picking_a_model_from_the_list_puts_its_name_in_the_row_and_typing_another_keeps_what_is_typed()
    {
        var vm = await WpfHost.RunAsync(OpenAsync);
        var row = vm.ModelRows.Single(r => r.Name == "Ejecutor");
        var option = row.Models.First(m => m.Model != row.ModelText);

        var (picked, typed, again) = WpfHost.Run(() =>
        {
            var view = Build(vm);
            vm.Step = SetupStep.Models;
            view.UpdateLayout();
            var box = Descendants<ComboBox>(view).First(c => c.IsEditable && AutomationProperties.GetName(c) == row.ModelName);
            box.SelectedItem = option;
            WpfHost.FlushBindings();
            var picked = row.ModelText;
            // Typed where the person types, as the keyboard does.
            ((TextBox)box.Template.FindName("PART_EditableTextBox", box)).Text = "sonnet";
            WpfHost.FlushBindings();
            var typed = row.ModelText;
            box.SelectedItem = option;
            WpfHost.FlushBindings();
            return (picked, typed, row.ModelText);
        });

        Assert.Equal(option.Model, picked);
        Assert.Equal("sonnet", typed);
        Assert.Equal(option.Model, again);
    }
}
