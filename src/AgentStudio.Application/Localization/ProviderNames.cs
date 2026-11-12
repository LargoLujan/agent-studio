using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Localization;

/// <summary>
/// The name a person knows a provider by (the product name, never translated), for the layers that give a coded text an argument
/// (docs/17 D-47): the id of a provider (<c>claude-code</c>) is never shown. It is the name <c>DebateTexts.Provider</c> paints.
/// </summary>
public static class ProviderNames
{
    public static string Of(ProviderId provider) =>
        provider == ProviderId.ClaudeCode ? "Claude Code"
        : provider == ProviderId.CodexCli ? "Codex CLI"
        : provider == ProviderId.GeminiCli ? "Gemini CLI"
        : provider.Value;
}
