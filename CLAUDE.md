# Agent Studio — instrucciones para Claude

IDE de escritorio personal (C# / .NET 10 / WPF) que orquesta agentes de IA con juez independiente. Uso **solo
personal**. Especificación: `docs/referencias/prompt-maestro.md`. Mapa de la documentación: `docs/README.md`.
Kit del proyecto (skills, agentes y herramientas): `.claude/PROJECT_KIT.md`.

## Bitácora obligatoria

- El hook `UserPromptSubmit` (`.claude/hooks/bitacora.ps1`) registra cada prompt en
  `.agentstudio/bitacora/YYYY-MM-DD.md` (un fichero por día, versionado).
- Al cerrar cada petición, añade debajo de su entrada una línea `  - Hecho: …` con el resultado en una frase
  (qué cambió, verificado o no). No reescribas entradas anteriores: la bitácora es append-only.

## Compilar y probar

- `dotnet build AgentStudio.slnx` debe dar 0 avisos (los avisos son errores).
- `dotnet test` (Microsoft.Testing.Platform). **No pases `-nologo`**: no se ejecuta ningún test.
  Filtros de xUnit v3: `dotnet test -- --filter-trait "Category=LocalTools"`.
- El proyecto de tests es WPF: añade `using System.IO;` cuando uses `Path`, `File` o `Directory`.
- Solo `Assert` de xUnit. FluentAssertions está prohibido (licencia no comercial).
- Nada se da por terminado sin la salida de build y tests. Sin funcionalidades simuladas en la UI.

## Convenciones

- Código, identificadores, comentarios y mensajes de excepción en inglés; textos de UI y documentación en castellano.
- Dependencias hacia dentro: `Domain` no depende de nada; los módulos solo de `Application`; `Desktop` compone
  (ADR-0002). Cada decisión relevante, un ADR en `docs/adr/`.

## Proveedores y seguridad

- Nunca leer, copiar ni registrar credenciales de los CLIs (`~/.claude`, `~/.codex`, `~/.gemini`, `~/.agents`).
- No ejecutar un CLI de proveedor con un prompt (consume cuota) sin OK expreso; las sondas solo usan
  `--version`, `claude auth status`, `codex login status` y, desde la Fase 4 (OK del usuario, 2026-10-18), la lectura
  de cuota `account/rateLimits/read` del app-server de Codex: solo lectura, sin turno del modelo, opt-in en Ajustes.
  Nunca se llaman `account/rateLimitResetCredit/consume` ni `account/sendAddCreditsNudgeEmail`.
- El texto libre va por stdin, nunca como argumento (los CLIs de npm son shims `.cmd`).
- Pasar siempre el modo de permisos de forma explícita al lanzar un CLI.

## Trabajo con agentes

- Implementadores en paralelo con `isolation: "worktree"`; el orquestador integra, ejecuta build y tests y revisa.
- Commits y pushes solo cuando el usuario los pida. Repositorio: GitHub `LargoLujan/agent-studio` (público).
