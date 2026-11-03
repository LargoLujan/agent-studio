# ADR-0017 · Verificación aislada bajo integridad Low

- **Estado:** aceptada · 2026-10-16. Responde al hallazgo crítico de la revisión adversarial y al spike
  [2026-10-16-spike-verificacion-aislada.md](../investigacion/2026-10-16-spike-verificacion-aislada.md). Lo implementan
  `Infrastructure/Processes` (el lanzador y las carpetas de trabajo) y `Orchestration/Verification` (el ejecutor). Amplía
  [ADR-0016](0016-perfil-de-verificacion.md), que no cambia salvo en la adenda de su final.
- **Contexto:**
  - Después de que un agente escribe código, `VerificationRunner` ejecuta `dotnet build` y `dotnet test` sobre él con el
    token del usuario (integridad Medium). Un target de MSBuild o una prueba que el propio agente escribió puede, mientras
    se ejecuta, escribir en cualquier sitio donde escriba el usuario: su repositorio, `.git/hooks`, los almacenes de la
    app. El Job Object solo mata el árbol de procesos; no impide una escritura.
  - El spike demostró en esta máquina, sin administrador, que `restore`, `build` y `test` (xUnit v3 sobre
    Microsoft.Testing.Platform, con informe TRX) funcionan bajo un token de integridad **Low** (S-1-16-4096), y que la
    política obligatoria de Windows (*no-write-up*) bloquea entonces toda escritura fuera de las carpetas etiquetadas Low:
    un target `BeforeTargets="Build"` y una prueba que intentan escribir en un «repositorio del usuario» fallan con
    *access denied*, y esa carpeta queda intacta. El Job Object sigue conteniendo y matando el árbol.
