# ADR-0012 · Worktrees fuera del repositorio, refs propias, `merge-tree` con CAS y entrega con aprobación

- **Estado:** aceptada · 2026-10-16. Recoge la decisión 4 y la §5 de [13](../13-fase-3-orquestacion.md) y lo verificado
  en los spikes S2 y S5 ([investigación](../investigacion/2026-10-15-spikes-fase-3.md)). Lo implementan
  `Infrastructure/Git` (`GitWorktreeManager`) y `Orchestration/Integration` (`Integrator`, `DeliveryService`,
  `WorkspaceCleanup`).
- **Contexto:**
  - F3-2 pide un worktree por tarea y una integración de sus cambios con build y tests; varias tareas escriben a la vez.
  - El repositorio es del usuario: su HEAD, su índice, sus ramas y su árbol de trabajo no pueden cambiar mientras
    trabajan los agentes. Además, la bitácora (ADR-0008) lo deja casi siempre con cambios sin confirmar.
  - Un agente con shell controla todo su worktree, incluido el fichero `.git`, y puede escribir refs en el repositorio,
    que todos los worktrees comparten.
  - Un worktree dentro del repositorio lo verían los globs de MSBuild de un `.csproj` en la raíz, y aparecería como
    ficheros sin seguimiento.
  - En Windows hay rutas largas, y los nodos de MSBuild o el antivirus retienen ficheros.
- **Decisión:**
  1. **Worktrees fuera del repositorio**, en `%LOCALAPPDATA%\AgentStudio\wt\<repo8>\<run6>\<nombre>`, con `nombre` =
     `T1`, `_base` o `_integration`. Se crean con
     `git worktree add --detach --lock --reason "agent-studio:<proyecto>:<run>:<nombre>"`.
     - La propiedad se demuestra con la ruta bajo la raíz propia, el motivo del bloqueo y la carpeta de administración
       del repositorio.
     - Todo comando que modifica un worktree usa `--git-dir` y `--work-tree` explícitos, nunca el `.git` que el agente
       puede reescribir.
  2. **Refs propias.**
     - Ocultas, una por tarea: `refs/agentstudio/<run>/<tarea>`, más `…/_start/<nombre>` con la base de cada worktree.
       No ensucian `git branch` y mantienen vivos los objetos.
     - Visible, una por ejecución: `agentstudio/run-<run>`, el destino de la integración y el origen de la entrega.
     - Solo se crean, mueven o borran refs con esos dos prefijos, siempre con `--no-deref` y como compare-and-swap.
       Sin `--no-deref`, borrar una ref simbólica plantada en ese espacio borraría la rama a la que apunta: se comprobó
       con git 2.45.2.
     - Una ref a la que se llega por una junction o un enlace se rechaza antes de listarla, crearla o borrarla: con
       `refs/agentstudio/<run>` convertida en junction a `refs/heads`, la limpieza borraba ramas del usuario (V).
  3. **El sistema hace los commits y las fusiones; el agente nunca.** La fusión no usa worktree:
     `git merge-tree --write-tree`, después `commit-tree -p <destino> -p <fuente>` con el mensaje por stdin, y después
     `update-ref` con el valor esperado (CAS).
     - Antes se comprueba `merge-base --is-ancestor`: una fuente que ya está en el destino no hace nada, así que repetir
       una integración es idempotente.
     - El código 1 de `merge-tree` solo es un conflicto si la salida empieza por un árbol; si no, es un error (S5).
  4. **Orden determinista.** El motor ordena las fuentes con `IntegrationOrder`: por la profundidad de la tarea en el
     grafo y luego por `TaskId` (comparación ordinal). El orden solo depende del grafo y de los ids, no del orden en que
     el plan listó las tareas. El integrador fusiona en el orden recibido.
     - La rama de la ejecución se crea en la base la primera vez y después tiene que seguir descendiendo de ella.
     - Todas las fuentes se resuelven a commits antes de fusionar nada, y el valor esperado del CAS se encadena de una
       fusión a la siguiente.
  5. **Un conflicto es un fallo con evidencia y nunca se resuelve solo.**
     - La integración se para en la fuente que choca. La rama conserva las fusiones limpias anteriores: es la cabeza
       parcial, de la que puede partir una ejecución de seguimiento.
     - No se confirma nada con marcadores de conflicto y `_integration` no se toca, porque una persona puede estar
       resolviéndolo allí.
     - Queda una evidencia con las rutas en conflicto y los mensajes de git: `EvidenceKind.ConflictReport`,
       `CapturedBySystem`, con el título «Informe de conflicto».
  6. **Integración verificada.** Si todo es limpio, `_integration` se pone exactamente en el resultado (reset y clean,
     solo en el worktree propio) y allí se ejecuta el perfil de verificación ([ADR-0016](0016-perfil-de-verificacion.md)).
     La integración solo es correcta si todos los pasos se ejecutan y pasan, y hace falta al menos uno.
  7. **Entrega con aprobación.** Fusionar en la rama del usuario es lo único que la cambia, y pasa por `IApprovalGate`
     como acción crítica, con la rama, los dos commits y las rutas que cambian.
     - La hace `IWorktreeManager.DeliverToUserBranchAsync`, que no ejecuta nada sin la petición y la respuesta de la
       aprobación (un sí explícito de alguien con nombre, riesgo crítico).
     - Por defecto `--ff-only --no-autostash`. Con `MergeCommit`, solo si `merge-tree` no da conflictos:
       `merge --no-ff --no-commit` y `commit -F -`, porque `merge -F -` no lee stdin y el mensaje nunca va en la línea
       de comandos.
     - Se fusiona el commit aprobado, no el nombre de la rama. Si HEAD avanzó o cambió de rama mientras se esperaba la
       aprobación, no se hace nada; la capa de git lo vuelve a comprobar justo antes de fusionar.
     - Si el usuario tiene cambios, ficheros sin seguimiento o ignorados en una ruta que cambia la entrega, no se hace
       nada: git sobrescribe un fichero ignorado sin avisar (V). En cualquier otro choque, git aborta por sí solo.
     - No se pregunta cuando no hay nada que entregar: HEAD desacoplado, sin rama de la ejecución o con ella ya dentro
       de la rama del usuario.
  8. **Hooks y firma.**
     - En los commits internos (las refs ocultas y `agentstudio/run-*`) se desactivan: `-c commit.gpgsign=false`,
       `--no-verify` y `commit-tree`, con la identidad «Agent Studio».
     - Además, ningún comando interno ejecuta hooks: todos reciben `-c core.hooksPath=<carpeta vacía>`, y si esa carpeta
       es un enlace o contiene algo, git no se ejecuta. Un agente con shell puede escribir los hooks del repositorio que
       comparten los worktrees, y `update-ref` ejecuta el hook `reference-transaction` con los permisos de Agent Studio:
       se comprobó con git 2.45.2 que un hook plantado se ejecuta sin la carpeta vacía y no con ella.
     - En la entrega se respetan: ni `--no-verify`, ni `commit.gpgsign=false`, ni `core.hooksPath`. El commit de fusión
       lleva la identidad, los hooks y la firma del usuario. Con `--ff-only` corre `post-merge`; con `MergeCommit`, los
       hooks de `commit` (`pre-commit`, `prepare-commit-msg`, `commit-msg`, `post-commit`). Si el commit falla, la
       fusión queda en curso y se dice.
     - Como esos hooks se ejecutan con el contenido entregado, la aprobación nombra cada fichero de hooks que cambia
       (bajo `.husky/`, `.githooks/`, `.git/hooks/` o la carpeta del `core.hooksPath` del usuario), calculado del diff
       antes de preguntar.
  9. **Limpieza con aprobación.**
     - Lo que se puede borrar sin perder trabajo se borra sin preguntar: worktrees limpios y refs cuyo commit está en la
       rama del usuario o, si es de una tarea, en la de la ejecución.
     - Un worktree con cambios sin confirmar y las refs con trabajo no integrado (también una rama de la ejecución que
       no se entregó) piden **una sola** aprobación, como acción destructiva.
     - Un worktree todavía en uso se conserva con sus refs, y el resto se limpia.
  10. **Cada comando de git** lleva `GIT_TERMINAL_PROMPT=0` y `GIT_OPTIONAL_LOCKS=0`, sin las variables que podrían
      redirigirlo (`GIT_DIR`, `GIT_WORK_TREE`, `GIT_CONFIG_PARAMETERS`, los pares `GIT_CONFIG_KEY_n`/`GIT_CONFIG_VALUE_n`…),
      y con `-c core.longpaths=true`; nunca se escribe en la configuración del usuario y solo se lee salida porcelain.
      Nunca hay `push` ni red.
- **Alternativas:**
  - Worktrees dentro del repositorio, en `.agentstudio/wt`: los globs de MSBuild y el `git status` del usuario los
    verían.
  - Un clon por tarea: más disco y tiempo, y los objetos no se comparten.
  - Ramas visibles por tarea: ensucian `git branch`. Las refs ocultas mantienen vivos los objetos igual.
  - `git merge` en el worktree de integración: con un conflicto deja índice y árbol a medio fusionar. `merge-tree` no
    toca ningún árbol.
  - Resolver los conflictos con un agente: aplazado, fuera de la Fase 3.
  - Entregar sin aprobación, o con un `reset` de la rama del usuario: es la acción más crítica del sistema.
- **Consecuencias:**
  - Del repositorio del usuario solo cambian la base de objetos, `refs/agentstudio/*`, `agentstudio/run-*`, la
    administración de sus worktrees e `info/exclude`, salvo en una entrega aprobada.
  - Hace falta git 2.38 o posterior (`merge-tree --write-tree`); Auto Doctor lo comprueba.
  - `Orchestration/Integration` no tiene camino propio a git: integración, entrega y limpieza usan solo
    `IWorktreeManager`, que ya resuelve refs, comprueba ancestría, crea la rama de la ejecución, lista y borra sus
    refs, encuentra las rutas de hooks y entrega. Los miembros nuevos del puerto tienen un cuerpo por defecto que
    rechaza, para que los dobles anteriores sigan compilando.
  - Un agente con shell puede mover refs del espacio propio. Lo mitigan el CAS, `--no-deref` y que la rama tenga que
    descender de la base; además, el motor debería pasar a la integración los commits registrados de cada tarea, no solo
    el nombre de su ref. El juez revisa el diff final.
  - **Riesgo abierto, mitigado en parte:** la entrega respeta los hooks del usuario. Si los ficheros de hooks llegan
    en el diff, la aprobación los nombra. Un hook que un agente plantara directamente en `.git/hooks` o en la carpeta
    de `core.hooksPath` sin versionarlo no está en el diff y se ejecutaría al aprobar. Mitigación propuesta: registrar
    al empezar la ejecución un hash de los hooks activos y negarse a entregar, o avisar en la aprobación, si cambiaron.
  - El disco crece con un checkout completo por tarea; la limpieza con aprobación lo recupera.
  - Mientras se integra, ningún agente puede trabajar en `_integration`: el checkout descarta lo que haya.

## Adenda · 2026-10-16: entrega verificada, aprobación atada y captura completa del repositorio del usuario

Una revisión independiente y otra adversarial de la Fase 3 encontraron que la entrega llevaba lo que hubiera en la rama
de la ejecución, que una aprobación de entrega se podía reutilizar y que la captura del repositorio del usuario no veía
contenidos, configuración ni hooks. Se decide:

1. **Solo se entrega lo que terminó bien y aprobó un juez, y solo ese commit.** `IDeliveryService.CheckAsync`, de solo
   lectura y sin git, exige:
   - un registro de decisiones que verifica;
   - el último `RunFinished` de la ejecución en `Succeeded`, con commit de integración y veredicto;
   - ese `VerdictIssued` válido y `Approved`, confirmado por el almacén de veredictos y sin otro que lo sustituya;
   - que la integración revisada (la última anterior al veredicto, sin conflictos y sin integraciones posteriores) sea
     la de `RunFinished.IntegrationCommit`.

   Una revisión escalada que aprobó una persona no basta: ningún juez la aprobó. `DeliverAsync` hace esa comprobación
   primero y entrega ese commit solo si la rama `agentstudio/run-<run>` sigue en él: un agente con shell puede moverla
   (riesgo de la decisión 2). Tras entregar se registra `IntegrationPerformed` con `IsDelivery = true` (actor System,
   destino `refs/heads/<rama>`); si no se puede anotar, la entrega ya está hecha y el mensaje lo dice.
   - **Pendiente:** el veredicto todavía no lleva el commit candidato. Mientras tanto, el veredicto queda atado al
     commit por el orden del registro y por `RunFinished.IntegrationCommit`; cuando el dossier y `VerdictIssued` lo
     lleven, la comprobación debe compararlo directamente.
   - El integrador sigue moviendo la rama antes de verificar la integración. La entrega ya no se fía de la rama, pero el
     cambio del integrador es de otro paquete.
2. **La aprobación de una entrega está atada a esa entrega y se usa una vez.** `DeliveryApproval.Bind` hace una atadura
   con el repositorio, la rama destino, el modo, el commit y la cabeza esperada, más un nonce. `DeliveryService` la pone
   en `ApprovalRequest.Binding`, y la puerta responde siempre con ella (`ApprovalDecision.Answers`), diga lo que diga el
   diálogo. `DeliverToUserBranchAsync` recalcula la atadura desde la entrega, exige que el sí responda a esa petición y
   gasta la aprobación (una vez por proceso, en cualquier instancia), todo antes de ejecutar git. El nonce hace que
   pedir otra vez la misma entrega sea otra aprobación.
3. **Una aprobación sin respuesta es del sistema, no del usuario.** El diálogo solo devuelve lo que la persona contestó.
   El tiempo agotado, la cancelación o un fallo al preguntar llegan a la puerta, que registra `ApprovalDenied` con actor
   System y el motivo, también si el diálogo dijo «no» al caducar la pregunta. Sigue fallando cerrado.
4. **La captura del repositorio del usuario ve el contenido, la configuración y los hooks** (`UserRepositoryState`
   gana `Contents` y `GitFiles`):
   - el status incluye los ignorados (`--ignored=matching`);
   - cada fichero listado va por SHA-256 hasta 8 MiB; uno mayor o que no se puede leer va por tamaño y fecha;
   - una carpeta sin seguimiento o ignorada (`bin/`) va por el listado de sus ficheros (nombre, tamaño y fecha), hasta
     50 000 ficheros por captura. Lo que no es SHA-256, la comparación lo llama aproximado;
   - se capturan `config` y `config.worktree` del directorio git y del común, byte a byte, y cada fichero de la carpeta
     efectiva de hooks: la del `core.hooksPath` del usuario, o `hooks` del directorio común. Son 512 ficheros como
     máximo, hasta dos niveles; si la carpeta es la raíz del árbol, solo los de primer nivel;
   - los enlaces nunca se siguen y todo es de solo lectura (`GIT_OPTIONAL_LOCKS=0`).

   `Compare` solo compara el contenido de las líneas de status que siguen en las dos capturas. Así la exclusión de
   `.agentstudio/` que aplica el guardián del motor deja fuera también su contenido.
   - **Coste medido en este repositorio:** un proceso de git más por captura (8 en total; con la máquina cargada,
     1,2-2,4 s entre todos). En proceso, 110-160 ms en un worktree con unas 1 100 entradas bajo carpetas ignoradas, y
     0,75-1,15 s en el checkout principal, con unas 9 300, casi todas de `.claude/worktrees/`.
   - **Límites:** cualquier escritura en `bin/`, `obj/` o en otra carpeta ignorada durante una ejecución hace fallar la
     tarea, por ejemplo una compilación del IDE o del usuario. Es coherente con ADR-0013: trabajar en el repositorio
     durante una ejecución la hace fallar. Además:
     - pasado el tope de ficheros, una carpeta se compara por lo listado;
     - `~/.gitconfig` y `info/` no se capturan.
   - Se resuelve el límite que anotaba ADR-0013: volver a editar un fichero que ya estaba modificado ahora se detecta.
   - Queda mitigado el riesgo abierto de los hooks: un hook que un agente plante en `.git/hooks` o en la carpeta de
     `core.hooksPath` durante su ejecución se detecta y la tarea falla, así que esa ejecución no llega a entregarse.
5. **La entrega está en la aplicación.** El monitor ofrece «Entregar a mi rama» (`--ff-only`) solo cuando la ejecución
   terminó en `Succeeded` y `CheckAsync` la da por lista; si no, muestra por qué. La entrega sigue pasando por
   `IApprovalGate` como acción crítica, nombrando los ficheros de hooks que cambian.

## Adenda · 2026-10-16: la rama visible solo apunta a integraciones verificadas

La adenda anterior dejaba pendiente que el integrador movía `agentstudio/run-<run>` antes de verificar: la rama podía
apuntar a un commit que nunca había pasado el perfil de verificación. La entrega ya no se fiaba de la rama, pero bastaba
un registro que dijera otra cosa para entregar un candidato que había fallado (lo muestra una prueba con el integrador
anterior). Se decide:

1. **El integrador fusiona en una ref candidata, nunca en la rama.** Cada tarea de integración tiene la suya, oculta:
   `refs/agentstudio/<run>/_candidate/<tarea>`. Ningún worktree puede llamarse `_candidate`, así que no choca con las
   refs de las tareas.
   - Se prepara en la cabeza de la rama (`IWorktreeManager.PrepareCandidateAsync`). Si ya la contiene, se conserva: son
     las fusiones de un intento anterior de la misma tarea, que una reanudación reutiliza. Si no, vuelve a la cabeza de
     la rama con un CAS.
   - Reutilizarla es seguro porque las fuentes de una tarea de integración no cambian dentro de una ejecución: son los
     commits que el motor registró al completarse cada tarea, y lo completado no se repite. Una tarea rehecha solo existe
     en una ejecución de seguimiento, que tiene sus propias refs.
   - Cada fusión es `MergeIntoCandidateAsync`: `merge-tree`, `commit-tree` y CAS de la candidata, con el valor esperado
     encadenado. El destino sale de la ejecución y la tarea, nunca de un nombre de ref, así que una fusión no puede mover
     la rama. Una fuente que ya está en la candidata no hace nada y devuelve el mismo commit.
2. **Se verifica la candidata.** `_integration` se pone exactamente en ella y allí corre el perfil; sus evidencias llevan
   la candidata como `SubjectCommit`.
3. **Solo si todos los pasos se ejecutan y pasan, la rama avanza**, con `FastForwardRunBranchAsync`: un CAS desde la cabeza
   con la que empezó la integración hasta la candidata, con `--no-deref`.
   - Se rechaza, y nada se mueve, si la candidata no desciende de esa cabeza (`NotFastForwardException`) o si la rama ya no
     está en ella (`StaleRefException`); el CAS también lo detecta si la rama se mueve entre las comprobaciones y la
     escritura. El integrador se detiene con ese error, que el motor trata como un error del sistema (la tarea queda
     bloqueada), y la rama queda donde la dejó quien la movió.
   - Una rama que ya está en la candidata se deja como está: un intento anterior terminó el avance.
