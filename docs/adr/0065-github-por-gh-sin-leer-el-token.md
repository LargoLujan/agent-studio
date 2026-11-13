# ADR-0065 · GitHub por la CLI `gh` de la persona, sin leer su token: `IHostingProvider` con capacidades, solo `gh api` con un catálogo cerrado y el entorno por lista blanca

- **Estado:** propuesta · 2026-11-13, pendiente de las decisiones 1, 6 y 9 de [docs/21 §14](../21-fase-10-git-remoto.md#14-decisiones-que-necesitan-a-la-persona)
  (se redacta con la opción recomendada de cada una). Recoge D-09 a D-14 y D-19 de [docs/21](../21-fase-10-git-remoto.md) y
  las D-25 y D-28 de [docs/16](../16-ampliacion-plataforma-de-proyectos.md). Ocupa, con [ADR-0067](0067-reconciliacion-por-sondeo-con-buzon.md),
  el lugar del ADR-0042 que docs/16 reservó. **Amplía** [ADR-0003](0003-integracion-por-clis-oficiales.md) (las plataformas,
  como la IA, por sus CLIs oficiales). El P0 fija los puertos; WP2 implementa y añade aquí lo que haga.
- **Contexto:**
  - F10-1 pide una interfaz común con capacidades declaradas; F10-2, GitHub por `gh` **sin leer su token**.
  - Regla fija del proyecto: Agent Studio nunca lee, copia ni registra credenciales; el texto libre va por la entrada
    estándar, nunca como argumento.
  - Leído en la ayuda de `gh` 2.96.0 (sin red): las órdenes de alto nivel reciben el título como argumento
    (`gh pr create --title`, `gh pr merge --subject`) y trabajan sobre el repositorio de la carpeta actual; no enseñan
    `ETag` ni las cabeceras de límite. `gh api` admite `-X`, `-H`, `-i` (línea de estado y cabeceras) e `--input -` (el
    cuerpo por la entrada estándar). `-F clave=@valor` lee un fichero y los marcadores `{owner}`, `{repo}` y `{branch}`
    salen de la carpeta actual o de `GH_REPO`. `gh` tiene telemetría y un aviso de actualización que usan la red.
    `GH_TOKEN` y `GITHUB_TOKEN` tienen preferencia sobre la sesión guardada; `GH_HOST`, `GH_REPO` y `GH_CONFIG_DIR`
    redirigen; `GH_DEBUG=api` vuelca el tráfico HTTP. `gh auth status` tiene `--show-token`. El código de salida 4
    significa «hace falta autenticarse».
- **Decisión:**
  1. **`IHostingProvider`,** un puerto por plataforma, con `HostingCapabilities`: lo que el adaptador sabe hacer con una
     versión de su CLI, estrechado por lo que el repositorio permite. La interfaz se construye a partir de las
     capacidades: lo no declarado no tiene botón. **El puerto no tiene ningún miembro que envíe una revisión, apruebe o
     fusione como administrador** (ADR-0066).
  2. **GitHub, solo por `gh api`,** con `--hostname github.com`, una versión fija de la API en una cabecera y un
     **catálogo cerrado** (`GitHubEndpoint`) que fija el método y la plantilla de la ruta de cada punto (docs/21 D-10).
     Quien llama pide una entrada con valores tipados; no puede pasar una ruta ni un argumento.
  3. **La ruta la construye un solo constructor** desde tipos validados (`RemoteRepository`, número de PR, commit de 40
     hexadecimales, rama validada), con cada segmento escapado y la ruta final comprobada contra su plantilla. **La
     consulta, con otro constructor único,** que escapa cada valor (una rama admite `&`, `#`, `%`, `+`, `{` y `}`).
     **Ninguna llave llega a `gh`:** sustituye `{owner}`, `{repo}` y `{branch}` desde la carpeta actual o `GH_REPO`;
     `RemoteBranch` las rechaza, el escapado las convierte, y una prueba exige que ningún argumento contenga `{` ni `}`.
     Las listas largas llevan un `page` tipado (hasta tres páginas) y, si aún llegan llenas, se marcan «truncadas» y se
     dice.
  4. **Los datos de una escritura van en un JSON por la entrada estándar** (`--input -`). Ningún texto libre es un
     argumento. Nunca `-F` (lee un fichero si el valor empieza por `@`, y convierte tipos) ni `-f` (cadena literal, pero el
     dato iría como argumento); nunca `--paginate`, `--cache`, `--jq`, `--template` ni `--verbose`.
  5. **`gh` arranca en una carpeta neutra** (`%LOCALAPPDATA%\AgentStudio\hosting\cwd`), nunca dentro de un repositorio,
     y con `gh.exe` resuelto fuera de los proyectos abiertos.
  6. **Entorno por lista blanca.** No pasan `GH_TOKEN`, `GITHUB_TOKEN`, `GH_ENTERPRISE_TOKEN`,
     `GITHUB_ENTERPRISE_TOKEN`, `GH_HOST`, `GH_REPO`, `GH_DEBUG` ni `DEBUG`. Se ponen
     `GH_PROMPT_DISABLED`, `GH_NO_UPDATE_NOTIFIER`, `GH_NO_EXTENSION_UPDATE_NOTIFIER`, `GH_TELEMETRY=false`,
     `DO_NOT_TRACK`, `NO_COLOR` y `GH_SPINNER_DISABLED`. **Pasan seis variables de la persona, de forma explícita:**
     `XDG_CONFIG_HOME` y `GH_CONFIG_DIR` (dónde tiene `gh` su configuración; solo si son una ruta absoluta fuera de los
     proyectos abiertos) y `HTTPS_PROXY`, `HTTP_PROXY`, `NO_PROXY` y `SSL_CERT_FILE` (`gh` no tiene un equivalente al
     `http.proxy` global de git). Las mismas variables sin red van también en `gh --version` de Auto Doctor: el aviso de
     actualización y la telemetría salen con cualquier orden.
  7. **El token no se lee, no se pasa y no se mira.** Ninguna orden `gh auth` (ni `token`, ni `status`), ningún
     `--show-token`; no se lee la configuración de `gh`, el Administrador de credenciales ni el almacén del gestor de git.
     La sesión se **deduce** de la primera petición real (código de salida 4 o 401 → «inicia sesión con `gh auth login`»);
     la cuenta que publica sale de `GET /user` (solo `login`).
  8. **La salida de `gh` es un dato no fiable.** Se lee por estructura (línea de estado, cinco cabeceras por lista y un
     JSON con campos por lista, tipos y topes; 20 s y 1 MB). El texto del remoto (títulos, nombres de checks o de
     entornos) nunca entra en el registro ni en un mensaje del sistema; vive en una caché local, acotado y limpio, y se
     pinta como dato. Ninguna dirección de una respuesta se abre. El error estándar no se guarda ni se muestra.
  9. **El texto del PR lo compone el sistema** con una plantilla en recursos, con cada dato variable dentro de un tramo de
     código (ahí GitHub no interpreta menciones, referencias ni enlaces); la persona puede editarlo; **ningún texto de un
     agente** va en él. Lo enviado queda como evidencia con su SHA-256.
  10. **Versiones:** `gh --version` (sin red) entra en la puerta de Auto Doctor. Probada, la 2.96.0, que es también la
      mínima hasta que se confirme desde cuándo existen `--input`, `-i` y el código de salida 4; por debajo, ninguna
      capacidad; por encima, «versión no probada».
  10 bis. **Un 304 es un error para `gh`** (su manual: cualquier estado por encima de 299; código de salida distinto de 0
      y `gh: HTTP 304` en el error estándar). El lector no se fía del código de salida para saber si hubo respuesta: lee
      la línea de estado de la salida estándar (con `-i`), y nunca toma el error estándar por cuerpo. *Probable, sin
      ejecutar.*
  11. **La costura para Azure DevOps** (F10-9), sin construirlo: `HostingProviderKind.AzureDevOps` sin adaptador, y
      modelos comunes de revisiones (que cubren sus votos numéricos), reglas de rama y checks. Entra con su propio ADR.
- **Las respuestas de las pruebas son fixtures escritas a mano** desde la documentación oficial y la ayuda de `gh`
  2.96.0, con la forma de `-i`, y así lo dicen (`handmade-`). Se sustituyen por capturas reales cuando la persona autorice
  la prueba en vivo sobre un repositorio privado de prueba que cree ella.
- **Alternativas descartadas:** las órdenes de alto nivel de `gh` (el título como argumento, sin cabeceras, sobre el
  repositorio de la carpeta); un `HttpClient` propio contra la API (haría falta el token: es lo que hace el doctor de
  skills con datos públicos, ADR-0063, y aquí no vale); `gh auth status` para saber la sesión (red, familia `auth` y
  `--show-token` a un argumento de distancia); una interfaz por operación en vez de capacidades.
- **Lo que se nota:** quien use `gh` solo con `GH_TOKEN` en el entorno, o con su configuración en otra carpeta
  (`GH_CONFIG_DIR`), verá «sin sesión» en Agent Studio.
- **Residuales aceptados:** `gh` y su sesión son de confianza (un programa del mismo usuario que los cambie engaña al
  adaptador); GitHub ve la cuenta, la dirección IP y qué PR se consultan; todo lo publicado sale con la cuenta de la
  persona.
- **Sin verificar al escribirlo** (lo cierra WP2 y la prueba en vivo): la forma exacta de la salida con `-i`; el código de
  salida con un 304 y con un 4xx; que con una ruta completa `gh api` no lanza `git`; la versión mínima con `--input` y el
  código 4; y los campos reales de cada punto de la API.

## Lo que fija el P0 (2026-11-13)

- **`IHostingProvider` e `IHostingProviders`** (Application) tal como los fija el diseño: sin ningún miembro que envíe una revisión, apruebe o fusione como administrador, y sin parámetros que se llamen `force` o `admin` (una prueba de
  superficie lo exige). `HostingCapability` son marcas y `HostingCapabilities.NarrowedBy` recorta por repositorio (métodos de fusión, y sin escrituras si la cuenta no puede escribir o el repositorio está archivado); los tipos que usa
  (`RemoteRepositoryFacts`, `BranchRules`) viven en Domain.
- **Identificadores validados** en Domain (`RemoteRepository`, `RemoteBranch`, `PullRequestRef`, `RemoteLogin`): sin expresiones regulares, solo ASCII, y lo que no pasa se rechaza, no se corrige.
- **Auto Doctor sin red:** `DoctorTool.GhVersion` (`gh --version`) con `GH_NO_UPDATE_NOTIFIER=1`, `GH_NO_EXTENSION_UPDATE_NOTIFIER=1`, `GH_TELEMETRY=false`, `DO_NOT_TRACK=1` y `GH_PROMPT_DISABLED=1` en `DoctorEnvironment`
  (`GhOffline`); la lista blanca ya quita `GH_TOKEN`, `GH_HOST`, `GH_REPO`, `GH_CONFIG_DIR` y `GH_DEBUG`. `ToolOutputParsers.Version` lee `gh version 2.96.0 (2026-07-02)`.
- **Primer paso de §10.2, comprobado en el equipo del P0** (sin red ni cuenta, con esas cinco variables): `gh --version` da `gh version 2.96.0 (2026-07-02)`; `gh api --help` trae `--input`, `--method`, `--include`, `--header`,
  `--hostname`, `--paginate`, `--cache` y `-F/--field` con `@` para leer de un fichero; `git push -h` trae `--porcelain`, `--no-verify`, `--no-follow-tags`, `--recurse-submodules` y `--signed`. Coincide con §13.

## Lo que fija WP2 (2026-11-13)

Implementado en `src/AgentStudio.Infrastructure/Hosting/{Cli,GitHub,Diagnostics}`; probado sin lanzar `gh` con un ejecutor de guion y respuestas **escritas a mano** (`tests/AgentStudio.Tests/Infrastructure/Hosting/GitHub/Fixtures/README.md`). En esta máquina solo se ejecutó
`gh --version` y la ayuda (`gh api --help`, `gh help exit-codes`).

- **Una sola orden:** `gh api --hostname github.com -X <MÉTODO> -i -H "Accept: application/vnd.github+json" -H "X-GitHub-Api-Version: 2022-11-28" [-H "If-None-Match: <etag>"] [--input -] <ruta>`. La ruta empieza por `/repos/<o>/<r>/…` o es `/user`
  (con la barra inicial, como dice D-10) y la comprueba `GhArguments.IsAllowedPath` en el lanzador además del constructor (`GitHubApiPath`): ni `.` ni `..` (tampoco codificados), ni llaves, ni espacios; la consulta, solo de caracteres no reservados y `%XX`.
  El `ETag` solo pasa con su forma exacta (`W/` opcional y entre comillas de 1 a 128 caracteres ASCII visibles); si no, la petición va sin condición.
- **El catálogo** (`GitHubEndpoint`, 25 entradas) es el de D-10 más dos que el diseño citaba sin nombre propio: `Branch` (`GET …/branches/{rama}`: el flag de protección, la cabeza de una rama y si existe) y `Commit`/`CommitList` (los padres de la fusión, los
  commits que deja un rebase y la búsqueda de un revert). **Sin `WorkflowRuns`:** ningún miembro del contrato ni campo de `RemoteSnapshot` lo entrega, y `ReadWorkflowRuns` no se declara. Una prueba exige que cada entrada aparezca en la batería y que cada orden
  lanzada sea exactamente una de la lista.
- **Quién lanza:** solo `GhRunner` (`gh.exe` por `ExecutableSearch` fuera de las raíces no fiables; nunca un `.cmd`; carpeta neutra; entorno de `GhEnvironment`; 20 s y 1 MB por salida; el cuerpo por la entrada estándar). **Siempre por `GhQueue`**
  (`SerialQueue`, un consumidor), que además guarda el estado del límite. La versión de `gh` no la lanza el adaptador: `GitHubCli.ReadAsync` pregunta a `IDoctorToolRunner` (la puerta de Auto Doctor, con `GhOffline`), y la usan por igual
  `ProbeAsync` y las comprobaciones de Auto Doctor, para que digan lo mismo.
- **Cómo se lee la respuesta:** la primera línea de la salida estándar (`HTTP/…`) manda; el código de salida solo cuenta si no hay línea de estado (4 → sesión; cualquier otro silencio → no se llegó al servidor; texto sin forma de respuesta → respuesta inválida).
  LF y CRLF valen. De las cabeceras, cinco. El error estándar no se lee en nada.
- **Límites:** 429, o 403 con `x-ratelimit-remaining: 0` o `retry-after`, paran **todo** lo que llegaría a GitHub (escrituras incluidas) hasta la hora de las cabeceras (mínimo un segundo después), recortada a dos horas; sin cabeceras, 1, 2, 5, 15 y 60 minutos;
  una respuesta buena reinicia la escalera. `GhQueue.RestoreRateLimit` carga la espera guardada (también recortada) y `RateLimitedUntil` la devuelve, para que el almacén de WP3 la guarde.
- **Las capacidades** salen de la versión: 2.96.0 (la probada y la mínima) declara todo salvo `ReadWorkflowRuns`; una posterior, lo mismo con «versión no probada»; una anterior, nada. Lo no declarado devuelve `Unsupported` sin lanzar nada.
- **Códigos de rechazo** (`GitHubRefusal`, nunca un texto del remoto): `gh.not-installed`, `capability.not-declared`, `answer.invalid`, `account.login-invalid`, `remote.moved`, `server.error`, `http.<estado>`, `text.title-invalid`, `text.body-invalid`, `commit.invalid`,
  `merge.method-not-declared`, `merge.blocked`, `merge.head-moved`, `branch.not-run-branch`, `branch.head-differs`, `reviewers.invalid`, `verdict.run-invalid`. Los textos para la pantalla son de WP5.
- **Sin verificar** (la prueba en vivo, `Live/README.md`): todo lo que dice §13 de docs/21 para `gh api`; en particular, que `-i` deje leer la línea de estado de un 304 (y con qué código de salida). **Si no la dejara,** el lector da `NoResponse` o `InvalidAnswer` y el sondeo no
  puede ser condicional: cada vuelta sería una lectura completa.