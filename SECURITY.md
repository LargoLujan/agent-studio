# Security Policy

## Supported versions

Agent Studio is a work in progress maintained by one person. Security fixes go to the `main` branch and into the
latest release on the [Releases](https://github.com/LargoLujan/agent-studio/releases) page, whether or not it is marked
as a pre-release. Older releases are not patched; update to the latest release or build `main` from source.

| Version | Supported |
|---|---|
| Latest release | Yes |
| `main` branch | Yes |
| Any older release | No |

## Reporting a vulnerability

Please do **not** open a public issue, pull request or discussion for a security problem.

Report it privately through GitHub:

1. Open the repository's security tab (it may be labelled **Security** or **Security & quality**).
2. Choose **Report a vulnerability**.
3. Fill in the form. Only the maintainer can see the report.

GitHub's guide to this feature:
[Privately reporting a security vulnerability](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/report-privately).

### What to include

- The commit (`git rev-parse HEAD`) or release you tested, and your Windows version.
- The provider CLIs involved and their versions (`claude --version`, `codex --version`), if any.
- The component affected (for example verification, worktrees and delivery, the decision log, the path policy, secret
  redaction, the local MCP server or the semantic index).
- Step-by-step reproduction: the project layout, the plan or settings used, and what an attacker controls (repository
  content, agent output, a file planted before a run…).
- What happened and what you expected, with logs if they help. Remove secrets, tokens and personal paths before you
  attach anything; the app's own logs live in `%LOCALAPPDATA%\AgentStudio\logs`.
- Whether the issue needs another process of the same user running at the same time (see the threat model below).

### What to expect

This is a single-maintainer, best-effort project with no service-level agreement. The aim is to acknowledge a report
within 7 days, keep you informed while it is investigated and fixed, and credit you in the advisory unless you prefer
otherwise. Please give reasonable time for a fix before any public disclosure.

Problems in the provider CLIs themselves (Claude Code, Codex CLI) or in third-party packages belong to their own
maintainers; report them there. If Agent Studio uses such a component in an unsafe way, report that here.

## Threat model in brief

The full threat model is [ADR-0018](https://github.com/LargoLujan/agent-studio/blob/main/docs/adr/0018-modelo-de-amenaza-fase-3.md)
(in Spanish). In short:

- **Personal use on your own machine.** Agent Studio runs with your Windows account, on your repositories. It does not
  call model APIs with its own keys: it launches the official Claude Code and Codex CLIs that you installed and signed in
  to with your own accounts, and it never reads their credentials. Agent runs spend your subscription quota.
- **Agents are untrusted workers.** Prompts go through stdin, permission modes are always passed explicitly, the
  orchestrator and the judge run read-only, and the default executor has file tools confined to its own worktree and no
  shell. Builds and tests that count as evidence are run by Agent Studio, not by the agent.
- **Inside the model** (findings are treated as open until fixed): repository content (Git configuration,
  `.gitattributes`, versioned scripts, submodules), agent output, and agents with their default capabilities, including
  files or links they leave behind before or after a step.
- **Accepted residual risks** (documented, not closed):
  - another process of the same user running at Medium integrity at the same time (for example an agent you gave a
    shell, a step marked `isolation: none`, or an unrelated program) can rewrite the decision log and race file checks;
  - verification runs under a Windows **Low integrity** token, which blocks writes outside its area but **not reads or
    network access** (also listed under "Known limitations" in the
    [README](https://github.com/LargoLujan/agent-studio#known-limitations));
  - executables are resolved through your `PATH`, which Agent Studio does not audit;
  - what each CLI writes into its own profile folder.
- **Your part.** Do not give agents a shell or run steps without isolation on repositories you do not trust. The daily
  prompt journal (`.agentstudio/bitacora/`) is versioned with each project, so do not paste secrets into prompts.

Stronger isolation of verification (AppContainer or a container without network), a network policy per agent, defences
against prompt injection and an encrypted credential vault are planned for Phase 7 and are **not** implemented yet; see
"Roadmap and Status" in the [README](https://github.com/LargoLujan/agent-studio#roadmap-and-status).
