# 18 · Peticiones del usuario y pendientes

> Documento vivo. Recoge cada petición del usuario y cada hallazgo del recorrido real de la app, con su estado y
> dónde se resuelve. Al cerrar lo pendiente, se repasa entero y se decide cómo implementar cada punto. Las peticiones
> se citan casi literales. Empezado el 2026-10-30, durante la instalación y la prueba en vivo de la app.

Estados: **en curso** (hay un paquete trabajando en ello), **planificado** (tiene sitio en el plan, sin empezar),
**pendiente de diseño** (entra en la propuesta de [17](17-constructor-visual-de-agentes.md)), **hecho** (verificado).

## 1. Peticiones del 2026-10-29/30

| # | Petición | Estado | Dónde |
|---|---|---|---|
| P1 | «Instálalo en el equipo, toma el control y haz las pruebas.» | Hecho | Publicada en `%LOCALAPPDATA%\Programs\AgentStudio` con acceso directo en el menú Inicio; recorrido en vivo con UI Automation y capturas |
| P2 | «Haz las pruebas que dices más un testeo de todos los elementos del IDE: que todo funcione, tenga sentido y sea visualmente sencillo pero completo y profesional. Si tienes que añadir más pruebas, hazlas. Al final dime qué has visto mejorado y qué ha sido retocado.» | En curso | Hallazgos en §3; arreglos en la tanda 1 (WP-A a WP-E) |
| P3 | «Todos los porcentajes de consumo de tokens, contexto y demás siempre son barras visibles en tiempo real. El contexto siempre es visible.» | Hecho (verificado en vivo el 2026-11-03; retoques V32 a V39 en §3) | Tanda 2 (WP-F), integrada el 2026-11-03: un control de barra común (`MetricBar`), siempre visible también sin porcentaje; la tabla base de ventanas por modelo, para que haya porcentaje desde la primera ejecución; y barras en la barra de estado, el monitor, el panel de la bandeja, Consumo, la planificación y la sala de debate. Las del lienzo llegan con la Fase 13 ([17](17-constructor-visual-de-agentes.md) D-12). Pantallas en [03](03-pantallas-y-navegacion.md) §2, §2.1 y §5 |
| P4 | Interfaz visual dinámica para montar orquestador, juez y agentes, como un pipeline de Fabric o un videojuego con elementos animados que se conectan. Debe enseñar a qué repositorio de git se conecta cada uno y lo principal de cada proyecto (contexto, URL…) y dar sugerencias en vivo mientras se monta. | Pendiente de diseño | [17](17-constructor-visual-de-agentes.md) |
| P5 | Consumo y contexto de cada elemento siempre visibles, con su máximo, y barras dinámicas por elemento. | Pendiente de diseño (se apoya en P3) | [17](17-constructor-visual-de-agentes.md) |
| P6 | Cuando interviene el juez, un análisis adicional (un botón) de si el contexto fue escaso o si se consumió algo sin sentido, y de cómo ahorrar tokens. | Pendiente de diseño | [17](17-constructor-visual-de-agentes.md) |
| P7 | Elegir el modelo de cada elemento en una lista dinámica: qué modelo y con qué potencia, y cuál se recomienda tras pasarle el contexto del proyecto. | En curso (selector de modelo en la plantilla, WP-E); recomendación pendiente de diseño | WP-E y [17](17-constructor-visual-de-agentes.md) |
| P8 | Un configurador paso a paso por proyecto, con lista de skills recomendadas. | Pendiente de diseño | [17](17-constructor-visual-de-agentes.md) |
| P9 | Las dudas de los agentes con varias opciones, como ventanas emergentes con selector: el consumo estimado de cada respuesta y la opción recomendada marcada, como hace Codex en VS Code. | Pendiente de diseño | [17](17-constructor-visual-de-agentes.md) |
| P10 | Decidir en el lienzo cuántos agentes hay. El orquestador comprueba entre agentes si todo encaja, qué orden deben llevar, si es dañino que uno haga algo antes que otro, y recomienda el orden o el paralelo. | Pendiente de diseño | [17](17-constructor-visual-de-agentes.md) |
| P11 | Idiomas: por defecto el de Windows, y los principales idiomas del mundo. Nosotros usamos español de España. | Pendiente de diseño | [17](17-constructor-visual-de-agentes.md) |
| P12 | Textos sencillos y cortos, sin bloques largos, con términos que entienda cualquiera y los tecnicismos mínimos. Las explicaciones y la ayuda, fáciles de entender. | Pendiente de diseño (y repaso de textos en la tanda 1) | [17](17-constructor-visual-de-agentes.md) |
| P13 | Botones de información (ⓘ) en los apartados complejos: qué es y qué se puede hacer. | Pendiente de diseño | [17](17-constructor-visual-de-agentes.md) |
| P14 | Un chat de ayuda con el contexto del IDE, en un botón de la barra lateral, para saber qué hacer en cada caso. | Pendiente de diseño | [17](17-constructor-visual-de-agentes.md) |
| P15 | Al terminar, la memoria del proyecto genera documentación lista para entregar a clientes (PDF, Word…), con todo lo necesario. | Pendiente de diseño | [17](17-constructor-visual-de-agentes.md) |
| P16 | El tablero tipo Jira que acordamos (Fase 9: hitos, backlog, tablero y trazabilidad), hecho como dijimos, al final junto con todo esto, y comprobado. | Planificado | [16](16-ampliacion-plataforma-de-proyectos.md) y [17](17-constructor-visual-de-agentes.md) |
| P17 | Probar cada elemento, opción y pregunta, con una UI/UX fácil, sencilla y dinámica, aunque lleve días. | Planificado | Plan de pruebas en [17](17-constructor-visual-de-agentes.md); recorrido en vivo tras cada tanda |
| P18 | Windows Hello cuando se requieran permisos especiales o muy altos, como barrera de seguridad siempre: los agentes no hacen nada crítico sin permiso, aunque el config.json dé permisos. | Pendiente de diseño | [17](17-constructor-visual-de-agentes.md) (hoy «Pedir Windows Hello» se puede apagar en Ajustes) |
| P19 | El índice vectorial sobre la bitácora, para que el orquestador y los agentes recuperen rápido lo de días anteriores cuando se vacía la caché. «Eso también lo dije en su día, y debería de estar.» | Planificado, adelantado | Tras la tanda 2. Hoy solo hay búsqueda por palabras (FTS5, bm25) sobre la memoria del proyecto; la bitácora no está indexada. Diseño en [17](17-constructor-visual-de-agentes.md) |
| P20 | Auto Doctor (proveedores) ocupa mucho espacio en la pantalla y no hace falta tanto. | Corregido (pendiente de verificar en vivo) | WP-B: panel compacto (una fila por proveedor, detalles plegados), plegado a la derecha y abierto desde «Proveedores» en la barra superior |
| P21 | Documentar todo lo que pida y, al terminar lo pendiente, analizarlo todo y ver cómo implementarlo. | En curso | Este documento |

## 2. Orden de trabajo acordado

1. **Tanda 1:** arreglos del recorrido en vivo, con cinco paquetes en paralelo y revisión independiente antes de integrar:
   - WP-A: tema oscuro y ventanas;
   - WP-B: shell;
   - WP-C: ajustes y vistas;
   - WP-D: motor, plan y verificación;
   - WP-E: interfaz del Orquestador.
2. **Tanda 2:** barras en tiempo real (P3), con un dueño único de todas las barras. Integrada el 2026-11-03 y recorrida en
   vivo ese día (§3, «Recorrido en vivo de la tanda 2»); sus retoques van en la tanda 2b.
3. **Índice semántico** de la bitácora y la memoria (P19).
4. **Revisión contigo** de la propuesta [17](17-constructor-visual-de-agentes.md), con sus decisiones abiertas. Hecha el 2026-11-06: aceptas las recomendadas de las diez (17 §12).
5. **Implementación** por paquetes de lo que decidas de P4 a P18, incluida la Fase 9 (P16).
6. **Las Fases 6, 10 y 7** que aprobaste el 20-10 (orden 8 → 9 → 6 → 10 → 7): P25 a P27 de §4.4.
7. **Recorrido completo** en vivo de cada pantalla, opción y pregunta (P17).

## 3. Hallazgos del recorrido en vivo (2026-10-30)

Recorrido con la app instalada sobre el proyecto de demostración `C:\AgentStudioDemo\calc`. La interfaz se manejó con UI
Automation y capturas, y hubo cinco ejecuciones reales (Claude haiku ejecuta, Codex juzga) y una entrega.

### Fallos graves (tanda 1)

| Id | Hallazgo | Paquete |
|---|---|---|
| B6 | El plan pidió solo «test»; el perfil `dotnet` ejecuta `dotnet test --no-build` en una copia sin compilar. Resultado: «No test projects were found» en los cuatro intentos, con el código correcto. El validador dio el plan por válido | WP-D |
| B10 | El plan del orquestador no tenía tarea de Integración y el validador lo dio por válido. La ejecución terminó aprobada por el juez, pero no se podía entregar | WP-D |
| B8 | «Ejecuciones anteriores» es de solo lectura: tras reiniciar la app no se puede abrir ni entregar una ejecución aprobada | WP-E |
| B2 | Si se cierra la Terminal, no hay forma de volver a abrirla | WP-B · corregido, pendiente de verificar en vivo |
| B1 | «Historial · bitácora» y «Auto Doctor» ocultan su panel cuando está en una pestaña no seleccionada | WP-B · corregido, pendiente de verificar en vivo |
| G1 | No se puede elegir el modelo de cada agente | WP-E |

### Fallos menores y aspecto (tanda 1)

**Ventana y tema**

| Id | Hallazgo | Paquete |
|---|---|---|
| V1 | Barra de título clara sobre una app oscura | WP-A · corregido, pendiente de verificar en vivo |
| V13 | Desplegables, casillas, botones y tooltips con el aspecto claro de Windows | WP-A · corregido, pendiente de verificar en vivo |
| V7 | En el Explorador, solo el texto de la fila responde al doble clic | WP-A · corregido, pendiente de verificar en vivo |
| B5/B9 | `Esc` no responde en el panel de la bandeja ni en la pregunta de salir cuando se abren desde fuera, porque falta el foco inicial | WP-A · corregido, pendiente de verificar en vivo |

**Shell y barra lateral**

| Id | Hallazgo | Paquete |
|---|---|---|
| V2 | La barra de estado dice «Fase 1 · IDE mínimo» | WP-B · corregido, pendiente de verificar en vivo |
| V3 | Gemini, que está aparcado, cuenta como proveedor no listo | WP-B · corregido, pendiente de verificar en vivo |
| V5 | Iconos muertos en la barra lateral: «Agentes» y «Pruebas» | WP-B · corregido, pendiente de verificar en vivo |
| V10 | Anillo de foco alrededor de toda la barra lateral | WP-B · corregido, pendiente de verificar en vivo |

