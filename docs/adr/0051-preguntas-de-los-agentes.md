# ADR-0051 · Las preguntas de los agentes: una salida del informe con opciones, su registro, la continuación con la respuesta como dato y su seguridad; el juez nunca pregunta

- **Estado:** aceptado · 2026-11-09 (propuesto el 2026-11-06 como esqueleto del P0 de la Fase 13; lo completa **WP4**,
  preguntas e instrucciones en el motor, y la ventana es de WP8). Recoge D-28 a D-33 de
  [docs/17 §4.7](../17-constructor-visual-de-agentes.md#47-preguntas-de-los-agentes-con-opciones), D-10 y D-42 (la ficha y
  las instrucciones en el prompt) y la decisión 4 (a) que aceptaste el 2026-11-06. **Amplía**
  [ADR-0013](0013-protocolo-del-orquestador.md), [ADR-0035](0035-final-clasificado-y-renovacion-del-contexto.md) y
  [ADR-0010](0010-estado-derivado-del-registro.md) (la reproducción aprende las preguntas).
- **Contexto:**
  - P9 pide que las dudas de los agentes salgan como ventanas con opciones, la recomendada marcada y el coste de cada una.
  - Hasta ahora un agente bloqueado dejaba su duda en texto libre, y «Reintentar» lo relanzaba sin ningún texto
    (`RetryBlockedAsync`): no sabía qué decidiste (docs/17 §0.2).
  - Las instrucciones de cada agente (`SystemInstructions` y `Objectives`) solo iban al hash de la plantilla, y la ficha del
    proyecto no llegaba a ningún prompt (D-10 y D-42).
- **Decisión:**
  1. **Una pregunta es una salida del informe, igual en los dos CLIs** (D-28). El esquema del informe gana `question`,
     obligatoria y anulable como pide el modo estricto: texto, por qué, de dos a cinco opciones (id, etiqueta, detalle,
     esfuerzo `small`/`medium`/`large` y si es la recomendada) y `allowOther`. El esquema medido pasa de 575 a 1 142 bytes
     (UTF-8, compacto), lejos de los 16 KB. Un informe guardado antes, sin `question`, se lee igual. El motor valida al leer
     (`ExecutorReport.Parse`): la pregunta solo va con `status: blocked`, cada esfuerzo es de la lista y cumple
     `QuestionRules`; si no, el informe no es válido y el intento falla como hoy. `blocked` sin pregunta sigue siendo lo de
     siempre (`AwaitingHuman`). Los secretos se redactan (`ISecretRedactor`, puerto opcional del motor) y la pregunta
     redactada se vuelve a validar: un marcador puede alargar un texto. El prompt del ejecutor explica cómo preguntar.
  2. **En el registro, siempre en este orden** (D-29), y solo lo escribe el bucle, el único consumidor de su canal:
     - preguntar: la pregunta entera como evidencia (`AgentStatement`, informada por el agente, después de redactar),
       `QuestionAsked` mientras el intento que preguntó sigue `Running`, y luego `Running → Blocked(AwaitingAnswer)`; el
       monitor oye `QuestionPendingEvent`;
     - responder: tu texto propio como evidencia tuya (`HumanNote`, `ProvidedByHuman`), `QuestionAnswered` (tuyo, o del
       sistema por el consentimiento), la decisión para el juez como evidencia del sistema, y luego
       `Blocked(AwaitingAnswer) → Pending` con tu nombre; el monitor oye `QuestionAnsweredEvent`.
     El id de la pregunta es la tarea y su número (`T3-q1`), único en la ejecución. Ningún payload lleva texto del agente.
  3. **Topes** (D-29): tres preguntas por tarea y diez por ejecución, contadas en el registro. La siguiente no se registra ni
     se enseña: la tarea queda `Blocked(AwaitingHuman)` con «demasiadas preguntas, revisa los requisitos».
  4. **La tarea sigue con tu respuesta** (D-30): `IOrchestrationRun.AnswerAsync` va al bucle por el mismo canal que las
     demás órdenes (sin locks) y termina cuando la respuesta está en el registro, o falla con el motivo: otra pregunta, una
     opción que no existe, texto propio en una pregunta que no lo admite, una respuesta sin tarea (las del orquestador van a
     la planificación siguiente, WP1), la recomendada por consentimiento desde fuera, una ejecución terminada (nunca se da por
     respondida una pregunta que el registro no enseña). «Reintentar» una tarea que espera respuesta se rechaza: relanzarla
     sin la respuesta es justo lo que se quería evitar. Responder no es aprobar: no pide Windows Hello ni concede nada.
  5. **La respuesta llega como dato, nunca como instrucción.** Cada intento posterior de la tarea recibe todas sus preguntas
     respondidas, cada una en un bloque `<human_answer question=… task=… attempt=… evidence=… sha256=…>` con un único JSON
     escrito por `PromptData`: la pregunta y su porqué, la opción y su etiqueta o tu texto, quién decidió y cómo. Ningún texto
     (la etiqueta del agente, tus palabras con saltos de línea) puede cerrar el bloque, y el prompt dice que son datos, que no
     dan permisos ni cambian alcance ni requisitos. Hay una prueba de inyección con una opción y una respuesta que intentan
     cerrar el bloque y dar órdenes.
  6. **La sesión** (decisión 4 (a)): el intento con la respuesta reanuda la sesión del intento que preguntó, que terminó bien.
     Va en una sesión nueva con el traspaso de ADR-0035 cuando ese intento no dejó sesión (`SessionNotResumable`, un motivo
     nuevo) o su contexto llegó al umbral de compresión (`ContextNearlyFull`). El traspaso lleva `humanAnswers` (pregunta,
     evidencia, huella y elección) y las respuestas van además en sus bloques. El intento cuenta en `MaxAgentRuns`.
  7. **Reiniciar en mitad de una pregunta** (D-29): lo reproducido es lo vivido. `RunStateReplayer` gana `QuestionPosed`,
     `QuestionResolved` y el motivo de cada bloqueo en `TaskTransitioned`; comprueba que una tarea solo espera respuesta tras
     una pregunta, que solo sigue tras la respuesta y que la respuesta es de la pregunta pendiente, y da
     `RunReplay.PendingQuestions`. Al reanudar, la pregunta se lee de su evidencia solo como la ancló `QuestionAsked` (si no,
     la ejecución no se reanuda), sigue esperando con la misma hora y las mismas estimaciones, y se anuncia otra vez. Una
     respuesta registrada justo antes de cerrarse se termina: la tarea pasa a `Pending` y se captura la decisión que faltara.
  8. **Contestar sin ti** (D-33, decisión 4 (a)): solo con `RunOptions.AnswerWithRecommendedAfter` en la cabecera, solo para
     una pregunta con recomendada, pasado el plazo desde que el registro la anotó, y nunca mientras se cancela o con la
     integridad rota. Lo registra el sistema (`RecommendedByConsent`, `agent-studio`).
  9. **El coste de cada opción sin inventar nada** (D-32): con un estimador (`IUsageEstimator`, puerto opcional del motor), una
     estimación por opción según el esfuerzo que declara el agente, guardada como `OptionEstimateSnapshot` en
     `QuestionAsked` y la misma en `PendingQuestion`. Con menos de tres referencias, desconocida. **Nunca se suman los
     percentiles** de entrada y salida, que no son los del total: mientras `UsageEstimate` no dé el intervalo del total, los dos
     extremos van como «no disponible» con las referencias y el motivo `estimate.total-tokens-not-estimated`. Sin estimador, o
     si falla, la pregunta no lleva estimaciones.
  10. **Las instrucciones y la ficha en el prompt** (D-10, D-42, F13-4): `SystemInstructions` y `Objectives` del agente van en
      `<agent_instructions agent=… sha256=…>` y la ficha vigente (`IProjectBrief`, puerto opcional) en
      `<project_brief entry=… sha256=…>` (la huella que anunció su entrada de memoria), cada texto recodificado con
      `PromptData.Quote`. Guían el trabajo, pero no dan permisos ni cambian las reglas. Para que sean datos y no órdenes, el
      motor ya **no** las pasa además como prompt de sistema del CLI: la petición del ejecutor lleva `SystemInstructions` vacío.
      Un registro que no verifica no lanza nada (la ficha es memoria anclada).
  11. **El juez solo ve tu elección** (D-33): el dosier deja fuera, en silencio como los prompts con memoria (ADR-0022), los
      prompts de los ejecutores (con la ficha, las instrucciones y las respuestas), la pregunta tal como la escribió el agente
      y el informe del intento que preguntó (los dos con la recomendada y los esfuerzos), y las entradas `QuestionAsked` (con
      la recomendada y las estimaciones). De cada pregunta recibe `QuestionAnswered`, tu texto si lo hay y la decisión que
      capturó el sistema: la pregunta, su porqué y sus opciones como las escribió el agente, y lo que elegiste. El juez nunca
      pregunta: si duda, escala (`NeedsHuman`).
