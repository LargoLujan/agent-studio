# ADR-0010 · El estado de una ejecución se deriva del registro de decisiones; SQL explícito sin Dapper

- **Estado:** aceptada · 2026-10-15
- **Contexto:**
  - La Fase 3 ejecuta grafos de tareas durante minutos u horas y tiene que poder reanudarlos tras un reinicio (F3-4).
  - La migración 0001 trae tablas de estado (`tasks`, `task_dependencies`, `task_status_history`, `agent_runs`,
    `usage_records`), pero solo `decision_log`, `evidence`, `verdicts`, `debate_contributions` y `requirement_sets` son
    append-only (ADR-0004); `task_status_history` ni siquiera tiene triggers.
  - Guardar el estado en dos sitios obliga a escribir dos veces y deja abierta la divergencia: una tabla retocada, o una
    escritura a medias, daría una ejecución que el registro no explica.
  - ADR-0005 anticipaba Dapper para los repositorios.
- **Decisión:**
  1. **El estado de una ejecución es el registro.** Cada cambio de estado de una tarea es una entrada de
     `decision_log` (`TaskStatusChanged`, con sus evidencias y el motivo del bloqueo; `ConditionEvaluated` para las
     condiciones). Lo inmutable de la ejecución (el plan, la plantilla de agentes, el perfil de verificación, la
     petición, el commit base) va en `plans` y `orchestration_runs` (migración 0002), que solo se insertan.
  2. **Reanudar es reproducir.** `IDecisionRecorder.LoadAsync` lee el registro y ejecuta `Verify()`. Si la cadena está
     rota, se bloquean la reanudación y las ejecuciones nuevas y se muestra una alerta de integridad. Si no,
     `RunStateReplayer` reconstruye el `TaskGraph` aplicando los eventos con la misma API pública que usa el motor
     (`TransitionTo`, `Complete`, `CompleteCondition`, `Retry`, `PropagateSkips`). Un registro que las reglas del dominio
     no habrían permitido no se puede reproducir: la ejecución queda como no reanudable y con la razón. Lo mismo pasa si
     el perfil de verificación de la cabecera ya no da el hash que registró `RunStarted` (ADR-0017).
  3. **Nada se relanza solo.** Una tarea que estaba en `Running` al reiniciarse pasa a `Blocked` con motivo
     `Interrupted` y su intento se cierra con `AgentRunFinished(Interrupted)`, sin gastar reintentos. Reanudar es una
     decisión del usuario, porque gasta cuota. Como mucho hay una ejecución de agente por intento.
  4. **Un solo escritor por proyecto.** `DecisionRecorder` mantiene el `DecisionLog` en memoria con un cerrojo por
     proyecto:
     - la secuencia y el hash los asigna el dominio, no la base de datos;
     - la hora es la mayor entre «ahora» y la de la última entrada, porque el registro rechaza una cronología inversa;
     - si el almacén falla, vuelve a leer el registro de él en lugar de seguir con una copia que puede ir por delante;
     - no escribe sobre un registro que no verifica.
  5. **Los payloads se serializan siempre igual** (camelCase, compactos, enums por nombre, ids como cadenas, sin
     nulos), llevan `V` y `RunId`, y **nunca llevan prompts ni salida de agentes**: llevan el id y el SHA-256 de la
     evidencia que los contiene. El texto forma parte del hash, de ahí que cambiar un payload exija subir `V`.
  6. **Las tablas `tasks`, `task_dependencies`, `task_status_history`, `agent_runs` y `usage_records` de la 0001 no se
     usan en la Fase 3.** En la Fase 4 serán proyecciones reconstruibles a partir de `AgentRunFinished` y
     `TaskStatusChanged`.
  7. **SQL explícito con Microsoft.Data.Sqlite, sin Dapper.** Un paquete menos; el SQL, con sus triggers e índices, se
     ve y se prueba tal cual, y el mapeo a mano se cubre con pruebas de ida y vuelta. Esto sustituye lo que decía
     ADR-0005 sobre Dapper.
  8. **Numeración de migraciones.** La 0002 contiene `plans`, `orchestration_runs`, `skill_activations` y las columnas
     nuevas de `evidence` y `verdicts`. Las tablas de skills del doc 05 (`skills`, `skill_health_checks`) pasan a la
     0003, de la Fase 4.
- **Alternativas:**
  - Una proyección de estado en tablas, escrita junto al registro (doble escritura y divergencia posible).
  - Solo tablas, sin reproducir (no habría forma de saber si la tabla cuenta la verdad, y el juez necesita el registro
    de todos modos).
  - Instantáneas del grafo cada cierto número de entradas (complejidad sin necesidad: el registro de una ejecución tiene
    decenas de entradas).
  - Dapper (otra dependencia para consultas cortas, que además oculta el SQL justo donde importan los triggers). EF Core:
    ver ADR-0005.
- **Consecuencias:**
  - Hay una única fuente de verdad: lo que el juez examina, lo que la interfaz muestra y lo que se reanuda salen de la
    misma cadena de hashes.
  - Abrir un proyecto cuesta releer y verificar su registro, proporcional a su tamaño; en la Fase 3 son pocas entradas.
  - Un registro manipulado bloquea la reanudación (fallo cerrado). Es evidencia, no prevención (ADR-0004).
  - El orden de los eventos de una condición es parte del contrato: la rama elegida (`ConditionEvaluated`) se registra
    justo antes de la transición que completa la condición.
  - Cambiar la forma de un payload exige subir `V` y mantener los ficheros dorados de registros (riesgo de evolución).

## Adenda · 2026-10-16 · integridad de evidencias y del registro

1. **Numeración de migraciones (corrige la decisión 8).** La `0003` es `0003_evidence_subject_commit.sql`: añade a
   `evidence` el commit que verificó cada build o ejecución de tests (`subject_commit`). El motor se lo pasa al runner
   de verificación en `EvidenceScope.SubjectCommit`, y el validador del juez solo deja que los tests del commit en
   revisión apoyen una aprobación (`ApprovedWithoutTestRunForCommit`). Las tablas de skills (`skills`,
   `skill_health_checks`) pasan a la `0004`, de la Fase 4.
2. **Ancla exterior del registro (completa la decisión 2).** `Verify()` solo comprueba la cadena desde el génesis: un
   registro truncado por el final, o reescrito desde una entrada con los hashes recalculados, la pasa. Por eso
   `DecisionRecorder` guarda además la cabeza fuera del proyecto, en
   `%LOCALAPPDATA%\AgentStudio\anchors\<projectId>.json` (`FileDecisionLogAnchorStore`; la carpeta la da la
   composición con `DecisionLogAnchorOptions`, y ese es su valor por defecto):
   - **Escritura en dos pasos y atómica.** Antes de guardar una entrada se anuncia en el ancla (`NextHash`); después,
     el ancla pasa a ella (número de entradas y hash de cabeza). Cada escritura va a un temporal de la misma carpeta y lo
     sustituye con un renombrado. Si el proceso muere entre los dos pasos, la entrada anunciada se reconoce; si el
     anuncio no se puede escribir, la entrada no se guarda.
   - **Al cargar** (`LoadAsync` y antes de escribir), el registro deja de ser fiable, con un motivo claro en
     `DecisionLogVerification`, si es más corto que el ancla (le quitaron el final), si en la posición anclada hay otro
     hash (lo reescribieron), si tiene entradas más allá del ancla que no son la anunciada (las añadió otro: así se
     esquivaría la comprobación de `EvidenceRegistered` del dossier) o si el ancla no se puede leer. Como cualquier
     registro que no verifica, bloquea la revisión (`NeedsHuman`), las ejecuciones nuevas y la reanudación.
   - **`AuditAlertRaised`:** no es posible en ese caso, porque el registrador nunca extiende un registro que no
     verifica (decisión 4); la alerta es el propio resultado de la verificación, que cada consumidor muestra.
   - **Sin ancla** (un proyecto anterior a este cambio, otra máquina) solo se comprueba la cadena, y la siguiente
     entrada lo ancla.
   - **Límite honesto.** Un proceso que corra como el mismo usuario puede reescribir las dos copias, la base de datos
     y el ancla. El ancla detecta la truncación y la manipulación hechas solo dentro del proyecto (por ejemplo, un
     agente con shell que edita `.agentstudio/agentstudio.db`); la protección real exige aislamiento de procesos
     (Fase 7, F7-4). Restaurar una copia antigua de la base de datos, o usar la misma carpeta de proyecto desde dos
     máquinas, se ve también como manipulación: decide una persona.

