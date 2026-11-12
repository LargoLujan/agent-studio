# ADR-0018 · Modelo de amenaza de la orquestación (Fase 3)

- **Estado:** aceptada · 2026-10-18. El usuario aceptó expresamente los residuales del punto 2 tras la novena ronda
  adversarial, que no dejó ningún hallazgo abierto dentro del modelo (propuesta el 2026-10-17). Ordena las
  revisiones de seguridad de la Fase 3 ([08](../08-plan-y-criterios-de-aceptacion.md#fase-3--orquestación)) y fija contra
  qué se juzga cada hallazgo. No cambia ninguna decisión anterior: dice qué protegen
  [ADR-0010](0010-estado-derivado-del-registro.md), [ADR-0012](0012-worktrees-fuera-del-repo.md) y
  [ADR-0017](0017-verificacion-aislada-baja-integridad.md), y frente a quién.
- **Contexto:**
  - Agent Studio es de uso personal: corre con la cuenta del usuario, en su máquina, sobre sus repositorios.
  - Tres rondas adversariales con Codex encontraron cada una unos ocho hallazgos nuevos. Muchos exigen un proceso del
    mismo usuario que corre **a la vez** que Agent Studio con integridad Medium. Ese proceso puede de todos modos
    reescribir el registro de decisiones y su ancla, un riesgo residual que ya estaba aceptado.
  - Sin un modelo escrito no hay criterio de cierre: cada ronda puede seguir encontrando variantes de ese mismo riesgo
    aceptado.
- **Decisión:**
  1. **Adversario dentro del modelo** (sus ataques hay que cerrarlos):
     - **El contenido del repositorio:** configuración de git, `.gitattributes`, scripts versionados, repositorios
       anidados y submódulos, ficheros que el agente puede escribir en su worktree.
     - **La salida de los agentes:** código, informes, refs que mueve un agente, y lo que ejecuta la verificación sobre
       ese código.
     - **Los agentes con sus capacidades por defecto:** el ejecutor sin shell (Read, Grep, Glob, Edit, Write), el
       orquestador y el juez de solo lectura.

     Lo que estos pueden hacer **antes o después** de un paso (dejar un fichero, una junction o una configuración
     plantada, reescribir una fila de SQLite con la app cerrada) también está dentro del modelo.
  2. **Fuera del modelo** (residuales aceptados, documentados):
     - **Un proceso concurrente del mismo usuario con integridad Medium.** Ejemplos: un agente al que el usuario dio
       `Bash`, un paso marcado `isolation: none` o un programa ajeno a Agent Studio. Puede:
       - reescribir el registro y su ancla;
       - cambiar una junction, una ref o un fichero incluido por la configuración entre la comprobación y el uso;
       - tocar el almacén de objetos o los padres de una carpeta mientras se extrae una copia;
       - sustituir la cadena de carpetas de `verify.lock`.

       Esas carreras se mitigan donde es barato (CAS, `--no-deref`, rutas finales, relecturas), pero no se cierran:
       cerrarlas exige un aislamiento del sistema operativo, que se evalúa en F7-4.
     - **Las lecturas y la red de un proceso Low.** La verificación aislada impide escribir fuera de su área, no leer ni
       salir a la red (ADR-0017).
     - **Un PATH controlado por otro programa del usuario.** Agent Studio resuelve los ejecutables por el PATH del
       usuario y no lo audita.
     - **Lo que cada CLI escribe en su propio perfil** (2026-10-18). Claude Code guarda ahí la memoria de sus agentes
       (`~/.claude/agent-memory`) y los ficheros de plan y de scratchpad de la sesión, sin pasar por las reglas de ruta.
       Agent Studio no los lee y no llegan al repositorio, al registro, a las evidencias ni a la verificación. Que influyan
       en sesiones posteriores del mismo CLI no toca ningún activo protegido: lo que produzca un agente se verifica y lo
       revisa un juez de otro vendor que solo lee el dossier.
  3. **Capacidad que cambia el modelo.** Dar a un agente `Bash`, usar Codex como ejecutor que escribe o marcar un paso
     `isolation: none` crea un escritor Medium dentro de la ejecución. Esa ejecución queda fuera del modelo para lo que
     haga ese escritor:
     - el plan lo avisa antes de aprobarlo;
     - la comprobación del repositorio del usuario se vuelve más estricta (las carpetas ignoradas que no se pueden
       verificar bloquean, ADR-0012).
  4. **Cómo se juzga un hallazgo.** Una revisión dice, para cada hallazgo, si está dentro del modelo:
     - si exige un proceso concurrente del mismo usuario con integridad Medium, es **residual**, no abierto;
     - si un adversario del punto 1 lo consigue con las capacidades por defecto, está **abierto** hasta corregirlo.

     Las mitigaciones baratas de residuales se pueden tomar sin cambiar su clasificación. Por ejemplo, comprobar la
     etiqueta de un `verify.lock` que ya existe.

     **Enlaces bajo el directorio git del usuario y bajo `.agentstudio` (2026-10-18).** Desde la quinta hasta la octava
     ronda, cada revisión encontró un destino más de una misma clase: un enlace (junction, simbólico o duro) plantado
     antes bajo el directorio git común, por el que luego escribe un comando interno o el filtro de git-lfs.

     - **Quién puede plantarlo.** Ningún adversario del punto 1:
       - un ejecutor por defecto no puede: Write y Edit no crean reparse points ni enlaces duros, y el confinamiento
         medido lo deja fuera de `.git`;
       - el contenido del repositorio tampoco: un checkout nunca crea entradas bajo `.git`, y los enlaces simbólicos
         versionados quedan en el worktree.

       Solo puede plantarlo un escritor Medium, sea una ejecución anterior con `Bash`, Codex escribiendo o
       `isolation: none`, sea otro programa. El punto 3 ya deja fuera del modelo lo que haga ese escritor, también
       cuando el efecto aparece en una ejecución posterior.
     - **La clase se protege igualmente con un único control** (ADR-0012, adenda del recorrido del directorio común).
       Antes de cada comando de contenido y de la entrega se recorre el directorio común entero y se rechaza:
       - cualquier reparse point;
       - cualquier fichero con más de un nombre, salvo los objetos inmutables;
       - cualquier carpeta que no se pueda listar.

       En `.agentstudio`, todos los escritores pasan por `ProjectStorageGuard`.
     - **Cómo se juzga.** Una revisión da por CERRADO cualquier destino de esas dos carpetas que el recorrido o la
       guarda cubren. Un hueco de la clase se describe como una ruta que el recorrido salta o una comprobación que
       omite, no como un destino nuevo.
  5. **Cierre de la Fase 3.** La seguridad de la Fase 3 se da por cerrada para uso personal cuando una ronda adversarial,
     juzgando contra este ADR, no deja ningún hallazgo abierto y el usuario acepta los residuales del punto 2.
- **Consecuencias:**
  - Las rondas siguientes tienen un criterio de cierre. Un hallazgo nuevo se clasifica antes de corregirlo.
  - No se usan agentes con `Bash` ni pasos sin aislamiento sobre repositorios de terceros. Lo dice el aviso del plan y lo
    repite este ADR.
  - El aislamiento fuerte del sistema operativo (Windows Sandbox o AppContainer), que cerraría los residuales del punto 2,
    se evalúa en F7-4.
  - **Medición · 2026-10-17 · Claude Code 2.1.290 no confina las escrituras del ejecutor a su worktree.** Lo midió
    `ClaudeWriteConfinementLiveTests`, una sola vez: el CLI real por el adaptador del proyecto, con el perfil exacto de un
    ejecutor que escribe (Read, Grep, Glob, Edit y Write en `acceptEdits`, sin `Bash`, sin red y sin `--add-dir`; lo fija
    `MeasuredExecutorProfileTests`), haiku y un worktree de git nuevo como directorio de trabajo. Con `Write` creó el
    fichero de fuera por su ruta absoluta y el de `../`, sin ninguna denegación de permisos ni error de herramienta, y
    también el de dentro. Su respuesta: los tres intentos funcionaron.
    - **Lo que cambia ya.** `UserRepositoryGuard.CanWriteAtMediumIntegrity` trata como escritor Medium a todo ejecutor
      que escribe ficheros, salvo uno de Claude Code en una versión con el confinamiento medido. Hoy no hay ninguna
      (`SupportedVersions.WriteConfinementMeasured` está vacía), así que toda ejecución con un ejecutor que escribe usa la
      comprobación estricta de ADR-0012: las carpetas ignoradas que no se pueden verificar bloquean. Gemini, y cualquier
      CLI cuyo confinamiento no se ha medido, cuentan igual (fallo cerrado). La versión instalada se pregunta al arrancar
      y al reanudar (`IWriteConfinementProbe`, con `--version`). Una versión solo entra en la lista tras pasar esa prueba
      con el perfil que da el motor.
    - **Coste.** En un repositorio cuyas carpetas ignoradas pasan del presupuesto de la captura (`bin/` y `obj/`
      grandes), las ejecuciones con un ejecutor que escribe se bloquean hasta que haya confinamiento.
    - **Abierto, por clasificar.** El ejecutor por defecto, un adversario del punto 1 con sus capacidades por defecto,
      escribe fuera de su worktree: en otros worktrees de la ejecución, en las carpetas de la app
      (`%LOCALAPPDATA%\AgentStudio`, anclas incluidas) y en el repositorio del usuario. La comprobación del repositorio
      vigila el último; nada vigila los dos primeros. El aviso del plan del punto 3 solo salta hoy con `Bash`, Codex o
      `isolation: none`. **Resuelto el 2026-10-18:** la segunda medición (más abajo) confina al ejecutor a su worktree, y
      el aviso del plan nombra ya a todo escritor a integridad media con la misma detección que la compuerta (A-04).
    - **Hipótesis por medir, no hechos.** El modo `dontAsk` con reglas de ruta solo para el worktree
      (`Edit(<worktree>/**)`, `Write(<worktree>/**)`): una regla de denegación no admite excepciones, así que tendría que
      ser una lista de permitidos. O el aislamiento de F7-4.
    - **Segunda medición · 2026-10-18 · Claude Code 2.1.290 con `dontAsk` y reglas de ruta sí se queda en su
      worktree.** Confirma la hipótesis anterior y sustituye a «Lo que cambia ya»: la compuerta vuelve a relajarse, pero
      solo con este lanzamiento.
      - **La causa de la fuga**, leída en el código del CLI 2.1.290 (el JavaScript que lleva `claude.exe`, sin
        ejecutarlo): una regla de toda la herramienta (`Write` a secas en `--allowedTools`) convierte en `allow` el `ask`
        de la comprobación de ruta de la propia herramienta. No era `acceptEdits`, que sin regla solo acepta lo que cae
        dentro de los directorios de trabajo.
      - **El lanzamiento** (`ClaudeCodeArguments`):
        - `--permission-mode dontAsk`: lo que ninguna regla permite se deniega.
        - Ninguna herramienta de ficheros va a secas.
        - `Read(//c/<carpeta>/**)` para el directorio de trabajo, cada `--add-dir` y la carpeta de skills.
        - `Edit(//c/<worktree>/**)` si el perfil escribe.
        - Siempre denegadas: `NotebookEdit`, `EnterWorktree`, `ExitWorktree` y `Agent`/`Task`. Según el código,
          `EnterWorktree` sin ruta se permite solo y crea un worktree y una rama en el repositorio.
        - Siguen `--setting-sources ""`, `--strict-mcp-config` y `--settings` con `disableAllHooks`. Ni `--settings` ni
          otra fuente añaden reglas: no hay configuración gestionada en la máquina.
      - **Sintaxis en Windows.** `//c/Users/…/wt/T1-…/**`: la letra de la unidad tras `//`, barras normales, y la ruta tal
        cual, con espacios y acentos (la carpeta de la prueba es `as git tést …`). El CLI la lee con raíz `C:\`. También
        acepta `C:/…`; `//C:/…`, con dos puntos, no es una ruta absoluta para él.
        - Las reglas `Edit(...)` gobiernan Write, Edit y NotebookEdit; 2.1.290 no consulta reglas `Write(...)`. El control
          positivo escribió con Write sin ninguna.
        - Las reglas `Read(...)` gobiernan Read, Grep y Glob. Dentro de sus directorios de trabajo, el CLI lee sin regla.
        - `--allowedTools` separa por espacios y comas fuera de paréntesis, así que una ruta con espacios sigue siendo
          una regla.
        - `Tool()` y `Tool(*)` cuentan como la herramienta a secas. Por eso el lanzamiento rechaza la regla de ruta propia
          de una herramienta de ficheros del perfil.
      - **Sabido por el código, no medido.**
        - `../x` nunca casa con una regla. La ruta relativa a la raíz de la regla empieza por `..` y se descarta.
        - Tampoco casa con `./**`, que no se usa: su raíz sigue al directorio actual de la sesión.
        - Las rutas de Windows sospechosas se deniegan en `dontAsk`: nombre corto con `~` y dígito, `\\?\`, `:` después
          de la unidad, UNC.
        - Las mayúsculas no se midieron. La raíz de una regla de permitir se compara sin plegarlas, así que otra grafía
          del worktree puede denegarse (falla cerrado).
      - **Ejecuciones.** `ClaudeWriteConfinementLiveTests`, haiku (`claude-haiku-4-5-20251001`),
        `AGENTSTUDIO_PROVIDER_TESTS=1` y `--filter-class`. En un solo mensaje, siete llamadas en paralelo: escribir y leer
        dentro (controles positivos), escribir fuera por ruta absoluta y por `../`, y leer un centinela de fuera por ruta
        absoluta, por `../` y con Grep.
        - **1** (8 turnos, 0,07 USD), con la sintaxis `//c/…`:
          - Escritura fuera, por ruta absoluta y por `../`: denegada por el CLI.
          - Lectura fuera, por ruta absoluta, por `../` y con Grep: denegada. El contenido del centinela no aparece en el
            flujo ni en la respuesta.
          - Escritura dentro: sí. Lectura dentro: sí.
        - **2** (8 turnos, 0,04 USD), con el código final: lo mismo.
        - En las dos, las cinco salidas llegan en `permission_denials` («Permission to use … has been denied because
          Claude Code is running in don't ask mode»).
        - El repositorio del usuario (estado, HEAD, refs e índice) queda igual.
        - Ni siquiera se ofrecen `Bash`, `NotebookEdit`, `EnterWorktree`, `ExitWorktree`, `Task`, `WebFetch` ni
          `WebSearch`.
        - El modelo resolvió `../` por su cuenta y mandó rutas absolutas. Write y Read piden rutas absolutas, y el CLI
          expande una relativa contra el directorio de trabajo antes de comprobarla.
      - **Lo que cambia.**
        - `SupportedVersions.WriteConfinementMeasured` lista 2.1.290 junto con el lanzamiento medido.
        - La huella, `ClaudeCodeArguments.WriteConfinementLaunch`, la calcula `Build` para el perfil medido. Si cambia lo
          que la sesión puede hacer, la lista queda vacía y la compuerta vuelve a fallar cerrada hasta que se mida el
          lanzamiento nuevo.
        - `UserRepositoryGuard.CanWriteAtMediumIntegrity` solo trata como confinado a un ejecutor de Claude cuyo perfil no
          pasa del medido (`MeasuredWriteConfinement`: Read, Grep, Glob, Edit y Write, sin shell, red ni carpetas
          añadidas). Con cualquier otra herramienta preaprobada sigue siendo un escritor Medium.
      - **Abierto, por clasificar.**
        - El CLI ofrece más herramientas que las de ficheros, como `Monitor`, `Workflow`, `CronCreate`, `RemoteTrigger`,
          `Artifact` o `PushNotification`. En `dontAsk` se deniegan las que piden permiso, pero no se midió cuáles se
          permiten solas. El propio mensaje de denegación invita al modelo a probar con otras herramientas.
        - Sin ninguna regla, el CLI deja escribir sus propios ficheros fuera del worktree: notas `.md` de memoria de
          agente en `~/.claude/agent-memory`, y los ficheros de plan y de scratchpad de la sesión. No son el repositorio,
          otros worktrees ni `%LOCALAPPDATA%\AgentStudio`.
        - Próximo paso propuesto, sin implementar: `--tools`, con la lista exacta de herramientas integradas, o
          `--restricted`, que confina las herramientas de ficheros a los directorios de trabajo en cualquier modo. Los dos
          hay que medirlos antes: cambian qué herramientas existen y pueden afectar a `--json-schema` y a las skills.
    - **Tercera medición · 2026-10-18 · con `--tools` la sesión solo tiene las herramientas del perfil.** Cierra lo que la
      segunda dejó abierto sobre las demás herramientas del CLI.
      - **Qué hace `--tools` en 2.1.290**, leído en su código:
        - Cada herramienta integrada que no se lista pasa a ser una regla de denegación (origen `toolsNarrowing`), y una
          denegación gana a cualquier regla de permitir.
        - En Windows retiene además PowerShell.
        - Una entrada `preset:default` las devuelve todas. Por eso el lanzamiento rechaza una herramienta del perfil que
          nombre un preset.
      - **El lanzamiento** añade `--tools` con lo que necesita el perfil:
        - Read, Grep y Glob siempre.
        - Edit y Write si escribe.
        - Bash solo con la shell, y entonces el ejecutor sigue contando como escritor Medium.
        - WebFetch y WebSearch con la red.
        - StructuredOutput si se pide esquema: por ahí llega la respuesta estructurada.

        Las reglas de ruta y las denegaciones explícitas siguen como segunda línea. La huella del lanzamiento incluye
        `--tools`, así que entre el cambio y esta medición la lista medida estuvo vacía y la compuerta, cerrada.
      - **Nunca `Skill`.** Mientras una skill está activa, las herramientas de su `allowed-tools` pasan a ser reglas de
        permitir, y `SkillMaterializer` copia el frontmatter tal cual. 11 de las 126 skills del índice lo declaran, y 3
        conceden `Write` o `Edit` a secas, que se saltarían las reglas de ruta.
        - Consecuencia: en Claude las skills no se pueden invocar, aunque estén activadas, y
          `SkillVisibilityLiveTests` falla.
        - Para recuperarlas: que la materialización quite `allowed-tools` y `disallowed-tools`, ofrecer `Skill` cuando
          haya carpeta de skills y volver a medir.
      - **Ejecuciones.** Es la misma prueba, con esquema como el del ejecutor, y pide además Monitor y Workflow.
        - **A** (8 turnos, 0,04 USD): ofrecidas exactamente Edit, Glob, Grep, Read, StructuredOutput y Write.
          - Escritura fuera, por ruta absoluta y por `../`: denegada por el CLI.
          - Lectura fuera, por ruta absoluta y con Grep: denegada. El centinela no aparece en el flujo.
          - Escritura y lectura dentro: sí. La respuesta estructurada llegó.
          - Monitor y Workflow: el modelo los dio por inexistentes.
          - Falló una sola comprobación: el modelo leyó el centinela una vez, porque las dos rutas llevan al mismo
            fichero. Esa comprobación pasa a ser una nota.
        - **B** (9 turnos, 0,03 USD): pasa, con el mismo resultado y las dos lecturas del centinela denegadas.
      - **Sigue abierto:**
        - Los ficheros internos que el CLI escribe sin regla: `~/.claude/agent-memory`, y los ficheros de plan y de
          scratchpad de la sesión.
        - La decisión sobre las skills.
    - **Cuarta medición · 2026-10-18 · las skills vuelven a Claude sin conceder herramientas.** Resuelve la decisión
      sobre las skills que la tercera dejó abierta: manda el perfil del agente, y una skill nunca concede más.
      - **La materialización** (`SkillMaterializer`, con `SkillFrontmatter.ForSession`) reescribe cada `skill.md` de la
        copia, en cualquier mayúscula y a cualquier profundidad.
        - La cabecera se rehace solo con las claves descriptivas: `name`, `description`, `when_to_use`, `argument-hint`,
          `version`, `license` y `compatibility`. También `disable-model-invocation` y `user-invocable`, si valen `true`
          o `false`.
        - El cuerpo queda igual, y el origen solo se lee.
        - Se quitan, entre otras, `allowed-tools`, `disallowed-tools`, `hooks` (y los eventos de hook en la raíz),
          `context`, `agent`, `background`, `shell`, `model`, `effort`, `fallback`, `paths`, `arguments` y `metadata`.
          2.1.290 las lee todas. `model` y `effort` no conceden permisos: se quitan porque el modelo lo decide el perfil.
        - Es una lista de claves permitidas, no de prohibidas, porque 2.1.290 cierra el frontmatter en el primer `---`,
          aunque esté dentro de un valor. Una cabecera que el parser del catálogo no ve puede serlo para el CLI. Por eso
          la cabecera original nunca llega a la sesión, y cada `-` de un valor que contiene `---` se escribe `\x2D`.
        - Vale también para Codex y Gemini (`.agents/skills`).
      - **El lanzamiento** solo ofrece `Skill` en `--tools` si hay carpeta de skills, y el materializador no crea una sin
        skills.
        - La huella se toma con skills, el lanzamiento más amplio: `Skill`, `--plugin-dir <skills>` y
          `Read(<skills>/**)`.
        - Una ejecución sin skills tiene estrictamente menos.
      - **Ejecuciones** (haiku):
        - **1** · `SkillVisibilityLiveTests` de Claude (3 turnos, 0,03 USD):
          - `--tools "Read Grep Glob Skill"`; ofrecidas exactamente Glob, Grep, Read y Skill.
          - La skill aparece en `init.skills`, se invoca y da sus dos códigos.
        - **2** · `ClaudeWriteConfinementLiveTests`, con una skill cuyo origen declara `allowed-tools: Write, Bash`
          (11 turnos, 0,06 USD):
          - La cabecera de la sesión solo tiene `name` y `description`.
          - La skill se invocó primero, en su propio mensaje.
          - Después, el CLI denegó las escrituras y lecturas fuera; dentro funcionaron.
          - Ofrecidas exactamente Edit, Glob, Grep, Read, Skill, StructuredOutput y Write; Bash, Monitor y Workflow no
            existen.
      - **Clasificado como residual** (punto 2): los ficheros internos que el CLI escribe sin regla en su perfil
        (`~/.claude/agent-memory`, y los ficheros de plan y de scratchpad de la sesión).
  - **`verify.lock` que ya existe (mitigación barata del punto 4, 2026-10-17): se rechaza, no se sustituye.** La etiqueta
    del cerrojo (Medium que niega escribir, leer y ejecutar a un nivel menor) solo se pone al crear el fichero. Ahora se
    lee por el mismo handle que lo sostiene cada vez que se toma: si es otra (ninguna, que Windows lee como Medium con solo
    `NW`, o una Low), la verificación aislada no se prepara y el mensaje pide borrar el fichero si no hay ninguna
    verificación en marcha.
    - No se reetiqueta ni se borra para crearlo de nuevo. Agent Studio siempre lo ha creado etiquetado, así que uno sin
      la etiqueta lo hizo otro proceso, y Agent Studio solo etiqueta lo que acaba de crear
      ([ADR-0017](0017-verificacion-aislada-baja-integridad.md)). Borrar y volver a crear dejaría además un hueco entre
      los dos pasos.
    - Sigue siendo un residual del punto 2: un proceso Medium del usuario puede sustituir el fichero o su carpeta.

## Adenda · 2026-11-12 · el localizador no resuelve programas desde un proyecto (WP-0 de la Fase 6)

Cierra la mitad del residual «un PATH controlado por otro programa del usuario» (punto 2 de arriba): la que el propio
repositorio, o un agente que escribe en él, puede provocar. No cambia ninguna decisión anterior; precisa cómo se resuelve un
programa. Es la decisión 14 (a) del diseño de la Fase 6 (docs/20): vale para toda la app, no solo para Auto Doctor.

- **El problema.** `ExecutableLocator` solo descartaba las entradas **relativas** del PATH. Una carpeta del proyecto abierto que
  estuviera en el PATH con ruta absoluta dejaba resolver, y ejecutar, un `claude.cmd`, un `git.exe` o un `node.exe` plantado por
  el repositorio. Las sondas arrancaban en la carpeta que tuviera la app, y los pasos de verificación llevaban el nombre
  suelto (`dotnet`, `node`) a `CreateProcess`, que también mira la carpeta de la app, la actual y todo el PATH.
- **Un puerto: `IUntrustedRoots`** (`Application/Execution/ProcessContracts.cs`). Las carpetas de las que no se resuelve ningún
  programa. En la app lo implementa `OpenProjectRoots` (Desktop): la raíz de **cada proyecto abierto en la sesión** y la raíz de
  las copias de trabajo de las ejecuciones. Un proyecto no se retira al abrir otro: su ejecución sigue en segundo plano y su
  carpeta no es más fiable por eso. Se añade antes de que nada del proyecto arranque (también el último proyecto, antes de la
  primera comprobación de los programas).
- **La regla, en un solo sitio** (`ExecutableSearch`, con `UntrustedPaths`): se descarta toda entrada del PATH no
  cualificada y toda entrada o resultado que, **resueltos sus enlaces y uniones**, caiga dentro de una raíz. Se comparan las
  rutas finales de Windows (`GetFinalPathNameByHandle`: uniones a media ruta, nombres cortos 8.3, mayúsculas reales), sin
  prefijo `\\?\`, sin barras finales y con frontera de carpeta (`C:\proy` no contiene `C:\proy2`). Se resuelve también la raíz.
  Una raíz que contenga la carpeta del sistema (un proyecto abierto en `C:\`) se ignora: ocultaría todo el sistema.
- **Quién la aplica.** `ExecutableLocator` (los programas de los proveedores, git, Node, Python, el shell del terminal, el
  lanzador de Gemini y las sondas); `ProcessRunner` y `StreamingProcessRunner` para todo nombre **sin ruta** (los pasos de la
  verificación, aislados o no: `dotnet`, `node`, `cmd`…), en el orden de `CreateProcess` menos la carpeta de la app y la
  actual: carpeta del sistema, carpeta de Windows y PATH; y `LowIntegrityProcess`, que ya no recorre el PATH por su cuenta. Un
  nombre con ruta es decisión de quien llama y no se toca: los pasos aislados ejecutan a propósito herramientas sobre una copia
  del repositorio, pero el ejecutable (`dotnet`, `node`) se resuelve fuera del proyecto aunque la carpeta de trabajo sea la
  copia. Si un nombre solo está dentro de una raíz, no se arranca nada y el error lo dice.
- **Las sondas** (`CliProbe`, `CliWriteConfinementProbe` y la lectura de cuota de Codex) arrancan en una carpeta neutra y
  vacía de la app (`%LOCALAPPDATA%\AgentStudio\doctor\cwd`, la misma que usará Auto Doctor), nunca en el proyecto. Si el programa
  solo estaba dentro del proyecto, el diagnóstico lo dice («programa del agente», con la ruta y qué hacer) en vez de un
  «No instalado» mudo.
- **Lo que se nota.** Un programa instalado dentro de la carpeta de un proyecto abierto deja de encontrarse, aunque esa carpeta
  esté en el PATH, para las sondas, la verificación, git y el terminal (que busca su shell con el mismo localizador; lo que la
  persona escribe después en él no cambia). Una carpeta demasiado amplia (la personal, una unidad) no cuenta como raíz: ver la segunda adenda.
- **Residuales que se aceptan.** Un enlace duro es solo otro nombre del fichero y no se distingue. Entre la comprobación y el uso
  un proceso del usuario puede cambiar un enlace (la misma carrera de ADR-0017). (Lo de que git guardara la ruta quedó cerrado en la segunda adenda.) Una herramienta comprometida **fuera** del proyecto
  sigue engañando a todo lo anterior.

## Segunda adenda · 2026-11-12 · lo que el programa lanzado busca por su cuenta, las carpetas demasiado amplias y los residuales (WP-0, revisión)

Precisa la adenda anterior después de una revisión independiente. Cambia tres cosas y deja los residuales como están ahora.

- **El hijo ya no busca donde el proyecto puede plantar.** Resolver bien el programa no bastaba: un `codex.cmd` de npm, ya fuera del
  proyecto, llama a `node` por nombre, y `cmd.exe` busca primero en la carpeta actual (la copia de trabajo, que contiene lo que el
  repositorio versiona) y luego en el PATH heredado. Ahora todo hijo que lanzan `ProcessRunner` y `StreamingProcessRunner`
  (`ExecutableSearch.EnvironmentFor`) recibe un `PATH` sin entradas relativas y sin raíces no fiables y, si no está aislado,
  `NoDefaultCurrentDirectoryInExePath=1`. Lo que el `ProcessSpec` fija por su cuenta gana. La prueba real (`ChildEnvironmentTests`,
  con shims `.cmd` que solo dejan un marcador) falla sin esto y pasa con ello.
- **Decisión para la copia aislada** (ADR-0017). Un paso aislado recibe el `PATH` limpio, pero **conserva** la búsqueda en la carpeta
  actual: su carpeta de trabajo es la copia a propósito, corre a integridad baja, sin escribir y sin permisos de la persona, y un
  perfil de verificación puede apoyarse en ello. Lo que se cierra es lo que corre con los permisos de la persona.
- **Decisión para el terminal integrado.** No cambia: lo lanza ConPTY, no los runners, así que su shell conserva el PATH completo y la
  persona escribe sus órdenes. Solo la **búsqueda** del shell (`ShellLocator`) pasa por el localizador.
- **`GeminiLaunchResolver` y los shims.** Ya lanza `node` con la ruta absoluta del paquete (`gemini.js`) cuando el paquete está en su sitio de
  siempre, sin pasar por `cmd`; Codex hace lo mismo con su binario nativo (`CodexExecutableResolver`). Solo cuando el paquete no está donde
  se espera se lanza el shim, y entonces vale lo anterior (PATH limpio, sin la carpeta actual).
- **Carpetas demasiado amplias.** Una raíz que sea la carpeta personal entera, una carpeta de programas (`%ProgramFiles%`,
  `%ProgramFiles(x86)%`, `%ProgramData%`), la de Windows, la raíz de una unidad o cualquiera que las contenga **no cuenta como raíz no
  fiable** (`BroadFolders`): no se puede tratar todo el disco como hostil, y esconder el `git` y el `node` de la persona sería peor
  que el riesgo. En su lugar la app lo **dice**: al abrir esa carpeta, la barra de estado, y en el diagnóstico de los programas, una
  nota primera («la carpeta abierta es demasiado amplia, la protección de ejecutables no se aplica a ella; abre la carpeta del
  repositorio»).
- **«No encontrado» ya no es mudo.** Git, Claude Code, Codex y Gemini dicen, cuando el programa existe pero solo dentro de una raíz, dónde
  está y por qué no se usa (`FindInUntrustedRoot`).
- **Git revalida.** `GitCommandRunner` ya no guarda la ruta para siempre: antes de usarla pregunta de nuevo al localizador
  (`IsUntrusted`) con las raíces de ese momento, y un `GitExecutable` configurado pasa por la misma regla.
- **La carpeta neutra de las sondas la da la composición** (`NeutralWorkingFolderOptions`, `doctor\cwd` bajo los datos locales de la
  app). Sin ella se usa la carpeta del sistema y no se escribe nada: ninguna prueba escribe en los datos reales de la persona.
- **Unidades `subst` y de red.** `GetFinalPathNameByHandle` las resuelve (medido con `subst`; las asignadas de red devuelven la ruta UNC
  real por el mismo mecanismo, sin medir aquí), así que una entrada escrita por la letra y una raíz por la ruta real, o al revés, se
  comparan igual.

**Residuales que quedan**, tras esto:

- Un enlace duro es solo otro nombre del fichero y no se distingue.
- Entre la comprobación y el uso un proceso de la persona puede cambiar un enlace (la carrera de ADR-0017).
- Lo que el programa lanzado haga con su propia configuración (`.npmrc`, `global.json`) sigue siendo suyo: solo las sondas arrancan en una carpeta neutra.
- Un paso aislado conserva la búsqueda en su carpeta actual (decisión de arriba).
- Un shim `.cmd` o un programa que se busque otro por ruta absoluta o que cambie el PATH de sus hijos no lo ve esta regla.
- Una carpeta demasiado amplia queda sin protección, avisada pero no cerrada.
- Una herramienta comprometida **fuera** del proyecto sigue engañando a todo lo anterior.