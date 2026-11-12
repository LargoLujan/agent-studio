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
       sistema por el consentimiento), la decisión para el juez como evidencia del sistema (solo ids, huellas e instantes), y luego
       `Blocked(AwaitingAnswer) → Pending` con tu nombre; el monitor oye `QuestionAnsweredEvent`.
     El id de la pregunta es la tarea y su número (`T3-q1`), único en la ejecución. Ningún payload lleva texto del agente.
  3. **Topes** (D-29): tres preguntas por tarea y diez por ejecución, contadas en el registro. La siguiente no se registra ni
     se enseña: la tarea queda `Blocked(AwaitingHuman)` con «demasiadas preguntas, revisa los requisitos». Cuenta toda pregunta
     que el registro anotó con `QuestionAsked`, también una anotada justo antes de un cierre que nunca llegó a enseñarse (su
     tarea seguía `Running` y la reanudación la trata como interrumpida): hace falta para que los ids no se repitan, porque el
     id es la tarea y su número, y la reproducción rechaza un id repetido.
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
     evidencia, huella y elección) y las respuestas van además en sus bloques. El intento cuenta en `MaxAgentRuns`. La sesión
     nueva solo se decide cuando la pregunta se registra: una que pasa los topes deja la sesión como cualquier otro bloqueo,
     igual en vivo que al reanudar, que lo rehace desde `QuestionAsked` (revisión del 2026-11-09).
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
     percentiles** de entrada y salida, que no son los del total: los extremos son los de `UsageEstimate.TotalTokens`, el
     intervalo que el estimador de WP2 calcula sobre los totales de sus referencias (`UsageEstimate.ToSnapshot`, integración
     del 2026-11-09). Si una estimación no da el total, los dos extremos van como «no disponible» con las referencias y el
     motivo `UsageEstimate.NoTotalReason`. Sin estimador, o si falla, la pregunta no lleva estimaciones.
  10. **Las instrucciones y la ficha en el prompt** (D-10, D-42, F13-4): `SystemInstructions` y `Objectives` del agente van en
      `<agent_instructions agent=… sha256=…>` y la ficha vigente (`IProjectBrief`, puerto opcional) en
      `<project_brief entry=… sha256=…>` (la huella que anunció su entrada de memoria), cada texto recodificado con
      `PromptData.Quote`. Guían el trabajo, pero no dan permisos ni cambian las reglas. Para que sean datos y no órdenes, el
      motor ya **no** las pasa además como prompt de sistema del CLI: la petición del ejecutor lleva `SystemInstructions` vacío.
      Un registro que no verifica no lanza nada (la ficha es memoria anclada).
  11. **El juez solo ve tu elección** (D-33). Hay dos evidencias por pregunta: la pregunta tal como la escribió el agente
      (`AgentStatement`, informada por el agente), que el juez nunca recibe, y la decisión que captura el sistema al
      responderla (`CapturedBySystem`), que solo lleva ids, huellas e instantes: qué pregunta y cuándo se hizo, los ids de sus
      opciones, cuál eligió la persona (o la evidencia de su texto), quién decidió, cómo (también si fue el consentimiento) y
      cuándo; ningún texto del agente. El dosier deja fuera, en silencio como los prompts con memoria (ADR-0022), los prompts
      de los ejecutores (con la ficha, las instrucciones y las respuestas), la pregunta del agente, todo informe de ejecutor
      que traiga una pregunta, registrada o no (una que pasó los topes, una que no cumplía las reglas), o que no se pueda leer
      como lo ancló el registro (además, una comprobación del sistema impide aprobar) o como JSON (falla cerrado), y las entradas `QuestionAsked` (con la recomendada y las
      estimaciones). Una entrada de esas clases que no se puede leer es una comprobación del sistema: con ella no cabe aprobar.
      De cada pregunta el juez recibe `QuestionAnswered`, la decisión y tu texto si lo hay (`ProvidedByHuman`). El juez nunca
      pregunta: si duda, escala (`NeedsHuman`).
