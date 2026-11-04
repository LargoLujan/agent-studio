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

## Adenda (2026-11-03, revisión de WP3): corte de lo que solo encuentra el significado, nivel propio de la bitácora y F12-4 con el modelo real

- **Contexto:** la revisión independiente de WP3 encontró que la lista por significado no tenía corte de relevancia. El
  coseno siempre devuelve vecinos, así que cada nivel por relevancia traía hasta 50 entradas pasara lo que pasara y el
  paquete se llenaba hasta el presupuesto con texto sin relación. Su sonda, con el foco «Implementar Multiply», daba 1
  elemento y 48 caracteres sin modelo y 33 elementos y 14 728 caracteres con modelo. Contradice el «solo lo relevante» de
  [ADR-0023](0023-empaquetado-de-contexto-por-niveles.md).
- **Decisión:**
  1. **Contrato.** `SimilarCandidate(Id, At, Similarity)`, junto a `RankedCandidate` en `HybridRanking.cs`: la similitud
     es el producto escalar de los dos vectores normalizados, su coseno. `ISemanticIndexStore.SearchVectorsAsync` la
     devuelve con los mismos parámetros y el mismo orden. `HybridRanking.Fuse` sigue recibiendo `RankedCandidate`: el
     índice convierte después del corte.
  2. **El corte.** Un fragmento que solo trae la lista por significado (la lista por palabras no lo tiene) entra en la
     fusión si cumple las dos condiciones:
     - su similitud llega al suelo del modelo (`EmbeddingModelDescriptor.MeaningOnlyMinimum`);
     - queda a menos del margen de la mejor similitud de la búsqueda, entre lo que su confianza admite, de cualquier
       categoría (`MeaningOnlyMargin`).

     Lo que también trae la lista por palabras entra siempre. Los dos valores son de cada modelo y no forman parte de su
     huella, porque no cambian los vectores. Para multilingual-e5-small, **suelo 0,79 y margen 0,04**.
  3. **La calibración.** Con el modelo real: la int8 de la revisión fijada, con su SHA-256 comprobado. El conjunto son
     las 50 preguntas de F12-4 más 36 distractores sin relación con ninguna pregunta, doce en cada nivel por relevancia.
     El ruido es lo que traen los tres niveles por relevancia de un paquete en las 50 preguntas (150 niveles):

     | Corte | F12-4 | Distractores | Entradas que no son la respuesta | Respuesta en su nivel |
     |---|---|---|---|---|
     | Sin corte | 36/50 | 1 800 (12,0 por nivel) | 4 363 (29,1 por nivel; 20 984 caracteres por paquete) | 37/37 |
     | Suelo 0,79 | 36/50 | 897 (6,0) | 2 711 (18,1; 15 029) | 37/37 |
     | **Suelo 0,79 y margen 0,04** | **36/50** | **428 (2,9)** | **1 577 (10,5; 9 628)** | **37/37** |
     | Suelo 0,79 y margen 0,03 | 36/50 | 261 (1,7) | 1 081 (7,2; 6 830) | 35/37 |
     | Suelo 0,80 | 35/50 | 529 (3,5) | 1 921 (12,8; 11 374) | 37/37 |
     | Suelo 0,82 | 32/50 | 140 (0,9) | 762 (5,1; 5 065) | 32/37 |

     **Por qué estos valores.** La regla fue conservar el acierto con el menor ruido:
     - 0,79 es el suelo más alto que conserva todos los aciertos de la búsqueda sin corte; 0,80 ya pierde uno.
     - Las similitudes de e5 están muy juntas: las respuestas van de 0,777 (mediana 0,835) y el mejor distractor de cada
       pregunta llega a 0,840 (mediana 0,816). Por eso el suelo solo corta la mitad del ruido.
     - El margen sí separa, porque los distractores quedan unas centésimas por debajo de la mejor similitud. 0,04 es el
       más holgado de los medidos (0,01 a 0,04) que conserva los aciertos y la respuesta en su nivel. Con el suelo, corta la
       mitad de sus distractores y deja una cuarta parte de los de sin corte.
     - 0,03 corta más, pero saca dos respuestas de su nivel. Se elige el más holgado para no ajustarse de más a 50
       preguntas.
     - La prueba de calibración (`MeaningOnlyFloorCalibrationTests`, categoría `LocalModel`) lo vuelve a medir con el modelo
       en disco (variable `AGENTSTUDIO_E5_MODEL_DIR`). Sin el modelo, se omite.
  4. **Un paquete, una lectura.** Los tres niveles por relevancia de un paquete se piden juntos
     (`ISemanticIndex.SearchManyAsync`): lo anclado y el estado del índice se leen una vez y el foco se embebe una vez. El
     corte de cada entrada se guarda por su id, su SHA-256 y su tiempo, así que no se vuelve a cortar ni a hashear.
  5. **El paquete dice cómo ordenó** (`ContextPack.Relevance`): por la búsqueda híbrida, o solo por palabras y por qué.
     `WordsOnlyReason.IndexUnavailable` dice que no hay índice o que falló y que ordenó el FTS5 de la memoria.
  6. **Filas que no son un fragmento** (texto vacío o de más de 1 500 caracteres, fuente u huella que no lo son):
     - el almacén las salta en lecturas y búsquedas y las da en su estado con `Sha256` vacío;
     - el refresco y la búsqueda las reescriben si siguen ancladas, y el refresco las quita si no;
     - ninguna búsqueda falla por una fila;
     - un fragmento que el índice ya no tiene (lo quitó un refresco a mitad de una búsqueda) no se vuelve a meter.
  7. **La bitácora tiene nivel propio** (`ContextTier.History`), detrás de «el resto». D-02 la ponía dentro de «el resto»;
     con un nivel propio es lo primero que se recorta cuando falta sitio y nunca desplaza a nada de arriba. Cumple la
     decisión 9: nunca desplaza a los requisitos ni a las decisiones.
  8. **Lo que puede llegar a un prompt** (`SemanticQuery.MayReachAPrompt`): de confianza, sin sustituir y nunca un resumen
     diario, sea cual sea su origen. Lo cumplen la búsqueda `TrustedOnly`, la instantánea del planificador y el
     empaquetador.
  9. **La instantánea lleva vectores** gracias a `ISemanticIndexStore.ReadIndexedAsync`.
