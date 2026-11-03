# 15 · Fase 5 · Debate Arena y juez: diseño

> **Estado:** aprobada el 2026-10-19, con las decisiones de la [§12](#12-decisiones-que-necesitan-al-usuario) resueltas
> (todas con la opción recomendada). Parte del código de `main` en `ef6f3fa` (Fase 4 integrada el 2026-10-18).
>
> La redactó un arquitecto independiente a partir de cuatro fuentes:
>
> - el código del repositorio;
> - las capturas reales de los CLIs (`tests/AgentStudio.Tests/Providers/Fixtures/`, Claude Code 2.1.290 y Codex 0.160.0) y
>   las ejecuciones reales registradas en las Fases 3 y 4 ([evidencias/fase3-e2e.md](evidencias/fase3-e2e.md),
>   [ADR-0018](adr/0018-modelo-de-amenaza-fase-3.md), [ADR-0024](adr/0024-recomendador-asistido-con-mcp-local.md));
> - la ayuda local de los dos CLIs instalados;
> - la documentación oficial y la literatura sobre jueces LLM y debate entre agentes (fuentes al final).
>
> **Sin cuota.** Solo se ejecutaron `claude --version`, `codex --version` y las ayudas (`claude --help`, `codex exec --help`,
> `codex exec resume --help` y `codex exec fork --help`). Ningún CLI recibió un prompt, no se llamó a ningún endpoint y no
> se leyó ninguna carpeta de los CLIs. Lo que te toca decidir está en la [§12](#12-decisiones-que-necesitan-al-usuario); lo
> que no se pudo verificar, en la [§13](#13-sin-verificar).

## 0. Las doce decisiones que vertebran la propuesta

1. **Un debate es una unidad del registro, como una ejecución.**
   - Tiene un id propio, `deb-AAAAMMDD-xxxxxx`, que va en el `RunId` de cada entrada. Así lo selecciona el mismo
     `RunLog.EntriesOf` que usa el juez y lo cuenta el panel de consumo sin tocarlo.
   - Su cabecera (modo, pregunta, requisitos, objeto, participantes y jueces con su definición completa, límites) es una
     fila inmutable de `debate_sessions` (migración 0005) que `DebateOpened` ancla con su hash.
   - El estado se deriva del registro verificado y se reanuda reproduciéndolo ([ADR-0010](adr/0010-estado-derivado-del-registro.md)).
     Las tablas de debate de la 0001, con estado mutable, quedan sin uso.
2. **Ningún payload lleva texto de un agente.** Las contribuciones, la pregunta, las citas de código y las razones de una
   impugnación son evidencias. El registro guarda su id y su SHA-256, y quien las lee comprueba ese hash (R3-08).
3. **Cada turno de un participante es una sesión nueva y no persistente** (`--no-session-persistence` en Claude,
   `--ephemeral` en Codex). Lo que sabe un participante está entero en su prompt, que es evidencia. Los debates no usan
   `--resume`; solo la corrección del juez lo conserva, como en la Fase 3.
4. **Las rondas son simultáneas y selladas.**
   - Todos los participantes de una ronda se lanzan a la vez; el límite de concurrencia por proveedor sigue mandando.
   - Ninguno ve nada de la ronda en curso: las respuestas no se capturan ni se anuncian hasta que han terminado todas.
   - La independencia de la ronda 1 es una garantía de contenido, comprobable en los prompts que guarda el registro.
5. **Los límites los mide el sistema:** rondas, tiempo del reloj del sistema, consumo y ejecuciones.
   - El consumo son las cifras de la Fase 4 con su origen. Una cifra que falta se estima y cuenta igual: nunca deja pasar
     el límite.
   - El primero que se alcanza manda el debate a juicio, y el motivo queda en el registro.
6. **El juez es el de la Fase 3:** `AgentJudge`, `VerdictValidator` y una corrección como máximo.
   - El dossier del debate lo construye el sistema desde los almacenes y va a ciegas: P1, P2… sin vendor, modelo ni agente.
   - El veredicto añade una resolución estructurada (la propuesta elegida) que el validador comprueba.
7. **Con dos vendors, el juez por defecto es del otro vendor.**
   - Los participantes comparten vendor, con modelos distintos, y [ADR-0006](adr/0006-juez-independiente-verificable.md) no
     cambia.
   - Mezclar vendors es una dispensa explícita y registrada: jurado cruzado (un juez de cada vendor; decide si coinciden y,
     si no, tú) en los modos que eligen, y un juez del vendor que no produjo el trabajo en los que lo revisan.
   - La regla de los productores ([ADR-0014](adr/0014-independencia-del-juez-con-dos-vendors.md)) no se dispensa nunca.
8. **«Auditar proyecto completo» audita el commit HEAD:** una copia de solo lectura, la verificación aislada del sistema, un
   dossier que es un índice (R8) y un juez del vendor que no produjo el código (los productores del registro y los autores
   que declares). Con un registro que no verifica no se lanza el juez.
9. **El auditor del orquestador es determinista y gratis.** Siete comprobaciones sobre el registro verificado al terminar
   cada ejecución. Deja alertas y pide replanificar, que es proponer una ejecución de seguimiento. El modo «Revisión del
   orquestador» añade el juicio de los agentes.
10. **Impugnar abre un debate de revisión.** Hereda quién puede juzgar y cambia de juez: otra sesión y, a ser posible, otro
    modelo. Nunca entrega solo: si da la vuelta hacia una aprobación, la aceptas tú.
11. **Los participantes son de solo lectura:** sin escritura, shell, red, MCP ni skills.
    - Las contribuciones llegan a los demás como datos delimitados, recodificados y con los secretos redactados.
    - La superficie nueva (de un agente a otro) queda dentro del modelo de ADR-0018, con un residual que aceptas tú.
12. **Una sola actividad con agentes por proyecto.** Un debate, una auditoría y una ejecución del mismo proyecto no corren a
    la vez, y la memoria espera a que terminen.

---

## 1. Alcance de la Fase 5

| ID | Entra | Pieza principal |
|---|---|---|
| F5-1 | Los seis modos, también con participantes de los dos proveedores | `DebateCoordinator` (módulo Debate), material por modo, contribución estructurada y `DebateJudgingRules` (dominio) |
| F5-2 | Ronda 1 en paralelo y sin ver las propuestas de los demás | Turnos sin estado, rondas selladas y una carpeta por turno |
| F5-3 | Límites de rondas, tiempo, consumo y ejecuciones que cortan el debate y lo mandan a juicio | `DebateBudget` (dominio) sobre el uso v2 de la Fase 4 y `DebateRoundClosed` con el motivo |
| F5-4 | Veredicto estructurado y validado, una corrección como máximo, escalado si persiste | `AgentJudge` con `JudgeBrief`, `DebateDossierBuilder`, `JudgingPanel` y dos reglas nuevas del validador |
| F5-5 | «Auditar proyecto completo»: informe de conformidad con evidencias y problemas pendientes | `ProjectAuditService`, `ProjectDossierBuilder` y el documento del informe |
| F5-6 | Auditoría del orquestador: alertas y petición de replanificación | `OrchestratorAudit` (siete comprobaciones puras), `OrchestratorAuditService` y el modo «Revisión del orquestador» |
| F5-7 | Impugnar un veredicto abre un debate de revisión | `VerdictChallengeService`, `VerdictChallenged` y sus efectos en la entrega |
| — | Sala de debate, historial de debates e informe de auditoría | `Desktop/Debate` y `Desktop/Audit`, la entrada «Debates» de la barra de actividad y el botón «Auditar proyecto completo» |

**Se aplaza:**

- **Nodos «Debate» en el lienzo del orquestador.** El validador los sigue rechazando; cambiarían el protocolo de
  [ADR-0013](adr/0013-protocolo-del-orquestador.md) y la reproducción de una ejecución (decisión 3 de la §12).
- **`MemoryPolicy.ReadAndPropose`.** Sigue rechazada: esta fase tampoco abre un canal para que un agente proponga memoria.
  Cambia solo el texto de la regla, que decía «hasta la Fase 5».
- **Skills para los participantes.** La copia de solo lectura del repositorio tiene que ser exactamente el commit, y Codex
  carga además las skills globales del usuario ([10](10-catalogo-de-skills.md#4-activación)). El juez tampoco recibe.
- **Un tercer vendor** (Gemini con API key, [ADR-0009](adr/0009-google-aparcado.md)). El diseño lo admite sin cambios.
- **Auditar al orquestador en mitad de una ejecución.** El motor ya vigila el grafo mientras corre (`Analyze`); el
  auditor revisa lo que quedó escrito.
- **Replanificar dentro de la misma ejecución.** Sigue siendo una ejecución de seguimiento ([13 §1](13-fase-3-orquestacion.md#1-alcance-de-la-fase-3)).
- **Calibrar el juez contra veredictos de personas.** Hace falta un corpus etiquetado, que no existe: lo dice el propio
  `JudgePrompt`. Queda para más adelante.
- **Firma del registro.** Fase 7, como estaba.

---

## 2. Inventario: lo que ya existe

Estados: **Hecho** (código y tests), **Parcial** (existe con un hueco que se nombra), **Contrato** (interfaz sin
implementación), **Sin uso** (existe y nadie lo lee ni lo escribe) y **Falta**. Rutas relativas a la raíz del repositorio.

### F5-1 · Modos y participantes

| Pieza | Dónde | Estado |
|---|---|---|
| `DebateMode` (los seis), `DebateRole` (`Proponent`, `Critic`, `Panelist`), `DebateParticipant` y las reglas de roles de `Open` (adversarial y auditoría con proponente y crítico; tribunal solo de panelistas; dos participantes como mínimo; el juez no participa) | `src/AgentStudio.Domain/Debate/DebateSession.cs`; `tests/AgentStudio.Tests/Domain/DebateSessionTests.cs` | Hecho (dominio) |
| `IDebateCoordinator.RunAsync(projectId, DebateSession, ct)` | `src/AgentStudio.Application/Coordination/CoordinationContracts.cs` | Contrato. Recibe un agregado en memoria: no se puede reanudar tras un reinicio (como el `IOrchestrator` de la Fase 0) |
| Coordinador, prompts de los participantes, esquema de la contribución | — | Falta |
| `WorkTaskKind.Debate`, `PlanLimits.DebateNodesAvailable = false` y `PlanViolationCode.DebateNotAvailable`; el lienzo muestra el nodo desactivado | `src/AgentStudio.Domain/Orchestration/PlanValidator.cs`, `src/AgentStudio.Desktop/Orchestrator/Canvas/NodeKinds.cs` | Hecho (rechazado a propósito) |
| Roles de agente `Architect`, `Critic`, `Tester`, `Documenter`, `Judge` y `Auditor` | `src/AgentStudio.Domain/Agents/AgentDefinition.cs` | Hecho; la plantilla por defecto solo trae orquestador, ejecutor y juez (`src/AgentStudio.Desktop/Orchestrator/Session/DefaultAgentRoster.cs`) |

### F5-2 · Independencia de la ronda 1

| Pieza | Dónde | Estado |
|---|---|---|
| Reglas de la ronda 1: una propuesta por participante, sin `RepliesTo`, y todas antes de deliberar (`debate.independent-proposals`, `debate.one-proposal`, `debate.missing-proposals`) | `DebateSession.Submit` y `AdvanceRound` | Hecho (dominio) |
| Lanzamiento de solo lectura: Claude con `dontAsk`, `--tools` y reglas `Read(//c/…/**)` (lecturas fuera de sus carpetas denegadas, medido); Codex con `-s read-only` | `src/AgentStudio.Providers/Cli/Claude/ClaudeCodeArguments.cs`, `…/Cli/Codex/CodexExecArguments.cs`; ADR-0018, «Medición» | Hecho (planificador y juez) |
| Sesiones que no se guardan | — | Falta. Los dos CLIs tienen la opción, pero el adaptador nunca la pasa, y `CodexExecArgumentsTests` exige que `--ephemeral` no salga |
| Paralelismo, sellado y carpetas por participante | — | Falta. `ConcurrencyLimitedAgentProvider` limita a 2 ejecuciones por proveedor (`MaxConcurrentRuns` de los dos adaptadores) |

### F5-3 · Límites

| Pieza | Dónde | Estado |
|---|---|---|
| `DebateLimits(MaxRounds, MaxDuration, MaxTotalTokens)` (4, 30 min y 400 000 por defecto), `CheckLimits` y `AdvanceRound`, que mandan a juicio con `LimitReached` | `DebateSession.cs` | Hecho (dominio). Quién mide los tokens y cuáles cuentan: Falta |
| Uso de cada intento con su origen: `AgentRunFinished` v2, `MetricOrigin` y `MetricAggregate<T>` | `src/AgentStudio.Application/Decisions/DecisionPayloads.cs`, `src/AgentStudio.Domain/Metrics/**`; ADR-0019 | Hecho |
| Presupuesto por ejecución: `--max-turns`, `--max-budget-usd` (Claude) y el tiempo máximo del proceso (los dos) | `ExecutionBudget` en `AgentDefinition`; los dos adaptadores | Hecho |

### F5-4 · Veredicto

| Pieza | Dónde | Estado |
|---|---|---|
| `Verdict`, `JudgeDossier` y `VerdictValidator` con sus 23 reglas | `src/AgentStudio.Domain/Judging/**`; `tests/AgentStudio.Tests/Domain/VerdictValidatorTests.cs` | Hecho |
| `AgentJudge` (solo lectura en los dos CLIs, identidad puesta por el sistema, una corrección con `--resume`), `VerdictSchema` (modo estricto, 16 KB), `VerdictJson`, `JudgePrompt` y `DossierDocument` | `src/AgentStudio.Debate/Judging/**` | Hecho, pensado para una ejecución: el prompt y el esquema van cableados dentro de `AgentJudge` |
| `IndependentReview`: `ReviewRequested`, registro verificado, cabecera anclada, juez no independiente rechazado sin gastar cuota, veredicto guardado tal cual, una corrección y la disposición | `src/AgentStudio.Debate/Judging/IndependentReview.cs` | Hecho. El registrador de lanzamientos (`JudgeRunRecorder`) y el guardado (`KeepAsync`) son privados |
| `DebateSession.Close(verdict)` | `DebateSession.cs` | Parcial: exige `verdict.Judge == Judge` con el modelo incluido, y `AgentJudge` pone en el veredicto el modelo que anuncia la sesión del CLI. Con Claude es el nombre completo (`claude-haiku-4-5-…`), no el alias configurado (`haiku`): el veredicto real de un juez de Claude nunca cerraría el debate |
| Dossier, prompt, esquema y resolución de un debate | — | Falta |

### F5-5 · Auditar proyecto completo

| Pieza | Dónde | Estado |
|---|---|---|
| `IProjectAuditService`, `AuditReport` (con `IsTrustworthy`) y `AuditScope.WholeProject` | `src/AgentStudio.Application/Judging/JudgingContracts.cs` | Contrato |
| `JudgeDossierBuilder` | `src/AgentStudio.Debate/Judging/JudgeDossierBuilder.cs` | Parcial: rechaza toda petición sin ejecución («Phase 3 builds the dossier of one orchestration run») |
| Copia de un commit sin worktree, sin configuración ni filtros del repositorio (`IWorktreeManager.ArchiveCommitAsync`) y verificación aislada a integridad Low con el commit en cada evidencia | `src/AgentStudio.Application/Git/GitContracts.cs`, `src/AgentStudio.Orchestration/Verification/**`; ADR-0017 | Hecho |
| Informe de conformidad (diseño de [03 §6](03-pantallas-y-navegacion.md#6-auditoría-y-veredictos-del-juez)) | — | Falta |

### F5-6 · Auditoría del orquestador

| Pieza | Dónde | Estado |
|---|---|---|
| `AuditAlertRaised` con `AuditAlertPayload(Code, Tasks, Message)` | `DecisionPayloads.cs`; lo escriben el motor (salud del grafo, repositorio del usuario, cabecera) y la revisión | Hecho |
| `ReplanRequested` y `ActorKind.Auditor` | `src/AgentStudio.Domain/Decisions/DecisionLog.cs` | Sin uso: el tipo existe, pero no tiene payload en `DecisionPayloadSerializer` y nadie lo escribe |
| Problemas de la reproducción (`RunStateReplayer`) | `src/AgentStudio.Domain/Orchestration/RunStateReplayer.cs` | Hecho |
| Las siete comprobaciones de [07 §8](07-debate-arena-y-juez.md#8-supervisión-del-orquestador-sección-9) | — | Falta |

### F5-7 · Impugnación

| Pieza | Dónde | Estado |
|---|---|---|
| Cadena de correcciones (`StoredVerdict.Supersedes`, `VerdictIssuedPayload.Supersedes`) | `src/AgentStudio.Application/Persistence/StoreContracts.cs`, `src/AgentStudio.Infrastructure/Persistence/SqliteVerdictStore.cs` | Hecho |
| La entrega rechaza un veredicto sustituido | `src/AgentStudio.Orchestration/Integration/DeliveryService.cs` | Hecho, sin noción de impugnación: hoy cualquier sustituto bloquea, también uno que confirme la aprobación |
| Impugnar | — | Falta |

### Persistencia, consumo, memoria y bitácora

| Pieza | Dónde | Estado |
|---|---|---|
| `debates` (con `phase`, `outcome` y `closed_at` mutables y sin triggers), `debate_participants`, `debate_contributions` (append-only, con el texto) y `debate_contribution_evidence`; `verdicts.debate_id` | `src/AgentStudio.Infrastructure/Persistence/Schema/0001_initial.sql` | Sin uso: nadie las escribe; `SqliteVerdictStore` pone `debate_id` a NULL |
| `AuxiliaryAgentRunner` | `src/AgentStudio.Application/Providers/AuxiliaryAgentRunner.cs` | Hecho, pero solo admite los propósitos `SkillRecommendation` y `DailySummary` |
| `AgentRunPurpose` | `DecisionPayloads.cs` | Parcial: no tiene propósitos de debate ni de auditoría |
| Proyección del consumo por (ejecución, tarea, intento), sin mirar `orchestration_runs` | `src/AgentStudio.Observability/Usage/UsageProjector.cs` | Hecho: contará los debates si cada turno tiene su clave |
| `IRunActivity` (la memoria espera mientras hay una ejecución o una planificación) | `src/AgentStudio.Application/Orchestration/RunActivity.cs`; lo implementa `OrchestrationActivity` (Orchestration) | Parcial: no sabe de debates ni de auditorías |
| `MemoryKind.DebateSummary`, con sus etiquetas | `src/AgentStudio.Application/Knowledge/MemoryContracts.cs`, `src/AgentStudio.Memory/**` | Contrato: nadie lo deriva |
| `PromptJournalEntry.DebateId` | `src/AgentStudio.Application/Knowledge/PromptJournal.cs` | Sin uso: `JournalingAgentProvider` nunca lo rellena |
| `PromptData` (recodifica `<`, `>`, `&` y comillas) | `src/AgentStudio.Orchestration/Engine/PromptData.cs` | Hecho, pero `internal`: el módulo Debate no lo puede usar (ADR-0002) |

**Pantallas que existen hoy:** las de las Fases 1 a 4. En la barra de actividad no hay entrada de debates, y el botón
«Auditar proyecto completo» de [03 §2](03-pantallas-y-navegacion.md#2-ventana-principal) no existe.

---

## 3. Lo que exponen de verdad los CLIs para debatir

### 3.1 Claude Code 2.1.290

| Capacidad | Cómo | Qué da | Cómo se sabe |
|---|---|---|---|
| Sesión nueva que no se guarda | `-p --no-session-persistence` | «Disable session persistence so sessions are not saved to disk and cannot be resumed. Print mode only.» La guía de sesiones lo describe como «suppress writes for one non-interactive run» | Ayuda local; doc «CLI reference» y «Sessions» |
| Dónde se guardan las sesiones | — | `~/.claude/projects/<proyecto>/<sesión>.jsonl`, 30 días por defecto (`cleanupPeriodDays`). La variable `CLAUDE_CODE_SKIP_PROMPT_HISTORY` suprime las transcripciones en todos los modos | Doc «Sessions». Agent Studio nunca lee esa carpeta |
| Id de la sesión | `--session-id <uuid>` (el adaptador ya lo pasa) | El id que anuncia `system/init` | Capturas 01, 02 y 04 |
| Reanudar o bifurcar | `--resume <id>`; `--fork-session` | El historial completo, con las llamadas a herramientas y sus resultados; hay que volver a pasar `--mcp-config`, `--settings`, `--plugin-dir` y `--add-dir`. Con `--fork-session`, en una sesión nueva. Desde 2.1.277 cada resultado incluye el gasto anterior de la sesión | Ayuda local; doc «Sessions», «CLI reference» y «Track cost and usage»; notas de 2.1.277 |
| Salida estructurada | `--json-schema` en línea (16 KB como mucho), por la herramienta `StructuredOutput` | `result.structured_output`; si no llega tras sus reintentos, `error_max_structured_output_retries` | Captura 03; spike S3; F2-3; doc «Agent loop» del SDK |
| Límites de una ejecución | `--max-turns` y `--max-budget-usd` | `result.subtype` `error_max_turns` o `error_max_budget_usd`, que el adaptador traduce a `LimitReached`. La respuesta que alcanza el tope de presupuesto se completa y cuenta en el coste | Ayuda local; doc «Agent loop» del SDK |
| Tiempo | No hay opción de tiempo máximo: lo pone el proceso (`ProcessSpec.Timeout`) | `LimitReached` | Doc «CLI reference»; adaptador |
| Uso | `result.usage` (suma de los pasos del bucle principal) y `result.modelUsage` | Entrada (con la caché leída y la escrita), salida y coste estimado a precio de lista | [14 §3.1](14-fase-4-observabilidad-y-memoria.md#31-claude-code-21290) |
| Lecturas confinadas | `dontAsk`, `--tools` y reglas `Read(//c/…/**)` | Leer fuera del directorio de trabajo y de cada `--add-dir` se deniega | Medido (ADR-0018, segunda y tercera medición) |
| Concurrencia | El adaptador declara 2 | La documentación no fija un límite de sesiones `-p` a la vez; avisa de que reanudar la misma sesión en dos terminales mezcla sus mensajes en una transcripción | Doc «Sessions»; F2-5 (medido con límite 1) |
| Modo restringido | `--restricted` (desde 2.1.248) | Quita las herramientas que ejecutan código y confina las de ficheros a los directorios de trabajo | Ayuda local; doc «CLI reference». No se usa ni se ha medido |

Coste medido de referencia, que usa la estimación de D-21:

- planificador con haiku: 59 247 tokens de entrada (41 467 de caché) y 6 295 de salida, 0,074 USD (F3-8);
- respuesta estructurada mínima: 38 964 tokens de caché creada, 0,080 USD (captura 03);
- recomendador con MCP: 5 turnos, 0,034 USD (medición D-30 de la Fase 4).

Cada ejecución arrastra unos 39 000 tokens de entrada fijos del propio CLI. El coste es «estimado por el CLI (precio de
lista)»: con suscripción no es lo que pagas.

### 3.2 Codex CLI 0.160.0

| Capacidad | Cómo | Qué da | Cómo se sabe |
|---|---|---|---|
| Sesión que no se guarda | `exec --ephemeral` | «Run without persisting session rollout files to disk.» En el código de 0.160.0 solo se aplica al empezar o bifurcar un hilo, no al reanudarlo. El historial `history.jsonl` es otra opción (`history.persistence`: `save-all` o `none`) | Ayuda local; doc «Developer commands» y «Config reference»; código de `rust-v0.160.0` (`exec/src/lib.rs`) |
| Reanudar o bifurcar | `exec resume <id>`; `exec fork <id>` | La ayuda local de `resume` admite `--output-schema`; la tabla de la documentación no lo lista, y el código lo envía con el turno | Ayuda local; doc «Non-interactive mode»; código (`exec/src/cli.rs`, `lib.rs`). Que `resume` respete el esquema no se ha medido (ADR-0014) |
| Salida estructurada | `--output-schema <fichero>` (modo estricto de OpenAI) | El JSON en el último `agent_message` | Captura 03; F2-3 |
| Límites | Ninguno en `exec`: ni turnos, ni tiempo, ni tokens. `model_auto_compact_token_limit` es un umbral de compactación, no un tope | Solo el tiempo del proceso | Ayuda local; doc «Config reference»; código (`lib.rs`) |
| Uso | `turn.completed.usage` | Exacto; es `usage.total` del `ThreadTokenUsage`, el acumulado del hilo. Todo a cero es «sin dato» | [14 §3.2](14-fase-4-observabilidad-y-memoria.md#32-codex-cli-01600); código (`event_processor_with_jsonl_output.rs`); capturas |
| Modelo | — | `exec --json` no lo informa: el juez del E2E sale «modelo no informado» | [fase3-e2e](evidencias/fase3-e2e.md) |
| Solo lectura | `-s read-only` | «Can inspect files, but it can't edit files or run commands without approval», sin limitar la lectura al workspace. En Windows, «the permission profile determines which paths are readable or writable», y el modo `unelevated`, el que pasa Agent Studio, «doesn't support denied read paths»: **no hay forma de restringir lo que lee**. El juez de la Fase 3 leyó el paquete del dossier en `%TEMP%`, fuera de su `-C` | Doc «Sandboxing» y «Windows sandbox»; ADR-0014 (medido). Que bloquee una escritura en Windows no se ha ejercitado |
| Concurrencia | El adaptador declara 2 | La documentación no fija un límite de ejecuciones a la vez: los mensajes locales y en la nube comparten la asignación del plan, con límites semanales | Doc «Pricing»; adaptador |

Coste medido de referencia:

- juez del E2E: 76 245 tokens de entrada (35 584 de caché) y 1 094 de salida, en 30 s (F3-8);
- respuesta estructurada mínima: 18 483 tokens de entrada (captura 03).

Codex no informa de coste en dinero.

### 3.3 Lo que dice la literatura sobre jueces LLM y debates

Son estudios con otros modelos y otras tareas: orientan el diseño, no miden a Claude ni a Codex en esta aplicación (§13).

- **Autopreferencia.** En MT-Bench, GPT-4 se favorecía con una tasa de victorias un 10 % mayor y Claude-v1 un 25 %, aunque
  los autores no pudieron afirmar que fuera un sesgo (Zheng et al., 2023). La capacidad de un modelo de reconocer sus propias
  respuestas se correlaciona linealmente con la fuerza de esa preferencia (Panickssery et al., 2024). De ahí se deduce que
  ocultar la autoría no basta si el juez reconoce el estilo; ninguna de las fuentes leídas lo mide directamente.
- **Posición.** Cambiar el orden de las respuestas cambia el veredicto: con el orden intercambiado, GPT-4 era consistente en
  el 65 % de los casos, GPT-3.5 en el 46 % y Claude-v1 en el 24 % (Zheng et al.). Agregar el juicio sobre varios órdenes lo
  mitiga (Wang et al., 2023).
- **Jurados.** Un panel de modelos de familias distintas tiene menos sesgo propio que un único juez grande, y cuesta menos
  (Verga et al., 2024). Con dos jueces que comparten vendor cada uno con un participante, eso no está medido: por eso el
  jurado cruzado de D-14 es una dispensa con desempate humano, no independencia recuperada.
- **Debate.** Que varios modelos debatan mejora el razonamiento y la factualidad en sus pruebas (Du et al., 2023), y unos
  debatientes más persuasivos ayudan a un juez menos experto a encontrar la verdad (Khan et al., 2024; la idea viene de
  Irving et al., 2018). Pero los sistemas de debate no superan de forma fiable a estrategias más simples, como la
  autoconsistencia (Smit et al., 2023).

### 3.4 Lo que esto decide

- **Los dos CLIs tienen sesiones que no se guardan:** el turno sin estado (D-16) es posible sin tocar el perfil de ningún
  CLI ni su configuración.
- **No guardar y reanudar son excluyentes.** Claude lo dice («cannot be resumed»); en Codex, un hilo sin sus ficheros no se
  puede reanudar. La corrección del juez sigue guardando su sesión.
- **Ninguno de los dos tiene un límite de tiempo, y Codex tampoco de turnos ni de presupuesto:** el debate se corta por el
  tiempo del proceso y por el consumo que mide el sistema.
- **Codex en solo lectura lee todo el disco del usuario,** y en Windows no hay opción para impedirlo. La independencia de la
  ronda 1 no puede apoyarse en lo que un participante no puede leer, sino en que no haya nada que leer (D-17).
- **Las cifras de tokens de los dos son exactas** («informado por el CLI»). Lo que Codex no expone es el coste, la ventana y
  el modelo.
- **Un debate no garantiza una decisión mejor** (§3.3): es una deliberación auditable, con un juez cuyo veredicto
  comprueba el sistema. Las demostraciones dirán cuánto aporta aquí.

---

## 4. Decisiones por criterio

### 4.1 Un debate en el registro

**D-01 · Dónde vive un debate.**

| Opción | Ventajas | Inconvenientes |
|---|---|---|
| **A. Una unidad del registro**, con cabecera inmutable (`debate_sessions`) y el estado reproducido, elegida | El patrón de una ejecución (ADR-0010): una sola fuente, encadenada y anclada. El juez, la pantalla y la reanudación leen lo mismo, y el consumo lo cuenta solo | Tipos de entrada y payloads nuevos, y un reproductor más |
| B. Las tablas `debates…` de la 0001 | Ya existen | `phase`, `outcome` y `closed_at` se actualizan sin triggers: dos fuentes que pueden divergir. `debate_contributions` guarda texto de agentes fuera de las evidencias |
| C. En memoria (`IDebateCoordinator.RunAsync(DebateSession)`) | Sencillo | No se reanuda y nada queda anclado |

Recomendación: **A**.

- **Id.** `deb-AAAAMMDD-xxxxxx`: el prefijo, el día en UTC y seis cifras hexadecimales aleatorias que no estén ya en el
  registro. Es válido como `RunId`. Una auditoría de proyecto lleva `aud-…`. Ninguno está en `orchestration_runs`, así que
  la lista de ejecuciones no los enseña.
- **Cabecera.** `DebateRecord`, en `debate_sessions`:
  - modo, hash de los requisitos, evidencia de la pregunta, objeto y commit de la copia;
  - participantes y jueces con su `AgentDefinition` completa;
  - disposición del juicio, dispensas, límites, ejecuciones máximas e impugnación.

  `DebateHeaderHash` es su SHA-256 canónico, con el patrón de `RunHeaderHash`, y `DebateOpened` lo ancla. Reanudar y lanzar
  a un juez exigen que la fila dé ese hash (N-05).
- **Estado.** `DebateStateReplayer` (dominio) abre la sesión con la cabecera y le aplica, por su API pública, lo que dice el
  registro: contribuciones, cierres de ronda, envío a juicio y cierre. Un registro que el dominio no habría permitido deja
  el debate sin reanudar, con el motivo.

**D-02 · El texto es evidencia.**

| Texto | Evidencia |
|---|---|
| La pregunta | `Document`/`ProvidedByHuman`, capturada antes de la fila de la cabecera, como la petición original de una ejecución |
| Una contribución | `AgentStatement`/`ReportedByAgent`: el JSON validado de la respuesta, con los secretos redactados (D-10) |
| Una cita de código | `Document`/`CapturedBySystem`, con `SubjectCommit` = el commit de la copia (D-10) |
| Tus razones al impugnar y tus notas | `HumanNote`/`ProvidedByHuman` |
| Las pruebas que pides | `BuildLog` o `TestRun`/`CapturedBySystem`, del verificador (D-27) |

- Cada una se anuncia con `EvidenceRegistered` v2 del debate (`RunId` = el id del debate). Toda lectura que alimenta un
  prompt o el dossier se comprueba contra el hash anclado (R3-08).
- Las tablas `debates`, `debate_participants`, `debate_contributions` y `debate_contribution_evidence`, y la columna
  `verdicts.debate_id`, quedan sin uso ([05 §5](05-modelo-de-datos.md#5-tablas-de-la-0001-sin-uso-en-la-fase-3)).

**D-03 · Entradas del registro.** Siete tipos nuevos, al final de `DecisionKind`. El resto se reutiliza.

| Tipo | Actor | Lo esencial del payload | Cuándo |
|---|---|---|---|
| `DebateOpened` (nuevo) | Sistema | Modo, hash de la cabecera y de la propuesta aprobada; requisitos; pregunta (id y SHA-256); objeto y commit de la copia; participantes (agente, papel, proveedor, vendor, modelo y etiqueta P1…); jueces; disposición; vendors productores; dispensas; límites; impugnación | Tras tu aprobación y antes del primer turno |
| `DebateContributionRecorded` (nuevo) | Sistema | Índice, ronda, autor, tipo y `repliesTo`; evidencia y SHA-256; evidencias citadas y citas no resueltas; redacciones; si terminó; intento y turno | Al cerrar la ronda, en el orden de la cabecera |
| `DebateRoundClosed` (nuevo) | Sistema | Ronda y fase siguiente; límite alcanzado y motivo; tokens con su origen y si había estimación; segundos transcurridos | Al cerrar cada ronda y al mandar a juicio |
| `DebateClosed` (nuevo) | Sistema o usuario | Resultado, veredictos, regla aplicada (juez único, coinciden, discrepan, abortado o decisión tuya), propuesta elegida y motivo | Al terminar |
| `VerdictChallenged` (nuevo) | Usuario | Veredicto, razones (id y SHA-256) y debate de revisión | Al impugnar (F5-7) |
| `ProjectAuditStarted` y `ProjectAuditFinished` (nuevos) | Sistema | Auditoría, alcance, commit, requisitos, hash del perfil, juez, autores declarados y hash de la cabecera; y luego veredictos y disposición | F5-5 |
| `ReplanRequested` (existe, sin payload hasta ahora) | Auditor o juez | Hallazgos, tareas, mensaje y el veredicto, si lo hay | F5-6 |
| `AgentRunStarted` y `AgentRunFinished` v2, `EvidenceRegistered` v2, `VerdictIssued`, `AuditAlertRaised`, `ApprovalRequested`, `ApprovalGranted` y `ApprovalDenied` | Los de siempre | — | Cada turno, evidencia, veredicto, alerta y aprobación |

- Los nombres de los tipos que ya existen no cambian: el hash cubre `kind.ToString()`. `DecisionKindCompatibilityTests` gana
  la lista de la Fase 5.
- Los payloads nuevos empiezan en `V = 1` y se escriben con `DecisionPayloadSerializer`.

**D-04 · Reanudar tras reiniciar.**

- La lista de la sala sale de reproducir cada debate. Uno sin `DebateClosed` y sin proceso detrás sale «Interrumpido».
- Reanudar es una acción tuya, porque gasta cuota, y pide su aprobación (D-06). Antes de lanzar nada:
  1. el registro verifica y la cabecera da el hash anclado;
  2. cada turno abierto (`AgentRunStarted` sin final) se cierra con `AgentRunFinished(Interrupted)`, que no cuenta como
     reintento;
  3. la respuesta de un turno que terminó en una ronda que no llegó a cerrarse se perdió con el proceso, porque estaba
     sellada (D-17): ese participante responde otra vez, con un intento nuevo;
  4. lo que ya está anunciado no se repite.
- Como los turnos no tienen estado (D-16), reanudar no depende de las sesiones que guarden los CLIs.
- Si la copia del repositorio no está, se vuelve a extraer del mismo commit.

**D-05 · Una sola actividad con agentes por proyecto.**

- `IProjectActivity` (Application) lleva la actividad de cada proyecto en el proceso: ejecución, planificación, debate o
  auditoría. Implementa `IRunActivity`, así que la memoria espera también durante un debate: derivarla o exportarla movería
  la cabeza del registro entre que se enseña un veredicto y se usa.
- El coordinador, la auditoría y el orquestador piden entrar antes de escribir nada. Si el proyecto está ocupado, se
  rechaza con el motivo.
- **Por qué no a la vez:** el motor compara el repositorio del usuario alrededor de cada paso y los dos escriben en el mismo
  registro. Nada compensa ese riesgo.

**D-06 · Tu aprobación antes de gastar.**

- «Abrir debate» muestra la configuración exacta y la estimación de D-21:
  - modo, participantes con su CLI y su modelo, jueces y su disposición, dispensas y límites;
  - las ejecuciones que puede hacer como mucho y los tokens, «estimado por Agent Studio».
- La aprobación va por `IApprovalGate` (acción `debate.open`, en el `RunId` del debate) y queda atada con
  `ApprovalRequest.Binding` al hash de lo que se te enseña (`DebateProposal.HeaderHash`): la pregunta y los requisitos por su
  SHA-256, los participantes y los jueces con su definición, la disposición, las dispensas y los límites. Cambiar algo
  después la anula. `DebateOpened` repite ese hash junto al de la cabecera guardada, así que el registro ata la aprobación al
  debate que se abrió.
- Un debate cuya apertura se deniega no llega a tener `DebateOpened` y no sale en la lista.
- Dentro de sus límites, el debate no vuelve a preguntar. Reanudar, impugnar y auditar piden la suya.

### 4.2 F5-1 · Los seis modos

**D-07 · Qué hace cada modo.**

| Modo | Participantes | Objeto | Ronda 1 | Rondas 2..N | Requisitos | Juicio | Salida |
|---|---|---|---|---|---|---|---|
| Libre | 2 o más, cualquier papel | Ninguno o el commit HEAD | Propuestas | Objeciones, réplicas y evidencias | Tus criterios (`JudgeAssessment` o `HumanReview`) | Elección | La propuesta elegida o «no resuelto» |
| Adversarial | 1 `Proponent` y 1 `Critic` como mínimo | Ídem | Propuesta y crítica independientes | El crítico busca errores, contradicciones, riesgos e incumplimientos; el proponente replica | Ídem | Elección (la del proponente o ninguna) | Aprobar, pedir cambios o rechazar |
| Revisión de arquitectura | 2 o más | Ídem | Diseños independientes | Comparación | Mantenibilidad, seguridad, rendimiento, coste y complejidad, más los tuyos | Elección con comparación | El diseño recomendado, con sus riesgos |
| Auditoría de implementación | 1 `Proponent` (defiende la entrega) y 1 `Critic` como mínimo | Una ejecución terminada con commit candidato | Defensa y cuestionamiento | Con las evidencias de la ejecución y «Ejecutar pruebas» | Los congelados de la ejecución | Revisión | ¿Cumple los requisitos? |
| Revisión del orquestador | 2 revisores o más (`Critic` o `Panelist`) | Una ejecución (su registro) | Revisiones independientes del registro y del informe del auditor (F5-6) | Contraste de hallazgos | Los siete de D-36 | Revisión | Alertas y petición de replanificación |
| Tribunal | 2 `Panelist` o más, y solo panelistas | Cualquiera | Evaluaciones independientes (`Assessment`) | No hay | Según el objeto | Síntesis | El veredicto sintetizado |

- **Elección y revisión** son las dos familias de D-13. En los modos de elección, el trabajo son las propias contribuciones;
  en los de revisión, una ejecución que produjeron otros.
- Con una sola ronda, el debate va a juicio tras las propuestas, como ya hace el dominio. El tribunal no delibera nunca.

**D-08 · Objeto y material.**

- **Sin objeto.** La carpeta de trabajo de cada turno está vacía: cuentan la pregunta, los requisitos y lo que se aporte.
- **Commit.**
  - Es el HEAD del repositorio del usuario al abrir (`InspectAsync`). Los cambios sin commit no entran y se avisa, como al
    empezar una ejecución.
  - La copia sale de `IWorktreeManager.ArchiveCommitAsync`: sin worktree ni refs, sin configuración ni filtros del
    repositorio, solo ficheros y carpetas.
  - Vive en `%LOCALAPPDATA%\AgentStudio\debates\<repo8>\<debate6>\snapshot` y la comparten los participantes y los jueces,
    todos de solo lectura. Se borra al cerrar el debate y se puede volver a extraer.
- **Ejecución.** El paquete del dossier de esa ejecución (`IJudgeDossierPackageBuilder`, el de la Fase 3) y la copia de su
  commit candidato. Sin commit candidato, la auditoría de implementación no se abre.
- **Veredicto.** El de una impugnación (D-38).
- **Material de cada turno.**
  - Es `debate.json` (formato `agent-studio/debate-material/1`): la pregunta, los requisitos, el papel del participante,
    las etiquetas, las contribuciones publicadas, las evidencias (id, tipo, origen, título y fichero) y el resumen del objeto.
  - Va en `…\<debate6>\turns\<turno>\`. Se escribe con `CreateNew` y sin seguir enlaces, como el paquete del dossier, y se
    pasa con `--add-dir`.
  - El mismo JSON va en el prompt.

**D-09 · Participantes.**

- Son agentes de la plantilla del proyecto de cualquier rol salvo `Judge`, `Auditor` y `Orchestrator`. Su papel en el
  debate (`DebateRole`) se asigna al abrirlo.
- **Un perfil fijo de solo lectura**, diga lo que diga su definición:
  - `PermissionProfile(AllowFileWrites: false, AllowShell: false, AllowNetwork: false)`, con `Read`, `Grep`, `Glob` y
    `StructuredOutput`;
  - en Claude, `dontAsk` y reglas `Read` para la copia y la carpeta de su turno; en Codex, `-s read-only` con `-C` en la
    copia;
  - sin MCP ni skills.
- **Memoria: ninguna por defecto.** El debate se decide con el material congelado, y la memoria trae contexto que nadie ha
  revisado para esta pregunta. La plantilla puede dar `ReadSelected` a un participante; el juez nunca recibe.
- **Presupuesto de un turno:** el del agente (`--max-turns`, tiempo y, en Claude, `--max-budget-usd`), con un tope de 12
  turnos del CLI y 5 minutos si el agente no pone menos.
- **Plantilla por defecto** de los proyectos sin plantilla (la cambia el lead): `arquitecto` (Architect, Claude), `critico`
  (Critic, Codex), `juez-claude` (Judge, Claude) y `juez-revision` (Judge, Codex con otro modelo), además de los tres de hoy.

**D-10 · La contribución.**

- **Esquema portable**, con el subconjunto de ADR-0013: todo obligatorio, sin anulables y con el centinela `"ninguna"`. Se
  construye para cada turno, con enums dinámicos:

  ```json
  {
    "kind": "Proposal | Assessment | Objection | Rebuttal | Evidence",
    "repliesTo": "C3 | ninguna",
    "summary": "…",
    "argument": "…",
    "claims": [{ "claim": "…", "support": ["C2", "EV-…", "CITA-1"] }],
    "citations": [{ "id": "CITA-1", "path": "src/…", "startLine": 10, "endLine": 40, "why": "…" }],
    "concedes": ["C2"],
    "finished": false
  }
  ```

  `kind` solo ofrece lo que admite la ronda: propuesta o evaluación en la 1, y objeción, réplica o evidencia después.
  `repliesTo` y `support` solo ofrecen lo publicado.
- **Parseo estricto**, como el del plan: nada desconocido, repetido ni nulo. Topes: resumen de 600 caracteres, argumento de
  8 000, 10 afirmaciones y 8 citas de 80 líneas como mucho.
- **Una corrección.** Una respuesta que no se lee o no valida da un intento nuevo con los problemas como datos
  (`<contribution_problems>`). Si falla otra vez, el turno queda sin contribución (D-22).
- **Citas ancladas.** El sistema resuelve cada cita en la copia del commit:
  - la ruta es relativa, sin `..`, `:` ni raíz, y su ruta final cae dentro de la copia;
  - es un fichero normal de 1 MiB como mucho, y las líneas existen;
  - las líneas exactas se capturan como `Document`/`CapturedBySystem`, con `SubjectCommit`, y la contribución las enlaza.

  Una cita que no se resuelve se anota en la contribución y llega al juez como un hecho («P2 citó `src/x.cs:200-210`, que no
  existe en el commit»). Es materia para un hallazgo `FabricatedResult`, no un problema de integridad del material.
- **Redacción.** Antes de capturarla, la contribución pasa por `ISecretRedactor`, y el payload dice cuántas redacciones
  hubo. La evidencia es el texto redactado: su hash cuadra con lo que ven los demás participantes y el juez.

**D-11 · Texto del criterio F5-1.**

- El texto actual («los seis modos funcionan con participantes de proveedores distintos») choca con ADR-0006 y ADR-0014
  cuando solo hay dos vendors (D-13).
- El prompt maestro pide que la configuración **permita** participantes de proveedores distintos.
- Texto propuesto para [08](08-plan-y-criterios-de-aceptacion.md), que actualiza el lead según la decisión 1 de la §12:

> F5-1 · Los seis modos funcionan, y su configuración admite participantes de proveedores distintos: por defecto, con el
> juez del otro vendor; mezclando vendors, con una dispensa registrada (jurado cruzado o juez del vendor que no produjo el
> trabajo) · Tests del coordinador + una demostración real por modo (con tu OK).

### 4.3 Independencia del juez con dos vendors

**D-12 · Dos reglas, y una no se dispensa.**

- **Regla P, de los productores** (ADR-0014). El juez nunca comparte vendor con quien produjo el **objeto** que se juzga,
  cuando lo hay:
  - en una ejecución, el orquestador y los ejecutores (`RunLog.ProducerVendors`);
  - en una auditoría de proyecto, los productores del registro y los autores que declares (D-28).

  **No se dispensa.**
- **Regla Q, de los participantes** (ADR-0006, decisión 1). El juez no comparte vendor con ningún participante. En los modos
  de elección, las contribuciones son el trabajo que se juzga, así que esta regla es allí la de los productores. Se puede
  dispensar por vendor, de forma explícita: la dispensa va en la cabecera, en `DebateOpened` y en tu aprobación, y la
  tarjeta del veredicto la enseña.
- **`ExcludedVendors` del dossier** = P ∪ (vendors de los participantes − dispensados). Lo usan el validador
  (`JudgeNotIndependent`) y el coordinador, que no lanza un juez excluido, igual que la revisión de la Fase 3.

**D-13 · Las tres disposiciones del juicio.** `DebateJudgingRules.Possible` (dominio, pura) calcula cuáles caben con el
modo, los participantes, los productores y los jueces de la plantilla.

| Disposición | Cuándo | Jueces | Dispensa |
|---|---|---|---|
| **Juez independiente**, la de por defecto | Hay un vendor que no está en P ni entre los participantes | Uno | Ninguna: ADR-0006 tal cual |
| **Jurado cruzado** | Un modo de elección con participantes de los dos vendors | Uno de cada vendor | Q para los dos vendors, con desempate humano |
| **Juez no productor** | Un modo de revisión con algún participante del vendor que no produjo el trabajo | Uno, del vendor que no lo produjo | Q para ese vendor |

Opciones para la decisión de fondo (la 1 de la §12):

| Opción | Ventajas | Inconvenientes |
|---|---|---|
| **A. Por defecto, participantes de un vendor con modelos distintos y juez del otro; para mezclar, dispensa registrada (jurado cruzado o juez no productor)**, recomendada | ADR-0006 y ADR-0014 no cambian en el caso normal; un juez por debate; se puede mezclar, pero nunca a escondidas | Un debate por defecto no enfrenta a Claude con Codex. El jurado cruzado añade un juez y una regla de combinación |
| B. Mezclar con un solo juez de uno de los dos vendors, en una sesión aislada y a ciegas | Barata | El juez comparte vendor con un participante y decide solo. El sesgo de autopreferencia de los jueces LLM está documentado (§3.3), y el validador no lo frena en los criterios de valoración |
| C. No mezclar nunca | La más simple | F5-1 deja de cumplirse, también con el texto de D-11 |
| D. Un tercer vendor: Gemini con API key | Independencia completa con participantes mezclados | Cuesta dinero por uso, y el nivel gratuito puede usar tus prompts. Gemini en modo headless no tiene salida estructurada (F2-3). ADR-0009 lo aparca |

Recomendación: **A**.

- Es la única opción que no cambia una decisión que ya aprobaste y que no se apoya en un mecanismo sin medir.
- **El jurado cruzado no recupera la independencia:** es una dispensa con desempate humano. Si los dos jueces coinciden, la
  decisión queda, con la dispensa a la vista; si no, decides tú.
- ADR-0027 lo escribe como adenda de ADR-0006 para los debates.

**D-14 · El jurado cruzado.**

- Un solo dossier (una sola cabeza del registro, que repiten los dos), con las propuestas en orden inverso para cada juez:
  mitiga el sesgo de posición (§3.3).
- Los dos jueces corren a la vez, en sesiones nuevas. Cada veredicto se valida por su cuenta y tiene su corrección.
- Los dos veredictos tienen que repetir la misma cabeza del registro (`DecisionLogHeadHash`, la del dossier compartido) y el
  mismo conjunto de requisitos: solo así hablan de lo mismo.
- La combinación (`JudgingPanel.Combine`, dominio):
  - los dos son válidos, repiten la misma cabeza y coinciden en la decisión y en la propuesta elegida: esa decisión, con la
    menor de las dos confianzas;
  - dos cabezas distintas son una pareja inválida, nunca un acuerdo;
  - cualquier otra cosa: «no resuelto», con intervención humana.
- Los dos se guardan tal cual y ninguno sustituye al otro. La tarjeta los enseña lado a lado.

**D-15 · A ciegas.**

- El dossier del juez de un debate nombra a cada participante por su etiqueta y su papel (`P1 · Proponente`), nunca por su
  agente, CLI, vendor o modelo.
- De las entradas del registro solo lleva las del protocolo: la apertura sin vendors, las contribuciones, los cierres de
  ronda y las evidencias. No lleva `AgentRunStarted` ni `AgentRunFinished`.
- El sistema guarda la correspondencia y la tarjeta te la enseña.
- Es «a ciegas» en la medida de lo posible: un modelo puede reconocer el estilo de su propia familia (§3.3). En la
  auditoría de implementación, los productores de la ejecución sí se ven, porque son parte de lo que se juzga.

### 4.4 F5-2 · Ronda 1 en paralelo y sin ver nada de los demás

**D-16 · Turnos sin estado.**

| Opción | Ventajas | Inconvenientes |
|---|---|---|
| **Una sesión nueva y no persistente por turno, con lo publicado en el prompt como datos**, elegida | Lo que supo cada participante está entero en la evidencia de su prompt. No queda ninguna transcripción en disco durante la ronda. Reanudar no depende de los CLIs. Cada intento es una sesión, así que su uso no hay que deduplicarlo | Se reenvía el debate en cada turno: más tokens, acotados por el límite |
| Reanudar la sesión de cada participante (`--resume`, `exec resume`) | Menos tokens por turno | Lo que «recuerda» el participante no está en ninguna evidencia. La sesión queda en el perfil del CLI, al alcance de Codex, que lee todo el disco. El uso acumulado hay que deduplicarlo por sesión, y reanudar tras un reinicio exige que la sesión exista |

- `AgentRunRequest.PersistSession = false` se traduce a `--no-session-persistence` en Claude y a `--ephemeral` en Codex.
  Una petición que reanuda y no persiste se rechaza: son excluyentes.
- En Codex se añade `-c history.persistence=none`, una opción documentada y de texto constante, por si `exec` escribe su
  historial de mensajes (`history.jsonl`): `--ephemeral` solo cubre los ficheros de la sesión, y no consta si `exec` usa ese
  historial.
- El juez guarda su sesión para la corrección (`--resume`), que ya funciona y no ve a nadie más.

**D-17 · La ronda 1, en paralelo y sellada.**

- **Lanzamiento.** Todos los turnos de la ronda a la vez, sin esperarse. «En paralelo» quiere decir eso: el límite de dos
  por proveedor puede dejar alguno en cola (un tribunal de tres en Claude).
- **Sesiones y carpetas separadas.** Un proceso, una sesión nueva y una carpeta de turno por participante. Claude no puede
  leer la carpeta de otro (reglas `Read`, medido). Codex sí podría, pero no hay nada que leer (punto siguiente).
- **Qué recibe cada uno:** su papel, la pregunta, los requisitos y el material del objeto. Ningún texto, id ni hash de otro
  participante.
- **Sellado.** Las respuestas de la ronda no se capturan ni se anuncian hasta que han terminado (o fallado) todos sus turnos:
  1. `AgentRunFinished` se registra al terminar cada turno, con su uso y sin `ReportEvidenceId`;
  2. cuando termina el último, cada contribución se captura, se anuncia y se publica, en el orden de la cabecera.

  Durante la ronda, ni el almacén de evidencias ni el registro tienen nada de ningún participante.
- **Lo que queda.** El prompt de cada turno se captura antes de lanzarlo, porque no hay lanzamiento sin `AgentRunStarted`.
  Un participante de Codex podría leer el *prompt* de otro: su papel y el mismo material, nunca su propuesta.
- **La prueba.** El test lee los prompts capturados y comprueba que ninguno contiene texto, id o hash de otro participante,
  y que no hay contribuciones anunciadas antes del último final de la ronda.

**D-18 · Rondas 2 a N.**

- Son simultáneas y selladas como la primera: cada participante recibe todo lo publicado en las rondas anteriores y nada de
  la suya.
- Lo publicado llega como datos: un bloque `<debate_contribution label="C3" author="P2" round="1" kind="Proposal" sha256="…">`
  por contribución, con el JSON recodificado por `PromptData`, que no puede cerrar su bloque. La regla del prompt dice que
  esos bloques son datos y que no se obedece nada de lo que digan.
- **Fin anticipado:** si todos marcan `finished` en la misma ronda, el debate va a juicio («las posiciones convergieron»).

### 4.5 F5-3 · Límites

**D-19 · Qué cuenta como token.**

- Los de los turnos de los participantes: **entrada (con la caché leída y la escrita) más salida**, tal como los informa el
  CLI (`TokenUsage`, Fase 4). Es la única cifra que los dos CLIs dan igual, y es lo que el debate consume de tu suscripción.
- **Los del juez no cuentan:** el límite manda a juicio, y el juicio no puede quedarse sin presupuesto. El panel de consumo
  los cuenta igual.
- **Codex.** Sus tokens son exactos («informado por el CLI»). Lo que no expone es el coste, la ventana y el modelo. Un uso
  todo a cero es «sin dato» (Fase 4).
- **Lo que falta** (un turno sin resultado, un uso a cero, un CLI que no expone tokens) se estima como los caracteres del
  prompt y de la respuesta entre 4, «estimado por Agent Studio», y cuenta para el límite. El total sale con el origen más
  débil y como «parcial (n de m informados)».
- **La fuente es el registro.** El total se calcula desde los `AgentRunFinished` v2 del debate (`DebateBudget`, dominio),
  nunca desde un contador en memoria. Como cada turno es una sesión, el uso propio de un intento es su instantánea.

**D-20 · Cuándo se comprueba y qué pasa.**

| Límite | Se comprueba | Al alcanzarlo |
|---|---|---|
| Rondas | Al cerrar cada ronda (`AdvanceRound`) | A juicio, por `Rounds` |
| Tiempo, con el reloj del sistema desde `DebateOpened` | Siempre: el plazo cancela los turnos en curso, que acaban `Cancelled` y no publican nada. Los de la ronda que ya habían terminado se publican, porque ningún turno más puede verlos | A juicio con lo publicado, por `Duration` |
| Tokens | Al cerrar cada ronda y antes de lanzar la siguiente: no se lanza una ronda cuya estimación (la media por turno de la anterior, por el número de participantes) pasaría del límite | A juicio, por `Tokens`, diciendo si lo disparó una estimación |
| Ejecuciones (`MaxAgentRuns`) | Antes de cada lanzamiento, reintentos incluidos | A juicio; o espera, si falta alguna propuesta (D-22) |

- El dominio gana `DebateJudgmentReason` (`Requested`, `Converged`, `Limit`, `TurnMissing`, `Tribunal`), que queda en
  `DebateRoundClosed`.
- `Submit` ya rechaza una contribución fechada después del plazo. La fecha de una contribución es la de su respuesta (la del
  `AgentRunFinished` de su turno, que repite `DebateContributionRecorded`), no la de su publicación al cerrar la ronda. Así
  el coordinador nunca publica una contribución fuera de plazo, y la reproducción da lo mismo.

**D-21 · Límites y estimación por defecto.**

- 4 rondas, 30 minutos y 400 000 tokens, los de docs/07, y `MaxAgentRuns` = participantes × rondas + 2 reintentos.
- Con lo medido (§3), un turno de Claude con haiku ronda los 40 000 a 160 000 tokens, según cuánto lea, y uno de Codex los
  20 000 a 80 000. Con un participante de cada uno, 400 000 dan para unas tres rondas. Es la decisión 5 de la §12.
- La estimación que enseña «Abrir debate» sale de esas cifras y del número máximo de turnos, «estimado por Agent Studio».

**D-22 · Turnos que fallan.**

- Un turno que falla (CLI ausente, error o respuesta que no valida tras su corrección) se reintenta una vez, con un intento
  nuevo, dentro de `MaxAgentRuns`.
- Si falla otra vez, el debate se detiene y espera a una persona, con tres salidas: «Reintentar el turno», «Pedir juicio con
  lo que hay» o «Abortar». Nunca sigue en silencio con una propuesta de menos.
- **Límite de uso del proveedor:** espera sola hasta 15 minutos si el CLI dijo cuándo se reinicia, como el motor. Si no, se
  detiene.

### 4.6 F5-4 · Veredicto

**D-23 · Lo que se reutiliza del juez de la Fase 3.**

- **`AgentJudge` tal cual:** solo lectura en los dos CLIs, identidad y hora puestas por el sistema y una corrección que
  reanuda la sesión. Gana un `JudgeBrief` en `JudgeLaunch`, con la rúbrica, el esquema y la lectura del veredicto. El brief de
  hoy es el de la revisión de una ejecución, y no cambia.
- **`VerdictValidator`** sin tocar sus reglas de las revisiones, con dos reglas nuevas para los debates (D-25).
- **El registrador de lanzamientos** (`JudgeRunRecorder`) **y el guardado del veredicto** (`VerdictKeeper`) salen de
  `IndependentReview` a clases propias del módulo. Los usan la revisión, el debate y la auditoría. La revisión de una
  ejecución se comporta igual, y sus pruebas lo fijan.
- **Lo propio del juez de un debate:** el dossier (D-24), la rúbrica, el esquema con la resolución y la disposición del
  juicio (D-13). Su lanzamiento se registra con el propósito `DebateJudge` y la tarea `juez-<agente>` (D-44).

**D-24 · El dossier de un debate.** Lo construye `DebateDossierBuilder` desde los almacenes, como el de una ejecución:

- la pregunta literal, leída con el hash anclado, y los requisitos congelados;
- las contribuciones en orden, con sus etiquetas y su `repliesTo`, las citas resueltas y las que no se resolvieron;
- las evidencias del debate: las contribuciones (`ReportedByAgent`, nunca prueba), las citas, las pruebas y tus notas;
- si el objeto es una ejecución, su dossier de la Fase 3 entero: requisitos, plan, tareas, evidencias, `candidateCommit` y
  sus problemas de integridad;
- las entradas del protocolo del debate y la cabeza del registro completo, que el veredicto tiene que repetir;
- los límites, y si uno cortó el debate;
- `ExcludedVendors` según la disposición (D-12), y `systemChecks`: una cabecera que no cuadra, evidencias que no casan
  con su entrada o no se pueden leer y, en una revisión, un veredicto impugnado que no cuadra con su `VerdictIssued` (D-38).

El paquete va a `DossierRoot\<proyecto>\<debate>\<16 primeros de la cabeza>\`, como el de la Fase 3, con `dossier.json`
(`agent-studio/debate-dossier/1`) y `evidence\`. El juez trabaja en la copia del commit, si la hay. Si el JSON pasa de
256 KB, solo lleva entero el texto de las contribuciones y de las citas; del resto, el índice con hashes, y el juez lee los
ficheros del paquete (R8).

**D-25 · La resolución y las reglas nuevas.**

- `Verdict` gana `Resolution`, una propiedad `init` que es nula fuera de los debates: `DebateResolution(ChosenProposal,
  Comparison[])`.
  - En el esquema, `chosenProposal` es un enum con las etiquetas de las propuestas y `"ninguna"`.
  - `comparison` da los puntos fuertes y débiles de cada propuesta.
- `JudgeDossier` gana `Debate` (`init`): el modo y las etiquetas de las propuestas.
- **Dos códigos nuevos**, al final de `VerdictViolationCode`:
  - `DebateResolutionMissing`: en el modo libre y en la revisión de arquitectura, aprobar o pedir cambios exige una
    propuesta elegida;
  - `DebateResolutionUnknown`: la propuesta elegida, o una de la comparación, no es de este debate.
- **Criterios de valoración en los modos de elección.**
  - Allí no hay código que probar: tus criterios son `JudgeAssessment` o `HumanReview`.
  - Un requisito cuyos criterios son todos `JudgeAssessment` puede darse por satisfecho citando contribuciones, que son la
    materia de la valoración: `ApprovedOnAgentClaimsOnly` no se le aplica. La tarjeta lo marca «valoración del juez, no
    verificable por pruebas».
  - En los modos de revisión, y en la impugnación de una ejecución, las reglas son exactamente las de la Fase 3.
  - Un criterio objetivo (test, comando o revisión humana) exige su evidencia objetiva en cualquier modo.
- `SatisfiedWithoutEvidence` sigue: dar algo por satisfecho exige citar al menos una evidencia. Las objeciones pendientes no
  bloquean por sí solas (docs/07).

**D-26 · Cierre y escalado.**

- **`DebateSession.Close` compara la identidad del juez por agente, proveedor y vendor, no por modelo.** `AgentJudge` pone
  en el veredicto el modelo que anuncia la sesión del CLI, que en Claude es el nombre completo y no el alias configurado
  (`haiku`); Codex no anuncia ninguno y queda el configurado. La prueba que exigía el mismo modelo cambia en ese sentido.
- `Close` recibe el resultado de `JudgingPanel.Combine` y los veredictos válidos, uno o dos, y comprueba en **cada uno** el
  juez y el conjunto de requisitos (hoy el dominio solo comprueba un veredicto).
- **Disposición.** Un veredicto válido `Approved`, `ChangesRequested` o `Rejected` es eso. Necesitan a una persona:
  `EscalateToHuman`, `Unresolved`, un veredicto que sigue inválido tras su corrección, uno que no llega y un jurado que
  discrepa.
- **«Decidir yo».** Cuando el debate necesita a una persona, anotas tu resolución: `ApprovalGranted` con la acción
  `debate.human-resolution`, atada a los veredictos y con tu comentario. `DebateClosed` registra la regla «decisión tuya».
  Nada se arregla en silencio.

**D-27 · Pruebas y evidencias durante el debate.**

- **«Ejecutar pruebas»** lanza el perfil de verificación de confianza sobre el commit del objeto, aislado a integridad Low
  (ADR-0017), con el id del debate en el ámbito y `SubjectCommit` = ese commit.
  - Un paso `isolation: none` no se ejecuta en un debate ni en una auditoría: lo haría con tus permisos.
  - No gasta cuota. Su evidencia se anuncia y la ven los participantes de la ronda siguiente y el juez.
- **«Añadir evidencia»** guarda una nota o un fichero tuyo como `ProvidedByHuman`. Es lo que respalda un criterio
  `HumanReview`.
- Ningún participante pide ni ejecuta nada.

### 4.7 F5-5 · Auditar proyecto completo

**D-28 · Qué se audita.**

- **El código:** el commit HEAD del repositorio del proyecto. Los cambios sin commit no entran, y el informe lo dice.
- **Contra qué:** un conjunto de requisitos congelado que eliges: el último del proyecto u otro que congeles para la
  auditoría. Sin ninguno no se audita.
- **Las pruebas:** las del perfil de verificación de confianza del proyecto.
- **Quién produjo el código:** los vendors productores de todas las ejecuciones del registro, más los autores que declares,
  porque el registro no sabe quién escribió lo que no hizo Agent Studio. Quedan en la cabecera y en `ProjectAuditStarted`.

**D-29 · El flujo.** Corrige el diagrama de [07 §9](07-debate-arena-y-juez.md#9-auditar-proyecto-completo).

```mermaid
sequenceDiagram
    autonumber
    actor U as Usuario
    participant A as ProjectAuditService
    participant L as Registro de decisiones
    participant V as Verificador aislado
    participant J as Juez (vendor no productor)
    participant Val as VerdictValidator

    U->>A: Auditar proyecto completo (requisitos, juez, autores y OK de cuota)
    A->>L: Verify (cadena y ancla)
    alt Registro roto
        A-->>U: Informe «no fiable»: integridad rota en la entrada N; comprobaciones sin anclar; sin juez
    else Registro íntegro
        A->>L: ProjectAuditStarted (cabecera anclada)
        A->>V: Build y tests aislados sobre una copia de HEAD
        V-->>L: EvidenceRegistered v2 (BuildLog, TestRun, SubjectCommit = HEAD)
        A->>A: Dossier índice desde los almacenes
        A->>J: Dossier y copia de HEAD (solo lectura, salida estructurada)
        J-->>A: Veredicto
        A->>Val: Validate
        opt Violaciones
            A->>J: Una corrección
            J-->>A: Veredicto corregido
        end
        A->>L: VerdictIssued y ProjectAuditFinished
        A-->>U: Informe de conformidad
    end
```

- **Con el registro roto** no se lanza el juez. Docs/07 decía que la auditoría «continúa marcada como no fiable», pero sin
  `AgentRunStarted` no hay lanzamiento (ADR-0010) y un veredicto no se puede anclar a un registro que no admite entradas. El
  informe enseña la rotura y lo que el sistema comprueba sin escribir. Docs/07 §9 se corrige.
- Los pasos `isolation: none` del perfil no se ejecutan (D-27), y el informe lo dice.
- **El juez sigue la regla P** con los productores de D-28, y P no se dispensa. Si los dos vendors produjeron código, no
  hay juez posible y la auditoría no se lanza: el informe da solo lo que el sistema comprueba sin juez (la verificación y los
  problemas pendientes), y lo dice.

**D-30 · El dossier del proyecto es un índice (R8).**

- Un repositorio no cabe en un prompt: este tiene unas 63 000 líneas de código, 87 000 de pruebas y 14 000 de documentación.
- El dossier lleva:
  - los requisitos con sus criterios;
  - la verificación de HEAD: contadores leídos del TRX, código de salida y commit;
  - de cada ejecución, su inicio, su final, su veredicto y sus alertas;
  - de cada debate, su modo, su pregunta, su resultado y sus impugnaciones;
  - la lista de documentos (`docs/**` y `README`) con su tamaño y su hash;
  - los problemas pendientes (D-31).
- El juez lee el código y los documentos en la copia de HEAD, con sus herramientas de lectura, y cita evidencias del dossier.
  Lo que afirme del código sin una evidencia es una valoración.
- Topes: 200 entradas del registro, resumidas, y 256 KB de JSON. El resto va como índice con hashes.

**D-31 · El informe de conformidad.**

- Decisión, confianza, validación, integridad del registro y si es fiable (`AuditReport.IsTrustworthy`).
- El commit auditado y los cambios sin commit.
- Requisitos satisfechos, incumplidos y no verificables, con sus evidencias.
- Hallazgos, riesgos, acciones correctivas, objeciones pendientes y la disposición del juez (independiente o con dispensa).
- **Problemas pendientes**, que el sistema lista sin juez:
  - ejecuciones fallidas, interrumpidas o que esperan a una persona;
  - revisiones que necesitan a una persona;
  - debates sin resolver e impugnaciones abiertas;
  - peticiones de replanificación sin atender;
  - propuestas de memoria sin revisar y tareas pendientes de la memoria derivada.
- Botones: «Impugnar veredicto», «Crear tareas correctivas» (D-42) y «Volver a auditar».

**D-32 · La demostración sobre Agent Studio.**

- Este repositorio no tiene `.agentstudio/agentstudio.db`: abrirlo en la app lo crea (excluido de git), con el registro
  vacío.
- Antes de la auditoría, y sin cuota, hacen falta un conjunto de requisitos congelado y un perfil de verificación guardado.
- **Riesgo que se mide sin cuota.** La suite de este repositorio (5 354 pruebas) nunca se ha ejecutado a integridad Low.
  Pruebas de ConPTY, Job Objects, uniones o WebView2 pueden fallar ahí por el entorno, y la auditoría lo vería como pruebas que
  fallan. Se mide antes con el verificador aislado. Si falla por el entorno, el perfil de la auditoría filtra esas categorías
  (`--filter-not-trait`) y el informe lo dice.
- **Autoría.** El código lo escribieron sesiones de Claude Code fuera de Agent Studio, que el registro no conoce. Se declara
  Anthropic, y juzga Codex.
- El coste va en la decisión 4 de la §12.

### 4.8 F5-6 · Auditoría del orquestador

**D-33 · Las siete comprobaciones.** `OrchestratorAudit` recorre las entradas de una ejecución del registro verificado, su
plan y su cabecera, comprobados por sus hashes. Está en el módulo Debate, como funciones puras sobre los payloads de
Application: el dominio no los conoce.

| Comprobación | Fuente | Hallazgo | Gravedad |
|---|---|---|---|
| Tareas asignadas a agentes con el rol y los permisos adecuados | `TaskAssigned`, `TaskCreated` y la plantilla de la cabecera | `orchestrator.assignment` | Alta |
| Dependencias respetadas | El orden de `TaskStatusChanged(→Running)` frente al grafo del plan y a `ConditionEvaluated` | `orchestrator.dependency-order` | Alta |
| Instrucciones completas | `InstructionsIssued` antes de cada `AgentRunStarted` de un ejecutor, con los `Covers` y el `WriteScope` del plan; su prompt, leído con el hash anclado, lleva el objetivo y el texto literal de los requisitos que cubre | `orchestrator.instructions` | Media |
| Revisiones obligatorias no omitidas | La `ReviewPolicy` de los ejecutores frente a las tareas `Review`, `ReviewRequested` y `VerdictIssued` | `orchestrator.review-omitted` | Alta |
| Permisos respetados | Las rutas cambiadas del registro del intento frente al `WriteScope`; diffs en tareas de solo lectura; `PermissionDenied` | `orchestrator.permissions` | Alta |
| Aprobaciones solicitadas | Un `ApprovalGranted` atado antes del plan, de la entrega y de un borrado con cambios | `orchestrator.approval-missing` | Crítica |
| Integración correcta | `IntegrationPerformed` seguido de build y tests del commit resultante, capturados por el sistema y en verde, antes de que nada dependa de él | `orchestrator.integration-unverified` | Alta |
| Además | Los problemas de `RunStateReplayer`, y un `RunFinished(Succeeded)` con tareas sin completar | `orchestrator.replay` y `orchestrator.premature-finish` | Alta |

**D-34 · Cuándo corre y qué escribe.**

- Es determinista y no gasta cuota. Corre solo al terminar cada ejecución, como la derivación de la memoria, y cuando lo
  pides desde el monitor. Si la decisión 6 de la §12 lo deja bajo demanda, solo entonces.
- Primero verifica el registro. Si no verifica, no escribe nada: el informe es la propia verificación.
- Por cada hallazgo nuevo deja un `AuditAlertRaised` (actor `Auditor`, id `auditor-orquestador`, con el código del
  hallazgo). Es idempotente: un hallazgo ya anotado con el mismo código y mensaje no se repite.
- Con algún hallazgo de gravedad alta o crítica, deja un `ReplanRequested`.
- Nunca corrige, borra ni reescribe nada: el registro es append-only.

**D-35 · Pedir replanificar es proponer una ejecución de seguimiento.**

- No hay replanificación a mitad de una ejecución (ADR-0013). El monitor enseña la petición con sus hallazgos y el botón
  «Preparar ejecución de seguimiento»: pone en el lienzo un plan vacío que sigue a esa ejecución, y la petición original con
  los hallazgos como datos. Planificas y apruebas tú.
- Con la decisión 6 de la §12, una petición sin atender bloquea la entrega de esa ejecución hasta que la descartas
  (`ApprovalGranted`, acción `replan.dismiss`) o haces la de seguimiento.

**D-36 · El modo «Revisión del orquestador».**

- Sus requisitos son fijos y versionados, `orq-1` a `orq-7`, uno por comprobación, obligatorios y con el método
  `JudgeAssessment`.
- El informe del auditor de esa ejecución se captura como `Document`/`CapturedBySystem`: es evidencia objetiva que el juez
  puede citar, junto con las entradas del registro.
- Un veredicto válido `ChangesRequested` o `Rejected` deja un `ReplanRequested` (actor: el juez) con sus acciones
  correctivas, que sigue D-35.

**D-37 · «Registros manipulados a propósito».** Son dos cosas distintas, y las dos se prueban:

- **Una cadena rota:** una fila editada saltándose los triggers, o un final cortado. `Verify` lo detecta. El auditor no
  escribe nada y su resultado es la verificación.
- **Una cadena válida que incumple el protocolo,** escrita con el registrador real: una tarea asignada al juez, un ejecutor
  que arranca antes que su dependencia, una revisión que falta, una entrega sin aprobación o una integración sin pruebas. Hay
  una prueba por comprobación, y un registro limpio que no da ningún hallazgo.

### 4.9 F5-7 · Impugnar un veredicto

**D-38 · Qué abre.**

- «Impugnar» está en cualquier veredicto guardado, de una ejecución, de un debate o de una auditoría, válido o no.
- Escribes tus razones. Se guardan como `HumanNote`/`ProvidedByHuman`, y `VerdictChallenged` las ancla con el `RunId` del
  veredicto impugnado y el id del debate de revisión.
- El debate de revisión es **adversarial**: el proponente defiende el veredicto y el crítico defiende tu impugnación; su
  ronda 1 parte de tus razones.
- Su material es el dossier del objeto original, reconstruido desde los almacenes; el veredicto impugnado y sus incidencias,
  como `AgentStatement` (es lo que dijo un juez); y tus razones. Sus requisitos son los del veredicto impugnado.
- El veredicto impugnado se lee del almacén y se compara con su `VerdictIssued` (decisión, validez, incidencias y los dos
  hashes), porque ese payload no ancla el texto entero. Si no cuadra, es un problema de integridad del dossier de la revisión
  y ninguna aprobación pasa el validador.

**D-39 · Quién juzga la revisión.**

- **Hereda los `ExcludedVendors` del objeto que impugna:** cambia el juez, no quién puede juzgar.
- El juez no puede ser el agente que emitió el veredicto impugnado si la plantilla tiene otro elegible (por defecto,
  `juez-revision`: Codex con otro modelo). Si no lo tiene, es el mismo agente en una sesión nueva, y la tarjeta lo dice.
- El vendor del juez original **no** se añade a los excluidos: con dos vendors, nadie podría juzgar una ejecución de Claude.
- Ejemplos:
  - el veredicto sobre una ejecución que hizo Claude se revisa otra vez con Codex, en una sesión nueva y, a ser posible, con
    otro modelo; los participantes pueden ser de los dos vendors, con la dispensa Q registrada;
  - el de un debate con jurado cruzado se revisa con jurado cruzado, en sesiones nuevas.
- Se descarta juzgar con Claude la impugnación de un veredicto de Codex sobre una ejecución de Claude: Claude juzgaría el
  trabajo de su propio vendor (ADR-0014).

**D-40 · Qué cambia.**

- El veredicto de la revisión se guarda con el `RunId` del original y `Supersedes` = el impugnado, y se anuncia con
  `VerdictIssued` en ese mismo `RunId`. La cadena de veredictos de una ejecución sigue siendo una sola.
- La entrega (`DeliveryService.CheckAsync`) cambia en tres puntos:
  - una impugnación sin revisión cerrada, o sin tu decisión, la bloquea;
  - una revisión que retira una aprobación la bloquea;
  - una revisión que da la vuelta hacia una aprobación (válida y del mismo commit) solo cuenta si la aceptas expresamente
    (`ApprovalGranted`, acción `verdict.revision.accept`, atada al veredicto). Nunca entrega sola.

  Hoy cualquier sustituto bloquea la entrega; con esto, uno que confirma la aprobación, y que tú aceptas, la deja pasar.
- En un debate o una auditoría, el resultado vigente pasa a ser el de la revisión, y el original queda a la vista como
  «impugnado».

### 4.10 Pantallas

**D-41 · «Debates», en la barra de actividad.** Abre un documento (`Ctrl+Shift+D`) con tres partes.

- **Historial.** Los debates del proyecto, de reproducir el registro: fecha, modo, pregunta, fase o resultado, decisión y si
  se impugnó. Se filtra por modo. Uno interrumpido ofrece «Reanudar».
- **Nuevo debate.**
  - Modo, con los papeles que exige; pregunta; criterios (el editor de requisitos, con los métodos que admite el modo); y
    objeto: ninguno, HEAD o una ejecución.
  - Participantes de la plantilla con su papel, y la disposición del juicio que calcula `DebateJudgingRules`, explicada. Si
    hay dispensa, va en color de aviso.
  - Límites, estimación y «Abrir debate» (D-06).
- **La sala**, el boceto de [03 §4](03-pantallas-y-navegacion.md#4-debate-arena):
  - cabecera con el modo, la fase, la ronda, el tiempo y los tokens con su origen («~» si hay estimaciones);
  - participantes con su etiqueta, papel, agente y CLI;
  - contribuciones por ronda, con su réplica y sus citas, que abren la evidencia; las citas no resueltas, en color de aviso;
  - una ronda en curso se ve como «Ronda 2: 2 turnos en curso, sellados hasta que terminen todos», sin texto;
  - acciones: «Pedir juicio ahora», «Ejecutar pruebas» (solo con commit y perfil), «Añadir evidencia», «Reintentar turno»
    (D-22) y «Abortar».
- **Al cerrar,** la tarjeta del veredicto (D-42).

**D-42 · La tarjeta del veredicto,** común a ejecuciones, debates y auditorías ([03 §6](03-pantallas-y-navegacion.md#6-auditoría-y-veredictos-del-juez)):

- decisión, propuesta elegida, confianza, validación con sus incidencias, corrección, disposición del juicio y dispensas;
- en un jurado, los dos veredictos lado a lado;
- la correspondencia de las etiquetas con su agente, CLI y modelo, que el juez no vio;
- «valoración del juez, no verificable por pruebas» donde toque (D-25);
- botones: «Impugnar», «Decidir yo» (cuando necesita a una persona) y «Crear tareas correctivas». Este último abre el
  orquestador con una petición nueva que cita las acciones correctivas como datos; nada se planifica ni se ejecuta sin ti.

**D-43 · «Auditar proyecto completo» y el monitor.**

- El botón de la barra superior (`Ctrl+Shift+A`) abre la preparación de la auditoría (requisitos, juez, autores declarados,
  estimación y OK) y después el documento del informe (D-31).
- El monitor del orquestador enseña tras cada ejecución el resultado del auditor (alertas y petición de replanificación,
  D-35), «Impugnar» en el veredicto de la revisión y, si la entrega está bloqueada, por qué.

### 4.11 Consumo, bitácora y memoria

**D-44 · Consumo.**

- `AgentRunPurpose` gana `DebateParticipant`, `DebateJudge` y `ProjectAudit`, al final.
- **Cada turno tiene su propia clave** (ejecución, tarea, intento): `RunId` = el debate, tarea = `r<ronda>-<agente>` y su
  intento. El juez lleva `juez-<agente>`, con los intentos 1 y 2 (la corrección). Sin esto, dos turnos en paralelo del mismo
  debate cerrarían el `AgentRunStarted` equivocado en la proyección.
- La proyección de la Fase 4 no cambia: agrupa por ejecución (los `deb-…` y `aud-…` salen como tales), tarea y propósito. El
  panel solo gana las etiquetas en castellano de los propósitos nuevos.

**D-45 · Bitácora.** Cada turno y cada juez pasan por `JournalingAgentProvider`, como todo. `AgentRunRequest` gana
`DebateId`, que llega a `PromptJournalEntry.DebateId` (hoy sin uso), y las etiquetas `#debate` o `#auditoria`.

**D-46 · Memoria.**

- **`DebateSummary`, del sistema,** se deriva de cada `DebateClosed`: modo, pregunta (la evidencia anclada, que escribiste
  tú), resultado, propuesta elegida y veredictos. No se cita el texto del juez: su respuesta no se anuncia con
  `EvidenceRegistered`, como en la Fase 4.
- La impugnación y su resultado, y los hallazgos del auditor, entran como `Decision` y `KnownError` del sistema.
- Los temas que hoy dicen «ejecución» para un `RunId` dicen «debate» o «auditoría» cuando toca.
- La regla `agent.memory-proposals-not-available` se queda; cambia su texto, que decía «hasta la Fase 5».

---

## 5. Componentes y contratos

Todos respetan [ADR-0002](adr/0002-capas-y-puertos.md): los módulos solo conocen `Application`, y `Desktop` compone. El
debate, el juez y los dos auditores viven en el módulo `Debate`, que no puede llamar al orquestador: lo que necesita del
motor (la entrega, la actividad) llega por puertos.

| Componente | Proyecto | Responsabilidad |
|---|---|---|
| `DebateSession` (cambios), `DebateJudgingRules`, `JudgingArrangement`, `JudgingPanel`, `DebateStateReplayer` y `DebateBudget`; `Verdict.Resolution`, `JudgeDossier.Debate` y dos reglas del validador | **Domain** (`Debate/`, `Judging/`) | Reglas puras: quién juzga, cómo se combinan dos veredictos, reproducción y presupuesto |
| Payloads de la Fase 5 y propósitos nuevos; contratos del coordinador, de la auditoría y de la impugnación; `IDebateRegistry` e `IProjectAuditRegistry`; `DebateHeaderHash`; `IProjectActivity`; `PromptData` público; `AgentRunObservations` | **Application** | Puertos y servicios sin E/S propia |
| `DebateCoordinator`, `DebateRun`, prompt y esquema de la contribución, citas, material, copia del commit y registrador de turnos | **Debate** (`Arena/`) | El protocolo del debate |
| `JudgeBrief`, `JudgeRunRecorder` y `VerdictKeeper`; `DebateDossierBuilder`, `DebateJudgePrompt`, `DebateVerdictSchema` y `DebateJudgment` | **Debate** (`Judging/`) | El juicio de un debate, sobre el juez de la Fase 3 |
| `ProjectAuditService`, `ProjectDossierBuilder`, `OrchestratorAudit`, `OrchestratorAuditService` y `VerdictChallengeService` | **Debate** (`Audit/`, `Challenges/`) | Auditar el proyecto, auditar al orquestador e impugnar |
| `SqliteDebateRegistry`, `SqliteProjectAuditRegistry`, `VerdictJson` con la resolución y la migración 0005 | **Infrastructure** | Cabeceras inmutables y veredictos |
| `--no-session-persistence` y `--ephemeral` | **Providers** | Turnos sin estado |
| `DeliveryService` (impugnación y replanificación) y la actividad del orquestador sobre `IProjectActivity` | **Orchestration** | La entrega respeta la impugnación y al auditor |
| Derivación de `DebateSummary`, de la impugnación y del auditor | **Memory** | Memoria del sistema |
| Sala de debate, historial, tarjeta del veredicto, auditoría e informe; monitor | **Desktop** | ViewModels y vistas |

### 5.1 Dominio

```csharp
// Debate/DebateJudging.cs (new): who may judge a debate and how two verdicts combine. Pure.
public enum DebateSubjectKind { None, Commit, Run, Verdict }
public enum DebateJudgingKind { IndependentJudge, CrossVendorPanel, NonProducerJudge }
public sealed record JudgingArrangement(
    DebateJudgingKind Kind,
    IReadOnlyList<JudgeIdentity> Judges,               // one, or one per vendor in a panel
    IReadOnlySet<VendorId> ProducerVendors,            // rule P: never waived
    IReadOnlySet<VendorId> WaivedParticipantVendors)   // rule Q: waived explicitly, recorded
{
    public IReadOnlySet<VendorId> ExcludedVendors(IReadOnlyList<DebateParticipant> participants); // P ∪ (participants − waived)
}
public static class DebateJudgingRules
{
    public static bool IsChoice(DebateMode mode, DebateSubjectKind subject);  // free, adversarial, architecture, tribunal; never with a run
    public static IReadOnlyList<JudgingArrangement> Possible(DebateMode mode, DebateSubjectKind subject,
        IReadOnlyList<DebateParticipant> participants, IReadOnlySet<VendorId> producers, IReadOnlyList<JudgeIdentity> candidates);
    // Stable codes: debate.judge-producer, debate.judge-participant-not-waived, debate.panel-shape, debate.panel-in-review...
    public static IReadOnlyList<string> Violations(JudgingArrangement arrangement, DebateMode mode, DebateSubjectKind subject,
        IReadOnlyList<DebateParticipant> participants);
}
public sealed record PanelOutcome(DebateOutcome Outcome, VerdictDecision? Decision, string? ChosenProposal, string Rule); // single | agree | disagree | invalid
public static class JudgingPanel
{
    public static PanelOutcome Combine(JudgingArrangement arrangement, IReadOnlyList<(Verdict Verdict, bool Valid)> verdicts);
}

// Debate/DebateSession.cs (changed)
public enum DebateJudgmentReason { Requested, Converged, Limit, TurnMissing, Tribunal }
// Open(..., JudgingArrangement judging, ...) checks the arrangement with DebateJudgingRules. The Phase 0 overload (one
// JudgeIdentity, requireIndependentJudge) stays: IndependentJudge, or every participant vendor waived when false.
// RequestJudgment(DebateJudgmentReason reason, DebateLimitReached limit = None): early judgment, also the cut a projection
// decides before a round (D-20), which AdvanceRound alone would not see.
// Close(PanelOutcome outcome, IReadOnlyList<Verdict> verdicts): every verdict comes from a judge of the arrangement,
// compared by agent, provider and vendor (never by model), and is about the debate's requirement set. In a panel both
// verdicts echo the same DecisionLogHeadHash; Combine reports a pair with different heads as invalid, never as agreement.

// Debate/DebateStateReplayer.cs (new), like RunStateReplayer: the public API of DebateSession, nothing else.
public sealed record DebateHeader(DebateId Id, DebateMode Mode, DebateSubjectKind Subject, string Question, FrozenRequirementSet Requirements,
    IReadOnlyList<DebateParticipant> Participants, JudgingArrangement Judging, DebateLimits Limits, DateTimeOffset StartedAt);
public abstract record DebateEvent(DateTimeOffset At);
public sealed record ContributionPublished(DateTimeOffset At, DebateContribution Contribution) : DebateEvent(At);
public sealed record RoundClosed(DateTimeOffset At, int Round, long TokensUsed, DebateJudgmentReason? Judgment) : DebateEvent(At);
public sealed record DebateEnded(DateTimeOffset At, PanelOutcome Outcome, IReadOnlyList<Verdict> Verdicts) : DebateEvent(At);
public sealed record DebateAborted(DateTimeOffset At, string Reason) : DebateEvent(At);
public sealed record DebateReplay(DebateSession Session, IReadOnlyList<string> Problems);
public static class DebateStateReplayer { public static DebateReplay Replay(DebateHeader header, IEnumerable<DebateEvent> events); }

// Debate/DebateBudget.cs (new): the tokens of a debate from its attempts, with their origins (D-19).
public sealed record TurnUsage(string Turn, int Attempt, MetricValue<long> InputTokens, MetricValue<long> OutputTokens, long Estimate);
public static class DebateBudget
{
    public static MetricAggregate<long> Used(IReadOnlyList<TurnUsage> turns);   // a missing figure counts as its estimate
    public static bool WouldExceed(DebateLimits limits, MetricAggregate<long> used, long nextRoundEstimate);
}

// Judging/Verdict.cs
public sealed record ProposalComparison(string Proposal, string Strengths, string Weaknesses);
public sealed record DebateResolution(string? ChosenProposal, IReadOnlyList<ProposalComparison> Comparison);
// Verdict gains: public DebateResolution? Resolution { get; init; }            (null outside debates)
public sealed record DebateDossierContext(DebateMode Mode, IReadOnlyList<string> Proposals, bool Choice);
// JudgeDossier gains: public DebateDossierContext? Debate { get; init; }

// Judging/VerdictValidator.cs: DebateResolutionMissing and DebateResolutionUnknown (appended). In a dossier whose Debate
// is a choice, a requirement whose criteria are all JudgeAssessment is exempt from ApprovedOnAgentClaimsOnly.

// Decisions/DecisionLog.cs: DecisionKind gains DebateOpened, DebateContributionRecorded, DebateRoundClosed, DebateClosed,
// VerdictChallenged, ProjectAuditStarted and ProjectAuditFinished, appended: no stored hash changes.
```

### 5.2 Payloads (Application)

```csharp
// Decisions/DecisionPayloads.cs: AgentRunPurpose gains DebateParticipant, DebateJudge and ProjectAudit (appended).
// Decisions/DebatePayloads.cs (new). RunId = the debate (deb-…) or the audit (aud-…), except where noted. V = 1.
public sealed record DebateParticipantEntry(AgentId Agent, DebateRole Role, ProviderId Provider, VendorId Vendor, string? Model, string Label);
public sealed record DebateJudgeEntry(AgentId Agent, ProviderId Provider, VendorId Vendor, string? Model);
public sealed record DebateOpenedPayload(
    DebateMode Mode, string HeaderHash, string ApprovedProposalHash, string RequirementSetHash, EvidenceId Question, string QuestionSha256,
    DebateSubjectKind Subject, string? SubjectRef, string? SnapshotCommit,
    IReadOnlyList<DebateParticipantEntry> Participants, IReadOnlyList<DebateJudgeEntry> Judges, DebateJudgingKind Judging,
    IReadOnlyList<VendorId> ProducerVendors, IReadOnlyList<VendorId> WaivedParticipantVendors,
    int MaxRounds, long MaxDurationSeconds, long? MaxTotalTokens, int MaxAgentRuns, string? Challenges) : DecisionPayload;
public sealed record DebateContributionRecordedPayload(
    int Index, int Round, AgentId Author, ContributionKind Kind, int? RepliesTo, EvidenceId Evidence, string Sha256,
    IReadOnlyList<EvidenceId> Cites, IReadOnlyList<string> UnresolvedCitations, int Redactions, bool Finished,
    string Turn, int Attempt, DateTimeOffset AnsweredAt) : DecisionPayload;   // AnsweredAt: the end of its turn, not the publication
public sealed record DebateRoundClosedPayload(int Round, DebatePhase Next, DebateJudgmentReason? Judgment, DebateLimitReached Limit,
    MetricSnapshot TokensUsed, bool TokensIncludeEstimates, long ElapsedSeconds, string Reason) : DecisionPayload;
public sealed record DebateClosedPayload(DebateOutcome Outcome, IReadOnlyList<string> Verdicts, string Rule, string? ChosenProposal, string Reason) : DecisionPayload;
public sealed record VerdictChallengedPayload(string Verdict, string ChallengedBy, EvidenceId Reasons, string ReasonsSha256,
    string RevisionDebate) : DecisionPayload;                       // RunId = the run, debate or audit of the challenged verdict
public sealed record ReplanRequestedPayload(IReadOnlyList<string> Findings, IReadOnlyList<TaskId> Tasks, string Message,
    string? Verdict) : DecisionPayload;                             // RunId = the run
public sealed record ProjectAuditStartedPayload(string HeaderHash, AuditScope Scope, string? HeadCommit, bool UncommittedChanges,
    string RequirementSetHash, string VerificationProfileHash, DebateJudgeEntry Judge, IReadOnlyList<VendorId> ProducerVendors,
    IReadOnlyList<VendorId> DeclaredAuthors) : DecisionPayload;
public sealed record ProjectAuditFinishedPayload(IReadOnlyList<string> Verdicts, ReviewDisposition Disposition, bool Trustworthy,
    string Summary) : DecisionPayload;
// DecisionPayloadSerializer maps the seven new kinds and ReplanRequested; nothing else changes.
```

### 5.3 Debate (Application/Coordination, sustituye al contrato de la Fase 0)

```csharp
public sealed record DebateSubject(DebateSubjectKind Kind, string? Reference);   // None; Commit (HEAD on open); Run (run id); Verdict (challenges)
public sealed record DebateParticipantRequest(AgentId Agent, DebateRole Role);
public sealed record DebateRequest(ProjectContext Project, DebateMode Mode, string Question, FrozenRequirementSet Requirements,
    DebateSubject Subject, IReadOnlyList<DebateParticipantRequest> Participants, IReadOnlyList<AgentId> Judges,
    DebateJudgingKind Judging, DebateLimits Limits, int? MaxAgentRuns = null);
public sealed record DebateEstimate(int MaxAgentRuns, MetricValue<long> Tokens, IReadOnlyList<string> Notes);   // estimated by Agent Studio
public sealed record DebateProposal(DebateRequest Request, JudgingArrangement Judging, IReadOnlyList<string> Problems,
    DebateEstimate Estimate, string HeaderHash, string ApprovalText);            // what «Abrir debate» shows; nothing is written
public interface IDebateCoordinator
{
    Task<DebateProposal> PrepareAsync(DebateRequest request, CancellationToken ct);
    Task<IDebateRun> OpenAsync(DebateProposal proposal, CancellationToken ct);  // IApprovalGate (debate.open), activity, question, header, copy, DebateOpened
    Task<IDebateRun> ResumeAsync(ProjectContext project, RunId debate, CancellationToken ct); // replays, closes open turns, asks again
    Task<IReadOnlyList<DebateSummary>> ListAsync(ProjectContext project, CancellationToken ct);
    Task<DebateTranscript> ReadAsync(ProjectContext project, RunId debate, CancellationToken ct); // text read against the anchored hashes
}
public interface IDebateRun : IAsyncDisposable                                    // the shape of IOrchestrationRun
{
    RunId Id { get; }
    IAsyncEnumerable<DebateProgress> Events { get; }  // turn started/finished, round sealed, contribution published, round closed, judge, verdict, awaiting you, ended
    Task<DebateResult> Completion { get; }
    DebateSnapshot Snapshot();
    Task RequestJudgmentAsync();
    Task RetryTurnAsync(AgentId participant);
    Task AddHumanEvidenceAsync(string title, string text);
    Task RunTestsAsync();
    Task AbortAsync(string reason);
}
public sealed record JudgedVerdict(string Id, Verdict Verdict, IReadOnlyList<VerdictViolation> Violations, bool Corrected);
public sealed record DebateJudgmentResult(IReadOnlyList<JudgedVerdict> Verdicts, PanelOutcome Outcome);
public interface IDebateJudgment   // the judging of the module behind a port, so the coordinator is tested with a double
{
    Task<DebateJudgmentResult> JudgeAsync(ProjectContext project, RunId debate, IProgress<AgentEvent>? progress, CancellationToken ct);
}
// DebateSummary, DebateTranscript, DebateProgress, DebateResult, DebateSnapshot (and ProjectAuditProposal below): read-only
// records whose fields P0 fixes from what the screens of D-41 to D-43 show; all of them come from replaying the log.
```

### 5.4 Auditoría e impugnación (Application/Judging)

```csharp
public enum AuditScope { WholeProject, CurrentPlan, OrchestratorDecisions, Debate }   // Debate appended (verdicts.scope stores the name)
public sealed record ProjectAuditRequest(ProjectContext Project, string RequirementSetHash, AgentId Judge, IReadOnlyList<VendorId> DeclaredAuthors);
public sealed record PendingProblem(string Code, string Source, string Message);
public sealed record AuditReport(   // replaces the Phase 0 record, which nothing implemented
    RunId Audit, AuditScope Scope, string? HeadCommit, bool UncommittedChanges, Verdict Verdict, string? VerdictId,
    IReadOnlyList<VerdictViolation> ValidationIssues, bool Corrected, bool DecisionLogIntact, ReviewDisposition Disposition,
    IReadOnlyList<EvidenceRecord> Verification, IReadOnlyList<PendingProblem> Pending, DateTimeOffset GeneratedAt)
{
    public bool IsTrustworthy => ValidationIssues.Count == 0 && DecisionLogIntact && VerdictId is not null;
}
public interface IProjectAuditService
{
    Task<ProjectAuditProposal> PrepareAsync(ProjectAuditRequest request, CancellationToken ct);   // judge, rule P, profile, estimate; writes nothing
    Task<AuditReport> AuditAsync(ProjectAuditProposal proposal, IProgress<AgentEvent>? progress, CancellationToken ct); // IApprovalGate (audit.run)
}
public sealed record OrchestratorFinding(string Code, Severity Severity, IReadOnlyList<TaskId> Tasks, IReadOnlyList<long> Entries, string Message);
public sealed record OrchestratorAuditReport(RunId Run, DecisionLogVerification Log, IReadOnlyList<OrchestratorFinding> Findings,
    IReadOnlyList<long> Recorded, bool ReplanRequested);
public interface IOrchestratorAuditService { Task<OrchestratorAuditReport> AuditRunAsync(ProjectContext project, RunId run, CancellationToken ct); } // free, idempotent
public sealed record VerdictChallengeRequest(ProjectContext Project, string Verdict, string Reasons, DebateRequest Revision);
public enum ChallengeStatus { None, Pending, Confirmed, Revoked, AwaitingAcceptance, Accepted, DecidedByUser }
public interface IVerdictChallenges
{
    Task<DebateProposal> PrepareAsync(VerdictChallengeRequest request, CancellationToken ct);   // inherits ExcludedVendors, picks another judge
    Task<IDebateRun> ChallengeAsync(DebateProposal proposal, CancellationToken ct);             // VerdictChallenged, then the revision debate
    Task<ChallengeStatus> StatusAsync(ProjectContext project, string verdict, CancellationToken ct);
    Task AcceptRevisionAsync(ProjectContext project, string revisionVerdict, CancellationToken ct); // IApprovalGate (verdict.revision.accept)
}
```

### 5.5 Proveedores, almacenes y actividad (Application)

```csharp
// Providers/AgentProvider.cs: AgentRunRequest gains bool PersistSession = true and string? DebateId = null.
//   PersistSession false: Claude --no-session-persistence, Codex --ephemeral; together with ResumeSessionId it is refused.
// Providers/AgentRunObservations.cs (new): what the events of one launch said (launch time, model of the last step,
//   peak context, compactions), shared by AuxiliaryAgentRunner, JudgeRunRecorder and the debate turns (three copies today).
// Prompts/PromptData.cs: moved from Orchestration.Engine (public now); ExecutorPrompt and PlannerPrompt only change their using.

// Persistence/StoreContracts.cs
public sealed record DebateParticipantDefinition(AgentDefinition Agent, DebateRole Role, string Label);
public sealed record DebateRecord(RunId Id, string ProjectId, DateTimeOffset CreatedAt, DebateMode Mode, string RequirementSetHash,
    EvidenceId Question, DebateSubject Subject, string? SnapshotCommit, IReadOnlyList<DebateParticipantDefinition> Participants,
    IReadOnlyList<AgentDefinition> Judges, DebateJudgingKind Judging, IReadOnlyList<VendorId> ProducerVendors,
    IReadOnlyList<VendorId> WaivedParticipantVendors, DebateLimits Limits, int MaxAgentRuns, string? Challenges);
public interface IDebateRegistry
{
    Task CreateAsync(DebateRecord debate, CancellationToken ct);   // insert only; the same row twice is a no-op, another one is refused
    Task<DebateRecord?> FindAsync(string projectId, RunId id, CancellationToken ct);
    Task<IReadOnlyList<DebateRecord>> ListAsync(string projectId, CancellationToken ct);
}
public sealed record ProjectAuditRecord(RunId Id, string ProjectId, DateTimeOffset CreatedAt, string? HeadCommit, bool UncommittedChanges,
    string RequirementSetHash, EvidenceId Request, VerificationProfile Verification, AgentDefinition Judge,
    IReadOnlyList<VendorId> ProducerVendors, IReadOnlyList<VendorId> DeclaredAuthors);
public interface IProjectAuditRegistry   // same shape as IDebateRegistry
{
    Task CreateAsync(ProjectAuditRecord audit, CancellationToken ct);
    Task<ProjectAuditRecord?> FindAsync(string projectId, RunId id, CancellationToken ct);
    Task<IReadOnlyList<ProjectAuditRecord>> ListAsync(string projectId, CancellationToken ct);
}
// Debate/DebateHeaderHash.cs (new): the canonical SHA-256 of a DebateRecord or a ProjectAuditRecord, length-prefixed fields,
// each agent with the encoding of RosterHash (the RunHeaderHash pattern).

// Orchestration/ProjectActivity.cs (new): one activity with agents per project in this process (D-05).
public enum ProjectActivityKind { Run, Planning, Debate, Audit }
public interface IProjectActivity : IRunActivity
{
    IDisposable? TryEnter(string projectId, ProjectActivityKind kind);   // null when another activity holds the project
    ProjectActivityKind? Current(string projectId);
}
```

---

## 6. Modelo de datos: migración 0005

Dos cabeceras inmutables, con el patrón de `orchestration_runs`. El estado sale del registro.

```sql
-- Agent Studio: Phase 5 (debates and audits, docs/15 section 6). Keys are TEXT unless stated; dates are ISO-8601 'O' text.
--
-- Like orchestration_runs, these rows hold what never changes once written: the header of a debate and of a whole-project
-- audit. Their state is derived by replaying decision_log (ADR-0010, ADR-0026), and DebateOpened / ProjectAuditStarted
-- anchor the canonical hash of each header (header_hash). The debate tables of 0001 (debates, debate_participants,
-- debate_contributions, debate_contribution_evidence) and verdicts.debate_id stay unused: they kept mutable state and the
-- text of the agents outside the evidence store.

CREATE TABLE debate_sessions (
    id                     TEXT PRIMARY KEY,                       -- deb-yyyyMMdd-xxxxxx
    project_id             TEXT NOT NULL REFERENCES projects(id),
    created_at             TEXT NOT NULL,
    mode                   TEXT NOT NULL CHECK (mode IN ('Free', 'Adversarial', 'ArchitectureReview', 'ImplementationAudit', 'OrchestratorReview', 'Tribunal')),
    requirement_set_hash   TEXT NOT NULL REFERENCES requirement_sets(content_hash),
    question_evidence_id   TEXT NOT NULL REFERENCES evidence(id),
    subject_kind           TEXT NOT NULL CHECK (subject_kind IN ('None', 'Commit', 'Run', 'Verdict')),
    subject_ref            TEXT NULL,                              -- the commit, run id or verdict id
    snapshot_commit        TEXT NULL,
    challenges_verdict_id  TEXT NULL REFERENCES verdicts(id),
    header_json            TEXT NOT NULL,                          -- participants and judges with their definitions, judging, waivers, limits
    header_hash            TEXT NOT NULL
);
CREATE INDEX debate_sessions_by_project ON debate_sessions(project_id, created_at);

CREATE TABLE project_audits (
    id                     TEXT PRIMARY KEY,                       -- aud-yyyyMMdd-xxxxxx
    project_id             TEXT NOT NULL REFERENCES projects(id),
    created_at             TEXT NOT NULL,
    head_commit            TEXT NULL,
    requirement_set_hash   TEXT NOT NULL REFERENCES requirement_sets(content_hash),
    request_evidence_id    TEXT NOT NULL REFERENCES evidence(id),
    header_json            TEXT NOT NULL,                          -- verification profile, judge, producers and declared authors
    header_hash            TEXT NOT NULL
);
CREATE INDEX project_audits_by_project ON project_audits(project_id, created_at);

-- Append-only, same pattern as 0001, 0002 and 0004.
CREATE TRIGGER debate_sessions_no_update BEFORE UPDATE ON debate_sessions BEGIN
    SELECT RAISE(ABORT, 'debate_sessions is append-only');
END;
CREATE TRIGGER debate_sessions_no_delete BEFORE DELETE ON debate_sessions BEGIN
    SELECT RAISE(ABORT, 'debate_sessions is append-only');
END;
CREATE TRIGGER debate_sessions_no_replace BEFORE INSERT ON debate_sessions
WHEN EXISTS (SELECT 1 FROM debate_sessions WHERE id = NEW.id) BEGIN
    SELECT RAISE(ABORT, 'debate_sessions is append-only');
END;
CREATE TRIGGER project_audits_no_update BEFORE UPDATE ON project_audits BEGIN
    SELECT RAISE(ABORT, 'project_audits is append-only');
END;
CREATE TRIGGER project_audits_no_delete BEFORE DELETE ON project_audits BEGIN
    SELECT RAISE(ABORT, 'project_audits is append-only');
END;
CREATE TRIGGER project_audits_no_replace BEFORE INSERT ON project_audits
WHEN EXISTS (SELECT 1 FROM project_audits WHERE id = NEW.id) BEGIN
    SELECT RAISE(ABORT, 'project_audits is append-only');
END;
```

- **Orden de escritura.** La pregunta (o la petición de la auditoría) se captura como evidencia, con el id del debate en
  `run_id`; después, la fila de la cabecera; después, `DebateOpened` con su hash. Es el orden de `orchestration_runs`.
- **Veredictos.** No cambia el esquema. Un veredicto de debate lleva `run_id` = el debate, `task_id` = la tarea del juez
  (`juez-<agente>`) y `scope = 'Debate'`; uno de auditoría, `run_id` = la auditoría y `scope = 'WholeProject'`; uno de revisión,
  el `run_id` del original (D-40). `payload_json` gana la resolución, y los veredictos ya guardados se leen igual.
- **Evidencias.** `run_id` = el debate o la auditoría, así que `ListByRunAsync` y `RunLog.RegisteredEvidence` sirven tal cual.
- **Documentación.** [05](05-modelo-de-datos.md) se actualiza en P0: la 0005, y las cuatro tablas de debate y
  `verdicts.debate_id` entre las que no se usan.

---

## 7. Seguridad frente a ADR-0018

**Qué cambia en la superficie.** Hasta ahora, lo que escribía un agente llegaba a otro como datos en un solo sentido: el
informe de una tarea al ejecutor siguiente y al juez. En un debate, los agentes se leen entre sí durante varias rondas, y
hay dos vendors leyendo lo que escribe el otro.

| Superficie nueva | Adversario del punto 1 | Mitigación | Clasificación |
|---|---|---|---|
| Lo que escribe un participante llega al prompt de otro (inyección de agente a agente) | La salida de los agentes | Bloques `<debate_contribution>` delimitados y recodificados (`PromptData`), con la regla de datos; participantes sin escritura, shell, red, MCP ni skills; contribuciones con esquema estricto y topes; sin votos: decide el juez con el validador, y al final tú | Cerrado dentro del modelo. **Residual:** un modelo puede obedecer igualmente un texto de datos, y persuadir es la naturaleza del debate. Un participante inyectado solo puede escribir su contribución |
| Un participante de Codex lee todo el disco: `-s read-only` no confina las lecturas (se vio en la Fase 3), y en Windows el modo `unelevated` no admite rutas de lectura denegadas (§3.2) | Los agentes con sus capacidades por defecto | Durante una ronda no hay nada del debate que leer (sellado y sesiones sin persistencia, D-17); los secretos se redactan antes de que una contribución llegue al otro vendor (D-10); el material es lo mínimo | **Residual de confidencialidad:** lo que lee un agente de solo lectura llega a su vendor (ya pasaba con el juez de la Fase 3) y, por el debate, también al otro. Lo aceptas tú (decisión 7) |
| Que el sandbox de Codex bloquee de verdad una escritura en Windows no se ha ejercitado (ADR-0014), y ahora Codex recibe texto de otro agente | Los agentes, inyectados por otro | La comprobación del repositorio del usuario rodea cada ronda y cada juicio (ADR-0013, decisión 7): una diferencia detiene el debate con una alerta. La copia del commit y las carpetas de turno son de la app y se rehacen desde el commit. **Medición antes de las demostraciones** (WP4): Codex en solo lectura intenta escribir dentro y fuera de su `-C` | **Abierto hasta la medición.** Si escribe, Codex no participa ni juzga hasta resolverlo (decisión 7) |
| Citas: un participante podría pedir que el sistema lea un fichero fuera de la copia (`../../`, rutas absolutas, enlaces) | La salida de los agentes | `CitationResolver`: ruta relativa sin `..` ni `:`, ruta final dentro de la copia, fichero normal con tamaño acotado. La copia la escribe la capa git endurecida, solo con ficheros y carpetas | Cerrado |
| La copia del commit, las carpetas de turno y los paquetes del dossier, en `%LOCALAPPDATA%\AgentStudio` | Un enlace plantado | Se crean con `CreateNew` y sin seguir enlaces, como el paquete del dossier de la Fase 3 | Cerrado. Que otro proceso Medium cambie algo entre la comprobación y el uso es el residual de siempre (punto 2) |
| El contenido del repositorio en la copia (instrucciones plantadas en un fichero que leen los participantes o el juez) | El contenido del repositorio | Lo mismo que en la Fase 3: es dato, todos son de solo lectura y el juez decide con evidencias | Cerrado dentro del modelo, con el residual de siempre |
| «Ejecutar pruebas» y la verificación de la auditoría | El contenido del repositorio | Solo el verificador del sistema, aislado a integridad Low (ADR-0017); los pasos `isolation: none` no se ejecutan en debates ni auditorías; ningún participante lanza nada | Cerrado; las lecturas y la red de Low son el residual de siempre |
| El auditor del orquestador | — | Solo lee el registro verificado y escribe alertas y peticiones (append-only) | Cerrado |
| Una revisión que da la vuelta a un veredicto | La salida del juez | Hacia la aprobación, nunca entrega sin tu aceptación expresa y atada (D-40); hacia el rechazo, bloquea | Cerrado |
| Lo que cada CLI escribe en su perfil (residual 4 de ADR-0018) | — | Los turnos sin persistencia lo reducen (D-16); la corrección del juez sí guarda su sesión | Residual, como estaba |

**Sesgos del juez, que no son seguridad pero se tratan aquí.** Que un juez prefiera las respuestas de su propia familia de
modelos, o la primera propuesta que lee, no ataca un activo protegido, pero puede torcer un veredicto. Lo acotan:

- la disposición por defecto (juez del otro vendor, D-13);
- en un jurado, el orden invertido y el desempate humano (D-14);
- el dossier a ciegas (D-15);
- el validador;
- y la impugnación (F5-7).

---

## 8. Estrategia de pruebas

Mismas reglas que las fases anteriores:

- solo `Assert` de xUnit;
- `using System.IO;` en el proyecto de tests, que es WPF;
- propiedades con `Random` sembrado y sin FsCheck;
- lo que gasta cuota va con `[Trait("Category","ProviderLive")]` y `Assert.SkipUnless(AGENTSTUDIO_PROVIDER_TESTS=="1")`, y
  solo se ejecuta con tu OK;
- los proveedores guionizados (`tests/AgentStudio.Tests/Debate/Fakes`, `Orchestration/Fakes`) sirven también aquí: se
  componen con el `AgentProviderPipeline.Compose` real y una bitácora en memoria.

| Criterio | Pruebas |
|---|---|
| F5-1 | **Coordinador, un escenario por modo**, con participantes en los dos proveedores guionizados (`claude-code` y `codex-cli`) y jueces guionizados: papeles exigidos, material según el objeto, disposición del juicio elegida (independiente, jurado o no productor) con sus dispensas registradas, y el resultado. **`DebateJudgingRules`:** propiedad con unos 200 repartos aleatorios por semilla (nunca un juez en P; Q solo con dispensa; jurado solo en los modos de elección y con un juez por vendor). **Contribución:** esquema portable y de 16 KB como mucho, parseo estricto, citas (ruta de escape, enlace, líneas inexistentes, tamaño) y redacción |
| F5-2 | Turnos retenidos con TCS: todos arrancan antes de que termine ninguno (o esperan en la cola del proveedor), cada uno en su carpeta, con `PersistSession = false` y sin `ResumeSessionId`. **Ningún prompt de la ronda contiene texto, id ni hash de otro participante.** No hay `DebateContributionRecorded` ni evidencia de una contribución antes del último `AgentRunFinished` de la ronda. Las reglas `Read` de un participante de Claude solo nombran su copia y su carpeta |
| F5-3 | Límite de rondas. Plazo con `FakeTimeProvider`: los turnos en curso se cancelan y no publican nada, los ya terminados de la ronda se publican con la fecha de su respuesta, y va a juicio por `Duration`. **Tokens:** sumas exactas desde el uso v2, una cifra que falta se estima y cuenta, la ronda que no se lanza por la proyección y el motivo con «disparado por una estimación». `MaxAgentRuns`. Un debate cortado se reproduce igual que en vivo |
| F5-4 | **Jueces guionizados:** válido a la primera; inválido y corregido; inválido dos veces (necesita una persona); sin veredicto; límite de uso. **Jurado:** coinciden, discrepan en la decisión, en la propuesta elegida, y uno inválido. Identidad: el mismo agente y vendor con otro modelo se acepta; otro vendor, no. Las dos reglas nuevas del validador. La exención de `JudgeAssessment` solo en un dossier de elección; **la revisión de una ejecución no cambia** (sus pruebas pasan igual). El dossier a ciegas no contiene vendor, modelo ni agente de los participantes, y el orden se invierte entre jueces. En vivo: las demostraciones |
| F5-5 | **Servicio con dobles:** registro roto (no se lanza el juez y el informe no es fiable); verificación atada a HEAD; los pasos `none` no se ejecutan; topes del dossier (R8) con un índice sintético grande; la regla P con autores declarados; los dos vendors productores (no se lanza); los problemas pendientes desde un registro preparado. Demostración: el propio repositorio |
| F5-6 | **Registros preparados con el registrador real:** uno que incumple cada comprobación (siete), la reproducción y el final prematuro; uno limpio sin hallazgos; una cadena rota (no escribe nada); idempotencia (dos pasadas, una alerta); `ReplanRequested` con un hallazgo alto. La entrega bloqueada por una petición sin atender y desbloqueada al descartarla (WP5) |
| F5-7 | La impugnación registra `VerdictChallenged` y abre la revisión con los `ExcludedVendors` heredados y otro juez; el vendor del juez original no se excluye. El veredicto de la revisión va al `RunId` del original con `Supersedes`. **Entrega:** impugnación pendiente, bloqueada; aprobación retirada, bloqueada; vuelta hacia la aprobación, bloqueada hasta que la aceptas; aceptada, se entrega. La de un debate con jurado se revisa con jurado. **Una fila de `verdicts` reescrita** por detrás de los triggers deja un problema de integridad en el dossier de la revisión, y `ApprovedWithIntegrityProblems` impide aprobar |
| Pantallas | ViewModels con puertos falsos: historial desde la reproducción; formulario (explicación de la disposición, aviso de la dispensa, estimación); sala (ronda sellada sin texto, acciones, reintento); tarjeta (jurado lado a lado, correspondencia de etiquetas, «valoración del juez»); informe de auditoría; monitor. Renders fuera de pantalla como evidencia |
| Reproducción | Propiedad con unos 200 debates aleatorios por semilla (modos, participantes, rondas, límites y fallos): la reproducción del registro da el mismo estado que el debate en vivo, y `DebateBudget` nunca cuenta de menos |
| En vivo, con tu OK | Mediciones (WP4): un turno de Claude con `--no-session-persistence`, `--json-schema` y las reglas de lectura; uno de Codex con `--ephemeral` y `--output-schema`; Codex en solo lectura intentando escribir. Después, las demostraciones (decisión 2) |

---

## 9. Desglose de trabajo

P0 va en serie y se fusiona a `main` antes de abrir los paquetes. Después, WP1 a WP6 van en paralelo, en worktrees distintos
y sin ningún fichero en común. Al final integra el lead.

**Propietarios únicos de los ficheros compartidos:**

- P0: `Directory.Packages.props`, `AgentStudio.slnx` y todos los `*.csproj` (esta fase no añade paquetes ni proyectos), la
  migración y los enums del dominio y de Application.
- El lead, en la integración:
  - `src\AgentStudio.Desktop\Composition\{PlatformServices,ShellServices}.cs`, `…\ViewModels\MainViewModel.cs`,
    `…\MainWindow.xaml(.cs)`, `…\App.xaml`, `…\Themes\DocumentTemplates.xaml` y `…\Orchestrator\Session\DefaultAgentRoster.cs`;
  - `docs\07-debate-arena-y-juez.md` (se reescribe desde los ADR de los paquetes), `docs\08-…`, `docs\README.md` y la
    bitácora;
  - `CLAUDE.md`, solo con tu OK.

**«Hecho» exige siempre:**

- `dotnet build AgentStudio.slnx` con 0 avisos;
- `dotnet test` en verde sin `ProviderLive`;
- los ADR y los documentos del paquete;
- nada simulado en la UI.

### P0 · Contratos, dominio, payloads y migración 0005 (M-L; camino crítico)

**Propiedad (rutas absolutas):**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.Domain\Debate\**`: `DebateSession.cs` (cambios) y los nuevos
  `DebateJudging.cs`, `DebateStateReplayer.cs` y `DebateBudget.cs`.
- `…\src\AgentStudio.Domain\Judging\{Verdict.cs, VerdictValidator.cs}`, `…\Domain\Decisions\DecisionLog.cs` (solo el enum) y
  `…\Domain\Agents\AgentDefinition.cs` (solo el texto de la regla de D-46).
- `…\src\AgentStudio.Application\Decisions\{DecisionPayloads.cs, DecisionPayloadSerializer.cs, DebatePayloads.cs}`
  (el último, nuevo).
- `…\Application\Coordination\CoordinationContracts.cs`, `…\Application\Judging\JudgingContracts.cs` y
  `…\Application\Persistence\StoreContracts.cs`.
- `…\Application\Providers\{AgentProvider.cs, AgentRunObservations.cs, AuxiliaryAgentRunner.cs, JournalingAgentProvider.cs}`
  (`AgentRunObservations.cs`, nuevo).
- `…\Application\Prompts\PromptData.cs` (movido desde `…\src\AgentStudio.Orchestration\Engine\PromptData.cs`, que se borra),
  con el cambio de `using` en `…\Orchestration\Engine\ExecutorPrompt.cs` y `…\Orchestration\Planning\PlannerPrompt.cs`.
- `…\Application\Debate\DebateHeaderHash.cs` y `…\Application\Orchestration\ProjectActivity.cs` (nuevos).
- `…\src\AgentStudio.Debate\Judging\AgentJudge.cs` (solo `JudgeBrief` y `JudgeLaunch.Brief`, con el brief de hoy por defecto),
  `…\Debate\Judging\IndependentReview.cs` y los nuevos `…\Debate\Judging\{JudgeRunRecorder.cs, VerdictKeeper.cs}`, que salen
  de ella sin cambiar su comportamiento, parametrizados por propósito, tarea y alcance para que los usen el debate y la
  auditoría.
- `…\src\AgentStudio.Infrastructure\Persistence\Schema\0005_debates.sql`.
- Tests:
  - `…\tests\AgentStudio.Tests\Domain\{DebateSessionTests.cs, DebateJudgingTests.cs, DebateStateReplayerTests.cs, DebateBudgetTests.cs, VerdictValidatorTests.cs, DecisionKindCompatibilityTests.cs}`;
  - `…\tests\AgentStudio.Tests\Application\**`: golden de los payloads nuevos, `AgentRunObservations`, `PromptData` y
    `DebateHeaderHash` contra una implementación independiente de la forma canónica;
  - `…\tests\AgentStudio.Tests\Infrastructure\SqliteSchemaTests.cs`.
- Docs: `docs\04-contratos.md` (§17, Fase 5), `docs\05-modelo-de-datos.md`, ADR-0026 y ADR-0027.

**Hecho cuando:**

- `DebateJudgingRules`, `JudgingPanel`, `DebateBudget` y `DebateStateReplayer` están probados, y las pruebas de la Fase 0 de
  `DebateSessionTests` siguen diciendo lo mismo, salvo la del modelo del juez (D-26).
- Las reglas nuevas del validador y la exención de `JudgeAssessment` tienen su prueba, y todas las de `VerdictValidatorTests`
  siguen en verde.
- Los payloads v1 de las fases anteriores se siguen leyendo y su hash no cambia; los nuevos hacen ida y vuelta.
- La 0005 es idempotente sobre una base 0004 y sus triggers abortan UPDATE, DELETE y REPLACE.
- `IndependentReview` usa `JudgeRunRecorder` y `VerdictKeeper` con sus pruebas sin tocar.
- Las pruebas de `ExecutorPrompt` y `PlannerPrompt` pasan sin cambios con el `PromptData` movido.
- Docs/04 y docs/05 están actualizados.

**Arranque anticipado permitido:** el analizador de la contribución y el resolvedor de citas (WP1) y las comprobaciones del
auditor sobre registros preparados (WP3), que solo necesitan los payloads de las Fases 3 y 4.

### WP1 · La sala: coordinador, turnos, material y citas (L; depende de P0)

**Propiedad:**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.Debate\Arena\**` (nuevo): `DebateCoordinator`, `DebateRun`, `DebateLog`
  (las entradas y los eventos de un debate), `DebateModes` (material y requisitos por modo, `orq-1` a `orq-7`),
  `ParticipantPrompt`, `ContributionSchema`, `ContributionParser`, `CitationResolver`, `DebateMaterial`, `DebateSnapshot` y
  `DebateTurnRecorder`.
- `…\src\AgentStudio.Debate\Arena\ArenaServiceCollectionExtensions.cs` (nuevo: `AddAgentStudioDebateArena`, solo lo de este
  paquete).
- Tests: `…\tests\AgentStudio.Tests\Debate\Arena\**` y los dobles nuevos `…\tests\AgentStudio.Tests\Debate\Fakes\ScriptedDebate*.cs`.
- Docs: ADR-0028.

**Hecho cuando:**

- Las pruebas de F5-1 (con `IDebateJudgment` doble), F5-2 y F5-3 están en verde, con la propiedad de la reproducción.
- La reanudación cierra los turnos abiertos, repite los sellados que se perdieron y no repite nada anunciado.
- La comprobación del repositorio del usuario rodea cada ronda, y `IProjectActivity` rechaza abrir un debate en un proyecto
  ocupado.
- Ningún payload lleva texto de un agente (prueba que recorre los payloads del debate).

### WP2 · El juicio: dossier, rúbrica, esquema, jurado y veredicto (M-L; depende de P0)

**Propiedad:**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.Debate\Judging\Debates\**` (nuevo): `DebateDossierBuilder`,
  `DebateDossierDocument`, `DebateJudgePrompt`, `DebateVerdictSchema` y `DebateJudgment` (implementa `IDebateJudgment`).
- `…\Debate\Judging\VerdictJson.cs` (la resolución) y `…\Debate\JudgingServiceCollectionExtensions.cs` (`AddAgentStudioJudging`
  gana el juicio de los debates).
- Tests: `…\tests\AgentStudio.Tests\Debate\Judging\**` (nuevos) y `…\tests\AgentStudio.Tests\Debate\{VerdictJsonTests.cs, VerdictSchemaTests.cs}`.
- Docs: ADR-0029.

**Hecho cuando:**

- Las pruebas de F5-4 están en verde.
- El dossier a ciegas no tiene identidades de los participantes, y el de una auditoría de implementación incluye el dossier
  entero de la ejecución.
- El esquema del veredicto de debate es portable y cabe en 16 KB con 50 requisitos y 6 propuestas.
- Un veredicto guardado antes de esta fase se lee igual.

### WP3 · Auditorías e impugnación (M-L; depende de P0; usa WP1 y WP2 por sus puertos)

**Propiedad:**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.Debate\Audit\**` (nuevo): `OrchestratorAudit`,
  `OrchestratorAuditService`, `ProjectAuditService`, `ProjectDossierBuilder`, `ProjectAuditPrompt` y `PendingProblems`.
- `…\src\AgentStudio.Debate\Challenges\**` (nuevo): `VerdictChallengeService` y `ChallengeLog`.
- `…\src\AgentStudio.Debate\Audit\AuditServiceCollectionExtensions.cs` (nuevo: `AddAgentStudioAudits`, con la impugnación).
- Tests: `…\tests\AgentStudio.Tests\Debate\Audit\**` (con los registros preparados en `…\Debate\Audit\Fixtures\**`) y
  `…\tests\AgentStudio.Tests\Debate\Challenges\**`.
- Docs: ADR-0030, ADR-0031 y ADR-0032.

**Hecho cuando:**

- Las pruebas de F5-5, F5-6 y F5-7 del lado del módulo están en verde, con `IDebateCoordinator` e `IVerificationRunner`
  dobles.
- El auditor no escribe nada sobre una cadena rota y es idempotente.
- La auditoría no lanza al juez con el registro roto ni con los dos vendors productores.
- Hay una lista de comprobación para la demostración sobre este repositorio.

### WP4 · Persistencia y proveedores (M; depende de P0)

**Propiedad:**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.Infrastructure\Persistence\{SqliteDebateRegistry.cs, SqliteProjectAuditRegistry.cs, Json\DebateJson.cs, Json\VerdictJson.cs, SqliteVerdictStore.cs, PersistenceServiceCollectionExtensions.cs}`.
- `…\src\AgentStudio.Providers\Cli\Claude\{ClaudeCodeArguments.cs, ClaudeCodeAgentProvider.cs}` y
  `…\Providers\Cli\Codex\{CodexExecArguments.cs, CodexCliAgentProvider.cs}`.
- Tests:
  - `…\tests\AgentStudio.Tests\Infrastructure\Persistence\{SqliteDebateRegistryTests.cs, SqliteProjectAuditRegistryTests.cs, SqliteVerdictStoreTests.cs}`;
  - `…\tests\AgentStudio.Tests\Providers\{Claude,Codex}\**` (los de argumentos);
  - `…\tests\AgentStudio.Tests\Providers\Live\{DebateTurnLiveTests.cs, CodexReadOnlyWriteLiveTests.cs}` (nuevos).
- Docs: `docs\06-integracion-proveedores.md`.

**Hecho cuando:**

- Las cabeceras son append-only y su hash hace ida y vuelta.
- La resolución del veredicto hace ida y vuelta y los veredictos guardados antes se leen igual.
- `--no-session-persistence` y `--ephemeral` (con `-c history.persistence=none`, D-16) salen solo con
  `PersistSession = false`, y la petición se rechaza si además reanuda. `CodexExecArgumentsTests` deja de prohibir
  `--ephemeral` en todo caso y lo prohíbe sin esa petición.
- **`ClaudeCodeArguments.WriteConfinementLaunch` no cambia** (una prueba lo fija): un ejecutor nunca pide un turno sin
  persistencia.
- Los dos tests en vivo están escritos; se ejecutan en la integración, con tu OK.

### WP5 · Orquestación y memoria: los enganches (S-M; depende de P0)

**Propiedad:**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.Orchestration\Integration\DeliveryService.cs` (D-35 y D-40),
  `…\Orchestration\Engine\Orchestrator.cs` (solo `OrchestrationActivity` sobre `IProjectActivity`) y
  `…\Orchestration\OrchestrationServiceCollectionExtensions.cs`.
- `…\src\AgentStudio.Memory\Derivation\**` (D-46).
- Tests: `…\tests\AgentStudio.Tests\Orchestration\Integration\DeliveryChallengeTests.cs`,
  `…\tests\AgentStudio.Tests\Orchestration\Engine\ProjectActivityTests.cs` y
  `…\tests\AgentStudio.Tests\Memory\Derivation\DebateDerivationTests.cs` (nuevos).
- Docs: la adenda de ADR-0012 sobre la entrega.

**Hecho cuando:**

- La entrega sigue las reglas de D-40 y, con la decisión 6, la de D-35. Las pruebas de la entrega de la Fase 3 siguen en
  verde.
- El orquestador no planifica, no arranca y no reanuda mientras un debate o una auditoría tienen el proyecto.
- La derivación da un `DebateSummary` por debate cerrado, idempotente, y nunca cita el texto del juez.

### WP6 · Escritorio: sala, historial, tarjeta, auditoría y monitor (L; depende de P0; usa los puertos con dobles)

**Propiedad:**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.Desktop\{Debate,Audit}\**`, `…\Desktop\Views\{Debate,Audit}\**` y
  `…\Desktop\Composition\DebateUiServices.cs` (nuevo).
- `…\Desktop\Orchestrator\Monitor\{RunMonitorViewModel.cs, MonitorTexts.cs}` y `…\Desktop\Views\Orchestrator\RunMonitorView.xaml`.
- `…\Desktop\Observability\UsageDashboardViewModel.cs` (solo las etiquetas de los propósitos nuevos).
- Tests: `…\tests\AgentStudio.Tests\Desktop\{Debate,Audit}\**`, `…\Desktop\Orchestrator\RunMonitorViewModelTests.cs` y las
  pruebas de las etiquetas del panel de consumo.
- Docs: `docs\03-pantallas-y-navegacion.md` (§2, §4, §6 y §8).

**Hecho cuando:**

- Las pruebas de ViewModel de las pantallas están en verde con puertos falsos.
- Hay renders fuera de pantalla de la sala, de la tarjeta (con jurado) y del informe.
- Hay una lista de comprobación para tu demostración.

### Integración (el lead, tras las fusiones)

- **Composición.** `AddAgentStudioDebateArena` y `AddAgentStudioAudits` en `PlatformServices.cs` (el juicio de los debates
  ya entra con `AddAgentStudioJudging`), y una sola `IProjectActivity`, que es también la `IRunActivity` de la memoria. En la barra de actividad aparece «Debates»; en la superior, «Auditar proyecto completo»; y los
  atajos `Ctrl+Shift+D` y `Ctrl+Shift+A`.
- **Plantilla por defecto** según D-09 y la decisión 1. Solo afecta a los proyectos sin plantilla.
- **Pruebas de integración** en `C:\Personal Projects\Agent Studio\tests\AgentStudio.Tests\Integration\Phase5\**`:
  - un debate completo sobre SQLite con proveedores guionizados de los dos CLIs, y su reproducción tras un reinicio simulado
    (`DebateStateReplayer` sobre la base reabierta);
  - una impugnación hasta la entrega;
  - una auditoría de proyecto sobre un repositorio temporal con el verificador real;
  - composición: todos los turnos pasan por la bitácora, y la memoria espera durante un debate.
- **En vivo, con tu OK:** las mediciones de WP4 y, después, las demostraciones de la decisión 2.
- **Documentación:** docs/07 desde los ADR, docs/08 (texto de F5-1 según D-11 y el estado de la fase), README y bitácora.

**Orden y dependencias.**

| Paso | Qué | Depende de |
|---|---|---|
| 1 | P0 | — |
| 2 | WP1 a WP6, en paralelo | P0. WP1 usa `IDebateJudgment` (WP2) con un doble; WP3 usa `IDebateCoordinator` (WP1) con un doble; WP6 usa todos los puertos con dobles |
| 3 | Mediciones en vivo | WP4, con tu OK |
| 4 | Integración, demostraciones y docs | Todos |

---

## 10. ADR a escribir

| ADR | Tema | Paquete |
|---|---|---|
| 0026 | Un debate es una unidad del registro: id propio, cabecera inmutable anclada, estado reproducido, el texto de los agentes como evidencia y una sola actividad con agentes por proyecto | P0 |
| 0027 | Independencia del juez en los debates con dos vendors: la regla de los productores no se dispensa; la de los participantes, solo de forma explícita y registrada (jurado cruzado con desempate humano, o juez no productor). Adenda de ADR-0006 | P0 |
| 0028 | Turnos sin estado (sesiones nuevas y no persistentes) y rondas simultáneas y selladas; los límites los mide el sistema y lo que falta se estima | WP1 |
| 0029 | El veredicto de un debate: el juez de la Fase 3 con su brief, el dossier a ciegas, la resolución estructurada y la valoración del juez en los modos de elección | WP2 |
| 0030 | «Auditar proyecto completo»: el commit HEAD, la verificación aislada, un dossier índice y la regla de los productores con autores declarados; sin juez con el registro roto | WP3 |
| 0031 | Auditor determinista del orquestador: siete comprobaciones, alertas y petición de replanificación como ejecución de seguimiento | WP3 |
| 0032 | Impugnación: un debate de revisión que hereda quién puede juzgar y cambia de juez; sus efectos en la entrega (con la adenda de ADR-0012) | WP3 y WP5 |

---

## 11. Riesgos y preguntas abiertas

| Riesgo | Mitigación |
|---|---|
| Un juez prefiere las respuestas de su familia de modelos o la primera que lee (documentado en la literatura) | Juez del otro vendor por defecto; en jurado, orden invertido, dossier a ciegas y desempate humano; el validador; la impugnación |
| Los agentes que se leen tienden a darse la razón (convergencia complaciente) | Ronda 1 sellada; papeles adversariales; decide el juez con evidencias, no el acuerdo; no hay votos |
| Inyección de un participante a otro | §7 |
| Codex lee todo el disco, y lo que lee llega al otro vendor | §7 y decisión 7 |
| El sandbox de solo lectura de Codex no se ha visto bloquear una escritura en Windows | Medición antes de las demostraciones (WP4); la comprobación del repositorio del usuario alrededor de cada ronda |
| Reenviar el debate en cada turno cuesta tokens | Límite con proyección; `MaxAgentRuns`; resumen y argumento con tope |
| El dossier del proyecto no cabe (R8) | Índice con hashes, topes, y el juez lee la copia |
| La suite de Agent Studio a integridad Low | Medición previa sin cuota; perfil filtrado y declarado en el informe |
| `--no-session-persistence` con `--json-schema` y `--tools`, o `--ephemeral` con `--output-schema`, no están medidos | Mediciones de WP4 antes de las demostraciones |
| El jurado cruzado da más «no resuelto» | Es un resultado legítimo; «Decidir yo» |
| El jurado cruzado se lee como independencia | La tarjeta dice «dispensa»; ADR-0027 |
| Un debate largo bloquea el proyecto (una sola actividad) | Límite de 30 minutos; «Abortar»; la sala lo dice |
| La entrega cambia en código cerrado en seguridad | Solo se vuelve más estricta, salvo la aceptación expresa de una revisión; sus pruebas de la Fase 3 siguen |
| Si los dos vendors produjeron código del proyecto, la auditoría completa no tiene juez | Es la consecuencia de no dispensar la regla P: el informe da lo que el sistema comprueba sin juez y lo dice; un tercer vendor lo resolvería |
| Un debate no siempre decide mejor que una estrategia más simple (§3.3) | El valor de la fase es la deliberación auditable y el juicio validado; las demostraciones lo dirán, y el debate no entra en los planes (decisión 3) |

**Preguntas abiertas (para el diseño, no para ti):**

1. ¿Se publica una ronda 1 en la que un participante falló dos veces? Propuesta: no; el debate espera (D-22).
2. ¿Qué parte del material del objeto ven los participantes de una auditoría de implementación? Propuesta: el mismo dossier
   que el juez de la Fase 3, que ya deja fuera los prompts con memoria.
3. ¿El auditor del orquestador revisa las ejecuciones anteriores a la Fase 5 al abrir un proyecto? Propuesta: sí, una vez, sin
   pedir replanificación de las que ya se entregaron.

---

## 12. Decisiones que necesitan al usuario

> **Resueltas el 2026-10-19.** Elegiste la opción recomendada en las cuatro que se te preguntaron: 1 (a) juez del otro
> vendor por defecto, con la mezcla como dispensa registrada (jurado cruzado al elegir, juez del vendor no productor al
> revisar); 2 (a) seis demostraciones con vendors mezclados, Claude con haiku, unas 45 a 50 ejecuciones en total; 6 (a) el
> auditor y la impugnación bloquean la entrega hasta que decidas; 7 (a) aceptas el residual de lectura de los
> participantes de Codex, y antes se mide si su sandbox bloquea una escritura en Windows (si escribe, Codex no participa
> ni juzga hasta resolverlo). Las otras tres se aplicaron con su opción recomendada, y puedes cambiarlas: 3 (a) los nodos
> «Debate» del lienzo se aplazan; 4 (a) la auditoría del propio repositorio usa un conjunto de requisitos congelado para
> ella y la autoría declarada como Anthropic, de modo que juzga Codex; 5 (a) 4 rondas, 30 minutos y 400 000 tokens.

1. **Cómo se juzga un debate con dos vendors.**
   - **Qué pasa.** ADR-0006 pide un juez de un vendor distinto de todos los participantes, y F5-1 pide participantes de
     proveedores distintos. Con dos vendors, un solo juez no puede cumplir las dos cosas.
   - **Opciones:**
     - **(a) Por defecto, participantes de un vendor (con modelos distintos) y juez del otro; para mezclar vendors, una
       dispensa registrada: jurado cruzado en los modos de elección y juez del vendor no productor en los de revisión.
       Recomendada:** no cambia ninguna decisión aprobada ni se apoya en nada sin medir, y la mezcla nunca va a escondidas;
     - (b) mezcla con un solo juez de uno de los dos vendors, a ciegas: la más barata, pero el juez decide solo sobre la
       propuesta de su propio vendor;
     - (c) no mezclar nunca: F5-1 deja de cumplirse;
     - (d) un tercer vendor, Gemini con API key (ADR-0009): independencia completa, con coste por uso y la privacidad del
       nivel gratuito.
   - **Con (a)**, el texto de F5-1 pasa a ser el de D-11.
2. **Las demostraciones reales y su coste.** Cifras «estimado por Agent Studio» a partir de lo medido (§3); con suscripción, el
   coste de Claude no es lo que pagas.
   - **(a) Una demostración por modo, todas con participantes de los dos vendors (Claude con haiku y Codex con su modelo por
     defecto), dos rondas, jurado cruzado en los cuatro modos de elección y juez Codex en los dos de revisión. Recomendada:**
     demuestra F5-1 al pie de la letra y cada mecanismo nuevo una vez.
     - Unas 35 ejecuciones: 32 de los seis debates, más 3 de una ejecución pequeña que sirva de objeto a los dos modos de
       revisión.
     - Claude, unas 17 ejecuciones: entre 0,7 y 1,7 USD a precio de lista.
     - Codex, unas 18 ejecuciones: entre 0,9 y 1,8 millones de tokens.
   - (b) Lo mismo con el juez independiente en los seis (participantes de Claude con dos modelos), más un único debate con
     jurado cruzado: unas 37 ejecuciones, unas 27 de ellas de Claude.
   - (c) Modelos de más calidad (sonnet u opus) para los participantes: el coste estimado de Claude se multiplica varias
     veces; la primera demostración da la cifra.
   - **En los tres casos, además:**
     - las tres mediciones de WP4: unas 3 ejecuciones baratas;
     - la auditoría del propio repositorio (decisión 4): 1 o 2 ejecuciones de juez;
     - una impugnación sobre uno de los veredictos de las demostraciones: unas 5 ejecuciones.

     Son unas 45 a 50 ejecuciones en total.
3. **Nodos «Debate» en el lienzo del orquestador.**
   - **(a) Aplazarlos. Recomendada:** cambiarían el protocolo de ADR-0013 y la reproducción de una ejecución, que están
     cerrados en seguridad, y el valor llega antes por la sala y por la auditoría de implementación de una ejecución
     terminada;
   - (b) incluir un nodo de debate de solo lectura antes de un trabajo de agente, cuyo veredicto pasa como dato a sus
     dependientes: un paquete más, de tamaño L, que toca el motor.
4. **La auditoría del propio repositorio de Agent Studio (F5-5).**
   - **(a) Un conjunto de requisitos congelado para la auditoría (los criterios comunes C-1 a C-4 y una selección de los de
     las Fases 3 y 4), la autoría declarada como Anthropic (juzga Codex) y, antes, sin cuota, ejecutar la suite a integridad
     Low para saber si hay que filtrar categorías. Recomendada;**
   - (b) auditar contra el último conjunto congelado del proyecto: este repositorio no tiene ninguno;
   - (c) no declarar autoría: el registro no conoce ningún productor, y el juez podría ser Claude, que escribió el código.
   - **Coste:** una ejecución de Codex que lee el repositorio, estimada entre 150 000 y 500 000 tokens y entre 5 y 15
     minutos, más una corrección como mucho. La verificación no gasta cuota.
5. **Límites por defecto de un debate.**
   - **(a) 4 rondas, 30 minutos y 400 000 tokens, los de docs/07, contando la entrada (con la caché) y la salida de los
     participantes. Recomendada:** con un participante de cada vendor dan para unas tres rondas;
   - (b) 4 rondas, 30 minutos y 1 000 000 de tokens, para debates que leen mucho código;
   - (c) límites distintos por modo.
6. **Qué hacen la auditoría del orquestador y la impugnación con la entrega.**
   - **(a) El auditor corre solo y gratis al terminar cada ejecución. Su petición de replanificación y una impugnación
     pendiente bloquean la entrega hasta que decidas, y una revisión que da la vuelta hacia la aprobación necesita tu
     aceptación. Recomendada:** falla cerrado, como el resto de la entrega;
   - (b) solo avisan, sin bloquear;
   - (c) el auditor, solo cuando lo pides.
7. **El residual de seguridad de los debates.**
   - **Qué es.** Un participante de Codex en solo lectura puede leer cualquier fichero tuyo, y lo que lea puede llegar al otro
     vendor a través del debate. Además, no se ha visto que el sandbox de solo lectura de Codex bloquee una escritura en
     Windows.
   - **Opciones:**
     - **(a) Aceptar el residual de lectura, con la redacción de secretos y el material mínimo, y medir antes la escritura con
       una ejecución. Si Codex escribe, no participa ni juzga hasta resolverlo. Recomendada;**
     - (b) Codex solo como juez, nunca como participante: equivale a quedarse en la disposición por defecto de la
       decisión 1(a), sin la dispensa para mezclar;
     - (c) aplazar la mezcla de vendors hasta el aislamiento fuerte de F7-4.

---

## 13. Sin verificar

- **Claude Code 2.1.290:**
  - que `--no-session-persistence` conviva con `--json-schema`, `--tools` y las reglas `Read` del lanzamiento de solo
    lectura. La documentación los da a todos como opciones de `-p` y no dice que choquen; lo mide WP4;
  - que una sesión sin persistencia no deje nada más en el perfil del CLI: la documentación no dice si también se salta los
    registros de depuración, el historial de ficheros o el de prompts. `CLAUDE_CODE_SKIP_PROMPT_HISTORY` podría ser una
    segunda línea, pero no se usa ni se ha medido. Agent Studio no lee esas carpetas, así que solo lo puede decir la
    documentación;
  - el lanzamiento de solo lectura con dos carpetas añadidas (la copia y la del turno) usa el mismo mecanismo que el juez y
    el planificador, pero no se ha medido con esa forma exacta.
- **Codex CLI 0.160.0:**
  - qué deja de escribir `--ephemeral` además de los ficheros de la sesión, si `exec` escribe `history.jsonl` y si
    `--ephemeral` convive con `--output-schema`. Lo mide WP4, salvo lo que solo se vería leyendo `~/.codex`;
  - que `-c history.persistence=none` (D-16) no cambie nada más en 0.160.0: la clave está documentada, pero no consta que
    `exec` escriba ese historial, y la versión puede avisar de una clave que no usa. Lo comprueba la medición de WP4;
  - que `exec resume` respete `--output-schema` en la corrección del juez: la ayuda local lo admite, la tabla de la
    documentación no lo lista y el código lo envía con el turno (ya abierto en ADR-0014);
  - que `-s read-only` bloquee de verdad una escritura en Windows (ADR-0014). Lo mide WP4;
  - que las lecturas de `-s read-only` alcancen todo el disco: se vio una vez (el juez de la Fase 3 leyó `%TEMP%`), la
    documentación no las limita y el código (`has_full_disk_read_access`) se leyó en un resumen, no línea a línea;
  - que `turn.completed.usage` sea el acumulado del hilo: se lee así en el código (`usage.total`); la documentación no lo
    define.
- **Las cifras de coste:** se extrapolan de las ejecuciones de F3-8 sobre un repositorio pequeño y de las capturas de las
  Fases 2 y 4. El juez de una auditoría de este repositorio no se ha medido.
- **La suite de Agent Studio a integridad Low:** no se ha ejecutado nunca así.
- **Los sesgos del juez:** las cifras de la literatura son de otros modelos (GPT-4, GPT-3.5, Claude-v1) y otras tareas; no
  se ha medido cuánto prefieren Claude y Codex sus propias propuestas en esta aplicación, ni cuánto discrepará un jurado
  cruzado. Las citas se tomaron de los resúmenes de los artículos y, las de Zheng et al., de su versión HTML en ar5iv: hay
  que contrastarlas con el texto completo antes de citarlas fuera de este documento.
- **El sellado frente a lo que escribe otro CLI en su perfil durante la ronda:** se apoya en la documentación de
  `--no-session-persistence` y `--ephemeral`; no se puede medir sin leer esas carpetas.

---

## Fuentes

Consultadas el 2026-10-19.

**Locales (sin cuota):**

- Ayuda de `claude` 2.1.290: `--no-session-persistence`, `--session-id`, `--resume`, `--fork-session`, `--json-schema`,
  `--max-turns`, `--max-budget-usd`, `--tools`, `--restricted` y `--add-dir`.
- Ayuda de `codex` 0.160.0: `exec`, `exec resume` y `exec fork`, con `--ephemeral`, `--output-schema`, `-s` y `-C`.
- Capturas de `tests/AgentStudio.Tests/Providers/Fixtures/` (Claude Code 2.1.290 y Codex 0.160.0) y su `README.md`.
- [evidencias/fase3-e2e.md](evidencias/fase3-e2e.md): consumo y duración del planificador, el ejecutor y el juez reales.
- [ADR-0014](adr/0014-independencia-del-juez-con-dos-vendors.md) (el juez de Codex lee fuera de su carpeta),
  [ADR-0018](adr/0018-modelo-de-amenaza-fase-3.md) (mediciones del confinamiento de Claude) y
  [ADR-0024](adr/0024-recomendador-asistido-con-mcp-local.md) (medición D-30 de la Fase 4).

**Claude Code:**

- «CLI reference» (`--no-session-persistence`, `--restricted`, reanudación y prompt de sistema):
  <https://code.claude.com/docs/en/cli-reference>
- «Sessions» (dónde se guardan, reanudar, no guardar, dos terminales): <https://code.claude.com/docs/en/sessions>
- «Track cost and usage» (el gasto de una sesión reanudada): <https://code.claude.com/docs/en/agent-sdk/cost-tracking>
- «Agent loop» del SDK (subtipos del resultado y presupuesto): <https://code.claude.com/docs/en/agent-sdk/agent-loop>
- Notas de la versión 2.1.277 (totales de las sesiones reanudadas en `-p`):
  <https://github.com/anthropics/claude-code/releases/tag/v2.1.277>

**Codex CLI** (las URL de `developers.openai.com/codex` redirigen a `learn.chatgpt.com`):

- «Sandboxing»: <https://learn.chatgpt.com/docs/sandboxing>
- «Windows sandbox» (perfiles de permisos y el modo `unelevated`): <https://learn.chatgpt.com/docs/windows/windows-sandbox>
- «Non-interactive mode» (`codex exec` y `exec resume`): <https://learn.chatgpt.com/docs/non-interactive-mode>
- «Config reference» (`permissions.<nombre>.filesystem`, `history.persistence`, `model_auto_compact_token_limit`):
  <https://learn.chatgpt.com/docs/config-file/config-reference>
- «Developer commands» (`--ephemeral`): <https://learn.chatgpt.com/docs/developer-commands?surface=cli>
- «Pricing» (asignación del plan y límites semanales): <https://learn.chatgpt.com/docs/pricing>
- Código en `rust-v0.160.0`:
  - `exec`: <https://github.com/openai/codex/blob/rust-v0.160.0/codex-rs/exec/src/lib.rs> y
    <https://github.com/openai/codex/blob/rust-v0.160.0/codex-rs/exec/src/cli.rs>
  - salida JSONL de `exec`: <https://github.com/openai/codex/blob/rust-v0.160.0/codex-rs/exec/src/event_processor_with_jsonl_output.rs>
  - políticas del sandbox: <https://github.com/openai/codex/blob/rust-v0.160.0/codex-rs/protocol/src/protocol.rs>

**Jueces LLM y debate:**

- Zheng et al. (2023), «Judging LLM-as-a-Judge with MT-Bench and Chatbot Arena»: <https://arxiv.org/abs/2306.05685>
  (cifras de <https://ar5iv.labs.arxiv.org/html/2306.05685>)
- Panickssery et al. (2024), «LLM Evaluators Recognize and Favor Their Own Generations»: <https://arxiv.org/abs/2404.13076>
- Verga et al. (2024), «Replacing Judges with Juries: Evaluating LLM Generations with a Panel of Diverse Models»:
  <https://arxiv.org/abs/2404.18796>
- Wang et al. (2023), «Large Language Models are not Fair Evaluators»: <https://arxiv.org/abs/2305.17926>
- Du et al. (2023), «Improving Factuality and Reasoning in Language Models through Multiagent Debate»:
  <https://arxiv.org/abs/2305.14325>
- Khan et al. (2024), «Debating with More Persuasive LLMs Leads to More Truthful Answers»: <https://arxiv.org/abs/2402.06782>
- Irving et al. (2018), «AI safety via debate»: <https://arxiv.org/abs/1805.00899>
- Smit et al. (2023), «Should we be going MAD? A Look at Multi-Agent Debate Strategies for LLMs»:
  <https://arxiv.org/abs/2311.17371>
