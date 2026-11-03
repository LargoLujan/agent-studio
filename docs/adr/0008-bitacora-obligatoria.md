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
