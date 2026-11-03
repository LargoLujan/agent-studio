# Spike: embeddings locales con multilingual-e5-small

Arnés desechable que decide el modelo del índice semántico de la Fase 12 ([docs/17 §3.2](../../docs/17-constructor-visual-de-agentes.md#32-embeddings-en-tu-equipo-sin-red),
D-03) con cuatro medidas: (a) tokenización de `Microsoft.ML.Tokenizers` frente a la de referencia, (b) tamaño y carga de
las variantes fp32 e int8, (c) velocidad en esta CPU y (d) calidad en castellano. El informe, con las cifras, está en
[docs/investigacion/2026-10-30-spike-embeddings.md](../../docs/investigacion/2026-10-30-spike-embeddings.md); la decisión,
en [ADR-0047](../../docs/adr/0047-indice-semantico-local.md).

No forma parte de `AgentStudio.slnx` y no se compila con la solución. Usa las versiones fijadas en el
`Directory.Packages.props` de la raíz (ONNX Runtime, Tokenizers y el `Microsoft.Data.Sqlite` de la app), que es justo lo
que mide. **El modelo nunca entra en el repositorio**: se descarga fuera, en una carpeta temporal.

## Componentes

| Fichero | Papel |
|---|---|
| `Program.cs` | El arnés: `tokens`, `parity`, `size`, `quality`, `speed`, `search` o `all` |
| `reference.py` | La referencia en Python: ids de `tokenizers` (sobre `tokenizer.json`) y de `sentencepiece` (con la regla de fairseq), y los embeddings de `onnxruntime` |
| `data/frases-tokenizacion.json` | 56 frases: castellano con tildes, eñes y diéresis, signos `¿¡«»`, números, rutas, código, emojis, Unicode descompuesto, ancho completo, otros alfabetos y un texto de más de 512 tokens |
| `data/preguntas.json` | 40 pasajes tipo bitácora y memoria y 30 preguntas que los parafrasean, cada una con su respuesta |

## Cómo ejecutarlo

1. Descargar los ficheros de la revisión fijada a una carpeta **fuera del repositorio** y comprobar su SHA-256:

   | Fichero (`https://huggingface.co/intfloat/multilingual-e5-small/resolve/614241f622f53c4eeff9890bdc4f31cfecc418b3/…`) | Bytes | SHA-256 |
   |---|---|---|
   | `onnx/model_qint8_avx512_vnni.onnx` | 118 346 824 | `dd476dd0c2514e9b9be83aeb3853fac0763e0bdf4a71645407587d77c48a2d88` |
   | `onnx/model.onnx` | 470 268 510 | `ca456c06b3a9505ddfd9131408916dd79290368331e7d76bb621f1cba6bc8665` |
   | `onnx/sentencepiece.bpe.model` | 5 069 051 | `cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865` |
   | `onnx/tokenizer.json` | 17 082 730 | `0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39` |

   ```powershell
   $m = "$env:TEMP\e5"; New-Item -ItemType Directory -Force "$m\onnx" | Out-Null
   $base = 'https://huggingface.co/intfloat/multilingual-e5-small/resolve/614241f622f53c4eeff9890bdc4f31cfecc418b3'
   foreach ($f in 'onnx/model_qint8_avx512_vnni.onnx','onnx/model.onnx','onnx/sentencepiece.bpe.model','onnx/tokenizer.json') {
       curl.exe -L --fail -o "$m\$($f -replace '/','\')" "$base/$f"
       (Get-FileHash -Algorithm SHA256 "$m\$($f -replace '/','\')").Hash.ToLowerInvariant()
   }
   ```

2. La referencia de Python, en un entorno virtual aparte (`tokenizers`, `sentencepiece`, `onnxruntime` y `numpy`):

   ```powershell
   python -m venv "$env:TEMP\e5-venv"
   & "$env:TEMP\e5-venv\Scripts\python.exe" -m pip install tokenizers sentencepiece onnxruntime numpy
   & "$env:TEMP\e5-venv\Scripts\python.exe" -I spikes/e5-embeddings/reference.py --model-dir $m --data spikes/e5-embeddings/data --out "$env:TEMP\e5-reference.json"
   ```

3. El arnés, en Release y con la máquina en reposo (las medidas de velocidad son sensibles a la carga):

   ```powershell
   dotnet build spikes/e5-embeddings/E5Spike.csproj -c Release
   spikes/e5-embeddings/bin/Release/net10.0/E5Spike.exe all --model-dir $m --reference "$env:TEMP\e5-reference.json" --data spikes/e5-embeddings/data
   ```

## Qué no hace

- No escribe nada fuera de su salida: las bases de FTS5 son en memoria y los vectores de la prueba de 10 000 fragmentos
  son aleatorios (sembrados): el coste de recorrerlos no depende de su contenido.
- No ejecuta ningún CLI de proveedor ni gasta cuota.
- No es el código de la app: el embedder, el almacén y la búsqueda los hacen WP2 y WP3 de la Fase 12 con las reglas de
  siempre (sin avisos, con pruebas).
