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

## Adenda · 2026-11-12 · los textos de Debate que se ven en pantalla pasan a código (WP-14A parte e)

Lo que `src/AgentStudio.Debate` escribía para la pantalla como frases en castellano llega ahora como código y argumentos
(`UserText`), y el escritorio lo pinta en el idioma de la persona con `Render`. Los códigos viven junto a su código, no en
`UserTextCodes`; cada uno tiene su clave `Text_` en `DebateStrings` o `AuditStrings`, en los dos idiomas y con los mismos marcadores
(`DebateCodedTextsTests` recorre las listas `DebateUserTexts.All` y `PendingProblems.TextCodes`).

**Mecanismo nuevo, mínimo.** `UserTextException` (`Application/Localization`) es una `InvalidOperationException` con el mensaje en
inglés y un `UserText`. Quien ya capturaba `InvalidOperationException` sigue funcionando. `DebateTexts.Reason(exception)` pinta una
excepción con código desde recursos; cualquier otra se enseña como «Ha pasado algo inesperado (detalle técnico: …)». La pantalla
ya no muestra `ex.Message` a secas en la sala, el nuevo debate ni el historial de debates.

**Contratos con un campo más, con valor por defecto.** `DebateAwaiting.Texts` (lista de `UserText`, uno por turno fallido o uno solo)
junto a `Message`; `PendingProblem.Text`, `ProjectAuditProposal.ProblemTexts` (paralela a `Problems`, creada con `CodedMessages`) y `AuditReport.EscalationText` junto a sus frases. Quien no los rellena sigue pintando la frase, como antes.

**Códigos de Debate** (`DebateUserTexts`):

| Grupo | Códigos |
|---|---|
| Rechazos de un debate en marcha | `debate.judgment-too-early`, `debate.not-waiting-retry`, `debate.closed-evidence`, `debate.closed-tests`, `debate.no-test-commit`, `debate.no-trust-profile`, `debate.no-isolated-step`, `debate.not-waiting-decision`, `debate.already-closed`, `debate.decision-not-approved`, `debate.cannot-judge-now`, `debate.cannot-open` |
| Rechazos del coordinador | `debate.proposal-outdated`, `debate.opening-not-approved`, `debate.opening-inconsistent`, `debate.resume-not-approved`, `debate.already-running`, `debate.not-found`, `debate.already-finished`, `debate.cannot-resume`, `debate.project-busy`, `debate.no-provider` |
| Auditoría: por qué la preparación no puede juzgar (`ProjectAuditProposal.ProblemTexts`) | `audit.problem.log-broken`, `audit.problem.requirements-missing`, `audit.problem.repository-unreadable`, `audit.problem.no-trust-profile`, `audit.problem.profile-unusable`, `audit.problem.judge-missing`, `audit.problem.judge-wrong-role`, `audit.problem.judge-invalid`, `audit.problem.judge-producer` |
| Auditoría: por qué acabó sin veredicto de un juez (`AuditReport.EscalationText`) | `audit.escalation.log-broken`, `audit.escalation.cannot-launch`, `audit.escalation.not-approved`, `audit.escalation.copy-failed`, `audit.escalation.log-broke-midway`, `audit.escalation.judge-excluded`, `audit.escalation.judge-limit`, `audit.escalation.judge-failed`, `audit.escalation.judge-unreadable` |
| Auditoría: rechazos | `audit.proposal-outdated`, `audit.project-busy` (`AuditUserTexts`) |
| Por qué espera la persona | `debate.await.jury-disagrees`, `debate.await.needs-person`, `debate.await.verdicts-unusable`, `debate.await.no-verdict` |
| Turno fallido (uno por turno) | `debate.turn.no-budget`, `debate.turn.invalid-answer`, `debate.turn.limit-resets`, `debate.turn.limit-unknown`, `debate.turn.overloaded`, `debate.turn.ended-badly` |
| Problemas pendientes de la auditoría (`PendingProblems.TextCodes`) | `audit.pending.run-unfinished`, `audit.pending.run-failed`, `audit.pending.review-invalid`, `audit.pending.review-escalated`, `audit.pending.review-unresolved`, `audit.pending.debate-unfinished`, `audit.pending.debate-unresolved`, `audit.pending.challenge-open`, `audit.pending.replan-pending`, `audit.pending.memory-unreviewed`, `audit.pending.memory-pending-task` |

