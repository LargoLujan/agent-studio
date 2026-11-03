# Salidas reales de los CLIs (fixtures)

Capturadas el 2026-10-15 en la máquina de desarrollo, con autorización del usuario para consumir cuota. Se han
eliminado datos personales: la ruta del perfil pasa a `C:\Users\usuario`, las carpetas temporales a
`AppData/Local/Temp/agentstudio-fixtures` y la salida de los hooks globales del usuario a `<salida de hook omitida>`.
Cada captura tiene su `.exit` (código de salida) y su `.stderr`.

Regenerarlas con cada versión nueva de un CLI y añadir la carpeta `<cli>/<versión>/`: así un cambio de formato rompe
un test en lugar de una ejecución real.

Las capturas `04-skill-*` (2026-10-15, también con autorización para consumir cuota) las escriben los propios tests en
vivo, no un script aparte: ver [Regenerar las capturas de skills](#regenerar-las-capturas-de-skills).

## Claude Code 2.1.290 (`claude-code/2.1.290/`)

| Fichero | Comando (prompt por stdin, en una carpeta temporal) |
|---|---|
| `01-texto.jsonl` | `claude -p --output-format stream-json --verbose --permission-mode plan --model haiku --no-session-persistence --max-turns 1` · «Responde exactamente con la palabra OK y nada más.» |
| `02-herramienta.jsonl` | `claude -p --output-format stream-json --verbose --setting-sources project,local --permission-mode acceptEdits --allowedTools "Write Read" --disallowedTools "Bash WebFetch WebSearch" --model haiku --no-session-persistence --max-turns 4` · «Crea un fichero llamado hola.txt con el texto hola y responde HECHO.» |
| `03-estructurada.json` | `claude -p --output-format json --setting-sources project,local --permission-mode dontAsk --tools "" --model haiku --no-session-persistence --max-turns 3 --json-schema <esquema>` · «Devuelve un objeto con respuesta igual a OK y numero igual a 42.» |
| `04-skill-plugin.jsonl` | `claude -p --output-format stream-json --verbose --setting-sources project,local --permission-mode dontAsk --disallowedTools "Bash WebFetch WebSearch Write Edit NotebookEdit" --model haiku --max-turns 4 --plugin-dir <sesiones>\claude-plugin-f2-10-claude --session-id <uuid>` (la lista de argumentos la construye `ClaudeCodeArguments`) · «Find the skill named agentstudio-probe-skill-&lt;8 hex&gt; in your list of available skills and invoke it with the Skill tool. Then reply with exactly two codewords separated by a space: first the codeword listed in its description, then the body codeword the skill gave you.» La skill de prueba la materializa `SkillMaterializer` como plugin de sesión; un codeword está en su `description` y otro solo en el cuerpo de su `SKILL.md` |

Observaciones verificadas con estas capturas:

- Sin `--setting-sources project,local` se ejecutan los hooks globales del usuario (eventos `system/hook_started` y
  `system/hook_response`). Con él, no, y la autenticación por suscripción sigue funcionando. **Corregido el
  2026-10-16:** `project,local` solo excluye la fuente del usuario; sigue cargando los hooks de `.claude/settings.json`
  y `settings.local.json` y los servidores de `.mcp.json` del repositorio. El adaptador pasa ahora
  `--setting-sources ""`, `--strict-mcp-config` y `--settings` con `{"disableAllHooks":true}`; las capturas de esta
  tabla se hicieron con los argumentos de entonces (ver `docs/06-integracion-proveedores.md`, «Reglas comunes»).
- `rate_limit_event` llega al inicio aunque no se alcance el límite, con `unifiedWindows.five_hour.utilization` y
  `seven_day.utilization` (0..1) y `resetsAt` (segundos Unix): es la cuota de la suscripción.
- `result.modelUsage.<modelo>.contextWindow` da la ventana de contexto de cada modelo usado.
- `system/thinking_tokens` informa de tokens de razonamiento estimados durante la ejecución.
- En `01-texto` el `init` dice `claude-haiku-4-5`, pero `modelUsage` muestra también `claude-sonnet-5-5`: el modo
  `plan` usa otro modelo para parte del trabajo (por eso cuesta más). Para ejecuciones baratas, evitar `plan`.
- `04-skill-plugin` (criterio F2-10): el `system/init` lista el plugin de sesión en `plugins` (`name:
  agent-studio-session`, `source: agent-studio-session@inline`, `path` = la carpeta pasada con `--plugin-dir`) y su skill
  en `skills` y en `slash_commands` con el espacio de nombres `agent-studio-session:<skill>`. Es la prueba de que el CLI
  la ve y no depende de lo que conteste el modelo. El adaptador no la expone (`SessionStarted` solo lleva el id de sesión
  y el modelo), por eso el test en vivo lee la línea cruda con un decorador del ejecutor de procesos.
- `--plugin-dir` funciona junto con `--setting-sources project,local`, y esa combinación deja fuera lo del perfil del
  usuario: `plugins` solo trae los integrados (`path: builtin`) y el de sesión, y `skills` solo las integradas y la de
  sesión. Resuelve el «NV» de `docs/investigacion/2026-10-15-skills.md` (aislamiento de `~/.claude/skills`). El
  2026-10-16 se repitió el test en vivo con `--setting-sources ""`: el plugin de sesión y su skill se siguen cargando.
- El modelo llamó a la herramienta `Skill` (`{"skill": "agent-studio-session:<skill>"}`), el CLI contestó «Launching skill: …»
  e inyectó el `SKILL.md` leído de la carpeta del plugin de sesión («Base directory for this skill: …»), y la respuesta
  trae los dos codewords: el de la `description` (viene en la lista de skills) y el del cuerpo (solo se conoce al invocar).
- `permission_denials` va vacío: en `dontAsk` y sin ninguna regla `--allowedTools`, invocar una skill del plugin de sesión
  no pide permiso, así que el adaptador no necesita añadir `Skill` a las herramientas aprobadas.
- `ToolInvoked.Summary` sale vacío para `Skill`: `ClaudeStreamJsonParser.SummaryKeys` no incluye `skill` (mejora menor).
- Tres turnos, 5,0 s, coste estimado por el CLI (`total_cost_usd`, precio de lista): 0,029 USD.

## Codex CLI 0.160.0 (`codex-cli/0.160.0/`)

| Fichero | Comando |
|---|---|
| `01-texto.jsonl` | `codex exec --json -s read-only --skip-git-repo-check --ephemeral -` · «Responde exactamente con la palabra OK y nada más.» |
| `02-herramienta.jsonl` | `codex exec --json -s workspace-write -C <repo git temporal> --ephemeral -` · «Crea un fichero llamado hola.txt con el texto hola y responde HECHO.» |
| `03-estructurada.jsonl` | `codex exec --json -s read-only --skip-git-repo-check --ephemeral --output-schema <esquema.json> -` · «Devuelve un objeto con respuesta igual a OK y numero igual a 42.» |
| `04-skill-agents.jsonl` | `codex exec --json -s read-only -C <repo git temporal> -m gpt-6-luna -` (lo construye `CodexExecArguments`) · «Without running any command or using any tool: look in your list of available skills for the skill named agentstudio-probe-skill-&lt;8 hex&gt; and reply with only the codeword listed in its description.» La skill de prueba la materializa `SkillMaterializer` en `<repo>/.agents/skills/<skill>/SKILL.md` |
| `05-rate-limits.jsonl` | `codex app-server --listen stdio://` · por la entrada estándar, `initialize`, `initialized` y `account/rateLimits/read` con `excludeResetCreditDetails` (la línea exacta, en `05-rate-limits.cmd.txt`). Grabada por `CodexQuotaLiveTests` el 2026-10-18 (F4-4, lectura sin turno del modelo, con el OK del usuario). Solo la respuesta de la lectura (id 2), saneada: `accountId`, los saldos de créditos y los créditos de reinicio sustituidos, y el upsell nulo |

Observaciones:

- Elementos `item.completed` de tipo `error` con avisos de configuración («Ignoring unknown `features` requirement…»)
  que NO son fallos de la ejecución.
- Las herramientas aparecen como `command_execution` (`item.started` / `item.completed`, en Windows con pwsh).
- La salida estructurada llega como texto JSON en el `agent_message` final.
- `turn.completed.usage`: `input_tokens`, `cached_input_tokens`, `cache_write_input_tokens`, `output_tokens`,
  `reasoning_output_tokens`.
- `04-skill-agents` (criterio F2-10): `exec --json` no emite ninguna lista de skills, así que la prueba de que Codex ve la
  skill es la respuesta del modelo (el codeword solo existe en la skill) y, sin gastar cuota, `codex debug prompt-input`
  ejecutado en el repo temporal: renderiza el prompt que vería el modelo y trae la línea
  `- <skill>: <descripción> (file: <raíz>/<skill>/SKILL.md)` de la skill materializada. Los tokens de la captura: 17 852 de
  entrada (0 en caché) y 115 de salida (100 de razonamiento); `exec --json` no informa de coste.
- `gpt-6-luna` es el modelo que el catálogo (`codex debug models`) describe como «Fast and affordable model for easier
  tasks»; el catálogo no da precios. La cuenta del usuario lo aceptó.
- Codex **no aísla** de las skills del usuario: junto a la materializada, `debug prompt-input` lista otras 25 en la
  máquina de desarrollo (perfil del usuario, skills del sistema y de plugins). `--enable skip_host_skill_discovery` (en
  desarrollo) no cambia la lista en 0.160.0. La activación explícita de skills (ADR-0007) solo queda garantizada en
  Claude Code.
- `05-rate-limits` (criterio F4-4): el app-server **termina al cerrarse su entrada estándar sin responder a la lectura**
  (medido el 2026-10-18: con la entrada cerrada solo llegan la respuesta a `initialize`, un `configWarning` y un
  `remoteControl/status/changed`). Por eso el lector deja la entrada abierta hasta la respuesta
  (`ProcessSpec.KeepStandardInputOpen`) y termina el árbol después; la respuesta llega en 1-2 s. Trae dos ventanas
  (`primary` de 300 min y `secondary` de 10 080 min) en `rateLimits` y en `rateLimitsByLimitId.codex`, y el `planType` de
  la cuenta.

## Gemini CLI 0.64.0 (`gemini-cli/0.64.0/`)

| Fichero | Comando |
|---|---|
| `01-sin-sesion.*` | `gemini -p "" -o stream-json --approval-mode plan` con «Responde OK» por stdin, sin sesión iniciada |

Observaciones:

- Sin autenticación: código de salida **41** (no documentado; la documentación cita 0, 1, 42 y 53), stdout vacío y el
  motivo en stderr («Please set an Auth method…»).
- En una carpeta no marcada como de confianza, el modo de aprobación se fuerza a `default` aunque se pida `plan`:
  el adaptador debe usar `--skip-trust` para que `plan` se respete.
- Falta capturar una ejecución autenticada: requiere que el usuario inicie sesión ejecutando `gemini`.

## Capturas sintéticas de los finales clasificados (Fase 8)

`claude-code/2.1.290/limits/` y `codex-cli/0.160.0/limits/` guardan capturas **sintéticas**, construidas a mano con la forma
de las reales y la documentación (Claude) o el código de la versión (Codex): no proceden de ninguna ejecución. Son los casos
de los que los analizadores deducen la clase del final de un intento (docs/16 D-41) y que aún no tienen captura real: una
ventana agotada, un presupuesto, el contexto lleno, la sesión caducada, una sobrecarga y la red. El README de cada carpeta da
la fuente de cada campo, lo inventado y lo que falta medir. Las pruebas exigen que cada señal de la que se deduce una clase
esté en una captura de su versión ([docs/06](../../../../docs/06-integracion-proveedores.md#final-clasificado-de-un-intento-fase-8-d-41)).
Cuando se capture un caso real, va a la carpeta de la versión con su `.exit` y su `.stderr`, y la sintética se borra.

## Regenerar las capturas de skills

Los tests `ProviderLive` envuelven el ejecutor de procesos real en un decorador de pruebas (`Live/RecordingProcessRunner`)
que guarda cada línea de salida tal como la recibe el adaptador. Con `AGENTSTUDIO_CAPTURE_DIR` definida, escriben ahí
`<nombre>.jsonl` (stdout), `.stderr`, `.exit`, `.cmd.txt` (la línea de comandos exacta) y un `evidence.log` con tiempos y
costes. Con el CLI ya instalado y sesión iniciada (Gemini no se ejecuta, está aparcado, ADR-0009):

```powershell
$env:AGENTSTUDIO_PROVIDER_TESTS = "1"
$env:AGENTSTUDIO_CAPTURE_DIR = "C:\ruta\a\capturas"
dotnet test --no-build -- --filter-class "AgentStudio.Tests.Providers.Skills.SkillVisibilityLiveTests"
```

Salen `f2-10-claude-skill-plugin.*` y `f2-10-codex-skill-agents.*`. Se guardan como `04-skill-plugin.*` y
`04-skill-agents.*` tras limpiar los datos personales sobre el texto, sin reserializar el JSON (cada línea sigue siendo
JSON válido):

1. `C:\Users\<usuario>\AppData\Local\Temp\agentstudio-tests-<guid>` pasa a `C:\Users\usuario\AppData/Local/Temp/agentstudio-fixtures`
   (también escapado como `\\` en el JSON).
2. El nombre que Claude da a su carpeta de proyecto (`C--Users-<usuario>-AppData-Local-Temp-agentstudio-tests-<guid>`) pasa a
   `C--Users-usuario-AppData-Local-Temp-agentstudio-fixtures`.
3. Cualquier otra mención del nombre de usuario pasa a `usuario`.
4. Se comprueba que no queda el nombre de usuario, ningún correo ni el nombre de la organización.

El mismo comando con `ProviderConcurrencyLiveTests` (criterio F2-5) deja `f2-5-run-1.*` y `f2-5-run-2.*` y la línea
temporal de los dos procesos en `evidence.log`; esas capturas no se versionan (miden tiempos reales, no un formato).

Con `ClaudeRepositoryIsolationLiveTests` y `CodexRepositoryIsolationLiveTests` salen `claude-planted-repository.*`,
`codex-planted-repository.*` y `codex-trusted-planted-repository.*`. Tampoco se versionan: prueban que no se ejecuta
la configuración plantada en un repositorio, no un formato. El de Codex con repositorio de confianza usa un
`CODEX_HOME` temporal sin credenciales y no gasta cuota.

Comprobación sin cuota de que Codex ve una skill materializada, útil para depurar antes de gastar una ejecución: en un
repositorio git con `.agents/skills/<skill>/SKILL.md`, `codex debug prompt-input` (sin prompt) imprime el prompt que vería
el modelo como JSON; la skill aparece en el mensaje `developer`. Para Claude Code, `claude --plugin-dir <carpeta> plugin
details agent-studio-session` lista el inventario del plugin sin llamar al modelo.