- **Hallazgo: F12-4 no se cumple con el modelo real en este conjunto, y no es por el corte.** Sin corte:

  | Búsqueda | Respuesta entre las 5 primeras |
  |---|---|
  | Híbrida | 36/50 (72 %) |
  | Solo por significado | 41/50 |
  | FTS5 como hoy | 26/50 |

  El criterio pide 43/50. La fusión RRF con pesos iguales pierde frente a los vectores solos porque la lista por palabras
  trae ruido:
  - **La plantilla de la bitácora:** `#prompt` y «ejecución» de la línea «Resultado» casan con todas las entradas de
    bitácora. Así se pierden q10 y q45.
  - **Palabras de contenido ambiguas:** q05, q24, q41 y de q47 a q49.

  El corte no lo arregla, porque solo quita candidatos que vienen por significado. Queda para decidir, y cada opción hay
  que medirla con este conjunto y con la bitácora real:
  - una fusión ponderada que dé menos peso a la lista por palabras;
  - una lista por palabras menos profunda;
  - indexar la bitácora sin su plantilla. Depende de qué texto lleve la entrada `JournalEntry` que deriva WP1: si lleva la
    línea «Resultado» con esos campos, el ruido es real; si no, el conjunto lo exagera.
- **Consecuencias:**
  - Con el modelo, un nivel por relevancia trae lo que casa por palabras y lo que el significado acerca de verdad: en el
    conjunto, 10,5 entradas que no son la respuesta por nivel, frente a 29,1.
  - La prueba Live de F12-4 mantiene el criterio de 43/50 y queda en rojo con el modelo (36/50). Sin modelo se omite.
    Lo resuelve la segunda adenda.
  - El suelo y el margen hay que recalibrarlos si cambia el modelo o la forma de los fragmentos.

## Adenda (2026-11-03, segunda): F12-4 con el modelo real, resuelto con la bitácora sin plantilla y una fusión ponderada (la sustituye la adenda de 2026-11-04)

- **Contexto:** la primera adenda dejó F12-4 en 36/50 con el modelo real por el ruido de la lista por palabras. La decisión
  fue atacar esa causa sin bajar el listón ni cambiar las preguntas: medir tres opciones en orden y quedarse con la
  combinación más sencilla que llegara a 43/50 sin empeorar el ruido (los distractores y la respuesta en su nivel).
- **Decisión:**
  1. **La bitácora se busca sin su plantilla** (`JournalSearchText`). Para un fragmento `JournalEntry`, lo que se indexa
     (FTS5 y embedding) es una vista limpia y determinista del texto anclado:
     - se queda con el resumen de la petición («…»), la nota libre de su línea «Resultado» y el texto de las líneas
       «Hecho»;
     - quita la hora, el `P-NNNN`, quién, el CLI y el modelo, las etiquetas, el estado, la duración, los tokens, el ancla
       del lanzamiento (`ejecución`, `tarea`, `intento`, `entrada`) y el tema de la entrada, que es el mismo en todas.

     Lo que llega al paquete de contexto es siempre el texto anclado completo. La huella de cada fragmento cubre la
     vista y se recalcula desde la entrada anclada, así que un texto alterado sigue sin pasar.
  2. **La lista por palabras pesa 0,15 en la fusión** (`HybridRanking.Fuse(…, lexicalWeight)`, con 1 por defecto, y
     `SemanticSearchTuning.DefaultLexicalWeight` en el índice):
     - lo que encuentra el significado va primero;
     - las palabras lo reordenan y traen detrás lo que solo encuentran ellas;
     - sin modelo no cambia nada, porque hay una sola lista.
  3. **El corte se queda:** suelo 0,79 y margen 0,04.
  4. **Opciones descartadas:**
     - **Ignorar los términos frecuentes (la opción 2):** ningún término de las preguntas está en más del 8 % de los
       fragmentos, así que con umbrales del 40, el 20 o el 10 % no cambia nada. Con el 5 % no sube y saca una respuesta de
       su nivel.
     - **Una lista por palabras menos profunda (20, 10 o 5):** no ayuda.
