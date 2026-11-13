# 21 · Fase 10 · Git remoto: publicación, PR y sincronización: diseño

> **Estado:** propuesta del 2026-11-13, pendiente de tus decisiones (§14). Parte de `main` en `db344c8b` (la Fase 6 integrada).
> Es el documento de diseño de la Fase 10: sustituye a los paquetes de [16 §4.7](16-ampliacion-plataforma-de-proyectos.md#47-fase-10-git-remoto-publicación-pr-y-sincronización)
> y concreta sus decisiones D-24 a D-28, D-31 y D-46 a D-48 sin cambiarles el sentido. Donde este documento las estrecha
> (por ejemplo, sin `git fetch`), lo dice y lo deja como decisión.
>
> **Qué fija:** cómo se cumplen F10-1 a F10-8 y F10-10 de [08](08-plan-y-criterios-de-aceptacion.md#fase-10--git-remoto-publicación-pr-y-sincronización),
> la costura para F10-9 (Azure DevOps, que queda fuera salvo que decidas otra cosa) y tres criterios nuevos de seguridad
> (F10-11 a F10-13, §9).
>
> **Cómo se hizo.** A partir de:
>
> - el código de `src/**` y `tests/**` en `db344c8b` (inventario con rutas en §2);
> - los documentos 04, 08, 16 y 20, y los ADR 0002, 0003, 0012, 0018, 0038, 0039, 0053, 0054 y 0063;
> - la ayuda de la CLI de GitHub instalada en este equipo (`gh --version` y `gh <orden> --help`, sin red ni cuenta): `gh` 2.96.0;
>   y `git` 2.45.2 (`git --version`, `git push -h`).
>
> **Sin red, sin cuenta y sin cuota.** No se ejecutó `gh auth status` ni ninguna orden que consulte la cuenta o la red, ni
> `git push` ni `git fetch`. Ningún CLI de proveedor recibió un prompt. No se leyó ninguna credencial. No se compiló nada y no
> se tocó código: las firmas de §5 son propuestas.

## 0. Resumen

La Fase 10 lleva fuera de tu equipo el trabajo que un juez ya aprobó, y trae de vuelta lo que pasa con él:

- **Publicar:** la rama de la ejecución sube con `git push` y tu gestor de credenciales.
- **Pedir la fusión:** se abre un PR en GitHub con la CLI `gh` de tu cuenta, y el veredicto del juez llega a ese PR como un
  estado y un comentario, nunca como una aprobación tuya.
- **Seguirlo:** un sondeo con tu permiso mira el estado del PR (fusionado, cerrado, checks, revisiones, despliegue) y lo deja
  en el registro, para que el tablero, la cadena y el hito digan «integrado» y «desplegado» con hechos.

Todo ello **sin que Agent Studio toque nunca un token** y sin abrir la red en la capa git interna.

### 0.1 Las decisiones que vertebran la propuesta

1. **Tres piezas nuevas y ninguna vieja cambia.** Un ejecutor remoto de git, un adaptador de GitHub sobre `gh` y un servicio de
   publicación con su buzón. `GitCommandRunner` no gana ni un argumento: sigue con `GIT_ALLOW_PROTOCOL=-` (D-01).
2. **Nada sale solo.** Publicar empieza con tu clic; sondear, con un permiso que das por proyecto y que nace apagado (D-02).
3. **El ejecutor remoto sabe hacer dos cosas:** `push` de un commit a una rama `agentstudio/run-*` y `ls-remote`. **No hay
   `git fetch`:** el estado del remoto se lee por la plataforma, que no escribe en tu repositorio (D-03).
4. **El destino del `push` lo construye el código,** no el remoto configurado: `https://github.com/<propietario>/<repo>.git`, a
   partir de un identificador validado. La configuración del repositorio no decide ni adónde se envía ni cómo se autentica
   (D-04 y D-05).
5. **«Nunca forzar» se garantiza por construcción:** la interfaz no tiene parámetro para forzar, el refspec lo escribe el
   código sin `+`, y una prueba recorre cada orden lanzada (D-07).
6. **GitHub, solo por `gh api`,** con una lista cerrada de puntos de la API. Los datos van en un JSON por la entrada estándar;
   ningún texto libre es un argumento. Es la única forma de ver `ETag`, 304, 429 y `retry-after`, que las órdenes de alto
   nivel (`gh pr view`) no enseñan (D-10).
7. **El token no se lee, no se pasa y no se mira.** `gh` usa su sesión; el entorno de `gh` va por lista blanca, así que
   `GH_TOKEN`, `GH_HOST`, `GH_REPO`, `GH_CONFIG_DIR` y `GH_DEBUG` no llegan. No se usa `gh auth token` ni `--show-token`, y
   tampoco `gh auth status`: la sesión se deduce de la respuesta (D-11 y D-13).
8. **Políticas por clase, en el registro.** Publicar y abrir o actualizar el PR admiten una autorización persistente por
   proyecto; fusionar, cerrar un PR y borrar una rama remota piden aprobación y Windows Hello cada vez (D-15 y D-16).
9. **El veredicto llega al PR como un estado del commit y un comentario etiquetado.** El adaptador no tiene ninguna orden
   para enviar una revisión: no puede aprobar aunque quisiera (D-21).
10. **Se observa estado, no eventos.** Cada sondeo da una instantánea con su huella; dos instantáneas iguales son una. Así
    un evento repetido o desordenado no puede duplicar nada (D-23).
11. **Un buzón por proyecto, con un solo consumidor.** Pone en serie las observaciones y las acciones. Para tocar git o el
    registro toma el proyecto como una actividad más; si una ejecución, un debate o una auditoría lo tienen, espera (D-25).
12. **La unión de commits tras un squash, un rebase o un revert es una proyección del registro,** no un dato nuevo que haya
    que mantener: la observación guarda los commits originales y los resultantes, y la cadena los une (D-28).
13. **«Integrado» por PR exige que lo fusionado sea lo juzgado:** la cabeza del PR al fusionarse tiene que ser el commit que
    vio el juez. Si alguien empujó algo después, se dice, y la tarjeta no pasa a «integrado» (D-29).
14. **Ninguna tabla nueva.** Cuatro clases de entrada en el registro, al final del enum, y dos ficheros locales que se pueden
    borrar (§6).

### 0.2 Hallazgos de paso

Cosas que salieron al leer el código y la ayuda de `gh`, y que condicionan el diseño:

- **Las órdenes de alto nivel de `gh` reciben el texto como argumento.** `gh pr create --title`, `gh pr edit --title`,
  `gh pr merge --subject` y `gh pr close --comment` no tienen variante por fichero; solo el cuerpo la tiene (`--body-file`).
  Con la regla «el texto libre nunca es un argumento», el título no podría enviarse. `gh api --input -` lee el cuerpo
  entero de la petición por la entrada estándar (D-10).
- **`gh api -F clave=@valor` lee un fichero** cuando el valor empieza por `@`, y sus marcadores `{owner}`, `{repo}` y
  `{branch}` se rellenan con el repositorio de la carpeta de trabajo o con `GH_REPO`. Ninguna de las dos cosas se usa:
  serían una lectura de ficheros y una redirección gobernadas por datos (D-10).
- **`gh` tiene telemetría y un aviso de actualización que salen a la red** (`GH_TELEMETRY`, `GH_NO_UPDATE_NOTIFIER`, en
  `gh help environment`). Se apagan en cada lanzamiento (D-11).
- **`gh auth status` tiene `--show-token`,** y con `--json hosts` no está comprobado qué campos salen. No se usa (D-13).
- **`gh pr merge` ofrece `--admin`** («usar privilegios de administrador para fusionar un PR que no cumple los requisitos») y
  `--delete-branch`, que borra también la rama **local**. Ninguna existe en el catálogo (D-20).
- **`gh pr comment --edit-last` edita «el último comentario del usuario actual»:** como todo se publica con tu cuenta,
  podría pisar un comentario tuyo. El comentario del veredicto se localiza por su identificador y su marca, nunca por
  «el último» (D-21).
- **`ICredentialVault` ya tiene implementación** (`DpapiCredentialVault`, de la Fase 6). Esta fase **no la usa:** no guarda
  ningún secreto.
- **`GitHubApiClient` (el doctor de skills) no sirve para esto.** Habla HTTP directamente y necesitaría un token. De él se
  copia solo el patrón: una cola con un único consumidor (`Channel` con `SingleReader`).
- **El código ya esperaba esta fase.** `TraceNodeKind` («Phase 10 appends the pull request»), `BranchKind`,
  `RepositoryActivityKind`, `ParticipationRole.PublishingAccount`, `BoardColumn.Integrated` («with Phase 10, its pull request
  was merged»), `MilestoneClosedPayload.IntegratedCommit` y `TraceSuggestionSource.PullRequestMention` dejan el hueco
  (§2).
- **`ApprovalRiskClassifier` ya trata `push` y `merge` como críticas,** y `PresenceAction.RemoteOrSecretOperation` ya está en
  la lista fija de Hello («con las Fases 10 y 11: fusionar, borrar en el remoto…»). Falta dar de alta los identificadores de
  las acciones nuevas; `CriticalApprovalCoverageTests` falla si no se hace.
- **`pre-push` es el hook con el que git-lfs sube sus objetos.** Sin hooks (como en toda la app), un `push` no sube los
  objetos LFS. Es un límite que hay que decir, no esconder (D-08).
- **Los ADR 0041 y 0042 que docs/16 reservó para esta fase no existen.** Esta fase usa 0064 a 0068 (§11).
- **El índice de ADR de `docs/README.md` acaba en el 0057:** faltan en él los 0061 a 0063. Aquí se añaden solo los nuevos.

### 0.3 Lo que se descarta o se aplaza

| Qué | Por qué | Dónde queda |
|---|---|---|
| `git fetch` y `git pull` desde Agent Studio | Escriben `refs/remotes/*` y `FETCH_HEAD` en tu repositorio; el estado del remoto se lee por la plataforma (D-03) | Decisión 3 |
| `git clone` desde Agent Studio | Ningún criterio F10 lo pide; abrir un repositorio que ya clonaste basta | Aplazado (decisión 12) |
| Publicar por SSH | Exigiría dejar pasar `ssh.exe` y su configuración; con HTTPS basta tu gestor de credenciales | Decisión 4 |
| Publicar en una rama que no sea `agentstudio/run-*` | Empujar a tu rama sería fusionar sin PR | Descartado en esta fase (decisión 13) |
| `--force`, `--force-with-lease`, `--mirror`, `--prune`, borrar con `push` | Barrera fija del proyecto | Descartado |
| Enviar una revisión del PR (aprobar, pedir cambios, comentar como revisión) | F10-5 | Descartado: no existe en el adaptador |
| Fusionar con privilegios de administrador | D-27 de docs/16 | Descartado |
| Fusión automática (`--auto`) y colas de fusión | Fusionar es por uso y con Hello | Descartado |
| Fusionar con autorización persistente | Es la opción (c) de la decisión 3 de docs/16, que no elegiste | Fuera |
| PR desde un fork (cabeza y base en repositorios distintos) | Otro modelo de permisos; no hay con qué probarlo | Aplazado |
| GitHub Enterprise Server y `ghe.com` | El único host admitido es `github.com` | Aplazado |
| Texto de un agente en el título o el cuerpo del PR | Un `@mención` avisa a una persona y un `Closes #n` cierra una incidencia (D-19) | Descartado en esta fase |
| Webhooks | D-47 de docs/16 | Descartado |
| Azure DevOps (F10-9) | Solo con la decisión 2 de docs/16; aquí solo la costura | Decisión 1 |
| GitLab y Bitbucket | D-28 de docs/16 | Aplazado |
| Resolver conflictos del PR con un agente | ADR-0012: un conflicto abre una ejecución de seguimiento que decides tú | Aplazado |
| Subir objetos de Git LFS | Exigiría ejecutar el hook `pre-push` | Límite (D-08) |

---

## 1. Alcance

### 1.1 Cada criterio y dónde va

| Criterio | Qué pide, en corto | Decisiones | Paquete |
|---|---|---|---|
| F10-1 | `IHostingProvider` con capacidades; lo no declarado no aparece | D-09 y D-14 | P0 y WP5 |
| F10-2 | GitHub por `gh`, sin leer su token | D-10 a D-14 y D-17 a D-20 | WP2 |
| F10-3 | Publicar con `git push` y tu gestor de credenciales; configuración plantada rechazada; nunca forzar; commit comprobado | D-03 a D-08 | WP1 |
| F10-4 | Políticas por clase, en el registro; fusionar y destruir con Hello; protecciones a la vista | D-15 a D-17 | P0 y WP3 |
| F10-5 | El veredicto como estado o comentario, nunca aprobación | D-21 y D-22 | WP2 y WP3 |
| F10-6 | Sondeo condicional con buzón por proyecto | D-23 a D-27 | WP3 |
| F10-7 | La cadena une los commits tras squash, rebase y revert | D-28 | WP4 |
| F10-8 | Pestaña PR, «integrado» y «desplegado» | D-29, D-30 y §7 | WP4 y WP5 |
| F10-9 | Azure DevOps | D-09 (solo la costura) | WP-AZ, si lo decides |
| F10-10 | La capa interna sigue sin red ni credenciales | D-01 | WP1 |
| F10-11 a F10-13 | Nuevos, de seguridad (§9) | D-10 a D-13 y D-02 | WP1, WP2 y WP3 |

### 1.2 El reparto con otras fases

| Pieza | Fase 10 | Otra fase |
|---|---|---|
| La capa git interna (`GitCommandRunner`, `GitWorktreeManager`) | No la toca. Una prueba lo fija (F10-10) | Fases 3 y 9 |
| La entrega local a tu rama (`DeliveryService`) | No cambia. Publicar reutiliza su comprobación (`IDeliveryService.CheckAsync`): mismo veredicto, mismo commit | Fase 3 |
| El tablero, la cadena, la participación y el informe del hito | Les añade lo remoto: `Delivered` también por PR fusionado, los pasos del PR y los papeles «cuenta que publica», «revisor» e «integrador» | Fase 9 los creó |
| «Repositorio y cambios» | Añade la sección «PR» y amplía «Ramas» y «Actividad» | Fase 9 hizo las otras cuatro |
| Auto Doctor | Añade una fuente de comprobaciones (`gh` y el remoto), sin red | Fase 6 hizo el panel y la puerta |
| La bóveda de credenciales | No la usa | Fases 6 y 7 |
| El aislamiento fuerte de la red (`AppContainer`, cortafuegos por destino) | No. La red de esta fase la limitan las listas cerradas | Fase 7 |
| La documentación para clientes y el informe del hito | Reciben los PR como un dato más del modelo (`ClientDocumentContracts` ya lo prevé) | Fases 14 y 16 los pintan |
| La prueba E2E de F7-5 | Recorrerá «cadena en remoto» con esta fase hecha | Fase 7 |

---

## 2. Inventario: lo que ya existe

Rutas relativas a la raíz del repositorio, comprobadas en `db344c8b`.

### Git interno (F10-3 y F10-10)

- **El ejecutor:** `src/AgentStudio.Infrastructure/Git/GitCommandRunner.cs`. Cada orden lleva `protocol.*.allow=never`,
  `credential.helper=`, `core.askPass=`, `core.sshCommand=` y `core.hooksPath` apuntando a un **fichero vacío**; y su entorno
  (`BuildEnvironment`) quita toda variable `GIT_*` heredada y fija `GIT_ALLOW_PROTOCOL=-` (`NoTransport`),
  `GIT_NO_LAZY_FETCH=1`, `GIT_TERMINAL_PROMPT=0` y `GIT_OPTIONAL_LOCKS=0`.
- **La inspección de la configuración por ámbito:** `ClassifyRepositoryCallbacksAsync` lanza
  `git config -z --show-scope --show-origin --get-regexp` y `GitCallbacks` clasifica cada clave por su origen. Es el patrón
  que el ejecutor remoto copia (D-05), con otra lista de claves.
- **Los puertos:** `IWorktreeManager` (`src/AgentStudio.Application/Git/GitContracts.cs`): `InspectAsync`,
  `CaptureUserRepositoryStateAsync`, `ResolveCommitAsync`, `IsAncestorAsync`, `ListRunRefsAsync`, `EnsureRunBranchAsync`.
  La rama visible de una ejecución es `refs/heads/agentstudio/run-<id>` (`GitNaming.RunBranchRef`).
- **Los remotos:** `IRepositoryRemoteReader.ListRemotesAsync` (`Application/Git/RepositoryRemoteContracts.cs`), que falla
  cerrado y da cada URL sin credenciales.
- **Rutas y textos no fiables:** `RepositoryPaths` (`src/AgentStudio.Domain/Common/RepositoryPaths.cs`) y las reglas de
  texto que fijó el P0 de la Fase 6 (sin caracteres de control ni de formato; una rama sigue las reglas de
  `git check-ref-format`).

### Lanzar procesos (F10-2 y F10-3)

- **`ProcessSpec` e `IProcessRunner`** (`src/AgentStudio.Application/Execution/ProcessContracts.cs`): carpeta de trabajo,
  entorno (un valor nulo quita la variable), tiempo máximo, entrada estándar y tope de salida.
- **`IExecutableLocator`, `IUntrustedRoots` y `ExecutableSearch`** (`src/AgentStudio.Infrastructure/Processes/`): un
  ejecutable que cae dentro de un proyecto abierto no se usa. `ExecutableSearch.ResolveBareName` solo da `.exe`.
- **La puerta de Auto Doctor:** `IDoctorToolRunner` con su enum cerrado `DoctorTool` y `DoctorToolCatalog`
  (`Application/Diagnostics/DoctorContracts.cs`, `Infrastructure/Diagnostics/`): carpeta neutra, entorno por lista blanca.
  Es el modelo del catálogo cerrado de esta fase.

### Entrega, veredicto y registro (F10-4 a F10-7)

- **`DeliveryService`** (`src/AgentStudio.Orchestration/Integration/DeliveryService.cs`): `CheckAsync` decide, solo con el
  registro verificado y el almacén de veredictos, si una ejecución se puede entregar y con qué commit (el que el juez vio).
  Bloquea con una impugnación abierta o una replanificación pendiente. `ApprovalAction = "deliver-integration"`.
- **Las aprobaciones:** `IApprovalGate`, `ApprovalRequest` (con `Binding`, que ata la aprobación a lo que se va a hacer) y
  `ApprovalDecision` (`Application/Security/SecurityContracts.cs`).
- **Hello:** `PresencePolicy` (`Domain/Security/PresencePolicy.cs`), con `RemoteOrSecretOperation` en «siempre»;
  `ApprovalRiskClassifier.PresenceActions` (`src/AgentStudio.Security/Approvals/`), que nombra cada acción por su
  identificador; y `ReinforcedConfirmation` (`Desktop/Orchestrator/Approvals/ApprovalPromptService.cs`).
- **El registro:** `DecisionKind` (`Domain/Decisions/DecisionLog.cs`; el último valor es `GraphRelationsProposed`) y
  `DecisionPayloadSerializer`. La huella cubre el nombre de la clase: se añade al final, nunca se inserta.
- **Una actividad por proyecto:** `IProjectActivity.TryEnter(projectId, kind)` e `IRunActivity.IsBusy`
  (`Application/Orchestration/`). La memoria ya espera con `IsBusy` antes de escribir en el registro.
- **Trazabilidad:** `TraceRef` y `TraceNodeKind` (`Domain/Tracing/`), `ITraceChainReader` y `TraceChainStage`
  (`Application/Work/TraceContracts.cs`), `TraceChainReader`, `VerdictReach` (la regla del veredicto en vigor, compartida
  por el tablero, la cadena y la participación), `BoardProjector` y `ParticipationProjector` (`Orchestration/Work/`).
- **El tablero:** `BoardRules` (`Domain/Work/Milestones/BoardRules.cs`): `RequirementBoardFacts.Delivered` decide entre
  «hecho» e «integrado». Invariante: ninguna tarjeta llega a «hecho» ni a «integrado» sin veredicto aprobado y válido.
- **El hito:** `MilestoneClosedPayload.IntegratedCommit`, `MilestoneReopenReason` (`Revert`, `CheckFailed`, `Challenge`,
  `ByPerson`) y `MilestoneReport` (`src/AgentStudio.Debate/Audit/MilestoneReport.cs`), que ya separa implementado,
  revisado e integrado.

### Pantallas, textos y ajustes (F10-8)

- **«Repositorio y cambios»:** `src/AgentStudio.Desktop/Repository/` (`RepositoryViewModel`, con cuatro secciones:
  Resumen, Ramas, Commits y Actividad; `RepositorySection`) y `Views/Repository/RepositoryView.xaml`. Sus contratos,
  en `Application/Git/RepositoryViewContracts.cs`.
- **«Trabajo»:** `src/AgentStudio.Desktop/Work/` (`WorkSection`, `BoardSection`, `ChainSection`, `MilestonesSection`,
  `ParticipationSection`).
- **Textos** (ADR-0054): `UserText` y un par `<Área>Strings.resx` / `<Área>Strings.en.resx` por área en
  `Desktop/Resources/` (`RepositoryStrings`, `WorkStrings`, `DoctorStrings`…).
- **Ayuda:** `Desktop/Help/{es-ES,en}/<id>.md` y `HelpScreens.cs` (existe `repository`).
- **Inventario de la interfaz:** `tests/AgentStudio.Tests/Desktop/UiInventory/` (`UiScreensRepository.cs`,
  `UiScreensWork.cs`, `UiScreensDoctor.cs`).
- **Ajustes:** `AppSettings` (`Desktop/Services/AppSettings.cs`); un ajuste nuevo va también en `WithPreferencesOf`.
  `SkillsDoctorNetworkEnabled` es el precedente de un permiso de red apagado por defecto.

### En este equipo

- `gh` 2.96.0 (2026-07-02) y `git` 2.45.2. El remoto de este repositorio es GitHub por HTTPS.
- `gh help exit-codes`: 0 correcto, 1 fallo, 2 cancelado y **4 «la orden necesita autenticación»**.
- `gh api`: `-X` (método), `-H` (cabecera), `-i` (incluye la línea de estado y las cabeceras de la respuesta), `--input`
  (cuerpo desde fichero o entrada estándar) y `--hostname`.

---

## 3. Modelo de amenazas de esta fase

[ADR-0018](adr/0018-modelo-de-amenaza-fase-3.md) no cambia: el contenido del repositorio y la salida de los agentes son no
fiables. Esta fase añade un adversario (el remoto y lo que otros escriben en él) y una frontera nueva (la red, con tu
cuenta). Lo que se defiende: **tu sesión de GitHub y la de tu gestor de credenciales, tus ramas, y la verdad del registro.**

| N.º | Adversario o vía | Qué intentaría | Cómo se cierra |
|---|---|---|---|
| A-1 | **Repositorio hostil:** su configuración local o de worktree | Elegir cómo se autentica o adónde se envía: `credential.helper` (un programa que recibe tu token), `url.*.insteadOf` y `pushInsteadOf` (otra dirección), `remote.*.pushurl`, `core.sshCommand`, `core.gitProxy`, `http.proxy`, `http.extraHeader`, `http.sslVerify`, `core.fsmonitor`, `core.hooksPath` | El destino lo construye el código (D-04); se inspecciona la configuración por ámbito y por origen y se rechaza la lista de D-05; las claves que mandan se fijan en la línea de órdenes (D-06) |
| A-2 | **Hooks:** `pre-push` (del repositorio o plantado por git-lfs) | Ejecutar código con tu sesión durante el `push`, con la dirección del remoto en sus argumentos | `core.hooksPath` al fichero vacío de siempre y `--no-verify` (D-06). Coste: sin objetos LFS (D-08) |
| A-3 | **Variables de entorno** heredadas: `GIT_*` (`GIT_SSH_COMMAND`, `GIT_ASKPASS`, `GIT_CONFIG_*`, `GIT_TRACE*`, `GIT_CURL_VERBOSE`), `GH_*` (`GH_TOKEN`, `GH_HOST`, `GH_REPO`, `GH_CONFIG_DIR`, `GH_DEBUG`), `GITHUB_TOKEN`, `GCM_*`, proxies | Redirigir la orden, cambiar de cuenta, o volcar las cabeceras HTTP (con el token) en la salida | Entorno por lista blanca en los dos ejecutores (D-06 y D-11): lo que no está en la lista no pasa, se llame como se llame |
| A-4 | **Texto no fiable como argumento:** nombres de rama, títulos, comentarios, nombres de usuario, que empiezan por `-`, por `@`, o llevan `..`, espacios o saltos de línea | Inyectar una opción (`--upload-pack=…`, `--admin`), leer un fichero (`-F x=@…`) o cambiar el punto de la API | Ningún texto libre es un argumento. Los identificadores se validan carácter a carácter antes de construir nada (D-04 y D-10); el cuerpo va por la entrada estándar; el catálogo es cerrado |
| A-5 | **Remoto hostil o comprometido:** redirecciones, respuestas enormes o con forma inesperada, nombres con secuencias de terminal | Llevar tus credenciales a otro host; agotar memoria; colar texto en la interfaz o en el registro | `http.followRedirects=false` y solo `https` (D-06); `gh` fijado a `github.com` (D-10); topes de tamaño y esquema estricto con campos por lista (D-12); ningún texto del remoto entra en el registro (D-23) |
| A-6 | **La salida de `gh`** (y su error estándar) | Que un dato del remoto pase por hecho, o que un volcado con el token acabe en un log | Se interpreta por estructura (la línea de estado HTTP y el JSON), nunca por frases; el error estándar no se guarda ni se muestra, solo su clasificación (D-12) |
| A-7 | **Otra persona con acceso al remoto** (o tú desde la web) | Empujar commits a la rama del PR después del veredicto y que se fusionen «con el visto bueno del juez» | La fusión desde Agent Studio va atada al commit juzgado (`sha`); una fusión hecha fuera con otra cabeza no cuenta como «integrado» y se dice (D-20 y D-29) |
| A-8 | **Confusión de autoría** | Que el veredicto de una IA parezca tu aprobación como revisor | Nunca una revisión; el estado y el comentario dicen qué son y quién juzgó (D-21) |
| A-9 | **Eventos repetidos, desordenados o inventados** | Duplicar entradas, o «des-fusionar» un PR | Se observa estado; huella por instantánea; una instantánea más vieja que la última registrada se descarta (D-23) |
| A-10 | **Una carrera con una ejecución** | Mover la cabeza del registro entre que se enseña un veredicto y se usa; o que la comprobación del repositorio vea un cambio «externo» | El buzón toma el proyecto con `IProjectActivity.TryEnter` antes de tocar git o el registro, y espera si está ocupado (D-25) |
| A-11 | **Un ejecutable plantado** (`gh.cmd`, `git.exe` en una carpeta del proyecto que está en el PATH) | Ejecutarse con tu sesión | Resolución por ruta absoluta fuera de las raíces no fiables (`ExecutableSearch`); solo `.exe` |

**Fuera del modelo, como ya lo estaban** (residuales de ADR-0018): un proceso de tu usuario que cambie tu configuración
global de git, tu `gh` o tu gestor de credenciales; y un PATH controlado por otro programa tuyo. Esta fase **confía en tu
configuración global y de sistema** de git: es donde vive tu gestor de credenciales.

---

## 4. Decisiones

Cada decisión es mía salvo que diga «decisión N», que te la deja a ti en §14. Las «D-nn de docs/16» se citan así, con su
documento; las demás son de este.

### 4.1 La forma general

**D-01 · Tres piezas nuevas; la capa interna no cambia.**

| Pieza | Capa | Qué hace | Qué no puede hacer |
|---|---|---|---|
| Ejecutor remoto de git | `Infrastructure/Git/Remote/**` | `push` y `ls-remote`, con tu gestor de credenciales | Cualquier otra orden; elegir destino; forzar |
| Adaptador de GitHub | `Infrastructure/Hosting/**` | Hablar con GitHub por `gh api`, con una lista cerrada | Tocar git o el repositorio; enviar una revisión; ver el token |
| Publicación y sincronización | `Orchestration/Publication/**` | La compuerta de políticas, el flujo de publicar, el buzón y el reconciliador | Lanzar procesos por su cuenta: solo usa los dos puertos de arriba |

- **`GitCommandRunner` no se toca.** El ejecutor remoto es otra clase, con su propia configuración base y su propio entorno.
  No comparten ni la lista de argumentos ni el constructor del entorno: así una opción que se abra en uno no se abre en el
  otro por descuido.
- **F10-10 se prueba por estructura:** una prueba recorre todas las órdenes que lanza la capa interna en su batería y exige
  `GIT_ALLOW_PROTOCOL=-` y `credential.helper=` en cada una; otra, de arquitectura, exige que ningún fichero de
  `Infrastructure/Git/` fuera de `Remote/` nombre `push`, `fetch`, `ls-remote` ni `clone` como subcomando.
- **Dependencias** (ADR-0002): Domain no depende de nada; los puertos, en Application; los dos adaptadores, en
  Infrastructure; la orquestación, en Orchestration; Desktop compone. Ningún proyecto nuevo.
- **Descartado:** dar un «modo remoto» a `GitCommandRunner`. Metería la red y tus credenciales en el componente que más
  costó cerrar (lo que ya decía docs/16 D-24).

**D-02 · Nada sale solo: publicar empieza con tu clic y sondear con tu permiso** (decisión 2).

- **Publicar, abrir o actualizar un PR, fusionar, cerrar y borrar** empiezan siempre con una acción tuya en la pantalla.
  Una autorización persistente (D-16) ahorra la aprobación y el Hello de cada vez, no el clic: Agent Studio no publica nada
  porque una ejecución haya terminado.
- **El sondeo** necesita el permiso «Sincronizar con GitHub», por proyecto y apagado por defecto. Sin él no se lanza ni un
  `gh` con red: el estado se actualiza solo cuando pulsas «Sincronizar ahora».
- **Auto Doctor no usa la red** en esta fase (D-31).
- **Se dice qué sale:** el propietario y el nombre del repositorio, los números de tus PR y los commits. Nada más del
  proyecto: ni rutas, ni el registro, ni texto de agentes.
- **Por qué:** es la misma regla que el doctor de skills (docs/20 D-13): la red, solo donde el diseño la permite y con tu
  permiso.

### 4.2 El ejecutor remoto de git (F10-3 y F10-10)

**D-03 · Dos operaciones y ninguna más: `push` y `ls-remote`. Sin `fetch`** (decisión 3).

- `IRemoteGitRunner` (§5.2) tiene tres miembros: inspeccionar la configuración, leer una rama del remoto (`ls-remote`) y
  publicar la rama de una ejecución (`push`). No hay ninguna sobrecarga con argumentos libres, como en `IDoctorToolRunner`.
- **Por qué sin `fetch`:** `git fetch` escribe `refs/remotes/*` y `FETCH_HEAD` en tu repositorio. La comprobación del
  repositorio del usuario, que rodea cada paso de una ejecución, lo vería como un cambio externo (docs/16 D-46). Y no hace
  falta: lo que la fase necesita del remoto (el estado del PR, el commit de la fusión, los checks) lo da la plataforma sin
  tocar el repositorio.
- **`ls-remote` no escribe nada** en el repositorio, y sirve para F10-3 («se comprueba el commit en el remoto») también
  contra un remoto local en las pruebas.
- **`push` a una dirección** (no a un remoto con nombre) no mueve ninguna rama de seguimiento: tu repositorio queda igual.
  WP1 lo fija con una prueba que compara `CaptureUserRepositoryStateAsync` antes y después (§13).
- **Lo que se pierde:** en «Ramas» no se puede calcular «delante y detrás» de una rama remota cuyos commits no estén ya en
  tu equipo. Se dice («para compararla, tráela con tu cliente git») en vez de traerla.
- **Descartado:** `fetch` a un espacio de refs propio (`refs/agentstudio/remote/*`). Escribe objetos y refs igualmente, y
  abriría una tercera operación con red para un dato que la plataforma ya da.

**D-04 · El destino lo construye el código a partir de un identificador validado.**

- **De dónde sale:** la URL del remoto que elijas (por defecto `origin`), leída por `IRepositoryRemoteReader`. Es
  configuración del repositorio: **no fiable**. De ella solo se extrae un identificador: host, propietario y nombre.
- **Se valida antes de construir nada** (`RemoteRepository`, en Domain; lo que no pasa se rechaza, no se corrige):
  - host: exactamente `github.com` (sin puerto, sin usuario); se reconocen las formas `https://github.com/o/r(.git)` y
    `git@github.com:o/r(.git)` solo para **identificar** el repositorio;
  - propietario: `[A-Za-z0-9]` y hasta 38 caracteres más de `[A-Za-z0-9-]`;
  - nombre: `[A-Za-z0-9._-]{1,100}`, nunca `.` ni `..`, ni con `..` dentro;
  - las mismas reglas que `GitHubSkillOrigin` (docs/20 D-14), comprobadas carácter a carácter.
- **La dirección del `push` es siempre** `https://github.com/<propietario>/<nombre>.git`, escrita por el código. Nunca se usa
  el nombre del remoto ni su URL tal cual: así `remote.<nombre>.pushurl`, `remote.<nombre>.push`,
  `remote.<nombre>.receivepack`, `remote.<nombre>.proxy` y `remote.<nombre>.vcs` no intervienen.
- **Solo HTTPS** (decisión 4). Si tu remoto es SSH, Agent Studio lo identifica igual y publica por HTTPS con tu gestor de
  credenciales; si no hay credenciales para HTTPS, lo dice.
- **El refspec lo escribe el código:** `<commit de 40 hexadecimales>:refs/heads/agentstudio/run-<id de la ejecución>`. El
  commit y el id salen del registro, no de un texto. No lleva `+` delante.
- **Solo ramas `agentstudio/run-*`** (decisión 13): empujar a otra rama remota sería fusionar sin PR.

**D-05 · La configuración se inspecciona por ámbito y por origen, y la del repositorio no decide nada.**

Antes de cada `push` y de cada `ls-remote` se lanza la misma consulta que usa la capa interna
(`git config -z --show-scope --show-origin --get-regexp`, que no abre ningún transporte) y se rechaza la operación si
alguna clave de la lista:

- tiene ámbito `local` o `worktree`; **o**
- viene de un fichero que cae dentro del repositorio, de su directorio común o de la raíz de worktrees (un `include` o un
  `includeIf` de tu configuración global que apunte dentro del proyecto).

| Familia | Claves | Por qué |
|---|---|---|
| Credenciales | `credential.*` (también `credential.<url>.*`) | Un ayudante del repositorio recibiría tu token |
| Direcciones | `url.*.insteadOf`, `url.*.pushInsteadOf`, `remote.*.url` distinta de la identificada, `remote.*.pushurl`, `remote.*.push`, `remote.*.receivepack`, `remote.*.proxy`, `remote.*.vcs`, `remote.pushDefault`, `branch.*.pushRemote` | Otra dirección u otro programa |
| Transporte | `http.*` (también `http.<url>.*`: `proxy`, `extraHeader`, `sslVerify`, `sslCAInfo`, `cookieFile`, `followRedirects`), `core.sshCommand`, `core.gitProxy`, `ssh.variant`, `protocol.*`, `transfer.credentialsInUrl` | Un intermediario, una cabecera o un programa |
| Programas | `core.fsmonitor`, `core.hooksPath`, `core.askPass`, `core.alternateRefsCommand`, `gpg.*`, `trace2.*` | Código que corre durante la orden |
| Comportamiento del `push` | `push.*`, `submodule.recurse`, `receive.*`, `uploadpack.*` | Empujar más de lo pedido (etiquetas, submódulos), firmar, opciones al servidor |

- **Fallo cerrado:** si la consulta falla, o su salida pasa del tope, no se publica.
- **La pantalla dice qué clave y de qué fichero,** nunca su valor (podría ser un secreto), y cómo quitarla.
- **Tu configuración global y de sistema sí cuenta,** con una excepción: si un `url.*.insteadOf` o `pushInsteadOf` **tuyo**
  reescribe la dirección de D-04, se rechaza con su motivo («tu configuración de git cambia la dirección del remoto: Agent
  Studio solo publica en la dirección que te enseña»). Fallo cerrado: lo que se aprueba es lo que se usa.
- **`remote.<nombre>.url` es por fuerza del repositorio.** No se rechaza por existir: se lee, se valida (D-04), se te enseña
  en la tarjeta de publicación y la autorización queda atada a ese identificador (D-16).

**D-06 · La orden exacta y su entorno.**

```text
git  -c core.hooksPath=<fichero vacío>  -c core.fsmonitor=false  -c gc.auto=0  -c maintenance.auto=false
     -c core.sshCommand=  -c core.gitProxy=  -c core.askPass=
     -c protocol.allow=never  -c protocol.https.allow=always
     -c protocol.file.allow=never  -c protocol.git.allow=never  -c protocol.ssh.allow=never
     -c protocol.http.allow=never  -c protocol.ftp.allow=never  -c protocol.ftps.allow=never  -c protocol.ext.allow=never
     -c http.followRedirects=false  -c submodule.recurse=false
     -c push.default=nothing  -c push.followTags=false  -c push.recurseSubmodules=no  -c push.gpgSign=false
     -c push.autoSetupRemote=false
     push --porcelain --no-verify --no-follow-tags --recurse-submodules=no --no-signed
     https://github.com/<propietario>/<nombre>.git  <commit>:refs/heads/agentstudio/run-<id>
```

- **`credential.helper` no se vacía:** es justo lo que hace falta. Lo que se impide es que lo defina el repositorio (D-05).
- **`ls-remote`:** las mismas `-c`, y `ls-remote --refs https://… refs/heads/agentstudio/run-<id>`.
- **Sin entrada estándar ni texto libre:** la orden no lleva mensaje, título ni nada que escriba una persona o un agente.
- **Carpeta de trabajo:** el repositorio (git no puede publicar de otra forma), como la capa interna. El ejecutable, el
  `git.exe` del sistema, resuelto fuera de los proyectos (`ExecutableSearch`).
- **Entorno por lista blanca** (como docs/20 D-02), no por lista negra:
  - de Windows: las mismas que la puerta de Auto Doctor (`SystemRoot`, `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`,
    `ProgramData`, `ProgramFiles`…), más `HOME`, `HOMEDRIVE` y `HOMEPATH`, que git usa para tu configuración global;
  - `PATH`, ya filtrado;
  - `TEMP` y `TMP`, en una carpeta propia (`%LOCALAPPDATA%\AgentStudio\hosting\tmp`);
  - y las que pone el ejecutor: `GIT_TERMINAL_PROMPT=0`, `GIT_OPTIONAL_LOCKS=0`, `GIT_PAGER=cat`, `GIT_NO_LAZY_FETCH=1`,
    `GIT_ALLOW_PROTOCOL=https` y `GCM_INTERACTIVE=never` (decisión 5). Esta última solo la entiende Git Credential
    Manager. Si tu ayudante global es otro (el de `gh`, que deja `gh auth setup-git`, pasó la inspección de la capa
    interna en este equipo según ADR-0012), no hay ventana que evitar: `git` lo lanza con este mismo entorno, sin
    `GH_TOKEN`, y usa la sesión guardada de `gh`.
  - Todo lo demás no pasa: ni `GIT_SSH_COMMAND`, ni `GIT_ASKPASS`, ni `GIT_CONFIG_*`, ni `GIT_TRACE*`, ni
    `GIT_CURL_VERBOSE`, ni `GCM_TRACE`, ni un `HTTPS_PROXY` heredado. Un proxy que necesites va en tu configuración
    global de git (`http.proxy`), que sí cuenta.
- **Topes:** 120 segundos para `push`, 20 para `ls-remote`; 64 000 caracteres de salida.
- **La salida se lee por estructura:** `--porcelain` da una línea por ref con un indicador (`=` al día, `*` nueva, ` `
  avance rápido, `!` rechazada). El error estándar no se muestra ni se guarda: se clasifica en un código
  (`credentials-needed`, `rejected-not-fast-forward`, `remote-unreachable`, `redirected`, `protocol-refused`,
  `git-failed`) y se pasa por `SecretRedactor` antes de cualquier registro de diagnóstico.
- **En las pruebas,** el remoto es una carpeta bare o `file://`: el ejecutor recibe sus opciones por constructor
  (`RemoteGitOptions.AllowedTransport`), y una prueba de la composición exige que la de la app sea `https` y nada más.

**D-07 · «Nunca forzar», con cinco cierres.**

1. `RemotePush` no tiene ningún campo para forzar, borrar ni elegir refspec.
2. El refspec lo escribe una sola función, sin `+`, con origen un commit y destino `refs/heads/agentstudio/run-…`.
3. La lista de argumentos es una constante: no hay concatenación con datos salvo la dirección y el refspec, ya validados.
4. Una prueba recorre **todos** los `ProcessSpec` que lanza el ejecutor en su batería y falla si alguno contiene `--force`,
   `-f`, `--force-with-lease`, `--force-if-includes`, `--mirror`, `--prune`, `--delete`, `-d`, `--all`, `--tags`, un
   refspec que empiece por `+` o por `:`, o `-o`/`--push-option`.
5. Si el remoto ya tiene la rama en otro commit que no es antepasado del nuevo, git rechaza el `push` (no es avance rápido)
   y eso es un resultado: «la rama del remoto cambió; no se sobrescribe». No hay reintento con fuerza.

**D-08 · Qué se publica, y qué se comprueba antes y después.**

- **El commit:** el que da `IDeliveryService.CheckAsync`, que es el que el juez vio y aprobó. Si no está lista para
  entregar (sin veredicto válido, impugnada, con una replanificación pendiente), tampoco se publica, con el mismo motivo.
- **Antes, se cuenta lo que va a subir.** La rama de la ejecución nace de tu rama local: si tenías commits sin publicar,
  subirían con ella. La tarjeta lo dice: «Se publican 7 commits: 5 de Agent Studio y 2 tuyos que aún no están en
  GitHub», con la lista. Se calcula con la cabeza de la rama base en el remoto (`ls-remote`) y la historia local (capa
  interna, solo lectura). Si esa cabeza no está en tu equipo, se dice que no se puede saber, y decides tú.
- **Git LFS:** si el repositorio usa LFS (`filter.lfs.*` en la configuración efectiva y algún fichero de lo que se publica
  con ese filtro), **no se publica** (decisión 16) y se explica: sin el hook `pre-push` los objetos no subirían y el PR quedaría con
  punteros rotos. El camino es publicar esa rama con tu cliente git. Ante la duda, se rechaza.
- **Después, se comprueba:** `ls-remote` de la rama tiene que dar exactamente el commit publicado. Si no, el resultado es
  «no confirmado», no «publicado».
- **Idempotente:** si el remoto ya tiene la rama en ese commit, no se empuja y cuenta como hecho.
- **Deja rastro:** `HostingOperationPerformed` (§5.3), con el resultado real.

### 4.3 GitHub por `gh` (F10-1 y F10-2)

**D-09 · `IHostingProvider`: una interfaz común con capacidades declaradas** (decisión 1 para Azure DevOps).

- **Las capacidades** (`HostingCapabilities`, §5.1) dicen qué sabe hacer un proveedor con una versión concreta de su CLI:
  leer el repositorio, leer las reglas de una rama, crear y actualizar un PR, comentar, pedir revisores, leer revisiones,
  leer checks y ejecuciones de Actions, fusionar (y con qué modos), cerrar, borrar una rama, publicar un estado de commit,
  leer despliegues y hacer peticiones condicionales.
- **Tres niveles que se cruzan,** y la pantalla usa el resultado:
  1. lo que declara el adaptador para esa versión de la CLI;
  2. lo que permite el repositorio (los modos de fusión activados, tu permiso sobre él);
  3. lo que permite el estado (un PR ya fusionado no se fusiona).
- **Lo no declarado no aparece:** el ViewModel construye sus órdenes a partir de las capacidades. No hay botones
  desactivados «porque este proveedor no lo soporta»: no hay botón (F10-1).
- **La interfaz no tiene ningún miembro para enviar una revisión ni para aprobar.** Es la garantía de F10-5, por el tipo.
- **La costura para Azure DevOps,** sin construirlo:
  - `HostingProviderKind` ya nombra `AzureDevOps`, sin adaptador;
  - las revisiones se leen a un modelo común (`ReviewVerdict`: aprobada, aprobada con sugerencias, cambios pedidos, en
    espera, rechazada, comentada), que cubre los votos numéricos de Azure DevOps (10, 5, 0, -5 y -10);
  - las protecciones de GitHub y las políticas de rama de Azure DevOps se leen a una lista común de reglas (`BranchRule`);
  - los checks y los builds, a `CheckRun`;
  - el canal del veredicto es una capacidad (`CommitStatus`, `Comment`): en Azure DevOps sería el estado del PR, nunca un
    voto.
  - Lo que **no** se resuelve aquí y se dice: `RemoteRepository` hoy tiene host, propietario y nombre; Azure DevOps añade
    organización y proyecto. El tipo es una jerarquía cerrada que admite otra forma, pero su validación se escribe cuando
    entre.
- **Descartado:** una interfaz por operación (`IPullRequestCreator`, `IMerger`…). Las capacidades ya dicen qué hay, y un
  solo puerto se sustituye entero en las pruebas.

**D-10 · Solo `gh api`, con un catálogo cerrado; los datos, por la entrada estándar.**

- **Una sola forma de orden:**

  ```text
  gh api --hostname github.com -X <MÉTODO> -i  -H "Accept: application/vnd.github+json"
         -H "X-GitHub-Api-Version: <fecha fija>"  [-H "If-None-Match: <etag>"]  [--input -]  <ruta>
  ```

- **El catálogo** (`GitHubEndpoint`, un enum cerrado en el adaptador) fija el método y la plantilla de la ruta de cada
  punto. Quien llama pide una entrada por su nombre con valores tipados; no puede pasar ni una ruta ni un argumento.

  | Entrada | Método y ruta | Para qué |
  |---|---|---|
  | `Viewer` | `GET /user` | La cuenta que publica (solo `login`) |
  | `Repository` | `GET /repos/{o}/{r}` | Rama por defecto, modos de fusión, tu permiso, archivado |
  | `BranchRules` | `GET /repos/{o}/{r}/rules/branches/{rama}` y `GET /repos/{o}/{r}/branches/{rama}` | Protecciones (D-17) |
  | `FindPullRequest` | `GET /repos/{o}/{r}/pulls?head={o}:{rama}&state=all&per_page=5` | Idempotencia al abrir |
  | `OpenPullRequest` | `POST /repos/{o}/{r}/pulls` | Abrir |
  | `UpdatePullRequest` | `PATCH /repos/{o}/{r}/pulls/{n}` (solo `title` y `body`) | Actualizar |
  | `ClosePullRequest` | `PATCH /repos/{o}/{r}/pulls/{n}` (solo `state: closed`) | Cerrar (destructiva) |
  | `PullRequest` | `GET /repos/{o}/{r}/pulls/{n}` | El estado |
  | `PullRequestCommits` | `GET /repos/{o}/{r}/pulls/{n}/commits?per_page=100` | Los commits originales (D-28) |
  | `PullRequestEvents` | `GET /repos/{o}/{r}/issues/{n}/events?per_page=100` | Contar los force-push y el borrado de la rama (D-27) |
  | `Reviews` | `GET /repos/{o}/{r}/pulls/{n}/reviews?per_page=100` | Leer revisiones. **Solo `GET`** |
  | `RequestReviewers` | `POST /repos/{o}/{r}/pulls/{n}/requested_reviewers` | Pedir revisores |
  | `CheckRuns` y `CombinedStatus` | `GET /repos/{o}/{r}/commits/{sha}/check-runs` y `…/status` | Checks |
  | `WorkflowRuns` | `GET /repos/{o}/{r}/actions/runs?head_sha={sha}&per_page=50` | Actions |
  | `Commit` | `GET /repos/{o}/{r}/commits/{sha}` | Los padres del commit de la fusión (D-27) |
  | `CommitsSince` | `GET /repos/{o}/{r}/commits?sha={rama}&since={fecha}&per_page=50` | Buscar un revert (D-27) |
  | `PostCommitStatus` | `POST /repos/{o}/{r}/statuses/{sha}` | El estado del veredicto (D-21) |
  | `PostComment`, `EditComment`, `Comments` | `POST /repos/{o}/{r}/issues/{n}/comments`, `PATCH /repos/{o}/{r}/issues/comments/{id}`, `GET …/issues/{n}/comments?per_page=100` | El comentario del veredicto |
  | `Merge` | `PUT /repos/{o}/{r}/pulls/{n}/merge` (`merge_method` y `sha`) | Fusionar |
  | `DeleteBranch` | `DELETE /repos/{o}/{r}/git/refs/heads/{rama}` | Borrar la rama remota (destructiva) |
  | `Deployments` y `DeploymentStatuses` | `GET /repos/{o}/{r}/deployments?sha={sha}&per_page=20` y `…/deployments/{id}/statuses?per_page=10` | «Desplegado» |

- **Cómo se construye una ruta:** un solo constructor (`GitHubApiPath`). Cada segmento sale de un tipo validado
  (`RemoteRepository`, un número de PR positivo, un commit de 40 hexadecimales, una rama de `agentstudio/run-…` o una rama
  base validada), se escapa con `Uri.EscapeDataString` **segmento a segmento** y la ruta final se comprueba contra la
  plantilla. Una rama lleva barras (`agentstudio/run-…`): se parte por ellas y cada trozo se escapa aparte, de modo que la
  barra queda literal y nunca `%2F` (sin verificar que los tres puntos con rama en la ruta la quieran así: §13). Nunca
  empieza por `-`: siempre por `/repos/` o es `/user`.
- **Nunca** `-f`, `-F`, `--field` ni `--raw-field` (un valor que empiece por `@` leería un fichero); nunca los marcadores
  `{owner}`, `{repo}` ni `{branch}` de `gh`; nunca `--paginate`, `--cache`, `--jq`, `--template` ni `--verbose`.
- **Los datos de una escritura van en un JSON por la entrada estándar** (`--input -`), escrito con `Utf8JsonWriter` desde
  tipos: el título, el cuerpo, la rama base, los revisores. Ningún texto libre llega a ser un argumento (los shims `.cmd` y
  la regla del proyecto). `gh.exe` es un ejecutable, no un shim; la regla se cumple igual.
- **Las cabeceras** son constantes del adaptador, salvo `If-None-Match`. Su valor es un `ETag` que dio el remoto: **es el
  único dato del remoto que llega a ser un argumento,** y la segunda excepción a «ningún dato no fiable en los
  argumentos» junto a la ruta validada. Solo se acepta con la forma exacta de un `ETag` (`W/` opcional y, entre comillas
  dobles, de 1 a 128 caracteres ASCII visibles sin comillas ni espacios); lo que no la tiene se descarta y la petición va
  sin condición.
- **Por qué no las órdenes de alto nivel** (`gh pr create`, `gh pr view --json`, `gh pr merge`): reciben el título como
  argumento; no enseñan `ETag`, 304, 429 ni `retry-after`; y trabajan sobre «el repositorio de la carpeta actual»,
  lanzando `git` dentro de él, que es justo la configuración no fiable. Con `gh api` y la ruta completa, `gh` no necesita
  ningún repositorio (sin verificar que no lance `git` en ningún caso: §13).
- **Descartado:** llamar a la API con un `HttpClient` propio. Haría falta el token.

**D-11 · Cómo se lanza `gh`: ejecutable, carpeta y entorno.**

- **Ejecutable:** `gh.exe`, resuelto por `ExecutableSearch` fuera de los proyectos abiertos; solo `.exe`.
- **Carpeta de trabajo neutra:** `%LOCALAPPDATA%\AgentStudio\hosting\cwd`, vacía y creada por la app. `gh` nunca arranca
  dentro de un repositorio.
- **Entorno por lista blanca:**
  - de Windows, las de la puerta de Auto Doctor (entre ellas `APPDATA`, donde `gh` tiene su configuración, y
    `LOCALAPPDATA`);
  - `PATH`, ya filtrado; `TEMP` y `TMP` propios;
  - y las que pone el adaptador: `GH_PROMPT_DISABLED=1`, `GH_NO_UPDATE_NOTIFIER=1`,
    `GH_NO_EXTENSION_UPDATE_NOTIFIER=1`, `GH_TELEMETRY=false`, `DO_NOT_TRACK=1`, `NO_COLOR=1` y `GH_SPINNER_DISABLED=1`.
  - **No pasan:** `GH_TOKEN`, `GITHUB_TOKEN`, `GH_ENTERPRISE_TOKEN`, `GITHUB_ENTERPRISE_TOKEN` (cambiarían la cuenta, y
    Agent Studio estaría transportando un token), `GH_HOST`, `GH_REPO`, `GH_CONFIG_DIR` (redirigen), `GH_DEBUG` y `DEBUG`
    (vuelcan el tráfico HTTP), `GH_PATH`, `GH_EDITOR`, `GH_BROWSER`, `GH_PAGER`, `GH_FORCE_TTY`.
- **Lo que se nota:** si inicias sesión en `gh` solo con la variable `GH_TOKEN`, o tienes su configuración en otra carpeta
  con `GH_CONFIG_DIR`, Agent Studio verá «sin sesión». La guía dice `gh auth login`.
- **Agent Studio no lee nunca** la configuración de `gh` (`%APPDATA%\GitHub CLI`), el Administrador de credenciales de
  Windows ni el almacén de Git Credential Manager. Tampoco ejecuta `gh auth token`, `gh auth status --show-token` ni
  ninguna orden `gh auth`.
- **Topes:** 20 segundos y 1 MB por respuesta; de una en una (D-25).

**D-12 · La salida de `gh` es un dato no fiable.**

- **Se lee por estructura.** Con `-i`, la salida trae la línea de estado HTTP, las cabeceras, una línea en blanco y el
  cuerpo. El lector exige esa forma; lo que no la tiene es `InvalidAnswer`.
- **De las cabeceras, solo cinco:** `etag`, `retry-after`, `x-ratelimit-remaining`, `x-ratelimit-reset` y
  `x-poll-interval`. `Link` no se sigue: no hay paginación; una lista que llega llena se marca «truncada» y se dice.
- **Del cuerpo, solo los campos de una lista por punto,** con su tipo y sus topes (`JsonDocument`, profundidad 16):
  números, booleanos, fechas, commits de 40 hexadecimales, y enumerados de un vocabulario cerrado (`state`,
  `mergeable_state`, `conclusion`…); un valor fuera del vocabulario es «desconocido», no un error ni un acierto.
- **El texto del remoto** (título de un PR, nombre de un check, de un entorno, de una persona) **nunca entra en el
  registro** ni en un mensaje del sistema. Lo que la pantalla necesita enseñar vive en la caché local (§6), acotado a 200
  caracteres, sin caracteres de control ni de formato, y se pinta como dato.
- **Los nombres de cuenta** (`login`) sí son identificadores: se validan con la regla del propietario (D-04) y, si no pasan,
  salen «no consta».
- **Ninguna dirección de la respuesta se abre ni se sigue.** «Abrir en el navegador» usa una dirección que construye el
  código (`https://github.com/<o>/<r>/pull/<n>`), no `html_url`.
- **El código de salida** solo se usa para un caso: el 4 (`gh` dice que hace falta autenticarse). Lo demás lo decide la
  línea de estado.
- **El error estándar** no se guarda ni se muestra: puede traer lo que el servidor quiera. Se clasifica en un código.

**D-13 · La sesión no se sondea: se deduce** (decisión 6).

- No se usa `gh auth status`: consulta la red, no está en la lista de sondas permitidas del proyecto y tiene
  `--show-token`.
- **Cómo se sabe si hay sesión:** por la primera petición real. Código de salida 4 o un 401 → «sin sesión: inicia sesión
  con `gh auth login`» (`SignInNeeded`). Un 403 sin límite agotado, o un 404 en un repositorio que existe → «tu sesión no
  tiene permiso sobre este repositorio».
- **La cuenta que publica** sale de `GET /user` (solo `login`), una vez por tanda de publicación, y queda en la entrada
  del registro. Es el papel «cuenta que publica» de la participación (D-30).
- **«Comprobar la conexión»** es un botón de la sección «PR»: lanza `GET /user` y `GET /repos/{o}/{r}`, con tu clic.
  Auto Doctor enseña el último resultado conocido, con su fecha, y no pregunta nada (D-31).

**D-14 · La versión de `gh`.**

- `gh --version` (sin red) da la versión. Entra en la puerta de Auto Doctor como `DoctorTool.GhVersion`, al final del enum.
- **Probada:** 2.96.0, la de las respuestas grabadas. **Mínima:** la que fije WP2 al comprobar desde cuándo existen
  `--input`, `-i` y el código de salida 4 (sin verificar, §13); por debajo, el proveedor no ofrece ninguna capacidad.
- **Otra versión** igual o superior a la mínima: funciona, y Auto Doctor avisa «versión no probada», como con los CLIs de
  los agentes (F2-7). `HostingCapabilities.UntestedCliVersion` lo lleva a la sección «PR».
- La versión de la API de GitHub va fijada en la cabecera `X-GitHub-Api-Version`: el adaptador depende de ella más que de
  la versión de `gh`.

### 4.4 Políticas y protecciones (F10-4)

**D-15 · Cuatro clases de operación remota y una compuerta.** Concreta la tabla de docs/16 D-26; las dos primeras filas de
aquella (el commit local y la entrega local) no cambian y no pasan por esta compuerta.

| Clase | Operaciones | Autorización | Hello |
|---|---|---|---|
| Publicar la rama | `push` de `agentstudio/run-<id>` | Persistente por proyecto si la das; si no, por uso | Al dar la autorización, o en cada uso sin ella |
| Escribir en el PR | Abrir, actualizar título y cuerpo, pedir revisores, publicar el estado y el comentario del veredicto | Persistente por proyecto si la das; si no, por uso | Igual |
| Fusionar | `Merge`, con un modo que el repositorio permita | **Siempre por uso** | Siempre |
| Destructivas | Cerrar un PR sin fusionar; borrar la rama remota de una ejecución | **Siempre por uso** | Siempre |

- **La regla la da el tipo.** `HostingPolicy.CanBePersistent(clase)` es código puro en Domain y devuelve falso para las dos
  últimas: `HostingPolicyGate.GrantAsync` rechaza una autorización persistente de esas clases, y el payload tampoco se
  puede construir (su fábrica lo valida).
- **Qué exige cada uso, con o sin autorización persistente:**
  1. el registro verifica contra su ancla;
  2. **el veredicto, según la operación** (`HostingPolicy.RequiresApprovedVerdict`, por operación y no por clase):
     - publicar la rama, abrir o actualizar el PR, pedir revisores y fusionar: `IDeliveryService.CheckAsync` da la
       ejecución por lista y el commit es el juzgado;
     - **publicar el veredicto** (el estado y el comentario): solo que el registro verifique y que haya un veredicto que
       contar. Tiene que poder decir «no aprobado» y «en revisión», que es justo cuando `CheckAsync` dice que no (D-22);
     - cerrar un PR y borrar su rama: no lo exigen (se cierra justo lo que no se aprobó);
  3. el destino coincide con el identificador de la autorización, o de la aprobación;
  4. la configuración pasa la inspección (D-05), para lo que use git;
  5. la capacidad está declarada (D-09).
- **Por uso** quiere decir `IApprovalGate`, con una petición atada (`ApprovalRequest.Binding`) a lo que se va a hacer:
  repositorio, número de PR, commit de cabeza, modo de fusión y rama base. La respuesta tiene que contestar a esa atadura,
  y se vuelve a comprobar el estado después de aprobar, como en la entrega (ADR-0012).
- **Las acciones se dan de alta por su identificador** en `ApprovalRiskClassifier.PresenceActions`, todas como
  `PresenceAction.RemoteOrSecretOperation` («siempre»): `hosting.policy.grant`, `hosting.publish`, `hosting.pr.write`,
  `hosting.pr.merge`, `hosting.pr.close` y `hosting.branch.delete`. Sus constantes viven en Application
  (`HostingActions`), y `CriticalApprovalCoverageTests` las vigila.
- **Descartado:** un ajuste que desactive el Hello de fusionar. Es barrera fija (ADR-0053).

**D-16 · Una autorización persistente es una entrada del registro, con alcance estrecho.**

- **`HostingPolicyGranted`:** la clase, el proveedor, el host, el propietario, el repositorio, la rama base (opcional: «solo
  hacia `main`») y quién la dio. Se da con aprobación y Hello.
- **Se retira con `HostingPolicyRevoked`,** que nombra la primera. Nada se edita.
- **El alcance es el identificador del repositorio remoto:** si cambias la URL de `origin` a otro repositorio, la
  autorización deja de aplicar (y se dice), porque estaba dada para otro destino.
- **Vale para el proyecto, no para una ejecución,** y no caduca sola (decisión 7). Se ve y se retira en la sección «PR».
- **Cada uso deja su entrada** (`HostingOperationPerformed`), con la autorización o la aprobación que lo amparó.
- **Dónde no vive:** ni en `AppSettings` ni en un fichero del proyecto. Un fichero del repositorio es contenido no fiable;
  el registro está anclado.

**D-17 · Las protecciones se leen y se enseñan antes; nunca se saltan.**

- **Qué se lee:** las reglas activas de la rama base (`BranchRules`): revisiones requeridas y cuántas, checks requeridos,
  firmas, historia lineal, cola de fusión, y si la rama está protegida. Y del repositorio, los modos de fusión permitidos.
- **Dónde se enseña:** en la tarjeta de publicación y en la de fusión, antes del botón. Con una autorización persistente
  no hay diálogo de aprobación, pero la tarjeta sale igual.
- **Si no se pueden leer** (la plataforma no las expone a tu permiso): «No se pudieron leer las protecciones de `main`.
  GitHub las aplicará igualmente». No se inventa un «sin protecciones».
- **Lo que choca se dice antes:** «`main` exige historia lineal: la fusión con commit de fusión no está disponible»;
  «exige commits firmados: los de Agent Studio no van firmados (ADR-0012). Fusiona con squash en GitHub, que firma el
  suyo, o publica tú».
- **Nunca administrador:** el catálogo no tiene forma de pedirlo. Si GitHub rechaza la fusión (405, 409 o 422), el
  resultado es `merge.blocked` con el estado que dio (`mergeable_state`), y se enseña.
- **La plataforma manda igualmente.** Agent Studio avisa antes y registra lo que pasó.

### 4.5 Publicar, abrir el PR y fusionar (F10-2)

**D-18 · «Publicar y abrir PR»: un flujo en pasos, idempotente.**

1. **Tarjeta de publicación** (`PublicationPreview`): destino, commits que suben (D-08), rama base (por defecto la del
   repositorio en GitHub), protecciones (D-17), título y cuerpo (D-19), y qué autorización lo ampara. Se calcula dentro del
   buzón, con el proyecto tomado (D-25): con una ejecución en marcha no se puede publicar, y la tarjeta lo dice.
2. **Tu clic.** Si no hay autorización persistente para alguna clase, la compuerta pide la aprobación, con Hello.
3. **El buzón** (D-25) toma el proyecto y, en orden: inspecciona la configuración → `push` → `ls-remote` → busca un PR
   abierto desde esa rama (`FindPullRequest`) → lo abre si no existe, o lo actualiza → publica el veredicto (D-21) → empieza a
   seguirlo.
4. **Cada paso deja su entrada** con su resultado. Un paso que falla para el flujo ahí y lo dice; lo ya hecho queda hecho.
5. **Tras un corte a mitad,** al reintentar se consulta el estado antes de repetir: la rama ya está en ese commit, el PR ya
   existe. Nada se duplica.

- **Solo PR dentro del mismo repositorio** (cabeza y base). Un fork queda fuera.
- **Borrador:** una casilla «Abrir como borrador», si la capacidad está declarada.
- **Desde dónde:** junto a «Entregar» en el monitor de la ejecución, y en «Repositorio y cambios › Resumen» (las ejecuciones
  con la rama pendiente de publicar).
- **La entrega local y el PR no se excluyen.** Son dos caminos a «integrado»; el tablero acepta cualquiera (D-29).

**D-19 · El título y el cuerpo los compone el sistema; tú puedes cambiarlos; ningún agente escribe en ellos** (decisión 9).

- **Por qué:** GitHub interpreta el cuerpo. Un `@alguien` avisa a esa persona, un `Closes #12` cierra una incidencia al
  fusionar, una imagen se carga desde donde diga su dirección. Texto de un agente ahí sería darle una vía para actuar
  con tu cuenta.
- **La plantilla** (en recursos, ADR-0054; en el idioma de la app):
  - título: «Agent Studio · » y el nombre del hito o, si no hay, el id de la ejecución;
  - cuerpo: la ejecución y su hito; los requisitos por su id con lo que el veredicto dice de cada uno; el resultado de la
    verificación en números; la decisión del juez y qué proveedor y modelo juzgó; las huellas de las evidencias; y una
    línea fija: «Publicado con Agent Studio. El veredicto de un juez de IA no es una revisión de GitHub».
- **Todo dato variable va dentro de un tramo de código** (entre acentos graves, y quitándoselos al dato): ahí GitHub no
  interpreta menciones, referencias ni enlaces.
- **Tu edición es tuya:** el cuadro deja cambiar el título (200 caracteres) y el cuerpo (20 000). Va por la entrada
  estándar como lo demás.
- **Qué queda en el registro:** el texto enviado, como evidencia con su SHA-256, y en la entrada solo la referencia. Ni el
  título ni el cuerpo van en el payload.

**D-20 · Fusionar, cerrar y borrar la rama.**

- **Fusionar:**
  - solo con un modo que el repositorio permita y la rama base admita (D-17); la lista sale de las capacidades;
  - la petición lleva `sha` = el commit juzgado: si la cabeza del PR ya no es esa, GitHub la rechaza. Es el equivalente de
    `--match-head-commit`, y lo que impide fusionar algo que el juez no vio (A-7);
  - no se envía título ni mensaje de la fusión: los pone la plataforma;
  - antes de pedir la aprobación se relee el PR (estado, cabeza, `mergeable_state`), y otra vez después de aprobar;
  - tras la respuesta, el buzón observa el PR y registra lo que **de verdad** pasó (D-27), no lo que se pidió.
- **Cerrar sin fusionar** y **borrar la rama remota:** por uso y con Hello. Solo se borran ramas `agentstudio/run-*`, y
  solo si su cabeza en el remoto es el commit esperado (se lee justo antes; la API de borrado no admite condición, así que
  queda una ventana mínima, que se anota como residual).
- **Nunca** se borra una rama local desde aquí: de eso se ocupa la limpieza de siempre (`IWorkspaceCleanup`).

### 4.6 El veredicto en el PR (F10-5)

**D-21 · Un estado del commit y un comentario etiquetado; nunca una revisión** (decisión 8).

| Canal | Qué es | Cómo se identifica |
|---|---|---|
| Estado del commit | `POST …/statuses/{commit juzgado}` con el contexto fijo `agent-studio/verdict` | Por el contexto y el commit: publicar otra vez lo sustituye |
| Comentario | Un comentario en el PR con una marca oculta en su primera línea: `<!-- agent-studio:verdict run=<id> -->` | Por su identificador, guardado en el registro; si se pierde, por la marca **y** que su autor sea tu cuenta |

- **Los dos por defecto, según capacidades:** el estado se ve en la caja de checks del PR y se puede exigir en las
  protecciones de la rama; el comentario explica. Si el estado no está disponible (sin permiso), queda el comentario.
- **El estado, de un vocabulario cerrado:**

  | Veredicto en vigor | `state` | Descripción (fija, del sistema) |
  |---|---|---|
  | Aprobado y válido | `success` | «Revisor independiente (juez): aprobado» |
  | Rechazado | `failure` | «Revisor independiente (juez): no aprobado» |
  | Impugnado, en revisión, o lo decide una persona | `pending` | «Revisor independiente (juez): en revisión» |
  | El registro no verifica | No se publica nada | — |

- **El comentario** lo compone el sistema (D-19): la decisión, el commit juzgado, el recuento por requisito con sus ids, qué
  proveedor y modelo juzgó, que el juez no produjo el trabajo, y una frase fija: «Esto no es una revisión de GitHub ni una
  aprobación de la cuenta que lo publica». **No lleva el razonamiento del juez:** es texto de un agente.
- **Se actualiza en su sitio** (`EditComment` por identificador): no se acumulan comentarios.
- **Cómo se garantiza que nunca es una aprobación:**
  1. `IHostingProvider` no tiene miembro para enviar revisiones;
  2. el catálogo solo tiene `Reviews` con `GET`; no existe una entrada que escriba en `/reviews`;
  3. una prueba recorre todos los `ProcessSpec` de la batería del adaptador y falla si alguno lleva `pr review`,
     `--approve`, o un método distinto de `GET` sobre una ruta que acabe en `/reviews`, o un cuerpo con `"event"`;
  4. una prueba de arquitectura: ningún fichero del adaptador contiene `APPROVE` fuera del lector de revisiones (que lee
     `APPROVED` como estado ajeno).
- **Descartado:** una revisión de tipo «comentario». Sigue siendo una revisión de tu cuenta, y aparece junto a las
  aprobaciones.

**D-22 · Cuándo cambia lo publicado.**

- **Un veredicto nuevo** (un reintento, una revisión que cierra) o **una impugnación** cambian el veredicto en vigor: la
  próxima vez que el buzón pase, actualiza el estado y el comentario. La regla del veredicto en vigor es la de
  `VerdictReach`, la misma del tablero.
- **La cabeza del PR cambió** (alguien empujó): el estado se queda en el commit juzgado, que ya no es la cabeza. El
  comentario se actualiza con «La cabeza del PR ya no es el commit que juzgó el juez», y la pantalla lo marca.
- **Es una escritura de la clase «escribir en el PR»:** sin autorización persistente, no se hace sola; queda como acción
  pendiente («El veredicto cambió: actualizar el PR»).
- **Cada publicación deja su entrada,** con el veredicto que refleja.

### 4.7 Sondeo y reconciliación (F10-6)

**D-23 · Se observa estado, no eventos: una instantánea con su huella.**

- **La instantánea** (`RemoteSnapshot`, §5.1) de un PR seguido: estado (abierto, borrador, fusionado, cerrado), commit de
  cabeza, rama base, si la rama de cabeza existe, commit y modo de la fusión, commits originales y resultantes, resumen de
  checks (cuántos pasan, fallan y esperan), resumen de revisiones, número de force-push vistos, commits que revierten la
  fusión, y el despliegue si lo hay. Solo identificadores, números y enumerados.
- **Su huella** es el SHA-256 de su forma canónica. **Dos instantáneas con la misma huella son una:** no se registra nada.
- **Lo que cambia se calcula en código puro** (`RemoteReconciler`, en Domain): de la última instantánea registrada y la
  nueva salen los cambios (`RemoteChangeKind`). Se prueba con secuencias repetidas y desordenadas, sin red ni disco.
- **Desorden:** las lecturas van en serie (D-25), y una instantánea cuyo `updated_at` es anterior al de la última
  registrada se descarta (una réplica atrasada del servidor). Un PR fusionado no vuelve a «abierto» por una respuesta vieja.
- **Se registra como `RemoteStateObserved`:** el objeto, la huella, la instantánea y los cambios. Nunca se reescribe una
  observación anterior (docs/16 D-46).
- **La última huella registrada se lee del registro,** no de la caché: si borras la caché, se relee todo una vez y no se
  duplica nada.

**D-24 · Peticiones condicionales, cadencia y límites.**

- **Qué se sondea:** solo los PR que Agent Studio abrió o que vinculaste, mientras estén abiertos; y los fusionados,
  durante 30 días, más despacio, por si hay un revert o un despliegue. «Dejar de seguir» lo corta.
- **Cadencia:** cada 5 minutos por defecto (2, 5, 15 o 60, en Ajustes), a intervalo fijo, solo con la app abierta y el
  permiso dado. Un fusionado, cada hora. Si GitHub da `x-poll-interval`, se respeta el mayor.
- **Condicional, y por recurso.** Cada lectura va con `If-None-Match` y el `ETag` de **ese mismo recurso** en la vuelta
  anterior. Un 304 dice que ese recurso no cambió; **no dice nada de los demás:** los checks, un revert en la rama base o
  un despliegue cambian sin que cambie el `ETag` del PR. Por eso cada recurso tiene su `ETag` y su 304:

  | Estado del PR | Qué se pregunta en cada vuelta | Solo si cambió la cabeza |
  |---|---|---|
  | Abierto o borrador | El PR; los checks y el estado combinado de su cabeza; sus revisiones | Sus commits y sus eventos |
  | Fusionado (30 días) | Los commits de la rama base desde la fusión; los despliegues del commit resultante | — |
  | Recién fusionado o cerrado (una vez) | El PR, sus commits y el commit de la fusión | — |

- **La instantánea se compone** con lo nuevo de los recursos que cambiaron y lo último conocido de los que dieron 304. Si
  todos dieron 304, no hay instantánea nueva ni entrada.
- **Coste:** un PR abierto sin cambios cuesta cuatro peticiones por vuelta, las cuatro con 304, que no cuentan contra el
  límite principal de GitHub (según sus buenas prácticas; sin verificar con `gh`, §13). Uno fusionado, dos por hora.
- **Límites:**
  - 429, o 403 con `x-ratelimit-remaining: 0` o con `retry-after`: no se lanza nada a ese host hasta la hora que digan
    `retry-after` o `x-ratelimit-reset`;
  - sin esas cabeceras, un minuto, y después espera progresiva (2, 5, 15 y 60 minutos);
  - nunca se insiste: GitHub avisa de que hacerlo puede acabar en el bloqueo de la integración;
  - la pantalla dice «GitHub pide esperar hasta las 14:32».
- **Solo lecturas en el sondeo.** Las escrituras son siempre acciones tuyas (D-02).

**D-25 · Un buzón por proyecto con un solo consumidor, y una sola cola hacia `gh`.**

- **El buzón** (`ProjectRemoteMailbox`) es un `Channel` con un único lector, como el motor de una ejecución. Recibe tres
  clases de mensaje: observar un PR, ejecutar una acción y registrar una observación. Los procesa en orden. No hay
  cerrojos nuevos: es la cola que pides para las carreras.
- **Una cola global hacia `gh`** (`GhQueue`, también de un consumidor) por debajo de todos los buzones: un solo `gh` a la
  vez en toda la app, que es lo que GitHub pide (peticiones en serie) y donde vive el estado de los límites.
- **El proyecto ocupado.** Tres reglas:
  1. **Leer por `gh` no toca ni git ni el registro:** el sondeo sigue aunque haya una ejecución en marcha.
  2. **Para lanzar `git`, sea cual sea la orden, o escribir en el registro,** el buzón toma el proyecto con
     `IProjectActivity.TryEnter(proyecto, ProjectActivityKind.RemoteOperation)`. Si devuelve nulo, hay otra actividad: el
     mensaje se queda en el buzón y se reintenta cuando el proyecto quede libre. No es «mirar y luego actuar»: es entrar
     o no entrar. «Sea cual sea» incluye lo que solo lee: `push`, `ls-remote`, la inspección de la configuración (D-05) y
     el recuento de commits de la tarjeta de publicación (D-08), que pide historia a la capa interna.
  3. **Todo el `git` de esta fase pasa por el buzón.** No hay ningún camino lateral:
     - la tarjeta de publicación (`PreviewAsync`) se calcula dentro del buzón; con el proyecto ocupado no lanza nada y
       dice «El proyecto está ocupado: podrás publicar cuando termine lo que está en marcha»;
     - las dos líneas de Auto Doctor que necesitan la inspección (`doctor.remote.config` y `doctor.remote.lfs`) la piden
       al buzón, y con el proyecto ocupado salen «sin comprobar»;
     - **el sondeo no lanza `git` nunca:** el identificador del remoto se guarda al empezar a seguir un PR, y un
       force-push se reconoce por la plataforma, no por la historia local (D-27).
  4. **Mientras el buzón lo tiene,** una ejecución, un debate o una auditoría que quieran empezar **se rechazan** con su
     motivo, como hoy cuando un debate tiene el proyecto: `Orchestrator.Enter` lanza si `TryEnter` da nulo; no espera.
     Por eso el buzón lo tiene lo mínimo: lo toma y lo suelta en cada mensaje (unos milisegundos para una entrada del
     registro; hasta 120 segundos para un `push`, que acabas de pedir tú). El motivo dice «Sincronizando con GitHub:
     inténtalo en unos segundos».
  5. **Lo que queda por comprobar** (§13): qué hace la reanudación automática de la Fase 8 si, al arrancar la app,
     encuentra el proyecto tomado. Para no cruzarse con ella, el buzón no empieza hasta que esa reanudación ha terminado.
- **`ProjectActivityKind.RemoteOperation`** se añade al final del enum. No comparte el proyecto con nada: en
  `OrchestrationActivity.TryAdd` (`Orchestration/Engine/Orchestrator.cs`) entra en la rama de `Debate` y `Audit`.
- **No es trabajo con agentes:** no gana un valor en `BackgroundWorkKind` ni aparece en la bandeja como una ejecución.
- **Lo que ya había y no cambia:** las pantallas de las Fases 6 y 9 que leen por la capa interna (los remotos, la
  historia) siguen como están; esta fase no les añade ni les quita nada. La regla de arriba es para el `git` que lanza
  esta fase.
- **Las observaciones que esperan** viven en memoria y en el fichero de pendientes (§6): si cierras la app, se releen al
  volver; no hay nada que perder, porque el estado se vuelve a observar.
- **Una acción tuya pasa delante** del sondeo en el buzón.
- **Al cerrar la app,** el buzón termina el mensaje en curso (un `push` no se corta a medias) y para.

**D-26 · Sin conexión o sin sesión: «pendiente de sincronizar», con su motivo.**

| Qué pasa | Cómo se reconoce | Qué se hace |
|---|---|---|
| Sin conexión | `gh` termina sin línea de estado HTTP; o `git` falla sin llegar al remoto | La acción queda «pendiente de sincronizar: sin conexión». Reintento con espera progresiva (1, 2, 5 y 15 minutos); nada se da por hecho |
| Sesión de `gh` caducada o ausente | Código de salida 4, o 401 | «Inicia sesión con `gh auth login`». El sondeo se para hasta que pulses «Reintentar»: no se insiste |
| Credenciales de git ausentes o caducadas | `git push` falla pidiendo credenciales, con los avisos desactivados | «Tu gestor de credenciales no tiene acceso a GitHub por HTTPS», con la guía. Sin reintento automático |
| Límite | D-24 | Espera hasta la hora indicada |
| El remoto redirige (repositorio renombrado) | `git` rechaza la redirección; la API responde 301 | «El repositorio cambió de dirección: actualiza tu remoto». No se sigue |

- **Las acciones pendientes** se guardan en el fichero de pendientes, no en el registro: no han pasado. Cuando pasan,
  dejan su entrada. Antes de cada reintento se vuelve a pasar la compuerta entera (D-15): un veredicto que se impugnó
  mientras tanto la para.
- **Una acción pendiente se puede cancelar,** y caduca a las 24 horas con su motivo a la vista.
- **La pantalla lo dice siempre** con la fecha de la última sincronización buena: nunca enseña como actual un estado que no
  pudo comprobar.

**D-27 · Qué cambios se reconocen, y con qué.**

| Cambio | Cómo se decide | Seguridad |
|---|---|---|
| Fusionado | `merged: true`, con `merge_commit_sha` y `merged_by.login` | Hecho de la plataforma |
| Modo de la fusión | Si la hizo Agent Studio, el modo pedido. Si no: el commit de la fusión con dos padres es «commit de fusión»; con uno, se compara el autor, la fecha de autor y el mensaje de su padre con el penúltimo commit del PR: iguales, «rebase»; distintos, «squash». Con un solo commit en el PR, «squash o rebase» | Deducido, y así se enseña |
| Cerrado sin fusionar, reabierto | `state` y `merged` | Hecho |
| Rama borrada | La rama de cabeza ya no existe (404 al leerla) | Hecho |
| Cabeza movida | `head.sha` distinto del anterior | Hecho |
| Force-push | El número de eventos `head_ref_force_pushed` del PR creció. No se mira la historia local: el sondeo no lanza `git` (D-25) | Hecho de la plataforma; se cuenta, no se aplica |
| Checks | El resumen cambió (pasan, fallan, esperan) | Hecho; los nombres no van al registro |
| Revisiones | El resumen cambió | Hecho |
| Revert | En la rama base, después de la fusión, hay un commit cuyo mensaje tiene la línea estándar de git `This reverts commit <commit de la fusión>.` | **Deducido de un texto:** se enseña «posible reversión (por el mensaje del commit)» y propone reabrir el hito; reabrirlo es decisión tuya |
| Despliegue | Un despliegue de la plataforma para un commit resultante, con estado `success` | Hecho de la plataforma (D-29) |

- **Un check que falla o un revert no reabren nada solos.** Sale la acción «Reabrir el hito», que registra
  `MilestoneReopened` con su motivo (`CheckFailed` o `Revert`, que ya existen) y la observación como evidencia.

### 4.8 Trazabilidad: la unión de commits, «integrado» y «desplegado» (F10-7 y F10-8)

**D-28 · La unión de commits es una proyección del registro.**

- **Lo que ya hay:** el registro conoce los commits del sistema (por intento), el commit de la integración que el juez vio y
  las evidencias de cada diff, que la limpieza no borra (docs/16 D-31).
- **Lo que añade la observación de una fusión** (`RemoteStateObserved` con el cambio `Merged`): los **commits originales**
  del PR (`PullRequestCommits`, hasta 250, que es el tope de GitHub; más allá, «truncado») y los **commits resultantes**
  (uno en un squash; uno por original, en orden, en un rebase; el de fusión en una fusión con commit).
- **La proyección** (`CommitSuccession`, pura, en Domain) responde dos preguntas:
  - de un commit original, ¿en qué quedó? → el PR, el modo y su commit resultante;
  - de un commit del remoto, ¿de dónde viene? → los originales, y por ellos el intento, la tarea, la ejecución, el
    requisito y las evidencias.
- **La cadena** (`ITraceChainReader`) gana tres etapas al final del enum `TraceChainStage`: `Publication` (el PR),
  `RemoteMerge` (modo y commit resultante; o «revertido por …») y `Deployment`. Y `TraceNodeKind.PullRequest`, cuyo id es
  `github.com/<o>/<r>#<n>`.
- **Nada se borra ni se reescribe:** los commits originales siguen en el registro aunque git los pode y la rama se borre.
- **«Commits», en «Repositorio y cambios»:** un commit de squash que aparezca en tu historia (cuando lo traigas con tu
  cliente) sale anotado «resultado del PR #12 (squash) de la ejecución …», por `IRepositoryAnnotator`.
- **Por qué proyección y no vínculos nuevos** (`TraceLinkRecorded` por cada commit): la observación ya lo dice todo, y un
  vínculo aparte podría retirarse o quedarse atrás. Los vínculos siguen para lo que afirma una persona.
- **Descartado:** inferir la unión de los trailers de los commits. Tras un squash dependen de la plataforma (docs/16 D-30);
  siguen siendo una ayuda para sugerir, nunca la fuente.

**D-29 · «Integrado» por PR exige que lo fusionado sea lo juzgado; «desplegado», que la plataforma lo diga** (decisión 15).

- **«Integrado»** (la columna del tablero y el estado del requisito en el informe del hito) se alcanza por cualquiera de dos
  hechos del registro:
  - la entrega local de siempre (`IntegrationPerformed` con `IsDelivery`); **o**
  - una observación `Merged` del PR de la ejecución **cuya cabeza al fusionarse es el commit juzgado.**
- **Si la cabeza era otra** (alguien empujó después del veredicto y se fusionó en la web): la tarjeta se queda en «hecho»,
  con el motivo «fusionado con cambios que el juez no vio», y el informe del hito lo lista como desviación. La invariante
  de `BoardRules` no cambia: nada llega a «integrado» sin un veredicto aprobado y válido **de eso mismo**.
- **`BoardRules` no se toca:** `RequirementBoardFacts.Delivered` pasa a calcularse con los dos hechos en `BoardProjector`.
- **Una reversión observada** no quita «integrado» sola: propone reabrir el hito (D-27).
- **«Desplegado»** solo aparece si hay una observación de despliegue con éxito de un commit resultante. Sin ese dato, el
  estado **no existe** en la pantalla: no sale «no desplegado» (docs/16 D-50). El nombre del entorno es texto del remoto:
  se enseña desde la caché, como dato; el registro guarda el identificador del despliegue y su estado.
- **`MilestoneClosedPayload.IntegratedCommit`** pasa a poder ser el commit de la fusión, como ya dice su comentario.

**D-30 · La participación gana sus tres fuentes remotas.**

- **Cuenta que publica:** el `login` de `GET /user` que quedó en `HostingOperationPerformed`.
- **Revisor:** además del juez, las personas que revisaron el PR, con su decisión, de la observación.
- **Integrador:** `merged_by` de la observación `Merged`, además de la entrega local.
- Un papel sin fuente sigue saliendo «no consta». `ParticipationSources` gana tres códigos.

### 4.9 Auto Doctor

**D-31 · Una fuente nueva de comprobaciones, sin red.**

| Sección | Código | Qué dice | De dónde sale |
|---|---|---|---|
| Herramientas | `doctor.gh.installed` | `gh` instalado y su versión; o «no instalado» (sin él no hay PR) | `DoctorTool.GhVersion` (`gh --version`) |
| Herramientas | `doctor.gh.version-untested` | Aviso: versión distinta de la probada | D-14 |
| Proyecto | `doctor.remote.host` | El remoto es GitHub; o «no reconocido»; o «sin remoto» | `IRepositoryRemoteReader` y `RemoteRepository` |
| Proyecto | `doctor.remote.transport` | Aviso: tu remoto es SSH; Agent Studio publica por HTTPS | La URL del remoto |
| Proyecto | `doctor.remote.config` | Error: el repositorio define claves que impiden publicar, con la lista (sin valores) | La inspección de D-05 |
| Proyecto | `doctor.remote.lfs` | Aviso: el proyecto usa Git LFS; no se puede publicar desde aquí | D-08 |
| Proyecto | `doctor.remote.session` | El último resultado conocido de la sesión de `gh`, con su fecha; o «sin comprobar» | El estado del buzón, sin preguntar nada |
| Proyecto | `doctor.remote.sync` | Si la sincronización está permitida y cuándo fue la última buena | Ajustes y el buzón |

- **Ninguna pregunta a la red.** «Sin comprobar» pesa más que «correcto», como siempre.
- **Los arreglos son de «ir» o de «guía»:** «Ir a Repositorio › PR», «Instala la CLI de GitHub» (con la orden de `winget`
  para copiar), «Inicia sesión con `gh auth login`» (para copiar; la app no la ejecuta).
- **La inspección de D-05 usa git dentro del repositorio,** por el ejecutor remoto y sin transporte: es la misma excepción
  que ya aceptaste para la sección «Proyecto» (decisión 13 de docs/20). Se pide al buzón, que toma el proyecto: con una
  ejecución, un debate o una auditoría en marcha, `doctor.remote.config` y `doctor.remote.lfs` salen «sin comprobar: el
  proyecto está ocupado» (D-25).

---

## 5. Contratos

Los escribe el P0 (§10.2) y los documenta en [04](04-contratos.md) §26 (la §24 la reserva docs/19). Aquí va su forma; los
nombres exactos de los miembros pueden ajustarse en el P0, no su reparto por capas ni lo que **no** tienen. Todos los
ficheros de esta sección son nuevos salvo que se diga.

### 5.1 Dominio

`src/AgentStudio.Domain/Hosting/RemoteRepository.cs`:

```csharp
public enum HostingProviderKind { GitHub, AzureDevOps }   // appended, never reordered; AzureDevOps has no adapter in this delivery

/// A repository on a hosting platform, by validated identifiers (D-04). Never built from free text: Parse rejects, it does not fix.
public sealed record RemoteRepository
{
    public static RemoteRepository? TryParseGitHub(string remoteUrl, out string? refusalCode);
    public HostingProviderKind Provider { get; }
    public string Host { get; }          // "github.com", exactly
    public string Owner { get; }
    public string Name { get; }
    public string PushUrl { get; }       // https://github.com/<owner>/<name>.git, written by the code
    public string Id { get; }            // github.com/<owner>/<name>
}

public sealed record RemoteBranch        // a ref name under refs/heads, by the rules of git check-ref-format; never starts with '-'
{
    public static RemoteBranch ForRun(RunId run);          // agentstudio/run-<id>
    public static RemoteBranch? TryParse(string name);
    public string Name { get; }
    public bool IsRunBranch { get; }
}

public sealed record PullRequestRef(RemoteRepository Repository, int Number)   // Number > 0
{
    public string TraceId { get; }       // github.com/<owner>/<name>#<number>: the id of TraceNodeKind.PullRequest
}

public sealed record RemoteLogin         // an account name by the rule of Owner; anything else is «no consta»
{
    public static RemoteLogin? TryParse(string? login);
    public string Value { get; }
}
```

`src/AgentStudio.Domain/Hosting/HostingPolicy.cs`:

```csharp
public enum HostingOperationClass { PublishRunBranch, PullRequestWrite, MergePullRequest, Destructive }

public enum HostingOperationKind
{
    PushRunBranch, OpenPullRequest, UpdatePullRequest, RequestReviewers,
    PublishVerdictStatus, PublishVerdictComment, MergePullRequest, ClosePullRequest, DeleteRemoteBranch,
}

public enum MergeMethod { MergeCommit, Squash, Rebase }

/// The rule of docs/21 D-15, pure: which class an operation is, whether its class may hold a standing grant, and whether a use needs a verdict.
public static class HostingPolicy
{
    public static HostingOperationClass ClassOf(HostingOperationKind operation);
    public static bool CanBePersistent(HostingOperationClass @class);      // false for MergePullRequest and Destructive
    /// By operation, not by class: false for PublishVerdictStatus and PublishVerdictComment (they must be able to say
    /// «not approved» and «in review») and for ClosePullRequest and DeleteRemoteBranch; true for everything else.
    public static bool RequiresApprovedVerdict(HostingOperationKind operation);
}

/// A standing grant as the log holds it (D-16). Matches only the same class and the same repository (and base branch, when it names one).
public sealed record HostingPolicyGrant(string GrantId, HostingOperationClass Class, RemoteRepository Repository,
    RemoteBranch? BaseBranch, string GrantedBy, DateTimeOffset At)
{
    public bool Covers(HostingOperationClass @class, RemoteRepository repository, RemoteBranch? baseBranch);
}
```

`src/AgentStudio.Domain/Hosting/HostingCapabilities.cs`:

```csharp
[Flags]
public enum HostingCapability
{
    None = 0, ReadRepository = 1, ReadBranchRules = 2, OpenPullRequest = 4, UpdatePullRequest = 8, Comment = 16,
    RequestReviewers = 32, ReadReviews = 64, ReadChecks = 128, ReadWorkflowRuns = 256, Merge = 512, ClosePullRequest = 1024,
    DeleteBranch = 2048, CommitStatus = 4096, ReadDeployments = 8192, ConditionalRequests = 16384, DraftPullRequest = 32768,
}

/// What a provider can do with one version of its CLI (D-09). What is not declared does not appear on the screen (F10-1).
public sealed record HostingCapabilities(HostingCapability Supported, IReadOnlyList<MergeMethod> MergeMethods, bool UntestedCliVersion)
{
    public static readonly HostingCapabilities None;
    public bool Has(HostingCapability capability);
    public HostingCapabilities NarrowedBy(RemoteRepositoryFacts repository);   // the merge methods the repository allows, the permission of the account
}
```

`src/AgentStudio.Domain/Hosting/RemoteSnapshot.cs`:

```csharp
public enum PullRequestState { Open, Draft, Merged, Closed }
public enum RemoteMergeMode { MergeCommit, Squash, Rebase, SquashOrRebase, Unknown }
public enum ReviewVerdict { Approved, ApprovedWithSuggestions, ChangesRequested, Waiting, Rejected, Commented, Dismissed }   // covers the numeric votes of Azure DevOps
public enum ChecksRollup { None, Pending, Passing, Failing }
public enum DeploymentState { Pending, Succeeded, Failed, Inactive, Unknown }

public sealed record ChecksSummary(ChecksRollup Rollup, int Passing, int Failing, int Pending, bool Truncated);
public sealed record ReviewsSummary(int Approved, int ChangesRequested, int Commented, IReadOnlyList<RemoteLogin> Approvers);
public sealed record RemoteDeployment(long Id, string Commit, DeploymentState State);

/// What was seen of one pull request (D-23): identifiers, numbers and enums only. No text of the remote.
public sealed record RemoteSnapshot(
    PullRequestRef PullRequest, PullRequestState State, string HeadCommit, RemoteBranch BaseBranch, bool HeadBranchExists,
    string? MergeCommit, RemoteMergeMode? MergeMode, RemoteLogin? MergedBy,
    IReadOnlyList<string> OriginalCommits, IReadOnlyList<string> ResultCommits, bool CommitsTruncated,
    ChecksSummary Checks, ReviewsSummary Reviews, int ForcePushes, IReadOnlyList<string> RevertCommits,
    RemoteDeployment? Deployment, DateTimeOffset UpdatedAt)
{
    public string Fingerprint { get; }   // SHA-256 of the canonical form; UpdatedAt is not part of it
}

public enum RemoteChangeKind
{
    Opened, HeadMoved, ForcePushed, Merged, Closed, Reopened, HeadBranchDeleted, ChecksChanged, ReviewsChanged,
    RevertSuspected, DeploymentChanged,
}

/// Pure (D-23): the changes between the last recorded snapshot and a new one. Same fingerprint, no change; an older UpdatedAt, discarded.
public static class RemoteReconciler
{
    public static ReconcileResult Reconcile(RemoteSnapshot? lastRecorded, RemoteSnapshot observed);
}
public sealed record ReconcileResult(bool Discarded, IReadOnlyList<RemoteChangeKind> Changes);
```

`src/AgentStudio.Domain/Hosting/CommitSuccession.cs`:

```csharp
/// What became of a commit on the remote, and where a commit of the remote comes from (D-28). A projection: built from the observations of the log.
public sealed record CommitSuccessor(PullRequestRef PullRequest, RemoteMergeMode Mode, IReadOnlyList<string> ResultCommits, bool HeadWasJudgedCommit, IReadOnlyList<string> RevertedBy);

public sealed class CommitSuccession
{
    public static CommitSuccession From(IEnumerable<RemoteSnapshot> mergedSnapshots, Func<PullRequestRef, string?> judgedCommitOf);
    public CommitSuccessor? SuccessorOf(string originalCommit);
    public IReadOnlyList<string> OriginalsOf(string resultCommit);
}
```

Enums existentes que crecen **al final** (los hace el P0, con sus pruebas de compatibilidad):

| Enum | Valores nuevos | Fichero (existe) |
|---|---|---|
| `DecisionKind` | `HostingPolicyGranted`, `HostingPolicyRevoked`, `HostingOperationPerformed`, `RemoteStateObserved` | `Domain/Decisions/DecisionLog.cs` |
| `TraceNodeKind` | `PullRequest` | `Domain/Tracing/TraceRef.cs` |
| `TraceChainStage` | `Publication`, `RemoteMerge`, `Deployment` | `Application/Work/TraceContracts.cs` |
| `ProjectActivityKind` | `RemoteOperation` | `Application/Orchestration/ProjectActivity.cs` |
| `DoctorTool` | `GhVersion` | `Application/Diagnostics/DoctorContracts.cs` |
| `BranchKind` | `RemoteRun` (la rama de una ejecución en el remoto) | `Application/Git/RepositoryViewContracts.cs` |
| `RepositoryActivityKind` | `RemoteOperation`, `RemoteObservation` | ídem |

- **`DecisionKind` crece en cuatro valores y ninguno más.** Lo pendiente de sincronizar no es una clase de entrada: no ha
  pasado (D-26). Una reapertura del hito por un revert usa `MilestoneReopened`, que ya existe.
- **`PresenceAction` no crece:** `RemoteOrSecretOperation` ya cubre todo lo de esta fase.

### 5.2 Application

`src/AgentStudio.Application/Hosting/HostingContracts.cs`:

```csharp
public enum HostingOutcome
{
    Ok, NotModified, SignInNeeded, RateLimited, Unreachable, Forbidden, NotFound, Refused, Unsupported, InvalidAnswer,
}

/// Every answer of a provider. RetryAt comes from retry-after or x-ratelimit-reset; RefusalCode is a stable code, never text of the remote.
public sealed record HostingResult<T>(HostingOutcome Outcome, T? Value = default, string? ETag = null,
    DateTimeOffset? RetryAt = null, string? RefusalCode = null);

public sealed record HostingAvailability(bool Installed, Version? CliVersion, HostingCapabilities Capabilities);
public sealed record RemoteRepositoryFacts(RemoteBranch DefaultBranch, IReadOnlyList<MergeMethod> AllowedMergeMethods,
    bool CanPush, bool Archived);
public enum BranchRuleKind { Protected, RequiredReviews, RequiredChecks, SignedCommits, LinearHistory, MergeQueue, Unknown }
public sealed record BranchRule(BranchRuleKind Kind, int? Count);
public sealed record BranchRules(bool Readable, IReadOnlyList<BranchRule> Rules);

public sealed record PullRequestText(string Title, string Body);          // sent through standard input, never as an argument
public sealed record PullRequestDraft(RemoteRepository Repository, RemoteBranch Head, RemoteBranch Base, PullRequestText Text, bool Draft);
public enum ObservedResource { PullRequest, Commits, Events, Reviews, CheckRuns, CombinedStatus, WorkflowRuns, MergeCommit, BaseCommits, Deployments }
/// One ETag per resource (D-24): a 304 of the pull request says nothing of its checks, of the base branch or of the deployments.
public sealed record ObservationCursor(IReadOnlyDictionary<ObservedResource, string> ETags, RemoteSnapshot? LastSnapshot);
/// The snapshot composed from the resources that changed and the last known of those that answered 304. Outcome NotModified: every resource answered 304.
public sealed record PullRequestObservation(RemoteSnapshot Snapshot, ObservationCursor Cursor, RemoteDisplayTexts Display);

/// Texts of the remote for the screen only (D-12): bounded, cleaned, kept in the local cache, never written to the log.
public sealed record RemoteDisplayTexts(string? Title, IReadOnlyList<string> FailingCheckNames, string? DeploymentEnvironment);

public enum VerdictStanding { Approved, Rejected, InReview }
/// What is published of a verdict (D-21): a closed vocabulary and ids. No text of the judge.
public sealed record VerdictNotice(RunId Run, string JudgedCommit, VerdictStanding Standing,
    IReadOnlyList<RequirementOutcome> Requirements, string JudgeProvider, string JudgeModel, bool HeadIsJudgedCommit);
public sealed record RequirementOutcome(string RequirementId, bool Satisfied);
public sealed record PublishedVerdict(bool StatusPublished, long? CommentId);
public sealed record MergeOutcome(bool Merged, string? MergeCommit, string? BlockedState);

/// A hosting platform through its official CLI (D-09 and D-10). No member submits a review, approves, or merges as an administrator.
public interface IHostingProvider
{
    HostingProviderKind Kind { get; }

    /// The version of the CLI and what it can do. Runs `--version` only: no network, no account.
    Task<HostingAvailability> ProbeAsync(CancellationToken ct);

    Task<HostingResult<RemoteLogin>> ReadAccountAsync(RemoteRepository repository, CancellationToken ct);
    Task<HostingResult<RemoteRepositoryFacts>> ReadRepositoryAsync(RemoteRepository repository, string? etag, CancellationToken ct);
    Task<HostingResult<BranchRules>> ReadBranchRulesAsync(RemoteRepository repository, RemoteBranch branch, CancellationToken ct);

    Task<HostingResult<PullRequestRef?>> FindPullRequestAsync(RemoteRepository repository, RemoteBranch head, CancellationToken ct);
    Task<HostingResult<PullRequestRef>> OpenPullRequestAsync(PullRequestDraft draft, CancellationToken ct);
    Task<HostingResult<bool>> UpdatePullRequestAsync(PullRequestRef pullRequest, PullRequestText text, CancellationToken ct);
    Task<HostingResult<bool>> RequestReviewersAsync(PullRequestRef pullRequest, IReadOnlyList<RemoteLogin> reviewers, CancellationToken ct);

    Task<HostingResult<PullRequestObservation>> ObserveAsync(PullRequestRef pullRequest, ObservationCursor? cursor, CancellationToken ct);

    /// The status of the judged commit and the tagged comment (D-21). commentId is the comment to edit; null looks it up by its mark and its author.
    Task<HostingResult<PublishedVerdict>> PublishVerdictAsync(PullRequestRef pullRequest, VerdictNotice notice, string commentBody, long? commentId, CancellationToken ct);

    /// Merges only when the head of the pull request is still expectedHead (D-20).
    Task<HostingResult<MergeOutcome>> MergeAsync(PullRequestRef pullRequest, MergeMethod method, string expectedHead, CancellationToken ct);
    Task<HostingResult<bool>> CloseAsync(PullRequestRef pullRequest, CancellationToken ct);
    Task<HostingResult<bool>> DeleteRunBranchAsync(RemoteRepository repository, RemoteBranch runBranch, string expectedCommit, CancellationToken ct);
}

/// The provider of a repository: by the kind its identifier carries. Unknown host, no provider.
public interface IHostingProviders
{
    IHostingProvider? For(RemoteRepository repository);
    Task<RemoteRepository?> IdentifyAsync(ProjectContext project, string remoteName, CancellationToken ct);
}
```

`src/AgentStudio.Application/Hosting/RemoteGitContracts.cs`:

```csharp
public sealed record RemoteConfigRefusal(string Key, string Scope, string OriginFile);   // never the value (D-05)
public sealed record RemoteGitInspection(bool Accepted, IReadOnlyList<RemoteConfigRefusal> Refusals, bool UsesLfs, string? FailureCode);

public enum RemotePushOutcome { Pushed, AlreadyUpToDate, RejectedNotFastForward, CredentialsNeeded, Unreachable, Redirected, ConfigurationRefused, LfsRefused, NotConfirmed, Failed }
public sealed record RemotePush(ProjectContext Project, RemoteRepository Destination, RunId Run, string Commit);   // no force, no refspec, no branch name
public sealed record RemotePushResult(RemotePushOutcome Outcome, string? RemoteCommit, RemoteGitInspection Inspection);
public sealed record RemoteBranchRead(bool Reached, string? Commit, string? FailureCode);

/// The remote executor of git (D-03): three members and no overload with arguments. Separate from the internal git layer, which never opens a transport.
public interface IRemoteGitRunner
{
    Task<RemoteGitInspection> InspectAsync(ProjectContext project, RemoteRepository destination, CancellationToken ct);
    Task<RemoteBranchRead> ReadBranchAsync(ProjectContext project, RemoteRepository destination, RemoteBranch branch, CancellationToken ct);
    Task<RemotePushResult> PushRunBranchAsync(RemotePush push, CancellationToken ct);
}
```

`src/AgentStudio.Application/Hosting/PublicationContracts.cs`:

```csharp
public static class HostingActions   // the ids of the approvals; ApprovalRiskClassifier lists each as RemoteOrSecretOperation
{
    public const string GrantPolicyAction = "hosting.policy.grant";
    public const string PublishAction = "hosting.publish";
    public const string PullRequestWriteAction = "hosting.pr.write";
    public const string MergeAction = "hosting.pr.merge";
    public const string ClosePullRequestAction = "hosting.pr.close";
    public const string DeleteBranchAction = "hosting.branch.delete";
}

public sealed record HostingOperation(ProjectContext Project, RunId Run, HostingOperationKind Kind, RemoteRepository Repository,
    RemoteBranch? BaseBranch, PullRequestRef? PullRequest, string? Commit, MergeMethod? Method);

public enum HostingAuthorizationSource { StandingGrant, Approval }
public sealed record HostingAuthorization(bool Allowed, HostingAuthorizationSource? Source, string? GrantId, string? ApprovalBinding, UserText? Refusal);

/// The gate of the policies (D-15): every remote operation passes here, and only here, before it runs.
public interface IHostingPolicyGate
{
    Task<HostingAuthorization> AuthorizeAsync(HostingOperation operation, CancellationToken ct);
    Task<HostingPolicyGrant?> GrantAsync(ProjectContext project, HostingOperationClass @class, RemoteRepository repository, RemoteBranch? baseBranch, CancellationToken ct);
    Task RevokeAsync(ProjectContext project, string grantId, CancellationToken ct);
    Task<IReadOnlyList<HostingPolicyGrant>> GrantsAsync(ProjectContext project, CancellationToken ct);
}

public sealed record PublicationPreview(bool Ready, UserText? NotReadyReason, RemoteRepository? Repository, string? Commit,
    RemoteBranch? BaseBranch, int SystemCommits, int? OwnUnpublishedCommits, BranchRules? Rules, RemoteGitInspection? Inspection,
    HostingCapabilities Capabilities, PullRequestText ProposedText, PullRequestRef? Existing);
public sealed record PublicationRequest(ProjectContext Project, RunId Run, RemoteBranch BaseBranch, PullRequestText Text, bool Draft);

/// What the person asks for (D-18 and D-20). Each call enqueues work in the mailbox of the project and returns its ticket.
/// PreviewAsync and InspectAsync run inside the mailbox too, holding the project: with the project busy they launch nothing and say so.
public interface IPublicationService
{
    Task<RemoteGitInspection?> InspectAsync(ProjectContext project, CancellationToken ct);   // for Auto Doctor; null when the project is busy
    Task<PublicationPreview> PreviewAsync(ProjectContext project, RunId run, RemoteBranch? baseBranch, CancellationToken ct);
    Task<RemoteWorkTicket> PublishAsync(PublicationRequest request, CancellationToken ct);
    Task<RemoteWorkTicket> UpdateVerdictAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct);
    Task<RemoteWorkTicket> RequestReviewersAsync(ProjectContext project, PullRequestRef pullRequest, IReadOnlyList<RemoteLogin> reviewers, CancellationToken ct);
    Task<RemoteWorkTicket> MergeAsync(ProjectContext project, PullRequestRef pullRequest, MergeMethod method, CancellationToken ct);
    Task<RemoteWorkTicket> CloseAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct);
    Task<RemoteWorkTicket> DeleteRunBranchAsync(ProjectContext project, RunId run, CancellationToken ct);
}
```

`src/AgentStudio.Application/Hosting/RemoteSyncContracts.cs`:

```csharp
public enum RemoteWorkState { Queued, WaitingForProject, Running, PendingSync, Done, Failed, Cancelled, Expired }
public enum PendingReason { Offline, SignInNeeded, CredentialsNeeded, RateLimited, ProjectBusy }
public sealed record RemoteWorkTicket(string Id, HostingOperationKind? Operation, PullRequestRef? PullRequest);
public sealed record RemoteWorkStatus(RemoteWorkTicket Ticket, RemoteWorkState State, PendingReason? Reason, DateTimeOffset? RetryAt, UserText? Message);

/// The mailbox of a project and the poll (D-23 to D-26): one consumer, in order. Reads by the CLI go on with the project busy; git and the log wait for it.
public interface IRemoteSync
{
    Task SyncNowAsync(ProjectContext project, CancellationToken ct);       // the person's click; allowed even with the periodic poll off
    Task FollowAsync(ProjectContext project, PullRequestRef pullRequest, RunId? run, CancellationToken ct);
    Task UnfollowAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct);
    Task CancelAsync(string projectId, string ticketId, CancellationToken ct);
    IReadOnlyList<RemoteWorkStatus> Pending(string projectId);
    RemoteSyncHealth Health(string projectId);                           // last good sync, session as last seen, rate limit until
    event EventHandler<string>? Changed;                                 // the project id
}
public sealed record RemoteSyncHealth(DateTimeOffset? LastGoodSync, HostingOutcome? LastOutcome, DateTimeOffset? RateLimitedUntil);

/// The permission of the network (D-02). AppSettings lives in Desktop, so the mailbox asks a port; without one in the composition, the network is never allowed.
public interface IRemoteSyncPolicy
{
    bool PeriodicSyncAllowed(string projectId);
    TimeSpan Interval { get; }
}

/// What the screen shows of a followed pull request: the last recorded snapshot from the verified log, with the display texts of the cache.
public sealed record PullRequestView(PullRequestRef PullRequest, RunId? Run, RemoteSnapshot? Snapshot, string? JudgedCommit, bool HeadIsJudgedCommit,
    VerdictStanding? Verdict, RemoteDisplayTexts Display, DateTimeOffset? ObservedAt, IReadOnlyList<RemoteWorkStatus> Pending);
public interface IPullRequestProjector
{
    Task<IReadOnlyList<PullRequestView>> ListAsync(ProjectContext project, CancellationToken ct);
}

/// The local state that can be deleted (§6): ETags, display texts, what is followed and what is pending.
public interface IRemoteSyncStore
{
    Task<RemoteSyncState> LoadAsync(string projectId, CancellationToken ct);
    Task SaveAsync(string projectId, RemoteSyncState state, CancellationToken ct);
}
```

- `IRemoteGitRunner` no tiene ninguna sobrecarga con una orden, un argumento o un refspec libres: es la garantía de D-03
  y D-07.
- `IHostingProvider` no tiene ningún miembro que envíe una revisión: es la garantía de D-21.
- `IHostingPolicyGate` es el único camino: `PublicationService` no recibe los dos puertos de arriba directamente, sino
  detrás de la compuerta (una prueba de arquitectura exige que `IHostingProvider` e `IRemoteGitRunner` solo se inyecten
  en `Orchestration/Publication/**`; la fuente de Auto Doctor usa `IDoctorToolRunner` para la versión de `gh` e
  `IPublicationService.InspectAsync` y `IRemoteSync.Health` para lo demás).
- Los contratos que ya existen y cambian: `ParticipationSources` gana `RemotePublication`, `RemoteReview` y `RemoteMerge`;
  `CommitAnnotation` gana un `CommitSuccessor?` opcional al final.

### 5.3 Payloads

`src/AgentStudio.Application/Decisions/HostingPayloads.cs`:

```csharp
public sealed record HostingPolicyGrantedPayload(string GrantId, HostingOperationClass Class, HostingProviderKind Provider,
    string Host, string Owner, string Repository, string? BaseBranch, string GrantedBy) : DecisionPayload;

public sealed record HostingPolicyRevokedPayload(string GrantId, string RevokedBy) : DecisionPayload;

public sealed record HostingOperationPerformedPayload(
    HostingOperationKind Operation, HostingOperationClass Class, string Run,
    HostingProviderKind Provider, string Host, string Owner, string Repository,
    int? PullRequest, string? Branch, string? Commit, MergeMethod? Method,
    string? GrantId, long? ApprovalSequence,          // exactly one of the two: what allowed it
    HostingOutcome Outcome, string? FailureCode,
    string? Account,                                  // the login that published, validated; null is «no consta»
    EvidenceId? Text, string? TextSha256,             // the title and body sent, as evidence (D-19)
    string? Verdict, long? CommentId) : DecisionPayload;

public sealed record RemoteStateObservedPayload(
    HostingProviderKind Provider, string Host, string Owner, string Repository, int PullRequest, string? Run,
    string Fingerprint, string? PreviousFingerprint, IReadOnlyList<RemoteChangeKind> Changes,
    RemoteSnapshotPayload Snapshot) : DecisionPayload;   // the snapshot of §5.1, field by field
```

- **Las cuatro con `RunId = NoRun`,** como las de la memoria y los vínculos: la ejecución va en un campo. Así la
  reproducción de una ejecución y el dossier del juez nunca las seleccionan, y una ejecución terminada no gana entradas.
- **Sin texto libre:** ni títulos, ni nombres de checks o de entornos, ni mensajes de error del remoto, ni rutas de tu
  equipo. Identificadores validados, commits, números y enumerados. El texto enviado (título y cuerpo) es una evidencia.
- **Las fábricas validan:** una autorización persistente de una clase que no la admite no se puede construir; un commit es
  de 40 o 64 hexadecimales; `Owner` y `Repository` pasan la regla de D-04.
- **Nada de credenciales, por construcción:** ningún campo podría llevarlas.

### 5.4 Infrastructure (interno, lo fijan WP1 y WP2)

- **`Infrastructure/Git/Remote/`:** `RemoteGitRunner` (implementa `IRemoteGitRunner`), `RemoteGitCommandLine` (la constante
  de D-06 y la única función que escribe el refspec), `RemoteGitEnvironment` (la lista blanca), `RemoteConfigInspection`
  (la lista de D-05, sobre el lector de `--show-scope` que ya tiene `GitCallbacks`) y `RemoteGitOptions`
  (`AllowedTransport`: `Https` en la app; `File` solo en pruebas).
- **`Infrastructure/Hosting/Cli/`:** `GhRunner` (el único que lanza `gh`: ejecutable, carpeta neutra, entorno, topes),
  `GhQueue` (la cola de un consumidor, con el estado del límite por host) y `GhResponseReader` (línea de estado, cabeceras
  por lista y cuerpo).
- **`Infrastructure/Hosting/GitHub/`:** `GitHubEndpoint` (el catálogo cerrado), `GitHubApiPath` (el constructor de rutas),
  `GitHubRequestBodies` (los JSON de las escrituras), los lectores de cada respuesta y `GitHubHostingProvider`.
- **`Infrastructure/Hosting/State/`:** `RemoteSyncStore` (el fichero local de §6), leído como no fiable.

---

## 6. Modelo de datos

**Ninguna migración.** El esquema no cambia.

| Dato | Dónde | Por qué ahí |
|---|---|---|
| Autorizaciones persistentes y su retirada | Registro: `HostingPolicyGranted`, `HostingPolicyRevoked` | Es una decisión tuya, anclada; un fichero del proyecto no sería fiable |
| Cada operación remota con su resultado | Registro: `HostingOperationPerformed` | Lo que hizo la app con tu cuenta |
| El título y el cuerpo enviados | Evidencia, con su SHA-256 | Texto; el registro guarda la referencia |
| Lo observado del remoto | Registro: `RemoteStateObserved` | Es la fuente de «integrado», «desplegado» y la cadena |
| `ETag`, textos para la pantalla, PR seguidos y cursor de cada uno | `%LOCALAPPDATA%\AgentStudio\hosting\<proyecto>\sync.json` | Caché: se borra y se relee una vez, sin duplicar nada (D-23) |
| Acciones pendientes de sincronizar | `%LOCALAPPDATA%\AgentStudio\hosting\<proyecto>\pending.json` | No han pasado: no son historia. Caducan a las 24 h |
| Carpeta neutra y temporal | `%LOCALAPPDATA%\AgentStudio\hosting\{cwd,tmp}` | D-06 y D-11 |
| El permiso «Sincronizar con GitHub» por proyecto y el intervalo | `AppSettings` (y `WithPreferencesOf`) | Como el permiso de red del doctor de skills |

- **Ningún secreto en ningún sitio:** la bóveda no se usa, y los dos ficheros locales no llevan nada que lo sea.
- **`AgentStudioFiles` no cambia:** nada de esta fase se escribe en `.agentstudio/` del proyecto.
- **`DecisionKind` crece en cuatro valores, al final,** con sus pruebas de compatibilidad.

---

## 7. Pantallas

Todas con sus textos en recursos (`HostingStrings.resx` y `.en.resx`, nuevos; ADR-0054), con su ⓘ, con su artículo de
ayuda y en el inventario automático. **Nada simulado:** una acción cuya capacidad no está declarada no tiene botón, y un
dato que no se pudo leer dice que no se pudo.

### 7.1 «Repositorio y cambios › PR» (sección nueva)

- **Cabecera:** el remoto identificado («GitHub · `propietario/repositorio`»), la sesión como se vio por última vez, la
  última sincronización buena, el interruptor «Sincronizar con GitHub» de este proyecto, «Sincronizar ahora» y «Comprobar
  la conexión».
- **Sin remoto reconocido, sin `gh` o sin sesión:** la sección lo dice con su guía, y no enseña una lista vacía como si no
  hubiera PR.
- **La lista,** una fila por PR seguido:

  | Columna | Qué enseña |
  |---|---|
  | PR | «#12» y su título (dato del remoto, de la caché) |
  | Estado | Abierto, Borrador, Fusionado (con su modo), Cerrado sin fusionar |
  | Ejecución e hito | De dónde viene |
  | Veredicto | El que está en vigor, y si es el publicado |
  | Lo juzgado | «La cabeza es el commit juzgado» o «La cabeza cambió después del veredicto» |
  | Checks | «5 pasan · 1 falla · 2 esperan» |
  | Revisiones | «Aprobado por 1 persona», «Cambios pedidos» |
  | Despliegue | Solo si la plataforma lo informa |
  | Sincronización | «Hace 3 min» o «Pendiente de sincronizar: sin conexión» |

- **El detalle** de un PR: las protecciones de su rama base, la cadena (enlace a «Trabajo»), los commits originales y los
  resultantes tras la fusión, y sus acciones. Cada fila se nombra por su texto ante el lector de pantalla.
- **Acciones** (cada una solo si su capacidad está declarada y el estado la admite): «Actualizar el PR», «Publicar el
  veredicto», «Pedir revisión…», «Fusionar…» (con los modos permitidos), «Cerrar sin fusionar…», «Borrar la rama
  remota…», «Abrir en el navegador», «Dejar de seguir», «Reabrir el hito…» (si hay un revert o un check que falla).
- **«Permisos de publicación»** (plegable): las autorizaciones persistentes de este proyecto, con quién y cuándo, «Dar
  permiso…» y «Retirar».
- **Ficheros:** `src/AgentStudio.Desktop/Repository/PullRequests/**` y `Repository/Policies/**` (nuevos),
  `Views/Repository/PullRequests/**` (nuevo); `RepositoryViewModel.cs`, `RepositoryRows.cs` (`RepositorySection.PullRequests`,
  al final) y `RepositoryView.xaml` (existen).
- **Ayuda:** el artículo `repository` se amplía y nace `publishing` (publicar, permisos, veredicto en el PR).

### 7.2 La tarjeta de publicación y la de fusión

- **«Publicar y abrir PR»** abre una tarjeta con: adónde va («`propietario/repositorio`, rama `agentstudio/run-…`, hacia
  `main`»), qué sube (D-08), las protecciones de la rama base, el título y el cuerpo (editables), «Abrir como borrador» y
  qué lo ampara («Con tu permiso del 12/11» o «Te pedirá confirmación con Windows Hello»).
- **«Fusionar…»:** el modo, el commit que se fusiona («el que juzgó el juez»), las protecciones, los checks y las
  revisiones tal como están, y el aviso fijo: «Esto cambia la rama `main` de GitHub. Se pide Windows Hello cada vez».
- **Dónde:** junto a «Entregar» en el monitor de la ejecución, y en «Resumen» y «PR» de «Repositorio y cambios».

### 7.3 «Ramas» y «Actividad» (amplían)

- **Ramas:** cada rama de ejecución dice si está publicada, en qué commit, o si se borró en el remoto. «Delante y detrás»,
  solo si los commits están en tu equipo (D-03).
- **Actividad:** las publicaciones, las escrituras en el PR, las fusiones pedidas y lo observado (fusionado, cerrado,
  force-push, rama borrada, checks, posible reversión, despliegue), cada una con su resultado real o su error; y, arriba, lo
  pendiente de sincronizar con su motivo.

### 7.4 «Trabajo»: tablero, cadena e hito

- **Tablero:** «Integrado» también por PR fusionado (D-29). Una tarjeta en «Hecho» con el motivo «Fusionado con cambios que
  el juez no vio» lo dice en su ⓘ.
- **Cadena:** después de «Entrega» o en su lugar, «PR #12» → «Fusionado (squash) en `a1b2c3d`» → «Desplegado», y «Revertido
  por …» si se observó.
- **Hito:** junto a implementado y revisado, **«integrado»** (por entrega o por PR) y **«desplegado»**, este solo si hay
  dato. Sin dato, no aparece.
- **Ficheros:** `src/AgentStudio.Desktop/Work/{BoardSection,ChainSection,MilestonesSection}.cs` y sus vistas (existen).

### 7.5 Ajustes

- Una sección «Git remoto»: el intervalo del sondeo, la lista de proyectos con la sincronización permitida y un texto que
  dice qué sale y qué no. El permiso se da en la sección «PR» de cada proyecto.
- **Ficheros:** `src/AgentStudio.Desktop/Settings/**` y `Services/AppSettings.cs` (existen; los toca el lead).

### 7.6 Auto Doctor

Las líneas de D-31, en sus secciones «Herramientas» y «Proyecto». Sin pantalla nueva.

### 7.7 Textos y sus ⓘ

Siguen la guía de textos de ADR-0054 (punto 9): frases de 20 palabras como mucho, una idea por frase, de tú; botones con
verbo y objeto; un error dice qué pasó, por qué y qué hacer. Y su glosario: «versión guardada (commit)» la primera vez,
«revisor independiente (juez)», «prueba guardada», «huella», «pasar a tu rama». Dos términos nuevos para el glosario, que
el P0 propone y WP5 fija: **«petición de fusión (PR)»** y **«publicar»** (subir la rama a GitHub).

Cada apartado con ⓘ lleva sus tres textos (`<InfoKey>_Title`, `_What` y `_Actions`); la tabla da el texto visible y el
«qué es esto». La versión inglesa va en el `.en.resx`. Como referencia para el P0 y WP5:

| Clave | Texto | ⓘ («Qué es esto») |
|---|---|---|
| `Hosting_Publish_Button` | Publicar y abrir la petición de fusión | Sube a GitHub la rama de esta ejecución y abre una petición de fusión (PR). Tu rama no cambia. |
| `Hosting_Publish_Uploads` | Se publican {0} versiones guardadas (commits): {1} de Agent Studio y {2} tuyas. | La rama de la ejecución sale de la tuya. Lo que tenías sin publicar sube con ella. |
| `Hosting_Publish_NotReady` | Esta ejecución aún no se puede publicar. Motivo: {0} | Solo se publica el trabajo que aprobó un revisor independiente (juez), igual que al pasarlo a tu rama. |
| `Hosting_Publish_Busy` | El proyecto está ocupado. Podrás publicar cuando termine lo que está en marcha. | Agent Studio no toca tu repositorio mientras trabaja una ejecución, un debate o una auditoría. |
| `Hosting_Verdict_NotApproval` | El resultado de la revisión se publica como un estado y un comentario. No es una aprobación tuya. | Agent Studio nunca aprueba una petición de fusión con tu cuenta. Aprobar sigue siendo cosa de una persona. |
| `Hosting_Head_Moved` | La petición de fusión cambió después de la revisión. | Alguien subió cambios que el revisor independiente no vio. Si se fusionan, este trabajo no contará como integrado. |
| `Hosting_Merge_Hello` | Fusionar cambia la rama {0} de GitHub. Se pide Windows Hello cada vez. | Fusionar y borrar en GitHub no admiten un permiso guardado. |
| `Hosting_Rules_Unreadable` | No se pudieron leer las protecciones de {0}. GitHub las aplicará igualmente. | Tu cuenta no tiene permiso para verlas, o GitHub no las enseña. |
| `Hosting_Pending_Offline` | Pendiente de sincronizar: no hay conexión. Se volverá a intentar a las {0}. | Nada se da por hecho hasta que GitHub lo confirma. |
| `Hosting_SignIn` | No hay sesión de GitHub. Abre una terminal y escribe: gh auth login | Agent Studio usa la sesión del programa `gh`. Nunca ve ni guarda tu clave de acceso. |
| `Hosting_Credentials` | Git no pudo identificarse en GitHub. Comprueba tu gestor de credenciales. | El envío lo hace git con tu gestor de credenciales. Agent Studio no ve la clave. |
| `Hosting_RateLimited` | GitHub pide esperar hasta las {0}. | Es el límite de uso de GitHub para tu cuenta. Agent Studio no insiste. |
| `Hosting_Config_Refused` | No se puede publicar: el repositorio define {0} en {1}. Quita esa línea o publica con tu programa de git. | La configuración del repositorio no puede decidir adónde se envía ni cómo te identificas. |
| `Hosting_Lfs_Refused` | Este proyecto usa Git LFS y Agent Studio no sube esos ficheros. Publica la rama con tu programa de git. | Subirlos exige ejecutar un programa del repositorio durante el envío. |
| `Hosting_Sync_Permission` | Sincronizar con GitHub | Cada pocos minutos pregunta a GitHub por tus peticiones de fusión. Sale el nombre del repositorio y sus números; nada más del proyecto. |
| `Hosting_Deployed` | Desplegado | Lo dice GitHub. Agent Studio no despliega nada. |
| `Hosting_Revert_Suspected` | Posible reversión, por el mensaje de una versión guardada | Es una deducción. Si es cierta, puedes reabrir el hito. |

- Los nombres de la pantalla siguen la misma regla: la sección se llama «Peticiones de fusión (PR)» y, en lo que sigue de
  este documento, «PR» es la abreviatura de trabajo.
- Lo que se publica **en GitHub** (el cuerpo del PR, el comentario y la descripción del estado) también sale de recursos,
  en el idioma de la app, y sigue la guía.
---

## 8. Seguridad frente a ADR-0018

El modelo de amenazas es el de §3. Aquí, superficie por superficie, cómo se cierra y qué prueba lo fija.

| Superficie nueva | Riesgo | Cómo se cierra | Prueba |
|---|---|---|---|
| `git push` con tus credenciales | Que la configuración del repositorio elija el destino o reciba el token | Destino construido (D-04), inspección por ámbito y origen (D-05), claves fijadas (D-06) | F10-3 |
| Hooks durante el `push` | Código del repositorio con tu sesión | `core.hooksPath` al fichero vacío y `--no-verify` | F10-3 |
| Forzar o borrar con `push` | Perder historia del remoto | Cinco cierres (D-07) | F10-3 |
| El entorno de `git` y de `gh` | Redirigir, cambiar de cuenta, volcar el tráfico | Lista blanca (D-06 y D-11) | F10-11 |
| Argumentos de `gh` | Inyección por una rama, un título o un nombre | Catálogo cerrado, ruta construida y comprobada, cuerpo por la entrada estándar (D-10) | F10-12 |
| El token | Que Agent Studio lo lea, lo pase o lo enseñe | Ninguna orden `gh auth`; sin `GH_TOKEN`; sin bóveda; error estándar sin guardar (D-11 a D-13) | F10-11 |
| La salida de `gh` | Datos manipulados o enormes; texto que pasa por hecho | Esquema estricto, topes, campos por lista; el texto del remoto, fuera del registro (D-12) | F10-2 |
| Redirecciones | Credenciales hacia otro host | `http.followRedirects=false`, `--hostname` fijo, ninguna dirección de una respuesta se sigue | F10-3 y F10-2 |
| El cuerpo del PR | Menciones, cierres de incidencias, imágenes remotas | Plantilla del sistema, datos en tramos de código, sin texto de agentes (D-19) | F10-2 |
| El veredicto en el PR | Que parezca una aprobación tuya | Sin miembro, sin entrada del catálogo y con prueba de que no hay revisión (D-21) | F10-5 |
| Fusionar | Fusionar algo que el juez no vio; saltarse una protección | `sha` atado, aprobación atada y Hello; sin administrador (D-17 y D-20) | F10-4 |
| Una fusión hecha fuera | Que cuente como integrado lo no juzgado | «Integrado» exige cabeza = commit juzgado (D-29) | F10-8 |
| La red en segundo plano | Tráfico sin que lo sepas | Permiso por proyecto, apagado; solo lecturas; Auto Doctor sin red (D-02) | F10-13 |
| El registro durante una ejecución | Mover su cabeza entre enseñar y usar un veredicto | El buzón entra en el proyecto o espera (D-25) | F10-6 |
| Eventos repetidos o viejos | Duplicar o deshacer estado | Instantánea con huella; lo viejo se descarta (D-23) | F10-6 |
| Los ficheros locales del buzón | Que alguien los edite para colar una acción | Lo pendiente vuelve a pasar la compuerta entera antes de ejecutarse (D-26); la caché se lee como no fiable | F10-6 |

**Residuales que se aceptan y se escriben en los ADR:**

- Tu configuración global y de sistema de git, tu `gh` y tu gestor de credenciales son de confianza: un programa de tu
  usuario que los cambie engaña a esta fase (ya era el residual del proceso concurrente y del PATH de ADR-0018).
- `git push` arranca `git` con la carpeta de trabajo en el repositorio, y el gestor de credenciales corre como un
  proceso hijo suyo. No se puede publicar de otra forma.
- GitHub ve tu dirección IP, tu cuenta y qué PR consultas.
- Todo lo que Agent Studio publica sale **con tu cuenta:** quien lea el PR ve tu nombre junto al comentario del veredicto.
  El texto dice que lo publica Agent Studio, pero la autoría en GitHub es tuya.
- Entre leer la cabeza de una rama remota y borrarla hay una ventana mínima (D-20).
- El modo de una fusión hecha fuera y una reversión son deducciones, y se enseñan como tales (D-27).
- Un PR con más de 250 commits se une «truncado» (D-28).

**Lo que no cambia:** la capa git interna sigue sin red ni credenciales; ningún CLI de proveedor de IA recibe un prompt en
esta fase; el juez no recibe nada del remoto; ningún agente lanza `git push` ni `gh` (lo hace el sistema, tras tu clic).

---

## 9. Criterios de aceptación y cómo se verifican

F10-1 a F10-10 son los de [08](08-plan-y-criterios-de-aceptacion.md#fase-10--git-remoto-publicación-pr-y-sincronización),
sin cambiar su texto. Aquí se concreta la prueba de cada uno.

| ID | Cómo se verifica, en concreto | Dónde |
|---|---|---|
| F10-1 | Pruebas de ViewModel con un proveedor de prueba: sin `Merge`, no hay orden «Fusionar»; con solo `Squash`, el selector tiene un modo; sin `ReadDeployments`, no hay columna «Despliegue»; sin `CommitStatus`, el veredicto sale solo como comentario; versión no probada, aviso. Y del dominio: `NarrowedBy` nunca añade capacidades | `tests/.../Domain/Hosting/**`, `tests/.../Desktop/Repository/PullRequests/**` |
| F10-2 | El adaptador contra respuestas grabadas (abajo) con un `IProcessRunner` que registra cada `ProcessSpec`: repositorio y reglas de rama; buscar, abrir y actualizar un PR; pedir revisores; leer revisiones, checks y Actions; fusionar con cada modo, y una fusión rechazada (405, 409 y 422). Cada caso comprueba la orden lanzada (ruta, método, cuerpo por la entrada estándar) y lo que se interpreta. Respuestas hostiles: sin línea de estado, JSON profundo, campo de otro tipo, 2 MB, un `login` con espacios, un título con secuencias de terminal | `tests/.../Infrastructure/Hosting/**` |
| F10-3 | Un remoto local (carpeta bare) y repositorios temporales: publica y `ls-remote` confirma; la segunda vez, «ya al día»; un remoto que avanzó por otro lado → rechazado, sin forzar; **configuraciones plantadas**, una prueba por familia de D-05 (`credential.helper` local con un programa centinela, `url.*.insteadOf`, `pushInsteadOf`, `remote.origin.pushurl`, `core.sshCommand`, `core.fsmonitor`, `http.proxy`, `http.extraHeader`, `push.followTags`, un `includeIf` global hacia un fichero del repositorio): no se publica, el centinela no corre y el motivo nombra la clave; un hook `pre-push` plantado no corre; variables `GIT_SSH_COMMAND`, `GIT_ASKPASS`, `GIT_CONFIG_COUNT` y `GIT_TRACE` heredadas no llegan al proceso; el repositorio del usuario queda igual tras publicar (`UserRepositoryState.Compare`); la prueba de D-07 sobre todos los `ProcessSpec`; LFS → rechazado | `tests/.../Infrastructure/Git/Remote/**` (`Category=LocalTools`) |
| F10-4 | La compuerta, con un registro en memoria: sin autorización pide aprobación, y con la respuesta atada a otra cosa no actúa; con autorización persistente no pide; una autorización de otro repositorio o de otra rama base no cubre; fusionar y lo destructivo piden siempre, y `GrantAsync` de esas clases se rechaza; con el veredicto impugnado se rechaza publicar la rama, abrir el PR y fusionar, **y se permite publicar el veredicto** (que sale «en revisión»); retirar la autorización vuelve a pedir; cada uso deja su entrada; las reglas de la rama llegan a la tarjeta, y «no se pudieron leer» también. Las seis acciones están en `KnownPresenceActions` como `RemoteOrSecretOperation` | `tests/.../Orchestration/Publication/**`, `tests/.../Security/Approvals/**` |
| F10-5 | El adaptador publica el veredicto en sus tres estados: la orden es `POST …/statuses/{sha}` y `POST` o `PATCH` de un comentario; el comentario lleva la marca y no lleva texto del juez. **Ninguna llamada de revisión:** la prueba de D-21 sobre todos los `ProcessSpec` de la batería (ni `pr review`, ni `--approve`, ni una escritura en `/reviews`, ni `APPROVE`, ni `"event"`), y la de arquitectura | `tests/.../Infrastructure/Hosting/GitHub/VerdictPublicationTests.cs` |
| F10-6 | `RemoteReconciler`, puro: la misma instantánea dos veces → un cambio; una secuencia desordenada (fusionado y después una respuesta vieja «abierto») → no vuelve atrás; una prueba de propiedad con permutaciones y repeticiones da siempre el mismo conjunto de observaciones. El buzón, con un reloj y un proveedor de prueba: 304 en todos los recursos → ninguna entrada; **304 en el PR y un cambio en los checks, en la rama base (un revert) o en los despliegues → se observa igual**; 429 con `retry-after` → nada sale hasta esa hora; 403 con el límite a cero y `x-ratelimit-reset`; sin cabeceras → un minuto y progresiva; sin conexión y sesión caducada → «pendiente de sincronizar» y ninguna entrada. **Proyecto ocupado:** con una ejecución, un debate o una auditoría dentro (`TryEnter` de cada clase), el buzón no lanza **ningún** `git` (ni `push`, ni `ls-remote`, ni la inspección, ni una lectura de historia) ni escribe en el registro (un lanzador y un registro que cuentan); la tarjeta de publicación y las dos líneas de Auto Doctor dicen «ocupado» sin lanzar nada; sí lee por el proveedor, y al quedar libre registra una sola vez. Y al revés: mientras el buzón lo tiene, `TryEnter` de una ejecución da nulo, y lo suelta al acabar cada mensaje | `tests/.../Domain/Hosting/**`, `tests/.../Orchestration/Publication/**` |
| F10-7 | Integración: una ejecución guionizada con dos tareas y veredicto aprobado; se publica a un remoto local; un ayudante de la prueba hace en el remoto un **squash**, un **rebase** y una **fusión con commit** (tres casos) y después un **revert**; el proveedor de prueba responde con esos commits. La cadena, desde un commit original, llega al commit resultante y vuelve; tras el revert, lo dice; los commits originales y sus evidencias siguen en el registro, y `Verify` pasa | `tests/.../Integration/Phase10/**` (`Category=LocalTools`) |
| F10-8 | ViewModel de «PR»: cada estado de la lista, lo pendiente, «la cabeza cambió». Tablero: PR fusionado con la cabeza juzgada → «Integrado»; con otra cabeza → «Hecho» con su motivo; prueba de propiedad: ninguna tarjeta en «Integrado» sin veredicto aprobado. Hito: «integrado» por PR; «desplegado» solo con la observación, y sin ella el estado no existe en el ViewModel | `tests/.../Desktop/{Repository,Work}/**`, `tests/.../Orchestration/Work/**` |
| F10-9 | Fuera de esta entrega (decisión 1). Lo que sí se prueba: un segundo proveedor de prueba con votos numéricos y sin estado de commit pasa por las mismas pantallas sin tocar su código | `tests/.../Desktop/Repository/PullRequests/**` |
| F10-10 | Dos pruebas. **De comportamiento:** un `IProcessRunner` que registra, bajo toda la batería de la capa interna (`GitWorktreeManager`, los lectores de historia y de remotos): cada orden lleva `GIT_ALLOW_PROTOCOL=-`, `credential.helper=`, `core.askPass=` y `core.sshCommand=`. **De arquitectura:** ningún fichero de `Infrastructure/Git/` fuera de `Remote/` nombra `push`, `fetch`, `pull`, `clone` ni `ls-remote` como subcomando, y `Remote/` no usa `GitCommandRunner` | `tests/.../Infrastructure/Git/InternalLayerHasNoNetworkTests.cs` |

**Criterios que se añaden** (decisión 10), porque F10-2 y F10-6 no dicen nada del token, de la inyección ni del permiso de
red, y esta fase gira en torno a ellos. No cambian el sentido de ninguno anterior.

| ID | Criterio de aceptación | Cómo se verifica |
|---|---|---|
| F10-11 | Agent Studio nunca lee, pasa ni enseña una credencial: ninguna orden `gh auth`, ningún `--show-token`, ninguna variable de token en el entorno de `gh` o de `git`, y nada de la salida de error en el registro | Con `GH_TOKEN`, `GITHUB_TOKEN`, `GH_ENTERPRISE_TOKEN`, `GH_HOST`, `GH_REPO`, `GH_CONFIG_DIR`, `GH_DEBUG`, `GIT_ASKPASS` y `GCM_TRACE` puestos en el proceso de la prueba, ningún `ProcessSpec` de las dos baterías los lleva. Ningún `ProcessSpec` contiene `auth`, `token` ni `--show-token` como argumento. Un token centinela escrito en el error estándar de un `gh` de prueba no aparece en el registro, la bitácora, los ficheros locales ni los mensajes. Prueba de arquitectura: el código de la fase no nombra `ICredentialVault`, `hosts.yml` ni la carpeta de configuración de `gh` |
| F10-12 | Ningún texto no fiable es un argumento ni cambia el punto de la API | **Prueba adversaria:** ramas, títulos, cuerpos, cuentas y remotos con `-`, `--admin`, `--upload-pack=x`, `@fichero`, `..`, `%2e%2e`, `?`, `#`, espacios, comillas, `&`, una barra vertical, saltos de línea y caracteres de formato. O se rechazan antes de lanzar nada, o viajan solo en el JSON de la entrada estándar; los argumentos lanzados son los mismos que con datos inocuos, con dos únicas excepciones, las dos con patrón estricto: la ruta validada y el valor de `If-None-Match`. Un `ETag` hostil (con un salto de línea, con otra cabecera detrás, sin comillas) se descarta y la petición sale sin condición |
| F10-13 | Sin tu permiso no hay red en segundo plano, y Auto Doctor no la usa nunca | Con el permiso apagado y un reloj que avanza un día, el lanzador no recibe ningún `gh` con `api`; Auto Doctor, con todas sus fuentes, de `gh` solo lanza `gh --version`; con el permiso dado, el sondeo solo lanza métodos `GET` |

**Las respuestas grabadas, dicho sin rodeos.** Las de `gh api` de esta fase se **escriben a mano** a partir de la
documentación oficial de la API REST de GitHub y de la ayuda de `gh` 2.96.0, con la forma que da `-i` (línea de estado,
cabeceras, línea en blanco y cuerpo). Viven en `tests/AgentStudio.Tests/Infrastructure/Hosting/GitHub/Fixtures/gh-2.96.0/`,
con el prefijo `handmade-` y un `README.md` que lo dice. **No son capturas reales:** lo serán cuando hagas la prueba en vivo
(decisión 14), y entonces se sustituyen por capturas con el prefijo `real-`, revisadas para que no lleven nada tuyo.

**Las pruebas en vivo quedan pendientes de tu OK** (decisión 14): un repositorio **privado de prueba que creas tú,** y una
lista cerrada de pasos: publicar, abrir, publicar el veredicto, leer checks, fusionar con squash, observar, cerrar otro PR
y borrar su rama. Van con `Category=HostingLive`, fuera de la suite y de la CI.

**Reglas de todas las pruebas:**

- solo `Assert` de xUnit; `using System.IO;` donde haga falta;
- **nunca se crea `App`;** las de ViewModel usan los dobles de siempre;
- **nunca red ni CLIs reales en la suite:** ni `gh` ni un remoto de verdad. El `git` real, solo contra remotos locales y
  con `Category=LocalTools`;
- las pruebas del ejecutor remoto usan `RemoteGitOptions.AllowedTransport = File`; una prueba de la composición exige
  `Https` en la app;
- antes de un PR, la pasada completa incluye `LocalTools` y `ConPty`, que la CI deja fuera.

---

## 10. Paquetes de trabajo

### 10.1 Reglas comunes

Las de [20 §9.1](20-fase-6-auto-doctor-y-auto-graph.md#91-reglas-comunes):

- el P0 va en serie y se fusiona antes de abrir los paquetes;
- los paquetes van en worktrees, **dos a la vez** (tres solo si el equipo aguanta: se comprueban CPU y memoria antes), sin
  ningún fichero en común; a cada implementador se le dan rutas absolutas de **su** worktree;
- integra el lead.

**Dueños únicos:**

- **El P0:** los enums compartidos, los contratos de §5, `DecisionPayloadSerializer.cs`, `ApprovalRiskClassifier.cs`,
  `DoctorToolCatalog.cs`, `Help/HelpScreens.cs` y `tests/.../UiInventory/UiScreens.cs`.
- **El lead, en la integración:** `src/AgentStudio.Desktop/Composition/**`, `MainViewModel*.cs`, `MainWindow.xaml(.cs)`,
  `Settings/**`, `Services/AppSettings.cs`, `Orchestrator/**` (el botón junto a «Entregar»), `Background/**` y `Lock/**`
  (el nombre de la actividad nueva), `docs/03`, `docs/08`, `docs/18`, `docs/README.md` y la bitácora.

**«Hecho»** exige `dotnet build AgentStudio.slnx` con 0 avisos, `dotnet test` en verde (sin `ProviderLive` ni
`HostingLive`), las categorías `LocalTools` y `ConPty` pasadas a mano, los ADR y documentos del paquete, su artículo de
ayuda, su pantalla en el inventario y nada simulado en la interfaz.

### 10.2 P0 · Contratos, dominio, payloads y altas (M; camino crítico)

- **Dominio:** `src/AgentStudio.Domain/Hosting/**` (nuevo): los tipos de §5.1 con su validación, `HostingPolicy`,
  `RemoteReconciler` y `CommitSuccession` **como firmas con su lógica mínima** (la completa es de WP3 y WP4), y
  `TraceNodeKind.PullRequest` con su regla de id en `Domain/Tracing/TraceRef.cs`.
- **Application:** `src/AgentStudio.Application/Hosting/**` (nuevo, §5.2) y `Decisions/HostingPayloads.cs` (nuevo, §5.3);
  los enums que crecen (tabla de §5.1); `ParticipationSources` y `CommitAnnotation`.
- **Registro:** `Domain/Decisions/DecisionLog.cs` (cuatro valores al final) y
  `Application/Decisions/DecisionPayloadSerializer.cs`.
- **Seguridad:** `src/AgentStudio.Security/Approvals/ApprovalRiskClassifier.cs` (las seis acciones).
- **Auto Doctor:** `DoctorTool.GhVersion` y su entrada en `Infrastructure/Diagnostics/DoctorToolCatalog.cs`.
- **Lo mínimo para que cada paquete llegue a verde solo:**
  - `ProjectActivityKind.RemoteOperation` en todos los sitios que lo recorren, que son seis ficheros (búsqueda completa
    de `ProjectActivityKind` en `src/`): `Orchestration/Engine/Orchestrator.cs` (`OrchestrationActivity.TryAdd` y
    `Remove`: sin su rama, `TryEnter` daría siempre nulo), `Desktop/Background/{BackgroundWork,BackgroundWorkInProgress}.cs`,
    `Desktop/Lock/{LockScreenViewModel,LockServices}.cs` y `Desktop/Panels/HeadTestsService.cs` (su texto de «ocupado»).
    Con un texto provisional en recursos; el lead lo deja fino. `DebateCoordinator` y `ProjectAuditService` solo llaman
    a `TryEnter` con su clase y no cambian;
  - el par `HostingStrings.resx` / `.en.resx` en `Desktop/Resources/`, con las claves de §7.7, y en
    `DoctorStrings*.resx` (existen) los textos de las ocho líneas de D-31;
  - el código `publishing` en `Help/HelpScreens.cs` con la primera versión de su artículo (`Help/{es-ES,en}/publishing.md`);
  - `tests/AgentStudio.Tests/Desktop/UiInventory/UiScreensHosting.cs` con su lista vacía, ya sumada en `UiScreens.cs`;
  - `tests/AgentStudio.Tests/AgentStudio.Tests.csproj`: las carpetas `Fixtures` de la fase, como contenido.
- **Pruebas:** `tests/AgentStudio.Tests/Domain/Hosting/**` (validación adversaria de `RemoteRepository`, `RemoteBranch` y
  `RemoteLogin`; `HostingPolicy`), `Application/Phase10PayloadTests.cs`, `DecisionKindCompatibilityTests`,
  `DecisionPayloadTests`, `ContractSurfaceTests` y `CriticalApprovalCoverageTests` al día.
- **Documentos:** [04](04-contratos.md) §26, y en los ADR 0064 a 0068 la nota de lo que el P0 fija.
- **Primer paso, una comprobación:** `gh api --help` y `git push -h` en el equipo de quien lo haga, contra lo que dice
  este documento (§13). Si algo no cuadra, vuelve a ti antes de escribir contratos.
- **Hecho cuando:** compila con 0 avisos; todos los contratos de §5 existen y `ContractSurfaceTests` los cubre; las
  pruebas de compatibilidad del registro pasan; ninguna implementación de red existe todavía.

### 10.3 WP1 · Ejecutor remoto de git (M-L)

- `src/AgentStudio.Infrastructure/Git/Remote/**` (nuevo): lo de §5.4, y `AddAgentStudioRemoteGit` en esa carpeta.
- Pruebas: `tests/AgentStudio.Tests/Infrastructure/Git/Remote/**` y
  `tests/AgentStudio.Tests/Infrastructure/Git/InternalLayerHasNoNetworkTests.cs` (nuevos).
- No toca ningún fichero existente de `Infrastructure/Git/`. Si necesita el lector de `--show-scope` de `GitCallbacks`,
  lo usa tal cual (es `internal` del mismo ensamblado); si hay que tocarlo, vuelve al lead.
- Criterios: F10-3, F10-10 y la mitad de F10-11 y F10-12. ADR-0064 (adenda con lo medido).
- **Hecho cuando:** las pruebas de F10-3 y F10-10 de §9 pasan con `LocalTools`; la medición de que publicar a una dirección
  deja el repositorio del usuario igual está escrita en el ADR; cada punto de §13 que le toca queda «medido» o «sigue sin
  verificar», con su motivo.

### 10.4 WP2 · GitHub por `gh` (M-L)

- `src/AgentStudio.Infrastructure/Hosting/{Cli,GitHub}/**` (nuevo): lo de §5.4, `HostingProviders` (identifica el remoto
  y da el proveedor) y `AddAgentStudioGitHubHosting`.
- `src/AgentStudio.Infrastructure/Hosting/Diagnostics/HostingCheckSource.cs` (nuevo): las comprobaciones de D-31, como un
  `IDoctorCheckSource` más. Sus textos ya están en `DoctorStrings*.resx`: los puso el P0, para que este paquete no
  comparta ese fichero con nadie.
- Pruebas: `tests/AgentStudio.Tests/Infrastructure/Hosting/**` con `Fixtures/gh-2.96.0/` y su `README.md`.
- Criterios: F10-2, F10-5 (el adaptador), F10-1 (las capacidades del adaptador) y la otra mitad de F10-11 y F10-12.
  ADR-0065 y ADR-0066.
- **Hecho cuando:** las pruebas de F10-2, F10-5, F10-11 y F10-12 de §9 pasan sin lanzar `gh`; el `README.md` de las
  respuestas grabadas dice que son a mano y de qué página sale cada una; Auto Doctor enseña las líneas nuevas con un `gh` de prueba.

### 10.5 WP3 · Compuerta, publicación, buzón y reconciliador (L)

- `src/AgentStudio.Orchestration/Publication/**` (nuevo): `HostingPolicyGate`, `PublicationService`,
  `ProjectRemoteMailbox`, `RemoteSyncService` (el sondeo y su reloj), `PullRequestTextComposer` (D-19),
  `VerdictNoticeBuilder` (sobre `VerdictReach`) y `PullRequestProjector`.
- `src/AgentStudio.Domain/Hosting/RemoteReconciler.cs` (lo completa).
- `src/AgentStudio.Infrastructure/Hosting/State/**` (nuevo): `RemoteSyncStore`.
- `AddAgentStudioPublication`, en su carpeta.
- Pruebas: `tests/AgentStudio.Tests/Orchestration/Publication/**`, `Domain/Hosting/RemoteReconcilerTests.cs` e
  `Infrastructure/Hosting/State/**`.
- Usa `IRemoteGitRunner` e `IHostingProvider` por sus puertos, con dobles: no espera a WP1 ni a WP2 para sus pruebas.
- Criterios: F10-4, F10-6, F10-13 y la parte de servicio de F10-5. ADR-0067 y ADR-0068.
- **Hecho cuando:** las pruebas de F10-4, F10-6 y F10-13 de §9 pasan; la prueba del proyecto ocupado falla si se quita la
  toma del proyecto; ninguna clase de la carpeta lanza un proceso.

### 10.6 WP4 · Proyecciones: cadena, tablero, participación e hito (M)

- `src/AgentStudio.Orchestration/Work/{TraceChainReader,BoardProjector,ParticipationProjector}.cs` (existen) y lo que
  haga falta junto a ellos para leer las observaciones.
- `src/AgentStudio.Domain/Hosting/CommitSuccession.cs` (lo completa).
- `src/AgentStudio.Debate/Audit/MilestoneReport.cs` y `MilestoneCloser.cs` (existen): «integrado» por PR, «desplegado» y la
  desviación «fusionado con cambios que el juez no vio».
- Los lectores de «Repositorio y cambios» que anotan y listan la actividad (los que implementan `IRepositoryAnnotator` e
  `IRepositoryActivityReader`; existen).
- Pruebas: `tests/AgentStudio.Tests/Orchestration/Work/**`, `Domain/Hosting/CommitSuccessionTests.cs` y
  `Debate/Audit/Milestones/**`.
- Solo depende del P0 (lee payloads): puede ir en la primera tanda si hay tres manos.
- Criterios: F10-7 y la parte de proyección de F10-8.
- **Hecho cuando:** las pruebas de proyección de F10-7 y F10-8 pasan con registros preparados; la prueba de propiedad del
  tablero sigue pasando con observaciones aleatorias; ninguna prueba de la Fase 9 cambia de resultado.

### 10.7 WP5 · Escritorio (M)

- `src/AgentStudio.Desktop/Repository/{PullRequests,Policies}/**` y `Views/Repository/PullRequests/**` (nuevos);
  `Repository/{RepositoryViewModel,RepositoryRows,RepositoryTexts}.cs` y `Views/Repository/RepositoryView.xaml` (existen).
- `src/AgentStudio.Desktop/Work/{BoardSection,ChainSection,MilestonesSection}.cs` y sus vistas (existen).
- `Resources/HostingStrings*.resx` (los completa; nadie más los toca tras el P0) y
  `Help/{es-ES,en}/{publishing,repository}.md`.
- Pruebas: `tests/AgentStudio.Tests/Desktop/Repository/**`, `Desktop/Work/**` y
  `Desktop/UiInventory/UiScreensHosting.cs`.
- Trabaja contra los puertos de §5.2 con dobles; lo real llega en la integración.
- Criterios: F10-1 y F10-8 (pantallas).
- **Hecho cuando:** las pruebas de ViewModel de F10-1 y F10-8 pasan; cada vista nueva está en el inventario; los dos
  artículos de ayuda cubren las claves ⓘ nuevas; ninguna pantalla enseña un dato que no venga de un puerto.

### 10.8 WP-AZ · Azure DevOps por `az` (M; solo con la decisión 1 (b))

- `src/AgentStudio.Infrastructure/Hosting/AzureDevOps/**` (nuevo), con sus respuestas grabadas y su prueba en vivo.
- Antes: la forma de `RemoteRepository` para organización y proyecto, y un ADR propio. No se diseña aquí.

### 10.9 Integración (el lead; M)

- La composición (`Composition/HostingServices.cs`, nuevo): los tres `AddAgentStudio…`, `IRemoteSyncPolicy` sobre
  `AppSettings`, y `RemoteGitOptions` con `Https`.
- `AppSettings` (el permiso por proyecto y el intervalo, también en `WithPreferencesOf`) y la sección «Git remoto» de
  Ajustes.
- «Publicar y abrir PR» junto a «Entregar» en el monitor de la ejecución.
- El nombre de la actividad nueva en la bandeja y en la pantalla de bloqueo.
- `tests/AgentStudio.Tests/Integration/Phase10/**`: la app compuesta con `AddAgentStudioApp`, un remoto local, git real y
  un `gh` de prueba (un `IProcessRunner` que responde con las respuestas grabadas): publicar → PR → veredicto → squash en el remoto →
  observación → tablero «Integrado» y cadena unida (F10-7); y el mismo recorrido con una ejecución en marcha (F10-6).
- Documentos: [03](03-pantallas-y-navegacion.md), [08](08-plan-y-criterios-de-aceptacion.md) (el estado, y F10-11 a
  F10-13), [18](18-peticiones-y-pendientes.md) (los pendientes nuevos, desde P70) y el README.
- `CLAUDE.md`: si eliges la decisión 6 (a), la lista de lo que se puede lanzar gana `gh --version` y las lecturas de
  `gh api` con tu permiso.
- **Falta siempre, con tu OK:** la prueba en vivo (decisión 14).

### 10.10 Orden y dependencias

| Paquete | Depende de | Tamaño | Tanda |
|---|---|---|---|
| P0 | `main` con la Fase 6 | M | 0, en serie |
| WP1 | P0 | M-L | 1 |
| WP2 | P0 | M-L | 1 |
| WP4 | P0 | M | 1 si hay tres manos; si no, 2 |
| WP3 | P0 (usa WP1 y WP2 por sus puertos) | L | 2 |
| WP5 | P0 (usa WP3 y WP4 por sus puertos) | M | 3 |
| Integración | Todos | M | 3, empezando junto a WP5 |
| WP-AZ | Todo lo anterior y la decisión 1 (b) | M | Después |

Con dos a la vez:

1. **WP1 y WP2:** los dos adaptadores, que no comparten ni una carpeta.
2. **WP3 y WP4:** el servicio y las proyecciones; el primero ya puede probarse contra los adaptadores reales.
3. **WP5,** con la integración empezando a su lado.

Ningún par de la misma tanda toca el mismo fichero: WP1 vive en `Infrastructure/Git/Remote`, WP2 en
`Infrastructure/Hosting/{Cli,GitHub,Diagnostics}`, WP3 en `Orchestration/Publication` e `Infrastructure/Hosting/State`,
WP4 en `Orchestration/Work` y `Debate/Audit`, y WP5 en `Desktop/{Repository,Work}`. Los dos ficheros de dominio que
completan WP3 y WP4 (`RemoteReconciler.cs` y `CommitSuccession.cs`) son uno de cada uno.

La fase entera es L. Los tamaños son estimaciones del diseño.

---

## 11. ADR a escribir

En `docs/adr/` el último es el 0063. Los 0041 y 0042 los reservó docs/16 para esta fase y nunca se escribieron: los
sustituyen estos, que nacen con este documento en estado «propuesta» y se aceptan con tus decisiones de §14.

| ADR | Tema | Paquete |
|---|---|---|
| [0064](adr/0064-ejecutor-remoto-de-git.md) | El ejecutor remoto de git, separado de la capa interna y sin credenciales propias: dos operaciones, destino construido, inspección por ámbito, nunca forzar (enmienda «nunca hay `push` ni red» de ADR-0012 solo para este componente; ocupa el lugar del 0041) | P0 y WP1 |
| [0065](adr/0065-github-por-gh-sin-leer-el-token.md) | GitHub por `gh` sin leer el token: `IHostingProvider` con capacidades, solo `gh api` con catálogo cerrado, entorno por lista blanca y la sesión deducida (amplía ADR-0003; ocupa parte del 0042) | P0 y WP2 |
| [0066](adr/0066-el-veredicto-en-el-pr-nunca-es-una-aprobacion.md) | El veredicto llega al PR como estado y comentario etiquetado, nunca como una aprobación; el texto del PR lo compone el sistema (amplía ADR-0006) | WP2 y WP3 |
| [0067](adr/0067-reconciliacion-por-sondeo-con-buzon.md) | Reconciliación por sondeo condicional con un buzón de un consumidor por proyecto; instantáneas con huella; sin webhooks; la unión de commits como proyección (ocupa el resto del 0042; amplía ADR-0039) | WP3 y WP4 |
| [0068](adr/0068-politicas-por-clase-de-operacion-remota.md) | Políticas por clase de operación remota: autorizaciones persistentes en el registro, fusionar y lo destructivo por uso y con Hello, y las protecciones a la vista (amplía ADR-0053) | P0 y WP3 |

---

## 12. Riesgos

| Riesgo | Mitigación |
|---|---|
| La forma real de `gh api -i` o de una respuesta difiere de las respuestas grabadas a mano | El lector decide por estructura y falla cerrado (`InvalidAnswer`, nunca un «correcto»); la prueba en vivo las sustituye por capturas reales; el primer paso del P0 comprueba la ayuda |
| `gh` cambia de versión y con ella su salida | Solo se usa `gh api`, la parte más estable; la versión de la API va fijada en una cabecera; Auto Doctor avisa de una versión no probada |
| El gestor de credenciales no tiene acceso por HTTPS (usas SSH) | Se dice con su guía; es la decisión 4 |
| Tu configuración global reescribe direcciones (`insteadOf`) | Se rechaza con su motivo; publicas con tu cliente git |
| Un proyecto con LFS no puede publicar desde aquí | Se dice antes, en Auto Doctor y en la tarjeta; decisión 16 |
| El límite de GitHub con muchos PR abiertos | Peticiones condicionales, en serie, solo lo seguido; una petición por PR sin cambios y por vuelta |
| Un 304 sí contara contra el límite | El coste sigue siendo una petición cada 5 minutos por PR; el intervalo se puede subir |
| El buzón retrasa el arranque de una ejecución | Suelta el proyecto tras cada mensaje; lo más largo es un `push` (120 s como mucho) |
| El modo de fusión deducido se equivoca | Se enseña como deducido; la unión de commits no depende de él cuando hay un solo commit resultante |
| Falsos positivos de «posible reversión» | No reabre nada sola; solo propone |
| Dos fases tocan el tablero y la cadena | WP4 no cambia `BoardRules`; las pruebas de propiedad de la Fase 9 siguen pasando |
| El comentario del veredicto se pierde o alguien lo borra | Se vuelve a publicar; se busca por identificador y, si no, por marca y autor |
| El equipo se queda sin memoria con tres agentes compilando | Dos a la vez por defecto, prioridad de build baja, CPU y memoria comprobadas antes |

---

## 13. Sin verificar en esta máquina

Lo comprobado: `gh` 2.96.0 y `git` 2.45.2 están instalados; las opciones de `gh api`, `gh pr *`, `gh auth status` y las
variables de `gh help environment` son las de su ayuda; los códigos de salida, los de `gh help exit-codes`; las opciones de
`git push`, las de `git push -h`. **Todo lo demás de este apartado es lectura de documentación o conocimiento previo, sin
medir:** no se ejecutó nada con red ni con cuenta.

- **`gh api`:**
  - la forma exacta de la salida con `-i` en Windows (versión de HTTP en la línea de estado, fin de línea, orden de las
    cabeceras);
  - **el código de salida con un 304** y con un 4xx o 5xx, y si el cuerpo se imprime en esos casos;
  - que `If-None-Match` pasado con `-H` llega tal cual y `gh` no añade su propia caché;
  - que con una ruta completa y `--hostname` **no lanza `git`** ni lee el repositorio de la carpeta de trabajo;
  - que `--input -` con `-X POST` o `PATCH` envía el JSON sin tocarlo;
  - desde qué versión existen `--input`, `-i` y el código de salida 4 (para fijar la mínima de D-14);
  - qué imprime sin conexión, y con qué código termina.
- **`gh` y el entorno:** que con `GH_PROMPT_DISABLED`, `GH_NO_UPDATE_NOTIFIER` y `GH_TELEMETRY=false` no hace ninguna
  petición más que la pedida; y que con la lista blanca de D-11 encuentra su sesión (dónde guarda el token en Windows:
  el Administrador de credenciales o su fichero; Agent Studio no lo mira, pero necesita que `gh` lo encuentre).
- **`gh auth status --json hosts`:** qué campos trae y si incluye el token sin `--show-token`. Por eso no se usa (D-13).
- **La API REST de GitHub** (las formas de D-10 son las de su documentación, no capturas):
  - los campos de `GET /repos/{o}/{r}/pulls/{n}` (`merged`, `merge_commit_sha`, `merged_by`, `mergeable_state`, `head.sha`,
    `updated_at`) y su vocabulario;
  - **qué punto de las protecciones es legible sin ser administrador:** `rules/branches/{rama}` y el campo `protection` de
    `branches/{rama}`; `branches/{rama}/protection` exige administración;
  - `issues/{n}/events` y el evento `head_ref_force_pushed`;
  - `deployments?sha=` y los estados de un despliegue;
  - el tope de 250 commits de `pulls/{n}/commits`;
  - el `PUT …/merge` con `sha`, y sus códigos de rechazo (405, 409 y 422);
  - `POST …/statuses/{sha}`: el permiso que exige, y el tope de la descripción (140 caracteres);
  - que un 304 no cuenta contra el límite principal con una petición autenticada, y los nombres de las cabeceras de
    límite; `x-poll-interval` puede no venir en estos puntos;
  - si los puntos con una rama en la ruta (`branches/{rama}`, `rules/branches/{rama}`, `git/refs/heads/{rama}`) quieren
    la barra de `agentstudio/run-…` literal o escapada;
  - el autor y el committer del commit de un squash, y si `refs/pull/N/head` conserva los commits tras borrar la rama (ya
    estaba en docs/16 §8).
- **`git push`:**
  - que a una **dirección** no escribe nada en el repositorio local (ni ramas de seguimiento ni `FETCH_HEAD`);
  - que con `core.hooksPath` en el fichero vacío y `--no-verify` no corre `pre-push`, tampoco el de git-lfs;
  - la salida de `--porcelain` y el texto del error cuando faltan credenciales;
  - que `-c http.followRedirects=false` hace fallar un repositorio renombrado en vez de seguirlo;
  - que `GIT_ALLOW_PROTOCOL=https` junto a `protocol.https.allow=always` deja pasar HTTPS y nada más;
  - que un `url.*.insteadOf` global se ve en la consulta por ámbito y que la reescritura se puede predecir en código.
- **El ayudante de credenciales:** cuál es el global de este equipo (no se leyó la configuración). Con Git Credential
  Manager: que `GCM_INTERACTIVE=never` hace que falle en vez de abrir una ventana, qué variables de entorno necesita de
  la lista blanca para encontrar su almacén, y qué imprime al fallar. Con el ayudante de `gh` (`gh auth git-credential`):
  que funciona con el entorno de D-06 y que no pide nada por pantalla.
- **Detectar Git LFS** sin ejecutar `git lfs`: qué combinación de configuración y atributos basta para decidir «este
  `push` llevaría objetos LFS».
- **`IProjectActivity`:** `ProjectActivityKind` aparece en nueve ficheros de `src/` (búsqueda completa; §10.2 lista los
  seis que hay que tocar). Sin comprobar: las pruebas que lo recorren; y **qué hace la reanudación automática de la Fase 8
  si `TryEnter` le da nulo al arrancar** (el orquestador lanza una excepción al planificar, empezar o reanudar; no espera).
  Las escrituras sin ejecución de la Fase 9 esperan preguntando `IsBusy` (`Orchestration/Work/WorkLog.cs`): si conviene
  que tomen el proyecto como aquí, es un pendiente para docs/18, no de esta fase.
- **Los tamaños de los paquetes** son estimaciones del diseño.
- **No se compiló ni se ejecutó nada** para escribir este documento: las firmas de §5 son propuestas.

---

## 14. Decisiones que necesitan a la persona

Dieciséis decisiones: de alcance, de red, de credenciales, de qué se publica con tu cuenta y de cómo leer un criterio. La
recomendada va primero, marcada.

1. **Azure DevOps (F10-9)** (D-09).
   - **(a) Recomendada:** fuera de esta entrega. Se deja la costura (`IHostingProvider` con capacidades, los modelos comunes
     de revisiones, reglas y checks) y entra más adelante como WP-AZ, con su ADR. Es coherente con tu decisión 2 (a) de
     docs/16.
   - (b) Dentro, como WP-AZ, tras la integración. Añade un paquete M y su prueba en vivo.
2. **La red del sondeo** (D-02 y D-24).
   - **(a) Recomendada:** un permiso por proyecto, apagado por defecto. Con él, sondeo cada 5 minutos (configurable) con la
     app abierta, solo lecturas. Sin él, solo «Sincronizar ahora».
   - (b) Solo a mano, siempre. F10-6 se cumple con el botón; nada sale sin un clic.
   - (c) Encendido por defecto en los proyectos con un PR abierto por Agent Studio. Más cómodo; es red sin haberlo pedido.
3. **`git fetch`** (D-03).
   - **(a) Recomendada:** no hay `fetch`. El estado del remoto se lee por `gh`. Tu repositorio no cambia nunca por
     sincronizar, y el ejecutor remoto se queda en dos operaciones.
   - (b) `fetch` de las ramas seguidas a refs propias (`refs/agentstudio/remote/*`), solo con el proyecto libre. Permite
     «delante y detrás» y anotar commits del remoto sin que los traigas tú; es otra operación con red y escribe en `.git`.
   - (c) `fetch` normal, como decía docs/16 D-24. Mueve tus ramas de seguimiento.
4. **El transporte del `push`** (D-04).
   - **(a) Recomendada:** solo HTTPS, con tu gestor de credenciales. El remoto de este repositorio ya es HTTPS.
   - (b) También SSH, con tu `ssh` y tu agente. Hay que dejar pasar `ssh.exe`, `core.sshCommand` global y `SSH_AUTH_SOCK`,
     y decidir qué se rechaza de `~/.ssh/config`. Más superficie.
5. **El gestor de credenciales, ¿puede abrir su ventana?** (D-06).
   - **(a) Recomendada:** no (`GCM_INTERACTIVE=never`). Si no hay credenciales, el envío falla con su guía y las arreglas
     tú en una terminal. Predecible y comprobable.
   - (b) Sí: si hace falta, aparece la ventana de inicio de sesión de tu gestor. Más cómodo; una ventana de otro programa
     en medio de una acción de Agent Studio, que no se puede probar en la suite.
   - Vale para Git Credential Manager. Con otro ayudante global (el de `gh`), la opción no cambia nada: no abre ventanas.
6. **Cómo se sabe si hay sesión de `gh`, y qué puede lanzarse** (D-13). Amplía la lista de sondas de `CLAUDE.md`.
   - **(a) Recomendada:** nunca `gh auth …`. La sesión se deduce de la primera petición; «Comprobar la conexión» lanza
     `GET /user` con tu clic. `CLAUDE.md` gana: `gh --version` siempre; `gh api` de la lista cerrada, con tu permiso o tu
     clic.
   - (b) `gh auth status` (nunca con `--show-token`) al pulsar «Comprobar la conexión». Da más detalle de la cuenta; es
     una orden de la familia `auth`, con una opción que enseña el token a un argumento de distancia.
7. **Las autorizaciones persistentes** (D-16).
   - **(a) Recomendada:** por proyecto y repositorio remoto, para «publicar la rama» y «escribir en el PR», sin caducidad,
     con opción de limitarla a una rama base, y retirables. Es tu decisión 3 (a) de docs/16, concretada.
   - (b) Lo mismo, pero caducan a los 30 días.
   - (c) Sin autorizaciones persistentes: todo por uso y con Hello.
8. **Cómo llega el veredicto al PR** (D-21).
   - **(a) Recomendada:** un estado del commit (`agent-studio/verdict`) y un comentario etiquetado, sin el razonamiento
     del juez.
   - (b) Solo el comentario. No aparece en la caja de checks ni se puede exigir en las protecciones.
   - (c) Solo el estado. Queda sin explicación en el PR.
9. **Qué texto lleva el PR** (D-19).
   - **(a) Recomendada:** el que compone el sistema, que puedes editar. Ningún texto de un agente.
   - (b) Además, el resumen del agente, dentro de un bloque de código. Más contexto para quien revisa; es texto de un
     agente publicado con tu cuenta.
10. **F10-11 a F10-13** (§9).
    - **(a) Recomendada:** entran en docs/08 como criterios de la Fase 10.
    - (b) Se quedan como pruebas de los paquetes, sin número.
11. **El orden** (§10.10).
    - **(a) Recomendada:** P0 y tres tandas de dos; la fase entera antes de la Fase 7.
    - (b) Primero solo publicar (WP1, WP2 y de WP3 la compuerta y el flujo), con «Sincronizar ahora» a mano; el sondeo, las
      proyecciones y «desplegado», en una segunda entrega.
12. **Clonar desde Agent Studio** (docs/16 D-25).
    - **(a) Recomendada:** fuera de esta fase: abres un repositorio que ya clonaste.
    - (b) Añadir `git clone` al ejecutor remoto, a una carpeta que elijas. Una tercera operación, con contenido no fiable
      recién llegado.
13. **A qué rama remota se puede empujar** (D-04).
    - **(a) Recomendada:** solo a `agentstudio/run-<id>`. Lo demás llega por PR.
    - (b) También a la rama que nombre tu aprobación (lo que admitía docs/16 D-24), por uso y con Hello: una entrega
      directa al remoto, sin PR.
14. **La prueba en vivo** (§9).
    - **(a) Recomendada:** al final de la integración, con tu OK, sobre un repositorio privado de prueba que creas tú, con
      la lista cerrada de pasos; sus capturas sustituyen a las respuestas escritas a mano.
    - (b) Más adelante: la fase se cierra con respuestas escritas a mano y se anota como pendiente en docs/18.
15. **Un PR fusionado con una cabeza que el juez no vio** (D-29).
    - **(a) Recomendada:** no cuenta como «integrado»: la tarjeta se queda en «hecho» con su motivo y el informe del hito
      lo da como desviación.
    - (b) Cuenta como «integrado», con un aviso. Más simple; rompe «nada integrado sin veredicto de eso mismo».
16. **Proyectos con Git LFS** (D-08).
    - **(a) Recomendada:** no se publica desde Agent Studio, y se dice por qué.
    - (b) Se publica avisando de que los objetos LFS no suben. El PR puede quedar con punteros rotos.

---

## Fuentes

- La ayuda de la CLI de GitHub 2.96.0 instalada en este equipo, consultada el 2026-11-13 sin red: `gh --version`,
  `gh api --help`, `gh pr create|view|edit|merge|checks|comment|review|list|close --help`, `gh repo view --help`,
  `gh run list --help`, `gh auth status --help`, `gh help environment` y `gh help exit-codes`.
- `git` 2.45.2: `git --version` y `git push -h`.
- GitHub Docs, «Best practices for using the REST API» (peticiones en serie, condicionales, `retry-after`), ya citada en
  [docs/16](16-ampliacion-plataforma-de-proyectos.md#fuentes):
  <https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api>. No se volvió a consultar en
  esta sesión.
- La documentación de la API REST de GitHub (pulls, commits, statuses, checks, deployments, rules) es la referencia de las
  formas de D-10; **no se consultó en línea para este documento:** WP2 la consulta al escribir las respuestas grabadas y anota la
  fecha (§13).
- El código y los documentos de este repositorio en `db344c8b` (§2).
