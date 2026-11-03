# Fixtures SINTÉTICAS de Codex CLI (derivadas del código, NO capturadas)

`0.160.0/05-rate-limits.jsonl` es la salida estándar que daría `codex app-server --listen stdio://` a los tres mensajes
de Agent Studio (docs/14, D-15): la respuesta a `initialize` (id 1), una notificación y la respuesta de
`account/rateLimits/read` (id 2). Se construyó a mano a partir de los tipos de `rust-v0.160.0`
(`codex-rs/app-server-protocol/src/protocol/v2/account.rs`: `GetAccountRateLimitsResponse`, `RateLimitSnapshot`,
`RateLimitWindow`, `CreditsSnapshot`, `RateLimitReachedType`; `codex-rs/protocol/src/account.rs`: `PlanType`) y del
ejemplo de la documentación del app-server (buckets `codex` y `codex_other`). La forma de la respuesta a `initialize` no se
comprobó: el analizador no la lee.

Los valores de `accountId`, del saldo de `credits`, de `rateLimitUpsell` y de `rateLimitResetCredits` son inventados y
llevan la marca `SINTETICO`: el test comprueba que ninguno sale del analizador.

No procede de una ejecución real. Cuando exista la captura de la lectura en vivo (`CodexQuotaLiveTests`, con el OK del
usuario), irá a `Fixtures/codex-cli/<versión>/05-rate-limits.jsonl`, con `accountId`, saldos y textos del backend ya
sustituidos por el propio test, y `CodexRateLimitsParserTests` la leerá también.
