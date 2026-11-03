# Kit del proyecto: Agent Studio

> Generado con `project-kickoff` el 2026-10-15. Estado: **aprobado** el 2026-10-15 (el usuario delegó «el resto lo que recomiendes»).
> Lo usa el orquestador (`C:/skills-index/orchestrator/ORQUESTACION.md`) en cada tarea de este proyecto.

## Qué es

- Objetivo: IDE de escritorio para Windows que orquesta agentes de IA (Claude Code, Codex, Gemini CLI) con
  debate entre agentes, juez independiente de otro proveedor, auditoría del orquestador, memoria persistente y
  observabilidad del contexto. Especificación: `docs/referencias/prompt-maestro.md`.
- Stack: C# sobre .NET 10 (LTS), WPF + MVVM (CommunityToolkit.Mvvm), WebView2 para Monaco y grafos, SQLite,
  xUnit. Detalle y alternativas en `docs/02-arquitectura.md`.
- Cómo se prueba: `dotnet build AgentStudio.slnx` y `dotnet test` (runner Microsoft.Testing.Platform declarado en
  `global.json`; **no pasar `-nologo`**, hace que no se ejecute ningún test). Filtros de xUnit v3:
  `dotnet test -- --filter-trait "Category=LocalTools"`. El proyecto de tests es WPF: añadir `using System.IO;` donde
  haga falta. Los tests que lanzan CLIs de proveedores con prompts (consumen cuota) irán tras la variable
  `AGENTSTUDIO_PROVIDER_TESTS=1` y no se ejecutan por defecto. Los implementadores en paralelo trabajan en copias
  aisladas (o worktrees cuando haya commits) y el orquestador integra.
- Cómo se despliega (y quién lo aprueba): solo uso personal, en la máquina del usuario; empaquetado
  (MSIX o instalador) en la fase 7. Cualquier publicación la aprueba el usuario.
- Reglas propias del proyecto: `CLAUDE.md` (bitácora obligatoria, comandos de build y test, seguridad de proveedores).

## Skills del índice que se usan

| Skill | Para qué en este proyecto | Ruta |
|---|---|---|
| wpf | MVVM, binding, threading y estructura de la app de escritorio | `C:/skills-index/domains/desktop-development/skills/wpf/SKILL.md` |
| test-driven-development | Dominio, orquestación y debate se escriben test primero | `C:/skills-index/domains/software-engineering/skills/test-driven-development/SKILL.md` |
| verification-before-completion | Ninguna fase se da por cerrada sin la salida de build y tests | `C:/skills-index/domains/software-engineering/skills/verification-before-completion/SKILL.md` |
| systematic-debugging | Fallos de procesos, CLIs y concurrencia | `C:/skills-index/domains/software-engineering/skills/systematic-debugging/SKILL.md` |
| write-judge-prompt | Prompts del Independent Judge y del auditor del orquestador (fase 5) | `C:/skills-index/domains/ai-engineering/skills/write-judge-prompt/SKILL.md` |
| error-discovery | Taxonomía de fallos de agentes a partir de las trazas registradas (fases 4-5) | `C:/skills-index/domains/ai-engineering/skills/error-discovery/SKILL.md` |
| security-threat-model | Threat model de agentes con acceso a ficheros, shell y red (fase 7, borrador en fase 0) | `C:/skills-index/domains/cybersecurity/skills/security-threat-model/SKILL.md` |
| design-system | Tokens de color, tipografía y componentes del tema oscuro (fase 1) | `C:/skills-index/domains/ux-ui-design/skills/design-system/SKILL.md` |
| accessibility-review | Contraste, teclado y lector de pantalla del tema oscuro (fases 1 y 7) | `C:/skills-index/domains/ux-ui-design/skills/accessibility-review/SKILL.md` |
| property-based-testing | Invariantes del grafo de tareas y del registro de decisiones | `C:/skills-index/domains/testing-qa/skills/property-based-testing/SKILL.md` |

## Equipo de agentes

| Tarea típica | Agente | Modelo | Skills que lleva en el brief |
|---|---|---|---|
| Verificar interfaces y términos de proveedores, versiones de paquetes | investigador | Sonnet para términos de proveedores; Haiku para el resto | — |
| Implementar un módulo acotado con sus tests | implementador | Sonnet | wpf, test-driven-development, verification-before-completion |
| Revisar un cambio contra su especificación | revisor | Sonnet | verification-before-completion |
| Redactar o actualizar documentación en castellano | documentador | Haiku | — |
| Decisiones de arquitectura, contratos y diseño del juez | orquestador (sesión principal) | modelo principal | write-judge-prompt |

## Herramientas

- MCP ya instalados que se usan: `microsoft-learn` (WPF, WebView2, ConPTY, .NET), `playwright` (solo si se
  prueba la parte WebView2 fuera de la app).
- MCP recomendados para este proyecto (scope proyecto, pendientes de OK): `context7` (documentación actualizada
  de librerías como Nodify, AvalonDock o Monaco).
- Dominios del índice activados como plugin (pendiente de OK): ninguno. Opcional: plugin oficial `winui`
  (microsoft/win-dev-skills) solo si se reabre la decisión WPF frente a WinUI 3.

## Límites

- Qué no se toca nunca: credenciales de los CLIs (`~/.claude`, `~/.codex`, `~/.gemini`); no se leen ni se copian
  tokens. La autenticación se comprueba solo con los comandos oficiales de cada CLI.
- Qué requiere OK expreso: ejecutar un CLI de proveedor con un prompt real (consume cuota de suscripción),
  instalar herramientas globales (p. ej. Gemini CLI), instalar MCP o plugins, commits y pushes.
