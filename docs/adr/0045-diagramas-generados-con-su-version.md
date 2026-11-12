# ADR-0045 · Diagramas generados con la versión que describen: Mermaid como texto, una gramática acotada y una vigencia que no lee de vuelta lo exportado

- **Estado:** aceptada · 2026-11-12 (P0 de la Fase 6). Recoge D-23 a D-30 de [docs/20](../20-fase-6-auto-doctor-y-auto-graph.md) y las
  decisiones 1, 2 y 9 de su §13 (todas, la opción recomendada). WP-D construye los generadores, la gramática, la caché y la exportación; la Fase 16
  le añade una adenda con el motor que dibuja ([docs/19](../19-documentacion-viva-del-proyecto.md) D-18). **Numeración:** docs/16 reservó el 0045 para
  esto; los 0041 a 0044 y el 0046 no existen. Corrige docs/16 D-21, que ponía los diagramas en `.agentstudio/diagrams/`: queda como la opción (b) de la
  decisión 9.
- **Contexto:**
  - F6-10 pide diagramas Mermaid de la solución, del plan de una ejecución y de la secuencia de agentes, orquestador y juez, con la cabecera de su versión y
    «desactualizado» cuando el modelo cambia, y que nunca se lean de vuelta. F6-11 pide cuatro discrepancias que se detecten sin IA.
  - Los nombres de proyecto, de espacio de nombres, de agente y los títulos de tarea vienen de un repositorio o del registro: son datos no fiables
    ([ADR-0018](0018-modelo-de-amenaza-fase-3.md)). Un nombre con `click n1 "https://…"` o con `%%{init}%%` no puede acabar siendo sintaxis.
  - La Fase 16 también habla de diagramas. Con docs/19 por un lado y docs/20 por otro, hacía falta un solo dueño de cada pieza.
- **Decisión:**
  1. **Mermaid es texto generado desde un modelo** (D-23): tres generadores puros, cuatro valores de `DiagramKind` (`Solution`, `ProjectNamespaces`,
     `RunPlan`, `RunSequence`) porque el de la solución escribe dos clases de fichero. Solo lo estático: una relación que propone una IA nunca sale en un
     diagrama generado. «Con Roslyn o MSBuild» de F6-10 se lee «con Roslyn y la lectura de [ADR-0062](0062-auto-graph-sin-msbuild.md)».
  2. **Una gramática acotada** (D-24). Identificadores propios (`n1`, `p1`…) por orden ordinal; **un solo escapador** para todo texto que venga de datos
     (comillas, `#`, `<`, `>`, `&`, `;`, `%`, los saltos de línea y los caracteres de control, `:` en un participante, tope de 80 caracteres); las etiquetas de
     un diagrama de flujo siempre entre comillas; en el de secuencia el mensaje sale de un vocabulario cerrado, y el título de una tarea va en una nota
     escapada; sin directivas, sin `click`, sin enlaces, sin estilos que vengan de datos; 200 nodos, 400 relaciones y 300 mensajes como mucho; `\n` y UTF-8 sin
     BOM para que los ficheros dorados sean iguales byte a byte. La prueba adversaria de F6-10 comprueba que los nombres hostiles dan las mismas sentencias que
     los inocuos.
  3. **En esta fase el diagrama se ve como texto** (D-26, decisión 2). La solución ya se dibuja en Auto Graph y el plan en el lienzo. El motor (mermaid.js
     en WebView2, con su filtro de peticiones) es de la Fase 16: traerlo aquí duplicaría su paquete.
  4. **Cabecera de versión** (D-25): un comentario al principio de cada diagrama con el diagrama, la versión del generador, el commit (o la ejecución y la cabeza
     del registro), la huella del modelo y la fecha, que sale de un `TimeProvider` inyectado. Un diagrama al día no se reescribe.
  5. **Dónde viven** (D-25, decisión 9 (a)): en una caché de este equipo (`%LOCALAPPDATA%\AgentStudio\diagrams\<proyecto>\`) con su índice y la versión anterior de
     cada diagrama, y en el repositorio **solo si la persona pulsa «Exportar al repositorio»** (por defecto `docs/generado/diagramas/`, con nombres de una lista
     cerrada). No se exporta con una ejecución en marcha ni se pisa un fichero cambiado sin preguntar. La Fase 16 exporta al lado (`docs/generado/`) con el
     mismo exportador y la misma cabecera.
  6. **«Desactualizado» es la huella del modelo, no el commit** (D-20 y D-25). `CodeGraph.ModelHash` es el SHA-256 del JSON canónico de la parte estática
     (nodos, relaciones estáticas y huecos) y la versión del generador, **sin el commit y sin las relaciones inferidas**: dos commits con el mismo modelo dan la
     misma huella y un diagrama no se queda «desactualizado» por un commit que no cambió nada.
  7. **Lo exportado nunca se lee de vuelta.** Agent Studio no interpreta un fichero que escribió en el repositorio, ni su cabecera ni su contenido: guarda en su
     índice la ruta y el SHA-256 de lo que escribió, y un fichero con otra huella es «cambiado fuera». Las discrepancias saltan la carpeta de exportación.
  8. **Regenerar actúa solo sobre la caché y se puede deshacer de verdad** (`<diagrama>.prev`), así que «Regenerar los diagramas desactualizados» es un arreglo
     reversible de Auto Doctor ([ADR-0061](0061-auto-doctor.md)).
  9. **Las discrepancias usan los códigos de docs/19** (D-27): `doc.link-broken`, `doc.adr-cites-missing`, `doc.diagram-stale` y, con el código que ya usa el
     informe del hito, `requirement.uncovered`. La Fase 16 amplía la lista y no la renombra. Con reglas cerradas (D-28), gravedad «aviso» y el mismo
     commit que lee el grafo (D-19).
- **Lo que fija el P0** ([docs/04 §25](../04-contratos.md#25-fase-6-auto-doctor-auto-graph-entorno-declarado-y-diagramas)): los contratos de
  `Application/Diagrams` y `Application/Consistency` y `CodeGraph.ModelHash`. `DiagramSource` ya impone lo que el texto promete (solo `\n`, sin BOM, una huella
  SHA-256, un tema de una línea), `DiagramExportRequest` rechaza una carpeta que salga del proyecto o sea la de git, y `ConsistencyFinding` solo lleva rutas
  relativas a la copia. **No** fija el escapador de Mermaid ni los generadores: son de WP-D (`src/AgentStudio.Memory/Diagrams`), donde la gramática se prueba con
  ficheros dorados.
- **Alternativas descartadas:**
  - Dibujar Mermaid en la app en esta fase (decisión 2 (b)): un paquete L con su spike y el filtro de peticiones que ningún WebView2 de la app tiene hoy.
  - Guardar siempre los diagramas en `.agentstudio/diagrams/` (decisión 9 (b)): cada regeneración sería un cambio en el repositorio de la persona.
  - Decidir «desactualizado» por el commit: marcaría como viejo un diagrama cuyo modelo no cambió.
  - Leer de vuelta el diagrama exportado para saber qué versión es: trataría como verdad un fichero que la persona puede editar.
- **Consecuencias:**
  - Un diagrama es un dato derivado que se reconstruye: borrar la caché no pierde nada.
  - La Fase 16 hereda los puertos (`IDiagramGenerator`, `IDiagramCatalog`) y añade valores a `DiagramKind`, más generadores y una adenda con el motor.
  - **Residual aceptado:** sin motor, «que compila» lo garantizan la gramática y los dorados, no `mermaid.parse`; eso llega con la Fase 16.
