# ADR-0036 · La vida de la app y la bandeja: cerrar y seguir, el panel como proyección, la salida gestionada en el registro, la suspensión y el reloj

- **Estado:** aceptada · 2026-10-21. Recoge D-14, D-15, D-16 y la parte del escritorio de D-40 de
  [docs/16](../16-ampliacion-plataforma-de-proyectos.md#27-segundo-plano-y-bandeja-del-sistema-sección-7), con los
  criterios F8-7 a F8-9 (paquete WP3 de su §4.5). Los contratos son de P0 ([docs/04 §18](../04-contratos.md#18-fase-8-trabajo-desatendido)):
  `RunExitChosen` y su significado están en [ADR-0034](0034-esperas-por-limite-y-reanudacion-con-consentimiento.md)
  (decisión 5), y `IPowerEvents`, `IKeepAwake` y los ajustes `CloseWindow` y `PreventSleepWhileWorking`, en docs/04 §18.5 y
  §18.6. El motor (WP1) lee las transiciones de energía; el bloqueo (WP4), el estado bloqueado. El código está en
  `src/AgentStudio.Desktop/Background/` y `src/AgentStudio.Desktop/Views/Background/`; las pruebas, en
  `tests/AgentStudio.Tests/Desktop/Background/`.
- **Contexto:**
  - **Cerrar la ventana termina la app.** `MainWindow.OnClosing` cierra la terminal, la app sale y el Job Object mata los
    CLIs: lo que estaba en marcha queda interrumpido hasta que lo reanudes. Una ejecución dura de minutos a horas.
  - **WPF no tiene icono de bandeja.** docs/16 D-14 pide decidir, con una medición y sin paquetes de terceros, entre
    `System.Windows.Forms.NotifyIcon` y un envoltorio propio de `Shell_NotifyIcon`.
  - **Nada enumera el trabajo vivo de todos los proyectos.** Las ejecuciones en marcha están en `Orchestrator._live` y los
    documentos del orquestador en `MainViewModel._orchestrators`, los dos privados; `IProjectActivity` dice qué ocupa un
    proyecto, pero no cuáles hay ni el id de lo que lo ocupa. El panel, el tooltip, la salida y «mantener despierto»
    necesitan la misma lista.
  - **El monitor de cada ejecución ya sabe casi todo** (fase, tareas, motivo de bloqueo, contexto por tarea, aprobaciones
    pendientes, ejecuciones de agente usadas) y es el único lector de los eventos de su ejecución: no admite un segundo.
  - **`Microsoft.Win32.SystemEvents.PowerModeChanged` no traduce `PBT_APMRESUMEAUTOMATIC`**: un despertar sin nadie delante
    (un temporizador de Windows) no llega como `Resume`, y es justo el caso del trabajo desatendido. Además, si la app toca
    `SystemEvents` por primera vez desde el hilo de la interfaz, sus eventos llegan en ese hilo.
  - **`SetThreadExecutionState` es del hilo que lo pide:** dura mientras ese hilo vive y solo ese hilo lo quita.
- **Decisión:**
  1. **El icono es `System.Windows.Forms.NotifyIcon`, del framework compartido de escritorio** (`NotifyIconTray`, detrás de
     `ITrayIcon`). `AgentStudio.Desktop.csproj` gana `UseWindowsForms` y quita los dos `using` globales que añade el SDK
     (`System.Windows.Forms` y `System.Drawing`), que harían ambiguos `MessageBox`, `Application`, `Color` o `Point` en todo
     el proyecto; solo `NotifyIconTray` usa Windows Forms. Windows Forms ya resuelve lo que un envoltorio propio tendría que
     escribir y probar: la ventana oculta que recibe los mensajes, volver a añadir el icono cuando Explorer se reinicia
     (`TaskbarCreated`), traer su ventana al frente antes de abrir el menú para que se cierre al hacer clic fuera, y el
     límite de 127 caracteres del tooltip. El menú (`ContextMenuStrip`) se pinta con los colores del tema
     (`ProfessionalColorTable`), se construye de nuevo cada vez que se abre y se maneja con el teclado; la imagen del icono
     se dibuja al arrancar (una «A» sobre el color de acento, al tamaño del icono pequeño del sistema), así que no hay ningún
     fichero de imagen nuevo.
  2. **Cerrar la ventana sigue `CloseWindowPolicy`** (la tabla completa está probada):

     | Ajuste `CloseWindow` | Sin trabajo | Con trabajo |
     |---|---|---|
     | «Preguntar» (por defecto) | Sale | Pregunta: seguir en la bandeja, pausar y salir al terminar, salir ahora o cancelar |
     | «Seguir en la bandeja» | Oculta la ventana | Oculta la ventana |
     | «Salir» | Sale | La salida gestionada pregunta (sin «seguir en la bandeja») |

     La ventana principal ya no se cierra nunca: se oculta, y la app solo termina con la salida gestionada
     (`Application.ShutdownMode = OnExplicitShutdown`). La integración lo pone **solo cuando la bandeja ya arrancó**
     (`UnattendedShell`, al quedar la aplicación inactiva tras enseñar la ventana): antes de que aparezca el icono, o si no
     pudiera arrancar, cerrar la ventana sale como antes de la Fase 8, y nunca queda un proceso sin ventana ni icono. Ese
     cierre deja «Salir ahora» en el registro de lo que esté en marcha (`ExitWithoutTray`), y sin la salida gestionada no
     corre ninguna pasada de la reanudación con consentimiento. Si algo falla después de que el icono aparezca, `Start` lo
     deshace todo y la app sigue como sin bandeja. Al
     ocultarse en la bandeja, la ventana oculta también sus paneles flotantes (Windows solo oculta las ventanas de otra al
     minimizarla, no al ocultarla), y los devuelve al volver. Con la interfaz bloqueada, cerrar la ventana no pregunta ni
     sale, sea cual sea el ajuste: la ventana va a la bandeja.
  3. **Un registro del trabajo en marcha, que alimenta el shell** (`BackgroundWorkRegistry`): cada documento «Orquestador»
     registra su monitor (`RunWork`, que sigue las ejecuciones de ese documento una tras otra), cada debate que se abre o se
     reanuda su `IDebateRun` (`DebateWork`, por su snapshot), y cada proyecto abierto su actividad (`ProjectActivityWork`,
     para las auditorías, las planificaciones y lo que ninguna pantalla sigue aquí). Un proyecto ocupado por algo que ya se
     sigue no cuenta dos veces. Lo que no tiene eventos propios (un debate, la actividad) se lee de nuevo cada 3 segundos.
  4. **El tooltip resume en 127 caracteres como mucho** (`TrayToolTip`), con recuentos y horas y sin ningún nombre de
     proyecto ni de ejecución: «Agent Studio · 2 ejecuciones · 1 espera cuota hasta las 14:05». Si no cabe, se descarta
     primero lo menos importante (lo pausado, los recuentos de cada clase de trabajo, las esperas de cuota y los bloqueos),
     y lo último que se pierde es lo que espera por ti.
  5. **Clic izquierdo, el panel; botón derecho, el menú** (`TrayController`): abrir la ventana, ver el panel, abrir un
     proyecto reciente o una carpeta, abrir los registros (`%LOCALAPPDATA%\AgentStudio\logs`), pausar, reanudar o detener
     cada ejecución (los mismos comandos del monitor), bloquear (WP4) y salir. Con la interfaz bloqueada (D-13) el menú no
     nombra ningún proyecto ni deja tocar el trabajo, abrir los registros ni salir: eso espera a que desbloquees.
  6. **Avisos sin datos del proyecto:** una ejecución que termina, que pide una aprobación o que deja de esperar cuota, y un
     debate que termina, con un título y un texto; nunca el nombre del proyecto, el id de la ejecución ni la acción.
  7. **El panel es una proyección** (`StatusPanelViewModel`), y nada se calcula aparte:

     | Dato | De dónde sale |
     |---|---|
     | Fase, tareas por estado, bloqueos con su motivo, aprobaciones pendientes, errores | El monitor de la ejecución |
     | Contexto de cada tarea en curso | La línea del monitor, tal cual; la barra, del mismo paso (`MonitorTaskViewModel.LastContext`) con el mismo evaluador y los mismos umbrales. Sin porcentaje no hay barra, nunca una a cero |
     | Orquestador, juez y agentes en marcha, con proveedor y modelo | El reparto congelado en la cabecera de la ejecución (`IRunRegistry`); el modelo informado, de los intentos terminados (`IUsageReport`) |
     | Tokens con su origen | `IUsageReport`, filtrado a la ejecución: solo los intentos terminados («el intento en curso cuenta cuando termina») |
     | Presupuesto | Ejecuciones de agente usadas (el monitor) frente a `MaxAgentRuns` (la cabecera) |
     | Cuotas con reinicio y antigüedad | `QuotaViewModel` sobre `IQuotaObservations`, lo mismo que «Consumo»; una vez, porque la cuota es de la cuenta |
     | Debates | Su snapshot: fase, ronda, turnos en curso, participantes, tokens, presupuesto y lo que espera |
     | Auditorías y planificaciones | Solo lo que dice `IProjectActivity` |

     Lo que no se puede leer dice «sin dato» o «no expuesto», con el motivo. Bloqueado, el panel solo dice cuánto sigue en
     marcha y cuánto espera por ti.
  8. **La salida gestionada escribe primero y sale después** (`ManagedExit`): con trabajo en marcha pregunta
     (`IExitPrompt`); con tu respuesta, y después de confirmar los cambios sin guardar del editor, escribe `RunExitChosen`
     en el registro de cada ejecución y de cada debate (por `IRunExitRecorder`, sobre `IDecisionRecorder`, nunca en el
     motor), firmado por `user` y con `DecidedBy` = `user`. Después:
     - «Pausar y salir cuando terminen los intentos en curso»: pausa cada ejecución (nada nuevo se despacha), oculta la
       ventana y espera en la bandeja a que ninguna tenga intentos en vuelo; lo que no se puede pausar (un debate, una
       auditoría) se espera hasta que termina. Un intento en vuelo es el que corre y también el que el motor ya despachó y
       aún no lanzó (la tarea sigue pendiente, con su cola puesta, mientras su trabajador prepara el worktree o entrega ya
       el prompt al CLI). Mientras espera, el tooltip dice cuántos intentos quedan y el menú ofrece «Salir ahora» (escribe
       `ExitNow` en lo que siga en marcha) y «No salir» (vuelve la ventana; las ejecuciones siguen en pausa).
     - **Abrir la ventana mientras espera es «No salir»:** todo lo que el escritorio hace para enseñarla (el menú de la
       bandeja, sus avisos, «Abrir Agent Studio» del panel, y lo que haga el shell con `BackgroundHost.ShowWindow`) pasa
       por `ManagedExit.ShowWindow`, que cancela la salida pendiente y lo avisa. Así nada de lo que hagas con la ventana
       de vuelta (editar, empezar otra ejecución) lo corta una salida que ya no esperas.
     - «Salir ahora y dejarlas interrumpidas»: sale enseguida; lo interrumpido se reanuda reproduciendo el registro cuando
       tú lo pidas. Desde que eliges, la app ya está saliendo: un segundo «Salir ahora» o un «No salir» mientras se escribe
       la elección no cambian nada.
     - «Cancelar» no escribe ni toca nada; «Seguir en la bandeja» oculta la ventana y no escribe nada.
     - **Se escribe en lo que se preguntó y sigue igual.** El trabajo se lee al preguntar, en el hilo de la interfaz, y la
       elección solo va al que, tras la respuesta, sigue en marcha y es la misma ejecución: una que terminó con la pregunta
       abierta no tiene nada que dejar, y la que su documento siga ahora no se preguntó. Cada escritura va aparte: un
       registro que no se puede escribir no detiene a los demás ni la salida, y queda en el log de la app. Una ejecución cuyo
       registro no verifica tampoco se puede reanudar, la pida quien la pida.
     - Una auditoría en marcha no tiene id en el escritorio: se lista y se espera, pero no se escribe nada en ella (nunca se
       reanuda sola).
     - **El cierre de sesión o el apagado de Windows** (`RecordSessionEnded`, desde `Application.SessionEnding`) escribe
       `SessionEnded`, firmado por el sistema (`agent-studio`) con `DecidedBy` = `windows`, en un hilo aparte con lo leído en
       el de la interfaz y esperando como mucho el tiempo que se le dé; sin entrada, cuenta igual (ADR-0034). **Solo lo
       escribe en lo que nadie decidió:** si la app ya está saliendo, la elección de la persona lo cubre todo; y en lo que la
       persona decidió en esta sesión de la app (por ejemplo, «Pausar y salir…» y después «No salir», o mientras espera para
       salir), su elección sigue en vigor y no se escribe un `SessionEnded` que le devolvería la reanudación automática
       (docs/04 §18.2).
  9. **La suspensión, con la notificación de energía de Windows y los relojes del núcleo** (`WindowsPowerEvents` detrás de
     `IPowerEvents`): `PowerRegisterSuspendResumeNotification` con `DEVICE_NOTIFY_CALLBACK` (Windows 8 y posteriores, sin
     ventana ni administrador) traduce `PBT_APMSUSPEND` a `Suspending` y tanto `PBT_APMRESUMEAUTOMATIC` como
     `PBT_APMRESUMESUSPEND` a `Resumed`, y solo se cuenta el primer despertar tras cada suspensión. El tiempo dormido es lo
     que avanzó `GetTickCount64` (cuenta el sueño) menos lo que avanzó `QueryUnbiasedInterruptTime` (no lo cuenta): no
     depende del reloj de pared, que Windows puede corregir al despertar, ni de lo que haga `Environment.TickCount64` en otra
     versión de .NET. Sin la suspensión vista, `Slept` es nulo. Cada evento se marca al recibirlo y entra en una cola con un
     solo lector (`System.Threading.Channels`), que ejecuta los manejadores en un hilo propio, uno tras otro y en el orden en
     que Windows los dio: un despertar nunca llega antes que la suspensión que lo precede. Un manejador que falla se anota y
     no impide a los demás. Si la llamada de registro falla o lanza, la memoria de sus parámetros se libera antes de salir.
  10. **Mantener despierto desde un hilo propio** (`ExecutionStateThread` y `KeepAwakeService`, detrás de `IKeepAwake`):
      `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` con el primer préstamo y `ES_CONTINUOUS` solo con el
      último, siempre desde el mismo hilo, que al terminar también lo quita. Solo `ES_SYSTEM_REQUIRED`: la pantalla se puede
      apagar y tú puedes suspender el equipo. Un 0 de `SetThreadExecutionState` es una negativa de Windows, nunca un éxito
      callado: llega a quien pidió y `KeepAwakeService` la anota en el log de la app (el préstamo se cuenta igual, para que
      su liberación cuadre). `KeepAwakeWhileWorking` lo pide solo con el ajuste `PreventSleepWhileWorking` y mientras el
      registro tenga trabajo, y lee el ajuste de nuevo cuando se guardan los ajustes.
- **Medición** (2026-10-21, este equipo, Windows 11 10.0.26300, SDK 10.0.303):

  | Qué | Antes | Después |
  |---|---|---|
  | Salida de Desktop (`bin\Debug`, mismos recursos) | 266 ficheros, 100 582 880 bytes | 266 ficheros, 100 587 368 bytes (+4 488: el ensamblado y su `deps.json`) |
  | `runtimeconfig.json` | `Microsoft.NETCore.App` y `Microsoft.WindowsDesktop.App` | Igual: Windows Forms viene en el mismo framework compartido |
  | Crear el icono y su menú por primera vez (programa de prueba en Release, icono nunca visible, 3 ejecuciones en frío) | — | 330 a 424 ms; 15 ensamblados del framework (`System.Windows.Forms`, `System.Windows.Forms.Primitives`, `System.Drawing.Common`, `System.Private.Windows.Core`, `System.Private.Windows.GdiPlus`, `Microsoft.Win32.SystemEvents`…); +2,9 a 4,0 MB privados y unos 17 MB más de conjunto de trabajo, casi todo páginas compartidas de las DLL |

  La integración crea el icono después de enseñar la ventana, para no retrasar el primer pintado.
- **Alternativas:**
  - **Un envoltorio propio de `Shell_NotifyIcon`** (`NOTIFYICONDATAW`, una ventana oculta de primer nivel —una de solo
    mensajes no recibe `TaskbarCreated`—, `NOTIFYICON_VERSION_4`, el menú en WPF con el truco de `SetForegroundWindow`):
    evita cargar Windows Forms, pero son unas 300 líneas de interoperabilidad que habría que probar sin poder enseñar el
    icono, para un coste medido de una vez al arrancar. Queda documentado por si el arranque importa más que hoy.
  - **Un paquete de terceros** (H.NotifyIcon.Wpf, Hardcodet.NotifyIcon.Wpf): D-14 pide no añadir paquetes de terceros.
  - **`SystemEvents.PowerModeChanged`:** pierde el despertar automático y entrega en el hilo que lo tocó primero.
  - **Entregar cada evento de energía por su cuenta en el pool de hilos:** un despertar podría llegar a un manejador antes
    que la suspensión que lo precede; la cola con un solo lector mantiene el orden.
  - **El tiempo dormido por el reloj de pared:** cambia cuando Windows corrige la hora al despertar.
  - **Un panel con cifras propias** (contar tokens de los eventos, estimar el contexto): sería un segundo cálculo que
    puede discrepar del monitor y de «Consumo»; el panel solo proyecta.
  - **Escribir la salida desde el motor:** el motor es de WP1 y no sabe qué elegiste; la entrada la escribe quien preguntó,
    por el único escritor del registro.
  - **Esperar a que terminen los debates o abortarlos al salir:** un debate no se puede pausar; con «Pausar y salir…» se
    espera a que acabe, y quien no quiera esperar elige «Salir ahora».
- **Consecuencias:**
  - Desktop usa Windows Forms en un solo fichero, y las pruebas lo cargan al construir el icono real, que nunca se hace
    visible (`NotifyIconTrayTests`).
  - **Integración (hecha, 2026-10-29).** `UnattendedShell` (en `Desktop/Composition`) lo pone todo junto en el hilo de la
    interfaz, tras enseñar la ventana: crea `BackgroundHost` con la confirmación de los cambios sin guardar, las acciones
    del menú (los proyectos recientes de los ajustes, «Abrir carpeta…» y «Bloquear», que ejecuta el comando de la pantalla de
    bloqueo) y la suspensión de las reanudaciones; arranca la bandeja y, solo si arrancó, pasa la ventana a la salida
    gestionada; vuelve a leer «mantener despierto» cada vez que se guardan los ajustes (`SettingsChanged`, desde
    `UnattendedShell` y no desde `MainViewModel`); y arranca el bloqueo por inactividad. `App` llama a su
    `SessionEnding` y lo libera al salir. `MainWindow.OnClosing` cancela el cierre y pregunta a
    `ManagedExit.WindowClosingAsync` salvo que la app ya esté saliendo, y solo entonces guarda el diseño y cierra la
    terminal. `MainViewModel` registra el monitor de cada «Orquestador», los proyectos de «Debates» y «Auditoría», y cada
    debate que sigue un documento «Debates» (abierto, impugnado o reanudado: un solo evento, `DebateFollowed`). La única
    vez que el shell enseña la ventana por su cuenta es al arrancar; después, siempre con `BackgroundHost.ShowWindow`.
  - **La reanudación sola en la bandeja** (WP1, D-39). Cada proyecto tiene una pasada del planificador la primera vez que
    se abre en la sesión (al arrancar, el último que tenías abierto), solo si ya tiene su base de datos. El planificador
    anuncia cada ejecución que reanuda en su propio hilo: el shell la mete en el registro en ese momento, sin el hilo de la
    interfaz (`BackgroundWorkRegistry.TrackFromAnyThread`, una cola que lee el hilo de la interfaz), y después el monitor del
    documento «Orquestador» de su proyecto la sigue, aunque no abrieras la pestaña: así tiene su sitio en la bandeja, su pausa
    y su `RunExitChosen`. Si ese monitor ya sigue otra, la sigue un monitor propio que solo ve la bandeja. **La salida
    gestionada suspende las reanudaciones** en el acto (`ResumeScheduler.Suspend()`) antes de leer el trabajo para preguntar,
    y espera a la pasada en curso (`WaitForIdleAsync`). Las libera si te quedas: «Cancelar», «Seguir en la bandeja», «No
    salir» o la ventana vuelta a enseñar mientras espera, a los intentos o a esa pasada (entonces no pregunta nada). No usa
    `SuspendAsync`, que no devuelve la suspensión si se cancela su espera. `SessionEnding` toma una suspensión antes de
    escribir nada, espera a la pasada con su tiempo límite y no la libera nunca. Así una ejecución que la pasada en curso
    reanuda entra en la pregunta, y ninguna se reanuda una vez elegiste.
  - **Sin salida gestionada no hay pasadas** (revisión de la integración, 2026-10-29). `ConsentedResumes` empieza retenido y
    `UnattendedShell.Start` lo suelta (`Enable`) como último paso, cuando la bandeja arrancó; una pasada retenida, o que una
    suspensión recortó, no cuenta como hecha y se vuelve a pedir con `Available`. Sin esto, una ejecución reanudada antes
    de la bandeja se cerraba sin `RunExitChosen` y el planificador leía tu cierre como uno que nadie pidió.
  - **Ninguna ejecución sin monitor.** Una pestaña ocupada («Pedir plan», «Ejecutar», «Reanudar») no toma una ejecución
    reanudada: la sigue un monitor propio que ve la bandeja. Si el monitor de la pestaña se ocupó mientras el motor
    arrancaba o reanudaba la tuya, la pestaña se la pasa al shell (`RunHandedOff`) en vez de lanzar un error. Y si ningún
    monitor pudo tomar una ejecución reanudada, su anuncio sigue en el registro.
  - **El bloqueo retira las preguntas de la salida:** `BackgroundHost.IsLocked` llega a `ManagedExit`, que cancela su
    pregunta y la confirmación de los cambios sin guardar (retiradas, son «Cancelar»: no escriben nada y sueltan la
    suspensión), y no pregunta mientras dure. La pregunta de los cambios sin guardar es ahora una ventana con el tema, que
    el bloqueo cierra en cualquier sitio donde se abra.
  - **El consentimiento es de cada ejecución:** la casilla «Reanudar sola si un límite o un cierre la interrumpe», junto a
    «Ejecutar», está desmarcada por defecto y vuelve a estarlo tras cada inicio (ADR-0034); si el inicio falla, se queda como
    la dejaste.
  - **Una orden que la ejecución no aplica se dice:** pausar o reintentar una ejecución que ya terminó o se paró falla en el
    motor; el monitor lo dice en castellano, y la bandeja avisa («No se pudo pausar…», sin nada del proyecto), también si la
    ejecución dejó de admitir la orden entre abrir el menú y el clic. Una pausa que no llega al registro dejaría que la
    ejecución se reanudara sola tras un cierre.
  - **«Pausar y salir…» pausa con el comando del monitor**, que solo admite `Running` y `WaitingForQuota`: una ejecución en
    `AwaitingHuman` o `Blocked` no se pausa. Se espera a sus intentos en curso (en esas fases, normalmente ninguno); si
    después despachara algo nuevo, la app esperaría también a eso.
  - **Qué lee la regla de reanudación (WP1, D-39).** El `RunExitChosen` de la persona (`PauseAndExitWhenIdle` o `ExitNow`)
    es la señal de consentimiento de la salida: retira la reanudación automática de esa ejecución, y un `SessionEnded`
    posterior no se la devuelve (docs/04 §18.2; el escritorio, además, no lo escribe en lo que la persona ya decidió en la
    sesión). `RunDispatchChanged` solo registra la pausa y la reanudación del despacho que pide una persona desde el monitor
    o la bandeja: si la pausa del monitor lo escribe también cuando «Pausar y salir…» pausa la ejecución, esa entrada no
    añade nada a la decisión, que ya tomó el `RunExitChosen`.
  - **«No salir» deja la entrada escrita:** una ejecución que pausaste para salir y luego reanudaste sigue teniendo
    `PauseAndExitWhenIdle` como elección de la persona, y un `RunDispatchChanged` de reanudación no la cambia; si después la
    app se cierra sin que lo pidas, no se reanuda sola. Es el lado conservador: nunca gasta cuota que no hayas vuelto a
    consentir.
  - **El panel lee una propiedad nueva del monitor** (`MonitorTaskViewModel.LastContext`, de solo lectura), fuera de la
    carpeta del paquete: ningún otro paquete de la Fase 8 toca ese fichero.
  - **Los ajustes `CloseWindow` y `PreventSleepWhileWorking` tienen pantalla:** «Trabajo desatendido», en «Ajustes» (documento
    «Consumo»), con su «Guardar»; los escribe como preferencias. La app lee el cierre en cada cierre y «mantener despierto»
    cuando se guardan los ajustes.
  - **Sin verificar** (docs/16 §8): el icono y sus avisos en la compilación 26300 sin identidad de paquete (las pruebas no
    enseñan ninguno); `SetThreadExecutionState` y la notificación de suspensión en equipos con espera moderna (S0ix), y si
    allí llegan los dos eventos de reanudación; cuánto tiempo deja Windows a `SessionEnding`; la posición del panel con la
    barra de tareas arriba o a un lado, o con varios monitores; y el menú con un lector de pantalla. La demostración de
    [docs/03 §8](../03-pantallas-y-navegacion.md#lista-de-comprobación-de-la-demostración-de-la-bandeja-y-la-salida-fase-8)
    recorre lo que se puede comprobar a mano.
- **Adenda (2026-10-30, tanda 1).** La decisión no cambia. Cambia esto:
  - **Los ajustes tienen documento propio.** `CloseWindow` y `PreventSleepWhileWorking` están en la sección «Trabajo
    desatendido» del documento «Ajustes» (el último icono de la barra lateral), no en «Consumo». Un solo «Guardar» escribe
    las tres secciones a la vez. Con cambios sin guardar en «Ajustes», salir pregunta por ellos como por los del editor;
    si «Guardar» falla en esa pregunta (una sección no vale o el fichero no se puede escribir), la app no sale: la ventana
    vuelve con «Ajustes» abierto y el motivo, por `ManagedExit.ShowWindow`, que también cancela una salida que esperaba.
  - **El nombre del icono.** Windows guarda como nombre del icono el texto con el que se añade: el que lista
    «Configuración» y el que lee UI Automation. Antes se quedaba con el primer tooltip. Ahora `NotifyIconTray` añade el
    icono con «Agent Studio» y pone el tooltip después.
  - **Una segunda ventana oculta para `TaskbarCreated`.** Cuando Explorer se reinicia, Windows Forms vuelve a añadir el
    icono con el tooltip que tenga. Una `NativeWindow` propia (`TaskbarRestarts`, dentro de `NotifyIconTray`), creada la
    primera vez que se enseña el icono, oye el mismo mensaje y lo vuelve a añadir con su nombre.
  - **Windows Forms sigue solo en `NotifyIconTray`.** La colocación de la ventana lee los monitores con user32
    (`ScreenWorkAreas`), no con `System.Windows.Forms.Screen`.
  - **Textos del menú bloqueado:** «desbloquea para verlo» con un trabajo y «para verlos» con varios.
  - **La colocación de la ventana** (tamaño, posición y si estaba maximizada) se guarda, como el diseño de paneles, solo al
    salir de verdad, nunca al ocultar la ventana en la bandeja (`SaveWindowPlacement`, docs/04 §18.6).
  - **El foco inicial.** El panel de la bandeja y la pregunta de salir, abiertos desde la bandeja, se activan desde fuera
    de la app, y WPF daba el teclado a la ventana y no a un control. Ahora lo recibe su primer control (en la pregunta,
    «Cancelar»), así que `Esc` e `Intro` responden enseguida (B5/B9; [docs/12 §9](../12-accesibilidad-tema-oscuro.md#9-ampliación-de-la-tanda-1-2026-10-30)).
- **Adenda (2026-11-03, tanda 2).** La decisión no cambia: el panel sigue siendo una proyección. Cambia esto:
  - **La barra del contexto está siempre** (P3 de [docs/18](../18-peticiones-y-pendientes.md)). La tabla de la decisión 7
    decía «sin porcentaje no hay barra». Ahora cada tarea en curso con agente tiene su barra, la del control común
    (`MetricBar`). Sin porcentaje, la pista se dibuja vacía y las palabras dicen por qué: «no expuesto», «sin observaciones
    todavía» o los tokens sin ventana. Nunca es una barra a cero.
  - **La barra sale del seguimiento del intento**, no solo del último paso: el panel lee `MonitorTaskViewModel.ContextBar`,
    que el monitor construye con su `ContextHealthTracker` (ADR-0021, adenda del 2026-11-03).
  - **También son barras** el presupuesto (ejecuciones usadas frente a `MaxAgentRuns`), cada ventana de la cuota y los
    tokens de un debate frente a su límite. Si falta el máximo, el texto lo dice.
  - **El registro de la app dice por qué se suelta «Mantener despierto»** (V26): «ya no hay trabajo en marcha», «se desactivó
    el ajuste…» o «Agent Studio se cierra». Antes repetía por qué se había pedido.
