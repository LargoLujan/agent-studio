# ADR-0048 · El constructor visual: un lienzo con las capas «Equipo» y «Plan», la tira del equipo, el nodo del repositorio y la ficha del proyecto; la capa Equipo es la plantilla, y las carpetas y las instrucciones de cada agente pasan a mandar

- **Estado:** aceptada · 2026-11-09. El P0 de la Fase 13 dejó el esqueleto (2026-11-06) y lo completa **WP6** (lienzo, capas,
  tira, inspector y preparación). Recoge D-07 a D-17 y D-42 a D-46 de [docs/17 §4.1 a §4.3 y §4.10](../17-constructor-visual-de-agentes.md#41-equipo-y-plan-en-un-solo-lienzo)
  y la decisión 2 (a) que aceptaste el 2026-11-06. **Amplía** [ADR-0015](0015-lienzo-nodify.md).
- **Contexto:**
  - P4 pide montar orquestador, juez y agentes de forma visual y animada, con el repositorio y lo principal del proyecto. P5
    pide el consumo y el contexto de cada elemento siempre a la vista. P29 pide la gestión de agentes.
  - Hoy el lienzo solo tiene el plan: un nodo es una tarea (`NodeViewModel` es un `TaskSpec`). La plantilla de agentes es
    fija y solo se edita su memoria. Cinco campos del agente no gobiernan nada (`PathGrants`, `MaxRounds`, `Objectives`,
    `MaxTotalTokens` y `SystemInstructions`; docs/17 §0.2).
- **Decisión** (la de docs/17; WP6 la detalla aquí):
  1. **Un documento, un lienzo, dos capas y una tira** (D-07): la capa «Equipo» (agentes y repositorio), la capa «Plan» (el
     lienzo de hoy, con su modelo intacto) y una tira fija con una tarjeta por agente y sus barras, visible en las dos capas.
     La asignación de una tarea a su agente es una etiqueta y una selección enlazada, no un cable.
  2. **La capa Equipo es la plantilla** (D-08): editarla cambia `RosterHash` y retira la aprobación del plan, y lo dice antes de
     guardar. Ningún cable del equipo entra en el plan (`BuildTasks`, `DependsOn`). Modelo: `Domain/Team/TeamGraph.cs`
     (`AgentNode`, `RepositoryNode`, `TeamEdge`; P0). Disposición: `canvas-layout.json` versión 2, que lee la 1 como la capa
     Plan.
  3. **Cuántos agentes** (D-09): un orquestador, al menos un juez de otro vendor y hasta 8 ejecutores (`TeamLimits`, P0).
  4. **El nodo del repositorio y la ficha** (D-10): rama, HEAD, remotos limpios (`RepositoryRemote` nunca guarda credenciales,
     P0), cambios sin confirmar, stack y carpetas principales, sin red. La ficha es memoria tuya (`MemoryKind.ProjectBrief`,
     `IProjectBrief`; P0).
  5. **Cada cable lo hace real una regla** (D-11): «Trabaja en» con `WriteScopeOutsideAgentArea` (WP1), «Revisa» con
     `JudgeVendorNotIndependent`, «Reparte» solo es dibujo y «Entrega» sale de la capa Plan.
  6. **Estado vivo, animación barata y accesible** (D-12 a D-17): las barras de la tanda 2 (`MetricBar`, con partes desde el
     P0), la proyección agrupada `IRunLiveProjection` (P0; la implementa WP8), `Storyboard` en XAML a 30 fps como mucho, que se
     apagan con «Reducir movimiento» (`AppSettings.ReduceMotion`, P0) o la opción de Windows.
  7. **Gestión de agentes** (D-42 a D-46): el inspector solo enseña lo que manda; las instrucciones del agente llegan a su
     prompt como datos (`<agent_instructions sha256=…>`).
  8. **Cómo se activa** (WP6). El constructor es opcional: `OrchestratorDocumentViewModel` recibe un `BuilderServices` (todos sus
     puertos son opcionales) y un `reduceMotion`. Sin `BuilderServices` el documento es el de antes de la fase, con sus pruebas;
     con él (aunque sea `BuilderServices.None`) las capas, la tira y el análisis están, y cada servicio que falta solo apaga lo
     que lo necesita, sin botones que no hacen nada.
  9. **La capa Equipo no toca el plan.** `CanvasViewModel.Nodes` sigue siendo solo tareas; el equipo es otra colección
     (`CanvasViewModel.Team`) con sus propios nodos y cables, derivados de la plantilla: «Reparte» y «Revisa» salen de los roles, y
     «Trabaja en» de las carpetas del agente (`PathGrants`). `BuildTasks` da el mismo plan con y sin la capa, y una prueba lo fija
     con el hash del plan.
  10. **Las carpetas del agente mandan por el cable.** Conectar un agente a una carpeta, arrastrando o con «Conectar con…», edita
      sus `PathGrants`. Pasar de «todo el proyecto» a una carpeta estrecha el alcance y no pide nada; ampliarlo (otra carpeta, o
      volver a todo el proyecto) o dar `Bash`, `WebFetch` o `WebSearch` es `GrantHighRiskCapability` y pide Windows Hello con la
      barrera fija de WP5. Sin una confirmación compuesta, no se concede.
  11. **El repositorio se lee sin red ni procesos propios.** La rama, la versión y los cambios por la capa endurecida
      (`IWorktreeManager.InspectAsync`, detrás de `IRepositoryStateReader`); los remotos, del texto de `.git/config` (o del común de
      un worktree), cada URL limpia con `RepositoryRemote.Cleaned` al leerla (se quita siempre el usuario, también el de la forma `usuario@host:ruta`,
      y entera la query y el fragmento; la autoridad llega hasta la primera `/` y el usuario hasta su última `@`): ninguna URL con usuario ni contraseña llega a un
      nodo, a un texto ni al fichero de disposición. No se sigue ningún `include`. Las carpetas principales son las de primer nivel
      con más ficheros (cuatro como mucho, sin `.git`, `bin`, `obj`, `node_modules` ni las que empiezan por punto), más las que algún
      agente tiene asignadas.
  12. **El análisis es una pieza aparte del hilo de la interfaz.** `AnalysisBandViewModel` (200 ms tras la última edición,
      `Task.Run` sobre una copia inmutable, generación como `RunMonitorViewModel`) corre al lado de `Revalidate`, que sigue siendo
      síncrono: `IsApproved` compara el hash del plan al instante y mover la validación al análisis rompería esa garantía. Los
      errores del análisis impiden aprobar, además de los del validador. El historial (`EditHistory<MontageEditState>`, Ctrl+Z y
      Ctrl+Y) guarda los arreglos y las ediciones a mano, estas una vez por pausa para que escribir no sea un paso por letra; si
      escribir el estado deshecho falla (Windows Hello denegado), el historial vuelve a lo que hay en pantalla.
  13. **«Revisar el orden» y el chat gastan cuota, así que preguntan.** Una tarjeta con la estimación (`IUsageEstimator`, con sus
      referencias; sin ellas lo dice, nunca un número inventado) y un OK por uso, que el servicio gasta una vez
      (`IQuotaConsentLedger`). Lo que dicen los agentes se enseña apartado y como texto de un agente. Ninguna sugerencia se aplica
      sola: un borrador de requisito entra al editor como borrador de la persona (`CopySuggestion`), y una del equipo es un
      arreglo de un clic.
  14. **Animación.** `Storyboard` en `Themes/Nodify.xaml`, disparados por `DataTrigger` sobre `IsAnimated` o `IsPulsing`, a 30
      fotogramas por segundo, de un ciclo de 0,8 s o más; los apaga el modelo de vista (`MotionPolicy`: el ajuste «Reducir
      movimiento» o `SystemParameters.ClientAreaAnimation`), así que sin animación no se dispara ninguno y el estado sigue en el
      texto y el icono. Nunca más de doce cables animados.
  15. **Atajos.** Ctrl+1 «Equipo» y Ctrl+2 «Plan», Ctrl+Z y Ctrl+Y en el lienzo. Son del lienzo, no de la ventana: el editor de
      ficheros (que usa Ctrl+Alt+D y Ctrl+Alt+F, y otro ámbito de foco) no los ve, y la ventana usa Ctrl+P, Ctrl+Mayús+D,
      Ctrl+Mayús+A y Ctrl+` (`MainWindow.xaml`).
- **Alternativas:**
  - Las de D-07 (dos vistas enlazadas; un lienzo combinado con carriles por agente).
  - **Un solo `NodifyEditor` con las dos colecciones:** un cable de «Equipo» y uno de «Plan» compartirían conectores y selección, y
    arrastrar entre capas sería posible. Son dos editores, uno visible cada vez, con su zoom y su disposición.
  - **Mover la validación al análisis diferido** (como sugiere D-16): descartado por lo de la decisión 12.
  - **Guardar las carpetas principales que elige la persona en el fichero de disposición:** la elección sale de las carpetas de
    cada agente; un fichero más con rutas del repositorio no ganaba nada.
  - **Leer los remotos con `git config --get-regexp` por la capa endurecida** (D-10): es lo previsto, y necesita un método nuevo
    en `IWorktreeManager`, fuera de WP6. Mientras tanto se lee el texto del fichero, que no ejecuta nada.
- **Consecuencias:**
  - **Medición de F13-7** (`TeamRenderAndAccessibilityTests`, con la etiqueta `Measurement`, fuera de la serie del CI): con 40
    nodos (el orquestador, 30 ejecutores, 8 jueces y el repositorio), 12 cables animados y una ráfaga de 100 eventos que llega
    como un solo cambio agrupado de 12 nodos, aplicar el estado y medir el diseño tarda unos 45 ms en este equipo, de ellos unos
    22 ms de enlaces y 24 ms de medida y colocación; aplicar solo en el modelo de vista son 0,4 ms. Algún barrido salió de los 50
    ms (hasta unos 110 ms, con una recolección de basura del proceso): la prueba admite uno por serie y ninguno por encima de 150 ms,
    y la mediana tiene que estar por debajo de 50. La parte determinista, que solo se actualizan los nodos que el cambio nombra y
    que no se mueven más de doce cables, va en la serie normal.
  - **Pruebas** (`tests/AgentStudio.Tests/Desktop/Orchestrator/{Canvas,Team,Preparation}`): la capa y sus cables, los límites, el
    editor y el inspector, el nodo del repositorio con repositorios temporales y una URL `https://usuario:token@…` (que nunca sale
    en un texto ni en el fichero), la disposición v2, las barras y los estados, la banda con su generación y su historial,
    «Revisar el orden» y el chat con agentes guionizados, la integración en el documento, el render fuera de pantalla y las
    pruebas con `AutomationPeer` (nombre completo, `LiveSetting`, valor, mínimo y máximo de las barras, el menú «Conectar con…»).
  - **Pendiente de la integración y de otros paquetes** (docs/04 §21.8): componer `BuilderServices` y `reduceMotion` en
    `OrchestratorDocumentViewModel`, llamar a `ShowTeamLayer()` desde «Agentes», `IAgentRoster.DeleteAsync` para quitar agentes
    y la lectura de remotos por la capa de git. WP8 implementa `IRunLiveProjection` en el monitor.
  - **Lo que no hace WP6:** la estimación del plan entero para `cost.over-budget` (el analizador recibe `EstimatedAgentRuns` nulo y
    calla), los hechos de contexto por tarea (`TaskContexts`, vacíos: las reglas que los necesitan callan) y el número de ficheros
    del proyecto en la recomendación de modelo. Ninguno inventa un dato.