- **Decisión:**
  1. **`ProcessSpec.Isolation` (Application, aditivo).** `None` (como hoy) y `LowIntegrity`. Solo la verificación lo pide;
     el resto de llamadas no cambia.
  2. **Lanzador Low (`Infrastructure/Processes/Isolation`).** Con P/Invoke `LibraryImport`:
     - se duplica el token de Agent Studio y se le baja la integridad a Low (`SetTokenInformation`,
       `TokenIntegrityLevel`); bajar el nivel del propio token no necesita privilegio;
     - `CreateProcessAsUser` con el token Low, suspendido, con entorno Unicode y sin ventana;
     - stdin, stdout y stderr son tuberías anónimas, y `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` (en `STARTUPINFOEX`) hace que
       esos tres sean los **únicos** handles que hereda: un handle heredado de Agent Studio se saltaría la comprobación de
       integridad (se hace al abrir, no al usar un handle heredado);
     - cada proceso entra en un Job Object propio con *kill-on-close* antes de reanudarse; matar es terminar el Job, y la
       sesión devuelve −1 cuando mata (un proceso muerto por el Job sale con 0). Se mantiene el streaming de stdin/stdout/
       stderr y el troceado de líneas exactamente como sin aislamiento.
     - El ejecutable se resuelve a ruta completa antes de lanzar (carpetas del sistema primero, luego el PATH, solo
       entradas absolutas), nunca la carpeta de Agent Studio ni la actual.
     - **Falla cerrado:** si no se puede crear el token, si Agent Studio corre elevado (High o más: un duplicado elevado
       conservaría los grupos de administrador), si no se puede limitar la herencia de handles o si falla cualquier paso,
       el arranque lanza `ProcessIsolationException` y **no** se ejecuta nada. Nunca se cae a Medium.
     - Siguen aplicando las validaciones de argumentos y metacaracteres de `ProcessLaunch`.
  3. **Área de trabajo Low por ejecución (`LowIntegrityWorkAreaFactory`).** Bajo
     `%USERPROFILE%\AppData\LocalLow\AgentStudio\verify\v-<aleatorio>` (con el cerrojo del usuario tomado, decisión 6):
     - las carpetas que faltan de la raíz se crean etiquetadas **Medium**, para que ningún proceso Low pueda plantar nada
       a ese nivel mientras se prepara el área; la carpeta de la ejecución nace Medium y se comprueba que se alcanza sin
       enlaces;
     - el árbol del commit lo escribe, mientras la carpeta sigue Medium, la capa git endurecida
       (`IWorktreeManager.ArchiveCommitAsync`, desde el 2026-10-17; la fábrica no ejecuta git ni lee ningún worktree):
       `git archive <commit>` a un fichero temporal, que lee un extractor `TarReader` propio que solo escribe ficheros y
       carpetas, dentro del destino, sin rutas absolutas, sin `..`, sin enlaces ni dispositivos, sin nombres reservados de
       Windows y sin sobrescribir; `git archive` corre con un `--git-dir` explícito que nombra un repositorio privado y
       vacío cuyo único objeto es el del directorio común **del repositorio del proyecto** (un *alternate*), con el entorno
       saneado, `core.hooksPath` vacío y `--end-of-options` antes del commit, como el resto de la capa; así no se lee
       ninguna configuración, hook o filtro del repositorio, y sus `info/attributes` apagan `export-ignore`,
       `export-subst`, `filter` e `ident`, de modo que la copia es el commit y no lo que sus atributos harían de él; se
       conservan los finales de línea del repositorio (`core.autocrlf`, `core.eol`, leídos con `--git-dir` del
       repositorio del proyecto), como en un checkout; se comprueba que el commit que `git archive` grabó en la cabecera
       del tar es el pedido;
     - la carpeta se reetiqueta **Low**, que Windows propaga a la copia, y dentro se crean las carpetas de la ejecución
       (TEMP, `DOTNET_CLI_HOME`, paquetes y cachés de NuGet);
     - solo se etiquetan o borran carpetas que el propio proceso creó; nunca carpetas del usuario, del sistema ni la caché
       real de NuGet;
     - al terminar la ejecución se cierra el Job, se lee el informe con las precauciones de abajo y se borra el área con un
       borrado que no sigue puntos de reparación (el patrón de la capa git, reutilizado).
  4. **Ejecutor (`VerificationRunner`).** El aislamiento está **activado por defecto** en los pasos de verificación:
     - un paso aislado copia el commit bajo verificación (`EvidenceScope.SubjectCommit`) del repositorio del proyecto
       (`EvidenceScope.RepositoryRoot`, que el motor y el `Integrator` pasan), nunca a través del `.git` del worktree; sin
       commit, sin repositorio o sin la capa git, el paso no se ejecuta. Se ejecuta en esa copia con
       `Isolation = LowIntegrity` y el entorno del spike: `NUGET_PACKAGES`,
       `NUGET_HTTP_CACHE_PATH`, `NUGET_PLUGINS_CACHE_PATH`, `DOTNET_CLI_HOME`, TEMP/TMP a carpetas Low; más
       `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT`, `DOTNET_NOLOGO`,
       `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false`, `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`, y el entorno fijo de ADR-0016
       (`MSBUILDDISABLENODEREUSE`, `DOTNET_CLI_USE_MSBUILD_SERVER=0`, `UseSharedCompilation=false`,
       `DOTNET_CLI_UI_LANGUAGE=en`);
     - todos los pasos de una ejecución comparten una misma área, así que un paso de test encuentra lo que compiló el paso
       de build; el worktree ya no lo escribe la verificación;
     - el informe TRX/JUnit de un paso aislado tiene que estar **dentro de la carpeta `{results}` de ese paso**: una
       carpeta nueva del área (si ya existe no se reutiliza) cuya ruta final se comprueba sin enlaces, también por encima
       del área. Cualquier otra ruta del área, la copia sobre todo, puede ser un enlace que dejó un paso anterior, y el
       ejecutor tiene los permisos del usuario: un perfil con una ruta fija (`TestResults/x.trx`, o `{results}/../…`) no
       se ejecuta aislado, falla cerrado. Como la carpeta es nueva, antes de un paso aislado no se borra nada;
     - el informe se lee solo cuando los procesos del paso ya han terminado, como fichero regular abierto sin seguir
       enlaces, con un tamaño acotado y solo si la ruta final del fichero abierto es la del área tal como se creó (un
       enlace en cualquier punto del camino, dentro o por encima del área, lleva a otra ruta y se rechaza); la salida de
       cada paso se escribe en la carpeta *scratch* (Medium), nunca en el área que los procesos pueden escribir;
     - la evidencia conserva su `SubjectCommit`, que es justo el commit copiado y verificado.
  5. **Política de desactivación.** Un perfil puede apagar el aislamiento **por paso** solo con un campo explícito
     (`"isolation": "none"`); cualquier otro valor es un error, nunca un «none» en silencio. Un paso sin aislamiento se
     ejecuta como antes (en el worktree, con los permisos del usuario). Que esté apagado aparece en el texto de aprobación
     del plan (`VerificationStepText.Line`) y en los metadatos de la evidencia (el título acaba en «· sin aislamiento» y la
     cabecera lleva `# isolation: none`).
  6. **De una en una por usuario.** Todos los procesos Low del usuario comparten identidad y nivel: la integridad no
     separa dos verificaciones Low entre sí, tampoco las de dos instancias de Agent Studio. Hasta tener una identidad por
     ejecución, solo corre una verificación aislada del usuario a la vez (desde el 2026-10-17, hallazgo N-03):
     - en el proceso, un semáforo (`VerificationRunner`); entre procesos, un **cerrojo del usuario**: el fichero
       `%LOCALAPPDATA%\AgentStudio\verify.lock`, abierto sin compartir nada y sin seguir enlaces. La fábrica lo toma antes
       de crear el área y el área lo suelta después de borrarse, así que cubre la creación, los pasos, la lectura de los
       informes y la limpieza; Windows lo suelta si el proceso muere. El orden es siempre semáforo y luego cerrojo, y la
       espera no tiene límite propio (como la del semáforo): se cancela con la ejecución;
     - vive donde ningún proceso Low puede crear, renombrar ni borrar nada (toda la cadena es Medium), no bajo `LocalLow`,
       y nace con la etiqueta `S:(ML;;NWNRNX;;;ME)`: un proceso Low no puede ni abrirlo para leer, así que no puede
       retenerlo para que no corra ninguna verificación (ver lo medido abajo);
     - con el cerrojo tomado y antes de crear el área, se borran, sin seguir enlaces, las áreas que dejaron ejecuciones
       que no terminaron (un cierre inesperado): solo las carpetas de la raíz con el nombre de un área (`v-` y 12 cifras
       hexadecimales). Un área con el nombre de antes (12 cifras, de una versión que no toma el cerrojo) no se toca: podría
       estar en uso;
     - la cabecera de la evidencia dice lo que Low bloquea de verdad: las escrituras en lo etiquetado Medium o más; las
       carpetas Low (el área, otras de `LocalLow`) siguen siendo escribibles.
