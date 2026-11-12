# Documentación de Agent Studio

Especificación funcional: [referencias/prompt-maestro.md](referencias/prompt-maestro.md). Referencias visuales en
[referencias/](referencias/). Estado real de la Fase 0, con evidencias: [09-informe-fase-0.md](09-informe-fase-0.md).

## Entregables de la sección 19 del prompt maestro

| # | Entregable | Dónde |
|---|---|---|
| 1 | Análisis de viabilidad técnica | [01-viabilidad-y-riesgos.md §1-2](01-viabilidad-y-riesgos.md) |
| 2 | Riesgos y limitaciones | [01-viabilidad-y-riesgos.md §3-4](01-viabilidad-y-riesgos.md#3-riesgos) |
| 3 | Arquitectura recomendada y alternativas | [02-arquitectura.md](02-arquitectura.md) + [adr/](adr/) |
| 4 | Diagramas Mermaid | Contexto, componentes, dependencias, ejecución y estados en [02](02-arquitectura.md); navegación en [03](03-pantallas-y-navegacion.md#8-navegación); entidad-relación en [05](05-modelo-de-datos.md#2-diagrama-entidad-relación); adaptadores en [06](06-integracion-proveedores.md#2-ciclo-de-vida-de-un-adaptador); debate y auditoría en [07](07-debate-arena-y-juez.md#4-protocolo); fases en [08](08-plan-y-criterios-de-aceptacion.md#vista-general) |
| 5 | Diseño de pantallas y navegación | [03-pantallas-y-navegacion.md](03-pantallas-y-navegacion.md) |
| 6 | Estructura de solución .NET | [02-arquitectura.md §4](02-arquitectura.md#4-estructura-de-la-solución-y-dependencias) y `AgentStudio.slnx` |
| 7 | Contratos de los componentes principales | [04-contratos.md](04-contratos.md) y `src/AgentStudio.Application/` |
| 8 | Modelo de datos inicial | [05-modelo-de-datos.md](05-modelo-de-datos.md) y `src/AgentStudio.Infrastructure/Persistence/Schema/` |
| 9 | Estrategia de integración de proveedores | [06-integracion-proveedores.md](06-integracion-proveedores.md) |
| 10 | Diseño detallado del sistema de debates | [07-debate-arena-y-juez.md](07-debate-arena-y-juez.md) |
| 11 | Plan de desarrollo incremental | [08-plan-y-criterios-de-aceptacion.md](08-plan-y-criterios-de-aceptacion.md) |
| 12 | Criterios de aceptación verificables por fase | ídem, una tabla por fase |

## Ampliaciones pedidas por el usuario (2026-10-15)

| Funcionalidad | Dónde |
|---|---|
| Catálogo de skills por sector TI, con tags, activación por ámbitos y salud | [10-catalogo-de-skills.md](10-catalogo-de-skills.md) + [ADR-0007](adr/0007-catalogo-de-skills.md) |
| Bitácora diaria obligatoria de prompts | [11-bitacora.md](11-bitacora.md) + [ADR-0008](adr/0008-bitacora-obligatoria.md) |

## Diseños por fase

| Fase | Diseño |
|---|---|
| 3 · Orquestación (cerrada el 2026-10-18) | [13-fase-3-orquestacion.md](13-fase-3-orquestacion.md) |
| 4 · Observabilidad y memoria (integrada el 2026-10-18; estado en [08](08-plan-y-criterios-de-aceptacion.md#fase-4--observabilidad-y-memoria)) | [14-fase-4-observabilidad-y-memoria.md](14-fase-4-observabilidad-y-memoria.md) |
| 5 · Debate Arena y juez (cerrada el 2026-10-21; [demostraciones](evidencias/fase5-demostraciones.md)) | [15-fase-5-debate-arena-y-juez.md](15-fase-5-debate-arena-y-juez.md) |
| 6 a 11 · Ampliación «plataforma de proyectos»: trabajo desatendido, trazabilidad, Git remoto, entornos (aprobada el 2026-10-20) | [16-ampliacion-plataforma-de-proyectos.md](16-ampliacion-plataforma-de-proyectos.md) |
| 6 · Auto Doctor, Auto Graph, entorno declarado y diagramas: el diseño que fija F6-1 a F6-11, sin ejecutar nada del repositorio, y su reparto con la Fase 16 (propuesta del 2026-11-12, pendiente de tus decisiones) | [20-fase-6-auto-doctor-y-auto-graph.md](20-fase-6-auto-doctor-y-auto-graph.md) |
| 12 a 15 · Índice semántico, constructor visual de agentes, bloque final (idiomas, lenguaje claro, ayuda, documentación para clientes y la Fase 9) y comprobación completa (propuesta del 2026-10-30, pendiente de tus decisiones) | [17-constructor-visual-de-agentes.md](17-constructor-visual-de-agentes.md) |
| 16 · Documentación viva del proyecto: documentación ejecutiva, funcional, técnica, de calidad y de cambios, con diagramas y diccionarios, que se construye mientras trabajan los agentes y se exporta como un paquete (propuesta del 2026-11-12, pendiente de tus decisiones) | [19-documentacion-viva-del-proyecto.md](19-documentacion-viva-del-proyecto.md) |

## Decisiones de arquitectura (ADR)

| ADR | Decisión |
|---|---|
| [0001](adr/0001-wpf-y-dotnet-10.md) | WPF sobre .NET 10 (LTS) |
| [0002](adr/0002-capas-y-puertos.md) | Once proyectos, dominio puro y puertos en Application |
| [0003](adr/0003-integracion-por-clis-oficiales.md) | CLIs oficiales, solo uso personal |
| [0004](adr/0004-registro-de-decisiones-append-only.md) | Registro de decisiones y evidencias append-only con cadena de hashes |
| [0005](adr/0005-sqlite-y-markdown.md) | SQLite con SQL explícito y Markdown legible |
| [0006](adr/0006-juez-independiente-verificable.md) | Juez independiente con veredicto verificable |
| [0007](adr/0007-catalogo-de-skills.md) | Catálogo de skills por sector, con tags, ámbitos y salud |
| [0008](adr/0008-bitacora-obligatoria.md) | Bitácora diaria obligatoria, por decorador y por hook |
| [0009](adr/0009-google-aparcado.md) | Google aparcado: Gemini CLI sin cuentas personales y Antigravity no se puede envolver |
| [0010](adr/0010-estado-derivado-del-registro.md) | El estado de una ejecución se deriva del registro de decisiones y se reanuda por reproducción; SQL explícito sin Dapper |
| [0011](adr/0011-tareas-condicionales.md) | Tareas condicionales: estado `Skipped`, aristas con rama y política de unión |
| [0012](adr/0012-worktrees-fuera-del-repo.md) | Worktrees fuera del repositorio, refs propias, `merge-tree` con CAS y entrega a tu rama solo con aprobación (hooks y firma desactivados dentro, respetados al entregar) |
| [0013](adr/0013-protocolo-del-orquestador.md) | Protocolo del orquestador: esquema portable, parseo estricto, validador, una corrección, aprobación del usuario y repositorio del usuario intacto |
| [0014](adr/0014-independencia-del-juez-con-dos-vendors.md) | Independencia del juez con dos vendors: el orquestador y los ejecutores en uno y el juez (Codex en solo lectura) en el otro; ADR-0006 sin cambios |
| [0015](adr/0015-lienzo-nodify.md) | Lienzo del orquestador con Nodify 7.3.0: cierra la D6 tras el spike (React Flow queda de respaldo) |
| [0016](adr/0016-perfil-de-verificacion.md) | Perfil de verificación de confianza, congelado por ejecución; resultados solo de TRX o JUnit y entorno fijo sin nodos de build |
| [0017](adr/0017-verificacion-aislada-baja-integridad.md) | Verificación aislada bajo integridad Low: build y test corren en una copia del commit en carpetas Low (bloquea escrituras, no lecturas ni red), activado por defecto y de una en una |
| [0018](adr/0018-modelo-de-amenaza-fase-3.md) | Modelo de amenaza de la orquestación (aceptado el 2026-10-18): contenido del repositorio y agentes con sus capacidades por defecto dentro; un proceso concurrente del mismo usuario a integridad Medium, las lecturas y la red de Low, el PATH y lo que cada CLI escribe en su perfil fuera, como residuales |
| [0019](adr/0019-consumo-como-proyeccion-del-registro.md) | El consumo es una proyección del registro de decisiones: payloads v2, deduplicación por sesión, seis orígenes de cada cifra y agregados con cobertura; sin tabla de uso |
| [0020](adr/0020-cuota-pasiva-en-claude-y-lectura-opcional-en-codex.md) | La cuota es pasiva en Claude y, en Codex, una lectura opcional del app-server (opt-in en Ajustes) con lista cerrada de mensajes y respuesta por lista blanca; la entrada queda abierta hasta la respuesta (medido) |
| [0021](adr/0021-salud-del-contexto-por-paso.md) | La salud del contexto se mide por paso con la ventana del modelo del paso; umbrales globales validados (70/85/95 % por defecto); Codex `exec`, «no expuesto» |
| [0022](adr/0022-memoria-append-only-anclada.md) | La memoria es append-only y anclada al registro, con confianza por origen y cuarentena de lo heredado; el Markdown solo se exporta y el juez nunca recibe memoria |
| [0023](adr/0023-empaquetado-de-contexto-por-niveles.md) | El empaquetado de contexto va por niveles y falla cerrado si los requisitos obligatorios no caben |
| [0024](adr/0024-recomendador-asistido-con-mcp-local.md) | El recomendador asistido de skills usa un servidor MCP local de solo lectura (el duodécimo proyecto), solo en lanzamientos de lectura y con OK por uso; medido con Claude Code 2.1.290. Adenda del 2026-11-03 (Fase 12): una tercera herramienta, `search_project_history`, con `--history`, para el planificador; conectada en la integración de la Fase 12, con su medición real pendiente |
| [0025](adr/0025-resumen-diario-determinista.md) | El resumen diario es determinista a partir de la bitácora; la versión por agente queda opcional y con OK |
| [0026](adr/0026-debate-como-unidad-del-registro.md) | Un debate es una unidad del registro: id propio, cabecera inmutable anclada, estado reproducido, el texto de los agentes como evidencia y una sola actividad con agentes por proyecto |
| [0027](adr/0027-independencia-del-juez-en-debates-con-dos-vendors.md) | Independencia del juez con dos vendors: la regla de los productores no se dispensa; la de los participantes, solo de forma explícita y registrada (adenda de ADR-0006) |
| [0028](adr/0028-turnos-sin-estado-y-rondas-selladas.md) | Turnos sin estado y rondas simultáneas y selladas; los límites los mide el sistema y lo que falta se estima |
| [0029](adr/0029-veredicto-de-un-debate.md) | El veredicto de un debate: el juez de la Fase 3 con su brief, el dossier a ciegas, la resolución estructurada y la valoración del juez en los modos de elección |
| [0030](adr/0030-auditar-proyecto-completo.md) | «Auditar proyecto completo»: el commit HEAD, la verificación aislada, un dossier índice y la regla de los productores con autores declarados; sin juez con el registro roto |
| [0031](adr/0031-auditor-determinista-del-orquestador.md) | Auditor determinista del orquestador: siete comprobaciones, alertas y petición de replanificación como ejecución de seguimiento |
| [0032](adr/0032-impugnacion-de-veredictos.md) | Impugnación: un debate de revisión que hereda quién puede juzgar y cambia de juez; sus efectos en la entrega |
| [0033](adr/0033-alcance-de-la-ampliacion-plataforma-de-proyectos.md) | El alcance de la ampliación «plataforma de proyectos»: qué entra (trabajo desatendido, trazabilidad como vistas del registro, GitHub, aislamiento y Hello), con qué cambio de perspectiva y qué se descarta |
| [0034](adr/0034-esperas-por-limite-y-reanudacion-con-consentimiento.md) | Esperas por límite de uso hasta el reinicio con margen y reanudación con un consentimiento por ejecución en su cabecera; nunca se cambia de cuenta, proveedor ni modelo (enmienda ADR-0010) |
| [0035](adr/0035-final-clasificado-y-renovacion-del-contexto.md) | El final clasificado de cada intento y la renovación del contexto con un traspaso que construye el sistema; nunca `--resume` de un intento interrumpido |
| [0036](adr/0036-vida-de-la-app-y-bandeja.md) | La vida de la app y la bandeja: cerrar y seguir, el panel como proyección, la salida gestionada en el registro, la suspensión y el reloj |
| [0037](adr/0037-windows-hello-y-bloqueo-de-la-interfaz.md) | Windows Hello en las aprobaciones críticas y un bloqueo de la interfaz que no detiene el trabajo; el SDK de Windows solo en Desktop (amplía ADR-0001) |
| [0038](adr/0038-hito-backlog-y-tablero.md) | El hito como conjunto de requisitos congelado con nombre (cabecera inmutable y anclada, estado reproducido), el backlog de solo añadir y anclado, y el tablero como proyección del registro: ninguna tarjeta llega a «hecho» sin veredicto (Fase 9) |
| [0039](adr/0039-vinculos-de-trazabilidad-y-participacion.md) | Vínculos de trazabilidad explícitos y de solo añadir, inferencias como sugerencia que solo cuenta si la confirmas, trailers de los commits del sistema y seis papeles de participación (Fase 9) |
| [0040](adr/0040-consumo-por-hito-y-actividad-y-presupuestos.md) | Consumo por hito y por actividad, presupuestos por proyecto y por hito y real frente a estimado (esqueleto del P0 de la Fase 9, completado por WP2; amplía ADR-0019) |
| [0047](adr/0047-indice-semantico-local.md) | Índice semántico local (Fase 12): multilingual-e5-small int8 en tu equipo y sin red, descargado una vez con tu OK y su huella fijada; búsqueda híbrida (palabras sin palabras vacías y significado, RRF k = 60); la bitácora anclada como memoria con su confianza; el juez no lo recibe (enmienda ADR-0023, amplía ADR-0022 y ADR-0024). Adendas del 2026-11-03 (el casado exige el recibo de la bitácora; los nativos de ONNX Runtime; el corte de lo que solo encuentra el significado) y del 2026-11-04 (la bitácora con dos textos y la fusión de D-05) |
| [0048](adr/0048-constructor-visual-equipo-y-plan.md) | El constructor visual (Fase 13): un lienzo con las capas «Equipo» y «Plan», la tira del equipo, el nodo del repositorio y la ficha del proyecto; la capa Equipo es la plantilla, y las carpetas y las instrucciones de cada agente pasan a mandar |
| [0049](adr/0049-analizador-del-montaje-y-orden.md) | El analizador del montaje y el orden: reglas deterministas con arreglos tipados; las notas de orden y «Revisar el orden» solo sugieren, y decide el validador |
| [0050](adr/0050-modelos-y-potencia.md) | Modelos y potencia: un catálogo local sin precios, lo aprendido del registro, el esfuerzo en la cabecera, una recomendación determinista que dice por qué y el estimador de uso |
| [0051](adr/0051-preguntas-de-los-agentes.md) | Las preguntas de los agentes: una salida del informe con opciones, su registro, la continuación con la respuesta como dato y su seguridad; el juez nunca pregunta ni recibe el texto del agente como prueba |
| [0052](adr/0052-analisis-del-consumo-y-contexto-por-partes.md) | El análisis del consumo, determinista siempre y con un agente opcional del vendor que no produjo; el contexto por partes en `AgentRunStarted` v3 |
| [0053](adr/0053-windows-hello-como-barrera-fija.md) | Windows Hello como barrera fija: una lista en el código que ningún ajuste ni fichero desactiva, el bloqueo de Windows comprobado como alternativa y una sola confirmación a la vez (amplía ADR-0037) |
| [0054](adr/0054-idiomas-y-lenguaje-claro.md) | Idiomas y lenguaje claro: recursos `.resx` con el castellano de España de origen, códigos con parámetros en las capas de abajo, el idioma de Windows por defecto y elegible en Ajustes (al reiniciar), lo que no se traduce y la guía de textos |
| [0055](adr/0055-configurador-por-proyecto.md) | El configurador por proyecto (Fase 13): siete pasos que proponen, dejan revisar y aplican por las puertas de siempre y dejan `ProjectSetupApplied`; los perfiles de verificación de Node y Python, en integridad baja y con su entorno aislado |
| [0056](adr/0056-ayuda-y-agente-de-ayuda.md) | La ayuda (Fase 14): local y sin cuota primero, y un agente de solo lectura con tu OK en cada pregunta, con una tarjeta de lo que se le envía (sin el proyecto ni las rutas salvo que lo marques) y acciones de una lista cerrada |
| [0057](adr/0057-documentacion-para-clientes.md) | La documentación para clientes (Fase 14): un modelo determinista y sin idioma desde el registro, cuatro formatos (Markdown, HTML, PDF y Word), lo escrito por agentes marcado y tu revisión antes de exportar con `ClientDocumentExported` |

## Investigación y evidencias

- [investigacion/2026-10-15-proveedores-y-stack.md](investigacion/2026-10-15-proveedores-y-stack.md): términos e
  interfaces de Claude Code, Codex, Gemini CLI y ACP; versiones y licencias del stack.
- [investigacion/2026-10-15-skills.md](investigacion/2026-10-15-skills.md): estándar Agent Skills, cómo carga
  skills cada CLI, fuentes de calidad, indicadores de salud y seguridad.
- [investigacion/2026-10-15-candidatas-skills.md](investigacion/2026-10-15-candidatas-skills.md): **propuesta
  pendiente de aprobación** de skills para los sectores sin cobertura (arquitectura, documentación, backend).
- [investigacion/2026-10-30-spike-embeddings.md](investigacion/2026-10-30-spike-embeddings.md): el spike del modelo del
  índice semántico (Fase 12): tokenización, tamaño, velocidad en esta CPU y calidad en castellano de multilingual-e5-small,
  con el arnés en [`spikes/e5-embeddings/`](../spikes/e5-embeddings/README.md).
- [evidencias/fase0-auto-doctor.png](evidencias/fase0-auto-doctor.png): la app de la Fase 0 mostrando el estado real
  de los proveedores en la máquina de desarrollo.
- [12-accesibilidad-tema-oscuro.md](12-accesibilidad-tema-oscuro.md): revisión de contraste AA y de foco de teclado del
  tema oscuro (F1-6), con los pares medidos antes y después y capturas
  [antes](evidencias/fase1-contraste-antes.png) y [después](evidencias/fase1-contraste-despues.png). Su §9 recoge la
  ampliación de la tanda 1 (2026-10-30): estilos oscuros de los controles, barra de título oscura y foco inicial. Su §10,
  la de la tanda 2 (2026-11-03): la barra común de contexto, cuota y presupuesto, la bitácora legible y los nombres
  accesibles de los elementos de las listas.
