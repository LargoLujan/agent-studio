# 24 · Rediseño tras la primera prueba

**Fecha:** 2026-11-16 · **Estado:** propuesta, pendiente de tus decisiones (§6).

Este documento es para el equipo que construye (agentes y orquestador). Para el propietario hay un resumen corto en el
chat; no necesita leer esto.

## 0. Lo que dijiste (literal)

> He mirado los pasos de la documetnacion para el proyecto pero vamos es un puto lio. Informacion por todos lados mil
> veces explicar todo. Es demasiado lioso.
>
> Abro una carpeta para el proyecto y hasta ahi bien. No entiendo para que es el boton inicio porque no hace nada ni me
> lleva a ningun sitio.
>
> Las configuaciones de proyecto deberian de poder dejar seleccionar las cosas pero vienes rarisima. Deberia de
> seleccionarse equipos no por tamaño sino por tipo de proyecto. Me refiero a desarrollo web, escriotiro, aplicaciones
> moviles,, ingenieria de datos, ciberseguridad y lo que se pueda con todas las posibilidades y tener plantillas para
> ello pero que no sean rigidas y cada elemento se pueda personalizar, quiza una plantilla personalizar tambien para
> añadir o quitar pero al seleccionar la planitlla de tipo se cargara algo autoinferido pero con posibilidad de editar.
> ahi mismo en modelox los modelos se sufieren pero deberiamos de pode editarlos ahi tambien. No necesito luego
> volvermne loco buscando en otros sitios para cambiarlo, la sugerencia y lo que termina siendo se debe poder editar en
> esa pantalla. La seccion de pruebas ni explicas bien para que es cobn lenguaje entendible y tampoco cambia si le
> escribes nada al resto de opcionbes asi que esto para que y que deberia de ser amable sencillo con lenguaje mas
> cercano. La seccion de skills deberia de cargar automaticamente las skills del tipo de proyecto una lista con check de
> seleccion pero ademas la posibilidad al final de añadir con url otra skill asociado a eso para poder añadir nuevas si
> no las tenemos. vamos que deberiamos de poder elegir que usar. En presupuesto no se entiende que es, ahi deberia de
> marcarse un limite de contexto, un limite de agentes simultaneos. un sistema de autodetectar con la info configurada
> del proyecto y cosas asi, y finalmente resumen deberia de ser un conjunto de todo lo anterior y al darle a aplicar los
> cmabios nos llevara donde empeiza .
>
> Ademas deberia de haber una seccion de Proveedores donde esten la configuracion para poner sincronizar las cuentas
> previamente de claude o codex o gemini y asi cuando configuremos eso que en la configuracion podamos elegirlos para los
> modelos o en consumo podamos ver el consumo actualizado de cada uno en el mes semana dia horas, vamos en todo porque
> sino no tiene sentido. En auditar dice que no se puede sin requisitos congelados pero no se donde estan los requisitos
> y me deberia de llegar alli pero vamos, en la seccion orquestador es una amalgam de datos.
>
> La parte inferior y la superiror esa barra que los separa no se mueve y la parte de abajo y la de arriba es un lio de
> la hostia con secciones por todos lados. la idea esta bien pero vamos todos tobre tdo recortado sin poder verse bien.
> seria mejor secciones despelgable con lenguaje entendible menos tecnico o almenos con explicaciones mas tencicas, y que
> no repitan lo que ya se hace en configuracion,. que la parte de resumen del plan esta genial pero sea mas versatil en
> cuanto que no se ve nada porque esta todo amontonoado.
>
> revisa todo esto y ademas otra cosa. Cuando el proyecto arranca a trabajar me gustaria que vieramos un terminal a la
> derecha donde se vea bien fluido leginble que van haciendo cada uno de los agentes como en la extension de claude o
> codex y en la pantalla principal un pixel art en una oficina de los distintos agentes con distintas avatares bien
> hechos moviendose segun hagan ademas de orquestador y jueces como en una oficina con PM desarroladores y el ceo de la
> empresa como el juex o algo por el estilo. ademas en cada terminal a la derecha mas pequeño que mencionamos y que seria
> bien que fuera desplegable abrir y encoger segun querramos podamos escribir, de manera que podemos escribir para
> influir en el orquestador juez o cualquier agente de manera INDDEPENDIENTE vaya que detectes algo y no quieras paralo
> todo. al seleccionar uno de los terminales pqeueños de uno de los agentes que esta trabajando que en la pantalla
> principal el personaje de pixer art se pare y mire a nosotros y se seleccione con un aura de color para saber que
> estamos eligiendo eso. ademas encima un titulo como en el wow esos personajes de que rol tiene modelo y si esta run
> paused o finished.
>
> Es mucha tela pero esto es importante que sea util, tu como maquina te da igual porque ves de otra manera pero para mi
> que no sea comoda y lo que se informa que hace cada cosa no se entienda es un follon ademas del detalle este final
> pbservando que hace cada pixerart del equipo es importante para cmabiar algo rapudo y para entretener mienbtras
> trabaja.