- **Medidas** (modelo real, las 50 preguntas y sus 36 distractores; el corte, salvo donde se indica):

  | Combinación | F12-4 | Distractores | Respuesta en su nivel |
  |---|---|---|---|
  | Primera adenda: bitácora con plantilla, peso 1 | 36/50 | 428 | 37/37 |
  | 1: bitácora sin plantilla, peso 1 | 40/50 | 302 | 37/37 |
  | 1, sin corte | 41/50 | 1 800 | 37/37 |
  | 1 + términos en más del 40, 20 o 10 % fuera | 40/50 | 302 | 37/37 |
  | 1 + términos en más del 5 % fuera | 40/50 | 301 | 36/37 |
  | 1 + profundidad 20, 10 o 5 | 40, 40 o 39 | 302 | 37/37 |
  | 1 + peso 0,5 o 0,25 | 39 o 41 | 302 | 37/37 |
  | 1 + peso 0,2 | 43/50 | 302 | 37/37 |
  | **1 + peso 0,15 (la elegida)** | **45/50** | **302** | **37/37** |
  | 1 + peso 0,125 o 0,1 | 45/50 | 302 | 37/37 |
  | 1 + peso 0,05 | 43/50 | 302 | 37/37 |
  | 1 + peso 0,15, sin corte | 44/50 | 1 800 | 37/37 |

  Solo por significado da 43/50 y FTS5 como hoy, 26/50. F12-4 pide 43/50 y 15 puntos más que FTS5. La combinación
  elegida da 45/50 (90 %), 38 puntos más.
- **Por qué 0,15:**
  - es el peso más alto de la meseta de 45 aciertos (de 0,1 a 0,15): la lista por palabras conserva todo el peso que
    puede sin tapar al significado;
  - los pesos de al lado (0,2 y 0,125) también cumplen;
  - con este peso, el corte no pierde ninguna respuesta (45 frente a 44 sin él) y deja la sexta parte de los distractores.
- **Las preguntas que siguen fuera:** q16, q17, q42, q45 y q49. Ninguna es ambigua, aunque dos dependen de algo que el
  conjunto no dice: q16, que el límite de OpenAI es la cuota de Codex; q17, que la huella y la cara son Windows Hello.
- **Consecuencias:**
  - D-05 cambia: la fusión ya no da el mismo peso a las dos listas.
  - Para la bitácora, la instantánea de la herramienta del planificador lleva la vista limpia (lo que se indexa) con su
    fecha. Es sobre lo que la herramienta busca por palabras.
  - El peso, el suelo y el margen se recalibran juntos si cambia el modelo, la forma de los fragmentos o la plantilla de
    la bitácora. Se miden con `MeaningOnlyFloorCalibrationTests` y la prueba Live de F12-4, que con el modelo en disco
    (`AGENTSTUDIO_E5_MODEL_DIR`) pasan las dos; `AGENTSTUDIO_CALIBRATION_REPORT` deja sus cifras en un fichero.

## Adenda (2026-11-04): la bitácora con dos textos, el corte para toda la lista por significado y la fusión de D-05

- **Contexto:** la segunda adenda no generaliza. En una validación ciega, con preguntas que no se usaron para calibrar,
  la búsqueda híbrida quedó por debajo de FTS5, sobre todo en las preguntas por los datos de la plantilla de la bitácora
  (fecha, `P-NNNN`, quién, ejecución): la vista limpia los había quitado también de las palabras. Y el peso 0,15 era un
  ajuste a estas 50 preguntas, casi todas paráfrasis. Lo decidido:
  - dos textos por entrada de la bitácora;
  - ignorar los términos de la pregunta que están en muchos fragmentos, con el umbral medido solo con las 50 preguntas de
    calibración;
  - volver a la fusión de D-05 (peso 1, k = 60) salvo una ganancia clara y estable en una meseta ancha;
  - el corte 0,79 y 0,04 se queda si no pierde respuestas;
  - el corte, en una tabla por la huella del modelo, no en su descriptor.