**Documentos y ventana principal**

| Id | Hallazgo | Paquete |
|---|---|---|
| V8 | Las pestañas no marcan los cambios sin guardar | WP-B · corregido, pendiente de verificar en vivo |
| A1 | Pestañas y listas sin nombre accesible | WP-B · corregido, pendiente de verificar en vivo |
| V6 | Bienvenida: tarjeta del proyecto descentrada y texto inútil con un proyecto abierto | WP-B · corregido, pendiente de verificar en vivo |
| V12 | Distribución por defecto apretada | WP-B · corregido, pendiente de verificar en vivo |
| V19 | La ventana no recuerda su tamaño ni su posición | WP-B · corregido, pendiente de verificar en vivo |
| V9 | Foco perdido al cerrar una pestaña | WP-B · corregido, pendiente de verificar en vivo |

**Ajustes, vistas y bandeja**

| Id | Hallazgo | Paquete |
|---|---|---|
| V15 | Ajustes enterrados en Consumo, con tres botones «Guardar» y textos cortados | WP-C · corregido, pendiente de verificar en vivo |
| V11 | Skills: un muro de avisos tapa el catálogo | WP-C · corregido, pendiente de verificar en vivo |
| V14 | Memoria: el buscador no tiene etiqueta | WP-C · corregido, pendiente de verificar en vivo |
| V22 | Menú bloqueado: «desbloquea para verlos» con un solo trabajo | WP-C · corregido, pendiente de verificar en vivo |
| V23 | El nombre del icono de la bandeja queda fijado con el primer tooltip | WP-C · corregido, pendiente de verificar en vivo |

**Orquestador**

| Id | Hallazgo | Paquete |
|---|---|---|
| B3 | Al reabrir se pierden la petición y el plan propuesto | WP-E |
| V24 | Tras reanudarse sola, no se abre su pestaña, y dice «Plan sin aprobar» | WP-E |
| B7 | El monitor no explica por qué falló una tarea | WP-E |
| V17 | La descripción del criterio mide 25 px de ancho | WP-E |
| V18 | Se ve «five_hour/seven_day» sin traducir | WP-E |
| — | La columna de estado del plan se parte sílaba a sílaba | WP-E |

**Motor**

| Id | Hallazgo | Paquete |
|---|---|---|
| V21 | Aviso de «cambios sin confirmar» provocado por la bitácora de la propia app | WP-D |

**Pulido (tanda 2, WP-G, integrada el 2026-11-03)**

| Id | Hallazgo | Paquete |
|---|---|---|
| V25 | Diálogo de aprobación: el título es el identificador crudo «deliver-integration», el proyecto sale como GUID y pone «fichero(s)» | WP-G · corregido y verificado en vivo el 2026-11-03: título en castellano («Entregar a tu rama»), el identificador como línea secundaria, el proyecto por el nombre de su carpeta y «Cambia 1 fichero» o «Cambian N ficheros» ([03](03-pantallas-y-navegacion.md) §3) |
| V26 | Texto del log de «Mantener despierto» | WP-G · corregido, pendiente de verificar en vivo: el log dice por qué se suelta |
| V27 | La memoria dice «user aprobó el plan» | WP-G · corregido y verificado en vivo el 2026-11-03: «La persona aprobó el plan … el 29/10/2026 a las 22:40 UTC», determinista ([03](03-pantallas-y-navegacion.md) §5.3) |
| V29 | Skills: con la ventana a 1280 px, la columna de las recomendaciones se corta por la derecha | WP-G · corregido, pendiente de verificar en vivo: columnas que se adaptan ([10](10-catalogo-de-skills.md) §8) |
| V30 | Skills: un cuadrado claro donde se cruzan las dos barras de desplazamiento | WP-G · corregido, pendiente de verificar en vivo: el rincón va en el color de superficie ([12](12-accesibilidad-tema-oscuro.md) §10) |
| V31 | «Bitácora de hoy» enseña el Markdown crudo («\\(intento 1\\)», «\*\*00:01:27\*\*»), y la cabecera que escribe la app habla del hook y de las líneas «Hecho:» | WP-G · corregido y verificado en vivo el 2026-11-03 (el refresco solo queda en V32): se lee como documento, un día largo enseña sus entradas más recientes con «Ver todo», `Ctrl+P` llega a «Ir al fichero», y la app escribe su propia cabecera ([03](03-pantallas-y-navegacion.md) §2, [11](11-bitacora.md) §1) |
| A1-bis | Las opciones del modelo de un agente se leen en UI Automation como «ModelOption { Label = haiku, … }» | WP-G · corregido en parte: en vivo quedan 11 tipos (V34): más de 20 tipos de elementos de listas dan su etiqueta como nombre accesible ([12](12-accesibilidad-tema-oscuro.md) §10) |

**Pendiente de repartir**

| Id | Hallazgo |
|---|---|
| H1 | Windows Hello sin la persona delante: la aprobación espera, y el diálogo de Windows no admite entrada simulada |

### Recorrido en vivo de la tanda 2 (2026-11-03)

App instalada desde la integración (45e3ec9) y una ejecución real en `C:\AgentStudioDemo\calc`: plan con el orquestador
(haiku), `Calc.Divide` implementado, integración, revisión del juez (Codex) y entrega con aprobación crítica (`main`
avanzó a d5d835d).

**Visto funcionar:**

- **Barra de estado:**
  - siempre visible, con el contexto, la cuota de Claude (5 h y 7 días) y el presupuesto;
  - la cuota caducada sale como pista vacía («se reinició el 30/10 a las 19:20, sin dato nuevo»), con la cifra anterior y
    su antigüedad en la ayuda;
  - recoge en vivo la cuota que anuncia la planificación (de «sin dato nuevo» a 54 % y 55 %).
- **Planificación:** la barra del orquestador pasa de «sin observaciones» a «~10 % estimado» y luego a «10 % exacto»
  (20.193 de 200.000).
- **Monitor:**
  - presupuesto «1 de 12» y «2 de 12»;
  - contexto por tarea, de «~1 % estimado» a «1 % exacto»;
  - el juez de Codex sale «no expuesto», con su motivo.
- **Panel de la bandeja y Consumo:** barras de cuota con su nivel y su antigüedad.
- **Diálogo de entrega (V25):**
  - título «Entregar a tu rama», «Cambian 2 ficheros» y «Origen: proyecto calc»;
  - el identificador como línea secundaria;
  - el foco en «Denegar».
- **Memoria (V27):** «La persona aprobó el plan … el 03/11/2026 a las 07:47 UTC».
- **Bitácora (V31):**
  - la app escribe su cabecera nueva;
  - el panel se lee como documento (sin `\(`), y `Ctrl+P` desde el visor abre «Ir al fichero».

**Retoques (tanda 2b)**

| Id | Hallazgo | Gravedad | Estado |
|---|---|---|---|
| V32 | «Bitácora de hoy» no se refresca sola: tras la ejecución seguía diciendo «No hay bitácora de hoy» hasta pulsar «Actualizar» | Media | Corregido, pendiente de verificar en vivo: el panel vigila la carpeta de la bitácora y vuelve a leer el día solo cuando lleva medio segundo sin cambios (3 s como mucho en una ráfaga sin fin), y a medianoche. Lee siempre con la lectura comprobada, fuera del hilo de la interfaz y de una en una, por una cola. La vigilancia sobrevive a borrar y volver a crear `.agentstudio` (un checkout, la demo creada de nuevo), y se reintenta cada 5 s mientras no está completa. Mientras sigue el mismo proyecto y el mismo día, quien lee el final sigue las entradas nuevas y quien lee más arriba se queda con la misma entrada arriba (también en un día largo); otro proyecto u otra fecha empiezan arriba. Con «Ver todo», un aviso «Hay entradas nuevas» y «Recargar» ([03](03-pantallas-y-navegacion.md) §2) |
| V33 | Durante la planificación, la barra de estado dice «Contexto: sin tareas en curso» aunque el orquestador tiene su barra en vivo: el contexto de lo que corre debe verse siempre (P3) | Media | Corregido, pendiente de verificar en vivo: mientras planifica, la barra de estado muestra «Contexto · orquestador». Si a la vez corre una ejecución, manda la más llena, con el mismo antirrebote y nada mientras la interfaz está bloqueada. Al cerrar la pestaña, la barra suelta su planificación y su monitor. Si algo sigue en marcha, los suelta cuando termina. Si la pestaña se abre otra vez, vuelve a seguirlos |
| V34 | A1-bis incompleto: 11 tipos siguen dando en UI Automation su nombre técnico o el texto del record. En el monitor: `ActivityEntry`, `EvidenceEntry`, `AssessmentRow` y `MonitorTaskViewModel`. En la preparación: `AgentMemoryViewModel` y `RequirementDraftViewModel`. En el lienzo: `ConnectorViewModel`. En los debates: `VerdictPanelViewModel`. En Consumo: `DimensionOption` y `UsageGroupRow`. En Memoria: `MemoryEntryViewModel` | Media | Corregido, pendiente de verificar en vivo: los 11 y otros 25 tipos de elementos de listas dan su nombre en castellano. Una prueba de guarda los busca por reflexión y no deja pasar uno nuevo sin nombre. Cubre también las propiedades estáticas y los tipos de los módulos que guarda una colección de la app ([12](12-accesibilidad-tema-oscuro.md) §10) |
| V35 | El resumen del monitor repite «Terminó bien: 3 tareas completadas y 0 omitidas.» | Baja | Corregido, pendiente de verificar en vivo: el resumen solo sale una vez; debajo solo va si dice algo más que el detalle de la fase |
| V36 | En Consumo, la fila de una cuota caducada repite «se reinició el …» (estado y línea de debajo) | Baja | Corregido, pendiente de verificar en vivo: lo dice solo la barra |
| V37 | Las barras ocultas (sin lectura) se exponen en UI Automation como «: sin dato» | Baja | Corregido, pendiente de verificar en vivo: una barra oculta o colapsada no es control para UI Automation; una visible sin lectura se lee «sin dato» ([12](12-accesibilidad-tema-oscuro.md) §10) |
| V38 | Durante una ejecución la barra de estado salta a dos líneas a 1920 px (el presupuesto baja a la segunda) | Baja | Corregido, pendiente de verificar en vivo, con un límite. Cambios: textos cortos junto a la cifra («57 % exacto · hace 14 min», «se reinició · sin dato»), pistas más cortas y el mensaje de la izquierda limitado a 180 px (con «…» y el texto entero en la ayuda). Con la ruta `C:\Personal Projects\Agent Studio` y textos habituales, las cuatro barras caben en una línea en una pantalla de 1920 px al 100 %, 125 % y 150 %. **Límite:** con los peores textos vistos (una ejecución cargada, «Contexto · T2, la más llena de 3 · ~87 % estimado · compresión recomendada», una cuota reiniciada y un presupuesto) caben al 100 % y al 125 %, pero al 150 % las barras pasan enteras y en orden a una segunda línea. Con la ventana sin maximizar (1859 px al 125 %), caben en una línea incluso con los peores textos. Con 1850 px al 150 %, pasan a dos líneas también con los textos habituales. No se corta ninguna barra. El mensaje de la izquierda (errores, avisos de reanudación) se recorta ahora a 180 px con «…», y el texto entero está en la ayuda. Un lector de pantalla lo lee entero: es su nombre en UI Automation, y avisa cuando cambia (región viva cortés). **Límite:** quien solo usa el teclado no llega a esa ayuda, y en un mensaje largo solo ve el principio |
| V39 | En Consumo, el contexto máximo por grupo y por intento sigue como cifra, sin barra (P3) | Baja | Corregido, pendiente de verificar en vivo: es la barra común, con su origen y su nivel; debajo, los tokens frente a la ventana y el modelo |

