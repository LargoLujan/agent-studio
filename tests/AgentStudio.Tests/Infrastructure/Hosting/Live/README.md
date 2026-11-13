# Prueba en vivo de GitHub por `gh`: guion

Escrita, **no ejecutada**. Hace falta el OK del propietario (`docs/21` decisión 14) y un **repositorio privado de prueba que cree él**. Va con
`Category=ProviderLive`, fuera de la suite y de la CI. Sin las variables de abajo, cada prueba de `GitHubLiveTests.cs` se salta.

```text
AGENTSTUDIO_PROVIDER_TESTS=1
AGENTSTUDIO_HOSTING_LIVE_REPO=<propietario>/<repositorio>      # el privado de prueba
dotnet test --project tests/AgentStudio.Tests -- --filter-trait "Category=ProviderLive" --filter-class "*GitHubLiveTests"
```

Para las pruebas que miran o escriben en un PR: `AGENTSTUDIO_HOSTING_LIVE_PR=<número>` y, para escribir (solo un estado y un comentario, borrables),
`AGENTSTUDIO_HOSTING_LIVE_WRITE=1` y `AGENTSTUDIO_HOSTING_LIVE_HEAD=<commit de cabeza de ese PR>`. Con `AGENTSTUDIO_CAPTURE_DIR` se guardan las notas
(`evidence.log`): solo líneas de estado, nombres de cabeceras, códigos de salida y recuentos; nunca un cuerpo, una cuenta ni un token.

## Lo primero que hay que medir (por este orden)

1. **¿`gh api -i` deja leer la línea de estado de un 304, y con qué código de salida?** Es de lo que depende todo el sondeo condicional
   (`docs/21` D-12 y §13). La primera prueba (`The_status_line_of_a_304_can_be_read_and_so_is_its_exit_code`) lee el repositorio, lo vuelve a leer con
   su `ETag` y anota las dos salidas. Se espera: línea `HTTP/2.0 304 Not Modified` y cabeceras en la salida estándar, código distinto de 0 y
   `gh: HTTP 304` en la estándar de error. **Si la línea de estado no sale, no hay 304 legible y el sondeo pasa a comparar huellas sin ahorro.**
2. Si con un 4xx o un 5xx se imprime el cuerpo y por dónde (la prueba de arriba no lo mide: probar a mano un PR que no existe desde la prueba del paso 4).
3. Que `If-None-Match` pasado con `-H` llega tal cual y `gh` no añade su propia caché (el 304 de arriba lo demuestra).
4. Que con ruta completa y `--hostname` `gh` **no lanza `git`** ni lee el repositorio de la carpeta de trabajo (la carpeta de las pruebas es la neutra; mirar con
   el Monitor de procesos que no nace ningún `git`).
5. Que con la lista blanca de `docs/21` D-11 `gh` encuentra su sesión (la segunda prueba lo exige: si falla, el motivo dice cuál).
6. Qué imprime sin conexión (desconectar la red y repetir el paso 1) y con qué código termina; y con la sesión cerrada (código 4 esperado).
7. Desde qué versión existen `--input`, `-i` y el código de salida 4 (probar con una versión de `gh` más antigua si el propietario la tiene).

## Después, con un PR del repositorio de prueba

| Paso | Qué se hace | Qué se anota |
|---|---|---|
| Observar | `A_pull_request_is_observed_and_the_second_round_is_not_modified` con un PR abierto | Cuántas de las 4 lecturas de la segunda vuelta dan 304; si `x-poll-interval` llega; que un 304 no gasta el límite (`x-ratelimit-remaining` antes y después) |
| Veredicto | `The_verdict_is_published_as_a_status_and_a_tagged_comment_and_edited_in_place` | El permiso que pide `POST …/statuses/{sha}`; el tope de la descripción (no está documentado); que el comentario se edita en su sitio |
| Reglas | Activar a mano un conjunto de reglas y la protección clásica en la rama base | Qué trae `rules/branches/{rama}` y el campo `protection` de `branches/{rama}` sin ser administrador |
| Fusiones | Fusionar tres PR a mano, con **squash**, **rebase** y **commit de fusión**, y observar cada uno | `merge_commit_sha` (cambia de significado según el estado), el modo deducido contra el real, los commits que deja un rebase |
| Revert | Revertir uno desde la web y observarlo | Que el mensaje lleva `This reverts commit <sha>.` y que la ventana de una hora hacia atrás lo encuentra |
| Línea de tiempo | Un force-push a la rama de un PR | Qué punto trae `head_ref_force_pushed` (`issues/{n}/timeline` o `…/events`) y con qué campos; `head_ref_deleted` al borrar la rama |
| Despliegues | Un flujo que cree un despliegue para el commit fusionado | `deployments?sha=` y los estados de un despliegue |
| Cierre | Cerrar otro PR y borrar su rama `agentstudio/run-…` | Que la barra de la rama va literal en `git/refs/heads/{rama}` y en `branches/{rama}` |
| Límite | Anotar `x-ratelimit-*` y `retry-after` de un 403 o 429 si se llega a uno (no provocarlo) | Los nombres y formatos de las cabeceras |

Con lo medido, las respuestas de `GitHub/Fixtures/gh-2.96.0/handmade-*` se sustituyen por capturas con el prefijo `real-`, revisadas para que no
lleven nada del propietario, y `docs/21` §13 se actualiza.