**Lo que queda fijo, y por qué (D-48).**

- `DebateRoundClosed.Reason`, `DebateClosed.Reason` y el motivo de aborto (la hora del límite, los tokens, las ejecuciones que
  quedan): están en el registro, con su SHA-256 encadenado. La sala ya no los pinta; usa los enumerados `DebateJudgmentReason` y
  `DebateLimitReached`, que salen de recursos. No se les añadió nada.
- `PendingProblem.Message` y `PendingProblem.Code`: entran en el expediente del juez (`ProjectDossierBuilder`), así que no cambian.
  El texto para la persona es otro código (`audit.pending.*`) y omite lo que era texto ajeno (el resumen de la ejecución fallida,
  el tipo de entrada de memoria, la regla del jurado).
- `DebateModes` (propósito y tarea de cada modo, para los agentes) y los requisitos `orq-1` a `orq-7`, con su `ContentHash`
  (`DebateAnchoredTextsTests` lo fija). Los nombres de modo que ve la persona ya salían de `DebateStrings` (`DebateTexts.Mode`).
- El texto de aprobación de la apertura y de la reanudación (`ApprovalText`, `ApprovalRequest.Description`), el informe del hito
  (`MilestoneReport`, D-49) y el de la auditoría de la orquestación, que se anclan o son determinista.

**Pendiente (sigue en castellano a mano en Debate, con pantalla).** Las listas `Problems` y `Notes` de la propuesta de debate
(`DebateCoordinator.PlanAsync`) y de impugnación (`VerdictChallengeService`), los `Problems` del resumen y de la instantánea de un
debate (`DebateRun._problems`, `DebateCoordinator` al releer) y los `Reasons` de `MilestoneCloseRefusedException`, que aún no tienen
pantalla. Cada uno necesita su propio código; la forma es la de esta adenda (un `UserText` junto a la frase, o `CodedMessages` si es
una lista). En la auditoría, el motivo de `Verdict.Summary` cuando no corre un juez se guarda tal cual en `ProjectAuditFinished`
(anclado): la pantalla lee `AuditReport.EscalationText`.

## Adenda · 2026-11-12 · lo que la memoria dice en tu idioma sin tocar lo anclado (WP-14A, parte e, `Memory`)

Los textos de `src/AgentStudio.Memory/**` que llegan a la pantalla pasan a códigos con argumentos. No cambia ninguna decisión de
arriba: aplica el punto 3 (las capas de abajo devuelven un código) y el punto 5 (lo anclado no se traduce) a la memoria, donde los
dos chocan, porque la misma frase se ancla con su SHA-256, va a los agentes y la lee la persona.

- **Una frase del sistema se compone de piezas** (`Domain/Common/TextArguments.cs`). Un argumento puede ser otro `UserText`
  (`INestedText`, que `UserText` implementa): «La persona aprobó el plan abc… de la ejecución R (2 tareas) el 01/01/2026 a las
  10:00 UTC» son una frase y cinco piezas (quién, el plan, cuántas tareas, la hora), y cada idioma las ordena como quiere. Las pinta
  `NestedTextRendering.RenderNested` (`Desktop/Localization`): primero las piezas, en el mismo idioma, y luego la frase con ellas
  como texto. `IUiTextProvider.Render` y `ResourceTextProvider` no cambian; `ResourceUserTextRenderer` (el puerto de los documentos y
  de la exportación) usa `RenderNested`. Una pieza no empieza ni acaba en espacio: una parte opcional que empieza por espacio tiene
  su propia frase para cuando está, y una que empieza por coma o punto y coma es una pieza que vale texto vacío cuando no está.
