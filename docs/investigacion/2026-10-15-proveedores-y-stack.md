# Verificación de proveedores y stack — 2026-10-15

> Evidencia de la Fase 0. Cada afirmación lleva su fuente y una etiqueta:
> **V** = verificado en documentación oficial o en la ayuda del CLI instalado; **I** = inferido; **NV** = no verificable hoy.
> Nada de lo aquí recogido se obtuvo ejecutando un prompt contra un proveedor (no se ha consumido cuota).
> Revisar este documento antes de la Fase 2 y antes de cualquier distribución: los términos cambian.

## Entorno de la máquina de desarrollo

| Herramienta | Estado | Cómo se comprobó |
|---|---|---|
| .NET SDK | 8.0.319, 10.0.303 y 11.0.100-preview.2; la solución fija 10.0.303 en `global.json` | `dotnet --list-sdks` |
| Claude Code | 2.1.290 | `claude --version` |
| Codex CLI | 0.160.0 (npm `@openai/codex`), sesión iniciada con ChatGPT | `codex --version`, `codex login status` |
| Gemini CLI | 0.64.0 (npm `@google/gemini-cli`, instalado el 2026-10-15 con OK del usuario); sesión desconocida (recién instalado) | `gemini --version`, `gemini --help` |
| Git | 2.45.2.windows.1 | `git --version` |
| Node | 24.14.0 | `node --version` |

## 1. Claude Code (Anthropic)

### Términos

- **V** — Uso personal con el binario sin modificar: "Nor does it prevent an end user from signing in to the unmodified
  Claude Code binary with their own Claude subscription". https://code.claude.com/docs/en/legal-and-compliance
- **V** — "OAuth authentication is intended exclusively for purchasers of Claude Free, Pro, Max, Team, and Enterprise
  subscription plans and is designed to support ordinary use of Claude Code and other native Anthropic applications."
  (misma página). Los límites de Pro/Max "assume ordinary, individual usage".
- **V** — Productos de terceros: "Anthropic does not permit third-party developers to offer Claude.ai login into their
  own applications, or to route requests through Free, Pro, or Max plan credentials on behalf of their users. Moreover,
  developers may not collect, store, or intermediate Claude.ai credentials or session tokens" (misma página).
- **V** — Agent SDK: "Unless previously approved, Anthropic does not allow third party developers to offer claude.ai
  login or rate limits for their products, including agents built on the Claude Agent SDK. Use the API key
  authentication methods". https://code.claude.com/docs/en/agent-sdk/overview
- **V** — Se puede ejecutar Claude Code dentro de un producto si el binario no se modifica, no se elimina ningún método de
  login y cada usuario final se autentica con sus propias credenciales; no se puede revender ni pagar por los usuarios
  (legal-and-compliance).
- **V** — Consumer Terms, sección 3: prohíbe acceder "through automated or non-human means, whether through a bot,
  script, or otherwise" salvo con API key o "where we otherwise explicitly permit it". https://www.anthropic.com/legal/consumer-terms
