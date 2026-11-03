# 14 · Fase 4 · Observabilidad y memoria: diseño

> **Estado:** aprobada el 2026-10-18 (decisiones de la §12 resueltas) e integrada en `main` ese mismo día. El estado de
> cada criterio y lo pendiente están en [docs/08](08-plan-y-criterios-de-aceptacion.md#fase-4--observabilidad-y-memoria).
> Lo que la §13 daba por sin verificar y se midió en la integración: la lectura de cuota de Codex (el app-server necesita
> la entrada abierta hasta responder, [ADR-0020](adr/0020-cuota-pasiva-en-claude-y-lectura-opcional-en-codex.md)) y el
> lanzamiento MCP de D-30 ([ADR-0024](adr/0024-recomendador-asistido-con-mcp-local.md)). Partió del código de `main` en
> `ec79a4c` (Fase 3 cerrada para uso personal).
>
> La redactó un arquitecto independiente a partir de cuatro fuentes:
>
> - el código del repositorio;
> - las capturas reales de los CLIs (`tests/AgentStudio.Tests/Providers/Fixtures/`, Claude Code 2.1.290 y Codex 0.160.0);
> - el código fuente de Codex en la etiqueta `rust-v0.160.0`, la versión instalada;
> - la documentación oficial (fuentes al final).
>
> **Sin cuota.** Solo se ejecutaron `claude --version`, `codex --version`, `claude auth status` (sin leer email ni
> organización), `codex login status` y las ayudas (`--help`). Ningún CLI recibió un prompt y no se llamó a ningún
> endpoint. Lo que te toca decidir está en la [§12](#12-decisiones-que-necesitan-al-usuario); lo que no se pudo
> verificar, en la [§13](#13-sin-verificar).

## 0. Las diez decisiones que vertebran la propuesta

1. **El consumo es una proyección del registro de decisiones.**
   - `AgentRunStarted` y `AgentRunFinished` ya llevan el uso de cada intento con su procedencia, encadenados por hash y
     con ancla externa.
   - La Fase 4 los amplía (v2) y calcula el panel a partir de ellos. No hay tabla de uso ni doble escritura (ADR-0010).
2. **Los CLIs informan acumulados, así que se deduplica por sesión.**
   - En Claude, `result.usage` suma todos los pasos de la ejecución, y una sesión reanudada informa del gasto de toda
     la sesión (documentado desde 2.1.277).
   - En Codex, `turn.completed.usage` es el total del hilo (leído en el código de 0.160.0).
   - Los reintentos, la corrección del planificador y la del juez reanudan la sesión. Sumar intentos contaría dos
     veces.
3. **Cada número lleva una de seis etiquetas:** informado por el CLI, medido por Agent Studio, estimado por el CLI,
   estimado por Agent Studio, no expuesto y sin dato.
4. **La salud del contexto se mide por paso, no por el total.**
   - La ocupación es la entrada más la caché leída y la creada del último paso (deduplicado por id del mensaje).
   - Se compara con la ventana del modelo que sirvió ese paso.
   - Con `result.usage` saldría el doble en una ejecución de dos pasos.
   - En `codex exec`, la ocupación no está expuesta.
5. **La cuota es pasiva en Claude y activa y opcional en Codex.**
   - Claude anuncia sus ventanas al empezar cada ejecución (`rate_limit_event`, ya analizado).
   - Codex solo las da por `account/rateLimits/read` del app-server: una lectura al backend de ChatGPT, sin turno del
     modelo (código de 0.160.0).
   - Esa lectura queda fuera de las tres sondas permitidas, así que necesita tu OK.
   - Se envía una lista cerrada de tres mensajes. Nunca `account/rateLimitResetCredit/consume`.
6. **La memoria es un dato anclado, nunca una instrucción.**
   - Filas append-only en SQLite, cada una anunciada en el registro con su SHA-256.
   - Confianza por origen: la de agentes es una propuesta hasta que la aceptas, y la heredada queda en cuarentena.
   - Llega a un prompt solo si la `MemoryPolicy` del agente lo permite, y va en un bloque delimitado y recodificado.
   - El juez nunca recibe memoria.
7. **El Markdown de la memoria se exporta y nunca se lee de vuelta.**
   - `.agentstudio/memory/` es contenido del repositorio, el adversario del punto 1 de ADR-0018.
   - Se escribe por el almacén comprobado, con nombres planos y nunca durante una ejecución: la comprobación del
     repositorio del usuario haría fallar la tarea.
8. **El empaquetado de contexto es determinista, por niveles, y falla cerrado.**
   - Orden de los niveles: requisitos obligatorios, decisiones, tareas pendientes y el resto por relevancia.
   - Si los requisitos obligatorios no caben, no se lanza el agente.
   - Lo omitido se informa.
9. **El recomendador asistido usa un servidor MCP local de solo lectura.**
   - Es un proceso propio por stdio, sin red, con dos herramientas de lectura sobre una instantánea del catálogo.
   - Va en una ejecución auxiliar de solo lectura con `--strict-mcp-config --mcp-config`, nunca en un ejecutor: la
     huella medida de ADR-0018 no cambia.
   - Gasta cuota, así que pide tu OK en cada uso. Activar sigue siendo cosa tuya.
10. **El resumen diario es determinista.**
    - Lee la bitácora por el almacén comprobado y entiende los formatos de sus dos escritores.
    - La versión narrativa por agente es opcional y con tu OK.

---

## 1. Alcance de la Fase 4

| ID | Entra | Pieza principal |
|---|---|---|
| F4-1 | Panel de consumo por proveedor, modelo, sesión, proyecto y periodo, con procedencia | Proyección `UsageProjector` sobre el registro (v2), agregación con cobertura y panel |
| F4-2 | Exportación CSV y JSON | `UsageExporter` (RFC 4180 y JSON con procedencia), con protección contra fórmulas |
| F4-3 | Salud del contexto con umbrales configurables, sin dar por exacto lo estimado | Evento `ContextObserved` por paso, `ContextHealthTracker`, umbrales en ajustes y monitor |
| F4-4 | Cuota expuesta por cada proveedor | Claude, de `rate_limit_event` (pasiva). Codex, de `account/rateLimits/read` (con tu opt-in). Gemini, «no expuesto» |
| F4-5 | Memoria por proyecto en SQLite y Markdown, cronológica y temática, con FTS5 | `ProjectMemory` (anclaje y confianza), `SqliteMemoryStore`, `MemoryMarkdownExporter` |
| F4-6 | Empaquetado que conserva requisitos críticos, decisiones y tareas pendientes | `ContextPacker` por niveles e integración en los prompts del planificador y del ejecutor |
| F4-7 | Pantalla del catálogo de skills | ViewModels y vistas `Desktop/Skills`, coste estimado de contexto y salud local |
| F4-8 | Recomendación determinista con razones y asistida por un MCP local que solo propone | `SkillRecommender` con detector de stack, `AgentStudio.SkillsMcp` y `SkillRecommendationService` |
| F4-9 | Resumen diario a partir de la bitácora | `DailySummaryBuilder` determinista sobre una lectura comprobada de la bitácora |

**Se aplaza:**

- **Reintentar en una sesión nueva cuando el contexto está alto.** Primero hay que medir con datos reales cuándo
  compensa. La Fase 4 solo lo muestra.
- **Propuestas de memoria hechas por los agentes** (`MemoryPolicy.ReadAndPropose`): no hay canal para ellas. Hasta la
  Fase 5 el dominio rechaza esa política (D-25), para no simular nada.
- **La recomendación de skills dentro de la planificación:** cambiaría el protocolo de ADR-0013.
- **Ejecutar Codex por el app-server,** que daría ocupación de contexto y cuota pasiva: es otro adaptador.
- **Importar memoria desde Markdown.** **Purgar memoria** (privacidad): «retirar» la oculta; borrarla de verdad queda
  para la Fase 7.
- **Coste estimado de Codex con una tabla de precios propia:** no se hará, porque sería un número inventado.
- **Caché de salud de skills y comprobaciones con red:** Fase 6.
- **Gemini:** aparcado ([ADR-0009](adr/0009-google-aparcado.md)). Todo lo suyo es «no expuesto».

---

## 2. Inventario: lo que ya existe

Estados: **Hecho** (código y tests), **Parcial** (existe con un hueco que se nombra), **Contrato** (interfaz sin
implementación), **Sin uso** (existe y nadie lo lee ni lo escribe) y **Falta**. Rutas relativas a la raíz del repositorio.

### F4-1 · Consumo

| Pieza | Dónde | Estado |
|---|---|---|
| `MetricValue<T>` y `MetricProvenance` (exacto, estimado o no disponible; al combinar gana el más débil) | `src/AgentStudio.Domain/Metrics/MetricValue.cs` | Hecho |
| `TokenUsage`, `UsageReported`, `QuotaReported`, `RateLimitSignaled` y `ProviderCapabilities.ReportsTokenUsage`/`ReportsSubscriptionQuota` | `src/AgentStudio.Application/Providers/AgentProvider.cs` | Parcial: sin desglose por modelo ni uso por paso |
| Analizador de Claude: tokens de `result.usage` (la entrada suma la caché creada y la leída), coste como estimado y ventana del modelo de `init` | `src/AgentStudio.Providers/Cli/Claude/ClaudeStreamJsonParser.cs` | Parcial: totales sin desglose; ventana mal emparejada (D-13) |
| Analizador de Codex: tokens exactos de `turn.completed.usage`; coste y ventana «no disponibles» | `src/AgentStudio.Providers/Cli/Codex/CodexExecJsonParser.cs` | Parcial: ignora `cache_write_input_tokens` y da por exacto un uso todo a cero |
| Analizador de Gemini, según su documentación | `src/AgentStudio.Providers/Cli/Gemini/GeminiStreamJsonParser.cs` | Hecho (aparcado) |
| Uso de cada intento en el registro: `AgentRunFinishedPayload.Usage` (`UsageSnapshot`, cada cifra con procedencia) | `src/AgentStudio.Application/Decisions/DecisionPayloads.cs`. Lo escriben `src/AgentStudio.Orchestration/Engine/AgentRunner.cs` (planificador y ejecutores) y `src/AgentStudio.Debate/Judging/IndependentReview.cs` (`JudgeRunRecorder`) | Parcial: faltan el modelo informado, la hora real de arranque y la sesión reanudada |
| `IUsageLedger` y `UsageRecord` | `src/AgentStudio.Application/Knowledge/MemoryAndUsageContracts.cs` | Contrato sin consumidores. Su `RecordAsync` contradice la proyección (D-01) |
| Tablas `agent_runs` y `usage_records` | `src/AgentStudio.Infrastructure/Persistence/Schema/0001_initial.sql` | Sin uso desde la Fase 3 ([05 §5](05-modelo-de-datos.md#5-tablas-de-la-0001-sin-uso-en-la-fase-3)) |
| Resultado de cada prompt en la bitácora: estado, duración y tokens con procedencia | `src/AgentStudio.Memory/Journal/MarkdownDailyJournal.cs`, `src/AgentStudio.Application/Providers/JournalingAgentProvider.cs` | Hecho (texto para personas) |
| Líneas de actividad del monitor: «Uso: …» y «Cuota: five_hour 47 %» | `src/AgentStudio.Desktop/Orchestrator/Monitor/MonitorTexts.cs` | Parcial: solo en vivo, con los nombres crudos de las ventanas |
| Panel de consumo | — | Falta |

### F4-2 · Exportación

| Pieza | Dónde | Estado |
|---|---|---|
| Escritura atómica de un fichero (temporal y movimiento) | `src/AgentStudio.Desktop/Services/AtomicFile.cs` | Hecho (se puede reutilizar) |
| Exportación CSV y JSON | — | Falta |

### F4-3 · Salud del contexto

| Pieza | Dónde | Estado |
|---|---|---|
| `ContextHealthEvaluator`, `ContextHealthThresholds` (70/85/95, validados) y `ContextHealthLevel` (`Unknown` nunca es «sano»; un dato estimado nunca da un resultado exacto) | `src/AgentStudio.Observability/ContextHealth/ContextHealthEvaluator.cs`; `tests/AgentStudio.Tests/Observability/ContextHealthEvaluatorTests.cs` | Hecho, sin consumidores |
| Ventana de Claude (`modelUsage.<modelo>.contextWindow`) | `ClaudeStreamJsonParser.ContextWindowOf` | Parcial: la empareja con el modelo de `init`, no con el del paso (D-13) |
| Uso por paso y compactaciones | — | Falta |
| Umbrales en los ajustes y en la UI | `src/AgentStudio.Desktop/Services/AppSettings.cs` no los tiene | Falta |

### F4-4 · Cuota

| Pieza | Dónde | Estado |
|---|---|---|
| Claude: `QuotaReported` (utilización exacta y reinicio por ventana) y `RateLimitSignaled` | `ClaudeStreamJsonParser.ParseRateLimit` | Parcial: no se guarda; solo deja una línea de actividad |
| Codex: `account/rateLimits/read` | — | Falta: no hay cliente del app-server. `CodexCliAgentProvider.Capabilities.ReportsSubscriptionQuota` es `false` |
| Gemini | — | No expuesto (aparcado) |

### F4-5 · Memoria

| Pieza | Dónde | Estado |
|---|---|---|
| `IProjectMemory`, `MemoryEntry`, `MemoryQuery` y `MemoryKind` (las diez categorías de la sección 14 del prompt maestro) | `src/AgentStudio.Application/Knowledge/MemoryAndUsageContracts.cs` | Contrato |
| `memory_entries` y `memory_entries_fts` (FTS5 de contenido externo) con sus triggers de sincronización | `0001_initial.sql` | Parcial: **no es append-only**. Tiene triggers `AFTER UPDATE` y `AFTER DELETE`, y `superseded_by` obliga a un UPDATE |
| `MemoryPolicy` por agente (`None`, `ReadSelected`, `ReadAndPropose`). Entra en el hash de la plantilla y en el de la cabecera de la ejecución | `src/AgentStudio.Domain/Agents/AgentDefinition.cs`; `src/AgentStudio.Application/Orchestration/RosterHash.cs` | Hecho, sin efecto: la plantilla por defecto pone `None` a los tres (`src/AgentStudio.Desktop/Orchestrator/Session/DefaultAgentRoster.cs`) |
| Escritura en `.agentstudio` sin seguir enlaces: `IProjectStorageFiles.OpenForAppend` y `Replace` | `src/AgentStudio.Application/Projects/ProjectStorageFiles.cs`, `src/AgentStudio.Infrastructure/Persistence/ProjectStorageFiles.cs` | Parcial: solo admite `carpeta/fichero` (`Locate`) y no tiene lectura |
| `ISecretRedactor` | `src/AgentStudio.Application/Security/ISecretRedactor.cs`, `src/AgentStudio.Security/Redaction/SecretRedactor.cs` | Hecho (lo usa el log) |
| Recuperación y exportación a Markdown | — | Falta |

### F4-6 · Empaquetado de contexto

| Pieza | Dónde | Estado |
|---|---|---|
| Datos delimitados en los prompts: `PromptData` (JSON con `<`, `>`, `&` y comillas escapados) y los bloques `<upstream_output … sha256=…>` y `<previous_attempt>` | `src/AgentStudio.Orchestration/Engine/PromptData.cs`, `…/Engine/ExecutorPrompt.cs` | Hecho (es el patrón a reutilizar) |
| Requisitos citados literalmente del conjunto congelado | `src/AgentStudio.Orchestration/Planning/PlannerPrompt.cs`, `ExecutorPrompt.cs` | Hecho |
| Empaquetador | — | Falta |

### F4-7 · Pantalla del catálogo

| Pieza | Dónde | Estado |
|---|---|---|
| Dominio: `SkillEntry`, `SkillSectors` (16), `SkillTags`, `SkillActivationResolver`, `SkillHealthReport`, `SkillHealthChecks` (licencia y fijación) y `SkillFrontmatter.Validate` (frontmatter y tamaño) | `src/AgentStudio.Domain/Skills/**` | Hecho |
| `SkillsIndexCatalogImporter`, `SkillCatalog` (carpeta local con comprobación de enlaces y avisos) y `SkillActivationStore` (proyecto y agente en SQLite; globales en `%APPDATA%\AgentStudio\skill-activations.jsonl`) | `src/AgentStudio.Infrastructure/Skills/**` | Hecho |
| `ISkillCatalog`, `ISkillActivationStore` e `ISkillProvisioner` (con `SkillProvisioner`) | `src/AgentStudio.Application/Skills/**` | Hecho |
| `ISkillHealthChecker` | ídem | Contrato (Fase 6) |
| Entrada «Skills» de la barra de actividad | `src/AgentStudio.Desktop/ViewModels/MainViewModel.cs` | Desactivada, con «Disponible en la Fase 4» |
| Tablas `skills` y `skill_health_checks`, que [05](05-modelo-de-datos.md) reservaba para la 0004 | — | No escritas; se retiran (D-27) |
| Pantalla y coste de contexto | — | Falta |

### F4-8 · Recomendación

| Pieza | Dónde | Estado |
|---|---|---|
| `SkillRecommender`, determinista, con razones en castellano y sin resultados de puntuación 0 | `src/AgentStudio.Domain/Skills/SkillRecommender.cs`; `tests/AgentStudio.Tests/Domain/Skills/SkillsDomainTests.cs` | Hecho |
| Etiquetas de contexto del proyecto | — | Falta (Auto Graph es de la Fase 6) |
| Servidor MCP local | — | Falta |
| Lanzamiento de Claude con servidores MCP | `ClaudeCodeArguments` siempre pasa `--strict-mcp-config` y nunca `--mcp-config` (`src/AgentStudio.Providers/Cli/Claude/ClaudeCodeArguments.cs`, líneas 148 y 149) | Falta: hoy la sesión no tiene ningún servidor |

### F4-9 · Resumen diario

| Pieza | Dónde | Estado |
|---|---|---|
| Escritores de la bitácora: `MarkdownDailyJournal` en el producto (entrada `- **HH:mm:ss** · P-NNNN · actor · proveedor/modelo · tags · «resumen»` y, debajo, su línea `Resultado P-NNNN · …`) y el hook de desarrollo (entradas con `sesión xxxxxxxx` en lugar de `P-NNNN`, más las líneas «Hecho…» que añade Claude en varias formas) | `src/AgentStudio.Memory/Journal/`, `.claude/hooks/bitacora.ps1` | Hecho |
| Panel «Bitácora de hoy», de solo lectura | `src/AgentStudio.Desktop/ViewModels/JournalViewModel.cs` | Parcial: lee con un `FileStream` directo, sin la comprobación de enlaces |
| `MemoryKind.DailySummary` | contratos de memoria | Contrato |
| Resumen | — | Falta |

**Pantallas que existen hoy:**

- Bienvenida, Explorador, editor Monaco con diff, Terminal ConPTY, Auto Doctor (proveedores), «Bitácora de hoy» e «Ir al
  fichero».
- La pestaña Orquestador: preparación, lienzo, propiedades del nodo, monitor y aprobaciones.
- En la barra de actividad, «Skills» (Fase 4) y «Pruebas» (Fase 7) siguen desactivadas. «Agentes» también, aunque su
  texto dice «Fase 3», ya cerrada.
- No hay ni consumo, ni contexto, ni memoria.

---

## 3. Lo que exponen de verdad los CLIs

### 3.1 Claude Code 2.1.290

| Señal | Dónde | Qué es | Cómo se sabe |
|---|---|---|---|
| Uso por paso | `assistant.message.usage` (`input_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`), con `message.id` y `message.model` | El prompt de esa llamada: la ocupación del contexto. Varios eventos de un mismo paso comparten id y uso, así que hay que deduplicar. El `output_tokens` por paso es provisional | Capturas 01, 02 y 04; doc «Track cost and usage» |
| Uso total | `result.usage` | Suma de los pasos del bucle principal. En `02-herramienta`: entrada 9+8 = 17, caché creada 30 391+423 = 30 814 y caché leída 0+30 391. Deja fuera los subagentes | Captura 02; doc |
| Uso por modelo | `result.modelUsage.<modelo>`: `inputTokens`, `outputTokens`, `cacheReadInputTokens`, `cacheCreationInputTokens`, `costUSD`, `contextWindow`, `maxOutputTokens`, `costBasis` | Incluye los modelos auxiliares y los subagentes. En `01-texto`, `init` anuncia `claude-haiku-4-5-20251001`, pero el bucle principal corrió en `claude-sonnet-5-5`, con ventana de 1 000 000 | Captura 01; doc |
| Coste | `total_cost_usd`, `costUSD`, `costBasis` (`list`, `managed`, `unknown`; desde 2.1.246) | Estimación del cliente con una tabla de precios embebida: «client-side estimates, not authoritative billing data». Con suscripción no es lo que pagas | Doc |
| Sesión reanudada | `--resume` | Desde 2.1.277 el resultado incluye el gasto anterior de la sesión. Los totales se guardan cuando el proceso sale con normalidad | Doc; sin medir |
| Cuota | `rate_limit_event.rate_limit_info`: `status`, `rateLimitType`, `overageStatus` y `unifiedWindows.{five_hour,seven_day}.{utilization,resetsAt}` | Llega al empezar cada ejecución, aunque no se alcance el límite | Capturas 01 y 02 |
| Compactación | `system/compact_boundary` con `compact_metadata.{trigger,pre_tokens}` | Documentado en los tipos del SDK | **Sin verificar** en captura: las ejecuciones grabadas no compactan |
| Razonamiento | `system/thinking_tokens.estimated_tokens` | Estimación del CLI durante la ejecución | Capturas |
| Tiempo y turnos | `duration_ms`, `duration_api_ms`, `num_turns` | Medidos por el CLI | Capturas |

### 3.2 Codex CLI 0.160.0

| Señal | Dónde | Qué es | Cómo se sabe |
|---|---|---|---|
| Uso | `turn.completed.usage`: `input_tokens` (con la caché), `cached_input_tokens`, `cache_write_input_tokens`, `output_tokens` (con el razonamiento) y `reasoning_output_tokens` | Es `ThreadTokenUsage.total`, el acumulado del **hilo**, no del turno. Si no llegó ninguna notificación de uso, sale todo a cero (`Usage::default()`) | `codex-rs/exec/src/event_processor_with_jsonl_output.rs` en `rust-v0.160.0`; capturas |
| Modelo y ventana | — | `exec --json` no los imprime, aunque recibe la notificación que trae `last` y `modelContextWindow` | En la etiqueta se comprobó que `exec` solo emite `total`; la estructura con `last` y `modelContextWindow` se leyó en `main` |
| Cuota | — | `exec` no la imprime. Solo el app-server (§4.3) | Ídem |
| Coste | — | No expuesto | — |

**App-server** (`codex app-server`, JSON-RPC 2.0 sin el campo `"jsonrpc"`, JSONL por stdio; «experimental and aren't
supported for production workloads»). Leído en `codex-rs/app-server-protocol/src/protocol/common.rs` de `rust-v0.160.0`:

- **De lectura:**
  - `account/rateLimits/read`, sin marca `experimental`;
  - la notificación `account/rateLimits/updated`;
  - `account/usage/read`, resúmenes de actividad de la cuenta;
  - la notificación `thread/tokenUsage/updated`, con `total`, `last` y `modelContextWindow`.
- **Que cambian algo, y Agent Studio nunca llama:**
  - `account/rateLimitResetCredit/consume`, que gasta un reinicio guardado;
  - `account/sendAddCreditsNudgeEmail`, que envía un correo;
  - `account/login/*`, `account/logout` y `config/value/write`.

### 3.3 Gemini CLI

Aparcado. Su analizador da tokens de `result.stats`, sin ventana ni coste, y la cuota de plan no tiene interfaz.

---

## 4. Decisiones por criterio

### 4.1 F4-1 y F4-2 · Consumo y exportación

**D-01 · De dónde sale el uso.**

| Opción | Ventajas | Inconvenientes |
|---|---|---|
| **A. Registro de decisiones** (`AgentRunStarted`/`AgentRunFinished` v2), elegida | Ya está encadenado y anclado y cubre planificador, ejecutores y juez. Hay una sola fuente (ADR-0010), y el panel solo enseña cifras de un registro que verifica | Hay que ampliar los payloads y registrar también las ejecuciones auxiliares (D-08). Se lee el registro entero en cada consulta |
| B. Tabla de uso propia, escrita por un decorador del proveedor | Cubre cualquier lanzamiento | Doble escritura que puede divergir. La tabla de la 0001 no es append-only ni está anclada |
| C. La bitácora | Ya tiene todos los prompts | Es texto para personas, con pérdidas. Además es contenido del repositorio (ADR-0018, punto 1): un repositorio de terceros traería cifras falsas |
| D. Ficheros de sesión de los CLIs (`~/.codex/sessions`, transcripciones de Claude) | Datos completos | Prohibido: Agent Studio nunca lee las carpetas de los CLIs |

Recomendación: **A**.

- La proyección se calcula en memoria. La 0004 no crea tabla de uso; una caché solo si una medición con un registro
  grande lo pide.
- `IUsageLedger.RecordAsync` desaparece: el registro ya es el libro.

**D-02 · Acumulados por sesión.** Una sesión es la pareja (proveedor, `SessionId`).

- **Uso propio de un intento:** su instantánea menos la del último intento anterior de la misma sesión que terminó con
  resultado.
- **Si alguna cifra baja,** el CLI no acumuló: el intento cuenta su instantánea entera, «estimado por Agent Studio».
- **Total de la sesión:** su última instantánea.
- **Intento sin resultado** (interrumpido, cancelado o tiempo agotado): «sin dato». Su gasto no se recupera, porque la
  salida por paso es provisional.
- `AgentRunFinished` v2 registra `ResumedSession`, la sesión que reanudó, para saber qué intentos la comparten.
- Afecta al planificador (su corrección usa `--resume`), a los reintentos de los ejecutores (`ResumeSessionId`) y a la
  corrección del juez.

**D-03 · Procedencia visible.**

| Etiqueta | Cuándo | Ejemplos |
|---|---|---|
| exacto · informado por el CLI | El CLI da la cifra | Tokens de Claude y de Codex, utilización de la cuota |
| exacto · medido por Agent Studio | La mide el sistema | Ejecuciones, duración, errores por límite |
| estimado por el CLI | El CLI la da como estimación | Coste de Claude (precio de lista), tokens de razonamiento en vivo |
| estimado por Agent Studio | La calcula el sistema con datos parciales | Caracteres entre 4, ventana aprendida (D-10), diferencia de sesión dudosa (D-02) |
| no expuesto | El CLI no la ofrece | Coste y ventana de Codex; todo lo de Gemini |
| sin dato | El CLI la ofrece, pero no llegó | Intento interrumpido |
| parcial (n de m) | Un agregado al que le faltan valores | — |

- **Contrato.** `MetricValue<T>` gana `Origin`, con esas seis etiquetas. `Provenance` sigue existiendo y se deriva de
  `Origin`, para no romper nada.
  - Los creadores que ya existían no cambian de significado: `Unavailable` sigue siendo «no expuesto», como dice su
    comentario.
  - `Missing` es nuevo, para «sin dato». WP1 lo usa donde hoy se escribe `Unavailable` para un dato que no llegó.
- **Payload.** `MetricSnapshot` gana `Origin`. En las entradas v1 vale `null`, que se lee como «informado por el CLI»:
  todas venían de un analizador.

**D-04 · Agregación.**

- **Dimensiones:**
  - proveedor y vendor;
  - modelo: el informado; si no lo hay, el configurado; si tampoco, «predeterminado del CLI (no expuesto)»;
  - sesión del CLI, ejecución de orquestación, tarea, agente y propósito;
  - proyecto;
  - periodo: día, semana ISO o mes, en tu zona horaria.
- **Cifras:**
  - ejecuciones (intentos);
  - duración: desde que el proceso arrancó (`LaunchedAt`), no desde `AgentRunStarted`, que incluye la espera en la
    cola del proveedor;
  - tokens: entrada, caché leída, caché escrita, salida y razonamiento;
  - coste estimado;
  - errores por límite de uso (`RateLimited`) y por presupuesto (`LimitReached`);
  - pico de contexto y compactaciones.
- **Reglas de los agregados** (`MetricAggregate<T>`):
  - nunca se mezclan procedencias en silencio: gana la más débil;
  - si falta algún valor, la cifra sale «parcial (n de m)».
- **Desglose por modelo:** sale de v2. Una entrada v1 aparece como «sin desglose (registro anterior a la Fase 4)».

**D-05 · Por proyecto.**

- Hay una base por proyecto. El panel muestra el proyecto abierto y, si lo pides, todos los proyectos recientes de
  tus ajustes.
- Los recientes se leen con `IProjectLogReader`: solo lectura y sin cerrojo. No escribe filas, no migra la base y no
  mueve anclas. SQLite puede crear los ficheros `-wal` y `-shm` de la base, que ya están excluidos de git y de la
  comprobación del repositorio.
- Rechaza los enlaces, igual que la apertura del proyecto, y verifica la cadena contra el ancla sin moverla.
- Un proyecto cuyo registro no verifica, o cuyo historial no tiene un ancla reconocida, se lista como excluido con su
  motivo. Nunca se mezcla.

**D-06 · Coste.**

- **Claude:** coste por modelo y total, como «estimado por el CLI (precio de lista)», con su `costBasis` y la nota
  «con suscripción no es lo que pagas». Así se cumple el «costes de API cuando corresponda» de la sección 11.
- **Codex:** «no expuesto». No se mantiene una tabla de precios propia.

**D-07 · Exportación (F4-2).**

- **CSV:**
  - RFC 4180, UTF-8 con BOM (para Excel), separado por comas, números en cultura invariante y fechas ISO-8601 en UTC
    con su hora local.
  - Una fila por intento y modelo (o por intento, sin desglose). Cada cifra lleva tres columnas: valor, `origin` y
    `source`. El valor va vacío si no está disponible.
- **JSON:** `{ "schema": "agentstudio-usage/1", generatedAt, zone, filters, projects[…], attempts[…], aggregates[…] }`.
  Cada cifra es `{ value, origin, source }`, con `value: null` cuando no está disponible.
- **Protección contra fórmulas.** Un campo de texto que empieza por `=`, `+`, `-`, `@`, tabulador o retorno de carro
  lleva un `'` delante: los errores vienen de CLIs y agentes.
- **Lo que nunca sale:** prompts y salidas de agentes. Solo identificadores y SHA-256.
- **Destino:** el fichero que eliges con «Guardar como», escrito de forma atómica.

**D-08 · Ejecuciones auxiliares.**

- El recomendador (D-29) y el resumen por agente (D-31) son ejecuciones de agente fuera de una orquestación. Pasan por
  `AuxiliaryAgentRunner` (Application), igual que el juez:
  - el prompt como evidencia;
  - `AgentRunStarted` antes del lanzamiento;
  - `AgentRunFinished` v2 al terminar.
- Cada una lleva un `RunId` nuevo, que no está en `orchestration_runs`, y su `Purpose`. Así el panel las cuenta y
  `ListRunsAsync` no las enseña.
- **Conciliación.** El panel compara los prompts de la bitácora del periodo con los intentos del registro y avisa de
  la diferencia, sin contarla: por ejemplo, las pruebas en vivo o una versión anterior.

### 4.2 F4-3 · Salud del contexto

**D-09 · Señales por CLI.**

| CLI | Ocupación | Ventana | Compactaciones | Etiqueta |
|---|---|---|---|---|
| Claude | Último paso deduplicado: entrada + caché leída + caché creada | `modelUsage[modelo del paso].contextWindow` al terminar; en vivo, la última conocida de ese modelo (D-10) | `compact_boundary` (documentado, sin verificar) | Exacto al terminar; estimado en vivo |
| Codex (`exec`) | No expuesta: el uso es el total acumulado del hilo, no la ocupación | No expuesta | No expuestas | «no expuesto (codex exec)». El app-server sí da `last` y `modelContextWindow`, pero sería otro adaptador |
| Gemini | No expuesta | No expuesta | No expuestas | Aparcado |

Nunca se deriva la ocupación de `result.usage`. En `02-herramienta` daría 61 222 tokens, frente a los 30 822 del
último paso: el doble.

**D-10 · Ventana en vivo.**

- Hasta que llega `result`, la ventana es la última vista para ese modelo en el registro del proyecto, con la
  etiqueta «estimado por Agent Studio (ventana vista en una ejecución anterior)».
- Sin ninguna anterior, la ocupación sale en tokens, sin porcentaje, hasta el final.
- **Desde la tanda 2 (2026-11-03), una tabla base de ventanas por modelo** (`ModelWindowTable`, versión `2026-10-30`;
  [ADR-0021](adr/0021-salud-del-contexto-por-paso.md), adenda del 2026-11-03). Si el registro no ha visto el modelo, la
  ventana sale de esta tabla. Así la primera ejecución de un proyecto ya tiene porcentaje desde su primer paso.
  - Siempre es «estimado por Agent Studio», y el origen nombra la tabla y su versión. Nunca dice «exacto».
  - Lo aprendido del registro del proyecto manda sobre la tabla, y la ventana que da el CLI manda sobre las dos.
  - Un modelo que la tabla no conoce sigue sin porcentaje: solo tokens.
  - Codex no tiene entradas: su contexto sigue «no expuesto».
  - Las ventanas de cada proyecto se leen de su registro fuera del hilo de la interfaz (`ContextWindowCatalogs`), al
    abrir su monitor y al terminar cada ejecución. Mientras tanto se usa la tabla sola. Una lectura que falla no borra lo
    aprendido.

**D-11 · Umbrales configurables.**

- Son globales, en `%APPDATA%\AgentStudio\settings.json` (aviso, compresión y crítico), y se validan con
  `ContextHealthThresholds` (0 < aviso < compresión < crítico ≤ 1).
- Por defecto, 70, 85 y 95 % (decisión 6 de la §12). No hay umbrales por agente en esta fase.
- **El evaluador pasa al dominio** (`Domain/Metrics/ContextHealth.cs`): es puro, y así el motor podrá usarlo cuando
  llegue la política de reintento aplazada.

**D-12 · Qué se muestra.**

- **En el monitor, por tarea en curso:** ocupación con su porcentaje y su etiqueta, tokens y ventana, modelo, nivel y
  compactaciones.
- **En la barra de estado:** la tarea seleccionada.
- **En el panel de consumo:** el pico de contexto de cada intento.
- **Contexto empaquetado por Agent Studio:** «N caracteres (exacto) · ~T tokens (estimado)» y «omitidas por tamaño:
  k entradas». Es la «posible pérdida de información» de la sección 10, junto con las compactaciones.
- Nunca se habla de la «comprensión» del modelo.

**Desde la tanda 2 (2026-11-03): barras siempre visibles y en tiempo real** (P3 y P36 de
[18](18-peticiones-y-pendientes.md)).

- **Un solo control**, `MetricBar`, para el contexto en vivo, la cuota y el presupuesto: la cifra, su origen y su nivel
  escritos junto a la barra. Lo estimado lleva «~», la palabra «estimado» y una trama. En el panel de consumo, el pico de
  contexto de cada grupo y de cada intento sigue como cifra, sin barra; allí las barras son las ventanas de la cuota.
- **La barra está siempre, también sin porcentaje.** Sin cifra, la pista se dibuja vacía y las palabras dicen por qué:
  «no expuesto», «sin observaciones todavía», «sin empezar» o los tokens con «sin porcentaje». Nunca es una barra a cero.
- **En vivo, de verdad.** El monitor crea un `ContextHealthTracker` por intento, y la planificación otro para el
  orquestador. Antes ese seguimiento no lo usaba ninguna pantalla.
- **En la barra de estado** ya no va la tarea seleccionada, sino la tarea en curso más llena: «Contexto · T1, la más
  llena de 3». Sin tareas en curso, «sin tareas en curso».
- **Sigue pendiente** el «contexto empaquetado» en la barra de estado (P31 de [18](18-peticiones-y-pendientes.md)).

**D-13 · Corrección del analizador de Claude.**

- La ventana se empareja con el modelo del paso, no con el de `init`.
- Ejemplo de `01-texto`: el analizador toma la ventana de haiku (200 000) para tokens de `claude-sonnet-5-5`, con
  ventana de 1 000 000. Da un 21 % en lugar de un 4 %.

### 4.3 F4-4 · Cuota

**D-14 · Claude: cuota pasiva.**

- Cada `QuotaReported`, de cualquier ejecución, se guarda en un registro del usuario,
  `%LOCALAPPDATA%\AgentStudio\usage\quota.jsonl`: solo de añadido y rotado por tamaño.
- Lo escribe `QuotaObservingAgentProvider`, un decorador del pipeline de proveedores, así que cubre también las
  ejecuciones auxiliares.
- La cuota es de la cuenta, no del proyecto: por eso va en el perfil.
- El panel muestra la última observación de cada ventana con su antigüedad («vista hace 12 min»), como «exacto ·
  informado por el CLI». No hay ninguna llamada extra.
- **Desde la tanda 2 (2026-11-03):**
  - **La antigüedad va junto a la cifra** en toda barra de cuota, también en la forma corta de la barra de estado:
    «47 % exacto · vista hace 12 min». La cuota es lo que dijo el proveedor en un momento dado, no lo que hay ahora.
  - **Una ventana cuyo reinicio ya pasó** no enseña su cifra como actual. La barra sale vacía y dice «se reinició a las
    15:00, sin dato nuevo». La cifra de antes queda en el detalle («antes del reinicio: 47 %»).
  - La barra de estado relee el registro de cuota cada minuto y cuando un CLI anuncia su cuota. Releer el fichero no
    gasta nada.

**D-15 · Codex: lectura del app-server.**

La superficie exacta es `codex app-server --listen stdio://`. Se envían tres mensajes de texto constante, sin texto libre:

```jsonl
{"method":"initialize","id":1,"params":{"clientInfo":{"name":"agent_studio","title":"Agent Studio","version":"<versión>"}}}
{"method":"initialized","params":{}}
{"method":"account/rateLimits/read","id":2,"params":{"excludeResetCreditDetails":true}}
```

Agent Studio espera la respuesta de `id` 2, con un tiempo máximo de 20 s, y termina el árbol de procesos.

- **Lo que hace** (código de `rust-v0.160.0`):
  - **Pide sesión de ChatGPT.** Sin ninguna sesión contesta «codex account authentication required to read rate
    limits». Con una API key contesta «chatgpt authentication required to read rate limits», que se muestra como «no
    expuesto: Codex usa API key».
  - **Hace un GET** a `{chatgpt_base_url}/wham/usage` (o a `/api/codex/usage`) con las credenciales del propio CLI. No
    hay turno del modelo; el PR #5302 lo describe como consultar los límites «without having to send a message».
  - **Al arrancar,** el app-server lanza además trabajos de fondo: catálogo de modelos, coste de turnos, plugins y
    vigilancia de skills. Leído en el código, sin medir. Ninguno es un turno.
- **Parámetros:**
  - `excludeResetCreditDetails: true` se ahorra una segunda petición;
  - nunca `supportsLunaReserve`, que dejaría al backend registrar la exposición a un experimento.
- **Respuesta, por lista blanca.**
  - **Se toma:** de `rateLimitsByLimitId` (o `rateLimits`), cada bucket con `limitId`, `limitName`,
    `primary`/`secondary` (`usedPercent` como utilización exacta, `windowDurationMins` y `resetsAt`),
    `rateLimitReachedType` y `planType`; también `ordinaryUsageAllowed`.
  - **Nunca se guarda:** `accountId`, `rateLimitUpsell`, el saldo de `credits` ni el detalle de los créditos de
    reinicio.
- **Lanzamiento:**
  - el `codex.exe` nativo (`CodexExecutableResolver`), dentro del Job Object;
  - como directorio de trabajo, una carpeta vacía de `%LOCALAPPDATA%\AgentStudio\runs\<id>`, nunca un repositorio, para
    que no cargue la capa `.codex/` de un proyecto;
  - lee `~/.codex/config.toml`, porque `app-server --help` no ofrece `--ignore-user-config`. Si eso arranca los
    servidores MCP de tu configuración sin un hilo está **sin verificar** (§13).
- **Cuándo:** desactivado por defecto. Con tu opt-in, bajo demanda («Consultar cuota de Codex») y una vez al terminar
  cada ejecución de Codex. Nunca en segundo plano en esta fase.
  - **Cómo se cumple desde la tanda 2 (2026-11-03; P37 de [18](18-peticiones-y-pendientes.md)).** Hasta entonces la
    lectura tras cada ejecución no existía. Ahora el pipeline de proveedores avisa del final de cada ejecución con su
    proveedor (`IAgentRunEnds`). Tras una de Codex, la cuota que enseñan «Consumo», la barra de estado y la bandeja
    (`QuotaViewModel`, una para toda la app) se lee si tienes el permiso y la última lectura tiene más de 5 minutos.
  - **Una sola lectura para todos.** El motor (cuando un límite corta un intento de Codex) y las pantallas pueden pedirla
    a la vez. `SharedQuotaReader` hace que sea una sola: el app-server se lanza una vez y cada uno recibe la respuesta.
    Nunca corre en el hilo de la interfaz ([ADR-0020](adr/0020-cuota-pasiva-en-claude-y-lectura-opcional-en-codex.md),
    adenda del 2026-11-03).
- **Versión:** la respuesta se fija por versión del CLI con una captura grabada (F4-4). Con otra versión se muestra
  «versión de Codex no probada», como hace Auto Doctor (F2-7).
- **Regla y términos:**
  - la llamada queda fuera de las tres sondas que permite `CLAUDE.md`, así que la regla la amplías tú (§12, decisión 1);
  - la documentación del app-server dice «App-server authentication has never been permitted for commercial or hosted
    services». Es uso personal ([ADR-0003](adr/0003-integracion-por-clis-oficiales.md)).

**D-16 · Gemini.** «No expuesto» ([ADR-0009](adr/0009-google-aparcado.md)).

**D-17 · Texto del criterio.** El texto actual de F4-4 («en el resto, no expuesto») es anterior a la Fase 2, que
comprobó que Claude anuncia su cuota en cada ejecución. Texto propuesto para [08](08-plan-y-criterios-de-aceptacion.md),
que actualiza el lead:

> F4-4 · Cuota de suscripción expuesta por cada proveedor: Claude, las ventanas de `rate_limit_event` de cada
> ejecución; Codex, `account/rateLimits/read` con tu opt-in; Gemini, «no expuesto» · Tests con salidas grabadas.

### 4.4 F4-5 y F4-6 · Memoria y empaquetado

**D-18 · Qué se guarda.**

| Categoría | De dónde sale | Origen |
|---|---|---|
| `Decision` | Aprobaciones y denegaciones del plan y de la entrega; resumen del plan aprobado | Sistema para el hecho; agente para el resumen, que escribió el orquestador |
| `PendingTask` | Tareas sin completar al terminar una ejecución, con su motivo estructurado; los `openIssues` del informe | Sistema; agente |
| `KnownError` | Verificación fallida (las pruebas que fallan, del TRX), conflictos de integración y alertas de auditoría | Sistema |
| `Change` | Integraciones y entregas: commit y rutas cambiadas | Sistema |
| `Verdict` | Decisión del juez y su validez; resumen del juez | Sistema; agente |
| `DailySummary` | Resumen diario (D-31) | Sistema, derivado de la bitácora, que es contenido del repositorio |
| `Architecture`, `Convention`, `Solution` | Notas que escribes tú | Usuario |
| `DebateSummary` | Fase 5 | — |

- **Cuándo se derivan.** Desde el registro verificado, después de cada ejecución y al abrir el proyecto. Nunca las
  escribe el motor durante una ejecución.
- **Idempotencia.** Cada entrada cita su fuente: ejecución y secuencia, o evidencia y SHA-256. Por eso derivar dos veces
  no duplica nada.
- **Antes de guardar,** el contenido pasa por `ISecretRedactor` y tiene un límite de 4 000 caracteres por entrada.

**D-19 · Almacén y anclaje.** Las filas son append-only. El estado sale del registro, como el de una ejecución
(ADR-0010):

- `MemoryRecorded` (id, categoría, origen, SHA-256 canónico del tema y el contenido, fuente y a qué entrada sustituye)
  se escribe antes que la fila. No lleva el texto: ningún payload lleva salida de agentes
  ([04 §11](04-contratos.md#11-registro-de-decisiones-y-payloads)), y el resumen de la entrada del registro es un texto
  del sistema.
- `MemoryReviewed` (aceptada, rechazada o retirada, y por quién) cambia el estado.
- **Recuperar** solo devuelve filas anunciadas en el registro con el mismo SHA-256. Una fila sin anunciar, o
  modificada, se ignora y deja una alerta de integridad.
- **Un anuncio sin fila** (la app murió entre los dos pasos) se ignora. Volver a derivar la entrada la completa, porque
  la derivación es idempotente.
- **Ninguna ejecución.** Estos payloads llevan `RunId` vacío. Así nunca entran en la reproducción ni en el dossier del
  juez, que seleccionan por ejecución.
- **Fallo cerrado.** Con un registro que no verifica, escribir memoria falla (`DecisionLogIntegrityException`).
- **Nada durante una ejecución.** Toda escritura de memoria en el registro (derivación, notas tuyas y revisiones) espera a
  que `IRunActivity.IsBusy` sea falso, y la UI la deja en cola. Así la cabeza del registro no se mueve entre que se
  enseña una aprobación o un veredicto y se usa.
- **Por qué el anclaje.** Un repositorio de terceros puede traer un `.agentstudio/agentstudio.db` con memoria
  envenenada: la exclusión vive en tu `info/exclude`, no en su historial. Con el anclaje, esa memoria corre la suerte
  del historial: sin un ancla reconocida, bloquea hasta que lo aceptas (A-01, Fase 3).

**D-20 · Confianza.**

| Origen | Estado inicial | ¿Entra en un prompt (`ReadSelected`)? | ¿Se exporta? |
|---|---|---|---|
| Sistema, solo con texto del sistema | De confianza | Sí | Sí |
| Usuario | De confianza | Sí | Sí |
| Agente (cita texto que escribió un agente) | Propuesta | Solo si la aceptas | Solo si la aceptas |
| Heredada: anclada antes del último «historial aceptado como nuevo origen» (`decision-log.origin-accepted`) | Cuarentena | No, hasta que la revises | No |
| Rechazada o retirada | — | No | No |
| No anclada o modificada | — | No (alerta) | No |

`DailySummary` no entra en los prompts por defecto: aporta poco y sale de la bitácora.

**D-21 · Recuperación FTS5.**

- Se ordena por `bm25` sobre tema y contenido. Se filtra por categoría, tema y confianza, y se quita lo sustituido.
- Se respeta el presupuesto de entradas y de caracteres. Los empates van por fecha (lo más reciente primero) y luego
  por id.
- **Escape de la consulta.** El texto se trocea y cada término va como frase entre comillas, con `"` doblada. Así nunca
  llegan operadores (`NEAR`, `*`, `^`, `columna:`) desde lo que escribe una persona o un agente.
- **Tokenizador `unicode61`,** el de la 0001: pliega los acentos, pero no lematiza en castellano («prueba» no encuentra
  «pruebas»). Es una limitación aceptada.

**D-22 · Markdown: solo exportación.**

- **Nunca se lee de vuelta** ([ADR-0005](adr/0005-sqlite-y-markdown.md)): `.agentstudio/memory/` es contenido del
  repositorio.
- **Nombres planos,** porque `IProjectStorageFiles` solo admite `carpeta/fichero`: `memory/cronologico-AAAA-MM.md` y
  `memory/tema-<categoria>.md` (diez como mucho).
- **Escritura:** el fichero entero con `Replace`, en orden determinista, con una cabecera que dice que se genera y que
  editarlo no cambia la memoria.
- **Contenido:** solo lo de confianza y lo aceptado.
- **Nunca durante una ejecución, ni mientras se planifica.** `UserRepositoryGuard` compara todo lo que hay bajo
  `.agentstudio/` salvo unas rutas exactas, así que una exportación en ese momento haría fallar la tarea. El puerto
  `IRunActivity` lo comprueba. Se exporta al terminar cada ejecución y cuando lo pides.
- Si se versiona o no es la decisión 4 de la §12.

**D-23 · Memoria en los prompts.**

- **Por agente, con `MemoryPolicy`,** que ya está en la plantilla y en el hash de la cabecera. Con `None` no hay
  memoria. Con `ReadSelected`, un paquete (D-24).
- **`ReadAndPropose`:** el dominio la rechaza hasta la Fase 5 (D-25).
- **Juez y auditor:** siempre `None`. Una regla nueva, `agent.judge-memory-none`, va en `AgentDefinition.Validate`. El
  veredicto solo se apoya en el dossier.
- **Qué recibe cada uno:**
  - **planificador:** decisiones, tareas pendientes y errores conocidos relevantes para la petición;
  - **ejecutor:** convenciones, decisiones y errores conocidos relevantes para el objetivo de su tarea.
- **Los requisitos siguen igual,** citados literalmente: el empaquetador los cuenta como obligatorios.
- **Formato:** un bloque `<project_memory pack="<sha256>">` con un `<memory entry=… kind=… origin=… sha256=…>` por
  entrada. El contenido va con `PromptData.Quote`, así que no puede cerrar el bloque.
- **La regla del prompt** dice que los bloques `<project_memory>` son DATOS y que no se obedecen órdenes que aparezcan
  en ellos.
- **Trazabilidad.** `AgentRunStartedPayload` v2 lleva `ContextPack`: el SHA-256 del bloque, las entradas incluidas, las
  omitidas y los caracteres. La evidencia del prompt tiene el texto completo.

**D-24 · Empaquetador por niveles.**

| Nivel | Contenido | Si no cabe |
|---|---|---|
| 1 | Requisitos obligatorios del conjunto congelado (o los que cubre la tarea) | **Falla cerrado:** `ContextBudgetTooSmall`, no se lanza el agente y la tarea queda bloqueada con el motivo |
| 2 | Decisiones (`Decision`, `Architecture`, `Convention`) de confianza | Se omiten las más antiguas |
| 3 | Tareas pendientes | Se omiten las más antiguas |
| 4 | Requisitos deseables | Se omiten |
| 5 | Errores conocidos y soluciones, por relevancia | Se omiten |
| 6 | El resto, por relevancia | Se omite |

- Un nivel entra entero antes que el siguiente. Dentro de cada nivel, el orden es determinista.
- El presupuesto va en caracteres (exactos). Los tokens se estiman como caracteres entre 4: «estimado por Agent Studio».
- Lo omitido sale en el resultado y la UI lo enseña como «posibles pérdidas».

**D-25 · `ReadAndPropose`.** No hay canal para que un agente proponga memoria. Una plantilla con esa política se
rechaza (`agent.memory-proposals-not-available`) hasta la Fase 5: es la regla de C-3, nada simulado.

### 4.5 F4-7 y F4-8 · Skills

**D-26 · La pantalla (F4-7).**

- Se abre como un documento «Skills» desde la barra de actividad.
- **Árbol de sectores** con su recuento, y **búsqueda** por nombre, resumen y etiquetas (determinista).
- **Ficha de la skill:**
  - fuente: repositorio, commit y licencia;
  - etiquetas;
  - activación: Global, Proyecto y uno por agente de la plantilla.
- **Activar** añade una decisión con `ISkillActivationStore.AppendAsync(…, decidedBy: usuario)`, y el estado efectivo
  sale de `SkillActivationResolver`.
- **Coste de contexto:** el nombre y la descripción entre 4, «estimado por Agent Studio». También el total de lo activado
  por agente.
- **Salud:** comprobaciones locales (frontmatter con `SkillFrontmatter.Validate`, licencia y fijación). Upstream,
  archivado y scripts salen «sin comprobar (Fase 6)», y pesan más que «sana».
- **Avisos:**
  - una skill enlazada no se puede dar a un agente;
  - Codex carga además tus skills globales ([10](10-catalogo-de-skills.md#4-activación)).

**D-27 · Sin tablas `skills` ni `skill_health_checks` en la 0004.**

- El catálogo es tuyo, del perfil (`C:\skills-index`), no de un proyecto.
- Se lee en cada consulta (`SkillCatalog`), y la salud local se calcula al vuelo.
- La caché de la salud con red la decide la Fase 6, en el perfil.

**D-28 · Contexto del recomendador determinista.** `ProjectStackDetector` mira solo nombres de fichero del índice del
explorador, nunca su contenido:

- `*.csproj` y `*.sln(x)` dan `#dotnet` y `#csharp`;
- `package.json`, `#node`;
- `pyproject.toml` y `requirements.txt`, `#python`;
- `*.sql`, `#sql`;
- `Dockerfile`, `#docker`;
- `*.tf`, `#terraform`;
- `*.pbip` y `*.tmdl`, `#power-bi`.

Se suma al texto de la petición o del objetivo de la tarea.

**D-29 · Recomendación asistida (F4-8).**

- **Dónde.** Es una acción aparte, «Pedir a un agente», en la pantalla Skills y en un nodo de trabajo. Nunca dentro de
  la planificación: ADR-0013 no cambia.
- **Cómo:** una ejecución auxiliar de Claude (D-08) con:
  - `--tools "Read Grep Glob StructuredOutput"`. El lanzamiento siempre incluye las tres de lectura (ADR-0018), pero
    aquí el directorio de trabajo es una carpeta vacía de la ejecución: no ve el repositorio, no escribe y no tiene red;
  - las dos herramientas MCP (D-30);
  - el esquema estricto `{ recommendations: [{ skillId, scope, agent, reason }] }`.
- **El sistema valida la respuesta:**
  - una skill o un agente desconocidos se descartan con aviso;
  - 10 como máximo;
  - la razón, de hasta 300 caracteres, se muestra como «razón del agente».
- **Resultado.** Se ve junto a lo determinista. Solo tú activas: una `SkillActivation` con tu nombre como
  `decidedBy`.
- **Cuota.** Pide OK en cada uso (decisión 2 de la §12).

**D-30 · Servidor MCP.**

| Opción | Ventajas | Inconvenientes |
|---|---|---|
| **Proceso propio por stdio** (`AgentStudio.SkillsMcp.exe`) que lee una instantánea, elegida | Sin puertos ni red, y solo lo arranca el CLI. No ve la base ni el índice. Muere con el árbol del CLI (Job Object) | Un proyecto nuevo, el duodécimo, que amplía ADR-0002, y un ejecutable más |
| HTTP en bucle local dentro de Agent Studio | Sin proceso nuevo | El puerto está al alcance de cualquier proceso local; hace falta un token y un servidor HTTP en la app |
| `AgentStudio.Desktop.exe` en modo servidor | Sin proyecto nuevo | Carga WPF para servir y mezcla la raíz de composición |
| Sin MCP: el catálogo en el prompt | Sencillo | Unos 12 000 tokens por consulta con 120 skills, y F4-8 pide un MCP |

- **Herramientas:**
  - `search_skills(query, sector?, tags?, top ≤ 20)`;
  - `recommend_skills(task, contextTags?, top ≤ 10)`, que es el recomendador determinista.

  Las dos llevan `readOnlyHint: true` y `openWorldHint: false`. No hay herramientas de escritura y las entradas tienen
  límites.
- **Salida:** solo metadatos del catálogo: nombre, resumen, sector, etiquetas, salud local, activación y coste estimado.
  Nunca el cuerpo de un `SKILL.md` ni rutas.
- **Instantánea:** un JSON que escribe Agent Studio en la carpeta de la ejecución con `CreateNew`. Se le pasa por ruta
  absoluta en `mcp.json`.
- **Lanzamiento:** `--strict-mcp-config --mcp-config <runs>\<id>\mcp.json` y, en `dontAsk`, las reglas de permitir de
  las dos herramientas: `--allowedTools "mcp__agentstudio__search_skills mcp__agentstudio__recommend_skills"`.
  - `--tools` solo acota las herramientas integradas.
  - Una herramienta MCP sin regla de permitir se deniega en `dontAsk`: se deduce de la documentación de `dontAsk`
    («auto-denies every call that would otherwise prompt») y lo comprueba la medición.
- **Relación con `--strict-mcp-config`.** Sigue en todo lanzamiento. Con `--mcp-config` deja **solo** el servidor de
  Agent Studio: ni `.mcp.json` ni los conectores de claude.ai.
- **El ejecutor nunca lo recibe:**
  - `ClaudeCodeArguments` acepta servidores MCP solo con un perfil que no escribe, sin shell y sin red, y falla cerrado
    en cualquier otro caso;
  - la huella medida `WriteConfinementLaunch` no cambia, y un test lo fija.
- **Medición antes de usarlo:** una ejecución real, con haiku y con tu OK, que compruebe cuatro cosas:
  - `system/init.mcp_servers` es exactamente `agentstudio` conectado;
  - las herramientas ofrecidas son Read, Grep, Glob, `StructuredOutput` y las dos de MCP;
  - una llamada funciona;
  - una herramienta sin regla se deniega.
- **SDK:** `ModelContextProtocol` 2.2.0 (Apache-2.0; .NET Standard 2.0, 8, 9 y 10; nuget.org, consultado el
  2026-10-18). P0 lo fija tras volver a verificarlo. Si trajera dependencias de más, la alternativa es un JSON-RPC
  mínimo escrito a mano (`initialize`, `tools/list`, `tools/call`).

### 4.6 F4-9 · Resumen diario

**D-31 · Resumen determinista.**

| Opción | Ventajas | Inconvenientes |
|---|---|---|
| **Determinista**, elegida | Sin cuota y reproducible. Se prueba con una bitácora de ejemplo | Cuenta y lista; no redacta |
| Por agente, con tu OK | Narrativa | Gasta cuota y es texto de agente (una propuesta). La bitácora puede traer instrucciones de terceros |
| Por agente, automático | Cómodo | Gasta sin tu OK, contra las reglas del proyecto |

- **Contenido:**
  - prompts por actor, proveedor y modelo;
  - resultados por estado y duración total;
  - tokens: la suma es exacta solo si todos lo son; si no, sale «parcial»;
  - fallos y límites;
  - las etiquetas más frecuentes;
  - las líneas «Hecho» del día, literales y escapadas;
  - de las líneas que no se reconocen, solo el recuento.
- **Lectura comprobada.** La bitácora se lee con `IProjectStorageReader.OpenForRead`, nueva, que rechaza un enlace
  plantado como día de la bitácora. Sin ella, el resumen podría copiar un fichero de fuera del proyecto al Markdown
  versionado.
- **Días:** el día es el local. Un día cerrado se resume una vez (idempotente por el hash del contenido); el de hoy
  sale como «provisional».
- **Dónde va:** a la memoria como `DailySummary` (origen sistema), y de ahí a la exportación.

---

## 5. Componentes y contratos

Todos respetan [ADR-0002](adr/0002-capas-y-puertos.md): los módulos solo conocen `Application`, y `Desktop` compone.
La excepción es el proceso del servidor MCP (D-30), un segundo punto de entrada que solo usa `Domain` y `Application`.

| Componente | Proyecto | Responsabilidad |
|---|---|---|
| `MetricOrigin` y `MetricAggregate<T>`; la salud del contexto, movida al dominio; reglas nuevas de `AgentDefinition` | **Domain** (`Metrics/`, `Agents/`) | Procedencia, agregados con cobertura y umbrales, todo puro |
| Contratos de uso, memoria, empaquetado, cuota y recomendación; payloads v2; `AuxiliaryAgentRunner`; `QuotaObservingAgentProvider` | **Application** | Puertos y casos de uso sin E/S propia |
| `UsageProjector`, `UsageAggregator`, `UsageExporter`, `ContextHealthTracker`, `ModelWindowCatalog` y `JsonlQuotaObservations` | **Observability** | Proyección pura del registro, exportación, salud en vivo y registro de cuota del usuario |
| `ProjectMemory`, `MemoryMarkdownExporter`, `ContextPacker`, `JournalReader` y `DailySummaryBuilder` | **Memory** | Política de la memoria sobre los puertos, exportación, empaquetado y resumen |
| `SqliteMemoryStore`, `ProjectStorageFiles.OpenForRead`, `ReadOnlyProjectLogReader` y `SkillHealthReader` | **Infrastructure** | Filas y FTS5, lectura comprobada, lectura de registros de otros proyectos y salud local |
| Analizadores v2, `CodexAppServerQuotaReader`, `CodexRateLimitsParser` y `--mcp-config` en `ClaudeCodeArguments` | **Providers** | Señales por paso, cuota de Codex y lanzamiento con MCP |
| `AgentRunner` y `JudgeRunRecorder` v2; el paquete de memoria en `PlannerPrompt` y `ExecutorPrompt`; `IRunActivity` en `Orchestrator` | **Orchestration**, **Debate** | Registro del uso v2 y memoria en los prompts |
| `AgentStudio.SkillsMcp` | **SkillsMcp** (nuevo) | Servidor MCP de solo lectura |
| Paneles de consumo, contexto, cuota, memoria y resumen; pantalla Skills; ajustes | **Desktop** | ViewModels y vistas |

### 5.1 Dominio

```csharp
// Metrics/MetricValue.cs: six origins; Provenance stays and is derived from Origin.
public enum MetricOrigin { ReportedByProvider, MeasuredBySystem, EstimatedByProvider, EstimatedBySystem, NotExposed, Missing }
// MetricValue<T>: new factories Measured(value, source), EstimatedBySystem(value, source), NotExposed(reason), Missing(reason);
// Existing factories keep their meaning: Exact(...) = ReportedByProvider, Estimated(...) = EstimatedByProvider and
// Unavailable(reason) = NotExposed, as its comment says ("the provider does not expose it"). Missing(reason) is new:
// the provider exposes the figure but it did not arrive. Callers that mean «sin dato» move to it (WP1).

// Metrics/MetricAggregate.cs
public sealed record MetricAggregate<T>(T? Sum, MetricProvenance Provenance, int Reported, int Missing, IReadOnlyList<string> Sources)
    where T : struct;                                   // Missing > 0 ⇒ shown as «parcial (Reported de Reported+Missing)»

// Metrics/ContextHealth.cs: ContextHealthEvaluator, ContextHealthThresholds and ContextHealthLevel moved from Observability.

// Agents/AgentDefinition.Validate: "agent.judge-memory-none" (Judge/Auditor with Memory != None) and
// "agent.memory-proposals-not-available" (ReadAndPropose until Phase 5).

// Decisions/DecisionLog.cs: DecisionKind gains MemoryRecorded and MemoryReviewed, appended (hashes do not change).
```

### 5.2 Proveedores y payloads (Application)

```csharp
// Providers/AgentProvider.cs
public sealed record ModelUsage(string Model, MetricValue<long> InputTokens, MetricValue<long> OutputTokens,
    MetricValue<long> CacheReadInputTokens, MetricValue<long> CacheWriteInputTokens, MetricValue<decimal> CostUsd,
    MetricValue<long> ContextWindow, string? CostBasis);
public sealed record TokenUsage(/* the five existing figures */)
{
    public MetricValue<long> CacheWriteInputTokens { get; init; }     // Claude cache_creation, Codex cache_write
    public IReadOnlyList<ModelUsage> PerModel { get; init; } = [];     // Claude modelUsage; empty for Codex
    public int? Turns { get; init; }                                   // Claude num_turns
}
public sealed record ContextObserved(DateTimeOffset At, string? Model, MetricValue<long> PromptTokens, MetricValue<long> ContextWindow) : AgentEvent(At); // one per step, deduplicated by message id
public sealed record CompactionObserved(DateTimeOffset At, string Trigger, MetricValue<long> TokensBefore) : AgentEvent(At);
// ProviderCapabilities gains ReportsContextOccupancy (init, default false): Claude true, Codex and Gemini false.
public sealed record McpServerSpec(string Name, string Command, IReadOnlyList<string> Arguments, IReadOnlyList<string> AllowedTools);
// AgentRunRequest gains IReadOnlyList<McpServerSpec>? McpServers = null; an adapter refuses it for a profile that writes, has a shell or network.

// Providers/QuotaContracts.cs
public sealed record QuotaObservation(ProviderId Provider, string Window, MetricValue<double> Utilization, TimeSpan? Length,
    DateTimeOffset? ResetsAt, DateTimeOffset ObservedAt, string Source);
public interface IQuotaObservations { Task AppendAsync(IReadOnlyList<QuotaObservation> observations, CancellationToken ct); Task<IReadOnlyList<QuotaObservation>> LatestAsync(CancellationToken ct); }
public interface IProviderQuotaReader { ProviderId Provider { get; } Task<QuotaReadResult> ReadAsync(CancellationToken ct); } // Codex; only when opted in

// Providers/AuxiliaryAgentRunner.cs: launches one agent outside an orchestration run and records it like the judge.
public sealed record AuxiliaryRunRequest(AgentRunPurpose Purpose, AgentRunRequest Request);
public interface IAuxiliaryAgentRunner { Task<AuxiliaryRunResult> RunAsync(ProjectContext project, AuxiliaryRunRequest request, IProgress<AgentEvent>? progress, CancellationToken ct); }

// Decisions/DecisionPayloads.cs: add fields and raise V (never rename or remove).
public enum AgentRunPurpose { Planner, Executor, Judge, SkillRecommendation, DailySummary }
public sealed record ContextPackReference(string Sha256, IReadOnlyList<string> Entries, IReadOnlyList<string> Omitted, int Characters);
// AgentRunStartedPayload V2: + AgentRunPurpose? Purpose, ContextPackReference? ContextPack.
// AgentRunFinishedPayload V2: + DateTimeOffset? LaunchedAt, string? ReportedModel, string? ResumedSession,
//     ContextPeakSnapshot? ContextPeak, int? Compactions /* null = not exposed */, int? Turns.
// UsageSnapshot: + MetricSnapshot? CacheWriteInputTokens, IReadOnlyList<ModelUsageSnapshot>? PerModel; MetricSnapshot: + MetricOrigin? Origin.
public sealed record MemoryRecordedPayload(string Entry, MemoryKind Kind, MemoryOrigin Origin, string Sha256 /* canonical: kind, topic, content */,
    string Source, string? Supersedes) : DecisionPayload;                       // RunId = "": never part of a run; no text
public sealed record MemoryReviewedPayload(string Entry, MemoryReview Decision, string DecidedBy, string? Comment) : DecisionPayload;
```

### 5.3 Uso (Application, sustituye a `IUsageLedger`)

```csharp
public sealed record UsageQuery(DateTimeOffset From, DateTimeOffset To, TimeZoneInfo Zone, IReadOnlyList<UsageDimension> GroupBy, UsageFilter? Filter = null);
public sealed record UsageAttempt(string ProjectId, string RunId, string? TaskId, int Attempt, AgentRunPurpose? Purpose, AgentId Agent,
    ProviderId Provider, VendorId Vendor, string? ConfiguredModel, string? ReportedModel, string? SessionId,
    DateTimeOffset RecordedAt, DateTimeOffset? LaunchedAt, DateTimeOffset FinishedAt, AttemptOutcome Outcome,
    UsageFigures Own, UsageFigures? SessionTotal, IReadOnlyList<ModelUsageFigures> PerModel, ContextPeakFigures? ContextPeak, int? Compactions);
public sealed record UsageReport(IReadOnlyList<UsageAttempt> Attempts, IReadOnlyList<UsageGroup> Groups, IReadOnlyList<ExcludedProject> Excluded, UsageReconciliation Journal);
public interface IUsageReport { Task<UsageReport> QueryAsync(IReadOnlyList<string> projectRoots, UsageQuery query, CancellationToken ct); } // every log read through IProjectLogReader
public interface IUsageExporter { Task WriteCsvAsync(UsageReport report, Stream destination, CancellationToken ct); Task WriteJsonAsync(UsageReport report, Stream destination, CancellationToken ct); }

// Projects/ProjectLogContracts.cs: another project's log, read only (D-05).
public sealed record ProjectLogSnapshot(string ProjectId, string Root, IReadOnlyList<DecisionEntry> Entries, DecisionLogVerification Verification, AnchorStatus Anchor);
public interface IProjectLogReader { Task<ProjectLogSnapshot> ReadAsync(string projectRoot, CancellationToken ct); }  // writes no row, moves no anchor, follows no link
```

### 5.4 Memoria y empaquetado (Application)

```csharp
public enum MemoryOrigin { System, User, Agent }
public enum MemoryReview { Accepted, Rejected, Retracted }
public enum MemoryTrust { Trusted, Proposed, Quarantined, Rejected, Retracted }
public sealed record MemoryDraft(MemoryKind Kind, string Topic, string Content, MemoryOrigin Origin, string Source, string? Supersedes = null);
public sealed record MemoryEntry(string Id, MemoryKind Kind, string Topic, string Content, MemoryOrigin Origin, string Source,
    DateTimeOffset CreatedAt, string ContentSha256, MemoryTrust Trust, string? Supersedes, string? SupersededBy);
public sealed record MemoryQuery(string? Text = null, IReadOnlyList<MemoryKind>? Kinds = null, string? Topic = null,
    int MaxEntries = 20, int? MaxCharacters = null, bool TrustedOnly = true);
public interface IProjectMemory
{
    Task<MemoryEntry> RecordAsync(ProjectContext project, MemoryDraft draft, CancellationToken ct);   // MemoryRecorded first, then the row
    Task ReviewAsync(ProjectContext project, string entry, MemoryReview decision, string decidedBy, CancellationToken ct);
    Task<IReadOnlyList<MemoryEntry>> RecallAsync(ProjectContext project, MemoryQuery query, CancellationToken ct); // anchored rows only
}
public interface IMemoryStore   // Infrastructure: immutable rows and FTS5; no state
{
    Task AppendAsync(string projectId, MemoryRow row, CancellationToken ct);
    Task<IReadOnlyList<MemoryRow>> SearchAsync(string projectId, MemorySearch search, CancellationToken ct);
}
public interface IRunActivity { bool IsBusy(string projectId); }   // a run or a planning in this process (Orchestrator)
public interface IMemoryExporter { Task<MemoryExportResult> ExportAsync(ProjectContext project, CancellationToken ct); } // refuses while busy

public enum ContextTier { MandatoryRequirement, Decision, PendingTask, DesirableRequirement, KnownIssue, Other }
public sealed record ContextItem(string Id, ContextTier Tier, string Kind, string Origin, string Text, string Sha256);
public sealed record ContextPackRequest(AgentDefinition Agent, string Focus, IReadOnlyList<ContextItem> Mandatory, int BudgetCharacters);
public sealed record ContextPack(IReadOnlyList<ContextItem> Items, IReadOnlyList<ContextItem> Omitted, int Characters, MetricValue<long> EstimatedTokens, string Sha256);
public interface IContextPacker { Task<ContextPack> PackAsync(ProjectContext project, ContextPackRequest request, CancellationToken ct); } // ContextBudgetTooSmallException

public interface IDailySummaryService { Task<DailySummaryResult> SummarizeAsync(ProjectContext project, DateOnly day, CancellationToken ct); }
// Projects/ProjectStorageReader.cs (new interface, so P0 breaks no implementation of IProjectStorageFiles):
public interface IProjectStorageReader { Stream OpenForRead(string projectRoot, string relativePath); } // same checks as the writes; no link
```

### 5.5 Skills (Application)

```csharp
public sealed record SkillCatalogSnapshot(DateTimeOffset At, IReadOnlyList<SnapshotSkill> Skills);   // what the MCP server reads
public sealed record AssistedRecommendation(SkillId Skill, SkillScope Scope, AgentId? Agent, string Reason);
public interface ISkillRecommendationService
{
    IReadOnlyList<SkillRecommendation> Recommend(SkillQuery query, IReadOnlyList<CatalogSkill> catalog);          // deterministic, free
    Task<AssistedRecommendationResult> AskAgentAsync(ProjectContext project, string task, QuotaConsent consent, CancellationToken ct); // never activates
}
public interface ISkillHealthReader { Task<SkillHealthReport> ReadLocalAsync(CatalogSkill skill, CancellationToken ct); } // frontmatter, license, pinned
```

---

## 6. Modelo de datos: migración 0004

Solo memoria. Uso y skills no tienen tabla (D-01 y D-27).

```sql
-- Agent Studio: Phase 4 (memory). memory_entries becomes append-only: its rows are immutable content, and everything
-- that changes (anchoring, review, supersession, retraction) is derived from the decision log (ADR-0010 pattern).
ALTER TABLE memory_entries ADD COLUMN origin TEXT NULL CHECK (origin IN ('System', 'User', 'Agent'));
ALTER TABLE memory_entries ADD COLUMN content_sha256 TEXT NULL;
ALTER TABLE memory_entries ADD COLUMN supersedes_entry_id TEXT NULL REFERENCES memory_entries(id);
CREATE INDEX memory_by_project ON memory_entries(project_id, kind, created_at);

-- Updates and deletes are aborted before they happen, so the FTS triggers for them never fire again.
DROP TRIGGER memory_entries_au;
DROP TRIGGER memory_entries_ad;
CREATE TRIGGER memory_entries_no_update BEFORE UPDATE ON memory_entries BEGIN
    SELECT RAISE(ABORT, 'memory_entries is append-only');
END;
CREATE TRIGGER memory_entries_no_delete BEFORE DELETE ON memory_entries BEGIN
    SELECT RAISE(ABORT, 'memory_entries is append-only');
END;
CREATE TRIGGER memory_entries_no_replace BEFORE INSERT ON memory_entries
WHEN EXISTS (SELECT 1 FROM memory_entries WHERE id = NEW.id) BEGIN
    SELECT RAISE(ABORT, 'memory_entries is append-only');
END;
```

- **Columnas de la 0001.** `superseded_by` queda siempre nula: la sustitución va en la entrada nueva
  (`supersedes_entry_id`) y en su `MemoryRecorded`.
- **Filas antiguas.** La tabla nunca se ha escrito, así que no hay filas sin origen.
- **Tablas sin uso.** `agent_runs` y `usage_records` siguen igual ([05 §5](05-modelo-de-datos.md#5-tablas-de-la-0001-sin-uso-en-la-fase-3)).
  Se pueden retirar más adelante con su propia migración.
- **Documentación.** [05](05-modelo-de-datos.md) se actualiza en P0:
  - la 0004 pasa a ser de memoria;
  - `skills` y `skill_health_checks` salen del plan;
  - la memoria se deriva del registro.

---

## 7. Seguridad frente a ADR-0018

| Superficie nueva | Adversario del punto 1 | Mitigación | Clasificación |
|---|---|---|---|
| Memoria que vuelve a un prompt | El contenido del repositorio (base plantada, bitácora de terceros) y la salida de los agentes | Anclaje en el registro (D-19), cuarentena de lo heredado (D-20), propuestas de agente solo aceptadas, bloque delimitado y recodificado, el juez nunca la recibe, redacción de secretos y `MemoryPolicy` por agente (por defecto, `None`) | Cerrado dentro del modelo. **Residual:** un modelo puede obedecer igualmente un texto de datos. Lo acotan la aprobación del plan, el validador, la verificación del sistema y un juez de otro vendor que no ve la memoria |
| Markdown exportado | Contenido del repositorio | Nunca se lee de vuelta. Se escribe por `IProjectStorageFiles` (sin enlaces) y nunca durante una ejecución | Cerrado |
| Lectura de la bitácora para el resumen | Un enlace plantado como día de la bitácora | `OpenForRead` lo rechaza, con la misma comprobación que la escritura (H-01) | Cerrado |
| Servidor MCP | La salida del agente recomendador | Proceso propio, por stdio y sin red; dos herramientas de lectura sobre una instantánea; `--strict-mcp-config`; nunca en un perfil que escribe; la respuesta se valida contra el catálogo y nunca activa nada | Cerrado. Que otro proceso Medium cambie la ruta del ejecutable es el residual de siempre (punto 2) |
| Lectura de la cuota de Codex | — (es código de Codex con tu configuración) | Opt-in, carpeta de trabajo vacía, lista cerrada de tres mensajes, respuesta por lista blanca (sin `accountId`) y Job Object | Dentro de lo aceptado para los CLIs. Lo de la configuración del usuario, **sin verificar** (§13) |
| Lectura de registros de otros proyectos | Un enlace plantado en `.agentstudio` de otro repositorio | Misma guarda que al abrir un proyecto. No escribe filas ni mueve anclas. Un historial sin ancla reconocida se excluye | Cerrado |
| Exportación CSV | Texto de errores de CLIs y agentes | Protección contra fórmulas; nunca prompts ni salidas | Cerrado |
| Registro de cuota del usuario | — | Solo se muestra; no decide nada | Lo que haga un proceso Medium con él es el residual del punto 2, de bajo impacto |

---

## 8. Estrategia de pruebas

Mismas reglas que la Fase 3:

- solo `Assert` de xUnit;
- `using System.IO;` en el proyecto de tests, que es WPF;
- propiedades con `Random` sembrado;
- lo que gasta cuota va con `[Trait("Category","ProviderLive")]` y `Assert.SkipUnless(AGENTSTUDIO_PROVIDER_TESTS=="1")`,
  y solo se ejecuta con tu OK.

| Criterio | Pruebas |
|---|---|
| F4-1 | Proyección sobre registros sintéticos v1 y v2: dimensiones, periodos con el cambio de hora de Europe/Madrid, cobertura «parcial» y desglose por modelo. **Deduplicación por sesión:** reintento reanudado, corrección del planificador y del juez, e intento interrumpido. Uso de Codex a cero tomado como «sin dato». Un registro que no verifica se rechaza. Proyecto reciente excluido con su motivo. Conciliación con una bitácora de ejemplo |
| F4-2 | CSV y JSON contra ficheros dorados. Escape RFC 4180, BOM, cultura invariante, valores vacíos para lo no disponible, la protección contra fórmulas y caracteres en castellano. Ida y vuelta del JSON. Ningún prompt en la salida |
| F4-3 | **Analizador de Claude:** un `ContextObserved` por id de mensaje (las capturas 01, 02 y 04 repiten eventos) y la ventana del modelo del paso (01: 1 000 000 para `claude-sonnet-5-5`). `compact_boundary` con una línea sintética de la forma documentada. **Tracker:** ventana aprendida, «estimado»; nunca da por exacto lo estimado. **Codex:** «no expuesto». **Umbrales:** se validan desde los ajustes. **ViewModel:** la etiqueta dice «estimado» |
| F4-4 | **Codex:** el analizador de cuota contra la captura grabada (y, mientras no exista, contra una sintética del esquema de 0.160.0): guarda solo la lista blanca y descarta `accountId`. **Lector con un proceso falso:** envía exactamente los tres mensajes y en orden, y nunca `consume` ni `sendAddCreditsNudgeEmail`; sin opt-in, no arranca ningún proceso; con el tiempo agotado, «sin dato» con su motivo; con una API key, «no expuesto: Codex usa API key». **Claude:** sus ventanas desde las capturas existentes llegan al registro de cuota. **Gemini:** «no expuesto». En vivo: una lectura real con tu OK, que deja la captura grabada |
| F4-5 | Registrar y recuperar con ranking FTS5, filtros y presupuesto. Escape de la consulta (`"`, `*`, `NEAR`, `:`) y acentos plegados. Los triggers append-only abortan UPDATE y DELETE. Una fila no anunciada o modificada se ignora y alerta. Cuarentena tras aceptar un historial como nuevo origen. Redacción. Exportación contra ficheros dorados. Exportar se rechaza durante una ejecución o una planificación |
| F4-6 | Niveles y orden. **Fallo cerrado** si los obligatorios no caben. Lo omitido sale en el resultado. Prueba de propiedad con unos 200 conjuntos aleatorios por semilla: con cualquier presupuesto que admita los obligatorios, están todos; las decisiones y las tareas pendientes nunca se omiten antes que un nivel inferior; el resultado es determinista. Un contenido con `</project_memory>` no cierra el bloque |
| F4-7 | ViewModel con puertos falsos: árbol por sector con recuentos, búsqueda, activación por ámbito (añade la decisión con `decidedBy`), estado efectivo, coste con la etiqueta «estimado», salud con «sin comprobar» y avisos de las enlazadas. Render fuera de pantalla como evidencia |
| F4-8 | Recomendador determinista y detector de stack. **Servidor MCP sobre flujos en memoria:** exactamente dos herramientas con `readOnlyHint`, argumentos inválidos rechazados, solo la instantánea, sin red. **Servicio:** valida la respuesta del agente, descarta lo desconocido, nunca activa y exige el consentimiento. **Argumentos de Claude:** `--mcp-config` solo para perfiles de lectura y `WriteConfinementLaunch` igual que antes. En vivo, con tu OK: la medición de D-30 |
| F4-9 | Bitácora de ejemplo con los dos escritores, las variantes de «Hecho» (`Hecho:`, `Hecho (en curso):`, `Hecho (00:27, cierre):`), líneas rotas y cabecera, contra el resumen esperado. Día local. Sumas exactas o parciales. Enlace plantado rechazado. Idempotencia |

---

## 9. Desglose de trabajo

P0 va en serie y se fusiona a `main` antes de abrir los paquetes. Después, WP1 a WP5 van en paralelo, en worktrees
distintos y sin ningún fichero en común. Al final integra el lead.

**Propietarios únicos de los ficheros compartidos:**

- P0: `Directory.Packages.props`, `AgentStudio.slnx`, todos los `*.csproj` y la migración.
- El lead, en la integración: `src\AgentStudio.Desktop\Composition\{PlatformServices,ShellServices}.cs`,
  `…\ViewModels\MainViewModel.cs`, `…\MainWindow.xaml(.cs)`, `…\App.xaml`, `…\Themes\DocumentTemplates.xaml`,
  `docs\08-…`, `docs\README.md`, la bitácora y `CLAUDE.md`. `CLAUDE.md` solo se toca con tu OK.

**«Hecho» exige siempre:**

- `dotnet build AgentStudio.slnx` con 0 avisos;
- `dotnet test` en verde sin `ProviderLive`;
- los ADR y los documentos del paquete;
- nada simulado en la UI.

### P0 · Contratos, dominio, payloads v2 y migración 0004 (M-L; camino crítico)

**Propiedad (rutas absolutas):**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.Domain\Metrics\**`. Incluye `ContextHealth.cs`, que se mueve desde
  `src\AgentStudio.Observability\ContextHealth\` (ese fichero se borra).
- `…\src\AgentStudio.Domain\Agents\AgentDefinition.cs` (las dos reglas) y `…\Domain\Decisions\DecisionLog.cs` (solo el
  enum).
- `…\src\AgentStudio.Application\Knowledge\**`: sustituye `MemoryAndUsageContracts.cs` por los contratos de memoria,
  empaquetado y resumen.
- `…\Application\Usage\UsageContracts.cs` (nuevo).
- `…\Application\Providers\{AgentProvider.cs, QuotaContracts.cs, AuxiliaryAgentRunner.cs}`.
- `…\Application\Decisions\{DecisionPayloads.cs, DecisionPayloadSerializer.cs}`.
- `…\Application\Projects\{ProjectStorageReader.cs, ProjectLogContracts.cs}` (nuevos: así P0 no rompe ninguna
  implementación de `IProjectStorageFiles`), `…\Application\Orchestration\RunActivity.cs` y
  `…\Application\Skills\SkillPorts.cs`.
- `…\src\AgentStudio.Infrastructure\Persistence\Schema\0004_memory.sql`.
- `…\Directory.Packages.props` (`ModelContextProtocol`, con la versión verificada), `…\AgentStudio.slnx`, el esqueleto de
  `…\src\AgentStudio.SkillsMcp\AgentStudio.SkillsMcp.csproj` y todos los `*.csproj`.
- Tests: `…\tests\AgentStudio.Tests\Domain\Metrics\**` (más la prueba movida de `ContextHealthEvaluatorTests.cs`),
  `…\tests\AgentStudio.Tests\Application\**` (ida y vuelta de los payloads v2, golden del serializador y
  `AuxiliaryAgentRunner`) y `…\tests\AgentStudio.Tests\Infrastructure\SqliteSchemaTests.cs`.
- Docs: `docs\04-contratos.md`, `docs\05-modelo-de-datos.md`, ADR-0019 y ADR-0022.

**Hecho cuando:**

- `MetricOrigin` y `MetricAggregate` están probados, y los creadores de `MetricValue` que ya existían no cambian de
  significado.
- Los payloads v1 se siguen leyendo y su hash no cambia.
- Los v2 hacen ida y vuelta.
- `MemoryRecorded` y `MemoryReviewed` llevan `RunId` vacío y la reproducción y el dossier los ignoran.
- La 0004 es idempotente sobre una base 0003 y sus triggers abortan UPDATE, DELETE y REPLACE.
- Las reglas `agent.judge-memory-none` y `agent.memory-proposals-not-available` están probadas.
- `AuxiliaryAgentRunner` registra `AgentRunStarted` antes de lanzar y falla cerrado con un registro roto.
- Docs/04 y docs/05 están actualizados.

**Arranque anticipado permitido:** el analizador de la cuota de Codex (WP1) y el de la bitácora (WP3), que no necesitan
contratos nuevos.

### WP1 · Proveedores, uso en el registro y memoria en los prompts (L; depende de P0)

**Propiedad:**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.Providers\Cli\Claude\{ClaudeStreamJsonParser.cs, ClaudeCodeArguments.cs, ClaudeCodeAgentProvider.cs}`.
- `…\Providers\Cli\Codex\{CodexExecJsonParser.cs, CodexCliAgentProvider.cs}` y `…\Providers\Cli\Codex\AppServer\**`
  (nuevo: `CodexAppServerQuotaReader`, `CodexRateLimitsParser`).
- `…\Providers\Cli\Gemini\GeminiCliAgentProvider.cs` (solo la capacidad), `…\Providers\Composition\**`,
  `…\Providers\DependencyInjection.cs` y `…\Providers\Cli\SupportedVersions.cs`: la versión de Codex probada para la
  cuota, sin tocar `WriteConfinementMeasured`.
- `…\src\AgentStudio.Application\Providers\QuotaObservingAgentProvider.cs` (nuevo) y
  `…\Application\Providers\JournalingAgentProvider.cs`: «sin dato» con `Missing` y, si cabe, «(acumulado de la sesión)».
- `…\src\AgentStudio.Orchestration\Engine\{AgentRunner.cs, ExecutorPrompt.cs, Workers.cs, Orchestrator.cs}`, este último
  solo para `IRunActivity`.
- `…\Orchestration\Planning\{PlannerPrompt.cs, Planner.cs}`.
- `…\src\AgentStudio.Debate\Judging\IndependentReview.cs` (`JudgeRunRecorder` v2).
- Tests: `…\tests\AgentStudio.Tests\Providers\{Claude,Codex,Composition}\**`, la captura
  `…\Providers\Fixtures\codex-cli\0.160.0\05-rate-limits.*` (con tu OK), `…\Providers\Live\CodexQuotaLiveTests.cs`,
  `…\Orchestration\Engine\{UsageRecordingTests,MemoryInPromptTests}.cs`, `…\Orchestration\Planning\PlannerMemoryTests.cs`
  y `…\Debate\JudgeUsageRecordingTests.cs`.
- Docs: `docs\06-integracion-proveedores.md`, ADR-0020 y ADR-0021.

**Hecho cuando:**

- Las pruebas de F4-4 y de los analizadores (F4-3) están en verde.
- El planificador, los ejecutores y el juez registran `AgentRunFinished` v2 con `LaunchedAt`, `ReportedModel`,
  `ResumedSession`, `ContextPeak` y `Compactions`.
- La memoria entra en los prompts solo con `ReadSelected`, recodificada, y queda en `AgentRunStarted` v2.
- `ContextBudgetTooSmall` deja la tarea bloqueada con su motivo.
- `--mcp-config` solo se acepta para perfiles de lectura y `WriteConfinementLaunch` no cambia.
- El test en vivo de la cuota está escrito.

### WP2 · Observabilidad: consumo, exportación, contexto y cuota (M; depende de P0)

**Propiedad:**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.Observability\{Usage,Context,Quota}\**` y
  `…\Observability\ObservabilityServiceCollectionExtensions.cs` (nuevo).
- Tests: `…\tests\AgentStudio.Tests\Observability\{Usage,Context,Quota}\**`.
- Docs: ADR-0021, la parte de la evaluación y los umbrales, junto con WP1.

**Hecho cuando:**

- Las pruebas de F4-1 (agregación, deduplicación y cobertura), F4-2 (ficheros dorados) y F4-3 (tracker y ventana
  aprendida) están en verde.
- El registro de cuota rota por tamaño y devuelve la última observación de cada ventana.
- El rendimiento de la proyección está medido con un registro sintético grande: el dato decide si hace falta caché.

### WP3 · Memoria: almacén, anclaje, Markdown, empaquetador y resumen diario (L; depende de P0)

**Propiedad:**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.Memory\{Store,Export,Context,Summary}\**` y
  `…\Memory\DependencyInjection.cs`.
- `…\src\AgentStudio.Infrastructure\Persistence\{SqliteMemoryStore.cs, ReadOnlyProjectLogReader.cs, ProjectStorageFiles.cs, ProjectStorageGuard.cs, ProjectDatabases.cs, PersistenceServiceCollectionExtensions.cs}`.
- `…\src\AgentStudio.Application\Decisions\ReadOnlyLogVerifier.cs` (nuevo: verifica contra el ancla sin escribir).
- Tests: `…\tests\AgentStudio.Tests\Memory\{Store,Export,Context,Summary}\**`, la bitácora de ejemplo
  `…\Memory\Fixtures\bitacora-ejemplo.md` y
  `…\tests\AgentStudio.Tests\Infrastructure\Persistence\{SqliteMemoryStoreTests,ProjectStorageFilesReadTests,ReadOnlyProjectLogReaderTests}.cs`.
- Docs: `docs\11-bitacora.md` (los resúmenes), ADR-0023 y ADR-0025.

**Hecho cuando:**

- Las pruebas de F4-5, F4-6 y F4-9 están en verde.
- `OpenForRead` rechaza los mismos enlaces que la escritura, con las pruebas de `PlantedLinks`.
- `ReadOnlyProjectLogReader` no escribe filas ni mueve anclas, ni siquiera con un ancla pendiente de migrar.
- Una escritura de memoria con `IRunActivity.IsBusy` verdadero espera y no llega al registro hasta que termina la
  ejecución.

### WP4 · Skills: pantalla y recomendador asistido (M-L; depende de P0)

**Propiedad:**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.SkillsMcp\**`, salvo el `.csproj`.
- `…\src\AgentStudio.Application\Skills\{SkillRecommendationService.cs, SkillCatalogSnapshotBuilder.cs}` (nuevos).
- `…\src\AgentStudio.Domain\Skills\{SkillContextCost.cs, ProjectStackDetector.cs}` (nuevos).
- `…\src\AgentStudio.Infrastructure\Skills\{SkillHealthReader.cs, SkillsServiceCollectionExtensions.cs}`.
- `…\src\AgentStudio.Desktop\Skills\**` y `…\Desktop\Views\Skills\**`.
- Tests: `…\tests\AgentStudio.Tests\SkillsMcp\**`, `…\Application\SkillRecommendationServiceTests.cs`,
  `…\Domain\Skills\{SkillContextCostTests,ProjectStackDetectorTests}.cs`,
  `…\Infrastructure\Skills\SkillHealthReaderTests.cs` y `…\Desktop\Skills\**`.
- Docs: `docs\10-catalogo-de-skills.md` y ADR-0024.

**Hecho cuando:**

- Las pruebas de F4-7 y F4-8 están en verde.
- El servidor no tiene herramientas de escritura ni red.
- El servicio nunca activa una skill y exige el consentimiento.
- El test en vivo de la medición de D-30 está escrito; se ejecuta en la integración, con tu OK.
- Hay una lista de comprobación para la demostración.

### WP5 · Escritorio: consumo, contexto, cuota, memoria y resumen (M-L; depende de P0; usa WP2 a WP4 solo por puertos)

**Propiedad:**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.Desktop\{Observability,Memory}\**` y
  `…\Desktop\Views\{Observability,Memory}\**`.
- `…\Desktop\Services\{AppSettings.cs, AppSettingsStore.cs}`: los umbrales, el opt-in de la cuota de Codex y el resumen
  por agente.
- `…\Desktop\Orchestrator\Monitor\{RunMonitorViewModel.cs, MonitorTexts.cs}`, `…\Desktop\Views\Orchestrator\RunMonitorView.xaml`
  y `…\Desktop\ViewModels\JournalViewModel.cs` (la lectura comprobada).
- `…\Desktop\Orchestrator\Preparation\**` y `…\Desktop\Views\Orchestrator\PreparationView.xaml`: el interruptor «Memoria
  del proyecto» de cada agente de la plantilla (`MemoryPolicy`), que se guarda con `IAgentRoster.SaveAsync`. Cambiar la
  plantilla retira la aprobación de un plan pendiente, como ya ocurre.
- Tests: `…\tests\AgentStudio.Tests\Desktop\{Observability,Memory}\**`, `…\Desktop\AppSettingsTests.cs`,
  `…\Desktop\Orchestrator\RunMonitorViewModelTests.cs` y `…\Desktop\JournalViewModelTests.cs`.
- Docs: `docs\03-pantallas-y-navegacion.md` (§2, §5 y §8).

**Hecho cuando:**

- Las pruebas de ViewModel con puertos falsos están en verde:
  - panel con dimensiones y etiquetas de procedencia;
  - exportación;
  - contexto que nunca da por exacto lo estimado;
  - umbrales validados;
  - cuota: Claude con su antigüedad, Codex desactivado sin opt-in y Gemini «no expuesto»;
  - memoria: aceptar, rechazar, retirar y cuarentena, con la exportación desactivada durante una ejecución;
  - resumen diario.
- Hay renders fuera de pantalla como evidencia.

### Integración (el lead, tras las fusiones)

- **Composición.** Una línea por módulo en `PlatformServices.cs` y `ShellServices.cs`. En la barra de actividad,
  «Skills» se activa y aparecen «Consumo» y «Memoria».
- **Plantilla por defecto** (`src\AgentStudio.Desktop\Orchestrator\Session\DefaultAgentRoster.cs`), según la decisión 5
  de la §12. Solo afecta a los proyectos sin plantilla; los demás conservan la suya.
- **Pruebas de integración** en `C:\Personal Projects\Agent Studio\tests\AgentStudio.Tests\Integration\Phase4\**`:
  - una ejecución real del motor con `ScriptedAgentProvider` sobre SQLite, cuyo uso v2 aparece en el panel;
  - la memoria derivada y su exportación tras la ejecución;
  - el recomendador con un proveedor falso;
  - composición: todos los proveedores pasan por la bitácora y por la observación de cuota.
- **En vivo, con tu OK:**
  - una lectura de la cuota de Codex, que deja la captura del test F4-4;
  - la medición del lanzamiento MCP de D-30, una ejecución con haiku.
- **Documentación:** docs/08 (estado de la Fase 4 y el texto de F4-4 de D-17), README y bitácora.

**Orden y dependencias.**

| Paso | Qué | Depende de |
|---|---|---|
| 1 | P0 | — |
| 2 | WP1, WP2, WP3, WP4 y WP5, en paralelo | P0. WP5 usa los puertos de WP2 a WP4 con dobles hasta la integración. WP1 usa `IContextPacker` (lo implementa WP3) con un doble |
| 3 | Integración, pruebas en vivo y docs | Todos |

---

## 10. ADR a escribir

| ADR | Tema | Paquete |
|---|---|---|
| 0019 | El consumo es una proyección del registro: payloads v2, deduplicación por sesión y seis etiquetas de procedencia; sin tabla de uso | P0 |
| 0020 | La cuota es pasiva en Claude (`rate_limit_event`) y en Codex una lectura opcional del app-server: lista cerrada de mensajes y respuesta por lista blanca | WP1 |
| 0021 | La salud del contexto se mide por paso, con la ventana del modelo del paso. Umbrales globales y validados. Codex `exec`, «no expuesto» | WP1 y WP2 |
| 0022 | La memoria es append-only y anclada al registro, con confianza por origen y cuarentena de lo heredado; el Markdown es solo de exportación; el juez nunca recibe memoria | P0 |
| 0023 | El empaquetado de contexto va por niveles y falla cerrado si los requisitos obligatorios no caben | WP3 |
| 0024 | El recomendador asistido usa un servidor MCP local de solo lectura, un duodécimo proyecto (amplía ADR-0002), solo en lanzamientos de lectura | WP4 |
| 0025 | El resumen diario es determinista a partir de la bitácora, leída por el almacén comprobado; la versión por agente es opcional y con OK | WP3 |

---

## 11. Riesgos y preguntas abiertas

| Riesgo | Mitigación |
|---|---|
| El app-server de Codex es experimental: el esquema puede cambiar entre versiones | Respuesta por lista blanca, captura grabada por versión, aviso de «versión no probada», `codex app-server generate-json-schema` (local, sin red) en cada versión nueva |
| La semántica de los acumulados al reanudar no está medida (Claude, tras un intento matado; Codex, `exec resume`) | Regla de D-02 con «estimado» cuando una cifra baja. Lo confirmará la primera ejecución real con reintento |
| El coste de Claude se lee como gasto real | Etiqueta «estimado por el CLI (precio de lista)», `costBasis` y la nota de la suscripción |
| `costBasis: unknown` en un modelo nuevo | Se muestra tal cual: el CLI no conoce su precio |
| La línea «Resultado» de la bitácora enseña el acumulado de una sesión reanudada | Hoy es lo que dice el CLI. Si cabe en WP1, se añade «(acumulado de la sesión)» |
| Inyección de instrucciones desde la memoria | D-19, D-20 y D-23, con el residual de la §7 |
| Una base plantada por un repositorio de terceros | Anclaje y cuarentena; el historial sin ancla bloquea (A-01) |
| La exportación ensucia el árbol del usuario después de cada ejecución | Como la bitácora (ADR-0008). Si molesta, la decisión 4 de la §12 la deja local |
| Proyectar el registro entero en cada consulta | Medirlo en WP2; caché solo si hace falta |
| El SDK de MCP cambia rápido (2.0 salió en julio de 2026) | Versión fijada en `Directory.Packages.props`, pruebas del protocolo sobre flujos y la alternativa a mano |
| Que Claude cargue un servidor MCP de más o que una herramienta MCP se permita sola | La medición en vivo de D-30, y `--strict-mcp-config` siempre |
| FTS5 no lematiza en castellano | Aceptado. Un tokenizador `trigram` se puede evaluar más adelante |
| La bitácora es texto libre de dos escritores | Análisis tolerante: lo que no se reconoce solo se cuenta |

**Preguntas abiertas (para el diseño, no para ti):**

1. ¿La conciliación entre la bitácora y el registro (D-08) debe excluir los días anteriores a la Fase 4? Propuesta: sí;
   antes no había ejecuciones auxiliares.
2. ¿Se muestra la cuota de Codex al terminar cada ejecución de Codex solo si la lectura anterior tiene más de 5 minutos?
   Propuesta: sí, para no repetir lecturas en ejecuciones seguidas.

---

## 12. Decisiones que necesitan al usuario

> **Resueltas el 2026-10-18.**
>
> - **1:** opción (b). `CLAUDE.md` ya permite la lectura de cuota del app-server de Codex.
> - **2:** opción (b).
> - **3:** opción (a).
> - **4:** opción (a), por defecto recomendado.
> - **5:** opción (b).
> - **6:** opción (a), por defecto recomendado.
>
> Las 1, 2, 3 y 5 las eligió el usuario. Las 4 y 6 quedan con la opción recomendada salvo que diga otra cosa.

1. **Leer la cuota de Codex con el app-server.**
   - **Qué es.** Codex llama al backend de ChatGPT con tu sesión. No es un turno del modelo (código de 0.160.0), pero
     queda fuera de las tres sondas que permite `CLAUDE.md`.
   - **Opciones:**
     - (a) no leerla: Codex queda «no expuesto» y F4-4 se cumple solo a medias;
     - **(b) opt-in en Ajustes, bajo demanda y una vez al terminar cada ejecución de Codex: recomendada;**
     - (c) además, sondeo periódico mientras la app está abierta.
   - **Con (b) hacen falta tres cosas:**
     - tu OK para ampliar la regla de sondas de `CLAUDE.md`;
     - una lectura real para la captura del test;
     - que corras `codex app-server generate-json-schema`, que es local.
2. **Recomendación asistida de skills, que gasta cuota.**
   - (a) solo determinista en la Fase 4 (F4-8 a medias);
   - **(b) botón con tu OK en cada uso, desactivado por defecto, una ejecución de Claude con modelo pequeño (haiku) por
     consulta: recomendada;**
   - (c) dentro de la planificación, que cambia ADR-0013. No se recomienda ahora.
3. **Resumen diario.**
   - **(a) determinista, sin cuota: recomendada;**
   - (b) determinista y, además, uno narrativo por agente con tu OK cada vez;
   - (c) narrativo automático.
4. **Versionar el Markdown de la memoria en tu repositorio.**
   - **(a) versionado, como la bitácora: solo lo de confianza y lo aceptado, exportado fuera de las ejecuciones y con los
     secretos redactados. Recomendada;**
   - (b) local, excluido de git en `info/exclude`;
   - (c) sin exportación automática, solo cuando la pidas.
5. **Qué memoria llega a los agentes.**
   - (a) ninguna por defecto: activas tú `ReadSelected` por agente en la plantilla;
   - **(b) `ReadSelected` para el orquestador y el ejecutor de la plantilla por defecto, solo con memoria de confianza
     (del sistema, tuya y la de agentes que aceptes); el juez, nunca. Recomendada;**
   - (c) como (b), y además lo propuesto sin revisar, marcado.
   - Lo escrito por un agente entra como propuesta y lo aceptas tú, de una en una o por ejecución.
6. **Umbrales de salud del contexto.**
   - **(a) 70, 85 y 95 %, globales, que ya son los del evaluador: recomendada;**
   - (b) otros valores;
   - (c) umbrales por agente, que cuestan más UI.

---

## 13. Sin verificar

- **Codex:**
  - **La respuesta real de `account/rateLimits/read` en 0.160.0.** Solo se leyeron el código, en la etiqueta, y la
    documentación. No se hizo ninguna llamada.
  - **Si `codex app-server` arranca sin hilo los servidores MCP de `~/.codex/config.toml`,** y si `-c mcp_servers={}`
    lo evitaría. Tampoco se midieron sus trabajos de fondo al arrancar.
  - **Si `codex exec resume` informa en `turn.completed.usage` también de los turnos anteriores.** El código toma el
    total del hilo; falta una ejecución.
  - **La salida de `codex app-server generate-json-schema` para 0.160.0:** no se ejecutó.
- **Claude:**
  - **`system/compact_boundary`:** está documentado en los tipos del SDK y no aparece en las capturas.
  - **Los totales de una sesión reanudada en `claude -p` 2.1.290:** documentados desde 2.1.277, sin medir, sobre todo
    tras un intento matado.
  - **El lanzamiento MCP:**
    - que `--mcp-config` con `--strict-mcp-config` cargue solo el servidor de Agent Studio;
    - el nombre de sus herramientas en 2.1.290;
    - que `dontAsk` deniegue una herramienta MCP sin regla.

    Está documentado, no medido.
  - **`result.usage.iterations`:** no está documentado y no se usa.
- **`ModelContextProtocol` 2.2.0:** su compatibilidad con el cliente MCP de Claude Code 2.1.290 (versión del protocolo).

---

## Fuentes

Consultadas el 2026-10-18.

- Claude Code, «Track cost and usage»: <https://code.claude.com/docs/en/agent-sdk/cost-tracking>
- Claude Code, referencia del SDK de TypeScript (`SDKResultMessage`, `SDKCompactBoundaryMessage`, `SDKSystemMessage`):
  <https://code.claude.com/docs/en/agent-sdk/typescript>
- Claude Code, permisos (reglas de herramientas MCP y `dontAsk`): <https://code.claude.com/docs/en/permissions>
- Ayuda local de `claude` 2.1.290: `--mcp-config`, `--strict-mcp-config`, `--tools` y `--permission-prompts`.
- Codex, app-server: <https://learn.chatgpt.com/docs/app-server> (y su versión `.md`)
- Codex, PR #5302, «[app-server] read rate limits API»: <https://github.com/openai/codex/pull/5302>
- Código de Codex en `rust-v0.160.0`:
  - métodos: <https://github.com/openai/codex/blob/rust-v0.160.0/codex-rs/app-server-protocol/src/protocol/common.rs>
  - tipos de cuota: <https://github.com/openai/codex/blob/rust-v0.160.0/codex-rs/app-server-protocol/src/protocol/v2/account.rs>
  - manejador de la cuota: <https://github.com/openai/codex/blob/rust-v0.160.0/codex-rs/app-server/src/request_processors/account_processor.rs>
  - peticiones al backend: <https://github.com/openai/codex/blob/rust-v0.160.0/codex-rs/backend-client/src/client/rate_limit_resets.rs>
  - arranque del app-server: <https://github.com/openai/codex/blob/rust-v0.160.0/codex-rs/app-server/src/message_processor.rs>
  - salida JSONL de `exec`: <https://github.com/openai/codex/blob/rust-v0.160.0/codex-rs/exec/src/event_processor_with_jsonl_output.rs>
- Ayuda local de `codex` 0.160.0: `app-server`, `app-server daemon`, `debug` y `doctor`.
- NuGet, `ModelContextProtocol`: <https://www.nuget.org/packages/ModelContextProtocol>
