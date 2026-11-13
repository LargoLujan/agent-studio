# ADR-0061 · Auto Doctor: una comprobación única, una puerta cerrada para lanzar herramientas, los manifiestos como datos no fiables y los arreglos en tres clases

- **Estado:** aceptada · 2026-11-12 (P0 de la Fase 6). Recoge D-01 a D-12 de [docs/20](../20-fase-6-auto-doctor-y-auto-graph.md) y las decisiones 8, 10, 12, 13 y 14
  de su §13 (todas, la opción recomendada). **Amplía** [ADR-0016](0016-perfil-de-verificacion.md) y [ADR-0018](0018-modelo-de-amenaza-fase-3.md). El P0 fija los
  contratos y las invariantes; WP-A (la puerta, las fuentes y los arreglos) y WP-E (el lector del entorno declarado y las versiones en las evidencias) añaden aquí
  lo que implementan.
- **Contexto:**
  - F6-1 a F6-9 piden que Auto Doctor compruebe los programas de los agentes, los runtimes, Git, Docker y WSL, lea el entorno que declara el proyecto y lo
    compare con lo instalado «sin ejecutar nada del repositorio», con arreglos reversibles o con aprobación.
  - Hoy el panel solo enseña proveedores. `ExecutableLocator` acepta una carpeta del repositorio si está en el PATH con ruta absoluta, y `CliProbe` no fija la
    carpeta de trabajo: un `claude.cmd` plantado en el proyecto se ejecutaría al abrir el panel (§0.2 de docs/20).
- **Decisión:**
  1. **Todo lo que Auto Doctor mira es una `DoctorCheck`** (D-01): un código `doctor.…`, una sección, un estado, hechos y el código de un arreglo. Cinco estados
     (correcto, aviso, error, sin comprobar y no aplica) y «sin comprobar» pesa más que «correcto». El título y la explicación salen del código
     ([ADR-0054](0054-idiomas-y-lenguaje-claro.md)). Las sondas de proveedores no se reescriben: un adaptador las convierte en comprobaciones.
  2. **Una única puerta para lanzar herramientas del sistema** (D-02): `IDoctorToolRunner.RunAsync(DoctorTool, string? projectRoot, ct)`. El que llama **nombra una
     entrada de la lista cerrada; no puede pasar un ejecutable ni un argumento**: no hay sobrecarga que lo permita y una prueba lo comprueba por reflexión. La puerta
     resuelve el ejecutable fuera de los proyectos abiertos, usa una carpeta de trabajo neutra, deja pasar solo las variables de entorno de una lista blanca y
     acota el tiempo (10 s) y la salida (64 000 caracteres). `DoctorToolOutcome.InsideProject` es el resultado de lo que solo se encuentra dentro de un proyecto.
  3. **El arreglo del localizador es de WP-0** (§9.0): `ExecutableLocator`, `LowIntegrityProcess` y `CliProbe` descartan lo que cae dentro de un proyecto abierto
     (`IUntrustedRoots`) y las sondas arrancan en la carpeta neutra. **Un cambio que se nota** (decisión 14): una herramienta instalada dentro de la carpeta de un
     proyecto abierto deja de encontrarse, y Auto Doctor lo avisa con `doctor.project.path-inside`.
  4. **La excepción de git** (decisión 13 (a)). Las comprobaciones de la sección «Proyecto» que preguntan a git van por la capa endurecida
     ([ADR-0012](0012-worktrees-fuera-del-repo.md)), que lanza `git` (el del sistema, sin hooks ni red) con la carpeta de trabajo en el repositorio. Es una lectura
     de F6-7: «leer el entorno declarado no lanza nada en el repositorio». **Pendiente de la integración:** el P0 solo añadió F6-12 y F6-13 a
     [docs/08](../08-plan-y-criterios-de-aceptacion.md); reescribir F6-7 con esas palabras lo hace el lead al integrar.
  5. **Los manifiestos son datos no fiables** (D-05 y D-06): una lista cerrada de 15 ficheros (`DeclaredFiles`) en la raíz del proyecto, nunca en subcarpetas;
     256 KB por fichero; sin seguir enlaces ni referencias (`include`, `dockerComposeFile`, `FROM` se nombran, nunca se abren); JSON con profundidad 32 y sin claves
     repetidas; líneas de hasta 4 096 caracteres; valores de hasta 200 caracteres que nunca pasan a una línea de órdenes. La Fase 6 **no interpreta YAML**
     (decisión 10 (a)): de `compose.yaml` solo se sabe que existe. Un solo lector, `IDeclaredEnvironmentReader`.
  6. **Comparar es código puro en Domain** (D-07): lo declarado (`DeclaredEnvironment`) y lo instalado (`InstalledTools`) entran, y salen comprobaciones. Las
     versiones son `ToolVersion`, con una forma estricta que compara como SemVer. Lo que no se entiende es «no interpretado», nunca «cumple» ni «falta».
  7. **Docker y WSL con tres estados cada uno** (D-09), decididos por estructura y código de salida y no por el texto; `DockerStatus` y `WslStatus` no admiten
     un estado con versiones que no le corresponden.
  8. **Arreglos en una jerarquía cerrada** (D-10): `DoctorFix` tiene un único constructor privado y cuatro formas, **ir** (`Navigate`), **guía** (`Guide`, que no
     ejecuta nunca su orden), **reversible** (`Reversible`, que lleva `Apply` y `Undo`) y **con aprobación** (`Approved`, que lleva su `ApprovalRequest`). Un
     «aplicar» sin deshacer ni aprobación no se puede escribir. En la Fase 6 **ningún arreglo toca el sistema** (decisión 8 (a)): no se instala nada.
  9. **Un arreglo aplicado deja rastro** (D-11): `DoctorFixApplied` (el código del arreglo, el de la comprobación y, si deshace otra entrada, su secuencia), fuera de
     toda ejecución y sin texto, sin rutas de la máquina y sin nada de un token.
  10. **Las versiones de las herramientas van dentro de la evidencia** (D-12, WP-E): un bloque `tools:` en la cabecera que ya escribe el verificador, anclado por la
      huella del contenido, sin campo nuevo ni migración. (La primera redacción las preguntaba con `--version` en la copia; la adenda de WP-E lo corrige: se leen del
      recurso de versión del ejecutable, sin ejecutar nada.)
