# ADR-0067 · Reconciliación por sondeo condicional con un buzón de un consumidor por proyecto: se observa estado, no eventos, y la unión de commits es una proyección

- **Estado:** aceptada · 2026-11-13, con la opción recomendada de las decisiones 2 y 3 de [docs/21 §14](../21-fase-10-git-remoto.md#14-decisiones-que-necesitan-a-la-persona)
  (se redacta con la opción recomendada de cada una). Recoge D-02 y D-23 a D-30 de [docs/21](../21-fase-10-git-remoto.md) y
  las D-31 y D-46 a D-48 de [docs/16](../16-ampliacion-plataforma-de-proyectos.md). Ocupa, con
  [ADR-0065](0065-github-por-gh-sin-leer-el-token.md), el lugar del ADR-0042 que docs/16 reservó. **Amplía**
  [ADR-0039](0039-vinculos-de-trazabilidad-y-participacion.md) (la cadena y la participación) y aplica a lo remoto la regla
  de [ADR-0022](0022-memoria-append-only-anclada.md) (las entradas sin ejecución esperan al proyecto libre). WP3 implementa el
  buzón y el reconciliador; WP4, las proyecciones.
- **Contexto:**
  - F10-6 pide un sondeo condicional que reconcilie estado con un buzón por proyecto, sin duplicar nada ante eventos
    repetidos o desordenados, que respete los límites, y que no toque git ni el registro mientras una ejecución, un
    debate o una auditoría tienen el proyecto. F10-7, que la cadena una los commits originales con el resultante tras un
    squash, un rebase o un revert.
  - Una app de escritorio no tiene un punto público para webhooks (docs/16 D-47).
  - El motor de una ejecución ya es un actor de un solo hilo con buzón, y la preferencia del proyecto para las carreras es
    una cola o un canal de un solo consumidor antes que cerrojos nuevos.
  - `IRunActivity.IsBusy` es «mirar»: entre mirar y actuar puede empezar una ejecución. `IProjectActivity.TryEnter` es
    «entrar o no entrar».
- **Decisión:**
  1. **Se observa estado, no se aplican eventos.** Cada sondeo de un PR seguido da una **instantánea** (`RemoteSnapshot`):
     estado, commit de cabeza, rama base, commit y modo de la fusión, commits originales y resultantes, resumen de checks y
     de revisiones, número de force-push, commits que revierten y despliegue. Solo identificadores, números y enumerados.
  2. **Cada instantánea tiene una huella** (SHA-256 de su forma canónica). Dos con la misma huella son una: no se registra
     nada. Una con fecha anterior a la última registrada se descarta. Los cambios los calcula código puro
     (`RemoteReconciler`, en Domain), probado con secuencias repetidas y desordenadas.
  3. **Lo observado va al registro** como `RemoteStateObserved` (sin ejecución; la ejecución, en un campo), nunca
     reescribiendo una observación anterior. La última huella se lee del registro, no de una caché: borrar la caché no
     duplica nada.
  4. **Ningún texto del remoto entra en el registro.** Los títulos y los nombres de checks o de entornos viven en una
     caché local, para la pantalla.
  5. **Peticiones condicionales, por recurso, y cadencia fija.** `If-None-Match` con el `ETag` anterior **de ese mismo
     recurso** (el PR, los checks de su cabeza, sus revisiones, los commits de la rama base desde la fusión, los
     despliegues): un 304 del PR no dice nada de los demás, que cambian sin que cambie el suyo. La instantánea se compone
     con lo nuevo y lo último conocido de lo que dio 304; si todo dio 304, no hay entrada. Cada 5 minutos por defecto, solo lo seguido y abierto (lo fusionado, 30 días y cada hora), solo lecturas, solo con
     la app abierta.
  6. **Los límites se respetan:** con 429, o 403 con el límite a cero o `retry-after`, no sale nada hacia ese host hasta la
     hora que digan `retry-after` o `x-ratelimit-reset`; sin ellas, un minuto y espera progresiva. Nunca se insiste.
  7. **Con el permiso de la persona.** El sondeo periódico necesita un permiso por proyecto, apagado por defecto (`IRemoteSyncPolicy`);
     sin él, solo «Sincronizar ahora». Auto Doctor no usa la red.
  8. **Un buzón por proyecto con un solo consumidor** (`Channel` con un único lector): pone en serie observaciones y
     acciones. Y **una sola cola hacia `gh`** en toda la app: un proceso a la vez, que es lo que pide GitHub, y donde vive
     el estado de los límites. Sin cerrojos nuevos.
  9. **El proyecto se toma lo mínimo.** Leer por `gh` no toca git ni el registro, así que sigue con una ejecución en
     marcha, y **`gh` nunca corre con el proyecto tomado.** El buzón **entra** en el proyecto con
     `IProjectActivity.TryEnter(…, RemoteOperation)` (un valor nuevo al final de `ProjectActivityKind`) solo para dos
     cosas: el tramo de git de una publicación (inspección → `push` → `ls-remote`, **de una vez:** soltarlo en medio
     dejaría a un agente cambiar `.git/config` después de inspeccionada) y **cada anotación del registro,** una a una. El
     sondeo solo lo toma si hay una huella nueva que anotar. El buzón anota con `IDecisionRecorder` directamente, nunca por
     un servicio que espere detrás de `WorkWriteGate` (se esperaría a sí mismo).
  9 bis. **Un mensaje que encuentra el proyecto ocupado no detiene al consumidor:** se aparca y se reintenta con un
     temporizador de 500 ms (`IProjectActivity` no tiene eventos). Las observaciones aparcadas se funden por PR: tras una
     ejecución larga queda la última de cada uno. Lo hecho en GitHub que aún no se anotó se dice en la pantalla y sobrevive
     a un cierre.
  9 ter. **Quien llega durante una toma remota espera; no se rechaza.** Hoy quien encuentra el proyecto tomado se niega
     (`Orchestrator.Enter` lanza; el planificador de reanudaciones lo da por «no se pudo» y no reintenta; la pantalla
     olvida la aprobación del plan). Con una toma de segundos eso sería un fallo sin motivo. `ProjectActivityEntry.EnterAsync`
     (Application) espera, solo cuando lo que tiene el proyecto es `RemoteOperation`, hasta 180 segundos, y después llama a
     `TryEnter` como siempre; la usan el orquestador, el debate, la auditoría y las pruebas de cabecera. Agotado el tope,
     una `UserTextException` con `project.busy-remote`, ante la que la aprobación del plan no se olvida. Con un debate o
     una auditoría delante, nada cambia. Es la decisión 18 de docs/21.
  10. **Sin conexión, sin sesión o sin credenciales: «pendiente de sincronizar», con su motivo.** Lo pendiente vive en un
      fichero local, no en el registro: no ha pasado. Sin conexión, reintento con espera progresiva; sin sesión, se para
      hasta que la persona pulse. Antes de cada reintento la acción vuelve a pasar la compuerta entera (ADR-0068). Caduca
      a las 24 horas.
  11. **Cada acción remota es idempotente por estado:** antes de empujar se mira la rama; antes de abrir un PR se busca
      uno desde esa rama; tras un corte se consulta antes de repetir.
  12. **La unión de commits es una proyección** (`CommitSuccession`): la observación de una fusión guarda los commits
      originales del PR y los resultantes, con el modo. De un commit original se llega a lo que quedó, y de un commit del
      remoto, a sus originales y por ellos al intento, la tarea, el requisito y las evidencias. No se crean vínculos por
      commit, y nada se borra.
  13. **Lo deducido se enseña como deducido:** el modo de una fusión hecha fuera (por los padres del commit y la
      comparación con el penúltimo commit del PR) y una reversión (por la línea estándar `This reverts commit …`). Una
      reversión o un check que falla **no reabren un hito solos:** proponen reabrirlo, y lo decide la persona
      (`MilestoneReopened`, que ya existe).
  14. **«Desplegado»** solo existe si la plataforma informa de un despliegue con éxito de un commit resultante. Sin ese
      dato, el estado no aparece.
- **Alternativas descartadas:** webhooks con un relé (una pieza viva y otra cuenta, docs/16 D-47); aplicar los eventos de
  la plataforma (un evento repetido o desordenado duplicaría estado); un cerrojo por proyecto alrededor de git y del
  registro (la regla del proyecto es una cola antes que un cerrojo, y `TryEnter` ya existe); esperar con `IsBusy` y
  actuar después (mirar y luego actuar); `git fetch` para saber del remoto (escribe en el repositorio de la persona;
  ADR-0064); registrar lo pendiente en el registro (no ha pasado); vínculos `TraceLinkRecorded` por cada commit unido (la
  observación ya lo dice, y un vínculo aparte podría quedarse atrás).
- **Residuales aceptados:** el estado se conoce con el retraso del intervalo; el modo de fusión y la reversión son
  deducciones; un PR con más de 250 commits se une «truncado»; los ficheros locales del buzón los puede editar un programa
  del mismo usuario (por eso lo pendiente vuelve a pasar la compuerta y la caché se lee como no fiable).
- **Guardas contra respuestas viejas, por recurso:** `updated_at` solo es del PR. Los commits y la línea de tiempo solo
  crecen; el identificador del estado de un despliegue solo crece; y un cambio en el resumen de checks o de revisiones,
  que no traen marca fiable, solo se anota si se repite en dos vueltas seguidas. La prueba de propiedad es sobre el
  **estado final y su monotonía,** no sobre el conjunto de observaciones (que depende del orden).
- **La hora hasta la que GitHub pide esperar se guarda** en el fichero local y tiene un tope de dos horas.
- **Sin verificar al escribirlo:** que `gh api -i` deja leer la línea de estado de un 304, que para `gh` es un error (código
  de salida distinto de 0): de ello depende el ahorro del sondeo condicional, y es lo primero que mide WP2; que un 304 no cuenta contra el límite con una petición autenticada por `gh`; el código
  de salida de `gh api` con un 304; los eventos de force-push y la forma de los despliegues en la API; y que añadir un
  valor a `ProjectActivityKind` no rompe más `switch` que los de la bandeja y el bloqueo.

## Lo que fija el P0 (2026-11-13)

- **`ProjectActivityKind.RemoteOperation`** (al final del enum) y **`ProjectActivityEntry.EnterAsync(activity, proyecto, clase, tiempo, ct)`**: espera mientras lo que tiene el proyecto es una toma remota, hasta 180 s y sondeando cada 250 ms
  con un `TimeProvider` (las pruebas mueven el reloj, nadie espera el real); pasado el tope, `UserTextException` con `project.busy-remote`. Con una ejecución, una planificación, un debate o una auditoría delante responde nulo al momento y con
  los mismos textos y códigos de siempre. Nunca devuelve nulo mientras la toma remota sigue: espera o rechaza.
- **Los cuatro sitios** la usan: `Orchestrator` (planificar, empezar, reanudar y reanudar por consentimiento; `Track` sigue con `TryEnter` porque ya tiene el proyecto para una ejecución), `DebateCoordinator`, `ProjectAuditService` y
  `HeadTestsService` (con su texto `Panels_Tests_HeadBusyRemote`). `OrchestratorDocumentViewModel` no olvida la aprobación del plan ante ese código.
- **Lo que ve el resto:** `ProjectActivityWork` (la bandeja) y `ProjectsWorkInProgress` (el bloqueo) ignoran la toma remota; no hay claves `Background_Work_Activity_RemoteOperation` ni de bloqueo.
- **`DecisionKind`** crece en cuatro valores al final (`HostingPolicyGranted`, `HostingPolicyRevoked`, `HostingOperationPerformed`, `RemoteStateObserved`) y sus payloads (`RemoteSnapshotPayload` plano, solo primitivos), con `RunId = NoRun`.
- **`RemoteSnapshot.Fingerprint`** (SHA-256 de la forma canónica, sin `UpdatedAt`) está fijada con una prueba de huella dorada: si cambia, las observaciones ya anotadas parecerían nuevas. `RemoteReconciler` y `CommitSuccession` traen la
  lógica mínima; la completan WP3 y WP4.


## Lo que fija WP3 (2026-11-13)

- **El buzón** (`Orchestration/Publication/ProjectRemoteMailbox`) es un `Channel` con un único lector por proyecto. Mensajes: observar, ejecutar un flujo de la persona (`RemoteFlow`, reanudable) y anotar; el resto es fontanería (el tic del
  temporizador, cancelar, seguir). El consumidor **no duerme nunca**: la cortesía de un segundo tras soltar el proyecto es un «no antes de T» de la toma; el mensaje que lo encuentra cerrado se aparca y lo recoge el temporizador de 500 ms
  (`TimeProvider` inyectado). Las observaciones aparcadas se funden por PR (queda la de mayor orden de contenido), el orden de una misma clave (el PR, o `run:<id>` antes de tener PR) se conserva, caducan a las 24 h y se pueden cancelar.
- **La compuerta se pasa dos veces, con la misma función.** En el clic, fuera de la toma y en la tarea de quien llama, `AuthorizeUseAsync(…, mayAsk: true)` pide la aprobación atada (`Binding`: acción, repositorio, ejecución, commit, rama base, modo y,
  si la operación ya tiene PR, su número) y vuelve a comprobar el estado después de aprobar. En el consumidor, justo antes de ejecutar y antes de **cada reintento**, `mayAsk: false`: vale una autorización persistente o la aprobación que el clic dejó en el
  `ApprovalLease` de ese trabajo (solo en memoria; nunca va a un fichero y se olvida con el trabajo); si hiciera falta preguntar, **rechaza**. Un diálogo es una persona y el consumidor no espera a nadie.
  Las operaciones de «escribir en el PR» de una publicación (abrir, estado y comentario) comparten una aprobación sin número de PR, porque el PR aún no existe al pedirla; el resto lleva el número.
- **`gh` nunca con el proyecto tomado; git y el registro, solo con él.** El tramo A (identificar el remoto → inspección → `push` → `ls-remote` → su anotación) va con una sola toma, sin soltar y con `CancellationToken.None`: un `push` no se corta. Los
  tramos de `gh` van sin toma; las anotaciones de B a E se intentan al momento y, si el proyecto no está libre, se aparcan sin parar el flujo. Tras fusionar o cerrar, y tras cualquier respuesta que no confirme, se **observa** antes de anotar o de repetir.
- **La repetición del cambio de checks o de revisiones en dos vueltas** es del buzón (`FollowedPullRequest.PendingConfirmation`). Se decide antes de tomar el proyecto con lo ya registrado en la sesión y, la primera vez, dentro de la toma contra el registro.
  Una vuelta sin huella nueva no toma el proyecto.
- **El fichero local no crea autoridad.** Las ETag no se restauran (la primera vuelta pregunta sin condición); lo aparcado que vuelve del fichero **no se escribe**: se vuelve a preguntar a la plataforma y solo eso se anota; las acciones pendientes que
  no habían pasado se listan (caducan o se cancelan) y no se ejecutan solas tras un reinicio; el repositorio del proyecto sale de `IdentifyAsync` hecho con la toma en esta sesión, nunca del fichero, y solo se observan PR de ese repositorio.
- **Lo que no se guarda en el fichero de pendientes:** las anotaciones de operaciones ya hechas en GitHub y las autorizaciones aparcadas (`PendingRemoteWork` no lleva el resultado, la cuenta ni qué las amparó, y un fichero que pudiera
  escribir historia sería el fichero autorizando). Viven en memoria; si la app se cierra antes de anotarlas, la siguiente observación recoge el estado real del PR.
- **Residuales:** el sondeo no respeta `x-poll-interval` (el puerto no lo expone); la hora en que pasó cada cosa no se puede dar al anotar (el registrador pone «ahora»); entre leer la rama remota y borrarla hay una ventana mínima; el recuento de commits de la
  tarjeta de publicación (`SystemCommits`, `OwnUnpublishedCommits`) queda sin dar: ningún puerto de este paquete lee la historia.

### Ronda de arreglos tras la revisión (2026-11-13)

- **El permiso se pide al lanzar.** Toda observación que no nazca de un clic exige `PeriodicSyncAllowed` en el momento de lanzarse, también las que vuelven de `pending.json` y los reintentos; sin él no sale ninguna llamada a `gh`.
- **Revocaciones pendientes.** `RemoteSyncState.PendingRevocations` guarda los ids de las autorizaciones cuya retirada se pidió y no está anotada. Vale desde el instante en que se pide (la compuerta no cuenta esa autorización), se ve en `IRemoteSync.Pending` y sobrevive a un reinicio; viene de un fichero no fiable, y por eso es seguro: solo puede quitar autoridad. Se valida cada id y la lista se acota a 50. Una autorización dada y aparcada sigue sin valer y no se guarda.
- **Cierre ordenado.** Antes de parar, el buzón hace un último intento, sin la cortesía, de anotar lo aparcado (anotaciones y observaciones, no el trabajo de la persona). Residual si no puede o la app cae: la siguiente observación registra el hecho (fusionado, cerrado…) y quién lo hizo según la plataforma, pero no qué aprobación lo amparó.
- **Escrituras dudosas.** Un «sin conexión», un «prohibido» o una respuesta ilegible no son un fallo seguro: se pregunta el estado antes (el PR existe, la rama ya no está) y se anota lo que de verdad pasó. Una lectura imposible tras ese intento deja el trabajo pendiente de sincronizar, y la siguiente vuelta lee antes de repetir.
- **Fusionar.** Se relee el PR antes de pedir la aprobación, después de aprobar y otra vez dentro del flujo; si no se pudo leer, la cabeza no es el commit juzgado o la base no es la de la aprobación, no se fusiona. `RemoteChangeKind.BaseChanged` lo da el reconciliador.
- **Pendientes visibles.** Lo aparcado de dar y retirar autorizaciones sale en `Pending` con el código `hosting.pending.annotation`.
