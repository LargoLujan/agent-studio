using AgentStudio.Application.Localization;
using AgentStudio.Desktop.Localization;

namespace AgentStudio.Desktop.Orchestrator;

/// <summary>
/// The detail of a failure of the orchestrator, for the person (docs/17 D-47, ADR-0054 addendum of 2026-11-12): a refusal with a
/// code (<see cref="UserTextException"/>) is painted from the resources in the language in use; any other failure is a technical
/// one and shows its message, which is in English. The screens frame it with a sentence of their own.
/// </summary>
public static class OrchestratorErrorText
{
    public static string Detail(Exception exception, IUiTextProvider? texts = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        texts ??= Texts.Current;
        return exception is UserTextException coded ? texts.Render(coded.Text) : exception.Message;
    }
}
