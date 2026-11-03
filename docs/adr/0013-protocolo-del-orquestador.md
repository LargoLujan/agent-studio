# ADR-0013 · Protocolo del agente orquestador: esquema portable, parseo estricto, validador, una corrección, aprobación y repositorio del usuario intacto

- **Estado:** aceptada · 2026-10-16
- **Contexto:**
  - En la Fase 3 el orquestador (Claude) propone el plan de una ejecución y los ejecutores lo llevan a cabo
    ([docs/13](../13-fase-3-orquestacion.md), decisiones 3 y 5). Nada de lo que dice un agente es de fiar: puede
    equivocarse y puede haber leído instrucciones plantadas en el repositorio (riesgo R4).
  - Claude Code solo admite el esquema de salida en línea (`--json-schema`, 16 KB como máximo; spike S3), y Codex lo
    recibe por fichero (`--output-schema`) con las reglas del modo estricto de OpenAI. Un mismo esquema tiene que servir
    a los dos.
  - Cada ejecución de agente gasta cuota de suscripción: ni el planificador ni el motor pueden entrar en bucles de
    corrección.
  - El sistema hace todos los commits y los agentes trabajan en worktrees fuera del repositorio, pero un agente con
    shell puede salirse de su worktree. El repositorio del usuario (HEAD, ramas, etiquetas, stash, índice y árbol de
    trabajo) no debe cambiar nunca por culpa de una ejecución (docs/13 §5).
  - Lo que produce una tarea llega a las siguientes: tiene que llegar como dato, nunca como instrucciones.
- **Decisión:**
  1. **El orquestador solo planifica.** Lee el repositorio en un worktree `_base` creado en HEAD (los cambios sin
     confirmar del usuario no entran), con `Read`, `Grep` y `Glob` y sin escritura, shell ni red (`dontAsk`), y con sus
     propias skills. El prompt va por stdin y se captura como evidencia (`Document`/`CapturedBySystem`): los payloads
     del registro solo llevan su id y su SHA-256 (`AgentRunStarted`, `InstructionsIssued`), nunca el texto.
  2. **Esquema portable construido en código** (`PlanSchema`). Solo usa el subconjunto común:
     - cada objeto declara todas sus propiedades en `required` y prohíbe las demás (`additionalProperties: false`);
     - lo que puede faltar es obligatorio y anulable (`anyOf` con `null`);
     - solo `type`, `properties`, `required`, `additionalProperties`, `items`, `enum`, `anyOf` y `description`: sin
       formatos, patrones, longitudes, cotas numéricas, `$ref`, `oneOf` ni valores por defecto.

     Los enums son dinámicos: los agentes de la plantilla salvo el orquestador, los requisitos del conjunto congelado, los
     pasos del perfil de verificación (como pasos y como herramientas), los tipos de tarea (sin `Debate` en la Fase 3),
     las uniones, los modos de worktree, los predicados y las ramas. El tamaño se comprueba antes de lanzar nada: si no
     cabe en 16 KB, la planificación falla sin gastar cuota. Lo que el esquema no puede expresar (formato de los ids,
     ciclos, alcances, independencia del juez…) lo decide el validador. Un test recorre el esquema con un verificador
     del modo estricto escrito aparte.
  3. **Parseo estricto** (`PlanParser`): un único objeto JSON con todas las propiedades del esquema, también las
     anulables (`RespectRequiredConstructorParameters`); ninguna desconocida (`UnmappedMemberHandling.Disallow`) ni
     repetida (`AllowDuplicateProperties = false`); ningún nulo donde el esquema no lo admite, tampoco dentro de las
     listas; sin comentarios, comas finales ni conversión de tipos; y los nombres exactos de los conjuntos cerrados
     (`"0"` o `"agentwork"` no valen). Parsear no es validar.
  4. **El validador del dominio decide** (`PlanValidator`, sin cambios). Se aplica sobre la plantilla, los requisitos,
     los pasos y los límites de la petición, y otra vez en `StartAsync`, porque un plan también puede escribirlo una
     persona.
  5. **Una corrección como máximo.** Si el plan no se puede leer o no valida, se reanuda la sesión (`--resume`) con los
     problemas como datos (`<plan_problems>`, una lista JSON); sin sesión, se repite el prompt completo con los problemas.
     Si sigue mal, la propuesta vuelve al usuario con sus violaciones y nunca hay una tercera ejecución. Un fallo del
     agente (CLI ausente, límite, error) no es un problema del plan y no se corrige. El registro guarda `PlanCreated`
     (actor: el orquestador) y, si hubo corrección, `PlanRevised` con el hash del plan anterior. Todo plan legible se
     guarda en `IPlanStore`, y la respuesta tal cual, como `AgentStatement`/`ReportedByAgent`.
  6. **El usuario aprueba antes de que se gaste cuota.** `StartAsync` rechaza un plan sin `PlanApproval.Approved` y
     registra `ApprovalRequested` y `ApprovalGranted` (si la puerta de aprobación no lo hizo ya) antes de `RunStarted`.
  7. **El repositorio del usuario se comprueba alrededor de cada ejecución de agente**: el planificador, cada intento
     de cada ejecutor y la revisión del juez. `IWorktreeManager.CaptureUserRepositoryStateAsync` se llama antes de
     `AgentRunStarted` y cuando el agente ha terminado, y `UserRepositoryState.Compare` da las diferencias. Las líneas
     de estado cuyas rutas están todas bajo `.agentstudio/` se excluyen de las dos capturas: Agent Studio escribe ahí
     durante cada ejecución (la bitácora versionada de ADR-0008), y es la salvedad que ya reconocía docs/13 §5. Con
     cualquier otra diferencia:
     - la tarea falla sin reintentos y queda un `AuditAlertRaised` (`user-repository.changed`) con todas las diferencias;
     - no se despacha nada más (tampoco después de reanudar) y la ejecución termina en `Failed`;
     - en la planificación, el plan se descarta.
  8. **El protocolo del ejecutor.** Su prompt lleva las reglas, el objetivo y las instrucciones del plan aprobado, los
     requisitos que cubre citados literalmente del conjunto congelado (con su hash), el alcance de escritura, la
     verificación que ejecutará el sistema y el informe que debe devolver (`ExecutorReport`, con un esquema igual de
     portable y un parseo igual de estricto). Lo que producen otras tareas o intentos va como dato delimitado,
     `<upstream_output task=… evidence=… sha256=…>` y `<previous_attempt …>`, re-codificado en JSON con un codificador
     que escapa `<`, `>`, `&` y las comillas, de modo que no puede cerrar su bloque. El alcance se comprueba sobre el diff
     real acumulado desde la base del worktree, tras el commit del sistema, nunca sobre lo que dice el informe.
  9. **El registro del intento.** Al final de cada intento el sistema captura su propia evidencia (`Registro del
     intento`): la base y la cabeza del worktree, las rutas cambiadas, las salidas del informe y el último resumen de
     pruebas. Una tarea completada la cita la primera. Es lo que permite reanudar sin adivinar commits ni hechos.