- **Qué bloquea y qué no (medido en el spike y en las pruebas de integración):**
  - **Bloquea escrituras:** un target de MSBuild y una prueba que intentan escribir en una carpeta Medium que hace de
    repositorio del usuario fallan, y la carpeta queda intacta. Es el hallazgo que se cierra.
  - **Enlaces (medido el 2026-10-17):** un proceso Low no puede crear una unión hacia una carpeta Medium (la mitigación de
    Windows para tokens *sandboxed* exige poder escribir el destino) ni un enlace simbólico de carpeta (exige un
    privilegio), pero sí una unión hacia una carpeta Low. Aun así, el ejecutor nunca borra ni lee una ruta del área que
    un paso haya podido convertir en enlace (decisión 4).
  - **Renombrar y retener (medido el 2026-10-17):** un proceso Low **sí** puede renombrar una carpeta Medium cuyo padre
    es Low (`FILE_DELETE_CHILD` en el padre), por ejemplo `LocalLow\AgentStudio`, siempre que no haya ningún handle
    abierto dentro; con uno abierto (un fichero o un directorio de dentro), Windows lo deniega. Un proceso Low también
    puede abrir para leer un fichero Medium sin `NR` (y retenerlo sin compartir); con `NWNRNX` se le deniega.
  - **No bloquea lecturas:** Low es *no-write-up*, no *no-read-up*. El código del agente que corre bajo Low **lee** todo lo
    que lee el usuario: otros repositorios, `%USERPROFILE%`, la configuración de NuGet (que puede llevar credenciales de
    feeds), perfiles de navegador, credenciales de los CLIs.
  - **No bloquea la red:** DNS, TCP saliente y el bucle local funcionan bajo Low (el propio restore los necesita). Cortar
    la red a un proceso Low necesitaría reglas de firewall, que exigen administrador.
  - Juntas, lectura y red dejan abierta la **exfiltración** de lo que el usuario pueda leer. El aislamiento demuestra que
    el código verificado no **escribió** los ficheros del usuario, no que sea de fiar.
