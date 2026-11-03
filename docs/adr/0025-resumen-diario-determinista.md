# ADR-0025 · El resumen diario es determinista a partir de la bitácora, leída por el almacén comprobado; la versión por agente queda opcional y con OK

- **Estado:** aceptada · 2026-10-18. Recoge D-31 de [docs/14](../14-fase-4-observabilidad-y-memoria.md#46-f4-9--resumen-diario) y
  la decisión 3 de su §12, opción (a). WP3 hace el lector y el resumen (`src/AgentStudio.Memory/Summary/`) y la lectura
  comprobada del almacén (`ProjectStorageFiles.OpenForRead`); WP5, el panel.
- **Contexto:**
  - F4-9 pide un resumen diario a partir de la bitácora ([docs/11](../11-bitacora.md)).
  - La bitácora tiene dos escritores con formatos distintos: el producto (`MarkdownDailyJournal`) y el hook de desarrollo
    (`.claude/hooks/bitacora.ps1`), más las líneas «Hecho» que añade Claude a mano, en varias formas.
  - Es texto libre y es contenido del repositorio: el adversario del punto 1 de [ADR-0018](0018-modelo-de-amenaza-fase-3.md).
    Un repositorio de terceros puede traer una bitácora con instrucciones, y un enlace plantado como día de la bitácora
    copiaría al resumen un fichero de fuera del proyecto (la clase H-01 de [ADR-0010](0010-estado-derivado-del-registro.md)).
  - Un resumen por agente gasta cuota, y lo que escribe un agente es una propuesta, no memoria de confianza.
- **Decisión:**
  1. **Determinista, sin agente y sin cuota.** El resumen cuenta y cita; no interpreta. Los mismos bytes dan siempre el
     mismo resumen.
  2. **Lectura comprobada.** El día se lee con `IProjectStorageReader.OpenForRead` (`ProjectStorageFiles`), con la misma
     política que las escrituras del almacén: `.agentstudio` y `bitacora` tienen que ser carpetas de verdad y el día un
     fichero con un solo nombre, abierto sin seguir enlaces y comprobado por ese mismo handle. Una junction o un enlace
     duro se rechazan (`ProjectStorageIntegrityException`) y no se resume nada. Leer no crea nada.
  3. **Formatos que se reconocen.**
     - Del producto: `- **HH:mm:ss** · P-NNNN · actor · proveedor/modelo · #tags · «resumen»` y su
       `  - Resultado P-NNNN · HH:mm:ss · estado · 12,3 s · tokens 1.200 → 340 (exactos)`. Un resultado puede llegar
       después de otras entradas: es del último prompt con su id que hay encima.
     - Del hook: `- **HH:mm:ss** · sesión `xxxxxxxx` · #tags · «extracto»`, también sin sesión y con una palabra en lugar
       de la hora, como las entradas retroactivas escritas a mano.
     - Las líneas «Hecho» (`Hecho:`, `Hecho (en curso):`, `Hecho (00:27, cierre):`, `Hecho (petición «…»):`…).
     - Los títulos, las dos líneas de debajo del título y las líneas en blanco son parte del formato.

     Todo lo demás (notas, líneas rotas) solo se cuenta.
  4. **Contenido**, en Markdown:
     - los prompts por actor, proveedor y modelo (los del hook, por sesión);
     - los resultados por estado, con su duración total, y cuántos prompts del producto no tienen resultado;
     - los tokens: la suma es «exactos» solo si todos los prompts del producto dieron cifras exactas; si no, «parcial:
       exactos en n de m»;
     - los fallos y límites (`Failed`, `LimitReached` y `RateLimited`);
     - las cinco etiquetas más frecuentes, sin `#prompt`;
     - las líneas «Hecho», literales y escapadas, cada una cortada a 500 caracteres;
     - de lo que no se reconoce, solo el número de líneas.

     Todo lo que sale de la bitácora va redactado (`ISecretRedactor`) y escapado, así que no puede formar un título, un
     enlace ni una etiqueta HTML. El resumen cabe en una entrada de memoria (`MemoryContent.MaxCharacters`): las líneas
     «Hecho» que no caben se cuentan al final de su lista.
  5. **Días.** El día es el local (`TimeProvider.LocalTimeZone`).
     - Hoy, o un día que aún no ha llegado, es **provisional**: se devuelve y no va a ninguna parte.
     - Un día cerrado con bitácora va a la memoria como `DailySummary`, con el sistema como origen y
       `bitacora/AAAA-MM-DD.md#sha256:<hash de sus bytes>` como fuente: se resume una vez por contenido.
     - Si la bitácora de un día cerrado cambia (una línea «Hecho» añadida después de medianoche), el resumen nuevo sustituye
       al anterior (`Supersedes`).
     - Un día sin bitácora no deja nada.
     - La escritura en la memoria espera mientras hay una ejecución o una planificación ([ADR-0022](0022-memoria-append-only-anclada.md)).
  6. **No va a los prompts.** `DailySummary` no entra en ningún paquete ([ADR-0023](0023-empaquetado-de-contexto-por-niveles.md))
     y se exporta como el resto de la memoria de confianza.
  7. **La versión narrativa por agente** (opción b de la decisión 3) no se hace en esta fase. Si llega, será una ejecución
     auxiliar (`AgentRunPurpose.DailySummary`) con OK en cada uso, y su texto entrará como propuesta.
- **Alternativas:**
  - **Un resumen por agente, con OK o automático:** gasta cuota, es texto de agente y la bitácora puede traer instrucciones
    de terceros. Automático, además, gastaría sin OK.
  - **Leer la bitácora por su ruta**, como hace hoy el panel «Bitácora de hoy»: un enlace plantado como día copiaría al
    resumen, y de ahí al Markdown versionado, un fichero de fuera del proyecto.
  - **Resumir desde el registro de decisiones:** no tiene los prompts del hook de desarrollo ni las líneas «Hecho».
  - **Un analizador estricto que rechace el día entero ante una línea rara:** la bitácora es texto libre de dos escritores,
    y una nota a mano dejaría el día sin resumen.
- **Consecuencias:**
  - El resumen no redacta en prosa: lista y cuenta.
  - Lo que no se reconoce se pierde, salvo su recuento. Cambiar el formato de un escritor exige cambiar el analizador y la
    bitácora de ejemplo de las pruebas (`tests/AgentStudio.Tests/Memory/Fixtures/bitacora-ejemplo.md`).
  - El hash de la fuente es el de los bytes del fichero: una conversión de saltos de línea (CRLF o LF) cuenta como un
    contenido nuevo y da otro resumen, que sustituye al anterior.
  - Las cifras de tokens del producto son las del CLI; en una sesión reanudada pueden ser acumuladas
    ([ADR-0019](0019-consumo-como-proyeccion-del-registro.md)). El resumen las suma tal cual las escribió la bitácora.
