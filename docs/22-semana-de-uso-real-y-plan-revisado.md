# 22 · Semana de uso real y plan revisado

**Fecha:** 2026-11-13 · **Estado:** acordado contigo el 2026-11-13. Sustituye el orden «Fase 10 → 7 → 15 → 16 sin parar».

## 0. Por qué

Hay mucho construido y poco usado: varias fases siguen «pendientes de tu demostración», y nadie ha visto las pantallas de la
Fase 10 con datos reales. Antes de gastar tres o cuatro días en la Fase 7, la app se usa una semana en un proyecto tuyo de
verdad. Lo que salga decide qué se termina, qué se congela y qué se recorta.

La pregunta de la semana no es «¿funciona?», que ya lo dicen las pruebas. Es **«¿la abro para trabajar, y me da algo que
los CLIs solos no me dan?»**

## 1. Qué cambia en el plan

| Antes | Ahora |
|---|---|
| Fase 10 → Fase 7 → Fase 15 → Fase 16, sin parar | Fase 10 → **día 0 juntos** → **semana de uso real** → **revisión** → lo que la revisión decida |
| Agentes trabajando sin pausa hasta el domingo | **Agentes parados mientras usas la app** (§6): comparten cuota con ella |
| Las fases 7, 15, 16 y 11 son el plan | Son **candidatas**: cada una tiene la pregunta que la decide (§8) |

No se empieza ninguna fase nueva hasta la revisión.

## 2. Antes de empezar

- [ ] La Fase 10 está en `main` y la copia principal, al día.
- [ ] Los programas son los medidos: Claude Code 2.1.290, Codex 0.160.0 y `gh` 2.96.0 (comprobado el 2026-11-13: coinciden).
      Si alguno se actualiza antes del día 0, dímelo: la app solo relaja sus cierres con la versión que se midió.
- [ ] **El proyecto de la prueba:** un repositorio tuyo que **no** sea Agent Studio, pequeño o mediano, con pruebas que se
      puedan lanzar, con trabajo real pendiente (dos o tres cosas que harías de todos modos esta semana) y con remoto en
      GitHub. Si no quieres publicar en él, la parte remota se hace contra `agent-studio-privado`.
- [ ] Arranque: `dotnet run --project src/AgentStudio.Desktop`, desde la copia principal.
- [ ] Un fichero de notas (el que quieras). Una línea por sesión, con las cinco preguntas de §5.

## 3. Día 0, juntos (dos o tres horas)

Es la única parte en la que tengo que estar pendiente: nadie ha visto estas pantallas con datos reales y yo no puedo
manejar la ventana. Tú haces; yo miro lo que me cuentes y arreglo lo que bloquee.

1. **Arranca la app y abre el proyecto de la prueba.** Mira «Auto Doctor»: ¿lo que dice de tu equipo y del proyecto es
   verdad? (Cierra la demostración pendiente de la Fase 6.)
2. **Primera ejecución de verdad:** escribe dos o tres requisitos de una tarea pequeña, deja que planifique, aprueba y
   espera al veredicto. No busques fallos: haz el camino normal.
3. **Entrega local** («Pasar a tu rama») de lo aprobado.
4. **La prueba en vivo de la Fase 10,** con Windows Hello: «Publicar y abrir PR», el veredicto en el PR, «Sincronizar ahora»,
   fusionar un PR con cada modo (squash, rebase y commit de fusión), revertir uno desde la web de GitHub, cerrar otro y
   borrar su rama. Contra `agent-studio-privado` si el proyecto de la prueba no es para esto.
5. **Las dos demostraciones de la Fase 6 que piden Hello:** el doctor de skills contra GitHub, y de un nodo de Auto Graph a
   su fichero.

Lo que no salga a la primera se apunta y se arregla ese día o el siguiente. Si algo impide usar la app, la semana no
empieza hasta que esté arreglado.

## 4. La semana: cinco sesiones

Una sesión es **un trabajo real** que harías igual, de una a dos horas. No son pruebas de la app: son tu trabajo, hecho
con ella. Cada sesión cierra además una demostración que ya estaba pendiente; no hay listas nuevas.

| Sesión | Qué haces | Qué miras | Lo que cierra |
|---|---|---|---|
| 1 · El camino normal | Una tarea real de principio a fin: requisitos, plan, ejecución, veredicto, entrega | Cuánto tardas en tener algo útil. Dónde te pierdes. Qué texto no entiendes. Si el veredicto te dice algo que no sabías | El recorrido de la Fase 5 ([03](03-pantallas-y-navegacion.md), lista de comprobación de la Fase 5) |
| 2 · Algo que sale mal | Una tarea ambigua o difícil, de las que un agente suele hacer a medias | Si el revisor independiente lo caza. Si las pruebas guardadas te dejan ver por qué. Si reintentar o impugnar es claro. Cuántas veces te pide aprobar y cuáles sobran | F4-3 (salud del contexto, en el monitor) |
| 3 · Sin estar delante | Una ejecución larga; te vas. Si llega al límite de cuota, que espere; bloquea el equipo y vuelve | Si al volver entiendes qué pasó sin preguntar a nadie. Si reanudó sola. Si la bandeja dice la verdad | La demostración de la Fase 8 ([03](03-pantallas-y-navegacion.md), pasos 1 a 23) |
| 4 · Con el remoto | Publica una ejecución aprobada, abre el PR, déjalo un día, fusiónalo | Si el tablero y la cadena cuentan lo que pasó en GitHub. Si «Sincronizar con GitHub» hace lo que esperas. Si te fías de lo que dice el PR | F10-2, F10-7 y F10-8 con datos reales |
| 5 · Lo de alrededor | Quince minutos a cada cosa, solo si te hace falta para tu trabajo: «Consumo», «Skills», «Memoria», Auto Graph y diagramas, el debate, la ayuda | **Qué echarías de menos si no estuviera.** Lo que no abras por necesidad, apúntalo como «no lo abrí» | F4-1 (consumo), F4-7 (skills) y F6-5 |