- **Decisión:**
  1. **La bitácora tiene dos textos.**
     - El fragmento lleva el texto anclado completo («tema: contenido»). Lo buscan las palabras (fechas, `P-NNNN`, quién,
       ejecución, etiquetas, estado) y lo da la instantánea del planificador.
     - El significado se calcula con la vista limpia de su trozo (`FragmentCutter.MeaningOf`, `JournalSearchText`), sin la
       plantilla.
     - La vista es una función pura del fragmento y de la categoría de su entrada, así que la huella del fragmento (su
       texto y el SHA-256 de la fuente) cubre los dos textos.
     - La huella de los vectores lleva la versión de la vista (`SemanticIndex.VectorFingerprint`,
       `JournalSearchText.Version` = 2): si la vista cambia, los vectores se recalculan.
     - La vista solo quita la plantilla tal como la escriben la app y el hook: `- **HH:mm:ss** · P-NNNN · …`,
       `- **HH:mm:ss** · sesión `…` · …`, `- Resultado P-NNNN · HH:mm:ss · estado · …` y `Hecho…:`. Una línea que solo
       empieza parecido («Resultado final…», «**Nota** · …») se queda entera.
  2. **El corte vale para toda la lista por significado.** Solo lo que llega al corte tiene puesto por significado, lo
     encuentren también las palabras o no. Lo que solo encuentran las palabras sigue entrando, por su puesto en la lista
     por palabras.
     - Antes, lo que casaba por palabras tenía puesto por significado aunque quedara por debajo del corte. Con k = 60 eso
       le daba dos aportaciones: una palabra en común le ganaba a la respuesta que el significado ponía primera. Y el
       corte, al acortar la lista por significado, subía de puesto a ese ruido: perdía respuestas (37/50 frente a 39/50
       sin corte).
     - Lo que sale es lo mismo, así que el ruido no cambia. Cambia el orden.
  3. **La fusión es la de D-05:** las dos listas con el mismo peso y k = 60. `HybridRanking.Fuse` vuelve a no tener peso.
  4. **Los términos comunes quedan fuera.** Un término de la pregunta que está en más de la mitad de los fragmentos del
     proyecto, contados por FTS5, no va a la lista por palabras (`SemanticSearchTuning.CommonTermFraction` = 0,5). Si la
     pregunta no tiene otro, se busca con ellos.
  5. **El corte se queda:** suelo 0,79 y margen 0,04 para multilingual-e5-small.
     - Ahora está en `SemanticSearchTuning`, por la huella del modelo (`MeaningOnlyCut`). Un descriptor reconstruido con
       los mismos campos tiene la misma huella y el mismo corte.
     - Un modelo que la tabla no conoce va sin corte, y la búsqueda y el paquete lo dicen: `SemanticSearchResult.Cut` y
       `ContextRelevance.Cut` quedan nulos.
- **Medidas** (modelo real, las 50 preguntas de calibración y sus 36 distractores; ninguna cifra de la validación):

  | Búsqueda | F12-4 | Bitácora | Memoria | Distractores | Respuesta en su nivel |
  |---|---|---|---|---|---|
  | **Diseño elegido** (dos textos, corte de toda la lista, peso 1, techo del 50 %) | **41/50** | **20/25** | **21/25** | **370 (2,5 por nivel)** | **37/37** |
  | El mismo, sin corte | 39/50 | 20/25 | 19/25 | 1 800 (12,0) | 37/37 |
  | Con el corte de antes (lo de las palabras con puesto por significado) | 37/50 | 18/25 | 19/25 | 370 (2,5) | 37/37 |
  | Solo por significado | 43/50 | | | | |
  | El índice solo por palabras (techo del 50 %) | 26/50 | 9/25 | 17/25 | | |
  | FTS5 como hoy | 26/50 | 9/25 | 17/25 | | |

  El corte, con el diseño elegido:

  | Corte | F12-4 | Distractores | Respuesta en su nivel |
  |---|---|---|---|
  | Sin corte | 39/50 | 1 800 | 37/37 |
  | Suelo 0,78 / 0,79 / 0,80 / 0,82 | 40 / 40 / 41 / 40 | 1 161 / 759 / 414 / 114 | 37 / 37 / 37 / 33 de 37 |
  | Margen 0,03 / 0,04 / 0,05 | 42 / 40 / 39 | 240 / 444 / 728 | 36 / 37 / 37 de 37 |
  | Suelo 0,78 y margen 0,04 | 41/50 | 420 | 37/37 |
  | **Suelo 0,79 y margen 0,04** | **41/50** | **370** | **37/37** |
  | Suelo 0,79 y margen 0,03 / 0,05 | 42 / 40 | 229 / 493 | 36 / 37 de 37 |
  | Suelo 0,80 y margen 0,04 | 42/50 | 261 | 37/37 |

  El techo de términos comunes, con el diseño elegido:

  | Techo | Híbrida | Distractores | El índice solo por palabras |
  |---|---|---|---|
  | Ninguno, 60, 50 o 45 % | 41/50 | 370 | 26/50 |
  | 40 % | 42/50 | 359 | 26/50 |
  | 35 o 30 % | 41/50 | 314 | 25/50 |
  | Del 25 al 10 % | 41/50 | 302 | 25/50 |
  | 5 % | 41/50 | 301 (36/37 en su nivel) | 25/50 |

  El peso de la lista por palabras, medido antes de quitar el parámetro (techo del 25 %):

  | Peso | 1 | 0,75 | 0,5 | 0,35 | 0,25 | 0,15 |
  |---|---|---|---|---|---|---|
  | Corte de toda la lista | 41 | 40 | 40 | 40 | 40 | 43 |
  | Corte de antes | 37 | 37 | 37 | 38 | 40 | 44 |

