# ADR-0035 · El final clasificado de un intento y la renovación del contexto con un traspaso que construye el sistema; nunca `--resume` de un intento interrumpido

- **Estado:** aceptada · 2026-10-21, la parte del final clasificado (WP2 de la Fase 8), y 2026-10-25, la de la renovación
  del contexto (WP1). Recoge D-41 de
  [docs/16](../16-ampliacion-plataforma-de-proyectos.md#215-contexto-y-recuperación-de-sesiones-sección-15) y el criterio F8-1.
  Los contratos son de P0 ([docs/04 §18.1 y §18.2](../04-contratos.md#18-fase-8-trabajo-desatendido),
  [ADR-0034](0034-esperas-por-limite-y-reanudacion-con-consentimiento.md)). La renovación del contexto y el traspaso (D-42,
  D-43 y F8-6) son de WP1, que trabaja en paralelo en el motor, y van en su sección, al final.

## El final clasificado (WP2)

- **Contexto:**
  - Antes de la Fase 8, el final de un intento solo tenía estado. Cinco causas caían en cuatro estados: el contexto agotado
    en `LimitReached` o `Failed`, la cuota y la velocidad juntas en `RateLimited`, la red en `Failed` con su texto, y el
    reinicio en `Interrupted`.
  - El analizador de Claude reducía un límite a `status != allowed` y no guardaba `rateLimitType`, así que una ventana de cinco
    horas y la semanal se veían igual. `codex exec --json` solo da el texto del error (`ThreadErrorEvent.message`).
  - Las capturas reales (Claude Code 2.1.290 y Codex CLI 0.160.0) son todas de turnos que terminaron: ninguna topa un límite,
    el contexto, la sesión o la red. La de Claude trae `rate_limit_event` con `rateLimitType` (`five_hour`, `allowed`) y
    `result.terminal_reason` (`completed`).
- **Decisión:**
  1. **La clase la reconoce cada analizador, solo de señales que están en una captura de su versión.** Cada uno expone las
     suyas (`EndClassSignals`), y una prueba por CLI falla si una no aparece en ninguna captura. Lo que no reconoce no lleva
     clase (`AgentRunOutcome.End` nulo): es «otra», y un turno que terminó es `Completed` (`AgentRunEnd.Implied`).
  2. **Mientras no haya una captura real de un caso, la hay sintética y lo dice:** `tests/…/Fixtures/<cli>/<versión>/limits/`,
     con un README que da la fuente de cada campo, lo inventado y lo que falta medir. Se construyen con la forma de las
     capturas reales y con la documentación (Claude) o el código de la versión (Codex), como la captura sintética de la Fase 4.
     Una captura real sustituye a la sintética, y la prueba dice si la señal sigue ahí.
  3. **Claude: primero las señales estructuradas, en este orden.** El subtipo del presupuesto (`error_max_turns`,
     `error_max_budget_usd`), `terminal_reason` `prompt_too_long`, el error estructurado `authentication_failed` del último
     mensaje del bucle principal (el código que la documentación da en `-p`), el último `rate_limit_event` con `rejected` (la
     ventana, `rateLimitType` tal cual, y su `resetsAt`), y `overloaded` o el 529. Solo la red va por texto («Unable to connect
     to API», «Request timed out»), porque la documentación no le da un código.
  4. **Codex: fragmentos de los textos de codex-rs 0.160.0**, en una tabla para los dos caminos de un fallo (`turn.failed`, y
     el proceso que termina sin `turn.completed`). El filtro de contenido se mira antes que la red, porque su texto empieza
     como un stream cortado. Una ventana va sin tipo ni reinicio (docs/04 §18.1): la espera es la progresiva, «estimada».
  5. **Gemini (aparcado, ADR-0009): ninguna clase.** No hay una sesión autenticada capturada.
  6. **La clase refina el estado y nunca lo cambia:** ningún mapeo de estado cambia en esta fase. El motor (`Workers`) decide
     hoy por el estado, y WP1 decidirá por la clase.
  7. **`AgentRunner` y `JudgeRunRecorder` escriben `AgentRunFinished` v3** con la clase del adaptador o, si no la dio, la que
     implica el estado (un intento que no llegó a lanzarse, por ejemplo). Los turnos de un debate, las ejecuciones auxiliares
     y los cierres por interrupción siguen en la v2 o la v1, y su clase es la que implica su estado (`ResolvedEnd()`).
  8. **La bitácora lleva la clase en la nota de la línea «Resultado» solo cuando dice más que el estado**
     (`clase WindowQuota five_hour, reinicio 2026-10-15 17:20 UTC`). Las demás líneas, y el resumen diario que las lee, no
     cambian.
- **Alternativas:**
  - **Deducir la clase en el motor a partir del texto del error:** sería adivinar (ADR-0034 ya la descarta).
  - **Clasificar Claude por los textos de la documentación** («You've hit your session limit», «Prompt is too long»…): los
    textos cambian entre versiones (la documentación lo dice de 2.1.227), y las señales estructuradas son tokens
    documentados.
  - **Leer la hora de reinicio del texto de Codex:** el mismo día solo trae la hora local, sin fecha ni zona. Un error
    despertaría la tarea antes o después de tiempo; la espera progresiva es la regla para «sin hora».
  - **Un 429 sin ventana rechazada como velocidad:** la documentación distingue el límite del plan de una limitación pasajera
    por las cabeceras de cuota unificada, que el analizador solo ve a través de `rate_limit_event`. Sin él no se sabe: «otra».
  - **Pasar a `RateLimited` un fallo con la ventana rechazada y sin 429:** cambiaría el motor de WP1 antes de integrar, y no
    hace falta en el caso documentado (el límite del plan es un 429).
  - **Clasificar como ventana los errores de uso de Codex que no lo son** (créditos o tope de gasto de un workspace, cuota de
    facturación, un plan sin Codex): esperar no los arregla. Son «otra».
  - **Escribir siempre la clase en la bitácora:** cambiaría todas las líneas con una clase que el estado ya dice.
- **Consecuencias:**
  - F8-1: cada intento del planificador, de un ejecutor y de un juez termina con una clase en el registro, y la v1 y la v2 se
    leen con la que implica su estado.
  - Las clases de los casos sin captura real se apoyan en capturas sintéticas marcadas. Hasta medirlas, una clase puede ser
    «otra» en la realidad aunque la sintética diga otra cosa, nunca al revés: lo que no coincide con una señal no clasifica.
  - Una versión nueva de un CLI sigue con las señales de la probada; el aviso de versión no probada (`SupportedVersions`) ya
    lo dice, y sus capturas van a su propia carpeta.
  - **Sin verificar** (docs/16 §8): todo lo sintético. En Claude, si en `-p` el código estructurado llega en `assistant.error`;
    si `rate_limit_event` sale con `rejected` antes del `result` de un 429; los valores de `rateLimitType` además de
    `five_hour`; el `api_error_status` y el `terminal_reason` de un contexto agotado; el texto de un fallo de red; y si un
    intento rechazado gasta algo. En Codex, si el límite de uso y la sesión caducada salen en el JSONL o solo en stderr, y con
    qué código de salida.
  - **Pendiente fuera de WP2:** el tiempo agotado no dice que fue el tiempo. El de un adaptador da `LimitReached` con `Budget`
    nulo (lo construye el adaptador, no el analizador), y el plazo de una tarea en el motor cancela el intento (`Cancelled`).
    Que lleven `AttemptBudget` `Time` lo tiene que decir el adaptador o `Workers`.

## La renovación del contexto y el traspaso (WP1)

Recoge D-42, D-43 y el criterio F8-6 de
[docs/16](../16-ampliacion-plataforma-de-proyectos.md#215-contexto-y-recuperación-de-sesiones-sección-15).

- **Contexto:**
  - Antes de la Fase 8, el reintento de un intento fallido reanudaba su sesión (`--resume`), con el bloque
    `<previous_attempt>` y la nota del fallo. Un contexto agotado se reintentaba en la misma sesión, que seguía llena, y lo
    que la Fase 4 aplazó («reintentar en una sesión nueva cuando el contexto está alto») no tenía regla.
  - Tras un reinicio, la tarea interrumpida volvía a `Pending`, y su siguiente intento reanudaba la última sesión
    registrada, aunque nada dijera que el CLI la hubiera guardado.
  - Agent Studio no lee las carpetas de los CLIs, así que no puede comprobar una sesión anterior.
- **Decisión:**
  1. **Cuándo se renueva la sesión.** El siguiente intento va en una sesión nueva, sin `--resume`, con el traspaso, cuando:
     - el final del intento tuvo la clase `ContextExhausted`;
     - el intento falló con el pico de su contexto en el umbral de compresión (85 %, `Handoff.CompressionThreshold`, el de
       [ADR-0021](0021-salud-del-contexto-por-paso.md)), leído del `ContextPeak` de su `AgentRunFinished`. Un CLI que no
       informa del contexto (`codex exec`) decide solo por la clase;
     - la aplicación se cerró durante el intento (D-43): al reanudar, la recuperación quita su sesión y marca la
       renovación. Nunca se reanuda la sesión de un intento interrumpido, ni una anterior.

     En los demás casos, el reintento sigue como antes. Un intento en marcha no se interrumpe para renovarlo: la
     compactación del propio CLI hace su trabajo y queda registrada.
  2. **Qué lleva el traspaso** (`Handoff.Block`): un bloque `<handoff task=… attempt=… reason=…>` con un solo documento
     JSON escrito con `PromptData`, así que nada de lo que lleva puede cerrar el bloque:
     - el objetivo de la tarea y los criterios de los requisitos que cubre, del conjunto congelado;
     - el resumen del plan aprobado;
     - el estado de la tarea (con sus reintentos) y el del grafo;
     - el commit en el que está el worktree (la base del intento) y los ficheros que la tarea cambió desde su base;
     - los contadores de las últimas pruebas que ejecutó el sistema (el registro no guarda los nombres de las que fallan);
     - los errores del intento anterior;
     - como dato del agente (`agent`), el resumen y lo pendiente de su último informe, sin verificar.

     Va en su sección del prompt («## Traspaso»), en lugar de `<previous_attempt>`, detrás de una línea que dice por qué
     se renovó, y la regla 4 del prompt del ejecutor lo nombra entre los bloques que son datos y no instrucciones.
  3. **El prompt, traspaso incluido, es la evidencia que ancla `InstructionsIssued`**, como cualquier prompt. El traspaso no
     se guarda aparte.
  4. **El traspaso no lleva memoria propia.** La memoria de confianza, si la política del agente la permite, sigue en su
     sección del prompt, y el `AgentRunStarted` del lanzamiento lo marca. El dosier del juez deja fuera todo prompt con
     memoria (adenda de [ADR-0022](0022-memoria-append-only-anclada.md)), así que el juez nunca recibe un traspaso con
     memoria. Uno sin memoria es evidencia de la tarea, como el resto de su prompt.
  5. **Lo que cuenta el traspaso sobrevive a un reinicio.** El bucle guarda, por tarea, las últimas pruebas y el último
     informe del agente, y la renovación pendiente. La reanudación los rehace del registro: la clase de un final v3 o el
     pico de uno v2, los registros de intento, y el informe tal como lo ancló el registro.
  6. **El motor decide por la clase**, como dejaba para WP1 la decisión 6 de la parte de WP2: `Workers` lee
     `Classified()` para la espera ([adenda de WP1 en ADR-0034](0034-esperas-por-limite-y-reanudacion-con-consentimiento.md)),
     para la sesión caducada y para la renovación.
- **Alternativas:**
  - **Un resumen que escribe el agente antes de renovar:** es texto de agente que nadie verifica, y cuesta un turno
    (docs/16 §2.15). Lo que dice el agente va como dato.
  - **Reanudar la sesión del intento interrumpido, o comprobarla antes:** comprobarla exige leer las carpetas del CLI, que
    Agent Studio no lee, y una sesión que no consta que se guardara puede no existir o estar a medias.
  - **Renovar a mitad del intento al pasar el umbral:** interrumpiría trabajo sano, y la compactación del CLI ya lo cubre.
  - **Texto libre en lugar de JSON:** el texto del agente (su resumen, sus errores) podría cerrar el bloque.
  - **Renovar todo fallo de un CLI que no informa del contexto:** sería adivinar. Sin pico, decide la clase.
- **Consecuencias:**
  - F8-6: con el contexto agotado, o con el pico del intento fallido en el umbral, el siguiente intento va en una sesión
    nueva con el traspaso anclado; nunca se reanuda la sesión de un intento interrumpido, y el juez no recibe un traspaso
    con memoria. Lo prueban los 7 casos de `HandoffTests`: el contexto agotado, los contadores de las pruebas, el umbral
    (85 % sí, 50 % no), el intento interrumpido tras reiniciar, el bloque que no se cierra desde dentro, y el dosier del
    juez (`RunLog.RegisteredEvidence`). Estaban en rojo antes del cambio, salvo el caso del 50 %.
  - Una sesión nueva empieza sin lo que el CLI tenía en la anterior: del intento anterior, el agente solo sabe lo que dicen
    el traspaso y el worktree.
  - El umbral depende de que el CLI informe del contexto: Claude sí; `codex exec`, no («no expuesto», ADR-0021).
  - **Sigue pendiente** lo que la parte de WP2 deja fuera: el tiempo agotado no dice que fue el tiempo (`AttemptBudget`
    `Time`). WP1 no lo cambió: el plazo del motor cancela el intento a través de `AgentRunner`, que es de WP2, y en esta
    fase no se cambia cómo se le llama.
  - **Sin verificar:** las clases y los picos que solo tienen capturas sintéticas (WP2), y si un CLI real trata el traspaso
    como dato.
