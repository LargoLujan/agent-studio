using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.ViewModels;

namespace AgentStudio.Desktop.Setup;

/// <summary>
/// The tab of the configurator of one project (docs/17 section 4.6, ADR-0055): the <see cref="SetupViewModel"/> in a document of the
/// dock, one per project for the session. The shell creates it, reads the project into it (<see cref="SetupViewModel.LoadAsync"/>)
/// and listens to <see cref="SetupViewModel.Applied"/>.
/// </summary>
public sealed class SetupDocumentViewModel : DocumentViewModel
{
    public const string IdPrefix = "setup:";

    public SetupDocumentViewModel(string projectRoot, SetupViewModel setup, string projectName)
        : base(IdPrefix + Path.GetFullPath(projectRoot).ToUpperInvariant(), Texts.Format("Shell_Setup_Title", projectName))
    {
        ArgumentNullException.ThrowIfNull(setup);
        ProjectRoot = projectRoot;
        Setup = setup;
    }

    public string ProjectRoot { get; }

    public SetupViewModel Setup { get; }
}

/// <summary>
/// Which projects were already offered the configurator on the welcome tab (docs/04 section 21.9): suggested the first time a project
/// is opened and never again, and never opened by itself. One empty marker file per project in the folder of the options, named
/// after a hash of its path, so nothing of the project is written and the name says nothing of it.
/// </summary>
public sealed class SetupSuggestions
{
    private readonly string _folder;

    public SetupSuggestions(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = Path.GetFullPath(folder);
    }

    /// <summary>The configurator was already suggested for this project.</summary>
    public bool WasSuggested(string projectRoot) => File.Exists(MarkerOf(projectRoot));

    /// <summary>Notes that it was suggested. Best effort: a folder that cannot be written only means it is suggested again.</summary>
    public void MarkSuggested(string projectRoot)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllBytes(MarkerOf(projectRoot), []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The suggestion is only a convenience: it will be made again.
        }
    }

    private string MarkerOf(string projectRoot)
    {
        var normalized = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        return Path.Combine(_folder, "suggested-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32] + ".marker");
    }
}
