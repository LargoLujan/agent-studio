# ADR-0009 · Google aparcado como proveedor

- **Estado:** aceptada · 2026-10-15. Se revisa si el usuario aporta una API key de Google o si Google cambia sus
  condiciones.
- **Contexto:**
  - Al iniciar sesión con su cuenta personal en Gemini CLI 0.64.0, el usuario obtuvo: «This client is no longer
    supported for Gemini Code Assist for individuals. To continue using Gemini, please migrate to the Antigravity
    suite of products». Google dejó de servir Gemini CLI a las cuentas individuales (gratuitas, AI Pro y Ultra) el
    18-06-2026; solo sigue funcionando con API key o licencias de empresa.
  - Su sucesor, Antigravity CLI (`agy` 1.3.5, instalado el 2026-10-15 con OK del usuario), tiene modo headless, pero
    sus condiciones lo excluyen para este caso:
    - FAQ (https://antigravity.google/docs/faq/): «Wrapping agy inside third-party coding agents or applications, or
      automating account rotation, is not supported under consumer plans».
    - Términos, §6 (https://antigravity.google/terms/): «Using third party software, tools, or services to access the
      Service … is a breach of this Agreement. Such actions may be grounds for suspension or termination of your
      Antigravity and/or Gemini CLI accounts».
  - Para herramientas de terceros, Google recomienda una API key de Google AI Studio o Gemini Enterprise.
- **Decisión** (del usuario):
  1. **Google queda aparcado.** Agent Studio trabaja con Claude Code (Anthropic) y Codex CLI (OpenAI). El juez
     independiente solo exige un proveedor distinto del ejecutor (ADR-0006), y dos bastan.
  2. **No se construye un adaptador de `agy` sobre una cuenta de consumo**, porque arriesga la suspensión de la cuenta
     de Google del usuario. `agy` queda instalado para que el usuario lo use directamente.
  3. **El adaptador de Gemini CLI se conserva**, con sus tests, para usarlo con una API key del propio usuario
     (`GEMINI_API_KEY`). Auto Doctor explica por qué una cuenta personal ya no basta.
- **Alternativas descartadas:**
  - Envolver `agy` con la cuenta personal: va contra la FAQ y los términos.
  - API key de AI Studio ahora mismo: en el nivel gratuito Google puede usar los prompts para mejorar sus productos, y
    en el de pago se cobra por uso. Queda a criterio del usuario más adelante.
- **Consecuencias:**
  - F2-8 se cierra documentando que Gemini no está soportado con una cuenta personal.
  - F2-2 y F2-3 quedan cubiertos con Claude y Codex.
  - Los debates con tres proveedores distintos esperan a una API key.
  - Retomarlo con API key no cambia el diseño: basta con definir `GEMINI_API_KEY` y capturar las salidas reales que
    faltan.
- **Adenda (2026-10-30, tanda 1): cómo lo enseña la app.** La decisión no cambia. Hasta ahora Gemini contaba en Auto
  Doctor como un proveedor más sin sesión, y el resumen de la barra superior lo daba por «no listo» (hallazgo V3 de
  [docs/18](../18-peticiones-y-pendientes.md)). Ahora:
  - Su comprobación sigue corriendo igual (solo `gemini --version` y mirar si están definidas las variables de la clave,
    sin gastar cuota) y se enseña.
  - Si la comprobación no encuentra una forma de entrar, Auto Doctor lo marca **«Aparcado»**, con un punto neutro (ni
    listo ni con problema) y, en sus detalles, el motivo: sin una clave de API (`GEMINI_API_KEY`), Agent Studio trabaja
    con Claude Code y Codex.
  - Un proveedor aparcado no cuenta en el resumen de la barra superior («Proveedores: 2 de 2 listos»). Si todos lo
    estuvieran, el resumen diría «Proveedores: todos aparcados».
  - Deja de estar aparcado en cuanto la comprobación ve una forma de entrar: una sesión que verificó o un método que nombra,
    como las variables de la clave de API (`GEMINI_API_KEY`, `GOOGLE_API_KEY` o `GOOGLE_GENAI_USE_VERTEXAI`). De ellas solo
    se mira si están definidas y se informa su nombre; el valor nunca se enseña ni se guarda. Entonces cuenta como los demás.
