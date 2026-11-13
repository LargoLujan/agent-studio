# ADR-0066 · El veredicto del juez llega al PR como un estado del commit y un comentario etiquetado, nunca como una aprobación de la cuenta de la persona

- **Estado:** propuesta · 2026-11-13, pendiente de las decisiones 8 y 15 de [docs/21 §14](../21-fase-10-git-remoto.md#14-decisiones-que-necesitan-a-la-persona)
  (se redacta con la opción recomendada de cada una). Recoge D-20 a D-22 y D-29 de [docs/21](../21-fase-10-git-remoto.md).
  **Amplía** [ADR-0006](0006-juez-independiente-verificable.md) (el veredicto verificable) y
  [ADR-0038](0038-hito-backlog-y-tablero.md) (ninguna tarjeta llega a «hecho» sin veredicto). WP2 implementa el canal y WP3
  cuándo se publica.
- **Contexto:**
  - F10-5: el veredicto llega al PR como estado o comentario etiquetado, nunca como una aprobación de la cuenta.
  - Todo lo que Agent Studio hace en GitHub lo hace **con la cuenta de la persona** (ADR-0065). Una revisión «aprobada»
    enviada desde la app sería, para GitHub y para cualquiera que mire el PR, la aprobación de esa persona, y podría
    satisfacer una protección de «revisión requerida» con la opinión de una IA.
  - `gh pr review --approve` existe, y `gh pr comment --edit-last` edita «el último comentario del usuario actual», que
    con una sola cuenta puede ser un comentario de la propia persona.
- **Decisión:**
  1. **Dos canales:**
     - un **estado del commit juzgado,** con el contexto fijo `agent-studio/verdict` y un vocabulario cerrado: `success`
       (aprobado y válido), `failure` (no aprobado) y `pending` (impugnado, en revisión o lo decide una persona), con una
       descripción fija del sistema;
     - un **comentario en el PR,** con una marca oculta en su primera línea (`<!-- agent-studio:verdict run=<id> -->`),
       que se localiza por su identificador guardado en el registro y, si falta, por la marca **y** el autor; nunca por
       «el último comentario». Se actualiza en su sitio.
  2. **Nunca una revisión, y se garantiza por estructura:**
     - `IHostingProvider` no tiene ningún miembro para enviar revisiones;
     - el catálogo del adaptador solo lee `/reviews` (con `GET`): no hay entrada que escriba en él;
     - una prueba recorre todos los `ProcessSpec` de la batería del adaptador y falla ante `pr review`, `--approve`, una
       escritura sobre una ruta que acabe en `/reviews`, o un cuerpo con `"event"`;
     - una prueba de arquitectura: ningún fichero del adaptador contiene `APPROVE` fuera del lector de revisiones ajenas.
  3. **Lo que dice lo compone el sistema:** la decisión, el commit juzgado, el recuento por requisito con sus ids, qué
     proveedor y modelo juzgó, que el juez no produjo el trabajo, y la frase fija «Esto no es una revisión de GitHub ni una
     aprobación de la cuenta que lo publica». **No lleva el razonamiento del juez:** es texto de un agente.
  4. **Sigue al veredicto en vigor** (la regla de `VerdictReach`, la del tablero): tras un veredicto nuevo o una
     impugnación, la pantalla dice que lo publicado ya no coincide y ofrece publicarlo. **No se actualiza solo:** empieza
     con un clic de la persona (el sondeo solo lee). Con el registro sin verificar, no se publica nada.
  5. **El veredicto es de un commit.** Si la cabeza del PR cambia después, el estado se queda en el commit juzgado, el
     comentario lo dice («la cabeza del PR ya no es el commit que juzgó el juez») y la pantalla lo marca.
  6. **Fusionar desde Agent Studio va atado al commit juzgado:** la petición lleva ese commit como cabeza esperada y la
     plataforma la rechaza si ya no lo es. Sin privilegios de administrador, sin título ni mensaje propios.
  7. **«Integrado» por PR exige que lo fusionado sea lo juzgado.** Una fusión hecha fuera con otra cabeza no pasa la
     tarjeta a «integrado»: se queda en «hecho» con el motivo «fusionado con cambios que el juez no vio», y el informe
     del hito lo da como desviación. La invariante del tablero (ADR-0038) no cambia.
  8. **Publicar el veredicto es una escritura** de la clase «escribir en el PR» (ADR-0068) y deja su entrada en el
     registro (`HostingOperationPerformed`) con el veredicto que refleja.
- **Alternativas descartadas:** una revisión «aprobada» (prohibida por F10-5); una revisión de tipo «comentario» (sigue
  siendo una revisión de la cuenta, junto a las aprobaciones); solo el comentario (no aparece en los checks ni se puede
  exigir en las protecciones); solo el estado (sin explicación en el PR); publicar el razonamiento del juez (texto de un
  agente, con su cuenta, donde GitHub interpreta menciones y referencias).
- **Consecuencias:**
  - La persona puede exigir `agent-studio/verdict` como check requerido en las protecciones de su rama, si quiere que un
    veredicto no aprobado bloquee la fusión. Agent Studio no lo configura.
  - Un `success` de ese contexto aparece como un check verde junto al commit: su nombre dice de qué es, y el comentario lo
    explica.
- **Residuales aceptados:** la autoría en GitHub del estado y del comentario es la cuenta de la persona, aunque el texto
  diga que lo publica Agent Studio; alguien con permisos en el repositorio puede borrar el comentario o publicar otro
  estado con el mismo contexto (lo que cuenta para Agent Studio es su registro, no lo que diga el remoto).
- **Sin verificar al escribirlo:** el permiso que exige publicar un estado de commit, el tope de su descripción y los
  códigos con que la plataforma rechaza una fusión con la cabeza cambiada.

## Lo que fija el P0 (2026-11-13)

- **`VerdictNotice`** es el vocabulario cerrado de lo que se publica del veredicto: la ejecución, el commit juzgado, `VerdictStanding` (aprobado, rechazado, en revisión), los requisitos con su id y si se cumplieron, el proveedor y el modelo
  del juez, y si la cabeza del PR es el commit juzgado. Ningún texto del juez.
- **`HostingPolicy.RequiresApprovedVerdict`** decide por operación, no por clase: publicar el estado y el comentario del veredicto no lo exigen (tienen que poder decir «no aprobado» y «en revisión»), y tampoco cerrar un PR ni
  borrar su rama; todo lo demás sí.
- **`IHostingProvider.PublishVerdictAsync`** es el único miembro que escribe un veredicto, y no hay ninguno que envíe una revisión.