- **Por qué no AppContainer ni Windows Sandbox ahora:**
  - **AppContainer** sí bloquea también lecturas y red (sin capacidades), y daría una identidad por ejecución con la que
    separar verificaciones simultáneas. Pero su coste de adopción es alto: perfil (`CreateAppContainerProfile`), un ACE
    para el SID del contenedor en cada carpeta que use el SDK y la caché, la capacidad de red para el restore (que también
    ejecuta MSBuild del agente), y la compatibilidad de MSBuild/NuGet/MTP dentro de un AppContainer está **sin verificar**.
    Low IL es lo único comprobado de punta a punta sin administrador y resuelve exactamente el hallazgo (escrituras).
  - **Windows Sandbox** aísla en una VM, pero activar la característica exige administrador y no está instalado en esta
    máquina; una VM por arranque, sin el SDK dentro y con una sola instancia a la vez, no encaja en el flujo.
- **Plan para la Fase 7:**
  - pasar a **AppContainer por ejecución** (o a un token restringido con un SID propio) para bloquear también las lecturas
    y la red y para separar verificaciones simultáneas, lo que permitiría quitar la restricción «de una en una» (el
    semáforo y el cerrojo del usuario);
  - etiquetar además las carpetas de datos de la propia Agent Studio (`.agentstudio`, evidencias, SQLite) como
    `NO_READ_UP` para una defensa en profundidad frente a la lectura;
  - estudiar un diseño en dos fases (restore con red, build y test sin red) y la caché real del usuario como *fallback* de
    solo lectura, para bajar el sobrecoste del restore en frío.
