# ADR-0031 · Auditor determinista del orquestador: siete comprobaciones, alertas y petición de replanificación como ejecución de seguimiento

- **Estado:** aceptada · 2026-10-20. Recoge D-33 a D-35 y D-37 de [docs/15](../15-fase-5-debate-arena-y-juez.md#48-f5-6--auditoría-del-orquestador),
  F5-6 y la decisión 6 de su §12. Lo implementa WP3 en el módulo Debate (`Audit/`): `OrchestratorAudit` (funciones puras, una
  por comprobación en `Audit/Checks`) y `OrchestratorAuditService` (`IOrchestratorAuditService`). Cuándo corre y qué bloquea
  en la entrega es de WP5.
- **Contexto:**
  - El motor de la Fase 3 registra cada paso, pero un orquestador puede desviarse: asignar al juez, arrancar antes que una
    dependencia, saltarse una revisión, entregar sin aprobación o integrar sin pruebas.
  - Un registro puede estar roto (una fila editada o un final cortado) o ser válido e incumplir el protocolo (D-37). Son dos
    cosas distintas.
  - No hay replanificación a mitad de una ejecución (ADR-0013).
- **Decisión:**
  1. **Funciones puras sobre una ejecución del registro verificado.** Se leen sus entradas, el plan, la cabecera y los
     requisitos por los hashes que ancló `RunStarted`, y el contenido de lo que las comprobaciones leen (prompts y registros
     de intento), solo si da el SHA-256 anclado. Si el registro no verifica, no se escribe nada y el informe es la verificación.
  2. **Las siete comprobaciones**, con su código y su gravedad:
     - `orchestrator.assignment` (alta): cada asignación es de una tarea creada, la del plan aprobado, a un agente de la
       plantilla; el orquestador solo planifica y solo los jueces y auditores revisan; ningún revisor corre en un vendor
       productor; cada ejecutor lanzado es el asignado, en el proveedor y el vendor de la plantilla.
     - `orchestrator.dependency-order` (alta): ninguna tarea sale de pendiente (a en curso, a esperando aprobación o a
       bloqueada), recibe instrucciones ni lanza su agente antes de estar lista. La regla es la de `TaskGraph`, aplicada a los
       estados que registró el motor, con la propagación de los saltos. **Un arranque fuera de orden es un solo hallazgo**:
       el del orden; la reproducción no lo repite.
     - `orchestrator.instructions` (media): `InstructionsIssued` antes de cada lanzamiento de un ejecutor, con los `Covers` y
       el `WriteScope` del plan; su prompt, leído como se ancló, lleva el objetivo y el texto literal de los requisitos que cubre.
     - `orchestrator.review-omitted` (alta): ningún `ReviewSkipped`; una revisión completa solo tras su `ReviewRequested` y su
       `VerdictIssued`, o la aprobación de la persona a la que escaló; con la ejecución terminada bien, cada tarea que escribió
       código de un agente cuya política pide revisión tiene una revisión completa después en el plan.
     - `orchestrator.permissions` (alta): cada `PermissionDenied`; cambios en una tarea que no podía escribir; rutas fuera del
       alcance de escritura en un intento que se aceptó; un registro de intento ilegible.
     - `orchestrator.approval-missing` (crítica): la aprobación de una persona antes del plan, de cada entrega y de cada borrado
       con cambios, que responde a su `ApprovalRequested`. El registro no lleva el `Binding`: «atada» es lo que el registro puede
       enseñar (acción, orden, persona y una aprobación por entrega). Los nodos `HumanApproval` no se comprueban.
     - `orchestrator.integration-unverified` (alta): **no exige más de lo que la ejecución tenía que verificar.** Cada
       integración aceptada se juzga con los pasos que su tarea nombra en el plan aprobado, del tipo que les da el perfil
       congelado que ancló `RunStarted` (ADR-0016): un `TestRun` en verde del commit integrado si un paso lo pide; evidencia
       del tipo de cada otro paso (un `BuildLog`) de ese commit; todo antes de que nada dependa de él y antes de entregarlo. Sin
       pruebas que pedir, la falta de un `TestRun` no es un hallazgo, ni siquiera informativo.
     - Además, `orchestrator.replay` (alta; media para una cabecera de versión 1, que no ancla hash): el plan, la cabecera o el
       perfil no son los anclados, dos `RunStarted`, un payload ilegible o una transición que `RunStateReplayer` rechaza por
       algo que no es el orden, con la entrada en la que se para. Y `orchestrator.premature-finish` (alta): un
       `RunFinished(Succeeded)` con tareas sin completar.
  3. **Mensajes en inglés**, como las alertas del motor. El comentario de `OrchestratorFinding` en P0 dice «en castellano»: no se
     ha tocado (es de P0) y queda anotado.
  4. **Idempotente.** Cada hallazgo nuevo deja un `AuditAlertRaised` (actor `Auditor`, `auditor-orquestador`, con el código).
     Uno ya anotado con el mismo código y mensaje no se repite. El servicio serializa las auditorías de cada proyecto.
  5. **Pedir replanificar es proponer una ejecución de seguimiento** (D-35). Con algún hallazgo alto o crítico nuevo, o con
     alguno y ninguna petición todavía, deja un `ReplanRequested` (actor `Auditor`) con los códigos, las tareas y el mensaje,
     en la unidad de la ejecución. Nunca corrige, borra ni reescribe nada. Con la decisión 6, la petición bloquea la entrega
     hasta `replan.dismiss` o una ejecución de seguimiento (WP5).
- **Alternativas:**
  - **Solo la revisión del orquestador por un juez (D-36):** gasta cuota y no es determinista. Las dos conviven: el modo de
    debate cita el informe del auditor.
  - **Exigir siempre pruebas en la integración:** falsos positivos en proyectos sin pruebas.
  - **Informar de todo lo que rechaza la reproducción:** un arranque fuera de orden saldría dos veces.
- **Consecuencias:**
  - Sin falsos positivos en registros reales del motor: cinco escenarios del motor y una propiedad sobre 120 mundos aleatorios
    (semillas fijas). Cada comprobación tiene su registro preparado con el registrador real, y un registro limpio no da nada.
  - Las acciones (`plan.approve`, `deliver-integration`, `discard-run-work`, `orchestration.review-escalation`) y el formato
    del registro de intento se duplican de Orchestration, que el Debate no puede referenciar (ADR-0002). Unas pruebas los
    fijan contra las constantes del motor y de `DeliveryService`.
  - La copia de la regla de `TaskGraph` puede derivar del dominio. La prueba de propiedad es la que lo detecta.
  - Sin verificar: que un `BuildLog` pasó. El registro no lo dice; la regla confía en que el integrador no acepta una
    verificación que falló.
