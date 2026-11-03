# ADR-0003 · Integración por CLIs oficiales, solo uso personal

- **Estado:** aceptada · 2026-10-15, revisada el mismo día tras la decisión del usuario de limitarlo a uso personal
  (revisar los términos antes de la Fase 2 y en cada actualización mayor de un CLI). Revisada otra vez el
  2026-10-15: Google queda aparcado, porque Gemini CLI ya no admite cuentas personales y Antigravity no se puede
  envolver con una cuenta de consumo ([ADR-0009](0009-google-aparcado.md))
- **Contexto:** el prompt pide trabajar «prioritariamente mediante suscripciones» y prohíbe tokens interceptados y
  endpoints privados. La verificación de términos ([investigación](../investigacion/2026-10-15-proveedores-y-stack.md))
  muestra que los tres proveedores toleran que el usuario use su propio CLI con su sesión, pero no que un producto de
  terceros use o intermedie las credenciales de suscripción de sus usuarios. El usuario ha decidido que Agent Studio
  es **solo para uso personal**.
- **Decisión:**
  1. Los adaptadores lanzan el **binario oficial sin modificar** en modo headless (`claude -p --output-format stream-json`,
     `codex exec --json`, `gemini -p -o stream-json`), con el prompt por stdin y los permisos explícitos.
  2. Agent Studio **nunca lee ni almacena** credenciales de los CLIs; el estado de sesión se consulta con los comandos
     oficiales (`claude auth status`, `codex login status`) y las sondas descartan datos personales.
  3. **Uso personal únicamente:** se trabaja con las suscripciones del propio usuario en su máquina. Las API keys son
     opcionales, del propio usuario y cifradas con DPAPI. No hay distribución a terceros; si algún día se planteara,
     esta decisión debe revisarse antes que cualquier otra.
  4. El app-server de Codex se usa solo para leer cuota (experimental, versión fijada).
- **Alternativas:** ACP como protocolo común (adaptadores de terceros para Claude y Codex, sin SDK .NET oficial; se
  reevalúa tras la Fase 2); APIs directas (opción puntual con API key del usuario); Agent SDK como proceso auxiliar
  (añade Node/Python al núcleo y sus términos piden API key a terceros).
- **Consecuencias:** tres formatos de eventos que normalizar y probar con salidas grabadas por versión; métricas de
  cuota casi siempre «no expuestas»; el uso debe mantenerse «ordinario e individual» como piden los términos de
  suscripción (límites de concurrencia y presupuesto por defecto).