- **Lo anclado queda como estaba (M).** `DerivationTexts` sigue escribiendo, byte a byte, el tema y el contenido de cada entrada
  derivada; el hash (`MemoryContent.Sha256`), la fuente y lo que llega a los agentes son los de siempre. `DerivationUserTexts` es su
  gemelo: cada método toma los mismos argumentos y dice lo mismo como código, con las mismas cifras, identificadores y rutas (no con las mismas palabras: ver el punto siguiente). `DerivationRules` pone el código al lado de cada
  hecho (`TopicText` y `ContentText` de `SystemFact` y `DebateSummaryFact`, `TopicText` de `AgentQuote`), fuera de lo que entra en el
  hash. `DerivedTextsGoldenTests` lo fija: un registro con todas las formas de frase da, entrada por entrada, el tema, el contenido y el hash que
  da `derived-entries.golden.txt`, escrito desde el código de antes.
- **El castellano de la pantalla sigue la guía de textos, y no es palabra por palabra el anclado.** Los textos de `Text_memory_*`
  pasan las pruebas de la guía (D-52): frases de 20 palabras como mucho, «versión guardada (commit)», «prueba guardada»,
  «límite de uso» y las explicaciones con INFLESZ 55 o más. El texto anclado, que va a los agentes, dice «commit», «evidencia»,
  «Prompts» y frases de 28 palabras, y no cambia. Los temas sí salen palabra por palabra; en los contenidos cambia la
  redacción (las frases largas se parten, «Detalle del registro» es «Detalle», «Motivo» abre lo que antes iba entre paréntesis).
  Lo que la pantalla nombra es lo mismo: cada identificador, ruta, número y hora del texto anclado está en el de la pantalla, y
  una prueba lo comprueba entrada por entrada. Dos diferencias que se ven: la alerta dice «Alerta» y el código (el anclado dice «Alerta
  de auditoría»; el código ya es el de la auditoría), y el contenido de una petición de replanificar dice «un plan nuevo» mientras
  su tema, que es el anclado, dice «replanificar» (la palabra larga rompía el INFLESZ).
- **El código no se guarda, se deriva al leer.** `memory_entries` es de solo añadir: una columna nueva no cubriría las entradas que ya
  existen, pediría una migración y pondría texto sin anclar junto a una fila anclada. `IMemoryEntryTexts.ReadAsync` (lo implementa
  `MemoryDerivation`) lee los mismos hechos del registro verificado que `DerivationRules` y da el código de una parte **solo si su
  texto en castellano es exactamente el que tiene la entrada**: una entrada recortada, con secretos tapados o escrita por otra
  versión se enseña como se ancló, y la otra parte, si aún coincide, va en el idioma de la persona. Lo que escribe un agente
  (`AgentQuote`) no se traduce: solo su tema, que escribe el sistema. El resumen de un debate acaba con una de las cinco frases de
  la pregunta (`memory.question.*`); la pregunta se cita como se ancló, y el código sale de leer ese final de la propia entrada.
  Los temas de la bitácora (`Bitácora del <día> · <clave>`) salen de leer el tema. Si no hay código, o el registro no verifica, o
  la lectura falla, la pantalla enseña lo anclado: el código solo cambia el idioma. **Coste:** cada refresco de la lista y cada
  búsqueda lee el registro con `LoadAsync` (carga incremental: solo verifica lo nuevo desde la última vez) y recorre los hechos en
  memoria; no se memoriza por la cabeza del registro, porque la lista ya es una pantalla de acción de la persona, y si pesara se
  puede memoizar sin cambiar nada de lo de arriba.
