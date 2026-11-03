# 01 · Viabilidad técnica, riesgos y limitaciones

> Entregables 1 y 2 de la sección 19 del prompt maestro. Fecha: 2026-10-15.
> Las afirmaciones sobre proveedores remiten a [investigacion/2026-10-15-proveedores-y-stack.md](investigacion/2026-10-15-proveedores-y-stack.md),
> donde cada una lleva su fuente y si está verificada o inferida.

## 1. Conclusión

Agent Studio es **viable como herramienta personal** en Windows con .NET 10 y WPF, lanzando los CLIs oficiales que el
propio usuario ya tiene instalados y autenticados. Hay que corregir una premisa del prompt maestro antes de seguir:

> **La sección 3 pide trabajar «prioritariamente mediante suscripciones».** Eso es defendible solo para uso personal.
> Para un producto distribuido a terceros, los tres proveedores empujan a API key:
>
> - **Anthropic** no permite a terceros ofrecer el login de claude.ai ni enrutar peticiones con credenciales Free/Pro/Max
>   de sus usuarios, ni recoger o intermediar esas credenciales. Sí permite ejecutar el binario de Claude Code sin
>   modificar si cada usuario se autentica por su cuenta (con condiciones comerciales).
> - **OpenAI** recomienda API key para automatización y dice que la autenticación del app-server "has never been
>   permitted for commercial or hosted services".
> - **Google** declara infracción acceder a los servicios de Gemini CLI con su OAuth desde software de terceros.

**Decisión del usuario (2026-10-15): Agent Studio es solo para uso personal.** No se distribuirá a terceros, así
que el escenario restringido no aplica ([ADR-0003](adr/0003-integracion-por-clis-oficiales.md)):

| Aspecto | Cómo queda |
|---|---|
| Autenticación | El usuario inicia sesión en cada CLI con sus herramientas oficiales (`claude auth login`, `codex login`, `gemini`) |
| Qué hace Agent Studio con las credenciales | Nada. Ejecuta el binario sin modificar y consulta el estado con los comandos oficiales. Nunca lee ni copia tokens |
| API keys | Opcionales y del propio usuario (p. ej. para un proveedor sin CLI). Se guardan cifradas con DPAPI |
| Distribución a terceros | Fuera de alcance. Si algún día cambiara, habría que revisar los términos antes de nada |

Aun en uso personal se respeta el espíritu de los términos: uso ordinario e individual, sin automatizar más allá de
lo que documenta cada CLI y sin reutilizar sus credenciales fuera del propio CLI.

## 2. Viabilidad por área

| Área (sección del prompt) | Veredicto | Condiciones y matices |
|---|---|---|
| IDE de escritorio WPF + MVVM (2, 4) | **Viable** | .NET 10 es LTS hasta 2028-11. Docking con AvalonDock (MS-PL); hay que crear el tema oscuro propio. |
| Editor real (5) | **Viable** | Monaco 0.57 en WebView2. WebView2 es propietario pero gratuito y redistribuible. Respaldo nativo: AvalonEdit (MIT). |
| Terminal con procesos reales (5) | **Viable con trabajo propio** | ConPTY por P/Invoke + xterm.js en WebView2. Los paquetes WPF de terminal en NuGet no declaran licencia: no se usan. |
| Adaptadores de CLIs (3) | **Viable para uso personal** | Claude Code (`-p --output-format stream-json`) y Codex (`exec --json`) verificados en esta máquina. Gemini CLI documentado pero **no instalado**: sin verificar. |
| Orquestador visual (6) | **Viable** | Nodify (MIT) para el lienzo. La dificultad real es la concurrencia sobre el mismo repositorio: un worktree por tarea paralela. |
| Debate Arena y juez (7, 8) | **Viable técnicamente** | El juicio de un LLM no es fiable por sí solo. Por eso el veredicto se valida con reglas deterministas y evidencias capturadas por el sistema ([07](07-debate-arena-y-juez.md)). |
| Auditoría del orquestador (9) | **Viable** | Registro de decisiones encadenado por hash + juez con dossier construido por el sistema, no por el orquestador. |
| Salud del contexto (10) | **Parcial** | Tokens por ejecución: sí (Claude `result.usage`, Codex `turn.completed.usage`, Gemini `stats`). Ventana de contexto: Codex la expone en app-server; en Claude y Gemini no está verificada. Las compactaciones internas de cada CLI son opacas: Agent Studio solo controla y mide el contexto que **él** empaqueta. |
| Consumo de suscripciones (11) | **Parcial** | Solo Codex expone cuota de plan oficialmente (`account/rateLimits/read`, app-server experimental). Claude: solo `rate_limit_event` al topar con el límite en headless. Gemini: nada en headless. El panel mostrará ejecuciones, duración, tokens y errores por límite; los porcentajes solo donde el proveedor los dé. |
| Auto Doctor (12) | **Viable** | Ya funciona la primera pieza: detección de instalación, versión y sesión sin consumir cuota. |
| Auto Graph (13) | **Viable por lenguaje** | C#: análisis estático con Roslyn. Python/SQL/YAML: análisis más pobre (imports, referencias). Las relaciones inferidas por IA se marcan aparte. |
| Memoria persistente (14) | **Viable** | SQLite con FTS5 + exportación Markdown. |
| Seguridad y aislamiento (15) | **Parcial** | Agent Studio no puede interceptar todo lo que hace un CLI por dentro. El control real combina: flags de permisos de cada CLI, worktrees, aprobaciones por protocolo (Codex app-server; Claude `--permission-prompt-tool` con un MCP propio) y la política de rutas de las operaciones que hace Agent Studio. El aislamiento fuerte (Windows Sandbox, AppContainer o contenedores) se evalúa en la Fase 7. |
| Paquete Azure/Fabric (16) | **Viable, fuera del núcleo** | Como extensión: agentes con skills y MCP oficiales (Fabric, Power BI Modeling, Learn) y autenticación con `az`/Entra. |

