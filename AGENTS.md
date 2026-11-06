# AGENTS.md

Instructions for AI coding agents (Codex, Claude Code, Copilot, Cursor and others) working in this repository.
Claude Code also reads [CLAUDE.md](CLAUDE.md), the maintainer's instructions in Spanish; this file states the same rules
in English for any tool. If the two ever disagree, `CLAUDE.md` wins and this file should be fixed.

## What this repository is

Agent Studio is a Windows desktop IDE (C#, .NET 10, WPF) that orchestrates the official Claude Code and Codex CLIs:
an orchestrator agent plans, executor agents work in isolated Git worktrees, Agent Studio builds and tests the result
itself, and a judge from the other vendor issues a verdict that deterministic rules validate. Decisions and evidence are
kept in an append-only, hash-chained log. Overview and status: [README.md](README.md). Design documents (in Spanish):
[docs/README.md](docs/README.md).

## Languages

- Code, identifiers, comments and exception messages: **English**.
- UI text and documentation under `docs/`: **Spanish (Spain)**.

## Repository map

| Path | Contents |
|---|---|
| `AgentStudio.slnx` | The solution (`.slnx` format) |
| `global.json` | .NET SDK 10.0.303 (`rollForward: latestFeature`, no previews) and Microsoft.Testing.Platform as test runner |
| `Directory.Build.props` | Nullable, warnings as errors, code style enforced in build |
| `Directory.Packages.props` | Central package versions; do not put versions in `.csproj` files of the solution |
| `src/AgentStudio.Domain/` | Pure rules (task graph, plans, decision log, verdicts, debates, semantic ranking); no I/O, no dependencies |
| `src/AgentStudio.Application/` | Use cases and every port (interface) |
| `src/AgentStudio.Orchestration/` | Engine, planner, verification runner, integration and delivery |
| `src/AgentStudio.Debate/` | Debate Arena, judge and dossiers, audits, challenges |
| `src/AgentStudio.Providers/` | CLI adapters (Claude Code, Codex, parked Gemini), probes, skill materialiser |
| `src/AgentStudio.Observability/` | Usage projection, context health, quota, file logging |
| `src/AgentStudio.Memory/` | Project memory, journal, context packer, semantic index, daily summary |
| `src/AgentStudio.Security/` | Approval gate, path access policy, secret redaction |
| `src/AgentStudio.Infrastructure/` | Processes, Low integrity isolation, ConPTY, git layer, SQLite stores and migrations, embeddings |
| `src/AgentStudio.SkillsMcp/` | Read-only local MCP server (skills recommender, project history tool) |
| `src/AgentStudio.Desktop/` | WPF app and the single composition root |
| `tests/AgentStudio.Tests/` | The only test project, by module, plus `Integration/Phase*` suites |
| `docs/adr/` | Architecture decision records (Spanish) |
| `.agentstudio/bitacora/` | Daily prompt journal of this repository (versioned) |
| `spikes/` | Research harnesses, not part of the solution |

## Build and test

```powershell
dotnet build AgentStudio.slnx          # must end with 0 warnings (warnings are errors)
dotnet test                            # Microsoft.Testing.Platform; NEVER pass -nologo (no test runs with it)
```

CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)) runs on Windows:

```powershell
dotnet restore AgentStudio.slnx
dotnet build AgentStudio.slnx -c Release --no-restore
dotnet test AgentStudio.slnx -c Release --no-build -- --filter-not-trait "Category=LocalTools" --filter-not-trait "Category=ConPty" --filter-not-trait "Category=Measurement" --filter-not-trait "Category=ProviderLive"
```

- xUnit v3 filters go after `--`, for example `dotnet test -- --filter-trait "Category=LocalTools"`.
- `ProviderLive` tests launch real CLIs and spend subscription quota. They only run with
  `AGENTSTUDIO_PROVIDER_TESTS=1`. Do not set it, and do not run a provider CLI with a prompt, unless the person you work
  for explicitly asks.
- Use xUnit `Assert` only. FluentAssertions is forbidden (non-commercial licence).
- The test project is WPF: add `using System.IO;` when you use `Path`, `File` or `Directory`.
- Clone or work in a short path: a deep path can break loading the native SQLite library in tests.

## Layering rules

From [ADR-0002](docs/adr/0002-capas-y-puertos.md) (ports and adapters):

