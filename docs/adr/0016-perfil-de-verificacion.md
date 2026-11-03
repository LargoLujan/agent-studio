# ADR-0016 · Perfil de verificación de confianza, congelado por ejecución; resultados solo de TRX o JUnit

- **Estado:** aceptada · 2026-10-16. Recoge la §4.5 y la §5 de [13](../13-fase-3-orquestacion.md) y el spike S4
  ([investigación](../investigacion/2026-10-15-spikes-fase-3.md)). Lo implementa `Orchestration/Verification`
  (`VerificationRunner`, `VerificationProfileFile`, `VerificationRules`, `DotNetVerificationProfile`).
- **Contexto:**
  - F3-5 pide que las evidencias de build y tests las capture el sistema, con su hash; F3-2, que la integración se
    verifique con build y tests.
  - Si un plan o un agente pudieran decidir qué comando se ejecuta, la verificación sería una vía para ejecutar código
    cualquiera y para fabricar evidencia (riesgo R4).
  - La consola de `dotnet test` sobre Microsoft.Testing.Platform está traducida («correcto: N»), y su código de salida no
    basta: con todas las pruebas omitidas sale con 0 (S4).
  - En Windows, los nodos de MSBuild y el servidor de compilación siguen vivos tras el build y retienen ficheros del
    worktree.
- **Decisión:**
  1. **Perfil de confianza.** Los pasos (`VerificationStep`: id, título, ejecutable, argumentos, tipo de evidencia,
     formato y ruta del informe, tiempo máximo) están en `<raíz>\.agentstudio\verification.json`.
     - Lo escribe el usuario, desde la UI, y debe estar excluido de git.
     - Se lee de la raíz del proyecto, nunca de un worktree.
     - El plan solo cita los pasos por su id, y ningún agente puede cambiarlos.
  2. **Congelado por ejecución.** Al empezar, el motor copia el perfil cargado en `orchestration_runs`
     (`RunRecord.Verification`), y su `ContentHash` identifica los comandos exactos. Cambiar el fichero después no
     afecta a una ejecución en curso ni a su reanudación.
  3. **Formato estricto, versión 1.**
     - JSON sin comentarios, sin comas finales, sin propiedades desconocidas, con los enums por nombre y sin valores
       obligatorios ausentes o nulos; UTF-8 y 256 KB como mucho.
     - Reglas (`VerificationRules`): ids con la forma de un id de tarea y únicos sin distinguir mayúsculas; título y
       ejecutable; argumentos sin caracteres de control, porque el texto libre nunca va en una línea de órdenes; tipo
       `BuildLog`, `TestRun` o `CommandOutput`; un paso es `TestRun` si y solo si lee un informe TRX o JUnit, y entonces
       tiene ruta; tiempo máximo mayor que cero y de 24 h como mucho, en segundos enteros.
     - Las mismas reglas se comprueban al guardar y otra vez en el ejecutor antes de lanzar nada.
  4. **Nada se aplica en silencio.** Sin fichero, el proyecto no tiene perfil y la carga lo dice
     (`VerificationProfileStatus.NotFound`). `DotNetVerificationProfile.Propose()` propone uno para .NET con xUnit v3:
     - `dotnet build`;
     - `dotnet test --no-build --results-directory {results} -- --report-xunit-trx --report-xunit-trx-filename test.trx --zero-tests-policy strict`,
       con el informe en `{results}/test.trx`.

     Solo es el perfil cuando el usuario lo guarda.
  5. **Solo TRX o JUnit, nunca la consola.** Un paso de pruebas se juzga por su informe (`TrxReportParser`,
     `JUnitReportParser`).
     - `TestRunSummary.Succeeded` exige código 0, ningún fallo y al menos una prueba correcta: cero pruebas, o todas
       omitidas, no es un éxito.
     - Un informe ausente, truncado o mal formado hace fallar el paso, y se registra como 0 correctas, 0 fallidas y
       0 omitidas, con el motivo en la evidencia.
     - El informe que hubiera en la ruta antes del paso se borra antes de lanzarlo, para que solo cuente el de esta
       ejecución.
  6. **`{results}`.** En los argumentos y en la ruta del informe es una carpeta nueva y vacía, fuera del worktree, que se
     borra al acabar el paso. Así los informes nunca caen en el worktree, donde el siguiente commit del sistema los
     recogería, y dos verificaciones en paralelo nunca comparten carpeta.
  7. **Entorno fijo de cada paso.**
     - `MSBUILDDISABLENODEREUSE=1`, `DOTNET_CLI_USE_MSBUILD_SERVER=0` y `UseSharedCompilation=false`, para que ningún nodo
       de MSBuild ni servidor de compilación siga reteniendo ficheros del worktree.
     - `DOTNET_CLI_UI_LANGUAGE=en`, para que la consola capturada esté en inglés. Se guarda, pero no se interpreta.
  8. **Ejecución.**
     - Por `IStreamingProcessRunner`, que mata el árbol de procesos y lo ata al Job Object. Nada va por la entrada
       estándar.
     - Cada paso tiene su tiempo máximo, contado desde que se lanza: al agotarse se mata el árbol y el resultado queda
       como `TimedOut`, con código -1.
     - Los pasos van en orden y la ejecución se para en el primero que no pasa: un `--no-build` tras un build fallido
       probaría binarios viejos.
     - La salida completa (stdout y stderr marcados línea a línea, una cabecera con el comando y un pie con el código, el
       tiempo y el resumen del informe) se guarda como evidencia `BuildLog` o `TestRun`, con origen `CapturedBySystem`,
       y el hash lo calcula el almacén.
     - Cancelar mata el paso en curso y no guarda nada de él.
