# Spike: embeddings locales para el índice semántico — 2026-10-30

> Decide el modelo de la Fase 12 ([docs/17 §3.2](../17-constructor-visual-de-agentes.md#32-embeddings-en-tu-equipo-sin-red),
> D-03) con las cuatro medidas que pedía el P0: tokenización, tamaño, velocidad en esta CPU y calidad en castellano. La
> decisión está en [ADR-0047](../adr/0047-indice-semantico-local.md). El arnés y los datos están en
> [`spikes/e5-embeddings/`](../../spikes/e5-embeddings/README.md), fuera de `AgentStudio.slnx`.
> El modelo se descargó con tu OK (decisión 9) a una carpeta temporal, **fuera del repositorio**. Ningún CLI de proveedor
> recibió un prompt y no se gastó cuota. Todas las cifras son de esta máquina, medidas el 2026-10-30 (**V**).

## Respuesta corta

**multilingual-e5-small, en su variante int8 del propio repositorio, sirve.** La tokenización de `Microsoft.ML.Tokenizers`
reproduce la de referencia, ocupa 113 MB, una consulta tarda unos 8 ms (mediana) y la búsqueda completa con 10 000
fragmentos, 52 ms en el percentil 95. Con una condición que el diseño no tenía: **la lista por palabras va sin palabras
vacías**. Con ellas, la búsqueda híbrida es peor que los vectores solos.

| Medida | Resultado | ¿Cumple? |
|---|---|---|
| (a) Tokenización | 56 de 56 frases iguales a `sentencepiece` con la regla de XLM-R; 55 de 56 iguales a `tokenizers` (la otra difiere solo en los espacios del final) | Sí, recortando el texto |
| (b) Tamaño | int8: 113 MB en disco, 0,4 s de carga, 134 MB de memoria. fp32: 449 MB, 0,8 s, 465 MB | Sí (int8) |
| (c) Velocidad (F12-5) | 10 000 fragmentos, int8 con 2 hilos: p50 25 ms, **p95 52 ms** (objetivo: menos de 200 ms) | Sí |
| (d) Calidad | Respuesta entre las 5 primeras: FTS5 de hoy 70 %, vectores 90 %, **híbrida sin palabras vacías 97 %** | Sí, en un conjunto pequeño |

## Entorno

| Elemento | Valor |
|---|---|
| Equipo | Windows 11 Pro 10.0.26300 · AMD Ryzen 7 7730U (Zen 3, 8 núcleos y 16 hilos): AVX2 sí, AVX-512 no, AVX-VNNI no |
| .NET | SDK 10.0.303, runtime 10.0.11 |
| Paquetes | `Microsoft.ML.OnnxRuntime` 1.31.1 y `Microsoft.ML.Tokenizers` 2.0.1, los fijados en `Directory.Packages.props`; `Microsoft.Data.Sqlite` 10.0.13 para FTS5 |
| Referencia | Python 3.12 en un entorno aparte: `tokenizers` 0.23.3, `sentencepiece` 0.2.2 y `onnxruntime` 1.31.1 |
| Modelo | `intfloat/multilingual-e5-small`, revisión `614241f622f53c4eeff9890bdc4f31cfecc418b3` (2026-04-02), licencia MIT en su ficha |

### Ficheros descargados

De `https://huggingface.co/intfloat/multilingual-e5-small/resolve/614241f622f53c4eeff9890bdc4f31cfecc418b3/`. Cada SHA-256
coincide con el que publica la API de Hugging Face para el fichero (LFS).

| Fichero | Bytes | SHA-256 |
|---|---|---|
| `onnx/model_qint8_avx512_vnni.onnx` (int8, **el elegido**) | 118 346 824 | `dd476dd0c2514e9b9be83aeb3853fac0763e0bdf4a71645407587d77c48a2d88` |
| `onnx/model.onnx` (fp32) | 470 268 510 | `ca456c06b3a9505ddfd9131408916dd79290368331e7d76bb621f1cba6bc8665` |
| `onnx/sentencepiece.bpe.model` (**el elegido**) | 5 069 051 | `cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865` |
| `onnx/tokenizer.json` (solo para la referencia) | 17 082 730 | `0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39` |

El repositorio trae también `onnx/model_O4.onnx` (235 MB), optimizado para GPU en fp16; no se midió. El grafo ONNX recibe
`input_ids`, `attention_mask` y `token_type_ids` (int64) y devuelve `last_hidden_state` (384 dimensiones). El embedding es
la media con la máscara de atención, normalizada (L2), como dice la ficha del modelo.

## (a) Tokenización

- **El modelo de SentencePiece es Unigram**, aunque el fichero se llame `.bpe.model`. `SentencePieceTokenizer.Create` de
  `Microsoft.ML.Tokenizers` 2.0.1 lo carga: 250 000 piezas, `unk=0`, `bos=1`, `eos=2`, prefijo de espacio y sin
  `byte fallback`.
- **La regla de XLM-RoBERTa** (fairseq): `<s>=0`, `<pad>=1`, `</s>=2` y `<unk>=3`; cualquier otra pieza, su id de
  SentencePiece más 1. Con ella, **56 de 56 frases dan los mismos ids que `sentencepiece`**. Las frases cubren tildes, eñes y
  diéresis, `¿¡«»`, números con separadores, rutas, código, URLs, emojis, tildes descompuestas (NFD), ancho completo,
  ligaduras, espacio duro, catalán, alemán, chino, japonés, coreano, ruso, griego y un texto de más de 512 tokens (se corta
  a 510 más los dos especiales).
- **Frente a `tokenizers`** (el `tokenizer.json` de la ficha), 55 de 56. La que difiere es `«   Espacios al principio y al
  final   »`: `tokenizers` añade un `▁` (id 6) por los espacios del final y `sentencepiece` no. Por eso el embedder recorta
  el texto antes de tokenizar, y así coincide con las dos referencias.
- **De punta a punta:** con los mismos ids, el embedding de .NET y el de Python (onnxruntime) dan coseno **1,000000** en las
  56 frases, en fp32 y en int8. Con el tokenizador de .NET, la media es 0,9998, y el mínimo, 0,991, es la frase de los
  espacios.
- **Carga del tokenizador:** unos 2 s la primera vez (con la compilación JIT). Se carga una vez, en segundo plano.

## (b) Tamaño, carga y memoria

| Variante | Disco | Carga (3 veces) | Memoria privada | Coseno con fp32 (mismos ids, 56 frases) |
|---|---|---|---|---|
| int8 (`model_qint8_avx512_vnni.onnx`) | 112,9 MiB | 486, 404 y 385 ms | +134 MB | mínimo 0,9905; media 0,9960 |
| fp32 (`model.onnx`) | 448,5 MiB | 850, 901 y 786 ms | +465 MB | — |

El nombre de la int8 dice cómo se cuantizó (para AVX-512 VNNI), no dónde corre: aquí, sin VNNI, ONNX Runtime usa sus
núcleos de AVX2, y el acuerdo con fp32 sigue por encima de 0,99.

**El paquete de ONNX Runtime trae el `onnxruntime.dll` nativo de cada plataforma**: 16,5 MB el de win-x64. Sin
`RuntimeIdentifier`, la compilación copia los de win-x64, win-arm64, linux-x64, linux-arm64 y osx-arm64, unos 128 MB. Lo
decide el instalador (Fase 7).

## (c) Velocidad en esta CPU

Un portátil con la carga de siempre: los percentiles 95 varían entre ejecuciones. 300 consultas por medida, tras 10 de
calentamiento.

**Una consulta** (tokenizar más embedding; tokenizar solo: 0,1 ms):

| Hilos (intra-op) | int8 p50 / p95 | fp32 p50 / p95 |
|---|---|---|
| 1 | 17,1 / 71,3 ms | 27,2 / 46,5 ms |
| 2 | **8,0 / 22,2 ms** | 13,8 / 27,4 ms |
| 4 | 8,3 / 19,6 ms | 14,4 / 44,7 ms |
| 8 | 8,7 / 14,4 ms | 17,7 / 84,3 ms |

**Indexar** fragmentos de 272 a 316 tokens (unos 1 200 caracteres): **de 100 a 200 ms por fragmento**, en int8 y en fp32,
sin que los lotes mejoren de forma estable (lotes de 1, 8, 16 y 32 con 4 y 8 hilos). Indexar 10 000 fragmentos así lleva de
17 a 35 minutos de CPU, una vez y en segundo plano. Después solo se indica lo nuevo. Los fragmentos de memoria suelen ser
más cortos.

**F12-5: la búsqueda completa con 10 000 fragmentos.** Incluye el embedding de la consulta, el coseno con 10 000 vectores
(14,6 MB, `System.Numerics.Vector<float>`, top 50), FTS5 sobre 8,6 millones de caracteres (top 50) y la fusión:

| Variante | Total p50 / p95 | Embedding p95 | Coseno p95 | FTS5 p95 |
|---|---|---|---|---|
| **int8, 2 hilos, FTS5 sin palabras vacías** | **25,4 / 51,9 ms** | 18,1 ms | 3,1 ms | 34,3 ms |
| int8, 4 hilos, FTS5 sin palabras vacías | 35,5 / 77,1 ms | 29,7 ms | 4,4 ms | 54,8 ms |
| int8, 2 hilos, FTS5 como hoy | 53,3 / 120,0 ms | 18,4 ms | 3,0 ms | 101,5 ms |
| int8, 4 hilos, FTS5 como hoy | 85,8 / 162,0 ms | 25,2 ms | 3,6 ms | 134,4 ms |
| fp32, 4 hilos, FTS5 sin palabras vacías | 34,2 / 69,8 ms | 27,6 ms | 4,5 ms | 45,4 ms |
| fp32, 4 hilos, FTS5 como hoy | 85,8 / 246,5 ms | 50,3 ms | 3,8 ms | 184,9 ms |

- **Lo que más cuesta es FTS5 cuando la consulta lleva palabras vacías:** un `OR` con «de», «la» o «el» casa con todos los
  fragmentos y `bm25` los puntúa todos. Sin ellas, FTS5 baja a un tercio.
- **Recorrer 10 000 vectores** cuesta unos 2 ms. Con 50 000, unos 10 ms; no hace falta sqlite-vec (D-04).
- Con **2 hilos** la consulta ya va rápida y deja la CPU libre para la interfaz.

## (d) Calidad en castellano

Conjunto sintético tipo bitácora y memoria: 40 pasajes y 30 preguntas, cada una con su respuesta. Las preguntas usan a
propósito otras palabras: sinónimos («modo nocturno» por «tema oscuro»), paráfrasis («copias de trabajo» por «worktrees»)
e inglés técnico («race condition»). FTS5 es el de hoy: `unicode61`, cada término entre comillas y unidos con `OR`, y
`bm25`.

| Búsqueda | Acierto en la 1.ª | Entre las 5 primeras | MRR |
|---|---|---|---|
| FTS5 como hoy | 17 (57 %) | 21 (70 %) | 0,645 |
| FTS5 sin palabras vacías | 16 (53 %) | 20 (67 %) | 0,590 |
| Vectores fp32 (`query:` y `passage:`) | 21 (70 %) | 27 (90 %) | 0,785 |
| Vectores fp32 sin prefijos | 23 (77 %) | 27 (90 %) | 0,825 |
| Vectores int8 | 22 (73 %) | 27 (90 %) | 0,804 |
| Híbrida (RRF, k = 60) con FTS5 como hoy, fp32 / int8 | 16 / 15 | 23 / 23 (77 %) | 0,644 / 0,629 |
| Híbrida con FTS5 solo sus 10 primeros, fp32 | 15 | 20 (67 %) | 0,603 |
| **Híbrida con FTS5 sin palabras vacías**, fp32 / int8 | 19 / 19 (63 %) | **29 / 29 (97 %)** | 0,772 / 0,775 |

- **Los vectores solos ya ganan 20 puntos** a FTS5 entre las 5 primeras.
- **Con palabras vacías, la fusión estropea los vectores** (77 % frente a 90 %): el `OR` deja casi todos los pasajes en la
  lista por palabras, en un orden que para una paráfrasis es ruido, y la fusión lo reparte por igual. Cortar esa lista a
  sus 10 primeros no lo arregla. **Quitar las palabras vacías sí:** 97 %, 27 puntos más que FTS5 hoy y 7 más que los
  vectores. Solo falla q07 («no puedo revertir un error en el editor de flujo», cuya respuesta habla de «deshacer» en el
  «lienzo»).
- **El acierto en la primera posición baja** con la fusión (63 % frente a 70-73 %). Para el paquete de contexto, que mete
  varios elementos, y para la herramienta del planificador, que devuelve varios, cuenta más estar entre los primeros.
- **int8 y fp32 dan la misma calidad.**
- **Los prefijos:** aquí, sin ellos, se acierta en la primera posición en 2 preguntas más, y entre las 5 primeras igual. Se
  mantienen, como pide la ficha del modelo, y que lo confirme el conjunto de 50 preguntas de WP3 (F12-4).
- **Límite:** son 30 preguntas sintéticas, escritas por el mismo autor que los pasajes. Es una indicación, no la prueba de
  F12-4, que exige 50 preguntas y la bitácora real de este repositorio.

## Recomendación

1. **El modelo:** `multilingual-e5-small` en la variante int8 del repositorio oficial
   (`onnx/model_qint8_avx512_vnni.onnx`) y su `onnx/sentencepiece.bpe.model`, con las huellas de la tabla, fijadas en el
   código (`EmbeddingModels.MultilingualE5Small`). Misma calidad que fp32, con un cuarto del disco, un tercio de la memoria
   y la mitad de tiempo por consulta.
2. **El tokenizador:** `SentencePieceTokenizer` de `Microsoft.ML.Tokenizers`, la regla de ids de XLM-R y el texto recortado.
3. **Las consultas, con 2 hilos.** Indexar va en segundo plano, con prioridad baja y en lotes que se pueden cancelar.
4. **La lista por palabras, sin palabras vacías** (`LexicalTerms`): enmienda D-05, que decía «con la consulta escapada como
   hoy». El escape se mantiene.
5. **Que WP3 vuelva a medir** con el conjunto de 50 preguntas sobre la bitácora real (F12-4) y deje la medición de F12-5
   como evidencia.

## Sin verificar

- La velocidad y el acuerdo de la int8 en una CPU con AVX-512 VNNI, en una sin AVX2 o en ARM64.
- El rendimiento con más de 10 000 fragmentos de texto real (el de los vectores no depende del contenido).
- La calidad con la bitácora real del repositorio y con 50 preguntas: es el criterio F12-4 de WP3.
