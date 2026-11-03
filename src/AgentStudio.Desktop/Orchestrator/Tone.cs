namespace AgentStudio.Desktop.Orchestrator;

/// <summary>
/// The colour family of a piece of status text on the orchestrator screen. The view maps it to a theme brush; the text
/// always says the same thing in words, so the colour is never the only cue.
/// </summary>
public enum Tone
{
    Neutral,
    Active,
    Success,
    Warning,
    Error,
}
