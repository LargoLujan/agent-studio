# ADR-0056 · La ayuda: local y sin cuota primero, y un agente de solo lectura, con tu OK en cada pregunta, que no ve el proyecto salvo que lo marques

- **Estado:** aceptada · 2026-11-11. Recoge D-54 a D-57 de
  [docs/17 §5.3 y §5.4](../17-constructor-visual-de-agentes.md#54-ayuda-y-chat-de-ayuda) y la decisión 8 (a) que aceptaste el
  2026-11-06: la ayuda local primero, y un agente de solo lectura (Claude, con un modelo ligero) con tu OK en cada pregunta. El
  P0 de la Fase 14 deja los **contratos** y sus reglas puras (`Application/Help/HelpContracts.cs`, más
  `Application/Security/{LocalPaths.cs, PlainText.cs}` y `Application/Localization/LanguageTags.cs`); los implementa **WP-14C**
  (`Desktop/Help` y `Application/Help`). Sigue a [ADR-0054](0054-idiomas-y-lenguaje-claro.md), que completa su adenda del
  mismo día, y se apoya en [ADR-0019](0019-consumo-como-proyeccion-del-registro.md) (el consumo), [ADR-0018](0018-modelo-de-amenaza-fase-3.md)
  (el confinamiento de Claude medido) y [ADR-0047](0047-indice-semantico-local.md) (los vectores, si el modelo está instalado).
- **Contexto:**
  - P14 pide una ayuda que se entienda sin leer nada técnico y un chat de ayuda. P13 puso el botón ⓘ en cada apartado
    complejo; su «Más ayuda» no se enseña mientras no lleve a ningún sitio (`InfoButton.MoreHelpCommand`, ADR-0054, punto 7).
  - Los documentos de `docs/` son de desarrollo y demasiado técnicos: no sirven como ayuda.
  - Ya hay un patrón para las ejecuciones auxiliares que gastan cuota: «Revisar el orden», el chat de preparación y el análisis
    del consumo con un agente son de solo lectura, piden tu OK en cada uso con la estimación delante (`QuotaConsent`, gastado
    una vez) y quedan en el registro con su propósito (`AuxiliaryAgentRunner`).
  - Lo que se manda a un agente llega a su vendor. Una ayuda que mandara tu proyecto, tus rutas o un secreto pegado sería una
    fuga que nadie pidió.
- **Decisión:**
  1. **La ayuda local responde primero, sin cuota** (D-55).
     - **El corpus** son artículos escritos en lenguaje claro, uno por pantalla o apartado, en `Help/<idioma>/<id>.md` dentro del
       escritorio. `HelpArticle` lleva un id (un código, como los de `UserText`: `orchestrator.team`, el mismo en todos los
       idiomas), el idioma, el título, el Markdown, las pantallas cuyo `F1` lo abre y las claves de los ⓘ cuyo «Más ayuda» lo
       abre (`InfoKey`, D-54: la clave de `Info_Canvas_Team` es la del control). `HelpArticleRules.Validate` devuelve los
       códigos de lo que incumple (`help.article-id-invalid`, `help.article-title-too-long`…), y una prueba sobre todo el
       corpus (WP-14C, F14-7) lo exige a cada artículo.
     - **`IHelpCorpus`** da los artículos de un idioma (lo que falta en un idioma sale en castellano de España, el origen, como
       cualquier texto), uno por id, el de una pantalla (`F1`) y el de la clave de un ⓘ.
     - **`IHelpSearch`** es local y gratis: FTS5 sobre el corpus, más los vectores del índice semántico si su modelo está
       instalado (ADR-0047), y cada resultado (`HelpSearchHit`: artículo, título, fragmento, puntuación y cómo se encontró)
       abre su artículo. Sus puertos **no reciben** ni un proyecto ni un consentimiento (una prueba lo comprueba): no pueden
       lanzar un agente ni gastar cuota. `HelpSearchRules.Validate` limita el texto de la búsqueda (200 caracteres, sin
       caracteres ocultos) y los resultados (10).
     - **«Preguntar a un agente»** aparece debajo, después de la respuesta local.
  2. **El agente de ayuda** (D-56).
     - `IHelpAgent.AskAsync(HelpAskRequest)` es una ejecución **auxiliar** (`AuxiliaryAgentRunner`) con el propósito nuevo
       `Help`, que se añade al final de `AgentRunPurpose`. Cuenta en el consumo como cualquier otra y queda en el registro del
       proyecto abierto: por eso la petición lleva un `ProjectContext`. **Sin proyecto abierto solo hay ayuda local**, porque no
       hay registro donde contarla.
     - **Tu OK en cada pregunta** (decisión 8 (a)): `QuotaConsent` con la estimación delante, de 10 minutos como mucho y
       gastado una vez en `IQuotaConsentLedger`, como el chat de preparación. Para estimarlo, el escritorio pide
       `UsageEstimateRequest` con `HelpAgent.Id` (`ayuda`) y el propósito `Help`.
     - **Quién contesta** (`HelpAgentChoice`): Claude por defecto, con el modelo más ligero que el catálogo clasifique
       (`Model` nulo). Con Codex, a tu elección: su modo de solo lectura lee todo el disco (docs/15 §3.2), por eso no es el de
       por defecto, y `ReadsWholeDisk` permite que la tarjeta lo diga antes de tu OK.
     - **De solo lectura y sin ver tu disco:** una carpeta de trabajo vacía y Read, Grep y Glob con el confinamiento de Claude
       medido (ADR-0018), sin escritura, shell, red ni memoria. **Lo que fuerza `AuxiliaryAgentRunner`** (guardia de
       `RunAsync`, `ReadOnlyViolations`, antes de capturar, registrar ni lanzar nada; si no se cumple, `ArgumentException` y
       nada queda en el registro): permisos sin escritura, shell ni red; herramientas permitidas solo `Read`, `Grep` y `Glob`;
       sin directorios adicionales, sin servidores MCP, sin skills y sin sesión que reanudar; y una carpeta de trabajo que existe y
       está vacía. **Lo que no fuerza** y es de `IHelpAgent` (WP-14C): crear esa carpeta vacía en cada pregunta, el esquema, el
       prompt y el modelo ligero; y el confinamiento de lectura lo da el adaptador de Claude, no el ejecutor.
     - **Cada pregunta es una ejecución nueva:** no reanuda la sesión del CLI ni reenvía las preguntas anteriores, a diferencia
       del chat de preparación. Así lo que viaja es exactamente lo que enseñó la tarjeta de esa pregunta, y el OK de una
       pregunta no cubre la siguiente. La conversación que ves en pantalla es solo historial.
     - **Recibe, como datos delimitados** (el prompt es de WP-14C): la tarjeta, los artículos de ayuda más parecidos a la
       pregunta (como mucho `HelpLimits.MaxSentArticles`, 4; la respuesta se apoya en ellos y los cita) y la pregunta. El idioma
       en que debe contestar es un dato, «responde a la persona en <idioma>» (D-48): el prompt no cambia con el idioma.
  3. **Qué ve y qué no ve nunca** (D-57), en `HelpContextCard`.
     - **La tarjeta** es lo que se manda además de la pregunta, con su texto exacto, delante de ti antes de tu OK:
       `HelpContextCard.Build(HelpScreenFacts, incluirDatosDelProyecto, ISecretRedactor)` junta la pantalla (su código y su
       nombre), hasta seis hechos de su estado, el último aviso visible y, solo con tu marca, el nombre del proyecto. Cada texto
       pierde sus caracteres de control y bidireccionales, pasa por `ISecretRedactor` y se corta a 200 caracteres: lo que ves
       es lo que viaja.
     - **La marca «Incluir datos del proyecto»** (`IncludesProjectData`): sin ella, el nombre del proyecto no va, y tampoco un
       hecho o un aviso que nombre una ruta de tu equipo. Lo que se dejó fuera está en `Withheld`, para que la tarjeta diga «no
       se envía: el nombre del proyecto». Qué es una ruta lo dice `LocalPaths`: una ruta con unidad (`C:\…`, `D:/…`), una de red
       (`\\servidor\recurso`) o bajo la carpeta personal (`~/…`, que es lo que deja el redactor del perfil de usuario); no lo es
       una ruta relativa ni una URL.
     - **Nunca:** credenciales, tus ficheros, el código, la memoria, el registro, los prompts de otros agentes ni nada de las
       carpetas de los CLIs. La tarjeta no tiene dónde ponerlos: sus partes son la pantalla, un hecho, el aviso y el proyecto.
     - **Se vuelve a comprobar antes de enviar:** `HelpContextCard.Validate(redactor)` mira la tarjeta que el servicio va a
       mandar, la haya hecho quien la haya hecho: una parte que cambia al redactarla otra vez todavía tiene un secreto
       (`help.card-not-redacted`), y un dato del proyecto sin la marca es `help.card-project-data-not-marked`. La pregunta que
       escribes se limpia igual (`HelpAskRules.CleanQuestion`) y se comprueba (`HelpAskRules.Validate`).
     - **La conversación** queda como evidencia de cada ejecución, en el proyecto abierto y fuera de git, como cualquier otra.
  4. **La respuesta y sus acciones** (D-56).
     - El agente responde con un esquema estricto que no puede decir longitudes ni listas de ids, así que el sistema lo
       comprueba (`HelpAnswerRules.Check`): el texto, de 1 200 caracteres como mucho y sin caracteres ocultos; los artículos
       que cita, solo entre los que se enviaron (4 como mucho); y las acciones, solo de la lista cerrada (2 como mucho). Lo
       que no pasa no es un error: se deja fuera y se dice en `Discarded` (en inglés, para «Detalles»). El texto es de un
       agente: se enseña enmarcado como tal. `Check` no conoce el catálogo ni lo enviado: **WP-14C tiene que pasarle** los ids de
       `IHelpActionCatalog.Actions` y los de los artículos que mandó con la pregunta.
     - **Las acciones son una lista cerrada** (`IHelpActionCatalog`): ids con forma de código (`open.consumption`) y el texto de
       su botón como código con argumentos (`UserText`), escritos en el código del escritorio. El agente solo nombra un id; la
       app enseña el botón, y no pasa nada hasta que lo pulsas. Una acción abre un sitio de la app («Abrir Consumo», «Abrir
       Ajustes → Trabajo desatendido»); nunca cambia nada. El esquema de la respuesta enumera exactamente esos ids.
  5. **Registro y consumo.** El propósito `Help` entra al final de `AgentRunPurpose` (los nombres se serializan: nunca se
     reordena); `AuxiliaryAgentRunner` lo admite y le escribe `AgentRunFinished` versión 3, con la clase de su final; el consumo
     lo nombra con `Usage_Purpose_Help` en `ConsumptionStrings.resx` (castellano e inglés). No hay `DecisionKind` nuevo: la
     ejecución se registra con las entradas de siempre de una auxiliar.
- **Alternativas:**
  - **Solo la ayuda local** (decisión 8 (c)): gratis y sin riesgo, pero no responde a lo que el corpus no dice.
  - **Un OK por sesión de ayuda, hasta diez preguntas** (decisión 8 (b)): la tarjeta cambia con la pantalla y con el estado, y
    un OK de sesión dejaría pasar una tarjeta que no viste.
  - **Reanudar la sesión del CLI entre preguntas**, como el chat de preparación: ahorra repetir los artículos, pero lo que
    viaja deja de ser lo que enseña la tarjeta de cada pregunta. Aquí pesa más que se vea todo lo enviado.
  - **Dar al agente herramientas para abrir pantallas o cambiar ajustes:** ejecutaría algo por una instrucción que puede venir
    de un texto del corpus o de la pantalla. La lista cerrada de botones lo deja en una sugerencia que decides tú.
  - **Un enum de acciones en Application** (`OpenConsumption`…): ataría los contratos a las pantallas y cada pantalla nueva
    tocaría un fichero compartido. Un catálogo en el escritorio crece sin tocarlos.
  - **Mandar siempre el nombre del proyecto o las rutas** para dar mejores respuestas: la ayuda no los necesita para explicar
    una pantalla, y lo que se manda llega a otra empresa.
  - **Usar `docs/` como corpus:** son técnicos y están en castellano; no pasarían la guía de textos (ADR-0054, punto 9).
  - **Codex como agente por defecto:** su solo lectura lee todo el disco.
- **Consecuencias:**
  - **Pruebas del P0:** `HelpContractsTests` (reglas de los artículos, de la búsqueda, de la tarjeta con un secreto sembrado y
    con rutas, de la pregunta y de la respuesta; los puertos sin proyecto ni consentimiento; los límites) y
    `Phase14TextRulesTests` (`LocalPaths`, `PlainText` y `LanguageTags`). Las de WP-14C (F14-7 y F14-8) usan un agente
    guionizado y buscan un secreto sembrado en lo enviado; ningún CLI real se lanza sin tu OK.
  - **Lo que hace WP-14C:** el corpus y sus ⓘ (el «Más ayuda» solo se enseña cuando hay corpus), el índice de búsqueda
    (dónde vive lo decide WP-14C; no es del proyecto: el corpus viaja con la app), `IHelpAgent` con su esquema y su prompt,
    el panel, `F1`, el catálogo de acciones y los textos de la tarjeta en los recursos.
  - **`AuxiliaryAgentRunner`** admite `Help` y `ClientDocumentDraft` desde el P0 (`IsAuxiliary` y `RecordsEndClass`) y les
    exige el lanzamiento de solo lectura de arriba (`RequiresReadOnlyLaunch`): así WP-14C y WP-14D no tocan los mismos renglones
    de un fichero compartido, y una llamada que pida otra cosa falla cerrada.
  - **El consumo** nombra los dos propósitos nuevos. Los tres de la Fase 13 (`OrderReview`, `ConsumptionAnalysis`,
    `PreparationChat`) siguen sin nombre en esa lista y no salen en el detalle de cada intento; es de WP-14A (c), que extrae
    los textos de `UsageDashboardViewModel`.
  - **Residuales:**
    - lo que envías llega al vendor del agente (docs/17 §8.3);
    - el redactor reconoce formatos de secretos conocidos: es un filtro de mejor esfuerzo, no una garantía; la tarjeta a la vista
      y tu OK son la segunda barrera;
    - `LocalPaths` reconoce rutas de Windows (con unidad y de red), de la carpeta personal (`~/`) y `file:///`, también con
      espacios: una ruta llega hasta una comilla, un paréntesis sin pareja, una coma o punto seguidos de espacio, el final de línea
      o la ruta siguiente, y quita **de más** antes que de menos. Una ruta de Unix sin `file:///` ni `~` (`/home/ana`) no la
      detecta, ni lo que escribas en la pregunta sin que sea una ruta;
    - una respuesta puede ser falsa o engañosa aunque cite artículos que existen: es texto de un agente y se enseña como tal.

## Adenda del 2026-11-11 · WP-14C: lo que quedó implementado

- **Corpus** (`Desktop/Help/<idioma>/<id>.md`, incrustado como `Help.<idioma>.<id>.md`): diez artículos cortos en `es-ES` y `en` (preparación, lienzo, monitor, debates, auditoría, memoria, skills, consumo, ajustes, bloqueo), con cabecera `title:`, `screens:` e `info:`. Una prueba recorre el corpus con `HelpArticleRules` y exige artículo a cada `InfoKey` de las vistas. El «Más ayuda» sale de un estilo implícito de `InfoButton` ligado a `OpenInfoHelpCommand` del shell; `InfoButton` solo lo enseña si el comando puede ejecutarse con su clave.
- **Búsqueda:** FTS5 en memoria (`Microsoft.Data.Sqlite`, `unicode61 remove_diacritics 2`), un índice por idioma, términos de `LexicalTerms` entre comillas, con prefijo y unidos por `OR`. **Todavía no usa los vectores** del índice semántico: todo resultado es `Lexical`.
- **`F1`** es un `KeyBinding` de `MainWindow` (se retira con el bloqueo como los demás); «Ayuda» va al final de la barra lateral. `HelpScreens.Of` convierte el contenido activo en un código de pantalla.
- **`HelpAgentService`:** valida con `HelpAskRules.Validate` y rechaza una ruta de tu equipo en la pregunta sin la marca (`help.question-project-data-not-marked`, solo del servicio); gasta el OK una vez (10 minutos); manda el artículo de la pantalla y los más parecidos (4 como mucho); crea una carpeta vacía por pregunta y la borra; `PersistSession = false`; esquema con enum de las acciones de la lista y de los artículos enviados; respuesta por `HelpAnswerRules.Check`. El prompt es texto fijo (D-48).
- **Refuerzos del guardia:** `ReadOnlyViolations` rechaza una carpeta que es un enlace o está dentro de uno (`ReparsePoint`) y una lista vacía de herramientas; `RunAsync` vuelve a mirar la carpeta antes de `provider.StartAsync` y, si ya no está vacía, cierra el intento como fallido sin lanzar.
- **Residual nuevo:** si la carpeta temporal del usuario o un padre es un enlace (una junction), el agente de ayuda falla cerrado.