4. **Tras una integración correcta coinciden** la cabeza de la rama, el `SubjectCommit` de sus pruebas,
   `IntegrationPerformed.ResultCommit` y `RunFinished.IntegrationCommit`. Por eso una entrega solo puede llevar algo
   verificado: aunque el registro dijera que se aprobó un candidato rechazado, la rama no está en él y no se entrega.
5. **Si la verificación falla, la rama no se toca y la tarea falla.** El candidato rechazado queda en su ref y en una
   evidencia del sistema (`Document`, «Candidato rechazado», ligada a él por `SubjectCommit`) con la cabeza que conserva
   la rama, lo fusionado y cómo terminó cada paso. `ResultCommit` es ese candidato: de él puede partir una ejecución de
   seguimiento que arregle lo que falló.
6. **Esto cambia la decisión 5 en un punto:** ante un conflicto, las fusiones limpias anteriores quedan en la candidata, no
   en la rama. La cabeza parcial sigue siendo `ResultCommit` y el punto de partida de una ejecución de seguimiento; nada
   con marcadores de conflicto se confirma.
7. **El registro no atribuye a la rama lo que no tiene.** `IntegrationPerformed` de una integración correcta tiene como
   destino `agentstudio/run-<run>`; el de una fallida (verificación o conflicto), la ref candidata de la tarea.
8. **Reanudar:**
   - si la aplicación se cierra entre la verificación y el avance, el intento siguiente reutiliza la candidata (el mismo
     commit, sin fusiones nuevas), la verifica una sola vez y avanza la rama. La verificación del intento interrumpido no
     cuenta: su evidencia nunca llegó al registro, y el juez solo admite lo registrado;
   - si se cierra después del avance, la rama ya está en la candidata: las fusiones no hacen nada, se verifica otra vez y
     el avance no tiene nada que hacer.
9. **Endurecimiento de las tres operaciones nuevas**, como el de las demás refs: entorno saneado, `core.hooksPath` vacío,
   `--git-dir` explícito, `update-ref --no-deref` como CAS, `--end-of-options` antes de cualquier revisión (también en
   `commit-tree`, comprobado con git 2.45.2), solo refs con prefijo `agentstudio`, ids de tarea validados antes de
   ejecutar git y rechazo de una ref a la que se llega por una junction. `MergeIntoAsync` sigue disponible con el
   endurecimiento que tenía; el integrador ya no lo usa.

- **Consecuencias:**
  - Se resuelve el pendiente del punto 1 de la adenda anterior: el integrador ya no mueve la rama antes de verificar.
  - La limpieza trata la candidata como una ref más de la ejecución: la de una integración correcta está en la rama y se
    borra sin preguntar; un candidato rechazado o una cabeza parcial es trabajo no integrado y pide aprobación.
  - Reanudar tras un cierre entre la verificación y el avance repite la verificación una vez. Es el precio de no fiarse
    de una evidencia que no llegó al registro.
  - **Riesgo abierto:** un agente con shell puede mover la ref candidata, entre dos intentos, a un commit que descienda de
    la cabeza de la rama; se reutilizaría. Lo cubren la verificación, que se repite en cada intento, y el juez, que
    revisa el diff final, como antes cubrían la propia rama.
  - **Resuelto después:** `RunLog.CandidateCommit` (Debate) solo cuenta las `IntegrationPerformed` cuyo destino es la rama
    `agentstudio/run-<run>`, así que un candidato rechazado nunca es el commit en revisión.

## Adenda · 2026-10-17: ningún comando interno ejecuta un callback que controle el repositorio o un worktree (N-01)

La decisión 8 y su adenda apagan los hooks y la firma en los comandos internos, pero git ejecuta además los programas que
nombra su configuración: filtros (`filter.*.clean/smudge/process`), drivers de fusión (`merge.*.driver`), `diff.*.textconv`,
`diff.*.command`, `diff.external`, `core.sshCommand`, `core.editor`, `sequence.editor`, `core.askpass`, `credential.*.helper`
y `gpg.program`. Una segunda revisión adversarial (Codex) mostró el ataque: el repositorio tiene un filtro legítimo cuyo
programa es un script versionado (`filter.format.clean = sh tools/filter.ps1`), activado por `.gitattributes`; el agente
reescribe ese script en su worktree y toca un fichero filtrado, y el `git add` de `CommitAllAsync`, o el `reset --hard` de
un checkout, ejecutarían el script del agente con los permisos del usuario, antes de la verificación aislada Low de
[ADR-0017](0017-verificacion-aislada-baja-integridad.md). Lo mismo valen `merge-tree` (que el integrador ejecuta con el
driver del repositorio) y las lecturas de diff. Se decide:

1. **Antes de cada comando interno que lee el árbol o un árbol a través de los atributos del repositorio** (`status`, `add`,
   `commit`, `reset`, `clean`, `checkout`, `merge-tree`, `archive` y `worktree add`), `GitCommandRunner` inspecciona la
   configuración **efectiva** con `git config -z --show-scope --show-origin --get-regexp`, pasando el mismo `--git-dir`,
   `--work-tree` y directorio de trabajo que el comando, de modo que los `includeIf` (`gitdir:`, `onbranch:`) y
   `config.worktree` se resuelven igual. La consulta no es un comando de contenido, así que no se revisa a sí misma.
   `worktree list`/`remove`/`prune` no se revisan, para que la limpieza funcione aun en un repositorio rechazado.
2. **Falla cerrado** (`UnsafeGitCallbackException`, que el motor trata como error del sistema y hace fallar la tarea) cuando
   un callback podría ejecutar un programa que el repositorio o un worktree controlan: una ruta **relativa** (se resuelve
   contra el directorio de trabajo del comando), una ruta **absoluta** bajo la raíz del repositorio o la de los worktrees,
   o un **intérprete** (`sh`, `bash`, `powershell`, `python`, `cmd`, `node`, `dotnet`…) cuyos argumentos que no son opciones
   no son todos rutas absolutas fuera de esas raíces: así se cazan `sh tools/filter.sh` y `powershell -File tools\filter.ps1`.
   Un valor con metacaracteres de shell (`$`, comillas invertidas, `;`, `|`, `&`, `<`, `>`, `(`, `)`, `{`, `}`) o una
   asignación de entorno al principio (`VAR=…`) también se rechaza: no se puede analizar con seguridad. El mensaje nombra la
   clave, el origen y la palabra ofensora, nunca el valor entero (un `credential.*.helper` puede llevar un token).
3. **Se permite** el callback cuyo programa es un nombre suelto (se resuelve por el PATH; `git-lfs`, `astextplain`,
   `git-credential-manager`) o una ruta **absoluta fuera** del repositorio y los worktrees (`gh.exe` bajo Archivos de
   programa): el agente no puede alterarlo. Así los repositorios git-lfs siguen funcionando. Un `credential.*.helper` con
   `!` (forma shell) y un valor suelto (`manager`, que git expande a `git-credential-manager`) se tratan como git los trata.
4. **El origen manda sobre el valor:** si el fichero de configuración del que sale el callback vive dentro de un worktree
   (un `includeIf` que el agente planta), se rechaza sea cual sea el programa.
5. **Diffs neutralizados en vez de revisados:** las lecturas de diff llevan siempre `--no-ext-diff` y `--no-textconv`
   (`diff.external` y `diff.*.textconv` no se ejecutan), así que no entran en la revisión por comando. Se comprobó con git
   2.45.2 que `-c diff.external=` **no** neutraliza (git lanza un programa vacío), por eso se usan los `--no-*`.
6. **Alcance de la entrega:** la fusión de la entrega (`RunWithUserHooksAsync`) **no** pasa por la revisión por comando:
   corre a propósito con los hooks, la identidad y la firma del usuario, tras la aprobación humana (decisiones 7 y 8). Pero
   eso solo es seguro para programas **fuera** del repositorio. Si un callback o el `core.hooksPath` (`.husky/`) apuntan
   **dentro** del repositorio y el diff entregado (del punto de bifurcación al commit entregado) toca esa ruta, la fusión
   ejecutaría la versión del agente con los permisos del usuario antes de que este la ejecutara nunca. Por eso, justo antes
   de la fusión, `DeliverToUserBranchAsync` lista los programas de los callbacks dentro del repositorio
   (`GitCallbacks.InRepositoryPaths`) y la carpeta de hooks si está dentro, y **rechaza** la entrega
   (`UnsafeGitCallbackException`, antes de cualquier fusión) si el diff toca alguno: el usuario revisa el cambio y lo fusiona
   a mano. Los hooks y callbacks fuera del repositorio, y los de dentro que el diff no toca, se ejecutan como siempre. Los
   callbacks dentro del repositorio ya los rechazaba antes la revisión por comando del `status` de la propia entrega (más
   estricta: sin mirar el diff); lo nuevo que cierra esto es la carpeta de hooks, que esa revisión no inspecciona.

- **Consecuencias y límites:**
  - Un repositorio cuyo filtro, driver o textconv legítimo apunte a un **script del propio repositorio por ruta relativa**
    (sin instalarlo fuera) no se puede usar: es indistinguible del ataque, y la tarea falla con el motivo. git-lfs, que usa
    el programa suelto `git-lfs`, es el caso común y sigue funcionando.
  - `worktree add` (crear el worktree) **no** se revisa: hace checkout del commit base intacto, antes de que el agente toque
    nada; su smudge corre la versión versionada del script. Queda como riesgo residual menor, cubierto porque cualquier
    comando posterior (`status`, `reset`…) sí se revisa y fallaría si el script pasó a ser peligroso.
  - **Coste:** una consulta `config` más por comando de contenido (decenas de ms; dominado por el arranque de git). No añade
    red ni escritura.
  - **Repositorios anidados y submódulos (cerrado el 2026-10-17, antes residual).** Un agente con edición de ficheros puede
    plantar `sub/.git/config` con `core.fsmonitor`, un filtro o un `includeIf` en su worktree; `git add -A` lo registra como
    gitlink y una operación posterior podría lanzar un git hijo dentro con esa configuración. Medido con git 2.45.2: un
    embebido sin registrar (gitlink sin `.gitmodules`) **no** hace que `status`/`reset`/`checkout` del padre recursen; pero
    con el submódulo registrado (`.gitmodules`), activo y `submodule.recurse=true` —que el agente puede escribir en el `.git`
    compartido— `reset --hard` **sí** recursa (migra el gitdir del submódulo, un git hijo dentro de él). Se neutraliza por
    comando: `status` y las lecturas de diff llevan `--ignore-submodules=all`; `reset` lleva `--no-recurse-submodules` (los
    tres flags, comprobados aceptados por git 2.45.2, y `--no-recurse-submodules` evita la recursión); `worktree add` lleva
    `-c submodule.recurse=false` (no admite el flag). Además, `CommitAllAsync` **rechaza** confirmar un repositorio embebido:
    tras `add -A`, si hay un gitlink cuyo directorio tiene un `.git` en disco, lanza `EmbeddedRepositoryException` (el motor
    la trata como error del sistema). Un submódulo sin inicializar del commit base (gitlink sin `.git` en disco) se respeta.
  - **Residual anotado (otro paquete):** TEMP es espacio escribible por el agente fuera de las raíces.

## Adenda · 2026-10-17: la entrega vuelve a comprobar HEAD justo antes de la fusión (carrera de HEAD)

La decisión 7 comprueba que HEAD está en la rama aprobada y en el commit aprobado al empezar la entrega
(`ReadUserHeadAsync`), pero entre esa comprobación y el `git merge` hay varias llamadas a git (resolver el commit, el
antecesor, `MERGE_HEAD`, las rutas sucias). Si el usuario hace `git checkout` de otra rama en esa ventana, `git merge
--ff-only` adelantaría **esa** rama, no la aprobada; con `--no-ff` se confirmaría la fusión sobre ella. Se decide:

1. **Justo antes de la fusión** (en los dos modos) se vuelve a leer HEAD (ref simbólica y commit) y, si no es la rama
   destino en la cabeza aprobada, la entrega falla cerrada con `RepositoryChanged` sin tocar ninguna rama.
2. **En `MergeCommit`**, además, se vuelve a comprobar HEAD **antes del `commit`** que cierra la fusión; si cambió, se
   aborta la fusión en curso (`merge --abort`) para no confirmar nada sobre otra rama. `ConcludeAsync` sigue exigiendo que
   HEAD acabe en la rama destino y que contenga el commit entregado.
- **Alcance y límite:** la nueva lectura reduce la ventana a los microsegundos entre esa lectura y el arranque del proceso
  `git merge`, con el candado del repositorio tomado (ningún otro comando de Agent Studio se intercala); el único actor que
  podría colarse es el usuario ejecutando git a mano en ese instante. La única forma de cerrarla del todo sería dejar
  `git merge` y mover la rama con un compare-and-swap por nombre (`update-ref --no-deref <rama> <commit> <esperado>`) más un
  `read-tree -m -u` que actualice árbol e índice, ejecutando a mano el hook `post-merge` y respetando
  `merge.verifySignatures`; se descarta ahora porque cambiaría el contrato de la entrega (que corre a propósito el `git
  merge` del usuario con sus hooks y su firma) y es la operación más crítica del sistema.

## Adenda · 2026-10-17: la captura ve el contenido de los ignorados, con presupuesto e indeterminados (N-07)

La adenda del 2026-10-16 (punto 4) hashea cada fichero de status hasta 8 MiB y representa las carpetas sin seguimiento o
ignoradas (`bin/`) por los nombres, tamaños y fechas de sus ficheros, no por su contenido. La segunda revisión adversarial
(Codex) mostró el hueco: un intercambio de contenido de la misma longitud con la fecha restaurada es invisible, tanto en un
fichero mayor de 8 MiB como dentro de una carpeta ignorada. Se decide:

1. **Se hashea el contenido** (SHA-256) de cada fichero de status y de **cada fichero dentro de una carpeta** sin
   seguimiento o ignorada, combinado en un hash por carpeta junto con los nombres. Así un intercambio de la misma longitud
   con la fecha restaurada sí se detecta (`FileFingerprint.Sha256` pasa a ser exacto para las carpetas).
2. **Dentro de un presupuesto por captura** (`CaptureBudget`, configurable en `GitWorktreeOptions`; por defecto 8 MiB por
   fichero, 64 MiB y 5 000 ficheros en total). Un fichero mayor que el tope por fichero, todo lo que pase del total, y
   cualquier fichero que no se pueda leer, se marcan **indeterminados** (con el motivo) en vez de fiarse de tamaño y fecha.
   **Solo el contenido de las carpetas ignoradas puede agotar el presupuesto:** los ficheros sueltos que lista el status
   (versionados, modificados, sin seguimiento o ignorados: el trabajo del usuario) se hashean **primero** y solo contra el
   tope por fichero, así que una carpeta ignorada grande nunca los vuelve indeterminados; la configuración y los hooks,
   igual. Las carpetas se hashean **al final**, en orden ordenado, contra el total compartido, de modo que ante el mismo
   repositorio dos capturas dejan fuera las mismas carpetas.
3. **`UserRepositoryState.Compare` distingue los indeterminados** (adición en Application): `CompareInDetail` devuelve las
   diferencias y los indeterminados por separado, y `Compare` los concatena (diferencias primero, indeterminados después),
   de modo que el motor actual, que bloquea ante cualquier salida no vacía, sigue fallando cerrado sin cambios. Dos
   fingerprints son indeterminados si cualquiera de los dos lo es: un contenido que no se pudo verificar bloquea aunque las
   dos capturas lo marquen igual. El motor (otro paquete) tratará los indeterminados como bloqueantes.
4. **Coste medido aquí (caché de disco templada, segunda captura):** en este worktree, ≈ 2,4 s (primera, en frío, ≈ 14 s);
   en el checkout principal, ≈ 2,2 s (en frío, ≈ 5 s). Antes de N-07 la captura de metadatos era ≈ 0,75-1,15 s en el
   principal; el sobrecoste lo domina el hashing del contenido hasta agotar el presupuesto.
- **Límite importante, sin resolver (lo trata el paquete del motor):** en un repositorio con muchos ficheros o carpetas
  ignoradas grandes, el presupuesto se agota y **muchas rutas quedan indeterminadas en un repositorio sin tocar**. Medido:
  el propio repositorio de Agent Studio da 24-25 rutas indeterminadas entre dos capturas idénticas (sus `bin/`, `obj/`,
  cachés de NuGet y `.claude/worktrees/` superan 64 MiB / 5 000 ficheros). Como `Compare` las incluye y el motor bloquea
  ante cualquier salida, cualquier ejecución sobre un repositorio así fallaría todos los pasos en cuanto esto entre. Es el
  comportamiento que pide el hallazgo (fallar cerrado ante contenido que no se puede verificar), pero el presupuesto por
  defecto hay que ajustarlo o el motor debe decidir qué indeterminados bloquean; queda anotado para ese paquete.

## Adenda · 2026-10-17: el motor decide qué indeterminados bloquean, según quién puede escribir a integridad media

La adenda anterior (N-07) deja abierto su límite importante: con el presupuesto por defecto, dos capturas idénticas del
propio repositorio de Agent Studio dan 24-25 rutas indeterminadas, así que cualquier ejecución fallaría en cada paso. Lo
que el hashing de contenido defiende es un intercambio de la misma longitud con la fecha restaurada **dentro de una carpeta
ignorada**, y eso exige un proceso que escriba en el repositorio del usuario a integridad **media**. Se decide:

1. **Modelo de amenaza.** La verificación aislada corre a integridad baja
   ([ADR-0017](0017-verificacion-aislada-baja-integridad.md)) y no puede escribir ahí; los ejecutores por defecto no tienen
   shell, y Claude con `Read`/`Grep`/`Glob`/`Edit`/`Write` queda confinado a sus carpetas de trabajo. Solo pueden hacerlo:
   un ejecutor en un worktree `Write` con `Bash` en sus `AllowedTools` (el único caso en que
   `AgentAttemptWorker.Permissions` concede shell); un ejecutor en `Write` sobre Codex,
   cuyo sandbox siempre tiene shell (se trata como capaz, por prudencia); y un paso del perfil sin aislamiento
   (`"isolation": "none"`, o cualquiera que no sea baja integridad) que el plan ejecuta, como verificación de una tarea o como
   herramienta.
2. **La capacidad se calcula una vez** (`UserRepositoryGuard.CanWriteAtMediumIntegrity`, del roster, las tareas del plan y el
   perfil) y se pasa al constructor del guardián: al empezar (`Orchestrator.StartAsync`), al reanudar (`RunResumer`, desde la
   cabecera ya comprobada contra `RunStarted`; la línea base se toma ahí, antes de que la recuperación toque ningún
   worktree) y al planificar (`Planner`: nunca, el orquestador solo lee). Falla cerrado: por defecto el guardián bloquea, y
   una tarea cuyo agente no está en el roster o un paso que el perfil no tiene cuentan como capaces. Un paso del perfil que
   ninguna tarea ejecuta no cuenta: nunca corre. El juez solo lee y no cuenta.