- **Lo que fija el P0** ([docs/04 §25](../04-contratos.md#25-fase-6-auto-doctor-auto-graph-entorno-declarado-y-diagramas)): `Domain/Diagnostics` y `Domain/Environments`,
  `Application/Diagnostics/{DoctorContracts,EnvironmentContracts}.cs`, `DoctorFixApplied` y sus pruebas (`Phase6DoctorContractTests` comprueba por reflexión que
  la puerta no tiene parámetros libres y que la jerarquía de arreglos está cerrada).
- **Alternativas descartadas:** un modelo por tipo de comprobación (cada uno pediría su vista y su prueba); lanzar las sondas a integridad baja (rompería
  `docker`, que habla con su servicio por una tubería, sin quitar un riesgo nuevo); un localizador propio solo para Auto Doctor (dejaba abiertas la verificación y
  las sondas); instalar herramientas con `winget` (ejecutar instaladores con permisos altos: cadena de suministro, decisión 8).
- **Adenda de WP-E (2026-11-13): el entorno declarado y las versiones en las evidencias.**
  - **El lector** (`IDeclaredEnvironmentReader`, `Infrastructure/Environments/DeclaredEnvironmentReader`) abre solo los 15 ficheros de la lista, validados con
    `RepositoryPaths`, con 256 KB como tope y sin pasar por un enlace simbólico ni una unión (el fichero ni ninguna carpeta entre la raíz y él; la raíz que eligió la
    persona sí puede ser una unión). Un punto de reanálisis sin destino, como el marcador de OneDrive, es un fichero normal. Se comprueba sobre lo abierto
    (la ruta final del manejador debe ser la raíz final más la ruta relativa), no por el nombre otra vez. Lo que hay dentro lo lee código puro de Domain
    (`DeclaredFileParser`): nada lanza para un texto hostil y todo valor llega limpio y acotado. De compose solo se dice que existe y su tamaño. El tipo no tiene
    ningún camino para arrancar un proceso, y una prueba lo comprueba por estructura y con un repositorio con herramientas plantadas.
  - **Comparar** (`EnvironmentComparer`, Domain): `rollForward` evaluado sobre los SDK instalados (`SdkResolver`, las nueve políticas), rangos de `engines` y de
    `.nvmrc` con un subconjunto de semver, y `requires-python` con un subconjunto de PEP 440. Todos totales y acotados. **`sdk.paths` que no sea solo `$host$` nunca da
    «cumple»**: el anfitrión buscaría antes en carpetas que no se leen. Lo que no se evalúa es «sin comprobar». `DeclaredKind.Extends` es el único tipo añadido al
    contrato del P0.
  - **Las versiones en la evidencia** (D-12, corregido): **no se ejecuta nada**. El bloque `# tools:` de la cabecera lo escribe `VerificationRunner` con lo que le da un
    `IStepToolVersionReader` opcional (el real, `StepToolVersionReader`, lee el recurso de versión del mismo `.exe` que resuelve el lanzamiento, con la regla de
    `ExecutableSearch`). El SDK de .NET sale con la versión del **host** y una nota que lo dice: saber cuál elige `global.json` sin ejecutar `dotnet` queda como límite
    conocido. Un bloque no se puede falsificar con la salida de un proceso, que va con prefijo y después de la cabecera.
  - **Composición:** `AddAgentStudioEnvironments` (en `Infrastructure/Environments`, para no tocar `AddAgentStudioInfrastructure`) lo llama la raíz de composición después de
    registrar el inventario de WP-A.
- **Consecuencias y residuales aceptados:** una herramienta del sistema comprometida fuera del proyecto engaña a Auto Doctor (el residual del PATH de ADR-0018);
  la capa git arranca `git` dentro del repositorio; y `global.json` admite `sdk.paths` desde el SDK de .NET 10, que la verificación seguiría usando dentro de la copia
  (a integridad baja): Auto Doctor lo avisa (`doctor.env.dotnet.sdk-in-repo`) y cambiar la verificación queda fuera de esta fase.

## Adenda de WP-A · la puerta, las fuentes, los arreglos y el panel (2026-11-12)

WP-A implementa la parte de Auto Doctor que no es el entorno declarado (WP-E) ni el doctor de skills (WP-S). Lo que decide, y que no estaba escrito en la decisión:

- **La puerta** (`Infrastructure/Diagnostics/DoctorToolRunner`) pregunta una herramienta del catálogo cerrado (`DoctorToolCatalog.All`: `dotnet --list-sdks`, `dotnet --list-runtimes`, `node`, `npm`,
  `python` y `py` con `--version`, `git --version`, `docker version --format json` y `wsl --status`) de una en una, con una cola de un solo consumidor (sin cerrojo), 10 s y 64 000 caracteres.
  Resuelve el ejecutable con el localizador endurecido de WP-0 y lo rechaza otra vez si cae dentro de la raíz del proyecto que le pasan; arranca siempre en `%LOCALAPPDATA%\AgentStudio\doctor\cwd`, vacía.
  **El entorno es una lista blanca** (`DoctorEnvironment.Allowed`): todo lo que el proceso tiene y no está en ella entra como `null`, que lo quita, sin distinguir mayúsculas; `PATH` no se nombra (lo pone el
  ejecutor de procesos, ya limpio). Docker recibe un `DOCKER_CONFIG` propio y vacío (así no se usa el contexto de `~/.docker/config.json`) y WSL `WSL_UTF8=1`. El `python` que es solo el acceso directo de
  la Tienda no se arranca (podría abrir la Tienda y no es un intérprete): cuenta como no instalado y se avisa.
- **Los lectores son totales y deciden por estructura** (`ToolOutputParsers`): Docker, por `Client` y `Server` del JSON (`"Server":null` es el motor parado, con cualquier idioma); WSL, por la forma de las líneas
  `nombre: valor` (la primera, si su valor no es un número, es la distribución; un valor numérico es la versión). Una salida que no se entiende, truncada, de otra forma o con NUL (UTF-16 leído como UTF-8)
  es «sin comprobar»: `ToolSnapshot` lo guarda como `ToolState.Unreadable`, un estado que `InstalledTools` no tiene (allí sale como «no instalado»).
- **Hallazgo con la salida real:** en este equipo la única distribución de WSL es `docker-desktop`, la interna de Docker Desktop, que una persona no puede usar. `WslReading.IsDockerInternal` la trata como
  «sin distribución propia». Las salidas reales están grabadas (`tests/**/Diagnostics/Fixtures/real-*`) y las que este equipo no puede producir están escritas a mano (`handmade-*`), y así dicen sus nombres y su README.
- **Una instantánea por ejecución** (`ToolSnapshotProvider`, que es también `IInstalledToolInventory` y `IDoctorRunScope`): lo que se pregunta al equipo se pregunta una vez por ejecución del doctor y no se guarda
  para la siguiente; fuera de una ejecución (WP-E, el verificador) no se guarda nada.
- **Las fuentes:** proveedores (las sondas de hoy no se reescriben: `ProviderCheckSource` las adapta, con «versión no probada» y «aparcado» inyectados por la composición), herramientas, proyecto (git por la capa
  endurecida; los hooks se listan por nombre, sin abrir ningún fichero) y contenedores (un problema solo cuenta si el proyecto declara contenedores: solo se mira que exista uno de los nombres de D-05).
  `DoctorService` no falla entero.
- **Los arreglos:** la lista cerrada de la Fase 6 solo tiene «ir» y «guía» (la decisión 8 (a)). La orden de una guía se muestra y se copia; la app nunca la ejecuta. `IDoctorFixRunner.ApplyAsync` lleva el código de la
  comprobación (cambio del contrato del P0, en [docs/04](../04-contratos.md#25-fase-6-auto-doctor-auto-graph-entorno-declarado-y-diagramas) §25.2).
- **La pantalla contiene el panel de proveedores** (no lo sustituye): el panel `autodoctor` pasa a tener cinco apartados y el primero conserva las filas de siempre, con su estado aparcado. El resumen de la barra
  superior sigue siendo el de `ProviderDiagnosticsViewModel`. Los textos salen de `DoctorStrings` por el código de la comprobación y su estado.
- **Residuales que esta parte añade:** las órdenes y páginas de las guías están escritas a mano y no se ejecutaron; el texto de `wsl --status` y de Docker en marcha solo está comprobado en este equipo
  (docs/20 §12); y Auto Doctor **no abre** el proyecto: abrirlo crea `.agentstudio/`, toma el cerrojo y edita `.git/info/exclude`, y el doctor solo mira. Pregunta qué proyectos tiene abiertos el espacio de trabajo (`IOpenProjects`) y, si la carpeta aún no la abrió nada (el orquestador, «Trabajo»…), el historial y la configuración salen «sin comprobar» y un aviso lo dice; el resto de comprobaciones del proyecto sí corre. **Dos efectos conocidos:** con el panel visible al arrancar, las sondas de los proveedores se lanzan dos veces (la del resumen de la barra superior y la primera del panel). **Revisión posterior (2026-11-13):** «WSL no instalado» se reconoce por la orden `--install` del mensaje y la ausencia de estado (código ≠ 0 incluido); el Python de la Tienda se distingue por la carpeta de su paquete y, si no se sabe, sale «sin comprobar»; Docker pregunta también a la tubería de Docker Desktop si el contexto por defecto no muestra motor, y la pantalla dice «no responde»; las preguntas se cancelan al ocultar el panel o cerrar la app y toda la lectura tiene un tope de 30 s; las sondas de los proveedores se comparten si el panel se abre mientras la comprobación de la barra superior sigue en curso o acaba de terminar; un paso del perfil con una ruta del proyecto como programa se avisa como ejecutable del repositorio.