«Escriotiro» se lee como «escritorio» (aplicaciones de escritorio).

## 1. Qué pasa hoy y por qué

Mapa hecho el 2026-11-16 por tres investigadores de solo lectura sobre `main` 27fdee8c. Nadie compiló ni abrió la app;
las alturas en píxeles salen del XAML y **no están medidas**. Rutas relativas a `src/AgentStudio.Desktop/` salvo que se
diga otra cosa.

| # | Qué ve el propietario | Causa | Dónde |
|---|---|---|---|
| 1 | La documentación es un lío | Para usar la app se le mandaba a documentos de desarrollo (03 tiene ~1600 líneas, 17 ~1900) y a una guía larga que repetía lo mismo | `docs/` |
| 2 | «Inicio» no hace nada | Abre la pestaña «Bienvenida», que ya es la activa al arrancar: no cambia nada visible | `ViewModels/MainViewModel.cs:419-423, 462-473` |
| 3 | 19 entradas en la barra lateral, solo iconos | El nombre va en la ayuda emergente; la bienvenida es el único menú con texto; «Configurar proyecto» no está en ninguna barra | `MainWindow.xaml:125-129`, `MainViewModel.cs:247-266` |
| 4 | El equipo se elige por tamaño | Tres plantillas fijas (pequeño/mediano/grande) según el número de ficheros; filas de solo lectura; no hay tipos de proyecto | `Application/Setup/SetupContracts.cs:56-67`, `SetupProposer.cs:184-220`, `Views/Setup/SetupView.xaml:183-196` |
| 5 | Los modelos se sugieren pero no se cambian ahí | El paso «Modelos» solo deja aceptar o no la recomendación; elegir otro modelo se hace en otros tres sitios del Orquestador | `SetupView.xaml:201-221`; `Orchestrator/Preparation/RosterEditorViewModel.cs`, `Orchestrator/Team/AgentInspectorViewModel.cs`, `TeamEditorViewModel.cs` |
| 6 | «Pruebas» no se entiende y escribir no cambia nada | El paso no tiene campo: muestra el perfil propuesto en solo lectura y dice «puedes escribirlo tú» sin dónde. El único texto libre, la «Ficha», no alimenta ninguna propuesta. Además hay otra «Pruebas» en la barra lateral que abre otra cosa | `SetupView.xaml:224-230`, `SetupStrings.resx:388-389`, `Application/Setup/ProjectSetupService.cs:104-141`, `IntegrationStrings.resx:24-25` |
| 7 | Skills sin añadir por URL | Propone tres por agente desde el índice local; no existe alta por URL (solo un analizador de URLs de GitHub reutilizable) | `Application/Setup/SetupProposer.cs:288-330`, `Infrastructure/Skills/SkillsIndexCatalogImporter.cs:294-305` |
| 8 | «Presupuesto» no se entiende | Son límites de ejecución (ejecuciones de agente, tareas a la vez, minutos por intento). «Presupuesto» nombra además los topes de Trabajo y el contexto de cada agente es una constante de 24 000 caracteres no configurable | `SetupStrings.resx:276-292`, `Domain/Work/WorkBudget.cs`, `Orchestration/Engine/ExecutorPrompt.cs:41` |
| 9 | «Aplicar» no lleva a ningún sitio | El resumen solo lista diferencias de los pasos encendidos (cuatro empiezan apagados); tras aplicar se queda en el asistente | `Setup/SetupViewModel.cs:245-290, 480-534, 613, 666-672`, `MainViewModel.Builder.cs:220-241` |
| 10 | No hay «Proveedores» | El estado de los programas está dentro de Auto Doctor; el límite de cuenta está en «Consumo»; el selector de modelos tiene Claude y Codex fijos en el código y no consulta las comprobaciones | `ShellStrings.resx:528-588`, `Observability/QuotaViewModel.cs`, `Orchestrator/Team/TeamEditorViewModel.cs:88` |
| 11 | El consumo no llega a horas | Periodos hoy/7 días/mes/30 días; agrupa por día, semana y mes; la hora se puede derivar de `FinishedAt`. Solo cuenta lo lanzado desde la app | `Observability/Usage/UsageAggregator.cs:130-186` |
| 12 | «Auditar» no lleva a los requisitos | El aviso es texto plano; los requisitos se congelan en Orquestador → Preparación → Requisitos, que se pliega sola | `AuditStrings.resx:92-93, 136-137`, `Views/Audit/ProjectAuditView.xaml:59-76` |
| 13 | El Orquestador es una amalgama | Cinco bloques repiten el asistente (ficha, equipo, modelos, perfil de pruebas, minutos); la cuota sale tres veces y el estado de cada agente otras tres | `Views/Orchestrator/OrchestratorView.xaml`, `PreparationView.xaml`, `CanvasView.xaml`, `RunMonitorView.xaml` |
| 14 | La barra que separa arriba y abajo no se mueve | **Por confirmar en la app.** Dos candidatos: (a) el separador interno del Orquestador: la cuadrícula pide un alto mínimo de bandas + 524 px y dos filas de mínimo 260, y con la ventana por defecto (800 px) ambas quedan en su mínimo y el separador no tiene recorrido (estimación del XAML); (b) el divisor del panel inferior (Terminal, Problemas, Salida, Pruebas), donde no se encontró nada que lo bloquee. En la captura del día 0 la barra visible a esa altura es la (b) | `OrchestratorView.xaml:22-39, 247`; `MainWindow.xaml:165-182` |
| 15 | Todo recortado | En 260 px el lienzo y el monitor ponen antes sus filas automáticas (barra de capas, franja, análisis, cabeceras con topes de 240-280 px) y el editor y las listas quedan casi a cero | `CanvasView.xaml:43-49`, `RunMonitorView.xaml:114-226` |
| 16 | «Resumen del plan» amontonado | Es un cuadro de una línea sin ajuste de texto; propuesta, riesgos y actividad van en una columna de 220-480 px | `OrchestratorView.xaml:91-97, 160-241` |
| 17 | No se ve bien qué hace cada agente | «Actividad» es una lista común de 300 líneas, cada mensaje recortado a su primera línea y 160 caracteres, sin desplazamiento automático; el juez emite con el mismo `TaskId` que su ejecutor | `Orchestrator/Monitor/RunMonitorViewModel.cs:103, 782-807`, `MonitorTexts.cs:121-169`, `Orchestration/Engine/Workers.cs:1068-1070` |
| 18 | No se puede escribir a un agente | Cada intento es un proceso de un solo uso; el stdin se cierra al escribir el prompt; el motor solo tiene Pausar, Reanudar, Reintentar y Cancelar. El orquestador no tiene proceso durante la ejecución: solo planifica antes | `Infrastructure/Processes/StreamingProcessRunner.cs:69`, `Application/Execution/StreamingProcessContracts.cs:18-37`, `Orchestration/Engine/Signals.cs:16-24`, `Orchestrator.cs:164` |