3. **Qué bloquea siempre** (`CompareInDetail` en lugar de `Compare`): toda diferencia; todo indeterminado que no sea una
   carpeta ignorada, es decir, un fichero versionado, sin seguimiento o ignorado pasado del tope por fichero o ilegible, y una
   carpeta sin seguimiento (`?? dir/`, un repositorio anidado); y todo fichero de git indeterminado (configuración, hooks).
   **Carpeta ignorada** es una ruta que las dos capturas listan con la línea `!! ruta/` y ninguna otra, y cuyo fingerprint es
   indeterminado en alguna de ellas; una carpeta hasheada en las dos con otro hash es una diferencia real. Una carpeta
   ignorada indeterminada solo bloquea si la ejecución puede escribir a integridad media.
4. **Si no puede, se registra en vez de bloquear.** El guardián deja esas carpetas fuera de la comparación (como deja
   `.agentstudio/`) y registra `AuditAlertRaised` con el código `user-repository.capture-indeterminate`, sin tareas y con el
   `RunId` de la ejecución, nombrando cada ruta con su motivo. Una vez por ruta y por guardián: la primera comprobación que la
   deja pasar la registra, y una carpeta que deja de poder verificarse más tarde tiene su propia alerta. Las comprobaciones de
   trabajadores en paralelo registran de una en una y una ruta solo cuenta como registrada cuando su alerta está en el
   registro, así que ninguna comprobación deja pasar una carpeta antes de que conste. Un guardián que relaja sin a quién
   registrarlo se rechaza al construirlo. Al reanudar, el guardián nuevo (línea base nueva) vuelve a registrarlas una vez; la
   alerta no cuenta como integridad rota (`RunResumer` solo atiende a `user-repository.changed`).
5. **Medido aquí** (dos capturas reales seguidas, sin cambios, de solo lectura): en el checkout principal, 0 diferencias y
   25 indeterminados, todos `!! …/` (`.claude/worktrees/`, el `bin/` y el `obj/` de cada proyecto, `monaco/` y `xterm/`); con
   el roster por defecto la ejecución termina con **0 entradas bloqueantes** y una alerta con las 25. En este worktree, 18
   indeterminados y 0 bloqueantes. Cada captura, ≈ 0,7 s en caliente (3,3 s en frío).

- **Consecuencias y límites:**
  - Se cierra el límite importante de N-07: el repositorio de Agent Studio admite ejecuciones con el roster por defecto.
  - Una ejecución con un ejecutor con `Bash` o sobre Codex en `Write`, o con un paso sin aislamiento que use, sigue fallando
    en cada paso en un repositorio así. Es el precio de fallar cerrado; para usarla, el presupuesto (`CaptureBudget` en
    `GitWorktreeOptions`) tiene que cubrir las carpetas ignoradas, o hay que vaciarlas antes de la ejecución.
  - En una ejecución que no puede escribir ahí, un cambio dentro de una carpeta ignorada sin verificar (del usuario o de algo
    ajeno a la ejecución) no se ve; la alerta lo dice. Un fichero ignorado pasado del tope por fichero sigue bloqueando: es
    trabajo del usuario y se hashea primero; el remedio es subir el tope.
  - La alerta no nombra la tarea: el guardián no sabe qué paso comprueba.
  - **Pendiente:** Gemini en `Write` corre en `auto_edit` sin sandbox y no entra en la puerta (su shell pide una aprobación
    que el modo sin interfaz no da); hay que revisarlo cuando Gemini entre en el roster.

## Adenda · 2026-10-17: lo que la fusión de la entrega rechazaría se rechaza antes de pedir la aprobación

La comprobación del punto 6 de la adenda N-01 (un diff entregado que reescribe un hook de un `core.hooksPath` dentro del
repositorio, o el programa de un callback del repositorio) corría dentro de `DeliverToUserBranchAsync`, es decir, después de
que el usuario aprobara: la aprobación se gastaba y la entrega fallaba. Se decide:

1. **Comprobación previa en el puerto** (adición en Application): `IWorktreeManager.FindDeliveryRefusalsAsync(repo,
   head, commit)` hace el mismo análisis, de solo lectura y sin el candado del repositorio (como `FindHookPathsAsync`), y
   devuelve un `DeliveryRefusal(Key, Path)` por cada programa que la fusión ejecutaría desde dentro y el diff reescribe. Su
   cuerpo por defecto rechaza, como el de los demás miembros posteriores.
2. **`DeliveryService` la llama antes de atar y pedir la aprobación crítica**, con la cabeza del usuario y el commit que
   aprobó el juez. Si encuentra algo, no pide nada: devuelve un mensaje en castellano que nombra cada ruta con su clave, dice
   que no se ha pedido ninguna aprobación y que la rama no se ha tocado, y pide revisar el cambio y fusionarlo a mano.
3. **La comprobación dentro de la fusión se queda como segunda línea**: el repositorio puede cambiar entre la pregunta previa
   y la fusión (un `core.hooksPath` puesto mientras se espera la aprobación). Entonces la aprobación ya se gastó y la entrega
   falla con `UnsafeGitCallbackException`, sin fusionar ni ejecutar nada.

- **Consecuencias:** una entrega que toca la carpeta de un `core.hooksPath` del repositorio ya no llega a pedir aprobación
  (antes la pedía nombrando esos ficheros y después fallaba); la aprobación sigue nombrando los hooks de las carpetas
  conocidas (`.husky/`, `.githooks/`, `.git/hooks/`) que no son la configurada. Cuesta tres consultas de git más antes de
  preguntar (`merge-base`, `diff` y `config`), sin escritura ni red.

## Adenda · 2026-10-17: la captura codifica cada entrada sin ambigüedad, compara los metadatos de todas y ve lo que el status no enseña (R3-02 a R3-04)

Desde R3-06, `CompareInDetail` solo deja como indeterminada una ruta que ninguna captura verificó y cuyos metadatos
(`FileFingerprint.Approximation`) coinciden en las dos. Pero los metadatos de una carpeta eran solo un recuento
(`folder:N files`), y la tercera revisión adversarial (Codex) mostró además que su hash de contenido era ambiguo. Se decide:

1. **Codificación canónica del contenido de una carpeta.** El hash concatenaba `nombre + "\n"` y los bytes sin longitudes:
   `a.txt`=`A` y `b.txt`=`B` daban lo mismo que `a.txt`=`Ab.txt\nB`. Ahora cada entrada es un registro de
   `ContentHash.Canonical` (cada campo con su longitud): tipo (fichero, carpeta o enlace), ruta relativa y el SHA-256 de su
   contenido o el destino del enlace; al final, el número de entradas. Se hashean las unidades UTF-16 tal cual, así que un
   nombre que no es Unicode válido no se confunde con otro.
2. **Los metadatos de cada entrada.** La `Approximation` de una carpeta es ahora un resumen canónico de los metadatos de
   **todas** sus entradas, también las que quedan fuera del presupuesto de contenido: tipo, ruta relativa, tamaño, fecha de
   modificación y destino de los enlaces, más el número de entradas (`folder:N entries;metadata:<sha256>`). Cuando el
   contenido ya no se puede verificar, el recorrido deja de leer contenido pero sigue listando. Así, en una carpeta sin
   verificar, añadir, quitar, cambiar de tamaño o tocar un fichero es una diferencia; solo un intercambio de la misma
   longitud con la fecha restaurada sigue indeterminado, que es el residual que pesa el motor (adenda anterior).
3. **La fecha de una subcarpeta o de un enlace se lee de la propia entrada**, no del listado de su carpeta. Medido en NTFS:
   el listado guarda una copia de la fecha de cada subcarpeta que se refresca tarde, cuando la subcarpeta se vuelve a abrir,
   y eso es justo lo que hace recorrerla. Leída del listado, la primera captura tras escribir subcarpetas (una compilación)
   cambiaba lo que leía la segunda: en la prueba, 20-25 de 30 subcarpetas recién escritas, y dos capturas de un
   repositorio sin tocar daban una diferencia. Cuesta una consulta de atributos por subcarpeta y por enlace.
4. **Tope de metadatos** (`CaptureBudget.MaxFolderEntries`, configurable; por defecto 200 000 entradas por captura). Se
   gasta en el orden ordenado de las carpetas, como el presupuesto de contenido, así que dos capturas del mismo repositorio
   dejan fuera las mismas. Una carpeta que lo supera se queda sin `Approximation` (vacía) e indeterminada: nunca se puede
   comparar y siempre bloquea.
5. **Una carpeta que no se puede listar nunca pasa por vacía (R3-03).** El listado ignoraba lo inaccesible
   (`IgnoreInaccessible`) y un error devolvía una lista vacía: una subcarpeta cuya ACL niega listarla, aunque deje abrir
   sus ficheros por su nombre, se daba por vacía. Ahora el listado no ignora nada, y cualquier fallo a cualquier
   profundidad (listar una carpeta, o leer la fecha o el destino de una entrada) deja la carpeta entera sin
   `Approximation`, indeterminada y con el motivo (`the folder 'locked/' in it could not be listed`): siempre bloquea. Una
   carpeta vacía sigue siendo una carpeta verificada, con cero entradas. Lo mismo vale para la carpeta de hooks: git puede
   ejecutar un hook de una subcarpeta que no se puede listar, así que esa subcarpeta queda como fichero de git sin
   metadatos, con su ruta terminada en `/`, y bloquea.
6. **Lo que el status no enseña (R3-04).** La captura solo hasheaba las rutas que lista `git status`, y dos clases de
   rutas versionadas nunca salen en él: un fichero que el índice marca `assume-unchanged` o `skip-worktree` (git no mira su
   árbol de trabajo, así que cambiarlo no da línea de status ni cambia el índice) y un submódulo inicializado (el status
   lleva `--ignore-submodules=all`, N-01). Se sacan del mismo `git ls-files -z --stage -v` que ya da el hash del índice, sin
   otro proceso de git: una etiqueta en minúscula (`assume-unchanged`) o `S` (`skip-worktree`), y el modo `160000` de un
   gitlink cuya carpeta tiene un `.git` en disco.
   - Van a un campo nuevo y aditivo, `UserRepositoryState.HiddenTracked` (por ruta; una carpeta acaba en `/`), que
     `CompareInDetail` compara al final, como los ficheros de git: lo que aparece, desaparece o cambia es una diferencia
     (`Hidden tracked path '…'`). Una ruta también aparece o desaparece con su marca en el índice o con el checkout de su
     submódulo, y el mensaje lo dice.
   - Un fichero oculto se hashea solo contra el tope por fichero, como los del status. La carpeta de un submódulo se trata
     como una carpeta ignorada: los mismos dos resúmenes, y el presupuesto compartido en el mismo orden ordenado.
   - **Nunca se ejecuta git dentro de un submódulo**, el camino del ataque N-01: se leen el índice del repositorio del
     usuario y los ficheros del disco. Lo comprueba una prueba que registra cada proceso que lanza la captura.
   - Un indeterminado de `HiddenTracked` bloquea siempre, también en una ejecución sin escritores a integridad media: el
     guardián solo deja pasar carpetas ignoradas del status (`!! ruta/`), y un fichero versionado o un submódulo nunca lo
     son.

- **Medido** (dos capturas reales seguidas, de solo lectura, sin cambios entre ellas, con la prueba
  `CaptureMeasurementTests`, que activa `AGENTSTUDIO_CAPTURE_MEASURE_ROOT`):
  - En este worktree, compilado y con el código final (32 líneas de status, 25 carpetas ignoradas con 1 368 entradas,
    ninguna ruta oculta): 0 diferencias, 18 indeterminadas (todas `bin/` u `obj/`, por el presupuesto de contenido) y 0 sin
    metadatos. En caliente, 0,9-1,0 s por captura; la primera tras compilar, 11 s (en frío: el antivirus revisa los
    binarios recién escritos). Con solo R3-02 y la CPU al 100 % por las compilaciones de otros dos agentes: 2,2-2,6 s, y
    34 s la primera.
  - Un repositorio sintético con una carpeta ignorada de 103 020 entradas: 2,2-2,9 s por captura con la máquina menos
    cargada y 7-9 s al 100 % de CPU, con 0 diferencias, también justo después de añadirle 2 000 ficheros nuevos.
  - Las carpetas ignoradas del checkout principal suman 4 544 entradas (3 843 ficheros, unos 225 MiB): listarlas sin git
    cuesta 110 ms. El tope por defecto deja un margen de más de 40 veces.
  - **Pendiente:** las dos capturas del checkout principal. El aislamiento del agente que hizo este cambio no le deja
    ejecutar git contra el checkout compartido. Se miden desde el checkout con
    `$env:AGENTSTUDIO_CAPTURE_MEASURE_ROOT = 'C:\Personal Projects\Agent Studio'; dotnet test --project tests/AgentStudio.Tests -- --filter-class "AgentStudio.Tests.Infrastructure.Git.CaptureMeasurementTests"`;
    el informe va a la salida de la prueba, y al fichero que nombre `AGENTSTUDIO_CAPTURE_MEASURE_REPORT`. Con agentes
    trabajando en `.claude/worktrees/`, una diferencia en esa carpeta es actividad concurrente, no un fallo.
- **Límites:**
  - Un submódulo sin inicializar (un gitlink sin `.git` en disco) no se captura: lo que se escriba en su carpeta no lo ven
    ni el status ni la captura.
  - Tampoco se capturan la configuración ni los hooks de los submódulos (`.git/modules/<nombre>/`). Un git del usuario que
    entre en el submódulo, como un `git status` sin `--ignore-submodules`, los ejecutaría.

## Adenda · 2026-10-18: callbacks por lista blanca, entorno sin `GIT_*` heredado y entrega sin hooks ni firma (R3-01, R3-07, R3-05)

La tercera revisión adversarial (Codex), juzgada contra [ADR-0018](0018-modelo-de-amenaza-fase-3.md), dejó tres hallazgos
dentro del modelo (contenido del repositorio y agentes con sus capacidades por defecto). Se decide:

1. **R3-01 · Un callback solo se acepta por lista blanca** (sustituye los puntos 2 y 3 de la adenda N-01).
   - **Por qué:** la regla de N-01 era una lista negra. Un primer token suelto contaba como un nombre seguro del PATH y
     los tokens que empezaban por `-` no se analizaban. git ejecuta los callbacks con `sh -c`: medido con git 2.45.2,
     `filter.<driver>.clean = exec ./tools/x.sh` y `= ! ./tools/x.sh` ejecutan el script del agente.
   - **Regla nueva** (`GitCallbacks.Classify`). Un valor se acepta solo si se cumplen las tres condiciones:
     - cada token está en el juego `[A-Za-z0-9%_.,:=/\+-]`. El primero puede ser una ruta absoluta entre comillas
       simples o dobles, y nada más lleva comillas. Las simples se admiten porque la configuración real de esta máquina
       las usa (`!'C:\Program Files\GitHub CLI\gh.exe' auth git-credential`);
     - el primer token no es un intérprete ni una shell (se rechaza sea cual sea su argumento, también
       `sh C:\tools\x.sh`, antes admitido) ni un builtin (`exec`, `eval`, `.`, `source`, `command`, `builtin`…);
     - el programa resuelve a un fichero cuya ruta final queda fuera del repositorio y de los worktrees. Un nombre suelto
       se busca con `IExecutableLocator` y después en las carpetas del propio git (`mingw64\bin`, `usr\bin`…, donde
       viven `astextplain` y el gestor de credenciales). Una ruta absoluta se toma tal cual. La ruta final resuelve los
       enlaces con `GetFinalPathNameByHandle`. Lo que no resuelve se rechaza.

     Se mantienen los casos especiales de `credential.*.helper` (el `!` se quita; un nombre suelto es
     `git-credential-<nombre>`) y la regla del origen (configuración dentro de un worktree).
   - **Claves:** se añaden `gpg.*.program`, `gpg.ssh.defaultKeyCommand`, `uploadpack.packObjectsHook`,
     `core.alternateRefsCommand` y `remote.*.uploadpack`/`receivepack`. Se dejan fuera a propósito claves cuyo valor es un
     booleano, un correo o un subcomando, porque tratarlas como programa rechazaría repositorios legítimos:
     - `core.fsmonitor`, ya forzado a `false` con `-c`;
     - `core.pager` y `pager.*`: con `GIT_PAGER=cat` y la salida nunca en una terminal, ningún paginador se ejecuta;
     - `sendemail.*` y `alias.*`: ningún comando interno los usa.
   - **Neutralización en todo comando interno** (cerrar todo transporte es lo que impide que corran los helpers de ssh,
     askpass, credenciales o un remote-helper; ningún comando interno necesita transporte):
     - cerrar el transporte carga el peso. `-c protocol.allow=never` es solo el valor por defecto para un protocolo sin
       nombre; git mira antes `protocol.<nombre>.allow`, y el repositorio puede ponerlo a `always`. Medido con git
       2.45.2: con `protocol.file.allow=always` en el repositorio, `-c protocol.allow=never` **no** cerró el transporte
       `file` (un `ls-remote file://…` funcionó); añadir `-c protocol.file.allow=never` volvió a cerrarlo. Por eso se
       fuerza `never` también en cada transporte con nombre (`file`, `git`, `ssh`, `http`, `https`, `ftp`, `ftps`, `ext`),
       y la línea de comandos gana al ser clave de un solo valor. Un remote-helper con un nombre arbitrario necesitaría un
       `git-remote-<nombre>` en el PATH, que ADR-0018 deja fuera del modelo;
     - `-c credential.helper=` reinicia **toda** la lista de helpers, incluidas las de `credential.<url>.helper`. Medido:
       sin el reinicio el helper con URL se ejecuta; con `-c credential.helper=` no se ejecuta y git cae en la petición por
       terminal, deshabilitada;
     - `-c core.askPass=` y `-c core.sshCommand=` quedan vacíos (`git config` los da vacíos); no se midió qué haría git con
       un transporte permitido, y no importa, porque ya no se abre ninguno;
     - además `gc.auto=0`, `maintenance.auto=false`, `GIT_PAGER=cat` y `GIT_TERMINAL_PROMPT=0`.
   - **Límites:**
     - se rechazan un intérprete con un script absoluto fuera de las raíces, un programa bajo una ruta con paréntesis
       (`Program Files (x86)`) y un programa que no está instalado;
     - se comprobó con la configuración real de esta máquina: git-lfs (`cmd\git-lfs.exe`), astextplain (`usr\bin`), el
       gestor de credenciales (`manager`) y el helper de `gh` pasan.
