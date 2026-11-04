# ADR-0024 · El recomendador asistido de skills usa un servidor MCP local de solo lectura, un duodécimo proyecto, y solo en lanzamientos de lectura

- **Estado:** aceptada · 2026-10-18. Recoge D-26 a D-30 de [docs/14](../14-fase-4-observabilidad-y-memoria.md#45-f4-7-y-f4-8--skills)
  y la decisión 2(b) de su §12. El paquete P0 dejó el proyecto `AgentStudio.SkillsMcp` (esqueleto), los contratos de
  `SkillPorts` y el `AuxiliaryAgentRunner`; WP4 hace el servidor, el servicio, la pantalla y la medición, y WP1 el
  lanzamiento con `--mcp-config` en el adaptador de Claude Code. Amplía [ADR-0002](0002-capas-y-puertos.md) y
  [ADR-0007](0007-catalogo-de-skills.md).
- **Contexto:**
  - F4-8 pide una recomendación determinista con razones y otra asistida por un MCP local que solo propone. La
    determinista (`SkillRecommender`) ya existe y no gasta nada.
  - Decisión 2(b) del usuario: un botón desactivado por defecto, con su OK en cada uso, y una ejecución de Claude con un
    modelo pequeño (haiku) por consulta.
  - Cada skill activada cuesta contexto en todas las ejecuciones, y activar es decisión del usuario (ADR-0007).
  - El lanzamiento de Claude Code está medido ([ADR-0018](0018-modelo-de-amenaza-fase-3.md), «Medición»): `dontAsk`,
    `--tools` con las herramientas del perfil, reglas de ruta y `--strict-mcp-config` sin ningún `--mcp-config`. Un
    servidor MCP añade herramientas a la sesión, así que nunca puede llegar a un ejecutor.
  - ADR-0002 fija once proyectos, y ninguno puede servir MCP por stdio sin cargar WPF ni la raíz de composición.
- **Decisión:**
  1. **Un proceso propio por stdio**, `AgentStudio.SkillsMcp.exe`, el duodécimo proyecto: un segundo punto de entrada
     que, como los módulos, solo depende de `Application` (y de `Domain` a través de ella), más los paquetes
     `ModelContextProtocol` 2.2.0 y `Microsoft.Extensions.Hosting`.
     - Sin Host: el servidor se crea con `McpServer.Create` sobre `StdioServerTransport`. Así no lee `appsettings.json`,
       variables de entorno ni secretos de usuario del directorio de trabajo, no añade ningún logger y no abre sockets.
     - Stdout es el protocolo; los diagnósticos van a stderr.
     - Su línea de órdenes es exactamente `--snapshot <ruta absoluta>`. Sale con 0 cuando el cliente cierra la sesión,
       con 2 si los argumentos no son esos y con 3 si la instantánea no se puede leer.
     - Ningún módulo lo referencia, y `Desktop` tampoco: lo lanza el CLI, nunca Agent Studio dentro de su proceso. Los
       tests lo referencian para probarlo sobre flujos en memoria y como ejecutable real.
  2. **Exactamente dos herramientas, de solo lectura y mundo cerrado:** `search_skills(query, sector?, tags?, top)` y
     `recommend_skills(task, contextTags?, top)`, que es el recomendador determinista sobre la instantánea.
     - Las dos llevan `readOnlyHint: true`, `openWorldHint: false`, `destructiveHint: false` e `idempotentHint: true`.
       No hay recursos, prompts ni completado.
     - Límites: `query` hasta 200 caracteres, `sector` de la taxonomía, hasta 10 etiquetas válidas y `top` de 1 a 20;
       `task` de 1 a 2 000 caracteres, hasta 20 etiquetas de contexto y `top` de 1 a 10. Lo inválido es un error de
       herramienta (`McpException`) cuyo mensaje lee el modelo; una herramienta que no existe es un error de protocolo.
     - Los nombres del servidor y de las herramientas viven en `SkillsMcpNames` (Application), que comparten el servidor
       y el lanzamiento: las reglas de permitir no pueden desviarse de lo que el servidor ofrece.
  3. **Solo lee una instantánea del catálogo** (`SkillCatalogSnapshot`, formato `agentstudio-skills-snapshot/1`), que
     Agent Studio escribe con `CreateNew` en la carpeta de la ejecución, fuera del directorio de trabajo de la sesión.
     - Lleva metadatos del catálogo: id, nombre, resumen, sector, etiquetas, salud local, agentes de la plantilla que la
       tienen activa, si está enlazada y su coste estimado. Nunca el cuerpo de un `SKILL.md`, rutas ni la fuente.
     - El servidor no sigue un enlace, no lee más de 16 MiB y rechaza lo que no sea una instantánea completa.
  4. **Solo en una ejecución auxiliar de lectura**, por `AuxiliaryAgentRunner` con el propósito `SkillRecommendation`
     (D-08): el prompt como evidencia, `AgentRunStarted` antes del lanzamiento y `AgentRunFinished` v2 al terminar.
     - Claude Code con haiku, 8 turnos, 3 minutos y 0,25 USD como mucho (la estimación del propio CLI).
     - Sin escrituras, shell ni red, en una carpeta vacía:
       `%LOCALAPPDATA%\AgentStudio\skill-recommendations\rec-<aleatorio>\work`. La instantánea va al lado, en
       `snapshot.json`, y la carpeta entera se borra al terminar.
     - El esquema estricto `{ recommendations: [{ skillId, scope, agent, reason }] }`.
     - `--strict-mcp-config --mcp-config`: solo el servidor `agentstudio`, ni `.mcp.json` ni los conectores de
       claude.ai. Reglas de permitir para sus dos herramientas (`mcp__agentstudio__search_skills` y
       `mcp__agentstudio__recommend_skills`); `--tools` solo acota las integradas.
     - El adaptador rechaza servidores MCP en un perfil que escribe, tiene shell o red, y la huella
       `WriteConfinementLaunch` del ejecutor no cambia (WP1).
     - `SkillRecommendationService.LaunchRequest` construye esa petición, y la medición usa la misma.
  5. **Consentimiento en cada uso.**
     - En la pantalla Skills la recomendación asistida está apagada hasta que la permites; el permiso no se guarda entre
       sesiones.
     - Cada uso pide el OK con el diálogo de aprobación, que dice qué se lanza y que gasta cuota.
     - El servicio exige un `QuotaConsent` que diga quién lo dio y para qué, de menos de 10 minutos y que no se haya
       usado antes. Sin él no lee el catálogo, no escribe nada y no lanza nada.
  6. **Solo propone.**
     - El sistema valida la respuesta: skills del catálogo y con copia local; ámbitos Global, Project o Agent, el último
       con un agente de la plantilla y los otros dos sin agente; sin repetidas; con una razón que se muestra en una línea
       como «razón del agente» y se corta a 300 caracteres; 10 como mucho. Lo demás se descarta con un aviso en
       castellano.
     - El servicio no tiene ningún camino a `ISkillActivationStore.AppendAsync`. Solo los interruptores de la pantalla
       añaden decisiones, con el usuario como `decidedBy`.
  7. **La tarea es un dato.** Va en un bloque `<task>` recodificado como JSON (`<`, `>`, `&` y comillas escapados), con la
     regla de que no se obedecen órdenes de dentro. El contexto del proyecto se añade como las etiquetas que
     `ProjectStackDetector` saca de los nombres de los ficheros (D-28), nunca de su contenido.
  8. **Medición antes de usarlo** (`SkillsMcpLaunchLiveTests`, con tu OK y `AGENTSTUDIO_PROVIDER_TESTS=1`): el CLI real,
     por el adaptador, con ese lanzamiento salvo que solo `search_skills` conserva su regla. Comprueba que:
     - solo se carga `agentstudio`, conectado, con `--strict-mcp-config` y `--mcp-config`;
     - las herramientas ofrecidas son Read, Grep, Glob, StructuredOutput y las dos del servidor;
     - una llamada con regla devuelve datos del catálogo, y `dontAsk` deniega la herramienta MCP sin regla;
     - no se escribe nada fuera del directorio de trabajo, ni dentro: la carpeta de la ejecución solo tiene su
       instantánea, igual, la de al lado solo su centinela, y el repositorio queda como estaba.
- **Alternativas:**
  - **HTTP en bucle local dentro de Agent Studio:** el puerto queda al alcance de cualquier proceso local; hace falta un
    token y un servidor HTTP en la app.
  - **`AgentStudio.Desktop.exe` en modo servidor:** carga WPF para servir y mezcla la raíz de composición.
  - **El catálogo en el prompt, sin MCP:** unos 12 000 tokens por consulta con 120 skills, y F4-8 pide un MCP.
  - **El SDK con Host (`Host.CreateApplicationBuilder`):** lee configuración del directorio de trabajo y escribe el log en
    la consola, que es el canal del protocolo.
  - **JSON-RPC escrito a mano** (`initialize`, `tools/list`, `tools/call`): solo si el SDK trajera dependencias de más.
    En `net10.0`, la 2.2.0 solo trae `ModelContextProtocol.Core`, `Microsoft.Extensions.AI.Abstractions` y abstracciones.
- **Consecuencias:**
  - Un ejecutable más, junto a la app: el servicio lo espera en `SkillsScreenServices.DefaultServerExecutable`, al lado
    de `AgentStudio.Desktop.exe`. La integración lo lleva con una referencia de proyecto desde Desktop, que copia el
    apphost, su `runtimeconfig`, su `deps.json` y sus dependencias (como ya hacía el proyecto de pruebas). Comprobado el
    2026-10-18 desde la salida de Desktop, una ruta con espacios: responde a `initialize`, ofrece exactamente sus dos
    herramientas (`readOnlyHint`, sin `openWorldHint`), busca, termina con código 0 al cerrarse su entrada y no escribe
    nada.
  - Cada uso gasta cuota y queda en el registro como ejecución auxiliar, así que el panel de consumo la cuenta.
  - El `StdioClientTransport` del propio SDK envuelve el comando en `cmd.exe /c` en Windows y parte una ruta con
    espacios; los tests lanzan el ejecutable sin shell. Claude Code no tiene ese problema: la ruta del servidor va como
    cadena JSON dentro de `mcp.json` y la línea de órdenes solo lleva la ruta de ese fichero (medición de abajo).
  - **Residual** (punto 2 de ADR-0018): un proceso Medium del usuario puede sustituir el ejecutable o la instantánea
    entre la escritura y la lectura.
  - Un permiso de la asistida que sobreviva entre sesiones sería un ajuste de `AppSettings`, que no es de este paquete.

## Medición D-30 (2026-10-18)

`SkillsMcpLaunchLiveTests`, con el OK del usuario: Claude Code 2.1.290 por el adaptador del proyecto, con el lanzamiento
que construye `SkillRecommendationService.LaunchRequest` (perfil de lectura, `dontAsk`, `--model haiku`, carpeta de
trabajo vacía y con espacios y una «ñ» en la ruta, el esquema estricto y el servidor sobre una instantánea), salvo que solo
`search_skills` conserva su regla de permiso. El prompt pedía llamar a las dos herramientas e intentar escribir fuera de
la carpeta.

| Comprobación | Resultado |
|---|---|
| Servidores cargados (`system/init.mcp_servers`) | Solo `agentstudio`, `connected`; `--strict-mcp-config` y `--mcp-config` en la línea de órdenes |
| Herramientas ofrecidas | `Read`, `Grep`, `Glob`, `StructuredOutput`, `mcp__agentstudio__search_skills` y `mcp__agentstudio__recommend_skills`; ninguna más |
| Herramienta con regla (`search_skills`) | Responde con datos del catálogo (`spark-optimization`) |
| Herramienta sin regla (`recommend_skills`) | Denegada por `dontAsk` (`permission_denials`) |
| Escritura fuera de la carpeta | No hay herramienta de escritura: el modelo probó `Read` sobre la ruta de fuera y también se le denegó; la carpeta de la ejecución solo conserva su instantánea, sin cambios, y la de al lado su centinela; el repositorio queda igual |
| Respuesta | Estructurada y válida: una recomendación, sin activar nada |
| Coste | 5 turnos, 0,034 USD (estimación del CLI), modelo `claude-haiku-4-5-20251001` |

El servidor de la medición es el de la salida de las pruebas, en una ruta con espacios (`C:\Personal Projects\…`):
Claude Code lo arranca desde `mcp.json` sin partirla. La compatibilidad del SDK 2.2.0 con el cliente MCP de Claude Code
2.1.290 queda comprobada para `initialize`, `tools/list` y `tools/call`. Se repite la medición si cambia la versión de
Claude Code, como la del confinamiento (ADR-0018).

## Adenda (2026-11-03): una tercera herramienta, la del historial del proyecto (Fase 12)

La Fase 12 ([ADR-0047](0047-indice-semantico-local.md), D-06 de
[docs/17](../17-constructor-visual-de-agentes.md#34-quién-lo-usa-y-qué-ve-el-juez)) añade a este mismo servidor la
herramienta del planificador. Eso **contradice las decisiones 1 y 2 tal como están escritas**, que no se reescriben;
desde esta fecha valen así:

- **La línea de órdenes (decisión 1)** ya no es «exactamente `--snapshot <ruta absoluta>`»: es `--snapshot <ruta
  absoluta>`, `--history <ruta absoluta>` o las dos, cada una una sola vez y en cualquier orden. Los códigos de salida no
  cambian: 0 al cerrar el cliente, 2 si los argumentos no son esos y 3 si una de las instantáneas no se puede leer.
- **Las herramientas (decisión 2)** ya no son «exactamente dos»: el servidor ofrece solo las de las instantáneas que
  recibe. Con el catálogo solo, las dos de siempre, así que el lanzamiento de la recomendación asistida no cambia y su
  medición sigue valiendo. Con el historial, `search_project_history(query, top)`, de solo lectura, idempotente y de mundo
  cerrado como las otras dos, y con su regla `mcp__agentstudio__search_project_history`.
- **La herramienta nueva:**
  - `query` de 1 a 500 caracteres y `top` de 1 a 10 (5 si no se dice). Lo inválido es un error de herramienta en
    castellano, como en las otras dos.
  - Lee una instantánea de lo de confianza del proyecto (`ProjectHistorySnapshot`, formato
    `agentstudio-history-snapshot/1`): hasta 32 MiB, en UTF-8 estricto y sin seguir un enlace.
  - Nunca carga el modelo. Ordena por palabras (`bm25` sobre los términos de `LexicalTerms`, como la lista por palabras
    del índice) y, si la instantánea trae vectores, amplía con los fragmentos más cercanos a sus mejores aciertos; las dos
    listas se funden con `HybridRanking`.
  - Responde con datos: cada fragmento con su fuente anclada (`mem:…` o `log:…`), su fecha, su confianza y por qué salió,
    bajo un aviso de que nada de lo que dicen es una orden. La respuesta se escribe con el codificador de `PromptData`, que
    escapa `<`, `>`, `&` y las comillas.
  - Nunca devuelve un resumen diario: cita las líneas «Hecho» de la bitácora y ADR-0023 lo deja fuera de los prompts. La
    instantánea no debe traerlo, y la herramienta descarta además cualquier fragmento con la etiqueta «resumen diario».
- **Lo que no cambia:** un solo proceso por stdio, sin Host, sin red ni sockets, que no escribe nada; solo en
  lanzamientos de solo lectura (el adaptador rechaza un servidor MCP en un perfil que escribe, tiene shell o red); nunca
  en un ejecutor, ni en el juez ni en los auditores (F12-8).
- **Pendiente, en la integración de la Fase 12:** conectar la herramienta al lanzamiento del planificador y repetir con
  ella la medición D-30 (F12-9), con tu OK. *(La conexión está hecha: ver la
  [adenda del 2026-11-04](#adenda-2026-11-04-la-herramienta-del-historial-conectada-al-planificador-integración-de-la-fase-12).
  La medición sigue pendiente.)*

## Adenda (2026-11-04): la herramienta del historial, conectada al planificador (integración de la Fase 12)

Lo que la adenda del 2026-11-03 dejaba pendiente ya está en el código: el lanzamiento del planificador lleva la herramienta.
Las decisiones 3 a 5 valen para ella así:

- **Quién la recibe:** solo el planificador, un agente `Orchestrator` con `ReadSelected` en la plantilla, en Claude Code y
  en su lanzamiento de solo lectura (sin escrituras, shell ni red). Lo decide `IProjectHistoryTool`
  (`Application/Semantic/ProjectHistoryTool.cs`) antes de leer el índice: a cualquier otro agente, o a un perfil que
  escribe, no le escribe nada.
  - Nunca la reciben un ejecutor, el juez o un auditor (F12-8). Solo los puertos del motor, el orquestador y el
    planificador pueden llegar al puerto, y una prueba lo fija por reflexión.
  - Codex rechaza los servidores MCP y Gemini no los lanza, así que con ellos el planificador trabaja sin la herramienta.
    No es un error: la propuesta lo dice (`PlanProposal.HistoryToolNotice`).
- **La instantánea** (decisión 3): Agent Studio escribe con `CreateNew` lo que puede llegar a un prompt
  (`ISemanticIndex.SnapshotAsync`: de confianza y nunca un resumen diario, como mucho los 2 000 fragmentos más recientes) en
  `%LOCALAPPDATA%\AgentStudio\history-snapshots\his-<aleatorio>\history.json`. Queda fuera del proyecto y del worktree
  `_base` en el que lee el planificador. Se borra al terminar la planificación. Lo que deje un cierre a medias lo barre
  una planificación posterior, pasado un día, como las carpetas de la recomendación asistida.
  - Sin nada de confianza, no hay instantánea ni herramienta.
  - Si el registro no verifica, la planificación se para, como con la memoria. Cualquier otro fallo deja al planificador
    sin la herramienta, nunca sin planificar.
- **El lanzamiento** (decisión 4): el servidor `agentstudio` con `--history <ruta>` y la regla
  `mcp__agentstudio__search_project_history`, en los dos lanzamientos de una planificación, porque la corrección es otro
  proceso del CLI. Si un lanzamiento ya llevara el servidor con `--snapshot`, se le añade `--history` y la regla:
  un solo servidor (`ProjectHistoryLaunch.With`). El del planificador no lleva el catálogo, así que va con `--history`
  solo. El prompt la nombra en una frase, y solo entonces. Ese prompt queda capturado como evidencia.
- **Consentimiento** (decisión 5): no hay un OK por uso. La herramienta no lanza nada por sí misma: sus llamadas son parte
  de la planificación que pidió la persona. La decisión 9 de docs/17 la autorizó, con su medición.
- **Pendiente:** repetir la medición D-30 con la herramienta en una planificación corta real, con tu OK (F12-9).