## 2. Principios

1. **Un sitio para cada cosa.** Se configura en «Configurar proyecto»; se trabaja en «Trabajar». Lo configurado se ve
   en Trabajar como una línea con un enlace «Cambiar», nunca como otro editor.
2. **Un paso a la vista.** Secciones plegables; la del paso actual abierta y las demás con su resumen de una línea.
3. **Lenguaje llano.** Títulos y ayudas sin jerga; lo técnico, detrás de un ⓘ.
4. **Ningún aviso sin camino.** Si un texto nombra una pantalla, es un enlace que lleva a ella.
5. **Hecho = visto.** Cada ola termina con capturas a la ventana por defecto (y a pantalla completa) hechas con el
   driver, y un rato de uso del propietario antes de empezar la siguiente. Cada texto nuevo en castellano lleva su
   gemelo en inglés y sus pruebas de textos.

## 3. Cómo queda

### 3.1 Navegación

- Barra lateral **con texto**, en grupos: *Proyecto* (Inicio, Configurar, Trabajar, Revisiones), *Cuentas*
  (Proveedores y consumo), *El proyecto por dentro* (Ficheros, Repositorio, Tablero, Memoria) y *Más* (Debates,
  Auto Graph, Herramientas, Documentación para clientes, Auto Doctor, Historial). Ayuda y Ajustes abajo.
