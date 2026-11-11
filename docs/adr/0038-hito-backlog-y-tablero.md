# ADR-0038 · El hito es un conjunto de requisitos congelado con nombre, el backlog es de solo añadir y está anclado, y el tablero es una proyección del registro

- **Estado:** aceptada · 2026-11-11. El P0 de la Fase 9 deja el dominio, los payloads, la migración 0006 y los contratos; WP1
  hace los servicios, el almacén y el proyector del tablero, WP3 el cierre y WP5 las pantallas. Recoge D-17 a D-19 y D-49 y D-50
  de [docs/16](../16-ampliacion-plataforma-de-proyectos.md#28-gestor-de-trabajo-estilo-jira-sección-8) y la decisión D-61 de
  [docs/17](../17-constructor-visual-de-agentes.md#56-la-fase-9-al-final-del-bloque). Sigue el patrón de
  [ADR-0026](0026-debate-como-unidad-del-registro.md) (cabecera inmutable anclada) y de [ADR-0022](0022-memoria-append-only-anclada.md)
  (filas de solo añadir anunciadas en el registro).
- **Contexto:**
  - Hoy no hay nada por encima de una ejecución: ni un nombre para una línea de trabajo que agrupe varias, ni dónde anotar lo que
    aún no se ha planificado, ni un tablero.
  - Un gestor estilo Jira tendría su propio estado, otra fuente de verdad con tarjetas que se mueven a mano, como las tablas
    `tasks*` que [ADR-0010](0010-estado-derivado-del-registro.md) dejó sin uso. Aquí una tarea se cierra con evidencia y veredicto.
- **Decisión:**
  1. **El hito** (`Domain/Work/Milestones`) es un nombre, un objetivo y la secuencia de sus conjuntos de requisitos congelados.
     Su cabecera (`MilestoneHeader`: nombre, objetivo, primer conjunto, presupuesto opcional) es la fila inmutable de `milestones`
     y `MilestoneOpened` ancla su hash canónico (`MilestoneHeaderHash`, `milestone-header/1`, con prefijos de longitud; el id y el
     proyecto son la clave, no parte de ella). Todo lo que cambia es una entrada: `MilestoneRequirementsRevised`, `MilestoneLinked`,
     `MilestoneClosed` y `MilestoneReopened`. Su estado se reproduce (`MilestoneStateReplayer`), como el de un debate.
  2. **Un hito es una unidad del registro:** sus entradas llevan su id (`mil-yyyyMMdd-xxxxxx`) como `runId`, así que
     `RunLog.EntriesOf` las selecciona como las de una ejecución, y ningún id de ejecución empieza por `mil-`.
  3. **Reglas del hito:** un conjunto de requisitos distinto del vigente (el anterior queda en la historia); cada ejecución o debate
     se vincula una vez; un hito se cierra solo con una ejecución vinculada, porque cerrar es auditar su trabajo (D-49) y el informe
     lleva la revisión siguiente; un hito cerrado solo admite reabrirse, y reabrir conserva todos los informes (D-50). Una
     reapertura por una reversión, un check que falla o una impugnación lleva su evidencia; la decisión de una persona puede no llevarla.
  4. **Las ejecuciones y los debates se vinculan con `MilestoneLinked`,** que es a la vez el vínculo de la traza ([ADR-0039](0039-vinculos-de-trazabilidad-y-participacion.md)):
     un hecho, una entrada. Los seguimientos de una ejecución se encuentran por `RunStarted.Follows`, no se escriben.
  5. **El backlog** (`Domain/Work/Backlog`) son requisitos en borrador: enunciado, prioridad de requisito (`Mandatory` o `Desirable`)
     y, si se quiere, criterios. Filas de solo añadir en `backlog_items`, una por versión, con clave `(item_id, version)`; cada
     cambio es una versión con todo su contenido, y retirar (`BacklogItemRetired`) y reordenar (`BacklogOrderChanged`) son
     decisiones de la persona en el registro. Nada se borra. `BacklogItemRecorded` anuncia cada versión con el SHA-256 canónico de
     su contenido (`BacklogItem.Sha256`) antes de guardar la fila, y leer el backlog solo devuelve filas anunciadas con el mismo
     hash; un anuncio sin fila se completa al volver a escribir. Sus entradas no tienen ejecución (`NoRun`) y esperan a que
     `IProjectActivity` deje libre el proyecto, como la memoria.
  6. **El tablero es una vista** (`BoardRules`, puro): siete columnas (`Backlog`, `Planned`, `InProgress`, `InReview`, `Returned`,
     `Done`, `Integrated`) que salen de los hechos del registro verificado, nunca de arrastrar. **Ninguna tarjeta llega a «hecho» o
     «integrado» sin un veredicto `Approved` y válido que dé el requisito por satisfecho**; un veredicto impugnado, `EscalateToHuman` o
     `Unresolved` la dejan en revisión. «Integrado» exige además «hecho». Una prueba con hechos aleatorios sembrados lo fija.
  7. **Sin tablas para el estado:** ni de tareas, ni de columnas, ni de vínculos. Las claves foráneas de `milestones` impiden un
     hito sin requisitos congelados.
- **Alternativas:**
  - **Un gestor propio con estado** (columnas y tarjetas guardadas): descartada, otra fuente de verdad que diverge del registro.
  - **La cabecera del hito mutable con su historia en otra tabla:** descartada, el hash anclado dejaría de decir qué se aprobó.
  - **Una prioridad de urgencia aparte de la del requisito:** descartada, el orden manual ya es una decisión registrada.
  - **Borrar o editar filas del backlog:** descartada por [ADR-0004](0004-registro-de-decisiones-append-only.md).
- **Consecuencias:**
  - Diez tipos nuevos al final de `DecisionKind` (`MilestoneOpened` a `TraceLinkRetracted`), en `V = 1`; los payloads anteriores
    se leen y se reescriben igual. La migración 0006 es idempotente sobre una base en la 0005.
  - `DecisionLog` no cambia. La guarda de `SqliteDecisionLogStore.AppendOnlyTriggers` sigue cubriendo solo el registro y la memoria:
    `milestones` y `backlog_items` se defienden con sus triggers y con el anclaje, como `debate_sessions`.
  - El cierre (`IMilestoneCloser`, WP3) es «Auditar proyecto completo» con el conjunto del hito: no hace falta un propósito nuevo.
  - Contratos en [docs/04 §22](../04-contratos.md#22-fase-9-trabajo-y-trazabilidad) y modelo en [docs/05 §4](../05-modelo-de-datos.md#4-migraciones).