- `Domain` depends on nothing and does no I/O.
- `Application` depends only on `Domain` and defines all ports.
- `Orchestration`, `Debate`, `Providers`, `Observability`, `Memory`, `Security`, `Infrastructure` and `SkillsMcp`
  depend only on `Application` and **never reference each other**. They collaborate through ports.
- `Desktop` is the only composition root.
- This is what keeps the judge (in `Debate`) independent of what the orchestrator chooses to show it. Do not add a
  project reference between modules to take a shortcut.

## Invariants that must not be broken

- **Append-only decision log.** Decisions, evidence, verdicts, debate contributions and memory are append-only: SQLite
  triggers reject `UPDATE` and `DELETE`, and entries are linked in a SHA-256 hash chain with an external anchor
  ([ADR-0004](docs/adr/0004-registro-de-decisiones-append-only.md)). A correction is a new entry. Never write code that
  updates or deletes these rows or bypasses the triggers.
- **State is derived from the log.** Run and debate state is rebuilt by replaying the verified log
  ([ADR-0010](docs/adr/0010-estado-derivado-del-registro.md)); do not add a second copy of state that can diverge.
- **Schema changes** go in a new numbered migration (`src/AgentStudio.Infrastructure/Persistence/Schema/NNNN_*.sql`);
  each migration is applied once.
- **Judge independence.** The judge runs on a vendor different from every producer, decides only from a dossier the
  system builds, and receives no memory, no skills and nothing from the semantic index
  ([ADR-0006](docs/adr/0006-juez-independiente-verificable.md), [ADR-0014](docs/adr/0014-independencia-del-juez-con-dos-vendors.md),
  [ADR-0047](docs/adr/0047-indice-semantico-local.md)).
- **The system verifies.** Builds and tests that count as evidence are run by Agent Studio, not by an agent.
- **No simulated features.** What is not implemented is not shown in the UI, or is shown as pending.

## Providers and security

- Never read, copy or log the CLIs' credentials or profile folders: `~/.claude`, `~/.codex`, `~/.gemini`, `~/.agents`.
- Probes only use `--version`, `claude auth status`, `codex login status` and, opt-in from Settings, the read-only Codex
  quota call `account/rateLimits/read`. Never call `account/rateLimitResetCredit/consume` or
  `account/sendAddCreditsNudgeEmail`.
- Free text goes to a CLI through **stdin**, never as an argument (the npm CLIs are `.cmd` shims).
- Always pass the permission mode explicitly when launching a CLI.
- No secrets in code, logs, evidence, prompts, fixtures or the journal.

## Decision records

- Relevant decisions get an ADR in `docs/adr/` (Spanish): status with date, context, decision, alternatives,
  consequences.
- Accepted ADRs are not rewritten. Record a change as a dated addendum ("Adenda · YYYY-MM-DD") at the end of the ADR,
  or as a new ADR that amends the old one. Numbers 0038 to 0046 are reserved for Phases 6 to 11, and later numbers are
  provisionally assigned in [docs/17](docs/17-constructor-visual-de-agentes.md) §10; ask before taking one.

## The prompt journal

[ADR-0008](docs/adr/0008-bitacora-obligatoria.md) makes a daily prompt journal mandatory.

- In the product, every agent prompt goes through `JournalingAgentProvider`; if the entry cannot be written, the agent
  is not launched. Keep providers registered only through that decorator.
- In this repository, Claude Code runs the `UserPromptSubmit` hook `.claude/hooks/bitacora.ps1`, which appends each
  prompt (with an excerpt of up to 160 characters) to `.agentstudio/bitacora/YYYY-MM-DD.md`. `CLAUDE.md` asks Claude to
  add a `  - Hecho: …` line under its entry when the request is done, saying in one sentence what changed and whether it
  was verified.
- Other tools have no hook. Whatever tool you are, never rewrite or delete existing journal lines: the journal is
  append-only and versioned in a public repository.

## Working rules

- Commit and push only when the person you work for asks.
- Parallel implementers work in separate Git worktrees; one integrator merges, runs build and tests, and reviews.
- Keep changes small and on topic; link the issue in the pull request.

## Definition of done

A change is done only when all of these hold, with the output to show for it:

1. `dotnet build AgentStudio.slnx` ends with 0 warnings and 0 errors.
2. The tests pass (at least the CI filter above), and new behaviour has tests.
3. No feature is simulated in the UI.
4. Documentation and ADRs reflect what was decided (an addendum or a new ADR when a decision changed).
5. Layering rules and the invariants above still hold.
6. No secrets or credentials were read, written or committed.
