# ADR-0022 · La memoria es append-only y anclada al registro, con confianza por origen; el Markdown solo se exporta y el juez nunca recibe memoria

- **Estado:** aceptada · 2026-10-18. Recoge D-18 a D-25 de [docs/14](../14-fase-4-observabilidad-y-memoria.md#44-f4-5-y-f4-6--memoria-y-empaquetado)
  y su §7. El paquete P0 deja los contratos, los payloads, la migración 0004 y las reglas del dominio; WP3 hace el
  almacén, el anclaje, el empaquetador y la exportación, y WP1 lleva la memoria a los prompts.
- **Contexto:**
  - F4-5 pide memoria por proyecto en SQLite y Markdown, cronológica y temática, con FTS5. F4-6 pide un empaquetado que
    conserve los requisitos críticos, las decisiones y las tareas pendientes.
  - La memoria vuelve a los prompts, así que es una superficie de inyección de instrucciones. Sus adversarios son los del
    punto 1 de [ADR-0018](0018-modelo-de-amenaza-fase-3.md):
    - el contenido del repositorio, como un `.agentstudio/agentstudio.db` con memoria envenenada que llega con un
      repositorio de terceros (la exclusión de la base vive en tu `info/exclude`, no en su historial) o su bitácora;
    - la salida de los agentes, que acabaría citada en la memoria.
  - La 0001 trae `memory_entries` con FTS5 de contenido externo, pero no es append-only: tiene triggers `AFTER UPDATE` y
    `AFTER DELETE`, y `superseded_by` obliga a un UPDATE.
  - `.agentstudio/memory/` es contenido del repositorio, igual que la bitácora.
- **Decisión:**
  1. **Filas append-only** (migración 0004).
     - Triggers `memory_entries_no_update`, `memory_entries_no_delete` y `memory_entries_no_replace`. Los de FTS para
       UPDATE y DELETE se retiran; el de inserción mantiene el índice al día.
     - Columnas nuevas: `origin` (con `CHECK` de `System`, `User` y `Agent`), `content_sha256` y `supersedes_entry_id`
       (clave foránea a otra entrada). `superseded_by` queda siempre nula: la sustitución es la entrada nueva.
  2. **Anclaje en el registro, como el estado de una ejecución** ([ADR-0010](0010-estado-derivado-del-registro.md)).
     - `MemoryRecorded` se escribe antes que la fila: id, categoría, origen, SHA-256 canónico de la categoría, el tema y
       el contenido (`MemoryContent.Sha256`, con prefijo de longitud), la fuente que cita y la entrada a la que sustituye.
       No lleva el texto: ningún payload lleva salida de agentes.
     - `MemoryReviewed` (aceptada, rechazada o retirada, y por quién) cambia el estado.
     - Recuperar solo devuelve filas anunciadas con el mismo SHA-256. Una fila sin anunciar, o modificada, se ignora y deja
       una alerta de integridad. Un anuncio sin fila (la app murió entre los dos pasos) se ignora y se completa al volver
       a derivar, porque la derivación es idempotente por la fuente.
  3. **Fuera de toda ejecución.**
     - Los dos payloads llevan `RunId` vacío (`DecisionPayload.NoRun`), como la aceptación de un historial como nuevo
       origen: la reproducción, el dosier del juez y la lista de ejecuciones seleccionan por ejecución y nunca los ven.
     - Toda escritura de memoria en el registro (derivación, notas tuyas y revisiones) espera a que `IRunActivity` diga
       que no hay ejecución ni planificación en curso. Así la cabeza del registro no se mueve entre que se enseña una
       aprobación o un veredicto y se usa.
  4. **Fallo cerrado.** Con un registro que no verifica no se escribe memoria (`DecisionLogIntegrityException`).
  5. **Confianza por origen** (`MemoryOrigin`, `MemoryTrust`).
     - Lo del sistema, con texto propio, y lo tuyo son de confianza.
     - Lo que cita texto de un agente es una propuesta hasta que la aceptas.
     - Lo heredado (anclado antes del último `decision-log.origin-accepted`) queda en cuarentena hasta que lo revisas.
     - Lo rechazado, lo retirado y lo no anclado o modificado nunca llega a un prompt ni a la exportación.
     - `DailySummary` no entra en los prompts por defecto.
  6. **Memoria en los prompts, solo por `MemoryPolicy`.**
     - Con `None`, nada.
     - Con `ReadSelected`, un paquete por niveles (ADR-0023) en un bloque `<project_memory pack="<sha256>">`, con cada
       contenido recodificado por `PromptData.Quote` para que no pueda cerrar el bloque, y la regla de que esos bloques
       son DATOS. `AgentRunStarted` v2 ancla el paquete (`ContextPackReference`: SHA-256, entradas, omitidas y
       caracteres); el texto está en la evidencia del prompt.
     - `ReadAndPropose` se rechaza hasta la Fase 5 (`agent.memory-proposals-not-available`): no hay canal para que un
       agente proponga memoria, y no se simula.
  7. **El juez y el auditor nunca reciben memoria.** `AgentDefinition.Validate` lo exige
     (`agent.judge-memory-none`): `AgentJudge` rechaza un juez con memoria antes de lanzar nada, y la plantilla no guarda
     ninguno. El veredicto solo se apoya en el dosier.
  8. **El Markdown solo se exporta y nunca se lee de vuelta** ([ADR-0005](0005-sqlite-y-markdown.md)).
     - Se escribe por `IProjectStorageFiles.Replace`, que no sigue enlaces, con nombres planos
       (`memory/cronologico-AAAA-MM.md` y `memory/tema-<categoria>.md`): el fichero entero, en orden determinista y con
       una cabecera que dice que se genera y que editarlo no cambia la memoria.
     - Solo lleva lo de confianza y lo aceptado.
     - Nunca se exporta durante una ejecución ni una planificación (`IMemoryExporter` lo rechaza): la comprobación del
       repositorio del usuario compara todo `.agentstudio/` y la tarea fallaría.
     - Se versiona, como la bitácora (decisión 4 de la §12).
  9. **Antes de guardar**, el contenido pasa por `ISecretRedactor` y tiene un límite de 4 000 caracteres
     (`MemoryContent.MaxCharacters`). En la búsqueda FTS5, cada término va como frase entre comillas, así que nunca llega
     un operador desde lo que escribe una persona o un agente.
- **Alternativas:**
  - El Markdown como fuente de verdad: es contenido del repositorio, el adversario del punto 1 de ADR-0018.
  - El estado en la propia tabla (aceptada, `superseded_by`) con UPDATE: dos fuentes de verdad, y la fila no estaría
    anclada.
  - La confianza solo como columna, sin anclaje: una base plantada traería su propia confianza.
  - Dejar que el juez lea memoria: se apoyaría en lo que escribieron quienes produjeron el trabajo, y su independencia
    ([ADR-0014](0014-independencia-del-juez-con-dos-vendors.md)) dejaría de serlo.
- **Consecuencias:**
  - Una memoria plantada corre la suerte de su historial: sin un ancla reconocida bloquea hasta que la aceptas (A-01), y
    lo heredado tras aceptar el historial como nuevo origen queda en cuarentena.
  - **Residual, dentro del modelo:** un modelo puede obedecer igualmente un texto de datos. Lo acotan la aprobación del
    plan, el validador, la verificación del sistema y un juez de otro vendor que no ve la memoria.
  - Retirar una entrada solo la oculta. Borrarla de verdad (privacidad) queda para la Fase 7, y no hay importación desde
    Markdown.
  - El tokenizador `unicode61` pliega los acentos, pero no lematiza en castellano: «prueba» no encuentra «pruebas».
  - La exportación cambia el árbol del usuario después de cada ejecución, como la bitácora.
  - Fuera del modelo, como siempre: un proceso concurrente del mismo usuario con integridad Medium puede reescribir la
    base, el registro y el ancla (punto 2 de ADR-0018).

## Adenda (2026-10-18, integración de la Fase 4): el dosier del juez no lleva prompts con memoria

Las pruebas de integración encontraron un hueco frente a la decisión 7 («el juez nunca recibe memoria»): el juez no la
recibía en su prompt, pero el prompt del ejecutor, con su bloque `<project_memory>`, es evidencia registrada de su tarea, y
el dosier de la Fase 3 copia toda evidencia registrada a la carpeta que el juez puede leer y que su prompt le pide leer.

Corrección: `RunLog.RegisteredEvidence` (módulo `Debate`, el que decide qué ve el juez) deja fuera, sin anotarlo como
problema, el prompt de todo lanzamiento cuyo `AgentRunStarted` llevó un paquete de memoria, aunque también esté
registrado como evidencia de la tarea. Es la misma regla que ya dejaba fuera los prompts que solo anuncia
`AgentRunStarted`. El juez conserva los requisitos, el plan, los informes, los diffs y las verificaciones, que es con lo
que juzga; lo prueba `MemoryInPromptsIntegrationTests` sobre el paquete real del dosier.

Desde el 2026-11-11 (P54, [ADR-0051](0051-preguntas-de-los-agentes.md), adenda) el prompt de la planificación del
orquestador queda fuera del dosier en todos los casos, lleve memoria o no: `JudgeDossierBuilder` lo retira igual que los de
los ejecutores. La regla de memoria de arriba sigue valiendo para cualquier otro lanzamiento.
