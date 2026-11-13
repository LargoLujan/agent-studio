using System.Windows;
using System.Windows.Controls;
using AgentStudio.Desktop.Doctor;

namespace AgentStudio.Desktop.Views;

public partial class AutoDoctorView : UserControl
{
    public AutoDoctorView()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <summary>«Al abrir el panel» (docs/20 D-03): the checks run when the panel is shown, and again only when the person asks or a minute has passed.</summary>
    private async void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is not DoctorViewModel doctor)
        {
            return;
        }

        if (e.NewValue is true)
        {
            await doctor.OpenedAsync();
        }
        else
        {
            doctor.Cancel();
        }
    }
}
