# ADR-0015 · Lienzo del orquestador con Nodify

- **Estado:** aceptada · 2026-10-15. Cierra la decisión D6 de [02](../02-arquitectura.md#7-decisiones-y-alternativas)
  con el spike que pedía [13](../13-fase-3-orquestacion.md) (§10, WP6). Arnés y resultados en
  [`spikes/nodify/`](../../spikes/nodify/).
- **Contexto:** D6 elegía Nodify (WPF nativo) con React Flow en WebView2 de respaldo, «a decidir con un spike». El lienzo
  de F3-6 debe mostrar los 7 tipos de `WorkTaskKind` (la Condición con dos salidas Sí/No y Debate deshabilitado hasta la
  Fase 5), colorear los 8 estados (`Skipped` llega con P0), rechazar los ciclos enseñando el camino de
  `DependencyCycleException.Cycle`, usar los tokens del tema oscuro, poder usarse con teclado y ir fluido con un plan de
  hasta 20 tareas (`PlanLimits.MaxTasks`).
- **Decisión:**
  1. **Nodify 7.3.0** (MIT, WPF nativo, sin dependencias) para el lienzo. React Flow queda como respaldo documentado, no
     se construye.
  2. ViewModels puros con CommunityToolkit.Mvvm; la vista son solo bindings a `NodifyEditor` (`CanvasView.xaml.cs` no
     tiene lógica).
  3. **El dominio decide los ciclos.** El lienzo reconstruye un `TaskGraph` desde sus nodos y cables y llama a
     `AddDependency` dentro de un `try`; así lienzo y validador no pueden discrepar.
  4. **El tema sale de los tokens de la app** con `NodifyThemeTokens.Apply`, como ya se hace con AvalonDock
     (`DockThemeTokens`).
  5. La Fase 3 no promete lienzos de más de 20 nodos (el validador los rechaza); los 200 del spike son margen.
- **Alternativas:**
  - **React Flow en WebView2** (`@xyflow/react` 12.12.0, MIT, publicada el 2026-09-24, 14,3 M de descargas semanales,
    repositorio con push el 2026-10-15 y equipo detrás). Mejor mantenido y con más funciones de serie. Descartado por el
    puente JS↔C# en cada interacción (crear, conectar, borrar, seleccionar, comprobar ciclos contra el dominio C#), por el
    doble modelo de estado, y porque tema, teclado y accesibilidad habría que rehacerlos en CSS/DOM. Sigue siendo viable:
    la app ya aloja Monaco y xterm.js en WebView2.
  - **Lienzo propio** sobre `Canvas`/`ItemsControl`: pan, zoom, selección, conexión y navegación por teclado son semanas
    de trabajo que Nodify da en 274 KB.
  - **Controles de diagramas comerciales:** no evaluados (licencia de pago).
- **Consecuencias:** WP6 parte de patrones ya probados (más abajo). Riesgo principal: **un solo mantenedor**; se
  mitiga con la licencia MIT (se puede vendorizar), una superficie de uso pequeña y un lienzo (`CanvasViewModel`,
  `PlanGraph`) que no depende de Nodify, de modo que cambiar de librería solo reemplazaría la vista.

## Evidencia: licencia y mantenimiento (consultado el 2026-10-15)

| Aspecto | Nodify | Fuente |
|---|---|---|
| Licencia | MIT (`license type="expression"` del nuspec y `LICENSE` del repositorio) | nuget.org, GitHub |
| Versión | 7.3.0, publicada el 2026-04-23; 37 versiones; 135 512 descargas | nuget.org |
| .NET 10 | `net10.0-windows7.0` en el nuspec; `master` compila `net10.0-windows` | nuspec, `Nodify.csproj` |
| Peso | `Nodify.dll` de 274 KB; paquete de 1 MB; sin dependencias de NuGet | restauración local |
| Actividad | último push 2026-06-10 (documentación); última release 2026-04-23; ritmo irregular (siete meses entre 7.1.0 y 7.2.0) | GitHub |
| Mantenimiento | 607 commits del autor y 4 el segundo (**bus factor 1**); 20 issues y 14 PR abiertas; 135 issues cerradas | GitHub |
| Issues que nos rozan | #281 y #284 (desplazamientos de cables en orientaciones y modos que no usamos); #285 (cuatro ajustes que un camino de código ignora: `IsSelectable` con `SelectArea`, `DisablePanning`...); #292 (alimentar la selección desde `CollectionChanged` lanza en 7.3.0). Abiertas desde julio con 0, 1, 1 y 0 comentarios | GitHub |

## Evidencia: lo verificado con el arnés

El arnés (`spikes/nodify/`, fuera de `AgentStudio.slnx`) ejecuta la aplicación WPF real **fuera de pantalla** y la maneja
con **mensajes de ventana** de ratón y teclado enviados a su propio `HWND` (`SendMessage`). WPF los procesa como entrada
de dispositivo (`HwndMouseInputProvider`, `MouseDevice`, eventos enrutados y las máquinas de estado de Nodify), así que
los gestos son los de un usuario y el arnés no llama a métodos del ViewModel para simularlos; pero no se mueve el cursor,
no se pulsa ninguna tecla real y no aparece nada en la pantalla. Las capturas son `RenderTargetBitmap` del lienzo a
120 ppp (pantalla al 125 %, 1859 × 984 px). Ejecución completa: **71 comprobaciones, 71 correctas, ninguna etapa omitida**
([`results/spike-results.json`](../../spikes/nodify/results/spike-results.json) y su `.log`).

| Función | Cómo se comprobó | Resultado |
|---|---|---|
| Crear los 7 tipos | clic en cada botón de la paleta | 6 nodos con ids únicos; Debate aparece como «Debate · Fase 5», deshabilitado, y su clic no crea nada |
| Conectar arrastrando | 6 arrastres salida → entrada | 6 cables, incluida la unión de dos cables en una entrada; las ramas Sí y No salen de conectores distintos |
| Rechazar | cable repetido, salida → salida, hacia el nodo Debate, a sí mismo, ciclo | ningún cable creado; Debate explica «disponible en la Fase 5»; el ciclo y la auto-conexión dan el camino |
| Ciclo visible | T5 → T1 con T1 → T2 → T3 → T5 existente | durante el arrastre, hilo rojo y «Ciclo: T1 → T2 → T3 → T5 → T1» junto al cursor; al soltar, banner «Conexión rechazada. Ciclo: …» ([captura](../evidencias/fase3-spike-nodify-ciclo.png), [banner](../evidencias/fase3-spike-nodify-rechazo.png)) |
| Borrar en cascada | tecla Supr sobre un nodo con 3 cables | nodo y 3 cables fuera; `IsConnected` coherente en todos los conectores; el grafo del dominio reconstruido es consistente |
| Pan y zoom | arrastre con el botón derecho y rueda | desplazamiento (200, 60) con un arrastre de (−200, −60); zoom 1,00 → 2,00 → 0,50 |
| Selección | banda elástica, clic, Ctrl+clic, clic en vacío, clic en un cable | todas correctas |
| Estados | los 8 botones de la leyenda sobre el nodo seleccionado | cada estado recolorea el borde en vivo y cambia el texto del pie; la [muestra](../evidencias/fase3-spike-nodify.png) los enseña a la vez |
| Teclado y foco | Tab, flechas, Espacio, Escape, Supr | Tab entra en el lienzo, las flechas mueven el foco entre nodos, Espacio selecciona, Escape deselecciona, Supr borra; aro de foco discontinuo de 226 píxeles del color del token ([captura](../evidencias/fase3-spike-nodify-foco.png)) |
| Lector de pantalla | cliente UI Automation real | 12 de 12 nodos expuestos como `DataItem` enfocables con nombre «T1, Trabajo de agente, Implementar Calc.Multiply, Completada» |
| Tema | claves de color, controles vivos y píxeles | 50 claves de Nodify: 46 remapeadas a tokens y 4 transparentes a propósito, ninguna conserva su color original; el fondo del lienzo (`#0A1020`) y la cabecera de nodo (`#16223D`) coinciden con los tokens píxel a píxel |
| Sin efectos en la sesión | ventanas nativas de WPF y primer plano, comprobados en cada etapa | una sola ventana (la aparcada fuera de pantalla), ningún tooltip ni menú; el primer plano, según lo descrito a continuación |

### Cómo se maneja la ventana sin tocar la sesión del usuario

1. **Fuera de pantalla.** La ventana se aparca más allá de todos los monitores, sin botón en la barra de tareas ni en
   Alt+Tab, y se muestra sin activarla. Se comprueba contra el escritorio virtual antes y después de mostrarla, y el
   arnés se niega a arrancar si fuera visible. Los tooltips están desactivados en `--auto` (WPF mantiene los popups dentro
   de un monitor) y cada etapa comprueba que no existe ninguna otra ventana nativa de WPF. Un temporizador de 4 minutos
   cierra el proceso si algo se cuelga y toda salida de error termina el proceso en lugar de dejar un cuadro de diálogo.
2. **Tres detalles para que WPF acepte los mensajes** (leídos en las fuentes de WPF): se llama a `SetCapture` antes de
   cada mensaje (si no, WPF descarta el movimiento como «espurio» al ver que el cursor real está en otra parte); cada
   pulsación va precedida de un movimiento al mismo punto, en la misma ráfaga; y `Mouse.LeftButton`, `Keyboard.Modifiers`
   e `IsKeyDown` leen `GetKeyState`, la tabla de teclas del hilo, que el arnés escribe con `SetKeyboardState`. La
   reconstruye entera en cada llamada porque **los gestos de ratón de Nodify solo coinciden si no hay ninguna otra tecla
   pulsada** (`MouseGesture.Matches`): una tecla Supr olvidada en la tabla hacía que ningún clic posterior sirviera.
3. **Doble clic.** Una repetición de la pulsación en el mismo punto en menos del tiempo de doble clic (500 ms) llega a
   WPF con `ClickCount` 2, y los gestos de Nodify son de clic simple: la ignoran por muy bien entregada que esté. Los
   reintentos del arnés esperan ese tiempo.
4. **Foco de teclado y primer plano.** El foco de teclado de WPF es un `SetFocus` de Win32 dentro del proceso, y Windows
   puede tomarlo como petición de hacer la ventana la ventana en primer plano: el programa del usuario perdería el teclado
   un instante. Solo importa la primera vez (después, mientras la ventana conserva el foco de su propia cola de entrada,
   WPF repite `SetFocus` sin efecto). Medido en cinco ejecuciones (tres guardadas en `results/`, dos de diagnóstico): con
   la última entrada del usuario 0,2 s antes **no hubo cambio de primer plano** (Windows lo rechaza mientras el bloqueo de
   primer plano está activo); con 10 s, 37 s, 539 s y 1415 s sin tocar nada la ventana **sí se hizo con él una vez**, y el
   arnés lo devolvió en 80–170 ms. Por eso el arnés toma el foco (a) justo después de una entrada real del usuario y con
   otro programa en primer plano (no el que lanzó el arnés: un editor o un terminal podrían tomarlo igualmente), o (b)
   cuando nadie ha tocado la máquina en 30 s, que es cuando un cambio devuelto al instante no molesta a nadie. Si ninguna
   de las dos se da en 90 s, omite las etapas que pulsan elementos y lo dice (ejecución incompleta, código de salida 1).
   Los cambios de primer plano que haya habido quedan en el informe (`ForegroundTakeovers`) y la comprobación de sesión
   falla si hubo alguno con el usuario activo.

Limitación: la entrada por mensajes no ejercita el camino del dispositivo (controlador, ratón físico, táctil).

## Evidencia: rendimiento

Release, WPF con aceleración por hardware (nivel de render 2), pantalla al 125 % y 60 Hz (el techo medido es ~59 fps),
Ryzen 7 7730U con Radeon integrada, .NET 10.0.11. **La máquina estaba compartida con otros agentes** (compiladores, tests y
sesiones de Claude activos) en todas las ejecuciones guardadas, y la carga de CPU cambia entre el principio y el final de
una misma ejecución: los números son una cota inferior y se dan como rango de tres ejecuciones con sus cargas
([`spike-results`](../../spikes/nodify/results/spike-results.json): CPU 71 % → 100 % → 100 %;
[`bench-run-1`](../../spikes/nodify/results/bench-run-1.json): 52 % → 90 % → 29 %;
[`bench-run-2`](../../spikes/nodify/results/bench-run-2.json): 36 % → 24 % → 95 %; cada una guarda su `.log`).

| Escenario | Resultado (mín.–máx. de las tres ejecuciones) |
|---|---|
| 12 nodos, primera carga en frío del proceso | 635 ms hasta el primer fotograma con la CPU al 71 % (255 ms en una ejecución de prueba con la CPU al 21 %, no conservada) |
| 20 nodos y 25 cables (límite del validador) | 144–391 ms |
| 200 nodos y 300 cables, primera carga | 1,4–3,4 s (primer fotograma = dibujado completo); recargas: 1,2–8,6 s; +48–120 MB de memoria. Una ejecución anterior, con la máquina más tranquila, dio 0,9 s (no conservada) |
| Dominio con 200 tareas y 300 dependencias | reconstruir el `TaskGraph`: 6,8–13 ms; sondear un ciclo: 3,9–15 ms (media de 20) |
| Pan con los 200 nodos a la vista (zoom 31 %) | **44–58 fps**, mediana 16,7 ms (la base de 12 nodos: 49–60) |
| Pan con arrastre del botón derecho (mensajes) | 37–55 fps |
| Barrido de zoom (×1,8 a cada lado) | 43–56 fps |
| Pan al 100 % (una quinta parte visible) | 46–58 fps |
| Arrastre de un nodo (sus cables lo siguen) | 40–55 fps |
| Rueda (una muesca cada 50 ms) | **30–38 fps**: cada muesca escala ×1,26 y re-rasteriza 200 nodos |

Los mínimos casi siempre son de la ejecución con la CPU al 100 %; con la máquina a medias, el pan con 200 nodos está en el
techo de refresco (57–58 fps). Con 200 nodos Nodify no activa su optimización de renderizado (`OptimizeRenderingMinimumContainers`
es 700); forzarla a 100 no mejoró nada (43–57 fps), porque ya se está en el techo de refresco. La primera carga crece de forma
lineal con el número de nodos y con la carga de la máquina, así que un plan de 20 tareas se dibuja en menos de 400 ms incluso
con la CPU al 90 %. [Captura con 200 nodos](../evidencias/fase3-spike-nodify-200.png).

**Metodología:** mover el viewport desde un manejador de `CompositionTarget.Rendering` obliga a WPF a hacer el layout dos
veces por fotograma y da unos 20 fps falsos con 200 nodos; el mismo lienzo da 57 fps si se mueve desde un bucle del hilo de
UI. El banco solo escucha `Rendering` (`--only exp` reproduce la comparación). Quien escriba pruebas de rendimiento de
WP6 debe evitar el error. Una ventana fuera de pantalla sigue dibujando a 60 Hz en este equipo, así que la medición de
fotogramas es válida.

## Patrones que reutiliza WP6

Todo está en `spikes/nodify/`; los nombres son los del arnés.

1. **Conectores** (`Themes/CanvasStyles.xaml`, `ConnectorViewModel`). Estilos implícitos para `nodify:NodeInput` y
   `nodify:NodeOutput` con `Anchor="{Binding Anchor, Mode=OneWayToSource}"`, `IsConnected="{Binding IsConnected}"`,
   `Header="{Binding Label}"` y `AutomationProperties.Name`. Nodify solo actualiza el ancla de un conector con
   `IsConnected` en `True`; el VM lo calcula contando sus cables (`AddConnection`/`RemoveConnection`). Las salidas Sí/No
   de la Condición se colorean con un `DataTrigger` sobre `Branch`.
2. **Nodo** (`NodeShell`, plantilla de `NodeViewModel`). Cabecera = id y tipo, contenido = título, pie = punto y texto del
   estado; el borde del nodo toma el pincel `Status.<estado>Brush` (`StatusColors.xaml`) mediante un convertidor, y Debate se
   atenúa. El marco de selección es del `ItemContainer` (azul), de modo que un nodo seleccionado enseña los dos. En
   `ItemContainerStyle`: `Location` en `TwoWay` y `AutomationProperties.Name`.
3. **Cables** (`Wire`, `WireTemplate`). `Text` toma «Sí»/«No», el trazo sale de `Branch`, y un cable seleccionado se engrosa
   con un `Trigger` sobre `nodify:BaseConnection.IsSelected` (requiere `BaseConnection.IsSelectable` en `True`).
4. **Conexión pendiente** (`PendingWireTemplate`, `PendingConnectionViewModel`). Dentro del `PendingConnection`,
   `Source` y `PreviewTarget` van **OneWayToSource** al VM, con `EnablePreview`, `EnableSnapping` y `AllowOnlyConnectors`.
   El VM contesta con `PreviewText`/`IsPreviewError`, que se pintan junto al cursor y tiñen el hilo de rojo. La plantilla de
   control propia oculta la etiqueta si no hay texto (la de Nodify dibuja el recuadro siempre que `EnablePreview` está
   activo).
5. **Cierre de la conexión**: `NodifyEditor.ConnectionCompletedCommand`. Su parámetro es una tupla de valor
   `(object Source, object? Target)`. `CanExecute` aplica solo reglas baratas (una salida con una entrada, no Debate, no
   duplicada); **el ciclo se decide en `Execute`**, porque si `CanExecute` lo rechazara Nodify descartaría el cable en
   silencio y no habría mensaje. `DisconnectConnectorCommand` y `RemoveConnectionCommand` cubren Alt+clic y la tecla Supr
   sobre un conector.
6. **Gancho del ciclo** (`PlanGraph.Build/CheckCycle`, `CanvasViewModel.Check`). `TaskGraph` solo crece (no tiene
   `Remove`), así que cada comprobación **reconstruye un grafo nuevo** desde los nodos y cables del VM y llama a
   `AddDependency(dependiente: nodo de la entrada, prerrequisito: nodo de la salida)`; `DependencyCycleException`
   deja el grafo intacto. `Cycle` sigue las aristas «depende de», al revés que las flechas: se invierte para leerlo en el
   sentido de los cables. Es lo mismo que hará `PlanDefinition.BuildGraph`, y el mismo método alimenta el aviso al pasar y
   el rechazo al soltar. La reconstrucción también hace trivial el borrado en cascada.
7. **Tema** (`Theming/NodifyThemeTokens.cs`, `App.xaml`). Orden de mezcla: `Dark.xaml` de la app (en el arnés, el fichero de
   `src/AgentStudio.Desktop/Themes/` enlazado como `Page`, sin copia), `Dark.xaml` de Nodify, `FocusVisual.xaml` de Nodify
   (**no** lo mezcla `Dark.xaml`; sin él no hay aro de foco), colores de estado y estilos del lienzo. Después,
   `NodifyThemeTokens.Apply(Resources, Resources)` en `OnStartup`, antes de la primera ventana: las claves de color escritas
   en el diccionario raíz ganan a cualquier mezcla y los pinceles de Nodify (`DynamicResource`) las leen. Se probó antes un
   `Nodify.xaml` con alias `<StaticResource x:Key="..." ResourceKey="..."/>`: el compilador de XAML de .NET 10 se cae con
   `MC1000` («Index was out of range»), y copiar los hex dejaría el lienzo desacoplado de los tokens.
8. **Selección y viewport.** `SelectedItems` y `SelectedConnections` van a `ObservableCollection`; **no** se debe tocar la
   selección desde su `CollectionChanged` ni enlazar `SelectedItem` en `TwoWay` realimentado desde ahí (issue #292; usar
   `OneWayToSource` en un inspector). `ViewportLocation` y `ViewportZoom` en `TwoWay`, `ViewportSize` en `OneWayToSource`
   (está en unidades del grafo): el centro de la vista es `ViewportLocation + ViewportSize / 2`, donde aparece un nodo nuevo.
   Supr es un `KeyBinding` del editor; Nodify no lo trae para nodos.
9. **Pruebas de interfaz sin tocar la sesión** (`Harness/WindowInput.cs`, `OffScreenHost.cs`, `Screenshot.cs`). Los tres
   ficheros son reutilizables tal cual para probar el lienzo de WP6 en una ejecución de pruebas: entrada por mensajes de
   ventana, ventana fuera de pantalla y capturas con `RenderTargetBitmap`. Valen las cuatro reglas de la sección
   anterior (sobre todo la tabla de teclas y el primer plano).

**Lo que WP6 tiene que cambiar o añadir:**

- Sustituir `NodeStatus` (copia local de 8 valores) por `WorkTaskStatus` cuando P0 aporte `Skipped`.
- Añadir a `Themes/Dark.xaml` tres tokens nuevos para `AwaitingApproval` (`#B692F6`), `Cancelled` (`#A38891`) y
  `Skipped` (`#5E6E8C`); son provisionales y no se ha revisado su contraste. Los otros cinco estados salen de tokens
  existentes.
- Pasar `branch` a `AddDependency` cuando P0 lo traiga. Hoy Sí → X y No → X al mismo destino se funden en una arista del
  `HashSet`.
- Un test de la tabla de `NodifyThemeTokens` (cubre las 50 claves de Nodify; el arnés ya lo comprueba en tiempo de
  ejecución) junto al de `DockThemeTokens`.
- El nombre accesible de los nodos viene de `AutomationProperties.Name`: Nodify no trae peers de automatización propios.
- El VM del lienzo se alimenta de `IOrchestrationRun`: aplicar los eventos con el `SynchronizationContext` inyectado, como
  `TerminalViewModel`.

## Riesgos y lo que no está verificado

- **Mantenimiento:** bus factor 1, ritmo irregular, bugs abiertos. Seguimiento en cada actualización de Nodify; la
  superficie usada es pequeña (`NodifyEditor`, `ItemContainer`, `Node`, `NodeInput`, `NodeOutput`, `Connection`,
  `PendingConnection`, `EditorCommands`) y el respaldo sigue descrito en D6.
- **Rueda a 30–38 fps con 200 nodos:** irrelevante con el límite de 20 tareas, pero WP6 no debe ampliarlo sin medir.
- **Gestos y teclas:** los gestos de ratón de Nodify no coinciden si hay otra tecla pulsada, y los de clic simple ignoran
  el doble clic. WP6 no debe esperar un atajo «mantener tecla + clic» sin probarlo, ni depender del doble clic sobre un
  nodo.
- **No verificado:** el camino real del dispositivo (la entrada fue por mensajes de ventana: ni ratón físico, ni táctil, ni
  más de un monitor con DPI distinto); lector de pantalla real (solo se inspeccionó el árbol UI Automation); números con la
  máquina descargada (todas las ejecuciones guardadas compartieron CPU con otros agentes); la garantía de no tocar el
  primer plano, que se midió en cinco ejecuciones y depende de reglas de Windows no documentadas del todo; el fallo
  `MC1000` solo se reprodujo con el SDK 10.0.303 del `global.json`; contraste de los tres colores nuevos; modo de solo
  lectura para el monitor de ejecución (`IsDraggable`, conectores inertes; el issue #285 toca `IsSelectable` y
  `DisablePanning`); conexión con el teclado (Espacio sobre un conector) y capa de conectores; minimapa, agrupación,
  deshacer y copiar/pegar (fuera de F3-6).

## Cómo repetirlo

```powershell
dotnet build spikes\nodify\NodifySpike.csproj -c Release
spikes\nodify\bin\Release\net10.0-windows\NodifySpike.exe --auto --out docs\evidencias --results spikes\nodify\results\spike-results.json
```

Sin `--auto` abre el lienzo con la muestra pequeña para probarlo a mano. `--only small|input|bench|exp` limita las etapas.
El modo `--auto` no muestra nada, no mueve el cursor y no pulsa teclas; tarda unos 2 minutos, no deja procesos y escribe el
informe JSON, su `.log` y las cinco capturas (`fase3-spike-nodify*.png`). Si hay alguien usando la máquina, hay que mover
el ratón o pulsar una tecla en los primeros 90 s (con otro programa delante que no sea el editor o el terminal desde el
que se lanzó), o dejarla quieta 30 s; si no, el arnés omite las etapas con entrada y termina con código 1 diciendo por
qué. Para repetir solo la medición: `--only bench --results spikes\nodify\results\bench-run-N.json`.
