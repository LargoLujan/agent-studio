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

## Adenda de WP1 · 2026-11-11

- **Servicios.** `TraceLinkService` registra, confirma y retira; lo asertado por el sistema lo decide `agent-studio` y lo de una persona, esa persona. Lee también
  `MilestoneLinked` como vínculo del hito (asertado por el sistema si lo vinculó `agent-studio`), que no se retira por aquí. `TraceSuggester` infiere de los trailers, de los
  ids de una ejecución o de un hito citados en un mensaje y de la rama de una ejecución, solo lo que el registro conoce, y no ofrece de nuevo lo confirmado, lo que el sistema o
  un hito ya afirman ni lo que una persona retiró.
- **Contrato.** `CommitNode` gana `Message` (opcional): los trailers están en el último párrafo del mensaje y el asunto solo no los trae.
- **Trailers.** El commit del sistema de un intento los escribe con `CommitTrailers.Append`. Los commits de integración y del intento interrumpido siguen con sus líneas
  antiguas, que no se leen como trailers del sistema.
- **Pendiente de WP1.** `ITraceChainReader`, `IParticipationProjector`, `IRepositoryAnnotator` e `IRepositoryActivityReader` no se han hecho en este paquete.

## Adenda del ajuste de la cadena · 2026-11-12

- **Una sola regla para el veredicto** (VerdictReach): el tablero, la cadena y la participación deciden con ella a qué tareas y requisitos alcanza un veredicto. Dentro de su
  alcance (la tarea sobre la que se emitió si es corriente; la ejecución si se emitió sobre la tarea de revisión o sobre ninguna) juzga una tarea solo si evaluó un requisito que
  cubre, y un requisito solo si lo evaluó; sin evaluaciones por requisito, juzga todo su alcance. Corrige la cadena, que colgaba el veredicto de la tarea de revisión de todas las
  tareas de la ejecución, y la participación, que no se lo atribuía a la tarea ejecutora.
- **Varios veredictos (D-38 a D-40).** La cadena los enseña todos, en el orden del registro, con `TraceChainStep.Standing`: en vigor, sustituido, impugnado o revisado. Está en
  vigor el último que juzgó cada requisito (como el tablero) y, si varias ejecuciones lo cubren, el de la última que empezó; un veredicto impugnado que es el último queda
  impugnado hasta que cierre su revisión, y revisado si esta cerró con otra decisión que la suya. Nunca se enseña como vigente uno que no lo es. Es un campo opcional nuevo, sin
  romper el contrato.
- **El veredicto y la entrega son nodos compartidos**: la cadena es la misma desde cualquiera de los nodos que contiene, y la unidad es la tarea (límite conocido: una tarea que
  cubre dos requisitos lleva el veredicto que evaluó cualquiera de ellos).- **La entrega** solo cuenta tareas fusionadas de verdad: una integración que el motor no aceptó (destino en la ref candidata oculta) no entrega nada, y la clausura por
  prerrequisitos no pasa por tareas que producen código y no completaron.

## Adenda del veredicto de una revisión · 2026-11-12

- **El veredicto de una revisión no compite por ser el vigente.** El producto lo guarda en la ejecución del original con `Supersedes` = el veredicto impugnado y lo anuncia antes
  de cerrar el debate; la regla compartida (`VerdictReach.Revises`) lo reconoce porque su cadena de `Supersedes` llega a un veredicto impugnado, y lo deja fuera de la decisión del
  «vigente» en el tablero, la cadena y la participación. Su decisión llega solo por el estado del impugnado (D-40): una revisión que vuelve hacia una aprobación sigue en
  revisión hasta que la persona la acepta; abierta o decidida por la persona, también. Un `Supersedes` sin impugnación (un juez que corrige el suyo) sustituye como siempre.
  Las pruebas guardan ahora el veredicto de revisión con el formato del producto, y una prueba de integración (Fase 9) lee el tablero tras una impugnación real.

## Adenda de la regla del veredicto en vigor · 2026-11-12

Una revisión independiente encontró cinco huecos en la regla compartida; esta es la regla que queda (detalle en docs/04 §23.6):

- **Las tarjetas de tarea siguen la misma regla que las de requisito.** Una tarea completada va con el último veredicto válido que la juzgó (`VerdictReach.Judges`) y su estado
  (`VerdictReach.Standing`); en revisión si está impugnado o ninguno la juzgó. Antes tomaba «el último veredicto no impugnado de la ejecución», que se saltaba uno posterior
  impugnado y daba por juzgada una tarea que el veredicto no tocó. Una tarea que no cubre requisitos la juzga todo veredicto de su alcance.
- **«De la revisión» es cuestión de orden.** Solo es de una revisión el veredicto emitido después de abrirse la impugnación del que sustituye (la secuencia del registro, como la
  entrega); una corrección anterior a la impugnación sustituye como siempre. Decisión tomada con D-40, que no fija el orden: coincide con lo que ya exigía `DeliveryService`
  («el veredicto es anterior a la impugnación» es incoherencia). La recursión de `Revises` recuerda lo recorrido, de modo que un ciclo de `Supersedes` no desborda la pila.