- **Consecuencias y pendientes:**
  - **Sobrecoste (medido aquí, proyecto pequeño):** build + test en el sitio con la caché caliente ≈ 3,8–4,0 s; aislado,
    con copia del commit y caché NuGet Low **por ejecución** en frío, ≈ 11 s. El delta (≈ 7 s) lo domina el restore en
    frío (22 paquetes), como en el spike; la integridad Low en sí no añade coste medible. La caché por ejecución es la
    decisión de seguridad (una caché Low compartida sería escribible por cualquier build Low y podría envenenarse).
  - **Persistencia (resuelto el 2026-10-17).** `RunJson.VerificationStepJson` guarda `isolation` solo en el paso que lo
    apaga (`"isolation":"None"`): un perfil que aísla todos sus pasos se guarda con el mismo texto que antes, y un paso
    guardado sin el campo es `LowIntegrity`, el valor seguro. Al reanudar, `RunResumer` compara el hash del perfil de la
    cabecera con el `VerificationProfileHash` de `RunStarted` (en el registro verificado); si no coinciden, la ejecución
    no es reanudable y queda un `AuditAlertRaised` (`run.not-resumable`). Así, una ejecución que empezó con un paso
    `none` antes de este cambio (guardado sin el campo) no se reanuda con otro perfil: hay que empezar otra.
  - **Confianza en el `.git` del worktree (resuelto el 2026-10-17).** La fábrica localizaba el almacén de objetos a
    partir de `<worktree>\.git` (y de su `commondir`), un fichero que el agente puede reescribir. Medido con git 2.45.2:
    un `.git` que apunta a un almacén plantado con un objeto **commit** falsificado (otro árbol bajo el mismo id) hace
    fallar `git archive` (`hash mismatch`: comprueba el hash del commit que archiva), pero uno con un **blob**
    falsificado bajo el id de un fichero del commit (y `objects/info/alternates` al almacén real para el resto) sí
    cambiaba lo verificado: la prueba `IsolatedCopyGitFileTests` lo reprodujo antes del arreglo (el paso leyó «contenido
    plantado» para el commit revisado). Ahora el motor (`EvidenceScope.RepositoryRoot = Project.RootPath`) y el
    `Integrator` pasan la raíz del repositorio del proyecto, y el ejecutor copia el commit con
    `IWorktreeManager.ArchiveCommitAsync`, que localiza el directorio común como el resto de la capa git (rechaza
    cualquier carpeta bajo la raíz de los worktrees) y nunca lee el `.git` del worktree. La misma prueba pasa con el
    arreglo. Queda fuera de esto lo que ya no es el `.git` del worktree: un agente con permisos de usuario podría
    escribir objetos sueltos en el almacén del propio proyecto, que ni la captura del repositorio ni el juez examinan.
  - **Informe a través de un enlace (resuelto el 2026-10-17, hallazgo N-02 de la segunda revisión).** El ejecutor
    validaba la ruta del informe como texto dentro del área y, antes del paso, la borraba con el token del usuario
    (`File.Exists`/`File.Delete`), así que una unión que un paso anterior dejara en la copia (`src\reports` → una carpeta
    Medium del usuario) le hacía borrar el `results.trx` del usuario. `IsolatedReportPathTests` lo reprodujo antes del
    arreglo (con la unión hecha por la prueba) y pasa con él: el informe aislado solo puede estar en su `{results}` nuevo,
    no se borra nada dentro del área con el token Medium (solo el borrado final del área, que no sigue enlaces) y cada
    lectura comprueba la ruta final del fichero abierto. Medido aquí (Windows 11): la mitigación de puntos de montaje
    para tokens *sandboxed* ya impide a un proceso Low crear una unión hacia una carpeta que no puede escribir (*acceso
    denegado*; hacia una carpeta Low, sí), y un enlace simbólico de carpeta exige un privilegio que no tiene. El arreglo
    no depende de esa mitigación.
  - **Verificaciones de dos instancias (resuelto el 2026-10-17, hallazgo N-03 de la segunda revisión).** La exclusión
    era un `SemaphoreSlim` estático, de un solo proceso, y todas las áreas viven etiquetadas Low bajo la misma carpeta
    del usuario: un build malicioso de una instancia podía alterar la copia o la caché NuGet Low de otra mientras
    verificaba. `IsolatedRunExclusionTests` (dos ejecutores, cada uno con su semáforo y su fábrica) y las pruebas del
    cerrojo de `LowIntegrityWorkAreaTests` (otro proceso real que lo retiene, sobras con un enlace dentro, liberación si
    el área no se puede preparar) fallaban antes del arreglo (decisión 6) y pasan con él.
  - **Riesgo residual (2026-10-17).** Cualquier proceso Low del usuario escribe en cualquier carpeta Low del usuario, así
    que los procesos Low de ejecuciones distintas alcanzan lo que las otras dejen. El cerrojo y la limpieza lo acotan:
    nunca hay dos verificaciones aisladas a la vez y las sobras de una ejecución que no terminó se borran antes de la
    siguiente. Queda: una sobra que no se pudo borrar (un fichero en uso) hasta la siguiente verificación; las áreas de
    una versión sin cerrojo; y los procesos Low de **otros programas** del usuario, que el cerrojo no detiene. Además, como
    se midió, un proceso Low puede renombrar `LocalLow\AgentStudio` cuando no hay ningún handle abierto dentro: durante un
    paso lo impiden los propios procesos del paso (su carpeta actual está en el área), pero entre ejecuciones un proceso
    Low de otro programa podría sustituir esa cadena, y la raíz existente conserva su etiqueta. Pendiente: retener un
    handle sobre la raíz mientras dura el cerrojo y rechazar una raíz etiquetada Low. Solo una identidad por ejecución
    (Fase 7) separa de verdad dos ejecuciones.
  - **Desktop (resuelto el 2026-10-17).** `OrchestratorDocumentViewModel.ProfileSteps` usa `VerificationStepText.Line`,
    así que cada paso `none` dice «SIN AISLAMIENTO: el código que escriben los agentes se ejecuta con tus permisos en el
    worktree». Además, junto a «Aprobar plan» aparece `VerificationStepText.IsolationWarning` del perfil del proyecto,
    que nombra cada paso `none`, y la petición de aprobación del plan que registra el motor (`ApprovalRequested`,
    `plan.approve`) lleva el mismo aviso. La aprobación del plan no pasa por `IApprovalGate` (la da el lienzo): lo que
    queda en el registro es esa petición. Desde el 2026-10-18 ese aviso es `VerificationStepText.PlanWarning`, calculado
    desde el plan, la plantilla y el perfil: nombra los pasos `none` que el plan ejecuta y también a los agentes con
    `Bash`, a Codex escribiendo y a los ejecutores fuera del confinamiento medido ([ADR-0018](0018-modelo-de-amenaza-fase-3.md), A-04).
  - **Detalles de implementación verificados aquí:** se añadió `JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION` (además del
    *kill-on-close* del spike) para que ningún diálogo de Windows Error Reporting espere en el escritorio; las utilidades
    MSYS de Git (`whoami.exe`) no arrancan bajo Low, de ahí que el ejecutable se resuelva primero en las carpetas del
    sistema; la SACL de la etiqueta no se marca protegida (lo pediría `SeSecurityPrivilege`), así que las carpetas padre se
    etiquetan Medium explícitamente para que un hijo Low no herede Low por encima.
  - **Sin verificar:** el rechazo a aislar desde un proceso elevado (no se dispone de uno elevado para probarlo);
    AppContainer; la caché del usuario como *fallback*.
