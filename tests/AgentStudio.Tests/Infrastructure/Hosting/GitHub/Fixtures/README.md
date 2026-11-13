# Respuestas de `gh api -i` de las pruebas del adaptador de GitHub

**Escritas a mano, no grabadas; pendientes de una captura real con el OK del propietario.** Ninguna sale de una ejecución de `gh`: en esta
máquina solo se ejecutó `gh --version` y los textos de ayuda (`gh api --help`, `gh help exit-codes`), por la regla del propietario de
`CLAUDE.md` (nada de `gh auth`, nada de `gh api <ruta>`, nada que use red, cuenta o token). Prueban que el adaptador lanza la orden que
debe y que interpreta bien la forma que **creemos** que tiene la respuesta; **no prueban que GitHub responda así.**

`gh-2.96.0/` tiene la forma que da `gh api -i`: la línea de estado (`HTTP/2.0 200 OK`), las cabeceras, una línea en blanco y el cuerpo.
Los ficheros llevan el prefijo `handmade-`. Cuando el propietario haga la prueba en vivo (`Live/GitHubLiveTests.cs`, categoría
`ProviderLive`), se sustituyen por capturas con el prefijo `real-`, revisadas para que no lleven nada suyo (cuenta, correos, identificadores).

## De dónde sale cada forma

Del apartado de la API REST de GitHub que se nombra (docs.github.com/en/rest, versión de la API `2022-11-28`), **de memoria de esa
documentación: no se volvió a descargar para escribir estos ficheros.** Un campo que no estuviera en la documentación real es un fallo de la
fixture, no del adaptador.

| Fichero | Página de la documentación | Qué lleva de interesante |
|---|---|---|
| `handmade-user-200.http` | Users › Get the authenticated user | `login`; un `ETag` débil |
| `handmade-repo-200.http` | Repos › Get a repository | `default_branch`, `allow_merge_commit`/`allow_squash_merge`/`allow_rebase_merge`, `permissions.push`, `archived` |
| `handmade-304.http` | Buenas prácticas (peticiones condicionales) | Un 304 sin cuerpo; `gh` sale con código distinto de 0 |
| `handmade-rules-200.http` | Repos › Rules › Get rules for a branch | `pull_request` (con su recuento), `required_status_checks`, `required_signatures`, `deletion` |
| `handmade-branch-200.http` | Branches › Get a branch | `commit.sha`, `protected`, `protection.required_status_checks` |
| `handmade-pulls-list-200.http` | Pulls › List pull requests (`head=`) | Uno cerrado y otro abierto de la misma rama |
| `handmade-pull-open-200.http` | Pulls › Get a pull request | `merged: false` con un `merge_commit_sha` (un commit de prueba que no se lee) |
| `handmade-pull-merged-200.http` | Pulls › Get a pull request | `merged: true`, `merge_commit_sha`, `merged_by.login` |
| `handmade-reviews-200.http` | Pulls › Reviews › List reviews | `COMMENTED`, `APPROVED`, `CHANGES_REQUESTED` y `PENDING`; una persona con dos revisiones |
| `handmade-check-runs-200.http` | Checks › List check runs for a Git reference | `total_count`; un check que pasa, otro que falla y otro que espera |
| `handmade-combined-status-200.http` | Commits › Get the combined status | Un estado de otro sistema y el del veredicto de Agent Studio |
| `handmade-merge-200.http` / `-405` / `-409` | Pulls › Merge a pull request | El acierto, «no se puede fusionar» y «la cabeza cambió» |
| `handmade-rate-limit-403.http` / `-429` | Rate limits | `x-ratelimit-remaining: 0` con `x-ratelimit-reset`, y `retry-after` |
| `handmade-unauthorized-401.http` | Autenticación | «Bad credentials» |
| `handmade-statuses-201.http` | Commits › Create a commit status | El estado del veredicto |
| `handmade-comment-201.http` | Issues › Comments › Create an issue comment | El `id` del comentario |
| `handmade-open-pr-201.http` | Pulls › Create a pull request | `number` |
| `handmade-timeline-200.http` | Issues › Timeline | `head_ref_force_pushed` (que punto lo trae es una de las cosas **sin verificar**, `docs/21` §13) |
| `handmade-deployments-200.http`, `handmade-deployment-statuses-200.http` | Deployments | Dos despliegues de un commit y sus estados |

Las fechas de `x-ratelimit-reset` (`1794571200` y `1794574800`) son las 12:00 y las 13:00 UTC del 13 de noviembre de 2026: las pruebas
usan un reloj propio que marca las 12:00 de ese día.