2. **R3-07 · Se retira toda variable `GIT_*` heredada** (amplía la decisión 10).
   - **Por qué:** medido con git 2.45.2, con `GIT_CONFIG=<fichero benigno>` la consulta `git config --get-regexp`
     (también con `--git-dir` explícito) solo lee ese fichero, mientras que `git add` sigue leyendo la configuración local
     y ejecuta su filtro. La inspección y la ejecución veían configuraciones distintas.
   - **Decisión:** `BuildEnvironment` retira todas las `GIT_*` heredadas salvo las tres que fija: `GIT_TERMINAL_PROMPT`,
     `GIT_OPTIONAL_LOCKS` y `GIT_PAGER`. Las conocidas siguen listadas para quedar siempre presentes y a nulo, y se añaden
     `GIT_CONFIG`, `GIT_CONFIG_GLOBAL` y `GIT_CONFIG_SYSTEM`. La configuración global y de sistema en disco no se toca, y
     `HOME` y `USERPROFILE` se conservan, para que sigan aplicando `core.autocrlf` y los `filter.lfs.*` que escribe
     `git lfs install`.
   - **Límite:** también se retiran las `GIT_LFS_*` (son de git-lfs, no de git; por ejemplo `GIT_LFS_SKIP_SMUDGE`), lo que
     puede cambiar si un smudge descarga contenido.
3. **R3-05 · La entrega no ejecuta código que dependa del repositorio.** Revierte la viñeta de la decisión 8 «En la entrega
   se respetan…» y la de nombrar los ficheros de hooks en la aprobación. Sustituye el punto 6 de la adenda N-01 y la
   comprobación por diff de la adenda «lo que la fusión de la entrega rechazaría».
   - **Por qué:** un hook en el que el usuario confía puede ejecutar un script versionado que no está en `hooksPath` (un
     `post-merge` que lanza `sh tools/postmerge.sh`). Si el diff entregado cambia ese script, la fusión con los hooks del
     usuario ejecuta la versión entregada con sus permisos. Nombrar los ficheros de hooks no lo cubre, y analizar de qué
     depende un hook no es factible. Medido en la prueba: un `git merge --ff-only` normal ejecuta el script entregado (el
     control) y la entrega no.
   - **Decisión:** la fusión de la entrega se ejecuta como cualquier comando de contenido (`merge` pasa a esa lista). Es
     `merge --ff-only`, o `merge --no-ff --no-commit` seguido de `commit -F -`, y `merge --abort` cuando hay que abortar.
     Corre así:
     - con la misma lista blanca;
     - con `core.hooksPath` apuntando a la carpeta vacía propia;
     - con `-c commit.gpgsign=false -c tag.gpgsign=false`, colocados detrás de `--git-dir`/`--work-tree` para que la
       inspección los siga leyendo;
     - con el mensaje por stdin;
     - con la identidad del usuario (no lleva `-c user.*`).

     La aprobación dice que no se ejecutan sus hooks ni se firma, y da la orden exacta para fusionar a mano tras revisar:
     `git merge --ff-only <commit>` o `git merge --no-ff <commit>`.
   - **Se retira lo que queda sin objeto:**
     - la comprobación por diff (`FindRepositoryProgramsRewrittenAsync`, la de reescritura del `hooksPath` del repositorio);
     - `FindHookPathsAsync` en la implementación de git. El miembro del puerto se queda con su cuerpo por defecto, por
       compatibilidad, y nadie lo llama;
     - `RunWithUserHooksAsync` y `GitCallbacks.InRepositoryPaths`.
   - **`FindDeliveryRefusalsAsync` se mantiene.** Ahora devuelve los rechazos de la lista blanca sobre la configuración
     efectiva del repositorio del usuario, sin depender del diff. `DeliveryRefusal` gana `Reason`, una adición.
     - En la práctica, un callback del repositorio ya hace fallar cerrado el primer `InspectAsync`, porque su `status`
       pasa por la lista blanca desde N-01. Ocurre antes de pedir nada, con `UnsafeGitCallbackException` y su mensaje en
       inglés.
     - La comprobación previa cubre un cambio entre esa inspección y la pregunta.
     - Tras la aprobación, la inspección repetida y la fusión vuelven a rechazar.

- **Consecuencias:**
  - Se cierra el riesgo abierto de las consecuencias de la decisión original (un hook plantado fuera del diff se
    ejecutaría al aprobar): en la entrega no corre ningún hook.
  - La adenda de la carrera de HEAD descartaba el compare-and-swap con `update-ref` más `read-tree -m -u` en parte porque
    «cambiaría el contrato de la entrega (que corre a propósito el `git merge` del usuario con sus hooks y su firma)». Ese
    motivo ya no vale. No se implementa aquí (se sigue con `git merge` y la relectura justo antes), pero queda como opción.
  - Quien quiera sus hooks o su firma fusiona a mano con la orden que da la aprobación.
  - `merge.verifySignatures` del usuario sigue aplicando a `git merge`, como antes: con los commits de Agent Studio, que no
    van firmados, la entrega fallaría.
  - Un hook del usuario ya no puede dejar una fusión en curso (`IncompleteMerge`). El estado se sigue informando si el
    commit falla por otro motivo.
  - La opción `DeliveryTimeout` queda sin uso: la entrega usa el tiempo límite de los comandos.

## Adenda · 2026-10-18: sobre el contenido solo corren los filtros de git-lfs (R4-01)

La lista blanca de la adenda R3-01 acepta un programa fuera del repositorio que no sea un intérprete. Sin embargo, un
programa así puede ejecutar código de un fichero del repositorio: MSBuild compila el proyecto que recibe, o el que
encuentra en su carpeta, y otras herramientas cargan un plugin o una configuración junto al fichero. Un filtro (`clean`,
`smudge`, `process`) o un driver de fusión corre en cada `add`, `checkout` o fusión interna, sobre ficheros que puede
haber escrito un agente. Por eso la lista blanca no basta para esas claves. Se decide:

1. **Filtros:** solo se aceptan los que instala git-lfs, con sus argumentos exactos: `clean -- %f`, `smudge -- %f`,
   `smudge --skip -- %f`, `filter-process` y `filter-process --skip`. El programa es `git-lfs`, o una ruta absoluta cuyo
   fichero se llama `git-lfs` o `git-lfs.exe`. Sigue haciendo falta que su ruta final quede fuera del repositorio y de
   los worktrees.
2. **Drivers de fusión:** no se acepta ninguno. Los integrados de git (`text`, `binary`, `union`) no se configuran con
   `driver` y siguen funcionando.
3. **El resto de claves con programa** (textconv, ssh, askpass, credenciales, gpg, editor) mantiene la lista blanca de
   R3-01. Además, ningún comando interno las ejecuta: cada diff lleva `--no-textconv --no-ext-diff`, los transportes están
   cerrados, `credential.helper` va vacío y las firmas, apagadas.

- **Consecuencias:**
  - Un repositorio con otro filtro (git-crypt o un formateador, por ejemplo) o con un driver de fusión propio no se puede
    usar con Agent Studio: cada comando interno falla con el motivo. Queda para más adelante una lista de programas de
    confianza en la configuración de Agent Studio, fuera del repositorio.
  - git-lfs sigue funcionando. Lee `.lfsconfig` del repositorio, pero solo honra de él un conjunto de claves (URL,
    `fetchinclude`, `fetchexclude`…) que no lanzan programas. Medido en la adenda de A-03 (más abajo): git-lfs 3.5.1
    descarta de `.lfsconfig` las claves con programa, así que no es un vector de ejecución. Que haga su propio tráfico
    de red al descargar objetos queda como residual, igual que la red de los procesos Low
    ([ADR-0018](0018-modelo-de-amenaza-fase-3.md)).

## Adenda · 2026-10-18: el worktree nuevo se inspecciona en su propio contexto, y git-lfs no ejecuta callbacks transitivos (A-02, A-03)

La cuarta revisión adversarial (Codex), juzgada contra [ADR-0018](0018-modelo-de-amenaza-fase-3.md), dejó dos hallazgos
dentro del modelo (contenido del repositorio y agentes con sus capacidades por defecto). Se decide:

1. **A-02 · `worktree add` comprobaba la configuración del padre, pero su checkout corre en otro contexto.**
   - **Por qué:** `GitWorktreeManager` creaba el worktree con `worktree add` tras inspeccionar la configuración
     **efectiva del padre** (`GitCommandRunner`, sin `--git-dir`/`--work-tree`). El checkout que hace `worktree add`
     resuelve la configuración con el `GIT_DIR`/`GIT_WORK_TREE` del **worktree nuevo**, donde un
     `includeIf "gitdir:.../worktrees/..."` o un `config.worktree` que el padre no ve puede activar un filtro que nunca
     pasó por `GitCallbacks`. Además, `Orchestration/Planning/Planner` creaba el worktree `_base` **antes** de que el
     guardián tomara su línea base.
   - **Medido con git 2.45.2** (offline, repos desechables):
     - un `includeIf "gitdir:**/worktrees/**"` puesto en el `.git/config` compartido se activa en el contexto del
       worktree nuevo —tanto en resolución natural como con `--git-dir`/`--work-tree` explícitos, en forma `C:/` y
       `C:\`— y **no** en el padre (cuyo gitdir es `.git`); `includeIf "gitdir:**/.git"` es justo al revés;
     - un `config.worktree` (con `extensions.worktreeConfig=true`) también está activo solo en el worktree;
     - `worktree add --no-checkout` deja HEAD resoluble y el árbol sin poblar; un `reset --hard` posterior lo puebla;
     - con el código anterior (checkout completo) ese `includeIf` ejecutó el smudge del agente al crear el worktree
       (centinela ejecutado): el hallazgo, reproducido.
   - **Decisión** (`GitWorktreeManager.EnsureAsync`):
     1. crear el worktree con `worktree add --no-checkout` (su creación ya no hace checkout ni corre filtros);
     2. validar su carpeta de administración: `VerifyRegistration` prueba que vive donde Agent Studio la deriva, y se
        añade que no se llega a ella por un enlace (`RequireAdminDirectoryWithoutLinks`, como el guardián de las refs);
     3. inspeccionar la configuración **efectiva del worktree nuevo** con `--git-dir`/`--work-tree` explícitos y el
        worktree como directorio de trabajo, por la misma lista blanca (`ClassifyRepositoryCallbacksAsync`), de modo que
        un `includeIf "gitdir:"` y un `config.worktree` que el padre no ve sí entran; si rechaza, se deshace el worktree
        a medio crear (`DropRegistrationAsync`) y se lanza `UnsafeGitCallbackException`;
     4. hacer el checkout con un `reset --hard --no-recurse-submodules` que vuelve a pasar por la compuerta de contenido
        en ese mismo contexto (defensa en profundidad y contra una carrera entre 3 y 4; si rechaza, también se deshace).
     La inspección del padre en `worktree add` se mantiene como primera defensa. Una creación interrumpida (sin índice)
     la sigue marcando incompleta `VerifyCompleteAsync`, así que un worktree a medias nunca se reutiliza.
   - **Planner:** la línea base del guardián se toma **antes** de crear `_base`, para que un programa que su creación
     pudiera ejecutar se vea como diferencia y no como la normalidad. `Orchestrator` y `RunResumer` ya lo hacían.

2. **A-03 · git-lfs ejecuta callbacks transitivos que no se inspeccionaban.**
   - **Por qué:** `GitCallbacks.Pattern` no cubría `lfs.*`. `AnalyzeContentCallback` acepta los filtros exactos de
     git-lfs, pero git-lfs ejecuta además los programas que nombran sus propias claves: `lfs.extension.<n>.clean` y
     `.smudge`, `lfs.customtransfer.<n>.path` y `.args`, y `lfs.standalonetransferagent`.
   - **Medido con git-lfs 3.5.1** (offline, solo objetos locales, con centinelas):
     - `lfs.extension.<n>.clean` en el `.git/config` local **ejecutó** el programa del agente en `git add` de un fichero
       lfs (centinela, arg=`clean`). El `.smudge` no se disparó en el checkout de la medición; se anota sin afirmar que
       corra, porque el rechazo cubre las dos claves;
     - `lfs.customtransfer.<n>.path`/`.args` y `lfs.standalonetransferagent` en el config local se **registran**
       (`DownloadTransfers=basic,<agente>,…`); solo corren en una transferencia (push/fetch), que ningún comando interno
       hace offline;
     - en el `.lfsconfig` versionado, git-lfs 3.5.1 **descarta** todas esas claves con programa con un aviso explícito
       («These unsafe '.lfsconfig' keys were ignored»: extension.clean/smudge, customtransfer.path/args,
       standalonetransferagent); de `.lfsconfig` solo honra claves sin programa (url, fetchinclude). O sea, `.lfsconfig`
       **no** es vector de ejecución en 3.5.1.
   - **Decisión** (`GitCallbacks`):
     - se añade `lfs` al `Pattern`; `IsCallbackKey` clasifica solo las claves con programa (`lfs.extension.<n>.clean`/
       `.smudge`, `lfs.customtransfer.<n>.path`, `lfs.standalonetransferagent`) y deja fuera las demás (`lfs.url`,
       `lfs.fetchinclude`, `lfs.concurrenttransfers`, `lfs.repositoryformatversion`, `lfs.<url>.access`, `.args`);
     - `Classify` las **rechaza de plano**, como un driver de fusión, sea cual sea su valor: en el contenido solo corren
       los filtros base de git-lfs, ni una extensión ni un custom transfer;
     - **no** se añade un escáner de `.lfsconfig`: la condición del hallazgo (que `.lfsconfig` pueda habilitar una clave
       con programa) es **falsa** en git-lfs 3.5.1, que se protege solo. La afirmación queda fijada a 3.5.1 y hay que
       volver a medirla al actualizar git-lfs, como se hace con las versiones medidas en ADR-0018.
   - **Consecuencias y límites:**
     - un repositorio con una extensión de git-lfs o un custom transfer legítimos no se puede usar: cada comando interno
       falla con el motivo. Los de transferencia solo corren en push/fetch, pero se rechazan igual (fallo cerrado);
     - se corrige la línea de la adenda de R4-01 que decía que el comportamiento de `.lfsconfig` «no se ha verificado».

- **Hallazgo separado, fuera de A-03 (anotado para otro paquete):** git-lfs real instala sus hooks (`post-checkout`,
  `post-commit`, `post-merge`, `pre-push`) en la carpeta que nombra `core.hooksPath` cuando corre su `filter-process`
  (medido con 3.5.1). Agent Studio apunta `core.hooksPath` a la carpeta vacía `.no-hooks`, así que tras un clean/smudge
  de git-lfs en un comando interno, el siguiente comando ve `.no-hooks` no vacía y `EnsureNoHookCanRun` falla cerrado.
  Es seguro (no corre ningún hook plantado), pero un repositorio git-lfs en el que un agente toque un fichero seguido
  haría fallar la tarea. No es un callback transitivo, así que queda para un paquete aparte. **Resuelto** en la adenda
  del fichero vacío como `core.hooksPath` (más abajo), que corrige además el «es seguro»: en la fusión de la entrega, el
  `post-merge` que git-lfs escribía en la carpeta corría en el mismo comando.

## Adenda · 2026-10-18: git no escribe en su administración a través de un enlace, y ningún comando interno busca objetos ni abre un transporte (H-03, H-04, A-03)

La quinta revisión adversarial (Codex), juzgada contra [ADR-0018](0018-modelo-de-amenaza-fase-3.md), dejó dos hallazgos
dentro del modelo: enlaces plantados antes de un paso, y contenido y configuración del repositorio. Dejó además un hueco
literal de A-03. Se decide:

1. **H-03 · Las escrituras de git en la administración del repositorio se comprobaban después de hacerlas, o nunca.**
   - **Por qué:**
     - `worktree add --no-checkout` corría antes de comprobar su carpeta de administración (A-02).
     - Los escritores antiguos de refs (`CreateRefIfMissingAsync`, `CompareAndSwapRefAsync`) no llamaban a
       `RequireNoLinkOnRefPath`.
     - `--no-deref` protege de una ref simbólica, no de un enlace en una carpeta padre.
   - **Medido con git 2.45.2** (repositorios desechables, junctions de `mklink /J`):
     - **Junction en `<común>/worktrees`:** `worktree add --no-checkout` escribió a través de ella la carpeta de
       administración nueva (`HEAD`, `gitdir`, `commondir`, `locked`, `logs`), fuera del repositorio. La comprobación
       de A-02 llegaba después y lanzaba la excepción con lo escrito ya fuera.
     - **Junction en `<común>/worktrees/T1`**, también colgante: git no escribe por ella. Su `mkdir` falla y pasa a
       `T11`.
     - **Junction en `refs/agentstudio/<run>`:** `update-ref --no-deref` escribió la ref oculta en el destino.
     - **El reflog.** git guarda uno por rama (`core.logAllRefUpdates` vale `true` fuera de un repositorio desnudo),
       así que la rama de la ejecución lo tiene en `logs/refs/heads/agentstudio/run-<run>`.
       - Con una junction en `logs/refs/heads/agentstudio`, `update-ref` creó y amplió ficheros de reflog en el destino.
       - `update-ref -d` **borró** uno de fuera del repositorio.
       - Con `core.logAllRefUpdates=always`, que el repositorio puede poner, también las refs ocultas tienen reflog.
     - **Carpeta de administración de un worktree convertida en junction después de crearlo:**
       - un `commit` escribió en el destino `HEAD`, el índice, `COMMIT_EDITMSG` y `logs`;
       - `worktree remove --force --force` **vació** la carpeta a la que apunta, con un fichero ajeno dentro.
   - **Decisión** (`GitWorktreeManager`). Todo rechazo es `WorktreeOwnershipException` («reached through a link»),
     antes de ejecutar git.
     1. **Antes de `worktree add`.** `<común>/worktrees` y `<común>/worktrees/<nombre>`, la primera carpeta que git
        intenta, se comprueban si existen (`RequireWorktreesFolderWithoutLinks`):
        - no pueden ser enlaces;
        - su ruta final tiene que quedar bajo la ruta final del directorio común.
     2. **Las refs y sus reflogs.** `RequireNoLinkOnRefPath` comprueba también el reflog (`logs/<ref>`). Se llama antes de
        cualquier escritura en todas las rutas que escriben refs:
        - `EnsureAsync`, al principio: las dos refs ocultas del worktree, antes de crear nada;
        - `CommitAllAsync`, antes de `add`;
        - `MergeIntoAsync` y `EnsureRefAsync`, antes de fusionar o escribir;
        - dentro de los escritores (`CreateRefIfMissingAsync`, `CompareAndSwapRefAsync`, `SwapRefAsync`), como segunda
          línea.

        Las operaciones que ya la usaban (`EnsureRunBranchAsync`, `DeleteRefAsync`, `FastForwardRunBranchAsync`,
        `ListRunRefsAsync` y las de la candidata) ganan el reflog sin más cambios.
     3. **Ampliación encontrada al medir.** La comprobación de A-02 solo corría al crear la carpeta de administración.
        Ahora:
        - `VerifyRegistration` la rechaza si se llega a ella por un enlace. Por ahí pasan todas las operaciones sobre un
          worktree propio: `CommitAllAsync`, `CheckoutAsync`, `RemoveAsync` y la reutilización en `EnsureAsync`;
        - `DropRegistrationAsync` comprueba `<común>/worktrees` y cada carpeta de administración del worktree antes de
          `worktree remove`.

2. **H-04 · La búsqueda perezosa de objetos y las claves de transporte quedaban fuera del clasificador.**
   - **Por qué:**
     - En un clon parcial, git trae bajo demanda un objeto que falta: es la búsqueda perezosa. Ocurre dentro de un
       comando normal, por el transporte o el helper remoto que elige la configuración del repositorio.
     - El clasificador no miraba `remote.*.vcs`, que elige `git-remote-<valor>`.
     - El ejecutor cerraba los transportes con nombre y el valor por defecto, pero no fijaba `GIT_NO_LAZY_FETCH`.
   - **Medido con git 2.45.2.** Se hizo un clon parcial (`--filter=blob:none`) de un repositorio local con
     `uploadpack.allowfilter`. En el PATH había un `git-remote-sentinel` que deja una marca. El PATH queda fuera del modelo
     (ADR-0018) y aquí solo sirve para ver si el helper corre.
     - **El clon** escribe `remote.origin.promisor=true` y `remote.origin.partialclonefilter=blob:none`, no
       `extensions.partialClone`, y le faltan los blobs.
     - **Qué claves lo convierten en promisor.** Cada una de estas, sola, hizo que git buscara de forma perezosa y
       ejecutara el helper:
       - `remote.origin.promisor=true`;
       - `remote.origin.partialclonefilter=blob:none`, y también **vacía**;
       - `extensions.partialClone=origin`;
       - `promisor=false` junto a un filtro.

       `promisor=false` sola no lo hace.
     - **La búsqueda.** Un `git cat-file` normal trae de `origin` el blob que falta. Con las `-c` del ejecutor y una URL
       `file://`, falla: «transport 'file' not allowed».
     - **El helper del repositorio.** Con `remote.origin.url = sentinel::x` o `remote.origin.vcs = sentinel`, y
       `protocol.sentinel.allow = always` en el repositorio, la búsqueda **ejecutó** `git-remote-sentinel`:
       - corría con todas las `-c` del ejecutor, porque la lista por transporte no cubre un nombre cualquiera;
       - sin `protocol.sentinel.allow`, lo cierra `protocol.allow=never`;
       - `ext::` con `protocol.ext.allow=always` lo cierra la `-c` de `ext`.
     - **Qué comandos internos la disparan:**
       - sí: `diff --binary`, `archive` y `reset --hard`;
       - no: `status`, `ls-files`, `merge-base`, `diff --name-only` ni `worktree add --no-checkout`.
     - **`GIT_NO_LAZY_FETCH=1`** (y `git --no-lazy-fetch`) se respeta. No se busca nada: el comando falla con «lazy
       fetching disabled» y el helper no corre.
     - **`protocol.*.allow`:**
       - con `protocol.file.allow=always` en el repositorio, `-c protocol.file.allow=never` sigue cerrando `file`. Se
         repite así la medición de R3-01;
       - con `protocol.sentinel.allow=always`, `ls-remote sentinel::x` ejecuta el helper aun con las `-c` del ejecutor.
     - **`GIT_ALLOW_PROTOCOL`** manda sobre toda `protocol.*.allow` de la configuración: con él, git solo permite los
       transportes de su lista.
       - **Vacía no significa «ninguno».** git la parte en un nombre vacío y permite el helper sin nombre de una URL
         `::x`: se ejecutó `git-remote-`, que las `-c` del ejecutor sí cerraban.
       - Con `GIT_ALLOW_PROTOCOL=-` quedan cerrados `''`, `sentinel` y `file`: es lo medido.
       - **Sabido por el código, no medido:** el nombre de un transporte o de un helper que sale de una URL empieza por
         letra o dígito, así que `-` nunca coincide. Sí podría coincidir un `remote.*.vcs = -`, que el clasificador
         rechaza.
     - **git-lfs 3.5.1** no lee ninguna de las dos variables, ni `GIT_PROTOCOL_FROM_USER`. Se buscaron sus cadenas en el
       binario real (`mingw64\bin\git-lfs.exe`, que sí contiene `GIT_LFS_SKIP_SMUDGE` y `GIT_SSH_COMMAND`): no están.
       Es una lectura del binario, no una ejecución. Sus transferencias solo corren al bajar o subir objetos, y siguen
       siendo el residual de red de R4-01.
   - **Decisión:**
     1. `GitCommandRunner.BuildEnvironment` fija `GIT_NO_LAZY_FETCH=1` y `GIT_ALLOW_PROTOCOL=-` en todo comando interno.
        También cuando el proceso padre trae otro valor: amplía la decisión 10 y R3-07. Las `-c protocol.*.allow=never`
        se quedan como segunda línea.
     2. `GitCallbacks` rechaza de plano, sea cual sea su valor:
        - `remote.*.vcs`, que elige el helper que git ejecuta para cualquier transporte de ese remoto;
        - las claves de un clon parcial: `remote.*.promisor`, `remote.*.partialclonefilter` (git convierte el remoto en
          promisor con cualquier filtro) y `extensions.partialClone`, que entra en el `Pattern`.

        Un clon parcial falla en el primer comando de contenido (el `status` de `InspectAsync`, el `worktree add` de
        `EnsureAsync`). Lo hace con `UnsafeGitCallbackException` y un motivo que dice que es un clon parcial y pide uno
        completo. También se rechaza `remote.*.promisor=false` (fallo cerrado): un `promisor` sin valor, que git lee como
        verdadero, y uno vacío, que lee como falso, llegan iguales al clasificador, como `""`.
     3. `protocol.*.allow` no entra en el clasificador. `GIT_ALLOW_PROTOCOL` la anula entera, y rechazarla impediría
        repositorios legítimos (`protocol.file.allow=always`).