- La entrada lateral «Pruebas» (panel de resultados) pasa a llamarse «Resultados de pruebas» para no chocar con el paso
  del asistente.

### 3.2 Inicio

Una página de estado del proyecto con **un solo botón principal**, el siguiente paso:
«Configura el proyecto» → «Escribe tu primera petición» → «Hay una ejecución en marcha: ver la oficina» →
«El juez ha terminado: revisa el veredicto». Debajo, los recientes. Deja de ser un menú duplicado.

### 3.3 Proveedores (nuevo)

- Una tarjeta por proveedor: instalado y versión, sesión iniciada («Conectado» / «Inicia sesión en el programa»), y su
  límite de uso con barras.
- **Conectar** es comprobar la sesión con las sondas permitidas (`--version`, `claude auth status`, `codex login status`)
  y, para Codex, la lectura de cuota opcional. La sesión se inicia en el propio programa; la app nunca lee sus
  credenciales.
- Los proveedores conectados son los que ofrece el selector de modelos en «Configurar».
- **Consumo** por proveedor y periodo (hora, día, semana, mes) de lo lanzado desde la app, y la evolución del
  porcentaje de límite de cada proveedor (el historial ya se guarda en `usage/quota.jsonl`; hoy solo se pinta la última
  lectura).
- Gemini sigue **aparcado** (docs/06). Una fuente dice que su CLI fue sustituida en junio de 2026; está sin confirmar.

### 3.4 Configurar proyecto

Seis pasos, todos encendidos, cada uno con una frase de para qué sirve:

1. **Tipo de proyecto.** Tarjetas: Web, Escritorio, Móvil, API y servicios, Ingeniería de datos, Análisis de datos e
   IA, Ciberseguridad, Infraestructura y DevOps, Documentación y escritura, Juegos, Personalizado. Se preselecciona lo
   detectado en los ficheros (`Domain/Skills/ProjectStackDetector.cs`) y en la descripción que escribas; se puede
   cambiar. La taxonomía parte de `SkillSectors` (16 sectores).
2. **Equipo y modelos.** La plantilla del tipo carga papeles (orquestador, desarrolladores, revisor, juez…) y en **cada
   fila** se cambia proveedor, modelo y esfuerzo, con la recomendación al lado («Recomendado: … · por qué»). Añadir,
   quitar y renombrar papeles aquí mismo. «Guardar como plantilla mía» para crear plantillas propias.
