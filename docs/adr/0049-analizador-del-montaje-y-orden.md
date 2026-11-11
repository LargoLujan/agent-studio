# ADR-0049 · El analizador del montaje y el orden: reglas deterministas con arreglos tipados; las notas de orden y «Revisar el orden» solo sugieren, y decide el validador

- **Estado:** aceptada · 2026-11-09. El P0 de la Fase 13 dejó el esqueleto (2026-11-06) y lo completa **WP1** (analizador, orden
  y chat con el orquestador). Recoge D-18 a D-21, D-28 a D-30 en lo que toca al orquestador y D-44 de
  [docs/17 §4.4, §4.7 y §4.10](../17-constructor-visual-de-agentes.md#44-análisis-en-vivo-y-orden-entre-agentes), y la
  decisión 3 (a) que aceptaste el 2026-11-06 con las demás recomendadas de su §12. **Amplía**
  [ADR-0013](0013-protocolo-del-orquestador.md) y [ADR-0031](0031-auditor-determinista-del-orquestador.md).
- **Contexto:**
  - P10 pide decidir cuántos agentes y que el orquestador compruebe el orden, lo dañino y lo que puede ir en paralelo.
  - Hoy `PlanValidator` valida en cada cambio (unos 40 códigos), sin arreglos ni explicación del orden, y no hay deshacer.
  - Las carpetas de un agente (`PathGrants`) no gobernaban nada (docs/17 §0.2).
- **Decisión:**
  1. **La regla `WriteScopeOutsideAgentArea` del validador** (D-11, F13-5). Las carpetas de un agente son las raíces de sus
     `PathGrant` de lectura y escritura, relativas al repositorio como `WriteScope` y `RepositoryNode.MainFolders`
     (`AgentWriteArea`, en `PlanValidator.cs`). Sin ninguna, el agente tiene todo el proyecto, como antes. Una raíz que
     `WriteScope.TryNormalize` rechaza (absoluta, con `..`, dentro de `.git`) dice que el agente tiene carpetas pero no cubre
     nada: falla cerrado. Una violación por entrada del alcance que no cabe, al final de las demás reglas, para que las
     violaciones de un plan de antes no cambien de orden. El prompt del planificador dice las carpetas de cada agente que las
     tiene; sin ellas no dice nada nuevo.
     - **Contrato de `PathGrant.Root`:** en la plantilla es relativa al repositorio (`src/`). `PathAccessPolicy` solo admite
       rutas absolutas y hoy nadie la construye con los permisos de un agente; quien lo haga resuelve antes cada raíz contra la
       copia en la que trabaja el agente (su worktree), nunca contra el directorio actual. Queda escrito en el tipo; no se
       cambia `PathAccessPolicy`.
  2. **El analizador del montaje** (D-18, F13-9) es `MontageAnalyzer`, en `Domain/Orchestration/Analysis/`, con una regla
     por fichero temático en `Analysis/Rules/` (`IMontageRule`). Es puro, determinista y gratis: el dominio no referencia
     nada, así que no puede lanzar un agente ni gastar cuota (lo comprueba una prueba sobre las referencias del ensamblado).
     - **Orden estable:** primero el validador envuelto y después las reglas en el orden de la tabla de D-18; dentro de cada
       una, el del plan y luego el del equipo.
     - **El validador envuelto, sin duplicar:** cada violación es un hallazgo `validator.<código>` (en minúsculas con guiones,
       el formato de `UserText`), error, sobre su tarea. Las tres que tienen código propio en D-18 (`JudgeVendorNotIndependent`,
       `OverlappingWriteScopes` y `WriteScopeOutsideAgentArea`) salen solo con ese código y su arreglo (`ValidatorRule.Mapped`).
     - **Cada código dice sus nodos y sus argumentos** en el comentario de su regla; es el contrato que pinta la banda. Los
       argumentos solo son ids, números, instantes y textos del sistema: el texto de un agente (la razón de una nota) nunca es
       argumento, se enseña aparte.

       | Código | Gravedad | Nodos | Argumentos | Arreglo |
       |---|---|---|---|---|
       | `delivery.no-integration` | Error | tareas que escriben | — | `AddIntegrationFix` |
       | `verification.test-without-build` | Error | tarea | tarea, paso de pruebas | `AddBuildStepFix` |
       | `team.judge-same-vendor` | Error | (revisión,) juez | juez, vendor | `SwitchJudgeFix` o `AddJudgeFix` |
       | `plan.parallel-write-overlap` | Error | las dos tareas | anterior, posterior | `AddDependencyFix` |
       | `team.write-outside-area` | Error | tarea, agente | tarea, agente, entrada | `ReassignTaskFix` o `WidenAgentFoldersFix` |
       | `team.parallel-capacity` | Aviso | ejecutores del CLI | cuántos, CLI, a la vez | — |
       | `context.pack-too-small` | Error | tarea, agente | tarea, caracteres obligatorios, presupuesto | — |
       | `context.window-tight` | Aviso | tarea, agente | tarea, tokens previos, ventana, % | `SetMemoryFix` (quitar) |
       | `context.thin` | Sugerencia | tarea, agente | tarea | `SetMemoryFix` (activar) |
       | `model.oversized` | Sugerencia | tarea, agente | tarea, agente, modelo | `ReassignTaskFix` |
       | `model.undersized` | Sugerencia | agente | agente, modelo, reintentos, rechazos | `ChangeModelFix` |
       | `cost.over-budget` | Aviso | — | ejecuciones estimadas, presupuesto | `RaiseRunBudgetFix` |
       | `quota.window-near` | Aviso | agentes del CLI | CLI, ventana, %, (reinicio) | — |
       | `order.note-unapplied` | Aviso | to, from | to, from | `AddDependencyFix` |
       | `order.harmful` | Aviso | first, then | first, then | `AddDependencyFix` |
       | `capability.high-risk` | Aviso | agente | agente, herramienta o CLI | `RemoveCapabilityFix` (con herramienta) |

     - **Lo que decide cada regla, cuando D-18 no lo fija:**
       - `team.judge-same-vendor` con plan sale de la violación del validador; sin plan, mientras montas, solo cuando ningún
         juez es de otra empresa que el orquestador. Los ejecutores se miran con el plan: un equipo puede tener un ejecutor de
         Codex para planes sin revisión, y la plantilla por defecto tiene `juez-claude` para los debates.
       - `verification.test-without-build` necesita el perfil (`MontageDraft.Steps`, ver el punto 7): un paso de pruebas está
         compilado si el paso lo requiere (`Requires`, lo que arregló WP-D para B6) o si la tarea nombra antes uno que compila.
         Sin el perfil, o con un perfil sin paso de compilar (pytest), no dice nada: callar es mejor que un error falso.
       - `context.pack-too-small` no ofrece `RaiseContextBudgetFix`: el presupuesto es el del prompt del ejecutor
         (`ExecutorPrompt.BudgetCharacters`), no un campo de la plantilla. La salida es partir la tarea o reducir sus requisitos.
       - `context.window-tight` solo ofrece quitar la memoria: el catálogo no guarda ventanas, así que «un modelo con más
         ventana» no se puede proponer sin inventar.
       - `model.oversized` mira si todas las entradas del alcance son de documentación o pruebas (`docs/`, `tests/`, carpetas
         que acaban en `Tests`, ficheros `.md`…). `model.undersized` exige al menos tres tareas del modelo (lo conocido).
       - `quota.window-near` usa el umbral de aviso de las barras (70 %) y solo los CLIs del equipo; sin hora de reinicio usa
         la frase `quota.window-near-no-reset` (`MontageTexts.SentenceOf`).
       - `capability.high-risk`: `Bash`, `WebFetch` o `WebSearch` en un agente que escribe, o un ejecutor de Codex que escribe;
         con plan, solo los agentes que tienen tareas en él.
       - Las notas `parallelOk` no generan hallazgos: un plan que ordena lo que podía ir a la vez solo es más lento.
  3. **Cada arreglo es una edición que se deshace** (`FixApplier` y `EditHistory<T>`, en `Analysis/Rules/FixApplier.cs`).
     - El arreglo se aplica sobre un estado inmutable (`MontageEditState`: plan, plantilla y presupuesto) y da otro; deshacer
       es volver al anterior (Ctrl+Z y Ctrl+Y, 100 pasos). El historial es genérico: sirve también para las ediciones a mano
       del lienzo.
     - Un arreglo que no se puede aplicar no cambia nada y dice por qué con un código (`FixRefusals`).
     - **Las dependencias las decide `TaskGraph`:** un `AddDependencyFix` solo se ofrece si `TaskGraph.AddDependency` lo admite
       sobre el plan (sin ciclo, sin depender de una condición, que pide rama) y el plan no lo cumplía ya.
     - **«Añadir Integración»** la pone detrás de las tareas que escriben y que ninguna otra que escribe tiene detrás, la
       verifica con los pasos de pruebas del perfil (y la compilación delante si no la requieren), y hace que cada revisión
       que va detrás de una tarea que escribe espere también a la integración: así el juez revisa lo integrado y la ejecución
       se puede entregar (B10). Una revisión que va antes de lo que la integración une (W1 → R1 → W2) no la espera: sería un
       ciclo. Su título es un texto de la interfaz y lo pasa quien la aplica (`FixEnvironment`).
     - **La red de todos los arreglos:** uno que dejaría sin un grafo sano un plan que lo tenía (un ciclo, un id repetido, una
       dependencia desconocida: el criterio del validador) no se aplica (`fix.refused.breaks-graph`), y el analizador solo
       ofrece los arreglos que se aplican sobre el borrador tal como está.
     - **«Añadir juez»** copia la plantilla del primer juez del equipo (o la que le pasen) con un id nuevo (`juez-2`), el CLI y
       el vendor del arreglo, sin modelo, sin memoria y sin carpetas. Con plan hace falta un segundo clic, «Revisar R1 con
       juez-2», que el analizador ofrece en cuanto existe.
     - Avisar de que un arreglo retira la aprobación (`ChangesRoster`) o pedir Windows Hello (`GrantsHighRiskCapability`) lo
       hace quien lo aplica, antes (WP6 y WP5).
  4. **Los textos** de los hallazgos, de los botones de los arreglos y de los motivos están en
     `Desktop/Resources/MontageStrings.resx` y su satélite inglés, uno por código (`Text_<código>`); `MontageTexts` da el
     código y los argumentos de cada uno. Una prueba comprueba que cada hallazgo y cada botón tiene su texto en los dos idiomas
     con exactamente sus argumentos y que se formatea con ellos.
  5. **Las notas de orden** (D-19, F13-10) van dentro del plan, sin ejecución extra: el esquema gana `orderNotes`
     (`{dependencies:[{from,to,reason}], parallelOk:[{a,b,reason}], harmful:[{first,then,reason}]}`), obligatorio como todo en
     modo estricto, con los ids como texto porque las tareas nacen en la misma respuesta.
     - **El parseo es tan estricto como el del plan** (sin nulos, sin propiedades de más), pero una respuesta sin `orderNotes`
       ni `questions` sigue siendo un plan, sin notas: así se leen las respuestas de antes de la fase y los planes de prueba.
       Un `null` explícito es un error.
     - **El sistema guarda solo las notas sobre dos tareas distintas del plan**, con una razón de una frase (300 caracteres),
       sin caracteres de control ni de dirección, una por par y 40 de cada clase como mucho (`OrderNotesCheck`); de lo demás
       dice por qué en inglés. Las notas no entran en el hash del plan: van en la propuesta (`PlanProposal.OrderNotes`) y en la
       respuesta guardada como evidencia.
     - El analizador las compara con el plan editado (`order.note-unapplied`, `order.harmful`).
  6. **Las preguntas del orquestador antes de planificar** (D-28, decisión 4 (a), «quién pregunta»).
     - **El esquema del plan gana `questions`**, con la forma de la pregunta del informe (texto, por qué, opciones con id,
       etiqueta, detalle, esfuerzo `small`/`medium`/`large` y recomendada, y `allowOther`). Preguntas y tareas a la vez es una
       respuesta inválida; tres como mucho; cada una pasa `QuestionRules`. Una pregunta inválida se corrige como un plan
       inválido, en la única corrección.
     - **Con preguntas no hay plan:** cada una es evidencia informada por el agente y un `QuestionAsked` sin tarea, con id
       `plan-1`… y sin estimaciones (seguir es planificar otra vez, no una tarea); la propuesta las lleva
       (`PlanProposal.Questions`).
     - **Con las respuestas** (`PlanningRequest.Answers`), la planificación sigue en la misma ejecución y en el mismo `_base`
       (en su commit), anota cada `QuestionAnswered` (el texto propio, como evidencia aportada por la persona) antes de lanzar,
       y da al orquestador las preguntas y las respuestas como datos (`<human_answer question="plan-1">`). Va en una sesión
       nueva con el prompt entero, no reanudando la que preguntó: el prompt ya lo tiene todo y no hace falta guardar la sesión.
       Ya no puede preguntar; si lo hace, es un problema para la corrección.
     - **Se rechazan antes de lanzar nada** las respuestas de varias ejecuciones, a preguntas que no se hicieron, con una
       opción que no existe, con texto propio si la pregunta no lo admite, o «con la recomendada»: una pregunta del orquestador
       siempre la contesta una persona.
     - **Todas a la vez:** las respuestas contestan todas las preguntas de la ejecución, sin respuestas parciales; así nunca
       hay una segunda planificación que pueda volver a preguntar en la misma ejecución. Si la planificación con ellas falla
       después de anotarlas, se reintenta con las mismas sin anotarlas otra vez; con otras, se rechaza.
     - **El `_base` de la continuación** es siempre el del commit desde el que se preguntó, el primer `_base` de la ejecución
       en el registro, que es el que toma `Orchestrator.BaseCommitAsync` para construir la ejecución. Si la copia se borró, se
       vuelve a crear en ese commit (y se anota otro `WorktreeCreated` con la misma base), nunca en un HEAD más nuevo: el plan
       se hace sobre el mismo código sobre el que luego se trabaja.
  7. **El contrato que WP1 añadió al P0** (aditivo, sin cambiar ningún hash ni payload): `MontageDraft.Steps`
     (`VerificationStepFacts`: id, compila, ejecuta pruebas, requiere), con lista vacía por defecto, y
     `PlanProposal.OrderNotes`, nulo por defecto.
  8. **«Revisar el orden»** (D-20, F13-10) es `OrderReviewService` (`IOrderReview`, `OrderReviewContracts.cs`).
     - Una auxiliar del orquestador con el propósito `OrderReview` (`IAuxiliaryAgentRunner`, que ahora admite los tres
       propósitos de la Fase 13), de solo lectura (Read, Grep y Glob, sin escritura, shell ni red) en el `_base` de la
       planificación, que se adopta si existe o se crea en HEAD si se borró. La ejecución tiene que ser una planificación del
       proyecto, con su `PlanCreated` en el registro verificado: nunca se lee la carpeta que nombre una petición cualquiera.
     - **Tu OK en cada uso:** un `QuotaConsent` reciente (10 minutos) que se gasta una vez, como el de la recomendación de
       skills; sin él no se lee ni se lanza nada. La estimación la pide la pantalla al estimador con el propósito `OrderReview`.
     - **Un solo registro de OK gastados para toda la app:** `IQuotaConsentLedger` y su implementación `QuotaConsentLedger`
       (`Application/Providers/QuotaConsentLedger.cs`), con un único método atómico, `TrySpend`, que da falso si el OK ya se
       gastó. Lo usan «Revisar el orden», el chat de preparación y la recomendación de skills (y el análisis del consumo con un
       agente, en la integración), así que un OK dado para un uso no sirve para otro servicio. La composición registra una
       sola instancia. **Vive en memoria:** tras reiniciar la app solo protege la caducidad del OK, que es de 10 minutos desde
       que lo das en los tres servicios (`ConsentValidity`).
     - El plan y el equipo van como datos; el esquema estricto lleva los ids de las tareas como enum.
     - **Nada se aplica solo:** se guardan las notas que pasan `OrderNotesCheck` y cada una sale como sugerencia comprobada
       (`OrderSuggestion`): si el plan ya la cumple y el arreglo que la cumpliría, decidido por `TaskGraph`. La respuesta queda
       como evidencia y la ejecución, en «Consumo».
  9. **El chat de preparación** (D-44, la parte del chat de F13-18) es `PreparationChatService` (`IPreparationChat`,
     `PreparationChatContracts.cs`).
     - Cada mensaje es una auxiliar `PreparationChat`, de solo lectura, con su OK. Una conversación tiene su copia de solo
       lectura, el `_base` de una ejecución propia `chat-aaaammdd-xxxxxx` (que ninguna ejecución ni auxiliar puede tener), y
       reanuda la sesión del CLI mientras dura; `EndAsync` borra esa copia. Una sesión que nombra otra copia que no empieza por
       `chat-` se rechaza, al enviar y al cerrar.
     - **Lo que escribes pasa por `ISecretRedactor` antes de salir** (lo que envías llega al vendor del agente, el residual de
       docs/17 §8.3).
     - La respuesta tiene un esquema estricto: texto (1 200 caracteres), borradores de requisitos, sugerencias para el equipo
       de una lista cerrada (`changeModel`, `memoryOn`, `memoryOff`, `addJudge`, `removeCapability`) con los agentes como enum,
       riesgos y preguntas, ocho de cada como mucho. El sistema quita los caracteres de control y de dirección, recorta, y
       convierte cada sugerencia en un arreglo tipado solo si encaja con la plantilla (el agente existe, tiene esa
       herramienta, hay una empresa que no produce para el juez); lo demás se descarta diciendo por qué. Los requisitos los
       copias tú al editor y los congelas tú.
  10. **El analizador no sustituye a nadie** (D-21): el validador decide si se puede aprobar; el auditor revisa lo que pasó, y
      el juez, el trabajo.
- **Alternativas:**
  - Las de la decisión 3: sin notas en el esquema (el analizador y el botón), o solo el analizador determinista.
  - **Duplicar las reglas del validador en el analizador:** dos sitios que podrían discrepar. Se envuelven y las tres con
    código de D-18 cambian de código, no de regla.
  - **Exigir `orderNotes` y `questions` en el parseo:** rompería la lectura de las respuestas guardadas y todos los planes de
    prueba de otros paquetes, sin ganar nada (el esquema ya las exige al CLI).
  - **Reanudar la sesión del orquestador que preguntó:** más barato en caché, pero obliga a guardar la sesión entre dos
    planificaciones; el prompt nuevo ya lleva todo.
  - **Contestar las preguntas del orquestador con la recomendada pasado un plazo:** es un consentimiento por ejecución, para
    las tareas; antes de planificar no hay ejecución que lo dé.
  - **Un deshacer por arreglo con su inversa:** cada tipo necesitaría la suya; volver al estado anterior sirve para todos.
  - **El juez comprobado contra todos los ejecutores del equipo sin plan:** avisaría siempre en equipos con ejecutores de los
    dos vendors.
- **Consecuencias:**
  - **El tamaño del esquema del plan, medido** (`OrderNotesInPlanTests`): con el equipo más grande (un orquestador, 8
    ejecutores, 3 jueces y los 2 participantes de los debates), 20 requisitos y 6 pasos, ocupa 4 467 bytes, de los que
    `orderNotes` y `questions` son 1 718; con 40 ejecutores, 80 requisitos y 20 pasos, 5 403. El límite es 16 384: los 8
    ejecutores de D-09 caben con mucho margen, y el límite real lo ponen los requisitos (unos 2 000 con ids de 6 caracteres).
  - **El coste del analizador:** recorre el plan (20 tareas como mucho) y el equipo un número fijo de veces; las reglas que
    miran el orden calculan los antecesores una vez y el validador se ejecuta una vez por borrador (`DraftViolations`). Sus
    pruebas analizan cada caso varias veces en milisegundos; no hace falta medirlo aparte frente a los 200 ms de D-16.
  - **Pruebas:** `MontageAnalyzerTests` (un caso por código de D-18, con nodos, argumentos y arreglo; cada arreglo aplicado
    quita su hallazgo y se deshace; los textos en los dos idiomas), `AgentWriteAreaTests`, el caso nuevo de
    `PlanValidatorTests`, `OrderNotesInPlanTests`, `PlannerQuestionsTests`, `OrderReviewServiceTests` y
    `PreparationChatServiceTests`, todas con agentes guionizados.
  - **Pendiente de otros paquetes y de la integración:**
    - WP6 pinta la banda, los nodos y los botones con `MontageAnalyzer`, `MontageTexts` y `FixApplier`, analiza fuera del hilo
      de la interfaz 200 ms después de la última edición con la generación del borrador (D-16), rellena `MontageDraft.Steps`
      con el perfil y pone el texto de `validator.write-scope-outside-agent-area` (hoy sale el genérico de `PlanViolationText`).
    - WP6 y WP8 piden la estimación de cada auxiliar al estimador antes del OK, y ofrecen «Revisar el orden» cuando las notas
      quedan viejas.
    - El lead registra `IOrderReview`, `IPreparationChat` e `IQuotaConsentLedger` como únicos en la composición (los
      contratos están en docs/04 §21.7).
    - La pregunta del orquestador se guarda como evidencia sin pasar por `ISecretRedactor` (el motor no lo tiene en sus
      puertos); su texto ya estaba entero en la respuesta guardada del plan.

## Adenda · 2026-11-11 · de dónde salen ahora los datos del borrador (P49)

Hasta hoy `DraftHost.BuildDraft` pasaba `TaskContexts` vacío y `EstimatedAgentRuns` nulo (ADR-0048: «ninguno inventa un dato»),
así que `context.pack-too-small`, `context.window-tight`, `context.thin` y `cost.over-budget` no podían avisar, y el inspector
recomendaba un modelo sin conocer el tamaño del proyecto. **No cambia ninguna regla, ningún umbral ni el contrato de
`MontageDraft`**: cambia de dónde sale el dato. Ninguna parte lanza un CLI ni gasta cuota.

1. **El contexto de cada tarea** (`TaskContextFacts`) sale de la misma cuenta que hace el motor. `ExecutorContextPreview`
   (`Orchestration/Engine/`) es una fachada pública sobre lo que es interno del motor: construye el prompt del primer intento con
   `ExecutorPrompt.Build` y lo mide con `PromptParts.Measure`, en las mismas partes que graba `AgentRunStarted`. Los tokens son
   caracteres entre 4, redondeado hacia arriba (docs/14 D-24), como estima el empaquetador.
   - **Qué tareas:** las de trabajo de agente cuyo agente está en la plantilla. Las revisiones no tienen hechos (el juez recibe un
     expediente que se construye al revisar, y nunca memoria ni ficha), y las reglas callan para ellas.
   - **`PreambleTokens` es un suelo, no una cifra inventada:** cuenta el protocolo, el objetivo, los requisitos que cubre, las
     instrucciones del agente, la ficha, la memoria y las skills. No cuenta lo que no existe antes de ejecutar: las salidas de las
     tareas anteriores (`HasUpstream` sale del grafo: hay un antecesor de trabajo de agente), los intentos anteriores y las
     respuestas de la persona, ni la memoria que depende de lo que trate la tarea (el empaquetador se pregunta sin foco, así que
     solo entran los niveles que no dependen de él). Así `context.window-tight` nunca avisa de más.
   - **El presupuesto** es el de `ProjectMemoryPrompt.BudgetCharacters` (24 000 caracteres) solo para el agente que lee memoria
     (`ReadSelected` y no juez ni auditor). Para los demás es `int.MaxValue`: el motor solo comprueba el presupuesto cuando pide un
     pack, así que con otra política la tarea arranca por largos que sean sus requisitos, y `context.pack-too-small` (un error, que
     impide aprobar) no puede dar por muerto un plan que sí se ejecutaría.
   - **La ventana** es la que el registro enseñó de ese modelo (`LearnedModels.Find(…).ContextWindow`) y, si no enseñó ninguna, la
     que usa el monitor (`ModelWindowCatalog.WindowFor`: el registro y, debajo, la tabla base), con su origen. Codex es «no
     expuesto» y `context.window-tight` calla.
   - **La ficha** es la vigente de `ProjectBriefViewModel` (`HasBrief` si existe y el agente no es juez ni auditor).
2. **Qué se guarda y dónde.** `BuildDraft` corre en el hilo de la interfaz en cada edición y **solo lee** lo guardado en
   `VisualBuilder.Facts` (`MontageFactsCache`, `Desktop/Orchestrator/Preparation/`). El cálculo del tamaño es rápido y puro y se hace
   en cada lectura (escribir el título o el objetivo de una tarea cambia su tamaño al momento); lo que cuesta se pregunta una vez, en
   segundo plano, y al llegar la banda vuelve a analizar:
   - **La memoria:** el pack de `ProjectMemoryPrompt.PackAsync` por el `IContextPacker` (verifica el registro), una vez por agente,
     rol, política, hash de los requisitos y requisitos que cubre la tarea. Editar el texto de una tarea no lo cambia, así que
     nunca lee el registro al teclear. Si falla (los requisitos no caben, el registro no verifica, el almacén falla), la tarea se mide
     sin memoria y no se vuelve a preguntar.
   - **Las skills:** `SkillPresentationSource` decide cuáles están activas para el agente como el aprovisionador
     (`SkillActivationResolver`: agente sobre proyecto sobre global, apagada salvo que alguien la encienda, y solo con copia local) y
     mide el frontmatter de su `SKILL.md` como el motor mide el materializado (`name` y `description`), sin copiar nada. Una vez por
     agente.
   - **Lo guardado se olvida al releer el contexto del documento** (`Reset`: al abrir y con «Recargar»), así que una memoria o una
     activación cambiadas mientras el documento está abierto se ven después. Un `Reset` con una respuesta en camino la descarta (una
     época), sin cerrojos: todo se escribe en el contexto de la interfaz desde donde se preguntó.
   - La banda vuelve a analizar también al final de `LoadBuilderDataAsync` (lo aprendido, la ficha y la cuota llegaban después del
     primer análisis y no lo repetía nadie) y al leer o guardar la ficha.
3. **Las ejecuciones del plan** (`MontageLearnedFacts.EstimatedAgentRuns`) salen de `IUsageEstimator.EstimatePlanAsync`, que lee el
   registro entero, así que se pregunta una vez por **forma del plan** (sus tareas con su clase y su agente, y el CLI y el modelo de
   cada agente de la plantilla), no por letra tecleada ni por cambio del presupuesto. Hasta que llega la respuesta de la forma
   actual no hay cifra: nunca se dice la de un plan anterior. Es el **extremo alto** (el percentil 75 sumado, `AgentRuns.High`): el
   arreglo `RaiseRunBudgetFix` sube el presupuesto a esa cifra, y con el extremo bajo seguiría pasándose. Es nulo cuando alguna tarea
   tiene menos de tres referencias, como ya devuelve el estimador. El extremo bajo avisaría menos; si el aviso resultara ruidoso con
   datos reales, es una decisión de esta cifra, no de la regla.
4. **El tamaño del proyecto** es `ProjectFileCounter` (`Desktop/Orchestrator/Team/`): recorre la carpeta una vez al abrir el
   proyecto (`VisualBuilder.LoadProjectFilesAsync`, fuera del hilo de la interfaz), oculta lo que oculta el explorador
   (`ExplorerFilter`: `.git`, `bin`, `obj`, `node_modules`, `.vs`) y además `.agentstudio`, no sigue enlaces y **para en 20 000
   ficheros** (el tope del índice del explorador), diciendo que la cuenta es un suelo (`IsTruncated`). Lo guarda
   `VisualBuilder.ProjectFiles` mientras dure el documento y llega a `InspectorContext.ProjectFiles` y de ahí a
   `ModelRecommendationInput.ProjectFiles` (el orquestador «máximo» pasados 2 000 ficheros). `MontageDraft` no gana ningún campo:
   ninguna regla del analizador usa el tamaño. Es inyectable (`BuilderServices.ProjectFiles`), así que las pruebas no leen el
   disco.
5. **Servicios nuevos de `BuilderServices`**, al final y opcionales: `ContextPacker`, `ProjectFiles` y `SkillCharacters`. Sin uno,
   lo que lo necesita calla: sin empaquetador no se cuenta la memoria, sin estimador no hay ejecuciones y sin contador no hay tamaño.
- **Lo que se espera ver:** con datos reales aparecen hallazgos que antes no podían salir en la banda. `context.thin` (sugerencia)
  en toda tarea que escribe con un agente sin memoria, sin ficha y sin nada antes; `cost.over-budget` (aviso) cuando el plan pasa
  del presupuesto; `context.window-tight` (aviso) y `context.pack-too-small` (error) solo con los datos del modelo y de los
  requisitos delante.
- **Pruebas:** `MontageFactsCacheTests` (las dos tareas con sus hechos, la memoria en segundo plano, una sola pregunta por clave y
  la época, las skills, las ejecuciones con un estimador falso y con el real, y las cuatro reglas sobre un borrador hecho así),
  `DraftFactsDocumentTests` (lo mismo por el documento, con el arreglo de `cost.over-budget` y el tamaño en el inspector),
  `ExecutorContextPreviewTests`, `SkillPresentationSourceTests` y `ProjectFileCounterTests` (carpetas temporales: tope y carpetas
  excluidas).
- **Pendiente:** las revisiones no tienen hechos; las salidas de las tareas anteriores no cuentan hasta que existen; la memoria que
  depende del foco tampoco.
