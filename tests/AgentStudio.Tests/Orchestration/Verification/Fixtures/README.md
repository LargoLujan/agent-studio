# Informes de pruebas reales (fixtures)

Salidas reales de `dotnet test` con xunit.v3 4.0.2 sobre Microsoft.Testing.Platform, capturadas el 2026-10-15. Las
usan los tests de `TrxReportParser` y `JUnitReportParser`. Cómo se obtienen y qué hay que pasar exactamente está en
`docs/investigacion/2026-10-15-spikes-fase-3.md` (spike S4).

Se han sustituido los datos personales: el usuario pasa a `usuario`, la máquina a `EQUIPO` y las rutas a `C:\repo\…`
o `C:\proyecto\…`. Nada más se ha tocado: no hay recortes ni contadores editados.

| Fichero | Origen | Contenido |
|---|---|---|
| `xunit-v3-4.0.2/repo-sample.trx` y `.junit.xml` | Tests de este repositorio: `--filter-class AgentStudio.Tests.Domain.EvidenceRecordTests --filter-class AgentStudio.Tests.Providers.Codex.CodexCliLiveTests` | 21 pruebas: 19 correctas y 2 omitidas (las pruebas en vivo de Codex, sin `AGENTSTUDIO_PROVIDER_TESTS=1`) |
| `xunit-v3-4.0.2/mixed.trx` y `.junit.xml` | Proyecto desechable de la spike S4 (una correcta, una con fallo, una omitida y una teoría de dos filas) | 3 correctas, 1 fallida, 1 omitida; `dotnet test` sale con 2 |
| `xunit-v3-4.0.2/zero-tests.trx` y `.junit.xml` | El mismo proyecto con `--filter-class NoExiste` | Ninguna prueba; `dotnet test` sale con 8 y aun así escribe los dos informes |
| `xunit-v3-4.0.2/skipped-only.trx` y `.junit.xml` | El mismo proyecto con `--filter-method Tiny.Tests.TinyTests.Skipped` | Una prueba omitida y ninguna ejecutada; `dotnet test` sale con **0** |

Comando de captura (el idioma de la consola no influye en los informes):

```text
dotnet test --no-build --results-directory <carpeta> -- <filtros> --report-xunit-trx --report-xunit-trx-filename <nombre>.trx --report-xunit-junit --report-xunit-junit-filename <nombre>.xml
```

Para regenerarlos con otra versión de xunit.v3, repite la captura y añade una carpeta `xunit-v3-<versión>/`: así un cambio
de formato rompe un test en lugar de una verificación real.

El proyecto de tests aún no copia estos ficheros a la carpeta de salida (no se ha podido tocar el `.csproj`); los tests los
localizan desde el código fuente con `[CallerFilePath]` (`ReportTestSupport.Fixture`). Cuando P0 añada el glob de fixtures,
bastará con cambiar ese método.
