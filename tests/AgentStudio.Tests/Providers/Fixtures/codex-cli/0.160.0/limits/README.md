# Capturas SINTÉTICAS de Codex CLI 0.160.0: finales clasificados (Fase 8, D-41)

> **No proceden de una ejecución real.** Se construyeron a mano el 2026-10-21 a partir del código de `rust-v0.160.0`, como la
> captura sintética de la Fase 4 (`../../../../Codex/Synthetic/README.md`). Sirven para que `CodexExecJsonParser` reconozca
> la clase del final de un intento (docs/16 D-41, criterio F8-1) sin adivinar: cada fragmento del que deduce una clase está
> en una de estas capturas, y `CodexEndClassTests` lo comprueba. Cuando exista la captura real de un caso, irá a `../` con su
> `.exit` y su `.stderr`, y la sintética se borra.

Fuentes, en <https://github.com/openai/codex/tree/rust-v0.160.0>, consultadas el 2026-10-21:

- `codex-rs/exec/src/exec_events.rs`: la forma de las líneas. `turn.failed` lleva `{"error":{"message":…}}` y `error`, un
  `message`: solo texto, sin un código del error.
- `codex-rs/exec/src/event_processor_with_jsonl_output.rs`: el `message` de `turn.failed` es el del error del turno (con
  ` (<detalles>)` detrás si los hay), o el del último `error`, o `turn failed`.
- `codex-rs/protocol/src/error.rs`: el texto de cada error (`#[error("…")]` y `UsageLimitReachedError::fmt`).
- `codex-rs/login/src/auth/manager.rs`: los textos de la sesión que no se pudo renovar (`REFRESH_TOKEN_*_MESSAGE`).

La forma de las líneas es la de las capturas reales de `../` (`thread.started`, `turn.started`), sin los avisos de
configuración de la máquina de desarrollo. Inventado: el `thread_id` (`00000000-…`) y, donde el error lleva un detalle que
Codex compone en tiempo de ejecución (`{0}` en `error.rs`), el texto `SINTETICO`. La hora del límite de uso («Try again at
3:04 PM.») tiene el formato de `error.rs` (`%-I:%M %p`), y el analizador no la lee: en `codex exec` el reinicio no se conoce
y la espera es la progresiva, «estimada» (docs/04 §18.1). Ninguna captura tiene `.exit` ni `.stderr`.

| Fichero | Error de 0.160.0 | Texto (de `error.rs` o `manager.rs`) | Fragmento que clasifica | Clase |
|---|---|---|---|---|
| `01-limite-de-uso.jsonl` | `UsageLimitReached` (sin plan) | «You’ve hit your usage limit. Try again at 3:04 PM.» | `hit your usage limit` | `WindowQuota` (sin tipo ni reinicio) |
| `02-contexto-agotado.jsonl` | `ContextWindowExceeded` | «Codex ran out of room in the model's context window. Start a new thread or clear earlier history before retrying.» | `ran out of room in the model's context window` | `ContextExhausted` |
| `03-sin-capacidad.jsonl` | `ServerOverloaded` | «Selected model is at capacity. Please try a different model.» | `Selected model is at capacity` | `RateOrOverload` |
| `04-limite-de-velocidad.jsonl` | `RateLimitExceeded` | «rate limit exceeded: {0}» | `rate limit exceeded:` | `RateOrOverload` |
| `05-reintentos-agotados.jsonl` | `RetryLimit` tras un 429 | «exceeded retry limit, last status: {}» con el estado `429 Too Many Requests` | `exceeded retry limit, last status: 429` | `RateOrOverload` |
| `06-conexion-fallida.jsonl` | `ConnectionFailed` | «Connection failed: {}» | `Connection failed:` | `Network` |
| `07-tiempo-agotado.jsonl` | `RequestTimeout` | «request timed out» | `request timed out` | `Network` |
| `08-respuesta-interrumpida.jsonl` | `ResponseStreamFailed` | «Error while reading the server response: {}» | `Error while reading the server response:` | `Network` |
| `09-stream-desconectado.jsonl` | `Stream` | «stream disconnected before completion: {0}» | `stream disconnected before completion:` | `Network` |
| `10-filtro-de-contenido.jsonl` | `ContentFilter` | «stream disconnected before completion: Incomplete response returned, reason: content_filter» | `reason: content_filter` (se mira antes que la red) | `Other` |
| `11-sesion-caducada.jsonl` | `RefreshTokenFailed` (`Expired`) | «Your access token could not be refreshed because your refresh token has expired. Please log out and sign in again.» | `Your access token could not be refreshed` (común a los cuatro motivos) | `SignedOut` |

Se dejan en «otra», a propósito, los errores de uso que no son una ventana, porque esperar no los arregla: los créditos o el
tope de gasto de un workspace («Your workspace is out of credits…», «You hit your spend cap…»), «Quota exceeded. Check your
plan and billing details.» y «To use Codex with your ChatGPT plan, upgrade to Plus…». También «We’re currently experiencing
high demand…» (`InternalServerError`): `error.rs` no la trata como sobrecarga.

Falta medir en vivo (con el OK del usuario): todo lo de esta tabla. En particular, si el límite de uso llega a `codex exec`
como `turn.failed` o solo en la salida de error, con qué código de salida; si una sesión caducada sale en el JSONL o solo
en la salida de error; y qué detalle trae cada error con `{0}`.
