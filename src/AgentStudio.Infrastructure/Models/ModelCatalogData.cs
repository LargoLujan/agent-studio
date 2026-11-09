namespace AgentStudio.Infrastructure.Models;

/// <summary>
/// The local catalog of models of this version of Agent Studio (docs/17 D-23, ADR-0050): <c>models.catalog.json</c>, versioned
/// with the app and reviewed with each version of it. It is data, not code: <see cref="ModelCatalogReader"/> reads it and
/// checks it, and every entry names its sources. There are no prices (docs/14 D-06).
/// </summary>
/// <remarks>
/// <para>
/// It lives in a raw string of C# so the project files need no change (they belong to the P0 of the phase, docs/17 §9.1); moving
/// it to an embedded <c>models.catalog.json</c> is one line in <c>AgentStudio.Infrastructure.csproj</c> and this file read as a
/// resource.
/// </para>
/// <list type="bullet">
/// <item><b>The order is the preference.</b> Within a level, the recommendation takes the first entry of its CLI
/// (<c>ModelRecommender</c>): Opus before Fable at «máximo», Haiku 5.5 at «ligero», GPT-6 Luna (the model the account
/// accepted in the real runs) before Terra.</item>
/// <item><b>Claude Code 2.1.290.</b> <c>claude --help</c> gives the aliases of <c>--model</c> («an alias for the latest model
/// (e.g. 'fable', 'opus', or 'sonnet') or a model's full name») and the levels of <c>--effort</c>. Which levels each model
/// takes comes from the reference of the API of Anthropic (its catalogue of models of 2026-10-06), not from the CLI: it is
/// not verified with the CLI (docs/17 §13). <c>haiku</c> is its own entry, without effort: which model it leads to in this
/// version is not verified, and Haiku 4.5 takes no effort. Mythos 5.1 is only for Project Glasswing, and <c>opusplan</c>
/// mixes two models: neither is classified.</item>
/// <item><b>Codex 0.160.0.</b> The models and their place come from the page of models of OpenAI (2026-11-08): GPT-6 Astra
/// the most capable, GPT-6.1 Sol near Astra at a lower cost, GPT-6.1 Terra fast and efficient (Codex 0.159.0 or later) and
/// GPT-6 Luna the most efficient. No page lists the levels of each model for the CLI, so each gets low to xhigh, what every
/// source names; <c>minimal</c> (named by no source today) and <c>max</c> (Luna, says the page) are not given (ADR-0050).</item>
/// </list>
/// </remarks>
public static class ModelCatalogData
{
    /// <summary>The catalog, as JSON.</summary>
    public const string Json = """
        {
          "version": 1,
          "checkedOn": "2026-11-08",
          "sources": [
            {
              "id": "claude-help",
              "description": "claude --help of Claude Code 2.1.290: --model takes an alias for the latest model ('fable', 'opus', 'sonnet') or a full name; --effort takes low, medium, high, xhigh or max",
              "location": "claude --help",
              "checkedOn": "2026-11-08"
            },
            {
              "id": "anthropic-models",
              "description": "Anthropic's catalogue of current models (Claude API reference): ids, context windows and the effort levels each model accepts in the API",
              "location": "https://platform.claude.com/docs/en/about-claude/models/overview",
              "checkedOn": "2026-10-06"
            },
            {
              "id": "codex-help",
              "description": "codex exec --help of Codex 0.160.0: -m <MODEL>, and -c key=value whose value is parsed as TOML or else taken literally",
              "location": "codex exec --help",
              "checkedOn": "2026-11-08"
            },
            {
              "id": "codex-models",
              "description": "OpenAI's page of the models of Codex: gpt-6-astra (most capable), gpt-6.1-sol (near Astra at a lower cost), gpt-6.1-terra (fast and efficient, Codex CLI 0.159.0 or later) and gpt-6-luna (most efficient); reasoning effort Low, Medium, High and Extra High",
              "location": "https://learn.chatgpt.com/docs/models",
              "checkedOn": "2026-11-08"
            },
            {
              "id": "codex-config-sample",
              "description": "OpenAI's sample configuration of Codex: model_reasoning_effort, whose supported levels vary by model and client",
              "location": "https://learn.chatgpt.com/docs/config-file/config-sample",
              "checkedOn": "2026-11-08"
            },
            {
              "id": "account-runs",
              "description": "The real runs of Phases 2 and 5 with the user's account: Codex accepted gpt-6-luna",
              "location": "docs/06-integracion-proveedores.md",
              "checkedOn": "2026-10-20"
            }
          ],
          "rules": {
            "orchestratorMaximumFromMandatoryRequirements": 5,
            "orchestratorMaximumFromFiles": 2000,
            "orchestratorEffort": "high",
            "executorEffort": "medium",
            "judgeEffort": "high"
          },
          "models": [
            {
              "provider": "claude-code",
              "testedCliVersion": "2.1.290",
              "names": ["claude-opus-5-5", "opus"],
              "tier": "maximum",
              "efforts": ["low", "medium", "high", "xhigh", "max"],
              "note": "model.note.efforts-from-api",
              "sources": ["claude-help", "anthropic-models"]
            },
            {
              "provider": "claude-code",
              "testedCliVersion": "2.1.290",
              "names": ["claude-fable-5-1", "fable"],
              "tier": "maximum",
              "efforts": ["low", "medium", "high", "xhigh", "max"],
              "note": "model.note.fable-unverified",
              "sources": ["claude-help", "anthropic-models"]
            },
            {
              "provider": "claude-code",
              "testedCliVersion": "2.1.290",
              "names": ["claude-opus-5"],
              "tier": "maximum",
              "efforts": ["low", "medium", "high", "xhigh", "max"],
              "note": "model.note.efforts-from-api",
              "sources": ["anthropic-models"]
            },
            {
              "provider": "claude-code",
              "testedCliVersion": "2.1.290",
              "names": ["claude-fable-5"],
              "tier": "maximum",
              "efforts": ["low", "medium", "high", "xhigh", "max"],
              "note": "model.note.fable-unverified",
              "sources": ["anthropic-models"]
            },
            {
              "provider": "claude-code",
              "testedCliVersion": "2.1.290",
              "names": ["claude-opus-4-8"],
              "tier": "maximum",
              "efforts": ["low", "medium", "high", "xhigh", "max"],
              "note": "model.note.efforts-from-api",
              "sources": ["anthropic-models"]
            },
            {
              "provider": "claude-code",
              "testedCliVersion": "2.1.290",
              "names": ["claude-opus-4-7"],
              "tier": "maximum",
              "efforts": ["low", "medium", "high", "xhigh", "max"],
              "note": "model.note.efforts-from-api",
              "sources": ["anthropic-models"]
            },
            {
              "provider": "claude-code",
              "testedCliVersion": "2.1.290",
              "names": ["claude-opus-4-6"],
              "tier": "maximum",
              "efforts": ["low", "medium", "high", "max"],
              "note": "model.note.efforts-from-api",
              "sources": ["anthropic-models"]
            },
            {
              "provider": "claude-code",
              "testedCliVersion": "2.1.290",
              "names": ["claude-sonnet-5-5", "sonnet"],
              "tier": "balanced",
              "efforts": ["low", "medium", "high", "xhigh", "max"],
              "note": "model.note.efforts-from-api",
              "sources": ["claude-help", "anthropic-models"]
            },
            {
              "provider": "claude-code",
              "testedCliVersion": "2.1.290",
              "names": ["claude-sonnet-5"],
              "tier": "balanced",
              "efforts": ["low", "medium", "high", "xhigh", "max"],
              "note": "model.note.efforts-from-api",
              "sources": ["anthropic-models"]
            },
            {
              "provider": "claude-code",
              "testedCliVersion": "2.1.290",
              "names": ["claude-sonnet-4-6"],
              "tier": "balanced",
              "efforts": ["low", "medium", "high", "max"],
              "note": "model.note.efforts-from-api",
              "sources": ["anthropic-models"]
            },
            {
              "provider": "claude-code",
              "testedCliVersion": "2.1.290",
              "names": ["claude-haiku-5-5"],
              "tier": "light",
              "efforts": ["low", "medium", "high", "xhigh", "max"],
              "note": "model.note.efforts-from-api",
              "sources": ["anthropic-models"]
            },
            {
              "provider": "claude-code",
              "testedCliVersion": "2.1.290",
              "names": ["haiku"],
              "tier": "light",
              "efforts": [],
              "note": "model.note.haiku-alias-unverified",
              "sources": ["anthropic-models"]
            },
            {
              "provider": "claude-code",
              "testedCliVersion": "2.1.290",
              "names": ["claude-haiku-4-5"],
              "tier": "light",
              "efforts": [],
              "note": "model.note.no-effort",
              "sources": ["anthropic-models"]
            },
            {
              "provider": "codex-cli",
              "testedCliVersion": "0.160.0",
              "names": ["gpt-6-astra"],
              "tier": "maximum",
              "efforts": ["low", "medium", "high", "xhigh"],
              "note": "model.note.codex-efforts-unverified",
              "sources": ["codex-help", "codex-models", "codex-config-sample"]
            },
            {
              "provider": "codex-cli",
              "testedCliVersion": "0.160.0",
              "names": ["gpt-6.1-sol"],
              "tier": "balanced",
              "efforts": ["low", "medium", "high", "xhigh"],
              "note": "model.note.codex-efforts-unverified",
              "sources": ["codex-help", "codex-models", "codex-config-sample"]
            },
            {
              "provider": "codex-cli",
              "testedCliVersion": "0.160.0",
              "names": ["gpt-6-luna"],
              "tier": "light",
              "efforts": ["low", "medium", "high", "xhigh"],
              "note": "model.note.codex-account-verified",
              "sources": ["codex-help", "codex-models", "codex-config-sample", "account-runs"]
            },
            {
              "provider": "codex-cli",
              "testedCliVersion": "0.160.0",
              "names": ["gpt-6.1-terra"],
              "tier": "light",
              "efforts": ["low", "medium", "high", "xhigh"],
              "note": "model.note.codex-efforts-unverified",
              "sources": ["codex-help", "codex-models", "codex-config-sample"]
            }
          ]
        }
        """;
}