- **I** — `claude -p` está documentado oficialmente (y `claude setup-token` "for CI and scripts. Requires a Claude
  subscription"), lo que apunta a permiso explícito para scripts del propio usuario. Sigue siendo zona gris para usos
  intensivos.
- **V** — Marca: no usar "Claude Code" en el nombre ni el logo del producto; sí decir en texto que "ejecuta Claude Code".

### Interfaz

- **V** — Headless: `claude -p` con `--output-format text|json|stream-json`; `stream-json` requiere `--verbose`;
  `--include-partial-messages` para tokens parciales; `--input-format stream-json` para entrada en tiempo real.
  https://code.claude.com/docs/en/headless
- **V** — Eventos `system/init` (session_id, model, tools, permissionMode, apiKeySource…), `assistant`, `user`,
  `stream_event`, `system/api_retry`, `permission_denied`, `tool_progress`, `rate_limit_event` y `result`.
  https://code.claude.com/docs/en/agent-sdk/typescript
- **V** — `result` incluye `subtype` (`success`, `error_max_turns`, `error_during_execution`, `error_max_budget_usd`…),
  `is_error`, `duration_ms`, `num_turns`, `total_cost_usd` (estimación del cliente, no facturación), `usage`,
  `modelUsage`, `permission_denials`, `structured_output` y `session_id`.
- **V** (ayuda local) — `--json-schema`, `--max-turns`, `--max-budget-usd`, `--permission-mode`
  (`acceptEdits|auto|bypassPermissions|manual|dontAsk|plan`), `--allowedTools`, `--disallowedTools`, `--tools`,
  `--add-dir`, `--mcp-config`, `--strict-mcp-config`, `--session-id`, `--resume`, `--no-session-persistence`,
  `--system-prompt`, `--append-system-prompt`, `--model`, `--settings`, `--permission-prompt-tool`.
- **V** — `-p` sin `--permission-mode` explícito puede arrancar en `auto`; `-p` ejecuta hooks y `.mcp.json` del
  proyecto sin diálogo de confianza. **Agent Studio debe pasar siempre el modo de permisos de forma explícita.**
- **V** — `--bare` no lee OAuth ni keychain: solo sirve con API key.
- **NV** — Formato crudo de los mensajes de control por stdin (interrupción, permisos) en `stream-json`.
- **NV** — Esquema exacto de `usage`, `modelUsage` y de la ventana de contexto.

### Autenticación, detección y cuota

- **V** — `claude auth status` (JSON por defecto, `--text` opcional): "Exits with code 0 if logged in, 1 if not";
  `authMethod` ∈ `none|claude.ai|oauth_token|api_key|api_key_helper|third_party`. Comando local, sin prompt.
  https://code.claude.com/docs/en/cli-reference
- **V** — `claude --version` devuelve `2.1.290 (Claude Code)`; existe `claude doctor` de solo lectura.
- **V** — `/usage` es interactivo y aproximado (historial local de la máquina). ~~`rate_limit_event` solo se emite al
  topar con un límite.~~ **Corregido en la Fase 2:** llega al inicio de cada ejecución (ver sección 6). El JSON del `statusLine` trae `rate_limits.five_hour` y `rate_limits.seven_day` con
  `used_percentage` y `resets_at` (Pro/Max). https://code.claude.com/docs/en/costs, https://code.claude.com/docs/en/statusline
- **V** — No hay SDK oficial de Agent SDK para .NET; la doc indica lanzar el CLI como subproceso con `-p`.

## 2. Codex CLI (OpenAI)

> Las URL `developers.openai.com/codex/*` redirigen (308) a `learn.chatgpt.com/docs/*`.

### Términos

- **V** — Dos métodos: suscripción ChatGPT y API key; las API keys "are still the recommended default for automation".
  https://learn.chatgpt.com/docs/auth
- **V** — `codex exec` reutiliza el login guardado del CLI; `CODEX_API_KEY` vale para una ejecución.
  https://learn.chatgpt.com/docs/non-interactive-mode
- **V** — App-server: "App-server authentication has never been permitted for commercial or hosted services."
  Las apps locales existentes pueden seguir usándolo. https://learn.chatgpt.com/docs/app-server
- **I** — Herramienta personal que lanza el `codex` del usuario con su login: no hay prohibición textual ni permiso
  textual. Producto distribuido: API key del usuario o el programa de partners "Sign in with ChatGPT".
- **NV** — Términos de uso generales de OpenAI aplicados a este caso.

### Interfaz

- **V** — `codex exec --json`: JSONL con `thread.started`, `turn.started`, `turn.completed` (con `usage`:
  `input_tokens`, `cached_input_tokens`, `output_tokens`, `reasoning_output_tokens`), `turn.failed`, `item.*`, `error`.
- **V** (ayuda local) — `--output-schema <FILE>`, `-o/--output-last-message`, `-s read-only|workspace-write|danger-full-access`
  (por defecto read-only), `--ephemeral`, `--skip-git-repo-check`, `-C`, `--worktree`, `--ignore-user-config`,
  `resume`, `fork`.
- **V** — `codex app-server`: JSON-RPC 2.0 por stdio (JSONL); "experimental and aren't supported for production
  workloads"; métodos `initialize`, `thread/start|resume|fork`, `turn/start`, `turn/interrupt`; aprobaciones
  `item/commandExecution/requestApproval` y `item/fileChange/requestApproval`; `ThreadTokenUsage` con
  `modelContextWindow`. Esquema generable con `codex app-server generate-json-schema` (no consume cuota).
- **V** — `account/rateLimits/read`: ventanas `primary` y `secondary` con `usedPercent`, `windowDurationMins`,
  `resetsAt`. **Es la única cuota de suscripción expuesta oficialmente por un proveedor de los tres.**

### Autenticación y detección

- **V** (local) — `codex login status` imprime "Logged in using ChatGPT" sin consumir cuota. `codex doctor --json`
  da un informe con `overallStatus` y `auth.credentials`. **NV**: código de salida de `login status`.
- **V** — SDK oficiales: TypeScript `@openai/codex-sdk` y Python `openai-codex`; ninguno para .NET.

## 3. Gemini CLI (Google)

### Términos

- **V** — Términos según método: login de Google (términos de Google / Google One / Cloud según el plan), API key
  ("Gemini API Terms of Service") o Vertex ("Google Cloud Platform Service Terms").
  https://github.com/google-gemini/gemini-cli/blob/main/docs/resources/tos-privacy.md
- **V** — "Directly accessing the services powering Gemini CLI (for example, the Gemini Code Assist service) using
  third-party software, tools, or services (for example, using OpenClaw with Gemini CLI OAuth) is a violation of
  applicable terms and policies. Such actions may be grounds for suspension or termination of your account." (misma página)
- **I** — La prohibición apunta a reutilizar el OAuth del CLI fuera del CLI. Lanzar el binario oficial sin modificar
  no aparece prohibido ni permitido expresamente. Para distribuir: API key o Vertex.
- **V** — Cuotas diarias por usuario: Code Assist individual 1.000 peticiones, AI Pro 1.500, AI Ultra 2.000, API key
  gratuita 250 (solo Flash). https://github.com/google-gemini/gemini-cli/blob/main/docs/resources/quota-and-pricing.md

### Interfaz

- **V** — `gemini -p`, `--output-format text|json|stream-json`; JSON con `response`, `stats`, `error`; stream-json con
  `init`, `message`, `tool_use`, `tool_result`, `error`, `result` (con tokens y `duration_ms`). Códigos de salida:
  0 éxito, 1 error, 42 error de entrada, 53 límite de turnos. `--approval-mode default|auto_edit|yolo|plan`.
  https://github.com/google-gemini/gemini-cli/blob/main/docs/cli/headless.md
- **V** (ayuda local 0.64.0) — `gemini --version` no consume cuota. Flags confirmados: `-p/--prompt` (se añade a lo que
  llegue por stdin), `-o/--output-format text|json|stream-json`, `--approval-mode default|auto_edit|yolo|plan`,
  `--include-directories`, `-s/--sandbox`, `-r/--resume`, `-w/--worktree`, `-e/--extensions`, `--acp`, `--skip-trust`.
  Subcomandos: `mcp`, `extensions`, `skills` («Manage agent skills»), `hooks`, `gemma`.
- **V** (ayuda local 0.64.0) — No existe un subcomando de estado de autenticación: la sonda informa «Desconocida».
  El primer inicio de sesión lo hace el usuario ejecutando `gemini` en una terminal.
- **V** — `gemini --acp` arranca el modo ACP (`--experimental-acp` está deprecado).

## 4. Agent Client Protocol (ACP)

- **V** — JSON-RPC por stdio entre editor y agente; versión estable del protocolo `1`; métodos `initialize`,
  `session/new`, `session/load`, `session/prompt`, `session/update`, `session/cancel`. https://agentclientprotocol.com
- **V** — Registro oficial con ~45 agentes: `gemini` (nativo), `claude-acp` (adaptador `zed-industries/claude-agent-acp`
  sobre el Claude Agent SDK), `codex-acp` (adaptador sobre el app-server de Codex).
- **I** — Los adaptadores de Claude y Codex son del ecosistema Zed/ACP, no de Anthropic ni OpenAI.
- **V** — SDK oficiales: Kotlin, Java, Python, Rust y TypeScript. Para .NET solo comunitarios (`AgentClientProtocol`
  de nuskey8, MIT, último push 2025-11; otros dos con muy poca adopción).

## 6. Verificado con ejecuciones reales (Fase 2, 2026-10-15)

> Con autorización expresa del usuario para consumir cuota. Capturas, comandos exactos y observaciones en
> `tests/AgentStudio.Tests/Providers/Fixtures/README.md`; tests `ProviderLive` (5/5 en verde en `main`).

- **V** Claude Code 2.1.290 — `rate_limit_event` llega al inicio aunque no se alcance el límite, con
  `unifiedWindows.five_hour.utilization` y `seven_day.utilization` (0..1) y `resetsAt`: es la cuota de la
  suscripción, y se expone sin coste adicional en cada ejecución.
- **V** Claude Code — `result.modelUsage.<modelo>.contextWindow` da la ventana de contexto; el `init` trae el modelo
  con fecha (`claude-haiku-4-5-20251001`).
- **V** Claude Code — `--setting-sources project,local` evita los hooks y plugins globales del usuario y la
  autenticación por suscripción sigue funcionando. `--append-system-prompt-file` existe aunque no sale en `--help`.
  **Corregido el 2026-10-16:** solo aísla del **usuario**. Carga los hooks y servidores MCP del repositorio; ver
  «Configuración del repositorio» más abajo. El adaptador ya no lo usa.
- **V** Claude Code — `--permission-mode plan` usa otro modelo para parte del trabajo (aparece en `modelUsage`) y
  cuesta más: los adaptadores no lo usan.
- **V** Codex CLI 0.160.0 — los avisos de configuración llegan como `item.completed` de tipo `error` y no son fallos;
  las herramientas aparecen como `command_execution` (pwsh en Windows); la salida estructurada es el texto JSON del
  `agent_message` final; `turn.completed.usage` añade `cache_write_input_tokens`.
- **V** Codex CLI — el binario nativo está en
  `%APPDATA%\npm\node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe`;
  lanzarlo evita que `cmd.exe` reinterprete los argumentos del shim `.cmd`.
- **V** Gemini CLI 0.64.0 — sin autenticación sale con código **41** (no documentado), stdout vacío y el motivo en
  stderr. En una carpeta no marcada como de confianza, `--approval-mode plan` se fuerza a `default`: hace falta
  `--skip-trust`. `gemini.cmd` ejecuta `node …\@google\gemini-cli\bundle\gemini.js`.
- **NV** Gemini CLI — formato real de una ejecución autenticada: el parser sigue `docs/cli/headless.md` y
  `packages/core/src/output/types.ts` de la v0.64.0 hasta tener una captura.

### Configuración del repositorio (revisión adversarial, 2026-10-16)

> Riesgo: el repositorio no es de confianza (lo escriben los agentes y puede ser de terceros), y su configuración
> ejecutable corre con los permisos del usuario. Cada orden plantada solo crea un fichero centinela fuera del
> repositorio. Tests `ProviderLive`: `ClaudeRepositoryIsolationLiveTests` y `CodexRepositoryIsolationLiveTests`.

Claude Code 2.1.290:

- **V** (documentación) — con `claude -p` en una carpeta sin confianza se usan los hooks de los ficheros de settings,
  el bloque `env`, `apiKeyHelper` y los hooks de las skills del proyecto. Los servidores de `.mcp.json` se conectan sin
  preguntar, aprobados o no. La propia guía recomienda excluir la fuente `project` y pasar
  `--settings '{"disableAllHooks": true}'`. https://code.claude.com/docs/en/permissions#what-runs-before-you-trust-a-folder
- **V** (documentación) — la fuente `project` carga también CLAUDE.md, `.claude/rules`, y las skills, comandos y
  subagentes del proyecto y de los directorios de `--add-dir`.
  https://code.claude.com/docs/en/agent-sdk/claude-code-features
- **V** (sin cuota) — `claude --init-only` ejecuta los hooks SessionStart y sale sin conversación. Se probó sobre un
  repositorio con un hook SessionStart y un servidor en `.mcp.json`, con cada variante de flags:

  | Flags | Hook SessionStart | Servidor de `.mcp.json` |
  |---|---|---|
  | `--setting-sources project,local` | se ejecuta | arranca |
  | `--setting-sources ""` (se acepta, código 0) | no | no |
  | `project,local` + `--settings` con `{"disableAllHooks":true}` | no | arranca |
  | `project,local` + `--strict-mcp-config` | se ejecuta | no |
  | `""` + `--settings` + `--strict-mcp-config` | no | no |

- **V** (antes del cambio) — se lanzó el adaptador real con el perfil del planificador (haiku, leer un fichero con
  Read). Se ejecutaron los cinco centinelas plantados: hooks SessionStart (`settings.json` y `settings.local.json`),
  UserPromptSubmit, PreToolUse y el servidor de `.mcp.json`. El `init` listaba ese servidor y los cinco conectores de
  claude.ai del usuario.
- **V** (después del cambio) — con `--setting-sources ""`, `--strict-mcp-config` y `--settings`, la misma ejecución:
  - termina bien con la suscripción y usa Read;
  - no deja ningún centinela ni ningún evento `system/hook_*` en el stream, ni siquiera de los hooks del usuario;
  - el `init` trae `mcp_servers` vacío y solo plugins `builtin`.
- **V** — el plugin de sesión de `--plugin-dir` sigue funcionando con `--setting-sources ""`. Se repitió F2-10: el
  `init` lista el plugin y su skill, y el modelo la invoca. Coste estimado por el CLI: 0,063 USD.

Codex CLI 0.160.0:

- **V** (código de `rust-v0.160.0`, `codex-rs/config/src/loader/mod.rs`) — Codex lee el `.codex/config.toml` de cada
  carpeta entre la raíz del proyecto y el directorio de trabajo.
  - Solo lo aplica si `[projects."<ruta>"].trust_level` lo marca como de confianza. Busca la carpeta, luego la raíz
    del proyecto y luego el repositorio principal, así que un worktree hereda la confianza del checkout principal.
  - El proyecto no puede fijar `notify`, `profile`, `model_provider`, `otel` y otras claves de la lista
    `PROJECT_LOCAL_CONFIG_DENYLIST`.
  - Sí puede declarar `mcp_servers`, cuyos comandos arrancan fuera del sandbox sin aprobación, además de
    `shell_environment_policy`, `sandbox_workspace_write.writable_roots`, permisos, hooks y reglas.
- **V** (sin cuota) — se usó un `CODEX_HOME` temporal sin credenciales, cuyo `config.toml` confía en un repositorio
  plantado. El `CODEX_HOME` real no se leyó ni se escribió.
  - `codex debug prompt-input` aplica el `developer_instructions` del proyecto y arranca su servidor MCP.
  - `codex exec` con los argumentos anteriores del adaptador arranca el servidor MCP (centinela).
  - Con `--ignore-user-config` no arranca nada.
  - Los hooks de `.codex/hooks.json` no corrieron en ningún caso: además exigen confianza por hash (`/hooks`).
- **V** — en esta máquina un `.codex/config.toml` plantado en un worktree de este repositorio no se aplica. Hoy el
  canal estaba cerrado de hecho; se abriría al marcar el repositorio como de confianza en Codex.
- **V** (código) — en Windows el backend del sandbox sale de `[windows] sandbox`. Las features antiguas están
  retiradas y `prefer_mxc` está desactivado por defecto. Sin ese ajuste, `get_platform_sandbox` no devuelve sandbox y
  los comandos se ejecutarían sin él: `--ignore-user-config` exige fijarlo en la línea de comandos.
- **V** — `-c windows.sandbox=bogus` falla con «unknown variant `bogus`, expected one of `elevated`, `unelevated`,
  `mxc`»: la clave se interpreta y el valor sin comillas se valida.
- **V** — `codex sandbox -P :read-only` corre como el propio usuario (`whoami`) y deniega la escritura. **I**: no es
  el modo elevated, que según el código usa usuarios dedicados del sandbox.
  - Antes del cambio, la ejecución real (gpt-6-luna, perfil de solo lectura) ya veía denegada la escritura: la config
    del usuario fija un backend.
  - Después, con `--ignore-user-config`, `--ignore-rules`, `--disable hooks` y `-c windows.sandbox=unelevated`, la
    ejecución termina bien. El comando de escritura falla con «Access to the path … is denied» y no queda centinela.
  - F2-10 de Codex repetido: el modelo sigue viendo la skill de `.agents/skills`.
- **V** (código, `codex-rs/core/src/mcp_skill_dependencies.rs`) — la instalación de dependencias MCP declaradas por
  skills (`skill_mcp_dependency_install`) no se activa con `approval Never` y un sandbox sin escritura total, que es
  lo que usa el adaptador.
- **V** — con un `CODEX_HOME` nuevo, Codex clona en segundo plano el marketplace oficial de plugins en
  `CODEX_HOME/.tmp` (git), y ese clon puede sobrevivir al proceso.

## 7. Google sin cuentas personales (2026-10-15)

- **V** — Al iniciar sesión con «Sign in with Google» en Gemini CLI 0.64.0, la cuenta personal del usuario obtuvo:
  «Failed to sign in. Message: This client is no longer supported for Gemini Code Assist for individuals. To continue
  using Gemini, please migrate to the Antigravity suite of products: https://antigravity.google».
- **V** (prensa y guías de migración; anuncio de Google del 19-05-2026) — Gemini CLI dejó de servir a las cuentas
  individuales (gratuitas, AI Pro y Ultra) el 18-06-2026. Siguen funcionando las API keys de pago y las licencias de
  empresa. https://github.com/google-gemini/gemini-cli/issues/28229
- **V** — Antigravity CLI (`agy` 1.3.5, instalado con el script oficial tras leerlo; solo añadió
  `%LOCALAPPDATA%\agy\bin` al PATH del usuario):
  - Modo headless con `-p`, `--input-format stream-json` (el prompt va por stdin) y `--output-format json|stream-json`.
  - Flags `--mode accept-edits|auto|plan`, `--json-schema` y `--add-dir`.
  - Las credenciales se guardan en el Administrador de credenciales de Windows.
  - https://antigravity.google/docs/cli/headless/
- **V** — FAQ de Antigravity (https://antigravity.google/docs/faq/): «Antigravity CLI headless mode (agy -p) is
  intended for direct scripting and CI workflows. Wrapping agy inside third-party coding agents or applications, or
  automating account rotation, is not supported under consumer plans». Para herramientas de terceros recomienda «a
  Gemini Enterprise or Google AI Studio API key».
- **V** — Términos de Antigravity, §6 (https://antigravity.google/terms/): «Using third party software, tools, or
  services to access the Service … is a breach of this Agreement. Such actions may be grounds for suspension or
  termination of your Antigravity and/or Gemini CLI accounts».
- **Decisión:** Google queda aparcado ([ADR-0009](../adr/0009-google-aparcado.md)).

## 5. Stack .NET verificado (versiones estables a 2026-10-15)

| Pieza | Paquete | Versión | Licencia | Decisión |
|---|---|---|---|---|
| Runtime | .NET 10 (LTS, soporte hasta 2028-11) | SDK 10.0.303 | MIT | Usar |
| MVVM | CommunityToolkit.Mvvm | 8.4.2 | MIT | Usar |
| Hosting / DI / Logging | Microsoft.Extensions.Hosting | 10.0.13 | MIT | Usar |
| Docking | Dirkster.AvalonDock (+ Themes.VS2013) | 5.0.1 | MS-PL | Fase 1 (tema oscuro propio) |
| Grafo de nodos | Nodify | 7.3.0 | MIT | Fase 3 |
| Editor | Microsoft.Web.WebView2 + monaco-editor | 1.0.4258.31 / 0.57.0 | Propietaria gratuita / MIT | Fase 1 |
| Editor (alternativa) | AvalonEdit | 6.3.1.120 | MIT | Respaldo |
| Terminal | ConPTY por P/Invoke + @xterm/xterm | — / 6.0.0 | — / MIT | Fase 1 |
| SQLite | Microsoft.Data.Sqlite (+ Dapper) | 10.0.13 / 2.1.89 | MIT / Apache-2.0 | Usar |
| Git | git.exe (+ LibGit2Sharp opcional) | 0.32.0 | MIT | git.exe primero |
| Observabilidad | OpenTelemetry / Serilog | 1.19.1 / 4.4.0 | Apache-2.0 | Fase 4 |
| Tests | xunit.v3 + xunit.runner.visualstudio + Microsoft.NET.Test.Sdk | 4.0.2 / 4.0.1 / 18.11.0 | Apache-2.0 / MIT | Usar |
| Aserciones | **FluentAssertions 8.x descartado** (licencia Xceed, no comercial) | — | — | Usar `Assert` de xUnit |
| Credenciales | System.Security.Cryptography.ProtectedData (DPAPI) | 10.0.13 | MIT | Fase 2 |
| JSON Schema | NJsonSchema | 11.6.1 | MIT | Fase 5 (JsonSchema.Net: licencia del paquete sin confirmar) |

Riesgos abiertos del stack: los paquetes de terminal para WPF (`CI.Microsoft.Terminal.Wpf`, `Quick.PtyNet`) no
declaran licencia en sus metadatos; no hay paquete VS2022 oscuro para AvalonDock; WebView2 tiene licencia propietaria
(gratuita y redistribuible, pero no OSI).
