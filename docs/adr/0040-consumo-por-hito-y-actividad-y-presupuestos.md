# ADR-0040 · Consumo por hito y por actividad, presupuestos por proyecto y por hito, y real frente a estimado

- **Estado:** aceptada · 2026-11-11 (esqueleto del P0), completada por WP2 el 2026-11-12 (decisiones 8 a 13). El P0 de la Fase 9 fija lo
  que le toca (las dimensiones, `ActivityClass`, el presupuesto y la comparación con la estimación); **WP2** añade la proyección, el
  clasificador de actividad, la espera y el presupuesto en el motor. docs/16 asigna este ADR a WP2: se adelantó el esqueleto, como hizo la
  Fase 13 con los ADR 0048 a 0053. **Amplía**
  [ADR-0019](0019-consumo-como-proyeccion-del-registro.md). Recoge D-05, D-34 y D-35 de
  [docs/16](../16-ampliacion-plataforma-de-proyectos.md#213-consumo-costes-y-estimaciones-sección-13).
- **Contexto:** el consumo ya es una proyección del registro por proveedor, modelo, sesión, ejecución, tarea, agente, propósito,
  proyecto y periodo, sin doble cuenta. Faltan el hito y la actividad como dimensiones, la espera como cifra, el real frente a lo
  estimado al cerrar un hito y los presupuestos. El estimador lo hizo la Fase 13 ([ADR-0050](0050-modelos-y-potencia.md), D-61 de docs/17).
- **Decisión:**
  1. **Cuatro dimensiones nuevas al final de `UsageDimension`:** `Milestone` y los tres ejes de la actividad (`Activity`,
     `ActivityAttempt`, `ActivityDestination`). Cada intento tiene un valor en cada eje, así que no cuenta dos veces y los grupos de
     un eje suman el total.
  2. **Hito, por los vínculos.** Un intento vinculado a dos hitos sale en cada uno y una sola vez en el total; `UsageGroup.SharedAttempts`
     dice cuántos intentos del grupo están también en otro.
  3. **Actividad, clasificación determinista, sin preguntar a ningún agente** (`Domain/Metrics`). Qué: coordinación (el planificador),
     análisis (participantes de un debate, recomendación de skills), implementación, pruebas, documentación, revisión (juez, auditorías,
     impugnaciones) y conflictos (seguimiento tras un conflicto de integración). Para un ejecutor decide `ActivityPaths.OfDiff`: solo
     rutas de pruebas (`tests/` o las del perfil), pruebas; solo `docs/` o `*.md`, documentación; en cuanto una ruta es otra cosa,
     implementación; pruebas y documentación juntas cuentan como pruebas; sin ninguna ruta, implementación. Intento: primero o
     reintento. Destino: conservado (llegó a una integración entregada), descartado (tarea fallida o cancelada, ejecución no entregada,
     con o sin commit) y, para una ejecución en marcha, pendiente (`AttemptFate.Pending`, que docs/16 no nombra).
  4. **La espera es una duración medida aparte** (la cola del proveedor y la espera de cuota), no parte de la ejecución.
  5. **Presupuesto opcional por proyecto y por hito** (`WorkBudget`, D-05): tokens, ejecuciones o ambos, por periodo. El del proyecto
     es de la persona y de su equipo (`IProjectBudgetStore`, como las opciones del proyecto); el de un hito va en su cabecera anclada
     y es del hito entero. Antes de lanzar un intento el motor proyecta lo gastado con lo que el intento se estima (`WorkBudget.Check`:
     llegar justo al límite está dentro; lo que no tiene estimación cuenta como desconocido, nunca de menos, como `DebateBudget`) y, si
     lo pasaría, no lo lanza y dice por qué. La cuota sigue siendo de la cuenta: el presupuesto reparte el uso de la persona, no lo amplía.
  6. **Real frente a estimado** al cerrar un hito: `EstimateComparison.Compare` dice dentro, por debajo o por encima del intervalo (percentiles
     25 y 75) y «desconocida» si falta una de las dos cifras. Nunca se compara con un número inventado.
  7. **Descartado: el coste imputado de la suscripción** (D-35): una equivalencia que no existe. Se mantienen el coste a precio de lista
     que informa el CLI y, si algún día hay una API key, su gasto incremental.
- **Lo que decidió WP2** (2026-11-12), que completa lo anterior:
  8. **Todo sale del mismo registro, en la misma pasada.** `UsageProjector.Project` lee, además de `AgentRunStarted` y `AgentRunFinished`,
     `RunStarted`, `RunFinished`, `TaskStatusChanged`, `IntegrationPerformed`, `InstructionsIssued` y `MilestoneLinked`, y da a cada intento su
     hito, su actividad y su espera (`UsageAttempt.Milestones`, `.Activity`, `.QuotaWaitMilliseconds`; propiedades `init`, como `Effort`).
     No hay tabla ni lectura de evidencias: la proyección sigue siendo una función pura del registro y se sigue guardando por su último hash.
  9. **Hito.** `MilestoneMembership` (`Application/Work`) resuelve a qué hitos pertenece una ejecución o un debate: los vínculos
     `MilestoneLinked` y, por `RunStarted.Follows`, los de la ejecución que sigue, a cualquier profundidad. La proyección y el presupuesto de
     un hito lo leen igual, así que cuentan los mismos intentos. Clave del grupo: el id del hito, o «sin hito». Un intento de dos hitos tiene dos
     claves (producto cartesiano con las demás dimensiones), sale en cada grupo y una vez en el total; `SharedAttempts` cuenta los miembros del
     grupo con más de un hito. Una consulta sin la dimensión `Milestone` no repite nada.
  10. **Actividad, `ActivityClassifier` (puro).**
     - *Qué:* planificador, revisión del orden y chat de preparación son coordinación; participantes de un debate, recomendación de skills,
       análisis del consumo y ayuda son análisis; jueces y auditoría, revisión; resumen diario y partes de un documento para clientes,
       documentación. Un ejecutor de una ejecución que sigue a otra que acabó con conflicto de integración es **conflictos**; cualquier otro,
       `ActivityPaths.OfDiff`. Un inicio anterior a la Fase 4 no guarda el propósito: con tarea es ejecutor y sin ella planificador.
     - *Rutas del diff:* el diff no está en el registro (está en la evidencia del registro del intento). Para no abrir ficheros en una consulta de
       consumo se usa el **ámbito de escritura de la tarea** que ancla `InstructionsIssued`: `tests/` solo, pruebas; `docs/` o `*.md` solo,
       documentación; lo demás, implementación. Es una aproximación declarada: un ejecutor con ámbito `src/` que solo tocó pruebas cuenta como
       implementación. El clasificador acepta las rutas reales (`ActivityTaskFacts.ChangedPaths`) y las de pruebas del perfil, por si una
       fase posterior las lee; hoy la proyección no las pasa.
     - *Intento:* primero o reintento (la corrección del planificador es un reintento).
     - *Destino:* primero el resultado del propio intento: uno que falló o se canceló es **descartado**, sea cual sea el destino de su tarea. Luego
       la ejecución y la tarea: en una ejecución entregada, el intento que completó la tarea (`TaskStatusChanged` a `Completed` con su número) y
       los de la propia ejecución (planificador, juez) que acabaron bien son **conservados**; un intento anterior de esa tarea, o de una tarea que no
       se completó, es descartado; en una ejecución que acabó `Failed`, `Cancelled` o `Blocked`, todo es descartado; en las demás, una
       tarea fallida, cancelada u omitida, o un intento que otro superó, es descartado y el resto está **pendiente**. Una ejecución `Succeeded` que no se entregó (todavía) es
       pendiente: puede entregarse; docs/16 la llamaba «no entregada, descartada», pero a una ejecución a la espera de tu aprobación no se la puede
       llamar descartada. Un debate, una auditoría o una ejecución auxiliar no tienen integración: lo que acabó bien es conservado y lo que no,
       descartado.
     - *Con o sin commit:* `ActivityClass.HasCommit` es que el trabajo de la tarea llegó a una integración (un merge con commit). La clave del
       destino lo dice: **`Discarded+commit`** para lo descartado con commit y `Discarded` para lo descartado sin él, así los grupos del eje siguen
       sumando el total.
  11. **Espera.** La de cola es `LaunchedAt` menos el `RecordedAt` de `AgentRunStarted` (`UsageAttemptFacts.QueueWait`; «sin dato» si el proceso no
      arrancó o el registro es v1). La de cuota va del bloqueo `RateLimitedUntil` (también la forma estimada) a la salida de la tarea de ese
      bloqueo, y es del intento que llegó al límite; una espera sin salida es «sin dato», nunca cero, y un intento que no empezó ninguna tiene un
      cero medido. Ninguna entra en la duración. Los totales las suman aparte (`UsageTotals.QueueWaitMilliseconds` y `.QuotaWaitMilliseconds`).
  12. **El presupuesto en el motor (`ProjectBudgetGate`).** Falla cerrado y no gasta cuota:
     - Primero lo barato: el presupuesto del proyecto (su almacén) y las cabeceras de los hitos (`IMilestoneRegistry.ListAsync`). Sin presupuesto
       en ninguno deja pasar **sin leer el registro, el consumo ni la estimación**, aunque el registro no sea de confianza. Solo si algún hito
       tiene presupuesto se lee el registro, una vez por ejecución, para saber a qué hitos pertenece (se recuerda, y `Forget` lo olvida cuando la
       ejecución arranca o se reanuda; un vínculo posterior cuenta desde entonces). Con un presupuesto que aplicar, lo que no pueda saber (el almacén
       no se lee, el registro no es de confianza, lo gastado no se proyecta, el presupuesto no valida) **rechaza** (`project.budget-unreadable`).
     - La consulta del consumo se acota al comienzo del periodo más ancho entre los presupuestos que aplican (todo el historial solo para un total).
     - Lo gastado sale de `IUsageReport` como lo cuenta el panel: tokens de entrada más salida de los intentos que terminaron y su número; el
       presupuesto de un proyecto, en su periodo (día, semana ISO desde el lunes o mes, en la zona local); el de un hito, en todos sus intentos
       (`MilestoneMembership`). Un intento sin cifra de tokens cuenta por lo que da y el aviso dice cuántos faltaron.
     - Lo que se suma es la estimación del intento (percentil 75 del total de tokens, la cifra de la ventana de una pregunta, ADR-0050) y una
       ejecución, más lo mismo por cada intento en vuelo de la ejecución, que el registro aún no cuenta. Llegar justo al límite está dentro.
     - **Sin estimación** (menos de tres referencias, o el estimador no responde) el intento no es cero sin decirlo: sale solo mientras lo gastado
       esté por debajo del límite de tokens, y el veredicto lo marca (`ProjectionUnknown`). Rechazar siempre dejaría el presupuesto inservible en un
       proyecto sin historial. El límite de ejecuciones no depende de la estimación.
     - Se comprueba en `DispatchAgentAsync`, ya decidido que el intento podría lanzarse. Un rechazo **no es definitivo**: la tarea queda
       `Blocked` con `BlockReason.AwaitingHuman` y el motivo en español (el patrón de las demás tareas que esperan a la persona; no se añade ningún
       tipo de bloqueo nuevo), la ejecución queda `AwaitingHuman` y se deja un `AuditAlertRaised` con `project.budget-exceeded` (o
       `project.budget-unreadable`). La persona sube el presupuesto y reintenta la tarea (`RetryBlockedAsync`): se vuelve a comprobar y sigue. Un fallo
       de lectura pasajero se trata igual (bloqueada, nunca `Failed`) y el reintento lo lee de nuevo; no hay retención para toda la ejecución.
       **El juez no se bloquea**, como el juicio de un debate: una ejecución no podría acabar.
  13. **Estimación por actividad o por hito, encima del estimador.** `ScopedEstimation` no duplica el cálculo: filtra los intentos de la historia por
      actividad o por hito y llama a `UsageEstimation.Estimate`. `CompareWithPlan` compara lo gastado (entrada, salida y ejecuciones) con los
      intervalos de un `PlanEstimate` con `EstimateComparison`; una suma con huecos o un intervalo desconocido es «desconocida». Hoy el plan
      estimado no se guarda, así que quien cierre un hito (WP3) debe traer la estimación que se vio al aprobar el plan.
- **Limitaciones conocidas:** el almacén `IProjectBudgetStore` no tiene implementación (la composición lo registrará con la pantalla de
  presupuestos, WP5 o el lead); el motor no hace nada sin él. Un intento vinculado a un hito después de lanzado no cuenta para el presupuesto
  de ese hito hasta el siguiente intento. La exportación del consumo (CSV y JSON) escribe las claves nuevas pero no las esperas ni `SharedAttempts`.
- **Alternativas:** una sola dimensión de actividad con tres valores combinados (rompe «los grupos de un eje suman el total»); contar
  un intento compartido en un solo hito (esconde trabajo de uno de ellos).