- **Qué queda fijo, y por qué.**

  | Texto | Dónde va | Por qué no cambia |
  |---|---|---|
  | `DerivationTexts` (tema y contenido de cada entrada) | La memoria, el hash y los prompts de los agentes | Anclado (D-48) |
  | `MemoryLabels` (categoría, origen y revisión) | El resumen que `ProjectMemory` deja en el registro («Memoria registrada: …», «Memoria aceptada por …») | Anclado |
  | `SemanticLabels` | `HistoryFragment.Label`, que lee la herramienta del orquestador, y `SemanticHit.Label` de un fragmento de memoria, que la pantalla no pinta (usa la categoría de la entrada) | Va a un agente |
  | `AlertSummary`, `JournalAlertSummary`, `JournalTrustSummary` y los mensajes de las alertas | El registro (los mensajes, en inglés) | Anclado |
  | `Problems` de `MemoryDerivationResult` | Solo se cuentan en la pantalla; el texto va a las pruebas | No se muestra |
  | El «Detalle del registro» de una alerta, la nota del veredicto y las razones de los hallazgos | Argumentos de una frase, tal como los escribió quien los escribió | Es contenido (D-48): sale en su idioma |

- **N · categorías, orígenes y revisiones.** La pantalla ya los decía con `Memory_Kind_*`, `Memory_Origin_*` y `Memory_Trust_*`
  (`MemoryTexts`); ahora esas funciones y `MemoryEntryViewModel` aceptan el `IUiTextProvider` del idioma que se pinta, para una
  pantalla de prueba o un render por idioma. `MemoryLabels` y `SemanticLabels` quedan como están, por la tabla de arriba. La
  exportación usa `memory.export.kind.*`, `memory.export.title.*` y `memory.export.origin.*`.
- **O · decisiones.**
  - **El resumen diario es M.** Se ancla cuando su día se cierra (`DailySummaryService` lo guarda como entrada `DailySummary` del
    sistema) y va a los agentes, así que `DailySummaryBuilder` lo sigue escribiendo igual. Lo que enseña la pantalla (el de hoy, que
    no se ancla, y la entrada de un día cerrado) sale de `DailySummaryTexts.Coded`, una función pura del texto: cada línea que el
    constructor escribe con una plantilla vuelve a su código y sus cifras (con los números en castellano de la bitácora, `1.200` y
    `12,3`), y cualquier otra línea (una petición de la bitácora, una etiqueta, una línea «Hecho») pasa como está. Un texto que no es
    un resumen se enseña entero. En castellano la pantalla dice «Peticiones» donde lo anclado dice «Prompts» y abrevia la frase de
    arriba (el glosario y la guía, D-52); son las diferencias, y la prueba las nombra.
  - **La exportación a Markdown usa el idioma que eligió la persona, como parámetro.** `IMemoryExporter.ExportAsync(project,
    language, ct)` valida el idioma con `LanguageTags` y nunca lee la cultura del hilo (la exportación corre en un hilo de trabajo).
    El marco de los ficheros (títulos, introducciones, nombres de campo, categorías, orígenes y el mes) sale de `memory.export.*` por
    `IUserTextRenderer`, el mismo puerto de los documentos para clientes. Lo que cada entrada tiene (tema y contenido) sale como se
    ancló: la exportación imprime su SHA-256 al lado, y traducirlo lo haría mentir. El castellano da los mismos bytes de antes (los
    ficheros dorados de `Memory/Export/Fixtures` siguen en verde) y el mismo recuerdo con el mismo idioma da siempre los mismos
    bytes. El escritorio le pasa `Texts.Current.Culture.Name`, el idioma ya aplicado de la interfaz. Sin un `IUserTextRenderer`
    compuesto, la exportación se puede resolver y falla con un mensaje claro solo cuando tiene un fichero que escribir.
  - **La documentación para clientes no cambia.** Ya tiene su idioma (D-59). Lo que `ClientDocumentExporter` escribe en español
    (el título de la evidencia «Documento para clientes · versión N» y el resumen del registro) va al registro y a las evidencias,
    anclado.
- **Por qué se llega a la pantalla.** `Reason` de `MemoryDerivationResult` y de `MemoryExportResult` sigue en castellano, para las
  pruebas y el registro; cada uno gana `ReasonText` (un `UserText?`) para la pantalla, y `MemoryReasonTexts.Say` la pinta y, sin
  código, enseña el texto en castellano, nunca una línea vacía.
