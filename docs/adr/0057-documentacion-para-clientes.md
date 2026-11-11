# ADR-0057 · La documentación para clientes: un modelo determinista y sin idioma desde el registro, cuatro formatos y tu revisión antes de exportar

- **Estado:** aceptada · 2026-11-11. Recoge D-58 a D-60 de
  [docs/17 §5.5](../17-constructor-visual-de-agentes.md#55-documentación-para-clientes) y la decisión 10 (a) que aceptaste el
  2026-11-06: cuatro formatos (Markdown, HTML, PDF y Word, con el Open XML SDK, una dependencia nueva con licencia MIT) y lo
  redactado por un agente, opcional y marcado. El P0 de la Fase 14 deja los **contratos** y sus reglas puras
  (`Application/Documents/ClientDocumentContracts.cs`), la carga `ClientDocumentExported`, el propósito `ClientDocumentDraft` y la
  versión de `DocumentFormat.OpenXml` en `Directory.Packages.props`; los implementa **WP-14D**, que termina después del cierre de
  un hito de la Fase 9 (WP3). Se apoya en [ADR-0010](0010-estado-derivado-del-registro.md) (el registro es la fuente y nada se
  escribe en él que no se pueda comprobar), en [ADR-0022](0022-memoria-append-only-anclada.md) (la memoria de confianza) y en
  [ADR-0054](0054-idiomas-y-lenguaje-claro.md) (lo que se traduce y lo que no, con su adenda del mismo día).
- **Contexto:**
  - P15 pide documentación para entregar a clientes que el sistema construya solo, desde lo que ya sabe: el registro
    verificado, la memoria de confianza, los veredictos del juez y las evidencias.
  - Un documento así sale de tu equipo y llega a otra empresa. Los riesgos son los de siempre en lo que se publica: un secreto
    pegado en algún sitio, una ruta de tu equipo, un prompt o la conversación con un agente que no debían salir.
  - Lo escrito por un agente es texto que nadie ha comprobado: si va en el documento, el cliente tiene que saber que lo escribió
    un agente y si tú lo revisaste.
  - Hay que poder volver a producir el mismo documento del mismo estado del proyecto, y elegir el idioma al exportar sin que el
    contenido cambie por ello.
  - El hito (la unidad que cierra la Fase 9, docs/16 D-49) todavía no existe en el código.
- **Decisión:**
  1. **Un modelo, sin idioma y determinista** (D-58). `ClientDocument(Cover, Sections)`:
     - **La portada** (`ClientDocumentCover`) es estructurada: proyecto, hito, fecha, versión del documento y commit. El hito es
       **su nombre** (texto, o nulo en una entrega sin hito): el tipo del hito llega con la Fase 9 y no cambia este contrato.
     - **Siete secciones, en este orden** (`ClientDocumentSectionKind`): resumen, alcance, verificación, decisiones, cambios,
       riesgos y pendientes, y anexos (el índice de evidencias con su huella y un glosario). El título de cada una es un código
       (`ClientDocumentSections.TitleCode`). «Lo que no va» no es una sección sino una regla (punto 6).
     - **Bloques:** párrafo, lista (con o sin numerar), tabla (con tantas celdas por fila como la cabecera) y lista de referencias
       a evidencias (id, huella y qué es).
     - **Cada texto sabe quién lo escribió** (`ClientText`): el **sistema** (un código con argumentos: los títulos, los estados,
       el glosario, las frases de la plantilla), una **persona** o los registros anclados citados tal cual (un requisito, el asunto
       de un commit, el título de una decisión) y un **agente** (con su marca, punto 4). Como el texto del sistema es un código y
       no una frase, el modelo no tiene idioma: es el mismo para los cuatro formatos y para cualquier idioma, y lo único que cambia
       es cómo se pintan los códigos (punto 3).
     - **Determinista:** el mismo estado del proyecto da el mismo modelo, y su `Sha256()` es el de una forma canónica propia
       (campos con su longitud por delante, el instante en UTC y los números con la cultura invariante, versión
       `agent-studio/client-document/1`), igual en cualquier equipo y cultura. Un cambio en la forma canónica sube esa versión.
  2. **Cuatro formatos desde el mismo modelo** (D-59), cada uno un `IClientDocumentFormatWriter`:
     - **Markdown y HTML:** los escribe Agent Studio desde el modelo, en `Memory/Documents`, sin librerías nuevas.
     - **Word (.docx):** con el Open XML SDK (`DocumentFormat.OpenXml` 3.5.1, MIT, de Microsoft), en
       `Infrastructure/Documents/Docx`.
     - **PDF:** el HTML impreso con WebView2 (`CoreWebView2.PrintToPdfAsync`, que ya es una dependencia), en `Desktop/Documents`.
       Imprime de uno en uno, y si la app sale antes de terminar, el fichero no se guarda.
     - Un escritor es puro respecto al modelo: el mismo modelo y el mismo idioma dan los mismos bytes (el PDF, desde el mismo
       HTML). Lo que escribió un agente sale marcado, sea cual sea el formato.
  3. **El idioma se elige al exportar** (D-59; por defecto, el de la interfaz). Los escritores de Memory e Infrastructure no
     pueden leer los recursos del escritorio, así que el escritorio implementa `IUserTextRenderer`
     (`Render(UserText, idioma)`), que pinta cada código con los recursos de ese idioma y con los números y las fechas en su
     cultura; lo que falta en un idioma sale en castellano de España. Lo que escribiste tú o los agentes sale en su idioma, sin
     traducir (ADR-0054, punto 5). `LanguageTags` comprueba que el idioma tiene la forma del nombre de una cultura.
  4. **Lo redactado por un agente es opcional, y va marcado** (D-59).
     - **`IClientDocumentDrafter`** lanza una auxiliar de solo lectura (`AuxiliaryAgentRunner`, propósito `ClientDocumentDraft`),
       en una carpeta vacía, con tu OK en cada uso y la estimación delante. El ejecutor (`AuxiliaryAgentRunner`) fuerza, como para
       la ayuda ([ADR-0056](0056-ayuda-y-agente-de-ayuda.md)), el lanzamiento de solo lectura: sin escritura, shell ni red,
       herramientas solo `Read`, `Grep` y `Glob`, sin directorios adicionales, MCP, skills ni sesión que reanudar, y una carpeta
       que existe y está vacía; si no, falla cerrado antes de registrar nada. Redacta una de las dos partes de
       `ClientDraftKind`: el resumen (`Summary`, en la sección de resumen, en lugar de la plantilla) o una explicación de uso
       (`UsageExplanation`, en los anexos). Solo recibe el modelo determinista, que ya no lleva secretos ni rutas: nunca el
       registro, la memoria ni tus ficheros.
     - **Lo que el sistema se queda** (`ClientDraftRules.Check`): hasta 6 párrafos de 800 caracteres, sin caracteres de control
       ni bidireccionales; un párrafo que nombra una ruta de tu equipo se descarta entero. Lo que no pasa no es un error: se dice
       en `Discarded`. Nada entra en el documento hasta que lo aceptas.
     - **La marca no se puede quitar:** el texto de un agente solo se hace con `ClientText.ByAgent(texto, ClientAgentMark)`, que
       lleva la ejecución que lo escribió y si tú lo revisaste. Los cuatro formatos lo enseñan como «Redactado por un agente ·
       revisado por ti: sí o no». La lista de lo que sale (punto 5) saca esas partes del propio documento, así que no puede
       discrepar de él.
  5. **Lo revisas antes de entregarlo** (D-60). `ClientDocumentBuild` junta el modelo con la lista de lo que sale
     (`ClientDocumentDisclosure`): cuántos secretos se redactaron, qué rutas se quitaron (la lista es para ti: no se escribe en el
     registro), qué partes escribieron agentes y si las revisaste, cuántos riesgos van y si va el consumo. La pantalla enseña
     la vista previa y esa lista.
  6. **Lo que no va** (D-58, «Lo que no va»). Los prompts, las transcripciones de los agentes, la bitácora, el consumo (salvo que
     lo marques) y las rutas de tu equipo no entran en el modelo.
     - **No hay por dónde entren:** la petición de construcción (`ClientDocumentBuildRequest`) solo trae el proyecto, el nombre
       del hito, `IncludeConsumption` (falso por defecto) y los borradores que aceptaste, y la portada no tiene un campo para
       rutas ni para el nombre de usuario.
     - **Lo que el modelo puede comprobar solo** (`ClientDocumentRules.Validate(documento, redactor)`): ninguna ruta de tu equipo
       (`LocalPaths`) ni carácter oculto en lo que escribieron personas o agentes, ni en el proyecto o el hito de la portada;
       **ningún secreto** en ningún texto (también los argumentos de las frases del sistema y la portada): un texto que cambia
       al pasar otra vez por `ISecretRedactor` es `document.not-redacted`, como la tarjeta de la ayuda; las secciones, en su orden
       y sin repetirse; las tablas bien formadas, las listas y secciones no vacías, las huellas de verdad SHA-256 y el commit con
       la forma de un commit. Un modelo con huecos (secciones, bloques o textos nulos) da `document.malformed`, nunca una
       excepción. El resto de la plantilla del sistema es del constructor: se prueba con una prueba de fuga (F14-9).
  7. **«Aprobar y exportar»** (D-60), `IClientDocumentExporter.ExportAsync(ClientDocumentExportRequest)`.
     - **La petición** lleva lo que revisaste (el `ClientDocumentBuild`), los formatos (uno o más, sin repetir), el idioma, la
       carpeta (una ruta absoluta que eliges tú) y quién lo aprobó; `ClientDocumentExportRules.Validate` devuelve los códigos de
       lo que incumple, y entonces no se escribe nada.
     - **Cada exportación es una versión y la anterior se conserva:** la versión es una más que la mayor registrada para el mismo
       hito (o para las entregas sin hito) y va en el nombre de cada fichero (`ClientDocumentFiles.FileName`:
       `mi-tienda-hito-1-v2.docx`, en ASCII minúsculas y con guiones). El nombre solo no basta: dos hitos con el mismo `slug`
       (`Hito 1` y `hito-1`), nombres sin letras latinas (todos dan `documento`) o ficheros que ya tenías en la carpeta chocan.
       **El exportador nunca sobrescribe:** si el nombre está tomado usa `ClientDocumentFiles.WithCollisionSuffix` (`-2`, `-3`…
       antes de la extensión) hasta dar con uno libre, y registra el nombre que usó. La prueba de ese comportamiento es de WP-14D.
     - **Los ficheros se escriben con la escritura atómica de hoy**, uno a uno, donde elijas. El Markdown generado queda siempre
       como evidencia, aunque no lo elijas como formato.
     - **Dentro de la carpeta del proyecto, nunca durante una ejecución** (`IRunActivity`), como la exportación de la memoria:
       escribiría donde trabajan los agentes. Fuera, cuando quieras.
     - **Lo último que hace es dejar `ClientDocumentExported` en el registro.** Si escribir falla, no se registra nada.
  8. **La carga `ClientDocumentExported`** (docs/17 §8.2), `DecisionKind` nuevo al final, detrás de `ProjectSetupApplied`; los
     de la Fase 9 van después. Está fuera de toda ejecución (`RunId` vacío, como las entradas de la memoria) y empieza en `V = 1`.
     - **Qué lleva:** la versión, el hito, el commit, el idioma, la huella del modelo (`ClientDocument.Sha256()`), la evidencia
       del Markdown y su huella, los ficheros (formato, **nombre sin ruta**, huella y tamaño), cuántos secretos se redactaron y
       cuántas rutas se quitaron (**solo la cuenta**), cuántos riesgos van, si va el consumo, las partes de agentes (sección,
       ejecución y si las revisaste) y quién lo aprobó.
     - **Qué no lleva:** ningún texto del documento ni de un agente, ninguna ruta de tu equipo (ADR-0010, punto 5). Una prueba
       recorre sus propiedades.
     - `ClientDocumentExportedPayload.Exporting(…)` valida (versión, commit, idioma, huellas, un fichero por formato con un
       nombre simple que acaba con la extensión de su formato, cuentas sin negativos y quién decidió) y
       `ClientDocumentExportedPayload.Of(petición, resultado)` la hace de lo que aprobaste y de lo que escribió el exportador,
       para que los dos no discrepen. Su texto está fijado en `Phase14PayloadTests`, con el hash de una entrada calculado aparte.
  9. **La dependencia.** `DocumentFormat.OpenXml` **3.5.1** se fija en `Directory.Packages.props`, tras comprobar en nuget.org
     el 2026-11-11 que es la última versión estable (publicada el 2026-03-18, sin otra posterior, sin aviso de obsoleta), que su
     licencia es **MIT** (expresión del `.nuspec`, de Microsoft) y que ni ella ni `DocumentFormat.OpenXml.Framework` 3.5.1 ni
     `System.IO.Packaging` 10.0.2 (lo que trae en `net10.0`) tienen avisos en la lista de vulnerabilidades de nuget.org de ese
     día. Solo se fija la versión: el proyecto que escribe el `.docx` (Infrastructure, WP-14D) la referencia.
- **Alternativas:**
  - **Sin Word** (decisión 10 (b)) o **solo Markdown y HTML** (10 (c)): menos dependencias, pero un cliente corriente pide un
    `.docx` o un PDF.
  - **Un modelo con las frases ya escritas en un idioma:** cada idioma sería otro modelo, los ficheros dorados se
    multiplicarían y la huella del modelo cambiaría con el idioma. Con códigos, el contenido es uno y solo se pinta distinto.
  - **Que los escritores lean los recursos del escritorio:** Memory e Infrastructure no pueden depender de Desktop
    ([ADR-0002](0002-capas-y-puertos.md)); el puerto `IUserTextRenderer` invierte la dependencia.
  - **Que un agente redacte todo el documento:** no sería determinista, no se podría comprobar contra el registro y cualquier
    cosa que viera podría salir. Un agente solo redacta dos partes marcadas, con tu OK.
  - **Marcar lo del agente en una sección aparte, en lugar de en cada texto:** se podría mezclar sin darse cuenta. Con la
    marca en el texto, un agente no puede escribir sin ella.
  - **Guardar en el registro las rutas que se quitaron** para poder enseñarlas luego: el registro quedaría con rutas de tu
    equipo, justo lo que el documento no puede llevar. Se enseñan en la lista de revisión y se registra cuántas.
  - **Otra librería para el PDF** o imprimir con otro motor: WebView2 ya es una dependencia, y el PDF sale del mismo HTML que ves.
  - **Sobrescribir el documento anterior:** se perdería lo que ya se entregó.
- **Consecuencias:**
  - **Pruebas del P0:** `ClientDocumentContractsTests` (formatos, secciones, textos y marcas, reglas del modelo, hash canónico
    en varias culturas, lista de lo que sale, borradores, nombres de fichero, petición de exportar y puertos),
    `Phase14PayloadTests` (el texto de la carga, lo que rechaza, su construcción desde la petición y el hash de la entrada) y
    `DecisionKindCompatibilityTests`, `Phase8PayloadTests` y `DecisionPayloadTests` al día con el tipo y los propósitos nuevos.
    Las de WP-14D (F14-9) usan ficheros dorados por formato y una de fuga con un secreto sembrado y rutas de tu equipo.
  - **Lo que hace WP-14D:** el constructor (`IClientDocumentBuilder`) y los textos del sistema en los recursos
    (`Text_document_…`, en castellano e inglés), los escritores, el exportador, el borrador con un agente, la vista previa y la
    pantalla; y se acaba después del cierre de un hito (Fase 9, WP3), que es de donde sale el documento.
  - **El hito es un nombre** hasta que exista el de la Fase 9. Cuando exista, la versión «una más que la mayor del mismo hito»
    se leerá del registro por ese nombre; un hito renombrado sería otra serie de versiones.
  - **Residuales:**
    - lo que decides incluir (docs/17 §8.3), también un riesgo que preferirías no contar;
    - `LocalPaths` solo reconoce rutas de Windows, de red y de la carpeta personal, y el redactor, formatos de secretos
      conocidos: la lista de lo que sale y tu lectura de la vista previa son la segunda barrera;
    - un texto de un agente puede ser falso aunque esté marcado y revisado: la marca dice quién lo escribió, no que sea cierto;
    - un PDF o un `.docx` de otro equipo puede pintar distinto; lo fijado es el modelo y el Markdown.