## Adenda · 2026-11-11 · quitar agentes de la plantilla (P48)

La capa Equipo no podía quitar agentes: `IAgentRoster` solo guardaba, así que `TeamEditorViewModel` recibía un delegado nulo, no
enseñaba «Quitar del equipo» y un conjunto de cambios con una baja (por ejemplo, el deshacer de «Añadir un juez») se rechazaba
antes de guardar nada.

1. **El borrado en el almacén.** `IAgentRoster.DeleteAsync(projectId, agent, ct)` (Application) y su implementación en
   `SqliteAgentRoster`: `DELETE FROM agents WHERE id = $id AND project_id = $project`, en una transacción y por la misma
   conexión de `ProjectDatabases` que el resto (el punto 8 dice qué más escribe). Borrar un id que no está (nunca guardado, ya
   borrado o de otro proyecto) **no hace nada y no falla**: el editor ya comprobó la plantilla que enseñaba, y un error ahí solo
   convertiría un doble clic en un mensaje. El único campo que apunta a `agents(id)` es `tasks.assigned_agent_id`, que ningún
   almacén escribe (0002 deja esas tablas sin uso): en la aplicación no hay fila que impida el borrado, y una puesta desde fuera
   sí lo impide, con las claves foráneas activas, y entonces falla la operación entera (una prueba lo fija con una fila real).
   `OrchestrationSession.RemoveAgentAsync` lo expone al documento con la misma condición que `SaveAgentAsync` (que exista el
   almacén), y `InitializeBuilder` pasa ese delegado al editor: `CanRemoveAgents` es cierto cuando se puede guardar, y sin
   almacén sigue sin botón.