- **Alternativas:**
  - Leer la consola: está traducida y engaña (sale con 0 con todo omitido).
  - Comandos en el plan: una vía de inyección desde el repositorio y el agente hacia la máquina (R4).
  - Aplicar un perfil por defecto sin preguntar: el usuario no sabría qué se ejecuta en su máquina.
  - `--report-trx` (paquete `Microsoft.Testing.Extensions.TrxReport`): xUnit v3 no lo trae, y la ejecución acaba con el
    código 5 sin ejecutar ninguna prueba.
  - Informes dentro del worktree (`TestResults/`): el siguiente commit del sistema los recogería, y un agente podría
    dejar uno preparado.
- **Consecuencias:**
  - Un repositorio con MSTest, NUnit u otro lenguaje necesita su propio perfil: lo escribe el usuario.
  - Con varios proyectos de pruebas y el mismo `--report-xunit-trx-filename`, el informe puede sobrescribirse. El código
    de salida sigue protegiendo, pero los recuentos serían los del último proyecto; un paso por proyecto lo evita.
  - **Riesgo abierto:** un repositorio clonado podría traer un `.agentstudio/verification.json` versionado, que se
    cargaría como si fuera de confianza. Por eso la apertura del proyecto debe añadir el fichero a `.git/info/exclude`
    (hoy añade `.agentstudio/*.db*`, `evidence/` y `tmp/`), y la UI debe enseñar el perfil y su hash antes de la primera
    ejecución.
  - Un test que el propio agente escribió puede hacer cualquier cosa mientras se ejecuta, también escribir un informe:
    la verificación demuestra qué pasó en la máquina, no que el código sea correcto. Por eso hay juez.

## Adenda 2026-10-16 · Aislamiento (ADR-0017)

El riesgo del último punto —que el código verificado escriba donde escribe el usuario— lo cierra
[ADR-0017](0017-verificacion-aislada-baja-integridad.md): los pasos de verificación corren **aislados bajo integridad
Low por defecto**, sobre una copia del commit, no en el worktree. Cambios en el perfil y su contrato:

- **Campo `isolation` por paso.** `VerificationStep` gana `ProcessIsolation Isolation` (`LowIntegrity` por defecto,
  `None` para apagarlo). En el fichero, un paso sin el campo es `LowIntegrity`; solo `"isolation": "none"` (cualquier
  caja) lo apaga, y cualquier otro valor es un error (`VerificationRules`), nunca un «none» en silencio. Un paso `None`
  se ejecuta como se describe arriba (en el worktree, con los permisos del usuario); uno aislado, en una copia Low del
  commit.
- **Hash del perfil.** `ContentHash` solo añade un campo por el paso que **no** está aislado, así que el hash de un
  perfil que nunca apaga el aislamiento es el mismo que antes de que el aislamiento existiera. El perfil congelado en
  `orchestration_runs` guarda el campo en los pasos `none`, y reanudar exige que su hash sea el de `RunStarted` (ver las
  consecuencias de ADR-0017).
- **`{results}` y entorno.** En un paso aislado, la carpeta que reemplaza `{results}` está **dentro** del área Low (no en
  la *scratch*), y el entorno del paso añade las carpetas Low de NuGet (`NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH`,
  `NUGET_PLUGINS_CACHE_PATH`), `DOTNET_CLI_HOME` y TEMP/TMP, sobre el entorno fijo de este ADR.
- **Revisión del plan.** La línea que el usuario lee antes de aprobar (`VerificationStepText.Line`) marca «SIN
  AISLAMIENTO» en los pasos que lo apagan.
