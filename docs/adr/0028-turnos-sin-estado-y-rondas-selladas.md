# ADR-0028 · Turnos sin estado y rondas simultáneas y selladas; los límites los mide el sistema y lo que falta se estima

- **Estado:** aceptada · 2026-10-20. Recoge D-04, D-08 a D-10 y D-16 a D-22 de
  [docs/15](../15-fase-5-debate-arena-y-juez.md#44-f5-2--ronda-1-en-paralelo-y-sin-ver-nada-de-los-demás), las acciones de
  la sala de D-20, D-26 y D-27 y la comprobación del repositorio de su §7. Lo implementa el paquete WP1 en
  `src/AgentStudio.Debate/Arena/` (`DebateCoordinator`, `DebateRun`, `DebateLog`, `DebateModes`, `ParticipantPrompt`,
  `DebateMaterial`, `ContributionSchema`, `ContributionParser`, `CitationResolver`, `DebateFolders`, `DebateTurnRecorder` y
  `DebateRepositoryGuard`) sobre el dominio de P0 (`DebateSession`, `DebateStateReplayer`, `DebateBudget`) y los turnos sin
  estado de WP4. `AddAgentStudioDebateArena` registra `IDebateCoordinator`.
- **Contexto:**
  - La independencia de la ronda 1 (F5-2) es una garantía de contenido: ningún participante puede saber nada de lo que
    responden los demás en su ronda, y tiene que poder comprobarse en lo que guarda el registro.
  - Un participante que reanuda su sesión sabe lo que haya en ella, que no es evidencia ni se puede comprobar. Los dos CLIs
    tienen turnos que no se guardan: `--no-session-persistence` en Claude y `--ephemeral` en Codex (WP4, medido).
  - Las cifras de consumo de la Fase 4 llevan su origen, y no siempre llegan: un turno cancelado, un CLI que no las da o un
    proceso que termina antes de registrarlas.
  - Ningún payload lleva texto de un agente ([ADR-0026](0026-debate-como-unidad-del-registro.md)), y el juicio (WP2,
    [ADR-0029](0029-veredicto-de-un-debate.md)) solo admite las evidencias que el registro anunció para el debate.
  - El motor compara el repositorio del usuario alrededor de cada paso, pero el módulo de debates no puede referenciar el de
    la orquestación ([ADR-0002](0002-capas-y-puertos.md)).
- **Decisión:**
  1. **Cada turno es una sesión nueva que no se guarda.** `PersistSession = false`, sin `ResumeSessionId` y con el id del
     debate; solo lectura (`Read`, `Grep` y `Glob`; sin escritura, shell ni red), sin MCP ni skills; trabaja en la copia del
     commit del objeto (o en su carpeta, si el debate no trata de código) y lee además su carpeta propia,
     `turns\r<ronda>-<agente>-a<intento>`, con un tope de 12 turnos del CLI y 5 minutos por turno. Lo que sabe está entero
     en su prompt: se captura como evidencia y lo ancla `AgentRunStarted` v2 (propósito `DebateParticipant`, tarea
     `r<ronda>-<agente>`). La copia vive, por defecto junto a las worktrees, en `…\debates\<repositorio>\<debate>`, y se borra
     al cerrar el debate.
  2. **El material de un turno es dato, nunca instrucción.** `debate.json` (`agent-studio/debate-material/1`) lleva la
     pregunta, los requisitos, el objeto, los tipos de contribución que admite el turno, las contribuciones publicadas y las
     evidencias que son material, cada una en su fichero. En el prompt van como JSON escapado con `PromptData`, cada
     contribución en su `<debate_contribution label= author= round= kind= sha256=>` con el texto que ancla su hash.
  3. **Rondas simultáneas y selladas.** Los turnos de una ronda se lanzan a la vez (el límite de concurrencia del proveedor
     sigue mandando). Ninguna respuesta se captura ni se anuncia hasta que terminan todos: `AgentRunFinished` va sin
     `ReportEvidenceId`. Al cerrar la ronda, en el orden de la cabecera, cada contribución se resuelve (sus citas), se
     redacta, se captura (`AgentStatement`/`ReportedByAgent`, en JSON canónico), se anuncia con sus citas y solo entonces
     queda `DebateContributionRecorded`, con la fecha de su respuesta, y se entrega al dominio. Lo que un turno dejó
     registrado solo es material de otros turnos cuando lo cita una contribución publicada: la evidencia de una respuesta
     cuya publicación cortó una parada sigue sellada.
  4. **La contribución es estructurada y sus problemas no llevan texto.** El esquema es portable y de 16 KB como mucho, con
     los tipos, las etiquetas (`C<n>` y `ninguna`) y las citas (`CITA-1` a `CITA-8`) de cada turno. El análisis es estricto y
     sus problemas solo nombran miembros del esquema, rutas JSON, valores de conjuntos cerrados y números, nunca lo que
     escribió el participante. Una respuesta que no vale tiene una corrección (`<contribution_problems>`); un turno que falla,
     un reintento; si falla otra vez, el debate espera a una persona («Reintentar el turno», «Pedir juicio con lo que hay» o
     «Abortar») y nunca sigue con una propuesta de menos. Con el límite de uso del proveedor espera solo hasta 15 minutos si
     el CLI dijo cuándo se reinicia; si no, espera a una persona.
  5. **Las citas las resuelve el sistema.** Solo se leen las líneas exactas (80 como mucho) de un fichero regular UTF-8 de
     hasta 1 MiB, dentro de la copia y sin pasar por ningún enlace. Una ruta o unas líneas mal formadas son un problema que se
     corrige; una cita que no se resuelve (no existe, es un enlace, es demasiado grande, no es texto, sus líneas no están) no
     hace fallar el turno: queda como hecho para el juez (`CITA-n: <problema>`, de un conjunto cerrado). La que se resuelve se
     captura como `Document`/`CapturedBySystem` con `SubjectCommit` = el commit de la copia.
  6. **Ningún payload lleva texto de un agente.** `Reason`, `Message` y los resúmenes de las entradas los escribe el sistema.
     El cierre lleva la regla que combinó los veredictos, cuántos valían, la decisión y el resultado, no el resumen del juez.
     La única cadena que procede de un participante es la ruta de una cita resuelta en el título de su evidencia, y es la de
     un fichero que existe en el commit. Una prueba recorre todas las cadenas de todos los payloads y resúmenes del registro.
  7. **Los límites los mide el sistema.**
     - **Rondas**, al cerrar cada una (`AdvanceRound` del dominio).
     - **Tiempo**, con el reloj del sistema desde `DebateOpened` (un `CancellationTokenSource` sobre el `TimeProvider`): el
       plazo cancela los turnos en curso, que no publican, y se publica lo que ya había respondido, con la fecha de su
       respuesta. Si se agota sin ninguna contribución publicada, el debate se aborta con ese motivo: el dominio no admite
       juzgar un debate vacío y ningún turno puede responder ya.
     - **Tokens**, desde el registro (`DebateBudget` sobre los `AgentRunFinished` v2: la entrada con la caché más la salida).
       Lo que falta se estima a 4 caracteres por token del prompt y de la respuesta y cuenta igual; antes de lanzar una ronda
       se proyecta la media por turno de la anterior por el número de participantes, y el motivo dice «disparado por una
       estimación» cuando entra alguna.
     - **Ejecuciones** (`MaxAgentRuns`, por defecto participantes × rondas + 2), reservadas antes de cada lanzamiento,
       reintentos incluidos: una ronda que no cabe no se lanza y va a juicio por `TurnMissing`.
     - `DebateRoundClosed` lleva el motivo, los tokens con su origen y los segundos enteros transcurridos, y el dominio se
       alimenta exactamente como lo hará la reproducción: la ronda cierra en el inicio más esos segundos.
  8. **Reanudar es reproducir.** El estado sale del registro verificado (`DebateStateReplayer`); la cabecera tiene que dar el
     hash que ancló `DebateOpened`, y reanudar pide una aprobación atada a la cabeza del registro. Los lanzamientos que el
     registro muestra abiertos se cierran como `Interrupted` (no cuentan como reintento), las respuestas selladas que se
     perdieron se vuelven a pedir con un intento nuevo y nada anunciado se repite.
  9. **Abrir no deja nada a medias.** `PrepareAsync` no escribe. `OpenAsync` entra en la actividad del proyecto antes de
     escribir (la única del proceso, la de `AddAgentStudioOrchestration`; la sala nunca registra otra), vuelve a planificar y
     compara el hash, verifica el registro, pide la aprobación atada al hash de la propuesta, captura la pregunta y guarda la
     cabecera; después la lee de vuelta del registro de debates y comprueba sobre ella, con el SHA-256 de la pregunta
     capturada, el hash que aprobaste (`DebateHeaderHash.Proposal`) antes de `DebateOpened`, que ancla esa cabecera. Una vez
     capturada, toda evidencia del debate se anuncia aunque el debate se detenga (su `EvidenceRegistered` o el
     `AgentRunStarted` de su prompt).
  10. **El repositorio del usuario se compara antes y después de cada ronda y del juicio**, con la misma comparación que el
      motor, repetida en el módulo. Una diferencia deja `AuditAlertRaised` (`user-repository.changed`) y aborta el debate sin
      publicar la ronda.
  11. **Las acciones de la sala.** «Pedir juicio ahora» cancela lo que está en curso y publica lo que respondió (sin nada
      publicado se rechaza); «Decidir yo» pasa por la puerta de aprobación (`debate.human-resolution`, atada a los veredictos
      y a la decisión) y cierra con la regla `user`; «Añadir evidencia» guarda una nota `HumanNote`/`ProvidedByHuman`;
      «Ejecutar pruebas» lanza el perfil de confianza sobre la copia del commit, solo con sus pasos aislados (el perfil lo da
      la composición con `IDebateTestProfiles`; sin él, la sala dice que no se puede); y «Abortar».
- **Alternativas:**
  - **Reanudar la sesión de cada participante entre rondas:** menos tokens, pero lo que sabe dejaría de estar entero en su
    prompt y la independencia no se podría comprobar.
  - **Turnos de uno en uno:** el segundo vería al primero, o habría que sellarlos igual y el debate tardaría el doble.
  - **Problemas que citan la respuesta:** serían más fáciles de corregir, pero devolverían al prompt, que es evidencia del
    debate, un texto que el sistema no validó.
  - **Contar los tokens en memoria:** se pierden al reiniciar y la reproducción no daría lo mismo.
  - **Publicar en el orden de llegada:** dependería de una carrera; el de la cabecera es reproducible.
  - **Esperar a una persona cuando el plazo se agota sin nada publicado:** ya no queda tiempo para ningún turno.
- **Consecuencias:**
  - Cada turno relee su material: gasta más que reanudar, y la estimación de «Abrir debate» lo refleja (160 000 tokens por
    turno de Claude y 80 000 por uno de Codex, como máximo).
  - Una propiedad con 200 debates por semilla (modos, participantes de los dos CLIs, límites, fallos, veredictos, una persona
    que reintenta, pide juicio, decide o aborta, y procesos que terminan y se reanudan) comprueba que la reproducción da el
    estado en vivo, que el presupuesto cuenta cada lanzamiento y que el dossier real del juicio no encuentra nada que
    señalar.
  - La decisión de una persona no se reproduce: `DebateClosedPayload` lleva la regla `user` y la propuesta elegida, pero no
    la decisión, y el historial la muestra sin ella.
  - `MaxAgentRuns` no tiene un `DebateLimitReached` propio: el corte va por `TurnMissing`, con el motivo en
    `DebateRoundClosed`.
  - Una parada brusca (el proceso muerto) entre capturar una evidencia y anunciarla deja una fila sin anunciar: el dossier
    del juicio la señala y el debate necesita a una persona. Una parada normal no la deja.
  - Deuda: la comparación del repositorio del usuario repite la del motor; los participantes con memoria `ReadSelected`
    todavía no la reciben; el gestor de la copia se llama `DebateFolders` porque `DebateSnapshot` ya es el contrato de la
    sala; los esquemas repiten ayudas de `VerdictSchema`; el error del CLI de un turno que falla se registra como en la
    Fase 3.
