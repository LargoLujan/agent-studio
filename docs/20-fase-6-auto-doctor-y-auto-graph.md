# 20 · Fase 6 · Auto Doctor, Auto Graph, entorno declarado y diagramas: diseño

> **Estado:** las 15 decisiones de §13 se toman con la opción (a), la recomendada (2026-11-12), y el P0 (§9.2) está hecho en la rama `fase6-p0`: lo que
> verificó y ajustó está en §4.3, §4.4 y §12. Revisada el 2026-11-12 tras una revisión independiente: endurece
> los diagramas, las rutas del análisis, las direcciones de GitHub y Docker, alinea con docs/19 dónde viven los diagramas y
> quién lee YAML, y separa el paquete previo WP-0 (§9.0). Parte de la rama `fase14-integracion` en `45f243d3` (la Fase 14 y
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
   resuelto fuera del proyecto, carpeta de trabajo neutra y entorno por lista blanca. Por construcción, ningún proceso arranca dentro
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
12. **Los diagramas viven en una caché de tu equipo y van al repositorio solo si lo pides.** «Desactualizado» se decide
    con un índice propio: lo exportado nunca se lee de vuelta (D-25).
13. **Las discrepancias usan los códigos de docs/19** (`doc.link-broken`, `doc.adr-cites-missing`, `doc.diagram-stale`),
    para que la Fase 16 amplíe la lista y no la renombre (D-27).
14. **Ninguna tabla nueva.** Dos clases de entrada en el registro, cachés locales que se pueden borrar y un ajuste (§5).

### 0.2 Hallazgos de paso

Cosas que salieron al leer el código y que afectan a esta fase:

- **`ExecutableLocator` acepta una carpeta del repositorio si está en el PATH con ruta absoluta.** Solo descarta las entradas
  relativas (`src/AgentStudio.Infrastructure/Processes/ExecutableLocator.cs`). Para F6-7 no basta. Lo arregla, para toda
  la app, el paquete previo WP-0 (§9.0).
- **`CliProbe` no fija carpeta de trabajo y usa el localizador general:** las sondas heredan la carpeta de la app, y un
  `claude.cmd` de una carpeta del proyecto que esté en el PATH se ejecutaría (`src/AgentStudio.Providers/Cli/CliProbe.cs`).
  Lo arregla WP-0 (§9.0).
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
| Interpretar YAML (`compose.yaml`) | Un solo lector en la app: el de la Fase 16 (D-05) | Fase 16 (decisión 10) |

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
| Cabecera de versión, caché local y exportación al repositorio (`docs/generado/`) | Las fija (D-25) | Las reutiliza para sus documentos (docs/19 D-26) |
| Leer `compose.yaml` | Solo que existe | Su contenido, con su librería de YAML (único lector) |
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
- la fila «F6-10 y F6-11» de su tabla F16 desaparece;
- su D-26 y su decisión 8 (dónde vive lo generado) valen también para los diagramas de esta fase, con un solo exportador;
- su decisión 9 (la librería de YAML) da el único lector de `compose.yaml` de la app.

docs/19 §11 y §14 ya llevan estas notas.

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
  en `.agentstudio/`. Con los diagramas en la caché local (D-25) no hay que tocarla; con la opción (b) de la decisión 9
  habría que añadirle la carpeta.
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
- **Resuelto fuera del proyecto, y para toda la app.** El paquete previo WP-0 (§9.0) corrige el localizador general:
  `ExecutableLocator` descarta toda entrada del PATH, relativa **o absoluta**, que ya resuelta caiga dentro de un proyecto
  abierto (`IUntrustedRoots`). Compara rutas finales (enlaces resueltos), sin distinguir mayúsculas. La puerta, además,
  rechaza cualquier resultado que caiga dentro: dos comprobaciones para lo mismo.
- **Carpeta de trabajo neutra.** Siempre `%LOCALAPPDATA%\AgentStudio\doctor\cwd`, vacía y creada por la app. Así ningún
  `global.json`, `.npmrc` ni `.python-version` del proyecto influye en la respuesta.
- **Entorno por lista blanca, no por lista negra.** El proceso solo recibe las variables de una lista fija; todo lo demás no
  pasa, se llame como se llame:
  - de Windows: `SystemRoot`, `windir`, `SystemDrive`, `ComSpec`, `PATHEXT`, `USERPROFILE`, `HOMEDRIVE`, `HOMEPATH`,
    `APPDATA`, `LOCALAPPDATA`, `ProgramData`, `ProgramFiles`, `ProgramFiles(x86)`, `ProgramW6432`, `CommonProgramFiles`,
    `NUMBER_OF_PROCESSORS`, `PROCESSOR_ARCHITECTURE`, `USERNAME` y `USERDOMAIN`;
  - `PATH`, ya filtrado;
  - `TEMP` y `TMP`, apuntando a una carpeta propia (`%LOCALAPPDATA%\AgentStudio\doctor\tmp`);
  - y las que pone la puerta: `DOTNET_NOLOGO=1`, `DOTNET_CLI_TELEMETRY_OPTOUT=1`, `DOCKER_CONFIG` (una carpeta vacía
    propia, D-09) y `WSL_UTF8=1`.

  Por qué lista blanca: una lista negra se queda vieja con cada variable nueva de cada herramienta (`NODE_OPTIONS`,
  `DOTNET_ROOT`, `PYTHONSTARTUP`, `DOCKER_HOST`, `GIT_*`…); la blanca falla hacia lo seguro.
- **Topes.** 10 segundos y 64 000 caracteres de salida por sonda; de una en una.
- **Sin texto libre.** No hay entrada estándar ni argumentos que vengan del proyecto.
- **Las sondas de proveedores** (`CliProbe`) no entran en el catálogo: sus órdenes son las tres de siempre (`--version`,
  `claude auth status` y `codex login status`) y necesitan su entorno para leer su sesión. Pero, con WP-0, se resuelven con
  el localizador ya corregido y arrancan en la carpeta neutra. Sin eso, un `claude.cmd` plantado en una carpeta del proyecto
  que esté en tu PATH se ejecutaría al abrir el panel.
- **Un cambio que se nota** (decisión 14): un CLI de agente, o cualquier herramienta, instalado **dentro** de la carpeta de
  un proyecto abierto deja de encontrarse, para Auto Doctor, para las sondas, para la verificación y para git. Auto Doctor lo
  dice con la comprobación `doctor.project.path-inside` (aviso): «tu PATH tiene una carpeta de este proyecto; lo que hay en
  ella no se usa».
- **La excepción de git, sin rodeos** (decisión 13). Las comprobaciones de la sección «Proyecto» que preguntan a git
  (`InspectAsync`, `FindHookPathsAsync`, los remotos) van por la capa git endurecida, y esa capa lanza `git` **con la carpeta
  de trabajo en el repositorio**: no se puede preguntar a git por un repositorio de otra forma. El ejecutable es el del
  sistema (WP-0 lo garantiza), sin hooks, sin red y con la configuración inspeccionada (ADR-0012). Pero el literal de F6-7
  dice «ningún proceso arranca dentro del repositorio», y esto es un proceso que arranca dentro. F6-7 habla de **leer el
  entorno declarado**, que no usa git; aun así, es una lectura del criterio que te toca aceptar a ti. Si no la aceptas, la
  sección «Proyecto» no usa git: solo dice si existe `.git`, y los hooks y los remotos salen «sin comprobar».
- **Descartado:** lanzar las sondas a integridad baja. Son herramientas del sistema instaladas por ti, fuera del proyecto;
  la integridad baja rompería algunas (`docker` habla con su servicio por una tubería con nombre) sin quitar ningún riesgo
  nuevo.
- **Descartado:** un localizador propio solo para Auto Doctor, dejando el general como está (lo que decía la primera versión
  de este documento). Cerraba el panel y dejaba abiertas la verificación y las sondas.

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
| `compose.yaml`, `compose.yml`, `docker-compose.yaml`, `docker-compose.yml` | Solo que existe, y su tamaño. **No se interpreta** | Ninguno: YAML lo lee la Fase 16 |
| `Dockerfile` | Las líneas `FROM` | Lector de líneas propio |

- **Solo esas rutas, relativas a la raíz del proyecto.** No se buscan en subcarpetas: un monorepo saldría con ruido y la
  búsqueda sería una superficie más.
- **De la carpeta de trabajo, no de HEAD:** lo que importa a quien va a trabajar es lo que tiene delante. Es la diferencia
  con Auto Graph (D-19), que describe un commit.
- **YAML no se interpreta en esta fase** (decisión 10). De compose, a Auto Doctor le basta saber que existe para pedir
  Docker. Sus servicios y sus imágenes los leerá la Fase 16 con su librería de YAML (docs/19, decisión 9): así hay **un
  solo lector de compose** en la app, y no un lector de líneas aquí y una librería allí.
- **TOML y `Dockerfile` sí, con lectores de líneas propios:** un campo (`requires-python`) y una instrucción (`FROM`). Lo
  que no se entiende sale «no interpretado», con su fichero y su línea.

**D-06 · Las reglas de lectura, iguales para todos.** Viven en un único lector (`IDeclaredEnvironmentReader`) y se prueban una
vez.

- **Tamaño:** 256 KB por fichero (el tope de `verification.json`). Si pasa, no se lee y sale «demasiado grande».
- **Sin enlaces:** si el fichero o cualquier carpeta entre la raíz y él es un enlace simbólico o una unión (junction), no
  se lee y sale «es un enlace». Solo esos dos, no cualquier punto de reanálisis: los marcadores de OneDrive y de otros
  servicios de sincronización también lo son, y son ficheros normales. Se abre el fichero y se comprueba sobre lo abierto,
  no por su nombre otra vez.
- **Sin seguir referencias:** `dockerComposeFile` y `build.dockerfile` de un Dev Container,
  `extends` de cualquier JSON y las imágenes `FROM` se **nombran** como hechos; nunca se abren ni se descargan.
- **JSON:** profundidad máxima 32, comentarios y coma final admitidos, duplicados rechazados.
- **Texto:** UTF-8; una línea de más de 4 096 caracteres corta la lectura.
- **Nada se ejecuta:** ni `npm`, ni `dotnet tool`, ni el `python.exe` de `.venv`, ni `docker compose config`.
- **Los valores leídos son datos:** se muestran escapados, con un tope de 200 caracteres, y nunca pasan a una línea de
  órdenes.
- **Secretos:** no se lee ningún `.env`, y de compose no se lee nada.

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
  Las imágenes de `Dockerfile` y del Dev Container se listan; no se comprueba que existan (sería red).
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
  distribución.
- **La pregunta va siempre al motor local.** No basta con quitar `DOCKER_HOST` y `DOCKER_CONTEXT`: el contexto activo
  está en `~/.docker/config.json` (`currentContext`) y puede apuntar a un motor remoto. La puerta lanza `docker` con
  `DOCKER_CONFIG` en una carpeta vacía propia (`%LOCALAPPDATA%\AgentStudio\doctor\docker-config`): sin configuración no
  hay contexto elegido ni complementos, y se usa el motor local por defecto. La orden sigue siendo la del criterio,
  `docker version --format json`.
- **`wsl --status` se lanza con `WSL_UTF8=1`,** para que su salida llegue en UTF-8 y no en UTF-16.
- **Sin verificar** (§12): que `WSL_UTF8=1` hace efecto en la versión de WSL de este equipo; que el texto de
  `wsl --status` cambia con el idioma de Windows; y que, con un `DOCKER_CONFIG` vacío, Docker Desktop responde por su
  tubería por defecto. Por eso WP-A graba las salidas reales de este equipo en castellano y en inglés antes de escribir
  el lector, y el lector se apoya en la estructura.

### 3.4 Arreglos (F6-2)

**D-10 · Una lista cerrada de arreglos, de tres clases.**

| Clase | Qué hace | Ejemplos de la Fase 6 |
|---|---|---|
| Ir | Abre una pantalla de la app | «Abrir el configurador», «Abrir Ajustes», «Ver la skill» |
| Guía | Enseña qué hacer, con una orden para copiar o la página oficial. **La app no ejecuta la orden** | «Instala el SDK 10.0.303», «Abre Docker Desktop» |
| Aplicar | Agent Studio cambia **su propio estado**, y se puede deshacer | «Regenerar los diagramas desactualizados», «Desactivar la skill archivada» |

- **La regla la da el tipo, no una convención.** `DoctorFix` es una jerarquía cerrada (constructor privado) con cuatro
  formas: ir, guía, reversible (lleva su operación de deshacer) y con aprobación (lleva su petición, que `IApprovalGate`
  resuelve antes de aplicar). Un «aplicar» sin una de las dos cosas no se puede escribir (§4.2). Además, una prueba
  recorre los arreglos registrados y aplica y deshace cada reversible (F6-2).
- **En la Fase 6 todos los arreglos «aplicar» son reversibles** y actúan sobre datos de Agent Studio. Ninguno toca el sistema, tu repositorio ni la configuración de un CLI.
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
- **Solo `https://api.github.com`.** El cliente rechaza cualquier otro host; las redirecciones se siguen a mano y solo al
  mismo host (D-14). 10
  segundos y 1 MB por respuesta.
- **Se dice qué sale:** el propietario, el repositorio y el commit de cada skill fijada. Nada del proyecto, ni rutas, ni tu
  nombre.

**D-14 · Qué se pregunta, cómo se construye cada dirección y cómo se interpreta.**

| Indicador | Petición | Resultado |
|---|---|---|
| `archived` | `GET /repos/{owner}/{repo}` | `archived: true` → crítico |
| `upstream` | Dos peticiones pequeñas: `GET /repos/{owner}/{repo}/commits?sha={commit fijado}&path={carpeta}&per_page=1` y la misma con `sha={rama}` | Cada una da el último commit que tocó la carpeta de la skill. Si no son el mismo, «actualización por revisar» |
| `scripts` | Ninguna: es local | Tiene `scripts/` y no hay revisión tuya de esa huella → aviso |
| `size` | Ninguna: es local | Lo de hoy, más el coste de contexto del cuerpo (`SkillContextCost`) |

- **Por qué no `compare`:** su respuesta trae el parche de cada fichero (hasta 300) y puede pasar de varios megas. Las dos
  peticiones de `commits` devuelven un commit cada una, sin ficheros, y la del commit fijado no cambia nunca: se guarda para
  siempre. El tope de 1 MB por respuesta sobra.
- **De dónde salen `owner`, `repo`, la rama, el commit y la carpeta:** de la fijación de la skill en el catálogo (lo que
  guardó `SkillsIndexCatalogImporter` al importarla). Son datos que vinieron de un índice o de una URL: **no fiables**.
- **Se validan antes de construir nada,** y lo que no pasa no se pregunta (sale «origen no válido»):
  - `owner`: `^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$`;
  - `repo`: `^[A-Za-z0-9._-]{1,100}$`, y nunca `.` ni `..`;
  - commit: `^[0-9a-f]{40}$`;
  - rama: `^[A-Za-z0-9._/-]{1,200}$`, sin `..`, sin empezar ni acabar en `/`;
  - carpeta: segmentos relativos con la misma regla que `repo`, sin `..`.
- **Un solo constructor de direcciones.** Cada segmento va escapado (`Uri.EscapeDataString`), la rama y la carpeta van como
  valores de consulta, y la dirección final se comprueba: `https`, host `api.github.com` y ruta que empieza por
  `/repos/{owner}/{repo}`. Así un `..`, un `?` o un `#` en un dato no llevan a otro punto de la API con tu token.
- **Redirecciones a mano:** `AllowAutoRedirect = false`. Una redirección solo se sigue si es `https` y al mismo host, como
  mucho tres veces (GitHub redirige los repositorios renombrados). Un `DelegatingHandler` comprueba el host de **cada**
  petición que sale, también de las seguidas a mano. **Corregido por WP-S:** la cabecera del token (y el `ETag`) van solo en la primera petición, nunca en una redirección seguida, aunque sea al mismo host; una redirección solo se sigue bajo `/repos/` o `/repositories/`.
- **`scripts`:** «Marcar los scripts como revisados» guarda la huella (SHA-256) del contenido de `scripts/`. Si un script
  cambia, la skill vuelve a «sin revisar». Nunca se ejecuta ningún script.
- **En serie, con una cola y un solo consumidor,** como pide GitHub y como prefieres para las carreras.
- **`ETag`:** cada respuesta se guarda con su `ETag` en `%LOCALAPPDATA%\AgentStudio\skills-doctor\`; la siguiente va con
  `If-None-Match`, y un 304 no cuenta contra el límite.
- **Límites:** se respetan `x-ratelimit-remaining` y `retry-after`. Al llegar al límite, las skills que faltan salen «sin
  comprobar: límite de GitHub, se reinicia a las HH:MM». No se insiste.
- **Los fallos tienen código,** y su mensaje nunca lleva la dirección completa, las cabeceras ni el cuerpo: sin red, 403,
  404 (repositorio borrado o privado → aviso) y respuesta que no se entiende.
- **Las respuestas son datos no fiables:** solo se leen los campos de la tabla, con sus tipos y topes.

**D-15 · El token es opcional, nunca sale de los CLIs, y se dice lo que protege y lo que no.**

- **Sin token:** 60 peticiones por hora. Con las cachés basta para catálogos pequeños. Es lo que hay por defecto.
- **Con token:** 5 000 por hora. Un token personal de acceso fino, solo de lectura de repositorios públicos: lo menos
  valioso que GitHub deja crear.
- **Dónde se guarda:** en la bóveda de `ICredentialVault`, cifrado con DPAPI para tu usuario de Windows. Hoy ese contrato no
  tiene implementación: esta fase escribe la mínima, y la Fase 7 (ADR-0043) la amplía por proyecto. Es la decisión 6, y cómo
  se llama a DPAPI, la 15.
- **Lo que DPAPI protege, dicho con honestidad:** el fichero no sirve en otro equipo ni a otro usuario. **No** protege de un
  proceso que corra con tu usuario: cualquiera de ellos puede pedir a Windows que lo descifre. Y Hello protege la interfaz de
  Agent Studio, no el fichero. Frente a eso, el token es de solo lectura de datos públicos, y es opcional.
- **Frente a una variable de entorno:** la variable está en claro en tu configuración y la hereda **cada** proceso que
  arranques, incluidos los CLIs de los agentes, sin que nadie tenga que ir a buscarla. La bóveda exige ir a por un fichero
  concreto y llamar a DPAPI adrede. Las dos caen ante un proceso malicioso de tu usuario; la bóveda no lo regala.
- **Hello, según la lista fija de ADR-0053.** `RemoteOrSecretOperation` cubre «usar o revelar un secreto» y está en
  «siempre». Por tanto: guardar, sustituir o borrar el token pide Hello, y **cada tanda de «Comprobar en GitHub» que use el
  token pide Hello una vez** (una confirmación para toda la tanda, no una por skill). Sin token no hay secreto ni operación
  que cambie nada en el remoto (son lecturas de datos públicos): no pide Hello, solo el permiso de Ajustes y tu clic.
- **Dónde se comprueba ese Hello: en el comprobador, no en la pantalla.** `GitHubSkillHealthChecker` solo recibe el token de
  un puerto (`ISkillsDoctorSecrets.UseTokenAsync`) que primero pide la presencia y, sin ella, no lee la bóveda: la tanda sigue
  sin token y lo dice. Una prueba lo fija: con la aprobación denegada, la bóveda no recibe ninguna lectura.
- **Nunca** se lee `gh auth token`, el gestor de credenciales de git ni ninguna carpeta de un CLI.
- **El token no aparece en ningún texto:** ni en el registro, ni en la bitácora, ni en un log, ni en el mensaje de un error
  (las excepciones de HTTP se envuelven sin sus cabeceras). `SecretRedactor` ya reconoce su forma, y una prueba busca un token
  centinela en todo lo que se escribió tras una tanda con fallos.

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
- **Ninguna ruta sale de la copia.** Las rutas de proyecto de un `.sln` o `.slnx` y los `ProjectReference` de un `.csproj`
  son datos del repositorio: pueden traer `..\..\` o una ruta absoluta, y un proceso a integridad baja puede **leer** casi
  todo lo tuyo. El proceso de análisis normaliza cada ruta contra la raíz de la copia y exige que la ruta final quede
  dentro; si no, no la abre y anota `graph.outside-root`. Tampoco entra en enlaces simbólicos ni en uniones, ni al
  recorrer carpetas ni al abrir un fichero. Es la prueba adversaria de F6-12.
- **Descartado:** `MSBuildWorkspace` dentro de un proceso a integridad baja. Baja el daño, pero sigue ejecutando código del
  repositorio con tus lecturas y tu red (los residuales de ADR-0017), y F6-12 pide que no se ejecute nada.

**D-17 · Roslyn, solo el analizador sintáctico.** Dependencia nueva: es la decisión 3.

- **El paquete:** `Microsoft.CodeAnalysis.CSharp` **5.6.0** (fijada por el P0, §12): la versión de Roslyn que trae el SDK de
  .NET 10 de `global.json` (10.0.303 trae `Microsoft.CodeAnalysis.CSharp.dll` 5.6.0-2.26377). Licencia **MIT**, autor Microsoft,
  repositorio `dotnet/roslyn`, rama `release/stable`. Para `net10.0` solo trae dos dependencias: `Microsoft.CodeAnalysis.Common` 5.6.0 y
  `Microsoft.CodeAnalysis.Analyzers` 5.3.0 (reglas para quien escribe analizadores; solo actúan al compilar), también MIT. La 5.9.0, que
  la primera versión de este documento citaba como la última del índice, apunta a la rama `release/insiders`: no es la del SDK.
- **Lo que no entra:** `Microsoft.CodeAnalysis.Workspaces.*`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`,
  `Microsoft.Build.*` ni `Microsoft.Build.Locator`. Una prueba de arquitectura lo fija (F6-12).
- **Cómo se usa:** `CSharpSyntaxTree.ParseText` con la versión más reciente del lenguaje. No se crea ninguna
  `Compilation`: sin analizadores, sin generadores de código, sin resolver referencias.
- **No se recorre el árbol entero.** Solo se miran los `Usings` de la unidad y de cada espacio de nombres, y las
  declaraciones de espacios de nombres y de tipos (sus nombres), bajando por `Members` sin entrar en los cuerpos de los
  tipos ni en ninguna expresión. Un recorrido completo de un fichero con anidamiento extremo puede agotar la pila; este no
  baja más que el anidamiento de espacios de nombres y tipos, y se corta a 64 niveles.
- **Un fichero que falla no tumba el grafo.** Cada fichero tiene tamaño máximo (1 MB) y tiempo máximo (5 segundos, con
  cancelación). Si se excede o el analizador lanza una excepción, ese fichero sale `graph.file-skipped` y su proyecto,
  «parcial».
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
- **Si aun así el proceso cae** (una pila agotada no se puede capturar): antes de cada fichero escribe su ruta en su
  salida de progreso. El cliente lo relanza saltando ese fichero, que sale `graph.file-skipped`; como mucho tres veces, y
  después el grafo sale «parcial» con lo que haya.
- **La salida del proceso es un dato no fiable para la app:** se lee con un esquema estricto y los mismos topes. **El anfitrión (WP-G) aplica el
  tope de bytes mientras lee** (deja de leer, mata el proceso y falla el análisis al pasarlo; no lee todo para comprobarlo después), acota también las
  líneas del error estándar, recorta el `\r` final de cada línea `progress:` antes de compararla (`TryParseProgress` ya lo hace) y solo cree una ruta segura. Ningún
  mensaje de excepción cita lo que escribió el proceso (`CodeGraphJson.Parse` no pasa la excepción interna).

**D-19 · El grafo describe un commit, no tu carpeta.**

- **Cómo se lee:** `IRepositorySnapshots` (nuevo) pide a la capa git una copia del commit (`ArchiveCommitAsync`) en un área
  aislada, igual que la verificación. El proceso de D-18 solo ve esa copia.
- **Una sola copia para todo.** El grafo y las discrepancias (D-27) se calculan en el mismo paso y sobre la misma copia:
  `ICommitAnalysisService` la abre una vez, lanza el análisis, la pasa a las comprobaciones y la cierra. Los dos
  resultados se guardan por commit.
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

- **Nodos:** solución, carpeta de la solución (`SolutionFolder`, añadida por el P0 al final del enum: virtual, sin ruta), proyecto, espacio de nombres y
  paquete. Cada uno con su identificador estable (la ruta relativa del proyecto, que **es** su ruta; o proyecto más nombre), su ruta si la tiene y si es «parcial».
  La estructura es un árbol: la solución contiene carpetas y proyectos, una carpeta contiene carpetas y proyectos, un proyecto contiene espacios de nombres, y
  cada proyecto, carpeta y espacio de nombres tiene un solo padre (las carpetas no se contienen a sí mismas).
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

**D-23 · Tres generadores, de modelo a texto, puros.** Viven en `src/AgentStudio.Memory/Diagrams/**` (nuevo). Son los tres
diagramas de F6-10; el de la solución escribe dos clases de fichero (la solución, y los espacios de nombres de cada
proyecto), y por eso `DiagramKind` tiene cuatro valores.

| Diagrama | Sintaxis Mermaid | Modelo de origen |
|---|---|---|
| La solución | `flowchart LR`, con un `subgraph` por carpeta de solución; y uno por proyecto con sus espacios de nombres | `CodeGraph` |
| El plan de una ejecución | `flowchart TD` | `PlanDefinition` de esa ejecución, por `IPlanStore` (`src/AgentStudio.Application/Persistence/StoreContracts.cs`) |
| La secuencia de una ejecución | `sequenceDiagram` | Las entradas del registro de esa ejecución: lanzamientos, informes, verificaciones, integraciones, veredictos y aprobaciones |

- **Participantes de la secuencia:** tú, el orquestador, cada agente por su nombre de la plantilla, el sistema (verificación
  e integración) y el juez.
- **«Con Roslyn o MSBuild»,** dice F6-10: aquí es con Roslyn y la lectura de D-16. MSBuild queda descartado.
- **Solo lo estático.** Las relaciones inferidas no salen en un diagrama generado.

**D-24 · Una gramática acotada, para que compile siempre y nada del repositorio se cuele como sintaxis.**

Los nombres de proyecto, de espacio de nombres, de agente y los títulos de las tareas son **datos no fiables**. Las reglas:

- **Identificadores propios** (`n1`, `n2`…, `p1`, `p2`… para los participantes), asignados por orden ordinal del modelo.
  Ningún nombre del repositorio ni del registro es un identificador de Mermaid.
- **Un solo escapador para todo texto que venga de datos,** antes de escribirlo en cualquier sitio:
  - se sustituyen por su entidad de Mermaid (`#NN;`) las comillas, `#`, `<`, `>`, `&`, **`;`** y **`%`**;
  - los saltos de línea, las tabulaciones y los caracteres de control se cambian por un espacio;
  - `:` se sustituye también en los nombres de participante, donde separa el nombre del mensaje;
  - tope de 80 caracteres, con «…».

  El orden importa: primero `#` y `&`, luego el resto, para no escapar dos veces.
- **En los diagramas de flujo,** las etiquetas van siempre entre comillas, ya escapadas.
- **En el de secuencia, donde el texto de un mensaje no lleva comillas y `;` separa sentencias,** el mensaje **no es texto
  libre:** sale de un vocabulario cerrado, uno por clase de entrada del registro («lanza la tarea», «entrega el informe»,
  «verifica», «integra», «pide el veredicto», «veredicto: aprobado», «aprueba»…), en castellano fijo y sin parámetros de
  texto. El título de la tarea va en una nota aparte (`Note over`), escapado, y el nombre del agente solo en la declaración
  del participante (`participant p3 as "…"`), escapado.
- **Sin directivas** (`%%{init}%%`), sin comentarios `%%` que vengan de datos, sin `click`, sin enlaces, sin estilos por
  clase que vengan de datos.
- **Topes:** 200 nodos y 400 relaciones por diagrama, y 300 mensajes en una secuencia. Si se pasa, se agrupa o se corta, y
  una nota dice cuánto se omitió.
- **Fin de línea `\n` y UTF-8 sin BOM,** para que los dorados sean iguales byte a byte en cualquier equipo.
- **Por qué basta sin motor:** quien escribe el texto es código nuestro con una gramática pequeña, y los ficheros dorados
  fijan cada forma. «Que compile» con `mermaid.parse` hace falta para los diagramas de agentes, que son de la Fase 16.
- **La prueba adversaria** (F6-10): nombres de agente, títulos de tarea y nombres de proyecto con `;`, `%%`, `%%{init}%%`,
  `-->>`, `click n1 "https://…"`, `end`, `Note over`, comillas y saltos de línea. Se comprueba que el texto generado tiene
  exactamente las mismas sentencias que con nombres inocuos: mismo número de líneas, mismas palabras clave al principio de
  cada una, y ningún carácter de la lista sin escapar fuera de las posiciones que escribe el generador.

**D-25 · Cabecera, dónde viven y «desactualizado».**

- **Dónde viven: en una caché de tu equipo, y en el repositorio solo si lo pides.** Es la misma regla que docs/19 D-26, para
  que los dos documentos no digan cosas distintas, y es la decisión 9.
  - **Por defecto:** `%LOCALAPPDATA%\AgentStudio\diagrams\<proyecto>\`, con el texto de cada diagrama, la versión anterior
    de cada uno y un índice (`index.json`: diagrama, huella del modelo y SHA-256 del texto). No ensucia tu repositorio y se
    reconstruye.
  - **«Exportar al repositorio»,** cuando lo pides: escribe los diagramas elegidos como Markdown con un bloque `mermaid`
    (GitHub lo pinta) en la carpeta que elijas, por defecto `docs/generado/diagramas/`. Nombres de una lista cerrada:
    `solucion.md`, `proyecto-<nombre>.md`, `plan-<ejecución>.md` y `secuencia-<ejecución>.md`, con el nombre saneado.
  - **Un solo dueño de esa carpeta y de esa regla:** este paquete (ADR-0045). La Fase 16 exporta sus documentos al lado
    (`docs/generado/`) con el mismo exportador y la misma cabecera.
  - Esto corrige docs/16 D-21, que los ponía en `.agentstudio/diagrams/`: queda como la opción (b) de la decisión 9.
- **La cabecera,** un comentario al principio de cada diagrama, en la caché y en lo exportado:

  ```text
  <!-- Generado por Agent Studio. Editarlo no cambia nada: se regenera.
       diagrama: solucion · generador: 1
       commit: 45f243d3… · modelo: sha256:… · fecha: 2026-11-12T10:00:00Z -->
  ```

  Para el plan y la secuencia, en vez del commit va la ejecución y la cabeza del registro que describe.
- **La fecha no rompe el determinismo:** sale de un reloj inyectado (`TimeProvider`), que las pruebas fijan. Y un diagrama
  «al día» **no se reescribe**: «Regenerar» sobre uno al día no toca nada, así que la fecha solo cambia cuando cambia el
  modelo. Lo que se compara byte a byte en los dorados es el texto entero con el reloj fijo.
- **Lo exportado nunca se lee de vuelta.** Agent Studio no interpreta un fichero que escribió en tu repositorio: ni su
  cabecera ni su contenido. Para saber qué exportó guarda en el índice la ruta y el SHA-256 de lo que escribió. Y las
  discrepancias (D-27) saltan la carpeta de exportación, para no tratar lo generado como escrito por ti.
- **Estados de un diagrama** (en la caché):
  - **sin generar;**
  - **al día:** la huella del modelo de ahora es la del índice;
  - **desactualizado:** no lo es. Se dice qué commit describe.
- **Estados de una copia exportada,** además:
  - **igual que la caché;**
  - **anterior:** exportaste una versión y la caché ya tiene otra;
  - **cambiada fuera:** el SHA-256 del fichero no es el que se escribió (lo editaste o lo borraste). Solo se calcula su
    huella; no se interpreta;
  - **sin datos:** el fichero existe pero este equipo no lo exportó (un clon).
- **Regenerar actúa solo sobre la caché, y se puede deshacer de verdad:** antes de sustituir un diagrama, su texto anterior
  se guarda al lado (`<diagrama>.prev`, uno por diagrama). «Deshacer» lo restaura y restaura su línea del índice. Por eso
  «Regenerar los diagramas desactualizados» puede ser un arreglo de clase «aplicar» (D-10).
- **Exportar no es un arreglo: es una orden tuya, y no pisa nada sin decirlo.** Si el destino existe y está «cambiado fuera»
  o «sin datos», se para y pregunta: sobrescribir, guardar con otro nombre o dejarlo. Nunca se exporta con una ejecución en
  marcha.
- **Editar:** lo generado no se edita. Para un diagrama tuyo, copia el fichero a otro sitio; ahí el sistema no lo toca.

**D-26 · En la Fase 6 el diagrama se ve como texto; el dibujo ya existe en otra pantalla.** Es la decisión 2.

- **La sección «Diagramas»** lista cada diagrama con su estado y enseña su fuente en el editor en solo lectura, con
  «Generar», «Regenerar», «Deshacer», «Copiar» y «Exportar al repositorio».
- **El dibujo:** la solución se ve dibujada en Auto Graph (D-22) y el plan, en el lienzo. La secuencia no tiene dibujo en la
  app hasta la Fase 16; exportada, GitHub y cualquier visor de Markdown la pintan.
- **Por qué no el motor ahora:** docs/19 D-18 ya lo diseña entero (mermaid.js fijado por huella, filtro de peticiones, cola,
  saneado del SVG y un spike previo). Traerlo aquí duplicaría ese paquete o lo partiría en dos fases.

### 3.9 Discrepancias (F6-11)

**D-27 · Cuatro comprobaciones con código, sin IA, sobre el commit HEAD.** Viven en `src/AgentStudio.Memory/Consistency/**`
(nuevo) y leen la misma copia del commit que el grafo (D-19).

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
- **Lo generado no cuenta:** la carpeta de exportación de D-25 se salta.
- **Topes:** 5 000 ficheros `.md`, 1 MB por fichero y 2 000 hallazgos.

**D-29 · Los requisitos sin cubrir salen del tablero.** Para cada hito abierto, `IBoardProjector.ProjectAsync`; cada tarjeta
de requisito en la columna `Backlog` es un hallazgo. Memory solo depende de Application, así que usa ese puerto y no los
lectores internos de Orchestration. Sin hitos, la comprobación sale «no aplica».

**D-30 · Dónde se ven.** En la sección «Discrepancias» del documento Auto Graph, agrupadas por código, y en una línea de la
sección «Proyecto» de Auto Doctor («3 discrepancias»). **Esa línea lee el resultado guardado del commit HEAD: abrir el panel
nunca lanza la copia ni el análisis.** Si no hay resultado para HEAD, dice «sin calcular» y ofrece ir a Auto Graph. No se
guardan como evidencia: eso es de la Fase 16.

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
public enum GraphNodeKind { Solution, Project, Namespace, Package, SolutionFolder }   // SolutionFolder: D-23, a subgraph per folder
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
public abstract class DoctorFix   // closed hierarchy: the constructor is private, the four forms are nested
{
    private DoctorFix(string code) => Code = code;
    public string Code { get; }
    public sealed class Navigate(string code, string target) : DoctorFix(code);
    public sealed class Guide(string code, string? command, Uri? page) : DoctorFix(code);   // the app never runs the command
    public sealed class Reversible(string code, DoctorAction apply, DoctorAction undo) : DoctorFix(code);
    public sealed class Approved(string code, ApprovalRequest approval, DoctorAction apply) : DoctorFix(code);
}
public delegate Task DoctorAction(ProjectContext? project, CancellationToken ct);
public interface IDoctorFixRunner   // asks IApprovalGate for Approved fixes; records DoctorFixApplied
{
    Task<DoctorFixResult> ApplyAsync(DoctorFix fix, string checkCode, ProjectContext? project, CancellationToken ct);   // WP-A: checkCode, for the entry of the log
    Task UndoAsync(long appliedSequence, ProjectContext? project, CancellationToken ct);
}
```

- `IDoctorToolRunner` no tiene ninguna sobrecarga con un ejecutable o argumentos libres: es la garantía de D-02.
- `DoctorFix` no deja construir un arreglo que se aplique sin deshacer ni aprobación: es la garantía de D-10.
- `IUntrustedRoots` (las raíces de los proyectos abiertos) no es de este fichero: lo trae WP-0 en
  `src/AgentStudio.Application/Execution/ProcessContracts.cs` (§9.0).
- `DoctorFactNames` fija los nombres de los hechos que comparten paquetes: `declared`, `source`, `installed` y `resolved`
  para la tabla del entorno (la pinta WP-A, la rellena WP-E), y `version` y `path` para las herramientas.

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
public interface ICodeGraphBuilder   // runs the worker over an open snapshot
{
    Task<CodeGraph> BuildAsync(IRepositorySnapshot snapshot, CancellationToken ct);
}
public sealed record CommitAnalysis(CodeGraph Graph, ConsistencyReport Consistency);
public interface ICommitAnalysisService   // one snapshot per commit for the graph and the checks; cached by commit
{
    Task<CommitAnalysis?> FindAsync(ProjectContext project, string commit, CancellationToken ct);   // cache only
    Task<CommitAnalysis> AnalyzeAsync(ProjectContext project, string commit, CancellationToken ct);
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
public enum DiagramState { NotGenerated, Current, Stale }
public enum DiagramExportState { NotExported, SameAsCache, Older, ChangedOutside, Unknown }
public sealed record DiagramSource(DiagramKind Kind, string Subject, string ModelHash, string Mermaid);
public interface IDiagramGenerator   // one per kind; pure text from a model
{
    DiagramKind Kind { get; }
    Task<DiagramSource> GenerateAsync(ProjectContext project, string subject, CancellationToken ct);
}
public interface IDiagramCatalog
{
    Task<IReadOnlyList<DiagramStatus>> ListAsync(ProjectContext project, CancellationToken ct);
        Task<DiagramStatus> GenerateAsync(ProjectContext project, DiagramKind kind, string subject, CancellationToken ct);   // cache; keeps the previous text
    Task<DiagramStatus> RestorePreviousAsync(ProjectContext project, DiagramKind kind, string subject, CancellationToken ct);
    Task<DiagramExportResult> ExportAsync(ProjectContext project, DiagramExportRequest request, CancellationToken ct);   // refuses to overwrite a changed file unless asked
}
```

- La Fase 16 añade valores a `DiagramKind` y más `IDiagramGenerator`. Su `IDiagramRenderer` (docs/19 D-18) recibe un
  `DiagramSource`.

`src/AgentStudio.Application/Consistency/ConsistencyContracts.cs`:

```csharp
public sealed record ConsistencyFinding(string Code, string? Path, int? Line, IReadOnlyList<DoctorFact> Facts);
public interface IConsistencyCheck { string Code { get; } Task<IReadOnlyList<ConsistencyFinding>> RunAsync(ConsistencyScope scope, CancellationToken ct); }
public interface IConsistencyService { Task<ConsistencyReport> RunAsync(ProjectContext project, IRepositorySnapshot snapshot, CancellationToken ct); }
```

- La Fase 16 añade sus `doc.*` como más `IConsistencyCheck`.

`src/AgentStudio.Application/Skills/SkillPorts.cs` (existe): `ISkillHealthChecker` no cambia. Se añaden
`ISkillScriptReviews` (guardar y leer la huella revisada), el resultado de una tanda con su motivo cuando no se pudo
comprobar, e `ISkillsDoctorPolicy { bool NetworkAllowed { get; } }`: el permiso de Ajustes visto desde abajo. `AppSettings`
vive en Desktop, así que el cliente de GitHub pregunta a este puerto antes de cada tanda, y la prueba de F6-13 lo apaga con
un doble. E `ISkillsDoctorSecrets.UseTokenAsync(...)`: pide la presencia (Hello) y solo entonces lee la bóveda. Es el único
camino por el que el comprobador recibe el token (D-15).

`src/AgentStudio.Application/Security/SecurityContracts.cs` (existe): `ICredentialVault` no cambia; se implementa (D-15).

### 4.3 Payloads

`src/AgentStudio.Application/Decisions/DoctorPayloads.cs`:

```csharp
public sealed record DoctorFixAppliedPayload(string FixCode, string CheckCode, long? Undoes) : DecisionPayload;
public sealed record GraphRelationsProposedPayload(string Commit, string ModelHash, EvidenceId Proposal,
    IReadOnlyList<ProposedRelation> Accepted, int Discarded, string Agent, string Model) : DecisionPayload;
```

- Las dos con `RunId = NoRun`. Sin texto libre, sin rutas absolutas y sin nada del token.
- `ProposedRelation` lleva los identificadores de los dos nodos y la clase de la relación. **Corregido por el P0:** el diseño le daba además un motivo de
  200 caracteres, pero es texto de un agente y las entradas del registro no llevan texto de un agente (como `QuestionAsked`): el motivo vive en la
  evidencia de la propuesta y en `GraphInference.Reason` del almacén de relaciones inferidas.
- El segundo solo existe con la decisión 7 (a).

### 4.4 Lo que el P0 ajustó respecto a esta sección

Los contratos de arriba son los del diseño; al escribirlos el P0 cambió esto, y la referencia es ahora [04 §25](04-contratos.md#25-fase-6-auto-doctor-auto-graph-entorno-declarado-y-diagramas):

- **Los tipos de dominio validan.** `DoctorFact`, `DoctorCheck`, `GraphNode`, `GraphEdge`, `GraphGap` y `CodeGraph` son tipos con constructor que rechaza lo que no es válido (en vez de records
  posicionales a secas): ids únicos, relaciones entre nodos que existen y de la clase que cada tipo de relación une, un nodo parcial con su hueco, rutas relativas
  que no salen de la copia, topes. `CodeGraph` se ordena al construirse y se compara por lo que tiene. Las listas se copian.
- **Carpetas de la solución** (D-23): `GraphNodeKind.SolutionFolder` al final del enum, con su regla de `Contains` (solución o carpeta → carpeta o proyecto), su unicidad de padre y la
  ausencia de ciclos, ya en el contrato, en `CodeGraphJson` y en `ModelHash`; así no hay que subir la versión del generador después. Un nodo `Project` tiene por id su ruta.
- **Textos no fiables:** ningún texto del modelo, de un hecho ni de lo declarado admite caracteres de control, separadores de línea o párrafo (U+2028 y U+2029) ni
  caracteres de formato (marcas de dirección, ancho cero, BOM); una ruta tampoco admite los nombres de dispositivo de Windows (`CON`, `NUL`, `COM1`…, con o sin
  extensión). Una rama sigue las reglas de `git check-ref-format` que afectan a la dirección.
- **`ToolVersion`:** la igualdad es por valor, la misma que la comparación (`10.0` es `10.0.0`); el texto original se conserva para mostrarlo.
- **`CodeGraph.ModelHash`** cubre nodos, relaciones estáticas, huecos y la versión del generador; **no** el commit ni lo inferido (el §3.7 decía «solo con lo estático»; el commit
  no es parte del modelo). `StaticOnly()` y `WithInferred(…)` dejan poner encima las relaciones de una IA sin mover la huella.
- **`GitHubSkillOrigin`** (la validación de D-14) vive en **Domain**, no en `Infrastructure/Skills/GitHub`: es una regla pura y la prueba el P0. WP-S construye la dirección a partir de él.
- **El protocolo del proceso de análisis** (`GraphWorkerProtocol`, `GraphWorkerRequest`, `GraphWorkerExitCodes` y `CodeGraphJson`, en `Application/Graph`) lo fija el P0: el §3.7 decía
  que el proceso «devuelve el modelo en JSON por su salida» sin más detalle. El esqueleto termina con el código 4 y no devuelve nada.
- **`IConsistencyCheck.RunAsync`** devuelve `null` cuando la comprobación no aplica, y `ConsistencyReport` lleva `NotApplicable` y `Truncated`. **`IDiagramCatalog`** usa `DiagramRef`,
  `DiagramStatus` (con la huella, el commit y la fecha), `DiagramExportRequest` (con la carpeta y qué hacer ante un conflicto) y `DiagramExportResult`.
- **`ISkillHealthBatchChecker`** es la tanda contra GitHub (una entrada por skill, con su código de fallo); `ISkillHealthChecker` no cambia. `GitHubToken` y `SkillsDoctorTokenUse` modelan el
  token y el estado de la presencia para que el token no pueda salir en un texto.
- **`DoctorFix.Guide`** solo admite una página `https` sin credenciales, y **`DoctorToolResult`** lleva el resultado de una herramienta que no corrió (`NotInstalled`, `InsideProject`, `TimedOut`, `FailedToStart`).
- **No están en el P0:** `IUntrustedRoots` (WP-0), `EvidenceToolVersions` (WP-E, que decide cómo embebe el bloque `tools:` en la cabecera de la evidencia, hoy líneas `# …`) y el escapador de
  Mermaid (WP-D, en Memory). `DoctorFactNames` se reduce a lo que el §4.2 listaba.

---

## 5. Modelo de datos

**Ninguna migración.** El esquema sigue en la versión 7.

| Dato | Dónde | Por qué ahí |
|---|---|---|
| Arreglos aplicados y deshechos | Registro: `DoctorFixApplied` | Es una decisión tuya sobre el proyecto |
| Relaciones propuestas por una IA | Registro: `GraphRelationsProposed`, y el texto del agente como evidencia | Texto de agente, anclado y con su autor |
| Versiones de las herramientas | Dentro del contenido de cada evidencia de verificación | Ya anclado por su huella (D-12) |
| El modelo de Auto Graph y las discrepancias de un commit | `%LOCALAPPDATA%\AgentStudio\graph\<proyecto>\<commit>.json` | Derivado: se borra y se recalcula |
| Los diagramas, la versión anterior de cada uno y su índice | `%LOCALAPPDATA%\AgentStudio\diagrams\<proyecto>\` | Derivado, y de tu equipo (D-25) |
| Los diagramas exportados | La carpeta del repositorio que elijas (por defecto `docs/generado/diagramas/`), solo si lo pides | Tu elección (decisión 9) |
| Respuestas de GitHub con su `ETag` | `%LOCALAPPDATA%\AgentStudio\skills-doctor\cache.json` | Caché: se puede borrar |
| Huella revisada de los scripts de una skill | `skill-script-reviews.json`, junto a las decisiones globales de skills (**corregido por WP-S:** un fichero propio, no un campo de `SkillActivationStore`, que es un historial de decisiones) | Va con la skill, no con un proyecto |
| El token de GitHub | La bóveda (`%LOCALAPPDATA%\AgentStudio\vault\`), cifrado con DPAPI | D-15 |
| El ajuste «Comprobar las skills en GitHub» | `AppSettings` (y `WithPreferencesOf`) | Como los demás permisos de Ajustes |
| La carpeta neutra, la temporal y la configuración vacía de Docker | `%LOCALAPPDATA%\AgentStudio\doctor\{cwd,tmp,docker-config}` | D-02 y D-09 |

- **El resultado de Auto Doctor no se guarda:** se calcula al abrir el panel. No es historia del proyecto.
- **`DecisionKind` crece en dos valores, al final,** con sus pruebas de compatibilidad.
- **`AgentStudioFiles` no cambia:** los diagramas no viven en `.agentstudio/` (D-25).

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

> **Corregido por WP-S (2026-11-13):** el permiso y la clave de acceso se manejan en la propia pantalla de Skills (la petición de WP-S lo pidió así), no solo en Ajustes. El permiso vive en
> `AppSettings.SkillsDoctorNetworkEnabled` y la clave en la bóveda; el lead puede repetirlos en Ajustes sobre `ISkillsDoctorNetworkSetting` e `ISkillsDoctorTokenManager`, que ya existen.
> **Auto Doctor no aloja ninguna comprobación de skills:** D-01 fija cinco secciones (proveedores, herramientas, proyecto, entorno declarado y contenedores) y D-10 solo nombra los arreglos
> «Ver la skill» y «Desactivar la skill archivada», que §9.8 deja al lead («cruzan paquetes»). WP-S no añadió una fuente a `IDoctorService`.

- El permiso «Comprobar las skills en GitHub» (apagado), y debajo «Token de GitHub (opcional)»: guardar, sustituir y borrar,
  cada uno con Hello. El token nunca se vuelve a mostrar: solo «hay un token guardado». Un texto avisa de que, con token,
  cada comprobación pedirá Hello (D-15).

### 6.4 Auto Graph (documento nuevo, con tres secciones)

- **«Grafo»** (D-22): el lienzo, la leyenda (estática, parcial y, si las hay, IA), el commit que describe, la cobertura y
  «Actualizar».
- **«Diagramas»** (D-26): la lista con su estado, la fuente en solo lectura, «Generar», «Regenerar», «Deshacer», «Copiar» y
  «Exportar al repositorio», que pregunta antes de pisar un fichero que cambió fuera.
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
| Lanzar herramientas del sistema (D-02) | Que se ejecute algo del repositorio, o con su configuración | Lista cerrada, resolución fuera del proyecto (WP-0), carpeta neutra, entorno por lista blanca, Docker sin la configuración de tu usuario. F6-7 lo prueba |
| Git en la sección «Proyecto» (D-02) | Es un proceso que arranca con la carpeta de trabajo en el repositorio | Solo por la capa endurecida y con el ejecutable del sistema. Es una excepción al literal de F6-7, y la decides tú (decisión 13) |
| Leer manifiestos (D-05) | Fichero enorme, enlace hacia fuera, `include` que tira de otro fichero, valores que acaban en una orden | Lista cerrada, 256 KB, sin enlaces, sin seguir referencias, valores solo como datos |
| Analizar la solución (D-16 a D-19) | Ejecutar código por MSBuild, analizadores o generadores; agotar memoria | Sin MSBuild ni `Compilation`; proceso aparte a integridad baja sobre una copia; ninguna ruta sale de la copia ni cruza un enlace; el recorrido no entra en cuerpos; topes por fichero. F6-12 lo prueba |
| La salida del proceso de análisis | Un JSON manipulado | Esquema estricto y topes al leerlo |
| Red a GitHub (D-13) | Fuga de datos, destino inesperado, uso sin permiso | Apagado por defecto, solo a petición, solo `api.github.com`; cada dato de la dirección validado y escapado; redirecciones a mano y host comprobado en cada petición. F6-13 lo prueba |
| Respuestas de GitHub | Datos manipulados o enormes | 1 MB, solo los campos esperados |
| El token (D-15) | Que se filtre o se tome de donde no toca | DPAPI; Hello al guardar, al borrar y en cada tanda que lo usa, comprobado en el comprobador; nunca en registro, logs ni mensajes de error; nunca de un CLI |
| Texto de un agente (D-21) | Que pase por hecho, o que traiga instrucciones | Esquema estricto, extremos que existen, marcado, fuera de la huella y de los diagramas; el juez no lo recibe |
| Exportar diagramas al repositorio (D-25) | Sobrescribir algo tuyo; leerlo de vuelta como verdad | Solo si lo pides; nombres de lista cerrada; nunca durante una ejecución; pregunta antes de pisar un fichero cambiado; nunca se interpreta |
| Nombres del repositorio dentro de un diagrama (D-24) | Inyectar sintaxis de Mermaid (un `click` con una URL) | Identificadores propios; un escapador único (también `;` y `%`); los mensajes de la secuencia, de un vocabulario cerrado; sin directivas. Prueba adversaria en F6-10 |
| Arreglos (D-10) | Que la app cambie algo sin que lo sepas | Tres clases en el tipo; «aplicar» solo con deshacer o con aprobación; rastro en el registro |

**Residuales que se aceptan y se escriben en los ADR:**

- Una herramienta del sistema comprometida fuera del proyecto engaña a Auto Doctor (ya era el residual del PATH de ADR-0018).
- El proceso de análisis, a integridad baja, puede leer lo que tu usuario lee y abrir conexiones (residuales de ADR-0017). No
  ejecuta nada del repositorio y solo abre rutas de la copia (D-16), así que solo un fallo del propio analizador lo
  aprovecharía.
- `git archive` respeta `export-ignore`: el grafo y las discrepancias no ven esos ficheros.
- GitHub ve tu dirección IP y qué repositorios públicos consultas.
- El token de la bóveda lo puede descifrar cualquier proceso que corra con tu usuario (D-15).
- La capa git arranca `git` dentro del repositorio (decisión 13).

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
| F6-3 | Una solución de ejemplo fija (`tests/AgentStudio.Tests/GraphWorker/Fixtures/SampleSolution/**`, con sus `.cs` fuera de la compilación de las pruebas) contra un dorado: proyectos, referencias y espacios de nombres. Y una prueba de humo sobre `AgentStudio.slnx`, **sin dorado** (cambiaría con cada commit): salen sus proyectos, cada uno con algún espacio de nombres y ninguna ruta fuera; lectores de `.sln` y `.slnx` con ejemplos; un proyecto con `Condition` sale «parcial» | `tests/.../Infrastructure/Graph/**`, `tests/.../GraphWorker/**` |
| F6-4 | Prueba de ViewModel: una relación inferida tiene otro estilo, su etiqueta y su nombre accesible, y el interruptor la oculta; la huella del modelo no cambia al añadirla | `tests/.../Desktop/Graph/**` |
| F6-5 | Demostración: de un proyecto a su `.csproj`, de un espacio de nombres a un fichero y de un proyecto a una tarea. Más una prueba de ViewModel de cada orden | Demostración y `tests/.../Desktop/Graph/**` |
| F6-6 | Respuestas grabadas de la API (archivado, último commit de la carpeta igual y distinto, 304, 403 por límite, 404 y redirección al mismo host); la huella de scripts cambia y vuelve a «sin revisar»; con y sin token | `tests/.../Infrastructure/Skills/GitHub/**` con `Fixtures/` |
| F6-7 | Un repositorio de ejemplo por pila y uno con todo; lectores con ficheros hostiles (enorme, enlace simbólico, unión, JSON profundo) y un marcador de sincronización, que sí se lee. **La prueba central:** un repositorio con `dotnet.cmd`, `node.exe`, `git.exe`, `claude.cmd`, `codex.cmd` y `.venv\Scripts\python.exe` plantados, su carpeta en el PATH y un `global.json` con `sdk.paths`; un lanzador que registra cada `ProcessSpec` comprueba que ninguno (herramientas, entorno ni sondas de proveedores) tiene la carpeta de trabajo ni el ejecutable dentro del repositorio. Las comprobaciones de git quedan fuera de esta prueba (decisión 13): otra comprueba que su ejecutable no es el plantado | `tests/.../Domain/Environments/**`, `tests/.../Infrastructure/Diagnostics/**` |
| F6-8 | El verificador con un perfil de dos pasos (dos herramientas distintas): cada evidencia lleva su bloque `tools:` con la versión de su paso, y `EvidenceToolVersions.Parse` lo lee | `tests/.../Orchestration/Verification/**` |
| F6-9 | Salidas grabadas de `docker version --format json` (sin motor y con él) y de `wsl --status` (sin WSL, sin distribución y listo), en castellano y en inglés; cada una da su estado, su versión y su arreglo; el `ProcessSpec` de `docker` lleva su `DOCKER_CONFIG` propio y el de `wsl`, `WSL_UTF8=1` | `tests/.../Infrastructure/Diagnostics/Fixtures/**` |
| F6-10 | Dorados de los tres generadores sobre modelos fijos (un `CodeGraph` de ejemplo, un plan y un registro preparados), y una prueba de humo sobre la propia solución, sin dorado; dos generaciones seguidas, iguales byte a byte; cambia el modelo y sale «desactualizado»; se edita una copia exportada y sale «cambiada fuera» sin que nada la interprete, y exportar encima no escribe sin confirmación; «Regenerar» y «Deshacer» devuelven el texto anterior byte a byte. **Prueba adversaria** (D-24): nombres de agente, títulos de tarea y nombres de proyecto con `;`, `%%`, `%%{init}%%`, `-->>`, `click`, `end`, `Note over`, comillas y saltos de línea dan las mismas sentencias que con nombres inocuos | `tests/.../Memory/Diagrams/**` |
| F6-11 | Un repositorio preparado con un fallo de cada clase y uno sin fallos; los casos de D-28 (ancla, URL, bloque de código, mayúsculas, patrón con `*`); un hito con un requisito sin tarea | `tests/.../Memory/Consistency/**` |

**F6-7 y git, sin rodeos.** El criterio dice «ningún proceso arranca dentro del repositorio ni con un ejecutable suyo». Lo que
este diseño garantiza y prueba es: (1) leer el entorno declarado no lanza nada; (2) las herramientas y las sondas de
proveedores arrancan fuera del repositorio y con un ejecutable de fuera. Lo que **no** cumple al pie de la letra: las
comprobaciones de la sección «Proyecto» que usan git arrancan `git` (el del sistema) con la carpeta de trabajo en el
repositorio. Eso es de F6-1, no de la lectura del entorno, y es como trabaja ya toda la capa git; pero es una lectura del
criterio, y te toca a ti (decisión 13). Si eliges que no, esas comprobaciones salen «sin comprobar» y el criterio se cumple
literalmente.

**Criterios que se añaden,** porque F6-3 y F6-6 no dicen nada de seguridad y esta fase gira en torno a ella. No cambian el
sentido de ninguno anterior; si no los quieres como criterios, quedan como pruebas (decisión 12).

| ID | Criterio de aceptación | Cómo se verifica |
|---|---|---|
| F6-12 | Auto Graph no ejecuta nada del repositorio: no evalúa MSBuild ni crea una compilación, analiza una copia del commit en un proceso a integridad baja y no abre ninguna ruta de fuera de esa copia | Una prueba de arquitectura: ningún proyecto referencia `Microsoft.Build*` ni `Microsoft.CodeAnalysis.Workspaces*`. Un repositorio con un `Directory.Build.props` que lanza un proceso, un analizador y un generador plantados: tras el análisis, su centinela no existe y el único proceso lanzado es el de análisis. **Prueba adversaria de rutas:** un `.slnx`, un `.sln` y un `.csproj` con `..\..\`, una ruta absoluta y una unión hacia fuera, y un `.cs` centinela fuera de la copia: el centinela no se abre (su espacio de nombres no sale) y queda `graph.outside-root`. Un `.cs` con anidamiento extremo sale `graph.file-skipped` y el resto del grafo, entero |
| F6-13 | El doctor de skills no usa la red sin tu permiso, solo habla con `api.github.com` y nunca deja el token a la vista | Con el ajuste apagado, un manejador HTTP de prueba no recibe ninguna petición; una redirección a otro host se rechaza; un `owner`, un `repo`, una rama y una carpeta con `..`, `?`, `#` o `%2e%2e` no producen ninguna petición; con el Hello denegado, la bóveda no recibe ninguna lectura y la tanda sigue sin token; un token centinela no aparece en el registro, la bitácora, los logs ni los mensajes de error tras una tanda con fallos |

**Reglas de todas las pruebas:** solo `Assert` de xUnit; `using System.IO;` donde haga falta; las que lanzan herramientas
reales del equipo, con `Category=LocalTools`; ninguna con red real (todo con respuestas grabadas); ninguna con un CLI de
proveedor.

---

## 9. Paquetes de trabajo

### 9.0 WP-0 · Localizador y sondas (S; independiente, se puede hacer ya)

Un arreglo de seguridad del código de hoy. No depende del P0 ni del resto de la fase, y la fase lo da por hecho (D-02).

- **Qué arregla:** un ejecutable plantado en una carpeta del proyecto que esté en tu PATH con ruta absoluta se resuelve hoy
  como si fuera del sistema; y las sondas de `--version` arrancan en la carpeta que tenga la app.
- **Qué cambia:**
  - `IUntrustedRoots { IReadOnlyList<string> Roots { get; } }`, en `src/AgentStudio.Application/Execution/ProcessContracts.cs`
    (existe): las raíces de los proyectos abiertos;
  - `src/AgentStudio.Infrastructure/Processes/ExecutableLocator.cs` (existe): descarta las entradas del PATH que, resueltas
    sus rutas finales, caen dentro de una de esas raíces, y también un resultado que caiga dentro;
  - `src/AgentStudio.Infrastructure/Processes/Isolation/LowIntegrityProcess.cs` (existe): `ResolveExecutable` recorre el PATH
    por su cuenta, así que aplica la misma regla;
  - `src/AgentStudio.Providers/Cli/CliProbe.cs` (existe): fija la carpeta de trabajo a una neutra
    (`%LOCALAPPDATA%\AgentStudio\doctor\cwd`, creada vacía);
  - `src/AgentStudio.Desktop/Services/OpenProjectRoots.cs` (nuevo), que implementa el puerto con el proyecto abierto, y su
    registro en `src/AgentStudio.Desktop/Composition/PlatformServices.cs` (existe).
- **Pruebas:**
  - `tests/AgentStudio.Tests/Infrastructure/Processes/ExecutableLocatorTests.cs` (nuevo): una entrada absoluta dentro de la
    raíz no resuelve; la misma fuera, sí; una unión (junction) que apunta dentro, tampoco; sin raíces, todo como hoy;
  - `tests/AgentStudio.Tests/Infrastructure/Processes/LowIntegrityProcessTests.cs` (existe): el mismo caso a integridad baja;
  - `tests/AgentStudio.Tests/Providers/ProbeTests.cs` (existe): con `claude.cmd` y `codex.cmd` plantados en una carpeta del
    proyecto que está en el PATH, la sonda da «no instalado» y el `ProcessSpec` registrado lleva la carpeta neutra.
- **Lo que se nota:** una herramienta instalada dentro de la carpeta de un proyecto abierto deja de encontrarse (decisión
  14). Se anota en [18](18-peticiones-y-pendientes.md) al integrarlo.
- **Tamaño:** S. Un agente. No comparte ningún fichero con los demás paquetes de la fase.

### 9.1 Reglas comunes

Las de [17 §9.1](17-constructor-visual-de-agentes.md#91-reglas-comunes) y docs/19 §11.2:

- el P0 va en serie y se fusiona antes de abrir los paquetes;
- los paquetes van en worktrees, **como mucho dos a la vez,** sin ningún fichero en común;
- integra el lead.

**Dueños únicos:**

- **El P0:** `Directory.Packages.props`, `AgentStudio.slnx`, los `*.csproj`, los enums compartidos y los contratos de §4.
- **El lead, en la integración:** `src/AgentStudio.Desktop/Composition/**`, `MainViewModel*.cs`, `MainWindow.xaml(.cs)`,
  `App.xaml(.cs)`, `Themes/DocumentTemplates.xaml`, `Settings/**`, `Services/AppSettings.cs`, `docs/03`, `docs/08`, `docs/18`,
  `docs/README.md` y la bitácora. `Help/HelpScreens.cs` y `tests/.../UiInventory/UiScreens.cs` los toca el P0 una vez (§9.2)
  y nadie más.

**«Hecho»** exige `dotnet build AgentStudio.slnx` con 0 avisos, `dotnet test` en verde sin `ProviderLive`, los ADR y documentos
del paquete, su artículo de ayuda, su pantalla en el inventario y nada simulado en la interfaz.

### 9.2 P0 · Contratos, dominio, payloads y dependencias (M; camino crítico)

- **Dominio:** `src/AgentStudio.Domain/Diagnostics/DoctorCheck.cs`, `src/AgentStudio.Domain/Graph/**` y los tipos de datos de
  `src/AgentStudio.Domain/Environments/` (`DeclaredEnvironment`, `InstalledTools`; la lógica es de WP-E). Todos nuevos.
- **Application:** los ficheros de §4.2 y §4.3; `Skills/SkillPorts.cs` (lo que se añade).
- **Registro:** `src/AgentStudio.Domain/Decisions/DecisionLog.cs` (dos valores al final) y
  `src/AgentStudio.Application/Decisions/DecisionPayloadSerializer.cs`.
- **Dependencias y solución:** `Directory.Packages.props` (`Microsoft.CodeAnalysis.CSharp`, tras comprobar versión, licencia
  y avisos); el proyecto `src/AgentStudio.GraphWorker/AgentStudio.GraphWorker.csproj` (nuevo, vacío, con su entrada en
  `AgentStudio.slnx`), si la decisión 4 es (a).
- **Escritorio, lo mínimo para que cada paquete llegue a verde solo:**
  - `src/AgentStudio.Desktop/Graph/{GraphDocumentViewModel,GraphSection}.cs` (el anfitrión y el contrato de sección);
  - los pares de recursos `DoctorStrings`, `GraphStrings` y `DocsStrings` en `src/AgentStudio.Desktop/Resources/`;
  - los códigos `doctor` y `graph` en `Help/HelpScreens.cs`, con la primera versión de sus dos artículos
    (`Help/{es-ES,en}/{doctor,graph}.md`): `HelpTests` exige un artículo por código, así que van juntos. Cada paquete
    completa después **su** artículo con las claves ⓘ de sus vistas;
  - `tests/AgentStudio.Tests/Desktop/UiInventory/{UiScreensDoctor,UiScreensGraph,UiScreensDocs}.cs` con su lista `All`
    vacía, ya sumadas en `UiScreens.cs`. Cada paquete añade sus `Screen(...)` en su fichero y no toca el agregador.
- **Pruebas:** `tests/AgentStudio.Tests/Domain/{Diagnostics,Graph}/**`, `Application/Phase6PayloadTests.cs`,
  `DecisionKindCompatibilityTests` y `DecisionPayloadTests` al día, y la prueba de arquitectura de F6-12.
- **Documentos:** [04](04-contratos.md) §25 y los esqueletos de los ADR de §10.
- **Fixtures:** `tests/AgentStudio.Tests/AgentStudio.Tests.csproj` copia como contenido las carpetas `Fixtures` de la fase
  (`GraphWorker/**`, `Infrastructure/**` y `Domain/**`) y saca sus `.cs` de la compilación: la solución de ejemplo tiene
  código que no debe compilarse con las pruebas.
- **El primer paso del P0 es una comprobación, no un spike:** qué versión estable de `Microsoft.CodeAnalysis.CSharp`
  corresponde al compilador del SDK 10.0.303; su ficha (licencia, dependencias y avisos); que compila con 0 avisos en
  `net10.0`; y qué pesa publicado. Lo mismo con el paquete de DPAPI, si la decisión 15 es (a). Si algo no cuadra, se
  vuelve a ti antes de escribir contratos.
- **Depende de WP-0** solo por `IUntrustedRoots`.

### 9.3 WP-A · Auto Doctor: herramientas, proyecto, contenedores y arreglos (M-L)

- `src/AgentStudio.Infrastructure/Diagnostics/**` (nuevo): la puerta de D-02 (el catálogo, la lista blanca del entorno y sus carpetas propias), el inventario de lo instalado, WebView2 por el registro, Docker y WSL.
- `src/AgentStudio.Application/Diagnostics/{DoctorService,ProviderCheckSource,ProjectCheckSource,DoctorFixes}.cs` (nuevos).

- `src/AgentStudio.Desktop/Doctor/**` y `Views/Doctor/**` (nuevos; aquí va también la tabla del entorno, que pinta con los
  hechos de `DoctorFactNames` y rellena WP-E), `Views/AutoDoctorView.xaml(.cs)` y
  `ViewModels/ProviderDiagnosticsViewModel.cs` (existen), `Resources/DoctorStrings*.resx` y `Help/{es-ES,en}/doctor.md`.
- Pruebas: `tests/AgentStudio.Tests/{Application,Infrastructure}/Diagnostics/**`, 
  `Desktop/Doctor/**` y `Desktop/UiInventory/UiScreensDoctor.cs`.
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

- `src/AgentStudio.Infrastructure/Skills/GitHub/**` (nuevo): el constructor de direcciones con su validación, el manejador que comprueba el host, el
  cliente, la cola, la caché y `GitHubSkillHealthChecker`.
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
- `src/AgentStudio.Application/Graph/CommitAnalysisService.cs` (nuevo): una copia por commit para el grafo y las
  discrepancias, y su caché.
- `src/AgentStudio.Orchestration/Graph/**` (nuevo): `IGraphTaskIndex`, desde los intentos del registro.
- `src/AgentStudio.Application/Graph/GraphInferenceService.cs` (nuevo), si la decisión 7 es (a).
- `src/AgentStudio.Desktop/Graph/**` (salvo los dos ficheros del P0) y `Views/Graph/**` (nuevos), `Resources/GraphStrings*.resx`,
  `Help/{es-ES,en}/graph.md` (nuevo).
- Pruebas: `tests/AgentStudio.Tests/{GraphWorker,Infrastructure/Graph,Orchestration/Graph,Desktop/Graph}/**` y
  `Desktop/UiInventory/UiScreensGraph.cs` (nuevo).
- Criterios: F6-3, F6-4, F6-5 y F6-12. ADR-0062.

### 9.7 WP-D · Diagramas y discrepancias (M-L)

- `src/AgentStudio.Memory/Diagrams/**` (nuevo): los tres generadores, la gramática y su escapador, la cabecera, la caché con la
  versión anterior, el índice y la exportación al repositorio.
- `src/AgentStudio.Memory/Consistency/**` (nuevo): las cuatro comprobaciones y el servicio.
- `src/AgentStudio.Desktop/Docs/**` y `Views/Docs/**` (nuevos), `Resources/DocsStrings*.resx`.
- Pruebas: `tests/AgentStudio.Tests/Memory/{Diagrams,Consistency}/**` con dorados, `Desktop/Docs/**` y
  `Desktop/UiInventory/UiScreensDocs.cs` (nuevo).
- Lee el grafo y la copia del commit por sus puertos. Sus dorados usan un `CodeGraph` fijo, así que no espera a WP-G; la
  prueba de humo sobre la propia solución va en la integración.
- Criterios: F6-10 y F6-11. ADR-0045.

### 9.8 Integración (el lead; M)

- La composición (`Composition/{DiagnosticsServices,GraphServices}.cs`, nuevos, con `IUntrustedRoots` e
  `ISkillsDoctorPolicy`), «Auto Graph» en la barra de actividad, las plantillas de documento, y el permiso y el token en
  Ajustes. Los textos de WP-E y de WP-S que salen en el panel van en `DoctorStrings` y `SkillsStrings`: los de WP-E los
  añade el lead al integrar, para que WP-E no toque el fichero de recursos de WP-A.
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
| WP-0 | Nada: se puede hacer ya | S |
| P0 | La Fase 14 con la Fase 9 integradas, y WP-0 | M |
| WP-A | P0 | M-L |
| WP-G | P0 | L |
| WP-E | P0 (usa WP-A por su puerto) | M |
| WP-D | P0 | M-L |
| WP-S | P0 | M |
| Integración | Todos | M |

WP-0 va antes y aparte. Después del P0, con dos a la vez, tres tandas:

1. **WP-G y WP-A:** el más largo y el que más desbloquea.
2. **WP-D y WP-E:** el segundo ya tiene el inventario real de WP-A.
3. **WP-S,** con la integración empezando a su lado.

La fase entera es L. Los tamaños son estimaciones del diseño.

---

## 10. ADR a escribir

En `docs/adr/` el último es el 0057. Los 0041 a 0046 los reservó docs/16 (solo el 0045 es de esta fase) y los 0058 a 0060,
docs/19. Ninguno de ellos existe todavía. Esta fase usa el 0045 y propone 0061 a 0063; si otra tanda los toma, se corren.

| ADR | Tema | Paquete |
|---|---|---|
| 0045 (reservado por docs/16) | Diagramas generados con la versión que describen: Mermaid como texto, la gramática acotada, la cabecera, el índice propio y la vigencia; las discrepancias deterministas y sus códigos. La Fase 16 le añade una adenda con el motor | P0 y WP-D |
| 0061 | Auto Doctor: la comprobación única, la puerta cerrada para lanzar herramientas, los manifiestos como datos no fiables, comparar en código, los arreglos en tres clases y las versiones dentro de la evidencia; recoge el arreglo del localizador de WP-0 y la excepción de git (amplía ADR-0016 y ADR-0018) | P0, WP-A y WP-E |
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
| Los lectores de líneas de TOML y `Dockerfile` se equivocan con un fichero raro | Solo extraen un campo y una instrucción, y listan lo no interpretado; nunca deciden un error por sí solos |
| Tras WP-0, deja de encontrarse una herramienta que usabas desde dentro de un proyecto | Auto Doctor lo avisa con su motivo; es la decisión 14 |
| Demasiadas comprobaciones vuelven a hacer grande el panel | Secciones plegadas con el peor estado; «no aplica» oculto por defecto |
| Falsos positivos en las discrepancias | Reglas cerradas (D-28), gravedad «aviso» y pruebas con los documentos de este repositorio |
| El límite de GitHub sin token | Caché con `ETag`, en serie, y un estado claro al llegar al límite |
| La copia del commit es lenta en un repositorio grande | Una por cálculo y con caché por commit; comparte el cerrojo de la verificación y se puede cancelar |
| Dos fases tocan los diagramas | El reparto de §1.2 y los puertos de §4.2; ADR-0045 con su adenda |
| El equipo se queda sin memoria con dos agentes compilando | Dos paquetes a la vez como mucho, con la prioridad de build baja, como ya haces |

---

## 12. Sin verificar

- **`Microsoft.CodeAnalysis.CSharp`: verificado por el P0 el 2026-11-12.** La versión del compilador del SDK 10.0.303 es la **5.6.0**
  (`dotnet --list-sdks` da 10.0.303 y `Roslyn\bincore\Microsoft.CodeAnalysis.CSharp.dll` del SDK es 5.6.0-2.26377), no la 5.9.0 que
  decía este apartado: se fija la 5.6.0, cuya ficha apunta a `release/stable`. Licencia MIT, autor Microsoft; dependencias en
  `net10.0`: `Microsoft.CodeAnalysis.Common` 5.6.0 y `Microsoft.CodeAnalysis.Analyzers` 5.3.0, también MIT; listada, publicada el
  2026-07-02, sin retirada; ninguno de los tres figura en el feed de vulnerabilidades de nuget.org del 2026-11-11. Compila con 0
  avisos con `TreatWarningsAsErrors` (el proyecto de análisis la referencia con `PrivateAssets="compile"`). Peso publicado (`dotnet publish -c Release` del proyecto de análisis, con
  su satélite de idioma): 12,6 MB en 13 ficheros; sin él, 12,1 MB en 11, de ellos `Microsoft.CodeAnalysis.CSharp.dll` 6,7 MB y
  `Microsoft.CodeAnalysis.dll` 2,9 MB (unos 9,6 MB de Roslyn, que no se carga en la app).
- **Que el analizador de Roslyn no agote la pila al analizar** (no al recorrer) un fichero con anidamiento extremo. Por eso
  el proceso aparte y el relanzamiento de D-18.
- **Los límites del analizador con ficheros hostiles:** que un fichero con anidamiento extremo termine con un error y no con
  la caída del proceso. Por eso va en un proceso aparte.
- **`wsl --status`: comprobado por WP-A el 2026-11-12 en este equipo (Windows en castellano).** Sin `WSL_UTF8=1` la salida es UTF-16 y con él UTF-8; el código
  de salida es 0 y el texto es «Distribución predeterminada: docker-desktop» y «Versión predeterminada: 2» (capturado: `tests/**/Diagnostics/Fixtures/real-wsl-status-es-docker-desktop-only.txt`).
  **Hallazgo:** la única distribución de este equipo es la interna de Docker Desktop, que una persona no puede usar; el lector la trata como «sin distribución propia».
  **Sin verificar:** el texto en inglés, el de «sin distribución» y el de WSL no instalado (fixtures `handmade-*`, escritos a mano), y con qué código falla
  `wsl --status` si WSL no está instalado: el lector da «sin comprobar» ante cualquier código distinto de 0.
- **`docker version --format json`: comprobado por WP-A el 2026-11-12 con Docker Desktop 27.3.1 y el motor parado**, con un `DOCKER_CONFIG` vacío: código de salida 1, y en la
  salida estándar un JSON con `Client` y `"Server":null` (capturado). **Sin verificar:** el motor en marcha (fixture a mano con el mismo esquema) y que con el
  motor en marcha un `DOCKER_CONFIG` vacío responda por la tubería por defecto.
- **Las claves del registro de WebView2:** son las de Microsoft Learn («Distribute your app and the WebView2 Runtime», consultada el 2026-11-12); la prueba `LocalTools` las lee en este equipo
  sin fallar. Falta probarlas con una instalación por usuario.
- **Las guías (revisadas el 2026-11-13):** los identificadores de `winget` (`Microsoft.DotNet.SDK.10`, `OpenJS.NodeJS.LTS`, `Python.Python.3.13`, `Git.Git`, `Microsoft.EdgeWebView2Runtime`, `Docker.DockerDesktop`) se comprobaron con
  `winget show --id … --exact` en este equipo, y las órdenes llevan `--id … --exact`. Las páginas son las finales, sin redirección (se comprobó con peticiones HEAD; `docs.anthropic.com` redirigía a `code.claude.com/docs/en/setup`).
  Siguen sin comprobar `npm install -g @anthropic-ai/claude-code`, `@openai/codex` y `@google/gemini-cli`, `claude auth login` y `codex login`: ninguna se ejecuta, solo se copian y se abren a petición.
- **Docker y el contexto:** con un `DOCKER_CONFIG` vacío el contexto es `default`, y un Docker Desktop puede escuchar solo en la tubería de su contexto `desktop-linux` (`npipe:////./pipe/dockerDesktopLinuxEngine`).
  Si el contexto por defecto no muestra un motor, la puerta pregunta también a esa tubería (`DoctorTool.DockerVersionDesktopPipe`, `docker --host npipe:////./pipe/dockerDesktopLinuxEngine version --format json`, también con el `DOCKER_CONFIG` vacío: no se lee la configuración de la persona).
  **No se pudo medir aquí** (el motor está parado en este equipo): cuál de las dos tuberías sirve cada contexto. **Prueba manual que lo cierra:** con Docker Desktop en marcha, ejecutar la prueba `LocalTools` de la puerta y comprobar que
  `docker=Found` con `Server`; luego `docker context ls` para ver qué tubería usa `default`. Mientras el motor no responde, la pantalla dice «no responde», no «parado».
- **WSL no instalado:** Microsoft documenta que sin WSL `wsl.exe` termina con un código distinto de cero y un mensaje (Learn, «WSL interop»), pero no documenta el código ni el texto. La puerta lee también el error estándar y reconoce el caso por la orden `--install`, que no se traduce,
  sin ninguna línea con la versión por defecto; cualquier otra cosa que falle es «sin comprobar». **Sin verificar:** el texto real en un equipo sin WSL (la fixture `handmade-wsl-status-garbage.txt` es a mano). Prueba manual: en un equipo o una máquina virtual sin WSL, `wsl --status` y comprobar que el mensaje nombra `wsl.exe --install`.
  `Default Version` es la versión de las distribuciones nuevas, no la de la distribución por defecto: la pantalla lo dice y no afirma «WSL 2».
- **Python de la Tienda:** el `python.exe` real de la Tienda y el acceso directo vacío son el mismo tipo de fichero en la misma carpeta. Se mira si existe `%LOCALAPPDATA%\Packages\PythonSoftwareFoundation.Python.*` (solo nombres de carpeta): con él se pregunta como a cualquier Python; sin él no se arranca y sale «sin comprobar» con su explicación, porque un Python instalado y nunca ejecutado tampoco tiene esa carpeta.
- **La API de GitHub:** que `commits?sha=…&path=…&per_page=1` da el último commit que tocó la carpeta también cuando `sha` es
  un commit y no una rama, y que un 304 no cuenta contra el límite sin token. Lo segundo
  lo dicen sus buenas prácticas (citadas en docs/16).
- **DPAPI en .NET 10: verificado por el P0 el 2026-11-12.** `System.Security.Cryptography.ProtectedData` 10.0.13 (la última 10.x es la 10.0.15;
  se fija la 10.0.13 para alinearla con los demás paquetes de `Microsoft.Extensions.*`): licencia MIT, autor Microsoft
  (`dotnet/dotnet`), grupo `net10.0` sin dependencias, listada, publicada el 2026-10-13, sin retirada y sin aviso en el feed de
  vulnerabilidades de nuget.org del 2026-11-11. La referencia está en `AgentStudio.Infrastructure.csproj` (la usará WP-S).
- **El doctor de skills (WP-S, 2026-11-13): nada se probó contra GitHub.** Las pruebas usan un `HttpMessageHandler` falso y respuestas **escritas a mano** a partir de la documentación de la API REST (`tests/**/Infrastructure/Skills/GitHub/Fixtures`, con su `README.md`); ninguna toca la red ni lleva un token real. Queda sin verificar:
  - **Las respuestas reales:** que `GET /repos/{owner}/{repo}` trae `archived` como booleano, que `commits?sha=…&path=…&per_page=1` devuelve una lista con `sha` también cuando `sha` es un commit, que la carpeta raíz (sin `path`) funciona y que el último commit de la carpeta del commit fijado coincide con el de la rama cuando nada cambió.
  - **Los códigos reales:** el `403` y el `429` de límite (cabeceras `x-ratelimit-remaining`, `x-ratelimit-reset` y `retry-after`), el `404` de un repositorio privado, la redirección `301` de un repositorio renombrado (a `/repositories/{id}`) y que un `304` no cuenta contra el límite sin token. El cliente solo sigue una redirección a `/repos/` o `/repositories/`; si GitHub usara otra ruta, saldría «redirección rechazada».
  - **El límite de tiempo (10 s con el cuerpo) y la conexión real:** ninguna prueba espera diez segundos ni abre un socket; `GitHubApiClient.CreateTransport()` solo se comprueba por sus propiedades (`AllowAutoRedirect = false`, sin cookies ni credenciales).
  - **DPAPI y Hello en la aplicación:** la bóveda se prueba con DPAPI real en una carpeta temporal (ida y vuelta, el fichero no lleva el texto en claro), pero Windows Hello y el bloqueo de Windows de `ReinforcedConfirmation` no se ejercen en una prueba. **Prueba manual que lo cierra, con tu OK:** abrir Skills, encender el permiso, guardar una clave de solo lectura (Hello), pulsar «Comprobar en GitHub» (Hello una vez) y comprobar el estado, el límite y que «Borrar» pide Hello.
  - **Los mensajes de las comprobaciones de GitHub** salen en castellano en cualquier idioma, como los de `SkillHealthReader` (los textos de la pantalla alrededor sí van en recursos).
  - **Falta el enlace «abrir» de cada script** de la tarjeta (D-14 / §6.2): se muestra la lista, no un enlace.
- **`git archive` y `export-ignore`:** que la copia de la verificación se comporta así en esta capa.
- **El coste de «Proponer relaciones»** (D-21): tokens por lanzamiento. Es una estimación hasta medirlo.
- **Los tamaños de los paquetes** son estimaciones del diseño.
- **No se compiló ni se ejecutó nada** para escribir este documento: las firmas de §4 son propuestas.

---

## 13. Decisiones que necesitan a la persona

Quince decisiones: de producto, de dependencias, de red, de cuota y de cómo leer un criterio. La recomendada va primero,
marcada.

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
   - **(a) Recomendada:** `Microsoft.CodeAnalysis.CSharp`, solo el analizador sintáctico, en la versión estable que
     corresponde al compilador del SDK de .NET 10 fijado en `global.json`. Averiguarla y comprobar su ficha es el primer paso
     del P0.
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
   - **(a) Recomendada:** una bóveda DPAPI mínima ahora, con Hello para guardar, borrar y usar. Sabiendo lo que es: protege
     de otro usuario y de otro equipo, **no** de un proceso que corra con tu usuario, y Hello solo guarda la interfaz. Se
     recomienda porque el token es de solo lectura de datos públicos y porque la alternativa es peor.
   - (b) Una variable de entorno (`AGENTSTUDIO_GITHUB_TOKEN`). Sin bóveda y sin Hello; el token queda en claro en tu
     configuración y lo hereda cada proceso que arranques, también los CLIs de los agentes.
   - (c) Sin token hasta la bóveda completa de la Fase 7. F6-6 dice «con token opcional»: esa parte quedaría pendiente. Es
     la opción más prudente si no quieres ningún secreto en la app todavía.
7. **Las relaciones que propone una IA** (D-21). Gasta cuota.
   - **(a) Recomendada:** la acción «Proponer relaciones», con tu OK cada vez y un solo lanzamiento de solo lectura.
   - (b) Sin acción en esta fase: el modelo y la vista distinguen los orígenes, y quien las aporta llega con las notas de los
     agentes de la Fase 16. Sin cuota; F6-4 se prueba con datos preparados.
8. **Arreglos que tocan el sistema** (D-10).
   - **(a) Recomendada:** nunca. Auto Doctor explica y te da la orden; la ejecutas tú.
   - (b) Instalar con `winget`, con aprobación y Hello. Cómodo, pero es ejecutar instaladores desde la app.
9. **Dónde viven los diagramas generados** (D-25). Es la misma pregunta que la decisión 8 de docs/19, y la respuesta vale
   para las dos fases.
   - **(a) Recomendada:** en una caché de tu equipo, y en el repositorio solo cuando pulsas «Exportar al repositorio»
     (por defecto `docs/generado/diagramas/`). No ensucia tu repositorio ni cambia tu árbol sin que lo pidas.
   - (b) Siempre en el proyecto, en `.agentstudio/diagrams/`, como decía docs/16 D-21. Se ven en GitHub sin hacer nada; cada
     regeneración es un cambio en tu repositorio.
   - (c) Nunca en el repositorio: solo la caché y «Copiar».
10. **Quién lee YAML** (D-05). Un solo lector de `compose.yaml` en toda la app.
    - **(a) Recomendada:** la Fase 6 no interpreta YAML: de compose solo dice que existe y que hace falta Docker. Los
      servicios y las imágenes los lee la Fase 16 con su librería (decisión 9 de docs/19).
    - (b) Traer esa librería de YAML ya al P0 de la Fase 6, y que la Fase 16 la reutilice. Auto Doctor lista los servicios
      desde el principio; una dependencia más en esta fase.
    - (c) Un lector propio de líneas en la Fase 6. Descartado como recomendación: acabaría habiendo dos lectores.
11. **El orden** (§9.9).
    - **(a) Recomendada:** WP-0 ya; y tras integrar la Fase 14 con la 9, P0 y tres tandas de dos; después la Fase 16.
    - (b) Solo Auto Doctor y el entorno primero (WP-A, WP-E y WP-S), y Auto Graph con los diagramas junto a la Fase 16.
12. **F6-12 y F6-13** (§8).
    - **(a) Recomendada:** entran en docs/08 como criterios de la Fase 6.
    - (b) Se quedan como pruebas de los paquetes, sin número.
13. **Git dentro de Auto Doctor y el literal de F6-7** (D-02). F6-7 dice «ningún proceso arranca dentro del repositorio»;
    preguntar a git por los hooks y los remotos lanza `git` con la carpeta de trabajo en el repositorio.
    - **(a) Recomendada:** aceptar esa excepción, solo para git y solo por su capa endurecida (ejecutable del sistema, sin
      hooks, sin red, configuración inspeccionada), que ya trabaja así en toda la app. F6-7 se lee como «leer el entorno
      declarado no lanza nada en el repositorio», y docs/08 lo dice con esas palabras.
    - (b) Auto Doctor no usa git: la sección «Proyecto» solo dice si hay `.git`; hooks, remotos y rechazos salen «sin
      comprobar». F6-7 se cumple al pie de la letra y F6-1 («Git y configuración del proyecto») queda más corto.
14. **Las herramientas instaladas dentro de un proyecto dejan de encontrarse** (WP-0, §9.0). Es un cambio que se nota.
    - **(a) Recomendada:** sí, para toda la app: un `claude.cmd`, un `node.exe` o un `git.exe` dentro de la carpeta de un
      proyecto abierto no se usan nunca, aunque esa carpeta esté en tu PATH. Auto Doctor lo avisa.
    - (b) Solo para Auto Doctor y las sondas; la verificación y git siguen resolviendo como hoy.
    - (c) No cambiar el localizador: se queda el riesgo descrito en §0.2.
15. **Cómo se llama a DPAPI** (D-15). Solo si la decisión 6 es (a).
    - **(a) Recomendada:** el paquete de Microsoft `System.Security.Cryptography.ProtectedData` (dependencia nueva; su
      versión y su licencia se comprueban en el P0, como la de Roslyn). Menos código nativo propio alrededor de un secreto.
    - (b) Una llamada directa a la API de Windows (`CryptProtectData`), sin paquete. Sin dependencia; unas decenas de líneas
      de interoperabilidad propias que hay que revisar bien.

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
