# Spikes de la Fase 3 (S2 a S5) — 2026-10-15

> Evidencia del paquete WP3 de [docs/13](../13-fase-3-orquestacion.md) (§8, §9 y §10). Cada afirmación lleva una
> etiqueta: **V** = verificado ejecutándolo en esta máquina; **D** = verificado en documentación oficial; **NV** = no
> verificado todavía.
> Se ejecutó un único prompt real contra Codex (S2) y otro contra Claude Code con `--model haiku` (S3), con el OK del
> usuario para consumir cuota. Se han sustituido los datos personales: el usuario pasa a `usuario` y las carpetas
> temporales a `<tmp>`.

## Entorno

| Herramienta | Versión |
|---|---|
| Windows | 11 Pro (10.0.26300) |
| git | 2.45.2.windows.1 |
| .NET SDK | 10.0.303 (`global.json` fija `test.runner = Microsoft.Testing.Platform`) |
| xunit.v3 | 4.0.2 (con `Microsoft.NET.Test.Sdk` 18.11.0 y `xunit.runner.visualstudio` 4.0.1) |
| Codex CLI | 0.160.0, binario nativo `codex.exe` |
| Claude Code | 2.1.290 |

## Resumen

| Spike | Pregunta | Resultado | Estado |
|---|---|---|---|
| S2 | ¿Codex escribe dentro de un worktree y deja intacto el árbol principal? | Sí: `hola.txt` aparece en el worktree y no cambia nada fuera | V |
| S3 | ¿Claude devuelve `structured_output` y usa herramientas en la misma ejecución? | Sí: llama a `Read`, luego a `StructuredOutput`, y el `result` trae el objeto con el valor leído | V |
| S4 | ¿Cómo se obtiene un informe legible por máquina de `dotnet test` con xunit v3 sobre MTP? | `--report-xunit-trx` y `--report-xunit-junit`; `--report-trx` no existe en este proyecto | V, D |
| S5 | ¿`merge-tree --write-tree`, `commit-tree` y `update-ref` como compare-and-swap funcionan como pide el diseño? | Sí, con una salvedad: el código de salida 1 de `merge-tree` también significa error | V |

## S2 — Codex escribiendo en un worktree

Pregunta: la captura 02 del Fase 2 usó un repo normal. Aquí `.git` es un fichero que apunta a
`<repo>/.git/worktrees/T1`. ¿Funciona `workspace-write`?

### Preparación

```text
git init -q -b main <tmp>\s2\repo
git -C <tmp>\s2\repo config user.name Spike          (idem user.email, commit.gpgsign=false, core.autocrlf=false)
# README.md y un commit "base"
git -C <tmp>\s2\repo worktree add --detach <tmp>\s2\wt\abcd1234\a1b2c3\T1 <base>
```

```text
Preparing worktree (detached HEAD bb465a2)
HEAD is now at bb465a2 base
```

```text
$ type <tmp>\s2\wt\abcd1234\a1b2c3\T1\.git
gitdir: <tmp>/s2/repo/.git/worktrees/T1
```

### Ejecución

El binario nativo que resuelve `CodexExecutableResolver` es
`%APPDATA%\npm\node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe`.
Se lanza con `ProcessStartInfo.ArgumentList` (nunca como línea de comandos), el prompt por stdin y el directorio actual
del proceso en el repo principal; la raíz escribible la fija `-C`.

```text
codex.exe exec --json -s workspace-write -C <tmp>\s2\wt\abcd1234\a1b2c3\T1 --skip-git-repo-check -c sandbox_workspace_write.network_access=false -   < prompt.txt
```

Prompt (una línea): `Crea un fichero llamado hola.txt con el texto hola y responde HECHO.`

Salida (`stdout`, JSONL; `stderr` vacío). Entre `thread.started` y `turn.started` hay dos `item.completed` de tipo `error`
que se omiten aquí: son avisos de configuración de la cuenta («Ignoring unknown `features` requirement
`ultrafast_mode`…»), no fallos. El campo `command` del segundo evento de la orden se abrevia:

```json
{"type":"thread.started","thread_id":"01a1404b-5e50-7ef3-aadc-9ec4d8ac1d33"}
{"type":"turn.started"}
{"type":"item.completed","item":{"id":"item_2","type":"agent_message","text":"Crearé `hola.txt` con el texto `hola`.\n"}}
{"type":"item.started","item":{"id":"item_3","type":"command_execution","command":"\"C:\\\\Program Files\\\\PowerShell\\\\7\\\\pwsh.exe\" -Command \"Set-Content -LiteralPath 'hola.txt' -Value 'hola' -NoNewline -Encoding ascii\"","aggregated_output":"","exit_code":null,"status":"in_progress"}}
{"type":"item.completed","item":{"id":"item_3","type":"command_execution","command":"... Set-Content -LiteralPath 'hola.txt' -Value 'hola' ...","aggregated_output":"","exit_code":0,"status":"completed"}}
{"type":"item.completed","item":{"id":"item_4","type":"agent_message","text":"HECHO"}}
{"type":"turn.completed","usage":{"input_tokens":39521,"cached_input_tokens":26752,"cache_write_input_tokens":0,"output_tokens":73,"reasoning_output_tokens":0}}
```