## Adenda · 2026-10-17 · lo que decide una aprobación o una reanudación, anclado en el registro

Las filas de `evidence` y `orchestration_runs` son del almacén: quien quite sus triggers puede reescribirlas sin romper
la cadena ni el ancla. Lo que decide una aprobación, una reanudación o con qué se lanza el juez pasa al registro.

1. **`EvidenceRegistered` versión 2 (completa la decisión 5).** Además del id, el tipo, el origen y el SHA-256, ancla
   el título, el autor (`producedBy`), la hora de captura (se compara como instante), los contadores de la ejecución de
   pruebas (aprobadas, fallidas, omitidas y código de salida, nunca un «pasó» calculado) y el commit verificado
   (`subjectCommit`).
   - El motor solo escribe la v2 y la escribe con el registro que le dio quien capturó la evidencia (el verificador o
     el integrador), nunca releyendo la fila; si la fila de una evidencia de la integración ya no coincide al
     registrarla, la integración falla. Cualquier fila que no coincida con su entrada queda fuera del dossier con un
     problema de integridad que nombra el campo y deja la ejecución sin poder reanudarse.
   - El juez recibe la evidencia tal como la ancló el registro. `Covers` (los requisitos que dice cubrir) no se ancla:
     es una pista para el juez, que evalúa cada requisito con la evidencia que cita, y el validador no la usa.
   - Una entrada v1 se sigue leyendo y reescribiendo byte a byte igual (la cadena no cambia), pero sus metadatos solo
     los dice la fila: llega al juez con un problema de integridad y una ejecución con alguna entrada v1 no se reanuda.
2. **Una evidencia anunciada solo por la v1 bloquea cualquier aprobación del dossier**, no solo la que la cite: el
   validador no aprueba un dossier con problemas de integridad (`ApprovedWithIntegrityProblems`) y decide una persona.
   Es preferible a aprobar sobre un dato que nadie puede comprobar; las ejecuciones nuevas ya no producen v1.
3. **`RunStarted` versión 2 con `HeaderHash` (completa la decisión 2).** Es el SHA-256 canónico de toda la cabecera
   guardada (`RunHeaderHash`): la hora de inicio como instante, los hashes del plan, de los requisitos y del perfil, la
   evidencia de la petición original, el commit y la ref base, la ejecución a la que sigue, las opciones y cada campo
   de cada agente de la plantilla (nombre, rol, proveedor, vendor y modelo, instrucciones, objetivos, herramientas,
   permisos de rutas, presupuesto, rondas, memoria y política de revisión), con la plantilla ordenada por id y cada
   campo con prefijo de longitud (un valor ausente nunca equivale a uno vacío).
   - Deja fuera las claves con que se busca la cabecera (el id de la ejecución, que ya nombra `RunStarted`, y el
     proyecto). El contenido del plan y de los requisitos entra por sus hashes, que sus almacenes recalculan al leer.
   - Reanudar exige que la cabecera dé ese hash. Una ejecución con `RunStarted` v1 no lo tiene y no se reanuda
     (alerta `run.not-resumable`): se lanza una de seguimiento.
   - El juez solo se lanza con esa misma cabecera, porque de ella salen sus instrucciones, herramientas y modelo. Si no
     da el hash anclado, o con `RunStarted` v1, no se lanza: alerta `RunHeaderMismatch`, decide una persona y el dossier
     lo lleva como problema de integridad (de la cabecera salen también la petición original y el inicio de la
     reproducción).
4. **Cierre de la ejecución con pruebas (N-06).** Si la política de revisión de un agente que escribió código exige
   pruebas del sistema, cada commit final necesita una ejecución de pruebas capturada por el sistema que pase, tal
   como la ancló el registro (v2: `TestRun`, `CapturedBySystem`, ese commit y contadores que pasan), nunca según la
   fila. Son finales los commits de los productores de código completados sobre los que no construye ningún otro: la
   integración final o una tarea que escribe después de ella; con varios escritores sin integrar, cada uno necesita la
   suya. Si falta, la ejecución termina `Failed` con alerta `run.tests-required`. El validador del plan lo anticipa con
   los pasos del perfil que ejecutan pruebas (`TestStepRequiredByPolicy`): una compilación no basta.

## Adenda · 2026-10-17 · evidencia de la integración manipulada

1. **Una fila de la evidencia de la integración que cambia entre su captura y su lectura es una brecha de integridad, no
   un fallo que se reintenta (corrige el punto 1 de la adenda anterior).** El motor relee las filas milisegundos después
   de que el integrador las capture, en el mismo proceso: una fila que falta, o que no coincide con el registro que
   devolvió el integrador, solo la explica alguien que reescribe el almacén por detrás de sus triggers.
   - Se trata como un repositorio del usuario que cambia o una ref de origen que se mueve: no se reintenta ni se
     despacha nada más, se lanza la alerta `integration.evidence-tampered` (nombra la integración, cada evidencia y el
     campo que cambió) y la ejecución termina `Failed`, así que no se revisa ni se entrega. La alerta queda en el
     registro: al reanudar tampoco se despacha nada, aunque la fila vuelva a su valor.
   - La evidencia se ancla igualmente tal como se capturó; la fila que no coincide es un problema de integridad del
     dossier y de la reanudación.
   - Un id que el integrador devuelve sin su registro es un fallo del propio integrador, no una manipulación: no se
     ancla y el intento falla como hasta ahora.
   - **`IntegrationPerformed` en ese caso** se registra como el de un candidato rechazado: con la ref candidata de la
     tarea como destino y el commit fusionado como `ResultCommit`, aunque el integrador ya hubiera avanzado la rama
     `agentstudio/run-<run>` hasta él (la alerta lo dice). Se usan las dos defensas: `RunLog.CandidateCommit` solo
     cuenta las integraciones cuyo destino es la rama, así que ese commit nunca es el que revisa un juez, ni siquiera si
     llegara a lanzarse; y la ejecución falla. Lo que integró una ejecución lo dice el registro, no la rama: la entrega
     solo toma la integración aprobada de una ejecución `Succeeded`, y solo si la rama sigue en ella.
   - **Consecuencia:** una ejecución de seguimiento parte de la última `IntegrationPerformed` con resultado sin mirar su
     destino, como tras un conflicto o una verificación fallida. Por eso no se sigue una ejecución que lanzó la alerta
     `integration.evidence-tampered`: `StartAsync` lo rechaza antes de escribir nada y pide empezar una ejecución nueva
     desde la rama del usuario.

## Adenda · 2026-10-17 · la ejecución arranca con la plantilla validada

1. **La plantilla de agentes de una ejecución es la que se validó y se aprobó, no la que haya en el almacén al arrancar
   (completa el punto 3 de la adenda «lo que decide una aprobación o una reanudación»).** La cabecera congela la
   plantilla y `RunStarted` la ancla, pero se leía de SQLite al arrancar: un agente editado entre la revisión del plan y
   el arranque (con `Bash`, por ejemplo) quedaba congelado como si se hubiera aprobado.
   - `RunRequest.ValidatedRoster` lleva la plantilla con la que el lienzo validó el plan y que mostraba al aprobarlo.
     `StartAsync` exige que la del almacén sea exactamente esa, comparadas con `RosterHash`; si no, o si la petición no
     la trae, no arranca ni escribe nada, y el mensaje dice qué agentes se añadieron, se quitaron o cambiaron.
   - `RosterHash` es la única codificación de una plantilla: `RunHeaderHash` escribe con ella la de la cabecera sin que
     cambie su valor (una prueba lo fija), así que lo que se compara al arrancar es lo que se ancla.
   - El lienzo guarda la plantilla al aprobar y la envía tal cual: si el proyecto se relee después con otra, el arranque
     se rechaza y hay que aprobar otra vez. Que el lienzo anule la aprobación en cuanto cambia la plantilla, como ya
     hace con los requisitos, queda pendiente.