### Recorrido en vivo de la Fase 12 (2026-11-05)

App instalada desde `integracion-fase12` (b11fdf1) sobre `C:\AgentStudioDemo\calc`.

**Visto funcionar:**

- La descarga del modelo, con su confirmación: qué se descarga, de dónde (las dos URL fijadas de huggingface.co), cuánto
  pesa (123,4 MB), dónde se guarda y que se comprueba la huella. El índice calculó los 58 fragmentos en segundo plano y
  el panel lo dijo («Activa…»).
- La búsqueda por significado en «Memoria»: «cuándo el revisor independiente dio por buena la división» trae primero las
  revisiones independientes del juez, «Coincide por palabras y por significado».
- La confianza de la bitácora: confiar en un día y retirarla cambia el estado y el recuento.
- «Búsqueda en el historial para el orquestador» en «Ajustes», activada por defecto.
- Una planificación real con la herramienta disponible y sin aviso; el orquestador prefirió leer los ficheros. V33: la
  barra de estado mostró «Contexto · orquestador» mientras planificaba. V32: «Bitácora de hoy» mostró la entrada nueva
  sin «Actualizar», con el ancla `2026-11-05/P-0001`.

**Retoques (Fase 12b)**

| Id | Hallazgo | Gravedad | Estado |
|---|---|---|---|
| V40 | En «Memoria», las entradas que escribió Agent Studio antes de la Fase 12 (sin recibo) dicen «no la escribió Agent Studio»: debería decir que no se puede comprobar quién la escribió | Media | Corregido, pendiente de verificar en vivo: toda entrada de la bitácora sin recibo dice «En cuarentena: no se puede comprobar quién la escribió. Acéptala, o confía en su día o en toda la bitácora», y su origen, «Autor sin comprobar» (antes, «Otro autor»). No se afina por el formato de la línea (`P-NNNN` o la del hook): se puede copiar, así que no comprueba nada. El panel «Bitácora» dice que lo que no se puede comprobar espera en cuarentena, también lo que Agent Studio anotó antes de dejar recibos, y su botón habla de «las entradas cuyo autor no se puede comprobar» ([03](03-pantallas-y-navegacion.md) §5.3) |
| V41 | Las entradas de la bitácora en «Memoria» enseñan el Markdown crudo (`**04:22:31**`, `\(intento 1\)`), como pasaba en «Bitácora de hoy» antes de V31 | Baja | Corregido, pendiente de verificar en vivo: la tarjeta lee la entrada con el mismo lector de V31 (`MarkdownLite`) y la enseña sin marcas ni barras, con su línea «Resultado» o «Hecho» debajo y «•». Solo cambia lo que se enseña: el texto anclado, la búsqueda y lo que llega a los agentes siguen igual; las demás categorías se enseñan como están |
| V42 | Tras descargar el modelo, el aviso verde «El índice calculará… hasta entonces, la búsqueda va por palabras» se queda aunque el estado de arriba ya diga «Activa» | Baja | Corregido, pendiente de verificar en vivo: el aviso desaparece cuando se lee el estado del índice (tras `IndexRefreshedAsync`) y ya hay fragmentos con su significado; el estado de arriba dice cuánto lleva. Un fallo, una cancelación o «No se ha descargado nada» se quedan |

**Para la Fase 14 (lenguaje sencillo, P12):**

- la bitácora del producto escribe «Succeeded» y «claude-code/-» (modelo predeterminado);
- el párrafo del diálogo de entrega es denso;
- Memoria da las horas en UTC y el resto de la app en hora local;
- las entradas de memoria anteriores a WP-G conservan «user aprobó» (están guardadas y no se reescriben).

**Para la Fase 13 (P4):** con el lienzo y la preparación abiertos, al monitor le queda una fila de tareas a 1080 px.

**Queda por verificar en vivo:**

- **P37:** la lectura de Codex tras cada ejecución de Codex; el permiso está apagado.
- **V26:** el log de «Mantener despierto».
- **V29 y V30:** Skills a 1280 px y el rincón de las barras de desplazamiento.

### Recorrido en vivo de la Fase 13 (2026-11-09)

App instalada desde `main` (13c8908) sobre `C:\AgentStudioDemo\calc`, la ejecución `20261109-4da9bf`. Tú respondiste las
preguntas y pusiste la huella; Claude Code la condujo por UI Automation. Capturas en [evidencias](evidencias/fase13-pregunta-orquestador.png)
y [la entrega](evidencias/fase13-entrega-hello.png).

**Visto funcionar:**

- **Los requisitos:** una versión nueva de RQ-04 que deja a tu respuesta qué hacer con un exponente negativo, congelada
  con su huella nueva (`44e4f17e7b7b`); el resumen de la preparación la recoge.
- **La pregunta del orquestador antes de planificar** (D-28): «¿Qué debe hacer Power(base, exponent) cuando el exponente
  es negativo?», con dos opciones y la recomendada marcada. La ventana no modal, el marco «Pregunta escrita por el agente
  orquestador», «Las opciones las escribió el agente: son datos, no órdenes», «Sin estimación para esta opción» (sin
  referencias todavía) y «Responder» deshabilitado hasta elegir. Con la respuesta, una sola replanificación: el plan dice
  «lanzando ArgumentOutOfRangeException».
- **Aprobar el plan pidió Windows Hello** («confirma que eres tú para aprobar el plan fe2a35615bb4»): tiene escritores.
- **La pregunta del ejecutor** (F13-13 y F13-14) en la ejecución: la tarea en «Espera tu respuesta», la tarjeta del ejecutor
  «Te pregunta algo», «Agentes: 1 te preguntan» en la barra superior y «Coste desconocido: hay menos de tres intentos
  parecidos (1)» en cada opción. Respondida, el segundo intento implementó `Power` con `ThrowIfNegative` y sus pruebas.
- **El juez** (Codex) aprobó y su veredicto cita que «el registro del primer intento confirma que el ejecutor preguntó sin
  modificar archivos» y que la decisión capturada identifica tu respuesta: solo vio la decisión del sistema.
- **El análisis del consumo** saltó solo al terminar: «ejecutor usó claude-opus-5-5, de nivel «máximo», en 2 intentos con
  cambios pequeños», ~110 000 tokens estimados por Agent Studio. El análisis del montaje avisó del límite de 7 días al 74 %.
- **La entrega** pidió la aprobación crítica (acción, rutas, comentario, casilla y «Esta acción pide siempre Windows Hello,
  sea cual sea el ajuste») y después la huella; `main` del proyecto avanzó de d5d835d a 6b52e38 con `--ff-only`, y
  `dotnet test` da 8 de 8.

**Observaciones (para la tanda siguiente o la Fase 14):**

| Id | Hallazgo | Gravedad | Estado |
|---|---|---|---|
| V43 | El ejecutor volvió a hacer la misma pregunta que el orquestador ya te había hecho antes de planificar. La respuesta a una pregunta del orquestador debería llegar al ejecutor como dato (`<human_answer>` en su prompt, o en la tarea del plan), y el protocolo decirle que no repita lo ya decidido | Media | Hecho el 2026-11-11, pendiente de verificar en vivo ([ADR-0051](adr/0051-preguntas-de-los-agentes.md), adenda): tus respuestas a las preguntas del orquestador llegan al prompt del ejecutor de cada tarea como bloques `<human_answer askedBy="orchestrator">` de datos, con la regla de no volver a preguntar lo que ya decidiste. Llegan también con el plan editado en el lienzo (`PlannedIn` en `RunStarted`, solo si la petición y los requisitos son los de la planificación) y al reanudar. El juez no las recibe de este camino (ver P54). Que el modelo deje de preguntar solo lo dirá una demostración en vivo |
| V44 | Las opciones del ejecutor llegaron en inglés («Throw ArgumentOutOfRangeException», «Return 0») aunque la petición y los requisitos están en castellano: el protocolo del ejecutor no le pide el idioma de la persona | Baja | Hecho el 2026-11-12, pendiente de verificar en vivo ([ADR-0054](adr/0054-idiomas-y-lenguaje-claro.md), adenda; docs/04 §22.6): el idioma que eliges en «Ajustes» llega como dato `<response_language>`, con una regla fija, a todos los agentes que escriben para ti: ejecutor, planificador, juez y auditoría, debatientes, chat de preparación, revisión del encargo, análisis del consumo, ayuda y recomendador de skills. No depende de la cultura del sistema ni entra en nada anclado, y los esquemas del juez ya no piden castellano |
| V45 | La tarjeta del ejecutor decía «Contexto: 1 % · exacto» con la pregunta en el aire, y el inspector del orquestador enseña las rutas absolutas de la copia `_base` (con el nombre de usuario) en lo que hace | Baja | Nuevo |

### Verificado en vivo (Fase 8, lista de docs/03 §8)

Pasos 1, 2, 3, 4, 5, 7, 8, 9, 10, 12 (log), 14, 18 (inactividad), 21, 22 y 23, y la entrega con aprobación crítica (`main`
avanzó con `--ff-only`). Quedan para ti los pasos 6, 11, 13, 15, 17 (confirmar con huella y cancelar), 18 (Windows), 19
y 20. El paso 16 (teclado y Narrador) solo se comprobó en parte, por los nombres accesibles. Lo visto en vivo se
llevará a [08](08-plan-y-criterios-de-aceptacion.md) al integrar la tanda 1.

## 4. Revisión de lo pedido desde el inicio

> Revisión del 2026-10-30. Fuentes:
> - la bitácora del 15 al 30-10;
> - tus 47 mensajes de la sesión (34 directos y 13 escritos mientras trabajaba un agente), sin resultados de herramientas;
> - el [prompt maestro](referencias/prompt-maestro.md) y el texto de la ampliación del 20-10 ([16](16-ampliacion-plataforma-de-proyectos.md));
> - [08](08-plan-y-criterios-de-aceptacion.md), los documentos 09 a 16 y los ADR.
>
> Las horas son locales.

