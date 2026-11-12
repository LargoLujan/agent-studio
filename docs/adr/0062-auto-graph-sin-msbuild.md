# ADR-0062 · Auto Graph: leer sin MSBuild, Roslyn solo sintáctico y un proceso de análisis aparte sobre una copia del commit

- **Estado:** aceptada · 2026-11-12 (P0 de la Fase 6). Recoge D-16 a D-22 de [docs/20](../20-fase-6-auto-doctor-y-auto-graph.md) y las decisiones 3, 4 y 7 de su §13
  (todas, la opción recomendada). **Amplía** [ADR-0002](0002-capas-y-puertos.md) con el decimotercer proyecto. El P0 verifica y fija la dependencia, crea el
  esqueleto del proceso y los contratos; WP-G implementa el análisis.
- **Contexto:**
  - F6-3 pide el grafo de un proyecto de C# «con Roslyn» (proyectos, espacios de nombres y referencias) y F6-12 (nuevo) que no ejecute nada del repositorio.
  - Microsoft dice sin rodeos que MSBuild solo se ejecuta sobre fuentes de plena confianza, «también para abrir proyectos»: `MSBuildWorkspace` evalúa cada proyecto
    e importa `Directory.Build.props`, resuelve SDK y ejecuta los destinos de diseño, que pueden ejecutar código arbitrario. Para Agent Studio el repositorio es
    contenido no fiable ([ADR-0018](0018-modelo-de-amenaza-fase-3.md)).
  - Un fichero hostil no ejecuta código al parsearse, pero puede agotar la memoria o la pila del analizador.
- **Decisión:**
  1. **Sin MSBuild: se lee, no se evalúa** (D-16). `.slnx` y `.csproj` con `XmlReader` sin DTD ni resolvedor y con tope; `.sln` con un lector de líneas; los `.cs`
     con el analizador sintáctico de Roslyn, sin compilar. Lo que MSBuild resolvería (un valor con `$(…)`, un `Condition`, un comodín, un `Import`) no se adivina: el
     proyecto sale **parcial** con su motivo (`graph.unevaluated`). Ninguna ruta sale de la copia: se normaliza contra la raíz y, si queda fuera, no se abre y se anota
     `graph.outside-root`; tampoco se entra en enlaces ni uniones.
  2. **Roslyn, solo el analizador sintáctico** (D-17, decisión 3 (a)). Dependencia nueva: **`Microsoft.CodeAnalysis.CSharp` 5.6.0**, la versión de Roslyn que trae el
     SDK 10.0.303 de `global.json` (verificado el 2026-11-12: `Microsoft.CodeAnalysis.CSharp.dll` de `Roslyn\bincore` del SDK es 5.6.0-2.26377; la ficha de nuget.org de
     la 5.6.0 apunta a la rama `release/stable` de `dotnet/roslyn`, y la 5.9.0, que el diseño citaba como última, a `release/insiders`). Licencia MIT, autor Microsoft;
     en `net10.0` solo trae `Microsoft.CodeAnalysis.Common` 5.6.0 y `Microsoft.CodeAnalysis.Analyzers` 5.3.0, también MIT; ninguno de los tres tiene aviso en el
     feed de vulnerabilidades de nuget.org del 2026-11-11. **No entran** `Microsoft.CodeAnalysis.Workspaces.*`, `Microsoft.Build.*` ni `Microsoft.Build.Locator`:
     una prueba de arquitectura lo fija (F6-12). Solo se usa `CSharpSyntaxTree.ParseText`: ninguna `Compilation`, sin analizadores ni generadores, y el recorrido
     no baja a cuerpos ni expresiones (se corta a 64 niveles).
  3. **El análisis corre en un proceso aparte, a integridad baja** (D-18, decisión 4 (a)): `AgentStudio.GraphWorker`, un ejecutable de consola como SkillsMcp, que
     depende solo de Application. **`PackageReference` de Roslyn con `PrivateAssets="compile"`**: ningún otro proyecto ve un tipo de Roslyn (Application,
     Desktop e Infrastructure no lo referencian; `PrivateAssets` sustituye al valor por defecto, así que nombra también `analyzers` y `build`, y una referencia
     directa y privada a `Microsoft.CodeAnalysis.Analyzers` 5.3.0 evita que sus reglas para autores de analizadores lleguen como activo de compilación a Desktop y a
     las pruebas, que una prueba comprueba en sus `project.assets.json`), mientras sus ficheros de ejecución llegan a la salida de quien publica el ejecutable (Desktop lo referencia solo
     para que salga junto a la app, como SkillsMcp). Roslyn (unos 10 MB) no se carga en el proceso de la interfaz y un fallo del analizador no tira la app.
  4. **El protocolo es un contrato compartido** (`GraphWorkerProtocol`, `GraphWorkerRequest`, `GraphWorkerExitCodes`, `CodeGraphJson`): `--root`, `--commit` y
     `--skip`; el modelo en JSON por la salida estándar o nada; una línea `progress:<ruta>` por la salida de error antes de abrir cada fichero, para relanzar saltando
     el que mató al proceso (hasta tres veces); códigos de salida 0, 2, 3, 4 y 5. **La salida del proceso es un dato no fiable**: se lee con un esquema estricto,
     topes (32 MB, 100 000 nodos, 500 000 relaciones) y las invariantes de `CodeGraph` como última palabra.
  5. **El grafo describe un commit, no la carpeta** (D-19): una copia del commit en un área aislada (`IRepositorySnapshots`, sobre `ArchiveCommitAsync`) que el grafo y
     las discrepancias leen una vez. No se añade ningún puerto para leer objetos de git.
  6. **El modelo lleva el origen de cada relación y su huella** (D-20 y D-21): estática (leída) o inferida (propuesta por una IA, con agente, modelo, fecha y
     motivo). `CodeGraph.ModelHash` no incluye el commit ni lo inferido. Las relaciones de una IA viven aparte (`IGraphInferenceStore`), se dibujan distintas y solo
     se piden con el OK de la persona cada vez (decisión 7 (a)); `GraphRelationsProposed` registra los dos extremos y la clase de cada relación aceptada, **no el
     motivo del agente**, que es texto de un agente y queda en la evidencia (cambio respecto al §4.3 de docs/20).
