# ADR-0026 · Un debate es una unidad del registro: id propio, cabecera inmutable anclada, estado reproducido, el texto de los agentes como evidencia y una sola actividad con agentes por proyecto

- **Estado:** aceptada · 2026-10-19. Recoge D-01 a D-05 de [docs/15](../15-fase-5-debate-arena-y-juez.md#41-un-debate-en-el-registro)
  y su §6. El paquete P0 deja el dominio (`DebateSession`, `DebateStateReplayer`, `DebateBudget`), los payloads, los
  contratos (`IDebateCoordinator`, `IDebateRun`, `IDebateRegistry`, `IProjectAuditRegistry`, `DebateHeaderHash`,
  `IProjectActivity`) y la migración 0005; WP1 hace el coordinador, WP4 los registros en SQLite y WP5 la actividad del
  orquestador sobre `IProjectActivity`.
- **Contexto:**
  - La Fase 0 dejó un `DebateSession` en memoria y un `IDebateCoordinator.RunAsync(projectId, DebateSession)`: un debate no
    se podía reanudar tras un reinicio y nada de él quedaba anclado.
  - La 0001 trae `debates`, `debate_participants`, `debate_contributions` y `debate_contribution_evidence`. `debates` tiene
    `phase`, `outcome` y `closed_at` mutables y sin triggers, y `debate_contributions` guarda el texto de los agentes fuera
    del almacén de evidencias. Serían dos fuentes que pueden divergir.
  - Una ejecución del orquestador ya resuelve lo mismo ([ADR-0010](0010-estado-derivado-del-registro.md)): una cabecera
    inmutable que `RunStarted` ancla por su hash, y el estado reproducido desde el registro verificado.
  - Un debate, una auditoría y una ejecución del mismo proyecto escriben en el mismo registro, y el motor compara el
    repositorio del usuario alrededor de cada paso.
- **Decisión:**
  1. **Id propio.** `deb-AAAAMMDD-xxxxxx` (el día en UTC y seis cifras hexadecimales que no estén ya en el registro), válido
     como `RunId`, va en el `RunId` de cada entrada del debate. Una auditoría de proyecto lleva `aud-…`. Así los selecciona el
     mismo `RunLog.EntriesOf` del juez y los cuenta el panel de consumo sin cambiarlo. Ninguno está en `orchestration_runs`.
  2. **Cabecera inmutable y anclada.** `debate_sessions` (y `project_audits` para la auditoría) guarda lo que no cambia: el
     modo, los requisitos, la evidencia de la pregunta, el objeto y el commit de la copia, y en `header_json` los participantes
     y los jueces con su `AgentDefinition` entera, la disposición, las dispensas, los límites, las ejecuciones máximas y el
     veredicto que impugna. Es append-only: `UPDATE`, `DELETE` y `INSERT OR REPLACE` abortan.
     - `DebateHeaderHash.Of` es su SHA-256 canónico, con el patrón de `RunHeaderHash`: campos con prefijo de longitud, cada
       agente con la codificación de `RosterHash` como plantilla de uno, los participantes y los jueces en su orden (decide el
       orden de publicación y qué juez lee las propuestas al revés) y los conjuntos de vendors ordenados.
     - `DebateOpened` (y `ProjectAuditStarted`) ancla ese hash. Reanudar y lanzar un juez exigen que la fila dé el hash anclado
       (N-05).
     - `DebateHeaderHash.Proposal` es el hash de lo que se te enseña antes de escribir nada (sin el id, la fecha ni la evidencia
       de la pregunta, que aún no existen, y con el SHA-256 de la pregunta). Tu aprobación (`debate.open`) se ata a él y
       `DebateOpened` lo repite junto al de la cabecera: el registro une la aprobación con el debate que se abrió.
     - Orden de escritura: la pregunta como evidencia (con el id del debate en `run_id`), la fila de la cabecera y después
       `DebateOpened`.
  3. **Estado reproducido.** `DebateStateReplayer` abre la sesión con la cabecera y le aplica, por la API pública de
     `DebateSession`, las contribuciones publicadas, los cierres de ronda, el envío a juicio y el cierre. Un registro que el
     dominio no habría permitido (una ronda cerrada sin juicio cuando el dominio llega a un límite, una ronda que no es la
     actual, una contribución fuera de su ronda) deja el debate sin reanudar, con el motivo. Una cabecera que no abre no da
     sesión.
  4. **El texto es evidencia.** Ningún payload lleva texto de un agente: la pregunta, cada contribución (el JSON validado y
     redactado), cada cita de código y tus razones son evidencias, y el registro guarda su id y su SHA-256. Quien las lee
     comprueba ese hash (R3-08).
  5. **Siete tipos de entrada nuevos, al final de `DecisionKind`** (`DebateOpened`, `DebateContributionRecorded`,
     `DebateRoundClosed`, `DebateClosed`, `VerdictChallenged`, `ProjectAuditStarted` y `ProjectAuditFinished`), y
     `ReplanRequested`, que existía sin payload, gana el suyo. Los nombres de los tipos anteriores no cambian (el hash cubre
     `kind.ToString()`), los payloads nuevos empiezan en `V = 1` y los v1 y v2 de las fases anteriores se leen y se escriben
     byte a byte.
  6. **Los límites los cuenta el registro.** `DebateBudget` suma la entrada (con la caché) y la salida de los turnos de los
     participantes desde sus `AgentRunFinished` v2, con el origen más débil; lo que falta (un turno sin resultado, un uso todo a
     cero, un CLI que no lo expone) cuenta como su estimación, los caracteres entre 4, redondeando hacia arriba. Nunca cuenta
     de menos. Los tokens del juez no cuentan para el límite.
  7. **Una sola actividad con agentes por proyecto.** `IProjectActivity` (Application) dice qué tiene el proyecto en este
     proceso: una ejecución o una planificación del orquestador, un debate o una auditoría. El coordinador, la auditoría y el
     orquestador entran antes de escribir nada y, si el proyecto está ocupado, se rechaza con el motivo. La planificación y la
     ejecución del orquestador pueden compartirlo, como hoy; un debate o una auditoría no lo comparten con nada.
     `IProjectActivity` es también la `IRunActivity` de la memoria, así que la memoria espera durante un debate; se mantiene
     el orden por el que una ejecución libera su proyecto antes de que se vea su fin, y la implementación no depende de nada
     (sin ciclo memoria → actividad).
- **Alternativas:**
  - **Las tablas de la 0001:** ya existen, pero su estado mutable y sin triggers sería una segunda fuente, y guardan el texto
    de los agentes fuera de las evidencias.
  - **El debate en memoria (`RunAsync(DebateSession)`):** sencillo, pero no se reanuda y nada queda anclado.
  - **Anclar solo la cabecera, sin el hash de la propuesta:** la aprobación quedaría atada a algo que no está en el registro.
  - **Debates y ejecuciones a la vez en el mismo proyecto:** la comprobación del repositorio del usuario y el registro
    compartido lo hacen arriesgado, y no hay nada que lo compense.
- **Consecuencias:**
  - Las cuatro tablas de debate de la 0001 y `verdicts.debate_id` quedan sin uso ([05 §5](../05-modelo-de-datos.md)); se
    pueden retirar más adelante con su propia migración.
  - Un debate se reanuda solo con tu OK, porque gasta cuota (D-04): los turnos abiertos se cierran como interrumpidos y lo
    sellado que se perdió con el proceso se pide otra vez.
  - Un debate largo bloquea el proyecto: lo acotan el límite de 30 minutos y «Abortar».
  - La manipulación sigue siendo evidente, no imposible ([ADR-0004](0004-registro-de-decisiones-append-only.md)): quien
    controle la base puede borrar los triggers; la cadena, el ancla del registro y el hash de la cabecera lo detectan.
