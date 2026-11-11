using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using AgentStudio.Desktop.Localization;

namespace AgentStudio.Desktop.Controls.Info;

/// <summary>
/// The ⓘ button of a complex section (docs/17 D-54, F13-19): a small button next to the title of the section that opens a small
/// window with «Qué es esto» (two or three sentences), «Qué puedes hacer aquí» (a list of actions) and, when there is an article
/// of the help for it, «Más ayuda». Its texts come from the resources by the key of the section, <see cref="InfoKey"/>:
/// <list type="bullet">
/// <item><c>&lt;InfoKey&gt;_Title</c>: the name of the section, for the accessible name «Información sobre &lt;sección&gt;»
/// (unless <see cref="Topic"/> gives it);</item>
/// <item><c>&lt;InfoKey&gt;_What</c>: what it is;</item>
/// <item><c>&lt;InfoKey&gt;_Actions</c>: what can be done there, one action per line.</item>
/// </list>
/// A click, Intro or Espacio opens it, Esc closes it and gives the focus back to the button, and it closes when the person
/// clicks elsewhere: it covers nothing needed to go on. «Más ayuda» is shown only with <see cref="MoreHelpCommand"/>: the help
/// arrives in Phase 14, and a button that leads nowhere is not shown.
/// </summary>
public partial class InfoButton : UserControl
{
    public static readonly DependencyProperty InfoKeyProperty = DependencyProperty.Register(
        nameof(InfoKey), typeof(string), typeof(InfoButton), new PropertyMetadata(null, OnTextsChanged));

    public static readonly DependencyProperty TopicProperty = DependencyProperty.Register(
        nameof(Topic), typeof(string), typeof(InfoButton), new PropertyMetadata(null, OnTextsChanged));

    public static readonly DependencyProperty TextProviderProperty = DependencyProperty.Register(
        nameof(TextProvider), typeof(IUiTextProvider), typeof(InfoButton), new PropertyMetadata(null, OnTextsChanged));

    public static readonly DependencyProperty MoreHelpCommandProperty = DependencyProperty.Register(
        nameof(MoreHelpCommand), typeof(ICommand), typeof(InfoButton), new PropertyMetadata(null, OnTextsChanged));

    public InfoButton()
    {
        InitializeComponent();
        Toggle.PreviewKeyDown += OnKeyDown;
        Card.PreviewKeyDown += OnKeyDown;
        Bubble.Opened += (_, _) => Card.Focus();
        Refresh();
    }

    /// <summary>The key of the section in the resources: its texts are <c>&lt;InfoKey&gt;_Title</c>, <c>_What</c> and <c>_Actions</c>.</summary>
    public string? InfoKey
    {
        get => (string?)GetValue(InfoKeyProperty);
        set => SetValue(InfoKeyProperty, value);
    }

    /// <summary>The name of the section, when the screen already has it; otherwise <c>&lt;InfoKey&gt;_Title</c>.</summary>
    public string? Topic
    {
        get => (string?)GetValue(TopicProperty);
        set => SetValue(TopicProperty, value);
    }

    /// <summary>The texts of a language other than the one of the interface (a test, a render per language); null for <see cref="Texts.Current"/>.</summary>
    public IUiTextProvider? TextProvider
    {
        get => (IUiTextProvider?)GetValue(TextProviderProperty);
        set => SetValue(TextProviderProperty, value);
    }

    /// <summary>What «Más ayuda» does (opens the article of the help of the section, Phase 14); without it, «Más ayuda» is not shown.</summary>
    public ICommand? MoreHelpCommand
    {
        get => (ICommand?)GetValue(MoreHelpCommandProperty);
        set => SetValue(MoreHelpCommandProperty, value);
    }

    /// <summary>Whether its small window is open.</summary>
    public bool IsOpen
    {
        get => Toggle.IsChecked == true;
        set => Toggle.IsChecked = value;
    }

    /// <summary>The name of the section as the button says it.</summary>
    public string SectionName => Topic is { Length: > 0 } topic ? topic : Provider.Get(InfoKey + "_Title");

    /// <summary>What the section is, as the small window says it.</summary>
    public string Explanation => WhatText.Text;

    /// <summary>The actions of the section, one per line of its text.</summary>
    public IReadOnlyList<string> Actions => (IReadOnlyList<string>?)ActionsList.ItemsSource ?? [];

    private IUiTextProvider Provider => TextProvider ?? Texts.Current;

    private static void OnTextsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((InfoButton)d).Refresh();

    private void Refresh()
    {
        var texts = Provider;
        var hasKey = !string.IsNullOrWhiteSpace(InfoKey);
        var section = hasKey || Topic is { Length: > 0 } ? SectionName : string.Empty;
        AutomationProperties.SetName(Toggle, texts.Format("Info_Button_Name", section));
        AutomationProperties.SetHelpText(Toggle, texts.Get("Info_CloseHint"));
        WhatHeading.Text = texts.Get("Info_WhatIsThis");
        ActionsHeading.Text = texts.Get("Info_WhatYouCanDo");
        WhatText.Text = hasKey ? texts.Get(InfoKey + "_What") : string.Empty;
        ActionsList.ItemsSource = hasKey ? Lines(texts.Get(InfoKey + "_Actions")) : [];
        AutomationProperties.SetName(Card, section);
        AutomationProperties.SetHelpText(Card, WhatText.Text);
        MoreHelp.Content = texts.Get("Info_MoreHelp");
        MoreHelp.Visibility = MoreHelpCommand is { } command && command.CanExecute(InfoKey) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static IReadOnlyList<string> Lines(string text) =>
        [.. text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !IsOpen)
        {
            return;
        }

        IsOpen = false;
        Toggle.Focus();
        e.Handled = true;
    }

    private void OnMoreHelp(object sender, RoutedEventArgs e)
    {
        if (MoreHelpCommand is { } command && command.CanExecute(InfoKey))
        {
            IsOpen = false;
            command.Execute(InfoKey);
        }
    }
}