## Adenda · 2026-10-17 · lo que alimenta un prompt se lee con el hash que ancló el registro (R3-08)

Dentro del modelo de [ADR-0018](0018-modelo-de-amenaza-fase-3.md): con la app cerrada, alguien reescribe la fila del
informe de una tarea (mismo id, otro `run_id`, otro contenido con su hash). La reanudación no la veía en el listado de
la ejecución y no la comparaba; la tarea siguiente la leía por su id y el almacén la daba por buena contra el hash de su
propia fila.

1. **Reanudar exige que cada evidencia registrada siga siendo una fila de la ejecución (completa el punto 1 de la adenda
   «lo que decide una aprobación o una reanudación»).** Un `EvidenceRegistered` de la ejecución cuya fila no está en
   `ListByRunAsync` (se borró o se dio a otra ejecución) deja la ejecución sin poder reanudarse, con la alerta
   `run.not-resumable`, que nombra la evidencia y su tarea.
2. **Toda lectura de contenido que alimenta un prompt o una decisión toma el SHA-256 que ancló `EvidenceRegistered` y
   comprueba con él los bytes que usa, los mismos que hashea.** El almacén solo compara el contenido con su fila, y quien
   reescribe el contenido puede reescribir también la fila.
   - `RunRecorder` ya no lee sin ese hash: `ReadBytesAsync` y `ReadTextAsync` lo exigen.
   - El informe de una tarea anterior que recibe un ejecutor: si no da el hash anclado, el agente no se lanza. Se
     registra la alerta `upstream.evidence-tampered` (nombra el informe, el hash y las dos tareas) y la tarea queda
     bloqueada a la espera de una persona, sin reintentarse sola.
   - Los registros de intento al reanudar se leen igual; uno que no da su hash deja la ejecución sin reanudar.
   - La petición original del dosier del juez solo se muestra si da el hash de su `EvidenceRegistered`. Si no, o si el
     registro no la ancló, el juez recibe una nota en su lugar y el dosier un problema de integridad, que impide
     cualquier aprobación. Las copias de las evidencias del dosier ya se comprobaban contra el hash anclado sobre el
     mismo flujo que copian.
3. **`IEvidenceStore` no cambia.** La comprobación la hace quien lee, contra el hash del registro, y no hace falta otro
   parámetro en el almacén: unos bytes que dan el SHA-256 anclado son el contenido que se capturó.

## Adenda · 2026-10-18 · el almacén del proyecto no se usa a través de enlaces (A-05)

Dentro del modelo de [ADR-0018](0018-modelo-de-amenaza-fase-3.md), porque es algo dejado en el proyecto antes de un paso.
Por ejemplo, un repositorio clonado con un enlace simbólico, o un enlace dejado entre dos sesiones:

- una junction o un enlace en `.agentstudio`, `evidence` o `tmp`;
- un fichero de la base de datos que es otro nombre (enlace duro) de un fichero ajeno.

Así se desviaban fuera del proyecto las escrituras de Agent Studio, con integridad Medium.

1. **Al abrir el proyecto, antes de abrir la base de datos o escribir nada**, `ProjectWorkspace` toma la ruta final de la
   raíz (`GetFinalPathNameByHandle`). Los enlaces por encima de la raíz se respetan, porque son cosa del usuario
   (`C:\dev` → `D:\dev`). Después comprueba cada pieza del almacén que exista (`ProjectStorageGuard`):
   - `.agentstudio`, `evidence` y `tmp`: carpetas de verdad, nunca un punto de reanálisis, a las que Windows llega en su
     sitio bajo la ruta final de la raíz.
   - `agentstudio.db` y los ficheros que SQLite deja a su lado (`-wal`, `-shm`, `-journal`): no son enlaces, tienen un
     solo nombre (un enlace duro escribiría también el otro fichero) y están en su sitio. Se leen por un handle que no
     sigue enlaces.
   - `agentstudio.db.lock` no es un enlace ni una carpeta. Se abre con borrado al cerrar, así que un enlace borraría su
     destino. Solo se leen sus atributos, porque otra instancia puede tenerlo abierto sin compartir.

   El orden: `.agentstudio` se comprueba antes de crearla y otra vez después, el cerrojo antes de abrirlo, y la base de
   datos y las carpetas con el cerrojo tomado. Si algo falla, el proyecto no se abre: `ProjectStorageIntegrityException`
   nombra la ruta y el motivo, y no se escribió nada a través del enlace.
2. **El almacén de evidencias** comprueba `.agentstudio` antes de crear `tmp` o `evidence`, y cada carpeta antes y
   después de crearla. El temporal de cada captura se comprueba por el mismo handle que lo escribe: Windows tiene que
   llegar a él dentro de `tmp` del proyecto.
3. **Límites.**
   - Un proceso del mismo usuario que corre a la vez puede cambiar una pieza entre la comprobación y el uso. Es un
     residual del punto 2 de ADR-0018, que se estrecha comprobando el handle donde lo hay.
   - `verification.json`, `canvas-layout.json` y `bitacora/` los escriben otros módulos (el escritorio y la memoria) sin
     pasar por esta comprobación. Un enlace en `.agentstudio` impide abrir el proyecto, pero no esas escrituras: queda
     abierto, por clasificar.
   - Un enlace simbólico de fichero en el cerrojo no se probó, porque crearlo exige un privilegio que las pruebas no
     tienen. Las junctions y los enlaces duros sí se prueban.

## Adenda · 2026-10-18 · el ancla es de la ubicación del proyecto, no del id de su base de datos (A-01)

Dentro del modelo de [ADR-0018](0018-modelo-de-amenaza-fase-3.md), porque es una fila de SQLite reescrita con la app
cerrada:

- se cambia el id del proyecto (`projects.id` y cada `project_id`);
- se deja una cadena truncada o reescrita con los hashes recalculados.

El ancla se buscaba por ese id (`<projectId>.json`), así que con otro id no había ancla, y sin ancla solo se comprobaba
la cadena. El hash de una entrada no incluye el proyecto.

1. **La clave del ancla es la ubicación física del proyecto (completa el punto 2 de la adenda del 2026-10-16).**
   - El fichero es `%LOCALAPPDATA%\AgentStudio\anchors\<sha256 de la ruta final de la raíz, en mayúsculas>.json`. La
     ruta final la da `GetFinalPathNameByHandle` al abrir el proyecto, con los enlaces de encima resueltos: es la misma
     que usa la comprobación del almacén (adenda A-05).
   - El formato pasa a `agent-studio/decision-log-anchor/2`, que guarda además el `projectId` y la `location`.
   - Un ancla de la ubicación con otro `projectId` es un fallo de integridad (`DecisionLogAnchorMismatchException`), y
     el mensaje nombra el proyecto anclado y sus entradas. Un fichero de otra ubicación o dañado es `InvalidDataException`.
     Ninguno de los dos cuenta nunca como «sin ancla».
2. **Un historial con entradas y sin ancla para su ubicación no es fiable** (corrige «Sin ancla» de la adenda del
   2026-10-16, que solo comprobaba la cadena). Pasa al mover o copiar la carpeta, al traerla de otra máquina, o con una
   base de datos que llega con el repositorio.
   - Bloquea registrar, planificar, ejecutar, reanudar y revisar, como cualquier registro que no verifica. El motor y el
     registrador ya se negaban a escribir en un registro así; ahora este también lo es.
   - La app lo muestra como problema de integridad en la cabecera del orquestador, junto a lo que falta
     (`DecisionLogState.Unanchored`, `SessionContext.IntegrityProblem`).
   - Un historial vacío sin ancla es un proyecto nuevo: lo ancla su primera entrada, como hasta ahora.