3. **Cómo se comprueba.** «¿Cómo sabemos que el trabajo funciona? Con la orden que lanza tus pruebas.» Campo editable con
   la orden detectada y un botón «Probarla ahora» que la ejecuta y enseña el resultado. Guardarlo sigue pidiendo
   Windows Hello.
4. **Skills.** Lista con casillas, ya marcadas las del tipo de proyecto, con una línea de para qué sirve cada una. El
   alta por URL va en la Fase 7 (§4).
5. **Límites.** «Agentes a la vez», «Contexto máximo por agente» (hoy constante), «Intentos por ejecución», «Minutos por
   intento» y, enlazado, el tope de gasto de Trabajo. Botón «Calcular por mí» que propone valores según el tamaño del
   proyecto, las ventanas de los modelos elegidos y los topes de cada proveedor (hoy fijos: Claude 2, Codex 2).
6. **Resumen.** Todo lo elegido, no solo las diferencias, con «Cambiar» en cada bloque. «Aplicar» guarda y **lleva a
   Trabajar** con el cuadro de petición listo.

La descripción del proyecto (hoy «Ficha») va en el paso 1 y **sí alimenta** la propuesta: al escribirla se recalculan
tipo, equipo y skills.

### 3.5 Trabajar (hoy «Orquestador»)

- Arriba, una línea con el equipo y los límites y un enlace «Cambiar» a Configurar. Fuera de esta pantalla quedan la
  ficha, el editor de equipo, los modelos, el perfil de pruebas y los minutos.
- Cuatro secciones plegables, una abierta cada vez: **1 · Qué quieres** (petición y requisitos, con «Congelar») →
  **2 · El plan** (resumen a todo el ancho, con párrafos, riesgos y el grafo de tareas; Aprobar) → **3 · En marcha**
  (la oficina y los terminales) → **4 · Veredicto y entrega**.
- Sin desplazamiento de la pestaña entera; cada sección usa el alto disponible. El panel inferior (Terminal y demás)
  empieza plegado en esta pantalla. La barra que no se mueve se arregla después de confirmar cuál es (§1, punto 14).
- La cuota aparece una sola vez (la barra de estado) y el estado de cada agente, en su terminal y su personaje.

### 3.6 Terminales por agente (columna derecha)

Un terminal plegable por agente, por el orquestador y por cada juez, con el mismo color que su personaje. Tres niveles,
de menos a más coste:

| Nivel | Qué da | Qué cambia por dentro | Ola |
|---|---|---|---|
| 1 · Transcripción | Todo lo que dice y hace cada agente, completo, en su terminal, con desplazamiento automático; el juez separado de su ejecutor | Nada en el lanzamiento de los CLIs: usa los eventos que ya llegan. Hay que dar identidad propia a los eventos del juez | 4 |
| 2 · Fluidez | El texto aparece mientras se escribe, como en las extensiones | Claude necesita `--include-partial-messages`: cambia la línea de lanzamiento y hay que comprobar que no invalida la relajación medida del confinamiento de escritura (ADR-0018) | 5 |
| 3 · Escribir | Un cuadro en cada terminal para hablar con ese agente sin parar lo demás | stdin abierto, un comando nuevo en el motor, una ADR y mediciones en vivo (§3.7) | 5 |

### 3.7 Escribir a cada uno

| A quién | Qué pasa con tu mensaje | Estado |
|---|---|---|
| Desarrollador con Claude | Le llega en el mismo turno, en cuanto acaban las herramientas que está usando (`--input-format stream-json`, prioridad `next`) | Documentado por Anthropic; sin medir en 2.1.290 |
| Desarrollador con Codex | (a) Le llega en el mismo turno con `turn/steer` del app-server, **o** (b) se pausa y reanuda con tu nota, lo que gasta un intento | (a) necesita tu OK y tu cambio en CLAUDE.md: hoy el app-server solo está permitido para leer la cuota. `turn/steer` existe en el código de la 0.160.0; sin medir |
| Orquestador | No tiene proceso durante la ejecución. Tu mensaje le hace revisar el plan y **proponerte** cambios; nada cambia sin tu aprobación, y si toca requisitos congelados, pasa por descongelar y volver a congelar | Diseño propio |
| Juez | Tu nota entra en su expediente y **sale escrita en el veredicto** («Nota tuya: …»). El juez sigue siendo de otra empresa y nada de lo que diga el ejecutor le llega por esta vía | Regla nueva; toca la promesa central de la app, va en la ADR |
| Gemini | Aparcado | — |