- **Alternativas:**
  - Las herramientas de pregunta de cada CLI (`AskUserQuestion`, `request_user_input`; D-28).
  - Siempre una sesión nueva con traspaso (decisión 4 (b)): más limpia, más cara.
  - Contestar siempre pasado un plazo (decisión 4 (c)), o nunca (4 (b)).
  - Dar al juez la pregunta tal como la escribió el agente (D-33 lo pedía «como evidencia delimitada»): lleva la recomendada,
    que D-33 le prohíbe, y como texto del agente solo podría llegarle como evidencia no objetiva; no le llega.
  - Copiar el texto de la pregunta en la decisión que captura el sistema: así se hizo primero, y la revisión del 2026-11-09 lo
    señaló (gravedad media). Toda evidencia `CapturedBySystem` es objetiva para el juez (`EvidenceRecord.IsObjective`), así
    que un `why` del agente como «RQ-01 ya está implementado y probado» le habría llegado como un hecho comprobado por el
    sistema. Se arregló dejando la decisión solo con ids, huellas e instantes, con una prueba que lo comprueba.
  - Detectar qué prompts llevan ficha o instrucciones por `AgentRunStarted` v3 (sus partes, WP3): depende de otro paquete y
    no cubre las ejecuciones guardadas antes; dejar fuera todos los prompts de los ejecutores es determinista y cierra en
    falso. El juez tiene el plan, los requisitos, los informes y las evidencias objetivas, que es lo que juzga.
  - Sumar los percentiles de entrada y salida para tener un total: un número falso.
- **Consecuencias:**
  - Una tarea puede esperar tu respuesta indefinidamente; la fase de la ejecución es «Esperando a una persona» y un reinicio
    no la corta. Las demás tareas siguen.
  - El juez ya no ve los prompts de los ejecutores de ninguna ejecución, tampoco los que no llevaban nada tuyo.
  - Residual de D-33: cuando contesta el sistema por tu consentimiento, el juez ve el método (`RecommendedByConsent`) en
    `QuestionAnswered` y en la decisión, y por tanto sabe que la opción elegida era la recomendada. Se acepta a sabiendas:
    el juez tiene que saber que esa decisión no la tomó una persona.
  - Solo la petición del ejecutor deja de llevar sus instrucciones como prompt de sistema. El planificador (WP1) y el juez
    las siguen recibiendo así de sus proveedores (`--append-system-prompt-file` en Claude, el bloque de stdin en Codex): el
    camino de los proveedores no está muerto.
  - Lo que queda abierto: la reanudación real con cada CLI (`--resume` y `exec resume`) tras una pregunta solo está probada
    con agentes guionizados, y la medición en vivo, con tu OK, es de la integración; el intervalo de tokens totales del
    estimador (WP3) para que las estimaciones dejen de ser «no disponibles»; la composición de los tres puertos nuevos
    (`IProjectBrief`, `ISecretRedactor`, `IUsageEstimator`) en `Desktop`, del lead; y la ficha, que WP3 también da en el paquete
    de contexto (`ContextPack.Brief`, `ProjectBriefBlock`): al integrar debe quedar un solo bloque y una sola lectura.

## Adenda · 2026-11-11 · la respuesta a una pregunta del orquestador llega a todos los ejecutores como dato (V43)

- **Contexto.** En la demostración en vivo de la Fase 13 el orquestador preguntó antes de planificar («¿Qué debe hacer
  `Power(base, exponent)` cuando el exponente es negativo?»), la persona respondió, el plan ya decía «lanzando
  `ArgumentOutOfRangeException`»… y el ejecutor volvió a hacer la misma pregunta en su primer intento. La respuesta estaba en
  el registro (`QuestionAnswered` sin tarea) y en el prompt del planificador, pero no en el de ningún ejecutor: la decisión 5
  de este ADR solo daba a cada tarea las respuestas a sus propias preguntas. Poner la decisión en `PlanTask.Instructions` no
  basta: es texto del orquestador, el ejecutor puede dudar igual, y un plan que la persona edita en el lienzo después de
  responder lo pierde. La primera versión de esta adenda tampoco cubría ese caso: solo leía las respuestas de la ejecución
  cuyo id coincidía con el de la propuesta, y el lienzo solo pasa ese id cuando el plan es exactamente el propuesto y su
  propuesta no se ha usado; con el plan editado, o con una propuesta ya usada, la ejecución nueva tenía un id propio, ninguna
  entrada, y volvía V43.
