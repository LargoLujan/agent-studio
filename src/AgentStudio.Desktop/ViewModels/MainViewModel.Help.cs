using System.Windows.Input;
using AgentStudio.Desktop.Help;
using AgentStudio.Desktop.Localization;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.ViewModels;

// What the shell shows of the help (docs/17 D-55, ADR-0056): «Ayuda» in the activity bar, F1 (the article of the screen that has the
// focus), the «Más ayuda» of each ⓘ (the article of its section) and the buttons of the answer of the agent, which open a place of
// the app. The tab itself, its search and its agent are in Desktop/Help.
public sealed partial class MainViewModel
{
    private HelpDocumentViewModel? _help;

    private void InitializeHelp(HelpDocumentViewModel? help)
    {
        _help = help;
        if (help is not null)
        {
            help.Navigate = OpenHelpAction;
        }
    }

    /// <summary>«Ayuda», only when its document was composed.</summary>
    private IEnumerable<ActivityItemViewModel> HelpActivities()
    {
        if (_help is not null)
        {
            yield return new ActivityItemViewModel("help", Texts.Get("Help_Activity"), "", null, null, OpenHelpCommand) { Shortcut = "F1" };
        }
    }

    /// <summary>
    /// «Ayuda» and <c>F1</c>: the help on the article of the screen the person was in (the tab itself, when it already has the focus).
    /// What the card of the agent says of that screen is taken now, before the tab takes the focus.
    /// </summary>
    [RelayCommand]
    private void OpenHelp()
    {
        if (_help is null)
        {
            return;
        }

        if (!ReferenceEquals(ActiveContent, _help))
        {
            _help.ShowFor(HelpScreens.Of(ActiveContent), ActiveContent, StatusMessage, ProjectPath);
        }

        AddDocument(_help);
    }

    /// <summary>The «Más ayuda» of an ⓘ: the article of its section, by the key of the control. Not offered for a key without an article.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenInfoHelp))]
    private void OpenInfoHelp(string? infoKey)
    {
        if (_help is null)
        {
            return;
        }

        if (!ReferenceEquals(ActiveContent, _help))
        {
            _help.ShowFor(HelpScreens.Of(ActiveContent), ActiveContent, StatusMessage, ProjectPath);
        }

        if (_help.OpenInfoKey(infoKey))
        {
            AddDocument(_help);
        }
    }

    private bool CanOpenInfoHelp(string? infoKey) => _help?.HasInfoArticle(infoKey) == true;

    /// <summary>An action of the answer of the agent, by its id in the closed list: it opens a place of the app and changes nothing. False for an id that is not in the list.</summary>
    private bool OpenHelpAction(string id)
    {
        ICommand? command = id switch
        {
            HelpActionCatalog.OpenOrchestrator => OpenOrchestratorCommand,
            HelpActionCatalog.OpenAgents => OpenAgentsCommand,
            HelpActionCatalog.OpenTools => OpenToolsCommand,
            HelpActionCatalog.OpenSkills => OpenSkillsCommand,
            HelpActionCatalog.OpenUsage => OpenUsageCommand,
            HelpActionCatalog.OpenMemory => OpenMemoryCommand,
            HelpActionCatalog.OpenDebates => OpenDebatesCommand,
            HelpActionCatalog.OpenAudit => OpenAuditCommand,
            HelpActionCatalog.OpenSettings => OpenSettingsCommand,
            _ => null,
        };
        if (command is null || !command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }
}
