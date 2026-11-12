# ADR-0054 · Idiomas y lenguaje claro: recursos `.resx` con el castellano de España de origen, códigos con parámetros en las capas de abajo, el idioma de Windows por defecto, lo que no se traduce y la guía de textos

- **Estado:** aceptada · 2026-11-06. Recoge D-47 a D-54 de [docs/17](../17-constructor-visual-de-agentes.md#51-idiomas) y la
  decisión 7 (a) que aceptaste el 2026-11-06 con las demás recomendadas de su §12. Los **cimientos** (este ADR) van en el P0
  de la Fase 13 (D-65), para que todo lo que nazca en el constructor sea traducible y claro desde el principio. La Fase 14
  extrae y reescribe los textos de antes (WP-14A y WP-14B) y añade aquí su adenda; la Fase 15 traduce la primera ola.
- **Contexto:**
  - P11 pide idiomas: por defecto el de Windows, los principales del mundo y, para ti, castellano de España. P12 pide textos
    cortos y claros, con los tecnicismos justos. P13 pide un botón ⓘ en los apartados complejos.
  - Hoy todos los textos de la interfaz están escritos a mano, en castellano: unos 550 en 44 XAML y entre 750 y 1 000 en el
    C# de Desktop, más los que llegan desde Orchestration, Debate y Memory (docs/17 §2). No hay ningún `.resx`, nadie lee
    `CurrentUICulture` y no hay ajuste de idioma.
  - El validador ya separa código y texto (`PlanViolation(Code, Task, Message)` y su `PlanViolationText`): es el patrón que se
    generaliza.
  - La Fase 13 la hacen varios paquetes en paralelo, en worktrees distintos y sin ningún fichero en común (docs/17 §9.1). Un
    solo fichero de textos para todos chocaría en cada integración.
- **Decisión:**
  1. **Los textos de la interfaz salen de recursos `.resx` en Desktop.**
     - `src/AgentStudio.Desktop/Resources/Strings.resx` está en castellano de España, el idioma de origen
       (`<NeutralLanguage>es-ES</NeutralLanguage>` en el `.csproj`), y cada idioma tiene su satélite (`Strings.en.resx`…).
     - **Ficheros por área:** cada paquete pone sus claves en un fichero propio, `Resources/<Área>Strings.resx` y sus
       satélites (por ejemplo, `TeamStrings.resx`). El proveedor de textos encuentra todos los recursos que se llaman
       `AgentStudio.Desktop.Resources.*Strings`, con `Strings` primero, así que nadie tiene que registrar su fichero. Una
       clave vive en un solo fichero, y una prueba lo comprueba.
     - **Acceso tipado:** cada `<Área>Strings.resx` tiene su clase `AgentStudio.Desktop.Resources.<Área>Strings`, que genera
       MSBuild en `obj` (los objetivos `GenerateTypedStrings` e `IncludeTypedStrings` del `.csproj`). No hay ningún
       `.Designer.cs` en el repositorio. Se genera antes de la compilación del marcado, porque el ensamblado temporal de WPF
       compila el código antes de que el build genere los recursos. Los metadatos `StronglyTyped*` de `EmbeddedResource` no
       sirven: su clase no llega al ensamblado temporal, y el build falla.
  2. **Cómo se leen.**
     - **En XAML:** `{l:T Clave}` (`Localization/TExtension.cs`), con `xmlns:l="clr-namespace:AgentStudio.Desktop.Localization"`.
       El valor se lee una vez, al construir el elemento: el idioma solo cambia al reiniciar.
     - **En código:** `Texts.Get(clave)`, `Texts.Format(clave, …)` y `Texts.Render(userText)`
       (`Localization/ResourceTextProvider.cs`), o la clase tipada.
     - **Una pantalla que pinta en un idioma dado** (una prueba, un render por idioma) recibe un `IUiTextProvider` propio en
       vez de usar `Texts.Current`. Así lo hace `InfoButton.TextProvider`.
     - **Una clave que no existe** sale como `[Clave]`, nunca como un texto vacío. Las pruebas de claves lo impiden.
  3. **Las capas de abajo no escriben frases para la interfaz:** devuelven un código con parámetros.
     - `UserText(Code, Arguments)` está en `Application/Localization/UserText.cs`. El código son palabras en minúscula unidas
       por `.` y `-` (`delivery.no-integration`). Su recurso es `Text_` y el código con `_` (`Text_delivery_no_integration`),
       igual en todos los idiomas.
     - **Los argumentos** solo pueden ser texto (ids, nombres y rutas tal cual), números, instantes y duraciones
       (`Domain/Common/TextArguments.cs`). Desktop formatea los números y las fechas con la cultura del idioma. El texto de un
       agente nunca es argumento de una frase del sistema: se enseña aparte y enmarcado como escrito por un agente.
     - **Los tipos del dominio** llevan su código y sus argumentos con la misma regla: `Finding` del analizador,
       `ModelRecommendation` y `ConsumptionFinding`. Los códigos que comparten varios paquetes están en `UserTextCodes`.
     - **Los mensajes de excepción y los registros técnicos** siguen en inglés, como manda `CLAUDE.md`. La Fase 14 convierte en
       códigos lo que hoy llega a la pantalla desde Orchestration, Debate y Memory.
  4. **El idioma** (D-49), en `Localization/UiLanguages.cs`.
     - **Elección:** el que eliges en «Ajustes» → «Idioma» (`AppSettings.Language`, el nombre de una cultura de la lista).
       Sin elección, el de la interfaz de Windows si está en la lista: el mismo nombre o, si no, el mismo idioma (`es-MX` es
       castellano de España y `en-GB`, inglés). Si no está, inglés.
     - **Ajustes:** un idioma que no está en la lista, en un fichero editado a mano, se lee como «sin elección».
     - **Cuándo se aplica:** al arrancar y una sola vez (`Texts.UseLanguage`). Pone la cultura de la interfaz de los hilos,
       la de las clases tipadas y el proveedor. Aplicar otro idioma después falla: se aplica al reiniciar, y así no quedan
       textos a medio cambiar.
     - **Si falta un texto** en un idioma, sale en castellano de España: es el recurso neutro.
     - **La cultura de los números y las fechas** no cambia en el P0. Las pantallas de antes formatean con la suya (invariante
       o es-ES fijo en tres sitios) hasta que la Fase 14 las pase a la cultura del idioma.
  5. **Qué no se traduce** (D-48):

     | No se traduce | Por qué |
     |---|---|
     | El registro, las evidencias, la memoria y la bitácora | Es historia anclada con su SHA-256 y append-only: se enseña tal cual. Lo nuevo que el sistema escriba para personas se guarda como código y parámetros, cuando el formato lo admite |
     | Los prompts de los agentes | Son el protocolo (ADR-0013), probado y guardado como evidencia. Los agentes reciben un dato, «responde a la persona en es-ES», para lo que escriben para ti |
     | Lo que escribiste tú (requisitos, ficha, instrucciones) y lo que escribieron los agentes | Es contenido, no interfaz |
     | Nombres propios y de productos, comandos, rutas y códigos | Ídem. El nombre de cada idioma va en su propia lengua («English», «Deutsch») |

     Sí se traducen la interfaz, los avisos, la bandeja, los botones ⓘ, la ayuda, los textos del sistema en la documentación
     para clientes y los mensajes que salen de códigos.
  6. **Qué idiomas** (D-50, decisión 7 (a)):
     - castellano de España (el origen) e inglés, completos y revisados por una persona;
     - una primera ola de nueve en la Fase 15, con traducción automática que revisa un agente de otro vendor: chino
       simplificado, hindi, portugués de Brasil, francés, alemán, japonés, ruso, coreano e italiano;
     - la lista dice de cada uno quién lo revisó (`TranslationReview`): «Revisado por una persona» o «Traducción automática
       revisada»;
     - los idiomas de derecha a izquierda (árabe, urdu, hebreo y persa) quedan aplazados.
  7. **El botón ⓘ** (D-54), en `Controls/Info/InfoButton.xaml`.
     - **Sus textos salen de los recursos por la clave del apartado** (`InfoKey`): `<InfoKey>_Title` (el nombre del apartado),
       `<InfoKey>_What` («Qué es esto», dos o tres frases) y `<InfoKey>_Actions` («Qué puedes hacer aquí», una acción por
       línea).
     - **Accesible:** su nombre es «Información sobre <apartado>». Se abre con un clic, Intro o Espacio, y Esc lo cierra y
       devuelve el foco al botón. No es modal y se cierra al pulsar fuera: no tapa nada que haga falta para seguir.
     - **«Más ayuda»** solo sale cuando lleva a algún sitio (`MoreHelpCommand`): la ayuda llega en la Fase 14, y un botón que
       no lleva a nada no se enseña.
     - **Una prueba exige** sus tres textos a cada apartado que tiene ⓘ, en cada idioma.
  8. **Cómo se prueba** (D-51):
     - **Cobertura de claves:** cada clave de cada `*Strings.resx` existe en cada satélite, sin claves de más, sin textos
       vacíos y con los mismos marcadores (`{0}`, `{1}`). Una clave vive en un solo fichero
       (`Desktop/Localization/LocalizationResourcesTests.cs`).
     - **Nada escrito a mano en lo nuevo de la Fase 13** (F13-19, `Phase13HandWrittenTextTests`). Va por convención: revisa
       cada XAML y cada C# de las carpetas que crea la fase (docs/17 §9.3) y los ficheros nuevos que cada paquete añade a una
       carpeta de antes, que cada uno lista en su propio `Phase13Files.<paquete>.txt` junto a la prueba (así ninguno comparte
       fichero):
       - en XAML, ningún texto visible literal, ni en atributos (`Text`, `Content`, `Header`, `ToolTip`, `Title`, los de
         `AutomationProperties`…) ni como texto de un elemento;
       - en C#, ninguna cadena con letras fuera de ASCII (las del castellano) y ningún literal con letras asignado a una
         propiedad visible o dado como nombre accesible;
       - cada clave que se nombra literalmente existe en los recursos;
       - las excepciones van en una lista con su motivo, y una que ya no se usa también falla. Hoy hay una: los nombres de los
         idiomas en su propia lengua.

       Lo que no ve: una frase en castellano escrita solo con letras ASCII y montada fuera de esas asignaciones. Eso lo cazan
       la revisión y los renders por idioma de la Fase 14, que además extiende la prueba a todo Desktop.
     - **En la Fase 14:** la pseudolocalización (`qps-ploc`) para ver textos cortados, y fechas y números en cada idioma.
  9. **La guía de textos y su glosario** (D-52). Vale para todos los idiomas, y cada traducción tiene su glosario:
     - frases de 20 palabras como mucho, una idea por frase, en activa y de tú;
     - primero, qué pasa y qué puedes hacer; el detalle técnico va en «Detalles», plegado;
     - botones con verbo y objeto: «Aprobar el plan», no «Aceptar»;
     - errores con tres partes: qué pasó, por qué y qué hacer, sin culpar a nadie;
     - sin bloques: listas, y párrafos de tres líneas como mucho;
     - cifras con su unidad y su origen, como hoy.

     | Término técnico | En la interfaz |
     |---|---|
     | worktree | copia de trabajo aislada |
     | commit | versión guardada (la primera vez, con «commit» entre paréntesis) |
     | hash, SHA-256 | huella (en «Detalles») |
     | vendor | empresa del modelo (Anthropic, OpenAI) |
     | proveedor, CLI | programa del agente (Claude Code, Codex) |
     | token | token: el trozo de texto que cuenta el modelo (se explica en su ⓘ) |
     | ventana de contexto | memoria de trabajo del modelo |
     | cuota | límite de uso de tu plan |
     | sandbox | zona aislada |
     | registro de decisiones append-only | historial que no se puede borrar |
     | integración, entrega | unir los cambios; pasarlos a tu rama |
     | juez | revisor independiente (juez) |
     | dossier | expediente para el juez |
     | evidencia | prueba guardada |
     | payload, `RunId` y otros nombres internos | no aparecen |

     La claridad se mide en la Fase 14 (D-53): la longitud de las frases, los términos que no pueden salir en el primer nivel
     y un índice de legibilidad para el castellano (INFLESZ de 55 o más en las explicaciones).
- **Alternativas:**
  - **Un `ResourceDictionary` de cadenas con `DynamicResource`:** permitiría cambiar de idioma sin reiniciar, pero dejaría
    textos a medio cambiar en el código, que no se entera, y no da acceso tipado ni satélites. Descartada (D-49).
  - **Ficheros JSON propios:** otro formato que validar y otra carga que mantener. `.resx` es el estándar de .NET, con
    satélites y su cadena de reserva.
  - **Un solo `Strings.resx` para todos los paquetes:** chocaría en cada integración. Los ficheros por área no.
  - **Ficheros `.Designer.cs` en el repositorio:** los genera Visual Studio (`ResXFileCodeGenerator`), no `dotnet build`, y
    quedarían viejos sin que nadie lo viera.
  - **Los metadatos `StronglyTyped*` en `EmbeddedResource`:** se probaron, y la clase no llega al ensamblado temporal de WPF.
  - **Traducir lo anclado o los prompts:** cambiaría la historia o el comportamiento de los agentes. Descartada (D-48).
  - **Por defecto, castellano cuando Windows no tiene un idioma de la lista:** D-49 dice inglés, que entiende más gente. El
    castellano queda como recurso neutro: lo que falte en un idioma sale en castellano.
- **Consecuencias:**
  - **Hasta que la Fase 14 extraiga los textos de antes,** en un Windows que no esté en castellano las pantallas nuevas salen
    en inglés y las de antes, en castellano. En tu equipo todo sale en castellano.
  - **Pendiente de la integración (el lead):** aplicar el idioma al arrancar, antes de la primera ventana
    (`Texts.UseLanguage(UiLanguages.Resolve(ajustes.Language, CultureInfo.CurrentUICulture))` en `App.OnStartup`). Hasta
    entonces se usa el idioma de Windows sin elección, que es el valor por defecto de D-49. La pantalla de «Idioma» y la de
    «Reducir movimiento» llegan con su paquete; los textos ya están en los recursos.
  - **El cambio de `CLAUDE.md`** («castellano por defecto, más idiomas») es tuyo (decisión 7).
  - **Las claves no se renombran a la ligera:** las usan el XAML y el código, y las pruebas fallan si una falta.
  - **Cada idioma nuevo** añade su satélite en `en\`, `fr\`… junto al ejecutable, y su cultura a
    `SatelliteResourceLanguages` de `Directory.Build.props` (hoy `en;es`), para que se copie también a la salida de las
    pruebas.
  - **Coste de traducir:** unas 2 000 a 2 500 cadenas por idioma, del orden de 100 000 a 200 000 tokens por idioma entre
    traducir y revisar (estimación del diseño, D-50).

## Adenda · 2026-11-08 · catalán, euskera y gallego, en castellano de España

Decisión del orquestador tras la revisión independiente del P0. Matiza el punto 4 (D-49) para los idiomas de España que
Agent Studio no tiene:

- **Si Windows está en catalán, euskera o gallego** (`ca`, `eu` y `gl`, con cualquier región o variante, como
  `ca-ES-valencia`), y la persona no eligió idioma, la interfaz sale en **castellano de España**, no en inglés: quien usa
  Windows en esos idiomas lee castellano. Lo dice `UiLanguages.SpanishSpeakingRegions`, y lo aplica `UiLanguages.Resolve`.
- **Cualquier otro idioma sin recursos** sigue saliendo en inglés, como dice D-49 (por ejemplo, `pt-PT` o `fr-FR`).
- **Lo que eliges en «Ajustes» → «Idioma»** sigue mandando sobre todo lo anterior.
- **La prueba:** `LocalizationResourcesTests.The_language_is_the_one_chosen_else_the_one_of_windows_else_english`, con los
  tres idiomas, una variante y un idioma de otro país que sigue en inglés.
- **Cuando haya recursos** en catalán, euskera o gallego, entrarán en la lista como cualquier idioma y esta regla dejará de
  aplicarse a ese idioma, porque `Find` lo encontrará antes.

## Adenda · 2026-11-08 · la prueba de nada escrito a mano mira todo lo nuevo, sin listas

La revisión independiente del P0 vio que la prueba del punto 8 se esquivaba: un fichero nuevo en una carpeta de antes
(`Orchestrator/Canvas`) que nadie apuntaba en `Phase13Files.<paquete>.txt`, un texto en `StringFormat`, `FallbackValue` o
`ToolTipService.ToolTip`, y un literal castellano sin tildes. Sustituye en el punto 8 lo que dice de las listas:

- **Qué ficheros:** todo XAML y todo C# de `src/AgentStudio.Desktop` que no está en el inventario de antes de la fase,
  `tests/AgentStudio.Tests/Desktop/Localization/DesktopFilesBeforePhase13.txt`. Son los 262 ficheros de `main` en `6cf8bf7`,
  es decir, los de `96c8aca` sin los siete que creó el propio P0, que así también se revisan. El inventario no se edita: cada
  fichero nuevo de un paquete entra solo, en una carpeta nueva o en una de antes. Desaparecen las listas `Phase13Files` y las
  carpetas fijas. Un fichero de antes que un paquete cambie queda para la Fase 14, que extiende la prueba a todo Desktop.
- **En XAML,** además: `ToolTipService.ToolTip`; las partes de un binding con texto (`StringFormat`, `FallbackValue` y
  `TargetNullValue`, entre comillas o no); y los formatos (`StringFormat`, `ContentStringFormat`, `HeaderStringFormat` e
  `ItemStringFormat`). De un formato solo cuenta lo que añade: no cuentan el escape `{}`, los marcadores (`{0:N0}`) ni un
  especificador solo (`N0`, `HH:mm`).
- **En C#,** además de las letras fuera de ASCII: un literal con una palabra de una lista corta de palabras castellanas
  frecuentes de interfaz sin tilde («guardar», «tarea», «agente», «para», «sin»…). Se eligieron las que ningún identificador,
  clave, formato ni mensaje en inglés usa como palabra. Las palabras se cortan por lo que no es letra, así que una clave
  (`Info_Language_Title`) o una cultura (`de-DE`) no son castellano.
- **El límite que queda:** un texto castellano escrito solo con letras ASCII, sin ninguna palabra de la lista y fuera de las
  asignaciones que se miran («Error grave» en una lista). Lo cazan la revisión y los renders por idioma de la Fase 14.
- **La prueba de la prueba** (`The_checks_find_what_is_written_by_hand_and_nothing_else`) tiene un ejemplo de cada caso, y
  `The_check_covers_every_file_of_phase_13_and_none_of_before` comprueba que el inventario es el de antes de la fase.

## Adenda · 2026-11-11 · lo que la ayuda y la documentación para clientes toman de este ADR (P0 de la Fase 14)

El P0 de la Fase 14 ([docs/17 §9.4](../17-constructor-visual-de-agentes.md#94-fase-14--bloque-final)) completa este ADR con lo que
los contratos de la ayuda ([ADR-0056](0056-ayuda-y-agente-de-ayuda.md)) y de la documentación para clientes
([ADR-0057](0057-documentacion-para-clientes.md)) fijan sobre los idiomas. No cambia ninguna decisión de arriba; precisa cómo
se aplican a dos sitios nuevos. El resto de la Fase 14 (WP-14A y WP-14B: extraer y reescribir los textos de antes, y medir la
claridad) añade su adenda cuando se haga.

- **El nombre de un idioma en las capas de abajo** (`Application/Localization/LanguageTags.cs`). Las capas por debajo del
  escritorio no conocen la lista de idiomas (vive en `UiLanguages`), pero ahora reciben un idioma: el de un artículo de la
  ayuda, el de la respuesta del agente y el de un documento al exportarlo. `LanguageTags.IsValid` comprueba solo que tiene la
  forma del nombre de una cultura (`es-ES`, `en`, `pt-BR`, `zh-Hans`): que una ruta o una frase nunca viaje como idioma. El
  escritorio decide si ese idioma tiene recursos; si no, sale en castellano de España (el punto 4).
- **Los artículos de la ayuda** (D-55) no son claves de `.resx`: son contenido que viaja con la app, en
  `Help/<idioma>/<id>.md`, en los idiomas de la app. Su id es un código (`UserText.IsCode`) igual en todos los idiomas, y lo que
  falta en un idioma sale en el artículo de castellano de España, como cualquier texto. Los ⓘ siguen en los recursos
  (`<InfoKey>_Title`, `_What` y `_Actions`, punto 7); su «Más ayuda» abre el artículo que declara esa misma clave
  (`HelpArticle.InfoKeys`). Un artículo está sujeto a la guía de textos (punto 9) como cualquier texto de origen; qué pruebas de
  claridad lo miden (D-53) lo decide WP-14B.
- **El agente de ayuda contesta en tu idioma por un dato, no por el prompt** (D-48). `HelpAskRequest.Language` es el idioma de
  la interfaz; el servicio lo manda como dato («responde a la persona en <idioma>») y el prompt no cambia con él (F14-4: su hash
  no cambia con la cultura). Lo que escribe el agente es texto de un agente: no se traduce y se enseña enmarcado. La tarjeta de
  lo que se envía y la pregunta salen en el idioma que ves: son lo que la persona leyó, no un texto del sistema para el agente.
- **Un documento para clientes no tiene idioma hasta que se exporta** (D-59). El modelo guarda el texto del sistema como
  `UserText` (código y argumentos, punto 3) y lo que escribieron personas o agentes tal cual (punto 5); el idioma se elige al
  exportar, y puede ser otro que el de la interfaz. Como los escritores viven por debajo del escritorio, el escritorio
  implementa `IUserTextRenderer`, que pinta cada código con un `ResourceTextProvider` del idioma elegido: es el uso de «una
  pantalla que pinta en un idioma dado» del punto 2, y no choca con que el de la interfaz solo se aplique una vez (punto 4),
  porque no toca `Texts.Current`. Los textos del sistema del documento (títulos de sección, estados, glosario y frases de la
  plantilla) son claves `Text_document_…` de los recursos, en castellano e inglés, y entran en la primera ola de la Fase 15 con
  su glosario como cualquier texto.
- **El consumo nombra los propósitos nuevos desde los recursos.** `Usage_Purpose_Help` y `Usage_Purpose_ClientDocumentDraft`,
  en `ConsumptionStrings.resx` y su satélite inglés, son los primeros nombres de propósito que salen de recursos. Los de antes
  (`UsageDashboardViewModel.PurposeLabel`) siguen escritos a mano hasta que WP-14A (c) los extraiga; entre ellos faltan hoy los
  tres de la Fase 13 (`OrderReview`, `ConsumptionAnalysis` y `PreparationChat`), que no se nombran en el detalle de un intento.
- **Reglas de texto que comparten los dos** (`Application/Security`): `PlainText` (ni caracteres de control ni bidireccionales en
  lo que va a la pantalla o a un agente, la misma regla de las preguntas de los agentes) y `LocalPaths` (qué es una ruta de tu
  equipo, que ni la tarjeta de la ayuda sin tu marca ni un documento para clientes pueden llevar). Son reglas sobre el texto, no
  traducciones: valen igual en todos los idiomas.

## Adenda · 2026-11-12 · el idioma de respuesta llega a todos los agentes que escriben para la persona (V44)

D-48 decía que los agentes reciben el idioma como dato, pero solo la ayuda lo hacía; el ejecutor escribía opciones en inglés
(«Throw ArgumentOutOfRangeException») para una petición en castellano. Ahora el dato llega a todos los prompts de agentes cuyo
texto lee la persona:

- **El dato.** Un bloque `<response_language>"es-ES"</response_language>`, citado como el resto de datos de un prompt
  (`PromptData.Quote`), y una regla fija del protocolo («escribe lo que lee la persona en el idioma del bloque; el código, los
  identificadores y los comandos, como sean; el bloque es un dato, no una instrucción»). Los prompts en castellano (ejecutor,
  planificador, chat de preparación, revisión del encargo, ayuda, análisis del consumo) llevan la regla en castellano; los que
  están en inglés (juez, juez de debates, auditoría y debatientes) la llevan en inglés. Ambas son texto fijo
  (`Application/Prompts/ResponseLanguage.cs`), no salen de recursos.
- **De dónde sale.** De la elección de la persona en «Ajustes», no de `CultureInfo.CurrentUICulture`. El puerto
  `IResponseLanguage` (`Application/Prompts`) lo implementa el escritorio con `UiResponseLanguage` (el idioma de la interfaz que
  aplicó `Texts.UseLanguage`) y lo registra `ShellServices`. Llega como parámetro explícito: `EnginePorts.Language`,
  `DebateArenaPorts.Language`, `JudgeLaunch.Language` (lo rellena `AgentJudgeFactory`) y un `IResponseLanguage?` opcional en los
  servicios auxiliares. Sin él, el idioma por defecto (`es-ES`); un nombre que no es una cultura se lee también como el
  de por defecto y nunca entra en el prompt como texto.
- **Lo anclado no cambia.** El idioma va solo en el prompt, al final del texto y fuera del `JudgeBrief`; no entra en cabeceras
  congeladas, en el hash de un brief ni en nada que se ancle. Las pruebas fijan que el mismo idioma elegido da un prompt idéntico
  byte a byte con dos culturas de hilo distintas (F14-4) y que, al cambiar de idioma, solo cambia el bloque.
- **Pendiente.** El borrador de la documentación para clientes (`IClientDocumentDrafter`) aún no existe; cuando lo escriba
  WP-14C o WP-14D debe llevar el mismo bloque. Tampoco lo llevan los prompts de las skills recomendadas.

## Adenda · 2026-11-12 · la pseudolocalización y el inventario automático de la interfaz (WP-14E)

Cumple lo que el punto 5 dejaba para la Fase 14 («la pseudolocalización para ver textos cortados») y el criterio F14-10 de
docs/17 (el inventario automático de la interfaz, D-62, en verde en castellano, en inglés y en pseudolocalización).

- **`PseudoTextProvider` (`Desktop/Localization`).** Un `IUiTextProvider` que envuelve a otro: pone acentos a las letras que
  tienen gemela acentuada (`a`→`à`, `e`→`é`, `o`→`ö`…) y añade al final alrededor de un 40 % más de caracteres de relleno
  (` ~ ~ ~`), nunca dentro de un marcador (`{0}`, `{1:N1}`) ni de una llave escapada, de modo que `Format` sigue rellenándolos.
  Una clave que no existe sigue saliendo como `[clave]`, para que se encuentre. Su cultura es `qps-ploc`, como la nombra
  Windows. **Solo lo usan las pruebas**: no está en la lista de idiomas (`UiLanguages.All`), ni en «Ajustes», ni la app lo
  construye jamás.
- **Un proveedor por ámbito, sin tocar el idioma de la app.** `Texts.Scope(proveedor)` hace que `Texts.Current` devuelva ese
  proveedor solo en el flujo de ejecución que lo abrió (`AsyncLocal`, sigue a los `await` y nada que corra al lado lo ve) hasta
  que se descarte el ámbito. Es lo que permite a una prueba pintar la misma pantalla en tres idiomas, ya que el idioma de la
  app solo se aplica una vez (punto 4) y la cultura de las pruebas la fija `TestCulture`. La app no lo llama.
- **El inventario** (`tests/AgentStudio.Tests/Desktop/UiInventory`) recorre cada documento, panel, diálogo y ventana con sus
  modelos de vista de prueba, sin mostrarlos, y lee el árbol de diseño (nunca píxeles: el runner del CI no tiene ClearType).
  Comprueba en los tres idiomas que cada control tiene nombre accesible (ni vacío, ni el nombre de su tipo, ni solo un icono) y
  se alcanza con Tab, que cada ⓘ se llama «Información sobre …», que ningún texto sale como `[Clave]`, que ningún texto que no
  puede partirse queda recortado sin `TextWrapping` ni `TextTrimming` (con la pseudolocalización ven los textos un 40 % más
  largos) y que en inglés no queda texto en castellano ni al revés.
- **El idioma se lee de lo que entrega el proveedor, no de lo que hay en pantalla.** Los datos de prueba de las pantallas (una
  pregunta, el nombre de una tarea) están en castellano en cualquier idioma, y no se distinguen de un texto sin traducir; en
  cambio un texto de los recursos sin traducir cae al castellano **a través del proveedor**, así que se encuentra ahí. Lo escrito
  a mano en el código (sin pasar por el proveedor) lo guarda `Phase13HandWrittenTextTests` para todo lo posterior a la Fase 13;
  lo anterior que sigue escrito a mano (`DesktopFilesBeforePhase13.txt`: el panel de consumo, su modelo de vista y su código detrás)
  lo extrae WP-14A (c), y hasta entonces el inventario no lo ve en inglés.
- **Una guardia por vistas.** `Every_view_of_the_app_is_walked` compara las clases XAML de `Views/**`, `Controls/**` y la
  ventana principal con las que alcanza algún recorrido: una vista nueva sin recorrer, o una excluida que ya se recorre, hace
  fallar la prueba. La Fase 15 (traducción de la primera ola) se apoya en este inventario: una traducción que alargue un texto
  más de lo previsto lo rompe en la pseudolocalización antes de llegar a nadie.

## Adenda · 2026-11-12 · el esquema del juez, el recomendador de skills y la estimación de contexto (V44, revisión)

- **Los esquemas del juez ya no dicen «In Spanish.»** `VerdictSchema` y `DebateVerdictSchema` describen los campos que lee la
  persona con «In the language of the response_language block of the prompt.»: con la interfaz en inglés el juez ya no recibe
  dos órdenes contrarias. El texto del esquema no se ancla en nada (no entra en `JudgeRunStarted`, ni en el expediente, ni en
  una huella): viaja solo como `OutputJsonSchema` de cada ejecución, así que cambiarlo no toca lo antiguo.
- **El recomendador de skills** (`SkillRecommendationService`) recibe `IResponseLanguage?` como el chat de preparación: su
  `reason` ya no es «en castellano», y el prompt lleva el bloque y la regla de `ResponseLanguage`.
- **La estimación de contexto** (`ExecutorContextPreview.Measure`) recibe el idioma y lo pasa a `ExecutorPrompt.Build`: la barra
  mide el prompt real, con su bloque de idioma.