2. **La aprobación.** Quitar un agente no concede nada, y la lista de Windows Hello de D-37 no lo recoge, así que **por sí
   solo no pide Hello**: no se añade ninguna acción a `PresencePolicy`. Lo que ya mandaba sigue mandando: si el conjunto de
   cambios concede poder (shell, red o más carpetas a algún agente), se pide **un solo Hello para todo el conjunto, antes de
   guardar o quitar nada**, y las bajas llegan al almacén después de esa aprobación y de las comprobaciones. Si se prefiere
   que quitar también pida Windows Hello, es una acción nueva de D-37 y una decisión aparte; el «sí» de la tarjeta del punto 7 es
   una pregunta de la capa, no Hello.
3. **Qué plantilla queda.** «Quitar del equipo» deja de llamar al almacén por su cuenta y pasa por `ApplyManyAsync`, como
   cualquier cambio de la plantilla. Antes de preguntar o escribir nada se comprueba la plantilla que quedaría
   (`TeamRemoval.Problems`, dominio puro): una baja no puede llevarse **al orquestador** (`team.orchestrator-required`) ni **al
   último juez** (`team.judge-required`, el código que ya usa `TeamGraph.Validate`), y lo dice con el texto de la capa
   (`Canvas_Problem_*`) dentro de `Canvas_Editor_Refused`. `TeamGraph.Validate` no pide orquestador (una plantilla leída del
   almacén puede no tenerlo y la capa ofrece añadirlo), por eso la regla vive en la baja y no en el grafo: una plantilla que ya
   carecía de un rol no se rechaza por ello al quitar otra cosa, y un cambio que sustituye al único juez por otro en la misma
   operación pasa. Deshacer «Añadir un juez» cuando ese juez era el primero que tenía el equipo queda así rechazado.