**Estados:**
- **hecho:** 08 lo da por «Cumplido» o la bitácora por verificado;
- **parcial:** falta algo. Aquí cuenta lo que 08 marca «Cumplido según tests» o «Integrado; pendiente tu demostración»;
- **no hecho:** con su fase, si la tiene;
- **aplazado:** por tu decisión o por diseño;
- **descartado**;
- **respondido:** era una pregunta.

### 4.1 Peticiones por día

**15-10**

| Hora | Petición | Estado | Evidencia | Qué falta |
|---|---|---|---|---|
| 10:02 | Prompt maestro y dashboard de referencia: los 12 entregables de la sección 19 y la Fase 0 | Hecho | 09 §1: F0-1 a F0-7, 593/593 pruebas. README enlaza los 12 entregables | El boceto a mano que cita el prompt no se aportó (09 §6). El resto del prompt, en §4.2 |
| 10:14 | Lanzar varios agentes «sin miedo» | Hecho | Memoria del proyecto | — |
| 11:00 | Uso personal únicamente | Hecho | ADR-0003 | — |
| 11:00 | Instalar Gemini CLI | Hecho, y después aplazado | 09 §7; F2-8; ADR-0009 | Solo funciona con una API key tuya |
| 11:00 | Índice de las mejores skills de la comunidad, GitHub, foros y fuentes oficiales | Parcial | Importa tu índice local: `catalog.json`, 182 entradas (10 §3) | Dar de alta skills desde repositorios oficiales, de la comunidad y propios: «Diseño» en 10 §3, sin fase (P30) |
| 11:00 | Carpetas por sector TI y tags | Parcial | 10 §2; F4-7 «según tests» | Tu demostración de F4-7 |
| 11:00 | Que los agentes las recomienden según lo pedido y el contexto | Hecho | F4-8: determinista, y asistida por el MCP local con D-30 medida en real | — |
| 11:00 | Activarlas y desactivarlas al gusto | Parcial | F4-7; 10 §4 | Tu demostración |
| 11:00 | Indicador de salud o doctor | Parcial | Salud local: cabecera, tamaño, licencia y fijación (10 §6) | `upstream`, `archived`, `scripts` y el coste del cuerpo: F6-6 |
| 11:00 | Commit inicial en tu GitHub privado | Hecho | `LargoLujan/agent-studio`, 530 commits | — |
| 11:00 | Bitácora obligatoria: cada prompt, con fecha, hora y tags, un fichero por día | Hecho | F2-9; 11; ADR-0008; hook activo | V21 (la propia bitácora dispara el aviso de cambios sin confirmar), en WP-D |
| 11:00 | «El resto, lo que recomiendes, sumamente profesional» | Hecho | 09 §7: kit y `CLAUDE.md` | — |
| 11:22 | Usar todos los agentes que hagan falta | Hecho | Memoria del proyecto | — |
| 11:37 | «1. Sí [a las skills candidatas], y busca más. 2. ok. 3. Sí. 4. Empieza por las fases que veas» | Hecho según la bitácora, con documentos sin actualizar | Bitácora: 181 skills en 16 sectores, «refs en los links, sin el fichero test». Arrancan F1 y F2 | 10 §3 aún dice 119 skills y 15 sectores. README e `investigacion/2026-10-15-candidatas-skills.md` dicen «pendiente de aprobación» (P30) |
| 15:41 | «Hazlo con commit y todo» | Hecho | Bitácora: 4 commits más los documentos, con el correo noreply | Reescribir los 5 commits que llevan tu correo personal: pendiente de tu OK desde ese día (P35) |
| 15:48 | ¿Cómo inicio Gemini en el terminal? | Respondido | Bitácora | — |
| 15:50 | Error de inicio de sesión en Gemini | Aplazado: Google aparcado, decisión tuya | ADR-0009; F2-8 | Los debates con tres vendors esperan una API key |
| 16:04 | «Sí, y ¿qué sigue?» | Hecho | ADR-0009 publicado | — |
| 16:10 | Lanzar los agentes «a máxima potencia» | Parcial | «Cumplido»: F1-1, F1-3, F1-4, F1-6, F1-7 y F2-1 a F2-10 (Claude y Codex) | F1-2 y F1-5: tu demostración |
| 17:55 | La ventana «Cambios sin guardar» no paraba de salir | Hecho | Bitácora | — |
| 21:09 | «continua» | Hecho | Reanudada la Fase 3 | — |

**16-10**

| Hora | Petición | Estado | Evidencia | Qué falta |
|---|---|---|---|---|
| 08:16 | ¿Qué queda o por dónde vas? | Respondido | Fase 3 integrada en `main` (bc763ba) | — |
| 11:16 | Parar hasta nuevo aviso | Hecho | Bitácora | — |
| 12:27 a 17:54 | «continua» (dos veces) y «sigue donde lo dejaste» | Hecho | Primer E2E real de F3-8; publicado hasta 22878ac | — |

**17-10**

| Hora | Petición | Estado | Evidencia | Qué falta |
|---|---|---|---|---|
| 09:30 y 14:42 | «continua» (dos veces) | Hecho | Rondas 2 y 3 de Codex; N-01 a N-07 (08 F3); ADR-0018 | — |

**18-10**

| Hora | Petición | Estado | Evidencia | Qué falta |
|---|---|---|---|---|
| 00:27 | Continúa, y dime por dónde vamos del plan de fases | Hecho | Rondas 4 a 9. Seguridad de la Fase 3 cerrada al aceptar tú los residuales (08 F3; ADR-0018) | La respuesta sobre el plan no consta en la bitácora. F3-6: falta la demostración en la app |
| 13:44 | «Dale sin problema» (Fase 4) | Parcial | 14; ADR-0019 a 0025; integrada en 4980294. «Cumplido»: F4-2, F4-4, F4-5, F4-6, F4-8 y F4-9 | F4-1, F4-3 y F4-7 van «según tests»: falta tu demostración. Pendientes técnicos de 08 F4 (P31) |
| 18:10 | ¿Cuándo estará listo para terminar y probarlo? | Respondido | «Ya se puede probar con `dotnet run`; el instalador es F7-6» | F7-6 sigue sin hacer (P27) |
| 20:58 y 21:00 | «Continua» y «Ya son las 9 pm» | Hecho | Reanudado tras el límite | — |

**19-10**

| Hora | Petición | Estado | Evidencia | Qué falta |
|---|---|---|---|---|
| 07:57 | «Vamos a la fase 5» | Parcial | Cerrada el 21-10 (9579f8c). «Cumplido»: F5-1, F5-2 y F5-4 a F5-7; F5-3, «según tests». Demostraciones en `evidencias/fase5-demostraciones.md` | Tu recorrido (03 §8, lista de la Fase 5, 13 pasos). 93 pruebas que fallan a integridad Low (P23). Pendientes de 08 F5 (P31) |
| 12:51 | «continua» | Hecho | — | — |

**20-10**

| Hora | Petición | Estado | Evidencia | Qué falta |
|---|---|---|---|---|
| 07:58 | «continua» | Hecho | — | — |
| 10:50 y 10:56 | Analizar la ampliación de 19 secciones: añadir lo útil, cambiar la perspectiva o descartar | Hecho (análisis y plan) | 16; ADR-0033; tus decisiones de 16 §7 | La implementación: tabla siguiente |
| 11:01 | Las carreras se resuelven con colas de mensajes | Hecho | Memoria del proyecto. Las reanudaciones de la Fase 8 van por una sola cola (08 F8) | — |
| 22:24 y 22:25 | «continua»; que sigan los agentes parados | Hecho | — | — |

**La ampliación del 20-10, por sección**

| § | Pide | Estado | Evidencia | Qué falta |
|---|---|---|---|---|
| 1 | Análisis inicial y ejecución | Hecho | 16 §2.1 | — |
| 2 | Aislamiento y seguridad por proyecto | No hecho (Fase 7) | F7-4, F7-7 y F7-8. Descartado: la red por destino y las credenciales de los CLIs por proyecto (ADR-0033) | Toda la Fase 7 |
| 3 | Entornos reproducibles | No hecho | Detectarlos: F6-7. Gestionarlos: F11-6, aplazada | — |
| 4 | Contenedores y bases de datos | Aplazado | Fase 11 (ADR-0033, punto 4) | — |
| 5 | Direcciones locales y pruebas de APIs | Aplazado; una parte descartada | F11-7. Descartado: `hosts`, la red local e Internet | — |
| 6 | Autenticación y bloqueo con Windows Hello | Parcial (integrado) | F8-10 y F8-11; ADR-0037. Tu decisión 5 (a): sin Hello al abrir la app | Pasos 17, 18 («Bloquear Windows») y 19 de tu demostración. P18 lo endurece |
| 7 | Segundo plano y bandeja | Parcial (integrado) | F8-7 a F8-9. Vistos en vivo el 30-10: pasos 1 a 5, 7 a 10, 22 y 23 | Pasos 6, 11, 15 y 16 |
| 8 | Gestor de trabajo estilo Jira | No hecho (Fase 9) | F9-1 a F9-10 | Toda la fase (P16) |
| 9 | Diagramas y documentación | No hecho (Fase 6) | F6-10 y F6-11; D-22 | — |
| 10 | Proveedores Git | No hecho (Fase 10) | F10-1 a F10-10. GitLab y Bitbucket, aplazados | — |
| 11 | Trazabilidad | Parcial | La cadena ya está en el registro. Vínculos y cadena navegable: F9-3 y F9-4. Tras un squash: F10-7 | Las vistas |
| 12 | Participación y supervisión | Parcial | Está en el registro; la vista es F9-5 | La vista |
| 13 | Consumo, costes y estimaciones | Parcial | F4-1 y F4-2; F9-6 a F9-8. Descartado: el coste imputado | Por hito y por actividad, la estimación y los presupuestos |
| 14 | Límites y reanudación automática | Parcial (según tests) | F8-1 a F8-4. El paso 21 se vio en vivo | Paso 20, con un límite real; capturas reales (P32) |
| 15 | Contexto y recuperación de sesiones | Parcial (según tests) | F8-6; ADR-0035 | — |
| 16 | «Repositorio y cambios» | No hecho (Fase 9) | F9-9; el PR, en F10-8. Descartado: un cliente git completo | — |
| 17 | Trabajo simultáneo y sincronización | Hecho en local; lo remoto, no | F3-2; F10-6. Descartados los webhooks | — |
| 18 | Cierre de hitos con informe | No hecho (Fase 9) | F9-10 | — |
| 19 | Validación y entrega | Repartido entre los criterios | 16 §2.19; F7-5 | La prueba de extremo a extremo de la cadena entera |