3. **A-03, hueco literal · Una clave que se rechaza de plano se juzga antes que su valor.**
   - **Por qué:** `Classify` saltaba los valores vacíos antes de mirar la clave. Así pasaban vacías
     `lfs.extension.*.clean`/`.smudge`, `lfs.customtransfer.*.path` y `lfs.standalonetransferagent`.
   - **Decisión:**
     - esas claves, `remote.*.vcs` y las de clon parcial se rechazan con cualquier valor;
     - las demás siguen ignorando un valor vacío, porque así se reinicia una lista: `credential.<url>.helper =`, que
       está en la configuración real de esta máquina.

- **Pruebas** (todas `LocalTools`). Las nuevas vieron el fallo antes del cambio, salvo ocho casos del clasificador que
  son guardas y ya pasaban: las cinco claves que no se clasifican y tres de las cuatro del `Pattern`, que ya casaban
  por `^remote\.`.
  - `GitAdministrativeLinkTests`, con repositorios reales y junctions. Comprueba la carpeta víctima y los comandos que
    llegaron a correr, no solo la excepción.
  - `GitPartialCloneTests`, en la colección que corre sola, porque cambia el PATH. Cada prueba tiene un control positivo:
    un git normal sí ejecuta el helper. Una de ellas repite la configuración y el entorno de un comando interno real
    sobre `ls-remote sentinel::x` y `::x`.
  - Las del clasificador en `GitCallbackClassifierTests`.
- **Consecuencias y límites:**
  - Un clon parcial no se puede usar con Agent Studio: hay que completarlo o clonar de nuevo. Tampoco un repositorio con
    `remote.*.vcs`, como un remoto de Mercurial con `git-remote-hg`.
  - Un `git-remote-<x>` del PATH sigue fuera del modelo (ADR-0018). Lo nuevo hace que ningún comando interno llegue a
    ejecutarlo.
  - Una junction en `worktrees/<nombre>` se rechaza aunque git no escribiría por ella: está plantada, y la comprobación
    de propiedad lee todas las carpetas de `worktrees`.
  - **Coste:** unas pocas consultas de atributos por operación. `MergeIntoAsync` y `EnsureRefAsync` hacen además un
    `rev-parse` para conocer el directorio común.
  - Sigue siendo residual (ADR-0018, punto 2) una junction que un proceso concurrente del mismo usuario plante entre la
    comprobación y la escritura.
  - **No medido:** un repositorio con `extensions.refStorage=reftable` guarda sus refs en `reftable/` y no en ficheros
    sueltos. La comprobación por rutas supone el formato de ficheros.
  - **Ampliación a la entrega.** Medido con git 2.45.2: la fusión escribe la rama del usuario, su reflog, `logs/HEAD` y
    `ORIG_HEAD`; con un commit de fusión, también `MERGE_HEAD`, `MERGE_MSG`, `MERGE_MODE`, `AUTO_MERGE` y `COMMIT_EDITMSG`.
    Con una junction en `logs/refs/heads` o en `refs/heads/feature`, un fast-forward escribió fuera del repositorio. Con un
    enlace duro (`mklink /H`, sin privilegios), git añadió líneas a `logs/HEAD` y al reflog, y sobrescribió `MERGE_MSG`,
    `MERGE_MODE` y `COMMIT_EDITMSG`; con `ORIG_HEAD` y `MERGE_HEAD` falló sin escribir. Esta máquina también crea
    enlaces simbólicos de fichero sin elevación. Por eso `FindDeliveryRefusalsAsync`, que `DeliveryService` llama antes
    de pedir la aprobación, y la entrega justo antes de `git merge` lanzan `WorktreeOwnershipException`:
    - si se llega a la rama o a su reflog por un enlace (`RequireNoLinkOnRefPath`, que ahora rechaza además un reflog
      con más de un nombre, también para las refs propias);
    - si alguno de esos ficheros del directorio git es un enlace, tiene más de un nombre o está en una carpeta que no
      resuelve bajo el directorio común.

    Lo prueban `GitWorktreeDeliveryTests` (dos junctions y una teoría con los ocho ficheros),
    `DeliveryLocalToolsTests` (no se pide ninguna aprobación) y `GitAdministrativeLinkTests` (el reflog de la rama de la
    ejecución). `DeliveryService` no cambia.

## Adenda · 2026-10-18: el repositorio original no aporta callbacks, la administración de un worktree se comprueba por dentro, HEAD sigue desacoplado y nadie elige dónde escriben git y git-lfs (X-01, X-02, P-01 a P-03)

La sexta revisión adversarial (Codex), juzgada contra [ADR-0018](0018-modelo-de-amenaza-fase-3.md), dejó dos hallazgos
confirmados (X-01, X-02) y tres plausibles (P-01 a P-03). Los cinco están dentro del modelo: el contenido y la
configuración del repositorio, y lo que se planta antes de un paso, con la app cerrada (un enlace, un fichero o una
clave). Se decide:

1. **X-01 · Un callback podía ser un ejecutable del repositorio original.**
   - **Por qué:** desde un worktree, `ClassifyRepositoryCallbacksAsync` solo tomaba como raíces la de worktrees y el
     directorio de trabajo, que es el propio worktree. El repositorio del proyecto no estaba entre ellas.
   - **Medido con git 2.45.2** (repositorios desechables, con centinela):
     - un `includeIf "gitdir:**/worktrees/**"` en el `.git/config` compartido que pone
       `filter.x.process = '<repo>/tools/git-lfs.exe' filter-process` (o `smudge -- %f`) solo lo ve el contexto del
       worktree, no el padre;
     - git ejecuta con su `sh` un script llamado `git-lfs.exe` o `git-lfs`. El `reset --hard` del worktree nuevo lo
       ejecutó (centinela) y terminó con código 0, aunque el protocolo de `filter-process` fallara;
     - con el código anterior, `EnsureAsync` aceptaba ese programa, «fuera de las raíces», y el checkout lo ejecutaba: el
       hallazgo, reproducido por la prueba;
     - `rev-parse --show-toplevel` desde una junction que lleva al repositorio devuelve la ruta física.
   - **Decisión:**
     - todo comando de contenido (`status`, `add`, `commit`, `reset`, `clean`, `merge`, también `merge --abort`,
       `merge-tree`, `archive` y `worktree add`) recibe un `GitScope` con la raíz del proyecto y el directorio común.
       Sin él, `GitCommandRunner` no lo ejecuta (falla cerrado);
     - las raíces son la de worktrees, el directorio de trabajo, la raíz del proyecto, el directorio común y el árbol
       principal que git deriva del común: su padre si se llama `.git`, la regla del propio `worktree list`, porque el
       proyecto puede ser a su vez un worktree enlazado;
     - `GitCallbacks.Classify` toma cada raíz por su ruta y por su ruta final (`GetFinalPathNameByHandle`), con la misma
       función que el programa. Además juzga el programa por el nombre con el que se le llama: un programa nombrado
       dentro de una raíz se rechaza aunque un enlace lo saque fuera.
   - **Límite:** solo se conocen esas raíces. Un programa de otro clon, o de un worktree del usuario fuera de ellas, no se
     reconoce como del repositorio.

2. **X-02 · Los ficheros de la carpeta de administración de un worktree propio.**
   - **Por qué:** `RequireAdminDirectoryWithoutLinks` comprobaba la cadena hasta esa carpeta, pero no lo que hay dentro.
   - **Medido con git 2.45.2**, en la carpeta de administración de un worktree:
     - `worktree add --no-checkout` crea `commondir`, `gitdir`, `HEAD`, `locked` y `logs/HEAD`;
     - `reset --hard` sustituye `HEAD`, `index` y `ORIG_HEAD` por renombrado y añade a `logs/HEAD`; el primero crea
       `index` y `ORIG_HEAD`;
     - `add` sustituye `index` por renombrado;
     - `commit` escribe `COMMIT_EDITMSG` en el sitio, sustituye `HEAD` e `index` por renombrado y añade a `logs/HEAD`;
     - `status`, `diff --cached`, `clean` y `rev-parse` no escriben nada ahí.

     Y con enlaces plantados:
     - con un enlace duro (`mklink /H`), el commit sobrescribió el otro fichero de `COMMIT_EDITMSG` y añadió al de
       `logs/HEAD`, y el reset al de `logs/HEAD`. Un renombrado deja el otro fichero como estaba (`HEAD`, `index`,
       `ORIG_HEAD`);
     - con un enlace simbólico de fichero (esta máquina los crea sin elevación), `add`, `commit` y `reset` escribieron a
       través de `index`, porque el fichero de bloqueo sigue el enlace; el commit, a través de `COMMIT_EDITMSG`; commit y
       reset, a través de `logs/HEAD`. Un `HEAD` que es un enlace a otro fichero hace que git diga «not a git
       repository»;
     - con una junction en `logs`, commit y reset añadieron fuera, y `worktree remove --force --force` **vació** la
       carpeta a la que apunta. Un enlace duro dentro no se sigue al borrar.
   - **Decisión:**
     - `RequireAdminDirectoryWithoutLinks` rechaza además cualquier enlace dentro de la carpeta, a cualquier profundidad,
       con un recorrido que nunca sigue enlaces (`WorktreeFileSystem.FindLinkInside`). Lo llaman `VerifyRegistration`,
       por la que pasa toda operación sobre un worktree propio antes de mover nada a la papelera, y
       `DropRegistrationAsync`, también en el camino del worktree desaparecido de `EnsureAsync`;
     - antes de cada comando que escribe ahí, cada fichero que escribe pasa la comprobación por fichero de la entrega
       (`RequireGitFilesWithoutLinks`: sin enlace en el camino, un solo nombre y bajo el común). Antes de `add`, y otra
       vez antes de `commit`: `index`, `COMMIT_EDITMSG`, `HEAD` y `logs/HEAD`. Antes de cada `reset`: `HEAD`, `index`,
       `ORIG_HEAD` y `logs/HEAD`. `clean` no escribe nada ahí. La entrega usa la misma función.

3. **P-01 · El HEAD de un worktree propio tiene que seguir desacoplado.**
   - **Medido con git 2.45.2:** con `ref: refs/heads/feature` en el `HEAD` de la administración, o con `HEAD` como enlace
     simbólico cuyo destino es `refs/heads/feature`, `symbolic-ref` lo da como rama, y el `commit` y el `reset --hard`
     del worktree movieron esa rama del usuario.
   - **Decisión:** `RequireDetachedHead` lee ese `HEAD` sin git. No puede ser un enlace, y su primera línea tiene que ser
     un id de objeto completo; si no, `WorktreeOwnershipException`. Se exige al reutilizar el worktree (`EnsureAsync`),
     antes de `add` y otra vez antes de `commit`, y antes de cada `reset` (`CheckoutAsync` y el checkout del worktree
     nuevo). `RemoveAsync` no lo exige: un HEAD plantado no impide limpiar.

4. **P-02 · El almacén de objetos y el backend de refs.**
   - **Medido con git 2.45.2:**
     - con una junction en `objects/<xx>` (la carpeta de reparto del id del objeto nuevo) o en `objects`, `add` escribió
       el objeto suelto fuera;
     - con `core.bigFileThreshold=1`, que el repositorio puede poner, `add` escribió un paquete a través de una junction
       en `objects/pack`. Con el umbral por defecto, `add` no escribe nada en `objects/pack` ni en `objects/info`;
     - coste: listar `objects` con sus 256 carpetas de reparto, leyendo solo los atributos que trae el listado, cuesta
       0,3-0,9 ms; preguntar a cada carpeta por el destino de su enlace, unos 20 ms;
     - con `git init --ref-format=reftable`, `rev-parse --show-ref-format` dice `reftable` (`files` en un repositorio
       normal, también desde un worktree). `HEAD` es `ref: refs/heads/.invalid`, no hay `refs/heads`, y `worktree add`
       y `update-ref` funcionan sin escribir ningún fichero de ref. Se resuelve así el «no medido» de la adenda anterior.
   - **Decisión:**
     - `RequireObjectStoreWithoutLinks`: `objects`, `objects/pack` y `objects/info` resuelven bajo el común, y ninguna
       entrada de `objects` es un enlace. Así se comprueban todas las carpetas de reparto con un solo listado. Se llama
       antes de `add` y de `commit`, de cada `merge-tree` y `commit-tree` (`MergeIntoAsync`, `MergeIntoCandidateAsync`),
       de la vista previa de una entrega con commit de fusión y en `RequireDeliveryWithoutLinks`, antes de la aprobación
       y antes de la fusión. Ningún comando interno usa `update-index`;
     - un repositorio con otro backend de refs se rechaza al abrirlo (`ResolveRepositoryRootAsync`,
       `OpenRepositoryAsync`, `OpenUserRepositoryAsync`) con una excepción nueva, `UnsupportedRepositoryException`. Con
       git 2.45 o posterior, el `rev-parse` que ya se hacía pide también `--show-ref-format`, y tiene que decir `files`.
       Con un git anterior no se pregunta: no tiene reftable y rechaza un repositorio que lo declare
       (`extensions.refStorage` exige el formato 1, cuyas extensiones desconocidas git rechaza). Eso no se ha medido.
   - Sigue siendo residual (ADR-0018, punto 2) un cambio concurrente entre la comprobación y la escritura.

