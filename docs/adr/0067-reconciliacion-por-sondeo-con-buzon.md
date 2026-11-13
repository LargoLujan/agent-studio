# ADR-0067 · Reconciliación por sondeo condicional con un buzón de un consumidor por proyecto: se observa estado, no eventos, y la unión de commits es una proyección

- **Estado:** propuesta · 2026-11-13, pendiente de las decisiones 2 y 3 de [docs/21 §14](../21-fase-10-git-remoto.md#14-decisiones-que-necesitan-a-la-persona)
  (se redacta con la opción recomendada de cada una). Recoge D-02 y D-23 a D-30 de [docs/21](../21-fase-10-git-remoto.md) y
  las D-31 y D-46 a D-48 de [docs/16](../16-ampliacion-plataforma-de-proyectos.md). Ocupa, con
  [ADR-0065](0065-github-por-gh-sin-leer-el-token.md), el lugar del ADR-0042 que docs/16 reservó. **Amplía**
  [ADR-0039](0039-vinculos-de-trazabilidad-y-participacion.md) (la cadena y la participación) y aplica a lo remoto la regla
  de [ADR-0022](0022-memoria-append-only-anclada.md) (las entradas sin ejecución esperan al proyecto libre). WP3 implementa el
  buzón y el reconciliador; WP4, las proyecciones.
- **Contexto:**
  - F10-6 pide un sondeo condicional que reconcilie estado con un buzón por proyecto, sin duplicar nada ante eventos
    repetidos o desordenados, que respete los límites, y que no toque git ni el registro mientras una ejecución, un
    debate o una auditoría tienen el proyecto. F10-7, que la cadena una los commits originales con el resultante tras un
    squash, un rebase o un revert.
  - Una app de escritorio no tiene un punto público para webhooks (docs/16 D-47).
  - El motor de una ejecución ya es un actor de un solo hilo con buzón, y la preferencia del proyecto para las carreras es
    una cola o un canal de un solo consumidor antes que cerrojos nuevos.
  - `IRunActivity.IsBusy` es «mirar»: entre mirar y actuar puede empezar una ejecución. `IProjectActivity.TryEnter` es
    «entrar o no entrar».
- **Decisión:**
  1. **Se observa estado, no se aplican eventos.** Cada sondeo de un PR seguido da una **instantánea** (`RemoteSnapshot`):
     estado, commit de cabeza, rama base, commit y modo de la fusión, commits originales y resultantes, resumen de checks y
     de revisiones, número de force-push, commits que revierten y despliegue. Solo identificadores, números y enumerados.
  2. **Cada instantánea tiene una huella** (SHA-256 de su forma canónica). Dos con la misma huella son una: no se registra
     nada. Una con fecha anterior a la última registrada se descarta. Los cambios los calcula código puro
     (`RemoteReconciler`, en Domain), probado con secuencias repetidas y desordenadas.
  3. **Lo observado va al registro** como `RemoteStateObserved` (sin ejecución; la ejecución, en un campo), nunca
     reescribiendo una observación anterior. La última huella se lee del registro, no de una caché: borrar la caché no
     duplica nada.
  4. **Ningún texto del remoto entra en el registro.** Los títulos y los nombres de checks o de entornos viven en una
     caché local, para la pantalla.
  5. **Peticiones condicionales y cadencia fija.** `If-None-Match` con el `ETag` anterior; un 304 corta la vuelta de ese
     PR. Cada 5 minutos por defecto, solo lo seguido y abierto (lo fusionado, 30 días y cada hora), solo lecturas, solo con
     la app abierta.
  6. **Los límites se respetan:** con 429, o 403 con el límite a cero o `retry-after`, no sale nada hacia ese host hasta la
     hora que digan `retry-after` o `x-ratelimit-reset`; sin ellas, un minuto y espera progresiva. Nunca se insiste.
  7. **Con el permiso de la persona.** El sondeo periódico necesita un permiso por proyecto, apagado por defecto (`IRemoteSyncPolicy`);
     sin él, solo «Sincronizar ahora». Auto Doctor no usa la red.
  8. **Un buzón por proyecto con un solo consumidor** (`Channel` con un único lector): pone en serie observaciones y
     acciones. Y **una sola cola hacia `gh`** en toda la app: un proceso a la vez, que es lo que pide GitHub, y donde vive
     el estado de los límites. Sin cerrojos nuevos.
  9. **El proyecto ocupado.** Leer por `gh` no toca git ni el registro, así que sigue con una ejecución en marcha. Para
     lanzar `git` o escribir en el registro, el buzón **entra** en el proyecto con
     `IProjectActivity.TryEnter(…, RemoteOperation)` (un valor nuevo al final de `ProjectActivityKind`); si no puede, el
     mensaje espera en el buzón. Mientras lo tiene, ninguna ejecución, debate ni auditoría empieza; lo suelta tras cada
     mensaje.
  10. **Sin conexión, sin sesión o sin credenciales: «pendiente de sincronizar», con su motivo.** Lo pendiente vive en un
      fichero local, no en el registro: no ha pasado. Sin conexión, reintento con espera progresiva; sin sesión, se para
      hasta que la persona pulse. Antes de cada reintento la acción vuelve a pasar la compuerta entera (ADR-0068). Caduca
      a las 24 horas.
  11. **Cada acción remota es idempotente por estado:** antes de empujar se mira la rama; antes de abrir un PR se busca
      uno desde esa rama; tras un corte se consulta antes de repetir.
  12. **La unión de commits es una proyección** (`CommitSuccession`): la observación de una fusión guarda los commits
      originales del PR y los resultantes, con el modo. De un commit original se llega a lo que quedó, y de un commit del
      remoto, a sus originales y por ellos al intento, la tarea, el requisito y las evidencias. No se crean vínculos por
      commit, y nada se borra.
  13. **Lo deducido se enseña como deducido:** el modo de una fusión hecha fuera (por los padres del commit y la
      comparación con el penúltimo commit del PR) y una reversión (por la línea estándar `This reverts commit …`). Una
      reversión o un check que falla **no reabren un hito solos:** proponen reabrirlo, y lo decide la persona
      (`MilestoneReopened`, que ya existe).
  14. **«Desplegado»** solo existe si la plataforma informa de un despliegue con éxito de un commit resultante. Sin ese
      dato, el estado no aparece.
- **Alternativas descartadas:** webhooks con un relé (una pieza viva y otra cuenta, docs/16 D-47); aplicar los eventos de
  la plataforma (un evento repetido o desordenado duplicaría estado); un cerrojo por proyecto alrededor de git y del
  registro (la regla del proyecto es una cola antes que un cerrojo, y `TryEnter` ya existe); esperar con `IsBusy` y
  actuar después (mirar y luego actuar); `git fetch` para saber del remoto (escribe en el repositorio de la persona;
  ADR-0064); registrar lo pendiente en el registro (no ha pasado); vínculos `TraceLinkRecorded` por cada commit unido (la
  observación ya lo dice, y un vínculo aparte podría quedarse atrás).
- **Residuales aceptados:** el estado se conoce con el retraso del intervalo; el modo de fusión y la reversión son
  deducciones; un PR con más de 250 commits se une «truncado»; los ficheros locales del buzón los puede editar un programa
  del mismo usuario (por eso lo pendiente vuelve a pasar la compuerta y la caché se lee como no fiable).
- **Sin verificar al escribirlo:** que un 304 no cuenta contra el límite con una petición autenticada por `gh`; el código
  de salida de `gh api` con un 304; los eventos de force-push y la forma de los despliegues en la API; y que añadir un
  valor a `ProjectActivityKind` no rompe más `switch` que los de la bandeja y el bloqueo.
