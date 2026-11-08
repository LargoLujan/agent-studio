# ADR-0052 · El análisis del consumo, determinista siempre y con un agente opcional del vendor que no produjo; el contexto por partes

- **Estado:** aceptada · 2026-11-08 (WP3 de la Fase 13). El esqueleto lo dejó el P0 el 2026-11-06. Recoge D-13 y D-34 a D-36
  de [docs/17 §4.2 y §4.8](../17-constructor-visual-de-agentes.md#48-análisis-del-consumo), la decisión 5 (a) que aceptaste el
  2026-11-06 y el hallazgo de la ventana aprendida (§0.2, F13-20). **Amplía** [ADR-0019](0019-consumo-como-proyeccion-del-registro.md)
  y [ADR-0021](0021-salud-del-contexto-por-paso.md). El estimador de F9-7, que el esqueleto ponía aquí, lo hace WP2
  ([ADR-0050](0050-modelos-y-potencia.md)): este ADR solo lo usa por su contrato (`IUsageEstimator`).
- **Contexto:**
  - P6 pide un análisis (un botón) de si el contexto fue escaso o algo se consumió sin sentido. P5 pide el contexto por partes.
  - Hasta ahora había consumo por intento con seis orígenes, pico y compactaciones, y el auditor de 9 códigos; no había
    desglose por partes ni análisis del consumo. La tanda 2 conectó la ventana aprendida del registro al monitor, pero solo
    se aprendía al releer el registro tras una ejecución: un intento no aprovechaba la ventana que acababa de informar otro de
    la misma ejecución.
- **Decisión:**
  1. **El contexto por partes** (D-13, F13-6). Cada lanzamiento del motor (`AgentRunner`: el planificador y los ejecutores)
     mide las partes de su prompt **sobre el texto exacto que recibe el agente**, en caracteres (`PromptParts`), y las guarda en
     `AgentRunStarted` versión 3 (`ContextParts`). Sin partes, el arranque sigue en la versión 2, byte a byte.
     - Un bloque de datos (`<project_brief>`, `<agent_instructions>`, `<project_memory>`, `<upstream_output>`,
       `<previous_attempt>`, `<handoff>` y `<human_answer>`) es su parte, de la línea que lo abre a la que lo cierra. Su
       contenido va recodificado por `PromptData`, que escapa `<`: ninguna línea de datos abre ni cierra un bloque, ni empieza
       una sección.
     - Una sección (`## …`) va con el primer bloque que contiene, con su título y su línea de «son datos». Una sección que
       empieza por `## Requisitos` es la de los requisitos. Lo demás del sistema (título, reglas, objetivo e instrucciones de la
       tarea, alcance, verificación e informe) es el protocolo.
     - Las partes del texto suman exactamente su longitud. Las skills suman aparte el nombre y la descripción de cada
       `SKILL.md` materializado (lo que el CLI presenta), sin seguir enlaces.
     - Un constructor de prompts que sepa sus partes mejor puede darlas (`AgentInvocation.ContextParts`); si no, se miden. Así
       las partes nuevas de WP4 (ficha, instrucciones y respuesta) se miden solas si usan sus etiquetas.
     - Observability da el desglose (`ContextBreakdown`): cada parte, estimada por Agent Studio (caracteres entre 4, redondeo
       hacia arriba); la conversación, lo que el CLI informó del paso menos las partes; en Codex, «no expuesto»; sin paso,
       «sin dato». Nunca un cero en lugar de una cifra. `ContextPartsLog` lee las partes de cada intento del registro.
  2. **La ficha del proyecto** (D-10). `ProjectBriefStore` implementa `IProjectBrief` sobre la memoria: una entrada de la
     persona (`MemoryOrigin.User`), con su tema y su fuente fijos, que sustituye a la vigente. Guardar el texto vigente no
     escribe nada. El empaquetador la da con el paquete (`ContextPack.Brief`) como dato con su huella a **todo agente salvo el
     juez y los auditores**, sea cual sea su memoria. No es un elemento del paquete: no cuenta en el presupuesto ni en su
     hash, y ningún nivel pide su categoría. Su bloque es `ProjectBriefBlock` (`<project_brief entry=… sha256=…>`). El paquete
     dice sus partes (`ContextPack.Parts()`: requisitos, memoria y ficha).
  3. **La ventana aprendida en vivo** (F13-20). Un catálogo leído del registro aprende, mientras la ejecución sigue, cada
     ventana que el CLI informa exacta (`ModelWindowCatalog.LearnLive`, que llama `ContextHealthTracker`). Los seguidores que lo
     comparten (los intentos del monitor y la planificación) la usan desde el primer paso del intento siguiente, como «estimado
     por Agent Studio», sin esperar a releer el registro. Lo aprendido en vivo gana a lo del registro y a la tabla base; la
     ventana del propio intento les gana a todos. Los catálogos compartidos (`Empty` y `Baseline`) nunca aprenden, para que nada
     pase de un proyecto a otro. No hace falta tocar el monitor: ya pasa su catálogo a cada seguidor.
  4. **El análisis determinista** (D-34, F13-15). `ConsumptionAnalyzer` (servicio) y `ConsumptionRules` (puro) leen el registro
     verificado; uno que no verifica falla cerrado. Gratis e instantáneo: no lanza nada ni escribe nada. Corre al terminar cada
     ejecución y bajo demanda (lo llama el escritorio, WP8). Cada hallazgo dice lo que costó con su origen (los tokens de entrada
     propios de sus intentos, con el origen más débil; una suma parcial es estimada y lo dice), nunca un ahorro. Umbrales de
     partida (`ConsumptionThresholds`), iguales para todos los proyectos:

     | Código | Se encuentra cuando | Sobre qué | Lo que costó |
     |---|---|---|---|
     | `ctx.pack-omitted` | El paquete de un arranque dejó entradas fuera | Agente | Entrada de esos intentos |
     | `ctx.high-peak` | Pico ≥ umbral de compresión (85 %) de la ventana | Intento | El pico |
     | `ctx.compacted` | El CLI compactó al menos una vez | Intento | Su entrada |
     | `ctx.heavy-preamble` | Skills + memoria ≥ 4 000 tokens estimados en un intento que terminó bien en 3 minutos o menos (solo arranques v3) | Agente | Esos tokens, estimados |
     | `retry.same-class` | 2 o más intentos seguidos de una tarea con la misma clase de final, distinta de «completado» | Tarea | Su entrada |
     | `retry.same-failing-tests` | 2 o más intentos seguidos con el mismo paso y los mismos contadores anclados (fallos > 0 y aprobadas) | Tarea | Su entrada |
     | `plan.corrected` | `PlanRevised` de la ejecución o de la planificación de su plan | Ejecución | Los intentos del planificador tras el primero |
     | `verdict.corrected` | Un veredicto que sustituye a otro | Ejecución | Los intentos del juez tras el primero |
     | `model.oversized` | Ejecutor que terminó bien con un modelo «máximo» del catálogo y un cambio de 3 ficheros y 50 líneas como mucho | Agente | Su entrada |
     | `cache.low-reuse` | Intento que siguió su sesión, con 10 000 tokens de entrada o más y menos del 30 % leído de caché | Intento | La entrada no leída de caché |
     | `work.discarded` | Intentos de tareas que acabaron fallidas o canceladas; todos los del ejecutor si la ejecución acabó fallida o cancelada | Ejecución | Su entrada |
     | `wait.queue` | Esperas en la cola del proveedor de 2 minutos o más | Ajuste `provider-concurrency:<programa>` | Sin tokens: el tiempo va en los argumentos |

     - El TRX no se guarda con los nombres de las pruebas: «las mismas pruebas en rojo» se aproxima con el mismo paso y los
       mismos contadores anclados en `EvidenceRegistered` v2.
     - El tamaño de un cambio se lee solo para los candidatos a `model.oversized`, del registro del intento (ficheros) y del
       diff (líneas), y solo si su contenido coincide con el SHA-256 que ancló el registro. Sin catálogo de modelos
       (`ConsumptionAnalysisOptions.Catalog`, el de WP2), ese código no dice nada.
     - Una ejecución que terminó bien y espera su entrega no descarta nada todavía.
     - Las frases y sugerencias salen de `Resources/ConsumptionStrings.resx` (castellano) y `.en.resx`: `Text_<código>` con los
       argumentos de `ConsumptionTexts`, y `Text_<código>_suggestion`. `work.discarded` solo informa.
  5. **El análisis con un agente** (D-35, decisión 5 (a)). `AssistedConsumptionAnalysisService`:
     - **Tu OK en cada uso:** un consentimiento dice quién y para qué, tiene 10 minutos como mucho y se gasta en un análisis. Sin
       él no se lee ni se lanza nada.
     - **Del vendor que no produjo:** el juez de la ejecución (o, si no, otro agente de su plantilla) de un vendor que no es el de
       ningún productor (planificador y ejecutores). Si no lo hay, no se lanza.
     - **Aparte y de solo lectura:** una auxiliar propia (`AgentRunPurpose.ConsumptionAnalysis`, por `IAuxiliaryAgentRunner`,
       que la registra: su consumo sale en «Consumo»), con el rol de auditor, sin herramientas, sin memoria, sin ficha, sin
       skills, en una carpeta vacía que se borra después y con una sesión que no se guarda.
     - **Recibe** el informe determinista, la tabla de uso de cada intento con el origen de cada cifra, sus partes y el tamaño
       de cada prompt, en un bloque de datos JSON. Nunca el texto de un prompt, de un informe, de un error ni de la memoria.
     - **Responde** con un esquema estricto. Cada sugerencia se vuelve a comprobar: destino de esta ejecución (un agente de su
       plantilla, una tarea o un intento suyos) o un ajuste; texto de 300 caracteres como mucho, sin caracteres de control ni
       bidireccionales; un arreglo tipado de cinco (`SetMemoryFix`, `RemoveSkillsFix`, `ChangeModelFix`,
       `RaiseContextBudgetFix` y `RaiseRunBudgetFix`) sobre un agente de la plantilla, o ninguno; nunca memoria para un juez o
       un auditor. El ahorro es «lo dice el agente». Se quedan 10 como mucho. Nada se aplica solo.
     - `AuxiliaryAgentRunner` admite los tres propósitos auxiliares de la Fase 13 (`OrderReview`, `ConsumptionAnalysis` y
       `PreparationChat`) y les escribe `AgentRunFinished` versión 3 con su clase de final; las auxiliares de antes siguen en la 2.
  6. **Fuera del juicio** (D-36). El juez no recibe el consumo, ni la ficha, ni la memoria, ni el índice: el empaquetador no le
     da la ficha y el dosier no cambia.
- **Alternativas:** el análisis dentro de la ejecución del juez (decisión 5 (b): más barato, pero cambia su veredicto y le da
  cifras que no juzga); solo el determinista (decisión 5 (c)); medir las partes con lo que sabe cada constructor de prompts (se
  deja como opción, `AgentInvocation.ContextParts`, pero medir el texto exacto no depende de que cada paquete lo haga); una
  tabla de umbrales en los ajustes (se aplaza: primero hay que ver los hallazgos en ejecuciones reales).
- **Consecuencias:**
  - Los arranques nuevos del motor escriben la versión 3; lo guardado se lee igual. El juez, los debates y las auxiliares
    registran su arranque fuera de `AgentRunner` y siguen sin partes.
  - La medición depende de las etiquetas de los bloques y del título `## Requisitos`. Un requisito escrito con una línea que
    empiece por `## ` partiría su sección; solo afecta al tamaño mostrado.
  - Para la ficha a agentes sin memoria, el motor (WP4) tiene que pedir el paquete también para ellos, o leer `IProjectBrief`.
  - Sin catálogo de modelos o sin evidencias legibles, `model.oversized` calla; sin arranques v3, `ctx.heavy-preamble` calla.
  - La barra en vivo por partes necesita las partes del intento en el monitor: hoy solo están en el registro
    (`ContextPartsLog`). Un evento de la ejecución con ellas es de la integración.
