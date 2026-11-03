# ADR-0029 · El veredicto de un debate: el juez de la Fase 3 con su brief, el dossier a ciegas, la resolución estructurada y la valoración del juez en los modos de elección

- **Estado:** aceptada · 2026-10-19. Recoge D-23 a D-26 de [docs/15](../15-fase-5-debate-arena-y-juez.md#46-f5-4--veredicto),
  la disposición de [ADR-0027](0027-independencia-del-juez-en-debates-con-dos-vendors.md) y la diferencia de
  [docs/04 §17.8](../04-contratos.md#178-diferencias-con-docs15-5) sobre la familia de una impugnación. Lo implementa el
  paquete WP2 en `src/AgentStudio.Debate/Judging/Debates/` (`DebateDossierBuilder`, `DebateDossierDocument`,
  `DebateJudgePrompt`, `DebateJudgeBrief`, `DebateVerdictSchema` y `DebateJudgment`, que implementa `IDebateJudgment`) y en
  `VerdictJson` (la resolución). `AddAgentStudioJudging` registra el juicio de los debates junto a la revisión de una
  ejecución.
- **Contexto:**
  - El juez de la Fase 3 (`AgentJudge`, `VerdictValidator`, una corrección) estaba pensado para revisar una ejecución: su
    prompt, su esquema y su dossier hablan de un plan, unas tareas y un commit candidato. Un debate juzga contribuciones,
    a veces sobre una ejecución y a veces sin ningún código.
  - Con dos vendors el juez comparte a veces vendor con un participante (la dispensa de ADR-0027), y la literatura documenta
    la autopreferencia y el sesgo de posición de los jueces LLM (docs/15 §3.3).
  - `AgentJudge` añade a las reglas de lectura una sola carpeta (la del paquete) además de su directorio de trabajo: todo lo
    que el juez deba leer tiene que estar ahí.
  - Las adendas de [ADR-0022](0022-memoria-append-only-anclada.md): el juez nunca recibe memoria y ningún prompt que la
    llevó entra en su dossier.
  - Ningún contrato de P0 da la ruta de la copia del commit que preparan los turnos (WP1).
- **Decisión:**
  1. **El juez de la Fase 3, con el brief de un debate.** `DebateJudgeBrief` da la rúbrica por familia, el esquema con la
     resolución y la lectura de la respuesta (`VerdictJson.ParseDebate`). `AgentJudge` no cambia: solo lectura en los dos
     CLIs, quién juzgó y cuándo los pone el sistema, y una corrección que reanuda su sesión (por eso el juez sí guarda la
     suya). Sus lanzamientos se registran con `JudgeRunRecorder` (`JudgeRunScope.ForDebate`: el debate, la tarea
     `juez-<agente>` y el propósito `DebateJudge`) y sus veredictos con `VerdictKeeper` (alcance `Debate`). El juez nunca
     lleva memoria.
  2. **El dossier sale de los almacenes, con todo leído contra lo que ancló el registro** (`DebateDossierBuilder`):
     - la cabecera tiene que dar el hash que ancló `DebateOpened` y, con el SHA-256 de la pregunta, el de la propuesta que
       aprobaste (`DebateHeaderHash.Proposal`); si no, es una comprobación del sistema;
     - la pregunta se lee contra `DebateOpened`, cada contribución contra su `DebateContributionRecorded` y cada cita contra
       su `EvidenceRegistered`; lo que no cuadra o no se puede leer se retiene, con una comprobación;
     - solo entran las evidencias que el registro anunció para el debate (`RunLog.RegisteredEvidence`), sin los prompts de
       los lanzamientos y sin ningún prompt que llevó memoria aunque se registrara;
     - **no se lanza ningún juez** con el registro roto (y entonces no se escribe nada), sobre una cabecera que no verifica,
       con una disposición que el dominio no permite o en un vendor excluido (el real, el del proveedor): las tres últimas
       dejan un `AuditAlertRaised` en el debate (`DebateHeaderMismatch`, `DebateJudgingNotAllowed`, `JudgeNotIndependent`).
  3. **A ciegas, sobre lo que lee el juez** (D-15). Participantes por etiqueta y papel (`P1`, `Proponent`), contribuciones por
     etiqueta (`C1`…) y el protocolo por secuencia y tipo con una referencia que escribe el sistema. Los títulos y autores de
     las evidencias del debate pierden todo agente, nombre, CLI, vendor y modelo de un participante, y lo que produjo un
     participante se atribuye a su etiqueta. No llegan al juez la disposición, los vendors excluidos, las entradas
     `AgentRunStarted`/`AgentRunFinished`, los payloads crudos ni los motivos y fuentes de los cierres de ronda (nombran el
     CLI). El texto de las contribuciones no se toca: es la materia, y su hash es el anclado. La prueba recorre lo que lee el
     juez (los ficheros del paquete y los prompts de los dos asientos) con identidades distintivas. Sigue valiendo el límite
     de D-15: un modelo puede reconocer el estilo de su familia.
  4. **La resolución estructurada** (D-25). `Verdict.Resolution`: la comparación de cada propuesta y la elegida, o
     `"ninguna"`, el centinela que el subconjunto portable necesita en vez de un nulo. En el esquema la comparación va antes de
     la elección y las dos antes de la decisión. Sin propuestas que elegir, el esquema es el de la Fase 3 (un enum no puede
     estar vacío). `VerdictJson.Parse` sigue siendo el de la revisión de una ejecución y rechaza una resolución;
     `ParseDebate` la lee y exige dentro de ella una propuesta elegida que sea texto, porque un nulo se leería como
     «ninguna», que el juez no dijo. `ToJson` escribe la resolución solo cuando existe: el JSON de la Fase 3 queda byte a
     byte.
  5. **Qué se puede elegir.** En el modo libre y en la revisión de arquitectura, las propuestas de la ronda 1 de todos; en el
     adversarial, solo las de los proponentes (las del crítico son críticas); en el tribunal, ninguna (sintetiza); en los
     modos que revisan una ejecución, ninguna. El segundo juez de un jurado las lee al revés.
  6. **La familia decide las reglas.** `JudgeDossier.Debate.Choice` es la de `DebateJudgingRules.IsChoice`; solo en un
     dossier de elección un requisito cuyos criterios son todos `JudgeAssessment` puede apoyarse en las contribuciones.
     **`CandidateCommit` solo se pone cuando el objeto es una ejecución** (su commit candidato) o una auditoría impugnada (su
     HEAD): en un debate de elección sobre un commit no hay código que probar, y con él el validador exigiría una ejecución de
     pruebas de la copia para aprobar.
  7. **El objeto ejecución va entero.** El dossier de la Fase 3 de la ejecución (`IJudgeDossierPackageBuilder`) entra tal
     cual en el del debate, con sus productores a la vista, y sus evidencias se copian al paquete del debate (es la carpeta que
     el juez puede leer). La regla P suma siempre los productores de esa ejecución a los excluidos, aunque la cabecera no los
     traiga: no se dispensa nunca.
  8. **La impugnación** (D-38 a D-40 y docs/04 §17.8). El veredicto impugnado se lee del almacén y se compara con su
     `VerdictIssued` (unidad, tarea, decisión, validez, incidencias, a quién sustituye y los hashes del material, salvo el que
     sus propias incidencias dicen que no repitió); si no cuadra, no está o no se anunció, es una comprobación y ninguna
     aprobación pasa. Las razones se leen contra el hash de `VerdictChallenged`. El objeto se reconstruye: una ejecución, con
     su dossier de la Fase 3; un debate, a ciegas y con la ejecución que juzgaba; una auditoría, con su registro y sus
     evidencias. **La familia, el modo y las propuestas salen del objeto:** una ejecución, una auditoría o un objeto
     desconocido se revisan con las reglas de la Fase 3; un debate conserva su familia, su modo y sus propuestas, así que la
     revisión elige entre las propuestas del debate que revisa. Los excluidos del objeto se heredan. Los veredictos de la
     revisión se guardan en la unidad y el alcance del impugnado, y el primero de cada juez lo sustituye (`Supersedes`).
  9. **El jurado cruzado lee un solo dossier, a la vez.** Una cabeza del registro para los dos; el primero lee
     `dossier.json` y el segundo `dossier-reversed.json`, con las propuestas, los participantes y la ronda 1 al revés (las
     rondas siguientes conservan su cronología) y el enum del esquema en ese orden. Los veredictos se guardan de uno en uno y
     `JudgingPanel.Combine` los combina.
  10. **Un veredicto que no llega necesita a una persona** (D-26). Si el juez falla, alcanza su límite o el de uso de su
      proveedor, o responde algo que no es un veredicto, el resultado no trae veredicto suyo y `Combine` da un conjunto
      inválido. Nada sigue en silencio: un `AuditAlertRaised` en el debate dice por qué (`DebateJudgeNoVerdict`, con la tarea
      del juez), también cuando el lanzamiento ni siquiera se pudo registrar; su lanzamiento, si lo hubo, queda en el registro
      con cómo terminó, y sus eventos (`RateLimitSignaled` incluido) llegan al `progress` de quien llama. Solo la cancelación
      se lanza como excepción. Una corrección que no llega deja el primer veredicto con sus incidencias, que no es la
      corrección (`Corrected = false`), y su alerta (`DebateJudgeNoCorrection`).
  11. **Dónde trabaja el juez.** En una copia del commit del objeto dentro del paquete (`snapshot`), que hace la capa git
      endurecida (`IWorktreeManager.ArchiveCommitAsync`, solo ficheros y carpetas); sin capa git, sin commit o si la copia
      falla, en el paquete, y el prompt lo dice.
  12. **Un dossier grande es un índice** (R8). Por encima de 256 KB el documento conserva entero el texto de las
      contribuciones y las citas, y de la ejecución objeto da sus entradas sin payloads y el plan por referencia; la ejecución
      entera está en `subject-run.json`, en el paquete.
- **Alternativas:**
  - **Reutilizar el prompt y el esquema de la Fase 3:** hablan de un plan y un commit, no tienen resolución, y la revisión de
    una ejecución tiene que quedar igual.
  - **Pasar al juez las entradas del registro del debate tal cual:** `DebateOpened` lleva los vendors y modelos, y el turno de
    una contribución lleva el agente.
  - **Poner la copia del commit como `CandidateCommit` también en los modos de elección:** ningún debate de elección sobre
    un commit podría aprobarse sin pruebas de ese commit.
  - **En una impugnación de un debate, el modo y las propuestas del debate de revisión:** la regla «aprobar exige propuesta
    elegida» dejaría de aplicarse, y la revisión no podría sustituir el resultado del debate que revisa.
  - **Lanzar una excepción cuando el juez no responde:** el coordinador no tendría la combinación, y D-26 lo trata como un
    caso que necesita a una persona.
  - **Trabajar en la copia compartida de los turnos (WP1):** no hay contrato que dé su ruta; la integración puede cambiarlo.
- **Consecuencias:**
  - El juicio no depende del coordinador más que por identificadores: lo que ve el juez sale de los almacenes, y un registro
    o una cabecera que no cuadran no gastan cuota.
  - Para que un debate se pueda aprobar, toda fila de evidencia con el id del debate tiene que estar anunciada: con
    `EvidenceRegistered`, o como prompt de un `AgentRunStarted` o respuesta de un `AgentRunFinished` (la regla de la Fase 3).
    Una fila sin anunciar es una comprobación del sistema. Las razones de una impugnación que solo ancla `VerdictChallenged`
    se leen contra su hash, pero no son evidencia citable.
  - Cada juicio extrae una copia del commit (coste de disco y tiempo en repositorios grandes) y reescribe el paquete de la
    ejecución objeto de la Fase 3.
  - La auditoría impugnada se reconstruye con su registro y sus evidencias, no con el índice del dossier de la auditoría
    (WP3).
  - Deuda: `DossierPackageWriter` repite las reglas de escritura segura de `JudgeDossierBuilder` (sin enlaces, ficheros
    nuevos), porque allí son privadas; conviene unificarlas.

## Adenda (2026-10-20, integración de la Fase 5): la plantilla de la ejecución objeto no rompe el dossier a ciegas

Las pruebas de integración encontraron que, en una auditoría de implementación, el dossier copiaba el `RunStarted` de la
ejecución objeto con la plantilla entera del proyecto: agente, rol, CLI y vendor de todos sus agentes, también de los
participantes del debate. Con eso el juez podía deducir qué vendor estaba detrás de P1 o P2.

Corrección: el dossier del debate (el nodo de la ejecución y `subject-run.json`) solo conserva en esa plantilla los agentes
que trabajaron en la ejecución, los que lanzó o a los que asignó una tarea. Los productores siguen a la vista, como pide la
§7; los demás, no. Lo comprueba `DebateOnSqliteTests.The_judge_of_an_implementation_audit_reads_no_agent_of_its_participants`
sobre el paquete real que lee el juez.
