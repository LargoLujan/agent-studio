# ADR-0050 · Modelos y potencia: un catálogo local sin precios, lo aprendido del registro, el esfuerzo en la cabecera y una recomendación determinista que dice por qué

- **Estado:** propuesto · 2026-11-06. Esqueleto del P0 de la Fase 13; lo completa **WP2** (modelos) el 2026-11-08. Recoge D-22
  a D-25 de [docs/17 §4.5](../17-constructor-visual-de-agentes.md#45-modelos-y-potencia) y el criterio F13-11 (§9.3).
- **Contexto:**
  - P7 pide elegir el modelo y la potencia de cada agente, con una recomendación según el proyecto.
  - Hoy el modelo es texto libre (`CliArgumentGuard`), no hay esfuerzo ni catálogo, y las ventanas aprendidas del registro no
    se usan en vivo (docs/17 §0.2). No hay tabla de precios, y no la habrá (docs/14 D-06, ADR-0019).
  - Lo que admite cada CLI sale de su ayuda local: `claude --help` (2.1.290) y `codex exec --help` (0.160.0), consultadas
    el 2026-11-08. Ninguna sonda lanza un modelo: ni este paquete ni sus pruebas gastan cuota.
- **Decisión:**
  1. **La potencia es nivel, esfuerzo y ventana, cada uno con su origen** (D-22). El nivel se dice con palabras
     (`ModelTier`: ligero, equilibrado, máximo; P0), y un modelo que el catálogo no conoce sale «sin clasificar».
  2. **El catálogo local** (D-23) es `models.catalog.json`, versionado con la app, por CLI y versión probada, sin precios.
     - **Dónde:** `Infrastructure/Models/ModelCatalogData.cs`, como texto JSON dentro de una cadena de C#. Los `*.csproj` son del
       P0 (docs/17 §9.1), y un recurso incrustado pediría tocar el de Infrastructure. Pasarlo a un `models.catalog.json`
       incrustado es una línea de ese proyecto, si la integración lo prefiere.
     - **Formato, versión 1:** `version`, `checkedOn`, `sources` (identificador, qué es, dónde y cuándo se consultó), `rules`
       (las de D-24, con los esfuerzos por su nombre) y `models` (CLI, versión probada, nombres, nivel, esfuerzos, nota y
       fuentes). No admite ningún otro campo, así que un precio no puede colarse.
     - **La nota es un código** (`model.note.*`), no una frase: la pinta el escritorio en tu idioma (ADR-0054).
     - **El lector** (`ModelCatalogReader`) comprueba la forma, las reglas del dominio (`ModelCatalog.Validate`) y que cada
       entrada nombre fuentes conocidas. Entre las reglas del dominio está que una entrada solo ofrezca niveles de su CLI
       (`model-catalog.effort-not-for-cli`, en `ModelCatalogEntry.Validate`): si no, el selector ofrecería uno que la
       plantilla rechazaría después (`agent.effort-not-for-provider`). Falla cerrado: con cualquier problema da un
       catálogo vacío y la lista de códigos (`model-catalog.*`), nunca una excepción ni medio catálogo. Sin catálogo, todo
       sale «sin clasificar» y nada se rompe.
     - **El orden es la preferencia:** dentro de un nivel, la recomendación elige la primera entrada de su CLI. Así «máximo»
       es Opus antes que Fable sin añadir un campo al tipo del P0.
     - **Lo que contiene hoy** (Claude Code 2.1.290 y Codex 0.160.0):

       | CLI | Nivel | Entradas, en su orden | Esfuerzos | Fuentes |
       |---|---|---|---|---|
       | Claude Code | máximo | `claude-opus-5-5` (`opus`), `claude-fable-5-1` (`fable`), `claude-opus-5`, `claude-fable-5`, `claude-opus-4-8`, `claude-opus-4-7`, `claude-opus-4-6` | de `low` a `max`; Opus 4.6 sin `xhigh` | `claude --help` y el catálogo de modelos de Anthropic (referencia de la API, 2026-10-06) |
       | Claude Code | equilibrado | `claude-sonnet-5-5` (`sonnet`), `claude-sonnet-5`, `claude-sonnet-4-6` | de `low` a `max`; Sonnet 4.6 sin `xhigh` | Ídem |
       | Claude Code | ligero | `claude-haiku-5-5`, `haiku`, `claude-haiku-4-5` | Haiku 5.5, de `low` a `max`; `haiku` y Haiku 4.5, ninguno | Ídem |
       | Codex | máximo | `gpt-6-astra` | de `low` a `xhigh` | `codex exec --help`, la página de modelos de OpenAI y su configuración de muestra (2026-11-08) |
       | Codex | equilibrado | `gpt-6.1-sol` | Ídem | Ídem |
       | Codex | ligero | `gpt-6-luna`, `gpt-6.1-terra` | Ídem | Ídem, y las ejecuciones reales de tu cuenta para Luna |

     - **Lo que no clasifica:** `opusplan` (mezcla dos modelos) y Mythos 5.1 (solo para Project Glasswing). El alias `haiku`
       es una entrada propia sin esfuerzos: no está comprobado a qué modelo lleva en esta versión, y Haiku 4.5 no admite
       esfuerzo.
     - **Sin red:** el catálogo nunca se descarga. La lista de modelos de Codex por su app-server (`model/list`) sigue
       aplazada hasta medirla.
  3. **Lo aprendido del registro** (D-23) es una proyección del registro verificado (`Observability/Models/LearnedModelsProjection`,
     servida por `ProjectModelLearning`): nada se guarda, y el mismo registro enseña siempre lo mismo. Un registro que no
     verifica, o que ningún ancla reconoce, no enseña nada. Por cada modelo:
     - **Su nombre:** el de su entrada del catálogo cuando lo clasifica (un alias y su nombre completo, con o sin la fecha que
       añade el CLI, son un modelo); si no, el que informó el CLI, sin la fecha. El modelo de un intento es el que informó el
       CLI (`ReportedModel`) o, si no lo hizo, el configurado; un intento sin ninguno no enseña nada.
     - **Una sola regla de qué nombres son el mismo modelo** (`Domain/Models/ModelKeys.Of`, revisión del 2026-11-09): la usan
       el aprendizaje, la búsqueda (`LearnedModels.Find`, que guarda el catálogo con el que aprendió) y el estimador. Así, lo
       aprendido con el nombre largo se encuentra por el alias y al revés, con o sin fecha y sin distinguir mayúsculas. Se
       descartó apuntar además el nombre configurado en `ReportedAs`: no cubre un alias que el registro nunca vio, y habría
       dejado dos reglas.
     - **Sus tareas:** las de los ejecutores (`AgentRunPurpose.Executor`; antes de la versión 2 del arranque, un intento con
       tarea cuyo agente no sea el orquestador, un juez ni un auditor del equipo de su ejecución) que terminaron
       (`Completed` o `Failed`). Una tarea es del modelo de su primer intento.
     - **Aprobada a la primera:** terminada con un solo intento en una ejecución cuyo último veredicto válido es `Approved`.
       **Reintentos:** los intentos de cada tarea después del primero. **Rechazos:** sus tareas en una ejecución cuyo último
       veredicto válido es `Rejected` o `ChangesRequested`. Todo, estimado por Agent Studio; con menos de tres tareas,
       «desconocido» (`ModelPerformance.IsKnown`, P0).
     - **La ventana:** la última que informó el CLI (`ModelWindowCatalog`), como estimación para lo que venga; falta si nunca
       la informó.
     - **El coste por tarea:** la suma de sus intentos a precio de lista del CLI (`UsageProjector`, sin contar dos veces una
       sesión), y del modelo, la mediana de sus tareas con coste, desde tres. Codex no lo expone: «no expuesto», nunca cero.
  4. **El estimador de F9-7** (docs/16 D-20, adelantado) lo implementa WP2 (`Observability/Estimates/UsageEstimator` sobre
     `UsageEstimation`, puro; reasignado por el coordinador el 2026-11-09, porque docs/04 §21.4 lo daba a WP3 y nadie lo
     había hecho). Lee el registro verificado por `IProjectLogReader` y no gasta cuota. Un registro que no verifica, o que
     ningún ancla reconoce, falla cerrado: todo «desconocido», sin referencias.
     - **Las referencias de un intento:** los intentos del mismo CLI y propósito, con el mismo modelo (el alias y el nombre
       completo, con o sin fecha, son uno; el modelo por defecto casa con los intentos configurados sin modelo), que
       terminaron con resultado. Primero, los del mismo agente («medido en el proyecto»: `SameAgentAndModel`); con menos de
       tres, los de cualquier agente («aprendido del modelo»: `SameModel`). Con menos de tres también, «desconocida». No hay
       tabla base de consumo y no se inventa: la tabla base de la tanda 2 es de ventanas, no de tokens.
     - **El tamaño** (D-32): las referencias se parten en tercios por sus tokens (entrada y salida), y solo cuenta el tercio
       del tamaño pedido (`SameSize`).
     - **El intervalo:** los percentiles 25 y 75 por rango más cercano, así que cada extremo es una cifra que tuvo algún
       intento. Cada cifra va por su cuenta: la que tienen menos de tres referencias es «desconocida», y el coste de Codex,
       «no expuesto» (`CostNotExposed`); un coste conocido es el precio de lista del CLI (`ListPrice`).
     - **Los tokens totales** (`UsageEstimate.TotalTokens`, a petición de WP4): la entrada más la salida de cada referencia,
       como las cuenta el panel de uso (la entrada es el prompt entero: en Claude, con la caché leída y escrita; en Codex,
       con la entrada en caché; la salida de Codex incluye su razonamiento). Sus percentiles se calculan sobre esos totales, nunca la suma de los percentiles de la entrada y la salida, que no sería ninguno.
       `UsageEstimate.ToSnapshot(opción)` da el `OptionEstimateSnapshot` de una pregunta.
     - **Un plan:** cada tarea de trabajo (ejecutor) o de revisión (juez) se estima como un intento, y sus ejecuciones son los
       percentiles 25 y 75 de las que llevaron las tareas parecidas, con sus reintentos. Las ejecuciones y los tokens del
       plan suman los extremos bajos y los altos (un intervalo, no un percentil de la suma). La duración sigue el grafo con
       las tareas a la vez y la concurrencia de cada CLI (2 por defecto), y cuenta todas las ramas de una condición
       (`CriticalPath`); las comprobaciones, las condiciones y tus aprobaciones no suman (`AgentsOnly`). Una tarea sin tres
       referencias deja la cifra del plan «desconocida» y dice cuál.
     - **La ventana aprendida** no entra en `UsageEstimate`, que no tiene ese campo: está en lo aprendido (`LearnedModel.ContextWindow`).
     - **La comparación de opciones** de D-24 lo usa por su puerto (`ModelOptionEstimates.CompareAsync`: una estimación por
       opción, con solo el modelo cambiado).
  5. **La recomendación es determinista y dice por qué** (D-24): `Domain/Models/ModelRecommender`, puro y sin cuota, sobre las
     entradas y el resultado del P0.
     - **Candidatos:** las entradas del CLI del agente que admiten algún esfuerzo. Un nivel sin candidatos pasa al más
       cercano, primero el de arriba. Sin ningún candidato, no hay recomendación.
     - **Orquestador:** «máximo» con cinco requisitos obligatorios o más, o con más de 2 000 ficheros; si no, «equilibrado».
       Esfuerzo alto.
     - **Juez y auditor:** el nivel más alto de su CLI, con esfuerzo alto. Que su CLI sea de otra empresa que la de quien
       produjo es la regla del equipo (ADR-0014; el hallazgo `team.judge-same-vendor`), no de la recomendación, que nunca
       mueve a un agente de CLI.
     - **Los demás:** «equilibrado», con esfuerzo medio, salvo que mande el historial.
     - **El historial manda** cuando hay datos: entre los modelos conocidos del mismo CLI que el catálogo clasifica, el mejor
       (más aprobadas a la primera por tarea, y después menos rechazos, menos reintentos, más tareas y el orden del catálogo)
       se recomienda si aprobó a la primera más de la mitad de sus tareas. No cambia al orquestador ni al juez: cuenta tareas,
       y solo las hacen los que producen.
     - **El esfuerzo:** el de la regla si el modelo lo admite; si no, el más cercano que admita, el de abajo en un empate.
     - **El segundo ejecutor «ligero»** se recomienda solo si tus planes suelen tener tareas de documentación o de pruebas.
     - **Aplicar lo que ya se cumple no cambia nada** (`ModelRecommender.IsFollowedBy`): un alias y su nombre completo son el
       mismo modelo, así que la plantilla y su hash no cambian por recomendarle lo que ya tiene.
  6. **El esfuerzo va en la cabecera y llega a cada CLI de una lista cerrada** (D-25).
     - `AgentDefinition.Effort` (P0) entra en `RosterHash` y en el JSON de la plantilla solo cuando se fija: el hash de las
       plantillas guardadas no cambia (`SavedTemplateHashTests`, con un hash calculado por una implementación independiente).
     - **Claude Code** recibe `--effort <nivel>` justo después de `--model`; **Codex**, `-c model_reasoning_effort=<nivel>`
       justo después de `-m` y antes de `resume`, sin comillas, como el bloque de aislamiento. El valor es siempre
       `ReasoningEfforts.Token`: nunca texto libre en `-c`.
     - **Solo se pasa si el CLI lo admite** (`ReasoningEfforts.Accepts`, P0): Claude, de `low` a `max`; Codex, de `minimal` a
       `xhigh`. El dominio ya rechaza otro (`agent.effort-not-for-provider`); si llega igual, en una plantilla editada a mano o
       con un valor fuera de la lista, no se pasa: el CLI usa el suyo, y el lanzamiento es byte a byte el de antes.
     - `--effort` dice qué ejecutar, no qué puede hacer la sesión, así que no entra en la huella del lanzamiento medido de
       ADR-0018 (`ClaudeCodeArguments.WriteConfinementLaunch`).
     - **Se dice** con un código (`ModelNotices`): «sin clasificar», un esfuerzo que el CLI no admite y que usará el suyo, un
       esfuerzo que el catálogo no confirma para el modelo (se pasa igual: lo que admite cada modelo está sin verificar), y un
       esfuerzo con el modelo por defecto del CLI.
  7. **Los textos** de todo esto (las razones, los avisos, las notas del catálogo, los niveles y los esfuerzos) están en
     `Desktop/Resources/ModelsStrings.resx` y su satélite inglés. La pantalla del selector es de WP6, y la usa con estos datos.
- **Contratos que añade WP2** (los del P0 no cambian):
  - **Dominio** (`Domain/Models`): `ModelRecommender` (`Recommend`, `RecommendSecondExecutor`, `NearestEffort`,
    `IsFollowedBy`, `HistoryMinimumFirstPassShare`), y `ModelNotices`, `ModelNotice` y `ModelNoticeCodes`. La tabla de
    niveles de cada CLI es la del P0 (`ReasoningEfforts.For` y `Accepts`, revisión ccdac10), la misma con la que el dominio
    rechaza un esfuerzo que su CLI no tiene (`agent.effort-not-for-provider`): el lanzamiento, los avisos, la recomendación y
    el lector del catálogo la leen, y ninguno la repite.
  - **Application** (`Application/Models/ModelContracts.cs`): `IModelCatalogSource`, `ModelCatalogDocument` y
    `ModelCatalogSource`; `IModelLearning`, `LearnedModels` y `LearnedModel`; `ModelOptionEstimates` y `ModelOptionEstimate`.
  - **Infrastructure** (`Infrastructure/Models`): `ModelCatalogData`, `ModelCatalogReader`, `BuiltInModelCatalog` y
    `AddAgentStudioModelCatalog`. **Observability** (`Observability/Models`): `LearnedModelsProjection`, `ProjectModelLearning`
    y `AddAgentStudioModelLearning`; (`Observability/Estimates`): `UsageEstimation`, `UsageEstimator` y
    `AddAgentStudioUsageEstimator`. La composición (del lead) llama a los tres registros.
  - **Application** (`Usage/EstimateContracts.cs`, del P0): `UsageEstimate.TotalTokens` (opcional, al final),
    `UsageEstimate.ToSnapshot` y `NoTotalReason`, y `EstimateAssumptions.SameModel`, `AgentsOnly` y `All`. Los textos de los
    supuestos, en `Desktop/Resources/EstimatesStrings.resx` y su satélite inglés.
  - **Providers:** `CodexExecArguments.ReasoningEffortKey`.
- **Alternativas:**
  - Una tabla de precios propia (descartada, docs/14 D-06).
  - Pedir la recomendación a un agente: no añade nada y gasta cuota.
  - La lista de modelos de Codex por su app-server (`model/list`): aplazada hasta medirla.
  - Quitar en el lanzamiento el esfuerzo que el catálogo no confirma para el modelo: el lanzamiento tendría que leer el
    catálogo, y lo que admite cada modelo está sin verificar. Se pasa, y se dice.
  - Un catálogo con un campo de preferencia: el orden basta y no cambia el tipo del P0.
  - Que el historial cambie también al orquestador y al juez: sus intentos no son tareas, y el registro no dice cómo rindieron.
- **Consecuencias:**
  - Todo lo de este ADR es determinista, sin red y sin cuota; las pruebas no crean `App` ni lanzan ningún CLI.
  - Cada versión nueva de un CLI, o un modelo nuevo, pide revisar el catálogo con sus fuentes y su fecha. Una versión probada
    que no esté en `SupportedVersions` hace fallar la prueba del catálogo.
  - El catálogo dice «no comprobado» en la nota de cada entrada cuyo dato no salió del CLI.
  - **Sin verificar** (docs/17 §13), y por eso no se le da más peso que una nota:
    - qué esfuerzos admite cada modelo en Claude Code: salen de la referencia de la API de Anthropic, no del CLI; y qué hace
      Claude Code con un esfuerzo que el modelo no admite;
    - el nivel del alias `fable` en Claude Code (el catálogo lo pone en «máximo», después de Opus) y a qué modelo lleva
      `haiku`;
    - qué esfuerzos admite cada modelo de Codex. Ninguna fuente da la lista por modelo para el CLI. La configuración de
      muestra dice ahora que «varían según el modelo y el cliente», y la página de modelos de OpenAI nombra `max` (para
      GPT-6 Luna) y `ultra`, y nunca `minimal`. En el binario de `codex.exe` 0.160.0 se ven además las cadenas `none` y
      `max`, pero eso no es documentación. Por eso el catálogo da a Codex de `low` a `xhigh`, el lado seguro: `max`, `none`
      y `minimal` para Codex quedan para cuando se midan con esta versión, y `ultra` (subagentes en paralelo) no está en la
      lista cerrada;
    - `model/list` del app-server de Codex: sus campos, si pide red y qué autenticación.
  - **Límites conocidos del estimador:**
    - antes de la versión 2 del arranque no hay propósito, y un intento con tarea cuenta como de ejecutor: el de un juez con
      tarea de revisión podría colarse como referencia (lo aprendido, en cambio, lo descarta por el equipo de su ejecución);
    - lee y proyecta el registro en cada llamada, así que comparar N opciones lo lee N veces. Si la ventana de una pregunta
      se nota lenta, el arreglo es recordar la proyección por la huella de la última entrada.