### Comprobaciones

| Comprobación | Resultado |
|---|---|
| Código de salida y duración | 0, 16,7 s |
| `hola.txt` en el worktree | Sí, con el contenido `hola` |
| `git -C <worktree> status --porcelain=v1` | `?? hola.txt` |
| `hola.txt` en el árbol principal | No |
| HEAD, `status`, refs y ficheros del árbol principal | Idénticos antes y después |
| `git worktree list --porcelain` | El worktree sigue desprendido y con el mismo HEAD |

### Conclusiones de S2

- **V** — `workspace-write` con `-C <worktree>` escribe dentro de un worktree aunque `.git` sea un fichero, y no toca el
  árbol principal. La plantilla de agentes puede usar Codex como ejecutor.
- **V** — El adaptador no necesita cambios: `CodexExecArguments.IsRepository` ya acepta un `.git` que sea fichero y, con
  `--skip-git-repo-check`, no importa.
- **V** — El agente trabajó con `pwsh.exe -Command` (`command_execution`), como en las capturas anteriores.
- **NV** — Que el sandbox impida escribir fuera del worktree (por ejemplo en `<repo>/.git/worktrees/T1`) no se ha
  probado. El diseño no depende de ello: el sistema hace los commits y la verificación comprueba el alcance sobre el diff.
- **NV** — Que `network_access=false` bloquee de verdad la red: solo se comprobó que Codex acepta el parámetro.

## S3 — Claude con `--json-schema` y herramientas

Pregunta: la captura 03 usó `--tools ""`. ¿Llega `structured_output` cuando el agente además usa herramientas?

### Ejecución

Carpeta temporal con `datos.json` (`{"id": 7, "codigo": "ZX-1059-CU", "comentario": "dato de prueba"}`; el código se
genera al azar en cada ejecución, de modo que solo se puede devolver leyendo el fichero). Las banderas son las que
construye `ClaudeCodeArguments` para un agente de solo lectura (`AllowFileWrites = false`, sin shell ni red,
`AllowedTools = [Read, Grep, Glob]`), con `--model haiku`:

```text
claude.exe -p --output-format stream-json --verbose --setting-sources project,local --permission-mode dontAsk
  --allowedTools "Read Grep Glob" --disallowedTools "Bash WebFetch WebSearch Write Edit NotebookEdit"
  --model haiku --max-turns 6
  --json-schema '{"type":"object","properties":{"codigo":{"type":"string"}},"required":["codigo"],"additionalProperties":false}'
  --session-id 8e06a00a-d60d-4d03-b506-dd4df073a5eb   < prompt.txt
```

Prompt: `Lee el fichero datos.json de la carpeta actual y devuelve el valor de su campo "codigo".`

### Resultado

Código de salida 0 en 15,4 s. Tipos de evento del flujo: `system/init` ×1, `system/thinking_tokens` ×5,
`rate_limit_event` ×1, `assistant` ×4, `user` ×2, `result/success` ×1.

```text
init:        model=claude-haiku-4-5-20251001  permissionMode=dontAsk  (84 herramientas listadas, 55 de ellas de los conectores `mcp__` de la cuenta)
tool_use:    Read {"file_path":"<tmp>\\s3\\work\\datos.json"}
tool_result: 1	{"id": 7, "codigo": "ZX-1059-CU", "comentario": "dato de prueba"} 2
tool_use:    StructuredOutput {"codigo":"ZX-1059-CU"}
tool_result: Structured output provided successfully
result:      subtype=success is_error=false num_turns=3 duration_ms=12008 total_cost_usd=0.0664118
result.structured_output: {"codigo":"ZX-1059-CU"}
result.result (texto):     {"codigo":"ZX-1059-CU"}
usage:       input=18 cache_creation=30030 cache_read=29678 output=474   modelUsage: claude-haiku-4-5-20251001
valor esperado ZX-1059-CU = valor devuelto ZX-1059-CU
```

### Conclusiones de S3

- **V** — `structured_output` llega en el evento `result` (el campo que ya lee `ClaudeStreamJsonParser.BuildOutcome`) en la
  misma ejecución en la que el agente usó herramientas. Las herramientas se usaron de verdad: `Read` sobre el fichero y el
  valor devuelto es el que contenía.