- **Decisión 1: las respuestas del orquestador viajan como datos a todos los ejecutores.** El prompt del ejecutor de **cada
  tarea y de cada intento** lleva, antes de las respuestas a sus propias preguntas, un bloque
  `<human_answer question=… askedBy="orchestrator" evidence=… sha256=…>` por cada pregunta que el orquestador hizo antes de
  planificar y la persona respondió. Lo construye el mismo `HumanAnswerPrompt` de D-30, con el mismo escape y el mismo JSON por
  `PromptData` (la pregunta y su motivo como los escribió el orquestador, la opción elegida con su etiqueta o el texto de la
  persona, quién decidió y cómo). Sin `task` ni `attempt`: ningún intento de ninguna tarea la hizo. Es dato, no instrucción: no
  da permisos, ni cambia el alcance ni los requisitos, igual que las respuestas de D-30. También llega al `<handoff>` (en
  `humanAnswers`, con `askedBy`) y sobrevive a un reinicio.
- **Decisión 2: el protocolo prohíbe repetir lo decidido.** Cuando el prompt lleva algún bloque `<human_answer>`, una regla
  nueva del protocolo del ejecutor dice: no vuelvas a preguntar lo que un bloque ya decide; si la duda es la misma, sigue esa
  decisión y no devuelvas `blocked` por ella; pregunta solo por lo que ninguno decide. La regla de los bloques de datos y el
  texto del traspaso nombran ahora las dos procedencias (las preguntas del propio agente y las del orquestador).
- **Decisión 3: de dónde se leen.** Las respuestas del orquestador no son de ninguna tarea ni, a veces, de la ejecución que
  las usa: están en las entradas de la ejecución **en la que planificó el orquestador**, que es la propia (el plan es el
  propuesto y su id se reutiliza) o la que nombra `RunRequest.PlannedIn`. El lienzo lo rellena con la ejecución de su propuesta
  mientras el plan del lienzo sigue siendo el de esa propuesta, esté editado o no y se haya usado ya o no su id, y mientras la
  petición y los requisitos siguen siendo aquellos con los que planificó el orquestador. Para saberlo, al cargar la propuesta el
  lienzo guarda su origen (la ejecución, la petición y el hash de los requisitos: un campo propio, porque limpiar `Proposal`
  tiene otros efectos en la pantalla, como el estado de la propuesta y el borrador). Ese origen se olvida cuando otra propuesta,
  o ninguna, ocupa su lugar y cuando llega al lienzo el plan de una ejecución antigua (verla, reanudarla, su seguimiento);
  vuelve con el lienzo al cerrar una ejecución vista y con un borrador recuperado: el borrador guarda con su propuesta la
  petición y el hash de los requisitos con los que se planificó (`PlanDraftProposal.PlannedRequest` y `RequirementsHash`,
  los dos o ninguno), y uno de antes, que no los dice, se recupera sin origen y no manda respuestas en vez de suponerlas. Con otra petición, otros requisitos, el plan
  de una ejecución antigua o un seguimiento, no se manda `PlannedIn`; rechazar la propuesta y dibujar otro plan para la misma
  petición y los mismos requisitos sí cuenta como el plan de esa propuesta.
  - **Dónde se recuerda.** `RunStarted` gana el campo opcional `PlannedIn` (la ejecución en la que planificó, solo cuando no es
    esta). Sin subir la versión del payload: el serializador omite los nulos (`WhenWritingNull`), así que las entradas ya
    escritas se leen y se vuelven a escribir byte a byte, y una prueba lo fija. No se eligió la cabecera de la ejecución
    (`RunRecord`, que es una fila guardada con migración y entra en `RunHeaderHash`): `RunStarted` ya ancla la cabecera y no
    cambia ese hash. Si `PlannedIn` es la propia ejecución, el motor lo trata como no puesto y no lo escribe.
  - **Cuándo y cómo.** Al **iniciar** (`Orchestrator.StartAsync`, antes de escribir nada) y al **reanudar** (`RunResumer`, que lee
    `PlannedIn` de `RunStarted`), con `QuestionRecovery.LoadOrchestratorAsync`, solo como las anclaron `QuestionAsked` y
    `QuestionAnswered`: la pregunta, de su evidencia (el JSON del planificador, no el documento de la pregunta de un ejecutor), y
    el texto propio de la persona, de la suya. Si una de las dos no se puede leer como el registro la anclaba, la ejecución no
    empieza o no se reanuda (falla cerrado, como el resto de la recuperación de preguntas). Lo mismo si `PlannedIn` no es una
    ejecución de este registro en la que el orquestador propuso un plan (`PlanCreated` del orquestador): un id equivocado, ilegible
    o de una ejecución con un plan escrito a mano quitaría las respuestas sin avisar, o daría a los ejecutores respuestas a
    preguntas que nadie les hizo. Un `PlannedIn` que no es un id de ejecución falla con el mismo mensaje que uno desconocido.
  - **Solo las respondidas, y una vez.** Se lee la evidencia de las preguntas que tienen respuesta; una pregunta sin respuesta no
    sirve de nada y su evidencia no se toca (una ejecución antigua con esa evidencia borrada sigue reanudándose). Si el registro
    tiene dos respuestas a la misma pregunta, cuenta la primera. (Una ejecución que se reanuda con sus propias entradas ni
    llega ahí: la reproducción rechaza un registro que contesta dos veces la misma pregunta.)
  - No cuentan en los topes de D-29 (tres por tarea, diez por ejecución), que son de los ejecutores, y el motor no registra nada
    nuevo por ellas al empezar ni al reanudar: solo las lee. La decisión que recibe el juez la captura la planificación al
    registrar la respuesta (adenda siguiente, P54). Tampoco se copian las entradas a la ejecución nueva: duplicarían la pregunta
    y su evidencia.