- **Por qué:**
  - **El peso, 1.** No hay meseta. Con el corte de toda la lista, el 1 da 41, de 0,75 a 0,25 da 40 y 0,15 da 43. Con el
    corte de antes, el acierto sube solo al bajar el peso. Es lo que cabe esperar de un conjunto de paráfrasis, no una
    ganancia que generalice.
  - **El techo, el 50 %, elegido por riesgo y no por el conjunto.** Ningún valor mueve F12-4 más de una pregunta: q45
    entra con el 40 %, porque «ejecución» está en el 42 % de los fragmentos. Por debajo del 40 %, las palabras solas
    pierden una respuesta. Las 50 preguntas no preguntan por la plantilla, así que no miden el coste real de un techo
    bajo: en un proyecto con mucha bitácora, los roles, los estados y los nombres están en muchos fragmentos, y son justo
    lo que busca una pregunta por los datos. Se elige el valor más alto que aún quita la plantilla de verdad.
  - **El corte, 0,79 y 0,04.** Con el corte de toda la lista ya no pierde respuestas: 41 frente a 39 sin él, con la
    quinta parte de los distractores. Sus vecinos dan de 40 a 42: está en una meseta, no en un pico. Con 0,80 y 0,04, o
    con 0,79 y 0,03, saldrían 42, pero sería ajustarse a una pregunta.
  - **Sobre estas 50 preguntas, la híbrida no pierde ninguna respuesta que FTS5 como hoy encuentre.** Lo comprueba
    `MeaningOnlyFloorCalibrationTests`.
- **Hallazgo: con la fusión de D-05, F12-4 no llega a 43/50: queda en 41/50 (82 %).** Los 15 puntos sobre FTS5 sí se
  cumplen (41 frente a 26).
  - **Las que faltan:** q05, q09, q16, q17, q27, q41, q42, q45 y q49.
  - **La causa:** con RRF y k = 60, cualquier candidato que esté en los N primeros puestos de las dos listas (N < 62)
    suma 2 / (60 + N), más que el 1 / 61 del primer puesto de una sola lista. Cinco de las que faltan son primeras a
    quintas por significado y las desplazan candidatos que las dos listas traen en puestos medianos: q05 (1.ª por
    significado, sale 6.ª), q09 (3.ª, 9.ª), q16 (5.ª, 6.ª), q41 (2.ª, 6.ª) y q45 (1.ª, 8.ª). q17, q42 y q49 están del
    décimo puesto por significado en adelante. q27 solo la encuentran las palabras, en el 8.º puesto.
  - **Sin salida dentro de las reglas:** lo único que lo cambia es el peso o k, y los dos los fija D-05.
  - **La prueba Live de F12-4** queda en rojo con el modelo en disco (41/50 frente a 43/50); sin el modelo se omite.
- **Para comparar en la validación:** volver al corte de antes es un cambio de pocas líneas en `SemanticIndex`
  (`SemanticAsync` y `Admitted`): admitir por debajo del corte lo que tiene la lista por palabras.
- **Consecuencias:**
  - D-05 vuelve a ser la de siempre. La segunda adenda queda sustituida: el peso y la vista limpia como texto que buscan
    las palabras.
  - La búsqueda y el paquete dicen qué corte tuvo la lista por significado (`Cut`); nulo, sin corte.
  - La instantánea del planificador lleva el texto anclado completo de la bitácora y los vectores de su vista.
  - El corte y el techo se recalibran si cambia el modelo, la forma de los fragmentos o la plantilla de la bitácora, con
    `MeaningOnlyFloorCalibrationTests` y la prueba Live de F12-4 (`AGENTSTUDIO_E5_MODEL_DIR`,
    `AGENTSTUDIO_CALIBRATION_REPORT`). La calibración deja también dónde queda cada respuesta que falta en cada lista.
  - La cifra de la validación ciega la anota quien la tiene.

## Adenda (2026-11-04, segunda): los identificadores exactos primero y las palabras de la plantilla fuera de la búsqueda

