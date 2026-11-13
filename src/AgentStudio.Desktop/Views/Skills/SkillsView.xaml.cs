using System.Windows;
using System.Windows.Controls;
using AgentStudio.Desktop.Skills;

namespace AgentStudio.Desktop.Views.Skills;

/// <summary>The «Skills» document: the catalog, the card of a skill and the recommendations; bindings only, but for the access key of GitHub.</summary>
public partial class SkillsView : UserControl
{
    public SkillsView() => InitializeComponent();

    /// <summary>
    /// The access key goes from the password box to <see cref="SkillsDocumentViewModel.SaveTokenAsync"/> as an argument and the box is emptied at once, before
    /// anything is awaited: nothing binds it, and nothing keeps it. Windows Hello is asked inside the call.
    /// </summary>
    private async void OnSaveTokenClick(object sender, RoutedEventArgs e)
    {
        var typed = TokenBox.Password;
        TokenBox.Clear();
        if (DataContext is SkillsDocumentViewModel document && typed.Length > 0)
        {
            await document.SaveTokenAsync(typed);
        }
    }
}
