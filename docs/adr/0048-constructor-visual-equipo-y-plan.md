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
