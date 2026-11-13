# ADR-0068 · Políticas por clase de operación remota: autorizaciones persistentes en el registro, fusionar y lo destructivo por uso y con Windows Hello, y las protecciones a la vista

- **Estado:** propuesta · 2026-11-13, pendiente de la decisión 7 de [docs/21 §14](../21-fase-10-git-remoto.md#14-decisiones-que-necesitan-a-la-persona)
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
     | Escribir en el PR | Abrir, actualizar, pedir revisores, publicar el veredicto | Persistente por proyecto si se da; si no, por uso |
     | Fusionar | Fusionar el PR con un modo permitido | Siempre por uso, con Hello |
     | Destructivas | Cerrar un PR sin fusionar; borrar la rama remota de una ejecución | Siempre por uso, con Hello |

     El commit local del sistema y la entrega local a la rama de la persona no cambian y no pasan por aquí.
  2. **Qué clase admite un permiso guardado lo decide el tipo:** `HostingPolicy.CanBePersistent` es falso para fusionar y
     para lo destructivo; la compuerta rechaza concederlo y el payload no se puede construir. Ningún ajuste lo cambia.
  3. **Una autorización persistente es una entrada del registro** (`HostingPolicyGranted`: clase, proveedor, host,
     propietario, repositorio, rama base opcional y quién), dada con aprobación y Hello, y se retira con otra
     (`HostingPolicyRevoked`). Su alcance es el **identificador del repositorio remoto:** si el remoto del proyecto pasa a
     ser otro, deja de aplicar. Vale para el proyecto, no caduca sola y se ve y se retira en la pantalla.
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