3. **Solo una persona lo desbloquea, con «Aceptar este historial como nuevo origen».**
   - La pregunta va por el diálogo de aprobación (`IApprovalPrompt`), como acción crítica con confirmación explícita. La
     respuesta queda atada al historial mostrado: entradas, cabeza y ancla encontrada, en `ApprovalRequest.Binding`. Si
     el historial cambió desde que se mostró, no se acepta.
   - No pasa por `IApprovalGate`, porque el gate registra `ApprovalRequested` antes de preguntar, y en un registro
     bloqueado no se puede escribir. Por eso el registrador (`IDecisionLogOrigin`, el mismo `DecisionRecorder` y bajo
     el mismo cerrojo) escribe las entradas del gate tras el sí, y antes una alerta:
     1. `AuditAlertRaised` (`decision-log.origin-accepted`), con lo que se aceptó y por qué no tenía ancla.
     2. `ApprovalRequested`, con la pregunta.
     3. `ApprovalGranted`, con la persona como actor.

     Después escribe el ancla. No anuncia nada antes: si el proceso muere antes del ancla, el historial sigue sin ella y
     se vuelve a preguntar, así que ningún historial queda anclado sin su aceptación en el registro.
   - Un no, o cerrar el diálogo, no escribe nada (el registro sigue bloqueado y no admite entradas) y la pantalla lo
     dice.
4. **Un historial vacío bajo el ancla de otro proyecto también se puede aceptar.** Ocurre al borrar `.agentstudio` o al
   volver a clonar en la misma carpeta.
   - No hay entradas de nadie que aceptar, y la alerta deja constancia del historial que vivía allí: el proyecto y sus
     entradas. El texto de la pantalla es distinto del de la carpeta movida: dice que aquel historial ya no está.
   - Esto amplía lo pedido, que trataba todo id distinto como fallo de integridad. Con entradas sigue siéndolo, sin
     aceptación posible, porque es la huella del ataque: aceptar ahí convertiría el bloqueo en un clic.
5. **Migración de las anclas antiguas.**
   - Un ancla `<projectId>.json` (formato 1) solo se lee si la ubicación no tiene ancla, y solo como ancla de ese id.
   - La primera vez se mueve a la clave de la ubicación: se escribe allí y se borra la antigua. Si se quedara, dejaría
     de seguir al registro, y una copia o un traslado posterior la tomaría por su ancla y vería un registro que creció
     detrás de ella: una manipulación sin salida.
   - Coste conocido: una copia del mismo proyecto que se abra antes que el original se queda con el ancla antigua, y el
     original pide la aceptación una vez.
   - Límite: el id de un proyecto que nunca se abrió con esta versión puede cambiarse antes de la migración. Entonces no
     se encuentra su ancla antigua y se ve como historial sin ancla, que solo una persona acepta, con el texto de la
     carpeta movida.
6. **Límites que no cambian.**
   - Un proceso del mismo usuario que corre a la vez puede reescribir el ancla y la base de datos (punto 2 de ADR-0018).
   - Otra carpeta que ocupe después la misma ruta final (otra unidad montada con la misma letra, por ejemplo) encuentra
     el ancla de la anterior: con otro proyecto y entradas es un fallo de integridad, y vacía se acepta como historial
     nuevo.
   - Un historial con entradas bajo el ancla de otro proyecto no se puede aceptar desde la app, ni siquiera cuando es
     legítimo (mover un proyecto a la carpeta donde vivía otro).

## Adenda · 2026-10-18 · la bitácora, el lienzo y el perfil escriben con la política del almacén (H-01, H-02)

Dentro del modelo de [ADR-0018](0018-modelo-de-amenaza-fase-3.md), porque es algo dejado en el proyecto antes de un paso.
Cierra lo que el punto 3 de la adenda A-05 dejó abierto: `bitacora/`, `canvas-layout.json` y `verification.json` se
escribían sin la comprobación del almacén. La quinta revisión (Codex) lo confirmó:

- **H-01.** Con una junction en `.agentstudio/bitacora`, o con un día que es un enlace duro de otro fichero, la
  siguiente línea de la bitácora se escribía fuera del proyecto.
- **H-02.** El lienzo guardaba sus posiciones aunque abrir el almacén se hubiera rechazado, o antes de abrirlo.
  `canvas-layout.json` y `verification.json` se escribían con un temporal y un cambio de nombre por ruta, sin comprobar
  las carpetas.

1. **Una sola política, por un puerto.** `Memory` y `Orchestration` solo dependen de `Application` (ADR-0002). Por eso
   la política de `ProjectStorageGuard` les llega por `IProjectStorageFiles` (`Application/Projects`). La implementa
   `ProjectStorageFiles` (`Infrastructure/Persistence`) con los métodos del propio guard, sin copiarlos. La registra
   `AddAgentStudioPersistence`, y `AddOrchestrationUiServices` si falta. Tiene dos operaciones:
   - `OpenForAppend`:
     - `.agentstudio` y la carpeta del fichero se comprueban antes de crearlas y después.
     - El fichero se abre sin seguir enlaces (`FILE_FLAG_OPEN_REPARSE_POINT`, `OPEN_ALWAYS`), compartido solo para leer.
     - Antes de escribir, ese mismo handle tiene que ser un fichero normal, con un solo nombre y en su ruta final bajo
       la raíz.
     - Otro escritor recibe una violación de uso compartido (una `IOException` con ese `HResult`), nunca un rechazo de
       integridad, y puede reintentar.
   - `Replace`:
     - Comprueba las carpetas igual.
     - Escribe el contenido entero en `tmp/`, con un handle comprobado como el del almacén de evidencias.
     - El destino no puede ser un enlace ni un segundo nombre. Lo sustituye un cambio de nombre.
   - La ruta es relativa a `.agentstudio`: un nombre, o una carpeta y un nombre. Nada de `..`, rutas absolutas ni `:`.
     La carpeta del proyecto tiene que existir.
2. **La bitácora** (`MarkdownDailyJournal`) abre cada día con `OpenForAppend`.
   - Solo reintenta las violaciones de uso compartido: un rechazo sale a la primera.
   - El registro falla cerrado (`JournalingAgentProvider`), así que el agente no se lanza.
   - Al abrir el proyecto, `ProjectStorageGuard.CheckStorage` comprueba también `bitacora`.
3. **El lienzo** solo recibe la raíz del proyecto cuando `IProjectWorkspace.OpenAsync` ha ido bien
   (`OrchestratorDocumentViewModel.OnContextChanged`).
   - Antes de abrir, o con el almacén rechazado, las posiciones se quedan en memoria: no se escribe nada ni se avisa de
     un guardado. La cabecera ya dice por qué no se abrió el proyecto.
   - `FileCanvasLayoutStore.Save` y `VerificationProfileFile.SaveAsync` escriben con `Replace`.
4. **Quién escribe en `.agentstudio`:**
   - Con la adenda A-05, sin cambios: `ProjectWorkspace` (la carpeta, ahora con el helper común), `ProjectLock` (el
     cerrojo), SQLite (`agentstudio.db*`) y `SqliteEvidenceStore` (`tmp/` y `evidence/`).
   - Con esta adenda: `MarkdownDailyJournal` (`bitacora/<día>.md`), `FileCanvasLayoutStore` (`canvas-layout.json`) y
     `VerificationProfileFile.SaveAsync` (`verification.json`).
   - El editor de código guarda por su ruta el fichero que la persona abrió, también uno de `.agentstudio`. Es una
     acción suya sobre un fichero que eligió, no una escritura del almacén.
   - Fuera de la app, el hook `.claude/hooks/bitacora.ps1` del repositorio de Agent Studio sigue los enlaces
     (`New-Item -Force` y `AppendAllText`). Solo corre en las sesiones interactivas de Claude Code sobre este
     repositorio: los CLIs que lanza Agent Studio llevan `--setting-sources ""` y `disableAllHooks`. Aquí no se cambia.
