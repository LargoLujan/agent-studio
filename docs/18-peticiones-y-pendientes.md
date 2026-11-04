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
4. **Revisión contigo** de la propuesta [17](17-constructor-visual-de-agentes.md), con sus decisiones abiertas.
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
| P23 | Las 93 pruebas que fallan a integridad Low. Hasta adaptarlas, «Auditar proyecto completo» no puede aprobar este repositorio | Alta | Nuevo (08 F5) |
| P3 | Barras de consumo y contexto en tiempo real | Hecho (verificado en vivo el 2026-11-03; retoques V32 a V39 en §3) | Ya en 18. Tanda 2 (WP-F), integrada el 2026-11-03 |
| P19 | Índice semántico de la bitácora y la memoria | Alta | Ya en 18. Enmienda ADR-0023 (§4.3) |
| P24 | Tus demostraciones (lista debajo), y llevar a 08 lo visto en vivo el 30-10 | Alta | Nuevo. Cruza con P17 |
| P16 | Fase 9 (F9-1 a F9-10) | Alta | Ya en 18 |
| P4 a P15 y P18 | Constructor visual, modelos, configurador, preguntas, idiomas, textos, ayuda, documentación para clientes y Hello siempre | Alta | Ya en 18. Su diseño va en 17 (P22) |
| P25 | Fase 6 (F6-1 a F6-11): Auto Doctor completo, Auto Graph, el doctor de skills (lo que falta del «indicador de salud» del 15-10), el entorno y los diagramas | Media | Nuevo. Cruza con P20 |
| P26 | Fase 10 (F10-1 a F10-10): GitHub por `gh`, PR y sincronización | Media | Nuevo |
| P27 | Fase 7 (F7-1 a F7-8), con el empaquetado y las actualizaciones (F7-6, que P1 no cubre) y la bóveda DPAPI | Media | Nuevo. Es lo que queda de «¿cuándo estará listo?» (18-10) |
| P28 | Lo mandado «a la Fase 7» sin criterio: la firma del registro (ADR-0004, 15 §1), la accesibilidad completa (12 §7, F1-6) y la purga real de la memoria (ADR-0022, 14 §1). Darles criterio o descartarlos con un ADR | Media | Nuevo |
| P29 | Lo que §4 del prompt pide y no tiene sitio: el chat con el orquestador (13 §1); los paneles de salida, errores, depuración y resultados de pruebas; «Herramientas»; «Pruebas»; la gestión de agentes; el estado de los agentes en la barra superior | Media | Nuevo. Cruza con P7, P10 y P14 |
| P30 | Catálogo de skills: dar de alta desde repositorios oficiales, de la comunidad y propios (10 §3, «Diseño»). Y poner al día 10 §3, el README e `investigacion/2026-10-15-candidatas-skills.md` | Media | Nuevo. Cruza con P8 |
| P31 | Pendientes técnicos sin fase en 08 (lista debajo) | Media-baja | Nuevo. D-12 cruza con P3 |
| P32 | Capturas y mediciones reales: el `rate_limit_event` con `rateLimitType` de Claude, el límite y la sesión caducada de Codex, una suspensión real. Repetir D-30 y el confinamiento de Claude si cambia su versión (hoy, 2.1.290) | Baja | Nuevo |
| P33 | Paquete Azure y Microsoft Fabric (prompt §16): decidir si entra y en qué fase | Baja; decides tú | Nuevo |
| P34 | Revisar lo aplazado (lista debajo) | Baja; decides tú | Nuevo. Viene de 13 §1, 14 §1, 15 §1, 06 §5, ADR-0033 y 16 §7 |
| P35 | Los 5 commits que llevan tu correo personal como autor (3ae5d84, 688533e, 35f9419, e9b7302 y a6da41d). Reescribirlos quedó pendiente de tu OK el 15-10 | Baja | Nuevo |
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
