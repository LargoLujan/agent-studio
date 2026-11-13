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