- **Lo que fija el P0:** la dependencia y su verificación (`Directory.Packages.props`), el proyecto en `AgentStudio.slnx` y su publicación junto a la app, los
  contratos de `Application/Graph` y `Application/Git`, `Domain/Graph` y el esqueleto del proceso, que **no analiza nada**: termina con el código 4 y la salida
  estándar vacía (un grafo vacío parecería un grafo). `GraphArchitectureTests` y `GraphWorkerProtocolSmokeTests` lo comprueban.
- **Alternativas descartadas:** `MSBuildWorkspace` dentro de un proceso a integridad baja (baja el daño pero sigue ejecutando código del repositorio con las lecturas
  y la red de la persona, y F6-12 pide que no se ejecute nada); analizar dentro de la app con topes (un fichero hostil podría tirarla y Roslyn se cargaría en ella);
  analizar dentro del ejecutable de SkillsMcp (mezcla dos cosas); un lector propio de `using` sin Roslyn (falla con espacios de nombres de fichero, anidados,
  directivas `#if` y cadenas crudas, y no cumple la letra de F6-3).
- **Consecuencias y residuales aceptados:** el proceso, a integridad baja, puede leer lo que la persona lee y abrir conexiones (residuales de
  [ADR-0017](0017-verificacion-aislada-baja-integridad.md)); no ejecuta nada del repositorio y solo abre rutas de la copia, así que solo un fallo del propio
  analizador lo aprovecharía. `git archive` respeta `export-ignore`: esos ficheros no salen en el grafo. Un proyecto que depende de lógica de MSBuild sale parcial. Roslyn
  sube de versión mayor a menudo: se usa la parte más estable y la versión va fijada.

## Adenda de WP-G (2026-11-13): lo que midió y decidió la implementación

- **Lo medido.** El analizador sintáctico de Roslyn **sí agota la pila** con anidamiento extremo, y no se puede capturar: 20 000 paréntesis anidados y 3 000 `A<` anidados
  (medido en el proceso real) terminan con el código `0xC00000FD`. El proceso aparte y el relanzamiento de D-18 no son un lujo. Una cuenta barata de corchetes anidados (`(`, `[`, `{`;
  más de 256) devuelve el fichero como hueco antes de parsear y evita el caso más corriente; los genéricos anidados pasan y los cubre el relanzamiento (prueba real:
  `GraphWorkerLocalToolsTests`).
- **Los ficheros que declaran cada espacio de nombres** (F6-5) viajan **al lado** del modelo (`NamespaceFiles`, campo opcional `files` de `CodeGraphJson`) y nunca entran en
  su huella. `ICodeGraphBuilder.BuildAsync` devuelve `GraphBuild`. Los códigos de hueco son los de `GraphGapCodes`.
- **Un proceso relanzado tres veces que sigue cayendo** no da un grafo «parcial con lo que haya»: no queda salida de la que sacarlo, así que el análisis **falla** con un mensaje
  sin texto del repositorio. Las cuatro cosas que sí dan grafo parcial son un fichero que falla con una excepción o un tope (hueco `graph.file-skipped`), el que mató al proceso
  (el relanzamiento lo salta), un tope total (`graph.limit`) y lo que MSBuild resolvería (`graph.unevaluated`, `graph.build-props`). Residual aceptado.
- **El anfitrión no usa `IProcessRunner.RunAsync`**: su tope es en caracteres, uno para las dos salidas, y un error estándar largo lo mataría aunque el proceso fuera legítimo. Lee
  la salida por bytes con su tope y el error estándar en líneas acotadas, sobre el mismo arranque a integridad baja (`ProcessLaunch`), con el ejecutable por ruta absoluta.
- **La copia comparte el cerrojo de la verificación** porque usa la misma fábrica de áreas (`IIsolatedWorkAreaFactory`); no hay un segundo cerrojo.
- **Relaciones de una IA** (decisión 7 (a)): propósito propio `GraphRelations` en el consumo, auxiliar de solo lectura en carpeta vacía con el OK de la persona cada vez (el mismo
  patrón que el agente de la ayuda, ADR-0056); el OK es para este grafo y este agente. Sus relaciones se guardan en un almacén derivado, aparte del modelo, y en el registro
  (`GraphRelationsProposed`, con el texto del agente como prueba guardada).
- **Lo que queda parcial:** `IConsistencyService` (WP-D) es opcional en `CommitAnalysisService`; sin él las discrepancias salen vacías con todos los códigos «no aplica» y la caché lo
  apunta, de modo que el análisis se recalcula cuando WP-D lo componga.