- **Alternativas:**
  - Texto libre y extraer el JSON: frágil y abre la puerta a instrucciones escondidas en la respuesta.
  - Un esquema más rico (`oneOf` por tipo de tarea, patrones, cotas): no es portable al modo estricto de OpenAI y no
    sustituye al validador, que habría que mantener igual.
  - El esquema en un fichero para Claude: el CLI no lo admite (S3).
  - Un parseo tolerante que ignore lo desconocido: esconde errores del agente y vías de inyección.
  - Varias rondas de corrección: cuota sin techo para un problema que suele necesitar a una persona.
  - Confiar en el sandbox de cada CLI para proteger el repositorio del usuario: no está verificado en todos los casos
    (S2 deja abierto si Codex puede escribir fuera de su worktree), y la comprobación es barata y de solo lectura.
  - Un campo con el commit resultante en `TaskStatusChangedPayload`: obligaba a cambiar un contrato de Application ya
    publicado; el registro del intento da lo mismo sin tocarlo.
- **Consecuencias:**
  - Planificar cuesta una ejecución de agente y dos como máximo. En la prueba real con Haiku sobre un repositorio
    pequeño: 1 ejecución, sin corrección, 68 s, unos 0,14 USD según la estimación del propio CLI, y un esquema de
    2 363 bytes. El plan de 5 tareas pasó el parseo y el validador.
  - El esquema crece con la plantilla, los requisitos y el perfil. Con unos 2 000 requisitos ya no cabe, y la
    planificación lo dice antes de lanzar nada.
  - Los problemas del parseo y del validador están en inglés, como los mensajes del validador, porque vuelven al agente
    en la corrección. La interfaz muestra la propuesta y sus códigos.
  - **Límites de la comprobación del repositorio del usuario:**
    - volver a editar un fichero que el usuario ya tenía modificado no cambia su línea de `status --porcelain` y no se
      detecta (el índice sí, porque su hash cubre el contenido preparado);
    - la comprobación es por ventana de tiempo: con tareas en paralelo, un cambio se atribuye a todas las que se
      solapan con él;
    - si el usuario trabaja en su propio repositorio durante una ejecución, la tarea falla. Es intencionado, y la
      interfaz debe avisarle antes de empezar.
  - Los worktrees se tratan como de vida corta: el motor no guarda leases entre intentos y los vuelve a pedir con
    `EnsureAsync`, y nunca devuelve un worktree a su base mientras su agente sigue vivo.