4. **Todo o nada, y su límite.** El almacén no tiene operación por lotes: el conjunto se escribe como una secuencia, los
   guardados primero y las bajas al final, y si el almacén falla a medias se deshace por compensación (se vuelve a guardar la
   versión anterior de cada agente tocado y se vuelve a guardar cada agente ya quitado). Lo que una compensación no devuelve es el
   sitio del agente que se quitó y se volvió a guardar, que pasa al final de la lista y con otra fecha de creación (`RosterHash`
   ordena por id, así que su huella no cambia), ni las skills que ese agente tenía activadas, que la baja apagó (punto 8) y la
   persona tendrá que volver a encender. Una operación por lotes del almacén, en una transacción que guarde y borre a la vez,
   quitaría esa ventana, y no se ha hecho porque el contrato crecería más de lo que pide P48. Si falla también la compensación, la
   capa ya no dice solo «No se pudo guardar»: avisa de que la plantilla puede haber quedado a medias y manda a «Volver a cargar»; y
   si lo que falla es la relectura del documento tras un guardado bueno, lo dice sin convertirlo en una excepción. Los conjuntos de
   cambios no se solapan (`ApplyManyAsync` lleva una marca de ocupado: el segundo no empieza y lo dice, y los botones esperan).
5. **Por qué no afecta al historial.** Las cabeceras congeladas llevan las definiciones enteras: `RunRecord.Roster` (con
   `RunHeaderHash`) y `DebateParticipantDefinition.Agent` y `DebateRecord.Judges` en los debates. Ninguna cabecera lee la
   plantilla viva después de crearse, así que borrar un agente de la plantilla no cambia lo que una ejecución o un debate
   pasados hicieron con él. Lo que sí lee la plantilla viva es lo que empieza de nuevo (planificar, empezar una ejecución o un
   debate, auditar, elegir quién analiza el consumo), que es justo lo que debe ver la baja. Por eso no hay borrado lógico ni
   marca de «archivado», y una prueba lo fija: la cabecera de una ejecución y su hash se leen igual tras borrar de la plantilla un
   agente que la ejecución congeló.
