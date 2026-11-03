# ADR-0014 · Independencia del juez con dos vendors: orquestador y ejecutores en uno, juez en el otro

- **Estado:** aceptada · 2026-10-16
- **Contexto:**
  - [ADR-0006](0006-juez-independiente-verificable.md) pide que el juez corra en un vendor distinto de quienes
    produjeron el trabajo y del orquestador. `VerdictValidator` lo comprueba con `JudgeNotIndependent` contra
    `JudgeDossier.ExcludedVendors`.
  - En la Fase 3 solo hay dos vendors con sesión: Anthropic (Claude Code, con la suscripción Max del usuario, la de más
    cuota) y OpenAI (Codex). Gemini está aparcado ([ADR-0009](0009-google-aparcado.md)).
  - La propuesta original de [docs/13](../13-fase-3-orquestacion.md) ponía al juez en Claude, el vendor del orquestador,
    y solo excluía el del ejecutor. Así el juez revisaría un plan hecho por su mismo vendor, con sus mismos sesgos. La
    decisión 7 se enmendó.
- **Decisión:**
  1. **`ExcludedVendors` son los vendors de todos los que produjeron lo revisado**, y se calcula desde el registro de
     la ejecución (`RunLog.ProducerVendors`). Nadie lo pasa como dato:
     - los orquestadores de la plantilla que registra `RunStarted` y el autor del plan (`PlanCreated`);
     - los agentes asignados (`TaskAssigned`) a tareas que no son `Review`;
     - todo agente lanzado para la ejecución (`AgentRunStarted`), con el vendor en el que corrió de verdad.

     Los jueces y auditores de la plantilla revisan y no cuentan, así que una revisión anterior no hace que el juez se
     excluya a sí mismo. Un agente de la plantilla que no trabajó tampoco cuenta.
  2. **Con dos vendors, el orquestador y los ejecutores comparten uno y el juez usa el otro.** En el E2E de F3-8,
     Claude orquesta y ejecuta y Codex juzga, de modo que `ExcludedVendors = {anthropic}`.
     - `PlanValidator` rechaza una plantilla en la que el juez comparta vendor con el orquestador o con un ejecutor
       (`JudgeVendorNotIndependent`).
     - Si aun así llega a la revisión, `IndependentReview` no lanza al juez: registra `AuditAlertRaised`
       (`JudgeNotIndependent`) y devuelve `NeedsHuman` sin gastar cuota.
  3. **El juez preferido es Codex**, con `codex exec -s read-only` y el esquema del veredicto en `--output-schema`.
     Sigue siendo configurable: si la plantilla pone al juez en Claude, se lanza con `--permission-mode dontAsk`, con
     `Read`, `Grep` y `Glob` permitidas y `Write`, `Edit`, `NotebookEdit`, `Bash`, `WebFetch` y `WebSearch` denegadas.
     **Nunca con `plan`**, que cambia de modelo y cuesta más (Fase 2).
  4. **La identidad del juez la pone el sistema**: el agente, el proveedor y el vendor que corrieron de verdad, y el
     modelo que anunció la sesión (`SessionStarted`). La hora del veredicto también. El juez solo repite los dos hashes.
  5. **ADR-0006 no cambia.** Esta decisión lo aplica con los dos vendors disponibles.
- **Por qué Codex en solo lectura como juez:**
  - **El solo lectura lo impone el sandbox de Codex**, no una lista de herramientas: aunque el modelo intentara
    escribir, el proceso no debería poder. En Claude depende de `dontAsk` y de las listas del CLI, y los conectores de
    la cuenta aparecen entre sus herramientas sin que se haya ejercitado su denegación (spike S3). Que el sandbox de
    Codex bloquee de verdad una escritura en Windows tampoco se ha ejercitado: en la prueba en vivo el juez solo leyó.
  - **`--output-schema` usa el modo estricto de OpenAI**: la forma del veredicto la garantiza la decodificación, y el
    validador se dedica a la coherencia con el dossier.
  - **Cuota**: lo que más ejecuciones gasta (planificar y ejecutar, con reintentos) va a la suscripción con más cuota.
    El juez es una ejecución, dos si hay corrección.
  - **Independencia completa**: el juez no comparte vendor ni con quien hizo el plan ni con quien escribió el código.
- **Alternativas:**
  - Juez en Claude, orquestador en Claude y ejecutor en Codex (la propuesta original): el juez compartiría vendor con
    el plan. Rechazada.
  - Excluir solo el vendor de los ejecutores: deja fuera al orquestador, que también produce lo revisado (el plan).
  - Desactivar la independencia (`JudgePolicy(RequireVendorIndependence: false)`): exige una decisión explícita y
    registrada; no sirve para el flujo normal.
  - Un tercer vendor como juez (Gemini): aparcado (ADR-0009).
  - `plan` como modo de solo lectura de Claude: otro modelo y más coste.
- **Consecuencias:**
  - En la plantilla por defecto el ejecutor es Claude, no Codex. El spike S2 (Codex escribiendo en un worktree) sigue
    valiendo porque la plantilla es configurable.
  - Si el usuario pone un ejecutor en OpenAI, un juez Codex deja de ser independiente: el plan no valida y, si llegara a
    la revisión, el juez no se lanza. Con un solo vendor disponible no hay juez independiente posible y la revisión
    queda en `NeedsHuman`.
  - `RunStartedPayload` registra la plantilla con rol y vendor de cada agente: es lo que permite calcular
    `ExcludedVendors` solo desde el registro.
  - El juez de Codex lee el paquete del dossier fuera de su carpeta de trabajo (`-C`, el workspace). Está verificado:
    en la prueba en vivo del 2026-10-16 (`JudgeLiveTests`, Codex 0.160.0, una ejecución de 99 s) el juez leyó con
    `pwsh` los ficheros del paquete en `%TEMP%`, volvió a calcular sus SHA-256 y devolvió un `Approved` válido a la
    primera. Aun así, lo esencial (requisitos, tareas, resúmenes de las pruebas) viaja también en el propio prompt.
  - Sin verificar: que `codex exec … resume` respete `--output-schema` en la ronda de corrección. En la prueba en vivo
    no hizo falta corregir.
