# ADR-0021 · La salud del contexto se mide por paso, con la ventana del modelo del paso; umbrales globales y validados; Codex `exec`, «no expuesto»

- **Estado:** aceptada · 2026-10-18. Recoge D-09 a D-13 de [docs/14](../14-fase-4-observabilidad-y-memoria.md#42-f4-3--salud-del-contexto).
  Lo escriben dos paquetes:
  - **WP1**, los analizadores, la capacidad y el registro: un `ContextObserved` por paso, la ventana del modelo de ese
    paso, las compactaciones y el pico de contexto de `AgentRunFinished` v2 (§ 2 de la decisión).
  - **WP2**, la evaluación, la ventana aprendida, el seguimiento en vivo y los umbrales (§ 3 a § 7):
    `ContextHealthTracker` y `ModelWindowCatalog`, en `src/AgentStudio.Observability/Context/`, sobre
    `ContextHealthEvaluator` (`Domain/Metrics/ContextHealth.cs`, que P0 movió al dominio).
- **Contexto:**
  - F4-3 pide la salud del contexto con umbrales configurables, sin dar por exacto lo estimado.
  - Claude Code informa del uso de cada llamada al modelo (`assistant.message.usage`) y, al terminar, del total
    (`result.usage`) y de la ventana de cada modelo (`result.modelUsage.<modelo>.contextWindow`).
  - El total no es la ocupación: suma los pasos. En `02-herramienta` daría 61 222 tokens, frente a los 30 822 del último
    paso.
  - Cada paso llega en varios eventos `assistant` con el mismo `message.id` y el mismo uso: sumar eventos contaría varias
    veces el mismo paso.
  - El modelo que anuncia `init` no es el del bucle principal: en `01-texto` anuncia `claude-haiku-4-5-20251001` (ventana
    de 200 000) y el bucle corrió en `claude-sonnet-5-5` (1 000 000). Con la ventana de `init` saldría un 21 % en lugar de
    un 4 % (D-13).
  - Mientras la ejecución no termina no se conoce la ventana.
  - `codex exec --json` solo da el total acumulado del hilo: ni la ocupación, ni la ventana, ni las compactaciones.
    Gemini está aparcado ([ADR-0009](0009-google-aparcado.md)).
- **Decisión:**
  1. **La ocupación es la del último paso, nunca el total.**
     - Es el prompt de esa llamada: la entrada más la caché leída y la creada.
     - Se compara con la ventana del modelo que sirvió el paso.
     - Nunca se deriva de `result.usage`.
  2. **Analizadores, capacidad y registro (WP1).**
     - **Un `ContextObserved` por paso.** El analizador de Claude lo emite en el primer evento de cada `message.id`: entrada
       más caché leída más caché creada, con el modelo de ese paso, «exacto · informado por el CLI». Los eventos siguientes
       del mismo paso no cuentan otra vez. Los pasos de un subagente (`parent_tool_use_id`) son otro contexto y no se
       observan.
     - **La ventana del modelo del paso.** En vivo, el paso la lleva «sin dato»: el CLI solo la da al terminar. La ventana
       de `result.usage` es la del modelo del último paso, nunca la de `init`; sin pasos, solo un desglose de un único
       modelo la dice, y con varios queda «sin dato». Una clave con fecha (`claude-haiku-4-5-20251001`) casa con su nombre
       sin fecha. El desglose por modelo (`ModelUsage`) lleva la ventana exacta de cada uno.
     - **Compactaciones.** `system/compact_boundary` es un `CompactionObserved` con su disparador (`auto`, `manual` u otro
       token; si no, `unknown`) y los tokens de antes (`compact_metadata.pre_tokens`; si no llegan, «sin dato»).
     - **Capacidad.** `ProviderCapabilities.ReportsContextOccupancy` es verdadera en Claude Code y falsa en `codex exec` y
       en Gemini.
     - **Registro.** `AgentRunFinished` v2 de planificador, ejecutores y juez guarda el pico de contexto (el paso con el
       prompt mayor, el primero de los iguales) con la ventana de su modelo tomada del desglose final, exacta cuando la hay;
       las compactaciones, nulas si el proveedor no informa del contexto; y el modelo informado, el del último paso, nunca
       el de `SessionStarted`.
  3. **Evaluación (WP2).**
     - `ContextHealthEvaluator` convierte los tokens y la ventana en una utilización y un nivel: sano, aviso, compresión
       recomendada o crítico.
     - La utilización toma el origen más débil de sus dos cifras ([ADR-0019](0019-consumo-como-proyeccion-del-registro.md)):
       una ventana aprendida la hace «estimado por Agent Studio», y una estimación del CLI, «estimado por el CLI». Nunca
       sale exacta si una de las dos no lo es.
     - Si falta una de las dos cifras, el nivel es «desconocido», nunca «sano», y la utilización dice por qué: «sin dato» o
       «no expuesto».
     - Por encima de la ventana es crítico, no desconocido.
  4. **La ventana de un paso (WP2, D-10).** `ContextHealthTracker` sigue una ejecución evento a evento y usa, por este
     orden:
     1. la ventana que trae el paso;
     2. la última que el CLI dio en esta misma ejecución para ese modelo, también la del desglose de su resultado: es
        exacta, así que al terminar la ejecución el último paso pasa a exacto;
     3. la última vista para ese modelo en el registro del proyecto (`ModelWindowCatalog`), como «estimado por Agent Studio
        (ventana vista en una ejecución anterior)»;
     4. ninguna: la ocupación sale en tokens, sin porcentaje, y el nivel es desconocido.

     Nunca toma el modelo de `SessionStarted` por el de los pasos.
  5. **Qué ventanas se aprenden (WP2).**
     - Solo de un registro que verifica: el del proyecto abierto o el de otro proyecto con un ancla reconocida. Si no,
       ninguna.
     - Solo las que el CLI informó (exactas) para un modelo con nombre, del desglose por modelo y del pico de contexto de
       `AgentRunFinished` v2. Gana la última vista de cada modelo.
     - Nunca las de la v1: el analizador de la Fase 3 emparejaba la ventana con el modelo de `init` (D-13).
  6. **Umbrales (WP2, D-11).**
     - Son globales, en `%APPDATA%\AgentStudio\settings.json` (aviso, compresión y crítico). No hay umbrales por agente.
     - Por defecto, 70, 85 y 95 % (decisión 6 de la §12 de docs/14).
     - `ContextHealthThresholds` exige 0 < aviso < compresión < crítico ≤ 1, y el seguimiento rechaza unos umbrales que no
       lo cumplen, también los que llegaran por una expresión `with`. Guardarlos y validarlos desde los ajustes es de WP5.
  7. **Lo que no se expone (WP2).**
     - Con un proveedor que no informa del contexto (`ProviderCapabilities.ReportsContextOccupancy` falso: `codex exec` y
       Gemini), la ocupación, la ventana, la utilización y las compactaciones son «no expuesto» de principio a fin, con el
       nombre del CLI en el motivo.
     - El app-server de Codex sí da `last` y `modelContextWindow`, pero sería otro adaptador (aplazado).
     - Las compactaciones las cuenta Agent Studio a partir de los eventos del CLI («medido por Agent Studio»). En el
       registro, `Compactions` nulo es «no expuesto», nunca cero.
- **Alternativas:**
  - **La ocupación de `result.usage`:** daría el doble en una ejecución de dos pasos.
  - **La ventana del modelo de `init`:** es la de un modelo auxiliar (D-13).
  - **Volver a emitir el `ContextObserved` de los pasos al final, ya con la ventana exacta:** serían dos observaciones del
    mismo paso, y quien guarda el pico con «mayor que» estricto se quedaría con la primera, sin ventana. Por eso la ventana
    exacta llega con el desglose del resultado.
  - **Retrasar el `ContextObserved` hasta el resultado:** el monitor no vería nada en vivo.
  - **Dar por exacta la ventana aprendida:** el modelo puede haber cambiado de ventana desde aquella ejecución, o ser otro
    con el mismo nombre en otra configuración.
  - **Un porcentaje sobre una ventana supuesta** (la de la familia del modelo, por ejemplo) **cuando no se conoce ninguna:**
    sería un número inventado. Sin ventana se muestran solo los tokens.
  - **Umbrales por agente:** más UI para un beneficio que aún no se ha medido. Se puede añadir sin romper nada.
- **Consecuencias:**
  - `01-texto` ocupa un 4 % de la ventana de `claude-sonnet-5-5`, no un 21 % (`ClaudeStreamJsonParserTests`).
  - El monitor puede enseñar, por tarea en curso, la ocupación con su porcentaje y su etiqueta, los tokens y la ventana,
    el modelo, el nivel y las compactaciones (D-12, WP5). El panel de consumo enseña el pico de cada intento (ADR-0019).
  - Una ejecución de Claude pasa de «estimado» a exacto al llegar su resultado. Sin ejecuciones anteriores, enseña solo
    tokens hasta el final.
  - El juez registra como `LaunchedAt` la llegada del primer evento de su ejecución: `AgentJudge` no avisa del fin de la
    cola del proveedor. Llega como mucho el arranque del CLI más tarde que el lanzamiento.
  - `AuxiliaryAgentRunner` (P0) guarda el pico con la ventana que traía el paso, que en Claude es «sin dato»: la
    integración puede resolverla con el desglose final, como el motor y el juez.
  - El evaluador es puro y está en el dominio: la política de reintentar en una sesión nueva con el contexto alto
    (aplazada) podrá usarlo.
  - Nunca se habla de la «comprensión» del modelo: solo de ocupación, ventana y compactaciones.
  - **Sin verificar:**
    - `system/compact_boundary` está documentado en los tipos del SDK, pero no aparece en las capturas grabadas: se prueba
      con una línea sintética.
    - Una ventana aprendida puede quedar vieja si el proveedor cambia la de un modelo: por eso es una estimación.