- **Los códigos de `Memory`** (250, claves `Text_<código con _>` en `MemoryStrings.resx` y `MemoryStrings.en.resx`, mismos
  marcadores en los dos). Las piezas: `who.*`, `unit.*`, `of-unit.*`, `in-unit.*`, `when`, `count.*`, `changes.*`,
  `changes-capital.*`, `phase.*`, `status.*`, `task-kind.*`, `block.*`, `decision.*`, `validity.*`, `mode.*`, `outcome.*`, `rule.*`,
  `plan.*`, `list.more`, `join`, `join-lines`, `raw`. Los temas: `topic.*` (23). Los contenidos: `plan-decision.*`,
  `delivery-decision.*`, `revision-acceptance.*`, `replan-dismissal.*`, `challenge`, `pending` y `unfinished*`, `debate-summary`
  con `debate.*` y `question.*`, `failed-tests*` con `tests.*`, `conflict*`, `alert*`, `replan*` con `replan-who.*`, `integration*`,
  `delivery*`, `sources`, `verdict*` con `verdict-target.*`, `with-question`. Los motivos: `reason.*` (7). La exportación:
  `export.*` (39). El resumen diario: `daily.*` (19). Una prueba los recorre: cada código que el código fuente nombra, y cada valor
  de las enumeraciones que las frases dicen por su nombre, tiene su texto en los dos idiomas con los mismos marcadores.
- **Pruebas.** `DerivedTextsGoldenTests` (los hashes no cambian), `DerivedUserTextsTests` (el castellano del código es el texto
  anclado, palabra por palabra, en los temas, y con todos los datos en los contenidos, de las 94 entradas del sistema de un registro con todas las formas; el
  inglés no tiene claves que falten; una parte que no es la que dan los hechos se deja como se ancló; las claves), `DailySummaryTextsTests`,
  `MemoryMarkdownExporterTests` (el marco en inglés, la cultura del hilo y un idioma que no lo es) y `MemoryEntryTextsTests` (con la
  interfaz en inglés, la pantalla enseña el texto en inglés y la entrada anclada no cambia).

## Adenda · 2026-11-12 · lo que la orquestación dice en tu idioma sin tocar lo anclado (WP-14A, parte e, `Orchestration`)

Los textos de `src/AgentStudio.Orchestration/**` que llegan a la pantalla del orquestador pasan a códigos con argumentos. Aplica el
punto 3 (las capas de abajo devuelven un código) y el punto 5 (lo anclado no se traduce) donde chocan: el motor ancla la misma frase
(el motivo de cada transición, el resumen de `RunFinished`, el aviso que lleva la aprobación) y la lee la persona.

- **Un código al lado de la frase, nunca en su lugar.** La frase en castellano se escribe y se ancla como siempre. El código viaja en
  un campo nuevo, opcional y al final de cada contrato: `TaskStatusChangedEvent.ReasonText`, `RunPhaseChangedEvent.DetailText`,
  `RunResult.SummaryText`, `BudgetGateVerdict.Text` y `PlanProposal.ErrorText`. Ninguno entra en un payload del registro ni en una
  huella. Si el campo es nulo, la pantalla enseña la frase (`OrchestratorCodedText.Painted`).
- **Una ejecución releída del registro no guarda código: se deriva al leer** (`LoggedTexts`, público). Es el mismo camino que usa la
  memoria: el código sale de la frase exacta que el motor escribe (plantillas con regex, frases fijas), y una frase que no es de esas
  (el motivo de un trabajador, las palabras de un agente, una frase de otra versión) da `null` y se enseña como se ancló. Una prueba
  recorre cada frase y comprueba que el código que el motor da en vivo es el que se deriva del registro (`CodedTextsInTheEngineTests`).
- **Quién pinta.** `OrchestratorCodedText` (`Desktop/Orchestrator`) pinta piezas y frases con `RenderNested`; `OrchestratorErrorText.Detail`
  (calcado de `DebateTexts.Reason`) pinta una `UserTextException` desde recursos y, para cualquier otro fallo, enseña su mensaje técnico,
  en inglés. `RunMonitorViewModel` (los nueve sitios de `ex.Message`) y `OrchestratorDocumentViewModel*` lo usan.