Una ADR fija que lo que escribes es **una instrucción del propietario** (hoy las respuestas a preguntas entran como datos,
`HumanAnswerPrompt`), que queda registrada, y que no cambia los requisitos congelados por la puerta de atrás.

### 3.8 Oficina en pixel art (pantalla principal de «En marcha»)

- Papeles como en una oficina: el orquestador es la jefa o el jefe de proyecto, los ejecutores son el equipo de
  desarrollo, el juez es la dirección y los revisores, el equipo de calidad.
- Cada personaje se mueve según su estado: teclea en su mesa mientras escribe, va a la pizarra o al servidor cuando usa
  herramientas, levanta la mano con un bocadillo cuando pregunta, toma un café en pausa, celebra o se lamenta al
  terminar.
- Encima, una placa con nombre: papel · modelo · estado (en marcha, en pausa, terminado).
- Al elegir su terminal, el personaje se para, mira al frente y se ilumina con un aura de su color; elegir el personaje
  abre su terminal.
- Respeta «Reducir movimiento»: con él, posturas fijas (`Orchestrator/Canvas/MotionPolicy.cs`). La información nunca
  depende solo del dibujo: también está en el terminal y en la placa.
- Render: un único reloj a 8-12 imágenes por segundo, hojas de sprites con escalado `NearestNeighbor`, parado cuando la
  vista no se ve.
- **El riesgo es la calidad del arte, no el código.** Primero una prueba corta: un personaje, tres estados, placa y aura,
  dentro de la app. Con ella decides si sigue y de dónde sale el arte. Las opciones son una base con licencia CC0 más
  fotogramas propios, arte propio entero, o arte que tú aportes; si tu arte no permite redistribuirlo, se queda fuera del
  repositorio público. Ningún paquete está elegido: varias licencias están sin confirmar.
- Existe la extensión Pixel Agents (MIT), que hace algo parecido para Claude Code leyendo `~/.claude/projects/`, algo que
  aquí está prohibido. No se copia su arte; aquí los estados salen de los eventos del motor.

### 3.9 Documentación para el propietario

La guía larga se retira. Cuando cada ola cambie pantallas, una página corta por pantalla en la Ayuda de la app («para qué
sirve» y «qué hago aquí»), sin repetir nada. Los documentos `docs/` son para quien construye.

## 4. Límites que no se saltan

- **Consumo:** solo lo lanzado desde la app y el porcentaje de límite que da cada proveedor. El uso de la cuenta fuera de
  la app no se puede saber sin leer credenciales, y eso está prohibido.
- **Codex en el mismo turno:** necesita tu OK y tu edición de CLAUDE.md (§6).
- **Skill por URL:** es una puerta de entrada de código de terceros. Se diseña con el modelo de amenazas de la Fase 7:
  solo GitHub, referencia fijada, revisión antes de activarla y Windows Hello.
- **El juez:** sigue siendo de otra empresa; tus notas son visibles en el veredicto, nunca ocultas.

## 5. Olas

