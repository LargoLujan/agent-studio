using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Theming;
using AgentStudio.Desktop.Views;
using AgentStudio.Desktop.Views.Background;
using AgentStudio.Desktop.Views.Orchestrator;
using AgentStudio.Tests.Desktop.Background;
using AgentStudio.Tests.Desktop.Orchestrator;

namespace AgentStudio.Tests.Desktop.Theming;

/// <summary>
/// Where the keyboard goes in a window activated with nothing focused in it (<see cref="InitialFocus"/>): the question of the
/// exit opened from the tray and the panel of the tray took no key until a Tab, so Esc did nothing. The decision is checked
/// on views that are laid out but never shown: no window is activated and nothing takes the focus.
/// </summary>
public sealed class InitialFocusTests
{
    private static T LaidOut<T>(T element, double width = 640, double height = 900)
        where T : FrameworkElement
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        WpfHost.FlushBindings();
        element.UpdateLayout();
        return element;
    }

    private static Button Named(string name, bool isDefault = false, bool isCancel = false) =>
        new() { Content = name, IsDefault = isDefault, IsCancel = isCancel };

    private static string? ChosenName(Panel root) => (InitialFocus.ChooseTarget(LaidOut(root)) as ContentControl)?.Content as string;

    [Fact]
    public void A_window_that_holds_the_keyboard_itself_with_no_control_needs_the_first_focus()
    {
        // The live case: the question opened from the menu of the tray, activated from another process. WPF gave the keyboard
        // to the window itself, so IsKeyboardFocusWithin was true and Esc reached no button until a Tab.
        var (itself, nothing, elsewhere) = WpfHost.Run(() =>
        {
            var window = new ExitPromptWindow { DataContext = new ExitPromptViewModel(new ExitQuestion([], OffersKeepInTray: false)) };
            return (
                InitialFocus.NeedsFocus(window, window),
                InitialFocus.NeedsFocus(window, null),
                InitialFocus.NeedsFocus(window, new Button()));
        });

        Assert.True(itself, "Only the window has the keyboard: no button gets Esc or Enter.");
        Assert.True(nothing);
        Assert.True(elsewhere, "An element of another window does not count as focused here.");
    }

    [Fact]
    public void A_control_of_the_window_that_has_the_keyboard_keeps_it()
    {
        var (button, inPopup) = WpfHost.Run(() =>
        {
            var cancel = Named("Cancelar", isCancel: true);
            var listEntry = Named("Una entrada de la lista de un desplegable");
            var window = new Window
            {
                Content = new Grid { Children = { cancel, new System.Windows.Controls.Primitives.Popup { Child = listEntry } } },
            };
            return (InitialFocus.NeedsFocus(window, cancel), InitialFocus.NeedsFocus(window, listEntry));
        });

        Assert.False(button, "A button of the window has the keyboard: it stays there.");
        Assert.False(inPopup, "An element in a popup of the window (an open list) is in the window too.");
    }

    [Fact]
    public void The_default_button_comes_first()
    {
        var chosen = WpfHost.Run(() => ChosenName(new StackPanel
        {
            Children = { new TextBox(), Named("Guardar"), Named("Cancelar", isDefault: true), Named("Descartar", isCancel: true) },
        }));

        Assert.Equal("Cancelar", chosen);
    }

    [Fact]
    public void Without_a_default_button_the_cancel_button()
    {
        var chosen = WpfHost.Run(() => ChosenName(new StackPanel
        {
            Children = { Named("Aprobar"), Named("Denegar", isCancel: true) },
        }));

        Assert.Equal("Denegar", chosen);
    }

    [Fact]
    public void Without_either_the_first_element_the_tab_key_reaches_in_the_order_of_the_tree()
    {
        var chosen = WpfHost.Run(() => ChosenName(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "Estado del trabajo" },
                new Border { Child = new DockPanel { Children = { Named("Abrir Agent Studio") } } },
                Named("Otro"),
            },
        }));

        Assert.Equal("Abrir Agent Studio", chosen);
    }

    [Fact]
    public void What_cannot_take_the_focus_is_passed_over_with_everything_under_it()
    {
        var chosen = WpfHost.Run(() =>
        {
            var noTab = new StackPanel { Children = { Named("Dentro de un panel sin Tab", isDefault: true) } };
            KeyboardNavigation.SetTabNavigation(noTab, KeyboardNavigationMode.None);
            return ChosenName(new StackPanel
            {
                Children =
                {
                    new Button { Content = "Plegado", IsDefault = true, Visibility = Visibility.Collapsed },
                    new Border { Visibility = Visibility.Hidden, Child = Named("Dentro de algo oculto", isDefault: true) },
                    new Border { IsEnabled = false, Child = Named("Dentro de algo desactivado", isCancel: true) },
                    new Button { Content = "Desactivado", IsDefault = true, IsEnabled = false },
                    new Button { Content = "Fuera del Tab", IsDefault = true, IsTabStop = false },
                    new Button { Content = "No enfocable", IsCancel = true, Focusable = false },
                    noTab,
                    new ScrollViewer { Content = new TextBlock { Text = "Un ScrollViewer no es una parada del Tab" } },
                    Named("El primero de verdad"),
                },
            });
        });

        Assert.Equal("El primero de verdad", chosen);
    }

    [Fact]
    public void With_nothing_that_can_take_the_focus_there_is_no_target_and_the_root_is_never_one()
    {
        var (texts, rootOnly) = WpfHost.Run(() => (
            InitialFocus.ChooseTarget(LaidOut(new StackPanel { Children = { new TextBlock { Text = "Solo texto" } } })),
            InitialFocus.ChooseTarget(new Button { Content = "La raíz" })));

        Assert.Null(texts);
        Assert.Null(rootOnly);
    }

    [Fact]
    public void Locked_main_window_the_keyboard_goes_to_the_lock_screen_and_not_behind_it()
    {
        // MainWindow locked: the content is hidden and disabled, the lock screen is on top of it.
        var chosen = WpfHost.Run(() => (InitialFocus.ChooseTarget(LaidOut(new Grid
        {
            Children =
            {
                new Grid { Visibility = Visibility.Hidden, IsEnabled = false, Children = { Named("Abrir carpeta") } },
                new StackPanel { Children = { new TextBlock { Text = "Agent Studio está bloqueado" }, Named("Desbloquear con Windows Hello") } },
            },
        })) as ContentControl)?.Content);

        Assert.Equal("Desbloquear con Windows Hello", chosen);
    }

    [Fact]
    public void The_question_of_the_exit_opened_from_the_tray_takes_the_keyboard_on_cancelar_so_esc_cancels()
    {
        var (chosen, isCancel) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var question = new ExitQuestion([new ExitWorkLine("monorepo", "Ejecución 20261021-c4a1f2", "En ejecución", CanPause: true)], OffersKeepInTray: false);
            var model = new ExitPromptViewModel(question);
            var target = InitialFocus.ChooseTarget(LaidOut(new ExitPromptView { DataContext = model }, 620)) as Button;
            return (target?.Content, target?.IsCancel);
        });

        Assert.Equal("No salir", chosen);
        Assert.True(isCancel);
    }

    [Fact]
    public void The_question_of_unsaved_changes_takes_the_keyboard_on_its_cancel_button()
    {
        var (withSave, withoutSave) = WpfHost.Run(() => (
            (InitialFocus.ChooseTarget(LaidOut(new UnsavedChangesView { DataContext = new UnsavedChangesPromptViewModel(["Program.cs"], canSave: true) }, 520)) as Button)?.Content,
            (InitialFocus.ChooseTarget(LaidOut(new UnsavedChangesView { DataContext = new UnsavedChangesPromptViewModel(["Plan del orquestador"], canSave: false) }, 520)) as Button)?.Content));

        Assert.Equal("Cancelar", withSave);
        Assert.Equal("Volver", withoutSave);
    }

    [Fact]
    public void An_approval_takes_the_keyboard_on_denegar_never_on_aprobar()
    {
        var chosen = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var request = new ApprovalRequest("deliver-integration", "Entregar la ejecución 20261021-abc123 en main.", RiskLevel.Critical, null, [@"src\Calc.cs"], "p-1", "20261021-abc123");
            return (InitialFocus.ChooseTarget(LaidOut(new ApprovalView { DataContext = new ApprovalDialogViewModel(request) }, 600)) as Button)?.Content;
        });

        Assert.Equal("Denegar", chosen);
    }

    [Fact]
    public void The_panel_of_the_tray_takes_the_keyboard_on_its_first_button_so_esc_reaches_the_window()
    {
        var chosen = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            using var work = BackgroundSamples.Registry();
            var panel = new StatusPanelViewModel(work, PanelSamples.Clock(), openApp: () => { });
            return (InitialFocus.ChooseTarget(LaidOut(new StatusPanelView { DataContext = panel }, 540)) as Button)?.Content;
        });

        Assert.Equal("Abrir Agent Studio", chosen);
    }
}
