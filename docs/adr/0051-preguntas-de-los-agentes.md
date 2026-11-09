# ADR-0051 · Las preguntas de los agentes: una salida del informe con opciones, su registro, la continuación con la respuesta como dato y su seguridad; el juez nunca pregunta

- **Estado:** propuesto · 2026-11-06. Esqueleto del P0 de la Fase 13; lo completa **WP4** (preguntas e instrucciones en el
  motor) con WP8 (la ventana). Recoge D-28 a D-33 de [docs/17 §4.7](../17-constructor-visual-de-agentes.md#47-preguntas-de-los-agentes-con-opciones)
  y la decisión 4 (a) que aceptaste el 2026-11-06. **Amplía** [ADR-0013](0013-protocolo-del-orquestador.md) y
  [ADR-0035](0035-final-clasificado-y-renovacion-del-contexto.md).
- **Contexto:**
  - P9 pide que las dudas de los agentes salgan como ventanas con opciones, la recomendada marcada y el coste de cada una.
  - Hoy un agente bloqueado deja su duda en texto libre, y «Reintentar» lo relanza sin ningún texto (`RetryBlockedAsync`): no
    sabe qué decidiste (docs/17 §0.2).
- **Decisión** (la de docs/17; WP4 la detalla aquí):
  1. **Una pregunta es una salida del informe, igual en los dos CLIs** (D-28): `status: blocked` con un campo `question` de
     dos a cinco opciones. El sistema la valida (`QuestionRules`, P0: ids únicos, 400, 80 y 300 caracteres, una recomendada
     como mucho, sin caracteres de control ni de dirección) y redacta los secretos. Preguntan los ejecutores y, antes de
     planificar, el orquestador (tres como mucho); el juez nunca.
  2. **En el registro** (D-29): la pregunta entera es evidencia; `QuestionAsked` y `QuestionAnswered` (P0, `V = 1`) solo llevan
     ids, huellas, la recomendada del agente y las estimaciones del sistema con su origen. La tarea queda `Blocked` con el
     motivo `AwaitingAnswer` («Espera tu respuesta», `UserTextCodes.QuestionAwaitingAnswer`), y la reproducción la rehace tras
     reiniciar. Topes: tres por tarea y diez por ejecución.
  3. **La tarea sigue con tu respuesta** (D-30): `IOrchestrationRun.AnswerAsync(QuestionAnswer)` (P0, con un cuerpo por
     defecto que dice que la ejecución no admite respuestas; WP4 lo implementa en el motor). Un intento nuevo, que cuenta en el
     presupuesto, recibe la pregunta y la respuesta como dato (`<human_answer …>`), reanudando la sesión que preguntó o, si no
     se puede, en una nueva con traspaso.
  4. **La ventana** (D-31, WP8): no modal, con teclado y lector, y se retira con la interfaz bloqueada. Responder no es
     aprobar: no pide Windows Hello (`PresenceAction.AnswerAgentQuestion`, ADR-0053).
  5. **El coste de cada opción sin inventar nada** (D-32): el estimador (`IUsageEstimator`, P0; WP3) da el intervalo del
     percentil 25 al 75 con su origen, o «desconocido» con menos de tres referencias.
  6. **Seguridad y juez** (D-33): las opciones son datos del agente; elegir no concede nada; el juez ve tu elección, nunca la
     recomendada ni las estimaciones. Contestar sin ti solo con el consentimiento de la ejecución
     (`RunOptions.AnswerWithRecommendedAfter`, desmarcado por defecto; en la cabecera y su hash solo si lo das; P0).
- **Alternativas:** las herramientas de pregunta de cada CLI (`AskUserQuestion`, `request_user_input`; D-28); siempre una
  sesión nueva con traspaso (decisión 4 (b)); contestar siempre pasado un plazo (decisión 4 (c)).
- **Consecuencias:** pendientes de WP4 (el esquema del informe medido, la prueba de inyección de F13-13 y la reanudación real
  con cada CLI).