- **B está anclado.** `VerificationStepText.PlanWarning` es el aviso que viaja en la aprobación y que el motor recalcula y compara
  byte a byte (`OrchestratorDocumentViewModel.IsolationWarning`, `Orchestrator.EnsureApprovedWarning`, `RunResumer`). No cambia.
  `IsolationWarningText` es otra propiedad, pintada desde `PlanWarningTexts` (los mismos escritores, las mismas tareas, agentes y pasos), y
  es la que enseña la vista. `Notice` de `PlanWarningText` (A) va al registro como primera nota y tampoco cambia; la revisión del plan
  usa `Texts`. `Line` de un paso se queda para quien no es la pantalla y `LineParts` da sus piezas.
- **Una enumeración es una pieza.** «a, b y c» es `plan.list.and` y `plan.list.comma` (cada idioma pone su «y»); las frases en fila son
  `plan.list.space`. Una pieza no empieza ni acaba en espacio, como en la memoria.
- **El castellano de la pantalla sigue la guía de textos** (D-52, D-53) y no es palabra por palabra el anclado: no dice «worktree»,
  «integridad Medium» ni «cuota», parte las frases largas y escribe «versión guardada (commit)». Lo que nombra es lo mismo.

**Códigos de `Orchestration`** (`OrchestrationUserTexts.All`, 101; claves `Text_<código con _>` en `OrchestratorStrings.resx` y `.en.resx`,
mismos marcadores; `DebateCodedTextsTests` tiene gemela en `OrchestrationCodedTextsTests`):

| Grupo | Códigos |
|---|---|
| Listas (piezas) | `plan.list.and`, `plan.list.comma`, `plan.list.space` |
| A · entrega del plan | `plan.delivery.not-checked`, `.never-one`, `.never-many`, `.maybe-one`, `.maybe-many`, `.reason-branch`, `.reason-timing`, `.branch-yes`, `.branch-no`, `.branch-other`, `.how` |
| B · perfil y escritores con permisos amplios | `verification.step-requires`, `verification.step-without-isolation`, `verification.medium.opening`, `.task`, `.meaning` y `verification.medium.reason-*` (10: agente desconocido, sin agente, Bash, Codex, CLI sin medir, Claude sin medir, herramientas de más, permisos de más, paso desconocido, paso sin aislamiento) |
| C · cómo acaba o está una ejecución | `run.verdict.*` (24): `succeeded`, `cancelled-after-stop`, `cancelled-by-user`, `review-required`, `tests-required`, `untested-commit`, `untested-no-commit`, `stopped-repository`, `stopped-source-moved`, `stopped-evidence`, `failed`, `no-retries`, `unreachable`, `budget-spent`, `interrupted`, `interrupted-in-log`, `awaiting-human`, `blocked-launch`, `waiting-quota`, `waiting-quota-estimated`, `paused-checks`, `paused-long`, `paused`, `deadlock` |
| D · presupuesto | `project.budget-name-{project,milestone}-{day,week,month,total}` (8 piezas), `project.budget-exceeded-tokens`, `-tokens-unknown`, `-exceeded-runs`, `project.budget-partial`, `project.budget-raise`, `project.budget-unreadable`, `project.budget-why-no-usage`, `-why-untrusted`, `-why-failed` |
| E · por qué una tarea está donde está | `task.attempt-launched`, `.attempt-started`, `.evaluating-condition`, `.retry-by-user`, `.quota-wait-ended`, `.awaiting-approval`, `.node-unavailable`, `.no-provider`, `.cancelled-by-user`, `.interrupted`, `.judge-approved-resumed`, `.verdict-needs-person-resumed`, `.question-answered-before-close`, `.resumed-by-user`, `.resumed-by-consent` |
| E · lo que se cuenta de una ejecución reanudada o releída | `run.resumed-consent-quota`, `.resumed-consent-close`, `.resumed-consent-session`, `.resumed-from-log`, `.paused-by-you`, `.resumed-by-you`, `.paused-by`, `.resumed-by`, `run.notice-uncommitted-changes` |
| F · el planificador no dio un plan | `planner.schema-too-big`, `.no-provider`, `.memory-too-small`, `.repository-changed`, `.cancelled`, `.launch-failed`, `.ended` |
| G · preguntas | `question.awaiting-answer` y `question.too-many`, que ya eran de `UserTextCodes`: el motor los pone al lado del motivo de la tarea |