- **V** — Claude añade una herramienta sintética, `StructuredOutput`, que se ve como un `tool_use` más y **gasta un turno**:
  aquí `num_turns = 3` (`Read`, `StructuredOutput`, cierre). El planificador debe llevar un `--max-turns` holgado (leer
  varios ficheros más dos turnos).
- **V** — `result.result` repite el JSON como texto. El adaptador debe seguir prefiriendo `structured_output`.
- **V** — Con `--model haiku` y `dontAsk` no hubo ninguna petición de permiso. La lista de herramientas de `init` incluye
  los conectores de la cuenta; `dontAsk` deniega lo que no está en `--allowedTools`.
- **NV** — Que esos conectores se denieguen de verdad si el agente intentara usarlos no se ha ejercitado.
- **NV** — Comportamiento con `--max-turns` demasiado bajo y `--json-schema` (¿`error_max_turns` sin `structured_output`?).

## S4 — Informe TRX o JUnit con xunit v3 sobre Microsoft.Testing.Platform

Pregunta: ¿cómo se obtiene un informe legible por máquina de `dotnet test`? docs/13 §5 pide leer TRX o JUnit y no la
consola, que está traducida («correcto: N»).

### De dónde salen las banderas

- **V** — `dotnet test --no-build --help` (con `global.json` en modo MTP) imprime las opciones de la plataforma y las de las
  extensiones registradas por el proyecto de pruebas. Es la forma de comprobar qué acepta cada proyecto.
- **V** — `dotnet test -- --help` hace fallar el propio `dotnet test` del SDK 10.0.303 («Se recibió un mensaje inesperado
  relacionado con la ayuda cuando no se usó "--help"»). No usarlo.
- **V** — Ejecutar `AgentStudio.Tests.exe --help` directamente muestra el ejecutor nativo de xUnit, con otras banderas (de un
  solo guion, como `-result-trx`). No son las de `dotnet test`.
- **V** — Opciones de informe que registra xunit.v3 4.0.2 (todas con su `...-filename`):

```text
--report-xunit-ctrf       --report-xunit-html      --report-xunit-junit
--report-xunit-markdown   --report-xunit-nunit     --report-xunit-trx
--report-xunit-xml
```

