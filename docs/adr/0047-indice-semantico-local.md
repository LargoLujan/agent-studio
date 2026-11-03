# ADR-0047 · El índice semántico es una proyección local de lo anclado: multilingual-e5-small int8 en tu equipo y sin red, búsqueda híbrida y la bitácora anclada como memoria con su confianza; el juez no lo recibe

- **Estado:** aceptada · 2026-10-30. Recoge D-01 a D-06 de [docs/17 §3](../17-constructor-visual-de-agentes.md#3-fase-12--índice-semántico-de-la-bitácora-y-la-memoria-p19),
  tu decisión 9 del 2026-10-30 (las tres recomendadas) y el
  [spike de embeddings](../investigacion/2026-10-30-spike-embeddings.md). **Enmienda** [ADR-0023](0023-empaquetado-de-contexto-por-niveles.md):
  la relevancia de los niveles 5 y 6 pasa a ser híbrida, y la bitácora entra en el último. **Amplía**
  [ADR-0022](0022-memoria-append-only-anclada.md) con la categoría `JournalEntry`, su confianza y la decisión de confiar
  en la bitácora. **Amplía** [ADR-0024](0024-recomendador-asistido-con-mcp-local.md) (su adenda del 2026-11-03): el
  mismo servidor MCP ofrece una tercera herramienta, `search_project_history`, con el argumento `--history`, y sigue sin
  cargar el modelo. El P0 de la Fase 12 deja los contratos, el formato de la bitácora y los paquetes; WP1 a WP4 y la integración
  hacen el resto (docs/17 §9.2). El número sigue la reserva de docs/16 (0038 a 0046, Fases 6 a 11).
- **Contexto:**
  - P19 pide recuperar lo de días anteriores cuando se vacía el contexto. Hoy la memoria se busca con FTS5, que no lematiza
    («prueba» no encuentra «pruebas») y no entiende paráfrasis. La bitácora no se indexa y queda fuera de los prompts.
  - La bitácora es contenido del repositorio, el adversario del punto 1 de [ADR-0018](0018-modelo-de-amenaza-fase-3.md): un
    repositorio de terceros puede traer una con instrucciones plantadas. Leerla en crudo al lanzar a un agente no es
    aceptable.
  - Un modelo de embeddings local es una dependencia nueva de la cadena de suministro. Además ocupa disco y memoria, y gasta
    CPU en un portátil.
- **Decisión:**
  1. **El modelo:** `intfloat/multilingual-e5-small` (MIT), revisión `614241f622f53c4eeff9890bdc4f31cfecc418b3`, en la
     variante int8 de su propio repositorio, con su SentencePiece. Queda fijado en el código
     (`EmbeddingModels.MultilingualE5Small`, en `src/AgentStudio.Application/Semantic/SemanticIndexContracts.cs`):

     | Fichero | Bytes | SHA-256 |
     |---|---|---|
     | `onnx/model_qint8_avx512_vnni.onnx` | 118 346 824 | `dd476dd0c2514e9b9be83aeb3853fac0763e0bdf4a71645407587d77c48a2d88` |
     | `onnx/sentencepiece.bpe.model` | 5 069 051 | `cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865` |

     El vector tiene 384 dimensiones. Cada entrada admite 512 tokens: lo que pasa se corta. Lleva los prefijos `query: ` y
     `passage: `, y el vector es la media de `last_hidden_state` con la máscara de atención, normalizada. Se elige int8 por
     el spike: la misma calidad que fp32 con un cuarto del disco (113 frente a 449 MB), un tercio de la memoria (134 frente
     a 465 MB) y la mitad de tiempo por consulta. La huella del modelo (`Fingerprint()`) acompaña a cada vector del índice,
     así que nunca se comparan vectores de modelos distintos.
  2. **El motor, en el proceso y sin red:** ONNX Runtime 1.31.1 y `Microsoft.ML.Tokenizers` 2.0.1 (MIT los dos; el segundo
     trae Google.Protobuf, BSD-3-Clause). Solo los usa Infrastructure (`Embeddings/Onnx`).
     - Los ids siguen la regla de XLM-RoBERTa: `<s>=0`, `<pad>=1`, `</s>=2`, `<unk>=3`, y cada pieza, su id más 1.
     - El texto se recorta antes de tokenizar. Así coincide con la referencia (56 de 56 frases en el spike).
     - Las consultas usan 2 hilos. La indexación va en segundo plano, con prioridad baja.
  3. **Dónde viven, fuera del repositorio:**
     - el modelo, en `%LOCALAPPDATA%\AgentStudio\models\multilingual-e5-small\<revisión>\`;
     - el índice de cada proyecto, en `%LOCALAPPDATA%\AgentStudio\index\<clave>\semantic.db`. La clave es el SHA-256 de la
       ruta final del proyecto en mayúsculas, la misma que la del ancla de su registro (`SemanticIndexLocation`).

     git no los ve, un repositorio de terceros no los puede traer y la comprobación del repositorio del usuario no tropieza
     con ellos. Borrar el índice no pierde nada: se reconstruye con los mismos fragmentos y las mismas huellas (F12-3).
  4. **La descarga, una vez y solo con tu OK** (`IEmbeddingModelInstaller`, en la integración). Cada fichero viene de su
     URL fijada a un temporal y pasa a su sitio solo si da el tamaño y el SHA-256 fijados. Si no, se borra y no queda nada a
     medias. Copiar tú los ficheros en la carpeta también vale, si dan la huella. Un fichero que no la da nunca se carga
     (`Mismatch`). Es lo único del índice que toca la red.
  5. **Sin modelo, todo funciona por palabras,** con el FTS5 del propio índice, y la búsqueda lo dice: «Solo por palabras: …»
     (`SemanticSearchMode.WordsOnly` con su `WordsOnlyReason`). Lo mismo si el modelo no verifica, si no carga o si aún no
     ha calculado los vectores.
  6. **La búsqueda híbrida** (D-05, con un cambio que trae el spike):
     - la lista por palabras es FTS5 (`bm25`) sobre los términos de la consulta **sin palabras vacías** (`LexicalTerms`);
       cada término va entre comillas y unidos con `OR`, como hoy (docs/14 D-21);
     - la lista por significado es el coseno con el embedding de la consulta (`query:`);
     - las dos se funden por rangos recíprocos con k = 60 (`HybridRanking`); los empates se deciden por la fecha de la
       fuente (lo reciente primero) y después por el id.

     Con las palabras vacías, el `OR` casa con casi todo y la fusión quedaba por debajo de los vectores solos (77 % frente a
     90 % entre las 5 primeras). Sin ellas, la híbrida llega al 97 %.
  7. **Qué se indexa: solo lo anclado** (D-01). La memoria, la bitácora incluida, y los textos que el sistema escribe desde
     entradas del registro. Cada fragmento tiene 1 500 caracteres como mucho, está redactado y cita su fuente: la entrada de
     memoria con el SHA-256 que anunció `MemoryRecorded`, o la entrada del registro con su hash (`SemanticFragment`).
     - La búsqueda comprueba cada resultado contra el registro en ese momento: lo retirado, sustituido, modificado o sin
       anclar no sale nunca (F12-2).
     - El índice ordena; el texto que llega a un prompt es el de la fuente anclada.
     - Nunca entran credenciales, las carpetas de los CLIs, el código fuente, las transcripciones ni las evidencias en
       crudo.
  8. **La bitácora como memoria anclada** (D-02, decisión 9):
     - **Cada entrada,** con su línea «Resultado» y sus líneas «Hecho», se anuncia como memoria `JournalEntry`. Su fuente es
       `bitacora:<día>#sha256:<hash del texto de la entrada>`, así que derivar dos veces no duplica nada y cambiar el
       fichero después no cambia lo anclado. *(Corregido por la [adenda del 2026-11-03](#adenda-2026-11-03-el-casado-exige-el-recibo-de-lo-que-se-escribió):
       las líneas «Hecho» y lo demás que va debajo de una entrada del producto son una entrada aparte, nunca `System`.)*
     - **Lo que escribió Agent Studio** termina su línea «Resultado» con el `AgentRunStarted` del lanzamiento:
       `` · ejecución `<run>` · tarea `<task>` · intento <n> `` (`JournalResultAnchor`). Lo pasan en la petición el motor,
       las ejecuciones auxiliares, los turnos de debate y el juez (`AgentRunRequest.Anchor`). La nota libre que va antes no
       puede imitarlo: el escritor cambia sus acentos graves.
     - **El casado:** la derivación casa cada entrada con un `AgentRunStarted` del registro verificado con la misma
       ejecución, tarea e intento (y el mismo agente y proveedor). Cada arranque casa con una sola entrada. Lo que casa
       tiene origen `System` y es de confianza. *(Corregido por la [adenda del 2026-11-03](#adenda-2026-11-03-el-casado-exige-el-recibo-de-lo-que-se-escribió):
       ejecución, tarea e intento están en el fichero a la vista de cualquiera, así que el casado exige el recibo del texto
       exacto que escribió Agent Studio.)*
     - **Lo demás** tiene origen `Agent`, que para esta categoría significa «texto que Agent Studio no escribió»: el hook de
       desarrollo, una entrada que no casa o la de un tercero. Queda en cuarentena hasta que lo aceptas: por entrada (una
       revisión), por día o con «Confiar en la bitácora de este proyecto». Esas dos últimas son una entrada nueva del
       registro, `JournalTrustChanged`, fuera de toda ejecución y que se puede retirar (`IJournalTrust`). Lo heredado de un
       historial aceptado como nuevo origen sigue la regla de ADR-0022: se revisa una a una.
     - **No se exporta a Markdown** (`MemoryKinds.IsExported`): su fuente ya es la bitácora, Markdown y versionada.
     - **El resumen diario** lee igual las entradas de antes y las nuevas: su línea «Resultado» ya admitía un añadido.
  9. **Quién lo usa** (D-06):
     - el paquete de contexto de todo agente con `ReadSelected`, en los niveles por relevancia, con la bitácora en el
       último: nunca desplaza a los requisitos ni a las decisiones;
     - el documento «Memoria», que busca y muestra la bitácora con su confianza;
     - el planificador, con la herramienta `search_project_history(query, top ≤ 10)` del servidor MCP local, solo en
       lanzamientos de solo lectura. Lee una instantánea de lo de confianza con sus vectores (`ProjectHistorySnapshot`) y no
       carga el modelo: ordena por palabras y puede ampliar con los vectores de sus mejores aciertos;
     - **el juez y los auditores, nunca:** ni paquete ni herramienta (F12-8).
- **Alternativas:**
  - **La variante fp32:** la misma calidad, con 449 MB y más del triple de memoria.
  - **paraphrase-multilingual-MiniLM-L12-v2:** solo 128 tokens por entrada, así que partiría los fragmentos.
  - **bge-m3 o Qwen3-Embedding-0.6B:** de 1,2 a 2 GB, demasiado para un IDE.
  - **EmbeddingGemma:** su licencia no es abierta, como pasaba con FluentAssertions.
  - **Un servidor local que instales tú (Ollama, LM Studio):** hoy no hay ninguno, y sería otro proceso que mantener.
  - **sqlite-vec:** está antes de su versión 1 y obliga a cargar extensiones nativas. Recorrer 10 000 vectores en memoria
    cuesta unos 2 ms.
  - **FTS5 con un lematizador de castellano:** no da significado («test en rojo» no encuentra «pruebas que fallan»).
  - **La bitácora en crudo en el prompt:** contenido del repositorio sin anclar. Descartada.
  - **El modelo también en el servidor MCP:** 0,4 s de carga y unos 134 MB en cada sesión del planificador, más nativo
    cargado por un proceso que lanza el CLI, y ampliaría todavía más ADR-0024, cuya adenda solo añade la herramienta. El
    paquete del planificador ya trae la relevancia híbrida.
  - **Las palabras vacías en la lista por palabras, como decía D-05:** el spike midió que empeoran la fusión.
- **Consecuencias:**
  - **Coste del modelo:** 123 MB de descarga una vez y unos 134 MB de memoria con el modelo cargado. Cargar el tokenizador
    lleva unos 2 s; se hace en segundo plano.
  - **Indexar:** de 0,1 a 0,2 s de CPU por fragmento de unos 300 tokens. El primer índice de un historial grande tarda
    minutos, en segundo plano. Después solo se indexa lo nuevo.
  - **Una consulta con 10 000 fragmentos:** 52 ms en el percentil 95 en esta CPU. Lo que más cuesta es FTS5.
  - **El paquete de ONNX Runtime trae el nativo de cinco plataformas** (unos 128 MB) si no se fija `RuntimeIdentifier`.
    Lo decide el instalador (Fase 7).
  - **Las entradas de la bitácora de antes de la Fase 12 y las del hook quedan en cuarentena** hasta que las aceptes o
    confíes en la bitácora del proyecto.
  - **El formato de la línea «Resultado» crece al final.** Lo leen el resumen diario (sin cambios, con su prueba) y la
    derivación.
  - **Residual, como el de la memoria de hoy:** una entrada de confianza que engañe. Lo acotan la aprobación del plan, el
    validador, la verificación del sistema y un juez de otro vendor que no ve el índice.
  - **Residual:** un proceso del mismo usuario a integridad Medium puede reescribir el índice o el modelo en
    `%LOCALAPPDATA%` (punto 2 de ADR-0018). El modelo se vuelve a verificar al cargarlo, y el índice no decide la
    confianza de nada.
  - **Sin medir:** la int8 en una CPU sin AVX2 o en ARM64.

## Adenda (2026-11-03): el casado exige el recibo de lo que se escribió

La revisión independiente del P0 encontró cuatro huecos en la confianza de la bitácora. WP1 los cierra así:

1. **Un historial heredado no trae confianza.** `JournalTrustChanged` no pertenece a ninguna ejecución, así que una
   `agentstudio.db` plantada con «Confiar en la bitácora» habría vuelto de confianza toda la bitácora al aceptar su historial
   (A-01). Ahora una decisión sobre la bitácora solo cuenta si se registró después del último `origin-accepted`, y el casado
   solo vale con ejecuciones propias del registro (`DerivationLog.OwnRuns`), como el resto de lo derivado.
2. **El casado autentica el texto.** La ejecución, la tarea y el intento de una línea «Resultado» se pueden copiar, porque el
   fichero está versionado. Por eso:
   - **Recibo.** Cuando Agent Studio escribe la línea «Resultado» de un lanzamiento anclado, el escritor da el SHA-256 del texto
     exacto de la entrada: su línea y su línea «Resultado», unidas por un salto de línea (`JournalReceipt`). Quien registra el
     final del lanzamiento (el motor, las ejecuciones auxiliares, los turnos de debate y el juez) lo guarda en
     `AgentRunFinished` (`AgentRunFinishedPayload.Journal`, que solo se escribe si existe: ningún payload guardado cambia).
   - **El casado exige el recibo:** una entrada es `System` solo si es una entrada del producto con una sola línea
     «Resultado», que nombra esa misma entrada, y el `AgentRunFinished` de esa ejecución, tarea e intento, en una ejecución
     propia, tiene el recibo con su día, su número y el SHA-256 de su texto. Copiar el final en otra entrada, o cambiar la
     entrada antes o después de derivarla, da otro hash.
   - **Si dos entradas reclaman el mismo lanzamiento, ninguna casa:** ni dos entradas nuevas, ni una nueva frente a otra que
     ya está en la memoria.
   - **`System` solo para la línea de la entrada y su línea «Resultado».** Las líneas «Hecho» y lo demás que va debajo de una
     entrada del producto son una entrada aparte (`… · notas`), de origen `Agent`, con la confianza de la bitácora (por día o
     del proyecto), nunca `System`.
   - **Los arranques de antes de la Fase 12 no se reclaman nunca:** su `AgentRunFinished` no tiene recibo.
   - **La bitácora se lee antes que el registro.** Un lanzamiento que termina entre las dos lecturas (una ejecución auxiliar
     no cuenta como actividad del proyecto) escribió su línea «Resultado» antes que su recibo, así que el registro leído
     después lo tiene. Y si aun así una entrada se ancló como `Agent` porque su recibo no había llegado, en una derivación
     posterior pasa a `System` y sustituye a la primera: lo que la memoria ya tiene se reconoce por su origen y su fuente, no
     solo por su fuente.
3. **Medianoche.** El resultado va al fichero del día en que termina el agente, y su `P-NNNN` no decía de qué día era. Ahora
   las dos líneas nombran la entrada con su día: la línea de la entrada termina con `(… · entrada 2026-11-03/P-0001)` y la
   línea «Resultado» con `` · entrada `2026-11-03/P-0001` ``, detrás del intento (`JournalResultAnchor.Format(anchor, entry)`).
   La derivación junta el resultado escrito en el fichero del día siguiente con su entrada, y el resumen diario lo cuenta
   como un resultado del día en que se escribe sin atribuírselo a la entrada de ese día que tenga el mismo número.
4. **La confianza de una entrada de la bitácora la calcula el registro, con una precedencia explícita**
   (`MemoryLedger.JournalTrustOf`). Entre niveles manda el más concreto: la entrada, después su día y después el proyecto.
   Dentro de un nivel manda la decisión más reciente, como en el resto de la memoria: una «Aceptar» posterior a una retirada
   de la misma entrada la devuelve. En orden:
   - la última revisión de la propia entrada (aceptada, rechazada o retirada; una aceptación que llegó con un historial
     heredado no cuenta);
   - lo heredado queda en cuarentena y se revisa una a una;
   - lo que escribió Agent Studio, con su recibo, es de confianza;
   - lo demás sigue la última decisión sobre su día y, si no la hay, la última sobre el proyecto.

   Sin ninguna, una entrada de la bitácora está en **cuarentena** (`Quarantined`), nunca es una propuesta (`Proposed`). La
   fuente `bitacora:<día>#sha256:<hash>` se lee de vuelta con `MemorySources.TryParseJournal`.

Qué queda igual: la bitácora entra como memoria anclada con su confianza, cada entrada por el hash de su texto, sin
exportarse a Markdown, y lo demás de esta ADR.

**Residuales, los dos del lado de la cuarentena (nunca dan confianza de más):**

- **Un «Resultado» copiado rebaja la entrada legítima.** Si alguien copia la línea «Resultado» de una entrada en otro sitio
  de la bitácora (otro día, o bajo otra entrada) antes de que esa entrada se ancle, dos entradas reclaman el mismo
  lanzamiento, o la legítima pasa a tener dos resultados, y ninguna es `System`: la legítima queda en cuarentena hasta que la
  aceptes. Ya anclada, una copia posterior no le cambia nada.
- **Un lanzamiento sin final no deja recibo.** Una entrada cuyo lanzamiento no pudo empezar (la petición falló antes de
  arrancar), o cuyo final se cortó antes de escribirse (la app murió entre la línea «Resultado» y `AgentRunFinished`), no
  tiene recibo, porque no hay final que lo lleve: queda en cuarentena.

## Adenda · 2026-11-03 · los nativos de ONNX Runtime: siete plataformas, y solo se copian las dos de Windows

La consecuencia «El paquete de ONNX Runtime trae el nativo de cinco plataformas (unos 128 MB)» se quedaba corta, y la
decisión no espera al instalador (Fase 7).

- **Lo medido** (`Microsoft.ML.OnnxRuntime` 1.31.1): trae el nativo de siete plataformas, no de cinco. Son android
  (53 MB), ios (57 MB), linux-arm64 (25 MB), linux-x64 (29 MB), osx-arm64 (43 MB), win-arm64 (17 MB) y win-x64 (17 MB):
  237 MB en la salida. Una compilación o una publicación sin `RuntimeIdentifier` los copia todos en `runtimes/`.
- **La decisión:** Agent Studio es una app de Windows (WPF), así que solo se copian los dos nativos que puede usar,
  win-x64 y win-arm64 (34 MB).
  - Lo hace `Directory.Build.targets`, en la raíz, para cualquier proyecto que reciba el paquete. Justo después de
    `ResolvePackageAssets` quita de `RuntimeTargetsCopyLocalItems` los nativos de ONNX Runtime de los demás RID. Así no
    se copian ni se publican, y `deps.json` tampoco los nombra.
  - Una publicación por RID (`-r win-x64`) ya llevaba solo el suyo, en la raíz de la salida, y no cambia.
- **Antes y después** (MB, medido el 2026-11-03):

  | Salida | Antes | Después |
  |---|---|---|
  | Desktop (Debug) | 337 | 133 |
  | Pruebas (Debug) | 358 | 154 |
  | Publicación de Desktop sin RID (Release) | 336 | 132 |
  | Publicación de Desktop con `-r win-x64` (Release, dependiente del framework) | 85 | 85 |

- **Comprobado en x64:** con la salida recortada, las pruebas del modelo real (`OnnxEmbedderLocalModelTests`, LocalTools)
  dan los ids de `sentencepiece` en 56 de 56 frases y los vectores de onnxruntime en Python (coseno de 0,9999 o más).
  El de win-arm64 se copia, pero sigue sin probar, como ya decía «Sin medir».
- **Lo que queda en `runtimes/`** (67 MB): los dos nativos de ONNX Runtime (34 MB), `e_sqlite3` de SQLite para 21 RID
  (31 MB) y `WebView2Loader` para tres (0,5 MB). Recortar los de otros paquetes es otra decisión, fuera de esta adenda.
