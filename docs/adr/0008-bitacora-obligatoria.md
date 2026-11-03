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