- **D** — `--report-trx` y `--report-trx-filename` pertenecen al paquete `Microsoft.Testing.Extensions.TrxReport`, que este
  repositorio no referencia: la opción no se reconoce y la ejecución termina con el código 5
  ([documentación](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-troubleshooting#resolve-configuration-errors)).
  **V** — Comprobado: `dotnet test -- --report-trx` termina con el código 5, sin ejecutar ninguna prueba y sin mostrar el
  motivo; lo mismo ocurre con `--report-xunit-trx-filename` sin `--report-xunit-trx`.

### Banderas exactas

```text
dotnet test [<proyecto>] --results-directory <carpeta> -- --report-xunit-trx --report-xunit-trx-filename <nombre>.trx
dotnet test [<proyecto>] --results-directory <carpeta> -- --report-xunit-junit --report-xunit-junit-filename <nombre>.xml
```

- `--results-directory` va **antes** de `--` (es del SDK y de la plataforma) y la carpeta se crea si no existe. El informe
  queda en `<carpeta>\<nombre>`.
- Si no se indica nada, el fichero va a `TestResults\<usuario>_<equipo>_<fecha>.trx` junto al proyecto: no es determinista.
- Los dos informes se pueden pedir a la vez. `dotnet test` imprime la ruta de cada uno en «In process file artifacts produced».
- Defensa adicional, **V**: `-- --zero-tests-policy strict` convierte «todo omitido» en el código 8, y
  `-- --minimum-expected-tests <n>` termina con el código 9 si se ejecutaron menos pruebas.

### Prueba sobre un proyecto desechable

Proyecto en `<tmp>\s4\tiny` con el `global.json` del repositorio y cuatro pruebas: una correcta, una que falla, una omitida
(`Assert.Skip`) y una teoría de dos filas. Referencias: `xunit.v3` 4.0.2, `Microsoft.NET.Test.Sdk` 18.11.0 y
`xunit.runner.visualstudio` 4.0.1, que se restauran desde la caché local.

```text
set DOTNET_CLI_UI_LANGUAGE=en
dotnet test --results-directory .\results-en -- --report-xunit-trx --report-xunit-trx-filename tiny.trx --report-xunit-junit --report-xunit-junit-filename tiny-junit.xml
```

Consola con `DOTNET_CLI_UI_LANGUAGE=en` (código de salida 2):

```text
Running tests from <tmp>\s4\tiny\bin\Debug\net10.0\Tiny.Tests.dll (net10.0|x64)
skipped Tiny.Tests.TinyTests.Skipped
  motivo de omision
failed Tiny.Tests.TinyTests.Fails (7ms)
  Assert.Equal() Failure: Values differ
  Expected: 1
  Actual:   2
    at Tiny.Tests.TinyTests.Fails() in <tmp>\s4\tiny\TinyTests.cs:9
<tmp>\s4\tiny\bin\Debug\net10.0\Tiny.Tests.dll (net10.0|x64) failed with 1 error(s) (1s 163ms)
Exit code: 2

  In process file artifacts produced:
    - <tmp>\s4\tiny\results-en\tiny-junit.xml
    - <tmp>\s4\tiny\results-en\tiny.trx

Test run summary: Failed!
  total: 5
  failed: 1
  succeeded: 3
  skipped: 1
  duration: 2s 559ms
Test run completed with non-success exit code: 2 (see: https://aka.ms/testingplatform/exitcodes)
```

La misma ejecución sin `DOTNET_CLI_UI_LANGUAGE` (idioma del sistema, castellano):

```text
Ejecutando pruebas desde <tmp>\s4\tiny\bin\Debug\net10.0\Tiny.Tests.dll (net10.0|x64)
omitido Tiny.Tests.TinyTests.Skipped
con errores Tiny.Tests.TinyTests.Fails (7ms)
...
Código de salida: 2

  Artefactos de archivo en proceso producidos:
    - <tmp>\s4\tiny\results-es\tiny.trx

Resumen de la serie de pruebas: Error!
  total: 5
  error: 1
  correcto: 3
  omitido: 1
  duración: 1s 563ms
```

- **V** — La consola cambia de palabras con el idioma (`failed/succeeded/skipped` frente a `error/correcto/omitido`, y
  `Test run summary` frente a `Resumen de la serie de pruebas`). `DOTNET_CLI_UI_LANGUAGE=en` la deja en inglés, pero la
  fuente de verdad es el informe. Los mensajes de los asertos de xunit no se traducen.

### Cómo son los informes

TRX de `xunit.v3` (una sola línea, sin declaración XML; aquí se separa por legibilidad y se recortan los atributos):

```xml
<TestRun id="..." name="usuario@EQUIPO ..." runUser="usuario" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Times .../><TestSettings .../>
  <Results>
    <UnitTestResult testName="Tiny.Tests.TinyTests.Passes" outcome="Passed" duration="00:00:00.1900000" .../>
    <UnitTestResult testName="Tiny.Tests.TinyTests.Skipped" outcome="NotExecuted" ...><Output><StdOut>motivo de omision</StdOut></Output></UnitTestResult>
    <UnitTestResult testName="Tiny.Tests.TinyTests.Theory(value: 2)" outcome="Passed" .../>
    <UnitTestResult testName="Tiny.Tests.TinyTests.Fails" outcome="Failed" ...>
      <Output><ErrorInfo><Message>Assert.Equal() Failure: Values differ ...</Message><StackTrace>...</StackTrace></ErrorInfo></Output>
    </UnitTestResult>
  </Results>
  <TestDefinitions>...</TestDefinitions><TestEntries>...</TestEntries><TestLists>...</TestLists>
  <ResultSummary outcome="Failed"><Counters total="5" executed="4" passed="3" failed="1" ... notExecuted="1" .../></ResultSummary>
</TestRun>
```

JUnit de `xunit.v3`:

```xml
<testsuites name="Test results" tests="5" failures="1" errors="0" disabled="0" time="0.432000" timestamp="2026-10-15T15:45:07">
  <testsuite name="<ruta>\Tiny.Tests.dll" tests="5" failures="1" errors="0" disabled="0" skipped="1" time="0.432000" hostname="EQUIPO">
    <testcase name="Tiny.Tests.TinyTests.Passes" time="0.190000" classname="Tiny.Tests.TinyTests" />
    <testcase name="Tiny.Tests.TinyTests.Skipped" time="0.000000" classname="Tiny.Tests.TinyTests"><skipped>motivo de omision</skipped></testcase>
    <testcase name="Tiny.Tests.TinyTests.Fails" time="0.007000" classname="Tiny.Tests.TinyTests"><failure message="Assert.Equal() Failure: Values differ...">...</failure></testcase>
  </testsuite>
</testsuites>
```

Reglas que siguen los analizadores (`TrxReportParser` y `JUnitReportParser`):

- TRX: cada `UnitTestResult` cuenta una vez por su `outcome`. `Passed` es correcta; `NotExecuted` e `Inconclusive` son
  omitidas; cualquier otro valor, incluido uno desconocido, es fallo. No se confía en `Counters`.
- JUnit: cada `testcase` cuenta una vez; un hijo `failure` o `error` lo hace fallido, un hijo `skipped` omitido, y si no
  tiene ninguno es correcto. No se confía en los atributos `tests`/`failures` de los `testsuite`.

### Casos que justifican «0 pruebas no es éxito»

| Caso | Orden | Código de salida | Informe |
|---|---|---|---|
| Ninguna prueba (`--filter-class NoExiste`) | `dotnet test --no-build -- --filter-class "NoExiste" --report-xunit-trx ...` | **8** | Se escribe igualmente: `<Results />`, `total="0"`; en JUnit `tests="0"` |
| Solo una prueba omitida (`--filter-method Tiny.Tests.TinyTests.Skipped`) | `dotnet test --no-build -- --filter-method "..." ...` | **0** | `passed="0"`, `notExecuted="1"`; la consola dice «Zero tests ran» pero sale con 0 |
| Igual, con `--zero-tests-policy strict` | añadiendo esa opción tras `--` | **8** | El mismo informe |
| `--minimum-expected-tests 2` con una sola prueba correcta | añadiendo esa opción tras `--` | **9** | — |
| Opción desconocida (`--report-trx`) | ver arriba | **5** | No se escribe ningún informe |

- **V** — Una ejecución que solo omite pruebas sale con código 0: el código de salida por sí solo no prueba nada.
  `TestRunSummary.Succeeded` exige `Passed > 0` y `ExitCode == 0` por eso.
- **V** — Con fallos (código 2) y sin pruebas (código 8) el informe se escribe.
- **NV** — Si el proceso de pruebas se cuelga y se mata por tiempo, o se cae (código 7), puede no haber informe, o quedar a
  medias. Los analizadores lanzan `TestReportException` ante un fichero ausente, truncado o mal formado, y el ejecutor de
  verificación debe tratarlo como paso fallido.
- **NV** — Una solución con varios proyectos de prueba y el mismo `--report-xunit-trx-filename` podría sobrescribir el
  informe; el entorno de pruebas de F3-8 tiene un solo proyecto. Usar una carpeta por proyecto si hiciera falta.

### Informe real de las pruebas de este repositorio

```text
dotnet test --no-build --results-directory <tmp>\s4\repo-results -- --filter-class "AgentStudio.Tests.Domain.EvidenceRecordTests" --filter-class "AgentStudio.Tests.Providers.Codex.CodexCliLiveTests" --report-xunit-trx --report-xunit-trx-filename repo-sample.trx --report-xunit-junit --report-xunit-junit-filename repo-sample-junit.xml
```

```text
Test run summary: Passed!
  total: 21
  failed: 0
  succeeded: 19
  skipped: 2
  duration: 2s 33ms
```

Las 2 omitidas son las pruebas en vivo de Codex, sin `AGENTSTUDIO_PROVIDER_TESTS=1`. Ese TRX (32 KB, sin recortar) y los
otros tres casos, junto con sus JUnit, están como fixtures en `tests/AgentStudio.Tests/Orchestration/Verification/Fixtures/`
(con su `README.md`).

### Conclusiones de S4

- **V** — Receta para el perfil de verificación: un paso `dotnet` con `Arguments = ["test", "--no-build", "--results-directory",
  "<carpeta>", "--", "--report-xunit-trx", "--report-xunit-trx-filename", "<nombre>.trx", "--zero-tests-policy", "strict"]`,
  `Report = Trx` y `ReportPath = <carpeta>\<nombre>.trx`. Variable de entorno `DOTNET_CLI_UI_LANGUAGE=en` para que la salida
  de consola que se guarde como evidencia esté en inglés (no se analiza).
- **V** — Hay que pasar `--report-xunit-trx`, no `--report-trx`. Si el proyecto de pruebas de un repositorio ajeno es de
  MSTest o NUnit, la bandera es otra (`--report-trx` con el paquete `Microsoft.Testing.Extensions.TrxReport`): el perfil es
  de confianza y lo escribe el usuario.
- **D** — Códigos de salida de MTP: 0 correcto, 1 error desconocido, 2 fallos, 3 abortada, 5 argumentos inválidos, 7 caída,
  8 ninguna prueba, 9 menos de las esperadas
  ([documentación](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-troubleshooting#exit-codes)).

## S5 — `git merge-tree --write-tree`, `commit-tree` y `update-ref`

Repositorio desechable con la rama `main` (commit `base`) y tres ramas hijas: `ta` (cambia la línea 1 de `shared.txt`, edita
`a.txt` y añade `a-only.txt`), `tb` (cambia la línea 8 de `shared.txt`, edita `b.txt` y añade `b-only.txt`) y `tc` (cambia la
línea 1 de `shared.txt` de otra forma que `ta`, y añade `c-only.txt`). Estado previo: `status` vacío, HEAD en `main`.

### Fusión limpia (`ta` + `tb`)

```text
$ git merge-tree --write-tree --name-only --messages ta tb
8ff46eac06e50ef535303dcbec4141eaf140058b

Auto-merging shared.txt
[exit 0]
```

- Con `-z` (NUL mostrado como `^@`): `8ff46eac...^@^@1^@shared.txt^@Auto-merging^@Auto-merging shared.txt\n^@`.
- Sin `--messages` una fusión limpia imprime solo el identificador del árbol.
- El árbol resultante existe en la base de objetos (`git cat-file -t` da `tree`) y contiene los cinco ficheros, con
  `shared.txt` fusionado (`linea 1 (A)` … `linea 8 (B)`).

### Fusión con conflicto (`ta` + `tc`)

```text
$ git merge-tree --write-tree --name-only --messages ta tc
69ce9a35be521deff07b102f256f41af3fc12d2d
shared.txt

Auto-merging shared.txt
CONFLICT (content): Merge conflict in shared.txt
[exit 1]
```

- Con `-z`: `69ce9a35...^@shared.txt^@^@1^@shared.txt^@Auto-merging^@Auto-merging shared.txt\n^@1^@shared.txt^@CONFLICT (contents)^@CONFLICT (content): Merge conflict in shared.txt\n^@`.
- Sin `--name-only` salen las etapas (`100644 <objeto> 1<TAB>shared.txt`, `2`, `3`).
- **V** — El árbol del conflicto se escribe igualmente y contiene marcadores (`<<<<<<< ta`, `=======`, `>>>>>>> tc`):
  **nunca se debe confirmar**. El código de conflicto de `MergeIntoAsync` no llama a `commit-tree`.

### Formato y códigos de salida

| Caso | Código | Salida |
|---|---|---|
| Limpia | 0 | Árbol, línea en blanco y mensajes (con `--messages`) |
| Conflicto | 1 | Árbol, rutas en conflicto, línea en blanco y mensajes |
| Revisión que no existe: `merge-tree --write-tree --name-only --messages ta no-existe` | **1** | Nada en `stdout`; en `stderr`: `merge-tree: no-existe - not something we can merge` |
| `merge-base --is-ancestor <a> <b>` | 0 si lo es, 1 si no, 128 si un identificador no es válido | `fatal: Not a valid object name no-existe` |

- **V** — El código 1 de `merge-tree` es ambiguo: significa conflicto, pero también ese error. Solo una salida cuya primera
  línea sea un identificador de árbol es un resultado de fusión. `GitPorcelain.TryParseMergeTree` lo comprueba y
  `MergeIntoAsync` lanza `GitCommandException` en el otro caso.
- **V** — Con `-z` la estructura es: árbol `NUL`, las rutas en conflicto (cada una con su `NUL`), un campo vacío y, por cada
  mensaje, `número de rutas NUL ruta... NUL tipo NUL texto NUL`. Las rutas con espacios o acentos salen sin comillas
  (la capa de git usa `-z`). **NV** — Según la documentación de git, esa estructura por mensaje es posterior a 2.38 y las
  versiones anteriores imprimen texto libre; esta máquina tiene 2.45.2. El analizador lee la estructura y, si no encaja,
  conserva el texto tal cual (hay un test con ese formato).

### `commit-tree` con dos padres y el mensaje por stdin

```text
$ printf '<mensaje>' | git -c user.name="Agent Studio" -c user.email=agent-studio@localhost -c commit.gpgsign=false commit-tree <arbol> -p <ta> -p <tb>
fbb90b99b68c543bd0eb2d23dbea0aa879b08096
$ git cat-file -p fbb90b99b68c543bd0eb2d23dbea0aa879b08096
tree 8ff46eac06e50ef535303dcbec4141eaf140058b
parent 4365a2507a803810c73289deb5f71cde88383bcf
parent a9508d030b05f27437276fb82d4ccbd58dc19963
author Agent Studio <agent-studio@localhost> 1792077480 +0200
committer Agent Studio <agent-studio@localhost> 1792077480 +0200

Integra ta + tb

Cuerpo con acentos: integración válida.
Agent-Studio-Attempt: T1/1
```

- **V** — `commit-tree` lee el mensaje de stdin si no se pasa `-m` ni `-F`, respeta los acentos (UTF-8) y el trailer, y la
  identidad de `-c user.name/user.email` basta.
- **V** — Idempotencia: tras fusionar, `merge-base --is-ancestor <ta> <fusión>` y `<tb> <fusión>` salen con 0, y con 1 para
  una rama no fusionada (`tc`).

### `update-ref` como compare-and-swap

```text
$ git update-ref refs/agentstudio/20261015-a1b2c3/_integration <ta> 0000000000000000000000000000000000000000
[exit 0]                       # crear solo si no existe
$ git update-ref refs/agentstudio/20261015-a1b2c3/_integration <tb> 0000000000000000000000000000000000000000
fatal: update_ref failed for ref '...': cannot lock ref '...': reference already exists
[exit 128]
$ git update-ref refs/agentstudio/20261015-a1b2c3/_integration <fusión> <ta>
[exit 0]                       # avanzar con el valor esperado correcto
$ git update-ref refs/agentstudio/20261015-a1b2c3/_integration <tc> <ta>
fatal: update_ref failed for ref '...': cannot lock ref '...': is at fbb90b99... but expected 4365a250...
[exit 128]                     # valor esperado obsoleto: la ref no se mueve
```

- **V** — El CAS falla con 128 si el valor esperado está obsoleto, y la ref conserva su valor. Con el identificador de ceros
  como valor esperado actúa como «crear solo si no existe».
- **V** — `git rev-parse --verify --quiet <ref>^{commit}` sale con 1 si la ref no existe.
- **V** — Todo ello no toca el repositorio de trabajo: `status`, HEAD y las refs que no son de `refs/agentstudio/` quedaron
  idénticos, y el único cambio fue la ref nueva.

### Hechos de `git worktree` que usa la capa de git

Comprobados en el mismo repositorio desechable con `git worktree add --detach --lock --reason "agent-studio:<proyecto>:<run>:T1"`:

- **V** — `git worktree list --porcelain -z` separa los atributos con NUL y las entradas con un campo vacío; la primera
  entrada es siempre el árbol principal (`worktree <ruta>`, `HEAD <id>`, `branch <ref>`); un worktree desprendido muestra
  `detached` y, si está bloqueado, `locked <motivo>`. Las rutas salen con `/` (`C:/Users/...`). Sin `-z` el motivo con
  comillas o saltos de línea sale entrecomillado (`locked "a\"b\nc"`); con `-z`, en bruto.
- **V** — Desde dentro de un worktree, `git rev-parse --show-toplevel` da la ruta del worktree y
  `--path-format=absolute --git-common-dir` da el `.git` del repositorio principal.
- **V** — `git worktree remove` sobre un worktree bloqueado falla (código 128) incluso con `--force`; hace falta
  `--force --force` o desbloquear antes. Un worktree con ficheros sin seguimiento exige `--force`.
- **V** — Un worktree bloqueado cuyo directorio desapareció no se poda con `git worktree prune`; hay que desbloquearlo antes.
- **V** — `git worktree add` sobre un directorio que ya existe y no está vacío falla (`already exists`); sobre uno vacío
  funciona.
- **V** — `git status --porcelain=v1 -z -- . ':(exclude).agents'` y `git add -A -- . ':(exclude).agents'` dejan fuera
  `.agents/`.

## Desviaciones del diseño y puntos para docs/13

1. **§5, conflictos.** El código de salida 1 de `merge-tree` no basta para detectar un conflicto (S5); se exige además un
   árbol en la primera línea.
2. **§5, formato.** La capa de git usa `-z` en `merge-tree`, `worktree list` y `status`/`diff`: las rutas con espacios,
   acentos o saltos de línea no se entrecomillan.
3. **§5, borrado.** `RemoveAsync` usa `worktree remove --force --force`: un solo paso que no deja el worktree desbloqueado
   (y por tanto sin prueba de propiedad) si falla a medias; la negativa por cambios sin confirmar la decide antes la propia
   capa.
4. **§3.4, `ListOwnedAsync` y `EnsureAsync`.** El motivo del bloqueo (`agent-studio:<proyecto>:<run>:<nombre>`) no puede
   llevar el commit base. Se guarda en la ref oculta `refs/agentstudio/<run>/_start/<nombre>` (el nombre `_start` no puede
   ser el de un worktree), y «coincide (base, motivo)» es una igualdad contra esa ref.
5. **§3.4, ref de integración.** `IWorktreeManager` no tiene forma de crear la rama `agentstudio/run-<run>` que es el destino de
   `MergeIntoAsync`; la clase concreta añade `EnsureRefAsync` (crear si no existe, fallar si apunta a otro commit).
6. **§8, perfil de verificación.** Para xunit.v3 la bandera es `--report-xunit-trx` (no `--report-trx`), conviene añadir
   `--zero-tests-policy strict`, y un paso que solo omite pruebas sale con 0.

## Revisión de seguridad de la capa git

Una revisión independiente reprodujo en repos desechables cómo un agente, que escribe lo que quiera dentro de su
worktree (también el fichero `.git`), conseguía que Agent Studio tocase el repo del usuario. Cada hallazgo tiene ahora
un test que lo reproduce (`tests/AgentStudio.Tests/Infrastructure/Git/`) y su arreglo. Todo **V** con git 2.45.2.

| Hallazgo | Arreglo |
|---|---|
| `.git` del worktree reescrito a `gitdir: <repo>/.git`: `CommitAllAsync` movía `main` y el índice del usuario, `CheckoutAsync` hacía `reset --hard` sobre `main` | El repo sale del proyecto (`WorktreeLease.RepositoryRoot`), nunca del worktree. La propiedad la prueba ese repo: `worktree list`, el motivo del bloqueo y la carpeta `worktrees/<id>` cuyo `gitdir` apunta de vuelta al worktree (también `locked` y `commondir`). Todo comando sobre el worktree lleva `--git-dir=<carpeta admin>` y `--work-tree=<ruta>` |
| `git worktree prune` podaba worktrees del usuario con la carpeta ausente un momento (USB) | Nunca `prune`: se quita solo el registro propio con `worktree remove --force --force <ruta tal como la lista git>` |
| Los hooks seguían corriendo (`--no-verify` solo salta pre-commit y commit-msg); un `core.hooksPath` relativo (husky) se resolvía dentro del worktree | `-c core.hooksPath=<raíz>\.no-hooks` en todos los comandos; si esa carpeta deja de estar vacía o es un enlace, git no se ejecuta |
| `worktree remove` con un fichero abierto: git borraba el registro y el bloqueo y dejaba la carpeta huérfana para siempre | Ver el hallazgo nuevo de abajo: la carpeta se aparta con un rename (todo o nada) antes de que git toque el registro. Una huérfana previa se borra solo si está en la ruta exacta de Agent Studio, ningún `gitdir` apunta a ella y se pide descartar |
| `reset --hard <sha>` sin `--`; `clean -f` conservaba repos anidados; `author.*`/`committer.*` de la configuración ganaban a `-c user.*` | `reset --hard <sha> --`, `clean -f -f -d -x`, y `-c author.*`/`-c committer.*` además de `-c user.*` |
| La ref de la tarea se sobrescribía sin CAS y `update-ref` seguía refs simbólicas | Toda escritura de ref es `update-ref --no-deref <ref> <nuevo> <esperado>`; una ref movida da `StaleRefException` |
| Variables heredadas que redirigen git | Se quitan también `GIT_CEILING_DIRECTORIES`, `GIT_CONFIG_COUNT` y cada `GIT_CONFIG_KEY_n`/`GIT_CONFIG_VALUE_n`. La configuración global se respeta (`core.autocrlf`) |
| Un worktree a medio crear se aceptaba para reutilizar | Solo se reutiliza si HEAD resuelve, existe su índice y la ref `_start` (escrita después de `worktree add`) registra la base; si no, falla cerrado y se arregla con `RemoveAsync(discardChanges: true)` |

**Hallazgos nuevos (V).** Una *junction* que el agente deja en su worktree (no hace falta ser administrador):

- `git worktree remove --force --force` la sigue y borra el contenido del destino. Por eso git ya nunca borra un
  worktree: Agent Studio lo mueve a `<raíz>\.trash\<guid>` y lo borra sin seguir enlaces, y git solo quita el registro
  de una ruta que ya no existe.
- En lugar de una carpeta versionada (`src` → `src` del usuario), `reset --hard` sobrescribió una edición sin confirmar
  del usuario y `clean -ffdx` borró un fichero suyo sin seguimiento. `CheckoutAsync` elimina antes, como enlaces, todos
  los del worktree (también en `.agents/`, que el aprovisionador copia y nunca enlaza).
- Además, ninguna operación acepta como repositorio una ruta dentro de la raíz de worktrees.

**Ficheros `*.lock`.** Agent Studio nunca borra un lock de git. Si un comando falla porque existe uno (git lo nombra
entre comillas, también traducido), se lanza `GitLockFileException` con la ruta del fichero: hay que comprobar que no
corre ningún git y borrarlo a mano.

**Comprobación de integridad.** `CaptureUserRepositoryStateAsync` captura, solo leyendo (`GIT_OPTIONAL_LOCKS=0`), HEAD,
`refs/heads/*` salvo `agentstudio/*`, `refs/tags/*`, `refs/stash`, un hash del índice y las líneas de `status`; el motor
la compara antes y después de cada ejecución de un agente con `UserRepositoryState.Compare`. Dos decisiones:

- El hash del índice es el de `git ls-files --stage -v` (lo preparado y las marcas assume-unchanged/skip-worktree), no
  el del fichero: un `git status` del usuario o de su IDE lo reescribe para refrescar la caché y daría falsos positivos.
- `status` lista cada fichero sin seguimiento (`--untracked-files=all`), así que se ve uno nuevo dentro de una carpeta
  sin seguimiento. Límite conocido: editar otra vez un fichero que ya estaba modificado no cambia su línea y no se
  detecta.