Si un día no te apetece abrirla, no te obligues: apúntalo. Ese dato es el que más vale.

## 5. La nota de cada sesión

Cinco preguntas, una línea cada una:

1. **¿Qué intenté?**
2. **¿El veredicto cambió lo que yo habría hecho?** (cazó algo real · confirmó lo que ya sabía · falsa alarma)
3. **¿Con el CLI solo habría ido más rápido?** (sí · igual · no, y por qué)
4. **¿Dudé alguna vez de lo que decía la pantalla?** (qué pantalla y qué dato)
5. **¿Qué no abrí?**

Y, aparte, **todo lo que te haga dudar de un dato o perder trabajo** me lo dices en el momento, sin esperar a la revisión:
eso se arregla antes que nada.

## 6. Qué hago yo mientras

- **Callado mientras usas la app.** Los agentes que lanzo y los programas que lanza Agent Studio gastan la misma ventana de
  uso de tu suscripción: si yo sigo con agentes, tus ejecuciones fallarían por límite y juzgarías mal la app. Esto sustituye
  a «exprimir los tokens hasta el domingo».
- **Arreglo lo que encuentres,** en cuanto me lo digas y con prioridad sobre todo lo demás. Poco a poco y sin agentes en
  paralelo.
- **La comparación con lo publicado,** una vez, en un rato en que no uses la app: qué hacen hoy los proyectos abiertos que
  orquestan agentes de código, contra los cinco puntos que creo que son lo tuyo (un juez de otro proveedor que decide con
  pruebas guardadas; un registro que no se reescribe; aprobaciones atadas a lo que se va a hacer, con Hello; la traza del
  requisito al commit, al veredicto y al PR; no tocar nunca tus credenciales). Lo dije de memoria: hay que comprobarlo antes
  de recortar apoyándose en ello.
- Nada de fases nuevas.

## 7. Las reglas, escritas antes de mirar

Para que la revisión no dependa del humor del día:

| Lo que salga | Qué se hace |
|---|---|
| La abriste por tu cuenta cuatro días o más, y el veredicto cambió lo que habrías hecho al menos dos veces | Se sigue: se termina lo que usaste y se pule |
| La abriste, pero con el CLI solo habrías ido igual o más rápido casi siempre | Se recorta al núcleo: revisor independiente, pruebas guardadas y registro, con la menor fricción posible. Lo demás se congela |
| Dudaste de un dato en pantalla, o perdiste trabajo | Eso se arregla antes de cualquier otra cosa, salga lo que salga |
| Una pantalla o función no la abriste por necesidad en toda la semana | Se congela: el código se queda, no se le dedica más trabajo |
| A partir del tercer día dejaste de abrirla | Se para de añadir. Decidimos entre quedarnos con el núcleo o dejarla como está |

«Congelar» no es borrar: es no gastar más en ello.

## 8. La revisión (una hora, juntos)

Con tus notas y la comparación delante, se recorre esta lista. Cada candidata tiene la pregunta que la decide:

| Candidata | Tamaño | La pregunta que la decide |
|---|---|---|
| Fase 7 · aislamiento por proyecto, bóveda y permisos efectivos | Grande | ¿Hubo algún momento en que no te fiaste de dejar a un agente solo en tu proyecto? |
| Fase 7 · empaquetado y actualizaciones | Media | ¿La vas a usar en otro equipo, o la va a usar otra persona? Si no, `dotnet run` basta |
| Fase 7 · lector de pantalla, alto contraste y zoom | Media | ¿Lo necesitas tú? |
| Fase 15 · comprobación completa y traducciones | Media | ¿La vas a enseñar o a compartir? |
| Fase 16 · documentación viva | Grande | ¿Abriste los diagramas o las discrepancias porque te hacían falta? |
| Fase 11 · entornos y servicios | Muy grande | ¿Tu proyecto los necesita y hoy los levantas a mano con molestia? |
| Lo que ya existe y no abriste (sesión 5) | — | Se congela, salvo que la comparación diga que es justo lo que nadie más tiene |
| Pendientes de [18](18-peticiones-y-pendientes.md) | Pequeños | Solo los que te molestaron en la semana |

De ahí sale el plan nuevo, con sus días, y se escribe aquí debajo.

## 9. Resultado de la revisión

*(Se rellena al terminar la semana.)*
