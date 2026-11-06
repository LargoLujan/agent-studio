# ADR-0034 · Esperas por límite de uso y reanudación con consentimiento: el consentimiento va en la cabecera de la ejecución, la espera llega hasta el reinicio con margen y nunca se cambia de cuenta, proveedor ni modelo (enmienda la decisión 3 de ADR-0010)

- **Estado:** aceptada · 2026-10-21. Recoge D-16, D-36 a D-39 y D-41 de [docs/16](../16-ampliacion-plataforma-de-proyectos.md#214-límites-y-reanudación-automática-sección-14)
  y la decisión 4, opción (a), de su §7, que aprobaste el 2026-10-20. El paquete P0 de la Fase 8 deja lo que se guarda y se
  ancla: las opciones de la cabecera, la clase del final de cada intento (`AgentRunFinished` v3), la elección al salir
  (`RunExitChosen`), la pausa y la reanudación del despacho (`RunDispatchChanged`) y la comprobación antes de seguir
  (`AccessChecked`); estas dos últimas las propuso P0 y las aprobó el coordinador el mismo día. WP1 hace el motor (la
  política de espera, la comprobación antes de seguir, la reanudación tras reiniciar y el reloj que descuenta la
  suspensión) y añade a este ADR su adenda con lo que mida. Los contratos están en
  [docs/04 §18](../04-contratos.md#18-fase-8-trabajo-desatendido).
- **Contexto:**
  - Una tarea limitada queda `Blocked` con `RateLimitedUntil(t)`. Si el reinicio llega en `MaxAutoWaitForQuota` o menos
    (15 minutos por defecto), la ejecución espera sola; si no, queda en pausa hasta que tú reintentas. Sin hora de reinicio,
    el enfriamiento es de 5 minutos.
  - Las ventanas de cinco horas y la semanal de Claude, y el límite de uso de Codex, duran mucho más de 15 minutos: una
    ejecución larga se queda parada esperando a que vuelvas.
  - ADR-0010, decisión 3: «Nada se relanza solo… Reanudar es una decisión del usuario, porque gasta cuota». Tras reiniciar la
    app no se reanuda nada.
  - La cabecera de cada ejecución ya ancla sus opciones: `RunHeaderHash` cubre `MaxAutoWaitForQuota`, y `RunStarted` v2 y v3
    llevan ese hash. Reanudar exige que la cabecera guardada dé el hash anclado.
  - El analizador de Claude reduce un límite a `status != allowed` y no guarda `rateLimitType`, así que una ventana de cinco
    horas y la semanal se ven igual. Y el final de un intento no dice si se acabó la cuota, el contexto, la red o la sesión.
  - Cerrar la ventana termina la app, y nada dice en el registro si lo que quedó a medias lo dejaste tú o lo cortó un
    apagado.
  - La pausa que das desde el monitor solo vive en memoria (`OrchestrationRun._paused`), y la comprobación antes de seguir
    que pide F8-3 no tiene dónde quedar registrada.
- **Decisión:**
  1. **El consentimiento es de cada ejecución y va en su cabecera.** `RunOptions.AutoResumeConsent` (falso por defecto) lo
     marcas al aprobar el plan. Con él:
     - una tarea limitada espera hasta el reinicio que comunicó el proveedor más el margen, sin el tope de
       `MaxAutoWaitForQuota`;
     - tras reiniciar la app, y mientras sigue en la bandeja, se reanuda sola si quedó esperando cuota o la cortó un cierre
       que no pediste.

     Sin él, todo sigue como hoy: espera sola 15 minutos como mucho, y lo demás lo decides tú. **Esto enmienda la decisión 3
     de ADR-0010** solo para las ejecuciones que lo llevan: la regla por defecto sigue siendo que nada se relanza solo.
  2. **El margen y el tope son opciones de la cabecera, con su valor por defecto en el contrato.**
     - `RunOptions.QuotaResetMargin`: lo que se espera después del reinicio comunicado. Nulo es el del motor,
       `RunOptions.DefaultQuotaResetMargin` (3 minutos, D-36).
     - `RunOptions.MaxEstimatedQuotaWait`: el tope de cada espera sin hora de reinicio, que es progresiva (5, 10, 20, 40 y
       60 minutos) y sale «estimada». Nulo es el del motor, `RunOptions.DefaultMaxEstimatedQuotaWait` (60 minutos, D-37). La
       secuencia es de WP1.
  3. **Las opciones nuevas solo se escriben cuando no son las de por defecto,** como el campo `isolation` de
     [ADR-0016](0016-perfil-de-verificacion.md):
     - en `RunHeaderHash`, cada una como su nombre y su valor, detrás de la plantilla y en un orden fijo
       (`quotaResetMargin`, `maxEstimatedQuotaWait` y `autoResumeConsent`). La plantilla se delimita sola (cuenta y campos de
       cada agente), así que una cabecera con opciones nunca coincide con una sin ellas;
     - en `orchestration_runs.options_json`, solo cuando tienen valor (el consentimiento, solo cuando es verdadero).

     El hash de toda cabecera guardada antes no cambia (`494f1230…` en `RunHeaderHashTests`), y una cabecera con las tres
     tiene el suyo fijado, calculado con una implementación independiente de la forma canónica (`fe25aff3…`). Un valor dado
     forma parte de la cabecera aunque coincida con el del motor: solo uno que falta es el de por defecto.
  4. **El final de cada intento lleva su clase** (`AgentRunFinished` v3, D-41): `Completed`, `WindowQuota`,
     `RateOrOverload`, `ContextExhausted`, `Network`, `SignedOut`, `AttemptBudget`, `Interrupted`, `Cancelled` u `Other`.
     - Para una cuota de ventana, la ventana que se agotó tal como la nombra el CLI (`LimitType`, el `rateLimitType` de
       Claude) y su reinicio (`ResetsAt`): es el reinicio que se espera (D-36). Sin reinicio, la espera es la progresiva.
     - La clase refina el estado del intento y nunca lo sustituye: un `RateLimited` de clase `Other` sigue siendo un límite,
       de un tipo que el adaptador no supo decir.
     - Lo que un adaptador no reconoce en una captura de su CLI es `Other`, nunca una suposición (WP2).
     - Un final anterior a la v3 se lee con la clase que su estado implica (`ResolvedEnd()`), y la v1 y la v2 se reescriben
       byte a byte.
  5. **Lo que elegiste al salir queda en el registro de cada ejecución afectada** (`RunExitChosen`, D-16):
     - «Pausar y salir cuando terminen los intentos en curso» (`PauseAndExitWhenIdle`) y «Salir ahora y dejarlas
       interrumpidas» (`ExitNow`) son tu decisión: esa ejecución nunca se reanuda sola, tenga o no el consentimiento;
     - un cierre de sesión o un apagado de Windows (`SessionEnded`) cuenta como salir ahora, pero nadie lo eligió: con el
       consentimiento, se reanuda sola (`AllowsAutomaticResume()`);
     - «Cancelar» no escribe nada: nada cambia;
     - si no hay ninguna entrada (la app murió, o Windows no dio tiempo a escribirla), es un cierre que no pediste.
  6. **La pausa queda en el registro** (`RunDispatchChanged`, D-39). Cada vez que una persona pausa o reanuda el despacho de
     una ejecución, desde el monitor o desde la bandeja, se registra quién y cuándo. La última entrada de ese tipo decide:
     una ejecución en pausa nunca se reanuda sola tras un cierre que no pediste, aunque tenga el consentimiento; si la
     reanudaste, el consentimiento vuelve a decidir. Antes de la Fase 8 la pausa solo vivía en memoria.
  7. **La comprobación antes de seguir queda en el registro** (`AccessChecked`, D-38 y F8-3): una entrada por fuente, antes
     de relanzar tras una espera, con la tarea (o el proveedor), la fuente, el resultado y la hora. Las fuentes son solo las
     permitidas, y ninguna gasta un turno del modelo:
     - la sonda de sesión del CLI (`claude auth status` o `codex login status`): `Ready`, `SignedOut` o `Missing`;
     - en Codex, con el opt-in de [ADR-0020](0020-cuota-pasiva-en-claude-y-lectura-opcional-en-codex.md), la lectura de la
       cuota del app-server: `Ready`, `StillLimited` con su reinicio, `NotOptedIn`, `NotExposed` o `Missing`.

     No se guarda nada de lo que imprime el CLI, porque la sonda puede imprimir la cuenta. En Claude no hay lectura de cuota
     sin turno: la comprobación es el propio intento, y su final v3 dice si sigue limitado.
  8. **Nunca se cambia de cuenta, de proveedor ni de modelo, ni se reasigna la tarea** ante un límite. La plantilla está
     congelada en la cabecera y la reanudación exige su hash. Esperar es lo único que hace el motor.
  9. **Nunca se pasa del presupuesto aprobado.** Cada lanzamiento, también el que comprueba si el límite sigue, cuenta en
     `MaxAgentRuns`, así que la espera y la reanudación no pueden dar vueltas.
- **Alternativas:**
  - **Reanudación automática por defecto en todas las ejecuciones** (decisión 4 (b)): gasta cuota sin que lo hayas dicho
    para esa ejecución.
  - **Dejarlo como hoy** (decisión 4 (c)): las ventanas largas acaban siempre en una pausa manual.
  - **Un ajuste global de la app en lugar de la cabecera:** no quedaría anclado, y cambiarlo después de aprobar cambiaría lo
    que hace una ejecución ya aprobada.
  - **Escribir siempre las opciones nuevas:** cambiaría el hash de todas las cabeceras guardadas, y ninguna ejecución anterior
    se podría reanudar.
  - **Las opciones nuevas antes de la plantilla:** también sería inequívoco (un nombre nunca es un número), pero detrás de la
    plantilla cada fase puede añadir las suyas sin pensar en lo que hay delante.
  - **Una clase deducida del texto del error en el motor:** sería adivinar; la deduce cada adaptador de sus capturas.
  - **Deducir la pausa del motivo de un `TaskStatusChanged`, o no registrarla:** el motor no cambia ninguna tarea al pausar,
    y sin entrada no hay forma de cumplir «nunca las que pausaste» tras un cierre que no pediste.
  - **Guardar el comando de la comprobación como texto:** se deduce de la fuente y del proveedor, y un texto libre podría
    acabar llevando lo que imprime el CLI.
- **Consecuencias:**
  - Una ejecución aprobada sin el consentimiento se comporta exactamente como antes de la Fase 8.
  - Cambiar el margen o el tope de una ejecución ya aprobada es imposible: está en su cabecera. Hace falta otra ejecución.
  - `RunExitChosen`, `RunDispatchChanged` y `AccessChecked` van al final de `DecisionKind`, en ese orden, y sus payloads
    empiezan en `V = 1`. La reproducción y el consumo no los leen; el dosier del juez los lleva como entradas de la
    ejecución, sin interpretarlas.
  - La reanudación tras reiniciar (WP1) lee tres cosas del registro de cada ejecución con el consentimiento: su última
    `RunDispatchChanged`, su `RunExitChosen` si lo hay y si quedó esperando cuota.
  - No hay entrada propia para quién reanudó (`RunResumed`): se añadirá solo si WP1 necesita distinguir sin leer motivos la
    reanudación por consentimiento de la tuya.
  - **Sin verificar** (docs/16 §8): los valores de `rateLimitType` de Claude Code 2.1.290 y el mensaje de `codex exec` al
    topar el límite, que fijará WP2 con capturas; y si un intento de Claude rechazado por la cuota gasta algo.

## Adenda · 2026-10-25 · el motor (WP1): el reloj de la suspensión, la política de espera, la comprobación antes de seguir y la reanudación sola

WP1 hizo el motor de las decisiones 1, 2 y 5 a 9 (F8-2 a F8-5) y añadió `RunResumed`, que aprobó el coordinador el
2026-10-21 ([docs/04 §18.2](../04-contratos.md#18-fase-8-trabajo-desatendido)). Esto es lo que midió y lo que decidió.

1. **Qué reloj usa cada comprobación** (D-40, F8-5; .NET 10.0.11 en Windows 11). El hallazgo de la §2.14 de docs/16 solo
   estaba en el abandono, y se reprodujo en rojo antes del arreglo (`SleepTests`, con un reloj que adelanta la pared sin
   disparar temporizadores):

   | Comprobación | Reloj | ¿Cuenta la suspensión? |
   |---|---|---|
   | Abandono (`LastActivityAt` frente a la hora) | pared | sí: al despertar, un intento sano parecía abandonado |
   | Plazo del intento y latido del bucle | temporizadores de .NET | no: el plazo no vence al despertar |
   | Espera de cuota (`Until`, una hora absoluta) | pared, mirada en cada latido (5 s) | sí, y es lo correcto: el reinicio se acerca mientras el equipo duerme |
   | Gracia de la cancelación | `DateTimeOffset.UtcNow` directo; ahora el `TimeProvider` | sí |

   - **Los temporizadores no cuentan la suspensión:** un escaneo IL de `System.Private.CoreLib` 10.0.11 lo dice
     (`TimerQueue.get_TickCount64` llama a `QueryUnbiasedInterruptTime`, y su hilo espera con `WaitOne`, que desde
     Windows 8 no cuenta el tiempo dormido), igual que la página de cambios incompatibles de .NET 11. Con
     `UseWindowsThreadPool`, que aquí no está activado, irían por `SetThreadpoolTimer`: sin medir.
   - **QPC sí la cuenta** (la marca de tiempo de `TimeProvider.System` y `Stopwatch`), y `Environment.TickCount64` también
     (Microsoft Learn).
2. **El abandono descuenta el tiempo dormido** (`SleepAwareClock`). Antes de cada análisis de salud, el bucle compara lo
   que avanzaron el reloj de pared y un reloj despierto, que se para mientras el equipo duerme. La diferencia es la
   suspensión: la última actividad de cada tarea en marcha avanza ese tiempo (nunca más allá de ahora), y el monitor recibe
   un aviso con los minutos. Una diferencia de 2 s o menos es deriva entre los relojes, no suspensión.
   - El reloj despierto es la marca de tiempo del `TimeProvider`. La de `TimeProvider.System` es QPC, con la que no se ve
     ninguna diferencia, así que la app registra `AwakeTimeProvider` (`Infrastructure/Clocks`): la pared y los
     temporizadores del sistema, con la marca de `QueryUnbiasedInterruptTime`. Registrarlo en `AppComposition.cs` es de la
     integración.
   - Los eventos de energía de WP3 (`IPowerEvents`: `Resumed` con `Slept`) llegan al bucle como una señal más y lo
     despiertan. Las dos fuentes ven las mismas suspensiones, así que se descuenta la mayor de las dos cuentas, nunca la
     suma.
   - Una hora de pared adelantada cuenta como suspensión (nadie trabajó en ese tiempo); una atrasada no se deshace.
   - Las esperas de cuota siguen en la pared y los plazos son temporizadores: ninguno necesita descuento. Un debate espera
     el reinicio de su proveedor en trozos de 5 s como mucho contra la pared (`DebateRun`): con un solo temporizador largo,
     tras dormir más allá del reinicio, seguiría esperando el tiempo despierto que le quedaba. El `MaxDuration` del debate
     sigue en la pared (D-20 de docs/15), sin cambios.
3. **La espera se decide por la clase del final** (`QuotaWaitPolicy`, decisiones 2 y 4, F8-2), no por su estado:
   `WindowQuota` y `RateOrOverload` son límites aunque el estado sea `Failed` (una ventana de Claude sin el 429, WP2), y
   `SignedOut` deja la tarea esperando a una persona con el comando que lo arregla (`claude auth login`, `codex login`),
   sin reintentos.
   - **Con el consentimiento:** hasta el reinicio comunicado más `QuotaResetMargin`, sin tope. Sin reinicio (una ventana
     de `codex exec`), primero la lectura de la cuota de Codex con el opt-in, que deja su `AccessChecked`; si tampoco da
     hora, la espera progresiva de 5, 10, 20, 40 y 60 minutos, cada una con el tope de `MaxEstimatedQuotaWait`. La cuenta
     vuelve a empezar cuando un intento de la tarea termina de otra forma. Un `RateOrOverload` sin hora espera la progresiva
     corta (1, 2, 4, 8 y 15 minutos), en la misma sesión.
   - **La espera estimada queda marcada en el registro:** el motivo del bloqueo es `RateLimitedUntilEstimated(<hora>)`,
     que `BlockReason.Parse` lee igual que `RateLimitedUntil`. La reanudación rehace de ahí cuántas esperas seguidas
     llevaba la tarea, y el monitor añade «(estimada)».
   - **Sin el consentimiento, como antes de la Fase 8:** el reinicio o el enfriamiento de 5 minutos, sola solo hasta
     `MaxAutoWaitForQuota` y, si no, en pausa hasta que reintentas. Es como se lee aquí «si no, parar y preguntar»: la
     ejecución sin consentimiento se para y te espera.
   - **`Network` todavía no tiene su reintento progresivo** (D-41): sigue como antes, un fallo que reintenta el
     presupuesto de la tarea.
4. **La comprobación antes de seguir** (`AccessCheck`, decisión 7, F8-3). Tras una espera por límite (la que terminó sola
   o tu reintento de una tarea limitada), y antes de preparar la memoria, el worktree o el prompt: la sonda de sesión del
   CLI y, en Codex, la lectura de la cuota, cada una con su `AccessChecked`. `SignedOut` deja la tarea esperando a una
   persona; `StillLimited` la devuelve a esperar, con el reinicio leído. Un intento que la comprobación paró no se lanzó y
   no cuenta en `MaxAgentRuns`. Un proveedor sin sonda ni lector registrados no se comprueba.
5. **La reanudación sola** (`ResumeScheduler`, decisiones 1, 5 y 6, F8-4). La composición se la pide por proyecto al
   arrancar la app, y mientras sigue en la bandeja. Lee el registro como dice docs/04 §18.2: solo lo ocurrido desde la
   última `RunResumed` de la ejecución; la última `RunDispatchChanged` dice si la pausaste, y solo eso; y cualquier
   `RunExitChosen` con la elección de una persona impide la reanudación sola, aunque después llegue un `SessionEnded` de
   Windows. `RunResumed` va antes de la recuperación y de lanzar nada, con la causa y, si es por consentimiento, el hash de
   la cabecera. Cada ejecución reanudada se anuncia (`ResumeScheduler.Resumed`) para que la integración la una al monitor
   de su documento. Sus eventos esperan a su único lector, así que si aún no hay ventana, la ejecución se guarda y se da al
   monitor cuando se abre.

   Esto sustituye dos consecuencias de arriba: sí hay entrada propia para quién reanudó (`RunResumed`), y la reanudación
   no lee «su `RunExitChosen` si lo hay», sino cualquier elección de una persona desde la última `RunResumed`. Y precisa la
   decisión 5: un `SessionEnded` solo deja reanudar sola la ejecución si nadie eligió antes.
6. **Pruebas:** `SleepTests` (13, una con el reloj real y `AwakeTimeProvider`), `QuotaWaitTests` (17),
   `ResumeAfterRestartTests` (13), `DebateQuotaWaitTests` (3), `AwakeTimeProviderTests` (5) y el texto de `RunResumed` en
   `Phase8PayloadTests`. Ninguna suspende el equipo ni llama a un CLI.
7. **Sin verificar:** que `QueryUnbiasedInterruptTime` se pare en una suspensión real, o en la espera moderna (Modern
   Standby) de este equipo, como dice la documentación de Windows; los temporizadores con `UseWindowsThreadPool`; y las
   clases de WP2 que solo tienen capturas sintéticas.

## Adenda · 2026-10-29 · lo que corrigió la revisión independiente de WP1

Una revisión independiente de WP1 dio el consentimiento por bueno:
- nada se reanuda sin el consentimiento anclado de cada ejecución;
- la elección de una persona al salir se respeta;
- `RunResumed` va antes de lanzar nada;
- el juez nunca se salta.

Encontró siete defectos, cuatro de ellos reproducidos con sondas. Cada uno tiene ahora su prueba, en rojo antes del
arreglo. Se corrigen así:

1. **Las comprobaciones no dan vueltas** (decisión 9).
   - El defecto: una lectura de la cuota de Codex con el límite activo y un reinicio ya pasado (una ventana leída tarde, o
     relojes distintos) daba un bloqueo que ya había vencido. La comprobación se repetía en la misma pasada del bucle, sin
     gastar presupuesto: miles de lecturas por segundo, con el consentimiento y sin él.
   - Un reinicio que no está por delante (con el margen, si hay consentimiento) no es un reinicio que esperar
     (`QuotaWaitPolicy.Announced`). Con el consentimiento toca el siguiente paso estimado; sin él, el enfriamiento de 5
     minutos. Así cada bloqueo termina por delante.
   - Una comprobación no lanza nada y sigue sin gastar presupuesto. Pero una tarea no se comprueba más veces seguidas que
     `MaxAgentRuns` (`RunLedger.StoppedChecks`): entonces deja de esperar sola, y la ejecución queda en pausa hasta que la
     reintentas. Un lanzamiento de la tarea pone la cuenta a cero.
   - Así se cumple aquí la decisión 9: la espera no puede dar vueltas. Contar las comprobaciones en `MaxAgentRuns`
     gastaría el presupuesto del trabajo en lecturas y daría la ejecución por fallida.
2. **La reanudación rehace lo que sabía la ejecución viva:**
   - el enfriamiento de cada proveedor, con el final más tardío de los bloqueos por límite de sus tareas. Antes, tras
     reanudar, las demás tareas de un proveedor aún limitado se lanzaban en el acto;
   - el presupuesto: una revisión que paró su comprobación no gasta lanzamiento;
   - las esperas estimadas, también cuando la comprobación paró a un ejecutor todavía pendiente;
   - la cuenta de comprobaciones seguidas. Para el lanzamiento una `AccessChecked` con la sesión cerrada o la cuota aún
     agotada, de una revisión o de una tarea que aún no está en curso. La lectura de la cuota al agotarse la ventana llega
     con la tarea en curso, y no cuenta.

   Queda una diferencia menor: un bloqueo al despachar (sin proveedor, o sin base) pone a cero las esperas estimadas en la
   reanudación, y no en vivo.
3. **Un turno de debate con el proveedor sobrecargado** (`RateOrOverload` que no es un límite, como un 529 de Claude)
   espera un minuto y tiene su único reintento, como antes de la Fase 8. Ya no espera a una persona como si fuera un límite
   de uso. En el motor, `RateOrOverload` sigue con la progresiva corta.
4. **`ResumeScheduler`:**
   - Las pasadas van de una en una, por una cola con un solo lector.
   - El orquestador decide otra vez, bajo la reserva de la ejecución, sobre el registro que acaba de leer. Si ya no
     procede, no escribe nada: una persona pudo pausarla o salir entre medias.
   - Una ejecución cuyo bucle se paró en esta sesión de la app por un error del sistema no se reanuda sola en esa sesión.
   - El fallo de una ejecución es solo suyo, y la pasada sigue con las demás. Si el fallo escribió en su registro (la
     alerta de que no se puede reanudar, o `RunResumed` antes de fallar su recuperación), no se vuelve a intentar en esa
     sesión de la app.
   - `Suspend()` para las pasadas en el acto, y la suspensión dura hasta que se desecha, pase lo que pase con cualquier
     espera. `WaitForIdleAsync` espera a que acabe la pasada en curso: cada ejecución que reanudó se ha anunciado por
     `Resumed` antes de que termine, y cancelar la espera no suelta nada. `SuspendAsync` hace las dos cosas para quien
     no la suelta nunca. La salida gestionada toma la suspensión antes de mirar el trabajo en marcha para preguntarte, y
     la suelta si te quedas. El fin de la sesión de Windows la toma antes de escribir nada, espera con su tiempo límite y
     no la suelta. (Corrige la primera versión, que soltaba la suspensión al cancelar la espera.)
5. **Una pausa que no quedó en el registro falla para quien la pidió.** Pasa si la ejecución ya había terminado, o si su
   bucle se paró antes de aplicarla. Ya no se da por hecha: sin su `RunDispatchChanged`, la ejecución podría reanudarse
   sola.
6. **La memoria lee también `RateLimitedUntilEstimated(...)`.** La nota de `BlockReason` dice ahora quién lee las dos
   formas, igual que el lector de la bandeja (`WorkStatus`, de WP3), corregido en la integración.
7. **Sigue pendiente** el reintento progresivo de `Network`.

Una segunda revisión, el mismo día, dio por cerrados los siete y encontró tres defectos menores más:

8. **La suspensión no se suelta al cancelar su espera.** Es el cambio del punto 4: `Suspend()`, `WaitForIdleAsync` y
   `SuspendAsync`.
9. **El reintento de una persona vuelve a armar la espera sola.** Pone a cero las comprobaciones seguidas de la tarea, así
   que un reinicio anunciado por delante se espera solo. Su `TaskStatusChanged` (`Blocked` → `Pending`) lleva ahora el
   actor `User`, y la reanudación pone la cuenta a cero en él. Un registro escrito antes no la pone a cero, que es lo
   prudente.
10. **Las esperas de una ejecución nueva tienen que tener sentido** (`RunOptions.EnsureValid`, antes de escribir nada). Se
    rechaza un margen negativo y un tope de las esperas sin reinicio de menos de un minuto: con ellos, cada espera
    terminaría en el acto. Una cabecera guardada con esperas raras no se rechaza: se lee y se hashea como está guardada, y
    el motor usa los valores con sentido más cercanos (`ResolvedQuotaResetMargin()`, `ResolvedMaxEstimatedQuotaWait()`).

## Adenda · 2026-11-06 · dos carreras de la espera de cuota que hacían intermitente `QuotaWaitTests` (docs/18 P42)

`QuotaWaitTests` fallaba de forma intermitente, también sola: 3 de 20 pasadas de la clase en `main` (6cf8bf7). Cada vez
caía una prueba distinta. Había dos causas, las dos en el motor.

1. **La espera que no despertaba con un reloj falso** (`OrchestrationRun.WaitAsync`).
   - El síntoma: la ejecución quedaba en `WaitingForQuota` y su tarea `Blocked` con `RateLimitedUntil(…)`, aunque el
     reloj ya había pasado el final de la espera.
   - La causa: el bucle calcula cuánto dormir con una lectura del reloj (hasta el final de la espera, un latido como
     mucho) y después arma un temporizador relativo (`Task.Delay(delay, TimeProvider)`). Ese temporizador cuenta desde
     que se arma, no desde la lectura: si el reloj avanza entre las dos cosas, llega tarde. Las pruebas avanzan el reloj
     falso en cuanto ven la fase publicada, y el bucle publica antes de armar. Una traza lo mostró: el bucle leyó las
     09:00, la prueba avanzó a las 09:05 y el temporizador de 5 s se armó ya a las 09:05, para las 09:05:05. Con un reloj
     falso que nadie vuelve a adelantar, esa hora no llega nunca.
   - En la app no colgaba. Con el reloj del sistema, entre la lectura y el temporizador pasan microsegundos. Si el reloj
     saltara justo entonces (el equipo se duerme, o cambia la hora), el temporizador llegaría tarde un latido (5 s) como
     mucho, porque la espera se mira en cada latido. Ninguna ejecución se queda esperando cuota para siempre.
   - El arreglo: tras armar el temporizador, el bucle mira el reloj otra vez. Si avanzó todo el retraso mientras lo
     armaba, no espera y vuelve a mirar en el acto, como habría hecho el temporizador. No hay cerrojos: el bucle sigue
     siendo el único lector de su cola. Un avance menor que el retraso deja el temporizador tarde como mucho ese avance,
     menos de un latido; ninguna prueba avanza así.
   - La prueba: `A_wait_ends_when_the_clock_reaches_its_end_while_the_loop_arms_its_timer`, con un reloj falso que salta
     justo al armar el temporizador de la espera (`JumpingFakeTimeProvider`). Antes del arreglo se colgaba 5 de 5 veces;
     después pasa.
2. **La reanudación contaba de más una comprobación** (`RunResumer.StoppedTheLaunch`). Apareció con la CPU cargada, ya
   arreglada la primera.
   - El síntoma: una ejecución reanudada se quedaba en pausa una comprobación antes que su ejecución viva.
   - La causa: la reanudación decidía si una `AccessChecked` paró un lanzamiento por el estado de la tarea en ese punto
     del registro: «no está en curso». Pero la lectura de la cuota al agotarse la ventana la escribe el trabajador, y el
     paso de pendiente a en curso lo escribe el bucle por su cuenta. Si el intento termina en el acto (Codex sin cuota) y
     la máquina va cargada, la lectura llega antes al registro. La frase del punto 2 de la adenda del 2026-10-29 («llega
     con la tarea en curso, y no cuenta») solo era cierta cuando el bucle escribía antes.
   - En la app sí podía pasar, aunque es raro: hace falta que el intento acabe antes de que el bucle anote su arranque.
     El efecto es menor: tras reanudar, la ejecución pide a una persona que la reintente una comprobación antes de lo
     debido. No lanza nada de más ni gasta presupuesto.
   - El arreglo: cuenta como comprobación la que llega antes de lanzar al agente desde que la tarea volvió a pendiente.
     Eso lo dice su `AgentRunStarted`, que escribe el mismo trabajador antes de la lectura, así que el orden ya no depende
     de los hilos. Las revisiones no cambian.
   - La prueba: `A_resumed_run_goes_on_with_the_steps_and_the_count_of_the_checks_of_its_live_run` es ahora una teoría.
     Su caso nuevo retiene en el registro el paso a en curso hasta que llega la lectura (`HoldingRecorder`). Antes del
     arreglo fallaba igual que con la carga; después pasa.
3. **Tasas de la clase** (este equipo, 16 núcleos; con carga, 20 bucles ocupados que dejan la CPU al 100 %):

   | Código | Sin carga | Con la CPU cargada |
   |---|---|---|
   | `main` (6cf8bf7) | 3 de 20 fallan (causa 1) | 1 de 30 (causa 1) |
   | Con el arreglo 1 | 0 de 30 | 1 de 30 (causa 2) |
   | Con los dos arreglos | 0 de 30 | 0 de 30, y 0 de 30 con la suite entera en paralelo |

4. **Queda el mismo patrón que la causa 1 en el debate.** `DebateRun.WaitForResetAsync` lee el reloj y arma el
   temporizador igual. Sus pruebas esperan a ver armado el temporizador antes de avanzar (`SuspendableClock.Timers`), y
   no se ha visto fallar. Se deja como está.
