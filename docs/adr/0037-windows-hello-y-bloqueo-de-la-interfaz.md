# ADR-0037 · Windows Hello como confirmación reforzada de las aprobaciones críticas y un bloqueo de la interfaz que no detiene el trabajo; el SDK de Windows solo en Desktop (amplía ADR-0001)

- **Estado:** aceptada · 2026-10-21. Recoge D-13 de [docs/16](../16-ampliacion-plataforma-de-proyectos.md#26-autenticación-y-bloqueo-del-ide-sección-6),
  F8-10 y F8-11 y la decisión 5, opción (a), de su §7, que aprobaste el 2026-10-20. P0 dejó los contratos en
  [docs/04 §18.4, §18.6 y §18.7](../04-contratos.md#184-presencia-del-usuario-y-aprobaciones-applicationsecurity):
  - el puerto `IUserPresenceVerifier`;
  - el método de `ApprovalDecision` y `ApprovalAnsweredPayload` v2;
  - los ajustes;
  - el TFM.

  Lo implementa WP4:
  - en `src/AgentStudio.Desktop/Lock/`: el verificador, el bloqueo, la inactividad, la pantalla de bloqueo, los ajustes y su registro;
  - en `src/AgentStudio.Desktop/Views/Lock/`, sus vistas;
  - `ApprovalPromptService`, `ApprovalDialogViewModel` y `ApprovalGate`;
  - «Aprobar plan» en el lienzo (`OrchestratorDocumentViewModel` y su revisión en `OrchestratorView`) y su registro en el
    motor;
  - `ApprovalAnswers` en Application: la única forma de escribir una respuesta, que usan la puerta, el motor y
    `DecisionRecorder`.

  Lo cablea la integración de la Fase 8 (2026-10-29):
  - la composición registra `AddWindowsHelloAndLock()` y, como `IWorkInProgress`, `BackgroundWorkInProgress`, que lee el
    registro del trabajo de la bandeja (ADR-0036), así que la pantalla de bloqueo cuenta lo mismo que la bandeja;
  - `OrchestrationUiServices` pasa la `ReinforcedConfirmation` y el `IInterfaceLock` a `ApprovalPromptService`, y la misma
    `ReinforcedConfirmation` a `OrchestratorDocumentFactory`;
  - `ApprovalView` enseña `HelloNoticeView` entre la casilla de confirmación y los botones;
  - `LockSettingsView` está en «Ajustes» → «Trabajo desatendido» (documento «Consumo»), con su «Guardar»;
  - `MainWindow` pone la pantalla de bloqueo en lugar del contenido (oculto y deshabilitado), siguiendo `IInterfaceLock` en
    su propio hilo (al momento si el cambio llega en él; si no, por el dispatcher, leyendo el estado de entonces). Mientras
    está bloqueada se ocultan los paneles flotantes, se cierra la lista «Ir al fichero», se quitan todos los atajos de la
    ventana (`Ctrl+P` no abre nada) y la bandeja y su panel ocultan lo que sigue; al desbloquear vuelve todo, los paneles
    flotantes incluidos;
  - **«Bloquear»** es un botón de la barra superior (no hay barra de menús) y dos atajos: `Ctrl+Alt+L` y `Ctrl+Mayús+B`.
    `Ctrl+Alt` es `AltGr` en muchas distribuciones de teclado: en la española `AltGr+L` no escribe nada, así que `Ctrl+Alt+L`
    también bloquea ahí, pero en una distribución donde `AltGr+L` escribe un carácter (la polaca, «ł») bloquearía en su lugar.
    `Ctrl+Mayús+B` no usa `Alt` y funciona igual en cualquiera; `Ctrl+Mayús+L` no estaba libre (en el editor, Monaco la usa
    para «seleccionar todas las coincidencias», y la ventana se queda los atajos antes que el editor). Monaco no usa ninguno de
    los dos elegidos. En la bandeja, «Bloquear» ejecuta el mismo comando de la pantalla de bloqueo
    (`InterfaceLock.LockAsync` con `LockReason.Manual`, y su resultado en la pantalla);
  - `UnattendedShell` arranca el `IdleLockMonitor` en cuanto arranca la bandeja;
  - `MainWindow` no inicia la salida ni pregunta nada si se cierra bloqueada, y `WpfUnsavedChangesPrompt`, ahora una ventana
    con el tema que recibe el `IInterfaceLock`, no se abre bloqueada y se cierra si el bloqueo llega con ella abierta
    (decisión 4).
- **Contexto:**
  - Las aprobaciones (`IApprovalGate`) fallan cerradas y registran quién decidió, pero un clic en el diálogo basta, también
    uno por error o una entrada que simula otro programa.
  - Windows ya te autentica y bloquea la sesión. Pedir Windows Hello al abrir la app repite lo que Windows acaba de hacer
    (decisión 5 (a): no se pide).
  - Una app de escritorio pide Hello con `UserConsentVerifierInterop.RequestVerificationForWindowAsync` y el HWND de su
    ventana. Esa interoperabilidad es de Windows 11 (compilación 22000) y viene en la proyección del SDK de Windows, que exige
    un TFM con su versión (P0 midió la salida: +26 MB).
  - El editor y la terminal son controles WebView2, que viven en su propia ventana (`HwndHost`): ninguna capa WPF los tapa.
  - En el registro importa quién decide. Un «no» de la persona a abrir un debate de revisión hace que la entrega ignore la
    impugnación (`DeliveryService`); uno del sistema, por una pregunta que caducó, no.
- **Decisión:**
  1. **Confirmación reforzada (F8-11).** Con `WindowsHelloForCriticalApprovals` (activo por defecto), una aprobación cuyo
     riesgo clasificado es crítico pide Windows Hello después de «Aprobar». Hoy son críticas:
     - la entrega;
     - abrir y reanudar un debate, y su resolución humana;
     - la auditoría de un proyecto y aceptar una revisión;
     - los nodos de aprobación crítica de un plan;
     - aceptar un historial como nuevo origen.

     Con la Fase 10 se añade lo crítico del remoto. Lo hace `ApprovalPromptService`, en el hilo de la interfaz, sobre la
     ventana principal, y lee el ajuste en cada pregunta.
     - Solo un «no está» claro de Windows (`NotConfiguredForUser`, `DeviceNotPresent` o `DisabledByPolicy`) deja decidir al
       diálogo solo. El diálogo lo avisa, con el motivo, y la respuesta, sea un sí o un no, lleva `WindowsHelloUnavailable`
       («sin Hello: no disponible»).
     - En cualquier otro caso se pide Hello: `Available`, `DeviceBusy`, un error al preguntar, un valor que Windows no
       documenta o ninguna respuesta en 10 segundos. Esa espera tiene límite porque Windows podría no contestar nunca; la
       pregunta sigue dentro del plazo de la puerta. Solo `Verified` aprueba, con `WindowsHello`; cancelar, agotar los
       intentos o no poder preguntar no aprueba nunca.
     - Un «no» en el diálogo no pide Hello: decide el diálogo (`Dialog`).
     - **«Aprobar plan» en el lienzo sigue la misma regla**, porque la aprobación del plan es crítica:
       - Hello después del clic, y solo una confirmación aprueba;
       - si Windows dice que Hello no está, decide el clic y la revisión del plan lo avisa (`HelloNoticeView`);
       - un clic que Hello no confirma no aprueba nada, así que no arranca ninguna ejecución y el motor no escribe ninguna
         concesión; la pantalla dice por qué.

       La regla está una sola vez, en `ReinforcedConfirmation` (`PlanAsync` antes de preguntar y `ConfirmAsync` tras un
       «sí»), para que el diálogo y el plan no se separen.
  2. **El método en el registro.** `ApprovalGranted` y `ApprovalDenied` llevan el método, en la v2, solo cuando no es `Dialog`.
     - Las respuestas del diálogo solo y las denegaciones que la puerta decide por sus motivos siguen en la v1, byte a byte:
       una v1 es una respuesta del diálogo.
     - Las tres escrituras de una respuesta usan `ApprovalAnswers`, así que escriben lo mismo: la puerta, la aprobación del
       plan en el motor (`Orchestrator`) y la aceptación de un historial (`DecisionRecorder`).
  3. **Un «sí» que Hello no confirma lo deniega el sistema.** El servicio devuelve ese «sí» como una denegación con el método
     `WindowsHello` y el motivo: el resultado de Windows (`Canceled`, `RetriesExhausted`…) o el error. La puerta la registra
     como denegación del sistema (actor `System`, `agent-studio`), con ese motivo y la v2, igual que una pregunta que caduca.
     Hello solo se pide tras un «sí», así que un «no» de la persona nunca lleva ese método.

     **Un «no» nunca pide Hello: es una decisión de diseño aceptada.** Hello confirma lo que se concede, no lo que se
     rechaza. Esto incluye el «no» a `debate.open`: la entrega lo lee como que la persona declinó abrir la revisión y
     descarta esa impugnación (`DeliveryService`, líneas 581–586), así que alguien sentado ante la sesión abierta puede
     descartar una impugnación con un clic. La entrega en sí sigue necesitando Hello. Pedir Hello también para un «no»
     convertiría cada rechazo en un trámite, y a quien está delante le basta, de todos modos, con no aprobar nunca.
  4. **El bloqueo de la interfaz (F8-10)** es manual («Bloquear») o llega tras `IdleLockMinutes` sin entrada en la sesión,
     si `IdleLockEnabled` está activo (desactivado por defecto; 15 minutos).
     - **Oculta el contenido al momento.** La ventana pone la pantalla de bloqueo en lugar del contenido, no encima: así
       tampoco se ven los WebView2.
     - **No detiene nada.** Las ejecuciones, las planificaciones, los debates y las auditorías siguen.
     - **Las aprobaciones esperan.** La que está abierta se retira, porque su ventana es otra que la pantalla no tapa, y se
       vuelve a preguntar al desbloquear, con Windows Hello si se estaba pidiendo. Una respuesta dada mientras se bloqueaba
       no cuenta, y una pregunta a la que el bloqueo alcanza de camino al hilo de la interfaz no abre ninguna ventana.
     - **Las demás preguntas se retiran y no vuelven** (revisión de la integración, 2026-10-29). La pregunta de la salida y
       la de los cambios sin guardar también son ventanas que la pantalla no tapa, y enseñan nombres de proyectos y de
       ficheros: el bloqueo las cierra (`LockWithdrawal`, `ManagedExit.IsLocked`) y cuentan como «Cancelar». No se vuelven a
       hacer al desbloquear, porque no las pidió nadie más que tú, que puedes volver a cerrar o a salir.
     - **Cerrar la ventana no la saca del bloqueo.** La barra de título, `Alt+F4` y «Cerrar ventana» de la barra de tareas
       quedan fuera del contenido oculto: con la interfaz bloqueada, el cierre no pregunta ni sale, y la ventana va a la
       bandeja (sin ella, no hace nada). Un cierre de la sesión de Windows sí sale, y escribe lo de siempre.
     - **La pantalla de bloqueo dice desde cuándo y por qué está bloqueada.** También qué sigue en marcha, contado por tipo
       y sin nombrar proyectos, y qué espera: de cada pregunta, la acción, el riesgo y desde cuándo, nunca la descripción
       ni las rutas.
  5. **El desbloqueo, con Windows Hello o, sin él, con Windows.**
     - Con Windows Hello, solo `Verified` desbloquea.
     - Si Windows dice que Hello no está al bloquear, la app se bloquea y bloquea también Windows (`LockWorkStation`).
     - Si lo dice al desbloquear, la pantalla ofrece «Bloquear Windows»; la ofrece siempre.
     - El desbloqueo de Windows desbloquea la interfaz **solo si la app pidió ese bloqueo de Windows mientras estaba
       bloqueada**: entonces Windows acaba de verificar a la persona.
     - Un desbloqueo de Windows que la app no pidió (Win+L, por ejemplo) la deja bloqueada.
     - Agent Studio nunca pide la contraseña de Windows, y solo le llega el resultado: ni datos biométricos ni el PIN.
  6. **La inactividad se mide en toda la sesión** (`GetLastInputInfo`): trabajar en otra app no cuenta como inactividad.
     - Cuenta como mucho desde el último desbloqueo, así que nunca vuelve a bloquear al momento de desbloquear. Ese tiempo se
       mide con las marcas de tiempo de `TimeProvider`, nunca con el reloj de pared: un reloj atrasado tras desbloquear no
       deja la interfaz sin bloquear. Es el reloj del sistema, cuyas marcas cuentan una suspensión: un equipo que durmió es un
       equipo que nadie usó, y bloquea al despertar. El reloj que se para mientras duerme (`AwakeTimeProvider`) es solo del
       motor de orquestación (integración de la Fase 8; docs/04 §18.5).
     - No bloquea lo que ya está bloqueado: sin Hello, bloquea Windows una sola vez.
     - Si Windows no dice cuándo fue la última entrada, no bloquea.
     - Comprueba cada 10 segundos y lee los ajustes en cada comprobación.
     - Preguntar si Hello está tiene el mismo límite de 10 segundos: un Windows que no contesta deja la interfaz bloqueada y
       no para el bloqueo por inactividad durante el resto de la sesión.
  7. **El TFM (amplía [ADR-0001](0001-wpf-y-dotnet-10.md)).**
     - Solo Desktop y el proyecto de pruebas, que lo referencia, apuntan a `net10.0-windows10.0.22000.0`.
     - El dominio, Application y los módulos siguen en `net10.0`.
     - El puerto `IUserPresenceVerifier` vive en Application y no conoce ventanas ni tipos de Windows.
     - Los resultados de WinRT se traducen uno a uno, por nombre (`UserPresenceMapping`), y un valor que Windows no documente
       hoy nunca pasa por uno conocido.
  8. **La interoperabilidad nativa de Desktop es `DllImport`** (`LockWorkStation` y `GetLastInputInfo`), no
     `LibraryImport`: su generador necesita código `unsafe`, que el proyecto de Desktop no permite, y estas firmas son
     *blittable* sin él.
- **Alternativas:**
  - **Hello también al abrir la app** (decisión 5 (b)): repite la autenticación de Windows en un equipo de un solo usuario.
  - **Sin Hello** (decisión 5 (c)): Desktop no cambiaría de TFM y el bloqueo sería solo «Bloquear Windows».
  - **Hello en la puerta (Security) y no en el servicio del diálogo.** Una implementación de `IApprovalPrompt` que no
    pidiera Hello no podría saltárselo, pero:
    - la puerta no conoce el ajuste ni la ventana;
    - el diálogo no podría avisar antes de preguntar;
    - la aceptación de un historial, que pregunta al servicio sin pasar por la puerta, no pediría Hello.

    Hoy `ApprovalPromptService` es la única implementación.
  - **Caer al diálogo ante cualquier «no disponible», también `DeviceBusy` o un error:** un dispositivo ocupado o una llamada
    que falla, o que alguien hace fallar, bastarían para saltarse Hello.
  - **Registrar el «sí» no confirmado como «no» de la persona:** el registro diría algo que la persona no decidió, y la
    entrega dejaría de tener en cuenta la impugnación.
  - **Escribir siempre la v2:** cambiaría sin necesidad el texto de toda respuesta del diálogo.
  - **Desbloquear con cualquier desbloqueo de Windows:** evitaría desbloquear dos veces tras Win+L. Pero cualquier programa
    de la sesión, también uno que lance alguien sentado delante con la sesión abierta, puede enviar un aviso falso de
    desbloqueo (un `PostMessage` de `WM_WTSSESSION_CHANGE`), y eso desbloquearía la interfaz.
  - **Parar el plazo de la puerta mientras está bloqueada:** la puerta (Security, `net10.0`) no sabe del bloqueo, y una
    ejecución podría esperar sin fin.
  - **La pantalla de bloqueo encima del contenido:** no tapa los WebView2.
  - **Una interoperabilidad COM escrita a mano, sin la proyección:** es la alternativa de docs/16 §6 si el tamaño de la
    salida no compensa.
- **Consecuencias:**
  - **El plazo de la puerta (30 minutos) sigue corriendo mientras la interfaz está bloqueada.** Una pregunta que nadie contesta
    a tiempo es una denegación del sistema, como con la interfaz desbloqueada, y la ejecución vuelve a pedirla cuando le hace
    falta. La pantalla de bloqueo dice desde cuándo espera cada una.
  - **Un «sí» que Hello no confirmó hay que volver a pedirlo** (repetir la entrega o volver a pulsar «Aprobar plan»).
  - **Toda aprobación crítica guarda el método:** las preguntas de la puerta, el plan aprobado en el lienzo y la aceptación de
    un historial, que pide Hello porque es crítica y usa el mismo servicio.
  - **Residuales** (el 1 de [ADR-0018](0018-modelo-de-amenaza-fase-3.md)):
    - Hello confirma tu presencia ante la app, pero no impide que un programa de tu sesión actúe sin pasar por Agent Studio
      ni protege los datos en reposo.
    - Un programa de la sesión puede falsificar el aviso de desbloqueo de Windows mientras la app espera uno, y entonces la
      sesión está bloqueada.
    - Entre comprobar la disponibilidad y verificar, el resultado puede cambiar: entonces se deniega.
  - **Si Hello no está y Windows rehúsa bloquearse,** la interfaz sigue bloqueada hasta que Windows acepte «Bloquear Windows»
    o Hello vuelva. Se puede salir de la app, y lo que quede a medias se reanuda por reproducción, como tras cualquier cierre.
  - **Sin verificar:**
    - Windows Hello en tu equipo, con la lista de la demostración del informe de WP4;
    - el diálogo de Hello sobre una ventana oculta en la bandeja (WP3);
    - si cancelar la operación de WinRT cierra el diálogo de Hello;
    - si la entrada en la pantalla de bloqueo de Windows cuenta para `GetLastInputInfo`;
    - cómo cuenta `GetTickCount` el tiempo de suspensión.
- **Adenda (2026-10-30, tanda 1): dónde están los ajustes.** La decisión no cambia; cambia dónde se tocan los ajustes.
  - `LockSettingsView` ya no está en «Trabajo desatendido» dentro de «Consumo». Es la sección «Windows Hello y bloqueo» del
    documento «Ajustes», que se abre desde «Ajustes», el último icono de la barra lateral ([docs/03 §5.2](../03-pantallas-y-navegacion.md#52-consumo-cuota-y-ajustes-documentos-consumo-y-ajustes)).
  - Ya no tiene un «Guardar» propio. El único «Guardar» del documento comprueba las tres secciones y, si todas valen, las
    escribe a la vez con un solo `SavePreferences` ([docs/04 §18.6](../04-contratos.md#186-ajustes-del-escritorio-desktopservicesappsettingscs-y-appsettingsstorecs)).
  - Los minutos del bloqueo por inactividad (de 1 a 1 440) se comprueban siempre, también con el bloqueo desactivado: un
    valor que no vale impide guardar todo el documento, y la sección dice por qué.
  - Lo que lee cada ajuste no cambia: el servicio de las aprobaciones lee `WindowsHelloForCriticalApprovals` en cada
    pregunta y el bloqueo por inactividad lee los suyos en cada comprobación.
- **Adenda (2026-11-09, Fase 13, WP5): la barrera fija de Windows Hello** ([ADR-0053](0053-windows-hello-como-barrera-fija.md);
  docs/17 D-37 a D-41; tu decisión 6 (a) del 2026-11-06, que enmienda tu decisión 5 (a) del 2026-10-20). Cambia la
  decisión 1 y una parte de las decisiones 3 y 5. El bloqueo de la interfaz (decisiones 4 y 6) no cambia.
  - **Decisión 1.** Lo que pide Hello ya no se reconoce por el nivel crítico ni por las palabras de la acción, sino por la
    acción, con la lista de `PresencePolicy`. Lo de la lista (entregar, aceptar un historial, aprobar un plan con escritores a
    integridad media, dar capacidades de riesgo, cambiar el perfil, quitar una protección, y lo remoto y los secretos) pide
    Hello **siempre**: el ajuste, que ahora se llama «Pedir Windows Hello también en las aprobaciones importantes», no se lee
    para ello. El ajuste solo gobierna lo configurable: aprobar un plan sin escritores, las tareas de aprobación del plan,
    los debates, las auditorías, las revisiones y, por primera vez, descartar el trabajo de una ejecución.
  - **«Sin Hello: no disponible»** (`WindowsHelloUnavailable`) solo vale ya para lo configurable. Para lo de la lista, un
    equipo sin Hello bloquea Windows tras el «sí» y comprueba con el estado de la sesión que se desbloqueó después
    (`ApprovalVerification.WindowsLock`). Con Hello en el equipo, el bloqueo no vale: solo Hello.
  - **Decisión 3.** Un «sí» que el bloqueo de Windows no confirma también lo deniega el sistema, con el método `WindowsLock`.
  - **Decisión 5.** El bloqueo de Windows de una aprobación no se fía del aviso de desbloqueo, sino de `SessionFlags`: cierra
    para la barrera el residual del aviso falsificado. El desbloqueo de la interfaz sigue como estaba.
  - **La alternativa «Hello en la puerta»** se adopta en parte. La puerta no pide Hello ni conoce el ajuste, pero deniega en
    nombre del sistema un «sí» a una acción de la lista que no traiga Hello ni el bloqueo comprobado, venga del diálogo que
    venga (`ApprovalGate.PresenceNotConfirmed`).
  - **Una sola regla.** La puerta, el diálogo, aceptar un historial y «Aprobar plan» juzgan con `PresencePolicy.IsSatisfiedBy`.
    El consentimiento de la recomendación de skills pasa por la puerta y deja su `ApprovalGranted`.
