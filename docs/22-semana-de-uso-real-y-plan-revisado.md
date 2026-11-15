# 22 · Semana de uso real y plan revisado

**Fecha:** 2026-11-13 · **Estado:** acordado contigo el 2026-11-13 y corregido ese mismo día (§1): la semana de uso
**se suma** a lo pendiente, no lo sustituye.

## 0. Por qué

Hay mucho construido y poco usado: varias fases siguen «pendientes de tu demostración», y nadie ha visto las pantallas de la
Fase 10 con datos reales. Por eso la app se usa una semana en un proyecto tuyo de verdad, a la vez que sigue lo pendiente.
Lo que salga decide qué se pule, qué se congela y qué se recorta de lo que aún no esté hecho.

La pregunta de la semana no es «¿funciona?», que ya lo dicen las pruebas. Es **«¿la abro para trabajar, y me da algo que
los CLIs solos no me dan?»**

## 1. Qué cambia en el plan

| Antes | Ahora |
|---|---|
| Fase 10 → Fase 7 → Fase 15 → Fase 16, sin parar | El mismo orden, y **además** el día 0 y la semana de uso real, cuando tú quieras |
| Agentes trabajando sin pausa | Agentes parados **solo mientras lanzas ejecuciones en la app** (§6): comparten cuota con ella |
| Dentro de cada fase, todo por su orden | Dentro de cada fase se deja para el final lo que el uso puede recortar: el empaquetado y las traducciones |
| Las fases 7, 15, 16 y 11 son el plan | Las fases 7, 15 y 16 siguen en el plan; la revisión (§8) puede recortar lo que quede de ellas. La 11 es candidata |

**Corrección del 2026-11-13.** La primera versión de este documento decía que no se empezaba ninguna fase hasta la
revisión. Fue un malentendido mío: lo que pediste es que esta guía se sume a lo pendiente. El orden de lo pendiente es:
los restos de la Fase 10 y de [18](18-peticiones-y-pendientes.md), la Fase 7, la Fase 15 y la Fase 16.

**Adenda · 2026-11-15.** El usuario pide continuar desarrollando las fases pendientes mientras termina la verificación del relevo. Se adelanta trabajo técnico acotado de la Fase 7 en copias aisladas: resolución de enlaces en la política de rutas (F7-2), bóveda por proyecto (F7-7) y recorrido automático de la cadena con proveedores guionizados (F7-5). Esta autorización cambia la espera del desarrollo; no acredita el día 0, las cinco sesiones, la revisión ni una fase terminada. La aplicación al origen sigue siendo humana y se conservan las decisiones y comprobaciones pendientes.

## 2. Antes de empezar

- [ ] La Fase 10 está en `main` y la copia principal, al día.
- [ ] Los programas son los medidos: Claude Code 2.1.290, Codex 0.160.0 y `gh` 2.96.0 (comprobado el 2026-11-13: coinciden).
      Si alguno se actualiza antes del día 0, dímelo: la app solo relaja sus cierres con la versión que se midió.
- [ ] **El proyecto de la prueba:** un repositorio tuyo que **no** sea Agent Studio, pequeño o mediano, con pruebas que se
      puedan lanzar, con trabajo real pendiente (dos o tres cosas que harías de todos modos esta semana) y con remoto en
      GitHub. Si no quieres publicar en él, la parte remota se hace contra `agent-studio-privado`.
- [ ] Arranque: `dotnet run --project src/AgentStudio.Desktop`, desde la copia principal.
- [ ] Un fichero de notas (el que quieras). Una línea por sesión, con las cinco preguntas de §5.

## 3. Día 0: mi parte, y después la tuya

Nadie ha visto estas pantallas con datos reales. El 2026-11-13 me pediste que hiciera por mi cuenta lo que pudiera. El
reparto queda así:

- **Lo que hago yo, sin ti.** Arranco la app con un proyecto de prueba (la copia de `agent-studio-privado`) y miro con
  capturas «Auto Doctor» y las pantallas de la Fase 10 con datos reales, hasta el primer aviso de Windows Hello, que
  cancelo. Aparte, las mediciones en vivo del adaptador que faltaban (fusiones con los tres modos, push forzado, cierre y
  borrado de rama), solo contra `agent-studio-privado`.
- **Lo que es tuyo: todo lo que pide Windows Hello.** Aprobar el plan (el ajuste viene activado), «Entregar a mi rama»,
  publicar, fusionar y borrar una rama. En los cuatro últimos no hay ajuste que lo quite, y yo no lo rodeo: esa barrera
  es lo que la app promete.

Los cinco pasos son los de siempre. Lo que yo haya visto antes en el ensayo se apunta en §3.1, para que tu parte sea corta.

1. **Arranca la app y abre el proyecto de la prueba.** Mira «Auto Doctor»: ¿lo que dice de tu equipo y del proyecto es
   verdad? (Cierra la demostración pendiente de la Fase 6.)
