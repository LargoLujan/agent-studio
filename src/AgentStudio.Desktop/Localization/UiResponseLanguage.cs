using AgentStudio.Application.Prompts;

namespace AgentStudio.Desktop.Localization;

/// <summary>
/// The language the agents write in for the person (V44, docs/17 D-48, ADR-0054): the language of the interface the person chose in
/// «Ajustes», the one <see cref="Texts.UseLanguage"/> applied at startup. It is read from the applied provider, never from the
/// culture of the thread that asks, so the same choice gives the same prompt on any thread.
/// </summary>
internal sealed class UiResponseLanguage : IResponseLanguage
{
    public string Tag => Texts.Current.Culture.Name;
}