- **Decisión 4 (corregida el mismo día por P54, adenda siguiente): ni los prompts de los ejecutores ni el de la planificación
  llegan al juez.** El bloque nuevo y la regla no llegan al juez, porque `InstructionsIssued` (el prompt de cada ejecutor) sigue
  fuera del dosier, y una prueba de integración con el juez guionizado lo comprueba (`OrchestratorQuestionReachesTheExecutorsTests`:
  su prompt y todo lo que puede leer carecen de `askedBy` y de los prompts de los ejecutores). La primera redacción de esta
  decisión añadía que el dosier sí incluía, de la ejecución en la que planificó el orquestador, la evidencia «Prompt del
  orquestador (intento N)» y el informe del primer intento del planificador, con la pregunta tal como la escribió, sus
  opciones, la recomendada y la respuesta, y lo dejaba pendiente. Ya no es así: ninguno de los dos llega al juez. De la pregunta
  del orquestador el juez recibe lo mismo que de la de un ejecutor: la decisión que captura el sistema, `QuestionAnswered`
  (ids y huellas) y, si la persona escribió su texto, esa evidencia (`ProvidedByHuman`).
- **Alternativas descartadas:**
  - Escribir la decisión en `PlanTask.Instructions` al planificar: es texto del orquestador, no dato de la persona, y un plan
    editado después de responder lo pierde.
  - Reutilizar el id de la propuesta para el plan editado: esa ejecución lleva su `PlanCreated`, su `_base` y su aprobación, y el
    plan editado es otro; por eso `PlannedIn` es un campo aparte y el id de la ejecución nueva es el suyo.
  - Copiar las preguntas y respuestas del orquestador al registro de la ejecución nueva: duplica evidencia y deja dos veces
    `QuestionAnswered` con el mismo id, que la reproducción rechaza.
  - Dárselas solo a la primera tarea o a las tareas cuyo alcance toque el asunto: el motor no sabe de qué trata una pregunta, y
    un ejecutor que repite una pregunta ya contestada cuesta un intento y una interrupción a la persona.
  - Contarlas en los topes de D-29: tres preguntas del orquestador dejarían siete a los ejecutores sin que estos hayan
    preguntado nada.
- **Consecuencias y lo que queda abierto:**
  - Un ejecutor ve las respuestas del orquestador aunque su tarea no tenga nada que ver con ellas: son pocas (tres como mucho)
    y llevan su pregunta, para que el agente juzgue si le atañen.
  - Que el ejecutor haga caso de la regla depende del modelo: el sistema se lo dice y se lo da como dato, pero no puede
    impedir que pregunte. Lo comprobará la próxima demostración en vivo.
  - Un plan escrito a mano, sin propuesta del orquestador, no tiene `PlannedIn` y no recibe respuestas: no las hubo.

## Adenda · 2026-11-11 · el juez ve la decisión sobre la pregunta del orquestador, no la pregunta (P54)

- **Contexto.** La decisión 4 de la adenda de V43 dejó dicho que el dosier incluía, de la ejecución en la que planificó el
  orquestador, el «Prompt del orquestador (intento N)» y el informe del primer intento del planificador: los dos llevan la
  pregunta tal como el orquestador la escribió, con sus opciones, la recomendada y tu respuesta, y D-30 y D-33 dicen que el juez
  solo ve la decisión del sistema. `JudgeDossierBuilder` retiraba los prompts y los informes de los ejecutores, no los de la
  planificación, y la planificación no capturaba ninguna decisión.