2. **Primera ejecución de verdad:** escribe dos o tres requisitos de una tarea pequeña, deja que planifique, aprueba y
   espera al veredicto. No busques fallos: haz el camino normal.
3. **Entrega local** («Entregar a mi rama») de lo aprobado.
4. **La prueba en vivo de la Fase 10,** con Windows Hello: «Publicar y abrir PR», el veredicto en el PR, «Sincronizar ahora»,
   fusionar un PR con cada modo (squash, rebase y commit de fusión), revertir uno desde la web de GitHub, cerrar otro y
   borrar su rama. Contra `agent-studio-privado` si el proyecto de la prueba no es para esto.
5. **Las dos demostraciones de la Fase 6 que piden Hello:** el doctor de skills contra GitHub, y de un nodo de Auto Graph a
   su fichero.

Lo que no salga a la primera se apunta y se arregla ese día o el siguiente. Si algo impide usar la app, la semana no
empieza hasta que esté arreglado.

**Preparación técnica del día 0 · 2026-11-15.** En una copia aislada se han preparado los remates P77, P85, P87–P90 y P94, y se ha conciliado P74, que ya estaba implementado en la base (estado y comprobaciones en [18](18-peticiones-y-pendientes.md)). La revisión y aplicación humanas, el foco y el recorrido con la ventana real, P82/P83, P78 con un push real autorizado y P81 conservan sus comprobaciones pendientes. Estas propuestas y sus pruebas automáticas no acreditan que haya terminado el día 0 ni que se haya realizado una sesión de la semana; el resultado de §9 se completa contigo al terminar el uso real.

### 3.1 Lo que salió de mi ensayo (2026-11-13)

**Cómo lo hice.** Arranqué la app compilada de `main` (9382121) desde una copia, con el equipo cargado por tres agentes.
La manejé por la accesibilidad de Windows, sin ratón ni teclado, y capturé solo su ventana. No pulsé nada que pida
Windows Hello ni cambié ningún ajuste. El proyecto fue una copia local de `agent-studio-privado`.

**Lo que funciona con datos reales:**

- Arranca en 24 segundos y abre el último proyecto. Las trece secciones del menú abren sin colgarse ni cerrarse.
- Auto Doctor dice la verdad del equipo (programas 6 de 6, herramientas 9 de 9) y del proyecto: el remoto es GitHub, usa
  HTTPS, la configuración deja publicar, no hay Git LFS y la sincronización está apagada.
- La sección de PR reconoce el repositorio; «Comprobar la conexión» encuentra tu sesión de `gh`.
- La ficha de publicar se niega bien con una ejecución sin terminar, y avisa de que pedirá Windows Hello.
- Al cerrar, la app termina limpia y no deja procesos.

**Lo medido en vivo por el código de la app** (detalle en [21 §13](21-fase-10-git-remoto.md)): el `push` por HTTPS, abrir y
cambiar un PR, fusionar con los tres modos (la app deduce bien cada uno), un revert hecho por GitHub, un push forzado, y
cerrar un PR y borrar su rama. Quedan en el repositorio de prueba los PR 2 a 7 (el 7 metió la calculadora de demostración).

**Lo que falla o confunde** (apuntado en [18](18-peticiones-y-pendientes.md), P82 a P90):

| Qué | Gravedad |
|---|---|
| El panel de Auto Doctor desplegado no expone nada a la accesibilidad: un lector de pantalla no lo alcanzaría (P82; medido con un solo cliente, falta confirmarlo con el Narrador) | Alta, por confirmar |
| Tras comprobar la conexión, la pantalla dice a la vez «Conectado a GitHub como…» y «La sesión aún no se ha comprobado» (P84) | Media |
| «Sincronizar ahora» sin PR seguidos promete un resultado que no llega (P85) | Media |
| La ficha de publicar culpa a la versión de `gh` cuando el motivo es otro (P86) | Media |
| El terminal tapa una franja del panel de Auto Doctor (P83, falta confirmarlo en pantalla) | Media |
| La ficha de publicar se abre por debajo de lo visible; rutas largas en la barra de estado; detalles de estilo (P87 a P90) | Baja |

Ninguno impide tu parte.

**Lo que no pude ver:** nada de lo que hay detrás de Windows Hello. Es decir, una ejecución de verdad, la entrega, y las
pantallas de PR, el tablero y la cadena con un PR publicado por la app.

### 3.2 Tu parte: solo lo que pide tu dedo (veinte o treinta minutos)

Avísame antes de empezar, para parar a los agentes mientras tanto.

1. Abre la app: se abre sola con el proyecto de prueba, `C:\AgentStudioDemo\agent-studio-privado`. Es un clon de
   `agent-studio-privado` con la calculadora de demostración; compila y sus 8 pruebas pasan (comprobado el 2026-11-13).
2. En «Orquestador»: escribe una petición pequeña, añade dos requisitos y congélalos. Pulsa «Usar el perfil propuesto»
   (**Hello**), pide el plan y apruébalo (**Hello**). Espera al veredicto.
