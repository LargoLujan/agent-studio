# 09 · Informe de la Fase 0

> 2026-10-15. Qué se ha construido, qué está verificado y con qué evidencia, y qué queda pendiente. Nada de lo que
> aparece como «verificado» se afirma sin el comando y la salida correspondientes.

## 1. Criterios de aceptación de la Fase 0

| ID | Criterio | Estado | Evidencia |
|---|---|---|---|
| F0-1 | Los 12 entregables de la sección 19 | ✔ | [README de docs](README.md) los enlaza uno a uno |
| F0-2 | 11 proyectos con dependencias justificadas | ✔ | `AgentStudio.slnx`; [02 §4](02-arquitectura.md#4-estructura-de-la-solución-y-dependencias); [ADR-0002](adr/0002-capas-y-puertos.md) |
| F0-3 | Build sin avisos y tests en verde | ✔ | `dotnet build AgentStudio.slnx` → `0 Advertencia(s), 0 Errores`; `dotnet test` → `total: 593, error: 0, correcto: 593, omitido: 0` |
| F0-4 | Reglas de dominio probadas | ✔ | 185 métodos de test en `tests/AgentStudio.Tests/Domain`; las 21 reglas del validador tienen al menos un test cada una; propiedades sobre grafos aleatorios; pruebas de mutación manuales del agente de tests (4 mutaciones detectadas, según su informe), una reproducida en la integración: quitar `Passed > 0` de `TestRunSummary.Succeeded` hace fallar 3 tests |
| F0-5 | La app arranca y muestra el estado real de los CLIs sin consumir cuota | ✔ | Exe ejecutado: `HasExited=False Responding=True`, cierre con `ExitCode=0`; [captura](evidencias/fase0-auto-doctor.png); test `LocalTools` en verde |
| F0-6 | Términos e interfaces de proveedores verificados con fuente | ✔ | [investigación](investigacion/2026-10-15-proveedores-y-stack.md) |
| F0-7 | Esquema SQLite con tablas append-only | ✔ | `SqliteSchemaTests`: UPDATE, DELETE e `INSERT OR REPLACE` rechazados en `decision_log`, `evidence`, `verdicts`, `debate_contributions` y `requirement_sets`; FK activas; FTS5 operativo; aplicación idempotente |
| — | Diagramas Mermaid válidos | ✔ | Los 12 bloques de `docs/` renderizados con Mermaid 11.4.1 en Edge sin errores (`TOTAL 12 OK 12`) |

Tests por área (métodos; con los `[Theory]` expandidos suman los 593 casos): Domain 185 · Infrastructure 29 ·
Providers 24 · Observability 17 · Security 17 · Desktop 8.

## 2. Lo que funciona hoy, en la máquina de desarrollo

Resultado real de las sondas (solo `--version`, `claude auth status` y `codex login status`):

| Proveedor | Instalación | Versión | Sesión | Método |
|---|---|---|---|---|
| Claude Code | Instalado | 2.1.290 | Sesión iniciada | claude.ai (suscripción max) |
| Codex CLI | Instalado | 0.160.0 | Sesión iniciada | ChatGPT |
| Gemini CLI | Instalado (con tu OK) | 0.64.0 | Desconocida: no hay comando de estado sin coste; inicia sesión ejecutando `gemini` | — |

## 3. Estado real de cada proyecto

| Proyecto | Implementado | Solo contrato (fase) |
|---|---|---|
| Domain | `WorkTask`, `TaskGraph`, `FrozenRequirementSet`, `EvidenceRecord`, `DecisionLog`, `Verdict`, `VerdictValidator`, `DebateSession`, `AgentDefinition`, `MetricValue` | — |
| Application | `ProviderDiagnostics` | `IAgentProvider` (2), almacenes (3-4), `IOrchestrator` (3), `IDebateCoordinator`, `IJudge`, `IJudgeDossierBuilder`, `IProjectAuditService` (5), `IApprovalGate`, `ICredentialVault` (2-3), `IProjectMemory`, `IUsageLedger` (4) |
| Infrastructure | `ProcessRunner`, `ExecutableLocator`, `SqliteSchema` + migración `0001_initial.sql` | Repositorios (3-4) |
| Providers | `ClaudeCodeProbe`, `CodexCliProbe`, `GeminiCliProbe` y sus parsers | Adaptadores de ejecución (2) |
| Observability | `ContextHealthEvaluator` | Panel de consumo (4) |
| Security | `PathAccessPolicy` (léxica, solo rutas absolutas) | Aprobaciones, DPAPI (2-3); enlaces simbólicos (7) |
| Orchestration, Debate, Memory | — | Fases 3, 5 y 4 (proyectos vacíos a propósito) |
| Desktop | Ventana oscura con Auto Doctor (MVVM, Generic Host, tema con tokens) | Docking, editor, terminal (1) |

## 4. Fallos encontrados y corregidos durante la integración

| Fallo | Cómo se detectó | Corrección |
|---|---|---|
| Los puntos de estado salían siempre ámbar (`Trigger` sobre `Tag` comparaba un enum con una cadena) | Captura de la app en ejecución; los tests del ViewModel estaban en verde | `DataTrigger` sobre `Status` + test del estilo real en hilo STA |
| `dotnet test` no ejecutaba nada con xUnit v3 en el SDK 10 | Agente de escritorio | Runner `Microsoft.Testing.Platform` en `global.json`; no usar `-nologo` |
| Tests sin `using System.IO` en el proyecto de tests WPF | Compilación de la solución integrada | `using` explícito |
| `ProcessRunner` podía esperar sin límite si un nieto mantenía abiertos los pipes | Revisión propia | El drenaje respeta el timeout |
| `TaskGraph.Analyze`: tarea fallida reintentable que depende de una cancelada no salía como inalcanzable | Agente de tests | Corregido con test rojo → verde |
| `DecisionLog.Verify` aceptaba una cadena reescrita con la cronología invertida | Agente de tests | Corregido con test rojo → verde |
| Registro duplicado de `ProviderDiagnostics` en DI | Revisión propia | Eliminado |

## 5. Revisión independiente (revisor, solo lectura)

Sin hallazgos críticos. Lo corregido, cada punto con test que falló antes del arreglo:

| Gravedad | Hallazgo | Corrección |
|---|---|---|
| Alto (demostrado) | `INSERT OR REPLACE` sobrescribía filas de tablas append-only sin disparar triggers | Triggers `BEFORE INSERT` contra claves existentes + `PRAGMA recursive_triggers = ON` |
| Alto (demostrado) | `requirement_sets` no era append-only | Triggers de no-UPDATE, no-DELETE y no-REPLACE |
| Medio | El validador aceptaba cualquier evidencia objetiva para un criterio de test automático | Reglas `ApprovedWithoutEvidenceForCriterion` y `SatisfiedByFailingTestRun` |
| Medio | Empate de fecha en la «última» ejecución de tests dependía del orden | Si alguna ejecución del último instante falla, no se aprueba |
| Medio | Un veredicto con campos nulos (JSON del juez) lanzaba excepción | Regla `MalformedVerdict` |
| Medio | `DebateSession.Close` solo comparaba el agente juez, no su vendor | Se compara la identidad completa; no se puede juzgar un debate vacío; contribuciones fuera de tiempo rechazadas |
| Medio | `DecisionLog` no era seguro ante llamadas concurrentes | Lock y `Entries` como instantánea |
| Medio | La sonda de Codex podía guardar texto libre del CLI | Lista blanca: «ChatGPT», «API key» o nada |
| Medio | Un `--version` con error podía dar una versión falsa | Código de salida distinto de 0 → estado Error |
| Medio | Rutas relativas resueltas contra el directorio del proceso | `PathAccessPolicy` exige rutas absolutas |
| Bajo | Entradas relativas del PATH («.») permitían que un repo plantara un `claude.cmd` | El localizador ignora entradas relativas y solo admite .exe/.com/.cmd/.bat |
| Bajo | `x.cmd.` esquivaba la protección contra metacaracteres | Se normalizan puntos y espacios finales |
| Bajo | Excepciones no previstas podían dejar el proceso vivo | Se mata el árbol antes de relanzar |
| Bajo | `Approved` con intervención humana pedida | Regla `ApprovedPendingHumanIntervention` |

Aceptado y documentado, no corregido en la Fase 0:

- Quien controle el fichero SQLite puede borrar los triggers: lo detecta la cadena de hashes; firma en la Fase 7 ([ADR-0004](adr/0004-registro-de-decisiones-append-only.md)).
- Sin Job Object, un cierre abrupto de la app puede dejar procesos hijos vivos: Fase 2, con el runner de streaming.
- Las dependencias entre tareas y la existencia real de las evidencias las hará cumplir el motor (Fase 3), no la entidad.
- La comprobación de la app no se cancela al cerrar la ventana y `OnExit` es `async void`: Fase 1.
- Las objeciones pendientes no bloquean una aprobación (decisión deliberada; quedan registradas).

## 6. Desviaciones respecto al prompt maestro

- **Suscripciones:** solo uso personal (decisión del usuario); sin distribución a terceros ([01](01-viabilidad-y-riesgos.md), [ADR-0003](adr/0003-integracion-por-clis-oficiales.md)).
- **Boceto manuscrito:** no se aportó; las referencias de diseño son el dashboard oscuro y el diagrama del núcleo.
- **Gemini CLI:** no está instalado; su sonda está implementada, pero el adaptador no se ha podido verificar.
- **Un solo proyecto de tests**, como pide la sección 17, organizado por carpetas.

## 7. Decisiones del usuario (2026-10-15)

| Decisión | Aplicación |
|---|---|
| Solo uso personal | Documentación y ADR-0003 actualizados; sin modo de distribución |
| Instalar Gemini CLI | Instalado 0.64.0 y verificado con `--version` y `--help`; falta que inicies sesión |
| Catálogo de skills por sector TI, con tags, activación y salud | Nueva funcionalidad: [10-catalogo-de-skills.md](10-catalogo-de-skills.md) y ADR-0007 |
| Bitácora diaria obligatoria por proyecto | Funcionalidad del producto ([11-bitacora.md](11-bitacora.md)) y, desde ya, hook en este repo |
| Commit inicial en GitHub personal privado | Repositorio `LargoLujan/agent-studio` (privado) |
| «El resto, lo que recomiendes» | Kit aprobado; `CLAUDE.md` creado; rutas de la app abreviadas con `~`; identidad de git del repo con el email noreply de GitHub |

## 8. Ampliación tras las decisiones (2026-10-15)

| Pieza | Estado | Evidencia |
|---|---|---|
| Bitácora del producto: `IPromptJournal`, `MarkdownDailyJournal`, `JournalingAgentProvider` (fail closed) | Implementado | 21 tests nuevos (formato, corte de día, concurrencia, orden «registrar antes de lanzar») |
| Bitácora del desarrollo: hook `UserPromptSubmit` | Activo desde el 2026-10-15 a las 11:18 | Entradas reales en `.agentstudio/bitacora/2026-10-15.md`; ignora los mensajes del sistema |
| Catálogo de skills: dominio, recomendador e importador del índice local | Implementado | 45 tests nuevos; importación real: 119 skills en 15 sectores, 61 de 62 `SKILL.md` válidos |
| Revisión independiente del incremento | 6 hallazgos (1 alto demostrado: ids `P-NNNN` duplicados entre procesos), todos corregidos con tests | Ids únicos con 4 instancias + hook; importador que no se cae por una entrada mala; frontmatter multilínea; validación de rutas y URLs |
| Propuesta de skills para los sectores vacíos | Pendiente de tu aprobación | [candidatas](investigacion/2026-10-15-candidatas-skills.md) |
| Totales | ✔ | `dotnet build` sin avisos; `dotnet test` → `total: 728, error: 0` |
