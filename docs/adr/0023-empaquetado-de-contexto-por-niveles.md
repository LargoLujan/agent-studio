# ADR-0023 · El empaquetado de contexto va por niveles y falla cerrado si los requisitos obligatorios no caben

- **Estado:** aceptada · 2026-10-18. Recoge D-23 y D-24 de [docs/14](../14-fase-4-observabilidad-y-memoria.md#44-f4-5-y-f4-6--memoria-y-empaquetado).
  WP3 hace el empaquetador (`ContextPacker`, en `src/AgentStudio.Memory/Context/`); WP1 lo usa al escribir los prompts del
  planificador y de los ejecutores y ancla el paquete en `AgentRunStarted` v2.
- **Contexto:**
  - F4-6 pide un empaquetado de contexto que conserve los requisitos críticos, las decisiones y las tareas pendientes.
  - El prompt de un agente tiene un tamaño finito. Lo que Agent Studio le añade (requisitos y memoria del proyecto) tiene
    que caber en un presupuesto, y la sección 10 del prompt maestro pide decir qué se pierde.
  - La memoria vuelve a un prompt, así que es una superficie de inyección de instrucciones
    ([ADR-0018](0018-modelo-de-amenaza-fase-3.md), [ADR-0022](0022-memoria-append-only-anclada.md)): solo debe llegar la
    de confianza, a quien la pide su plantilla, y nunca al juez.
  - Los requisitos obligatorios ya se citan literalmente del conjunto congelado. Lanzar un agente sin ellos sería peor que
    no lanzarlo.
- **Decisión:**
  1. **Niveles en un orden fijo** (`ContextTier`):
     1. requisitos obligatorios, en el orden en que llegan;
     2. decisiones: `Decision`, `Architecture` y `Convention`, lo más reciente primero;
     3. tareas pendientes, lo más reciente primero, solo para el planificador (el rol `Orchestrator`);
     4. requisitos deseables, en el orden en que llegan;
     5. errores conocidos y soluciones, por relevancia al foco (la petición o el objetivo de la tarea);
     6. el resto (`Change`, `Verdict` y `DebateSummary`), por relevancia al foco.

     Los niveles 5 y 6 solo traen lo relevante (FTS5, [ADR-0022](0022-memoria-append-only-anclada.md)); sin foco, nada.
  2. **Fallo cerrado.** Si los requisitos obligatorios necesitan más caracteres que el presupuesto, el empaquetador lanza
     `ContextBudgetTooSmallException` con lo que necesitan y el presupuesto, sin leer la memoria. El agente no se lanza y
     la tarea queda bloqueada con el motivo (WP1).
  3. **Prefijo estricto.** Cada elemento entra mientras cabe en los caracteres que quedan. El primero que no cabe se
     omite, y también todo lo que viene detrás:
     - un nivel entra entero antes que el siguiente;
     - una decisión o una tarea pendiente nunca se omite mientras entra algo de un nivel inferior;
     - dentro de un nivel se omiten primero las más antiguas (niveles 2 y 3) o las menos relevantes (5 y 6).

     Lo omitido sale en `ContextPack.Omitted`, en ese orden: son las «posibles pérdidas» que enseña la UI (D-12).
  4. **Qué memoria entra.**
     - Solo para un agente con `MemoryPolicy.ReadSelected`. Con `None` no se lee la memoria, y `ReadAndPropose` no existe
       hasta la Fase 5 (D-25).
     - Nunca para un juez ni un auditor, diga lo que diga su definición. `AgentDefinition.Validate` ya lo prohíbe
       (`agent.judge-memory-none`); el empaquetador no se fía de que se haya validado.
     - Solo la de confianza (del sistema, del usuario y la aceptada), sin lo que sustituye otra entrada de confianza.
     - Nunca un `DailySummary` (D-20): aporta poco y sale de la bitácora, que es contenido del repositorio.
     - Como mucho 50 entradas por nivel (`MaxCandidatesPerTier`), para que la lista de omitidas que ancla
       `AgentRunStarted` no crezca sin límite.
  5. **Un elemento de memoria** es `"tema: contenido"`, con la categoría y el origen de la entrada y su SHA-256 canónico,
     el mismo que anunció `MemoryRecorded` (`MemoryContent.Sha256`). Así cada elemento del prompt se puede cotejar con el
     registro.
  6. **Medida.** El presupuesto y `Characters` cuentan los caracteres del texto de cada elemento, exactos. Los tokens son
     esos caracteres entre cuatro, redondeando hacia arriba, con el origen «estimado por Agent Studio». El marcado del
     bloque y el escapado de `PromptData` no cuentan: el prompt real es algo mayor.
  7. **El SHA-256 del paquete** es el de la lista canónica de sus elementos, en orden: el id, el nivel, la categoría, el
     origen, el SHA-256 y el texto de cada uno, con prefijo de longitud (`ContentHash.Canonical`). No depende de cómo
     escriba el prompt el bloque `<project_memory pack="…">`, que pone ese hash; `AgentRunStarted` v2 lo ancla con
     `ContextPackReference` (hash, ids incluidos, ids omitidos y caracteres), y la evidencia del prompt tiene el texto.
  8. **Determinista.** La misma petición y la misma memoria dan el mismo paquete. Una prueba de propiedad con 200
     conjuntos por semilla lo comprueba, junto con que los obligatorios están siempre y que las decisiones y las tareas
     pendientes nunca se omiten antes que un nivel inferior.
  9. **El bloque es del prompt.** Escribir `<project_memory>` con cada contenido recodificado por `PromptData.Quote`, para
     que no pueda cerrarlo, y la regla de que esos bloques son datos, es de WP1 (`ExecutorPrompt` y `PlannerPrompt`).
- **Alternativas:**
  - **Llenar el presupuesto saltando lo que no cabe** (mochila voraz): cabe más, pero una decisión grande podría quedarse
    fuera mientras entra un error conocido pequeño, que es justo lo que F4-6 prohíbe.
  - **Un único orden por relevancia, sin niveles:** una tarea pendiente poco parecida al foco se perdería antes que un
    cambio irrelevante.
  - **Presupuesto en tokens reales:** ningún CLI expone un tokenizador para contar antes de lanzar; sería una estimación
    igualmente, y peor explicada.
  - **Recortar el texto de una entrada para que quepa:** cortaría un requisito citado literalmente o una decisión a medias.
  - **Que el agente pida la memoria que necesita:** es un canal de agente, de la Fase 5.
- **Consecuencias:**
  - Un elemento grande al principio de un nivel puede dejar fuera todo lo que viene detrás. Es el precio de la garantía, y
    se ve en las omitidas.
  - Un ejecutor no ve las tareas pendientes, y un foco vacío no trae errores conocidos ni el resto.
  - El tokenizador `unicode61` no lematiza en castellano, así que la relevancia de los niveles 5 y 6 depende de palabras
    exactas, con acentos y mayúsculas plegados.
  - WP1 tiene que anclar `ContextPack.Sha256` tal cual, y no recalcularlo con su propio formato del bloque.