3. «Publicar y abrir PR» (**Hello**). Mira en «Repositorio» → «Peticiones de fusión (PR)» que el PR aparece, con su
   veredicto, y pulsa «Sincronizar ahora».
4. Fusiona el PR desde la app (**Hello**) y mira que «Trabajo» → «Tablero» y «Cadena» lo cuentan.
5. «Entregar a mi rama» (**Hello**), con una segunda ejecución pequeña: la primera ya entró por el PR, y entregarla además a
   tu rama local la dejaría distinta de la de GitHub.
6. Las dos demostraciones de la Fase 6 que piden Hello: el doctor de skills contra GitHub, y de un nodo de Auto Graph a su
   fichero.

Los pasos 2 a 5 **no los he podido ejercitar con la app**, porque todos piden Hello. Lo que sé de ellos es por las pruebas
con dobles y por el código. Si alguno no sale, es justo lo que hay que encontrar.

Con una fusión basta: los tres modos, el revert, el push forzado y el cierre con borrado ya están medidos (§3.1). Lo que te
haga dudar de un dato, dímelo en el momento.

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

- **Sigo con lo pendiente** (§1), con tres agentes como mucho a la vez y las revisiones de siempre.
- **Paro los agentes mientras lanzas ejecuciones en la app.** Dímelo al empezar y al terminar. Los agentes que lanzo y los
  programas que lanza Agent Studio gastan la misma ventana de uso de tu suscripción: si yo sigo, tus ejecuciones fallarían
  por límite y juzgarías mal la app.
- **Sin relevo automático.** Del 13 al 15 Codex siguió el trabajo solo, con un relevo automático. El 2026-11-15
  decidiste dejarlo de lado: no entra en el repositorio, y su trabajo se revisó y se aceptó con arreglos
  ([18](18-peticiones-y-pendientes.md), P95 y P98).
- **Arreglo lo que encuentres,** en cuanto me lo digas y con prioridad sobre todo lo demás.
- **La comparación con lo publicado,** una vez, en un rato en que no uses la app: qué hacen hoy los proyectos abiertos que
  orquestan agentes de código, contra los cinco puntos que creo que son lo tuyo (un juez de otro proveedor que decide con
  pruebas guardadas; un registro que no se reescribe; aprobaciones atadas a lo que se va a hacer, con Hello; la traza del
  requisito al commit, al veredicto y al PR; no tocar nunca tus credenciales). Lo dije de memoria: hay que comprobarlo antes
  de recortar apoyándose en ello.

### 6.1 La comparación, primera pasada (2026-11-13)

La hizo un agente investigador leyendo las páginas de cada proyecto. **Yo no he abierto esas páginas:** es su lectura, con
sus enlaces. Y es parcial: no llegó a mirar Cursor, Devin, OpenHands, Aider, Cline, Roo Code, Goose ni opencode.

| Lo que creía propio | Qué encontró | Quién se acerca |
|---|---|---|
| Un juez de otro proveedor, con pruebas guardadas | **Ya existe** la revisión por un modelo de otra empresa. Lo que no vio en nadie: un veredicto atado a pruebas guardadas que se puedan volver a comprobar | [Sno Station](https://github.com/sno-ai/sno-station), [Claudexor](https://github.com/razzant/claudexor), [no_human](https://github.com/no-human-ai/no_human) y el [plugin de Codex para Claude Code](https://github.com/openai/codex-plugin-cc) |
| Un registro que no se reescribe | **Existe** en proyectos pequeños; en los grandes no lo vio | [Agent Console](https://github.com/cyl-castillo/agent-console) (cadena de hashes) |
| Aprobaciones atadas a lo que se aprueba, con Windows Hello | **No lo encontró en nadie** con biometría o PIN por acción. Sí aprobaciones atadas a un hash | Agent Console, sin biometría |
| La traza del requisito al commit, al veredicto y al PR | **Entera, en nadie.** Hay piezas sueltas | Agent Console y no_human |
| No tocar tus credenciales | **Es lo normal** en estos proyectos | Sno Station, Agent Console, [Fletch](https://github.com/fwdai/fletch), Claude Squad |

Lo que varios hacen y Agent Studio no: avisos y aprobaciones en el móvil, comentarios en línea sobre el diff, y pasar la
tarea a otro agente al llegar al límite de uso.

**Qué cambia:** de los cinco puntos, dos siguen pareciendo propios (las aprobaciones con Hello y la traza entera) y uno a
medias (el veredicto con pruebas que se vuelven a comprobar). Los otros dos ya no distinguen. Antes de recortar con esto en
la revisión (§8) hay que completar la lista que quedó sin mirar.

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

Con tus notas y la comparación delante, se recorre esta lista. Las fases 7, 15 y 16 ya estarán en marcha: la pregunta de
cada fila decide si lo que quede de ella se termina, se recorta o se congela:

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