## 3. Riesgos

Probabilidad (P) e impacto (I): A alta, M media, B baja.

| ID | Riesgo | P | I | Mitigación | Fase |
|---|---|---|---|---|---|
| R1 | Los términos de los proveedores cambian o se interpretan de forma más estricta para el uso automatizado personal | M | A | Solo uso personal; binarios sin modificar; Agent Studio nunca toca credenciales de los CLIs; revisión de términos antes de la Fase 2 y en cada actualización mayor de un CLI | 2, 7 |
| R2 | Cambian las interfaces de los CLIs (`stream-json`, `exec --json`; el app-server de Codex es experimental) | A | M | Adaptadores aislados tras `IAgentProvider`; tests de contrato con salidas grabadas por versión; matriz de versiones compatibles; Auto Doctor avisa de versiones no probadas | 2 |
| R3 | Límites de uso de las suscripciones cortan ejecuciones largas | A | M | Concurrencia máxima por proveedor, reintentos con espera, presupuestos por agente, registro de errores por límite | 2, 3 |
| R4 | Inyección de instrucciones desde ficheros del repo, webs o respuestas de otros agentes | A | A | Toda salida de agente es dato, no instrucción; el juez es de solo lectura; aprobación humana para acciones destructivas; política de red; marcado de contenido externo en los prompts | 3, 5, 7 |
| R5 | Inyección de argumentos al lanzar shims `.cmd` de npm (cmd.exe re-parsea los argumentos) | M | A | El texto libre va siempre por stdin; `ProcessRunner` rechaza metacaracteres en argumentos de `.cmd/.bat` (implementado en Fase 0) | 0, 2 |
| R6 | El aislamiento en Windows es débil: un agente con shell puede salirse del worktree | A | A | Permisos mínimos por defecto en cada CLI; worktrees; aprobaciones; evaluación de Windows Sandbox/AppContainer; la UI no promete aislamiento que no existe | 2, 7 |
| R7 | El juez se equivoca, es complaciente o comparte sesgos con quien ejecuta | M | A | Proveedor distinto por defecto; validador determinista; evidencias objetivas obligatorias; el usuario puede impugnar y escalar | 5 |
| R8 | El dossier del juez no cabe en su contexto | M | M | Dossier selectivo con resúmenes enlazados por hash a la evidencia completa; límites por auditoría | 5 |
| R9 | Varios agentes escriben a la vez sobre los mismos ficheros | A | A | Un worktree por tarea paralela; integración explícita con merge y tests; detección de conflictos | 3 |
| R10 | Complejidad de la UI (docking, Monaco, terminal) en WPF | M | M | Spikes al inicio de la Fase 1; AvalonEdit como respaldo de Monaco | 1 |
| R11 | Dependencias con licencias incompatibles | M | M | Lista verificada; FluentAssertions 8 descartado (licencia no comercial); terminal propia | 0 |
| R12 | Expectativas de métricas que los proveedores no dan | A | B | Procedencia visible en cada número: exacta, estimada o no disponible | 4 |
| R13 | Gemini CLI no está instalado: su adaptador no se ha podido verificar | Cierta | M | Instalarlo (con tu OK) al empezar la Fase 2 y verificar formato y estado de sesión | 2 |
| R14 | El registro encadenado detecta manipulaciones pero no las impide | M | M | Triggers append-only en SQLite; el hash de cabeza queda anclado en cada veredicto y en las exportaciones; firma opcional en Fase 7 | 3, 7 |
| R15 | Alcance enorme para un equipo pequeño | A | A | Fases con software ejecutable; esqueleto andante primero; cada fase cierra con criterios verificables | Todas |

## 4. Limitaciones que no tienen solución técnica

- **No se puede medir la comprensión real del modelo.** Solo tokens, ventana y lo que el propio sistema empaqueta.
- **El contexto interno de cada CLI es opaco.** Claude Code, Codex y Gemini compactan su propia conversación; Agent
  Studio ve lo que exponen sus eventos, no su estado interno.
- **Las cuotas de suscripción son en su mayoría invisibles** fuera de las interfaces interactivas de cada CLI.
- **Un veredicto de juez no es una prueba.** Por eso no puede aprobar contra una ejecución de tests fallida ni apoyarse
  solo en lo que afirma un agente.
- **Referencias de diseño incompletas:** el prompt menciona un «boceto manuscrito original» que no se ha aportado; solo
  están el dashboard oscuro y el render del diagrama del núcleo (en [referencias/](referencias/)).
