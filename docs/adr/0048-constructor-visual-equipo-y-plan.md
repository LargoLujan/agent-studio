# ADR-0048 · El constructor visual: un lienzo con las capas «Equipo» y «Plan», la tira del equipo, el nodo del repositorio y la ficha del proyecto; la capa Equipo es la plantilla, y las carpetas y las instrucciones de cada agente pasan a mandar

- **Estado:** propuesto · 2026-11-06. Esqueleto del P0 de la Fase 13; lo completa **WP6** (lienzo, capas, tira, inspector y
  preparación). Recoge D-07 a D-17 y D-42 a D-46 de [docs/17 §4.1 a §4.3 y §4.10](../17-constructor-visual-de-agentes.md#41-equipo-y-plan-en-un-solo-lienzo)
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
- **Alternativas:** las de D-07 (dos vistas enlazadas; un lienzo combinado con carriles por agente). WP6 añade las que
  descarte al implementar.
- **Consecuencias:** pendientes de WP6 (medición de F13-7, accesibilidad de F13-8 y lo que cambie en docs/03 §3).