**21-10**

| Hora | Petición | Estado | Evidencia | Qué falta |
|---|---|---|---|---|
| 09:21 | «Seguimos con la siguiente fase» (Fase 8) | Parcial (integrada) | Ver el detalle debajo | Ver el detalle debajo |

- **Lo que dice 08 F8:**
  - F8-1 a F8-6, F8-9 y F8-11: «según tests»;
  - F8-7, F8-8 y F8-10: «pendiente tu demostración»;
  - ADR-0034 a 0037.
- **Visto en vivo el 30-10 (§3):**
  - pasos 1 a 5 y 22 (F8-7);
  - paso 4 (F8-8);
  - pasos 7 a 10 (F8-9);
  - paso 12, solo el log (F8-5);
  - pasos 14, 18 (por inactividad) y 23 (F8-10). En el paso 14 se vio el bloqueo; para el desbloqueo con Hello, ver H1;
  - paso 21 (F8-4).
  08 todavía no recoge nada de esto.
- **Lo que queda de tu demostración:**
  - pasos 6 y 15 (F8-7);
  - paso 11 (F8-9);
  - paso 13 (F8-5);
  - paso 16, teclado y Narrador;
  - pasos 17 y 19 (F8-11);
  - paso 18, «Bloquear Windows» (F8-10);
  - paso 20 (F8-2 y F8-3).
- **Además:** pendientes técnicos (P31 y P32).

**25-10**

| Hora | Petición | Estado | Evidencia | Qué falta |
|---|---|---|---|---|
| 21:10 | «Continua» | Hecho | — | — |
| 21:48 | ¿Terminaron los agentes que salen en rojo? | Respondido | Los paró el límite del 20-10; su investigación la hizo el agente de 16 | — |

**29-10**

| Hora | Petición | Estado | Evidencia | Qué falta |
|---|---|---|---|---|
| 17:16 | Seguir con lo que cortó el límite | Hecho | Fusiones de la Fase 8; suite con 0 fallos | — |
| 23:24 | Instálalo, toma el control y haz las pruebas (P1) | Hecho | Comprobado: `%LOCALAPPDATA%\Programs\AgentStudio` (29-10, 23:33) y `Agent Studio.lnk` en el menú Inicio | Es una publicación a mano, no F7-6. Falta su línea «Hecho:» en la bitácora |
| 23:29 | Probar todos los elementos del IDE (P2) | En curso | §3 | — |

**30-10**

| Hora | Petición | Estado | Evidencia | Qué falta |
|---|---|---|---|---|
| 00:27 a 00:51 | Barras, constructor visual, idiomas, textos, ayuda, documentación para clientes, Jira, pruebas, Hello, índice vectorial, Auto Doctor compacto, documentarlo todo y esta revisión | P3 a P21 | §1 | Ninguna entrada del 30-10 tiene todavía su «Hecho:» |

### 4.2 Lo que pide el prompt maestro y falta

| Sección | Lo que falta | Estado | ¿Tiene sitio? |
|---|---|---|---|
| Referencias (parte 3, §1) | El boceto a mano | No aportado | — (09 §6) |
| §3 Proveedores | Gemini CLI | Aplazado (ADR-0009) | Se retoma con una API key |
| §3 | API directa como alternativa opcional | No hecho | «No está planificada» (06 §5) |
| §4 Barra superior | Selector de proveedor y modelo | No hecho | P7 (WP-E, por agente) |
| §4 Barra superior | Estado de los agentes | No hecho | Sin sitio (cerca de P5) |
| §4 Barra superior | Configuración global | Parcial: «Ajustes» está dentro de «Consumo» | V15 (WP-C) |
| §4 Barra lateral | Git | No hecho | F9-9 y F10-8 |
| §4 Barra lateral y zona central | Agentes y gestión de agentes | No hecho. El icono dice «disponible en la Fase 3», y la plantilla solo deja cambiar la memoria de cada agente | V5 (WP-B); P7 y P10 |
| §4 Barra lateral y zona central | Auto Graph y grafo de dependencias | No hecho | F6-3 a F6-5 |
| §4 Barra lateral | Pruebas | No hecho: el icono dice «disponible en la Fase 7» | V5. Ningún criterio de la Fase 7 lo cubre |
| §4 Barra lateral | Herramientas | No hecho | Sin sitio |
| §4 Zona inferior | Salida de procesos, errores, depuración y resultados de pruebas | No hecho: solo hay terminal | Sin sitio |
| §4 Panel derecho | Chat con el orquestador | No hecho; aplazado en 13 §1 sin fase | Sin sitio. No es el chat de ayuda de P14 |
| §4 Panel derecho | Estado de tareas, actividad de agentes y alertas del juez | Parcial: están en el monitor, la banda del auditor y la bandeja (F8-8), no en un panel | — |
| §4 | Guardar diseños por proyecto | Parcial | F1-5: tu demostración |
| §5 Editor | Autocompletado | Sin criterio y sin comprobar | Sin sitio |
| §5 Editor | Integración con Git y ver los cambios que proponen los agentes | No hecho | F9-9 y F9-4 (el diff dentro de la cadena) |
| §5 Terminal | Terminales independientes y persistentes | Parcial: hay una sola, y no se reabre si la cierras | B2 (WP-B) |
| §6 Orquestador | Nodo de debate | Aplazado (tu decisión 3 de 15 §12) | — |
| §6 | Configurar cada agente desde la interfaz (rol, proveedor, modelo, instrucciones, herramientas, presupuesto…) | No hecho: existe en el dominio y en la plantilla, no en la interfaz | P7 y P10 |
| §6 | Política de memoria con propuestas del agente | Aplazado (14 §1, 15 §1) | Sin fase |
| §9 Auditor | Auditar al orquestador en mitad de una ejecución | Aplazado (15 §1) | Sin fase |
| §10 Contexto | Compactaciones | Sin verificar con una captura real (14) | — |
| §11 Consumo | Filtros del informe y tu demostración | Parcial | F4-1; pendientes de 08 F4 (P31) |
| §12 Auto Doctor | Dependencias, configuración del proyecto, herramientas, permisos, errores frecuentes, runtimes y Git; correcciones reversibles | No hecho. Hoy comprueba CLIs, versiones y sesión | F6-1 y F6-2 |
| §13 Auto Graph | Todo | No hecho | F6-3 a F6-5 y F6-10 |
| §14 Memoria | Tus notas (arquitectura, convenciones, soluciones) | No hecho | Sin fase (08 F4) |
| §14 | Recuperación selectiva | Hecho por palabras (FTS5, F4-5), no semántica | P19 |
| §15 Seguridad | Política de red, defensa contra inyección de instrucciones, credenciales cifradas y aislamiento fuerte | No hecho | F7-3, F7-7 y F7-4 |
| §15 | Separación de entornos | Aplazado | Fase 11 |
| §16 Azure y Fabric | Paquete opcional de herramientas y agentes | Fuera de alcance «hasta nuevo aviso» (08) | Sin fase. La condición, «cuando el núcleo esté en la Fase 5», se cumplió el 21-10 (P33) |
| §18 Plan | Fases 6 y 7, con el empaquetado y las actualizaciones | No hecho | F6 y F7 (F7-6) |
| Parte 4, punto 3 | Validación final en un entorno aislado | Parcial: integridad Low | F7-4 |
| Parte 5 | «Auditar proyecto completo» | Hecho (F5-5), pero sobre este repositorio no puede aprobar | P23 |
| Lo mandado «a la Fase 7» | Firma del registro (ADR-0004); lector de pantalla, teclado, alto contraste y zoom (12 §7); purga de la memoria (ADR-0022) | No hecho | Sin criterio en la Fase 7 (P28) |

### 4.3 Lo que dices haber pedido «en su día»

| Lo que dices | ¿Consta antes del 30-10? | Dónde se buscó y qué hay |
|---|---|---|
| El índice vectorial de la bitácora (P19): «Eso también lo dije en su día, y debería de estar» | No consta en lo escrito | Ver el detalle debajo |
| Decidir en el lienzo cuántos agentes, «como dijimos en su día» (P10) | Consta | Prompt maestro §6 (un nodo por agente, cada uno con su configuración) y parte 5 («ampliaría a más agentes»). 13, decisión 7: plantilla por defecto de tres agentes, «configurable». Hoy la interfaz no deja añadir ni quitar agentes: solo cambiar la memoria de cada uno |
| El Jira «que dijimos» (P16) | Consta | Ampliación §8; 16 §2.8; ADR-0033; Fase 9 (F9-1 a F9-10), sin empezar |

Detalle del índice vectorial:

- **Términos buscados:** `vectorial`, `embedding`, `semántic`, `sqlite-vec` y `RAG`.
- **Dónde:** los 10 días de bitácora, tus 47 mensajes, las respuestas del asistente (solo lo nombran después de tu mensaje
  de las 00:47 del 30-10), el prompt maestro, el texto de la ampliación, `docs/`, `docs/adr/` y `src/`. Pudo decirse en
  un diálogo de preguntas o fuera de estas fuentes; da igual: se trata como un hueco que hay que cerrar (P19, adelantado).
- **Lo más cercano que sí pediste:**
  - «Permitir recuperar información selectivamente» (prompt §14): hecho con FTS5 en F4-5;
  - el «resumen estructurado» antes de renovar el contexto (ampliación §15): F8-6.
- **Choque con el diseño actual:** la bitácora nunca llega a un prompt, porque su resumen diario queda fuera del
  empaquetado (11 §6, ADR-0023). P19 enmienda ADR-0023.

### 4.4 Pendientes reales

Ordenados por importancia. «Ya en 18» es lo que ya recogen §1 a §3.

