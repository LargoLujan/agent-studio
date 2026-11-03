# ADR-0011 · Tareas condicionales: estado `Skipped`, aristas con rama y política de unión

- **Estado:** aceptada · 2026-10-15
- **Contexto:**
  - F3-1 pide tareas condicionales, y F3-3 que el análisis del grafo detecte lo que de verdad va mal sin dar falsas
    alarmas.
  - Con la máquina de estados de la Fase 0, una tarea cuya rama no se toma se queda en `Pending` para siempre.
    `Analyze` la contaría como trabajo sin terminar, y si no queda nada más, como bloqueo; y todo lo que dependiera de
    ella sería «inalcanzable».
  - No había forma honesta de decir «esta tarea no debe ejecutarse»: `Cancelled` es una decisión del usuario y rompe a
    sus dependientes.
- **Decisión:**
  1. **Estado nuevo `Skipped`.** Terminal, añadido al final del enum. Transiciones `Pending→Skipped` y
     `Blocked→Skipped`. `WorkTask.TransitionTo(Skipped)` se rechaza (`task.skip-via-graph`): solo
     `TaskGraph.PropagateSkips` omite una tarea.
  2. **Aristas con rama.** Una arista que sale de un nodo `Condition` lleva la rama que espera (`yes` o `no` en los
     planes). Es obligatoria si el prerrequisito es una condición y está prohibida si no lo es. Hay una sola arista por
     par de tareas.
  3. **Estado de una arista**, visto desde su dependiente:
     - *satisfecha*: el prerrequisito se completó y, si hay rama, es la que se tomó;
     - *muerta*: el prerrequisito se omitió, o se completó por otra rama;
     - *rota*: el prerrequisito se canceló o falló sin reintentos;
     - *pendiente*: cualquier otro caso.
  4. **Política de unión** por tarea (`JoinPolicy`):
     - `AllCompleted` (por defecto): lista con todas las aristas satisfechas, y se omite en cuanto una está muerta;
     - `AnyCompleted`, el punto de reunión de las ramas: lista cuando ninguna arista está pendiente ni rota y al menos
       una está satisfecha, y se omite solo si todas están muertas;
     - una tarea sin prerrequisitos está siempre lista y nunca se omite.
  5. **`CompleteCondition`** es la única forma de completar una condición: guarda la rama (`TakenBranch`). Una
     condición completada de otro modo no eligió rama, así que todas sus aristas con rama quedan muertas.
  6. **`PropagateSkips`** hace una pasada en orden topológico, que basta para llegar al punto fijo porque lo que ocurre
     con una tarea solo depende de sus prerrequisitos. Los motivos son deterministas, devuelve las tareas omitidas y es
     idempotente.
  7. **`Analyze`** ve el grafo como quedará tras `PropagateSkips`, sin cambiarlo: lo que se va a omitir cuenta como
     terminado (ni inalcanzable ni bloqueo), y lo que esa omisión desbloquea cuenta como listo. «Inalcanzable» no
     atraviesa tareas omitidas. Un grafo sin condiciones se comporta exactamente como antes (hay una prueba de
     propiedad contra la implementación anterior).
  8. **Los predicados son un conjunto cerrado** que evalúa el sistema sobre hechos que él mismo capturó: `TaskSucceeded`,
     `TestsPassed`, `OutputEquals` y `PathsChanged`. Un plan no lleva expresiones ni comandos, y un predicado solo puede
     citar a antecesores de su condición.
  9. **Reproducción.** Una omisión registrada no se aplica: se recalcula con `PropagateSkips` y se compara con el
     registro (ADR-0010).
- **Alternativas:**
  - No crear las tareas de la rama no tomada: se pierde el plan que aprobó el usuario y al reproducir habría que
    distinguir «no tomada» de «no creada».
  - Marcarlas `Cancelled`: mezcla la voluntad del usuario con una consecuencia del plan, y rompe a sus dependientes.
  - Dejarlas en `Pending`: bloqueos falsos.
  - Nodos explícitos de «si/si no» y de unión como tipos nuevos: más tipos de nodo para la misma semántica.
  - Expresiones libres en las condiciones: una vía de inyección desde el repositorio hacia el plan (riesgo R4).
- **Consecuencias:**
  - La interfaz debe mostrar `Skipped` («Omitida») y el monitor no debe tratarla como un problema.
  - `AnyCompleted` es estricto: una arista rota impide que la tarea corra aunque otra esté satisfecha, y el motor la
    muestra como cualquier otra tarea inalcanzable.
  - El validador del plan impone la forma del grafo: ramas solo en aristas que salen de condiciones, predicados sobre
    antecesores y una sola base de código por tarea salvo en las tareas `Integration`.
  - No cambian los hashes ni los datos existentes: `Skipped` es el último valor del enum y los `DecisionKind` nuevos van
    al final.
