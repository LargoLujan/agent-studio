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
     hexadecimales, rama validada), con cada segmento escapado y la ruta final comprobada contra su plantilla.
  4. **Los datos de una escritura van en un JSON por la entrada estándar** (`--input -`). Ningún texto libre es un
     argumento. Nunca `-f`, `-F`, los marcadores `{owner}`/`{repo}`/`{branch}`, `--paginate`, `--cache`, `--jq`,
     `--template` ni `--verbose`.
  5. **`gh` arranca en una carpeta neutra** (`%LOCALAPPDATA%\AgentStudio\hosting\cwd`), nunca dentro de un repositorio,
     y con `gh.exe` resuelto fuera de los proyectos abiertos.
  6. **Entorno por lista blanca.** No pasan `GH_TOKEN`, `GITHUB_TOKEN`, `GH_ENTERPRISE_TOKEN`,
     `GITHUB_ENTERPRISE_TOKEN`, `GH_HOST`, `GH_REPO`, `GH_CONFIG_DIR`, `GH_DEBUG` ni `DEBUG`. Se ponen
     `GH_PROMPT_DISABLED`, `GH_NO_UPDATE_NOTIFIER`, `GH_NO_EXTENSION_UPDATE_NOTIFIER`, `GH_TELEMETRY=false`,
     `DO_NOT_TRACK`, `NO_COLOR` y `GH_SPINNER_DISABLED`.
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
  10. **Versiones:** `gh --version` (sin red) entra en la puerta de Auto Doctor. Probada, la 2.96.0; por debajo de la
      mínima, ninguna capacidad; otra, «versión no probada».
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
