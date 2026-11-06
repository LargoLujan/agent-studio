# Contributing to Agent Studio

Thanks for your interest. Agent Studio is a work-in-progress Windows desktop IDE (C#, .NET 10, WPF) maintained by one
person, so the process is light: open an issue, keep pull requests small, and keep CI green.

## Languages

- The user interface and the design documents under [docs/](https://github.com/LargoLujan/agent-studio/tree/main/docs)
  are written in **Spanish**. Code, identifiers, comments and exception messages are in **English**.
- Issues and pull requests in **English are welcome** (Spanish too). You do not need to write Spanish to contribute;
  if a change needs new UI text or doc updates in Spanish, say so in the pull request and the maintainer can help.

## Before you start

- For anything beyond a small fix, open an issue first (or comment on an existing one) so the approach can be agreed
  before you spend time on it. Planned work is listed under "Roadmap and Status" in the
  [README](https://github.com/LargoLujan/agent-studio#roadmap-and-status).
- Security problems: do **not** open an issue; follow [SECURITY.md](SECURITY.md).
- Everyone taking part follows the [Code of Conduct](CODE_OF_CONDUCT.md).

## Prerequisites

- **Windows 11.** `AgentStudio.Desktop` and the test project target `net10.0-windows10.0.22000.0` (Windows Hello
  interop).
- **.NET SDK 10.0.303** or a later 10.0 feature band, no previews, as pinned in
  [global.json](https://github.com/LargoLujan/agent-studio/blob/main/global.json).
- **Git for Windows.**
- **Microsoft Edge WebView2 Runtime** (code editor and terminal).
- **PowerShell 7 (`pwsh`)** and **Windows PowerShell**, plus access to the npm registry on the first build: it
  downloads Monaco and xterm.js with pinned SHA-512 hashes through `tools/assets/fetch-monaco.ps1` and
  `tools/assets/fetch-xterm.ps1`. These assets are never committed.
- **Claude Code CLI** and **Codex CLI**, signed in with your own accounts, only if you want to use the agent features or
  run the live provider tests. Building and running the default test suite does not need them.

Clone to a **short path** such as `C:\src\agent-studio`. The SQLite native library is loaded from the test output
folder, and a deep path can exceed the Windows `MAX_PATH` limit and make many tests fail for unrelated reasons.

## Build, run and test

```powershell
git clone https://github.com/LargoLujan/agent-studio.git C:\src\agent-studio
cd C:\src\agent-studio
dotnet build AgentStudio.slnx
dotnet run --project src/AgentStudio.Desktop
```

The build must finish with **zero warnings**: `Directory.Build.props` treats warnings as errors and enforces code style
in the build.

### Tests exactly as CI runs them

The [CI workflow](https://github.com/LargoLujan/agent-studio/blob/main/.github/workflows/ci.yml) runs on
`windows-latest` for every push to `main` and every pull request to `main`:

```powershell
dotnet restore AgentStudio.slnx
dotnet build AgentStudio.slnx -c Release --no-restore
dotnet test AgentStudio.slnx -c Release --no-build -- --filter-not-trait "Category=LocalTools" --filter-not-trait "Category=ConPty" --filter-not-trait "Category=Measurement" --filter-not-trait "Category=ProviderLive"
```

Tests run on Microsoft.Testing.Platform (xUnit v3). **Do not pass `-nologo`**: with it no test runs. Filters go after
`--`.

| Category | What it needs | In CI |
|---|---|---|
| `LocalTools` | Real local tools: git worktrees and delivery, Low integrity verification, the WebView2 editor bridge, CLI probes, the journal hook | No |
| `ConPty` | The real ConPTY terminal session | No |
| `Measurement` | Performance measurements | No |
| `ProviderLive` | Real Claude Code and Codex runs that **spend subscription quota** | No |

On your own machine you can also run the tools-based tests:

```powershell
# Skip the tests that use real local tools
dotnet test -- --filter-not-trait "Category=LocalTools"

# Run only those tests
dotnet test -- --filter-trait "Category=LocalTools"
```

Live provider tests are skipped unless you opt in with `$env:AGENTSTUDIO_PROVIDER_TESTS = '1'`. They spend quota on
your own subscription; do not run them unless you mean to, and never make a test launch a provider CLI with a prompt
without that opt-in.

### Test conventions

- One test project, `tests/AgentStudio.Tests`, organised by module, plus integration suites under `Integration/`.
- Use xUnit's `Assert` only. **FluentAssertions is not allowed** (its licence is non-commercial).
- The test project is a WPF project: add `using System.IO;` when you use `Path`, `File` or `Directory`.
- Tag tests that need real local tools, ConPTY, measurements or a provider with the matching `Category` trait, so CI
  and other contributors can filter them out.

## Repository layout

```text
AgentStudio.slnx            Solution
global.json                 Pins the .NET SDK and selects Microsoft.Testing.Platform
Directory.Build.props       Shared build settings (nullable, warnings as errors, analysers)
Directory.Packages.props    Central package versions
src/
  AgentStudio.Domain/          Pure rules, no dependencies
  AgentStudio.Application/     Use cases and ports (interfaces)
  AgentStudio.Orchestration/   Engine, planner, verification, integration and delivery
  AgentStudio.Debate/          Debate Arena, judge, audits, challenges
  AgentStudio.Providers/       Claude Code, Codex and Gemini CLI adapters and probes
  AgentStudio.Observability/   Usage, context health, quota, logging
  AgentStudio.Memory/          Project memory, context packing, journal, semantic index
  AgentStudio.Security/        Approvals, path policy, secret redaction
  AgentStudio.Infrastructure/  Processes, isolation, ConPTY, git, SQLite, embeddings
  AgentStudio.SkillsMcp/       Read-only local MCP server
  AgentStudio.Desktop/         WPF app and the single composition root
tests/AgentStudio.Tests/    xUnit v3 tests by module, plus Integration/Phase*
tools/assets/               Scripts that fetch Monaco and xterm.js
spikes/                     Throw-away research harnesses, not part of the solution
docs/                       Design documents, ADRs, research and evidence (Spanish)
```

## Architecture rules

Dependencies point inwards
([ADR-0002](https://github.com/LargoLujan/agent-studio/blob/main/docs/adr/0002-capas-y-puertos.md), in Spanish):

- `Domain` depends on nothing.
- `Application` depends only on `Domain` and defines every port.
- The modules (`Orchestration`, `Debate`, `Providers`, `Observability`, `Memory`, `Security`, `Infrastructure`, and
  `SkillsMcp`) depend only on `Application` and never reference each other.
- `Desktop` is the only composition root.

Because the modules do not reference each other's projects, the compiler blocks shortcuts between them. Do not add such
a reference: collaboration goes through a port defined in `Application` and wired in `Desktop`.

## Architecture decision records

Relevant decisions are recorded as ADRs in
[docs/adr/](https://github.com/LargoLujan/agent-studio/tree/main/docs/adr) (in Spanish). An index with English
summaries is in the README.

- Each ADR has a status with its date, the context, the decision, the alternatives and the consequences.
- Accepted ADRs are not rewritten. A later change is recorded as a dated addendum at the end of the ADR or as a new ADR
  that amends the earlier one, so the history of the decision stays readable.
- If your change alters a recorded decision, or makes a new one, propose the ADR (or the addendum) in the same pull
  request. Ask in the issue for the number: some numbers are already reserved for planned phases.

## Branches, pull requests and commits

1. Fork the repository and create a branch from `main` with a short descriptive name.
2. Keep each pull request small and about one thing. Link its issue in the description (`Closes #123`).
3. Describe what changes and how you checked it (commands and results). The
   [pull request template](.github/pull_request_template.md) has the checklist.
4. CI must be green. The maintainer reviews and merges.

Commit messages in this repository are one descriptive sentence that says what changed and why, without type prefixes
(no `feat:` or `fix:`). Most history is in Spanish; English is fine.

Nothing in the UI may simulate a feature: what is not implemented is not shown, or is shown as pending.

## Provider and credential rules

- Never read, copy or log the CLIs' credentials or profile folders (`~/.claude`, `~/.codex`, `~/.gemini`, `~/.agents`).
- Free text goes to a CLI through stdin, never as a command-line argument, and the permission mode is always passed
  explicitly.
- Status probes only use `--version`, `claude auth status` and `codex login status` (plus the opt-in, read-only Codex
  quota read documented in `CLAUDE.md`).

## The prompt journal

If you use Claude Code in this repository, the project hook in `.claude/settings.json` runs on every prompt and appends a
line with an excerpt of it (up to 160 characters) to `.agentstudio/bitacora/YYYY-MM-DD.md`
([ADR-0008](https://github.com/LargoLujan/agent-studio/blob/main/docs/adr/0008-bitacora-obligatoria.md), in Spanish).
That folder is versioned and the repository is public, so never paste secrets into prompts. The journal is append-only:
never edit or remove existing lines. Mention in your pull request whether it includes journal lines.

## Licence

Agent Studio is released under the [MIT License](LICENSE). By submitting a contribution you agree that it is licensed
under the same terms.