5. **Límites.**
   - Un proceso del mismo usuario que corre a la vez puede cambiar una carpeta entre la comprobación y la apertura:
     sigue siendo un residual del punto 2 de ADR-0018. Los handles lo estrechan, pero `OPEN_ALWAYS` puede dejar un
     fichero vacío fuera antes de que su handle se rechace.
   - Las lecturas (`VerificationProfileFile.LoadAsync`, `FileCanvasLayoutStore.Load`, la vista de la bitácora y
     `UserRepositoryGuard`) siguen abriendo por ruta. No escriben fuera, pero un fichero leído a través de un enlace no
     se rechaza: queda abierto, por clasificar.
   - Los enlaces simbólicos de fichero no se prueban, porque crearlos exige un privilegio. Las junctions y los enlaces
     duros sí.

## Adenda · 2026-10-18 · el ancla antigua se mueve solo cuando el historial verifica contra ella (P-04)

Corrige el punto 5 de la adenda A-01. La sexta revisión vio que `FileDecisionLogAnchorStore.ReadAsync` movía el ancla
antigua (`<projectId>.json`) a la clave de la ubicación, y borraba la antigua, antes de que `DecisionRecorder`
comprobara el historial contra ella. Una copia del proyecto con el historial alterado que se abriera primero se quedaba
con el ancla y luego se rechazaba. El original ya no la encontraba y pedía la aceptación como historial sin ancla.

1. **Leer el ancla antigua no cambia nada.** `ReadAsync` la devuelve cuando la ubicación no tiene ancla, sin moverla ni
   borrarla. Lo demás sigue igual: un ancla dañada, de otro formato o de otro id es `InvalidDataException`, un ancla de
   la ubicación con otro id es `DecisionLogAnchorMismatchException`, y ninguna cuenta como «sin ancla».
2. **Se mueve después de verificar.** Si el historial verifica contra el ancla leída, el registrador se lo dice al
   almacén (`IDecisionLogAnchorStore.ConfirmAsync`, que por defecto no hace nada). Solo entonces el almacén de ficheros:
   1. comprueba que la ubicación sigue sin ancla y que el fichero antiguo sigue siendo el ancla verificada; si no, lo
      deja donde está;
   2. escribe esa ancla con la clave de la ubicación, con un temporal y un cambio de nombre;
   3. borra la antigua.

   Si escribir falla, el registro no es fiable en esa apertura, como cuando la migración fallaba dentro de la lectura, y
   la siguiente apertura lo vuelve a intentar.
3. **Un corte entre escribir y borrar se termina en la siguiente apertura.** Si el proceso muere entre los dos pasos, la
   ubicación ya tiene el ancla y se lee esa. Cuando el historial verifica contra ella y el fichero antiguo es la misma
   ancla, se borra. Un fichero antiguo distinto, o que no se puede leer como ancla de ese id, se deja donde está: nunca
   se lee para una ubicación que tiene ancla.
4. **El coste conocido del punto 5 se estrecha.**
   - Una copia con el historial alterado ya no se queda con el ancla antigua: se rechaza, y el original la migra al
     abrirse.
   - Una copia fiel (el mismo historial, o con la entrada que el ancla anunciaba) que se abra antes que el original sí
     se la queda, y el original pide la aceptación una vez, como antes.
5. **Límite que no cambia.** Borrar el fichero antiguo sigue siendo de mejor esfuerzo.
   - Si falla y el ancla de la ubicación avanza antes de otra apertura, el fichero ya no coincide y se queda.
   - Una copia o un traslado posterior lo tomaría por su ancla y vería un registro que creció detrás de ella, sin
     aceptación posible.
   - El remedio es borrar a mano `%LOCALAPPDATA%\AgentStudio\anchors\<projectId>.json`.

## Adenda · 2026-10-18 · solo se reanuda con los escritores con integridad Medium que se aprobaron (G-02)

Dentro del modelo de [ADR-0018](0018-modelo-de-amenaza-fase-3.md), punto 3: el plan avisa de sus escritores con
integridad Medium antes de aprobarlo. La séptima revisión vio que `StartAsync` comparaba ese aviso con el aprobado
(`RunRequest.ApprovedWarning`, A-04), pero `RunResumer` volvía a preguntar a la sonda del confinamiento, recalculaba la
comprobación estricta y seguía sin mirar el aviso. Además, la pantalla lanzaba la reanudación antes de mostrar el plan.
Un plan aprobado con Claude confinado (sin aviso) se reanudaba, con otra versión de Claude Code instalada o con la sonda
fallando, con su ejecutor como escritor Medium que nadie había visto ni aprobado.

1. **`RunStarted` versión 3 (completa el punto 3 de la adenda «lo que decide una aprobación o una reanudación»).**
   Ancla lo mismo que la v2 y, además, lo que se aprobó de los escritores con integridad Medium:
   - `approvedWarning`: el aviso que el usuario vio junto a «Aprobar plan», el mismo texto que ya comprueba
     `StartAsync`. No se escribe cuando no había ninguno.
   - `approvedWriters`: cada escritor que nombra, sin depender de la redacción. Lleva la tarea, el motivo por su nombre
     (`Shell`, `CodexWrites`, `UnmeasuredCli`, `UnmeasuredClaudeVersion`, `ToolsBeyondMeasuredProfile`, `UnknownAgent`,
     `UnknownStep` o `StepWithoutIsolation`) y, cuando forman parte del motivo, el agente, el CLI, las herramientas y el
     paso. Una lista vacía dice que no había ninguno; una v2 no dice nada.
   - El aviso y el ancla salen de una sola lectura (`MediumIntegrityWriters.Find`).
   - `HeaderHash` sigue igual: `RunHeaderHash` hashea la cabecera guardada, no el payload. Los lectores de la v2 (la
     reanudación, el dosier del juez y la revisión) solo miran `HeaderHash`, así que leen la v3 igual, y una v2 se sigue
     leyendo y reescribiendo byte a byte igual.
2. **Reanudar vuelve a leer los escritores antes de nada.** `RunResumer` los lee del plan, la plantilla y el perfil, ya
   comprobados por sus hashes, con lo que responde la sonda ahora. Lo hace antes de tomar la línea base del repositorio
   del usuario, de recuperar un worktree o de lanzar nada.
   - Con v3, tienen que ser exactamente los anclados, escritor a escritor y en cualquier orden
     (`MediumIntegrityWriters.AreTheApproved`). Uno más, uno menos o uno por otro motivo no es lo aprobado, también
     cuando desaparece uno, como en `StartAsync`.
   - Se comparan los escritores y no el texto: una versión de la app que redacte el aviso de otra forma no bloquea una
     reanudación de lo mismo que se aprobó.
   - Una ejecución con v2 no ancló ninguno: se reanuda solo si su plan no tiene ninguno ahora.
   - Si no, se registra la alerta `run.not-resumable` con los dos avisos y nada más: ningún intento cerrado, ninguna
     tarea movida, ningún worktree tocado, ninguna línea base. `ResumeAsync` lanza `RunNeedsApprovalException`
     (`Application/Orchestration`), con la ejecución, el hash de su plan, si estaba anclado y los dos avisos.
   - La ejecución no se reanuda nunca con una aprobación nueva. El camino es una ejecución de seguimiento.
3. **La pantalla ofrece la ejecución de seguimiento con los botones que ya tiene** (`OrchestratorDocumentViewModel`):
   - Dice en castellano por qué no se reanuda, con el aviso de ahora.
   - Pone el plan de la ejecución en el lienzo sin aprobar, aunque ya estuviera aprobado en la sesión. Así, junto a
     «Aprobar plan» se ve el aviso con el que se ejecutaría.
   - Aprobarlo otra vez y pulsar «Ejecutar» empieza una ejecución de seguimiento (`RunRequest.Follows`) con un id nuevo,
     y `StartAsync` vuelve a comprobar el aviso aprobado. «Rechazar» la descarta y quita el mensaje.
   - Es de seguimiento solo mientras el plan del lienzo sea el de esa ejecución. Un plan editado, otra propuesta o
     «Editar el plan» dan una ejecución propia.

   No hacen falta botones nuevos ni cambios en la vista.
