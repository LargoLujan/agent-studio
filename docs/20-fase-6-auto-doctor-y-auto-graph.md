# 20 · Fase 6 · Auto Doctor, Auto Graph, entorno declarado y diagramas: diseño

> **Estado:** propuesta, pendiente de tus decisiones (§13). Parte de la rama `fase14-integracion` en `45f243d3` (la Fase 14 y
> la Fase 9 integrándose, sin publicar). Es el documento que pedía [16 §4.3](16-ampliacion-plataforma-de-proyectos.md#43-fase-6-ampliada-auto-doctor-auto-graph-entorno-y-diagramas):
> «el documento de diseño de la Fase 6 fija el resto». Cubre P25 de [18](18-peticiones-y-pendientes.md).
>
> **Qué fija:** cómo se cumplen F6-1 a F6-11 de [08](08-plan-y-criterios-de-aceptacion.md#fase-6--auto-doctor-y-auto-graph),
> sin cambiar su sentido, y dos criterios nuevos de seguridad (F6-12 y F6-13, §8).
>
> **Con qué se solapa, y cómo se reparte:** la [documentación viva](19-documentacion-viva-del-proyecto.md) (Fase 16) también
> habla de diagramas y discrepancias. Aquí la Fase 6 construye **la base** (el modelo de Auto Graph, los generadores Mermaid
> de texto, la cabecera de versión, «desactualizado» y cuatro comprobaciones) y la Fase 16 construye **encima** (el motor que
> dibuja, C4, entidad-relación, diagramas de agentes, el paquete y el resto de comprobaciones). El reparto exacto está en §1.2.
> Difiere de la opción que recomendaba docs/19 §14.1: es la decisión 1 de §13.
>
> **Cómo se hizo.** A partir de:
>
> - el código de `src/**` y `tests/**` en `45f243d3` (inventario con rutas en §2);
> - los documentos 08, 10, 16, 18 y 19, y los ADR 0002, 0012, 0016, 0017, 0018, 0022, 0053, 0054, 0055 y 0057;
> - la documentación de Microsoft Learn sobre `global.json`, MSBuild seguro y la detección de WebView2, y el índice de
>   versiones de nuget.org (fuentes al final, consultadas el 2026-11-12).
>
> **Sin cuota.** Ningún CLI de proveedor recibió un prompt. No se leyeron carpetas de los CLIs ni credenciales, no se compiló
> nada y no se tocó código.

## 0. Resumen

La Fase 6 responde a tres preguntas sobre un proyecto, **sin ejecutar nada suyo**:

- **¿Está mi equipo listo para trabajar en él?** Auto Doctor comprueba los CLIs, los runtimes, Git, Docker y WSL, lee el
  entorno que el proyecto declara y lo compara con lo instalado.
- **¿Cómo está hecho?** Auto Graph lee la solución de C# y saca proyectos, espacios de nombres y referencias.
- **¿Coincide lo escrito con lo que hay?** Diagramas generados con la versión que describen, y cuatro discrepancias que se
  detectan sin IA.

### 0.1 Las decisiones que vertebran la propuesta

1. **Una sola forma para todo lo que Auto Doctor comprueba:** una comprobación con código, estado, hechos y arreglo. Las
   sondas de proveedores de hoy pasan a ser una fuente más (D-01).
2. **Una única puerta para lanzar herramientas del sistema:** lista cerrada de herramientas y argumentos, ejecutable
   resuelto fuera del proyecto, carpeta de trabajo neutra y entorno limpio. Por construcción, ningún proceso arranca dentro
   del repositorio ni con un ejecutable suyo (D-02).
3. **Los manifiestos son datos no fiables:** lista cerrada de rutas, tamaño acotado, sin seguir enlaces ni `include`, y
   lectores propios que solo extraen los campos que hacen falta (D-05 y D-06).
4. **Las versiones se comparan en código, no preguntando a la herramienta dentro del repositorio:** `dotnet --list-sdks`
   desde una carpeta neutra y la regla `rollForward` evaluada por Agent Studio (D-07).
5. **Los arreglos son una lista cerrada con tres clases:** ir a una pantalla, una guía con una orden para copiar, o un
   cambio que Agent Studio aplica sobre su propio estado y se puede deshacer. En la Fase 6 **ningún arreglo toca el sistema**
   (D-10).
6. **Las versiones de las herramientas van dentro de la evidencia,** en su cabecera, que ya está anclada por su huella. Sin
   migración (D-12).
7. **El doctor de skills es la única pieza con red:** solo `api.github.com`, apagado por defecto, solo cuando pulsas, con
   respuestas en caché y un token opcional guardado con DPAPI (D-13 a D-15).
8. **Auto Graph no usa MSBuild.** Lee `.slnx`, `.sln` y `.csproj` como texto y los `.cs` con el analizador sintáctico de
   Roslyn, sin compilar. Lo que necesitaría evaluar MSBuild se marca «parcial», no se adivina (D-16 y D-17).
9. **El análisis corre en un proceso aparte a integridad baja, sobre una copia del commit,** como la verificación (D-18 y
   D-19).
10. **Cada relación del grafo lleva su origen.** Las que propone una IA se guardan aparte, se dibujan distintas y nunca
    entran en la huella del modelo (D-21).
11. **Los diagramas son texto Mermaid generado desde un modelo,** con una gramática acotada y ficheros dorados. La Fase 6
    no dibuja Mermaid: la solución ya se ve en Auto Graph y el plan en el lienzo (D-23 a D-26).
12. **«Desactualizado» se decide con un índice propio, no leyendo el fichero:** lo generado nunca se lee de vuelta (D-25).
13. **Las discrepancias usan los códigos de docs/19** (`doc.link-broken`, `doc.adr-cites-missing`, `doc.diagram-stale`),
    para que la Fase 16 amplíe la lista y no la renombre (D-27).
14. **Ninguna tabla nueva.** Dos clases de entrada en el registro, cachés locales que se pueden borrar y un ajuste (§5).

### 0.2 Hallazgos de paso

Cosas que salieron al leer el código y que afectan a esta fase:

- **`ExecutableLocator` acepta una carpeta del repositorio si está en el PATH con ruta absoluta.** Solo descarta las entradas
  relativas (`src/AgentStudio.Infrastructure/Processes/ExecutableLocator.cs`). Para F6-7 no basta: D-02 lo resuelve para Auto
  Doctor y añade una comprobación que lo avisa.
- **`CliProbe` no fija carpeta de trabajo:** las sondas de `--version` heredan la de la app
  (`src/AgentStudio.Providers/Cli/CliProbe.cs`). WP-A la fija a la carpeta neutra.
- **`global.json` admite `sdk.paths` desde el SDK de .NET 10:** un proyecto puede decir al host que busque el SDK en una
  carpeta suya (por ejemplo `.dotnet`). La verificación ejecuta `dotnet` en la copia del commit, así que usaría ese SDK del
  repositorio (a integridad baja, eso sí). Auto Doctor lo avisa (D-07); cambiar la verificación queda fuera de esta fase y se
  anota en docs/18 al integrar.
- **`ISkillHealthChecker` no tiene implementación** y `SkillHealthReader` devuelve «Sin comprobar (Fase 6)» para `upstream`,
  `archived` y `scripts`. Es el hueco de F6-6.
- **`ICredentialVault` es solo un contrato** (`src/AgentStudio.Application/Security/SecurityContracts.cs`): no hay bóveda.
  El token opcional de F6-6 necesita una (decisión 6).
- **Ningún WebView2 de la app filtra las peticiones** (`AddWebResourceRequestedFilter` no aparece): la red se limita con la
  CSP, el host virtual y la cancelación de navegaciones. Vale para el editor, el terminal y el PDF; un motor de Mermaid
  necesitaría el filtro (docs/19 D-18). Es un motivo más para no dibujar Mermaid en esta fase.
- **El configurador devuelve una lista vacía de remotos** (`FileSetupRepositoryReader`, en
  `src/AgentStudio.Desktop/Setup/SetupServices.cs`), aunque `RepositoryRemoteReader` existe y está registrado. Auto Doctor usa
  el lector bueno.
- **El panel Auto Doctor no tiene artículo de ayuda** (`src/AgentStudio.Desktop/Help/HelpScreens.cs` no lo lista). Esta fase
  lo añade.
- **Los ADR 0041 a 0046 no existen en `docs/adr/`:** docs/16 los reservó. De ellos, solo el 0045 es de esta fase (§10).

### 0.3 Lo que se descarta o se aplaza

| Qué | Por qué | Dónde queda |
|---|---|---|
| `MSBuildWorkspace`, `Microsoft.Build.Locator` y cualquier evaluación de MSBuild | Abrir un proyecto con MSBuild puede ejecutar código del repositorio (D-16) | Descartado |
| Instalar o actualizar herramientas desde Auto Doctor | Cadena de suministro y permisos de administrador (D-10) | Decisión 8 |
| Dibujar Mermaid en la app (motor JS en WebView2) | Es el motor de docs/19 D-18, con su spike | Fase 16 (decisión 2) |
| C4, entidad-relación, despliegue, diagramas de agentes, texto alternativo | Necesitan modelos que esta fase no construye | Fase 16 |
| Auto Graph de Node y Python | F6-3 pide C#. El modelo admite otros lenguajes más adelante | Fase 16 (extractores) |
| Símbolos (tipos y miembros) y referencias semánticas | Exigen compilar; F6-3 pide proyectos, espacios de nombres y referencias | Fase 16, si hace falta |
| Crear o arrancar entornos (`.venv`, Dev Containers, compose) | docs/16 D-07 | Fase 11 |
| Comprobaciones periódicas en segundo plano con red | La red es solo a petición (D-13) | Descartado |
| Anclas de los enlaces (`#sección`) | docs/19 las incluye en `doc.link-broken` | Fase 16 |

---

## 1. Alcance

### 1.1 Cada criterio y dónde va

| Criterio | Qué pide, en corto | Decisiones | Paquete |
|---|---|---|---|
| F6-1 | CLIs, versiones, sesión, runtimes, Git y configuración del proyecto | D-01 a D-04 | WP-A |
| F6-2 | Arreglos reversibles o con aprobación | D-10 y D-11 | WP-A |
| F6-3 | Grafo de C#: proyectos, espacios de nombres y referencias | D-16 a D-20 | WP-G |
| F6-4 | Relaciones de IA, visualmente distintas | D-21 | WP-G |
| F6-5 | Ir de un nodo al fichero o a la tarea | D-22 | WP-G |
| F6-6 | Doctor de skills con token opcional | D-13 a D-15 | WP-S |
| F6-7 | Entorno declarado frente a instalado, sin ejecutar nada | D-02 y D-05 a D-08 | WP-E |
| F6-8 | Versiones de las herramientas en cada evidencia | D-12 | WP-E |
| F6-9 | Docker y WSL, con sus tres estados | D-09 | WP-A |
| F6-10 | Diagramas Mermaid con versión y «desactualizado» | D-23 a D-26 | WP-D |
| F6-11 | Cuatro discrepancias deterministas | D-27 a D-29 | WP-D |

### 1.2 El reparto con la Fase 16 (documentación viva)

docs/19 se escribió sin este documento y proponía llevarse a la Fase 16 todo el paquete de diagramas. El reparto que aquí se
propone es otro: **la Fase 6 entrega lo que F6-10 y F6-11 piden, solo como texto, y lo deja en puertos que la Fase 16 usa
sin reescribir.**

| Pieza | Fase 6 | Fase 16 |
|---|---|---|
| Modelo de Auto Graph (`CodeGraph`) y su puerto | La construye | La lee para el nivel de componentes de C4 (docs/19 D-06) |
| Copia del commit para leer sin ejecutar (`IRepositorySnapshots`) | La construye | La usan sus extractores (WP-16C) |
| Generadores Mermaid | Tres: solución, plan y secuencia | Los demás (C4, despliegue, entidad-relación, hitos, trazabilidad), con el mismo contrato |
| Gramática acotada, escapado e identificadores | La fija | La reutiliza |
| Cabecera de versión y carpeta `.agentstudio/diagrams/` | Las fija | Las reutiliza |
| «Desactualizado» por huella del modelo | Lo construye | Lo reutiliza (`doc.diagram-stale`) |
| Motor que dibuja (mermaid.js en WebView2), SVG, PNG y caché | No | Sí (docs/19 D-18) |
| Validar que compila (`mermaid.parse`) | No: lo garantiza la gramática acotada y los dorados | Sí, para los diagramas de agentes |
| Diagramas de agentes y `DiagramPolicy` | No | Sí |
| Texto alternativo y exportación a HTML, PDF y Word | No | Sí |
| Comprobaciones | `doc.link-broken` (sin anclas), `doc.adr-cites-missing`, `doc.diagram-stale` y `requirement.uncovered` | El resto de `doc.*` (docs/19 D-27), y las anclas |
| Informe de comprobaciones como evidencia del sistema | No | Sí |
| Pantalla | Dos secciones, «Diagramas» y «Discrepancias», en el documento Auto Graph | «Documentación», que las acoge |

Consecuencias para docs/19, si aceptas la decisión 1:

- su WP-16B deja de crear los generadores de solución, plan y secuencia y el estado «desactualizado»: los amplía;
- ADR-0045 lo escribe la Fase 6 (lo que se genera, la cabecera y la vigencia) y la Fase 16 le añade una adenda con el motor;
- la fila «F6-10 y F6-11» de su tabla F16 desaparece.

---

## 2. Inventario: lo que ya existe

Rutas relativas a la raíz del repositorio, comprobadas en `45f243d3`.

### Auto Doctor y sondas (F6-1, F6-9)

- **El puerto de las sondas:** `IProviderProbe` y `ProviderProbeResult` en `src/AgentStudio.Application/Providers/ProviderProbe.cs`.
- **El agregador:** `src/AgentStudio.Application/Diagnostics/ProviderDiagnostics.cs` lanza todas en paralelo; una sonda que
  falla sale como fila de error. Su comentario dice que es «la primera pieza de Auto Doctor».
- **Las sondas:** `src/AgentStudio.Providers/Cli/{CliProbe,ClaudeCodeProbe,CodexCliProbe,GeminiCliProbe,SupportedVersions}.cs`.
  Solo usan `--version`, `claude auth status` y `codex login status`.
- **Resolver ejecutables:** `IExecutableLocator` (`src/AgentStudio.Application/Execution/ProcessContracts.cs`) y
  `src/AgentStudio.Infrastructure/Processes/ExecutableLocator.cs`.
- **Lanzar procesos:** `ProcessSpec`, `ProcessIsolation` e `IProcessRunner` en el mismo fichero de contratos. `ProcessSpec`
  ya tiene carpeta de trabajo, entorno (un valor nulo quita la variable), tiempo máximo y tope de salida.
- **La pantalla:** el panel `panel:autodoctor`, con `src/AgentStudio.Desktop/Views/AutoDoctorView.xaml` y
  `src/AgentStudio.Desktop/ViewModels/ProviderDiagnosticsViewModel.cs`. Hoy solo enseña proveedores, en compacto (P20).

### Configuración del proyecto y perfiles (F6-1, F6-7, F6-8)

- **El configurador** (ADR-0055): `src/AgentStudio.Application/Setup/{SetupContracts,ProjectSetupService,SetupProposer}.cs`,
  con `ISetupVerificationProfiles` (proponer, cargar y guardar el perfil) y la entrada `ProjectSetupApplied`.
- **El detector de pila:** `src/AgentStudio.Domain/Skills/ProjectStackDetector.cs`, por nombres de fichero, sin leer contenido.
- **El índice de ficheros:** `src/AgentStudio.Desktop/ViewModels/ProjectFileIndexer.cs` (20 000 ficheros como tope, sin
  seguir enlaces de carpeta).
- **La verificación:** `src/AgentStudio.Orchestration/Verification/**`. `VerificationRunner.cs` escribe cada evidencia con una
  cabecera (`WriteHeaderAsync`) y la captura con `EvidenceDraft`. `StackStepSupport.cs` da el entorno aislado de cada pila y
  reconoce el alias de la Tienda de Windows. `NodeVerificationProfile.InstalledVersion` lee la versión de un ejecutable de su
  recurso de versión, sin ejecutarlo.
- **El perfil:** `VerificationProfileFile.cs` lee `verification.json` con un tope de 256 KB. Ese es el patrón de tope que se
  copia aquí.
- **El aislamiento:** `src/AgentStudio.Infrastructure/Processes/Isolation/**` (integridad baja, área de trabajo, cerrojo de
  «una a la vez») y `IIsolatedWorkAreaFactory` en `src/AgentStudio.Application/Execution/IsolationContracts.cs`.
  `LowIntegrityProcess.ResolveExecutable` acepta una ruta completa tal cual: un ejecutable propio se puede lanzar ahí.

### Git (F6-1, F6-3, F6-10, F6-11)

- **La capa endurecida:** `src/AgentStudio.Infrastructure/Git/**`. Sin red, sin hooks, sin ayudantes de credenciales.
- **Copiar un commit:** `IWorktreeManager.ArchiveCommitAsync(repoRoot, commit, destination, ct)`
  (`src/AgentStudio.Application/Git/GitContracts.cs`). Es lo que usa la verificación. No hay ningún puerto para leer un
  fichero de un commit (`cat-file`, `ls-tree`), y no se añade (D-19).
- **Remotos:** `IRepositoryRemoteReader.ListRemotesAsync`, con topes y sin credenciales en las URL.
- **Hooks y rechazos:** `IWorktreeManager.FindHookPathsAsync` e `InspectAsync`.

### Skills (F6-6)

- **La salud:** `src/AgentStudio.Domain/Skills/SkillHealth.cs` (`SkillHealthCodes`: `frontmatter`, `license`, `pinned`,
  `upstream`, `archived`, `scripts` y `size`).
- **Los puertos:** `ISkillHealthReader` (local, implementado en `src/AgentStudio.Infrastructure/Skills/SkillHealthReader.cs`) e
  `ISkillHealthChecker` (sin implementar), en `src/AgentStudio.Application/Skills/SkillPorts.cs`.
- **El diseño:** [10 §6](10-catalogo-de-skills.md#6-salud-doctor) ya dice qué comprueba cada indicador y que las consultas a
  GitHub usan un token opcional y `ETag`.
- **La pantalla:** `src/AgentStudio.Desktop/Skills/**` y `src/AgentStudio.Desktop/Views/Skills/SkillsView.xaml`.
- **Red en la app, hoy:** solo `HttpEmbeddingModelInstaller` (la descarga del modelo del índice semántico, con tu OK y huella
  fijada, ADR-0047). Es el precedente de «red solo donde el diseño la permite».

### Registro, trazabilidad y hitos (F6-5, F6-10, F6-11)

- **Escribir y leer el registro:** `IDecisionRecorder` (`src/AgentStudio.Application/Decisions/DecisionContracts.cs`). Una
  clase nueva de entrada se añade en dos sitios: el enum `DecisionKind` (`src/AgentStudio.Domain/Decisions/DecisionLog.cs`) y
  el mapa de `DecisionPayloadSerializer`. Dos pruebas lo fijan (`DecisionKindCompatibilityTests`, `DecisionPayloadTests`).
- **Leer el registro desde Memory:** `src/AgentStudio.Memory/Documents/ClientDocumentBuilder.cs` ya lo hace con
  `IDecisionRecorder` e `IEvidenceStore`. Es el camino del diagrama de secuencia (los lectores `WorkLog` y `RunLog` son
  internos de Orchestration).
- **El plan:** `src/AgentStudio.Domain/Orchestration/PlanDefinition.cs`.
- **Requisitos sin cubrir:** no hay un puerto propio. `IBoardProjector.ProjectAsync`
  (`src/AgentStudio.Application/Work/BoardContracts.cs`) da el tablero de un hito, y una tarjeta de requisito en la columna
  `Backlog` significa «ninguna tarea lo cubre». El informe del hito ya usa el código `requirement.uncovered`
  (`src/AgentStudio.Debate/Audit/MilestoneReport.cs`).
- **Ficheros que tocó cada tarea:** `AttemptRecord.ChangedPaths` (`src/AgentStudio.Orchestration/Engine/AttemptRecord.cs`).
- **Trazabilidad:** `TraceNodeKind.Document` (`ruta@commit`) en `src/AgentStudio.Domain/Tracing/TraceRef.cs`.

### Exportaciones generadas (F6-10)

- **El patrón:** `src/AgentStudio.Memory/Export/MemoryMarkdownExporter.cs` escribe en `.agentstudio/memory/` con una cabecera
  que dice que se genera, y nunca se lee de vuelta (ADR-0022).
- **Los nombres:** `src/AgentStudio.Application/Projects/AgentStudioFiles.cs` es la lista cerrada de lo que Agent Studio escribe
  en `.agentstudio/`. Hay que añadirle la carpeta de diagramas, o `IsAgentStudioStatusLine` contaría los diagramas como
  cambios tuyos.
- **Documentos para clientes** (ADR-0057): el modelo tiene párrafo, lista, tabla y lista de evidencias. No tiene bloque de
  código ni de diagrama; añadirlo es de la Fase 16 (docs/19 D-22) y esta fase no lo toca.

### Interfaz, textos y ayuda

- **WebView2:** tres usos, todos en Desktop: el editor (`Controls/Editor/CodeEditorControl.xaml.cs`, Monaco), el terminal
  (`Controls/Terminal/TerminalControl.xaml.cs`, xterm) y el PDF (`Documents/WebView2PdfPrinter.cs`). Cada uno con su host
  virtual y su CSP. No hay Mermaid.
- **El lienzo:** Nodify 7.3.0 (ADR-0015). Auto Graph lo reutiliza en solo lectura.
- **Textos** (ADR-0054): `UserText` (`src/AgentStudio.Application/Localization/UserText.cs`); un par
  `<Área>Strings.resx` y `<Área>Strings.en.resx` por área en `src/AgentStudio.Desktop/Resources/`. Las capas de abajo devuelven
  códigos con parámetros.
- **Inventario de la interfaz:** `tests/AgentStudio.Tests/Desktop/UiInventory/**`. Cada vista nueva necesita su `Screen(...)`
  o la guarda `Every_view_of_the_app_is_walked` falla.
- **Ayuda:** artículos en `src/AgentStudio.Desktop/Help/{es-ES,en}/<id>.md`, de 200 a 1800 caracteres, y los códigos de
  pantalla en `HelpScreens.cs`.
- **Navegación:** la lista `Activities` de `src/AgentStudio.Desktop/ViewModels/MainViewModel.cs`, que ya tiene `autodoctor`.
  [03](03-pantallas-y-navegacion.md) dice que Auto Graph tendrá su entrada cuando exista.
- **Ajustes:** `AppSettings` (`src/AgentStudio.Desktop/Services/AppSettings.cs`). Un ajuste nuevo hay que añadirlo también a
  `WithPreferencesOf`, o se pierde al guardar.
- **Hello** (ADR-0053): `PresencePolicy` (`src/AgentStudio.Domain/Security/PresencePolicy.cs`). `RemoteOrSecretOperation` está
  en la lista de «siempre».
- **Agentes auxiliares:** `AuxiliaryAgentRunner` (`src/AgentStudio.Application/Providers/AuxiliaryAgentRunner.cs`), que ya
  usan el análisis del consumo y la ayuda: un lanzamiento de solo lectura con tu OK.

---

## 3. Decisiones

Cada decisión es mía salvo que diga «decisión N», que te la deja a ti en §13.

### 3.1 Auto Doctor: una comprobación, una puerta

**D-01 · Todo lo que Auto Doctor mira es una `DoctorCheck`.**

- **Qué lleva:** un código estable (`doctor.dotnet.sdk`), una sección, un estado, un título y una explicación como
  `UserText`, una lista de hechos (nombre y valor: versión, ruta) y, si lo hay, un arreglo (D-10).
- **Estados:** correcto, aviso, error, sin comprobar y no aplica. «Sin comprobar» pesa más que «correcto», como en las skills:
  nada parece sano por no haberlo mirado.
- **Secciones:** proveedores, herramientas, proyecto, entorno declarado y contenedores.
- **Las sondas de proveedores no se reescriben.** Un adaptador convierte cada `ProviderProbeResult` en comprobaciones.
  `ProviderDiagnostics` y el resumen de la barra superior siguen como están.
- **Por qué:** una sola lista se prueba, se traduce y se pinta de una sola manera, y cada fuente nueva (entorno, Docker) solo
  añade códigos.
- **Descartado:** un modelo por tipo de comprobación. Cada uno pediría su vista y su prueba de inventario.

**D-02 · Una única puerta para lanzar herramientas: `IDoctorToolRunner`.** Es lo que hace F6-7 comprobable por estructura,
no por cuidado.

- **Lista cerrada.** Un catálogo en el código dice qué herramienta y con qué argumentos exactos:

  | Herramienta | Argumentos permitidos |
  |---|---|
  | `dotnet` | `--list-sdks`, `--list-runtimes` |
  | `node`, `npm` | `--version` |
  | `python`, `py` | `--version` |
  | `git` | `--version` |
  | `docker` | `version --format json` |
  | `wsl` | `--status` |

  Quien llama pide una entrada del catálogo por su nombre; no puede pasar ni un ejecutable ni un argumento.
- **Resuelto fuera del proyecto.** El localizador de Auto Doctor envuelve `IExecutableLocator`: quita del PATH toda entrada
  que, ya resuelta, caiga dentro de la raíz del proyecto abierto, y rechaza cualquier resultado que caiga dentro. Compara
  rutas finales (enlaces resueltos), sin distinguir mayúsculas.
- **Carpeta de trabajo neutra.** Siempre `%LOCALAPPDATA%\AgentStudio\doctor\cwd`, vacía y creada por la app. Así ningún
  `global.json`, `.npmrc` ni `.python-version` del proyecto influye en la respuesta.
- **Entorno limpio.** El PATH filtrado y, quitadas, las variables que cambian qué se ejecuta o a dónde se conecta:
  `DOTNET_ROOT*`, `DOTNET_ROLL_FORWARD*`, `MSBuildSDKsPath`, `NODE_OPTIONS`, `NODE_PATH`, `NPM_CONFIG_*`, `PYTHONPATH`,
  `PYTHONSTARTUP`, `PYTHONHOME`, `GIT_*`, `DOCKER_HOST` y `DOCKER_CONTEXT`.
- **Topes.** 10 segundos y 64 000 caracteres de salida por sonda; de una en una.
- **Sin texto libre.** No hay entrada estándar ni argumentos que vengan del proyecto.
- **No se cambia `ExecutableLocator` para todos.** La verificación y los proveedores siguen igual; cambiar su resolución es
  otro riesgo y otra prueba. Auto Doctor, en cambio, añade la comprobación `doctor.project.path-inside` (aviso): «tu PATH
  tiene una carpeta de este proyecto».
- **Las sondas de proveedores** (`CliProbe`) pasan a fijar la misma carpeta neutra. Sus argumentos no cambian.
- **Descartado:** lanzar las sondas a integridad baja. Son herramientas del sistema instaladas por ti, fuera del proyecto;
  la integridad baja rompería algunas (`docker` habla con su servicio por una tubería con nombre) sin quitar ningún riesgo
  nuevo.

**D-03 · Qué comprueba F6-1, con su código.**

| Sección | Comprobación | De dónde sale |
|---|---|---|
| Proveedores | Instalación, versión, versión no probada y sesión de cada CLI | Las sondas de hoy |
| Herramientas | SDK y runtimes de .NET; Node y npm; Python (y el alias de la Tienda, que no cuenta como instalado); Git | D-02 |
| Herramientas | WebView2: instalado y su versión | El valor `pv` de las dos claves del registro que documenta Microsoft; sin lanzar nada |
| Proyecto | Es un repositorio; la capa git no lo rechaza; hooks presentes; remotos | `InspectAsync`, `FindHookPathsAsync`, `IRepositoryRemoteReader` |
| Proyecto | El registro carga y su cadena es íntegra | `IDecisionRecorder.LoadAsync` |
| Proyecto | El configurador se aplicó; hay perfil de verificación y carga | `ProjectSetupApplied`, `ISetupVerificationProfiles.LoadAsync` |
| Proyecto | Cada herramienta que usa un paso del perfil está instalada | El perfil y D-02 |
| Proyecto | El PATH no tiene carpetas del proyecto | D-02 |
| Entorno declarado | Lo declarado frente a lo instalado | §3.2 |
| Contenedores | Docker y WSL | §3.3 |

- **Sin proyecto abierto** solo salen proveedores, herramientas y contenedores.
- **Cuándo corre:** al abrir el panel y al pulsar «Volver a comprobar», como hoy. No hay sondeo periódico.

**D-04 · El panel sigue compacto.** Una fila por sección con el peor estado y un recuento; al desplegar, una fila por
comprobación con sus detalles plegados. Es P20: «Auto Doctor ocupa mucho espacio». La sección de proveedores conserva la
vista de hoy.

### 3.2 El entorno que declara el proyecto (F6-7)

**D-05 · Se lee una lista cerrada de ficheros, de la carpeta de trabajo, como datos no fiables.**

| Fichero | Qué se extrae | Formato y lector |
|---|---|---|
| `global.json` | `sdk.version`, `sdk.rollForward`, `sdk.allowPrerelease`, `sdk.paths` | JSON con comentarios |
| `.config/dotnet-tools.json` | Identificador y versión de cada herramienta local | JSON |
| `.nvmrc`, `.node-version` | La primera línea | Texto |
| `package.json` | Solo `engines.node` y `engines.npm` | JSON |
| `.python-version` | La primera línea | Texto |
| `pyproject.toml` | Solo `requires-python` de la tabla `[project]` | Lector de líneas propio |
| `.venv/pyvenv.cfg` | La línea `version` | Texto `clave = valor` |
| `.devcontainer/devcontainer.json`, `.devcontainer.json` | `image`, `build.dockerfile`, `dockerComposeFile` y los identificadores de `features` | JSON con comentarios |
| `compose.yaml`, `compose.yml`, `docker-compose.yaml`, `docker-compose.yml` | Los nombres de los servicios y su `image`; si hay `include` o `extends` | Lector de líneas propio, marcado «parcial» |
| `Dockerfile` | Las líneas `FROM` | Lector de líneas propio |

- **Solo esas rutas, relativas a la raíz del proyecto.** No se buscan en subcarpetas: un monorepo saldría con ruido y la
  búsqueda sería una superficie más.
- **De la carpeta de trabajo, no de HEAD:** lo que importa a quien va a trabajar es lo que tiene delante. Es la diferencia
  con Auto Graph (D-19), que describe un commit.
- **Por qué lectores propios para YAML y TOML:** solo hacen falta tres campos. Una librería de YAML es una dependencia nueva
  con anclas, alias y etiquetas que aquí no se quieren interpretar. Lo que el lector no entiende sale como «no interpretado»,
  con su fichero y su línea. Es la decisión 10.

**D-06 · Las reglas de lectura, iguales para todos.** Viven en un único lector (`IDeclaredEnvironmentReader`) y se prueban una
vez.

- **Tamaño:** 256 KB por fichero (el tope de `verification.json`). Si pasa, no se lee y sale «demasiado grande».
- **Sin enlaces:** si el fichero o cualquier carpeta entre la raíz y él es un punto de reanálisis (enlace simbólico, unión),
  no se lee y sale «es un enlace». Se abre por su identificador ya comprobado, no por su nombre otra vez.
- **Sin seguir referencias:** `include` y `extends` de compose, `dockerComposeFile` y `build.dockerfile` de un Dev Container,
  `extends` de cualquier JSON y las imágenes `FROM` se **nombran** como hechos; nunca se abren ni se descargan.
- **JSON:** profundidad máxima 32, comentarios y coma final admitidos, duplicados rechazados.
- **Texto:** UTF-8; una línea de más de 4 096 caracteres corta la lectura.
- **Nada se ejecuta:** ni `npm`, ni `dotnet tool`, ni el `python.exe` de `.venv`, ni `docker compose config`.
- **Los valores leídos son datos:** se muestran escapados, con un tope de 200 caracteres, y nunca pasan a una línea de
  órdenes.
- **Secretos:** no se lee ningún `.env`, y de compose no se extraen `environment`, `env_file` ni `secrets`.

**D-07 · Comparar es código puro, en Domain.** `src/AgentStudio.Domain/Environments/**` (nuevo) recibe lo declarado y el
inventario de lo instalado y devuelve comprobaciones. No toca disco ni procesos.

- **.NET:** la lista de SDK instalados sale de `dotnet --list-sdks` en la carpeta neutra. La regla `rollForward` de
  `global.json` (las nueve políticas de la documentación, la que aplica por defecto cuando hay versión, y `allowPrerelease`) se
  evalúa en código. Resultado: «se usaría el SDK 10.0.303» o «falta: ninguno de los instalados cumple 10.0.303 con
  `latestFeature`».
- **`sdk.paths`:** si alguna ruta apunta dentro del proyecto, aviso `doctor.env.dotnet.sdk-in-repo`: «este proyecto pide usar
  un SDK de su propia carpeta. Auto Doctor no lo ejecuta ni lo cuenta como instalado».
- **Herramientas locales de .NET:** de cada una, si su paquete está en la caché global de NuGet
  (`%USERPROFILE%\.nuget\packages\<id>\<versión>`). Es mirar si existe una carpeta; no se ejecuta `dotnet tool`.
- **Node:** un subconjunto de los rangos de semver (`>=`, `>`, `<`, `<=`, `=`, `^`, `~`, `x`, espacios y `||`). `.nvmrc` con un
  alias (`lts/*`, `node`) sale «no interpretado».
- **Python:** un subconjunto de PEP 440 (`>=`, `>`, `<`, `<=`, `==`, `!=`, `~=` y comas). `.venv`: la versión de
  `pyvenv.cfg` frente a `requires-python` y frente a la instalada; si no hay `.venv`, se dice.
- **Contenedores:** si el proyecto declara Dev Container, compose o `Dockerfile`, se enlaza con el estado de Docker (§3.3).
  Las imágenes se listan; no se comprueba que existan (sería red).
- **Lo que no se entiende, se dice.** Un rango fuera del subconjunto no es ni «cumple» ni «falta»: es «no interpretado», con
  el texto original.

**D-08 · Lo declarado y lo instalado se enseñan juntos.** Cada fila: qué pide el proyecto, de qué fichero, qué hay instalado
y el veredicto. Con su arreglo si falta (D-10).

### 3.3 Docker y WSL (F6-9)

**D-09 · Tres estados de Docker y tres de WSL, decididos por estructura y código de salida, no por el texto.**

| Estado | Cómo se decide |
|---|---|
| Docker no instalado | El localizador no encuentra `docker` |
| Docker instalado, motor parado | `docker version --format json` da la parte `Client` pero no la `Server`, o termina con error |
| Docker en marcha | Vienen `Client` y `Server`: se muestran las dos versiones |
| WSL no instalado | El localizador no encuentra `wsl`, o `wsl --status` falla con el código de «no instalado» |
| WSL2 sin distribución | `wsl --status` no nombra ninguna distribución por defecto |
| WSL2 listo | Nombra la distribución por defecto y la versión 2 |

- **Solo aplica si importa:** con un proyecto que no declara contenedores, la sección sale plegada y en «no aplica» si falta.
- **El arreglo es una guía** («Abre Docker Desktop», «Instala una distribución con `wsl --install -d <nombre>`»): Auto Doctor
  no arranca el motor ni instala nada.
- **Ninguna de las dos órdenes arranca nada:** `docker version` pregunta al motor si está; `wsl --status` no inicia una
  distribución. Con `DOCKER_HOST` quitado (D-02), la pregunta va siempre al motor local.
- **Sin verificar** (§12): que `wsl --status` escribe en UTF-16 salvo con `WSL_UTF8=1`, y que su texto cambia con el idioma de
  Windows. Por eso WP-A graba las salidas reales de este equipo en castellano y en inglés antes de escribir el lector, y el
  lector se apoya en la estructura.

### 3.4 Arreglos (F6-2)

**D-10 · Una lista cerrada de arreglos, de tres clases.**

| Clase | Qué hace | Ejemplos de la Fase 6 |
|---|---|---|
| Ir | Abre una pantalla de la app | «Abrir el configurador», «Abrir Ajustes», «Ver la skill» |
| Guía | Enseña qué hacer, con una orden para copiar o la página oficial. **La app no ejecuta la orden** | «Instala el SDK 10.0.303», «Abre Docker Desktop» |
| Aplicar | Agent Studio cambia **su propio estado**, y se puede deshacer | «Regenerar los diagramas desactualizados», «Desactivar la skill archivada» |

- **La regla, en el tipo:** un arreglo de clase «aplicar» solo se puede construir de dos formas: con su operación de
  deshacer, o con una petición de aprobación (`IApprovalGate`) que se pide antes de aplicarlo. No hay una tercera.
- **En la Fase 6 todos los arreglos «aplicar» son reversibles** y actúan sobre datos de Agent Studio. Ninguno toca el
  sistema, el repositorio fuera de `.agentstudio/` ni la configuración de un CLI.
- **Hello:** un arreglo con aprobación pasa por la puerta de siempre, y si su acción está en la lista fija de ADR-0053, pide
  presencia. Ningún arreglo de esta fase lo necesita; el mecanismo queda probado con un arreglo de prueba.
- **Por qué no instalar:** instalar es ejecutar un instalador con permisos altos, elegir una fuente y fiarse de ella. Es
  cadena de suministro, y docs/16 ya lo descartó («instala herramientas por su cuenta, con su riesgo»). Es la decisión 8.

**D-11 · Un arreglo aplicado deja rastro y se puede deshacer.** Con proyecto abierto, deja `DoctorFixApplied` en el registro
(el código del arreglo, el de la comprobación y si deshace otra entrada). «Deshacer» es otro `DoctorFixApplied` que apunta al
primero. Sin texto libre ni rutas de tu equipo en el payload.

### 3.5 Las versiones en las evidencias (F6-8)

**D-12 · Las versiones van en la cabecera de la evidencia, no en un campo nuevo.**

- **Cómo:** antes de ejecutar un paso, `VerificationRunner` pregunta la versión de su ejecutable y de lo que declare en
  `Requires`, y la escribe en la cabecera que ya escribe (`WriteHeaderAsync`), en un bloque fijo:

  ```text
  tools:
    dotnet 10.0.303
    node 22.11.0
  ```

- **Dónde se pregunta:** en el mismo sitio y con el mismo aislamiento que el paso: en la copia del commit, a integridad
  baja y con el entorno del paso. Así sale «el SDK que resolvió `global.json` en la copia», que es lo que pide docs/16 D-06.
  Aquí sí se ejecuta dentro de la copia: es la verificación, que ya lo hace, no Auto Doctor.
- **Lista cerrada** de preguntas (`dotnet --version`, `node --version`, `npm --version`, `python --version`). Para cualquier
  otro ejecutable se lee su recurso de versión sin ejecutarlo (como `NodeVerificationProfile.InstalledVersion`), y si no
  tiene, sale `desconocida`.
- **Una versión que no se obtiene no falla el paso:** sale `desconocida` con su motivo. 20 segundos como tope.
- **Un lector puro** (`EvidenceToolVersions.Parse`, en Application) saca el bloque del contenido, para la tarjeta de la
  evidencia y para la Fase 16.
- **Por qué en el contenido:** el contenido ya está anclado por su SHA-256 en `EvidenceRegistered`. Un campo nuevo pediría
  cambiar `EvidenceDraft`, `EvidenceRecord`, el payload a la versión 3 y una migración, para decir lo mismo.
- **Descartado:** tomar las versiones de Auto Doctor. Serían las de la carpeta neutra, no las que resolvió el paso.

### 3.6 El doctor de skills (F6-6)

**D-13 · La única pieza con red, y con cuatro cerrojos.**

- **Apagada por defecto.** Un ajuste nuevo, «Comprobar las skills en GitHub», en Ajustes. Sin él, `upstream` y `archived`
  siguen en «sin comprobar», y no sale ni una petición.
- **Solo cuando pulsas** «Comprobar en GitHub», en la pantalla de skills. Nunca al abrir, nunca en segundo plano.
- **Solo `https://api.github.com`.** El cliente rechaza cualquier otro host y no sigue redirecciones a otro host. 10
  segundos y 1 MB por respuesta.
- **Se dice qué sale:** el propietario, el repositorio y el commit de cada skill fijada. Nada del proyecto, ni rutas, ni tu
  nombre.

**D-14 · Qué se pregunta y cómo se interpreta.**

| Indicador | Petición | Resultado |
|---|---|---|
| `archived` | `GET /repos/{owner}/{repo}` | `archived: true` → crítico |
| `upstream` | `GET /repos/{owner}/{repo}/compare/{sha}...{rama}` | Algún fichero cambiado bajo la carpeta de la skill → «actualización por revisar». Si la respuesta viene truncada (más de 300 ficheros), también: no se da por sana |
| `scripts` | Ninguna: es local | Tiene `scripts/` y no hay revisión tuya de esa huella → aviso |
| `size` | Ninguna: es local | Lo de hoy, más el coste de contexto del cuerpo (`SkillContextCost`) |

- **`scripts`:** «Marcar los scripts como revisados» guarda la huella (SHA-256) del contenido de `scripts/`. Si un script
  cambia, la skill vuelve a «sin revisar». Nunca se ejecuta ningún script.
- **En serie, con una cola y un solo consumidor,** como pide GitHub y como prefieres para las carreras.
- **`ETag`:** cada respuesta se guarda con su `ETag` en `%LOCALAPPDATA%\AgentStudio\skills-doctor\`; la siguiente va con
  `If-None-Match`, y un 304 no cuenta contra el límite.
- **Límites:** se respetan `x-ratelimit-remaining` y `retry-after`. Al llegar al límite, las skills que faltan salen «sin
  comprobar: límite de GitHub, se reinicia a las HH:MM». No se insiste.
- **Los fallos tienen código:** sin red, 403, 404 (repositorio borrado o privado → aviso) y respuesta que no se entiende.
- **Las respuestas son datos no fiables:** solo se leen los campos de la tabla, con sus tipos y topes.

**D-15 · El token es opcional y nunca sale de los CLIs.**

- **Sin token:** 60 peticiones por hora. Con dos peticiones por skill fijada y las cachés, basta para catálogos pequeños.
- **Con token:** 5 000 por hora. Un token personal de acceso fino, solo de lectura de repositorios públicos.
- **Dónde se guarda:** en la bóveda de `ICredentialVault`, cifrado con DPAPI para tu usuario de Windows. Hoy ese contrato no
  tiene implementación: esta fase escribe la mínima, y la Fase 7 (ADR-0043) la amplía por proyecto. Es la decisión 6.
- **Guardar o borrar el token pide Hello** (`RemoteOrSecretOperation`, de la lista fija). Usarlo en una comprobación, no: ya
  lo autorizaste con el ajuste y con el botón.
- **Nunca** se lee `gh auth token`, el gestor de credenciales de git ni ninguna carpeta de un CLI. El token no se escribe en
  el registro, en la bitácora ni en un log: `SecretRedactor` ya reconoce su forma, y una prueba lo comprueba.

### 3.7 Auto Graph (F6-3 a F6-5)

**D-16 · Sin MSBuild: se lee, no se evalúa.**

- **El riesgo.** La guía de Microsoft sobre MSBuild lo dice sin rodeos: hay que ejecutar MSBuild, «también para abrir
  proyectos», solo sobre fuentes de plena confianza, porque la lógica de build puede ejecutar código arbitrario.
  `MSBuildWorkspace` evalúa cada proyecto: importa `Directory.Build.props` y `.targets`, resuelve los SDK de `global.json` y
  de NuGet, y ejecuta los targets de diseño. Para Agent Studio el repositorio es contenido no fiable (ADR-0018).
- **La alternativa.** Leer los ficheros como texto:

  | Fichero | Lector | Qué se saca |
  |---|---|---|
  | `.slnx` | `XmlReader` con DTD prohibida, sin resolvedor y con tope de tamaño | Los proyectos y sus carpetas de solución |
  | `.sln` | Lector de líneas propio (no es XML) | Las líneas `Project(...) = "nombre", "ruta", "{guid}"` |
  | `.csproj` | El mismo `XmlReader` | `Sdk`, `TargetFramework(s)`, `OutputType`, `RootNamespace`, `AssemblyName`, y los `ProjectReference` y `PackageReference` con valor literal |
  | `.cs` | El analizador sintáctico de Roslyn, sin compilación | Los espacios de nombres que declara y sus `using` |

- **Lo que MSBuild resolvería no se adivina.** Un valor con `$(...)`, un elemento con `Condition`, un patrón con comodines o
  un `Import` no se evalúan: el proyecto sale «parcial» con el motivo (`graph.unevaluated`) y la línea. `Directory.Build.props`
  y `Directory.Packages.props` no se aplican; se anota que existen.
- **Qué ficheros `.cs` son de un proyecto:** los de su carpeta y subcarpetas, sin `bin`, `obj` ni las carpetas de otro
  proyecto. Es el comportamiento por defecto de un proyecto de estilo SDK. Un proyecto con `Compile Include` o `Remove` sale
  «parcial».
- **Descartado:** `MSBuildWorkspace` dentro de un proceso a integridad baja. Baja el daño, pero sigue ejecutando código del
  repositorio con tus lecturas y tu red (los residuales de ADR-0017), y F6-12 pide que no se ejecute nada.

**D-17 · Roslyn, solo el analizador sintáctico.** Dependencia nueva: es la decisión 3.

- **El paquete:** `Microsoft.CodeAnalysis.CSharp` (trae `Microsoft.CodeAnalysis.Common`). Licencia MIT, de Microsoft y la
  .NET Foundation. La última versión estable en nuget.org el 2026-11-12 es la **5.9.0**; el P0 la fija tras comprobar avisos
  de seguridad y que compila con 0 avisos.
- **Lo que no entra:** `Microsoft.CodeAnalysis.Workspaces.*`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`,
  `Microsoft.Build.*` ni `Microsoft.Build.Locator`. Una prueba de arquitectura lo fija (F6-12).
- **Cómo se usa:** `CSharpSyntaxTree.ParseText` con la versión más reciente del lenguaje, y se recorre el árbol. No se crea
  ninguna `Compilation`: sin analizadores, sin generadores de código, sin resolver referencias.
- **Referencias entre espacios de nombres:** el espacio A «usa» el B si un fichero que declara A tiene `using B` (o
  `using static`, o un alias) y B se declara en la solución. Es sintáctico: se marca así y no pretende ser el grafo de
  llamadas.
- **Por qué Roslyn y no un lector propio:** espacios de nombres con ámbito de fichero, anidados, directivas `#if`, cadenas
  crudas y comentarios hacen que una expresión regular falle en casos reales. Y F6-3 dice «con Roslyn».

**D-18 · El análisis corre en un proceso aparte, a integridad baja.** Es la decisión 4.

- **Un ejecutable propio,** `AgentStudio.GraphWorker` (proyecto nuevo, de consola), que recibe la ruta de una copia del
  commit y devuelve el modelo en JSON por su salida. Sería el proyecto número trece: SkillsMcp ya es un ejecutable aparte.
- **Se lanza con `ProcessIsolation.LowIntegrity`,** por el lanzador de siempre: no puede escribir fuera de su área, tiene su
  job de «matar al cerrar», 120 segundos y un tope de salida.
- **Por qué, si analizar no ejecuta nada:** un fichero hostil no ejecuta código, pero sí puede agotar la memoria o la pila
  del analizador. En un proceso aparte, eso no tira la app. Además Roslyn (unos 10 MB) no se carga en el proceso de la
  interfaz, y la app no referencia el paquete.
- **Topes dentro del proceso:** 2 000 proyectos, 50 000 ficheros `.cs`, 1 MB por fichero (los mayores se cuentan y se
  saltan), y los ficheros generados (`*.g.cs`, `*.Designer.cs`) fuera.
- **La salida del proceso es un dato no fiable para la app:** se lee con un esquema estricto y los mismos topes.

**D-19 · El grafo describe un commit, no tu carpeta.**

- **Cómo se lee:** `IRepositorySnapshots` (nuevo) pide a la capa git una copia del commit (`ArchiveCommitAsync`) en un área
  aislada, igual que la verificación. El proceso de D-18 solo ve esa copia.
- **Por qué un commit:** F6-10 define «desactualizado» frente al modelo de HEAD, y un commit se puede citar en la cabecera de
  un diagrama. Con cambios sin confirmar, la pantalla lo dice: «describe `abc1234`; tu carpeta tiene cambios».
- **Por qué la copia y no leer objetos de git:** un puerto de `ls-tree` y `cat-file` sería superficie nueva en la capa
  endurecida, con su revisión. La copia ya existe, ya está probada y ya resuelve los enlaces del árbol.
- **Coste:** una copia por cálculo. Se calcula cuando lo pides (al abrir la pantalla si HEAD cambió, o con «Actualizar») y el
  modelo se guarda por commit en `%LOCALAPPDATA%\AgentStudio\graph\<proyecto>\<commit>.json`. La copia se borra al terminar.
- **Comparte el cerrojo de la verificación** («una a la vez»): con una verificación en marcha, Auto Graph espera y lo dice.
- **Residual:** `git archive` respeta `export-ignore`. Un fichero así marcado no sale en la copia, y el grafo lo anota como
  «proyecto sin ficheros» si llega el caso.

**D-20 · El modelo: nodos, relaciones, origen y huella.** Vive en `src/AgentStudio.Domain/Graph/**` (nuevo), sin dependencias.

- **Nodos:** solución, proyecto, espacio de nombres y paquete. Cada uno con su identificador estable (la ruta relativa del
  proyecto, o proyecto más nombre), su ruta si la tiene y si es «parcial».
- **Relaciones:** contiene, referencia de proyecto, referencia de paquete y uso entre espacios de nombres.
- **Origen de cada relación:** estática (leída) o inferida (D-21).
- **Huella del modelo:** SHA-256 de su JSON canónico (ordenado de forma ordinal), solo con lo estático, más la versión del
  generador. El mismo commit da siempre la misma huella: es lo que decide «desactualizado».
- **Cobertura:** cuántos proyectos salieron completos, cuántos parciales y por qué. Nada parcial se presenta como completo.

**D-21 · Las relaciones de una IA van aparte y se ven distintas (F6-4).**

- **En el modelo:** una relación inferida lleva quién la propuso (agente y modelo), cuándo y por qué. Sus dos extremos tienen
  que existir en el modelo estático; si no, se descarta. Nunca entra en la huella ni en los diagramas generados.
- **En pantalla:** línea discontinua, otro color, la etiqueta «IA» y su propia entrada en la leyenda. El nombre accesible
  dice «relación propuesta por <agente>»: no depende solo del color. Un interruptor las oculta.
- **De dónde salen:** una acción «Proponer relaciones», con tu OK cada vez: un lanzamiento de solo lectura con
  `AuxiliaryAgentRunner`, que recibe la lista de nodos y devuelve, con un esquema estricto, como mucho 50 relaciones. Queda
  `GraphRelationsProposed` en el registro, con el texto del agente como evidencia. Gasta cuota: es la decisión 7.
- **Sin esa acción** (decisión 7, opción b), el modelo y la vista siguen distinguiendo los orígenes, la prueba de F6-4 usa
  relaciones preparadas y la leyenda de «IA» no se muestra mientras no haya ninguna: nada simulado en la interfaz.

**D-22 · La pantalla y la navegación (F6-5).**

- **Un documento «Auto Graph»,** con su entrada en la barra de actividad. Lienzo de Nodify en solo lectura.
- **Disposición determinista, calculada por la app:** por capas según la profundidad de dependencias y, dentro de cada capa,
  por nombre. Sin librería de disposición. Con más de 200 nodos visibles, los espacios de nombres se pliegan en su proyecto.
- **Dos niveles:** proyectos (por defecto) y, al desplegar uno, sus espacios de nombres. Los paquetes, ocultos salvo que los
  pidas.
- **Del nodo al fichero:** un proyecto abre su `.csproj` en el editor; un espacio de nombres enseña la lista de ficheros que
  lo declaran y abre el que elijas.
- **Del nodo a la tarea:** un proyecto enseña las tareas que cambiaron ficheros bajo su carpeta (de `ChangedPaths` de los
  intentos, en el registro) y abre la que elijas en el monitor.
- **Accesible por teclado,** con una lista equivalente al lienzo para el lector de pantalla.

### 3.8 Diagramas (F6-10)

**D-23 · Tres generadores, de modelo a texto, puros.** Viven en `src/AgentStudio.Memory/Diagrams/**` (nuevo).

| Diagrama | Sintaxis Mermaid | Modelo de origen |
|---|---|---|
| La solución | `flowchart LR`, con un `subgraph` por carpeta de solución | `CodeGraph` (proyectos y referencias; los espacios de nombres, en un segundo diagrama por proyecto) |
| El plan de una ejecución | `flowchart TD` | `PlanDefinition` de esa ejecución |
| La secuencia de una ejecución | `sequenceDiagram` | Las entradas del registro de esa ejecución: lanzamientos, informes, verificaciones, integraciones, veredictos y aprobaciones |

- **Participantes de la secuencia:** tú, el orquestador, cada agente por su nombre de la plantilla, el sistema (verificación
  e integración) y el juez.
- **«Con Roslyn o MSBuild»,** dice F6-10: aquí es con Roslyn y la lectura de D-16. MSBuild queda descartado.
- **Solo lo estático.** Las relaciones inferidas no salen en un diagrama generado.

**D-24 · Una gramática acotada, para que compile siempre.**

- **Identificadores propios** (`n1`, `n2`…), asignados por orden ordinal del modelo. Ningún nombre del repositorio es un
  identificador de Mermaid.
- **Etiquetas siempre entre comillas y escapadas:** las comillas, `#`, `<`, `>`, `&` y los saltos de línea se sustituyen por
  sus entidades. Tope de 80 caracteres por etiqueta.
- **Sin directivas** (`%%{init}%%`), sin `click`, sin enlaces, sin estilos por clase que vengan de datos.
- **Topes:** 200 nodos y 400 relaciones por diagrama. Si se pasa, se agrupa por carpeta y una nota dice cuánto se omitió.
- **Fin de línea `\n` y UTF-8 sin BOM,** para que los dorados sean iguales byte a byte en cualquier equipo.
- **Por qué basta sin motor:** quien escribe el texto es código nuestro con una gramática pequeña, y los ficheros dorados
  fijan cada forma. «Que compile» con `mermaid.parse` hace falta para los diagramas de agentes, que son de la Fase 16.

**D-25 · Cabecera, carpeta y «desactualizado».**

- **Dónde:** `.agentstudio/diagrams/`, en Markdown con un bloque `mermaid`, para que GitHub lo pinte. Nombres de una lista
  cerrada: `solucion.md`, `proyecto-<nombre>.md`, `plan-<ejecución>.md` y `secuencia-<ejecución>.md`. Se añaden a
  `AgentStudioFiles`.
- **La cabecera,** un comentario al principio del fichero:

  ```text
  <!-- Generado por Agent Studio. Editarlo no cambia nada: se regenera.
       diagrama: solucion · generador: 1
       commit: 45f243d3… · modelo: sha256:… · fecha: 2026-11-12T10:00:00Z -->
  ```

  Para el plan y la secuencia, en vez del commit va la ejecución y la cabeza del registro que describe.
- **Nunca se lee de vuelta.** Agent Studio no interpreta ese fichero: ni su cabecera ni su contenido. Para saber qué generó,
  guarda **su propio índice** en `%LOCALAPPDATA%\AgentStudio\diagrams\<proyecto>\index.json`: fichero, diagrama, huella del
  modelo y SHA-256 de lo que escribió.
- **Estados:**
  - **al día:** la huella del modelo de ahora es la del índice;
  - **desactualizado:** no lo es. Se dice qué commit describe;
  - **cambiado fuera:** el SHA-256 del fichero no es el que se escribió (lo editaste o lo borraste). Solo se calcula su
    huella; no se interpreta;
  - **sin datos:** el fichero existe pero este equipo no lo generó (un clon). Se regenera para saberlo.
- **Cuándo se escribe:** cuando pulsas «Generar» o «Regenerar», y nunca con una ejecución en marcha, como la exportación de
  la memoria. Regenerar sobrescribe; la versión anterior queda en el índice local para poder deshacer (D-10).
- **Versionarlos o no** es tu elección, como la memoria (decisión 9).
- **Editar:** lo generado no se edita. Para un diagrama tuyo, copia el fichero a otro sitio; ahí el sistema no lo toca.

**D-26 · En la Fase 6 el diagrama se ve como texto; el dibujo ya existe en otra pantalla.** Es la decisión 2.

- **La sección «Diagramas»** lista cada diagrama con su estado y enseña su fuente en el editor en solo lectura, con «Copiar»
  y «Abrir la carpeta».
- **El dibujo:** la solución se ve dibujada en Auto Graph (D-22) y el plan, en el lienzo. La secuencia no tiene dibujo en la
  app hasta la Fase 16; GitHub y cualquier visor de Markdown la pintan.
- **Por qué no el motor ahora:** docs/19 D-18 ya lo diseña entero (mermaid.js fijado por huella, filtro de peticiones, cola,
  saneado del SVG y un spike previo). Traerlo aquí duplicaría ese paquete o lo partiría en dos fases.

### 3.9 Discrepancias (F6-11)

**D-27 · Cuatro comprobaciones con código, sin IA, sobre el commit HEAD.** Viven en `src/AgentStudio.Memory/Consistency/**`
(nuevo) y leen la copia del commit por `IRepositorySnapshots`.

| Código | Qué detecta | Gravedad |
|---|---|---|
| `doc.link-broken` | Un enlace relativo de un `.md` a una ruta que no existe en el commit | Aviso |
| `doc.adr-cites-missing` | Un ADR nombra, entre comillas invertidas, una ruta que no existe en el commit | Aviso |
| `doc.diagram-stale` | Un diagrama generado «desactualizado» (D-25) | Aviso |
| `requirement.uncovered` | Un requisito de un hito abierto sin ninguna tarea que lo cubra | Aviso |

- **Los tres `doc.*` son los de docs/19 D-27,** con el mismo significado. El cuarto es el código que ya usa el informe del
  hito.
- **Cada hallazgo dice dónde** (fichero y línea, o hito y requisito) **y qué hacer:** abrir el fichero, regenerar el diagrama
  o ir al tablero.

**D-28 · Qué es un enlace y qué es una cita.** Reglas cerradas, para que no haya falsos positivos por sorpresa.

- **Enlaces:** `[texto](destino)` e imágenes, en ficheros `.md`, fuera de bloques de código. Se saltan los que tienen
  esquema (`https:`, `mailto:`) y los que son solo un ancla. Del destino se quita el ancla y se resuelve desde la carpeta del
  fichero; si sale del repositorio, es un hallazgo.
- **Mayúsculas:** git distingue mayúsculas y Windows no. Un enlace que solo falla por mayúsculas se dice así, porque en
  GitHub estaría roto.
- **ADR:** los ficheros `NNNN-*.md` bajo una carpeta llamada `adr`.
- **Citas de un ADR:** el texto entre comillas invertidas que parece una ruta del repositorio: tiene `/` o `\`, no tiene
  espacios y acaba en una extensión o en `/`. Los patrones con `*` se comprueban por su prefijo fijo. Lo que lleve `<`, `{` o
  `%` (plantillas y variables) se salta.
- **«Borrado» significa «no existe en HEAD».** No se mira el historial para saber si existió: sería otra lectura de git y la
  consecuencia para ti es la misma.
- **Topes:** 5 000 ficheros `.md`, 1 MB por fichero y 2 000 hallazgos.

**D-29 · Los requisitos sin cubrir salen del tablero.** Para cada hito abierto, `IBoardProjector.ProjectAsync`; cada tarjeta
de requisito en la columna `Backlog` es un hallazgo. Memory solo depende de Application, así que usa ese puerto y no los
lectores internos de Orchestration. Sin hitos, la comprobación sale «no aplica».

**D-30 · Dónde se ven.** En la sección «Discrepancias» del documento Auto Graph, agrupadas por código, y en una línea de la
sección «Proyecto» de Auto Doctor («3 discrepancias»). No se guardan como evidencia: eso es de la Fase 16.

---

## 4. Contratos

Los escribe el P0 (§9.2) y los documenta en [04](04-contratos.md) §25 (la §24 la reserva docs/19). Aquí va su forma; los
nombres exactos de los miembros pueden ajustarse en el P0, no su reparto por capas. Todos los ficheros de esta sección son
nuevos salvo que se diga.

### 4.1 Dominio

`src/AgentStudio.Domain/Diagnostics/DoctorCheck.cs`:

```csharp
public enum DoctorStatus { Ok, Warning, Error, Unknown, NotApplicable }
public enum DoctorSection { Providers, Tools, Project, DeclaredEnvironment, Containers }
public sealed record DoctorFact(string Name, string Value);
public sealed record DoctorCheck(string Code, DoctorSection Section, DoctorStatus Status,
    IReadOnlyList<DoctorFact> Facts, string? FixCode = null);
```

- El título y la explicación no van en el dominio: salen del código (`doctor.dotnet.sdk` → `Text_doctor_dotnet_sdk`), con los
  hechos como parámetros (ADR-0054).

`src/AgentStudio.Domain/Environments/**` (lo completa WP-E): `DeclaredEnvironment` (lo leído, con su fichero y su línea),
`InstalledTools` (el inventario), `GlobalJsonRollForward`, `SemverRange`, `Pep440Specifier` y `EnvironmentComparer`, todos
puros.

`src/AgentStudio.Domain/Graph/CodeGraph.cs`:

```csharp
public enum GraphNodeKind { Solution, Project, Namespace, Package }
public enum GraphEdgeKind { Contains, ProjectReference, PackageReference, NamespaceUse }
public enum GraphEdgeOrigin { Static, Inferred }
public sealed record GraphNode(string Id, GraphNodeKind Kind, string Name, string? Path, bool Partial);
public sealed record GraphInference(string Agent, string Model, DateTimeOffset At, string Reason);
public sealed record GraphEdge(string From, string To, GraphEdgeKind Kind, GraphEdgeOrigin Origin, GraphInference? Inference = null);
public sealed record GraphGap(string NodeId, string Code, string Path, int? Line);
public sealed record CodeGraph(string Commit, int GeneratorVersion, IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges, IReadOnlyList<GraphGap> Gaps)
{
    public string ModelHash { get; }   // static nodes and edges only, canonical JSON
}
```

### 4.2 Application

`src/AgentStudio.Application/Diagnostics/DoctorContracts.cs`:

```csharp
public interface IDoctorCheckSource
{
    DoctorSection Section { get; }
    Task<IReadOnlyList<DoctorCheck>> CheckAsync(ProjectContext? project, CancellationToken ct);
}
public interface IDoctorService   // runs every source; a source that throws becomes one Error check
{
    Task<DoctorReport> RunAsync(ProjectContext? project, CancellationToken ct);
}
public enum DoctorTool { DotNetListSdks, DotNetListRuntimes, NodeVersion, NpmVersion, PythonVersion, PyVersion,
    GitVersion, DockerVersionJson, WslStatus }
public interface IDoctorToolRunner
{
    Task<DoctorToolResult> RunAsync(DoctorTool tool, string? projectRoot, CancellationToken ct);
}
public interface IInstalledToolInventory
{
    Task<InstalledTools> ReadAsync(string? projectRoot, CancellationToken ct);
}
public enum DoctorFixKind { Navigate, Guide, Apply }
public interface IDoctorFix
{
    string Code { get; }
    DoctorFixKind Kind { get; }
    bool IsReversible { get; }               // Apply fixes only
    ApprovalRequest? Approval { get; }       // Apply fixes that are not reversible must carry one
    Task<DoctorFixResult> ApplyAsync(ProjectContext? project, CancellationToken ct);
    Task UndoAsync(ProjectContext? project, CancellationToken ct);
}
```

- `IDoctorToolRunner` no tiene ninguna sobrecarga con un ejecutable o argumentos libres: es la garantía de D-02.

`src/AgentStudio.Application/Diagnostics/EnvironmentContracts.cs` (la ruta que ya nombraba docs/16):

```csharp
public interface IDeclaredEnvironmentReader
{
    Task<DeclaredEnvironment> ReadAsync(string projectRoot, CancellationToken ct);
}
```

`src/AgentStudio.Application/Verification/EvidenceToolVersions.cs`: `Format(...)` y `Parse(...)` del bloque `tools:` (D-12).

`src/AgentStudio.Application/Git/RepositorySnapshotContracts.cs`:

```csharp
public interface IRepositorySnapshots
{
    Task<IRepositorySnapshot> OpenAsync(ProjectContext project, string commit, CancellationToken ct);
}
public interface IRepositorySnapshot : IAsyncDisposable
{
    string Commit { get; }
    string Root { get; }                                  // inside an isolated work area
    IReadOnlyList<string> Paths { get; }                  // relative, '/' separated, ordinal order
    Stream OpenRead(string relativePath, long maxBytes);  // refuses links and anything over the limit
}
```

`src/AgentStudio.Application/Graph/GraphContracts.cs`:

```csharp
public interface ICodeGraphReader   // cached by commit; computes through the worker when missing
{
    Task<CodeGraph> ReadAsync(ProjectContext project, string commit, CancellationToken ct);
}
public interface IGraphInferenceStore   // inferred edges, apart from the static model
{
    Task<IReadOnlyList<GraphEdge>> ListAsync(ProjectContext project, CancellationToken ct);
}
public interface IGraphTaskIndex   // F6-5: tasks whose attempts changed files under a folder
{
    Task<IReadOnlyList<GraphTaskRef>> TasksUnderAsync(ProjectContext project, string folder, CancellationToken ct);
}
```

`src/AgentStudio.Application/Diagrams/DiagramContracts.cs`:

```csharp
public enum DiagramKind { Solution, ProjectNamespaces, RunPlan, RunSequence }
public enum DiagramState { Current, Stale, ChangedOutside, Unknown, NotGenerated }
public sealed record DiagramSource(DiagramKind Kind, string Subject, string ModelHash, string Mermaid);
public interface IDiagramGenerator   // one per kind; pure text from a model
{
    DiagramKind Kind { get; }
    Task<DiagramSource> GenerateAsync(ProjectContext project, string subject, CancellationToken ct);
}
public interface IDiagramCatalog
{
    Task<IReadOnlyList<DiagramStatus>> ListAsync(ProjectContext project, CancellationToken ct);
    Task<DiagramStatus> WriteAsync(ProjectContext project, DiagramKind kind, string subject, CancellationToken ct);
}
```

- La Fase 16 añade valores a `DiagramKind` y más `IDiagramGenerator`. Su `IDiagramRenderer` (docs/19 D-18) recibe un
  `DiagramSource`.

`src/AgentStudio.Application/Consistency/ConsistencyContracts.cs`:

```csharp
public sealed record ConsistencyFinding(string Code, string? Path, int? Line, IReadOnlyList<DoctorFact> Facts);
public interface IConsistencyCheck { string Code { get; } Task<IReadOnlyList<ConsistencyFinding>> RunAsync(ConsistencyScope scope, CancellationToken ct); }
public interface IConsistencyService { Task<ConsistencyReport> RunAsync(ProjectContext project, CancellationToken ct); }
```

- La Fase 16 añade sus `doc.*` como más `IConsistencyCheck`.

`src/AgentStudio.Application/Skills/SkillPorts.cs` (existe): `ISkillHealthChecker` no cambia. Se añaden
`ISkillScriptReviews` (guardar y leer la huella revisada) y el resultado de una tanda con su motivo cuando no se pudo
comprobar.

`src/AgentStudio.Application/Security/SecurityContracts.cs` (existe): `ICredentialVault` no cambia; se implementa (D-15).

### 4.3 Payloads

`src/AgentStudio.Application/Decisions/DoctorPayloads.cs`:

```csharp
public sealed record DoctorFixAppliedPayload(string FixCode, string CheckCode, long? Undoes) : DecisionPayload;
public sealed record GraphRelationsProposedPayload(string Commit, string ModelHash, EvidenceId Proposal,
    IReadOnlyList<ProposedRelation> Accepted, int Discarded, string Agent, string Model) : DecisionPayload;
```

- Las dos con `RunId = NoRun`. Sin texto libre, sin rutas absolutas y sin nada del token.
- `ProposedRelation` lleva los identificadores de los dos nodos y un motivo de 200 caracteres como máximo, ya saneado.
- El segundo solo existe con la decisión 7 (a).

---

## 5. Modelo de datos

**Ninguna migración.** El esquema sigue en la versión 7.

| Dato | Dónde | Por qué ahí |
|---|---|---|
| Arreglos aplicados y deshechos | Registro: `DoctorFixApplied` | Es una decisión tuya sobre el proyecto |
| Relaciones propuestas por una IA | Registro: `GraphRelationsProposed`, y el texto del agente como evidencia | Texto de agente, anclado y con su autor |
| Versiones de las herramientas | Dentro del contenido de cada evidencia de verificación | Ya anclado por su huella (D-12) |
| El modelo de Auto Graph de un commit | `%LOCALAPPDATA%\AgentStudio\graph\<proyecto>\<commit>.json` | Derivado: se borra y se recalcula |
| El índice de diagramas generados | `%LOCALAPPDATA%\AgentStudio\diagrams\<proyecto>\index.json` | Derivado, y de tu equipo (D-25) |
| Los diagramas | `.agentstudio/diagrams/*.md`, en el proyecto | Exportación generada, como la memoria |
| Respuestas de GitHub con su `ETag` | `%LOCALAPPDATA%\AgentStudio\skills-doctor\cache.json` | Caché: se puede borrar |
| Huella revisada de los scripts de una skill | El almacén de activaciones de skills (`SkillActivationStore`), con un campo nuevo | Va con la skill, no con un proyecto |
| El token de GitHub | La bóveda (`%LOCALAPPDATA%\AgentStudio\vault\`), cifrado con DPAPI | D-15 |
| El ajuste «Comprobar las skills en GitHub» | `AppSettings` (y `WithPreferencesOf`) | Como los demás permisos de Ajustes |
| La carpeta neutra | `%LOCALAPPDATA%\AgentStudio\doctor\cwd` | D-02 |

- **El resultado de Auto Doctor no se guarda:** se calcula al abrir el panel. No es historia del proyecto.
- **`DecisionKind` crece en dos valores, al final,** con sus pruebas de compatibilidad.
- **`AgentStudioFiles`** gana `DiagramsFolder` e `IsDiagramFile`, e `IsAgentStudioStatusLine` los reconoce.

---

## 6. Pantallas

Todas con sus textos en recursos, en castellano y en inglés; con su ⓘ; con su artículo de ayuda; y en el inventario
automático.

### 6.1 Auto Doctor (panel, ampliado)

- **Qué cambia:** de una lista de proveedores a cinco secciones plegables (D-04). El botón «Volver a comprobar» sigue.
- **Cada comprobación:** punto de estado (con forma además de color), título, una línea y «Detalles» con sus hechos. Si tiene
  arreglo, un botón con su clase a la vista: «Ir a…», «Cómo arreglarlo» o «Aplicar» (y «Deshacer» después).
- **El entorno declarado,** como una tabla de cuatro columnas: qué pide, dónde lo dice, qué hay y el resultado (D-08).
- **Ficheros:** `src/AgentStudio.Desktop/Doctor/**` (nuevo), `src/AgentStudio.Desktop/Views/AutoDoctorView.xaml` (existe) y
  `src/AgentStudio.Desktop/Views/Doctor/**` (nuevo). `ProviderDiagnosticsViewModel` se conserva como la sección de
  proveedores.
- **Ayuda:** artículo `doctor` (nuevo).

### 6.2 Skills (pantalla, ampliada)

- **«Comprobar en GitHub»:** desactivado, con su motivo, si el ajuste está apagado. Al pulsar, una tarjeta dice qué se envía
  y a dónde, la primera vez.
- **«Marcar los scripts como revisados»,** en la skill, con la lista de scripts y un enlace para abrir cada uno.
- **Estado del límite** de GitHub y de la última comprobación.
- **Ficheros:** `src/AgentStudio.Desktop/Skills/**` y `src/AgentStudio.Desktop/Views/Skills/SkillsView.xaml` (existen).
- **Ayuda:** el artículo `skills` se amplía.

### 6.3 Ajustes

- El permiso «Comprobar las skills en GitHub» (apagado), y debajo «Token de GitHub (opcional)»: guardar, sustituir y borrar,
  cada uno con Hello. El token nunca se vuelve a mostrar: solo «hay un token guardado».

### 6.4 Auto Graph (documento nuevo, con tres secciones)

- **«Grafo»** (D-22): el lienzo, la leyenda (estática, parcial y, si las hay, IA), el commit que describe, la cobertura y
  «Actualizar».
- **«Diagramas»** (D-26): la lista con su estado, la fuente en solo lectura, «Generar», «Regenerar» y «Copiar».
- **«Discrepancias»** (D-30): los hallazgos por código, cada uno con su acción.
- **Un documento con secciones,** como «Trabajo» con `WorkSection`: el P0 crea el documento anfitrión y el contrato de sección
  (`src/AgentStudio.Desktop/Graph/GraphSection.cs`, nuevo); WP-G aporta «Grafo» y WP-D las otras dos, cada uno en su carpeta.
- **Ficheros:** `src/AgentStudio.Desktop/Graph/**` y `Views/Graph/**` (WP-G), `src/AgentStudio.Desktop/Docs/**` y
  `Views/Docs/**` (WP-D; la ruta que nombraba docs/16, distinta de `Desktop/Documentation/**`, que es de docs/19).
- **Ayuda:** artículo `graph` (nuevo), que cubre las tres secciones.
- **En la Fase 16,** «Diagramas» y «Discrepancias» se enseñan también en «Documentación»: son las mismas secciones.

---

## 7. Seguridad frente a ADR-0018

El modelo de amenaza no cambia: el contenido del repositorio es no fiable. Lo que esta fase añade:

| Superficie nueva | Riesgo | Cómo se cierra |
|---|---|---|
| Lanzar herramientas del sistema (D-02) | Que se ejecute algo del repositorio, o con su configuración | Lista cerrada, resolución fuera del proyecto, carpeta neutra, entorno limpio. F6-7 lo prueba |
| Leer manifiestos (D-05) | Fichero enorme, enlace hacia fuera, `include` que tira de otro fichero, valores que acaban en una orden | Lista cerrada, 256 KB, sin enlaces, sin seguir referencias, valores solo como datos |
| Analizar la solución (D-16 a D-19) | Ejecutar código por MSBuild, analizadores o generadores; agotar memoria | Sin MSBuild ni `Compilation`; proceso aparte a integridad baja sobre una copia; topes. F6-12 lo prueba |
| La salida del proceso de análisis | Un JSON manipulado | Esquema estricto y topes al leerlo |
| Red a GitHub (D-13) | Fuga de datos, destino inesperado, uso sin permiso | Apagado por defecto, solo a petición, solo `api.github.com`, sin redirecciones a otro host. F6-13 lo prueba |
| Respuestas de GitHub | Datos manipulados o enormes | 1 MB, solo los campos esperados |
| El token (D-15) | Que se filtre o se tome de donde no toca | DPAPI, Hello al guardar y borrar, nunca en registro ni logs, nunca de un CLI |
| Texto de un agente (D-21) | Que pase por hecho, o que traiga instrucciones | Esquema estricto, extremos que existen, marcado, fuera de la huella y de los diagramas; el juez no lo recibe |
| Escribir en `.agentstudio/diagrams/` (D-25) | Sobrescribir algo tuyo; leerlo de vuelta como verdad | Nombres de lista cerrada, nunca durante una ejecución, índice propio, nunca se interpreta |
| Nombres del repositorio dentro de un diagrama (D-24) | Inyectar sintaxis de Mermaid (un `click` con una URL) | Identificadores propios y etiquetas escapadas; sin directivas |
| Arreglos (D-10) | Que la app cambie algo sin que lo sepas | Tres clases en el tipo; «aplicar» solo con deshacer o con aprobación; rastro en el registro |

**Residuales que se aceptan y se escriben en los ADR:**

- Una herramienta del sistema comprometida fuera del proyecto engaña a Auto Doctor (ya era el residual del PATH de ADR-0018).
- El proceso de análisis, a integridad baja, puede leer lo que tu usuario lee y abrir conexiones (residuales de ADR-0017). No
  ejecuta nada del repositorio, así que solo un fallo del propio analizador lo aprovecharía.
- `git archive` respeta `export-ignore`: el grafo y las discrepancias no ven esos ficheros.
- GitHub ve tu dirección IP y qué repositorios públicos consultas.

**Lo que no cambia:** la capa git sigue sin red; ningún CLI de proveedor recibe un prompt salvo la acción opcional de D-21,
con tu OK cada vez; el juez no recibe nada de esta fase.

---

## 8. Criterios de aceptación y cómo se verifican

F6-1 a F6-11 son los de [08](08-plan-y-criterios-de-aceptacion.md#fase-6--auto-doctor-y-auto-graph), sin cambiar su texto.
Aquí se concreta la prueba de cada uno.

| ID | Cómo se verifica, en concreto | Dónde |
|---|---|---|
| F6-1 | Una prueba por comprobación de D-03, con un `IDoctorToolRunner` de prueba y salidas grabadas; una del agregador con una fuente que falla; una del adaptador de proveedores | `tests/AgentStudio.Tests/{Application,Infrastructure}/Diagnostics/**` |
| F6-2 | Una prueba recorre **todos** los arreglos registrados y exige que cada «aplicar» sea reversible o lleve aprobación; aplicar y deshacer deja el estado inicial y dos entradas; un arreglo de prueba con aprobación denegada no aplica nada | `tests/.../Application/Diagnostics/DoctorFixTests.cs` |
| F6-3 | El grafo de `AgentStudio.slnx`: sus proyectos, las referencias de cada `.csproj` y los espacios de nombres, contra un dorado; lectores de `.sln` y `.slnx` con ejemplos; un proyecto con `Condition` sale «parcial» | `tests/.../Infrastructure/Graph/**`, `tests/.../GraphWorker/**` |
| F6-4 | Prueba de ViewModel: una relación inferida tiene otro estilo, su etiqueta y su nombre accesible, y el interruptor la oculta; la huella del modelo no cambia al añadirla | `tests/.../Desktop/Graph/**` |
| F6-5 | Demostración: de un proyecto a su `.csproj`, de un espacio de nombres a un fichero y de un proyecto a una tarea. Más una prueba de ViewModel de cada orden | Demostración y `tests/.../Desktop/Graph/**` |
| F6-6 | Respuestas grabadas de la API (archivado, comparación con y sin cambios bajo la carpeta, truncada, 304, 403 por límite, 404); la huella de scripts cambia y vuelve a «sin revisar»; con y sin token | `tests/.../Infrastructure/Skills/GitHub/**` con `Fixtures/` |
| F6-7 | Un repositorio de ejemplo por pila y uno con todo; lectores con ficheros hostiles (enorme, enlace, JSON profundo, `include`). **La prueba central:** un repositorio con `dotnet.cmd`, `node.exe`, `git.exe` y `.venv\Scripts\python.exe` plantados, su carpeta en el PATH y un `global.json` con `sdk.paths`; un lanzador que registra cada `ProcessSpec` comprueba que ninguno tiene la carpeta de trabajo ni el ejecutable dentro del repositorio | `tests/.../Domain/Environments/**`, `tests/.../Infrastructure/Diagnostics/**` |
| F6-8 | El verificador con un perfil de dos pasos (dos herramientas distintas): cada evidencia lleva su bloque `tools:` con la versión de su paso, y `EvidenceToolVersions.Parse` lo lee | `tests/.../Orchestration/Verification/**` |
| F6-9 | Salidas grabadas de `docker version --format json` (sin motor y con él) y de `wsl --status` (sin WSL, sin distribución y listo), en castellano y en inglés; cada una da su estado, su versión y su arreglo | `tests/.../Infrastructure/Diagnostics/Fixtures/**` |
| F6-10 | Dorados de los tres generadores: la propia solución, y un plan y un registro preparados; dos generaciones seguidas, iguales byte a byte; cambia el modelo y sale «desactualizado»; se edita el fichero y sale «cambiado fuera» sin que nada lo interprete; nombres hostiles (comillas, `click`, saltos de línea) salen escapados | `tests/.../Memory/Diagrams/**` |
| F6-11 | Un repositorio preparado con un fallo de cada clase y uno sin fallos; los casos de D-28 (ancla, URL, bloque de código, mayúsculas, patrón con `*`); un hito con un requisito sin tarea | `tests/.../Memory/Consistency/**` |

**Criterios que se añaden,** porque F6-3 y F6-6 no dicen nada de seguridad y esta fase gira en torno a ella. No cambian el
sentido de ninguno anterior; si no los quieres como criterios, quedan como pruebas (decisión 12).

| ID | Criterio de aceptación | Cómo se verifica |
|---|---|---|
| F6-12 | Auto Graph no ejecuta nada del repositorio: no evalúa MSBuild ni crea una compilación, y analiza una copia del commit en un proceso a integridad baja | Una prueba de arquitectura: ningún proyecto referencia `Microsoft.Build*` ni `Microsoft.CodeAnalysis.Workspaces*`. Un repositorio con un `Directory.Build.props` que lanza un proceso, un analizador y un generador plantados: tras el análisis, su centinela no existe y el único proceso lanzado es el de análisis |
| F6-13 | El doctor de skills no usa la red sin tu permiso, solo habla con `api.github.com` y nunca deja el token a la vista | Con el ajuste apagado, un manejador HTTP de prueba no recibe ninguna petición; una redirección a otro host se rechaza; el token no aparece en el registro, la bitácora ni los logs tras una tanda |

**Reglas de todas las pruebas:** solo `Assert` de xUnit; `using System.IO;` donde haga falta; las que lanzan herramientas
reales del equipo, con `Category=LocalTools`; ninguna con red real (todo con respuestas grabadas); ninguna con un CLI de
proveedor.

---

## 9. Paquetes de trabajo

### 9.1 Reglas comunes

Las de [17 §9.1](17-constructor-visual-de-agentes.md#91-reglas-comunes) y docs/19 §11.2:

- el P0 va en serie y se fusiona antes de abrir los paquetes;
- los paquetes van en worktrees, **como mucho dos a la vez,** sin ningún fichero en común;
- integra el lead.

**Dueños únicos:**

- **El P0:** `Directory.Packages.props`, `AgentStudio.slnx`, los `*.csproj`, los enums compartidos y los contratos de §4.
- **El lead, en la integración:** `src/AgentStudio.Desktop/Composition/**`, `MainViewModel*.cs`, `MainWindow.xaml(.cs)`,
  `App.xaml(.cs)`, `Themes/DocumentTemplates.xaml`, `Help/HelpScreens.cs`, `Settings/**`, `Services/AppSettings.cs`,
  `tests/AgentStudio.Tests/Desktop/UiInventory/UiScreens.cs`, `docs/03`, `docs/08`, `docs/18`, `docs/README.md` y la bitácora.

**«Hecho»** exige `dotnet build AgentStudio.slnx` con 0 avisos, `dotnet test` en verde sin `ProviderLive`, los ADR y documentos
del paquete, su artículo de ayuda, su pantalla en el inventario y nada simulado en la interfaz.

### 9.2 P0 · Contratos, dominio, payloads y dependencias (M; camino crítico)

- **Dominio:** `src/AgentStudio.Domain/Diagnostics/DoctorCheck.cs`, `src/AgentStudio.Domain/Graph/**` y los tipos de datos de
  `src/AgentStudio.Domain/Environments/` (`DeclaredEnvironment`, `InstalledTools`; la lógica es de WP-E). Todos nuevos.
- **Application:** los ficheros de §4.2 y §4.3; `Skills/SkillPorts.cs` (lo que se añade); `Projects/AgentStudioFiles.cs` (la
  carpeta de diagramas).
- **Registro:** `src/AgentStudio.Domain/Decisions/DecisionLog.cs` (dos valores al final) y
  `src/AgentStudio.Application/Decisions/DecisionPayloadSerializer.cs`.
- **Dependencias y solución:** `Directory.Packages.props` (`Microsoft.CodeAnalysis.CSharp`, tras comprobar versión, licencia
  y avisos); el proyecto `src/AgentStudio.GraphWorker/AgentStudio.GraphWorker.csproj` (nuevo, vacío, con su entrada en
  `AgentStudio.slnx`), si la decisión 4 es (a).
- **Escritorio, lo mínimo para repartir:** `src/AgentStudio.Desktop/Graph/{GraphDocumentViewModel,GraphSection}.cs` (el
  anfitrión y el contrato de sección) y los pares de recursos vacíos `DoctorStrings`, `GraphStrings` y `DocsStrings` en
  `src/AgentStudio.Desktop/Resources/`.
- **Pruebas:** `tests/AgentStudio.Tests/Domain/{Diagnostics,Graph}/**`, `Application/Phase6PayloadTests.cs`,
  `DecisionKindCompatibilityTests` y `DecisionPayloadTests` al día, y la prueba de arquitectura de F6-12.
- **Documentos:** [04](04-contratos.md) §25 y los esqueletos de los ADR de §10.
- **Sale del P0 con una comprobación, no con un spike:** que Roslyn 5.9.0 compila con 0 avisos en `net10.0` y qué pesa
  publicado.

### 9.3 WP-A · Auto Doctor: herramientas, proyecto, contenedores y arreglos (M-L)

- `src/AgentStudio.Infrastructure/Diagnostics/**` (nuevo): la puerta de D-02, el localizador que excluye el proyecto, el
  inventario de lo instalado, WebView2 por el registro, Docker y WSL.
- `src/AgentStudio.Application/Diagnostics/{DoctorService,ProviderCheckSource,ProjectCheckSource,DoctorFixes}.cs` (nuevos).
- `src/AgentStudio.Providers/Cli/CliProbe.cs` (existe): la carpeta neutra.
- `src/AgentStudio.Desktop/Doctor/**` y `Views/Doctor/**` (nuevos), `Views/AutoDoctorView.xaml(.cs)` y
  `ViewModels/ProviderDiagnosticsViewModel.cs` (existen), `Resources/DoctorStrings*.resx`, `Help/{es-ES,en}/doctor.md` (nuevo).
- Pruebas: `tests/AgentStudio.Tests/{Application,Infrastructure}/Diagnostics/**`, `Desktop/Doctor/**` y
  `Desktop/UiInventory/UiScreensDoctor.cs` (nuevo).
- Criterios: F6-1, F6-2, F6-9 y la mitad de F6-7 (la prueba de «ningún proceso»). ADR-0061.

### 9.4 WP-E · Entorno declarado y versiones en las evidencias (M)

- `src/AgentStudio.Domain/Environments/**` (la lógica: `rollForward`, rangos y el comparador).
- `src/AgentStudio.Infrastructure/Environments/**` (nuevo): el lector de D-05 y D-06, y la fuente de comprobaciones del
  entorno. Va aparte de `Infrastructure/Diagnostics/**` para no compartir carpeta con WP-A.
- `src/AgentStudio.Orchestration/Verification/VerificationRunner.cs` (existe): el bloque `tools:`.
- `src/AgentStudio.Application/Verification/EvidenceToolVersions.cs` (lo completa).
- Pruebas: `tests/AgentStudio.Tests/Domain/Environments/**`, `Infrastructure/Environments/**` con los repositorios de ejemplo,
  y `Orchestration/Verification/**`.
- Usa el inventario de WP-A por su puerto (`IInstalledToolInventory`), con un doble en sus pruebas.
- Criterios: F6-7 y F6-8. Aporta a ADR-0061.

### 9.5 WP-S · Doctor de skills (M)

- `src/AgentStudio.Infrastructure/Skills/GitHub/**` (nuevo): el cliente, la cola, la caché y `GitHubSkillHealthChecker`.
- `src/AgentStudio.Infrastructure/Skills/{SkillHealthReader,SkillActivationStore}.cs` (existen): `scripts`, `size` y la huella
  revisada.
- `src/AgentStudio.Infrastructure/Security/DpapiCredentialVault.cs` (nuevo), si la decisión 6 es (a).
- `src/AgentStudio.Desktop/Skills/**`, `Views/Skills/SkillsView.xaml`, `Resources/SkillsStrings*.resx` y
  `Help/{es-ES,en}/skills.md` (existen).
- Pruebas: `tests/AgentStudio.Tests/Infrastructure/Skills/GitHub/**` con `Fixtures/`, `Infrastructure/Security/**` y
  `Desktop/Skills/**`.
- Criterios: F6-6 y F6-13. ADR-0063 y [10 §6](10-catalogo-de-skills.md#6-salud-doctor) al día.
- Lo de Ajustes (el permiso y el token) lo pone el lead en la integración, sobre los puertos de este paquete.

### 9.6 WP-G · Auto Graph (L)

- `src/AgentStudio.GraphWorker/**` (nuevo): los lectores de `.sln`, `.slnx` y `.csproj`, el recorrido de Roslyn y el JSON de
  salida.
- `src/AgentStudio.Infrastructure/Graph/**` (nuevo): `IRepositorySnapshots` sobre el área aislada, el cliente del proceso, la
  caché por commit y el almacén de relaciones inferidas.
- `src/AgentStudio.Orchestration/Graph/**` (nuevo): `IGraphTaskIndex`, desde los intentos del registro.
- `src/AgentStudio.Application/Graph/GraphInferenceService.cs` (nuevo), si la decisión 7 es (a).
- `src/AgentStudio.Desktop/Graph/**` (salvo los dos ficheros del P0) y `Views/Graph/**` (nuevos), `Resources/GraphStrings*.resx`,
  `Help/{es-ES,en}/graph.md` (nuevo).
- Pruebas: `tests/AgentStudio.Tests/{GraphWorker,Infrastructure/Graph,Orchestration/Graph,Desktop/Graph}/**` y
  `Desktop/UiInventory/UiScreensGraph.cs` (nuevo).
- Criterios: F6-3, F6-4, F6-5 y F6-12. ADR-0062.

### 9.7 WP-D · Diagramas y discrepancias (M-L)

- `src/AgentStudio.Memory/Diagrams/**` (nuevo): los tres generadores, la gramática, la cabecera, el catálogo y su índice.
- `src/AgentStudio.Memory/Consistency/**` (nuevo): las cuatro comprobaciones y el servicio.
- `src/AgentStudio.Desktop/Docs/**` y `Views/Docs/**` (nuevos), `Resources/DocsStrings*.resx`.
- Pruebas: `tests/AgentStudio.Tests/Memory/{Diagrams,Consistency}/**` con dorados, `Desktop/Docs/**` y
  `Desktop/UiInventory/UiScreensDocs.cs` (nuevo).
- Lee el grafo y la copia del commit por sus puertos. El dorado «sobre la propia solución» necesita WP-G ya fusionado.
- Criterios: F6-10 y F6-11. ADR-0045.

### 9.8 Integración (el lead; M)

- La composición (`Composition/{DiagnosticsServices,GraphServices}.cs`, nuevos), «Auto Graph» en la barra de actividad, las
  plantillas de documento, los códigos `doctor` y `graph` en `HelpScreens.cs`, y el permiso y el token en Ajustes.
- La línea de discrepancias en Auto Doctor (D-30) y los arreglos «Regenerar los diagramas» y «Desactivar la skill», que cruzan
  paquetes.
- `tests/AgentStudio.Tests/Integration/Phase6/**`: Auto Doctor sobre este repositorio; el grafo, los diagramas y las
  discrepancias de este repositorio de punta a punta; una verificación de dos pasos con sus versiones.
- Documentos: [03](03-pantallas-y-navegacion.md), [08](08-plan-y-criterios-de-aceptacion.md) (el estado, y F6-12 y F6-13),
  [18](18-peticiones-y-pendientes.md) (P25 y el hallazgo de `sdk.paths`) y el README.
- La demostración de F6-5, y la prueba en vivo del doctor de skills contra GitHub, con tu OK.

### 9.9 Orden y dependencias

| Paquete | Depende de | Tamaño |
|---|---|---|
| P0 | La Fase 14 con la Fase 9 integradas | M |
| WP-A | P0 | M-L |
| WP-G | P0 | L |
| WP-E | P0 (usa WP-A por su puerto) | M |
| WP-D | P0; WP-G fusionado para su dorado final | M-L |
| WP-S | P0 | M |
| Integración | Todos | M |

Con dos a la vez, tres tandas:

1. **WP-G y WP-A:** el más largo y el que más desbloquea.
2. **WP-D y WP-E:** el primero ya tiene el grafo real; el segundo, el inventario real.
3. **WP-S,** con la integración empezando a su lado.

La fase entera es L. Los tamaños son estimaciones del diseño.

---

## 10. ADR a escribir

En `docs/adr/` el último es el 0057. Los 0041 a 0046 los reservó docs/16 (solo el 0045 es de esta fase) y los 0058 a 0060,
docs/19. Ninguno de ellos existe todavía. Esta fase usa el 0045 y propone 0061 a 0063; si otra tanda los toma, se corren.

| ADR | Tema | Paquete |
|---|---|---|
| 0045 (reservado por docs/16) | Diagramas generados con la versión que describen: Mermaid como texto, la gramática acotada, la cabecera, el índice propio y la vigencia; las discrepancias deterministas y sus códigos. La Fase 16 le añade una adenda con el motor | P0 y WP-D |
| 0061 | Auto Doctor: la comprobación única, la puerta cerrada para lanzar herramientas, los manifiestos como datos no fiables, comparar en código, los arreglos en tres clases y las versiones dentro de la evidencia (amplía ADR-0016 y ADR-0018) | P0, WP-A y WP-E |
| 0062 | Auto Graph: leer sin MSBuild, Roslyn solo sintáctico (dependencia nueva), el proceso de análisis a integridad baja sobre una copia del commit (el decimotercer proyecto, amplía ADR-0002), el modelo con origen y huella, y las relaciones inferidas aparte | P0 y WP-G |
| 0063 | El doctor de skills con red: solo `api.github.com`, apagado por defecto y a petición, caché con `ETag`, el token opcional en una bóveda DPAPI mínima y lo que nunca se lee (amplía ADR-0007) | WP-S |

---

## 11. Riesgos

| Riesgo | Mitigación |
|---|---|
| Sin MSBuild, el grafo de un proyecto con mucha lógica de build queda corto | Se marca «parcial» con el motivo y la línea; la cobertura está a la vista; en esta solución, las referencias son literales |
| El proyecto nuevo de análisis complica el empaquetado | Es un ejecutable de consola más, como SkillsMcp; si no lo quieres, la decisión 4 (b) lo mete en el proceso con topes |
| Roslyn sube de versión mayor a menudo | Solo se usa el analizador sintáctico, la parte más estable; la versión va fijada |
| Las salidas de `docker` y `wsl` cambian de formato o de idioma | Se decide por estructura y código de salida; salidas grabadas de este equipo; lo no reconocido sale «sin comprobar», nunca «correcto» |
| Los lectores de líneas de YAML y TOML se equivocan con un fichero raro | Solo extraen tres campos, marcan «parcial» y listan lo no interpretado; nunca deciden un error por sí solos |
| Demasiadas comprobaciones vuelven a hacer grande el panel | Secciones plegadas con el peor estado; «no aplica» oculto por defecto |
| Falsos positivos en las discrepancias | Reglas cerradas (D-28), gravedad «aviso» y pruebas con los documentos de este repositorio |
| El límite de GitHub sin token | Caché con `ETag`, en serie, y un estado claro al llegar al límite |
| La copia del commit es lenta en un repositorio grande | Una por cálculo y con caché por commit; comparte el cerrojo de la verificación y se puede cancelar |
| Dos fases tocan los diagramas | El reparto de §1.2 y los puertos de §4.2; ADR-0045 con su adenda |
| El equipo se queda sin memoria con dos agentes compilando | Dos paquetes a la vez como mucho, con la prioridad de build baja, como ya haces |

---

## 12. Sin verificar

- **`Microsoft.CodeAnalysis.CSharp` 5.9.0:** que sea la versión que conviene fijar (es la última del índice de nuget.org, que
  no dice su fecha), sus avisos de seguridad, su peso publicado y que compila con 0 avisos con `TreatWarningsAsErrors`. Su
  licencia MIT es la del repositorio `dotnet/roslyn`; el P0 la confirma en la ficha del paquete.
- **Los límites del analizador con ficheros hostiles:** que un fichero con anidamiento extremo termine con un error y no con
  la caída del proceso. Por eso va en un proceso aparte.
- **`wsl --status`:** su codificación (UTF-16, y `WSL_UTF8=1`), sus códigos de salida y su texto en castellano.
- **`docker version --format json`:** la forma exacta con el motor parado en Docker Desktop y qué código de salida da.
- **Las claves del registro de WebView2:** las documenta Microsoft; falta probarlas en este equipo con instalación por usuario.
- **La API de GitHub:** el límite de 300 ficheros de `compare`, y que un 304 no cuenta contra el límite sin token. Lo segundo
  lo dicen sus buenas prácticas (citadas en docs/16).
- **DPAPI en .NET 10:** si se usa el paquete `System.Security.Cryptography.ProtectedData` (de Microsoft, MIT) o una llamada
  directa a la API de Windows. Lo decide ADR-0063.
- **`git archive` y `export-ignore`:** que la copia de la verificación se comporta así en esta capa.
- **El coste de «Proponer relaciones»** (D-21): tokens por lanzamiento. Es una estimación hasta medirlo.
- **Los tamaños de los paquetes** son estimaciones del diseño.
- **No se compiló ni se ejecutó nada** para escribir este documento: las firmas de §4 son propuestas.

---

## 13. Decisiones que necesitan a la persona

Doce decisiones: de producto, de dependencias, de red y de cuota. La recomendada va primero, marcada.

1. **El reparto con la Fase 16** (§1.2).
   - **(a) Recomendada:** la Fase 6 hace F6-10 y F6-11 como texto y deja los puertos; la Fase 16 construye el motor y el resto
     encima. Es la opción (b) de docs/19 §14.1, pero con el reparto escrito pieza a pieza para que no haya «dos dueños».
   - (b) Lo que recomendaba docs/19: la Fase 16 se lleva todo WP-D. La Fase 6 se queda sin F6-10 ni F6-11 y baja a M-L.
   - (c) La Fase 6 hace también el motor que dibuja. Pasa a XL y retrasa Auto Doctor.
2. **Dibujar los diagramas en la app, en esta fase** (D-26).
   - **(a) Recomendada:** solo el texto. La solución ya se ve en Auto Graph y el plan en el lienzo.
   - (b) Adelantar aquí el motor de docs/19 (mermaid.js empaquetado en WebView2, sin red, con filtro de peticiones). Añade un
     paquete L y su spike.
3. **Roslyn como dependencia nueva** (D-17).
   - **(a) Recomendada:** `Microsoft.CodeAnalysis.CSharp`, solo el analizador sintáctico. Es lo que dice F6-3.
   - (b) Sin Roslyn: los espacios de nombres con un lector propio. Sin dependencia, pero falla en casos reales y no cumple la
     letra de F6-3.
4. **Dónde corre el análisis** (D-18).
   - **(a) Recomendada:** un ejecutable nuevo (el proyecto número trece), lanzado a integridad baja sobre una copia del commit.
   - (b) Dentro de la app, con topes de tamaño y de tiempo. Sin proyecto nuevo; un fichero hostil podría tirar la app, y
     Roslyn se carga en ella.
   - (c) Dentro del ejecutable de SkillsMcp, con otro modo de arranque. Sin proyecto nuevo, pero mezcla dos cosas.
5. **La red del doctor de skills** (D-13).
   - **(a) Recomendada:** apagada por defecto y solo cuando pulsas.
   - (b) Además, una comprobación al día con el permiso encendido. Más cómoda; es red en segundo plano.
   - (c) Sin red en esta fase: `upstream` y `archived` siguen «sin comprobar». F6-6 quedaría a medias.
6. **El token de GitHub** (D-15).
   - **(a) Recomendada:** una bóveda DPAPI mínima ahora, con Hello para guardar y borrar. La Fase 7 la amplía.
   - (b) Una variable de entorno (`AGENTSTUDIO_GITHUB_TOKEN`) que lees tú. Sin bóveda, pero el token queda a la vista de
     cualquier proceso tuyo.
   - (c) Sin token hasta la Fase 7. F6-6 dice «con token opcional»: quedaría pendiente esa parte.
7. **Las relaciones que propone una IA** (D-21). Gasta cuota.
   - **(a) Recomendada:** la acción «Proponer relaciones», con tu OK cada vez y un solo lanzamiento de solo lectura.
   - (b) Sin acción en esta fase: el modelo y la vista distinguen los orígenes, y quien las aporta llega con las notas de los
     agentes de la Fase 16. Sin cuota; F6-4 se prueba con datos preparados.
8. **Arreglos que tocan el sistema** (D-10).
   - **(a) Recomendada:** nunca. Auto Doctor explica y te da la orden; la ejecutas tú.
   - (b) Instalar con `winget`, con aprobación y Hello. Cómodo, pero es ejecutar instaladores desde la app.
9. **Versionar `.agentstudio/diagrams/`** (D-25).
   - **(a) Recomendada:** lo mismo que decidiste para la exportación de la memoria (decisión 4 de docs/14), para no tener dos
     reglas.
   - (b) Siempre versionados: se ven en GitHub; cada regeneración es un cambio en tu repositorio.
   - (c) Nunca: ignorados por git.
10. **YAML y TOML** (D-05).
    - **(a) Recomendada:** lectores propios de líneas, que sacan tres campos y marcan «parcial».
    - (b) Una librería de YAML ya en esta fase (docs/19, decisión 9, la trae en la 16). Lectura completa de compose; una
      dependencia más y más formato que vigilar.
11. **El orden** (§9.9).
    - **(a) Recomendada:** tras integrar la Fase 14 con la 9, P0 y tres tandas de dos; después la Fase 16.
    - (b) Solo Auto Doctor y el entorno primero (WP-A, WP-E y WP-S), y Auto Graph con los diagramas junto a la Fase 16.
12. **F6-12 y F6-13** (§8).
    - **(a) Recomendada:** entran en docs/08 como criterios de la Fase 6.
    - (b) Se quedan como pruebas de los paquetes, sin número.

---

## Fuentes

Consultadas el 2026-11-12.

- Microsoft Learn, «Secure MSBuild usage best practices»:
  <https://learn.microsoft.com/visualstudio/msbuild/msbuild-security-best-practices>
- Microsoft Learn, «global.json overview» (`rollForward`, `allowPrerelease`, `paths` y `errorMessage`, estos dos desde el SDK
  de .NET 10): <https://learn.microsoft.com/dotnet/core/tools/global-json>
- Microsoft Learn, «Test prerelease .NET SDKs locally with global.json paths»:
  <https://learn.microsoft.com/dotnet/core/tools/test-prerelease-sdk-locally>
- Microsoft Learn, «Distribute your app and the WebView2 Runtime» (cómo detectar el runtime por el registro):
  <https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution>
- nuget.org, índice de versiones de `Microsoft.CodeAnalysis.CSharp`:
  <https://api.nuget.org/v3-flatcontainer/microsoft.codeanalysis.csharp/index.json>
- Del repositorio: [08](08-plan-y-criterios-de-aceptacion.md), [10](10-catalogo-de-skills.md),
  [16](16-ampliacion-plataforma-de-proyectos.md) (§2.3, §2.9 y §4.3), [19](19-documentacion-viva-del-proyecto.md) (§5, §7 y
  §11) y los ADR [0016](adr/0016-perfil-de-verificacion.md), [0017](adr/0017-verificacion-aislada-baja-integridad.md),
  [0018](adr/0018-modelo-de-amenaza-fase-3.md), [0022](adr/0022-memoria-append-only-anclada.md),
  [0053](adr/0053-windows-hello-como-barrera-fija.md), [0054](adr/0054-idiomas-y-lenguaje-claro.md),
  [0055](adr/0055-configurador-por-proyecto.md) y [0057](adr/0057-documentacion-para-clientes.md).