- **Dos lecturas del estado de una impugnación, no una, que no se contradicen.** No se unificó en una sola función: `DeliveryService.Revisions` es el evaluador delicado de lo que
  se entrega (comprueba además la coherencia de cada revisión: requisitos, commit, almacén, aceptación atada) y no se toca más de lo necesario. `ChallengeReader` (que repite
  `ChallengeLog.StatusOf` de Debate) se alinea con él en dos puntos: la impugnación cuya revisión la persona rechazó abrir no cuenta; y solo la del veredicto con el que terminó la
  ejecución exige aceptación, la de cualquier otro se lee como un veredicto aprobado. Una tabla de escenarios (`RevisionAgreementTests`) lee el mismo registro con el tablero y con
  la entrega: nada se entrega que el tablero tenga en revisión ni queda en revisión algo que la entrega dejaría pasar. Se mantiene un caso en que la entrega es más estricta (ver docs/04).
  Riesgo abierto: `ChallengeLog.StatusOf` (Debate; lo usan `VerdictChallengeService` para no impugnar dos veces y para aceptar una revisión) sigue diciendo `Pending` ante una
  impugnación rechazada y `AwaitingAcceptance` ante la de un veredicto que no es el de cierre.
- **La cadena respeta el orden del registro al marcar una entrega:** solo las tareas completadas (e integradas) cuando se hizo.

## Adenda de una sola lectura de las impugnaciones · 2026-11-12

Cierra el riesgo abierto de la adenda anterior (P63). El estado de una impugnación se lee ahora con **una sola función**, `ChallengeRules.Read` (`Application/Judging`, pura sobre el registro
y el almacén de veredictos): la llaman `ChallengeReader` (tablero, cadena y participación) y `ChallengeLog.StatusOf` (Debate, que usa `VerdictChallengeService`). La entrega
(`DeliveryService.Revisions`) sigue con su evaluador, que es el delicado y comprueba además la coherencia de cada revisión; `RevisionAgreementTests` (tabla de 15 escenarios) lee el
mismo registro con las tres lecturas.

- **Impugnación cuya revisión la persona rechazó abrir:** no cuenta (`None`), como en la entrega. Antes Debate decía `Pending` y la persona no podía volver a impugnar el veredicto. Ahora
  puede; si una impugnación anterior ya se juzgó, o hay una revisión abierta, sigue mandando. `ChallengeLog.LatestRevision` salta las rechazadas, de modo que aceptar la revisión juzgada
  sigue siendo posible aunque después se rechazara abrir otra.
- **Impugnación de un veredicto que no es el de cierre** (`RunFinished.VerdictId`): se lee como si ese veredicto fuera una aprobación, como en la entrega. Aceptar una revisión solo
  tiene sentido para el veredicto con el que terminó la ejecución, el que decide lo que se entrega; antes Debate decía `AwaitingAcceptance` y pedía una aceptación que la entrega
  nunca exigía. Ahora esa revisión sale `Confirmed` o `Revoked` y `AcceptRevisionAsync` la rechaza por no esperar aceptación.
- **Los veredictos de la revisión deben estar en el almacén, válidos**, también para Debate (antes solo lo exigía el tablero): `ChallengeLog.StatusOf` recibe el almacén.
- **Tarjetas de tarea y evaluación por requisito.** La tarea sigue a sus requisitos: con un veredicto aprobado que da algún requisito que la tarea cubre por no satisfecho, la tarea no
  está en «Hecho» (Devuelto, o En revisión si es no verificable o el veredicto no evalúa por requisito), igual que su requisito. El destino del requisito no cambia. Es **provisional**:
  «aprobado pero no cumplido → en revisión» es una regla del tablero que el dueño tiene pendiente de decidir.

## Adenda de la cadena de impugnaciones · 2026-11-12

Cierra las dos divergencias que `RevisionAgreementTests` dejaba fijadas con comentario (P64). El estado en vigor de un veredicto (`VerdictReach.Standing`, que usan el tablero, la
cadena y la participación) sigue ahora **la cadena entera** de impugnaciones (`ChallengeRules.Follow`, en `Application`, junto a `Read`), como el evaluador de la entrega: el veredicto de
una revisión puede estar impugnado y su revisión también. Cada revisión juzgada es una rama; si alguna está abierta, abortada, decidida por una persona o espera aceptación, la tarjeta
está «En revisión»; si alguna termina en un veredicto que no aprueba, «Devuelto»; solo termina en «Hecho» si todas acaban en una aprobación que cuenta (F9-2: ninguna tarjeta en «Hecho»
sin un veredicto aprobado en vigor). Protecciones: 16 niveles como la entrega, cada veredicto una vez (los ciclos o una revisión que nombra uno ya recorrido fallan cerrado, en
revisión). La cadena marca como «revisado» al veredicto de revisión que la siguiente desplaza, y la participación no atribuye la aprobación de uno cuya cadena está en revisión.

- **Un intento que el juez corrigió** (no el de cierre) y cuya revisión sigue abierta o retiró la aprobación arrastra la tarjeta del veredicto que lo corrige, como bloquea la entrega
  (antes la tarjeta seguía en «Hecho»).
- **El veredicto de una revisión no se lee como una aprobación** por no ser el de cierre (`AsApproval`): antes, una revisión que daba la vuelta hacia una aprobación sobre una que había
  retirado la anterior salía `Confirmed` en Debate y la persona no podía aceptarla.
- **Impugnación abortada:** `ChallengeStatus.Aborted`. No cuenta y se puede volver a impugnar (antes `Pending` lo impedía aunque la entrega dice «vuelve a impugnarlo»); la entrega y la
  tarjeta siguen bloqueadas mientras sea la última palabra.
- La entrega (`DeliveryService`) no se ha tocado. Queda abierto que una revisión cerrada sin decisión (un jurado que discrepa) siga en `Pending` en Debate mientras la entrega pide
  volver a impugnar.