| Ola | Qué | Tamaño |
|---|---|---|
| 1 · Claridad | Inicio con siguiente paso; barra lateral con texto; «Auditar» con «Ir a los requisitos»; «Aplicar» lleva a Trabajar; resumen completo; modelos editables en el asistente (con las listas que ya existen); orden de pruebas editable con «Probarla ahora»; «Presupuesto» → «Límites» con textos llanos; resumen del plan en varias líneas; panel inferior plegado en Trabajar; la barra fija, una vez confirmada | Pequeña |
| 2 · Configurar por tipo | Tipos de proyecto y plantillas editables y propias; detección por ficheros y descripción; skills por tipo; contexto por agente configurable; «Calcular por mí» | Mediana |
| 3 · Proveedores y consumo | Pantalla nueva; selector de modelos según proveedores conectados; consumo por hora; historial del límite | Mediana |
| 4 · Trabajar por pasos | Fuera los duplicados; cuatro secciones plegables; columna de terminales nivel 1 con el juez separado | Grande |
| 5 · Hablar con los agentes | ADR; niveles 2 y 3; Claude medido en vivo; Codex según tu decisión; orquestador y juez según §3.7 | Grande, con ejecuciones reales |
| 6 · La oficina | Prueba corta → tu decisión → oficina completa sincronizada con los terminales | Prueba pequeña; el resto, grande |

Tras la ola 1 se puede repetir el día 0; la semana de uso (docs/22) se reanuda tras la ola 4. Las Fases 7, 16 y 15
siguen después, salvo el alta de skills por URL, que entra con el modelo de amenazas de la Fase 7.

## 6. Decisiones del propietario

Tomadas el 2026-11-16 a las 11:16 («Si a los dos si es lo que recomiendas»):

1. **Orden: sí.** El rediseño va antes que la Fase 7; la semana de uso espera a la ola 4. La ola 1 empieza ya.
2. **Codex en el mismo turno: sí.** Se usará `turn/steer` del app-server de Codex. Antes de la ola 5 hacen falta la línea
   nueva en CLAUDE.md, que escribe el propietario (se le dará redactada), y una ADR. Hasta entonces, CLAUDE.md sigue
   permitiendo el app-server solo para leer la cuota.
3. **El arte de la oficina:** se decide después de la prueba corta de la ola 6.

## 6.1 Restos de la ola 1 (van con la ola 2)

Bajos, del lado seguro, hallados en la última revisión de la ola 1 (2026-11-16):

- «Usar el perfil propuesto» del Orquestador pide Hello pero no deja `ProjectSetupApplied.VerificationProfileHash`; el
  asistente dice entonces «No las has aceptado en esta app» (falso) y vuelve a pedir confirmación. Registrar el hash
  también ahí.
- La reanudación sola por consentimiento de una ejecución cuyo perfil rompe las reglas nuevas guarda el motivo con el
  detalle técnico en inglés (`LoggedTexts.cs:168`); usar el texto traducido. Cada sesión añade otra alerta
  `run.not-resumable`: no repetirla.
- Con el asistente ocupado, la lista de pasos puede quedar marcando un paso que no está en pantalla (solo visual).
- El comentario de `SetupChecks.cs:43` dice que el análisis de comillas es el del runtime de C; no lo es en `"a""b"` ni
  con espacios Unicode. La confirmación enseña el resultado real; corregir el comentario o el análisis.
- Aceptar como origen un historial que trae el propio repositorio hace contar sus entradas `ProjectSetupApplied`; la
  pregunta ya avisa, pero conviene decirlo también ahí.

## 7. Fuentes

- Claude Code, entrada en streaming y prioridades: https://code.claude.com/docs/en/agent-sdk/streaming-vs-single-mode y
  https://code.claude.com/docs/en/agent-sdk/typescript
- Codex app-server, `turn/steer` y `turn/interrupt`: https://learn.chatgpt.com/docs/app-server; en el código de la
  versión usada: https://raw.githubusercontent.com/openai/codex/rust-v0.160.0/codex-rs/app-server-protocol/src/protocol/common.rs
- Gemini ACP: https://geminicli.com/docs/cli/acp-mode/ (fuente única sobre su sustitución; sin confirmar)
- Pixel Agents: https://github.com/pablodelucca/pixel-agents
- Kenney, licencia CC0: https://kenney.nl/support
