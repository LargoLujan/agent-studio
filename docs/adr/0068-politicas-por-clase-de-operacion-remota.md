# ADR-0068 · Políticas por clase de operación remota: autorizaciones persistentes en el registro, fusionar y lo destructivo por uso y con Windows Hello, y las protecciones a la vista

- **Estado:** aceptada · 2026-11-13, con la opción recomendada de la decisión 7 de [docs/21 §14](../21-fase-10-git-remoto.md#14-decisiones-que-necesitan-a-la-persona)
  (se redacta con la opción recomendada). Recoge D-15 a D-18 de [docs/21](../21-fase-10-git-remoto.md), las D-26 y D-27 de
  [docs/16](../16-ampliacion-plataforma-de-proyectos.md) y la decisión 3 (a) que la persona tomó allí el 2026-10-20.
  **Amplía** [ADR-0053](0053-windows-hello-como-barrera-fija.md) (la barrera fija) y sigue el patrón de la entrega de
  [ADR-0012](0012-worktrees-fuera-del-repo.md) (aprobación atada y de un solo uso). El P0 fija los tipos y las altas; WP3
  implementa la compuerta.
- **Contexto:**
  - F10-4 pide políticas por clase, persistentes donde la clase lo admite y en el registro; fusionar y lo destructivo, por
    uso y con Hello; y leer y enseñar las protecciones antes.
  - `PresenceAction.RemoteOrSecretOperation` ya está en la lista «siempre» de la barrera fija, y
    `ApprovalRiskClassifier` ya trata `push` y `merge` como críticas; pero cada acción se da de alta por su identificador
    exacto, y `CriticalApprovalCoverageTests` falla si una aprobación crítica del código no está en la lista.
  - Un fichero del proyecto no puede guardar un permiso: es contenido no fiable. Los ajustes de la app tampoco: no están
    anclados ni dicen quién ni cuándo.
- **Decisión:**
  1. **Cuatro clases** (`HostingOperationClass`), y cada operación remota es de una (`HostingPolicy.ClassOf`, puro):

     | Clase | Operaciones | Autorización |
     |---|---|---|
     | Publicar la rama | `push` de `agentstudio/run-<id>` | Persistente por proyecto si se da; si no, por uso |
     | Escribir en el PR | Abrir, actualizar, publicar el veredicto | Persistente por proyecto si se da; si no, por uso |
     | Escribir en el PR avisando a otras personas | Pedir revisores | Siempre por uso: avisa a alguien por su nombre |
     | Fusionar | Fusionar el PR con un modo permitido | Siempre por uso, con Hello |
     | Destructivas | Cerrar un PR sin fusionar; borrar la rama remota de una ejecución | Siempre por uso, con Hello |

     El commit local del sistema y la entrega local a la rama de la persona no cambian y no pasan por aquí.
  2. **Qué admite un permiso guardado lo decide el tipo, por operación:** `HostingPolicy.CanBePersistent` es falso para
     fusionar, para lo destructivo y para pedir revisores; la compuerta rechaza concederlo y el payload no se puede construir. Ningún ajuste lo cambia.
  3. **Una autorización persistente es una entrada del registro** (`HostingPolicyGranted`: clase, proveedor, host,
     propietario, repositorio, rama base opcional y quién), dada con aprobación y Hello, y se retira con otra
     (`HostingPolicyRevoked`). Su alcance es el **identificador del repositorio remoto:** si el remoto del proyecto pasa a
     ser otro, deja de aplicar. Vale para el proyecto, no caduca sola y se ve y se retira en la pantalla. Una autorización
     sin rama base cubre cualquier base; con ella, solo esa; la de «publicar la rama» nunca la lleva. **Darla y retirarla
     son anotaciones del registro:** pasan por el buzón y toman el proyecto un instante (ADR-0067); con una ejecución en
     marcha quedan aparcadas, y hasta anotarse la autorización no vale.
  4. **Una sola compuerta** (`IHostingPolicyGate`): toda operación remota pasa por ella antes de ejecutarse, y solo el
     servicio de publicación tiene los puertos del ejecutor remoto y del proveedor (una prueba de arquitectura lo vigila).
  5. **Cada uso exige, con o sin permiso guardado:** que el registro verifique; que el destino sea el de la autorización
     o la aprobación; que la configuración pase la inspección del ejecutor remoto; y que la capacidad esté declarada. **El
     veredicto se exige por operación, no por clase** (`HostingPolicy.RequiresApprovedVerdict`): publicar la rama, abrir o
     actualizar el PR, pedir revisores y fusionar exigen que la ejecución esté lista para entregar según
     `IDeliveryService.CheckAsync` y que el commit sea el juzgado; **publicar el veredicto** (estado y comentario) solo
     exige el registro verificado, porque tiene que poder decir «no aprobado» y «en revisión»; cerrar y borrar no lo
     exigen.
  6. **«Por uso» es `IApprovalGate` con una petición atada** (`ApprovalRequest.Binding`) al repositorio, el número de PR,
     el commit de cabeza, el modo y la rama base. La respuesta tiene que contestar a esa atadura y el estado se relee
     después de aprobar, como en la entrega.
  7. **Las seis acciones se dan de alta por su identificador** como `RemoteOrSecretOperation`: `hosting.policy.grant`,
     `hosting.publish`, `hosting.pr.write`, `hosting.pr.merge`, `hosting.pr.close` y `hosting.branch.delete`. Sus
     constantes viven en Application (`HostingActions`).
  8. **Un permiso guardado ahorra la aprobación, no el clic.** Publicar empieza siempre con una acción de la persona, y la
     tarjeta de publicación (destino, commits que suben, protecciones, texto) sale siempre.
  9. **Las protecciones se leen y se enseñan antes** de publicar y de fusionar: revisiones y checks requeridos, firmas,
     historia lineal, cola de fusión y los modos de fusión del repositorio. Si no se pueden leer, se dice («GitHub las
     aplicará igualmente»); nunca se enseña «sin protecciones» por no haberlas podido leer.
  10. **Nunca se salta una protección:** el adaptador no tiene forma de pedir privilegios de administrador. Si la
      plataforma rechaza la fusión, eso es el resultado, con el estado que dio.
  11. **Los commits del sistema no van firmados** (ADR-0012). Si la rama exige firmas, se dice antes y el camino es la
      fusión con squash en la plataforma, que firma su propio commit.
  12. **Cada uso deja su entrada** (`HostingOperationPerformed`) con su resultado real, lo que lo amparó (la autorización
      o la aprobación) y la cuenta que publicó.
- **Alternativas descartadas:** fusionar con un permiso guardado cuando los checks requeridos están en verde (la opción
  (c) de la decisión 3 de docs/16, que no se eligió); guardar los permisos en `AppSettings` o en `.agentstudio/` (sin ancla,
  o contenido no fiable); un ajuste que apague el Hello de fusionar (barrera fija); clasificar por las palabras de la acción
  (la lista nombra cada acción por su identificador: una palabra nueva no saca una acción de la lista).
- **Consecuencias:** `DecisionKind` crece en tres valores por esta decisión (`HostingPolicyGranted`,
  `HostingPolicyRevoked` y `HostingOperationPerformed`), al final del enum; `PresenceAction` no crece.
- **Residuales aceptados:** entre releer el estado y actuar hay una ventana mínima en la plataforma (para fusionar la
  cierra el commit de cabeza esperado; para borrar una rama, no hay condición en la API); la plataforma impone sus reglas
  aunque Agent Studio no haya podido leerlas.

## Lo que fija el P0 (2026-11-13)

- **`HostingActions`** (Application) con los seis identificadores de aprobación; `ApprovalRiskClassifier` los lista todos como `RemoteOrSecretOperation` («siempre»), con cualquier riesgo que declare quien pide, y `CriticalApprovalCoverageTests` y
  `PresenceBarrierGateTests` los vigilan.
- **`HostingPolicy`** (Domain, pura): `ClassOf`, `CanBePersistent` (falso para pedir revisores, fusionar y lo destructivo), `RequiresApprovedVerdict` y `ClassAdmitsGrant`. **`HostingPolicyGrant`** no se puede construir para una clase que no
  admite ninguna autorización persistente, ni con rama base salvo en «escribir en el PR»; `Covers` exige la misma clase, el mismo repositorio y, si lleva rama base, esa misma.
- **Las fábricas de los payloads validan** lo mismo (una autorización persistente de fusionar no se puede anotar; una operación se anota con exactamente una de la autorización y la aprobación, y una autorización no puede amparar una
  operación que no admite ser persistente).


## Lo que fija WP3 (2026-11-13)

- **Una sola compuerta:** `HostingPolicyGate` (`IHostingPolicyGate`, más `IHostingUseGate` para la publicación). Por cada uso comprueba, en este orden: el registro verifica (carga completa); el veredicto según la operación (`IDeliveryService.CheckAsync` y que el
  commit sea el juzgado, o, para publicar el veredicto, que haya uno que contar según `RemoteJudgement.VerdictInForce`, la misma función de la vista de PR); la capacidad declarada (y el modo de fusión); la inspección de git (solo el `push`, con el proyecto
  tomado); y la autorización persistente (`HostingPolicyGrant.Covers`) o la aprobación atada, con el estado vuelto a comprobar tras aprobar.
- **El `ApprovalSequence`** del payload se toma del último `ApprovalGranted` de esa acción del registro, justo tras la aprobación: `HostingAuthorization` solo trae la atadura. Si no se encontrara, la entrada de la operación no se escribe en vez de inventar
  una secuencia.
- **Dar o retirar una autorización** pasa por el buzón y toma el proyecto un instante; con el proyecto ocupado queda aparcada en memoria y **no vale** hasta anotarse. Retirar no pide aprobación. `GrantAsync` de una clase que no admite ninguna operación
  persistente lanza `ArgumentException`.
- **Los textos** del PR, del comentario y del estado del veredicto son plantillas de `OrchestratorStrings` (`Text_hosting_pr_*`, `Text_hosting_verdict_*`), pintadas con `IUserTextRenderer` en el idioma de `IResponseLanguage`. Todo dato variable va en un tramo de
  código sin sus acentos graves (`PullRequestTextComposer.Fence`); el comentario empieza por la marca `<!-- agent-studio:verdict run=<id> -->`; nada del razonamiento del juez ni de un agente sale. Queda sin cumplir el nombre del hito en el título: se usa el id
  de la ejecución.
- **Pedir revisores, fusionar y lo destructivo** piden siempre; ninguna autorización los cubre aunque su clase sea la suya (prueba).

### Ronda de arreglos tras la revisión (2026-11-13)

- **Una aprobación para todo lo que el flujo va a hacer.** La atadura (`Binding`) no lleva el tipo de operación: acción, repositorio, ejecución, commit juzgado, rama base, modo y, si ya existe, el PR. Las operaciones del veredicto llevan el commit juzgado y la base, así que la aprobación de «escribir en el PR» del clic cubre abrir el PR y publicar el estado y el comentario, y una autorización (con base o sin ella) también. La descripción dice «+verdict» y termina con una referencia hecha de la atadura, con la que se casa la entrada de la respuesta para sacar el `ApprovalSequence`.
- **Pedir revisores** añade a la atadura y a la descripción los nombres a los que se avisa (validados, ordenados, hasta 20): la persona aprueba viéndolos.
- **La compuerta se obedece:** el resultado de cada llamada decide, y la entrada de cada operación nombra lo que la amparó cuando se comprobó, no lo que diga una comprobación posterior.
- **Revocar** vale desde que se pide (ver ADR-0067). Solo cuentan las entradas de autorización escritas por una persona (actor `User`).
- **Canales del veredicto:** estado y comentario pasan la compuerta cada uno; sin la capacidad del estado de commit se publica el comentario (D-21), y solo se rechaza si no hay ningún canal.
- **Un solo veredicto** para el estado, el commit, los requisitos y el modelo del juez: `RemoteJudgement.VerdictInForceWithVerdict`.
- **Textos:** siguen la guía (versión guardada (commit), revisor independiente (juez), INFLESZ 55). El título del PR lleva el id de la ejecución (aceptado).