**Lo que queda fijo, y por qué (D-48).**

| Texto | Dónde va | Por qué no cambia |
|---|---|---|
| `VerificationStepText.PlanWarning` | La aprobación del plan (`ApprovedWarning`), `RunResumer` y `EnsureApprovedWarning` | Se compara byte a byte; es lo que la persona aprobó |
| `PlanWarningText.Notice`, el aviso de cambios sin guardar y `RunResumer.Notice` | `RunPhaseChangedEvent.Detail` y el registro | Anclado; el código va en `DetailText` |
| Motivo de cada transición, `RunFinished.Summary` | El registro, encadenado con SHA-256 | Anclado; el código va al lado y se deriva al releer |
| `BudgetGateVerdict.Reason` y la alerta `project.budget-*` | El registro | Anclado. El texto libre que da el informe de uso de un registro sin confianza (`Excluded[0].Reason`) no es argumento de una frase: la pantalla no lo enseña |
| Prompts de `Planner`, `PlannerQuestions`, `OrderReviewService`, `PreparationChatService` y `ExecutorPrompt`, esquemas de `PlanSchema` | Los agentes | Van al agente (D-48) |
| `QuestionDocuments.DecisionTitle` y los motivos de las decisiones de preguntas | El registro y las pruebas guardadas | Anclado |
| `Orchestration` no tenía excepciones en castellano | — | Los mensajes ya estaban en inglés; ninguna pasó a `UserTextException` |

**Segunda parte (2026-11-12, WP-14A (e), restos de `Orchestration`).** Lo que quedaba a mano pasa a códigos con los mismos mecanismos
(un `UserText?` opcional junto a la frase, que no cambia; `LoggedTexts` deriva el código de la frase exacta del registro). Hay 161 códigos
más (`OrchestrationUserTexts.All` suma ahora 262; claves `Text_…` en `OrchestratorStrings.resx` y `.en.resx`, mismos marcadores):

| Qué | Contrato o sitio | Códigos |
|---|---|---|
| Entrega a tu rama | `DeliveryReadiness.ReasonText` y `DeliveryResult.MessageText` (al final, opcionales). Cada frase y su código nacen juntos (`Said`), sin que se separen. Las frases de la aprobación de la entrega, el resumen del registro (`Entrega de la ejecución…`) y el mensaje del commit de fusión siguen como estaban (anclados) | `delivery.*` (77): por qué no se entrega (registro, veredicto, almacén, integración, revisión, replanificación), las revisiones de una impugnación y sus incoherencias como piezas (`delivery.inconsistent-*`, `delivery.decision-*`), y cómo acabó una entrega |
| Motivos de los trabajadores | `AttemptEnd.Text` (`init`), `FailureNote.Text` y el parámetro `text` de `MoveAsync`; `LoggedTexts` deriva cada código de su frase | `worker.*` (43): cancelaciones, tiempos, verificación, pasos, integración, juez, aprobación, sesión no iniciada, límite de uso |
| Reintento | `Reintento n de m tras: …` lleva el motivo del fallo como pieza anidada: su código si lo tiene, o el texto tal cual (palabras de un agente) | `task.retry` |
| Reanudar sola | `ScheduledResume.ReasonText` y `ConsentResume.ReasonText`; el código se deriva de la frase (`LoggedTexts.ResumeReason`). `MainViewModel` los pinta | `resume.*` (12) |
| Espera del límite de uso | `QuotaWaitPolicy.UnannouncedText` junto a `Unannounced`. Si la frase es la misma que la de un trabajador (el proveedor sí dijo la hora), el código es el mismo, así el vivo y el derivado coinciden | `quota.*` (8) |
| Relectura de un registro | `RunPhaseChangedEvent.DetailText` de `RunInspector` («no se puede reproducir entero») con cada problema como pieza | `inspect.*` (4) |
| Planificador | `PlanProposal.ErrorText` para el plan corregido que no se lee o sigue sin ser válido; `HistoryToolNotice` se pinta derivando el código de su frase (`LoggedTexts.HistoryToolNotice`), también en un borrador guardado | `planner.corrected-*`, `history.*` (9) |
| Revisar el orden y chat de preparación | `OrderReviewResult.ErrorText` y `PreparationChatReply.ErrorText` (al final, opcionales) cuando el fallo es del sistema; si lo dice el agente o su programa se enseña como está | `order.*`, `chat.*` (5) |
| «sin motivo» y «desde el monitor» | El motivo de una cancelación es una pieza (`run.no-reason`, `run.cancelled-from-monitor`) elegida por una constante compartida (`LoggedTexts.CancelReason`) en el motor vivo y en la derivación | 2 |

