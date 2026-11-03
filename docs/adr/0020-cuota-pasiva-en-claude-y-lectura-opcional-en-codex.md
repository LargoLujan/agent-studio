# ADR-0020 · La cuota es pasiva en Claude y, en Codex, una lectura opcional del app-server con lista cerrada de mensajes y respuesta por lista blanca

- **Estado:** aceptada · 2026-10-18. Recoge D-14 a D-17 de [docs/14](../14-fase-4-observabilidad-y-memoria.md#43-f4-4--cuota)
  y la decisión 1, opción (b), de su §12. Lo implementa WP1 (`Providers` y `Application/Providers`); el registro de cuota
  del usuario es de WP2 y los paneles, de WP5.
- **Contexto:**
  - F4-4 pide la cuota de suscripción expuesta por cada proveedor.
  - **Claude Code 2.1.290** anuncia sus ventanas al empezar cada ejecución, aunque no se alcance el límite:
    `rate_limit_event.rate_limit_info.unifiedWindows.{five_hour,seven_day}` con `utilization` (0..1) y `resetsAt`
    (capturas 01, 02 y 04). Hasta la Fase 4 solo dejaba una línea de actividad en el monitor.
  - **Codex CLI 0.160.0**: `codex exec --json` no imprime la cuota. Solo la da el app-server, con
    `account/rateLimits/read`: un GET al backend de ChatGPT con las credenciales del propio CLI, sin turno del modelo
    (código de `rust-v0.160.0`; PR #5302).
  - El app-server también tiene métodos que cambian algo: `account/rateLimitResetCredit/consume` gasta un reinicio
    guardado y `account/sendAddCreditsNudgeEmail` envía un correo.
  - La regla de sondas de `CLAUDE.md` solo permitía `--version`, `claude auth status` y `codex login status`. El usuario
    la amplió el 2026-10-18 con esta lectura: solo lectura, sin turno del modelo y opt-in en Ajustes.
  - Gemini está aparcado ([ADR-0009](0009-google-aparcado.md)).
- **Decisión:**
  1. **Claude: cuota pasiva, sin ninguna llamada extra.**
     - `QuotaObservingAgentProvider` (Application) convierte cada `QuotaReported` de una ejecución en observaciones
       (`QuotaObservation`, una por ventana, con la utilización del proveedor) y las añade a `IQuotaObservations`, el
       registro del perfil (`%LOCALAPPDATA%\AgentStudio\usage\quota.jsonl`, de WP2).
     - Va en el pipeline de proveedores dentro de la bitácora: `Concurrency(Journaling(QuotaObservation(adaptador)))`. La
       bomba de la bitácora lee todos los eventos de toda ejecución, así que también cubre las que nadie lee y las
       auxiliares.
     - Lo que el CLI no dice no se inventa: ni la duración de la ventana ni un nombre para mostrar.
     - Un fallo al escribir el registro nunca toca la ejecución: el registro solo se muestra.
  2. **Codex: lectura opcional del app-server** (`CodexAppServerQuotaReader`, `IProviderQuotaReader`).
     - **Opt-in.** Sin él no se busca el CLI ni se lanza nada (`QuotaReadStatus.NotOptedIn`). El ajuste vive en el
       escritorio: el lector recibe una función que lo lee en cada llamada (`AddCodexQuotaReader`).
     - **Lanzamiento.** La superficie exacta es `codex app-server --listen stdio://`: el `codex.exe` nativo
       (`CodexExecutableResolver`), dentro del Job Object del ejecutor de procesos, en una carpeta vacía de la ejecución
       (`%LOCALAPPDATA%\AgentStudio\runs\<id>`), nunca un repositorio, que se borra al terminar.
     - **Lista cerrada de tres mensajes, texto constante** (solo varía la versión de Agent Studio, como token):
       `initialize` (id 1, `clientInfo` `agent_studio`), `initialized` y `account/rateLimits/read` (id 2) con
       `excludeResetCreditDetails: true`, que se ahorra la segunda petición al backend. Nunca `supportsLunaReserve`, que
       dejaría al backend registrar la exposición a un experimento. Nunca se envían
       `account/rateLimitResetCredit/consume` ni `account/sendAddCreditsNudgeEmail`, ni ningún otro método.
     - **Respuesta.** Se espera la de id 2 durante 20 s, y en cuanto llega se termina el árbol de procesos. Sin respuesta,
       o si el app-server termina sin darla, «sin dato» con el motivo.
     - **Lista blanca** (`CodexRateLimitsParser`). De cada bucket de `rateLimitsByLimitId` (por orden de id) o, sin él, del
       único de `rateLimits`, se toman:
       - las ventanas `primary` y `secondary`, como `<limitId>/primary` y `<limitId>/secondary`: `usedPercent` dividido
         entre 100 como utilización «exacta · informada por el CLI» (un cambio de unidad, no un cálculo),
         `windowDurationMins` como duración y `resetsAt` como reinicio;
       - `limitName` como nombre para mostrar y `rateLimitReachedType` como límite alcanzado, en las dos ventanas del
         bucket;
       - `planType` y `ordinaryUsageAllowed`.

       **Nunca se guarda** `accountId`, `rateLimitUpsell`, el saldo de `credits`, `rateLimitResetCredits`,
       `individualLimit`, `spendControlReached` ni `normalModelSlug`: no se leen. Un valor fuera de rango se descarta,
       nunca se adivina; una utilización negativa deja fuera su ventana.
     - **Errores.** Con API key («chatgpt authentication required to read rate limits»), «no expuesto: Codex usa API
       key». Sin sesión, «sin dato» con la indicación de `codex login`. Cualquier otro, «sin dato» con un motivo corto y
       sin caracteres de control. La salida de error del proceso nunca se guarda: puede llevar rutas del perfil.
     - **Versión.** `codex --version` dice si la respuesta de esa versión está probada
       (`SupportedVersions.QuotaReadTested`, hoy 0.160.0, por el esquema de su código). Con otra, la lectura se hace igual y
       lleva el aviso «Versión de Codex no probada para leer la cuota».
     - **Cuándo.** Bajo demanda («Consultar cuota de Codex») y una vez al terminar cada ejecución de Codex (decisión 1
       (b)); nunca en segundo plano. Quién lo llama lo decide la integración, con WP5.
  3. **Gemini:** «no expuesto».
- **Alternativas:**
  - No leer la cuota de Codex: F4-4 se cumpliría a medias.
  - Sondeo periódico mientras la app está abierta (decisión 1 (c)): lecturas sin que nadie las pida.
  - Ejecutar Codex por el app-server, que daría la cuota de forma pasiva: es otro adaptador, aplazado.
  - Leer `~/.codex`: prohibido; Agent Studio nunca lee las carpetas de los CLIs.
  - Cerrar la entrada estándar tras los tres mensajes, como hacía el contrato del ejecutor de procesos de la Fase 2: la
    medición lo descartó (ver Consecuencias).
- **Consecuencias:**
  - Claude no gasta nada más: su cuota llega con cada ejecución, también las auxiliares.
  - **Medido en la integración (2026-10-18, `CodexQuotaLiveTests`): con la entrada cerrada tras los tres mensajes, el
    app-server de 0.160.0 termina sin responder a la lectura.** Solo llegan la respuesta a `initialize`, un
    `configWarning` y un `remoteControl/status/changed`, y sale con código 0. Lo que se había leído en su código (que
    responde antes de que el fin de la entrada cierre la conexión) era falso. Con la entrada abierta responde en 1-2 s.
    Por eso el lector la deja abierta hasta la respuesta (`ProcessSpec.KeepStandardInputOpen`, que solo honra el ejecutor
    de sesiones; el ejecutor por lotes lo rechaza, porque esperaría al tiempo límite) y termina el árbol al recibirla.
    Con eso la prueba en vivo pasa: lectura `Read`, dos ventanas (300 y 10 080 minutos) y nada que identifique la cuenta
    fuera del analizador.
  - La prueba en vivo deja la captura `Fixtures/codex-cli/<versión>/05-rate-limits.jsonl` saneada: solo la respuesta de
    la lectura, con `accountId`, saldo, upsell y créditos de reinicio sustituidos, y nunca la salida de error.
    `CodexRateLimitsParserTests` la lee; la respuesta sintética del esquema de 0.160.0 (`Providers/Codex/Synthetic/`)
    sigue para los casos que la real no trae. Grabada el 2026-10-18.
  - El app-server lee `~/.codex/config.toml`: no ofrece `--ignore-user-config`. Agent Studio no lee esa carpeta.
  - **Sin verificar** (docs/14 §13): la respuesta real de 0.160.0; si el app-server arranca sin hilo los servidores MCP
    de la configuración del usuario; sus trabajos de fondo al arrancar.
  - Términos: «App-server authentication has never been permitted for commercial or hosted services». Es uso personal
    ([ADR-0003](0003-integracion-por-clis-oficiales.md)).
  - El decorador de cuota se compone solo si hay un `IQuotaObservations` registrado. Hasta que la integración registre el
    de WP2, los proveedores se componen sin él.
