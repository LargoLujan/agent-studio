# 23 · Fase 7: seguridad y pruebas

**Estado · 2026-11-15:** desarrollo técnico parcial autorizado por el usuario durante el relevo. Propuesta en copias aisladas; no aplicada al origen. La fase no está terminada.

## Alcance y precedentes

El índice maestro es [README](README.md). Los criterios siguen en [08](08-plan-y-criterios-de-aceptacion.md#fase-7--seguridad-pruebas-y-empaquetado) y las decisiones D-01 a D-04, en [16](16-ampliacion-plataforma-de-proyectos.md). La adenda de [22](22-semana-de-uso-real-y-plan-revisado.md) permite adelantar desarrollo; no sustituye las demostraciones, el día 0 ni la semana de uso.

Tres implementadores trabajan en partes independientes y el integrador amplía la vista de permisos. El integrador reúne las propuestas en su copia y ejecuta una sola vía de compilación y pruebas. Un código recibido o una captura guardada no son una comprobación satisfactoria.

| Criterio | Trabajo de esta tanda | Qué no acredita |
|---|---|---|
| F7-2 | Resolver enlaces y junctions en `Security/Paths/PathAccessPolicy`, incluidos componentes intermedios y sufijos que aún no existen; denegar si no se puede comprobar | Confinamiento de procesos ni ausencia de carreras entre comprobar y abrir |
| F7-3, parcial | Validar también el identificador de sesión nueva de Claude con el filtro de tokens que ya usa la reanudación | Un fallo explotable en el proveedor actual, que genera GUID válidos, ni el cierre de la política de red |
| F7-5 | Completar las pruebas de la cadena con fallos de verificación y rechazo del juez; usar proveedores guionizados y el registro real | Una ejecución contra proveedores o remotos reales |
| F7-7 | Añadir bóvedas ligadas al proyecto, con carpeta y entropía propias; conservar la bóveda global del doctor y sus aprobaciones | Protección frente a otro proceso de la misma cuenta ni cifrado del registro completo |
| F7-8, parcial | Añadir a «Herramientas» los permisos declarados por agente y paso: vendor, proveedor, modelo, modo, sandbox y red, con sus límites | Una medición del CLI instalado, el cierre completo de F7-8 o una garantía de aislamiento |

## Fronteras que se conservan

El repositorio, el texto de los agentes y el código que producen son entradas no fiables. El sistema valida los planes y construye el dossier del juez. El juez sigue siendo de otro vendor y no recibe memoria, skills ni índice semántico. Las compilaciones que cuentan como evidencia las ejecuta el sistema.

El registro de decisiones y la memoria siguen siendo de solo añadir, con comprobación de cadena y ancla. El estado se deriva al leerlos; no se añade una tabla paralela de estado para estas mejoras. Ninguna prueba desactiva triggers ni modifica hechos anteriores.

La política de rutas es un componente de decisión; en la base recibida solo tiene consumidores de prueba. Resolver enlaces la fortalece, pero no hace que un CLI quede confinado por ella. Un cambio concurrente del sistema de ficheros después de comprobar una ruta sigue siendo un riesgo. El aislamiento fuerte de F7-4 se mide por separado.

Tampoco se resuelven los alias de nombres cortos 8.3 ni los enlaces duros como identidades de un mismo fichero. Los puntos de reanálisis que la política no reconoce se rechazan. Estas limitaciones impiden presentar la política como una barrera completa del disco.

La bóveda nueva contiene solo secretos propios de Agent Studio. No busca, importa ni migra credenciales de Claude, Codex, Gemini, Git o `gh`. La bóveda global usada por el doctor conserva su formato y su ruta. Las carpetas de proyecto y la entropía evitan mezclar sus datos por error; DPAPI sigue usando la cuenta actual de Windows. Las decisiones de presencia pertenecen al consumidor y no se eluden desde el almacenamiento.

La bóveda de proyecto comprueba las entradas de su raíz, subcarpeta y fichero; confía en los ancestros de la raíz elegida por la app. No evita carreras de sustitución ni trata los enlaces duros como una identidad protegida. No se ofrece como defensa frente a otro proceso de la misma cuenta.

La vista de permisos reutiliza `EffectiveTools.For`, que calcula las herramientas y el sandbox con los argumentos del lanzamiento real. Reutiliza los pasos del perfil de verificación abierto. Distingue una red declarada como desactivada de un argumento que no informa de ella. Bash puede dar acceso a red aunque no haya WebFetch o WebSearch. Low limita escrituras, pero no bloquea las lecturas ni la red. La vista no ejecuta una sonda, no modifica permisos ni concede herramientas; los cambios siguen pasando por el inspector y sus compuertas.

Un candidato de integración interno puede existir antes del juicio: el juez necesita revisar ese commit. Un rechazo debe impedir la entrega a la rama de la persona y la publicación, sin borrar el candidato ni la evidencia. Un fallo de verificación anterior debe impedir avanzar a ese candidato. Las nuevas pruebas comprueban esas dos situaciones por separado.

## Comprobaciones necesarias

- Rutas: límites de carpeta, concesiones más específicas, enlaces internos y externos, componentes intermedios, destino nuevo, enlace que cambia de destino y fallos de inspección. Los enlaces de prueba solo viven en temporales propios.
- Bóveda: leer y escribir en A y B, el mismo nombre con valores distintos, trasplante de bytes cifrados entre proyectos, ausencia de texto claro en los ficheros, cancelación y archivos inválidos. Solo secretos ficticios y almacenes temporales.
- Cadena: partir del requisito y del hito, leer los hechos de la ejecución y sus vínculos, comprobar las fases alcanzadas y las que no deben alcanzarse, conservar un registro íntegro. Ninguna llamada a un proveedor real.
- Integración: Release sin avisos ni errores, suite CI y suites locales relevantes con TRX nuevos; comprobar la copia congelada antes de generar el parche para revisión.

Los resultados se registran con su salida real en los hitos privados del relevo. Esta lista define qué comprobar, no afirma que ya haya pasado.

**Primera comprobación focal · 2026-11-15.** La propuesta compila en Release con cero avisos y errores. Pasan 24 casos de rutas existentes, 28 de enlaces y junctions (sin omisiones), 23 de bóveda global, 17 de bóvedas por proyecto y 36 de herramientas y cambio de contexto: 128 correctos. La revisión de textos detectó tres fallos de lenguaje claro; se han simplificado los textos y falta repetir esa comprobación. No se atribuyen estos resultados a los ajustes posteriores del adaptador de Claude ni del transporte de pruebas.

Los recorridos locales de Fase 10 detectaron antes un fallo de MSYS al crear una memoria compartida dentro del sandbox. Se propone que sus fixtures lleguen al repositorio bare temporal por stdio con Git nativo, conservando espacios, commits y aserciones. El permiso para ese transporte pertenece solo al proceso de prueba; el ejecutor del producto sigue restringido a GitHub HTTPS. La propuesta debe comprobarse con el mismo aislamiento antes de dar por resuelto el fallo.

**Comprobaciones posteriores · 2026-11-15.** Pasan las 161 pruebas del adaptador de Claude, las ocho de textos claros, las 18 de herramientas, las cinco del inventario de pantallas y las 27 de composición. También pasan los dos casos del transporte nativo, incluido un push y un clone reales con espacios, acento y `%s` en el nombre del bare, y los 19 de navegación. La herramienta de empaquetado parcial supera 36 validaciones con fixtures sintéticos; no genera ni instala un MSIX de producto.

La evidencia de los nuevos recorridos mostró que la fixture elegía el `LocalLow` real aunque usase carpetas de aplicación temporales. Se ha sustituido su fábrica por la misma fábrica Low con raíz y cerrojo propios. No cambia la fábrica del producto. La siguiente comprobación llegó a esa ruta privada, pero el sandbox externo del supervisor impidió asignar la etiqueta obligatoria de Windows. No se amplían sus derechos de propietario ni se cambia Low por «sin aislamiento».

Las comprobaciones OS que necesiten esos derechos se registran como suplementos separados, con cuenta estándar y entorno privado sobre la copia congelada. Conservan el Low real de Agent Studio. Su host no tiene el aislamiento de ficheros o red del sandbox externo; un Job controla el árbol de procesos. No sustituyen la CI ni el veredicto `Verified` del supervisor y sus resultados deben comprobarse por separado antes de preparar la entrega.

## Pendientes que no se cierran con esta tanda

F7-1 conserva su revisión completa del modelo de amenazas. No se atribuye el uso de una skill que no esté disponible. F7-3 necesita comprobar la política de red efectiva y las entradas maliciosas; una instrucción en un prompt no es una barrera del sistema operativo. F7-4 necesita medir AppContainer o la alternativa de contenedor y declarar sus residuales. F7-6 necesita empaquetado, instalación limpia y actualizaciones. F7-8 necesita completar la vista de permisos efectivos con las mismas reglas de lanzamiento y compuertas; no puede mostrar garantías que no se hayan impuesto o medido.

Siguen pendientes Hello, Narrador o Inspect, las comprobaciones visuales, las operaciones remotas reales y la semana de uso. La aplicación al repositorio original, los commits, los pushes y las publicaciones requieren la acción humana que corresponda; esta tanda no los ejecuta.

## Referencias de las API

La API de [.NET para resolver enlaces](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesysteminfo.resolvelinktarget) resuelve enlaces simbólicos y junctions, y puede fallar por ciclos o exceso de niveles. La política debe tratar esos fallos sin ampliar el permiso.

[ProtectedData.Protect](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata.protect) admite entropía adicional y ámbito `CurrentUser`. Se emplea para separar los bytes cifrados por proyecto, manteniendo los límites de DPAPI descritos en D-03.

## Adenda · 2026-11-15 · Salida de datos y publicación, parcial

La propuesta de «Herramientas» añade categorías de datos que pueden salir hacia el proveedor del modelo y las operaciones de publicación. Los ajustes de red de las herramientas no impiden que el CLI envíe su prompt al proveedor. El resumen no afirma que todos los roles reciban el mismo contexto ni enumera envíos reales.

Las reglas de cada operación se leen de `HostingPolicy.CanBePersistent` y `RequiresApprovedVerdict`. Una concesión persistente posible no equivale a una concesión existente: esta pantalla no consulta permisos del proyecto ni autoriza operaciones. También distingue la lectura de sincronización del permiso para publicar. No se inicia ninguna llamada al proveedor, a GitHub ni a la compuerta de aprobación.

Este cambio completa una parte informativa de F7-8. No mide la red ni el aislamiento, no acredita accesibilidad con Narrador o Inspect y no cierra F7-8. Las pruebas nuevas y la compilación quedan pendientes del carril del supervisor.

**Comprobación adicional · 2026-11-15 · 20:05 Madrid.** La copia adicional compila en Release sin avisos ni errores, incluido el ajuste posterior de textos. Pasan 299 pruebas focales sin fallos ni omisiones: 13 del filtro de reglas, 68 de sus defensas existentes, 161 del adaptador de Claude, 21 de herramientas, 8 de textos, 23 de localización y 5 del inventario de pantallas. Se comprobó que los 2.758 ficheros públicos permanecían estables antes de añadir esta nota y la bitácora.

El filtro de reglas excluye también DEL y C1 conforme a su contrato; mantiene espacios y Unicode ordinario en los especificadores. No se presenta como un fallo explotable del CLI ni como cierre de F7-3. Esta propuesta posterior no forma parte del paquete C2 ya verificado: falta su CI completa, la revisión de Claude y la aplicación humana al origen. Las pruebas focales no crean un nuevo veredicto `Verified`.
