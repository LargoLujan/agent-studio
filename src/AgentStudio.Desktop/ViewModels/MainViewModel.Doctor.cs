using AgentStudio.Desktop.Doctor;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>The panel of Auto Doctor inside the shell (Phase 6, docs/20 section 6.1): it knows the open project and where «Ir a…» can go.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The screens a fix of Auto Doctor can open: the codes its checks name (<c>setup</c>, <c>settings</c>, <c>skills</c>).</summary>
    private const string DoctorSetup = "setup";
    private const string DoctorSettings = "settings";
    private const string DoctorSkills = "skills";

    /// <summary>The panel of Auto Doctor: five sections over one run of every source, with the rows of the providers inside (docs/20 D-04).</summary>
    public DoctorViewModel Doctor { get; private set; } = null!;

    private void InitializeDoctor(DoctorViewModel? doctor)
    {
        // Without a composed panel (tests, a shell built by hand) the panel still opens, with the providers alone, as before Phase 6.
        Doctor = doctor ?? new DoctorViewModel(null, Diagnostics, null, null, TimeProvider.System);
        Doctor.ProjectRoot = () => ProjectPath;

        var targets = new HashSet<string>(StringComparer.Ordinal) { DoctorSkills };
        if (_setupFactory is not null)
        {
            targets.Add(DoctorSetup);
        }

        if (_settingsDocument is not null)
        {
            targets.Add(DoctorSettings);
        }

        Doctor.Navigation = new DoctorNavigation(targets, GoFromDoctor);
    }

    /// <summary>«Ir a…»: opens the screen by the same command of the activity bar that opens it; a target it does not know does nothing.</summary>
    private void GoFromDoctor(string target)
    {
        var command = target switch
        {
            DoctorSetup => (System.Windows.Input.ICommand)OpenSetupCommand,
            DoctorSettings => OpenSettingsCommand,
            DoctorSkills => OpenSkillsCommand,
            _ => null,
        };
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }
}
