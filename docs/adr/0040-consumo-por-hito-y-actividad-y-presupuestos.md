# ADR-0040 · Consumo por hito y por actividad, presupuestos por proyecto y por hito, y real frente a estimado

- **Estado:** aceptada (esqueleto del P0) · 2026-11-11. El P0 de la Fase 9 fija lo que le toca (las dimensiones, `ActivityClass`, el
  presupuesto y la comparación con la estimación); **WP2** lo completa (proyección, clasificador de actividad, presupuesto en el
  motor). docs/16 asigna este ADR a WP2: se adelanta el esqueleto, como hizo la Fase 13 con los ADR 0048 a 0053. **Amplía**
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
- **Pendiente de WP2** (este ADR lo completa): la proyección por hito y por actividad, el clasificador (`ActivityClassifier`), la
  espera como cifra, `ProjectBudgetGate` y el aviso del motor. Hasta entonces `UsageAggregator.KeyOf` lanza con las dimensiones
  nuevas y el panel las ofrece (docs/04 §23.3): hay que resolverlo con WP2 y WP5.
- **Alternativas:** una sola dimensión de actividad con tres valores combinados (rompe «los grupos de un eje suman el total»); contar
  un intento compartido en un solo hito (esconde trabajo de uno de ellos).