| Id | Pendiente | Prioridad | Dónde |
|---|---|---|---|
| P2 | Fallos graves del recorrido (B6, B10, B8, B2, B1 y G1) y los menores | Alta | Ya en 18 (§3, tanda 1) |
| P22 | Escribir el documento 17 | Hecho | [17](17-constructor-visual-de-agentes.md), integrado el 2026-10-30: 65 decisiones (D-01 a D-65), las Fases 12 a 15 y las decisiones abiertas de su §12, pendientes de repasarlas contigo (paso 4 de §2) |
| P36 | Las ventanas de contexto aprendidas no se usan en vivo: ningún código crea `ModelWindowCatalog` ni `ContextHealthTracker`, y Claude solo informa la ventana al terminar el intento. Durante un paso no hay porcentaje | Hecho (verificado en vivo el 2026-11-03; retoques V32 a V39 en §3) | Tanda 2 (WP-F), integrada el 2026-11-03: un `ContextHealthTracker` por intento en el monitor y otro en la planificación, con las ventanas de cada proyecto leídas fuera del hilo de la interfaz y la tabla base debajo (adenda de ADR-0021) |
| P37 | La lectura de la cuota de Codex «una vez tras cada ejecución», que promete el ajuste, no existe: solo se lee a petición y al esperar un límite | Hecho (pendiente de verificar en vivo) | Tanda 2 (WP-F), integrada el 2026-11-03: implementada. El final de cada ejecución de Codex la pide (`IAgentRunEnds`), solo con el permiso y como mucho una vez cada 5 minutos, y las lecturas a la vez son una (`SharedQuotaReader`; adenda de ADR-0020) |
| P38 | Hay dos aprobaciones fuera de `ApprovalGate`: el consentimiento de skills (no deja `ApprovalGranted`) y «Aprobar plan» del lienzo. Con el ajuste apagado, lo crítico lo decide solo el diálogo, y lo destructivo nunca pide Hello | Alta | Hallazgo de 17 §0.2. Cruza con P18 (Hello siempre) |
| P39 | «Reintentar» no envía texto al agente bloqueado, así que hoy no hay forma de contestar su duda. Y cinco campos de `AgentDefinition` no gobiernan nada: `PathGrants`, `SystemInstructions`, `Objectives`, `MaxRounds` y `MaxTotalTokens` | Media | Hallazgo de 17 §0.2. Cruza con P9 (preguntas) y P10 (configurar agentes) |
| P23 | Las 93 pruebas que fallan a integridad Low. Hasta adaptarlas, «Auditar proyecto completo» no puede aprobar este repositorio | Hecho el 2026-11-12 | [ADR-0017](adr/0017-verificacion-aislada-baja-integridad.md), adenda: a integridad Low, con el filtro de la auditoría, pasan 8938 de 8940 (las 2 que no, de tiempo, pasan solas). Las causas eran la carpeta de ejecuciones en `%LOCALAPPDATA%` (ahora inyectable, `RunTempFolderOptions`, y los adaptadores validan antes de crearla) y pruebas que componían la app con las anclas reales (ahora `TestLocalData`, y ninguna prueba escribe ya en tu LocalAppData). No queda ninguna prueba marcada «necesita Medium». La auditoría dice en la petición que apruebas y en el expediente del juez qué pruebas no ejerce y por qué: `LocalTools` (la seguridad de git y del aislamiento), `ConPty`, `Measurement` y `ProviderLive`. El CI no pierde ninguna prueba |
| P3 | Barras de consumo y contexto en tiempo real | Hecho (verificado en vivo el 2026-11-03; retoques V32 a V39 en §3) | Ya en 18. Tanda 2 (WP-F), integrada el 2026-11-03 |
| P19 | Índice semántico de la bitácora y la memoria | Hecho (integrado el 2026-11-05 en `integracion-fase12`, pendiente de pasar a `main`) | Fase 12: la bitácora como memoria anclada con su confianza, la búsqueda híbrida (bm25 y significado con multilingual-e5-small, descargado solo con tu OK), «Memoria» con la búsqueda por significado y `search_project_history` en el planificador. F12-4 con el criterio que decidiste el 04-11, medido también sobre la bitácora real (23/40 frente a 17/40). Enmienda ADR-0023 (§4.3); ADR-0047 |
| P24 | Tus demostraciones (lista debajo), y llevar a 08 lo visto en vivo el 30-10 | Alta | Nuevo. Cruza con P17 |
| P16 | Fase 9 (F9-1 a F9-10) | Alta | Ya en 18 |
| P4 a P15 y P18 | Constructor visual, modelos, configurador, preguntas, idiomas, textos, ayuda, documentación para clientes y Hello siempre | Alta | Decidido el 2026-11-06 (17 §12, todas las recomendadas). Fase 13 en marcha; luego la 14 y la 15 |
| P25 | Fase 6 (F6-1 a F6-11): Auto Doctor completo, Auto Graph, el doctor de skills (lo que falta del «indicador de salud» del 15-10), el entorno y los diagramas | Media | Nuevo. Cruza con P20. Diseño en [20](20-fase-6-auto-doctor-y-auto-graph.md) (propuesta del 2026-11-12, con quince decisiones tuyas en su §13; su paquete WP-0, el arreglo del localizador de ejecutables y de las sondas, se puede hacer ya) |
| P26 | Fase 10 (F10-1 a F10-10): GitHub por `gh`, PR y sincronización | Media | Nuevo |
| P27 | Fase 7 (F7-1 a F7-8), con el empaquetado y las actualizaciones (F7-6, que P1 no cubre) y la bóveda DPAPI | Media | Nuevo. Es lo que queda de «¿cuándo estará listo?» (18-10) |
| P28 | Lo mandado «a la Fase 7» sin criterio: la firma del registro (ADR-0004, 15 §1), la accesibilidad completa (12 §7, F1-6) y la purga real de la memoria (ADR-0022, 14 §1). Darles criterio o descartarlos con un ADR | Media | Nuevo |
| P29 | Lo que §4 del prompt pide y no tiene sitio: el chat con el orquestador (13 §1); los paneles de salida, errores, depuración y resultados de pruebas; «Herramientas»; «Pruebas»; la gestión de agentes; el estado de los agentes en la barra superior | Media | Nuevo. Cruza con P7, P10 y P14 |
| P30 | Catálogo de skills: dar de alta desde repositorios oficiales, de la comunidad y propios (10 §3, «Diseño»). Y poner al día 10 §3, el README e `investigacion/2026-10-15-candidatas-skills.md` | Media | Nuevo. Cruza con P8 |
| P31 | Pendientes técnicos sin fase en 08 (lista debajo) | Media-baja | Nuevo. D-12 cruza con P3 |
| P32 | Capturas y mediciones reales: el `rate_limit_event` con `rateLimitType` de Claude, el límite y la sesión caducada de Codex, una suspensión real. Repetir D-30 y el confinamiento de Claude si cambia su versión (hoy, 2.1.290) | Baja | Nuevo |
| P33 | Paquete Azure y Microsoft Fabric (prompt §16): decidir si entra y en qué fase | Baja; decides tú | Nuevo |
| P34 | Revisar lo aplazado (lista debajo) | Baja; decides tú | Nuevo. Viene de 13 §1, 14 §1, 15 §1, 06 §5, ADR-0033 y 16 §7 |
| P35 | Los 5 commits que llevaban tu correo personal como autor | Hecho el 2026-11-03 | Reescribiste el historial de `main` en un solo commit (ff402ed) con el correo noreply y la licencia MIT |
| P40 | Mejoras de la búsqueda híbrida vistas sobre la bitácora real (05-11): la fusión RRF deja ganar siempre a lo que está en las dos listas; el corte calibrado con datos sintéticos deja pasar casi toda la bitácora real; horas, fases, paquetes (WP3…) y ADR no van por delante como identificadores; «ayer» no se entiende; el tope de tres entradas por día; las entradas retroactivas («**mañana**») no se limpian; y la fusión queda por debajo de solo vectores en paráfrasis | Media | ADR-0047, adendas del 2026-11-04. Se mide sobre la bitácora real (`scratchpad/f124-real` de la sesión) |
| P41 | El hook de la bitácora pierde el prompt cuando empieza con un bloque del sistema (`<browser_instruction>`, `<pasted_content>`): no los quita y recorta a 160 caracteres (`.claude/hooks/bitacora.ps1:48` y `:94`). Pasó en 4 entradas (15-10, 16-10 y 30-10) | Hecho el 2026-11-06 | El hook quita esos bloques (y `<system-reminder>`, `<ide_opened_file>` e `<ide_selection>`) antes del extracto y de los tags. Pone «[pegado]» en lugar de lo pegado y «(solo bloques del sistema)» si no queda nada. Probado en pwsh y Windows PowerShell; las 4 entradas no se reescriben ([11 §9](11-bitacora.md#9-el-extracto-del-hook-sin-los-bloques-del-sistema), adenda de ADR-0008) |
| P42 | Pruebas intermitentes: `ExitSuspendsResumesTests` (salida suspendida de la Fase 8) y `Phase4ShellTests…open_memory_tab_shows_it` fallan solo con la máquina cargada; `QuotaWaitTests` (una ejecución que espera cuota y no termina a tiempo) falla también sola, 1 de 5 en `main` y 2 de 5 con la Fase 12. Averiguar si la carrera está en la prueba o en el producto: la de la cuota es la más importante | Hecho el 2026-11-06 para `QuotaWaitTests` (y `Phase4ShellTests`, en #33); `ExitSuspendsResumesTests`, explicada y sin cambiar | `QuotaWaitTests`: dos carreras del motor, cada una con su prueba en rojo antes del arreglo ([ADR-0034](adr/0034-esperas-por-limite-y-reanudacion-con-consentimiento.md), adenda del 2026-11-06). **1.** El bucle leía el reloj y después armaba el temporizador de su espera. Si la prueba adelantaba el reloj falso justo entre las dos cosas, no despertaba nunca. En la app llegaría tarde un latido (5 s) como mucho. Ahora vuelve a mirar el reloj tras armarlo. El debate tenía la misma carrera (`DebateRun.WaitForResetAsync`), arreglada igual, y sus pruebas esperan ya a ver armada la espera en vez de hacer pausas reales. **2.** La reanudación contaba de más una comprobación cuando la lectura de cuota del trabajador llegaba al registro antes que el paso a en curso del bucle. En la app es rara y menor: pausa una comprobación antes. Ahora se decide por el `AgentRunStarted`. Tasas de la clase: `main`, 3 de 20 sin carga y 1 de 30 con la CPU al 100 %; con los arreglos, 0 de 30 sin carga, 0 de 30 con la CPU al 100 % y 0 de 30 con la suite entera en paralelo. `ExitSuspendsResumesTests`: no es el mismo patrón, porque no usa reloj falso. Da 400 ms al fin de la sesión: la mitad para la pasada, que nunca acaba, y el resto para escribir desde un `Task.Run`. Con el grupo de hilos del proceso de pruebas saturado, la escritura puede no empezar a tiempo. El producto se rinde a propósito (Windows no espera), pero la prueba exige que se escribiera. No se reprodujo: 0 de 30 sin carga, 0 de 30 con la CPU al 100 %, y pasó en la suite entera con carga. Se deja como está. Con la suite entera y la CPU al 100 % falló una vez otra prueba, `ResumeAfterRestartTests.Passes_asked_for_at_once_go_one_after_the_other_and_resume_a_run_once`: una pasada posterior devolvió la ejecución ya reanudada como «No está a medias». Es otra carrera, sin relación con estas (su reloj no avanza), y queda pendiente Pendiente: `ProjectMemoryTests.A_memory_write_while_a_run_goes_on_waits_and_reaches_the_log_only_after_the_run_ends` falló una vez en el CI de `main` el 2026-11-08 («The operation did not end once the project was no longer busy»): pasó al relanzar y 10 de 10 en local con carga. Es de la misma familia (una espera que no despierta) y queda por investigar en el lector único de la memoria. En la integración de la Fase 13 (2026-11-09), con varias suites a la vez en otras copias del repositorio, falló una vez `SqliteEvidenceStoreTests.Concurrent_captures_of_the_same_content_end_in_one_file` («The process cannot access the file … because it is being used by another process» al capturar el mismo contenido a la vez); pasó 3 de 3 sola. La Fase 13 no toca el almacén de evidencias: queda por mirar si dos capturas del mismo fichero pueden chocar también en la app. En P23 (2026-11-12) fallaron solas, con la máquina cargada, `ApprovalReviewAndIntegrationTests.A_verdict_that_needs_a_human_waits_for_the_approval_gate` (a Medium, en la suite entera; pasa sola) y `MonitorAccessibilityTests` (a Low; pasa sola). En la tanda corta del 2026-11-11, con agentes y suites a la vez en otras copias, `ExitSuspendsResumesTests` falló en las dos pasadas de la suite entera (pasa sola). `QuestionScenarioTests.A_restart_in_the_middle…` falló dos veces: miraba la fase de la ejecución reanudada justo al ver la pregunta, un instante antes de que el bucle la fije; ahora espera a la fase (3 de 3 tras el cambio). En la pasada final en Debug falló una vez `MainViewModelCompareTests.CompareWithFile_OffersTheProjectFiles_AndComparesWithTheChosenOne` (comparó con `# hola` en vez de `class Old {}`, como si el fichero elegido fuera otro); la tanda no toca el comparador, pasó en Release y 3 de 3 sola: queda por mirar si el orden de los ficheros ofrecidos depende del índice del explorador El 2026-11-12, en la Fase 14 ([ADR-0034](adr/0034-esperas-por-limite-y-reanudacion-con-consentimiento.md), adenda): `ResumeAfterRestartTests` era una carrera del motor (`ListRunsAsync` leía el registro antes que las ejecuciones vivas), arreglada con su prueba en rojo. `SqliteEvidenceStoreTests` también era del producto (otro manejador tenía el fichero) y se arregló con reintentos acotados y su prueba. `ProjectMemoryTests` era de la prueba (un plazo de 4 s). `MainViewModelCompareTests` venía del abrir rápido y se arregló por análisis. `MonitorAccessibilityTests` aún falla a veces con la máquina cargada; sola pasa |
| P43 | Grabar memoria cuesta unos 47 ms por entrada (escrituras durables del ancla y commits de SQLite). Opciones: anunciar y asentar en lote en las derivaciones, o `synchronous=NORMAL` | Baja | ADR-0010, adenda del 2026-11-04 |
| P44 | Restos menores: en «Consumo» una cuota con barra repite «vista hace…»; la bandeja repite el resumen de una ejecución fallida; quedan carpetas de ejecuciones del 30-10 en `%LOCALAPPDATA%\AgentStudio\runs` | Baja | Nuevo |
| P45 | Cerrar el canal `INSERT OR REPLACE` por `rowid` desde una conexión externa (sin `recursive_triggers`): cambia una fila del registro o de la memoria con los triggers intactos y el mismo `schema_version`; hoy se detecta en la siguiente verificación entera y nunca se sirve. Defensa propuesta y probada aparte: `OR EXISTS (… WHERE rowid = NEW.rowid)` en los triggers `*_no_replace`, con una migración | Hecho el 2026-11-12 | [ADR-0010](adr/0010-estado-derivado-del-registro.md), adenda: la migración `0007_replace_by_rowid.sql` recrea los 11 triggers `*_no_replace` para que rechacen también un `rowid` ya ocupado. 38 pruebas lo atacan desde una conexión externa y fallan sin la 0007. `debate_contributions` y `skill_activations` no lo necesitan: su id es el propio rowid |
| P46 | El registro de cada intento (`AttemptRecord`, capturado por el sistema) copia los `outputs` del informe del ejecutor, que escribe el agente; el juez los recibe como un hecho del sistema. Es anterior a la Fase 13 y la revisión de WP4 lo vio como residual: los `outputs` hacen falta para reanudar, así que el arreglo va en el dosier del juez (quitarlos o marcarlos como dichos por el agente) | Hecho el 2026-11-12 | [ADR-0051](adr/0051-preguntas-de-los-agentes.md), adenda: en el expediente del juez, los `outputs` del registro del intento van aparte y marcados como dichos por el agente (`outputsSaidByAgent`, y `saidByAgent` en la evidencia), y su protocolo lo explica. Lo guardado no cambia y la reanudación sigue leyéndolos |
| P47 | El estimador de uso no distingue el esfuerzo de razonamiento: `AgentRunStarted` no lo guarda, así que un intento `low` y uno `high` del mismo modelo cuentan juntos como referencias. Añadir el esfuerzo a `AgentRunStarted` v3 (cuando se fija) y compararlo en las referencias y en lo aprendido | Hecho el 2026-11-11 | Tanda corta del 2026-11-11 ([ADR-0050](adr/0050-modelos-y-potencia.md), adenda): `AgentRunStarted` v3 guarda `effort` solo cuando el agente lo fija (las entradas v2 y v3 ya escritas se leen y se reescriben byte a byte), lo escriben los cuatro que registran arranques (motor, auxiliares, juez y turnos de debate), y dos intentos son referencias del mismo esfuerzo si lo tienen igual o ninguno lo tiene. Lo aprendido por modelo no se parte. Límites: los intentos de antes no tienen esfuerzo, así que un agente que lo fija empieza en «desconocido» hasta tres intentos nuevos; un nivel que la app no conoce deja esa entrada sin leer (lista cerrada) |
| P48 | Quitar agentes de la plantilla: `IAgentRoster` no tiene borrado, así que el editor de la capa Equipo no enseña el botón y un arreglo que necesita quitar un agente se rechaza antes de guardar nada. Añadir `IAgentRoster.DeleteAsync` (Application e Infrastructure) con su aprobación | Hecho el 2026-11-11 (pendiente de verificar en vivo) | Tanda corta del 2026-11-11 ([ADR-0048](adr/0048-constructor-visual-equipo-y-plan.md), adenda): «Quitar del equipo» pregunta antes («Sí, quitar» o «Conservar»), rechaza quitar al orquestador o al último juez antes de preguntar y va por el mismo conjunto todo o nada que los arreglos de la banda. Quitar no concede poder, así que no pide Windows Hello; un conjunto que además da poder a otro agente lo sigue pidiendo. El almacén cierra en la misma transacción las skills que el agente tenía encendidas, para que otro agente con el mismo id no las herede (P57). Las ejecuciones y los debates pasados conservan sus agentes congelados. Ctrl+Z sigue sin llegar al almacén (P55) |
| P49 | Reglas del analizador que callan por falta de datos: `TaskContexts`, `EstimatedAgentRuns` y el número de ficheros del proyecto van vacíos en el borrador, así que no avisan `cost.over-budget`, `context.pack-too-small`, `context.window-tight` ni `context.thin`, y la recomendación de modelo no usa el tamaño del proyecto | Hecho el 2026-11-11 (pendiente de verificar en vivo) | Tanda corta del 2026-11-11 ([ADR-0049](adr/0049-analizador-del-montaje-y-orden.md), adenda): el borrador lleva el contexto de cada tarea (medido con el mismo prompt que el motor, sin lanzar nada), las ejecuciones que estima el plan (el extremo alto, el que usa el arreglo de subir el presupuesto) y el número de ficheros del proyecto (contados una vez al abrir, fuera del hilo de la interfaz, con un tope de 20 000). Todo se guarda por forma del plan y se olvida al releer el contexto o cerrar el documento. Límites en P56 |
| P50 | «Ejecutar las pruebas de HEAD» en el panel «Pruebas»: falta `IHeadTestsService`, y sin él el botón no se enseña. El panel tampoco lee del TRX los nombres de las pruebas que fallan: enseña contadores y la línea principal del error | Hecho el 2026-11-12 | [ADR-0017](adr/0017-verificacion-aislada-baja-integridad.md), adenda: «Ejecutar las pruebas de HEAD» pasa los pasos aislados del perfil sobre el commit de HEAD (copia de solo lectura, integridad Low, sin red), nunca los pasos sin aislar ni el árbol de trabajo. El panel lista los nombres de las pruebas que fallan (TRX y JUnit, 200 como mucho) y dice qué pasos no se ejecutaron. Su resultado no se anuncia en el registro, y el documento para clientes solo cuenta pruebas guardadas ancladas |
| P51 | Remotos del nodo del repositorio: se leen del texto de `.git/config` (y del común de un worktree) con un tope de 1 MB, sin lanzar git y sin seguir `include`. Falta `ListRemotesAsync` en la capa de git endurecida; el tope se comprueba antes de leer y un fichero que crece entre las dos cosas se lee entero | Hecho el 2026-11-12 | [ADR-0048](adr/0048-constructor-visual-equipo-y-plan.md), adenda: `IRepositoryRemoteReader.ListRemotesAsync` en la capa git endurecida (`git config --local --no-includes`, configuración neutralizada, sin hooks, salida acotada y fallando cerrado; más de 100 remotos se rechazan, nunca se cortan). El respaldo por fichero aplica el tope mientras lee. Sigue igual: el configurador de ADR-0055 aún no usa el lector |
| P52 | El monitor de una ejecución que se sigue en segundo plano (`FollowInBackground`) no recibe la plantilla ni las dependencias del plan: sin agentes libres ni pulsos en su estado vivo | Hecho el 2026-11-12 | [ADR-0036](adr/0036-vida-de-la-app-y-bandeja.md), adenda: el monitor que se sigue en segundo plano recibe la plantilla y las dependencias del plan desde el registro y el almacén de planes, así que muestra agentes libres y pulsos |
| P54 | El juez ve la pregunta del orquestador y tu respuesta: su expediente incluye la evidencia «Prompt del orquestador (intento N)» de la planificación y el informe del primer intento del planificador, con la pregunta, la opción recomendada y lo que respondiste (`JudgeDossierBuilder` solo retira los prompts e informes de los ejecutores). D-30 y D-33 dicen que el juez ve solo la decisión del sistema. Decidir si se retiran del expediente o se aceptan | Hecho el 2026-11-11 | [ADR-0051](adr/0051-preguntas-de-los-agentes.md), adenda del 2026-11-11 (P54): el prompt del orquestador nunca llega al juez, haya preguntas o no (como el de un ejecutor), y se reconoce también por su título anclado si no hay inicio; sus informes salen si traen una pregunta o no se pueden leer (con aviso del sistema). Tu decisión sí llega: al responder al orquestador se captura el mismo documento de decisión que para un ejecutor, y con un plan editado (`PlannedIn`) el expediente la trae de la ejecución de planificación, y nada más de ella. Una planificación con varias preguntas se puede reintentar aunque falle la captura o el registro de una respuesta a mitad |
| P55 | Deshacer (Ctrl+Z) un arreglo de la banda que cambia la plantilla no llega al almacén: al releer el contexto, `BuilderContextChanged` reinicia la banda (`Band.Reset()`) y su historial solo conoce estados con la plantilla ya cambiada. Además `AgentDefinition.Equals` compara sus listas por referencia, así que todo agente releído cuenta como cambiado al aplicar un conjunto. Una compensación retira la aprobación del plan aunque el estado final sea el mismo | Hecho el 2026-11-12 | [ADR-0048](adr/0048-constructor-visual-equipo-y-plan.md), adenda: Ctrl+Z de un arreglo de la banda llega al almacén, porque el reinicio ya no pisa una escritura de la propia banda. `AgentDefinition` compara su contenido. La aprobación del plan se conserva si la plantilla acaba igual a la aprobada, y se retira si la relectura falla |
| P56 | Límites de los datos del analizador (P49): el contexto de cada tarea es un suelo (no cuenta las salidas de las tareas anteriores, la memoria que depende del foco ni los intentos previos); el ejecutor de la plantilla por defecto no fija modelo, así que no tiene ventana y `context.window-tight` calla para él; las tareas de revisión no tienen datos; una memoria o una skill activada con el documento abierto no se ve hasta «Volver a cargar» | Baja | [ADR-0049](adr/0049-analizador-del-montaje-y-orden.md), adenda del 2026-11-11 |
| P57 | Un agente que vuelve con el id de uno quitado nace con las skills que aquel tenía encendidas apagadas a su nivel, y eso gana al encendido del proyecto. La pantalla de Skills dice «Desactivada (por el agente)» sin decir que fue por quitarlo: falta un texto que lo aclare | Baja | [ADR-0048](adr/0048-constructor-visual-equipo-y-plan.md), adenda del 2026-11-11; P48 |
| P58 | Petición del 2026-11-12: «un sistema que exporte diagramas, documentación funcional y ejecutiva, todo lo que cualquier proyecto serio requiera de documentación, que se construya con los agentes durante su trabajo en el proyecto, y cuando le demos a exportar que esté todo bien documentado: diagramas, diccionarios, todo» | Alta | Diseño en [19](19-documentacion-viva-del-proyecto.md) (2026-11-12): catálogo por audiencia, generadores deterministas (C4, diccionario de datos, trazabilidad), notas de los agentes con su marca, diagramas Mermaid sin red, el paquete de exportación sobre la documentación para clientes y la Fase 16. Pendiente de tus 9 decisiones abiertas (§14) |
| P59 | El buscador de la ayuda no conoce sinónimos: los artículos dicen «límite de uso» (guía de textos), así que buscar «cuota», «CLI» o «evidencia» no encuentra nada. Añadir sinónimos del glosario de ADR-0054 §9 al índice | Hecho el 2026-11-12 | [ADR-0056](adr/0056-ayuda-y-agente-de-ayuda.md), adenda: una tabla única de sinónimos (`HelpSynonyms`) amplía la consulta con la frase que usan los artículos, en los dos idiomas, y pasa por el mismo saneado que lo que escribe la persona. Una prueba comprueba que cubre cada término de la guía de textos. Es una tabla, no lematización |
| P60 | Textos que siguen sin código tras WP-14A (e), anotados en las adendas de ADR-0054: los motivos de un trabajador con palabras de un agente o de su programa, las diferencias del repositorio (en inglés, del guard), `finding.Message` del auditor en el monitor, `PlanViolationText` (pinta por código estable, con claves propias), los avisos `Discarded` de revisar el orden y del chat, y el anexo de los documentos exportados, que escribe «SHA-256:» fijo | Baja | Fase 14, 2026-11-12 |
| P61 | Rendimiento de «Trabajo»: cada escritura relee el registro unas ocho veces (hitos, backlog, tablero, participación, cadena y vínculos por separado). Leerlo una vez por recarga y repartirlo exige rehacer los servicios de `Orchestration/Work`. Igual en Memoria: `IMemoryEntryTexts.ReadAsync` recorre los hechos en cada refresco | Baja | Revisiones de WP5 y de 14A(e) Memory, 2026-11-12 |
| P62 | Límites de la regla que decide qué veredicto está en vigor (`VerdictReach`, compartida por el tablero, la cadena y la participación): un veredicto que sustituye a otro impugnado cuenta como de la revisión aunque fuera una autocorrección del juez posterior a la impugnación; en la cadena, el original y el de una revisión que lo confirma salen los dos «en vigor»; y la unidad de la cadena es la tarea, así que una tarea que cubre dos requisitos lleva el veredicto que evaluó solo uno | Baja | [ADR-0039](adr/0039-vinculos-de-trazabilidad-y-participacion.md), adenda del 2026-11-12; docs/04 §23.6 |
| P63 | `ChallengeLog.StatusOf` (Debate) lee el estado de una impugnación distinto que el tablero y la entrega en dos casos: una impugnación cuya revisión rechazaste abrir (dice «pendiente»; los otros dos no la cuentan) y la de un veredicto que no es el de cierre (dice «espera aceptación»; los otros la leen como una aprobación). Lo usa `VerdictChallengeService` para impedir impugnar otra vez y para aceptar una revisión. Una prueba fija la diferencia. Además, las tarjetas de tarea no miran la evaluación por requisito: un veredicto aprobado con un requisito no satisfecho deja el requisito en Devuelto y su tarea en Hecho | Hecho el 2026-11-12 | [ADR-0039](adr/0039-vinculos-de-trazabilidad-y-participacion.md) y ADR-0032, adendas: una sola regla (`ChallengeRules`, en Application) lee el estado de una impugnación para Debate, el tablero, la cadena y la participación, y sigue la cadena entera de revisiones. Una tabla de 28 escenarios comprueba que coinciden con la entrega. Una impugnación abortada tiene estado propio y deja volver a impugnar. La tarea sigue a lo que el veredicto dice de sus requisitos (regla provisional). Queda: una revisión que cierra sin decisión sigue en «pendiente» en Debate y no deja volver a impugnar |
| P64 | Restos del arreglo del localizador de programas (Fase 6, WP-0): abrir como proyecto la carpeta personal o la raíz de una unidad deja sin efecto la protección de ejecutables para esa carpeta (la app lo avisa); las raíces no fiables se acumulan hasta cerrar la app; un enlace duro no se detecta; las unidades de red mapeadas están sin medir; y el terminal integrado conserva el PATH completo, porque ahí escribe la persona | Baja | [ADR-0018](adr/0018-modelo-de-amenaza-fase-3.md), adendas del 2026-11-12 |
| P53 | Restos de los perfiles de Node y Python: un motivo de paso fallido con `&`, `|`, `<`, `>`, `^`, `%` o `"` (por ejemplo una ruta de node rara) lanza al proponer en vez de dar un paso que falla; la versión mínima de Node con el informe JUnit de `node --test` está sin comprobar. F13-7 se mide fuera del CI (`Measurement`): el lienzo, unos 45 ms de media con picos de unos 110 ms; la pasada completa del monitor, de 23 a 28 ms sola y de 52 a 411 ms dentro de la suite entera | Hecho el 2026-11-12 | ADR-0055, adenda: un motivo con caracteres de `cmd` da un paso que falla, sin lanzar y sin inyectar nada (se escapan con `^`, la comilla doble pasa a simple y `cmd` va con `/v:off`; probado con un `cmd` real). El paso de pruebas de Node exige 18.19.0, 20.8.0 o 21.0.0 para el informe JUnit, y lee la versión de `node.exe` sin ejecutarlo |
| P17, P20 y P21 | Probarlo todo, Auto Doctor compacto, documentarlo todo | — | Ya en 18 |

Detalle de P24, tus demostraciones:

- F1-2, F1-5, F3-6, F4-1, F4-3 y F4-7;
- la lista de la Fase 5 (13 pasos);
- de la Fase 8, los pasos 6, 11, 13, 15, 16, 17, 18 («Bloquear Windows»), 19 y 20.

Detalle de P31, pendientes técnicos de 08:

- los filtros del informe de consumo, y el JSON exportado sale con `filters: null`;
- el «contexto empaquetado» en la barra de estado (D-12);
- tus notas en la memoria;
- lo que la derivación no puede citar;
- «Decidir yo» no se reproduce tras reiniciar;
- el historial de debates deja de listarse si una cabecera está manipulada;
- los requisitos de «valoración del juez» en una auditoría;
- la clase `Network` sin reintento progresivo;
- `DebateTurnRecorder` y `AuxiliaryAgentRunner` siguen en `AgentRunFinished` v2;
- falta `AttemptBudget(Time)`;
- el cierre de sesión de Windows antes de que aparezca el icono;
- la comprobación de acceso gasta un número de intento.

Detalle de P34, lo aplazado:

- nodos de debate en el lienzo;
- propuestas de memoria hechas por los agentes;
- skills en los debates;
- un tercer vendor;
- auditar y replanificar a mitad de una ejecución;
- calibrar el juez;
- conflictos resueltos por un agente;
- aprobaciones por herramienta dentro del CLI;
- checkout parcial;
- ACP;
- la Fase 11;
- GitLab y Bitbucket;
- perfiles de CLI por proyecto;
- Hello al abrir la app.

**Cruces de P1-P21 con lo anterior:**

| P | Precedente o choque |
|---|---|
| P1 | Comprobado: la carpeta y el acceso directo existen. No es F7-6 (P27) |
| P3 | Precedente: la ampliación §7 pedía «ocupación del contexto mediante barras» (F8-8). Hasta la tanda 2 solo había barra en el panel de la bandeja, y solo con porcentaje; desde el 2026-11-03 hay barras siempre en la barra de estado, el monitor, la bandeja, Consumo, la planificación y la sala de debate |
| P9 | Precedente: las «aprobaciones interactivas por herramienta dentro del CLI», aplazadas en 13 §1 |
| P10 | Ver §4.3. El orden entre agentes ya lo comprueban `PlanValidator` (F3-1) y el auditor (F5-6) |
| P11 | Choca con la convención de `CLAUDE.md` (la interfaz va en castellano): pasará a «castellano por defecto, más idiomas» |
| P14 | Es distinto del «chat con el orquestador» del prompt (P29) |
| P15 | Precedentes: ampliación §9 y §18, es decir, D-22 y el informe al cerrar un hito (F9-10). Ninguno sale en PDF ni Word |
| P18 | Choca con tu decisión 5 (a) del 20-10 y con ADR-0037: hoy F8-11 se puede apagar. Enmienda ADR-0037 |
| P19 | Ver §4.3. Enmienda ADR-0023 |
| P20 | El Auto Doctor completo es F6-1 (P25) |