- **Decisión 1: el prompt de la planificación nunca llega al juez.** Haya preguntas o no, y en cada intento. Como el de un
  ejecutor, guía al agente y no dice nada de si se cumplen los requisitos, y puede llevar la ficha, la memoria y tus respuestas.
  La planificación no escribe `InstructionsIssued`: se identifica por su `AgentRunStarted`, el que no tiene tarea y dice
  `Purpose = Planner`, cuyo `PromptEvidenceId` es la evidencia. Como ese inicio se escribe después de la guarda del repositorio
  del usuario, un lanzamiento que la guarda rechaza deja el prompt registrado sin inicio: por eso se retira también la evidencia
  `Document`, `CapturedBySystem` y sin tarea cuyo título, anclado por `EvidenceRegistered` v2, es «Prompt del orquestador
  (intento N)». Un inicio de versión 1 (sin propósito) que no tiene tarea solo podía ser del planificador o del juez, y el prompt
  de ninguno de los dos es evidencia del trabajo: también se retira. Un inicio sin tarea con otro propósito (el del propio juez)
  no cambia.
- **Decisión 2: los informes de la planificación siguen la regla de los del ejecutor.** `AgentRunFinished` no lleva propósito ni
  agente, y el juez también lanza sin tarea, así que el informe se atribuye al planificador por el `AgentRunStarted` anterior,
  del mismo intento, en el orden del registro. Queda fuera si trae `questions` con algún elemento (o con un valor que no es una
  lista) o si no es un objeto JSON, y, con un aviso del sistema que impide que ninguna aprobación se apoye en el dosier, si no
  se puede leer como lo ancló el registro (falta, otro contenido, ilegible): fallar cerrado, como con un informe de ejecutor. El
  informe que solo trae el plan (`questions` vacío, nulo o ausente) llega al juez, como antes. Un informe de planificación de
  antes del propósito (versión 1) no se examina: las preguntas del orquestador son posteriores.
- **Decisión 3: la decisión de la persona sí llega, y la captura la planificación.** Al registrar la respuesta
  (`Planner.RecordAnswersAsync`, justo después de `QuestionAnswered`) el sistema captura el mismo documento que para un ejecutor
  (`QuestionDocuments.DecisionJson`, `agent-studio/question-decision/1`, con `OrchestrationRun.CaptureDecisionAsync`): ids,
  huellas e instantes, la opción elegida o la evidencia de tu texto, quién y cómo, y nunca el texto del agente, la recomendada ni
  las estimaciones. Va con `task` nulo (ninguna tarea la hizo) y el título «Decisión sobre la pregunta plan-1 del orquestador».
  Se construye con lo que anclaron `QuestionAsked` (la evidencia y su huella, el agente, el intento y el instante) y
  `QuestionAnswered` (el instante), no con lo que devuelve quien llama. Es idempotente: una planificación que se repite con las
  mismas respuestas no registra dos veces la respuesta ni la decisión, y captura la que la anterior no llegó a capturar (un
  cierre entre las dos escrituras). La entrada y la evidencia de `QuestionAsked` del orquestador ya se retiraban por la regla
  general, y una prueba lo fija.
- **Corrección de la adenda de V43.** Su decisión 3 decía que el motor no registraba nada por las preguntas del orquestador,
  «sin decisión capturada, porque nunca hubo una tarea cuyo juez la reciba». El motor sigue sin registrar nada al empezar ni al
  reanudar, pero la decisión sí existe: la captura la planificación.
- **Qué ve ahora el juez de una ejecución en la que el orquestador preguntó.** Ve `QuestionAnswered` (ids y huellas), la decisión
  que captura el sistema, el texto propio de la persona si lo hay, el plan y el informe de la planificación que planificó (solo
  el plan). No ve la pregunta, sus etiquetas, su motivo, la recomendada ni las estimaciones (ni la evidencia ni la entrada
  `QuestionAsked`), ni el prompt de ninguna planificación, ni el informe del intento que preguntó. Una ejecución en la que el
  orquestador no preguntó pierde solo el prompt de la planificación.
