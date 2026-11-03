namespace AgentStudio.Domain.Providers;

/// <summary>
/// The integration used to reach a model (a CLI adapter or an API). Open-ended so new tools can be added without
/// touching the domain.
/// </summary>
public readonly record struct ProviderId
{
    public static readonly ProviderId ClaudeCode = new("claude-code");
    public static readonly ProviderId CodexCli = new("codex-cli");
    public static readonly ProviderId GeminiCli = new("gemini-cli");

    public ProviderId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// The company that trains and serves the model. Judge independence is checked on the vendor, not on the provider:
/// Claude Code and the Anthropic API are different providers but the same vendor.
/// </summary>
public readonly record struct VendorId
{
    public static readonly VendorId Anthropic = new("anthropic");
    public static readonly VendorId OpenAI = new("openai");
    public static readonly VendorId Google = new("google");

    public VendorId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>Which provider, vendor and (optionally) model an agent runs on.</summary>
public sealed record ModelBinding(ProviderId Provider, VendorId Vendor, string? Model = null);
