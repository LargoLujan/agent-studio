# ADR-0039 · Vínculos de trazabilidad explícitos y de solo añadir, inferencias como sugerencia y seis papeles de participación

- **Estado:** aceptada · 2026-11-11. El P0 de la Fase 9 deja el dominio (`Domain/Tracing`), los payloads y los contratos; WP1 hace los
  servicios y las sugerencias, y WP5 la cadena y la vista de participantes. Recoge D-29 a D-32 de
  [docs/16](../16-ampliacion-plataforma-de-proyectos.md#211-trazabilidad-de-requisitos-tareas-y-código-sección-11) (la sección 12 sobre los
  papeles).
- **Contexto:**
  - Dentro de una ejecución la cadena requisito, tarea, intento, commit, verificación, integración, veredicto y entrega ya existe y
    está encadenada por hash. Faltan los vínculos entre ejecuciones y hacia fuera (hitos, commits de la persona, ADR y documentos,
    más tarde los PR) y qué pasa tras un squash, un rebase o el borrado de una rama.
  - Lo inferido (de un mensaje de commit, de una rama) puede equivocarse, y una inferencia que llegara al juez como un hecho sería
    texto no verificado dentro de la evidencia.
- **Decisión:**
  1. **Vínculos como entradas del registro:** `TraceLinkRecorded` une un origen y un destino (`TraceRef`: tipo e id) con quién lo
     afirma (`TraceAssertedBy`: el sistema o la persona) y un motivo; `TraceLinkRetracted` lo retira. Un vínculo nunca se edita: se
     retira y se registra otro. No hay tabla: se leen del registro verificado. Ambas entradas no tienen ejecución (`NoRun`) y esperan
     a que el proyecto esté libre, como la memoria.
  2. **El id de un vínculo se deriva de su contenido** (`TraceLinks.IdOf`: SHA-256 de los dos extremos y de quién lo afirma): el mismo
     vínculo registrado dos veces es uno, y retirar lo nombra exactamente. Se lee en los dos sentidos, de varios a varios
     (`TraceLinkSet`). Un vínculo retirado puede volver a registrarse; su retirada queda en el historial.
  3. **Ids estables que nombran una sola cosa:** un commit siempre con su id completo (40 o 64 hexadecimales en minúsculas, nunca
     una abreviatura); una tarea `<ejecución>/<tarea>`; un intento `<ejecución>/<tarea>#<n>`; un documento `<ruta>@<commit>` con la
     ruta relativa. Los tipos son los de docs/16 y los PR de la Fase 10 se añaden al final de `TraceNodeKind`.
  4. **`MilestoneLinked` es el vínculo del hito con una ejecución o un debate** ([ADR-0038](0038-hito-backlog-y-tablero.md)): un hecho, una
     entrada. La traza lee los dos tipos de entrada.
  5. **Lo inferido es una sugerencia** (`TraceSuggestion`, con su origen: mención en un mensaje, trailers, rama de una ejecución o PR
     que cita un hito). Solo `Confirm`, llamado con la decisión de una persona, la convierte en un vínculo asertado por ella, y la entrada
     dice de qué sugerencia vino. Nunca llega al juez como un hecho: las entradas de los vínculos no pertenecen a ninguna ejecución y el
     dosier las ignora.
  6. **Trailers de los commits del sistema** (`CommitTrailers`): `Agent-Studio-Run`, `Agent-Studio-Task`, `Agent-Studio-Attempt` y
     `Agent-Studio-Agent` (el agente y, tras `; model=`, el modelo), en un bloque al final del mensaje. Se escriben rechazando saltos de
     línea (un trailer no puede añadir otro) y se leen solo del último párrafo, sin claves repetidas. Sobreviven a un push y a un rebase;
     tras un squash depende de la plataforma. Son una ayuda para inferir, nunca la fuente: los ids viven en el registro.
  7. **La trazabilidad sobrevive al squash, al rebase y al borrado de ramas porque los ids están en el registro, no en git** (D-31): lo
     que pasa en el remoto llega como observaciones nuevas en la Fase 10, nunca reescribiendo las anteriores.
  8. **Seis papeles que nunca se confunden** (`ParticipationRole`, D-32): el agente productor (el intento), el autor y committer de git
     (el commit), la cuenta que publicó (la operación remota, Fase 10), el revisor (el veredicto o la revisión del PR), quien aprueba
     (`DecidedBy`) y quien integra (la entrega local o `merged_by`). Un papel sin fuente sale «no consta»
     (`ParticipationEntry.NotRecorded`) y nunca se deduce. El veredicto del juez nunca es la aprobación de la cuenta de la persona (D-33,
     Fase 10).
- **Alternativas:**
  - **Confiar en los mensajes de commit:** descartada, no sobreviven a un squash y cualquiera puede escribirlos.
  - **Una tabla de vínculos:** descartada, otra fuente de verdad; el registro ya es de solo añadir y está anclado.
  - **Confirmar las sugerencias en bloque o automáticamente:** descartada, lo inferido solo cuenta si lo confirmas.
- **Consecuencias:**
  - Contratos en [docs/04 §23](../04-contratos.md#23-fase-9-trabajo-y-trazabilidad): `ITraceLinkService`, `ITraceSuggester`, `ITraceChainReader` e
    `IParticipationProjector`. Las implementaciones y los trailers en `Workers.cs` son de WP1.
  - Los vínculos y sus retiradas crecen el registro sin límite, como cualquier entrada; una cadena larga se lee con la proyección, no a mano.