- **Alternativas descartadas:**
  - Retirarlos solo por el título de la evidencia: el título está anclado, pero es un texto que cambia con el idioma; por eso
    es el respaldo del inicio (`AgentRunStarted`) y no su sustituto, y los informes se atribuyen por el inicio.
  - Retirar solo el prompt que lleva la pregunta, o solo en las ejecuciones con preguntas: el prompt lleva ficha, memoria y
    respuestas aunque no hubiera preguntas, y lo que el orquestador se dijo a sí mismo no es lo que el juez juzga.
  - Examinar todo `AgentRunFinished` sin tarea: alcanzaría los informes del juez, que no son evidencia registrada, y pondría un
    aviso falso en cada ejecución con juez.
  - Copiar el texto de la pregunta en la decisión: ya descartado en la revisión de WP4 (llegaría como un hecho comprobado por el
    sistema).
- **Consecuencias y lo que queda abierto:**
  - Una ejecución propia para un plan editado después de responder (`PlannedIn`) no tiene esas entradas entre las suyas: el
    dosier las lee de la planificación que nombra `RunStarted.PlannedIn` (entradas verificadas del mismo registro) y lleva al juez
    solo los `QuestionAnswered` sin tarea y la evidencia de la decisión (con el texto propio, si lo hay), como el registro la ancló;
    nada más de esa planificación (ni su prompt, ni sus informes, ni `QuestionAsked`), y un `PlannedIn` que el registro no tiene, o
    una decisión o un texto que no se pueden leer como se anclaron, son un aviso del sistema que impide aprobar.
  - Una pregunta del orquestador que se respondió antes de esta adenda no tiene decisión capturada: el juez ve solo
    `QuestionAnswered` (el prompt y el informe sí se retiran, porque eso se decide al construir el dosier).
  - El campo `options` de la decisión sale de la pregunta que devuelve quien llama con las respuestas (la ventana la guarda de la
    propuesta), la misma contra la que se comprueba la opción elegida, y no de la lista de `QuestionAsked`.
  - Los títulos de lo retirado («Pregunta plan-1 del orquestador», «Prompt del orquestador (intento 1)») siguen en las entradas
    `EvidenceRegistered` del dosier: solo títulos, sin texto del agente.

## Adenda (2026-11-12): los `outputs` del registro del intento llegan al juez como dichos por el agente (P46)

- **Qué pasaba.** El registro de cada intento (`AttemptRecord`, evidencia `CapturedBySystem`) copia los `outputs` del informe del
  ejecutor, que escribe el agente, y el juez recibía ese documento entero como un hecho del sistema. Es anterior a la Fase 13.
  Los `outputs` hacen falta para reanudar (las tareas posteriores los reciben como `<upstream_output>`), así que no se quitan
  del registro.
- **Decisión.** El registro guardado no cambia (ni su contenido ni su SHA-256, que sigue anclado en el registro de decisiones).
  Cambia la copia que va al paquete del dosier (`JudgeDossierBuilder`): el juez lee `outputsSaidByAgent` en lugar de `outputs`,
  con `origin: "ReportedByAgent"` y una nota que dice que es una afirmación del agente que hay que contrastar, y la entrada de
  esa evidencia en `dossier.json` lleva `"saidByAgent": ["outputs"]`. Es el mismo criterio que el resto del dosier aplica a lo
  que escribe un agente: una afirmación, nunca una prueba. Los demás campos (`outcome`, `attemptBase`, `head`, `changedPaths`,
  `tests`, `reportEvidence`, `reportSha256`) los captura el sistema y no se marcan.
- **`contentVerified`** sigue comparando el contenido original con su huella; la copia difiere de él solo por esa marca. Si la
  copia no se puede marcar (versión desconocida, contenido cortado), se deja como estaba y el dosier añade un `systemCheck` que
  dice que esos `outputs` los escribió el agente, así que nunca pasan por capturados sin aviso.
- **Protocolo del juez.** `JudgePrompt` añade a sus reglas de confianza que lo marcado como dicho por el agente dentro de un
  documento del sistema no es un hecho del sistema. Las auditorías del proyecto y los debates no reciben registros de intento
  (su dosier no los incluye), así que sus prompts no cambian.
- **Efecto en los hashes.** Ninguno de los anclados cambia: el dosier solo se escribe en disco y las huellas del registro de
  decisiones son las del contenido guardado. Cambia `dossier.json` (campo `saidByAgent` por evidencia, nulo salvo en estos
  registros) y el texto de las reglas del prompt.