6. **Qué no cambia.** `FixApplier` no tiene ningún arreglo que quite agentes (los arreglos añaden o editan), así que no hubo
   nada que dejar de rechazar ahí; las bajas del borrador llegan por `DraftHost.ApplyAsync`, que ya calculaba los agentes que
   desaparecen entre el estado anterior y el nuevo y los pasaba a `ApplyManyAsync`. **Defecto encontrado, anterior a este cambio
   y aparte de él:** el anfitrión maneja bien las bajas, pero Ctrl+Z tras «Añadir un juez» (o tras cualquier arreglo que cambie
   la plantilla) no llega al almacén y no deshace nada. Tiene dos causas, ninguna arreglada aquí:
   - **El historial se reinicia al releer la plantilla.** `BuilderContextChanged` (`src/AgentStudio.Desktop/Orchestrator/OrchestratorDocumentViewModel.Builder.cs`)
     termina siempre con `Band.Reset()`, y guardar la plantilla vuelve a leer el contexto antes de que `ApplyFixAsync`
     (`src/AgentStudio.Desktop/Orchestrator/Canvas/AnalysisBandViewModel.cs`) anote el estado nuevo, así que el historial solo conoce
     estados con la plantilla ya cambiada. Las pruebas de `AnalysisBandTests` no lo ven porque su anfitrión falso no relee el contexto.
   - **`AgentDefinition.Equals` compara las listas por referencia.** Es un `record` con `IReadOnlyList` (`src/AgentStudio.Domain/Agents/AgentDefinition.cs`)
     y sin `Equals` propio, así que dos definiciones idénticas leídas en momentos distintos no son iguales; el cálculo de `changed` de
     `DraftHost.ApplyAsync` (`OrchestratorDocumentViewModel.Builder.cs`) trata por eso como cambiado a todo agente releído del
     almacén, y un deshacer escribiría de más aunque el historial funcionara.
7. **Quitar pide confirmación (revisión independiente, 2026-11-11).** No hay en la capa un servicio de diálogos reutilizable: lo
   único que confirma en el editor es `PresenceConfirmer`, que es Windows Hello de D-37 y no se usa para esto (punto 2). Se siguió
   el patrón de `PendingAdd`: una tarjeta en `TeamPanelView` (`PendingRemoval`, «Sí, quitar» y «Conservar») que dice que la
   definición se pierde de la plantilla y no se recupera desde aquí (Ctrl+Z no la alcanza), que se apagan las skills que tenía
   activadas y que las ejecuciones y los debates pasados la conservan. Solo la pide el botón: las bajas que llegan por el borrador
   (`DraftHost`) son de un arreglo o de su deshacer, que la persona ya eligió. Elegir otro agente, releer la plantilla o pasar a solo
   lectura retira la pregunta; y el botón avisa ahora de su `CanExecute` al cambiar la selección (antes quedaba apagado).
8. **Un id que vuelve no hereda skills (revisión independiente, 2026-11-11).** `skill_activations` (ámbito `Agent`, `scope_id` el id
   del agente) es un historial que nunca se reescribe, y el editor da el primer id libre (`ejecutor-2`): un agente nuevo con ese id
   heredaba por `SkillActivationResolver` las skills activadas para el anterior, sin que la pantalla de Skills lo enseñara. Por eso
   `DeleteAsync` escribe, **en la misma transacción que el borrado**, una decisión «apagada» (`decided_by` = `roster:agent-removed`)
   por cada skill cuya última decisión para ese agente sea «encendida»; no cambia ni borra filas. Lo hace aunque el agente ya no
   estuviera en la plantilla (cierra lo que dejó una baja anterior), y si no hay nada encendido no escribe nada; si el borrado falla,
   no se guarda nada de ello. Consecuencia que conviene conocer: un registro de solo añadir no tiene «sin decisión», así que el agente
   que llegue después con ese id arranca con esas skills apagadas **a su nivel, que gana al del proyecto** (el lado de menos poder), y
   la pantalla de Skills enseña el apagado como decisión del agente. La alternativa, no reutilizar ids, obligaba al editor a conocer
   el almacén de skills y dejaba el residuo en el historial; no se tomó.

## Adenda · 2026-11-12 · deshacer llega al almacén y la aprobación sigue al estado final (P55)

Ctrl+Z de un arreglo que cambiaba la plantilla no llegaba al almacén: al guardar, el documento relee el contexto y
`BuilderContextChanged` reiniciaba la banda (`Band.Reset()`), así que su historial solo conocía estados con la plantilla ya
cambiada. Tres correcciones, mínimas:

- **El reinicio no pisa una escritura de la propia banda.** `AnalysisBandViewModel.Reset()` no hace nada mientras la banda escribe
  un estado (un arreglo, Ctrl+Z o Ctrl+Y): esa relectura no es un punto de partida nuevo, y el historial que conoce el estado
  anterior se conserva. Deshacer vuelve a pasar por `ApplyAsync`, que guarda los agentes que difieren (la compensación). Se
  descartó que deshacer compense a mano en el almacén: duplicaría el conjunto todo o nada, el Windows Hello único y los
  rechazos que ya tiene `ApplyManyAsync`. Una relectura ajena a la banda (abrir, «Recargar») sigue reiniciándola.