5. **P-03 · Destinos de git-lfs que elige la configuración.**
   - **Medido con git-lfs 3.5.1**, sin red:
     - `lfs.storage` en el `.git/config`, absoluta o relativa, y en el `config.worktree` de un worktree hizo que el
       filtro `clean` de `git add` escribiera el objeto fuera del repositorio. Una ruta relativa se resuelve contra el
       directorio común, no contra la administración del worktree;
     - en `.lfsconfig` se descarta: «These unsafe '.lfsconfig' keys were ignored: lfs.storage»;
     - entre las cadenas del binario no hay otra clave `lfs.*` con una ruta donde escriba. `lfs.customtransfer.<n>.path`
       ya se rechazaba (A-03).
   - **Decisión:** `GitCallbacks` rechaza `lfs.storage` de plano, sea cual sea su valor, también vacío o `lfs` (el valor
     por defecto). Falla cerrado y es más simple que resolver la ruta.
   - **Consecuencia:** no se puede usar un repositorio con `lfs.storage`, ni un usuario que lo tenga en su configuración
     global.

- **Pruebas** (`LocalTools`, salvo las del clasificador, con repositorios reales y centinelas). Todas vieron el fallo
  antes del cambio, salvo una guarda: el `HEAD` convertido en enlace simbólico ya lo rechazaba el recorrido de X-02.
  - X-01: `GitCallbackTests`, el escenario exacto con el programa en el árbol de trabajo y en un directorio común
    separado (`git init --separate-git-dir`); antes, el centinela corría. Y dos del clasificador: una raíz se compara
    también por su ruta final, y un programa nombrado dentro de una raíz se rechaza aunque resuelva fuera.
  - X-02: `GitAdministrativeLinkTests`. Un enlace duro en cada fichero que escribe cada comando, una junction en `logs`
    para commit y reset, y una junction dentro de la carpeta antes de `RemoveAsync` y del worktree desaparecido.
  - P-01: `GitWorktreeTamperingTests`, para commit, reset y reutilización; antes, `main` se movía.
  - P-02: `GitAdministrativeLinkTests` (una junction en `objects`, en `objects/pack` con umbral 1, en `objects/info` y
    en una carpeta de reparto; las dos fusiones; un repositorio reftable) y `GitWorktreeDeliveryTests` (una junction en
    `objects/pack`, antes de la aprobación y antes de la fusión).
  - P-03: el clasificador, con cuatro valores, y `GitCallbackTests` con git-lfs real; antes, el objeto aparecía en el
    almacén de fuera.
- **Consecuencias y límites:**
  - Coste: una consulta de atributos por fichero comprobado, un listado de `objects` por comando que escribe objetos,
    unas pocas rutas finales por clasificación y, como mucho, un `git --version` más por instancia.
  - **Anotado, fuera de este paquete:**
    - una junction plantada en el almacén por defecto de git-lfs (`<común>/lfs`, `lfs/objects/…`, `lfs/tmp`) no se
      comprueba;
    - el origen de un fichero de configuración se compara con la raíz de worktrees por su ruta, no por su ruta final.

## Adenda · 2026-10-18: el almacén de git-lfs sin enlaces, git-lfs por identidad, el índice del usuario en la entrega y ninguna escritura auxiliar de git (G-01, G-03 a G-05)

La séptima revisión adversarial (Codex), juzgada contra [ADR-0018](0018-modelo-de-amenaza-fase-3.md), dejó un hallazgo
confirmado (G-01) y tres plausibles (G-03 a G-05). Los cuatro están dentro del modelo: el contenido y la configuración del
repositorio, y lo que se planta antes de un paso (un enlace, un fichero o una clave). Se decide:

1. **G-01 · El almacén por defecto de git-lfs no se comprobaba.** Resuelve el primer «anotado» de la adenda anterior.
   - **Por qué:** los filtros de git-lfs, los únicos callbacks de contenido que se admiten (R4-01), escriben en
     `<común>/lfs`, y `RequireObjectStoreWithoutLinks` solo miraba `<común>/objects`. Una junction plantada en `lfs` llevaba
     esas escrituras fuera del repositorio.
   - **Medido con git-lfs 3.5.1 y git 2.45.2** (repositorios desechables, sin red):
     - `git lfs env` da `LfsStorageDir=<común>/lfs`, `LocalMediaDir=<común>/lfs/objects` y `TempDir=<común>/lfs/tmp`, también
       desde un worktree. El clean de un `git add` lanzado como lo lanza Agent Studio en un worktree (`--git-dir` y
       `--work-tree` explícitos) escribió en el común, nunca en la administración del worktree;
     - el clean de `git add` escribe `lfs/objects/<aa>/<bb>/<oid>` pasando por `lfs/tmp` (fichero temporal y renombrado);
     - un smudge con el objeto en local no escribe nada en `lfs`. Sin el objeto y sin remoto, escribió
       `lfs/logs/<fecha>.log` y creó las carpetas de reparto del objeto;
     - `git lfs env` no nombra otra carpeta del almacén (`LocalReferenceDirs`, de solo lectura, está vacío), y la cadena
       `incomplete` del binario no es una ruta. Una descarga no se pudo medir sin red;
     - con una junction en `lfs`, en `lfs/objects`, en una carpeta de reparto de primer nivel (`objects/<aa>`) o en una de
       segundo (`objects/<aa>/<bb>`), `git add` escribió el objeto fuera del repositorio. Con `lfs/tmp` como junction, la
       carpeta de fuera quedó vacía al terminar, porque git-lfs renombra el temporal a `objects`: en ese caso la prueba mira
       que el comando no llegue a correr;
     - **`git status` también ejecuta el clean**, con `GIT_OPTIONAL_LOCKS=0`. Un cambio de tamaño no lo hizo (git lo ve por
       el stat), pero uno del mismo tamaño con la fecha cambiada sí: `git-lfs filter-process` corrió y guardó el objeto
       nuevo (de 1 a 2 objetos, y a 3 con otro cambio).
   - **Decisión** (`WorktreeFileSystem.RequireLfsStorageWithoutLinks`; `WorktreeOwnershipException`, «reached through a
     link»):
     - `lfs` no es un enlace ni se llega a él por uno, y su ruta final queda bajo la del común;
     - ninguna entrada de `lfs` es un enlace (`objects`, `tmp`, `logs` y cualquier otra), ni ninguna de `tmp`, `logs` y
       `objects` (el primer nivel de reparto), ni ninguna de cada carpeta de primer nivel (el segundo, donde están los
       ficheros);
     - `objects`, `tmp` y `logs` resuelven bajo el común;
     - una carpeta que no se puede listar falla cerrado. Solo se leen los atributos que trae cada listado;
     - toda carpeta que git-lfs crea ahí cuelga de una ya comprobada.
   - **Dónde:**
     - en la compuerta de contenido de `GitCommandRunner`, antes de **todo** comando de contenido (`status`, `add`, `commit`,
       `reset`, `clean`, `merge`, `merge-tree`, `archive` y `worktree add`), con el directorio común del `GitScope`. Como
       `status` también ejecuta el filtro, enumerar los sitios uno a uno se dejaría alguno (`InspectAsync`, la captura,
       `RemoveAsync` y la entrega lo usan);
     - en `RequireDeliveryWithoutLinks`, que corre antes de la aprobación (la comprobación previa no pasa por la compuerta)
       y antes de cada comando que escribe en la entrega.
   - **Coste medido** (el mismo recorrido en un almacén sintético, con la máquina sin carga): con las 256 carpetas de primer
     nivel, 1 024 de segundo y 1 024 objetos, 20-23 ms por comando de contenido; con solo `tmp` y `logs`, menos de 0,1 ms
     de listado; sin `lfs`, una consulta de atributos. Se suman unas pocas rutas finales.
   - **Consecuencias:** un repositorio con un enlace en su almacén de git-lfs no se puede usar: falla todo comando de
     contenido, también el `status` de `RemoveAsync` sin descartar. `RemoveAsync(discardChanges: true)` no lanza ninguno
     y sigue limpiando, como con la compuerta de callbacks.