4. **Límites.**
   - La ejecución de seguimiento repite el plan entero y parte, como cualquier otra, de la última integración de la que
     sigue, o de HEAD si no hubo ninguna.
   - La petición original no se recupera: «Ejecutar» pide la petición escrita, como siempre.
   - La ejecución rechazada sigue en la lista como interrumpida, con «Reanudar», que se vuelve a rechazar mientras sus
     escritores no sean los aprobados. «Rechazar» no la cierra en el registro.
   - Si la sonda cambia con la app abierta, el aviso del lienzo no lo ve hasta «Volver a cargar»; `StartAsync` sí, y no
     arranca.
   - Un proceso concurrente del mismo usuario con integridad Medium puede reescribir el registro y su ancla (punto 2 de
     ADR-0018).

## Adenda · 2026-11-04 · el registro se verifica a medida que crece

**Problema.** Cada carga del registro (`DecisionRecorder.LoadAsync`) lo leía entero y recalculaba toda la cadena dos veces.
La memoria carga el registro en cada `RecordAsync`, en cada revisión y en cada búsqueda (`RecallAsync`), y además
reconstruía su libro (`MemoryLedger`) leyendo el JSON de todas las entradas. Grabar memoria era cuadrático: 2 000 entradas
tardaron 144 s, y las 10 000 se pararon a los 14 minutos. También ponía en riesgo F12-5, porque el índice semántico
recupera toda la memoria antes de cada búsqueda.

**Decisión: verificación incremental, con una caché por proceso y por proyecto.**

1. **El almacén lee la cola** (`IDecisionLogTailReader`, en `SqliteDecisionLogStore`). En una sola transacción de lectura
   devuelve tres cosas:
   - el sello del almacén: esta apertura del proyecto, la versión del esquema de la base y la fecha de creación de su
     fichero;
   - si las entradas hasta lo verificado siguen ahí (cuántas son y el hash de la última);
   - y las entradas posteriores.
2. **El escritor del registro verifica solo lo nuevo.** El registrador guarda el último registro verificado del proyecto
   con su sello. Una carga verifica únicamente las entradas nuevas, encadenadas a la cabeza que ya verificó
   (`DecisionLog.Extend`), y compara el ancla como siempre, sin recalcular la cadena (`DecisionLog.VerifyHead`). El ancla se
   comprueba en cada carga.
3. **Si algo no cuadra, se verifica entero, como antes:** el sello es otro, faltan entradas, la última verificada tiene
   otro hash, o los triggers que hacen append-only el registro y, desde el 2026-11-05, la memoria no están exactamente
   como los crea el esquema (falta uno o tiene otro cuerpo). En ese último caso, cada carga verifica el registro entero
   hasta que vuelvan.
4. **Los momentos críticos leen el registro entero.** El contrato lo dice en cada llamada:
   `IDecisionRecorder.LoadAsync(projectId, LogLoad.Full, ct)` lee y verifica todo desde la primera entrada, pase lo que
   pase antes. Son los sitios donde una decisión o un agente se apoyan en el registro, y todo lo que ve un juez:
   - la entrega (`DeliveryService.CheckAsync`);
   - el dosier del juez (`JudgeDossierBuilder`) y la revisión independiente (`IndependentReview`);
   - el arranque de una ejecución y sus reanudaciones (`Orchestrator.StartAsync`, `ResumeAsync` y la reanudación por
     consentimiento);
   - la planificación (`Planner`);
   - en un debate, el dosier de su juez (`DebateDossierBuilder`), su veredicto (`DebateJudgment`) y su cierre
     (`DebateClosed`, en `DebateRun`);
   - la auditoría del proyecto (`ProjectAuditService`) en cada paso: la propuesta, que decide quién puede juzgar, el
     arranque, el dosier de su juez y el cierre de una auditoría interrumpida;
   - la impugnación de un veredicto (`VerdictChallengeService`): prepararla, que decide quién puede juzgar la revisión,
     el `VerdictChallenged` que la ancla y la aceptación de su revisión, que la resuelve.

   El resto de cargas son incrementales (`LogLoad.Incremental`, la sobrecarga sin `LogLoad`): las de una ejecución o un
   debate en marcha, abrir y reanudar un debate, listar e inspeccionar ejecuciones y debates, grabar y buscar memoria, el
   auditor del orquestador (que no es un juez: no da veredictos) y el estado de una impugnación que enseñan las pantallas.
5. **Una carga incremental verifica entero cada cierto tiempo.** Lo hace si la última verificación entera fue hace 5
   minutos o más (`DecisionRecorder.IncrementalAgeBeforeFullVerification`, medido con el `TimeProvider`), y además cada 256
   cargas desde la cola (`IncrementalLoadsBeforeFullVerification`). Al volver a abrir el proyecto, siempre.
6. **Al cerrar un proyecto, el registrador olvida lo que verificó de él** (`DecisionRecorder.Forget`, que el contenedor
   engancha a `ProjectDatabases.Closed`). La primera entrada tras volver a abrirlo se apoya en el registro leído entero otra
   vez, y la caché no crece con los proyectos que se abrieron y se cerraron.
7. **La memoria amplía su libro** con lo que creció el registro (`MemoryLedger.Extend`). El libro no cambia nunca: ampliarlo
   da otro, así que las búsquedas simultáneas lo comparten.

**Qué se detecta y cuándo.**
- **En la carga siguiente**, como antes:
  - un cambio con SQL, aunque se vuelva a crear el trigger que hubo que quitar, mientras la versión del esquema quede como la
    dejó ese cambio (quitar y crear un trigger la sube);
  - un trigger de `decision_log` quitado o con otro cuerpo: mientras siga así ninguna carga se fía de la cola, así que un
    cambio posterior, que ya no toca el esquema, también se ve;
  - lo añadido después por otro escritor, que llega por la cola y lo detecta el ancla, y lo cortado del final;
  - abrir otra vez el proyecto, que da otro sello.
- **En la siguiente verificación entera**, un cambio en su sitio que deja el sello igual:
  - un cambio con SQL tras el que se vuelve a poner la versión del esquema (`PRAGMA schema_version = N`);
  - el fichero sustituido por una copia preparada que conserva su fecha de creación (a mano, o por el *tunneling* de NTFS
    si se sustituye en pocos segundos);
  - unos bytes escritos en su sitio, con la interfaz de blobs de SQLite o en el propio fichero;
  - una fila sustituida por su `rowid` con `INSERT OR REPLACE` desde una conexión sin `recursive_triggers` (adenda del
    2026-11-05, punto 5 de su revisión).

  Esa verificación llega en el siguiente momento crítico, en la primera carga pasados 5 minutos desde la última verificación
  entera, tras 256 cargas desde la cola o al volver a abrir el proyecto: lo que ocurra antes.
- **Mientras tanto nadie en este proceso recibe las entradas cambiadas.** Una carga incremental sirve desde memoria lo que
  ya verificó, y del almacén solo toma lo añadido después, verificado encadenado a esa cabeza y contra el ancla. Lo que se
  escribe en ese tiempo se encadena a la cabeza verdadera, y la verificación entera encuentra la entrada cambiada.