- **Alternativas:**
  - Las herramientas de pregunta de cada CLI (`AskUserQuestion`, `request_user_input`; D-28).
  - Siempre una sesión nueva con traspaso (decisión 4 (b)): más limpia, más cara.
  - Contestar siempre pasado un plazo (decisión 4 (c)), o nunca (4 (b)).
  - Dar al juez la pregunta tal como la escribió el agente (D-33 lo pedía «como evidencia delimitada»): lleva la recomendada,
    que D-33 le prohíbe; la decisión capturada por el sistema cumple las dos cosas.
  - Detectar qué prompts llevan ficha o instrucciones por `AgentRunStarted` v3 (sus partes, WP3): depende de otro paquete y
    no cubre las ejecuciones guardadas antes; dejar fuera todos los prompts de los ejecutores es determinista y cierra en
    falso. El juez tiene el plan, los requisitos, los informes y las evidencias objetivas, que es lo que juzga.
  - Sumar los percentiles de entrada y salida para tener un total: un número falso.
- **Consecuencias:**
  - Una tarea puede esperar tu respuesta indefinidamente; la fase de la ejecución es «Esperando a una persona» y un reinicio
    no la corta. Las demás tareas siguen.
  - El juez ya no ve los prompts de los ejecutores de ninguna ejecución, tampoco los que no llevaban nada tuyo.
  - Lo que queda abierto: la reanudación real con cada CLI (`--resume` y `exec resume`) tras una pregunta solo está probada
    con agentes guionizados, y la medición en vivo, con tu OK, es de la integración; el intervalo de tokens totales del
    estimador (WP3) para que las estimaciones dejen de ser «no disponibles»; la composición de los tres puertos nuevos
    (`IProjectBrief`, `ISecretRedactor`, `IUsageEstimator`) en `Desktop`, del lead; y la ficha, que WP3 también da en el paquete
    de contexto (`ContextPack.Brief`, `ProjectBriefBlock`): al integrar debe quedar un solo bloque y una sola lectura.