2. **G-03 · Un `git-lfs.exe` falso donde las raíces no llegan.**
   - **Por qué:** el clasificador aceptaba como filtro un programa absoluto llamado `git-lfs.exe` (o `git-lfs`) si su ruta
     final quedaba fuera de las raíces de `GitScope` (X-01): la del proyecto, el común, el árbol principal (solo si el común
     se llama `.git`), la de worktrees y el directorio de trabajo. Faltan, entre otras, otro worktree enlazado del usuario y
     un árbol principal con el directorio git separado.
   - **Medido con git 2.45.2** (centinelas): con un `includeIf "gitdir:**/worktrees/**"` en la configuración compartida que
     pone `filter.x.process = '<programa>' filter-process`, el `reset --hard` del worktree nuevo ejecutó un `git-lfs.exe`
     falso (un script de sh):
     - en otro worktree enlazado del usuario;
     - en el árbol principal de un repositorio con el directorio git separado, cuando el proyecto es un worktree enlazado
       suyo.

     Y la prueba de R4-01 daba por bueno un `git-lfs` falso en una carpeta cualquiera fuera del repositorio: cada comando
     interno lo ejecutaba.
   - **En esta máquina hay dos ficheros `git-lfs.exe`** en el paquete de git: `cmd\git-lfs.exe` (45 KB, el que encuentra
     el PATH) y `mingw64\bin\git-lfs.exe` (11 MB).
   - **Decisión** (`GitCallbacks`):
     - un filtro cuyo programa es absoluto solo se acepta si es **el mismo fichero** que el git-lfs instalado, el que
       resuelve el nombre suelto `git-lfs` con el resolutor de siempre (`IExecutableLocator` y después el paquete de git);
     - mismo fichero quiere decir la misma ruta final (`GetFinalPathNameByHandle`) y el mismo id de fichero: número de serie
       del volumen e índice del fichero, con `GetFileInformationByHandle` (`GitCallbacks.ResolveFileIdentity`);
     - un segundo nombre del fichero (un enlace duro) tiene otra ruta final y se rechaza. Un enlace cuya ruta final es el
       instalado es ese fichero;
     - lo que no se resuelve o no se identifica se rechaza, y sin git-lfs instalado se rechaza todo `git-lfs.exe` absoluto;
     - el nombre suelto `git-lfs` sigue permitido, resuelto igual;
     - las raíces se quedan como segunda línea. Se juzgan antes, así que un programa dentro de ellas se sigue rechazando con
       el mismo motivo;
     - `Classify` gana un parámetro opcional con la identidad del fichero (aditivo). Solo cambia el juicio de los callbacks
       de contenido; los demás (textconv, credenciales, ssh, gpg…) siguen con la lista blanca de R3-01, y ningún comando
       interno los ejecuta.
   - **Cambio de contrato:**
     - se rechaza un `git-lfs.exe` absoluto que no es el instalado: un git-lfs instalado aparte (`Git LFS\`) o la copia de
       `mingw64\bin` cuando el PATH resuelve `cmd\git-lfs.exe`. `git lfs install` escribe el nombre suelto, así que la
       configuración que deja git-lfs no cambia; quien tenga una ruta absoluta tiene que poner el nombre suelto;
     - la prueba de R4-01 con un `git-lfs` falso fuera del repositorio pasa a comprobar que se rechaza.
   - **Límite:** el PATH sigue fuera del modelo (ADR-0018): quien controla el PATH elige qué es el git-lfs instalado.

3. **G-04 · La entrega no comprobaba el índice del usuario.**
   - **Por qué:** `DeliveryGitFiles` no incluía `index`, aunque la fusión lo escribe; las comprobaciones de los worktrees
     propios sí lo incluyen (X-02).
   - **Medido con git 2.45.2**, la entrega tal como la lanza Agent Studio:
     - con `.git/index` como enlace simbólico a un fichero de fuera, `merge --ff-only` y `merge --no-ff --no-commit`
       escribieron el índice nuevo a través de él (el fichero de bloqueo sigue el enlace), y siguió siendo un enlace;
     - con un enlace duro, git sustituyó el nombre por renombrado y el otro fichero quedó como estaba.
   - **Decisión:**
     - `index` entra en `DeliveryGitFiles`. El enlace duro se rechaza igual, por tener más de un nombre, como en X-02;
     - `RequireDeliveryWithoutLinks` corre antes de la aprobación y justo antes de la fusión, como antes, y ahora también
       justo antes de `commit -F -` y de `merge --abort`, que también escriben el índice.
   - **Consecuencia:** si el enlace aparece entre la fusión y el commit (eso exige un proceso concurrente: residual), la
     entrega falla cerrada con la fusión en curso. No se confirma ni se aborta nada a través del enlace; el usuario quita el
     enlace y termina o aborta la fusión a mano.

4. **G-05 · Destinos de escritura auxiliares de git.**
   - **Por qué:** rerere (`rr-cache`, `MERGE_RR`), el índice dividido (`sharedindex.*`), el grafo de commits, el
     multi-pack-index y la caché de no seguidos ni se apagaban ni se comprobaban, y su configuración no está en
     `GitCallbacks.Pattern`.
   - **Medido con git 2.45.2**, con la configuración por comando de Agent Studio anterior a este cambio, y con cada clave
     puesta en el repositorio antes del paso:
     - `core.splitIndex=true`: el ciclo de un worktree (`worktree add`, `reset --hard`, `add`, `commit`) dejó dos
       `sharedindex.*` en su administración y el índice con la extensión `link`. La fusión de la entrega, en los dos modos,
       dejó `sharedindex.*` en el `.git` del usuario;
     - `rerere.enabled=true`, o solo una carpeta `rr-cache` plantada (rerere se enciende cuando existe): el `commit` del
       worktree escribió `MERGE_RR` en su administración y creó `<común>/rr-cache`; el `commit -F -` de una entrega con
       commit de fusión, `MERGE_RR` y `rr-cache` en el `.git` del usuario. Con `MERGE_RR` como enlace simbólico a un fichero
       ajeno, el commit lo leyó y falló («corrupt MERGE_RR») sin escribirlo;
     - `core.untrackedCache=true`: el índice del worktree y el del usuario tras la entrega ganaron la extensión `UNTR`;
     - grafo de commits y multi-pack-index: con `gc.autoPackLimit=1` y dos paquetes, un `git commit` normal lanzó `gc
       --auto`, que escribió `objects/info/commit-graph`; con el mantenimiento automático (`maintenance.commit-graph` e
       `incremental-repack`), `commit-graphs` y `multi-pack-index`. Con `gc.auto=0` y `maintenance.auto=false`, que ya se
       forzaban, ningún comando interno los escribe. Con gc en marcha, `gc.writeCommitGraph=false` evitó el grafo;
     - `fetch.writeCommitGraph` no se puede medir aquí: ningún comando interno hace fetch.
   - **Decisión:** todo comando interno lleva además, con `-c`: `rerere.enabled=false`, `rerere.autoUpdate=false`,
     `core.splitIndex=false`, `core.untrackedCache=false`, `fetch.writeCommitGraph=false` y `gc.writeCommitGraph=false`.
     Con ellos no apareció ninguno de esos ficheros en ninguno de los casos medidos. La lista completa que se fuerza por
     comando queda así:
     - `core.longpaths=true`, `core.fsmonitor=false` y `core.hooksPath=<carpeta vacía propia>`;
     - `gc.auto=0`, `gc.writeCommitGraph=false`, `maintenance.auto=false` y `fetch.writeCommitGraph=false`;
     - `rerere.enabled=false`, `rerere.autoUpdate=false`, `core.splitIndex=false` y `core.untrackedCache=false`;
     - `protocol.allow=never` y `protocol.<nombre>.allow=never` para `file`, `git`, `ssh`, `http`, `https`, `ftp`, `ftps` y
       `ext`;
     - `credential.helper=`, `core.askPass=` y `core.sshCommand=`.
   - **Revisado con la documentación de git, sin medir, y fuera:**
     - `feature.manyFiles` enciende la caché de no seguidos, y `core.untrackedCache=false` la anula;
     - `core.bigFileThreshold` (medido en P-02) y `pack.writeReverseIndex` escriben en `objects/pack`, que se comprueba
       desde P-02;
     - `core.logAllRefUpdates` escribe reflogs, que se comprueban desde H-03;
     - `trace2.*` solo se lee de la configuración global y de sistema, que es del usuario;
     - `core.commitGraph` y `core.multiPackIndex` gobiernan sobre todo la lectura: apagarlos quitaría rendimiento, y el
       mantenimiento que los escribe ya está apagado.
   - **Consecuencias** (medido): si el índice del usuario ya está dividido y tiene la caché de no seguidos, la fusión de la
     entrega lo escribe entero y sin `UNTR`, con las mismas entradas, y deja sus `sharedindex.*` como estaban (no borra
     ninguno).

- **Pruebas** (`LocalTools`, salvo las del clasificador, con repositorios reales, junctions, enlaces y centinelas). Todas
  vieron el fallo antes del cambio, salvo las guardas que se dicen:
  - G-01: `GitAdministrativeLinkTests`. Las junctions de `lfs/objects` y `lfs/tmp` a la vez (el caso del hallazgo), una
    teoría con una junction en `lfs`, `objects`, los dos niveles de reparto, `tmp` y `logs`, y el `status` de `InspectAsync`
    sobre un cambio del mismo tamaño. Antes del cambio, `git add` y `git status` corrían a través de la junction (el `add`
    dejaba además los hooks de git-lfs en la carpeta vacía, y el comando siguiente fallaba cerrado). Además,
    `GitWorktreeDeliveryTests` con una junction en `lfs/objects` antes de la aprobación y antes de la fusión.
  - G-03: el clasificador (`GitCallbackClassifierTests`: otro `git-lfs.exe` absoluto, la copia de `mingw64`, sin git-lfs
    instalado, la misma ruta final con otro id, un segundo nombre, un id ilegible) y `GitCallbackTests` con el `git-lfs.exe`
    falso en otro worktree del usuario y en un árbol principal con el directorio git separado, y el `git-lfs` falso fuera
    del repositorio; antes, el centinela corría en los tres. Guardas: el git-lfs instalado por su ruta absoluta pasa, en el
    clasificador con los resolutores reales y en `InspectAsync`, `EnsureAsync`, `CommitAllAsync` y
    `FindDeliveryRefusalsAsync`, y un enlace cuya ruta final y cuyo id son los del instalado pasa en el clasificador.
  - G-04: `GitWorktreeDeliveryTests`, una teoría con un enlace duro y uno simbólico en `index` en los dos modos, y dos
    carreras con el enlace simbólico plantado justo después de la fusión: antes del commit y antes del `merge --abort`.
    Antes del cambio, la fusión (los dos modos) y el `merge --abort` escribieron el índice en el fichero de fuera; el
    commit no lo reescribió (el índice no cambiaba), pero corría con el enlace.
  - G-05: `GitAuxiliaryWritesTests`: que todo comando lleva las seis `-c`, el índice dividido, rerere (con la clave y con
    `rr-cache` plantada), la caché de no seguidos y la entrega con las tres claves. Guarda: el grafo de commits y el
    multi-pack-index, con un control al final que muestra que la misma configuración sí los escribe con un `git commit`
    normal.
- **Consecuencias y límites:**
  - Sigue siendo residual (ADR-0018, punto 2) un enlace que un proceso concurrente del mismo usuario plante entre la
    comprobación y la escritura.
  - **Anotado, fuera de este paquete:**
    - git-lfs real instala sus hooks en el `core.hooksPath` que recibe, la carpeta vacía de Agent Studio (adenda de A-03),
      así que el comando interno siguiente a un clean o un smudge de git-lfs falla cerrado. Resuelto en la adenda del
      fichero vacío como `core.hooksPath` (más abajo);
    - el origen de un fichero de configuración se sigue comparando con la raíz de worktrees por su ruta, no por su ruta
      final (segundo «anotado» de la adenda anterior).

## Adenda · 2026-10-18: un solo recorrido del directorio git antes de todo comando que escribe en él (S-01 a S-03)

La octava revisión adversarial (Codex), juzgada contra [ADR-0018](0018-modelo-de-amenaza-fase-3.md), dejó tres hallazgos
dentro del modelo: lo que se planta antes de un paso y la configuración del repositorio. Los tres son de la misma clase que
encontraron las cinco rondas anteriores (H-03, X-02, P-02, G-01, G-04): **un enlace plantado de antemano bajo el directorio
git del usuario, por el que un comando interno de git, o el filtro de git-lfs que se admite, escribe después.** Cada ronda
nombró un destino nuevo y cada corrección añadió una comprobación por destino. Esta adenda deja de enumerar: decide un solo
control para la clase, y las comprobaciones por destino se quedan como segunda línea.

1. **Los tres hallazgos, medidos** (git 2.45.2 y git-lfs 3.5.1, repositorios desechables, sin red; con las pruebas nuevas
   antes del cambio):
   - **S-01 · git-lfs escribe `<común>/config`.** Su `clean` y su `filter-process` escriben `lfs.repositoryformatversion`
     con `git config` cuando falta (medido: el primer `git add` de un fichero seguido lo escribió), y git resuelve el enlace
     simbólico del fichero que bloquea. Con `.git/config` como enlace simbólico a una configuración válida de fuera, sin esa
     clave, el `status` de `InspectAsync` sobre un cambio del mismo tamaño ejecutó el filtro y **añadió
     `[lfs] repositoryformatversion = 0` al fichero de fuera**. Con un enlace duro, git sustituyó el nombre por renombrado y
     el otro fichero quedó igual, pero el comando corrió.
   - **S-02 · `update-ref -d` reescribe `packed-refs`.** Con la rama de la ejecución empaquetada (`pack-refs --all`) y
     `packed-refs` como enlace simbólico a una copia de fuera, `DeleteRefAsync` **reescribió el fichero de fuera sin la
     rama**. Con un enlace duro, el renombrado dejó el otro fichero igual y el borrado se hizo.
   - **S-03 · `lfs/incomplete/<oid>.part`.** git-lfs reabre en lectura y escritura la descarga a medias. Ninguna comprobación
     miraba los enlaces duros de `lfs/`, ni nada de `incomplete`. La descarga no se puede medir sin red.
   - **Y los destinos que nadie nombraba.** Con un enlace duro en `FETCH_HEAD`, `info/exclude` o `refs/tags/v1`, una
     junction en `hooks/lib`, `modules/sub` o `worktrees/<worktree del usuario>/logs`, o `description` como enlace
     simbólico, `InspectAsync`, `FindDeliveryRefusalsAsync` y `EnsureRunBranchAsync` corrían sin rechazar nada.

2. **Decisión: el control de la clase** (`WorktreeFileSystem.RequireGitDirectoriesWithoutLinks`; `WorktreeOwnershipException`,
   «reached through a link», o «could not be listed»). Recorre entero el directorio común y, si queda fuera de él, el
   directorio git del proyecto, y rechaza:
   1. **cualquier punto de reanálisis**, fichero o carpeta: junction, enlace simbólico o de cualquier otro tipo. Los
      atributos salen del propio listado (`FileSystemEnumerable`, con `AttributesToSkip = 0` e `IgnoreInaccessible = false`
      puestos a mano: un `EnumerationOptions` nuevo salta los ocultos y los de sistema e ignora lo que no puede leer). El
      recorrido nunca entra en uno;
   2. **cualquier fichero con más de un nombre** (un enlace duro), salvo los de los almacenes de objetos (punto 3). El número
      de nombres sale de un handle por fichero abierto con el acceso mínimo: `FILE_READ_ATTRIBUTES`, todo compartido,
      `FILE_FLAG_OPEN_REPARSE_POINT` y `FILE_FLAG_BACKUP_SEMANTICS`, y `GetFileInformationByHandle.nNumberOfLinks`. Ese
      acceso no choca con un fichero que otro proceso tiene abierto sin compartir (un `*.lock` de git). Lo que no se puede
      contar se rechaza, y un fichero que desaparece entre el listado y la apertura se salta. `HasOtherNames`, la comprobación
      por fichero de antes, usa ahora el mismo conteo, y solo para ficheros: una carpeta no puede tener un enlace duro;
   3. **cualquier carpeta que no se pueda listar** (falla cerrado, como R3-03). Una subcarpeta que desaparece mientras se
      recorre (una carpeta de refs que git borra) se salta: ya no hay nada por donde escribir.
   - **Lo recorre todo:** `worktrees/*` (también los worktrees del usuario que no son de Agent Studio), `lfs/` con todas
     sus carpetas (`incomplete` incluida), `refs/`, `logs/`, `info/`, `hooks/`, `modules/` y lo demás.
   - **El directorio git del proyecto.** El proyecto puede ser a su vez un worktree enlazado. `GitScope` gana un campo
     opcional, `GitDirectory`, que se rellena con el del repositorio del usuario. Si queda bajo el común, como en todo
     worktree que crea git (`<común>/worktrees/<id>`), ya se recorre con él; si no, se recorre aparte, sin excepciones.
   - **Sin caché.** Cada comando ve el directorio como está. Un enlace que un proceso concurrente plante entre el recorrido y
     la escritura sigue siendo residual (ADR-0018, punto 2).

3. **Las excepciones de la regla 2, y por qué.** Pueden tener más de un nombre los ficheros de `objects/<xx>/`,
   `objects/pack/`, `objects/info/` y lo que cuelga de ella, y `lfs/objects/<aa>/<bb>/`, del directorio común y de cada
   directorio git bajo `modules/`:
   - **son inmutables:** git y git-lfs los escriben enteros, con un temporal y un renombrado, y nunca los reescriben en el
     sitio, así que un segundo nombre no deja escribir el otro fichero. Un enlace simbólico ahí sí se rechaza, por la regla 1,
     y sus carpetas también pasan esa regla;
   - **git los enlaza de forma legítima** (medido):
     - `git clone --local` da un segundo nombre a los objetos sueltos, a los paquetes (`.pack`, `.idx`, `.rev`), a
       `objects/info/packs`, a `objects/info/commit-graph` y a `objects/info/commit-graphs/*`;
     - el smudge del checkout de ese clon enlaza el objeto lfs del origen (`lfs/objects/<aa>/<bb>/<oid>`: 2 nombres con un
       clon, 3 con dos);
     - `git submodule add` de una ruta local enlaza los objetos sueltos del submódulo en `.git/modules/<nombre>/objects/<xx>/`;
   - **por eso `objects/info/` cuenta entera** (un commit-graph partido vive en `commit-graphs/`; medido quitando la
     excepción: la prueba del clon falla) y **por eso vale también en `modules/`** (medido igual: las tres pruebas de captura
     con submódulo fallan). Agent Studio nunca ejecuta git dentro de un submódulo (N-01);
   - `objects/info/` era la excepción por comprobar, porque no todo lo de ahí es un objeto. Queda medido que
     `git clone --local` la enlaza. Lo que git escribe ahí (`packs`, el commit-graph, `alternates`) lo escribe entero, con
     un temporal o un fichero de bloqueo y un renombrado; eso está leído en el código de git, no medido;
   - un fichero suelto directamente en `objects/`, en `lfs/`, en `lfs/objects/` o en `lfs/objects/<aa>/` sí se comprueba,
     y también todo `lfs/tmp`, `lfs/logs` e `lfs/incomplete`.

4. **Dónde corre**, siempre antes de que git corra:
   - **en la compuerta de `GitCommandRunner`**, antes de todo comando de contenido (`status`, `add`, `commit`, `reset`,
     `clean`, `checkout`, `merge`, `merge-tree`, `archive` y `worktree add`). Es lo primero de la compuerta, antes de la
     comprobación del almacén de git-lfs y de la clasificación de callbacks, así que git-lfs, que un comando de contenido
     puede arrancar, tampoco llega a correr. El `status` de `InspectAsync`, el momento de S-01, y el de la captura pasan por
     ahí;
   - **antes de los comandos que escriben refs u objetos sin leer el contenido:** `update-ref` (S-02) y `commit-tree`. No
     se inspeccionan (no ejecutan callbacks), pero ahora necesitan un `GitScope`, como los de contenido; sin él no se
     ejecutan (falla cerrado). Todos los escritores de refs se lo pasan;
   - **en `RequireDeliveryWithoutLinks`**, antes de la aprobación (`FindDeliveryRefusalsAsync`, que no pasa por la
     compuerta) y antes de la fusión, del `commit -F -` y del `merge --abort`;
   - **exclusión deliberada: `worktree remove`.** Solo borra la carpeta de administración de su propio worktree, que
     `DropRegistrationAsync` ya recorre entera antes (H-03, X-02), y un borrado no sigue un enlace duro. Así la limpieza
     (`RemoveAsync(discardChanges: true)`) sigue funcionando en un repositorio que el recorrido rechaza, como con la
     compuerta de callbacks.

5. **Segunda línea.** Las comprobaciones por destino se quedan como estaban: `RequireNoLinkOnRefPath`,
   `RequireWorktreesFolderWithoutLinks`, `RequireAdminDirectoryWithoutLinks`, `RequireGitFilesWithoutLinks` (con
   `DeliveryGitFiles`), `RequireObjectStoreWithoutLinks` y `RequireLfsStorageWithoutLinks`. Varias corren antes que la
   compuerta, y entonces su mensaje es el que se ve. Además miran lo que el recorrido no mira: que una carpeta resuelva bajo
   el común por su ruta final.

6. **Coste medido** (`GitDirectoryWalkMeasurementTests`, siete recorridos seguidos, con la máquina en uso normal):
   - **El `.git` de este repositorio**, de solo lectura: git solo contesta `rev-parse --git-common-dir`, con
     `GIT_OPTIONAL_LOCKS=0`, y el recorrido lista carpetas y abre ficheros para leer sus atributos. Tiene 278 carpetas y
     5 005 ficheros: 4 967 están en los almacenes de objetos y solo se listan, y 38 se abren para contar sus nombres.
     **27 ms de mediana** (25-35 ms; el primero, 35 ms). Ningún enlace.
   - **Un repositorio sintético con 10 000 refs sueltas, sus 10 000 reflogs y los objetos empaquetados.** Tiene 214
     carpetas y 20 028 ficheros, y se abren 20 024. **909 ms de mediana** (881-949 ms), unos 45 µs por fichero abierto. Lo
     caro es el handle por fichero; el listado cuesta unos 5 µs por entrada.
   - **Por operación** hay de uno a siete recorridos. `InspectAsync` hace uno; `CommitAllAsync` tres (`add`, `commit` y
     `update-ref`); `EnsureAsync` cuatro; una fusión tres; y una entrega con commit de fusión hasta siete. En este
     repositorio eso son 30-200 ms por operación; en el sintético, de 1 a 6 s.
   - Los reflogs no se empaquetan nunca (`pack-refs` solo empaqueta las refs), así que un repositorio con miles de ramas
     remotas paga sus reflogs aunque tenga las refs empaquetadas.
   - **No se cachea:** un cambio concurrente ya es residual, y una caché solo ensancharía la ventana. Si el coste molesta,
     hay dos alternativas que no cambian la regla: `GetFileInformationByName` (Windows 11 24H2), que da el número de
     nombres sin abrir un handle, y contar los nombres en paralelo. Ninguna de las dos se ha medido.

- **Cambio de contrato:**
  - Un repositorio con cualquier punto de reanálisis bajo su directorio git no se puede usar, aunque no sea un enlace:
    marcadores de posición de OneDrive u otro proveedor en la nube, ficheros deduplicados o comprimidos con
    `compact /exe` (WOF). Tampoco uno con `.git/hooks` enlazado a una carpeta versionada, que algunos montan así; para
    eso está `core.hooksPath`. Falla todo comando de contenido y toda escritura de refs u objetos. Tampoco se puede usar
    uno con un segundo nombre fuera de los almacenes de objetos.
  - `InspectAsync` y `CaptureUserRepositoryStateAsync` lanzan `WorktreeOwnershipException` con un enlace plantado en
    cualquier sitio del directorio git. Desde G-01 ya lo hacían con uno en el almacén de git-lfs, así que el motor ya trata
    así la captura. Dos pruebas antiguas tomaban la captura con la junction plantada: ahora la toman antes y después, y
    comprueban además que la captura se rechaza con la junction.
  - `GitScope` gana `GitDirectory` (opcional), y `update-ref` y `commit-tree` exigen un `GitScope`. En `IWorktreeManager`
    solo cambia la documentación (aditivo).
  - `RemoveAsync(discardChanges: true)` sigue limpiando.

- **Pruebas** (`GitDirectoryWalkTests`, `LocalTools`, con repositorios reales, git-lfs real sin red y víctimas fuera del
  repositorio que tienen que quedar intactas). Las quince de ataque vieron el fallo antes del cambio; las dos de lo que
  sigue funcionando son guardas y pasaban ya:
  - S-01: una teoría, `.git/config` como enlace simbólico y como enlace duro, con un fichero seguido por git-lfs.
    `InspectAsync` y `CommitAllAsync` se rechazan, el fichero de fuera no cambia y no corre `status`, `add` ni `commit`.
    Antes, el enlace simbólico dejó escrita la víctima.
  - S-02: una teoría, `packed-refs` como enlace duro y como enlace simbólico a una copia que contiene la ref. Se rechazan
    `DeleteRefAsync` y `FastForwardRunBranchAsync`, y no corre ningún `update-ref`. Antes, el enlace simbólico dejó
    reescrita la víctima.
  - S-03: un enlace duro en `lfs/incomplete/<oid>.part`; se rechazan `InspectAsync`, `CommitAllAsync` y
    `FindDeliveryRefusalsAsync`.
  - Sin lista: una teoría con siete destinos que no nombra ninguna comprobación (los del punto 1), cada uno contra
    `InspectAsync`, `FindDeliveryRefusalsAsync` y `EnsureRunBranchAsync`; el mensaje nombra el enlace plantado.
  - La clase entera: con un enlace duro en `FETCH_HEAD`, las dieciséis operaciones que escriben (de `InspectAsync` y la
    captura a la entrega) se rechazan sin ejecutar ningún comando que pueda escribir en el directorio git, y
    `RemoveAsync(discardChanges: true)` limpia después.
  - Regla 3: una carpeta de `hooks/` que no se puede listar.
  - El directorio git de un proyecto que es un worktree enlazado, con su `index` enlazado.
  - Lo que sigue funcionando: un repositorio hecho con `git clone --local` (objetos sueltos, paquete, `info/packs` y
    commit-graph partido con dos nombres, comprobado con `fsutil hardlink list`) hace todo el ciclo, de `InspectAsync` a la
    entrega y la limpieza; y un repositorio con git-lfs cuyo objeto lfs tiene dos nombres, sin que ninguna operación ejecute
    el filtro (por el pendiente de los hooks de git-lfs). Desde la adenda del fichero vacío, el commit del agente sí lo
    ejecuta.
  - `GitDirectoryWalkMeasurementTests` (`Measurement`, solo con `AGENTSTUDIO_WALK_MEASURE_ROOT`) mide el recorrido.

- **Consecuencias y límites:**
  - Las rondas siguientes ya no tienen que nombrar destinos bajo el directorio git: un enlace plantado ahí se rechaza
    esté donde esté. Lo que queda fuera es lo de siempre: un proceso concurrente (residual) y lo que git escribe fuera del
    directorio git.
  - **No medido:** la descarga de git-lfs que reabre un `.part` (sin red). El recorrido la cubre por la regla 2, sin
    depender de cómo escriba.
  - El recorrido supone el backend de ficheros, como el resto de comprobaciones; un repositorio reftable ya se rechaza
    (P-02).
  - **Anotado, fuera de este paquete:** sigue pendiente lo de los hooks que git-lfs instala en la carpeta vacía.
    Resuelto en la adenda del fichero vacío como `core.hooksPath` (más abajo).

## Adenda · 2026-10-18: el `.git` del árbol de trabajo nunca es un enlace (O-01)

La novena ronda observó que el recorrido del directorio común revisa sus entradas, pero no la propia raíz. Además, git
informa del directorio git por su ruta real, así que un `.git` convertido en junction llevaría todas sus escrituras a
otro sitio sin que el recorrido lo viera. Al abrir el repositorio del usuario o el del proyecto,
`WorktreeFileSystem.RequireDotGitWithoutLink` rechaza un `.git` que sea un reparse point. Un `.git` puede ser una
carpeta o un gitfile (un worktree enlazado o un directorio git aparte), y en ese caso se recorre su destino. El
recorrido rechaza además una raíz que sea un enlace o no sea una carpeta. Según ADR-0018 (punto 4) es un residual,
porque solo un escritor Medium planta ese enlace; se toma como mitigación barata, con su prueba
(`O01_A_common_directory_that_is_a_junction_is_refused_before_a_command_that_writes`).

## Adenda · 2026-10-18: el `core.hooksPath` propio es un fichero vacío, y git-lfs no instala sus hooks en él

La adenda de A-03 anotó, y las de G-01 y S-01 a S-03 arrastraron como pendiente, que git-lfs instala sus hooks en la
carpeta vacía `.no-hooks` y que el comando interno siguiente falla cerrado. Se dio por seguro. Al medirlo entero resulta
que no lo era del todo: en la fusión de la entrega, el hook que git-lfs escribe corre en el mismo comando. Es contenido
del repositorio (un fichero seguido por git-lfs), así que está dentro del modelo de
[ADR-0018](0018-modelo-de-amenaza-fase-3.md). Se decide:

1. **Medido** con git-lfs 3.5.1 y git 2.45.2, en repositorios desechables y sin red. `GIT_TRACE=1` hace que git-lfs
   trace cada instalación («Install hook: <hook>, force=false, path=…»).
   - **Quién instala.** Los tres filtros, al arrancar y antes de atender ningún fichero:
     - `filter-process`, el de `git lfs install` y el de la configuración de sistema de Git for Windows;
     - `clean -- %f` y `smudge -- %f`, de un solo fichero.

     Escriben `pre-push`, `post-checkout`, `post-commit` y `post-merge` en el `core.hooksPath` que reciben, que es el
     `-c` de Agent Studio. Son la plantilla estándar: `command -v git-lfs … || exit 2` y `git lfs <hook> "$@"`.
   - **Cuándo.** Siempre que corre un filtro:
     - con la carpeta vacía de Agent Studio, escribe los cuatro;
     - sin la carpeta, la crea y los escribe;
     - sin `git lfs install` en el repositorio: basta el `filter.lfs.*` de `C:\Program Files\Git\etc\gitconfig`;
     - con `lfs.repositoryformatversion` y sin ella;
     - con `filter-process --skip`, `GIT_LFS_SKIP_SMUDGE=1`, `GIT_LFS_SKIP_PUSH=1` o `GIT_LFS_TRACK_NO_INSTALL_HOOKS=1`.

     El `git lfs post-merge` que lanza uno de esos hooks no instala nada.
   - **Ninguna opción lo evita.** Las `GIT_LFS_*` del binario (`mingw64\bin\git-lfs.exe`) son `FORCE_PROGRESS`,
     `PROGRESS`, `SET_LOCKABLE_READONLY`, `SKIP_DOWNLOAD_ERRORS`, `SKIP_PUSH`, `SKIP_SMUDGE` y
     `TRACK_NO_INSTALL_HOOKS`. Esta última solo la lee `git lfs track`: medido, el filtro instaló igual.
     - Ninguna clave `lfs.*` del binario trata de hooks.
     - `--skip-repo` es de `git lfs install` y `uninstall`, no de los filtros.
     - Las páginas de `filter-process`, `clean` y `smudge` (`mingw64\share\doc\git-doc`) no nombran los hooks.
   - **Un hook escrito así corre en el mismo comando.** Con una carpeta vacía como `core.hooksPath`, el smudge de
     `git merge --ff-only` escribió `post-merge`, y git lo ejecutó al terminar la fusión. La traza muestra
     `run_command: …/post-merge 0` y después `git lfs post-merge 0`, que dejó de solo lectura el fichero `lockable`.
     - Es la fusión de la entrega. Con la carpeta `.no-hooks`, entregar un commit con ficheros lfs ejecutaba el hook de
       git-lfs con los permisos del usuario, y `EnsureNoHookCanRun`, que mira antes del comando, no llegaba a verlo. La
       prueba de la entrega lo reprodujo antes del cambio.
     - En el resto del ciclo, el comando siguiente fallaba cerrado: `add` y luego `commit`, `reset --hard` y luego
       `update-ref`, el `status` de `InspectAsync` y luego `EnsureAsync`.
   - **Con un fichero vacío como `core.hooksPath`** todo funciona y no se instala nada. Se trazaron `filter-process` en
     `add` y en `merge --ff-only`, y `clean` y `smudge` sueltos en `add` y en `reset --hard`:
     - código 0, el objeto guardado y el puntero y el contenido correctos;
     - ninguna línea «Install hook» y ningún hook ejecutado (sin `post-merge` en la fusión, y el `lockable` escribible);
     - el fichero sigue con 0 bytes;
     - `.git/hooks` del repositorio sigue igual, así que git-lfs no cae en él, y no aparece `lfs/logs`.

     `commit`, `checkout` y `reset --hard` con esa ruta también terminaron con código 0.

     De ahí se deduce, sin haberlo leído en el código, que git-lfs crea la carpeta de hooks antes de escribirlos, que con
     un fichero en su lugar falla, y que los filtros ignoran ese error.
2. **Decisión** (`GitCommandRunner`). El `core.hooksPath` de todo comando interno pasa a ser un **fichero vacío** propio,
   `<raíz de worktrees>\.no-hooks`. Es la misma ruta de antes, que era una carpeta.
   - git busca cada hook debajo de esa ruta, y debajo de un fichero no puede haber nada. No encuentra ningún hook, lo
     haya escrito quien lo haya escrito y en el momento que sea, también dentro del mismo comando.
   - git-lfs no puede crear ahí su carpeta: no instala nada y su filtro sigue.
   - Es más fuerte que la carpeta vacía, que dependía de que nadie la llenara entre la comprobación y el uso.

   **`EnsureNoHookCanRun`**, antes de todo comando:
   - acepta el fichero vacío: sin punto de reanálisis y con 0 bytes;
   - rechaza un enlace, una carpeta con algo dentro y un fichero con contenido, sin vaciar ni sustituir nada. Lo pudo
     dejar git-lfs con una versión anterior o un escritor Medium, y el usuario lo mira antes de borrarlo;
   - antes de un comando de contenido, con su `GitScope`, pone el fichero si falta. Son los únicos comandos que pueden
     arrancar git-lfs. El primero puede ser el `status` de `InspectAsync`, antes de que exista ningún worktree, y sin nada
     en esa ruta git-lfs crea la carpeta y la llena (medido);
   - la carpeta vacía de una versión anterior se sustituye por el fichero, con un borrado no recursivo: si aparece algo
     dentro, falla y git no corre;
   - nunca crea el fichero dentro del repositorio del comando. Con la raíz de worktrees dentro, el comando se rechaza,
     como ya hacía `EnsureAsync`;
   - un comando que no es de contenido no arranca git-lfs: sus diffs son entre árboles o contra el índice. Para él, que
     falte la ruta o que sea la carpeta vacía es tan seguro como antes.

   `EnsureAsync` crea el fichero, como antes la carpeta, tras comprobar que la raíz de worktrees queda fuera del
   repositorio.
3. **Alternativas descartadas:**
   - una opción de git-lfs que no instale hooks: no existe (punto 1);
   - una carpeta vacía nueva por comando, creada y borrada por Agent Studio: git ejecuta en el mismo comando el hook que
     git-lfs escribe en ella (medido con `merge --ff-only`);
   - aceptar en la carpeta los hooks con la plantilla de git-lfs: correrían (`git lfs post-merge` cambia los permisos de
     ficheros del usuario), y habría que relajar `EnsureNoHookCanRun`;
   - una ACL que niegue crear ficheros en la carpeta: más piezas para el mismo efecto. El propietario puede cambiar la
     ACL, y git-lfs fallaría igual al escribir.
4. **Quién puede tocar la ruta** (ADR-0018). La raíz de worktrees es de Agent Studio:
   - el ejecutor por defecto de Claude queda confinado a su worktree (segunda medición de ADR-0018);
   - la verificación aislada (Low) no escribe fuera de su área.

   Solo un escritor Medium puede sustituir el fichero por una carpeta con hooks. Antes del comando lo rechaza
   `EnsureNoHookCanRun`; entre la comprobación y el uso es el residual de siempre (ADR-0018, punto 2).

- **Pruebas** (`GitLfsHooksTests`, `LocalTools`, con git-lfs real y sin red). Los centinelas son hooks del usuario que
  dejan un registro (también en lugar de los de `git lfs install --local`) y un fichero lfs `lockable`, que los hooks de
  git-lfs dejan de solo lectura si corren. Un control con git normal fija la medición en la suite: con una carpeta vacía
  como `core.hooksPath`, git-lfs la llena durante la fusión y git ejecuta `post-merge`, que deja el `lockable` de solo
  lectura; con el fichero vacío, la misma fusión no instala ni ejecuta nada. Vieron el fallo antes del cambio:
  - el ciclo entero en un repositorio con `git lfs install --local`, `*.dat` y `*.lck lockable`, con el filtro en cada
    paso:
    - el `status` de `InspectAsync` sobre un cambio del mismo tamaño;
    - el checkout del worktree nuevo;
    - `add` y `commit` de los ficheros lfs del agente;
    - la integración (`merge-tree`, `commit-tree`, `update-ref`) y el checkout de `_integration`;
    - la entrega `--ff-only` y la limpieza.

    En los commits quedan punteros, y en cada árbol de trabajo el contenido real. Ningún hook corre, `.git/hooks` no
    cambia, el `lockable` sigue escribible y la ruta sigue siendo el fichero vacío. Antes del cambio, tras `InspectAsync`
    la ruta ya era una carpeta con los cuatro hooks de git-lfs;
  - la entrega que trae los primeros ficheros lfs del repositorio, en los dos modos. Antes del cambio, con `--ff-only` el
    `post-merge` que git-lfs escribió durante la fusión corrió y dejó el `lockable` de solo lectura; con commit de
    fusión, el `commit` tras `merge --no-commit` falló cerrado y dejó la fusión a medias;
  - el primer comando que puede arrancar git-lfs encuentra el fichero vacío: git-lfs guarda el objeto, no escribe ningún
    hook y no deja `lfs/logs`;
  - el fichero nunca se crea dentro del repositorio del comando;
  - la carpeta vacía de una versión anterior se sustituye por el fichero;
  - una carpeta con algo dentro no se vacía ni se sustituye, y todo comando falla. Antes también fallaba, pero con el
    mensaje de la carpeta;
  - un fichero con contenido hace fallar todo comando;
  - `GitWorktreeLifecycleTests`: tras la limpieza solo queda el fichero vacío.

  Son guardas, y ya pasaban: una junction en la ruta, y la prueba de `GitWorktreeTamperingTests`, adaptada para
  sustituir el fichero por una carpeta con un hook. Además, dos pruebas que esquivaban los ficheros lfs por este
  pendiente ahora los usan, y también fallaban antes del cambio: `The_installed_git_lfs_by_its_absolute_path_is_still_allowed`
  (`GitCallbackTests`) y `A_repository_with_git_lfs_whose_lfs_objects_are_hard_links_keeps_working`
  (`GitDirectoryWalkTests`).
- **Consecuencias y límites:**
  - Un repositorio que usa git-lfs de verdad se puede orquestar. Se cierra la limitación conocida de las adendas de A-03,
    G-01 y S-01 a S-03.
  - En los comandos internos y en la entrega nunca corren los hooks de git-lfs. `post-checkout`, `post-commit` y
    `post-merge` ponen de solo lectura los ficheros `lockable` que el usuario no ha bloqueado; tras una entrega quedan
    escribibles. Quien quiera ese efecto ejecuta `git lfs post-merge 0` a mano. `pre-push` no aplica: no hay push.
  - La afirmación queda fijada a git-lfs 3.5.1 y hay que volver a medirla al actualizarlo. Si un git-lfs futuro tratara
    ese error como fatal, el filtro y el comando fallarían: se vería un fallo, nunca un hook ejecutado.
  - Cambio de contrato: `.no-hooks` es un fichero. Si una versión anterior dejó ahí la carpeta con los hooks de git-lfs,
    todo comando falla hasta que el usuario la borra.
  - Sigue siendo residual (ADR-0018, punto 2) que un escritor Medium concurrente sustituya el fichero entre la
    comprobación y el uso.

## Adenda · 2026-10-19: la entrega respeta la impugnación de un veredicto y la petición de replanificar (Fase 5)

La Fase 5 ([15](../15-fase-5-debate-arena-y-juez.md), D-35 y D-40) añade dos cosas que pueden pasar después de que una
ejecución termine: el usuario impugna un veredicto, lo que abre un debate de revisión, y el auditor del orquestador (o el
juez de una revisión del orquestador) pide replanificar. Con la decisión 6(a) de la §12, las dos bloquean la entrega hasta
que el usuario decide. El arquitecto encontró además que `CheckAsync` bloqueaba cualquier veredicto sustituto, también uno
que confirmara la aprobación: una impugnación resuelta a favor dejaba la ejecución sin entregar para siempre. Se decide
(`DeliveryService.CheckAsync`; lo prueba `DeliveryChallengeTests`):

1. **Lo que no cambia.** Todo lo de la adenda del 2026-10-16 (punto 1) y las siguientes: registro que verifica,
   `RunFinished` en `Succeeded` con commit y veredicto, la integración revisada es la de `RunFinished`, ninguna integración
   posterior al veredicto, la rama de la ejecución sigue en ese commit y la aprobación atada a la entrega. Una revisión
   cambia qué veredicto cuenta, nunca el final de la ejecución: una ejecución que terminó `Failed` porque el juez pidió
   cambios no se entrega aunque una revisión la apruebe; se lanza una de seguimiento. Sin impugnaciones, la comprobación
   es la de la Fase 3, con sus mensajes, y `DeliveryServiceTests` pasa sin cambios.
2. **Qué cuenta cuando se impugna.** Todo se lee del registro verificado y del almacén de veredictos, sin git:
   - `VerdictChallenged`, en la ejecución del veredicto, nombra el veredicto y su debate de revisión;
   - el debate se abre con `DebateOpened`, que impugna ese veredicto (`Challenges`), sobre los mismos requisitos y, si
     nombra la copia (`SnapshotCommit`), con la integración de la ejecución («del mismo commit»);
   - se cierra una sola vez con `DebateClosed`, regla de un juez o de un jurado y resultado `Resolved`;
   - sus veredictos están anunciados en la ejecución después de la impugnación (D-40), son válidos, juzgan los mismos
     requisitos, llevan `Supersedes` hasta el impugnado (a través de la corrección de su juez), y el almacén los confirma; los
     de un jurado dicen lo mismo.

   Una revisión que no cuadra bloquea, con el detalle. Las reglas, por cada juicio (el veredicto con el que terminó la
   ejecución, o los de una revisión):
   - una impugnación cuya revisión se abrió y no se ha cerrado bloquea;
   - la última impugnación de un juicio es su última palabra: bloquea si su revisión no llegó a abrirse, se abortó,
     necesita a una persona o la decidió el usuario. Una anterior en esos estados cede ante la última;
   - una impugnación cuya revisión la persona decidió no abrir (su «no» a `debate.open`, en el debate) nunca empezó y no
     cuenta; un «no» de la puerta en su nombre (la pregunta caducó) no es una decisión, así que esa sí bloquea;
   - cada revisión juzgada es una rama, y toda rama tiene que acabar en una aprobación válida que cuente: la que confirma
     una aprobación que contaba (no hace falta nada más: es el hallazgo del arquitecto), o una que el usuario aceptó. Una
     revisión que retira la aprobación bloquea, y volver a impugnar el original no la deshace;
   - una revisión que da la vuelta hacia una aprobación (el veredicto revisado no era la aprobación válida de un juez, por
     ejemplo una revisión escalada que aprobó una persona) solo cuenta con la aceptación expresa del usuario;
   - la impugnación de otro veredicto de la misma ejecución (el primer intento que corrigió su juez) es sobre el mismo
     trabajo: su revisión tampoco puede retirar la aprobación, aunque no hace falta aceptarla;
   - un veredicto que sustituye a uno de un juicio sin una impugnación anterior bloquea, como en la Fase 3, y también uno
     que solo está en el almacén.
3. **La aceptación** (`DeliveryService.RevisionAcceptanceAction`, `verdict.revision.accept`) es el sí de una persona
   (`ApprovalGranted`, actor `User`) cuya petición, la última `ApprovalRequested` de esa acción en la misma unidad del
   registro, que su respuesta gasta, es posterior al cierre de la revisión y a sus veredictos. La puerta no escribe la
   atadura en el registro, así que el registro la ata por la unidad y el orden: en el debate de revisión acepta esa
   revisión; en la ejecución, la revisión que se cerró la última antes de pedirla. Una negativa, una respuesta del sistema
   o una pedida antes del cierre no aceptan nada.
4. **«Decidir yo» y el aborto bloquean** (falla cerrado). `DebateClosedPayload` no lleva la decisión de la persona, solo la
   regla `user`, y una decisión de una persona no es la aprobación de un juez independiente (adenda del 2026-10-16,
   punto 1). La salida es volver a impugnar el veredicto, que abre otra revisión, o una ejecución de seguimiento.
5. **La petición de replanificar** (`ReplanRequested` de la ejecución, del auditor o del juez) bloquea hasta que se
   atiende: un sí de una persona a descartarla (`DeliveryService.ReplanDismissalAction`, `replan.dismiss`, en la
   ejecución) pedido después de ella, o una ejecución de seguimiento (`RunStarted.Follows`) que empezó después. Una petición
   nueva vuelve a bloquear.
6. **Lo que no hace la entrega.** No lanza al auditor: corre al terminar cada ejecución, y eso lo cablea el escritorio.
   `CheckAsync` sigue siendo de solo lectura. Si la disponibilidad de la entrega se calcula antes de que el auditor escriba
   su petición, el botón puede ofrecerse un momento; `DeliverAsync` vuelve a leer el registro antes de pedir la aprobación,
   así que una petición ya escrita bloquea. El orden (auditor, después la disponibilidad) es de la integración.
- **Pruebas** (`DeliveryChallengeTests`): la revisión que confirma deja entregar, y también la impugnación que el usuario
  decidió no abrir; la pendiente, la que no se abrió, la abortada, la que necesita a una persona y la decidida por el
  usuario bloquean; la que retira bloquea, también si se
  vuelve a impugnar el original; la vuelta hacia la aprobación bloquea hasta la aceptación, en la ejecución o en el
  debate, y se entrega; las aceptaciones que no cuentan; las revisiones que no cuadran (doce maneras); el jurado; la
  corrección dentro de la revisión; la impugnación del intento corregido; la petición de replanificar, su descarte y la
  ejecución de seguimiento, con los descartes que no cuentan. Ninguna pide aprobación ni toca git cuando bloquea.
- **Consecuencias:**
  - La entrega solo se vuelve más estricta, salvo la aceptación expresa de una revisión (§11 de docs/15).
  - El comentario de `IDeliveryService.CheckAsync` en `Application/Git/GitContracts.cs` («superseded by none») se queda
    corto: lo actualiza la integración.
  - Los escritores de la impugnación (WP3), de su revisión (WP1 y WP2) y del descarte (el monitor) tienen que seguir estas
    formas; no pueden ver las constantes de `Orchestration`, así que repiten las acciones, y una prueba de la integración
    debe compararlas.
  - Si se quiere que «Decidir yo» confirmando una aprobación deje entregar, `DebateClosedPayload` necesita la decisión (una
    versión 2) y esta regla se revisa.