- **`AgentDefinition.Equals` compara el contenido.** Las listas (objetivos, herramientas, carpetas) se comparan elemento a
  elemento y en orden, como las codifica `RosterHash`, con un `GetHashCode` coherente. Un agente releído sin cambios ya no cuenta
  como cambiado al aplicar un conjunto, así que una compensación no reescribe agentes que no cambian.
- **La aprobación sigue al estado final.** `OnRosterSavedAsync` relee primero y compara `RosterHash` de la plantilla aprobada
  con la que quedó (el mismo hash con el que el motor se niega a arrancar). Si coincide, la aprobación se conserva; si difiere,
  se retira con el mismo aviso de antes. Un cambio de los requisitos congelados sigue retirándola en `OnContextChanging`.

## Adenda · 2026-11-12 · los remotos del nodo del repositorio por la capa git endurecida (P51)

- **`ListRemotesAsync`.** Los remotos del nodo ya no salen del texto de `.git/config` si el lector tiene la capa git:
  `IRepositoryRemoteReader.ListRemotesAsync` (`Infrastructure/Git/RepositoryRemoteReader.cs`, sobre `GitCommandRunner`) pregunta
  `git config --local --no-includes -z --get-regexp ^remote\..*\.url$`. Solo la configuración del repositorio (la común, en un
  worktree), ningún `include` ni `includeIf`, la configuración neutralizada de la capa, `core.hooksPath` sobre un fichero vacío, sin
  transporte y con el recorrido del directorio git por enlaces plantados. La respuesta es una lista de claves y valores, no un
  fichero, y git no ejecuta nada para darla, así que no pide la revisión de callbacks de los comandos de contenido.
- **Acotado y fallando cerrado.** La salida se acota en origen (256 KiB de caracteres: git muere al pasarlo y la lectura se rechaza
  con `output-too-big`, no se corta), con 2048 caracteres por dirección, y más de 100 remotos se rechazan igual, nunca se muestran cortados; un repositorio no aceptado, git fallando o
  una excepción de la capa dan cero remotos con un código, nunca una ruta. Una negativa **no vuelve al fichero**.
- **El respaldo, acotado mientras se lee.** Sin la capa (`IRepositoryStateReader.ListRemotesAsync` da `null`: lectores de prueba y
  composiciones sin git) se sigue leyendo el fichero, pero el tope de 1 MB ya no se mira antes (un fichero que crecía entre la
  comprobación y la lectura se leía entero): se leen como mucho el tope más un byte, y pasarlo es «demasiado grande».
- **Sigue igual:** `FileSetupRepositoryReader` (el paso 1 del configurador de ADR-0055) no usa aún el lector y deja los remotos vacíos.

## Adenda · 2026-11-12 · un fallo de la relectura retira la aprobación y una aprobación nueva no se toca (revisión de P55)

- Si `ReloadAsync` lanza después de guardar, el almacén ya cambió y la aprobación no se puede juzgar: se retira, como cuando la
  retirada iba antes de la relectura, y el fallo sigue siendo de quien llamó.
- Solo se juzga la decisión que había antes de releer. Si durante la relectura el plan se aprobó de nuevo, esa aprobación es
  más nueva que la plantilla leída y se deja como está.

## Adenda · 2026-11-13 · la pantalla de Skills dice por qué un agente que vuelve tiene una skill apagada (P57)

El almacén ya guardaba el motivo de las bajas que cierran skills (`skill_activations.decided_by` = `roster:agent-removed`, punto 8 de la adenda del 2026-11-11), pero `SkillActivation` no lo llevaba y la pantalla solo sabía «decisión del agente». `SkillActivation` gana al final `ClosedByRemoval` (falso por defecto; `SkillActivationStore` lo pone a verdadero cuando `decided_by` es ese valor; los registros anteriores se leen igual). `SkillsDocumentViewModel` usa el texto `Skills_Effective_OffRemoved` cuando la skill está apagada y la última decisión del agente es la de la baja. Si la persona decide después sobre ese agente, la última decisión es suya y vuelve el texto de siempre. El archivo global (`.jsonl`) no guarda bajas de agentes y no cambia.