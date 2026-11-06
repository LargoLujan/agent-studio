# ADR-0008 · Bitácora diaria obligatoria, por decorador y por hook

- **Estado:** aceptada · 2026-10-15
- **Contexto:** el usuario exige un Markdown por proyecto y por día que registre cada prompt lanzado, con resumen,
  fecha y hora y tags. «Obligatorio» solo se cumple si es estructural: una instrucción a un agente se puede olvidar.
- **Decisión:**
  1. En el producto, un decorador `JournalingAgentProvider` sobre `IAgentProvider`: escribe la entrada antes de lanzar
     el agente y, si no puede, no lo lanza; añade el resultado al terminar. La composición registra los proveedores
     solo a través de él.
  2. El formato lo escribe `MarkdownDailyJournal` (módulo Memory): un fichero por fecha local, append-only, entradas
     de una línea con identificador `P-NNNN` por día, tags normalizados y tolerancia a escrituras concurrentes.
  3. En el desarrollo de este repo, un hook `UserPromptSubmit` de Claude Code con el mismo formato de cabecera; nunca
     bloquea el prompt.
  4. La bitácora se versiona con el proyecto; base de datos y evidencias no.
- **Alternativas:** registrar en SQLite y exportar a Markdown al final del día (no es legible en tiempo real); un
  único fichero (crece sin límite y dificulta revisar un día); confiar en que cada agente lo escriba (no es
  obligatorio de verdad).
- **Consecuencias:** si el disco no es escribible, Agent Studio no lanza agentes (preferible a trabajar sin registro);
  el extracto del prompt de desarrollo queda en el repo (repo privado, uso personal).

## Adenda · 2026-11-03 · tanda 2: cada escritor explica lo suyo bajo el título (V31)

La decisión no cambia: un fichero por día, el mismo título y entradas que conviven en el mismo fichero. Cambia el punto 3,
«el mismo formato de cabecera».

- **Antes**, Agent Studio copiaba bajo el título las dos líneas del hook. Hablaban de la sesión, el extracto y las líneas
  «Hecho:» que añade Claude en este repositorio. En la bitácora de otro proyecto no tenían sentido.
- **Ahora** el título es el mismo (`# Bitácora · AAAA-MM-DD`), y quien crea el día escribe debajo sus propias dos líneas.
  Las de Agent Studio (`MarkdownDailyJournal`) dicen lo que hace la app:
  - «Registro automático de Agent Studio: una línea por cada petición a un agente (hora · número · quién ·
    proveedor/modelo · etiquetas · resumen).»
  - «Cuando el agente termina, se añade una línea «Resultado» con el estado, la duración y los tokens.»
- **El hook** sigue escribiendo las suyas.
- **La lectura reconoce las cuatro:** el resumen diario (`DailySummaryBuilder`) da por parte del formato las dos del hook
  (que la app también escribía antes) y las dos nuevas de la app. Un día antiguo y uno nuevo se resumen igual, sin líneas
  «sin reconocer».
- **Los días ya escritos no se tocan:** la bitácora es append-only.

## Adenda · 2026-11-06 · el extracto del hook, sin los bloques del sistema (P41)

La decisión no cambia. Se precisa el punto 3: el extracto y los tags del hook son de lo que escribió la persona, no del
texto entero que recibe el hook.

- **Contexto.** Claude Code y los programas desde los que se usa meten bloques propios en el prompt, delante o alrededor
  del texto de la persona: `<browser_instruction>`, `<pasted_content>` con lo pegado, `<system-reminder>`,
  `<ide_opened_file>` e `<ide_selection>`. El hook los copiaba y recortaba a 160 caracteres, así que el bloque ocupaba el
  extracto. Pasó en cuatro entradas (15-10, 16-10 y 30-10). Además, la bitácora está en un repositorio público, y lo
  pegado puede contener cualquier cosa.
- **Decisión.**
  - El hook quita esos bloques, con su contenido, antes del extracto, de los tags y de la fase. Es una lista cerrada de
    nombres.
  - Cada apertura va con el cierre más cercano de su nombre, sin otra apertura del mismo nombre en medio. Una apertura
    sin cierre no quita nada.
  - Lo pegado se sustituye por «[pegado]», y un prompt que solo trae bloques se registra como «(solo bloques del
    sistema)».
  - Los mensajes que no son de la persona (`<agent-message>`, `<task-notification>`, `[SYSTEM NOTIFICATION`) se buscan en
    el texto ya limpio y siguen sin registrarse. Un prompt que empieza con `<system-reminder>` solo se descarta si
    detrás no hay texto de la persona.
  - Detalle en [docs/11 §9](../11-bitacora.md#9-el-extracto-del-hook-sin-los-bloques-del-sistema).
- **Alternativas descartadas.**
  - Quitar solo los bloques del principio: no cubre los que van en medio.
  - Quitar cualquier etiqueta `<…>`: se comería texto de la persona, como XAML o genéricos de C#.
  - Copiar lo pegado recortado: puede llevar secretos a un repositorio público.
- **Consecuencias.**
  - Un bloque con un nombre nuevo no se quita hasta que se añade al hook.
  - Un `<pasted_content>` sin cierre deja pasar lo pegado, hasta 160 caracteres.
  - Una apertura y un cierre de esos nombres escritos por la persona se quitan con lo que haya entre ellos.
  - Las entradas ya escritas no se tocan: la bitácora es append-only.