- Lo prueban:
  - `IncrementalLogVerificationTests`, sin reabrir el proyecto, con otra conexión y con otro proceso (python y su SQLite): un
    trigger quitado y después un cambio, un trigger con otro cuerpo, bytes escritos con la interfaz de blobs (aparecen
    dentro del límite de cargas), y la versión del esquema falsificada o el fichero sustituido, que se encuentran en el
    siguiente momento crítico y a los 5 minutos de reloj falso (a los 4:59, todavía no);
  - `CriticalMomentsTests`: esos dos cambios, sobre los almacenes y el ancla reales, se encuentran en la entrega, en el
    dosier del juez, al arrancar una ejecución y al planificar;
  - `CriticalLogLoadsTests`, `JudgeDossierBuilderTests`, `IndependentReviewTests`, `ResumeAfterRestartTests`,
    `DebateDossierBuilderTests`, `DebateJudgmentTests`, `DebateCoordinatorTests`, `ProjectAuditServiceTests` y
    `VerdictChallengeServiceTests`: cada momento crítico pide la lectura entera, y listar e inspeccionar ejecuciones, una
    ejecución o un debate en marcha y el estado de una impugnación, la incremental;
  - `PersistenceRegistrationTests`: un proyecto cerrado se olvida;
  - `IncrementalMemoryLedgerTests`: el libro ampliado es, en cada paso, el que se lee de cero.

**Revisión de seguridad del 2026-11-04.** La primera versión de esta adenda decía que un cambio con SQL se veía siempre en la
carga siguiente, porque quitar un trigger cambia la versión del esquema aunque luego se vuelva a crear. No es así. Quien
puede escribir en la base puede volver a poner esa versión con `PRAGMA schema_version`, o sustituir el fichero por una copia
preparada que conserva su fecha de creación. En los dos casos el sello no cambia, y solo una verificación entera lo ve. La
revisión lo comprobó con un programa propio: antes de los puntos 4 a 6, solo lo encontraban la verificación entera de cada
256 cargas o la reapertura del proyecto. Un sello que nadie pudiera falsificar exigiría un secreto fuera del alcance de ese
proceso, y eso es lo que el punto 2 de ADR-0018 deja fuera.

**Cifras** (Release, esta máquina):

| | Antes | Después |
|---|---|---|
| Grabar 2 000 entradas | 140,9 s, de 54 a 98 ms por entrada | 94,3 s, plano en unos 47 ms |
| Grabar 10 000 entradas | Parado a los 14 min | 495 s, plano en unos 50 ms |
| Cargar el registro con 2 000 entradas | Unos 45 ms | 4 ms |
| Cargar el registro con 10 000 entradas | Un cuarto de segundo | 7,6 ms |
| Leerlo entero en un momento crítico, con 2 000 entradas | Unos 45 ms (todas las cargas) | 20,9 ms |

Lo que queda por entrada son las escrituras durables: el ancla con `flushToDisk` al anunciar y al asentar, y los commits
de SQLite. No se tocan. La lectura entera se paga en los momentos críticos, que son pocos por ejecución, y como mucho una
vez cada 5 minutos en las demás cargas de un proyecto con actividad. Con 10 000 entradas no se ha vuelto a medir: antes de
esta adenda, cada carga, entera, costaba un cuarto de segundo, y la lectura entera de ahora recalcula la cadena una vez en
lugar de dos.

**Residuales.**
- Un cambio en su sitio que deja el sello igual, con el proyecto abierto, ya no se ve en la carga siguiente. Es el caso del
  SQL con la versión del esquema puesta como estaba, del fichero sustituido por una copia que conserva su fecha de creación,
  de los bytes escritos con la interfaz de blobs de SQLite o en el fichero, o de una fila sustituida por su `rowid` con
  `INSERT OR REPLACE` desde una conexión sin `recursive_triggers`. Se ve en el siguiente momento crítico, a más
  tardar en la primera carga pasados 5 minutos o 256 cargas desde la última verificación entera, o al volver a abrir el
  proyecto. Mientras tanto nadie en este proceso recibe las entradas cambiadas: se sirve desde memoria lo verificado, así
  que lo que se retrasa es el aviso, no lo que se lee.
- Es el punto 2 de ADR-0018: un proceso del mismo usuario con integridad Medium puede reescribir el registro y también su
  ancla. Lo que esta adenda cambia es cuándo se ve, nunca si se puede hacer.
- Una ejecución o un debate en marcha, abrir y reanudar un debate y el auditor del orquestador cargan de forma
  incremental: trabajan sobre lo verificado y ven el aviso en la siguiente verificación entera. Lo que decide sobre ellos
  (la revisión, el veredicto y el cierre de un debate, la entrega) vuelve a leer el registro entero.
- Un almacén sin lectura de la cola (las pruebas en memoria) se sigue leyendo entero en cada carga.

## Adenda · 2026-11-05 · la memoria de confianza se sirve desde la memoria del proceso (F12-5)

**Problema.** El índice semántico recupera toda la memoria anclada antes de cada búsqueda (`RecallAsync` sin texto). Cada
recuperación leía las 10 000 filas de `memory_entries`, recalculaba el SHA-256 de cada una contra su anuncio y volvía a
construir la instantánea: p50 120,2 · p95 179,7 ms (en reposo) en la medición de F12-5 de principio a fin, en todas
las búsquedas. El objetivo de F12-5 es un p95 de la búsqueda por debajo de 200 ms.

**Decisión.** Lo que la memoria verificó se sirve desde la memoria del proceso mientras el registro solo crece:

1. **Una instantánea por proyecto, inmutable, que se sustituye entera** (`ProjectMemory`, sin locks): las filas que
   cuadran con su anuncio, en el orden del almacén, con la confianza y la sustitución que da el libro (`MemoryLedger`).
2. **Se amplía con lo nuevo.** Si el registro creció con algo que el libro lee, se leen y comprueban solo las filas de lo
   que anunció después (`IMemoryStore.FindAsync`) y las de lo anunciado cuya fila aún no estaba. La confianza y la
   sustitución de todas las entradas se vuelven a calcular con el libro nuevo (una revisión, la confianza de la bitácora o
   un historial aceptado pueden cambiar cualquiera). Si lo que creció no toca la memoria (las entradas de una ejecución),
   el libro es el mismo (`MemoryLedger.Extend` lo devuelve tal cual) y la instantánea también.
3. **Se rehace entera en cada verificación entera del registro** (`DecisionLogState.WholeVerification`: un valor nuevo cada
   vez que el registrador lee y verifica el registro entero, que no se repite tras cerrar y abrir el proyecto). Es decir,
   en el siguiente momento crítico, a los 5 minutos o 256 cargas, cuando el sello del almacén cambia y al volver a abrir el
   proyecto: todas las filas se leen y comprueban otra vez, como antes en cada recuperación.
4. **Los triggers de `memory_entries` cuentan como los del registro** en la lectura de la cola: con uno quitado o
   cambiado, cada carga verifica el registro entero, así que cada recuperación rehace la instantánea.

**Por qué la seguridad es la misma.** Lo que se sirve está verificado contra el registro: es la fila que cuadró con su
anuncio cuando se comprobó, y nunca se vuelve a tomar del almacén sin comprobarla. Una fila cambiada con SQL obliga a
quitar un trigger de la memoria (cambia el esquema y deja de estar el trigger: la carga siguiente es entera y la
recuperación siguiente la encuentra). Una fila cambiada sin que el sello lo vea (la versión del esquema puesta como estaba,
el fichero sustituido o bytes escritos en su sitio) se encuentra en la siguiente verificación entera, como el registro, y
mientras tanto se sirve la verificada. Lo prueban `MemorySnapshotTests` (sin lecturas cuando nada cambia, solo la fila
nueva tras una escritura, la fila que llega después de su anuncio, el cambio que el sello no ve en el siguiente momento
crítico y a los 5 minutos, y el trigger quitado) e `IncrementalLogVerificationTests` (el trigger de la memoria y el valor de
la verificación entera).

**Cifras** (Release, esta máquina, sobre el proyecto sembrado de F12-5 con 10 000 entradas; orientativas, con otro agente
trabajando a la vez):

| | Antes | Después |
|---|---|---|
| Recuperar lo anclado antes de cada búsqueda | p50 120,2 · p95 179,7 ms (en reposo) | p50 8,0 · p95 23,3 ms, con otro agente midiendo a la vez; 1 de 300 por encima de 50 ms (la verificación entera de cada 256 cargas) |