Desktop: los `ex.Message` de `Orchestrator/**` (Preparation, Questions, Team, Canvas, Session, Approvals) pasan por
`OrchestratorErrorText.Detail(ex)`: una `UserTextException` se pinta desde recursos y cualquier otro fallo enseña su mensaje técnico, en inglés.
`RunMonitorViewModel` pinta `DeliveryMessage` con `OrchestratorCodedText.Painted(texto, frase)`. `ReadFailureLine` no necesitaba cambio: lee
de la frase anclada (`La verificación falló en: …`, `El paso … falló.`) solo qué paso falló, para buscar su prueba guardada, y lo que enseña la tarjeta es el motivo
pintado desde su código más la línea de error del propio registro del paso, sin ninguna frase del sistema armada con ella.

**Pendiente (sigue en castellano a mano o como texto técnico, con pantalla).**

- Los motivos de un trabajador que llevan palabras de un agente, de su programa o de la guarda del repositorio, y por eso no tienen código:
  `Completada: …`, `El agente necesita a una persona: …`, `El agente declaró que no pudo terminar: …`, el motivo de un fallo que da el propio
  proveedor (`outcome.Error`), `El repositorio del usuario cambió …: <diferencias>`, `La evidencia de la integración …: <líneas>` y los de la
  memoria (`ProjectMemoryPrompt.TooSmall` y `Unverified`, que son de otro módulo). D-47: el texto de un agente no es argumento de una frase del sistema.
- Los mensajes de la lista `Discarded` de `OrderReviewResult` y de `PreparationChatReply` («a risk was cut to 400 characters», lo que el
  esquema o `OrderNotesCheck` descartan): son avisos técnicos en inglés sobre la respuesta del agente y siguen como están.
- `finding.Message` de `RunMonitorViewModel` (~1110): lo escribe `src/AgentStudio.Debate/Audit/AuditedRun.cs` (`OrchestratorFinding`), que es de
  otro paquete; la fila ya enseña el título de la comprobación por su código (`AuditFindingText.Check`) y el detalle sigue en inglés. Pasarlo a código es un cambio de `Debate/Audit`.
- `delivery.run-not-succeeded` enseña el estado con que terminó la ejecución con el nombre del tipo (`Failed`, `Cancelled`), sin traducirlo: es un valor técnico
  como un identificador. Traducirlo pide una pieza por estado.
- `PlanViolationText` se queda como está: ya pinta desde recursos por código estable (`PlanViolationCode`, claves `Orchestrator_Violation_*`) en
  el idioma en uso; pasarlo a `UserText` obliga a renombrar sus claves a `Text_*` y no añade nada visible.
- Las suites de `tests/AgentStudio.Tests/Integration/**` y `Debate/**` leen `DeliveryReadiness.Reason` y `DeliveryResult.Message`: no se tocaron ni se ejecutaron
  en este paquete (las frases no cambian).
