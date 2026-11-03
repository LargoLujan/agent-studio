# Capturas SINTÉTICAS de Claude Code 2.1.290: finales clasificados (Fase 8, D-41)

> **No proceden de una ejecución real.** Se construyeron a mano el 2026-10-21 con la forma de las capturas reales de esta
> carpeta (`../01-texto.jsonl` a `../04-skill-plugin.jsonl`) y los campos y valores que documenta Claude Code. Sirven para
> que `ClaudeStreamJsonParser` reconozca la clase del final de un intento (docs/16 D-41, criterio F8-1) sin adivinar:
> cada señal de la que deduce una clase está en una de estas capturas, y `ClaudeEndClassTests` lo comprueba. Cuando exista
> la captura real de un caso, irá a `../` con su `.exit` y su `.stderr`, y la sintética se borra.

Fuentes, consultadas el 2026-10-21:

- Referencia del Agent SDK para TypeScript (<https://code.claude.com/docs/en/agent-sdk/typescript>): `SDKRateLimitEvent`
  (`status`: `allowed`, `allowed_warning` o `rejected`, y `resetsAt`), `SDKAssistantMessage.error` y sus valores
  (`authentication_failed`, `rate_limit` «a 429 against your quota», `overloaded` «a 529 at server capacity»…),
  `SDKResultMessage` (los subtipos `error_max_turns` y `error_max_budget_usd`, con `errors: string[]`; `api_error_status`,
  «the HTTP status code of the API error that terminated the conversation»; y `terminal_reason`, uno de cuyos valores es
  `prompt_too_long`).
- Errores de Claude Code (<https://code.claude.com/docs/en/errors>): los textos de cada caso; que en modo no interactivo
  (`-p`) una sesión caducada lleva el código estructurado `authentication_failed`; que el límite del plan se distingue de
  una limitación pasajera por las cabeceras de cuota unificada que trae la respuesta (lo que el CLI refleja en
  `rate_limit_event`); y que los textos de red cambiaron en 2.1.227.
- De las capturas reales: la forma de `rate_limit_event` con `rateLimitType` (`five_hour`), `unifiedWindows` y `resetsAt` en
  segundos Unix; que `result` trae `terminal_reason` (`completed`) y `api_error_status` (`null`) en 2.1.290.

Qué es inventado: los identificadores (`00000000-…`, `msg_sintetico_…`), la carpeta de trabajo y los campos que el analizador
no lee (`is_error` de los subtipos de presupuesto, `num_turns`). Se omiten las cifras de consumo (`usage`, `modelUsage`,
`total_cost_usd`): si un intento rechazado por la cuota gasta algo está sin medir (docs/16 §8). Ninguna captura tiene `.exit`
ni `.stderr`: el código de salida de cada caso tampoco está medido.

| Fichero | Caso | Señal de la clase | Fuente de la señal | Clase |
|---|---|---|---|---|
| `01-ventana-cinco-horas-rechazada.jsonl` | Ventana de cinco horas agotada | `rate_limit_event` con `"status":"rejected"`, `rateLimitType` `five_hour` y su `resetsAt`; el `result` con `api_error_status` 429 | `rejected` y 429: documentación; la forma del evento y `five_hour`: captura real `04-skill-plugin` | `WindowQuota` (`five_hour`, reinicio 1792084800) |
| `02-ventana-semanal-rechazada.jsonl` | Ventana semanal agotada | Igual, con `rateLimitType` `seven_day` | `seven_day` es la clave de la ventana semanal en `unifiedWindows` de las capturas reales; como valor de `rateLimitType` está sin medir | `WindowQuota` (`seven_day`, reinicio 1792350000) |
| `03-max-turnos.jsonl` | Tope de turnos | `"subtype":"error_max_turns"` | Documentación | `AttemptBudget` (`Turns`) |
| `04-max-presupuesto.jsonl` | Tope de coste | `"subtype":"error_max_budget_usd"` | Documentación | `AttemptBudget` (`Cost`) |
| `05-contexto-agotado.jsonl` | Contexto lleno | `"terminal_reason":"prompt_too_long"` | Documentación (el campo existe en 2.1.290: captura real) | `ContextExhausted` |
| `06-sesion-caducada.jsonl` | Sesión caducada | `assistant` del bucle principal con `"error":"authentication_failed"` | Documentación: el código estructurado de `-p` | `SignedOut` |
| `07-sobrecarga.jsonl` | API sin capacidad | `"error":"overloaded"` y `"api_error_status":529` | Documentación | `RateOrOverload` |
| `08-red-sin-conexion.jsonl` | Sin conexión con la API | `result` con «Unable to connect to API» | Documentación (texto para 2.1.227 y posteriores) | `Network` |
| `09-red-tiempo-agotado.jsonl` | La conexión no respondió a tiempo | `result` con «Request timed out» | Documentación (texto para 2.1.227 y posteriores) | `Network` |

Falta medir en vivo (con el OK del usuario): todo lo de esta tabla. En particular, si en `-p` el código estructurado llega
en `assistant.error` o solo como texto en `result`; si `rate_limit_event` sale con `rejected` antes del `result` de un 429;
los valores reales de `rateLimitType` además de `five_hour`; el `api_error_status` y el `terminal_reason` de un contexto
agotado; el texto exacto que deja en `result` un fallo de red; y si un intento rechazado gasta algo.