**Residuales.**
- Una fila plantada en el almacén sin anuncio, o cambiada sin que el sello lo vea (la versión del esquema puesta como
  estaba, el fichero sustituido, bytes escritos en su sitio o una fila sustituida por su `rowid` con `INSERT OR REPLACE`
  sin `recursive_triggers`), ya no se avisa en la recuperación siguiente sino en la siguiente verificación entera del
  registro (como mucho 5 minutos o 256 cargas, o en el siguiente momento crítico). Nunca se sirve.
- ~~`ProjectMemory` guarda la instantánea y el libro de los proyectos que se cerraron~~: resuelto en la revisión de abajo
  (`IProjectClosedSignal`).

**Revisión de las cachés (2026-11-05).** La revisión dio el visto bueno a lo esencial (nunca se sirve contenido sin
verificar; las revisiones, las retiradas, `JournalTrustChanged` y el historial aceptado se reflejan) y pidió dos arreglos de
gravedad media y cuatro menores. Se hicieron así:

1. **Una carrera lanzaba `NullReferenceException`.** Una recuperación que había leído el registro se retrasaba; mientras,
   se grababa una entrada y otra recuperación publicaba una instantánea más nueva. La primera ampliaba esa instantánea con
   su libro viejo, en el que la entrada nueva no tenía anuncio. El efecto era un `LaunchFailed` en el motor y que fallara el
   refresco del índice. `_ledgers` tenía la misma regresión: no lanzaba, pero un libro viejo sustituía al nuevo y obligaba a
   leer el registro entero otra vez.
   - **Ahora hay un único lector por proyecto, una cola sin locks** (`ProjectMemory.ProjectState`). Lee el registro, amplía
     o rehace el libro y la instantánea, y publica el resultado entero. Ese resultado lleva cuántas entradas del registro
     cubre y nunca es más viejo que el anterior. Las recuperaciones y las escrituras toman lo publicado, que no cambia.
     Las peticiones que esperan a la vez se atienden con una sola lectura del registro.
   - Una alerta de integridad ya no espera a que acabe una escritura de la memoria. Si hay una en curso, la da la
     recuperación siguiente, como cuando el proyecto está ocupado: así una recuperación nunca espera.
   - Lo prueba `MemorySnapshotRaceTests`, que reproduce la carrera: antes del arreglo fallaba con esa excepción.
2. **Los proyectos cerrados se quedaban en memoria.** `Application` gana `IProjectClosedSignal`, que `ProjectDatabases`
   implementa y la persistencia registra. El módulo de memoria engancha `ProjectMemory.Forget` al construirla.
3. **Una consulta por categoría o tema filtraba con las filas de la base.** Ahora filtra sobre la entrada verificada; el
   almacén solo ordena por el texto. Una fila alterada ya no hace servir una entrada de otra categoría, ni esconde la
   verificada.
4. **Una entrada anunciada sin fila se buscaba por id en cada recuperación.** Ahora se recuerda como ausente hasta la
   siguiente verificación entera y se avisa una vez (`memory.entry-missing-row`). La busca otra vez la siguiente
   recuperación si este proceso guarda su fila entretanto, al derivarla de nuevo o porque se leyó entre el anuncio y la fila.
5. **Un tercer canal que el sello no ve.** Una conexión sin `recursive_triggers` (la de otro programa: la aplicación los
   activa) puede usar `INSERT OR REPLACE` con el `rowid` de una fila. La fila con la que choca se borra sin pasar por su
   trigger de borrado, y una clave que ninguna fila tiene (un id, una secuencia) pasa el trigger de inserción. Los triggers
   siguen intactos y la versión del esquema no cambia. Funciona igual en `memory_entries` y en `decision_log`. Lo encuentra
   la siguiente verificación entera, como los otros dos canales, y mientras tanto se sirve lo verificado. Lo prueban
   `MemorySnapshotStateTests` e `IncrementalLogVerificationTests`.
   - **Defensa barata que se propone**, no aplicada: que la condición de los triggers `*_no_replace` cubra también el
     `rowid`, con `WHEN EXISTS (… WHERE id = NEW.id) OR EXISTS (… WHERE rowid = NEW.rowid)`.
   - Una prueba aparte sobre una base de SQLite en memoria lo comprobó. Con la condición actual, el `REPLACE` por `rowid`
     cambia la fila. Con la nueva, se rechaza. Las inserciones normales siguen pasando, con `rowid` automático o con uno
     nuevo explícito, y un id repetido sigue rechazado.
   - Aplicarla exige una migración que vuelva a crear esos triggers en las tablas append-only (el registro, la memoria y
     las demás que siguen el mismo patrón de `0001` y `0002`) y poner al día `AppendOnlyTriggers`.
6. **Pruebas que faltaban.** `MemorySnapshotStateTests` cubre:
   - las revisiones sobre una instantánea ya cargada;
   - la confianza de la bitácora;
   - recuperaciones y escrituras a la vez: nunca fallan, nunca ven menos que antes y acaban con todas las entradas;
   - el filtro sobre lo verificado y la fila ausente;
   - el olvido de un proyecto.

   `ProjectMemoryCompositionTests` prueba el aviso de cierre.

## Adenda · 2026-11-12 · el canal del `rowid` queda cerrado (P45)

La defensa del punto 5 de la revisión del 2026-11-05 se aplica con la migración `0007_replace_by_rowid.sql`.

- **Qué cambia.** Los triggers `*_no_replace` de once tablas se vuelven a crear (`DROP TRIGGER` y `CREATE TRIGGER`) con
  `WHEN <la condición de siempre> OR EXISTS (SELECT 1 FROM <tabla> WHERE rowid = NEW.rowid)`:
  - `decision_log`, `evidence`, `verdicts`, `requirement_sets` (`0001`);
  - `plans`, `orchestration_runs` (`0002`);
  - `memory_entries` (`0004`);
  - `debate_sessions`, `project_audits` (`0005`);
  - `milestones`, `backlog_items` (`0006`).

  No se toca ninguna tabla ni ninguna fila. `AppendOnlyTriggers` (el registro y la memoria) lleva el texto nuevo, así que
  una base que conserve el trigger de `0001` o `0004` ya no cuenta como genuina y las lecturas por la cola piden la
  verificación entera hasta que migre; `Apply` la migra al abrir el proyecto.
- **Qué no cambia.** `debate_contributions` y `skill_activations` conservan su trigger: su `id` es un
  `INTEGER PRIMARY KEY`, es decir, el propio `rowid`, y la condición sobre `NEW.id` ya lo cubre (con `id` nulo se asigna un
  `rowid` nuevo y no choca con nada).
- **Qué se probó** (`ReplaceByRowidTests`, 38 casos):
  - con las migraciones hasta `0006`, el `REPLACE` por `rowid` cambia la fila en cada una de las once tablas;
  - con `0007`, en una base migrada y en una nueva, se rechaza con `<tabla> is append-only` y la fila queda igual;
  - una base en `0006` pasa a `0007` sin perder filas ni triggers y `Apply` es idempotente;
  - siguen pasando anexar con `rowid` automático o con uno nuevo explícito, un fallo seguido de otra inserción legítima, las
    versiones del backlog y las cabeceras nuevas de debates y de hitos.
  - `MemorySnapshotStateTests` e `IncrementalLogVerificationTests` ya no prueban que el canal funcione: prueban que se
    rechaza y que el registro y la memoria siguen válidos.
- **Qué queda fuera.**
  - Una conexión que borre los triggers o escriba en el fichero sigue siendo lo que vigilan `AppendOnlyTriggers` y las
    verificaciones enteras; esta adenda no lo cambia.
  - `NEW.rowid` en un `BEFORE INSERT` sin `rowid` explícito no está definido por SQLite. La prueba de las inserciones
    automáticas muestra que hoy no choca con ninguna fila, pero es una propiedad del motor, no una garantía documentada.