- **Contexto:** una validación ciega de la adenda anterior encontró que la búsqueda perdía frente a FTS5 en las preguntas
  por metadatos (fechas, ejecuciones, `P-NNNN`). Las pérdidas tenían el mismo mecanismo:
  - la respuesta era la primera por palabras, porque contenía el identificador exacto, pero quedaba bajo el corte por
    significado: solo puntuaba 1/61;
  - otras entradas casaban con una palabra de la plantilla que traía la pregunta («ejecución», «#prompt», un CLI), que
    está en menos de la mitad de los fragmentos y por eso pasaba el techo. Si además pasaban el corte, sumaban por las dos
    listas y la adelantaban.

  La revisión pidió además:
  - contar las frecuencias sin traer filas del almacén;
  - que una vista nueva de la bitácora no recalcule todos los vectores;
  - corregir la huella de la instantánea;
  - avisar cuando un modelo no tiene corte.

  La cifra de la validación la anota quien la tiene.
- **Decisión:**
  1. **Los identificadores exactos van primero** (`SearchIdentifiers`). Se reconocen, tal como los escribe la búsqueda y
     nunca dentro de otra palabra:
     - las ejecuciones (`20261015-a1b2c3`, y con `deb-`, `aud-` o `aux-`);
     - los números de la bitácora (`P-0102`), solos o con su día (`2026-10-15/P-0102`, que cuenta como uno solo);
     - los días: ISO, `dd/mm/aaaa`, `dd-mm-aaaa` y «29 de octubre [de 2026]». Un día sin año vale para cualquier año. Un
       `dd/mm` sin año solo cuenta si lleva delante «el», «del» o «día»: suelto, «3/5» es igual de probable que sea una
       proporción («3/5 pruebas en verde») y no debe abrir la cabeza de un nivel;
     - las tareas (`T2`, `T-12`);
     - los ids de la memoria (`mem-…`, `mem:mem-…#n`).

     Un fragmento contiene un identificador cuando lo escribe con sus palabras seguidas, como una frase de FTS5, o cuando
     lo dicen sus datos: el día de su hora o el id de su entrada. Un número seguido de una hora (`P-0004 · 10:15`) no es
     un día.

     Delante de la fusión van como mucho 3 (`MaxIdentifierHits`), uno por entrada y entre lo que la búsqueda admite
     (categorías y confianza), en este orden: más identificadores de la búsqueda, su puesto en la lista por palabras
     (`bm25`), la más nueva y el id. La marca `SemanticHit.Identifier`. Funciona también sin modelo.

     Una tarea sola no abre nada, porque los planes vuelven a numerar sus tareas: solo ordena lo que encontró otro
     identificador. Los números `P-NNNN` vuelven a empezar cada día, así que uno solo puede traer entradas de varios días;
     el tope y el día las ordenan.
  2. **Las palabras fijas de la plantilla no buscan** (`JournalTemplate`, en `Application/Knowledge`). Es una sola lista
     para la vista limpia y para la búsqueda:
     - palabras clave: Bitácora, `#prompt`, Resultado, tokens, exactos, estimados, n/d, ejecución, tarea, intento, entrada,
       sesión y Hecho;
     - estados: los nombres de `AgentRunStatus`;
     - CLI: los tres `ProviderId`.

     La búsqueda quita de sus términos las palabras clave y los CLI, nunca del índice. Si no le queda otro término, busca
     con ellas. El techo del 50 % se queda como red.
     - Los estados no se quitan: con ellos fuera, «¿qué ejecución terminó en Failed?» perdía su respuesta, que FTS5
       encuentra.
     - Quién y el modelo no son cerrados en el producto (el nombre del agente en su equipo, el id del modelo), así que se
       quedan como palabras. Son justo lo que distingue una pregunta por metadatos entre las entradas de un día.
     - El escritor del producto (`MarkdownDailyJournal`, `JournalResultAnchor`) sigue escribiendo sus palabras, porque
       WP1 cambia esos dos ficheros. Una prueba con el escritor real comprueba que usa las de la lista. Que use las
       constantes queda para después de integrar WP1.
  3. **Las frecuencias se cuentan sin preguntar al almacén.** Se cuentan sobre las palabras de los fragmentos anclados de
     la lectura (`FoldedWords`, como `unicode61`), cortadas una vez por fragmento por su SHA-256. El almacén solo contesta
     la lista por palabras: con 4 000 fragmentos, un paquete de tres niveles hace una sola búsqueda por palabras.
  4. **La huella de un vector depende de su categoría** (`SemanticIndex.VectorFingerprint(model, kind)`):
     - es la del modelo para cualquier entrada;
     - para una de la bitácora lleva además la versión de su vista. Una vista nueva solo recalcula los vectores de la
       bitácora;
     - la lista por significado junta las dos búsquedas;
     - la instantánea vuelve a nombrar el modelo, así que su documentación vuelve a ser cierta.
  5. **Un modelo sin corte lo dice.** El texto es `SemanticSearchTexts.NoCut`, que empieza por «Sin corte de relevancia
     para este modelo». Lo dan `SemanticSearchResult.CutNotice`, `ContextRelevance.CutNotice` (en `ContextPack.Relevance`)
     y `SemanticIndexStatus.Cut` y `CutNotice`, que es el estado que lee «Memoria». Mostrarlo en «Memoria» es de WP4.
- **Medidas** (modelo real, con los 36 distractores). Para decidir con metadatos se añadieron al conjunto 15 preguntas
  (`preguntas_metadatos`, escritas antes de tocar la búsqueda): 13 de metadatos e identificadores opacos y 2 de
  identificadores con significado (`ADR-0016`, FluentAssertions). No forman parte de F12-4.

  | Búsqueda | Paráfrasis (F12-4) | Metadatos | Identificador con significado |
  |---|---|---|---|
  | **Híbrida (esta adenda)** | **41/50** | **13/13** | **2/2** |
  | Híbrida sin los identificadores primero | 41/50 | 10/13 | 2/2 |
  | El índice sin modelo | 25/50 | 13/13 | 2/2 |
  | FTS5 como hoy | 26/50 | 9/13 | 2/2 |

  - **Lo que encuentra FTS5:** la híbrida no pierde nada en ningún tipo; lo comprueban la calibración y la prueba Live.
  - **El índice sin modelo** pierde q07 y q32 frente a FTS5, como antes de esta adenda, y q40, que FTS5 tampoco
    encuentra.
  - **El ruido** de los tres niveles por relevancia: 314 distractores (2,1 por nivel), respuesta en su nivel 37/37. Sin
    corte: 1 800 y 38/50.

  | Variante | Paráfrasis | Metadatos | Cambia |
  |---|---|---|---|
  | Sin quitar palabras de la plantilla | 41/50 | 12/13 | gana q40; pierde q45 y m11 |
  | Palabras clave | 41/50 | 13/13 | — |
  | Palabras clave y estados | 41/50 | 12/13 | pierde m11 |
  | **Palabras clave y CLI (la elegida)** | **41/50** | **13/13** | — |
  | Palabras clave, estados y CLI | 41/50 | 12/13 | pierde m11 |
  | Identificadores primero: 0 / 1 / 2 / 3 / 5 | 41/50 | 10 / 13 / 13 / 13 / 13 | con 0 pierde m08, m12 y m13 |
  | Techo: ninguno / 50 % / 25 % | 41/50 | 13/13 | — |

  El corte, con todo esto:

  | Corte | Paráfrasis | Distractores |
  |---|---|---|
  | Sin corte | 38/50 | 1 800 |
  | Suelo 0,79 | 39/50 | 712 |
  | Suelo 0,80 | 40/50 | 355 |
  | Suelo 0,78 y margen 0,04 | 41/50 | 367 |
  | **Suelo 0,79 y margen 0,04** | **41/50** | **314** |
  | Suelo 0,80 y margen 0,04 | 42/50 | 202 |
  | Suelo 0,79 y margen 0,03 | 42/50 | 172 (36/37 en su nivel) |

  Todas las variantes del corte dan 13/13 en metadatos.
- **Por qué:**
  - **Tope 3:** de 1 a 5 da lo mismo, y 0 pierde tres. Se queda el 3 propuesto, que deja sitio a una entrada de otro día
    con el mismo número.
  - **Sin estados en la lista:** pierden m11 sin ganar nada.
  - **Con los CLI:** no cambian nada en el conjunto y son palabras de la plantilla.
  - **El techo y el corte siguen como estaban:** no cambian con esta adenda. Sus vecinos dan de 40 a 42.
- **Hallazgo: F12-4 sigue en 41/50 (82 %).** Quitar «tarea» pierde q40 y quitar «ejecución» gana q45. Las que faltan
  (q05, q09, q16, q17, q27, q40, q41, q42 y q49) siguen siendo de la causa de la primera adenda de 2026-11-04: RRF con
  k = 60. El criterio de 43/50 no se cumple, y en el conjunto no se pierde nada frente a FTS5 en ningún tipo. Lo decide
  quien fija el criterio; el peso y k no se tocan. La prueba Live de F12-4 queda en rojo por el 85 %.
- **Consecuencias:**
  - Una búsqueda que nombra un identificador lo trae primero, también en los niveles de un paquete cuyo foco lo nombra.
  - La lista de palabras de la plantilla es una sola. Si la plantilla cambia, cambia la lista, y la prueba del escritor
    lo avisa.
  - Recalibrar como hasta ahora, con las 65 preguntas y la calibración, que deja dónde queda cada respuesta que falta.

## Adenda (2026-11-04, tercera): F12-4 cambia de criterio, y los arreglos de la validación final

- **Decisión de la persona (2026-11-04):** se acepta la búsqueda y F12-4 cambia. El criterio nuevo: «la híbrida nunca es
  peor que FTS5 como hoy en ningún tipo de pregunta (paráfrasis, identificador con significado y metadatos) y gana al
  menos 10 puntos en total con preguntas nuevas».
  - Deja de exigir el 85 % y los 15 puntos sobre FTS5 en las 50 preguntas.
  - La prueba Live de F12-4 lo comprueba sobre el conjunto de calibración: híbrida ≥ FTS5 por tipo, y al menos 10 puntos
    más en total.
- **Las tres medidas** (la respuesta entre las cinco primeras):

  | Medida | Híbrida | FTS5 como hoy |
  |---|---|---|
  | Las 50 originales de F12-4 | 41/50 | 26/50 |
  | Validación del revisor de la ronda 4, sobre 7730fc8 (51 preguntas)¹ | 47/51 | 48/51 |
  | **Validación final ciega, sobre d80128e (60 preguntas nuevas)** | **43/60** | **35/60 (+13,3 puntos)** |

  ¹ La ronda 5 no se volvió a medir en ese conjunto.

  Por tipo, en la validación final:

  | Tipo | Híbrida | FTS5 como hoy |
  |---|---|---|
  | Paráfrasis | 14/30 | 8/30 |
  | Identificadores con significado | 15/15 | 15/15 |
  | Metadatos | 14/15 | 12/15 |

  En el conjunto de calibración (las 50 y las 15 de metadatos de la segunda adenda de este día), sin cambios tras los
  arreglos de abajo:

  | Tipo | Híbrida | FTS5 como hoy |
  |---|---|---|
  | Paráfrasis | 41/50 | 26/50 |
  | Metadatos | 13/13 | 9/13 |
  | Identificadores con significado | 2/2 | 2/2 |

  La híbrida queda +29,2 puntos por encima en total y no pierde ninguna pregunta que FTS5 encuentre.
- **Hallazgos de la validación final:**
  - En paráfrasis, la fusión RRF queda por debajo de la búsqueda solo por vectores (17/30). Es una mejora pendiente, que
    se medirá sobre la bitácora real.
  - Las dos «pérdidas» frente a FTS5 solo coincidían en palabras vacías.
- **Arreglos de la misma validación** (sin tocar el peso, k ni el corte):
  1. **El día de un fragmento es el de su fuente**, con el que se compara un día de la búsqueda:
     - para una entrada de la bitácora, el día de su fichero (`bitacora:<día>#sha256:…`), no el día en que se derivó;
     - para el resto, el día en que se ancló en el huso de la persona, no en UTC.

     Antes, una bitácora antigua derivada el 5 de noviembre entraba en «¿qué hicimos el 5/11?», y entre las 00:00 y las
     02:00 de España el día salía corrido. El día del fichero se lee con el mismo formato que
     `MemorySources.TryParseJournal`, de WP1; cuando se integre, el índice puede usarlo.
  2. **Una búsqueda que solo pide sus identificadores no usa la lista por significado.** Se quitan sus identificadores,
     las palabras vacías, las de la plantilla, las de pedir («dame», «dime», «muéstrame», «enséñame», «búscame»,
     «tráeme», «quiero», «ver», «saber») y las que no tiene ningún fragmento. Si no queda nada, la búsqueda va solo con
     la cabeza de identificadores y las palabras. Así «Dame lo de la P-0127» ya no trae vecinos. Su modo sigue siendo
     híbrido y su corte, el del modelo.
  3. **Una proporción no es un día.** Un `dd/mm` sin año no cuenta si lo sigue «de las», «de los» o un número («el 24/10
     de las pruebas fallan»). El día y el mes son siempre del calendario.
  4. **Más formatos de día**, con las mismas reglas:
     - `29.10.2026` y `2026/10/18`;
     - los meses abreviados, con o sin punto («20 de oct.», «sept», «set»);
     - «al» y «días» como palabras de día («al 7/5», «los días 3/5»);
     - los días de una lista después del primero («los días 3/5 y 7/5», «el 3/5, 4/5 y 7/5»).
  5. **La versión de la vista de la bitácora cambia sola.** Lleva el número de sus reglas y un hash corto de los estados
     y de las palabras de la plantilla que lee (`JournalSearchText.VersionOf`). Un estado nuevo de `AgentRunStatus`
     recalcula los vectores de la bitácora sin que nadie suba el número.
- **Consecuencias:**
  - La fila F12-4 de docs/17 §9.2 sigue con el criterio de antes. La actualiza quien mantiene ese documento.
  - La prueba Live de F12-4 pasa con el modelo en disco.
  - La calibración sigue exigiendo que no se pierda ninguna pregunta que FTS5 encuentre.
