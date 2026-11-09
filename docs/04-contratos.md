# 04 · Contratos de los componentes principales

> Entregable 7 de la sección 19. Los contratos están en código, no solo aquí: este documento es el mapa.
> Estado: **Implementado** (código real con tests) o **Contrato** (interfaz sin implementación; la fase indicada la
> implementa). No hay implementaciones simuladas. Puesto al día con el paquete P0 de la Fase 3 (contratos de la
> orquestación, su dominio y la migración 0002); el diseño completo está en [13](13-fase-3-orquestacion.md) y las
> secciones 8 a 15 de este documento resumen lo que P0 deja fijado. Puesto al día también con el paquete P0 de la
> Fase 4 (contratos, dominio, payloads v2 y migración 0004; diseño en [14](14-fase-4-observabilidad-y-memoria.md)): lo
> resume la [sección 16](#16-fase-4-consumo-cuota-memoria-y-skills). Y con el paquete P0 de la Fase 5 (contratos,
> dominio, payloads y migración 0005; diseño en [15](15-fase-5-debate-arena-y-juez.md)): lo resume la
> [sección 17](#17-fase-5-debates-juez-auditorías-e-impugnación), con lo que se desvía de docs/15 §5. Y con el paquete P0 de
> la Fase 8 (la clase del final de un intento, payloads v3, opciones de la cabecera, presencia del usuario, energía, ajustes y
> TFM; diseño en [16](16-ampliacion-plataforma-de-proyectos.md) §4.5): lo resume la
> [sección 18](#18-fase-8-trabajo-desatendido), con lo que se desvía de docs/16. Y con la tanda 2 (2026-11-03: barras en
> tiempo real y pulido del recorrido en vivo; [18](18-peticiones-y-pendientes.md)): lo resume la
> [sección 19](#19-tanda-2-barras-en-tiempo-real-y-pulido-2026-11-03). Y con la Fase 12 (el índice semántico de la bitácora
> y la memoria, de su P0 a la integración; diseño en [17](17-constructor-visual-de-agentes.md) §3): lo resume la
> [sección 20](#20-fase-12-índice-semántico-de-la-bitácora-y-la-memoria). Y con el P0 de la Fase 13 (los tipos del
> constructor visual, los payloads de las preguntas y del configurador, los contratos del estimador, del análisis del
> consumo, del idioma y de la barrera de Windows Hello, y los cimientos del escritorio; diseño en
> [17](17-constructor-visual-de-agentes.md) §4 y §9.3): lo resume la [sección 21](#21-fase-13-constructor-visual-de-agentes).

## 1. Mapa

| Contrato | Fichero | Estado | Lo implementa |
|---|---|---|---|
| `IProcessRunner`, `ProcessSpec`, `ProcessResult` | `src/AgentStudio.Application/Execution/ProcessContracts.cs` | Implementado | `Infrastructure/Processes/ProcessRunner` |
| `IExecutableLocator` | ídem | Implementado | `Infrastructure/Processes/ExecutableLocator` |
| `IProviderProbe`, `ProviderProbeResult` | `src/AgentStudio.Application/Providers/ProviderProbe.cs` | Implementado | `Providers/Cli/ClaudeCodeProbe`, `CodexCliProbe`, `GeminiCliProbe` |
| `ProviderDiagnostics` (caso de uso) | `src/AgentStudio.Application/Diagnostics/ProviderDiagnostics.cs` | Implementado | — (es la implementación) |
| `IAgentProvider`, `IAgentRun`, `AgentEvent`, `ProviderCapabilities`, `PermissionProfile` | `src/AgentStudio.Application/Providers/AgentProvider.cs` | Implementado (adaptadores de la Fase 2) | `Providers/Cli/*` |
| Fase 4 en el mismo fichero: `ModelUsage`, `TokenUsage` (caché escrita, razonamiento, desglose y turnos), `ContextObserved`, `CompactionObserved`, `ProviderCapabilities.ReportsContextOccupancy`, `McpServerSpec` y `AgentRunRequest.McpServers` | ídem | Contrato | Fase 4 (`Providers`, WP1) |
| `IAuxiliaryAgentRunner`, `AuxiliaryAgentRunner`, `AuxiliaryRunRequest`, `AuxiliaryRunResult` | `src/AgentStudio.Application/Providers/AuxiliaryAgentRunner.cs` | Implementado | — (es la implementación) |
| `IQuotaObservations`, `IProviderQuotaReader`, `QuotaObservation`, `QuotaReadResult` | `src/AgentStudio.Application/Providers/QuotaContracts.cs` | Contrato | Fase 4 (`Observability`, WP2; `Providers`, WP1) |
| Tanda 2: `IAgentRunEnds`, `AgentRunEnded`, `AgentRunEnds` | `src/AgentStudio.Application/Providers/AgentRunEnds.cs` | Implementado | — (es la implementación; la avisa `QuotaObservingAgentProvider`) |
| Tanda 2: `SharedQuotaReader` (decorador de `IProviderQuotaReader`) | `src/AgentStudio.Application/Providers/SharedQuotaReader.cs` | Implementado | — (envuelve a `CodexAppServerQuotaReader` en `AddCodexQuotaReader`) |
| `IDecisionLogStore`, `IEvidenceStore`, `IRequirementStore`, `IPlanStore`, `IRunRegistry`, `IVerdictStore`, `IAgentRoster` | `src/AgentStudio.Application/Persistence/StoreContracts.cs` | Contrato (esquema SQLite implementado: migraciones 0001 y 0002) | Fase 3 (`Infrastructure`, WP2) |
| `IDecisionLogTailReader`, `DecisionLogMark`, `DecisionLogTail` (2026-11-04) | `src/AgentStudio.Application/Persistence/DecisionLogTail.cs` | Implementado | `Infrastructure/Persistence/SqliteDecisionLogStore`; lo usa `DecisionRecorder` para verificar solo lo que creció el registro ([ADR-0010](adr/0010-estado-derivado-del-registro.md), adenda del 2026-11-04) |
| `IProjectWorkspace`, `ProjectContext` | `src/AgentStudio.Application/Projects/ProjectContracts.cs` | Contrato | Fase 3 (`Infrastructure`, WP2) |
| `IProjectClosedSignal` (2026-11-05) | ídem | Implementado | `Infrastructure/Persistence/ProjectDatabases`; lo escucha `ProjectMemory` para olvidar un proyecto cerrado |
| `IDecisionRecorder` y `LogLoad` (2026-11-04), `DecisionRecorder`, `DecisionPayload` y sus payloads versionados, `DecisionPayloadSerializer` | `src/AgentStudio.Application/Decisions/` | Implementado | — (es la implementación; el almacén es el de WP2) |
| `IOrchestrator`, `IOrchestrationRun`, `PlanningRequest`, `PlanProposal`, `RunRequest`, `RunOptions`, `RunSnapshot`, `OrchestrationEvent` | `src/AgentStudio.Application/Orchestration/` | Contrato. **Sustituye** al `IOrchestrator.ExecuteAsync(projectId, TaskGraph, …)` de la Fase 0, que nunca se implementó | Fase 3 (`Orchestration`, WP4) |
| `IWorktreeManager`, `IIntegrator`, `IDeliveryService`, `IWorkspaceCleanup`; `UserRepositoryState` (con su comparación) | `src/AgentStudio.Application/Git/` | Contrato (`UserRepositoryState.Compare`: implementado) | Fase 3 (`Infrastructure/Git`, `Orchestration/Integration`, WP3) |
| `IVerificationRunner`, `VerificationStep`, `VerificationProfile` | `src/AgentStudio.Application/Verification/VerificationContracts.cs` | Contrato | Fase 3 (`Orchestration/Verification`, WP3) |
| `IDebateCoordinator`, `IDebateRun`, `IDebateJudgment` y los registros que enseñan las pantallas (`DebateSummary`, `DebateSnapshot`, `DebateTranscript`, `DebateProgress`…) | `src/AgentStudio.Application/Coordination/CoordinationContracts.cs` | Contrato. **Sustituye** al `IDebateCoordinator.RunAsync(projectId, DebateSession)` de la Fase 0 | Fase 5 (`Debate`, WP1 y WP2) |
| `IJudge`, `IJudgeDossierBuilder`, `IIndependentReview` | `src/AgentStudio.Application/Judging/JudgingContracts.cs` | Implementado | `Debate/Judging` (Fase 3, WP5) |
| `IProjectAuditService`, `ProjectAuditProposal`, `AuditReport`, `IOrchestratorAuditService`, `IVerdictChallenges`, `ChallengeStatus` | ídem | Contrato. `IProjectAuditService` y `AuditReport` **sustituyen** a los de la Fase 0, que nada implementaba | Fase 5 (`Debate`, WP3) |
| Payloads de la Fase 5 (`DebatePayloads.cs`) | `src/AgentStudio.Application/Decisions/` | Implementado | — |
| `IDebateRegistry`, `IProjectAuditRegistry`, `DebateRecord`, `ProjectAuditRecord` | `src/AgentStudio.Application/Persistence/StoreContracts.cs` | Contrato (esquema: migración 0005) | Fase 5 (`Infrastructure`, WP4) |
| `DebateHeaderHash` | `src/AgentStudio.Application/Debate/DebateHeaderHash.cs` | Implementado | — |
| `IProjectActivity`, `ProjectActivityKind` (también es `IRunActivity`) | `src/AgentStudio.Application/Orchestration/ProjectActivity.cs` | Contrato | Fase 5 (`Orchestration`, WP5) |
| `AgentRunObservations`, `PromptData` (movido desde `Orchestration/Engine`) | `src/AgentStudio.Application/Providers/`, `src/AgentStudio.Application/Prompts/` | Implementado | — |
| `IApprovalGate`, `IApprovalPrompt`, `ICredentialVault` | `src/AgentStudio.Application/Security/SecurityContracts.cs` | Contrato | Fases 2-3 (`Security`; `IApprovalPrompt` en `Desktop`) |
| Fase 8: `AttemptEndClass`, `AttemptBudgetKind`, `AgentRunEnd`, `AgentRunOutcome.End`, `RateLimitSignaled.LimitType` | `src/AgentStudio.Application/Providers/AgentProvider.cs` | Contrato | Fase 8 (`Providers` y motor, WP2) |
| Fase 8: `AgentRunFinished` v3, `RunExitChosen`, `RunDispatchChanged`, `AccessChecked`, `ApprovalGranted`/`ApprovalDenied` v2 | `src/AgentStudio.Application/Decisions/DecisionPayloads.cs` | Implementado (formato) | Escriben WP1 a WP4 |
| Fase 8: `RunOptions` (margen, tope de la espera estimada y consentimiento de reanudación) y su hash | `src/AgentStudio.Application/Orchestration/` | Implementado (cabecera y hash) | Fase 8 (motor, WP1) |
| `ApprovalVerification`, `IUserPresenceVerifier` | `src/AgentStudio.Application/Security/` | Contrato | Fase 8 (`Desktop/Lock`, WP4) |
| `IPowerEvents`, `IKeepAwake`, `PowerEvent` | `src/AgentStudio.Application/Execution/PowerContracts.cs` | Contrato | Fase 8 (`Desktop/Background`, WP3) |
| `IProjectMemory`, `IMemoryStore`, `IMemoryExporter` y sus tipos (`MemoryDraft`, `MemoryEntry`, `MemoryRow`, `MemoryQuery`, `MemorySearch`, `MemoryTrust`...) | `src/AgentStudio.Application/Knowledge/MemoryContracts.cs` | Contrato (`MemoryContent`: implementado). **Sustituye** al `IProjectMemory` de la Fase 0 | Fase 4 (`Memory` e `Infrastructure`, WP3) |
| `IContextPacker`, `ContextPackRequest`, `ContextPack`, `ContextBudgetTooSmallException` | `src/AgentStudio.Application/Knowledge/ContextPackContracts.cs` | Contrato | Fase 4 (`Memory`, WP3) |
| `IDailySummaryService`, `DailySummaryResult` | `src/AgentStudio.Application/Knowledge/DailySummaryContracts.cs` | Contrato | Fase 4 (`Memory`, WP3) |
| `IMemoryDerivation`, `MemoryDerivationResult`, `MemorySources` | `src/AgentStudio.Application/Knowledge/MemoryDerivationContracts.cs` | Contrato | Fase 4 (`Memory`, WP6; lo llama `Desktop` al terminar cada ejecución y al abrir un proyecto con almacenamiento) |
| `IUsageReport`, `IUsageExporter` y sus tipos | `src/AgentStudio.Application/Usage/UsageContracts.cs` | Contrato. **Sustituye** a `IUsageLedger`, que se retira | Fase 4 (`Observability`, WP2) |
| `IProjectLogReader`, `ProjectLogSnapshot`, `AnchorStatus` | `src/AgentStudio.Application/Projects/ProjectLogContracts.cs` | Contrato | Fase 4 (`Infrastructure`, WP3) |
| `IProjectStorageReader` | `src/AgentStudio.Application/Projects/ProjectStorageReader.cs` | Contrato | Fase 4 (`Infrastructure`, WP3) |
| `IRunActivity` | `src/AgentStudio.Application/Orchestration/RunActivity.cs` | Contrato | Fase 4 (`Orchestration`, WP1) |
| Fase 13: `IUsageEstimator` y sus tipos (§21.4) | `src/AgentStudio.Application/Usage/EstimateContracts.cs` | Implementado (`Observability/Estimates/UsageEstimator`) | Fase 13 (`Observability`, WP2) |
| Fase 13: `IConsumptionAnalyzer`, `IAssistedConsumptionAnalysis` y sus tipos (§21.4) | `src/AgentStudio.Application/Analysis/ConsumptionAnalysisContracts.cs` | Contrato | Fase 13 (`Observability`, WP3) |
| Fase 13: `IProjectBrief`, `ProjectBrief`, `ProjectBriefs` (§21.2) | `src/AgentStudio.Application/Knowledge/MemoryContracts.cs` | Contrato | Fase 13 (`Memory`, WP3) |
| Fase 13: `IWindowsLockConfirmation`, `WindowsLockResult` (§21.4) | `src/AgentStudio.Application/Security/UserPresenceContracts.cs` | Contrato | Fase 13 (`Desktop/Lock`, WP5) |
| Fase 13: `IOrchestrationRun.AnswerAsync`, `PendingQuestion`, `QuestionPendingEvent`, `QuestionAnsweredEvent` (§21.3) | `src/AgentStudio.Application/Orchestration/` | Contrato (cuerpo por defecto que no admite respuestas) | Fase 13 (motor, WP4) |
| Fase 13: `QuestionAsked`, `QuestionAnswered`, `ProjectSetupApplied`, `AgentRunStarted` v3 (§21.2) | `src/AgentStudio.Application/Decisions/` | Implementado (formato) | Escriben WP4 y WP7 |
| Fase 13: `UserText`, `UserTextCodes` (§21.4) | `src/AgentStudio.Application/Localization/UserText.cs` | Implementado | — |
| Fase 13: `IRunLiveProjection` y sus tipos (§21.5) | `src/AgentStudio.Desktop/Orchestrator/Live/IRunLiveProjection.cs` | Contrato | Fase 13 (monitor, WP8) |
| Reglas del dominio (abajo) | `src/AgentStudio.Domain/**` | Implementado | — |
| `ContextHealthEvaluator` (movido desde `Observability` en la Fase 4) | `src/AgentStudio.Domain/Metrics/ContextHealth.cs` | Implementado | — |
| `PathAccessPolicy` | `src/AgentStudio.Security/Paths/` | Implementado (comprobación léxica) | Resolución de enlaces en Fase 7 |
| `IPromptJournal`, `PromptJournalEntry`, `PromptOutcome` | `src/AgentStudio.Application/Knowledge/PromptJournal.cs` | Implementado | `Memory/Journal/MarkdownDailyJournal` |
| `JournalingAgentProvider` (decorador obligatorio de `IAgentProvider`) | `src/AgentStudio.Application/Providers/JournalingAgentProvider.cs` | Implementado | — |
| Dominio de skills: `SkillEntry`, `SkillSectors`, `SkillActivationResolver`, `SkillHealthReport`, `SkillFrontmatter`, `SkillTagger`, `SkillRecommender` | `src/AgentStudio.Domain/Skills/` | Implementado | — |
| `ISkillCatalogImporter` | `src/AgentStudio.Application/Skills/SkillPorts.cs` | Implementado | `Infrastructure/Skills/SkillsIndexCatalogImporter` |
| `ISkillMaterializer`, `ISkillHealthChecker` | ídem | Contrato | Fases 2 y 6 |
| `ISkillCatalog`, `ISkillActivationStore`, `ISkillProvisioner` | ídem | Contrato | Fase 3 (`Infrastructure/Skills`, WP2) |
| `SkillProvisioner` | `src/AgentStudio.Application/Skills/SkillProvisioner.cs` | Implementado | — |
| `ISkillRecommendationService`, `ISkillHealthReader`, `SkillCatalogSnapshot`, `SnapshotSkill`, `AssistedRecommendation`, `QuotaConsent` | `src/AgentStudio.Application/Skills/SkillPorts.cs` | Contrato | Fase 4 (`Application/Skills` e `Infrastructure/Skills`, WP4) |
| Servidor MCP local de solo lectura | `src/AgentStudio.SkillsMcp/` | Esqueleto (sale con código 2) | Fase 4 (WP4) |

## 2. Dominio: las reglas que todo lo demás respeta

| Tipo | Regla principal |
|---|---|
| `WorkTask` | Máquina de estados cerrada, con el estado terminal `Skipped` (que solo pone el grafo); completar exige evidencia en tareas que producen resultados; reintentos contados y limitados; política de unión; historial de transiciones. |
| `TaskGraph` | Rechaza ciclos al añadir la dependencia (y dice cuál sería el ciclo); aristas con rama desde las condiciones; tareas listas según la política de unión; `CompleteCondition` y `PropagateSkips` ([ADR-0011](adr/0011-tareas-condicionales.md)); orden topológico determinista; `Analyze` detecta abandono, inalcanzables, reintentos agotados y bloqueo, y ve lo omitido como terminado. |
| `PlanDefinition`, `PlanValidator` | Plan de una ejecución con `ContentHash` canónico (independiente del orden de entrada, del origen y del autor) y 34 reglas con código estable, entre ellas la independencia del juez ([sección 9](#9-planes-y-su-validación)). |
| `ConditionPredicate`, `ConditionEvaluator` | Conjunto cerrado de predicados (`TaskSucceeded`, `TestsPassed`, `OutputEquals`, `PathsChanged`) que el sistema evalúa sobre hechos que él mismo capturó. Un plan nunca lleva expresiones ni comandos. |
| `RunStateReplayer` | Reconstruye el grafo de una ejecución desde sus eventos, por la API pública del dominio, y devuelve los problemas en lugar de lanzar ([sección 10](#10-reanudación-por-reproducción)). |
| `WriteScope` | Alcance de escritura de una tarea: normalización segura (rechaza `..`, rutas absolutas, `.git`, `.agentstudio`, comodines y la raíz), pertenencia de una ruta cambiada y solapamiento conservador. |
| `RunId` | Identificador de una ejecución (`20261015-a1b2c3`), seguro para refs de git, carpetas y la carpeta de sesión de las skills. |
| `FrozenRequirementSet` | Copia inmutable de requisitos con `ContentHash` canónico (independiente del orden y de la fecha). |
| `EvidenceRecord` | Metadatos inmutables con SHA-256 del contenido y origen (`CapturedBySystem`, `ReportedByAgent`, `ProvidedByHuman`). Una ejecución de tests sin tests no es un éxito. |
| `DecisionLog` | Append-only con cadena SHA-256; `Verify` detecta modificación, borrado intermedio, reordenación y cronología imposible; seguro ante llamadas concurrentes; las correcciones son entradas nuevas que referencian la anterior. Los tipos de entrada de la Fase 3 (`RunStarted`, `RunFinished`, `AgentRunStarted`, `AgentRunFinished`, `ConditionEvaluated`, `WorktreeCreated`, `WorktreeRemoved`), los de la Fase 4 (`MemoryRecorded`, `MemoryReviewed`) y los de la Fase 5 (`DebateOpened`, `DebateContributionRecorded`, `DebateRoundClosed`, `DebateClosed`, `VerdictChallenged`, `ProjectAuditStarted`, `ProjectAuditFinished`) se añaden al final del enum: el hash cubre el nombre del tipo y los hashes existentes no cambian. |
| `Verdict` + `VerdictValidator` | Resolución estructurada del juez y 25 reglas deterministas que la convierten en verificable ([07](07-debate-arena-y-juez.md#5-validación-del-veredicto)). Desde la Fase 5, `Verdict.Resolution` (la propuesta elegida y la comparación) y dos reglas de los debates (`DebateResolutionMissing`, `DebateResolutionUnknown`); en un debate que elige, un requisito cuyos criterios son todos `JudgeAssessment` no cae en `ApprovedOnAgentClaimsOnly` ([sección 17](#17-fase-5-debates-juez-auditorías-e-impugnación)). |
| `DebateSession` | Protocolo del debate: requisitos congelados, propuestas independientes, límites de rondas/tiempo/tokens y juez que no participa. Desde la Fase 5 tiene objeto y disposición del juicio (`JudgingArrangement`, comprobada con `DebateJudgingRules`), registra por qué fue a juicio (`DebateJudgmentReason`) y se cierra con lo que decidió `JudgingPanel.Combine` o una persona, comparando al juez por agente, proveedor y vendor, nunca por modelo; no se puede juzgar un debate vacío. |
| `DebateJudgingRules`, `JudgingArrangement`, `JudgingPanel` | Quién puede juzgar un debate con dos vendors (la regla de los productores nunca se dispensa; la de los participantes, solo de forma explícita) y cómo se combinan los veredictos de un jurado cruzado ([ADR-0027](adr/0027-independencia-del-juez-en-debates-con-dos-vendors.md)). |
| `DebateStateReplayer`, `DebateBudget` | La reproducción de un debate desde su registro, por la API pública de `DebateSession`, y los tokens de sus turnos con su origen: lo que falta se estima y cuenta ([ADR-0026](adr/0026-debate-como-unidad-del-registro.md)). |
| `AgentDefinition` | Configuración completa del agente; un juez o auditor nunca tiene escritura sobre ficheros ni recibe memoria (`agent.judge-memory-none`), y ningún agente puede proponer memoria: no hay canal, y la Fase 5 tampoco lo abre (`agent.memory-proposals-not-available`, `MemoryPolicy.ReadAndPropose`). |
| `MetricValue<T>` | Valor con uno de seis orígenes (`MetricOrigin`: informado por el CLI, medido por Agent Studio, estimado por el CLI, estimado por Agent Studio, no expuesto y sin dato) y la procedencia de tres niveles derivada de él (`Exact` / `Estimated` / `Unavailable`). Los creadores de la Fase 0 no cambian de significado (`Exact`, `Estimated`, `Unavailable` son informado, estimado por el CLI y no expuesto); `Of` relee un valor guardado. Al combinar, gana el más débil (`MetricOrigins.Weakest`). |
| `MetricAggregate<T>` | Suma comprobada de los valores que hay, cuántos había y cuántos faltaban, y el origen más débil: con huecos es «parcial (n de m)» y sin ningún valor no hay suma, nunca un cero. |
| `ContextHealthEvaluator` | Ocupación del contexto y nivel con umbrales validados (70/85/95 por defecto); `Unknown` nunca es «sano», y la ocupación toma el origen más débil de sus dos cifras. |

## 3. Proveedores

```csharp
public interface IProviderProbe                       // Fase 0 · implementado
{
    ProviderId Provider { get; }
    VendorId Vendor { get; }
    string DisplayName { get; }
    Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken);
}

public interface IAgentProvider                       // Fase 2 · contrato
{
    ProviderId Provider { get; }
    VendorId Vendor { get; }
    ProviderCapabilities Capabilities { get; }        // lo que el adaptador puede hacer de verdad
    Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken);
}

public interface IAgentRun : IAsyncDisposable
{
    string RunId { get; }
    IAsyncEnumerable<AgentEvent> Events { get; }      // eventos normalizados
    Task<AgentRunOutcome> Completion { get; }
    Task CancelAsync();
}
```

- `ProviderProbeResult` nunca lleva datos personales ni secretos: la sonda de Claude descarta email y organización.
- `AgentEvent` normaliza los eventos nativos: `SessionStarted`, `MessageEmitted`, `ToolInvoked`, `ToolCompleted`,
  `PermissionRequested`, `UsageReported` (con `MetricValue`), `RateLimitSignaled`, `QuotaReported`, `RunFinished` y, desde
  la Fase 4, `ContextObserved` (la ocupación de cada paso, deduplicada por id del mensaje, con la ventana del modelo de
  ese paso) y `CompactionObserved`.
- `ProviderCapabilities` evita suposiciones: el orquestador consulta si hay salida estructurada, reanudación,
  aprobación interactiva, tokens o cuota antes de planificar. `ReportsContextOccupancy` (Fase 4, falso por defecto) dice
  si el adaptador informa de la ocupación y las compactaciones: Claude sí; `codex exec` y Gemini, «no expuesto».
- `TokenUsage` (Fase 4) añade la caché escrita y el razonamiento (los dos «no expuesto» si el adaptador no los da, nunca
  un valor sin fuente), el desglose por modelo (`ModelUsage`) y los turnos. `InputTokens` es el prompt entero, con la caché
  leída y la escrita.
- `AgentRunRequest.McpServers` (Fase 4, nulo por defecto) lleva los servidores MCP locales de la sesión (`McpServerSpec`);
  un adaptador los rechaza para un perfil que escribe, tiene shell o red.

## 4. Ejecución de procesos

```csharp
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null, IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? Timeout = null, string? StandardInput = null);

public interface IProcessRunner
{
    // Timeout → mata el árbol y devuelve TimedOut. Cancelación → mata el árbol y lanza OperationCanceledException.
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken);
}
```

Regla de seguridad del contrato: el texto libre (prompts, contenidos) viaja por `StandardInput`, nunca como argumento.
La implementación rechaza metacaracteres en argumentos de `.cmd`/`.bat` (los CLIs de npm en Windows son shims `.cmd`).
El streaming de eventos línea a línea se añade en la Fase 2 junto con los adaptadores.

## 5. Juez y auditoría

```csharp
public sealed record DossierRequest(string ProjectId, AuditScope Scope, string? RunId = null);   // con RunId: una ejecución

public interface IJudgeDossierBuilder   // lo usa el sistema, nunca el orquestador
{
    Task<JudgeDossier> BuildAsync(DossierRequest request, CancellationToken cancellationToken);
}

public interface IJudge
{
    JudgeIdentity Identity { get; }
    Task<Verdict> EvaluateAsync(JudgeDossier dossier, CancellationToken cancellationToken);

    // La ronda de corrección: el juez ve su veredicto anterior y las reglas que incumplió. Solo hay una.
    Task<Verdict> ReviseAsync(JudgeDossier dossier, Verdict previous, IReadOnlyList<VerdictViolation> violations, CancellationToken cancellationToken);
}

public interface IIndependentReview     // Fase 3, módulo Debate: el motor solo pasa identificadores
{
    Task<ReviewOutcome> ReviewRunAsync(ReviewRequest request, IProgress<AgentEvent>? progress, CancellationToken cancellationToken);
}

public interface IProjectAuditService   // botón «Auditar proyecto completo» (Fase 5)
{
    Task<ProjectAuditProposal> PrepareAsync(ProjectAuditRequest request, CancellationToken ct);                      // no escribe nada
    Task<AuditReport> AuditAsync(ProjectAuditProposal proposal, IProgress<AgentEvent>? progress, CancellationToken ct); // con tu OK
}
```

`AuditReport.IsTrustworthy` solo es verdadero si un juez dio el veredicto (`VerdictId`), pasa el validador y el registro
de decisiones está íntegro. `IIndependentReview` se detalla en la [sección 14](#14-revisión-independiente-de-una-ejecución);
la auditoría, el auditor del orquestador y la impugnación, en la [sección 17](#17-fase-5-debates-juez-auditorías-e-impugnación).

## 6. Almacenes

- `IDecisionLogStore`: `AppendAsync` y `ReadAllAsync`. **No existe** operación de actualizar ni borrar, y la tabla
  SQLite lo impide además con triggers.
- `IDecisionLogTailReader` (2026-11-04), que el almacén SQLite del registro también implementa:
  `ReadTailAsync(projectId, DecisionLogMark? after)` lee en una sola transacción el sello del almacén (esta apertura del
  proyecto, la versión del esquema y la fecha de creación del fichero), si las entradas hasta la marca siguen ahí (cuántas
  son y el hash de la última, con los triggers append-only del registro y de la memoria tal como los crea el esquema) y
  las entradas posteriores.
  Devuelve `DecisionLogTail(Stamp, Continues, Entries)`; sin marca, el registro entero. Un sello igual no prueba que nada
  cambiara: la versión del esquema se puede volver a poner a mano y el fichero se puede sustituir conservando su fecha de
  creación. Por eso existen las lecturas enteras de `LogLoad.Full` ([sección 11](#11-registro-de-decisiones-y-payloads)).
- `IEvidenceStore.CaptureAsync(draft, Stream content)`: el almacén calcula el hash a partir de los bytes; quien llama no
  puede inventarse un hash para una evidencia inexistente. `EvidenceDraft` gana `RunId`, `TaskId`, `Attempt` y
  `MediaType` (todos opcionales), e `IEvidenceStore.ListByRunAsync(projectId, runId)` devuelve la evidencia de una
  ejecución.
- `IRequirementStore`: guarda y recupera conjuntos congelados por su `ContentHash`.
- `IPlanStore`: planes inmutables por el `ContentHash` de su contenido. Guardar uno que ya existe no escribe nada, y al
  leerlo se recalcula el hash.
- `IRunRegistry`: la cabecera inmutable de cada ejecución (`RunRecord`: plan, requisitos, petición, commit base, perfil de
  verificación congelado, plantilla de agentes, opciones). **No guarda el estado**: sale del registro de decisiones
  ([ADR-0010](adr/0010-estado-derivado-del-registro.md)).
- `IVerdictStore`: veredictos append-only (`StoredVerdict`). Uno inválido se guarda tal cual lo dio el juez, con sus
  incidencias; una corrección apunta al veredicto que sustituye (`Supersedes`) y ese sigue en el almacén.
- `IAgentRoster`: la plantilla de agentes del proyecto.

## 7. Seguridad, memoria y uso

- `IApprovalGate.RequestAsync(ApprovalRequest)`: acciones `Destructive` o `Critical` siempre pasan por una persona;
  petición y respuesta van al registro de decisiones. `ApprovalRequest` gana `ProjectId`, `RunId` y `TaskId`
  (opcionales) para que la respuesta se registre en su sitio; `IApprovalGate` no cambia.
- `IApprovalPrompt.AskAsync(ApprovalRequest)`: lo que la puerta llama para preguntar a una persona (lo implementa
  `Desktop`). No contestar, cancelar o fallar al preguntar es denegar.
- `ICredentialVault`: secretos propios de Agent Studio (p. ej. una API key opcional del usuario) cifrados con DPAPI.
  Nunca toca las credenciales de los CLIs.
- Memoria y uso (Fase 4): ver la [sección 16](#16-fase-4-consumo-cuota-memoria-y-skills). `IUsageLedger` se retira: el
  registro de decisiones ya es el libro del uso ([ADR-0019](adr/0019-consumo-como-proyeccion-del-registro.md)).

## 8. Tareas condicionales (Fase 3)

Decisión y alternativas: [ADR-0011](adr/0011-tareas-condicionales.md). El contrato del dominio:

```csharp
public enum WorkTaskStatus { Pending, Running, Blocked, AwaitingApproval, Completed, Failed, Cancelled, Skipped }
public enum JoinPolicy { AllCompleted, AnyCompleted }

// WorkTask: parámetro opcional `JoinPolicy join = AllCompleted`.
// Transiciones nuevas: Pending→Skipped y Blocked→Skipped. Terminales: Completed, Cancelled y Skipped.
// TransitionTo(Skipped) se rechaza ("task.skip-via-graph"): solo el grafo omite tareas.

public sealed record DependencyEdge(TaskId Prerequisite, string? Branch);

// TaskGraph
void AddDependency(TaskId dependent, TaskId prerequisite, string? branch = null);  // rama: obligatoria si el prerrequisito es Condition, prohibida si no
IReadOnlyList<DependencyEdge> EdgesOf(TaskId dependent);
void CompleteCondition(TaskId condition, string takenBranch, DateTimeOffset at, string reason);  // Running→Completed y guarda la rama
string? TakenBranch(TaskId condition);
IReadOnlyList<TaskId> PropagateSkips(DateTimeOffset at);  // pasada topológica hasta el punto fijo; idempotente
```

Vista desde su dependiente, una arista está *satisfecha* (el prerrequisito se completó y, si hay rama, es la tomada),
*muerta* (se omitió, o se completó por otra rama), *rota* (cancelado, o fallido sin reintentos) o *pendiente*.

| Política | Lista cuando | Se omite cuando |
|---|---|---|
| `AllCompleted` | todas las aristas están satisfechas | alguna arista está muerta |
| `AnyCompleted` | ninguna arista está pendiente ni rota y al menos una está satisfecha | todas las aristas están muertas |

Una tarea sin prerrequisitos siempre está lista y nunca se omite. Un grafo sin condiciones no tiene aristas muertas, así
que se comporta exactamente como antes. Las ramas de los planes son `yes` y `no` (`ConditionBranches`). `Analyze` ve el
grafo como quedará tras `PropagateSkips`, sin cambiarlo.

## 9. Planes y su validación

```csharp
public sealed record TaskSpec(TaskId Id, string Title, WorkTaskKind Kind, AgentId? Agent, string Objective,
    string Instructions, IReadOnlyList<DependencySpec> DependsOn, JoinPolicy Join, IReadOnlyList<RequirementId> Covers,
    IReadOnlyList<string> WriteScope, IReadOnlyList<string> VerificationSteps, ConditionPredicate? Condition,
    string? ToolId, int MaxRetries, TimeSpan? Timeout, WorkspaceMode Workspace);

PlanDefinition.Create(summary, tasks, risks, PlanSource source, AgentId? author);  // forma canónica; no falla por el contenido
plan.ContentHash;                                                                  // canónico e independiente del orden
plan.BuildGraph(DateTimeOffset at);                                                // DependencyCycleException con su camino

PlanValidator.Validate(PlanDefinition plan, PlanContext context);  // → IReadOnlyList<PlanViolation(Code, Task, Message)>
```

- **Forma canónica.** Las tareas se ordenan por id; las dependencias, los requisitos cubiertos, el alcance y los riesgos
  se ordenan; los pasos de verificación **conservan su orden**, porque se ejecutan en él. El origen del plan y su autor
  no entran en el hash: el mismo contenido, venga del orquestador o del usuario, es el mismo plan.
- **El validador es la autoridad.** Es determinista, no llama a ningún agente y no confía en nada de lo que dijo el
  orquestador. Un plan sin violaciones siempre se puede convertir en grafo y ejecutar (hay una prueba de propiedad).
- **Orden estable.** Tamaño e identificadores, cada tarea, dependencias, requisitos, las reglas que necesitan el grafo y
  la independencia del juez. Mientras haya ids duplicados, dependencias desconocidas o ciclos se omiten las reglas que
  necesitan un grafo sano (ancestros de un predicado, unión de código, solapamiento de alcances), para no enterrar la
  causa bajo sus consecuencias.
- **El orquestador de `PlanContext`.** El contexto no lo nombra: se toman los agentes de la plantilla con rol
  `Orchestrator` y, si lo hay, el autor del plan.

| Grupo | Código | Regla |
|---|---|---|
| Plan | `EmptyPlan` | El plan no tiene tareas. |
| | `TooManyTasks` | Hay más tareas que `PlanLimits.MaxTasks` (20 por defecto). |
| Tareas | `InvalidTaskId` | El id no cumple `^[A-Za-z][A-Za-z0-9_-]{0,31}$` (seguro para refs y rutas). |
| | `DuplicateTaskId` | Un id aparece más de una vez. |
| | `TitleRequired` | La tarea no tiene título. |
| | `ObjectiveRequired` | Una tarea `AgentWork` no tiene objetivo. |
| Grafo | `UnknownDependency` | Depende de una tarea que no está en el plan. |
| | `DuplicateDependency` | Lista el mismo prerrequisito dos veces. |
| | `DependencyCycle` | Hay un ciclo (el mensaje trae el camino). |
| Agentes | `UnknownAgent` | El agente no está en la plantilla. |
| | `AgentRequired` | Una tarea `AgentWork` o `Review` no tiene agente. |
| | `OrchestratorHasTask` | Se asigna una tarea al orquestador, que solo planifica. |
| | `JudgeOutsideReview` | Un juez o un auditor tiene una tarea que no es `Review`. |
| | `ReviewerNotJudge` | Una tarea `Review` la tiene un agente que no es juez ni auditor. |
| | `JudgeVendorNotIndependent` | El agente de una tarea `Review` comparte vendor con el orquestador (o con quien escribió el plan) o con algún ejecutor `AgentWork` del plan. Es la decisión 7 de la Fase 3. |
| Condiciones | `ConditionWithoutPredicate` | Un nodo `Condition` sin predicado. |
| | `PredicateOnNonCondition` | Un predicado en un nodo que no es `Condition`. |
| | `PredicateNotAncestor` | El predicado cita una tarea que no es antecesora de la condición. |
| | `BranchRequired` | Una arista que sale de un `Condition` no dice la rama. |
| | `BranchOnNonCondition` | Una arista con rama que no sale de un `Condition`. |
| | `UnknownBranch` | La rama no es `yes` ni `no`. |
| Código | `FanInWithoutIntegration` | Una tarea que necesita un checkout (`AgentWork` con `ReadOnly` o `Write`, `ToolRun`, `Review`) se apoya en el código de dos o más antecesores maximales sin pasar por un nodo `Integration`. |
| Alcances | `UnsafeWriteScope` | Un alcance con `..`, ruta absoluta, `.git`, `.agentstudio`, comodines, caracteres que Windows no admite, segmentos acabados en punto o espacio, o que nombra la raíz. |
| | `WriteScopeRequired` | Una tarea `AgentWork` con `Write` no declara alcance. |
| | `OverlappingWriteScopes` | Dos tareas con `Write` que pueden correr a la vez tienen alcances que se solapan (comparación conservadora por prefijo). |
| Requisitos | `UnknownRequirement` | Cita un requisito que no está en el conjunto congelado. |
| | `MandatoryRequirementUncovered` | Ninguna tarea cubre un requisito obligatorio. |
| Referencias | `UnknownVerificationStep` | Cita un paso que no está en el perfil de verificación. |
| | `ToolRequired` | Una tarea `ToolRun` no dice qué herramienta ejecutar. |
| | `UnknownTool` | El `ToolId` no está en el perfil. |
| Límites | `RetriesNotAllowed` | `Review` o `HumanApproval` con `MaxRetries` distinto de 0. |
| | `RetriesOutOfRange` | `MaxRetries` negativo o por encima de `PlanLimits.MaxRetriesPerTask` (3). |
| | `InvalidTimeout` | Un tiempo máximo que no es positivo. |
| | `DebateNotAvailable` | Un nodo `Debate`, que llega en la Fase 5. |

Los alcances (`WriteScope`) son carpetas relativas con barra final (`src/Calc/`) o ficheros exactos; se comparan sin
distinguir mayúsculas, como en Windows. `WriteScope.Contains` es lo que comprueba las rutas cambiadas de un diff.

## 10. Reanudación por reproducción

Decisión: [ADR-0010](adr/0010-estado-derivado-del-registro.md).

```csharp
public abstract record RunEvent(DateTimeOffset At);
public sealed record TaskTransitioned(DateTimeOffset At, TaskId Task, WorkTaskStatus From, WorkTaskStatus To,
    string Reason, IReadOnlyList<EvidenceId> Evidence) : RunEvent(At);
public sealed record ConditionResolved(DateTimeOffset At, TaskId Task, string Branch) : RunEvent(At);
public sealed record RunReplay(TaskGraph Graph, IReadOnlyList<string> Problems) { public bool IsResumable { get; } }

RunStateReplayer.Replay(PlanDefinition plan, DateTimeOffset createdAt, IEnumerable<RunEvent> events, string? expectedPlanHash = null);
```

**Contrato de los eventos** (lo que el motor tiene que escribir, y en este orden):

- Un `TaskTransitioned` por cada entrada `TaskStatusChanged`. `Evidence` es lo que completa la tarea.
- Para una condición, un `ConditionResolved` (la entrada `ConditionEvaluated`) **justo antes** del
  `TaskTransitioned(Running→Completed)` de esa condición. La rama vale para esa transición; cualquier otra transición de
  la tarea la descarta.
- Una omisión se registra como una transición más (`→Skipped`), pero la reproducción no la aplica: la recalcula con
  `PropagateSkips` y comprueba que la tarea queda omitida. Que el grafo omita también una tarea cuya entrada no llegó a
  escribirse (la app murió entre dos entradas de la misma pasada) no es un error: es determinista y el motor la repite.

**Comprobaciones** además de las reglas de `WorkTask` y `TaskGraph`: cada transición parte del estado que tiene el
grafo (un evento que falta, repetido o reordenado se nota); una tarea solo sale de `Pending` hacia `Running`,
`AwaitingApproval` o `Blocked` si sus aristas estaban satisfechas; una condición solo se completa con la rama anunciada;
y, si se pasa `expectedPlanHash`, el plan es el de la ejecución.

La reproducción se detiene en el primer problema y devuelve el grafo tal como estaba justo antes. Con `Problems` no
vacío la ejecución no es reanudable y el motor registra un `AuditAlertRaised`. Las tareas que estaban en `Running` siguen
en `Running` en el grafo devuelto: pasarlas a `Blocked(Interrupted)` es el paso de recuperación del motor, no de la
reproducción.

## 11. Registro de decisiones y payloads

```csharp
public enum LogLoad { Incremental, Full }

public interface IDecisionRecorder
{
    Task<DecisionLogState> LoadAsync(string projectId, CancellationToken cancellationToken);   // lee, verifica y devuelve la cabeza (Incremental)
    Task<DecisionLogState> LoadAsync(string projectId, LogLoad load, CancellationToken cancellationToken);
    Task<DecisionEntry> RecordAsync(string projectId, Actor actor, DecisionKind kind, string summary,
        DecisionPayload payload, long? corrects = null, CancellationToken cancellationToken = default);
}
```

`DecisionRecorder` es el único escritor del registro de un proyecto:

- La secuencia y el hash los asigna `DecisionLog`; la hora es la mayor entre «ahora» y la de la última entrada, así que
  un reloj que retrocede no rompe la cronología.
- Hay un cerrojo por proyecto: escritores concurrentes obtienen secuencias contiguas y proyectos distintos no se esperan.
- Si el almacén falla, el recorder vuelve a leer el registro de él: la secuencia de la entrada fallida se reutiliza si no
  llegó a guardarse, y se continúa tras ella si se guardó.
- Un registro que no verifica (`LoadAsync` lo devuelve con su `Verification`) no se extiende:
  `RecordAsync` lanza `DecisionLogIntegrityException`.
- Desde el 2026-11-04 verifica el registro a medida que crece ([ADR-0010](adr/0010-estado-derivado-del-registro.md),
  adenda de esa fecha). Con un almacén que lee la cola (`IDecisionLogTailReader`), una carga `Incremental` lee y verifica
  solo lo añadido desde lo último verificado, y comprueba el ancla en cada carga. `LogLoad.Full` lee y verifica el
  registro entero desde su primera entrada. Lo piden los momentos críticos, donde una decisión o un agente se apoyan en el
  registro, y todo lo que ve un juez: la entrega, el dosier del juez, la revisión independiente, el arranque y la
  reanudación de una ejecución, la planificación, el dosier, el veredicto y el cierre de un debate, la auditoría del
  proyecto (propuesta, arranque, dosier y cierre) y la impugnación de un veredicto (prepararla, `VerdictChallenged` y la
  aceptación de su revisión). Una carga incremental también verifica entero si la última verificación entera fue hace
  5 minutos o más (`IncrementalAgeBeforeFullVerification`, con el `TimeProvider`), tras 256 cargas desde la cola
  (`IncrementalLoadsBeforeFullVerification`) y al volver a abrir el proyecto. Lo verificado se olvida al cerrar el
  proyecto (`DecisionRecorder.Forget`, que el contenedor engancha a `ProjectDatabases.Closed`).
- `DecisionLogState.WholeVerification` (2026-11-05) dice en qué verificación entera se apoya el estado: un valor nuevo, que
  el registrador no había dado antes, cada vez que verifica el registro entero, y el mismo mientras las cargas solo
  verifican lo añadido. Quien guarda algo verificado contra el registro (la memoria) lo comprueba otra vez cuando cambia.

**Formato de los payloads.** `DecisionPayloadSerializer` los escribe siempre igual, porque su texto entra en el hash:
camelCase, compactos, enums por nombre, ids como cadenas, sin miembros nulos, texto sin escapar más de lo que exige JSON y
sin depender de la cultura. Todos llevan `V` (versión de su forma; cambiar un payload exige subirla) y `RunId`. **Ningún
payload lleva prompts ni salida de agentes**: llevan el id y el SHA-256 de la evidencia. Las entradas que no son de
ninguna ejecución (la memoria y la aceptación de un historial como nuevo origen) llevan `RunId` vacío
(`DecisionPayload.NoRun`): la reproducción, el dosier del juez y la lista de ejecuciones seleccionan por ejecución y no
las ven.

| Tipo de entrada | Payload |
|---|---|
| `RunStarted`, `RunFinished` | `RunStartedPayload` (hashes de plan, requisitos y perfil, commit base, plantilla), `RunFinishedPayload` |
| `PlanCreated`, `PlanRevised` | `PlanCreatedPayload`, `PlanRevisedPayload` |
| `TaskCreated`, `TaskAssigned`, `TaskStatusChanged` | `TaskCreatedPayload`, `TaskAssignedPayload`, `TaskStatusChangedPayload` (el que lee la reproducción) |
| `InstructionsIssued` | `InstructionsIssuedPayload` (id y SHA-256 del prompt, requisitos, alcance, skills) |
| `AgentRunStarted`, `AgentRunFinished` | `AgentRunStartedPayload`, `AgentRunFinishedPayload` (con el uso como `UsageSnapshot`, cada cifra con su procedencia, y `AttemptOutcome`, que añade `Interrupted`). La v2 de la Fase 4 se describe en la [sección 16](#16-fase-4-consumo-cuota-memoria-y-skills) |
| `MemoryRecorded`, `MemoryReviewed` | `MemoryRecordedPayload` (sin texto: id, categoría, origen, SHA-256 canónico, fuente y entrada sustituida), `MemoryReviewedPayload` (decisión y quién); los dos con `RunId` vacío |
| `EvidenceRegistered` | `EvidenceRegisteredPayload` |
| `WorktreeCreated`, `WorktreeRemoved` | `WorktreeCreatedPayload`, `WorktreeRemovedPayload` |
| `IntegrationPerformed` | `IntegrationPerformedPayload` (también la entrega a la rama del usuario) |
| `ConditionEvaluated` | `ConditionEvaluatedPayload` (predicado, hechos, resultado y rama) |
| `ReviewRequested`, `VerdictIssued` | `ReviewRequestedPayload`, `VerdictIssuedPayload` (veredicto, validez, violaciones, hash de requisitos y cabeza del registro) |
| `ApprovalRequested`, `ApprovalGranted`, `ApprovalDenied` | `ApprovalRequestedPayload`, `ApprovalAnsweredPayload` |
| `AuditAlertRaised` | `AuditAlertPayload` |
| `ReplanRequested` (Fase 5) | `ReplanRequestedPayload` (hallazgos, tareas, mensaje y veredicto): existía sin payload y nadie lo escribía |
| `DebateOpened`, `DebateContributionRecorded`, `DebateRoundClosed`, `DebateClosed` (Fase 5) | `DebateOpenedPayload`, `DebateContributionRecordedPayload`, `DebateRoundClosedPayload`, `DebateClosedPayload` ([sección 17](#17-fase-5-debates-juez-auditorías-e-impugnación)) |
| `VerdictChallenged`, `ProjectAuditStarted`, `ProjectAuditFinished` (Fase 5) | `VerdictChallengedPayload`, `ProjectAuditStartedPayload`, `ProjectAuditFinishedPayload` |
| `RunExitChosen`, `RunDispatchChanged`, `AccessChecked` (Fase 8) | `RunExitChosenPayload` (la elección al salir con trabajo en marcha), `RunDispatchChangedPayload` (pausar o reanudar el despacho) y `AccessCheckedPayload` (la comprobación antes de seguir tras una espera). `AgentRunFinished` gana la v3 y `ApprovalGranted`/`ApprovalDenied`, la v2 ([sección 18](#18-fase-8-trabajo-desatendido)) |
| `ReviewSkipped`, `PermissionDenied`, `Correction` | Sin payload tipado (`DecisionPayloadSerializer.PayloadTypeFor` devuelve `null`) |

## 12. Orquestador y ejecuciones

Sustituyen al contrato de la Fase 0 (`IOrchestrator.ExecuteAsync(projectId, TaskGraph, …)` y `OrchestrationProgress`),
que nunca se implementó: un `TaskGraph` sin especificaciones ni `RunId` no permite reanudar.

```csharp
public interface IOrchestrator
{
    Task<PlanProposal> PlanAsync(PlanningRequest request, IProgress<AgentEvent>? progress, CancellationToken ct);  // reserva el RunId; guarda plan y PlanCreated
    Task<IOrchestrationRun> StartAsync(RunRequest request, CancellationToken ct);
    Task<IOrchestrationRun> ResumeAsync(ProjectContext project, RunId run, CancellationToken ct);                 // por reproducción; gasta cuota, así que lo decide el usuario
    Task<IReadOnlyList<RunSummary>> ListRunsAsync(ProjectContext project, CancellationToken ct);                  // una no terminada y sin proceso se muestra «Interrumpida»
}

public interface IOrchestrationRun : IAsyncDisposable      // misma forma que IAgentRun
{
    RunId Id { get; }
    IAsyncEnumerable<OrchestrationEvent> Events { get; }   // un solo consumidor, con búfer
    Task<RunResult> Completion { get; }
    RunSnapshot Snapshot();
    Task PauseAsync(); Task ResumeDispatchAsync(); Task RetryBlockedAsync(TaskId task); Task CancelAsync(string reason);
}
```

- `RunRequest` lleva el plan aprobado y la decisión de aprobación (`ApprovalDecision`), el perfil de verificación que se
  congela y las `RunOptions` (3 tareas en paralelo, 12 ejecuciones de agente como máximo, abandono a los 10 minutos,
  latido de 5 segundos, espera de cuota hasta 15 minutos; `null` significa el valor por defecto del motor).
  `RunRequest.Follows` es la ejecución de seguimiento que continúa otra.
- `RunPhase`: `Running`, `Paused`, `WaitingForQuota`, `AwaitingHuman`, `Interrupted`, `Succeeded`, `Failed`, `Blocked`,
  `Cancelled`. «Interrumpida» no es un bloqueo: el grafo está bien y solo falta reanudar.
- Eventos: `TaskStatusChangedEvent`, `TaskQueuedEvent` (en cola del proveedor), `AgentActivityEvent`,
  `EvidenceCapturedEvent`, `GraphHealthEvent`, `ApprovalPendingEvent`, `RunPhaseChangedEvent` y `RunFinishedEvent`.
  Desde la tanda 2, `TaskQueuedEvent` también sale al lanzar la revisión del juez, aunque no haya cola: dice en qué
  proveedor corre, para que su contexto diga «no expuesto» desde el principio si es Codex
  ([§19](#19-tanda-2-barras-en-tiempo-real-y-pulido-2026-11-03)).
- `RunSnapshot` lleva, desde la tanda 2, `MaxAgentRuns`: el presupuesto de la ejecución, para enseñar las ejecuciones
  usadas frente a él.
- `OrchestrationPaths(WorktreeRoot, DossierRoot, SkillSessionRoot)` son las carpetas de trabajo, todas bajo
  `%LOCALAPPDATA%\AgentStudio`; las decide `Desktop`, porque `Orchestration` y `Debate` no pueden usar el código de
  `Providers`.

## 13. Worktrees, integración y verificación

```csharp
public sealed record WorktreeLease(string Path, string Name, string RefName, string BaseCommit, RunId Run,
    string RepositoryRoot, string AdminDirectory);

// IWorktreeManager (entre otros): lo que protege al repositorio del usuario
Task<UserRepositoryState> CaptureUserRepositoryStateAsync(string repoRoot, CancellationToken cancellationToken);
```

- **`WorktreeLease` lleva la raíz del repositorio y el directorio de administración del worktree**
  (`RepositoryRoot` y `AdminDirectory`). Todos los comandos de git que modifican algo usan `--git-dir` y `--work-tree`
  explícitos derivados de ellos, nunca el fichero `.git` del worktree, que un agente puede reescribir.
- **El repositorio del usuario no cambia durante la ejecución de un agente.** `CaptureUserRepositoryStateAsync` devuelve
  un `UserRepositoryState`: HEAD (commit y rama), ramas `refs/heads/*` (sin las del sistema, `agentstudio/*`), etiquetas
  `refs/tags/*`, stash, hash del índice y estado `status --porcelain`. El motor lo captura antes y después de cada
  ejecución de agente; si `UserRepositoryState.Compare(antes, después)` devuelve diferencias, la tarea falla y se registra
  un `AuditAlertRaised` con ellas.

| Contrato | Qué garantiza |
|---|---|
| `IWorktreeManager` | Worktrees fuera del repositorio, separados y bloqueados; commits del sistema (el agente nunca hace commits); fusiones sin worktree con `merge-tree` y `update-ref` con valor esperado, así que `EnsureAsync` y `MergeIntoAsync` son idempotentes; solo toca worktrees propios. `CommitResult(Committed, Commit)` y `RemovalOutcome` (`Removed`, `AlreadyRemoved`, `RefusedDirty`) cierran lo que el diseño dejaba abierto. |
| `IIntegrator` | Fusiona en orden determinista y verifica en `_integration`. Un conflicto es un fallo con evidencia (`ConflictPaths`), nunca se resuelve solo. |
| `IDeliveryService` | Entrega la rama de integración a la rama del usuario (`FastForwardOnly` o `MergeCommit`). Siempre pasa por `IApprovalGate`. |
| `IWorkspaceCleanup` | Quita worktrees y refs de una ejecución; lo que tenga trabajo sin integrar pasa por `IApprovalGate`. |
| `IVerificationRunner` | Ejecuta los pasos del perfil de confianza y captura su salida como evidencia. Los resultados se leen del informe TRX o JUnit, nunca de la consola. |

`VerificationProfile` se congela al empezar la ejecución y nunca se lee de un worktree; su `ContentHash` identifica los
comandos exactos. Receta verificada para el proyecto de pruebas de este repositorio (xUnit v3 sobre Microsoft.Testing.Platform,
spike S4): un paso `dotnet` con los argumentos `test --no-build --results-directory <carpeta> -- --report-xunit-trx
--report-xunit-trx-filename <nombre>.trx --zero-tests-policy strict`, `Report = Trx` y `ReportPath = <carpeta>\<nombre>.trx`
(o `--report-xunit-junit` con `--report-xunit-junit-filename`). `--results-directory` va antes de `--`, y el código de salida
no basta: una ejecución con todas las pruebas omitidas sale con 0, por eso se lee el informe y `TestRunSummary.Succeeded`
exige al menos una prueba correcta.

## 14. Revisión independiente de una ejecución

`IIndependentReview.ReviewRunAsync(ReviewRequest)` es un caso de uso del módulo `Debate`. El motor solo le pasa
identificadores (`ReviewRequest(Project, Run, ReviewTask, Judge, WorkspacePath)`): construir el dossier es
responsabilidad exclusiva de `Debate`, y eso hace que el compilador impida que el orquestador filtre lo que ve el juez.

1. Registra `ReviewRequested` y verifica el registro: si no está íntegro, el resultado es `NeedsHuman`.
2. Construye el dossier desde los almacenes (`DossierRequest` con el `RunId`). `ExcludedVendors` contiene los vendors de
   **todos** los que produjeron lo revisado, el orquestador y los ejecutores, calculados desde el registro de la ejecución.
   Con dos vendors, el juez usa el que no ha producido nada.
3. Lanza el juez en solo lectura, valida el veredicto con `VerdictValidator` sin cambiarlo, permite **una** corrección
   (`IJudge.ReviseAsync`) y guarda cada veredicto en `IVerdictStore`: uno inválido se guarda tal cual, con sus incidencias.
4. `ReviewOutcome.Disposition`: `Approved` completa la tarea de revisión; `ChangesRequested` y `Rejected` la hacen fallar;
   `NeedsHuman` la deja esperando aprobación.

## 15. Skills por agente

`ISkillProvisioner.ProvisionAsync(projectId, agent, runKey, workingDirectory)` decide qué skills recibe un agente en una
ejecución y las prepara para su CLI; `Release` quita lo que creó y es seguro llamarlo siempre, también si la ejecución
falla o se cancela.

- Las activaciones se resuelven con `SkillActivationResolver`: Agente sobre Proyecto sobre Global; sin ninguna
  decisión, la skill no se activa. Si un ámbito tiene varias decisiones para la misma skill, vale la última.
- El catálogo (`ISkillCatalog`) dice dónde están los ficheros de cada skill; una skill enlazada (`LocalPath` nulo) no se
  puede materializar.
- Las skills se entregan en orden alfabético por id. Lo que no se puede proveer se avisa en castellano en
  `ProvisionedSkills.Warnings` (una skill enlazada, o activada pero que ya no está en el catálogo) y nunca se descarta
  en silencio.
- `ISkillMaterializer` crea la carpeta de sesión (un plugin para Claude Code, `.agents/skills` en el worktree para Codex
  y Gemini) sin escribir nunca en la configuración de los CLIs del usuario.

## 16. Fase 4: consumo, cuota, memoria y skills

Lo que deja fijado el paquete P0 de la Fase 4 ([14](14-fase-4-observabilidad-y-memoria.md), §5 y §9). Los paquetes WP1 a
WP5 trabajan en paralelo sobre estos contratos, y `tests/AgentStudio.Tests/Application/Phase4ContractTests.cs` falla si
uno cambia. Las decisiones están en [ADR-0019](adr/0019-consumo-como-proyeccion-del-registro.md) (consumo) y
[ADR-0022](adr/0022-memoria-append-only-anclada.md) (memoria).

### 16.1 Dominio

- `MetricOrigin` y `MetricAggregate<T>` en `Domain/Metrics` (ver la [sección 2](#2-dominio-las-reglas-que-todo-lo-demás-respeta)).
- `ContextHealth.cs` se mueve de `Observability/ContextHealth` a `Domain/Metrics`, con sus pruebas. Además de moverse,
  la ocupación toma el origen más débil de sus dos cifras: una ventana vista en una ejecución anterior la hace «estimado
  por Agent Studio», y un uso que no llegó, «sin dato».
- `AgentDefinition.Validate` gana `agent.judge-memory-none` y `agent.memory-proposals-not-available`.
- `DecisionKind` gana `MemoryRecorded` y `MemoryReviewed`, al final.

### 16.2 Payloads v2

```csharp
public enum AgentRunPurpose { Planner, Executor, Judge, SkillRecommendation, DailySummary }
public sealed record ContextPackReference(string Sha256, IReadOnlyList<string> Entries, IReadOnlyList<string> Omitted, int Characters);

// AgentRunStartedPayload v2 (PurposeVersion = 2): + AgentRunPurpose? Purpose, ContextPackReference? ContextPack.
// AgentRunFinishedPayload v2 (UsageVersion = 2): + DateTimeOffset? LaunchedAt, string? ReportedModel, string? ResumedSession,
//     ContextPeakSnapshot? ContextPeak, int? Compactions /* null = not exposed */, int? Turns.
// UsageSnapshot: + MetricSnapshot? CacheWriteInputTokens, MetricSnapshot? ReasoningOutputTokens, IReadOnlyList<ModelUsageSnapshot>? PerModel.
// MetricSnapshot: + MetricOrigin? Origin; ResolvedOrigin() reads a v1 figure by its provenance; ToInt64() and ToDecimal() rebuild the metric.
public sealed record ContextPeakSnapshot(string? Model, MetricSnapshot UsedTokens, MetricSnapshot ContextWindow);
public sealed record MemoryRecordedPayload(string Entry, MemoryKind Kind, MemoryOrigin Origin, string Sha256, string Source, string? Supersedes); // Announcing(...): RunId = ""
public sealed record MemoryReviewedPayload(string Entry, MemoryReview Decision, string DecidedBy, string? Comment);                            // Deciding(...): RunId = ""
```

- Todos los campos nuevos son opcionales y van al final: una entrada v1 se lee y se reescribe byte a byte.
- `UsageSnapshot.From` y `MetricSnapshot.From` siguen escribiendo la forma v1, la que usan `AgentRunner` y
  `JudgeRunRecorder` hasta que WP1 los pase a `UsageSnapshot.WithBreakdown` y `MetricSnapshot.WithOrigin`.
- `RecordsPurpose()` y `RecordsUsageWithOrigins()` dicen si una entrada es v2. Son métodos, no propiedades, para que el
  serializador no los escriba.
- Un `AgentRunFinished` cierra el último `AgentRunStarted` sin cerrar de la misma ejecución, tarea e intento.

### 16.3 Ejecuciones auxiliares

`IAuxiliaryAgentRunner.RunAsync(ProjectContext, AuxiliaryRunRequest(Purpose, Request), IProgress<AgentEvent>?, CancellationToken)`
lanza un agente fuera de una orquestación (D-08) y devuelve `AuxiliaryRunResult(Run, Outcome, Usage, PromptEvidence, AnswerEvidence)`.

- Solo para `SkillRecommendation` y `DailySummary`, y con un agente que el dominio acepta.
- Con un registro que no verifica no captura ni lanza nada (`DecisionLogIntegrityException`).
- `RunId` propio, `aux-yyyyMMdd-xxxxxx`, fuera de `orchestration_runs`.
- El prompt como evidencia y `AgentRunStarted` v2 antes del lanzamiento; `AgentRunFinished` v2 al terminar, con la hora
  real de arranque, el modelo del último paso (nunca el de `SessionStarted`, D-13), el pico de contexto y las
  compactaciones (nulas si el proveedor no informa del contexto). Un fallo al lanzar queda como intento fallido; una
  cancelación se registra antes de lanzar `OperationCanceledException`.

### 16.4 Consumo (`Application/Usage`)

```csharp
public interface IUsageReport { Task<UsageReport> QueryAsync(IReadOnlyList<string> projectRoots, UsageQuery query, CancellationToken ct); }
public interface IUsageExporter { Task WriteCsvAsync(UsageReport report, Stream destination, CancellationToken ct); Task WriteJsonAsync(UsageReport report, Stream destination, CancellationToken ct); }
public sealed record UsageQuery(DateTimeOffset From, DateTimeOffset To, TimeZoneInfo Zone, IReadOnlyList<UsageDimension> GroupBy, UsageFilter? Filter = null);
public sealed record UsageReport(IReadOnlyList<UsageAttempt> Attempts, IReadOnlyList<UsageGroup> Groups, IReadOnlyList<ExcludedProject> Excluded, UsageReconciliation Journal);
```

- `UsageDimension`: proveedor, vendor, modelo, sesión, ejecución, tarea, agente, propósito, proyecto, día, semana ISO y mes.
- `UsageAttempt` es la forma de docs/14 §5.3 más `RecordedVersion` (obligatorio): la versión del `AgentRunFinished` de
  donde salen sus cifras, para mostrar una v1 como «sin desglose».
- `UsageFigures` (entrada, caché leída, caché escrita, salida, razonamiento y coste), `ModelUsageFigures` y
  `ContextPeakFigures` llevan `MetricValue`. `UsageTotals` agrega con `MetricAggregate` (duración en milisegundos, tokens,
  coste y compactaciones) y cuenta intentos, límites de uso y de presupuesto.
- `UsageGroup(Key, Totals)`, con la clave en el orden de las dimensiones; `ExcludedProject(Root, ProjectId, Reason)`;
  `UsageReconciliation(JournalPrompts, RecordedAttempts, Warnings)` con `Difference`.

### 16.5 Cuota (`Application/Providers/QuotaContracts.cs`)

- `QuotaObservation(Provider, Window, Utilization, Length, ResetsAt, ObservedAt, Source)` con `Label` y `Reached`; la
  utilización es la del proveedor (0..1), nunca una calculada.
- `IQuotaObservations` (`AppendAsync`, `LatestAsync`): el registro del perfil, `%LOCALAPPDATA%\AgentStudio\usage\quota.jsonl`.
- `IProviderQuotaReader.ReadAsync` → `QuotaReadResult(Status, Observations, Reason)` con `PlanType`,
  `OrdinaryUsageAllowed` y `Warnings`; `QuotaReadStatus`: `Read`, `NotOptedIn`, `NotExposed` y `Missing`.

### 16.6 Memoria, empaquetado y resumen (`Application/Knowledge`)

- `IProjectMemory`: `RecordAsync(ProjectContext, MemoryDraft)` (primero `MemoryRecorded`, luego la fila),
  `ReviewAsync(ProjectContext, entry, MemoryReview, decidedBy, comment)` y `RecallAsync(ProjectContext, MemoryQuery)`
  (solo filas ancladas; `TrustedOnly` por defecto).
- `IMemoryStore` (`AppendAsync`, `SearchAsync` con `MemorySearch`, y desde el 2026-11-05 `FindAsync(projectId, ids)`, las
  filas de esos ids) guarda filas inmutables (`MemoryRow`) y no tiene estado. La memoria sirve desde la memoria del proceso
  lo que verificó contra el registro y solo lee las filas de lo que el registro anunció después
  ([ADR-0010](adr/0010-estado-derivado-del-registro.md), adenda del 2026-11-05).
- `IMemoryExporter.ExportAsync` → `MemoryExportResult`, o `MemoryExportResult.Refused(motivo)` durante una ejecución.
- `MemoryContent.Sha256(kind, topic, content)` es el SHA-256 canónico que anuncia `MemoryRecorded`, y
  `MemoryContent.MaxCharacters` (4 000) el límite de una entrada.
- `IContextPacker.PackAsync(ProjectContext, ContextPackRequest)` → `ContextPack` (con `ToReference()` para
  `AgentRunStarted` v2), o `ContextBudgetTooSmallException` si los requisitos obligatorios no caben.
  `ContextPackRequest.Desirable` lleva los requisitos deseables (nivel 4).
- `IDailySummaryService.SummarizeAsync(ProjectContext, DateOnly)` → `DailySummaryResult`.
- `IMemoryDerivation.DeriveAsync(ProjectContext)` → `MemoryDerivationResult(Recorded, AlreadyRecorded, Problems, Reason)`:
  deriva la memoria del registro verificado (D-18), una entrada por fuente, así que derivar dos veces no duplica nada.
  `MemorySources` fija la fuente: `registro:<ejecución>#<secuencia>` o `evidencia:<id>#sha256:<hash anclado>`; el texto
  de un agente solo se cita desde una evidencia anclada con su hash, y si no cuadra queda en `Problems` y una alerta
  `memory.evidence-tampered`. Con una ejecución o planificación en curso, o un registro que no verifica, no escribe nada
  y lo dice en `Reason`. No deriva las ejecuciones anteriores a la última aceptación de un historial como nuevo origen
  (la cuarentena de D-20). El escritorio la llama tras cada ejecución (y exporta después) y al abrir un proyecto que ya
  tiene `.agentstudio/agentstudio.db` (`ProjectMemoryUpkeep`): abrir una carpeta nunca crea su almacenamiento.

### 16.7 Proyectos y actividad

- `IProjectStorageReader.OpenForRead(projectRoot, relativePath)`: lectura con la misma política que las escrituras de
  `IProjectStorageFiles`. Es un puerto nuevo, así que ninguna implementación de las escrituras cambia.
- `IProjectLogReader.ReadAsync(projectRoot)` → `ProjectLogSnapshot(ProjectId, Root, Entries, Verification, Anchor)`, con
  `AnchorStatus` (`Anchored`, `NoHistory`, `Unanchored`, `AnotherProject`, `Unreadable`) e `IsTrusted`. No escribe, no
  migra, no mueve anclas y no sigue enlaces.
- `IRunActivity.IsBusy(projectId)`: hay una ejecución o una planificación del proyecto en este proceso. Lo informa
  `OrchestrationActivity` (no el orquestador, que formaría un ciclo con la memoria), y una ejecución libera su proyecto
  antes de que se vea su fin (el final de sus eventos o su `Completion`).

### 16.8 Skills

- `SkillCatalogSnapshot(At, Skills)` con `SnapshotSkill`: lo que lee el servidor MCP, solo metadatos del catálogo,
  sin cuerpos de `SKILL.md` ni rutas.
- `ISkillRecommendationService`: `Recommend` (determinista) y `AskAgentAsync(ProjectContext, task, QuotaConsent)` →
  `AssistedRecommendationResult(Recommendations, Warnings, Run)`. Nunca activa una skill.
- `ISkillHealthReader.ReadLocalAsync(CatalogSkill)`: salud local, sin red.

### 16.9 `AgentStudio.SkillsMcp` en las capas

- Es el duodécimo proyecto (D-30) y un segundo punto de entrada: un ejecutable `net10.0` que Claude Code lanza por stdio
  desde una ejecución auxiliar de solo lectura. Agent Studio nunca lo ejecuta dentro de su proceso.
- Como los módulos de [ADR-0002](adr/0002-capas-y-puertos.md), solo depende de `Application` (y, a través de ella, de
  `Domain`), además del paquete `ModelContextProtocol` 2.2.0 (`Microsoft.Extensions.Hosting` se quitó en la integración:
  no se usaba). Ningún módulo lo referencia. `Desktop` lo referencia sin usar sus tipos, solo para que su ejecutable
  llegue junto a la app.
- Las pruebas sí lo referencian, para probar sus herramientas sobre flujos en memoria (`InternalsVisibleTo`).
- Cómo llega el ejecutable junto a la app (decidido en la integración): la referencia de proyecto de `Desktop` copia el
  apphost, su `runtimeconfig`, su `deps.json` y sus dependencias a la salida, donde lo espera
  `SkillsScreenServices.DefaultServerExecutable`. Su `Program.cs` sirve las dos herramientas de solo lectura de WP4.

### 16.10 Diferencias con docs/14 §5

- `MetricAggregate<T>` guarda `Origin` como campo posicional y deriva `Provenance` (docs/14 lo escribía al revés): así un
  total del coste de Claude puede decir «estimado por el CLI», y no solo «estimado».
- `TokenUsage` y `UsageSnapshot` ganan también `ReasoningOutputTokens`, que D-04 cuenta entre las cifras y que docs/14 §5.2
  no listaba: sin él, el razonamiento no se podría registrar sin otra versión.
- `ContextPeakSnapshot` llama `UsedTokens` a los tokens del paso: ningún campo de un payload puede llamarse `Prompt…`
  salvo un id o un hash de evidencia.
- `IProjectMemory.ReviewAsync` recibe además el comentario que lleva `MemoryReviewedPayload`.
- `UsageAttempt` gana `RecordedVersion`; `ContextPackRequest`, `Desirable`.

## 17. Fase 5: debates, juez, auditorías e impugnación

Lo que deja fijado el paquete P0 de la Fase 5 ([15](15-fase-5-debate-arena-y-juez.md), §5 y §9). Los paquetes WP1 a WP6
trabajan en paralelo sobre estos contratos, y `tests/AgentStudio.Tests/Application/Phase5ContractTests.cs` falla si uno
cambia. Las decisiones están en [ADR-0026](adr/0026-debate-como-unidad-del-registro.md) (un debate es una unidad del
registro) y [ADR-0027](adr/0027-independencia-del-juez-en-debates-con-dos-vendors.md) (independencia del juez con dos
vendors). Lo que se aparta de docs/15 §5 está en la [§17.8](#178-diferencias-con-docs15-5).

### 17.1 Dominio

```csharp
// Debate/DebateJudging.cs
public enum DebateSubjectKind { None, Commit, Run, Verdict }
public enum DebateJudgingKind { IndependentJudge, CrossVendorPanel, NonProducerJudge }
public sealed record JudgingArrangement(DebateJudgingKind Kind, IReadOnlyList<JudgeIdentity> Judges,
    IReadOnlySet<VendorId> ProducerVendors, IReadOnlySet<VendorId> WaivedParticipantVendors)   // igualdad por contenido
{ public IReadOnlySet<VendorId> ExcludedVendors(IReadOnlyList<DebateParticipant> participants); } // P ∪ (participantes − dispensados)
public static class DebateJudgingRules
{
    public static bool IsChoice(DebateMode mode, DebateSubjectKind subject);   // libre, adversarial, arquitectura y tribunal, sin objeto o con commit
    public static IReadOnlyList<JudgingArrangement> Possible(DebateMode mode, DebateSubjectKind subject,
        IReadOnlyList<DebateParticipant> participants, IReadOnlySet<VendorId> producers, IReadOnlyList<JudgeIdentity> candidates);
    public static IReadOnlyList<string> Violations(JudgingArrangement arrangement, DebateMode mode, DebateSubjectKind subject,
        IReadOnlyList<DebateParticipant> participants);
}
public sealed record PanelOutcome(DebateOutcome Outcome, VerdictDecision? Decision, string? ChosenProposal, string Rule)
{ public ConfidenceLevel? Confidence { get; init; } public bool NeedsHuman { get; } }
public static class JudgingPanel   // Single, Agree, Disagree, Invalid, User; Rules
{
    public static PanelOutcome Combine(JudgingArrangement arrangement, IReadOnlyList<(Verdict Verdict, bool Valid)> verdicts);
    public static PanelOutcome DecidedByUser(VerdictDecision? decision, string? chosenProposal);   // «Decidir yo»
    public static DebateOutcome OutcomeOf(VerdictDecision decision);
    public static bool SameJudge(JudgeIdentity? left, JudgeIdentity? right);   // agente, proveedor y vendor; nunca el modelo
}

// Debate/DebateSession.cs (cambios)
public enum DebateJudgmentReason { Requested, Converged, Limit, TurnMissing, Tribunal }
// Open(id, mode, subject, question, requirements, participants, judging, limits, startedAt): la de la Fase 5.
// Open(id, mode, question, criteria, requirements, participants, judge, limits, startedAt, requireIndependentJudge): la de la Fase 0, se queda.
// RequestJudgment() = RequestJudgment(Requested); RequestJudgment(DebateJudgmentReason reason, DebateLimitReached limit = None).
// Close(Verdict) de la Fase 0 = Close(JudgingPanel.Combine(Judging, [(verdict, true)]), [verdict]).
// Close(PanelOutcome outcome, IReadOnlyList<Verdict> verdicts): los veredictos válidos (ninguno, uno o uno por juez).
// Nuevas propiedades: Subject, Judging, Judge (el primero), JudgmentReason, Panel, Verdicts (Verdict es el primero).

// Debate/DebateStateReplayer.cs
public sealed record DebateHeader(DebateId Id, DebateMode Mode, DebateSubjectKind Subject, string Question,
    FrozenRequirementSet Requirements, IReadOnlyList<DebateParticipant> Participants, JudgingArrangement Judging,
    DebateLimits Limits, DateTimeOffset StartedAt);
public abstract record DebateEvent(DateTimeOffset At);
public sealed record ContributionPublished(DateTimeOffset At, DebateContribution Contribution) : DebateEvent(At);
public sealed record RoundClosed(DateTimeOffset At, int Round, long TokensUsed, DebateJudgmentReason? Judgment,
    DebateLimitReached Limit = DebateLimitReached.None) : DebateEvent(At);
public sealed record DebateEnded(DateTimeOffset At, PanelOutcome Outcome, IReadOnlyList<Verdict> Verdicts) : DebateEvent(At);
public sealed record DebateAborted(DateTimeOffset At, string Reason) : DebateEvent(At);
public sealed record DebateReplay(DebateSession? Session, IReadOnlyList<string> Problems) { public bool IsResumable { get; } }
public static class DebateStateReplayer { public static DebateReplay Replay(DebateHeader header, IEnumerable<DebateEvent> events); }

// Debate/DebateBudget.cs
public sealed record TurnUsage(string Turn, int Attempt, MetricValue<long> InputTokens, MetricValue<long> OutputTokens, long Estimate);
public static class DebateBudget   // EstimateSource = "estimate: 4 chars/token"
{
    public static long EstimateOf(long characters);                    // los caracteres entre 4, redondeando hacia arriba
    public static MetricValue<long> TokensOf(TurnUsage turn);
    public static MetricAggregate<long> Used(IReadOnlyList<TurnUsage> turns);
    public static int ReportedTurns(IReadOnlyList<TurnUsage> turns);   // «parcial (n de m informados)»
    public static long NextRoundEstimate(IReadOnlyList<TurnUsage> previousRound, int participants);
    public static bool WouldExceed(DebateLimits limits, MetricAggregate<long> used, long nextRoundEstimate);
}

// Judging/Verdict.cs
public sealed record ProposalComparison(string Proposal, string Strengths, string Weaknesses);
public sealed record DebateResolution(string? ChosenProposal, IReadOnlyList<ProposalComparison> Comparison);   // null: «ninguna»
// Verdict: public DebateResolution? Resolution { get; init; }        (nula fuera de los debates)
public sealed record DebateDossierContext(DebateMode Mode, IReadOnlyList<string> Proposals, bool Choice);
// JudgeDossier: public DebateDossierContext? Debate { get; init; }   (nulo en la revisión de una ejecución y en la auditoría)
```

- **Disposiciones.** `Possible` ofrece primero los jueces independientes (en el orden de la plantilla), después los jurados
  cruzados (un juez de cada vendor de los participantes, en el orden ordinal de los vendors y todas las combinaciones) y por
  último los jueces no productores. Nunca sienta a un participante ni a un juez de un productor, y solo dispensa los vendors de
  sus jueces. Vacío cuando nadie puede juzgar.
- **Códigos estables de `Violations`**, en orden fijo y una vez cada uno: `debate.judge-required`, `debate.duplicate-judge`,
  `debate.judge-not-participant`, `debate.judge-producer`, `debate.judge-participant-not-waived`, `debate.waiver-of-producer`,
  `debate.waiver-not-participant`, `debate.single-judge`, `debate.panel-shape`, `debate.panel-in-review`,
  `debate.non-producer-shape` y `debate.non-producer-in-choice`.
- **`Combine`.** Un juez: su veredicto válido decide; uno inválido, ninguno, dos o uno de otro juez es `Invalid`
  (`EscalatedToUser`). Un jurado: todos válidos, con la misma cabeza del registro y los mismos requisitos, y la misma decisión
  y propuesta, deciden con la menor confianza (`Agree`); cabezas o requisitos distintos, un juez sin veredicto o un veredicto
  de otro son `Invalid`; cualquier otra cosa es `Disagree` (`Unresolved`). `NeedsHuman` es `Unresolved` o `EscalatedToUser`.
- **`Open` de la Fase 5.** Los criterios de aceptación salen de los requisitos (`R1/c1: descripción`). Cada modo debate solo
  sus objetos (`debate.subject-mode`): la auditoría de implementación y la revisión del orquestador, una ejecución; el
  adversarial también un veredicto (la impugnación); el tribunal, cualquiera; los demás, nada o un commit. La revisión del
  orquestador es solo de revisores (`debate.reviewer-roles`, también en la apertura de la Fase 0). La disposición pasa
  `Violations`, y la sesión guarda una copia de sus conjuntos.
- **`RequestJudgment(motivo, límite)`.** Solo `Limit` nombra un límite, y tiene que nombrarlo (`debate.judgment-limit`);
  `Tribunal` solo en un tribunal (`debate.judgment-tribunal`); `Converged` y `Tribunal` necesitan todas las contribuciones de
  la ronda 1 (`debate.missing-proposals`); `Requested`, `TurnMissing` y `Limit` juzgan lo que haya. La ronda no avanza.
  `AdvanceRound` registra `Limit` o `Tribunal` cuando manda a juicio, y deja el mismo estado que `RequestJudgment`.
- **`Close(outcome, verdicts)`** (`debate.close-needs-judgment-phase`, `debate.close-outcome`, `debate.close-rule`,
  `debate.verdict-from-other-judge`, `debate.verdict-requirements`, `debate.verdict-duplicate-judge`, `debate.verdict-heads`,
  `debate.close-unsupported`): cada veredicto es de un juez de la disposición (sin mirar el modelo), una vez por juez, sobre
  los requisitos del debate y con la misma cabeza; `Single` y `Agree` necesitan sus veredictos y que digan esa decisión;
  `Disagree` e `Invalid` solo cierran como «necesita a una persona»; `User` cierra con los veredictos que haya, también
  ninguno. `CloseReason` es el resumen del veredicto cuando hay uno solo y la regla en los demás casos.
- **Reproducción.** Aplica los eventos por la API pública; una ronda que no es la actual, una ronda cerrada sin juicio cuando
  el dominio la manda a juicio, o un límite sin juicio son problemas. `RoundClosed.At` es el instante en que se cerró la ronda,
  nunca uno posterior.
- **Presupuesto.** Cada intento cuenta entrada más salida con el origen más débil; si falta una cifra o todo es cero, cuenta
  el mayor entre lo que llegó y su estimación, «estimado por Agent Studio». `Used` suma todos los intentos (ninguno queda como
  hueco) y su origen es el más débil; `WouldExceed` es estrictamente más que el límite, y sin límite nunca.
- **Validador** (25 reglas). `DebateResolutionMissing`: en el modo libre y en la revisión de arquitectura, aprobar o pedir
  cambios exige una propuesta elegida. `DebateResolutionUnknown`: la propuesta elegida o una de la comparación no es de este
  debate (también un veredicto de la revisión de una ejecución que nombre una). Una resolución sin comparación o con un
  elemento nulo es `MalformedVerdict`. En un dossier cuyo `Debate.Choice` es verdadero, un requisito con criterios y todos
  `JudgeAssessment` no cae en `ApprovedOnAgentClaimsOnly`; en los demás, las reglas son las de la Fase 3.

### 17.2 Payloads (`Application/Decisions/DebatePayloads.cs`)

```csharp
// AgentRunPurpose gana DebateParticipant, DebateJudge y ProjectAudit, al final.
public sealed record DebateParticipantEntry(AgentId Agent, DebateRole Role, ProviderId Provider, VendorId Vendor, string? Model, string Label);
public sealed record DebateJudgeEntry(AgentId Agent, ProviderId Provider, VendorId Vendor, string? Model);
public sealed record DebateOpenedPayload(DebateMode Mode, string HeaderHash, string ApprovedProposalHash, string RequirementSetHash,
    EvidenceId Question, string QuestionSha256, DebateSubjectKind Subject, string? SubjectRef, string? SnapshotCommit,
    IReadOnlyList<DebateParticipantEntry> Participants, IReadOnlyList<DebateJudgeEntry> Judges, DebateJudgingKind Judging,
    IReadOnlyList<VendorId> ProducerVendors, IReadOnlyList<VendorId> WaivedParticipantVendors,
    int MaxRounds, long MaxDurationSeconds, long? MaxTotalTokens, int MaxAgentRuns, string? Challenges) : DecisionPayload;
public sealed record DebateContributionRecordedPayload(int Index, int Round, AgentId Author, ContributionKind Kind, int? RepliesTo,
    EvidenceId Evidence, string Sha256, IReadOnlyList<EvidenceId> Cites, IReadOnlyList<string> UnresolvedCitations, int Redactions,
    bool Finished, string Turn, int Attempt, DateTimeOffset AnsweredAt) : DecisionPayload;
public sealed record DebateRoundClosedPayload(int Round, DebatePhase Next, DebateJudgmentReason? Judgment, DebateLimitReached Limit,
    MetricSnapshot TokensUsed, bool TokensIncludeEstimates, long ElapsedSeconds, string Reason) : DecisionPayload;
public sealed record DebateClosedPayload(DebateOutcome Outcome, IReadOnlyList<string> Verdicts, string Rule, string? ChosenProposal,
    string Reason) : DecisionPayload;   // AbortedRule = "aborted"
public sealed record VerdictChallengedPayload(string Verdict, string ChallengedBy, EvidenceId Reasons, string ReasonsSha256,
    string RevisionDebate) : DecisionPayload;   // RunId = la ejecución, el debate o la auditoría del veredicto impugnado
public sealed record ReplanRequestedPayload(IReadOnlyList<string> Findings, IReadOnlyList<TaskId> Tasks, string Message,
    string? Verdict) : DecisionPayload;         // RunId = la ejecución
public sealed record ProjectAuditStartedPayload(string HeaderHash, AuditScope Scope, string? HeadCommit, bool UncommittedChanges,
    string RequirementSetHash, string VerificationProfileHash, DebateJudgeEntry Judge, IReadOnlyList<VendorId> ProducerVendors,
    IReadOnlyList<VendorId> DeclaredAuthors) : DecisionPayload;
public sealed record ProjectAuditFinishedPayload(IReadOnlyList<string> Verdicts, ReviewDisposition Disposition, bool Trustworthy,
    string Summary) : DecisionPayload;
```

- Todos empiezan en `V = 1` y su `RunId` es el debate (`deb-…`) o la auditoría (`aud-…`) salvo donde se dice. Ninguno lleva
  texto de un agente: `Reason`, `Message` y `Summary` los escribe el sistema.
- `DecisionPayloadSerializer` mapea los siete tipos nuevos y `ReplanRequested`; nada más cambia. Los payloads v1 y v2 de las
  Fases 3 y 4 se leen y se escriben byte a byte (sus pruebas doradas no cambian), y los nuevos tienen su texto exacto y el
  hash de una entrada calculado por una implementación independiente (`DebatePayloadTests`).

### 17.3 Debate (`Application/Coordination`, sustituye al contrato de la Fase 0)

```csharp
public sealed record DebateSubject(DebateSubjectKind Kind, string? Reference);
public sealed record DebateParticipantRequest(AgentId Agent, DebateRole Role);
public sealed record DebateRequest(ProjectContext Project, DebateMode Mode, string Question, FrozenRequirementSet Requirements,
    DebateSubject Subject, IReadOnlyList<DebateParticipantRequest> Participants, IReadOnlyList<AgentId> Judges,
    DebateJudgingKind Judging, DebateLimits Limits, int? MaxAgentRuns = null);
public sealed record DebateEstimate(int MaxAgentRuns, MetricValue<long> Tokens, IReadOnlyList<string> Notes);
public sealed record DebateProposal(DebateRequest Request, JudgingArrangement Judging, IReadOnlyList<string> Problems,
    DebateEstimate Estimate, string HeaderHash, string ApprovalText);   // HeaderHash = DebateHeaderHash.Proposal(...)
public interface IDebateCoordinator { PrepareAsync, OpenAsync, ResumeAsync, ListAsync, ReadAsync }   // firmas de docs/15 §5.3
public interface IDebateRun : IAsyncDisposable
{
    RunId Id { get; } IAsyncEnumerable<DebateProgress> Events { get; } Task<DebateResult> Completion { get; } DebateSnapshot Snapshot();
    Task RequestJudgmentAsync(); Task RetryTurnAsync(AgentId participant); Task AddHumanEvidenceAsync(string title, string text);
    Task RunTestsAsync(); Task DecideAsync(VerdictDecision? decision, string? chosenProposal, string comment); Task AbortAsync(string reason);
}
public sealed record JudgedVerdict(string Id, Verdict Verdict, IReadOnlyList<VerdictViolation> Violations, bool Corrected);
public sealed record DebateJudgmentResult(IReadOnlyList<JudgedVerdict> Verdicts, PanelOutcome Outcome);
public interface IDebateJudgment
{ Task<DebateJudgmentResult> JudgeAsync(ProjectContext project, RunId debate, IProgress<AgentEvent>? progress, CancellationToken ct); }
```

Lo que enseñan las pantallas de D-41 a D-43, todo de reproducir el registro:

| Registro | Campos |
|---|---|
| `DebateSummary` (historial) | `Debate`, `OpenedAt`, `Mode`, `Subject`, `Question` (leída con su hash; nula si no se puede), `Phase`, `Outcome`, `Decision`, `ChosenProposal`, `Interrupted` («Reanudar»), `Challenged`, `Challenges` (el veredicto que revisa) y `Problems` |
| `DebateSnapshot` (sala) | `Debate`, `Mode`, `Subject`, `Phase`, `Outcome`, `Round`, `OpenedAt`, `Elapsed`, `Limits`, `Tokens` (`MetricAggregate<long>`, «~» si es estimado), `ReportedTurns`, `AgentRuns`, `MaxAgentRuns`, `Participants`, `Contributions`, `TurnsInFlight` (la ronda sellada), `LimitReached`, `JudgmentReason`, `Awaiting`, `CanRunTests` y `Problems` |
| `DebateTranscript` (debate leído) | `Summary`, `RequirementSetHash`, `Judging`, `WaivedParticipantVendors`, `Participants`, `Contributions` (con su texto), `Verdicts`, `Outcome`, `LimitReached`, `JudgmentReason` y `Problems` |
| `DebateResult` (`Completion`) | `Debate`, `Phase`, `Outcome`, `Panel`, `Verdicts`, `LimitReached`, `JudgmentReason` y `Summary` |
| `DebateParticipantView` | `Label`, `Role`, `Agent`, `Provider`, `Vendor` y `Model`: la correspondencia que el juez no ve |
| `DebateContributionView` | `Label` (`C1`…), `Index`, `Round`, `Author` (`P1`…), `Kind`, `RepliesTo`, `Evidence`, `Sha256`, `Text` (leído con su hash; nulo si no cuadra), `Cites`, `UnresolvedCitations`, `Redactions`, `Finished` y `AnsweredAt` |
| `DebateAwaiting(Reason, Message, Participant, Until)` | `DebateAwaitReason`: `FailedTurn` (D-22), `ProviderLimit` y `HumanResolution` («Decidir yo») |
| `DebateProgress` | `DebateTurnStarted`, `DebateTurnFinished` (sin texto), `DebateRoundSealed`, `DebateContributionPublished`, `DebateRoundEnded`, `DebateJudgeLaunched`, `DebateVerdictReceived`, `DebateAwaitingUser` y `DebateFinished` |

### 17.4 Auditoría e impugnación (`Application/Judging`)

```csharp
public enum AuditScope { WholeProject, CurrentPlan, OrchestratorDecisions, Debate }   // Debate, al final
public sealed record ProjectAuditRequest(ProjectContext Project, string RequirementSetHash, AgentId Judge, IReadOnlyList<VendorId> DeclaredAuthors);
public sealed record ProjectAuditProposal(ProjectAuditRequest Request, string? HeadCommit, bool UncommittedChanges, AgentDefinition? Judge,
    IReadOnlyList<VendorId> ProducerVendors, VerificationProfile? Verification, IReadOnlyList<string> SkippedSteps,
    MetricValue<long> EstimatedTokens, IReadOnlyList<string> Problems, string HeaderHash, string ApprovalText) { public bool CanJudge { get; } }
public sealed record PendingProblem(string Code, string Source, string Message);
public sealed record AuditReport(RunId Audit, AuditScope Scope, string? HeadCommit, bool UncommittedChanges, Verdict Verdict, string? VerdictId,
    IReadOnlyList<VerdictViolation> ValidationIssues, bool Corrected, bool DecisionLogIntact, ReviewDisposition Disposition,
    IReadOnlyList<EvidenceRecord> Verification, IReadOnlyList<PendingProblem> Pending, DateTimeOffset GeneratedAt)
{ public bool IsTrustworthy => ValidationIssues.Count == 0 && DecisionLogIntact && VerdictId is not null; }
public interface IProjectAuditService { PrepareAsync(ProjectAuditRequest, ct); AuditAsync(ProjectAuditProposal, IProgress<AgentEvent>?, ct); }
public sealed record OrchestratorFinding(string Code, Severity Severity, IReadOnlyList<TaskId> Tasks, IReadOnlyList<long> Entries, string Message);
public sealed record OrchestratorAuditReport(RunId Run, DecisionLogVerification Log, IReadOnlyList<OrchestratorFinding> Findings,
    IReadOnlyList<long> Recorded, bool ReplanRequested);
public interface IOrchestratorAuditService { Task<OrchestratorAuditReport> AuditRunAsync(ProjectContext project, RunId run, CancellationToken ct); }
public sealed record VerdictChallengeRequest(ProjectContext Project, string Verdict, string Reasons, DebateRequest Revision);
public enum ChallengeStatus { None, Pending, Confirmed, Revoked, AwaitingAcceptance, Accepted, DecidedByUser }
public interface IVerdictChallenges { PrepareAsync, ChallengeAsync, StatusAsync, AcceptRevisionAsync }   // firmas de docs/15 §5.4
```

### 17.5 Proveedores, almacenes y actividad

- `AgentRunRequest` gana `bool PersistSession = true` y `string? DebateId = null`, al final. `PersistSession = false` es
  `--no-session-persistence` en Claude y `--ephemeral` (con `-c history.persistence=none`) en Codex; junto con
  `ResumeSessionId` lo rechazan los adaptadores (WP4).
- `JournalingAgentProvider` pasa `DebateId` a `PromptJournalEntry.DebateId` y añade `#debate` (prefijo `deb-`) o
  `#auditoria` (prefijo `aud-`) antes de la etiqueta del agente, sin repetir una que ya venga.
- `AgentRunObservations(reportsContext, resumedSession, launchedAt)` (`Observe(evento, cuándo)`, `LaunchedAt`, `SessionId`,
  `Usage`, `Peak`, `Compactions`, `ReportedModel(usage)` y `PeakSnapshot(usage)`) sustituye a las copias del ejecutor
  auxiliar y del registrador del juez, con el mismo resultado: la hora dada o la del primer evento, el modelo del último paso
  y las compactaciones solo si el CLI informa del contexto. Es seguro entre hilos.
- `PromptData` es público en `AgentStudio.Application.Prompts` y ya no está en el motor; `ExecutorPrompt` y `PlannerPrompt`
  solo cambian su `using`.
- `DebateParticipantDefinition(Agent, Role, Label)`, `DebateRecord`, `ProjectAuditRecord`, `IDebateRegistry` e
  `IProjectAuditRegistry` con las formas de docs/15 §5.5 (`CreateAsync`: la misma fila dos veces no escribe nada; otra con el
  mismo id se rechaza).
- `DebateHeaderHash` (`Application/Debate`): `Of(DebateRecord)` y `Of(ProjectAuditRecord)`, los hashes que anclan
  `DebateOpened` y `ProjectAuditStarted`; `Proposal(DebateRecord, questionSha256)` y `Proposal(ProjectAuditRecord, requestSha256)`,
  los de lo que se enseña antes de escribir nada, que ata la aprobación. Versiones `debate-header/1`, `debate-proposal/1`,
  `audit-header/1` y `audit-proposal/1`; cada agente con la codificación de `RosterHash` como plantilla de uno; participantes y
  jueces en su orden; los conjuntos de vendors ordenados; el id y el proyecto no entran.
- `IProjectActivity : IRunActivity` con `TryEnter(projectId, kind)` (nulo si otra actividad tiene el proyecto) y
  `Current(projectId)`. La planificación y la ejecución del orquestador pueden compartir el proyecto; un debate o una
  auditoría no lo comparten con nada. La implementación no depende de nada (sin ciclo con la memoria) y una ejecución libera su
  proyecto antes de que se vea su fin. P0 deja el contrato; la implementación y sus pruebas son de WP5.

### 17.6 Módulo `Debate`

- `JudgeBrief` (abstracta): `Evaluation`, `Correction`, `Schema` y `Read`, más `Purpose`, `Tags` y `DebateId` para la
  bitácora. `JudgeBrief.RunReview` es el de la revisión de una ejecución, el de la Fase 3 sin cambios. `JudgeLaunch` gana
  `JudgeBrief? Brief = null`, al final. Sea cual sea el brief, `AgentJudge` mantiene al juez en solo lectura y pone quién juzgó
  y cuándo.
- `JudgeRunScope(ProjectId, Run, EntryTask, EvidenceTask, Purpose)`, con `ForRunReview` (sin tarea en las entradas, la de
  revisión en las evidencias, propósito `Judge`), `ForDebate` y `ForAudit` (tarea `juez-<agente>` en las dos, propósitos
  `DebateJudge` y `ProjectAudit`) y `JudgeTask(agente)`.
- `JudgeRunRecorder(decisions, evidence, scope, agent, time, reportsContext, next)` y
  `VerdictKeeper(verdicts, decisions, projectId, run, task, scope).KeepAsync(judge, dossier, verdict, violations, supersedes, vendorNote)`
  salen de `IndependentReview` sin cambiar lo que registra (sus pruebas no se han tocado).

### 17.7 Migración 0005

`debate_sessions` y `project_audits`, append-only, con el SQL de docs/15 §6: ver [05 §4](05-modelo-de-datos.md#4-migraciones).

### 17.8 Diferencias con docs/15 §5

- **`IDebateRun.DecideAsync(decision, chosenProposal, comment)`**, que §5.3 no listaba: es «Decidir yo» (D-26), y sin él la
  tarjeta no tendría puerto para anotar tu resolución.
- **`PanelOutcome.Confidence`** (propiedad `init`), para «la menor de las dos confianzas» de D-14, y `NeedsHuman`, calculada.
- **`RoundClosed` gana `Limit`** (por defecto `None`): la reproducción tiene que saber qué límite cortó la ronda, que el
  payload ya lleva.
- **`DebateReplay.Session` es nula** cuando la cabecera no abre: no hay sesión que devolver.
- **La apertura de la Fase 5 no recibe criterios de aceptación**: salen de los requisitos (`R1/c1: descripción`), porque ni
  `DebateHeader` ni `DebateRequest` los llevan. Además comprueba el objeto de cada modo (`debate.subject-mode`) y los papeles
  de la revisión del orquestador (`debate.reviewer-roles`).
- **La impugnación** (objeto `Verdict`): `IsChoice` responde falso (el lado de las reglas de la Fase 3), `Possible` ofrece las
  dos familias y `Violations` no comprueba la familia, porque depende del objeto del veredicto impugnado, que el dominio no ve.
  WP2 pone `DebateDossierContext.Choice` desde ese objeto, y WP3 elige jurado para el veredicto de un jurado (D-39).
- **`DebateHeaderHash.Proposal`**, que §5.5 no listaba: el hash de «lo que se te enseña» de D-06, para que el coordinador
  (WP1) y la impugnación (WP3) aten la aprobación con la misma definición.
- **Los registros de solo lectura** (`DebateSummary`, `DebateSnapshot`, `DebateTranscript`, `DebateResult`, `DebateProgress`
  y los suyos, `DebateParticipantView`, `DebateContributionView`, `DebateAwaiting`) y **`ProjectAuditProposal`** quedan con
  los campos de la §17.3 y la §17.4.
- **`JudgeRunScope`**, que §5 no nombraba: fija la clave de D-44 (`juez-<agente>`) para WP2 y WP3.
- **`DebateBudget`** gana `EstimateOf`, `TokensOf`, `ReportedTurns` y `NextRoundEstimate` (la proyección de D-20). `Used`
  cuenta cada intento con una cifra, informada o estimada, así que su `MetricAggregate` no tiene huecos; «parcial (n de m
  informados)» sale de `ReportedTurns`.
- **Pruebas que hubo que tocar fuera de lo nuevo**, sin cambiar ninguna aserción dorada: los registros de
  `DecisionPayloadTests` (las muestras ganan las de la Fase 5, los propósitos ganan los tres nuevos, `ReplanRequested` sale de
  los tipos sin payload y la comprobación de campos gana `DebateParticipantEntry` y `DebateJudgeEntry`), la línea de
  `ContractSurfaceTests` que contaba un método en el coordinador de la Fase 0 y la de `SqliteSchemaTests` que comparaba la
  0003 con el esquema actual (ahora con la 0004, que es lo que afirmaba). `tests/AgentStudio.Tests/Debate/JudgeRunRecorderTests.cs`
  es nuevo, fuera de las carpetas de P0, para probar la extracción.

## 18. Fase 8: trabajo desatendido

Lo que deja fijado el paquete P0 de la Fase 8 ([16](16-ampliacion-plataforma-de-proyectos.md), §4.5): la clase del final
de cada intento, los payloads de esta fase, las opciones nuevas de la cabecera de una ejecución, la presencia del usuario,
la energía, los ajustes del escritorio y el TFM. Los paquetes WP1 a WP4 trabajan en paralelo sobre estos contratos, y
`tests/AgentStudio.Tests/Application/Phase8ContractTests.cs` y `Phase8PayloadTests.cs` fallan si uno cambia. Las esperas y
la reanudación con consentimiento están en [ADR-0034](adr/0034-esperas-por-limite-y-reanudacion-con-consentimiento.md); lo
que se aparta de docs/16, en la [§18.8](#188-diferencias-con-docs16-y-lo-que-queda-abierto).

### 18.1 El final clasificado de un intento (`Application/Providers/AgentProvider.cs`)

```csharp
public enum AttemptEndClass { Completed, WindowQuota, RateOrOverload, ContextExhausted, Network, SignedOut, AttemptBudget, Interrupted, Cancelled, Other }
public enum AttemptBudgetKind { Turns, Cost, Time }
public sealed record AgentRunEnd(AttemptEndClass Class, string? LimitType = null, DateTimeOffset? ResetsAt = null, AttemptBudgetKind? Budget = null)
{ public static AgentRunEnd Implied(AgentRunStatus status); }       // la clase que el estado implica por sí solo
// AgentRunOutcome gana, al final: AgentRunEnd? End = null.  Classified() => End ?? AgentRunEnd.Implied(Status).
// RateLimitSignaled gana, al final: string? LimitType = null (el rateLimitType de Claude, tal cual).
```

- **Las clases y su política** (D-41): `WindowQuota` (cuota de una ventana: se espera su reinicio con el margen, D-36 a
  D-38), `RateOrOverload` (velocidad o sobrecarga: espera corta y progresiva en la misma sesión), `ContextExhausted` (sesión
  nueva con traspaso, D-42), `Network` (reintento acotado y progresivo en la misma sesión), `SignedOut` (sesión caducada o
  sin sesión: bloqueo con el comando que lo arregla, sin reintentos), `AttemptBudget` (turnos, coste o tiempo: como hoy,
  `LimitReached`), `Interrupted` (solo lo escribe la recuperación: sesión nueva con traspaso, D-43), `Cancelled` (lo canceló
  la persona o el motor, por abandono) y `Other`. `Completed` es el turno que terminó.
- **La clase refina el estado, nunca lo sustituye.** `AgentRunStatus` y `AttemptOutcome` no cambian. Un `RateLimited` de
  clase `Other` sigue siendo un límite, de un tipo que el adaptador no supo decir.
- **Nunca se adivina.** Lo que un adaptador no reconoce en una captura de su CLI es `Other` (WP2). `Implied` es lo único que
  dice el estado: `Succeeded` → `Completed`, `Cancelled` → `Cancelled`, `LimitReached` → `AttemptBudget` (sin decir cuál),
  `RateLimited` y `Failed` → `Other`.
- `LimitType` y `ResetsAt` son los de la ventana que se agotó, tal como los da el CLI; nulos si no los da (Codex `exec`), y
  entonces la espera es la progresiva, «estimada». `Budget`, nulo si el CLI no dice cuál.

### 18.2 Payloads (`Application/Decisions/DecisionPayloads.cs`)

```csharp
public sealed record AttemptEndSnapshot(AttemptEndClass Class, string? LimitType = null, DateTimeOffset? ResetsAt = null, AttemptBudgetKind? Budget = null)
{ From(AgentRunEnd); ImpliedBy(AttemptOutcome); ToEnd(); }
// AgentRunFinishedPayload v3 (EndClassVersion = 3): + AttemptEndSnapshot? End, al final. RecordsEndClass(); ResolvedEnd() => End ?? ImpliedBy(Status).
public enum RunExitChoice { PauseAndExitWhenIdle, ExitNow, SessionEnded }
public sealed record RunExitChosenPayload(RunExitChoice Choice, string DecidedBy) : DecisionPayload   // DecisionKind.RunExitChosen, V = 1
{ public bool AllowsAutomaticResume(); }                                                              // solo SessionEnded
public enum RunDispatchChange { Paused, Resumed }
public sealed record RunDispatchChangedPayload(RunDispatchChange Change, string DecidedBy, DateTimeOffset At) : DecisionPayload
{ public bool AllowsAutomaticResume(); }                                                              // DecisionKind.RunDispatchChanged; solo Resumed
public enum AccessCheckSource { SessionProbe, QuotaRead }
public enum AccessCheckResult { Ready, SignedOut, StillLimited, NotOptedIn, NotExposed, Missing }
public sealed record AccessCheckedPayload(TaskId? Task, ProviderId Provider, AccessCheckSource Source, AccessCheckResult Result,
    DateTimeOffset CheckedAt, DateTimeOffset? ResetsAt = null) : DecisionPayload;                    // DecisionKind.AccessChecked
public enum RunResumeCause { UserRequested, ConsentAfterQuotaWait, ConsentAfterUnrequestedClose, ConsentAfterSessionEnded }
public sealed record RunResumedPayload(RunResumeCause Cause, string DecidedBy, DateTimeOffset At, string? ConsentHeaderHash = null)
    : DecisionPayload { public bool ByConsent(); }                                                    // DecisionKind.RunResumed (WP1)
// ApprovalAnsweredPayload v2 (VerificationVersion = 2): + ApprovalVerification? Verification, al final.
//     RecordsVerification(); ResolvedVerification() => Verification ?? Dialog.
```

- `DecisionKind` gana `RunExitChosen`, `RunDispatchChanged` y `AccessChecked` al final, en ese orden, detrás de
  `ProjectAuditFinished`, y después `RunResumed` (lo añadió WP1 con la aprobación del coordinador del 2026-10-21): ningún
  hash guardado cambia (`DecisionKindCompatibilityTests` lo fija).
- **`AgentRunFinished` v3.** La escriben `AgentRunner` y `JudgeRunRecorder` (WP2) con
  `End = AttemptEndSnapshot.From(outcome.Classified())` y `V = EndClassVersion`. La v1 y la v2 se leen y se reescriben byte a
  byte (sus pruebas doradas no cambian); `RecordsUsageWithOrigins()` sigue siendo cierto en la v3, y el consumo (que mira
  `RecordedVersion < 2`) la lee como una v2.
- **`RunExitChosen`** (D-16, F8-9) va en el registro de cada ejecución afectada (`RunId` = la ejecución, o el debate o la
  auditoría), con actor `User` (o `System` en `SessionEnded`) y quién decidió. «Cancelar» no escribe nada. Decide la
  reanudación automática (D-39): las elecciones de la persona nunca se reanudan solas; `SessionEnded` (cierre de sesión o
  apagado de Windows) sí, con el consentimiento de la cabecera, igual que un cierre sin ninguna entrada.
  - **La elección de la persona sigue en vigor frente a un `SessionEnded` posterior.** Si una ejecución tiene un
    `RunExitChosen` de la persona (`PauseAndExitWhenIdle` o `ExitNow`), un `SessionEnded` que llegue después no le devuelve
    la reanudación automática. El escritorio no lo escribe en lo que la persona ya decidió en esa sesión de la app (por
    ejemplo, tras «No salir», o mientras espera para salir o ya está saliendo), y la regla de reanudación (WP1) tampoco
    deja que uno posterior anule esa elección.
- **`RunDispatchChanged`** (D-39) va en el registro de la ejecución cada vez que una persona pausa o reanuda su despacho,
  desde el monitor o desde donde la app lo ofrezca (la bandeja), con actor `User`, quién decidió y cuándo. Hasta la Fase 8
  la pausa solo vivía en memoria. Con él, tras un cierre que no pediste, el registro dice que la pausaste: la última entrada
  de este tipo decide (`AllowsAutomaticResume()`), y una ejecución en pausa no se reanuda sola aunque su cabecera tenga el
  consentimiento. La pausa de la salida gestionada es `RunExitChosen`, y devolver una tarea bloqueada a pendiente ya es su
  `TaskStatusChanged`. Una pausa que no llegó a registrarse (la ejecución ya había terminado, o su bucle se paró antes de
  aplicarla) falla con `InvalidOperationException` para quien la pidió (`IOrchestrationRun.PauseAsync`): nunca se da por
  hecha.
- **`AccessChecked`** (D-38, F8-3) va antes de relanzar tras una espera por límite, una entrada por cada fuente, con actor
  `System`. Solo hay dos fuentes permitidas, y ninguna gasta un turno del modelo: la sonda de sesión del CLI
  (`claude auth status` o `codex login status`), que da `Ready`, `SignedOut` o `Missing`, y en Codex, con el opt-in, la lectura
  de la cuota del app-server, que da `Ready`, `StillLimited` (con el reinicio, si lo da), `NotOptedIn` (no se lanzó nada),
  `NotExposed` (API key) o `Missing`. `Task` es nula si la comprobación es del proveedor antes de ninguna de sus tareas, y
  `CheckedAt` es cuándo se hizo. No guarda nada de lo que imprime el CLI: la sonda puede imprimir la cuenta. En Claude no hay
  lectura de cuota sin turno: la comprobación es el propio intento, y su final v3 dice si sigue limitado.
- **`RunResumed`** (D-39, F8-4) va en el registro de la ejecución en cada reanudación tras reiniciar, antes de lanzar nada y
  también cuando ninguna tarea se interrumpió (una ejecución que solo esperaba cuota). Dice por qué (`Cause`): porque lo
  pediste (`UserRequested`, actor `User`, quién decidió «user») o sola, por el consentimiento de la cabecera (actor `System`,
  «agent-studio»), porque esperaba cuota, porque la cortó un cierre que no pediste o porque Windows cerró la sesión. En las
  reanudaciones por consentimiento, `ConsentHeaderHash` es el hash de la cabecera que lo llevaba (el que ancló
  `RunStarted`). Lo escribe `RunResumer`, después de todas sus comprobaciones y antes de la recuperación.
- **Cómo lee el registro la reanudación sola** (`ResumeScheduler`, WP1). Solo se reanuda sola una ejecución a medias (con
  `RunStarted` y sin `RunFinished`) que ningún proceso lleva y cuya cabecera tiene `AutoResumeConsent`. De su registro
  solo cuenta lo ocurrido desde su última `RunResumed`, porque esa reanudación la puso en marcha otra vez:
  1. Si la última `RunDispatchChanged` es una pausa, no se reanuda. Esta entrada solo dice si el despacho quedó en pausa:
     ni da ni quita el consentimiento, y reanudar el despacho no deshace una elección al salir.
  2. Si hay alguna `RunExitChosen` con la elección de una persona (`PauseAndExitWhenIdle` o `ExitNow`), no se reanuda,
     aunque después llegue un `SessionEnded` de Windows: decide la última elección de una persona, no la última
     `RunExitChosen`. `RunExitChosen` es lo que dice si el consentimiento de la cabecera puede actuar. La regla lee la
     elección (`AllowsAutomaticResume()`), no el actor ni `DecidedBy`, y vale por sí sola: no depende de que la salida
     gestionada deje de escribir `SessionEnded` tras una elección (defensa en profundidad).
  3. Si solo hay `SessionEnded`, se reanuda con `ConsentAfterSessionEnded`.
  4. Si no hay ninguna, se reanuda con `ConsentAfterQuotaWait` cuando solo esperaba cuota (alguna tarea bloqueada por un
     límite, ninguna en marcha y ningún lanzamiento sin su final), o con `ConsentAfterUnrequestedClose` si no.

  La pasada decide sobre el registro que leyó, y el orquestador decide otra vez, bajo la reserva de la ejecución, sobre el
  que lee entonces, sin escribir nada si ya no procede. Una ejecución cuyo bucle se paró en esta sesión de la app por un
  error del sistema no se reanuda sola en esa sesión (ADR-0034, adenda del 2026-10-29).
- **Cómo la pide la app** (integración de la Fase 8, `Desktop/Orchestrator/Session/ConsentedResumes.cs`). La composición da
  a `ResumeScheduler` el único orquestador de la app, el mismo con el que reanudas a mano desde el monitor. Cada proyecto
  tiene una pasada la primera vez que lo abres en la sesión (al arrancar, el último que tenías abierto), y solo si ya tiene
  su base de datos: abrir una carpeta nunca crea `.agentstudio/`. La salida gestionada toma la suspensión en el acto
  (`ResumeScheduler.Suspend()`) antes de leer el trabajo para preguntar, y después espera a que termine la pasada en curso
  (`WaitForIdleAsync`). La suelta si te quedas: «Cancelar», «Seguir en la bandeja», «No salir», o la ventana vuelta a
  enseñar mientras espera, a los intentos en curso o a esa pasada; en este último caso no pregunta nada. Cancelar la espera
  no suelta la suspensión, así que la suelta la salida; por eso no usa `SuspendAsync`, que no la devuelve si se cancela su
  espera. `SessionEnding` toma la suspensión antes de escribir nada y no la suelta nunca: espera a la pasada en curso como
  mucho la mitad de su tiempo (con un token y con un tope propio), y si una salida esperaba a esa pasada para preguntar, se
  para ahí. Cada ejecución que reanuda (evento `Resumed`, en el hilo de la pasada y antes de que termine `WaitForIdleAsync`)
  entra en ese momento en el registro del trabajo de la bandeja, sin necesitar el hilo de la interfaz (una cola que lee ese
  hilo antes de cada lectura del registro): así la pregunta de la salida la incluye, sea cual sea el orden del dispatcher, y
  el cierre de sesión, que bloquea el hilo de la interfaz mientras espera, también escribe en ella. Después el monitor del
  documento «Orquestador» de su proyecto la sigue en el hilo de la interfaz, creado si nunca abriste la pestaña; sus eventos
  esperan a ese único lector. Así tiene su sitio en la bandeja, su pausa y su `RunExitChosen` al salir. Si ese monitor ya
  sigue otra ejecución, o la pestaña está ocupada («Pedir plan», «Ejecutar» o «Reanudar» esperan al motor), la sigue un
  monitor propio que solo ve la bandeja. Solo cuando un monitor la tiene se olvida su anuncio en el registro; si ninguno
  pudo tomarla, el anuncio se queda (la bandeja la enseña y la salida pregunta por ella) y la barra de estado lo dice.
- **Ninguna pasada antes de la salida gestionada.** `ConsentedResumes` empieza retenido y solo deja correr las pasadas con
  `Enable()`, que el shell llama al final de `UnattendedShell.Start`, cuando la bandeja arrancó: sin ella, cerrar la app no
  escribiría ninguna elección y el planificador leería un cierre deliberado como uno que nadie pidió. `ResumeAsync`
  devuelve un `ConsentedPass` con lo que hizo y si recorrió el proyecto entero (`Complete`): una pasada retenida, o
  recortada por una suspensión tomada mientras corría, no cuenta, y el shell la vuelve a pedir cuando el evento
  `Available` dice que las pasadas pueden correr (la bandeja arrancó, o se soltó la última suspensión porque te quedaste).
  Una pasada que falla sí cuenta: pedirla otra vez en la sesión no lo arregla.
- **Cerrar sin la bandeja** (antes de que arranque, o si no pudo) deja «Salir ahora» (`RunExitChoice.ExitNow`) en el
  registro de cada trabajo en marcha con id, como la salida gestionada (`Desktop/Background/ExitWithoutTray.cs`, con el
  mismo tiempo que el cierre de sesión): lo cerraste tú. Un cierre de sesión de Windows antes de la bandeja no escribe nada,
  y el planificador lo lee como un cierre que nadie pidió.
- **Una ejecución que la persona inicia o reanuda nunca se queda sin monitor.** Tras cada espera al motor, el documento
  mira otra vez si su monitor sigue libre: si otra ejecución lo tomó mientras tanto, no se la quita ni lanza un error, y
  pasa la suya al shell (evento `RunHandedOff`), que la sigue con un monitor propio que ve la bandeja. Antes de pedir la
  reanudación, si el monitor ya no está libre, no reanuda nada y lo dice. La casilla del consentimiento se desmarca en
  cuanto una ejecución empieza; si el inicio falla, no empezó ninguna y la casilla se queda como la dejaste.
- **El bloqueo retira las preguntas de la salida.** Con la interfaz bloqueada, cerrar la ventana no pregunta ni sale (la
  ventana va a la bandeja, o no hace nada sin ella) y «Salir» espera al desbloqueo. `ManagedExit.IsLocked`, que sigue a
  `BackgroundHost.IsLocked`, cancela el token de la pregunta de la salida y el de la confirmación del shell
  (`BackgroundShell.ConfirmLeaving`, ahora con token): retirada, cuenta como «Cancelar», no escribe nada y suelta la
  suspensión. La pregunta de cambios sin guardar (`IUnsavedChangesPrompt.Ask` con token) se retira igual: la de WPF lo hace
  también por su cuenta con el bloqueo (`LockWithdrawal`), así que nunca enseña nombres de ficheros encima de la pantalla
  de bloqueo, ni al cerrar una pestaña.
- **Una pausa o un reintento que la ejecución no aplica** (`IOrchestrationRun.PauseAsync` y `RetryBlockedAsync` fallan con
  `InvalidOperationException` si terminó o se paró antes) se dicen en castellano a quien los pidió: el monitor, con su
  mensaje; la bandeja, con un aviso sin nada del proyecto, también si la ejecución dejó de admitir la orden entre abrir el
  menú y el clic; la salida gestionada lo anota en el registro de la app y sigue. `RunWork` lanza entonces
  `InvalidOperationException` con un mensaje en inglés; el texto en castellano es el del monitor y el del aviso.
- **`ApprovalGranted` y `ApprovalDenied` v2** registran cómo se confirmó a la persona (F8-11). La escribe `ApprovalGate`
  (WP4) con el método de `ApprovalDecision`. Una v1 es una respuesta del diálogo.
- Ningún campo nuevo lleva texto de un agente: `LimitType` es un token del CLI y `DecidedBy`, quién decidió. El texto de los
  payloads nuevos está fijado en `Phase8PayloadTests`, y el hash de una cadena con las cuatro entradas de la fase, con una
  implementación independiente de la forma canónica y con sus valores literales.

### 18.3 Opciones de la cabecera (`Application/Orchestration`, `Infrastructure/Persistence/Json/RunJson.cs`)

```csharp
public sealed record RunOptions(int MaxParallelTasks = 3, int MaxAgentRuns = 12, TimeSpan? StallThreshold = null,
    TimeSpan? HeartbeatInterval = null, TimeSpan? DefaultTaskTimeout = null, TimeSpan? MaxAutoWaitForQuota = null,
    TimeSpan? QuotaResetMargin = null, TimeSpan? MaxEstimatedQuotaWait = null, bool AutoResumeConsent = false)
{
    public static readonly TimeSpan DefaultQuotaResetMargin;        // 3 minutos (D-36)
    public static readonly TimeSpan DefaultMaxEstimatedQuotaWait;   // 60 minutos (D-37)
    public static readonly TimeSpan MinEstimatedQuotaWait;          // 1 minuto (WP1, segunda revisión)
    public void EnsureValid();                                       // ArgumentException: margen negativo, tope < 1 min
    public TimeSpan ResolvedQuotaResetMargin();                      // lo que usa el motor
    public TimeSpan ResolvedMaxEstimatedQuotaWait();
}
```

- `QuotaResetMargin`: lo que se espera tras el reinicio comunicado; `MaxEstimatedQuotaWait`: el tope de cada espera sin
  reinicio (la progresiva de 5, 10, 20, 40 y 60 minutos es de WP1); `AutoResumeConsent`: el consentimiento de D-39, que se
  marca al aprobar el plan (decisión 4 (a)). Nulo es el valor del motor.
- En la app, `AutoResumeConsent` es la casilla «Reanudar sola si un límite o un cierre la interrumpe» del documento
  «Orquestador», justo antes de «Ejecutar»: desmarcada por defecto y otra vez tras cada inicio, porque vale para una sola
  ejecución. «Ejecutar» la pasa en `RunRequest.Options`, que el motor congela en la cabecera; el monitor dice si la
  ejecución que sigue la tiene, leído de su cabecera cuando la reanudaste tú o se reanudó sola. Las demás opciones no
  tienen pantalla: van con el valor del motor.
- **Solo se escriben si no son los de por defecto.** `RunHeaderHash` los añade detrás de la plantilla, cada uno como su
  nombre y su valor, en este orden: `quotaResetMargin` y sus ticks, `maxEstimatedQuotaWait` y sus ticks, `autoResumeConsent`
  y `true`. El hash de las cabeceras guardadas no cambia, y el de una cabecera con las tres está fijado (ADR-0034).
- `RunOptionsJson` los guarda como `quotaResetMargin`, `maxEstimatedQuotaWait` y `autoResumeConsent`, solo cuando tienen
  valor (el consentimiento, solo cuando es cierto): un `options_json` sin ellos se lee con los valores por defecto y la misma
  cabecera.
- **Las esperas de una ejecución nueva tienen que tener sentido.** `Orchestrator.StartAsync` llama a `EnsureValid()` antes
  de escribir nada, y rechaza un margen negativo y un tope de menos de `MinEstimatedQuotaWait`. Una cabecera guardada no se
  valida: se lee y se hashea como está guardada, y el motor usa `ResolvedQuotaResetMargin()` y
  `ResolvedMaxEstimatedQuotaWait()`. Dan el valor dado, el de por defecto si falta, y el más cercano con sentido si es raro.
  Son métodos, y no propiedades, para que ningún serializador los escriba.

### 18.4 Presencia del usuario y aprobaciones (`Application/Security`)

```csharp
public enum ApprovalVerification { Dialog, WindowsHello, WindowsHelloUnavailable }
// ApprovalDecision gana, al final: ApprovalVerification Verification = ApprovalVerification.Dialog.

// UserPresenceContracts.cs (nuevo)
public enum UserPresenceAvailability { Available, DeviceNotPresent, NotConfiguredForUser, DisabledByPolicy, DeviceBusy }
public enum UserPresenceResult { Verified, DeviceNotPresent, NotConfiguredForUser, DisabledByPolicy, DeviceBusy, RetriesExhausted, Canceled }
public interface IUserPresenceVerifier
{
    Task<UserPresenceAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken);   // no enseña nada
    Task<UserPresenceResult> VerifyAsync(string message, CancellationToken cancellationToken);     // pide Windows Hello
}
```

- Los dos enums son espejo exacto de `UserConsentVerifierAvailability` y `UserConsentVerificationResult` (una prueba los
  compara con los de la proyección), así que WP4 los traduce uno a uno. Solo vuelve un resultado: ningún dato biométrico ni
  el PIN llegan a la app (D-13).
- El puerto no conoce ventanas ni tipos de Windows: Application sigue en `net10.0`. La implementación (WP4, en
  `Desktop/Lock`) llama a `UserConsentVerifierInterop.RequestVerificationForWindowAsync` con la ventana de WPF; si Windows
  no se puede consultar, lanza, y quien llama decide.
- `WindowsHelloUnavailable` es «sin Hello: no disponible»: se iba a pedir y no se pudo (sin configurar, sin dispositivo, o
  desactivado por una directiva), y decidió el diálogo solo, con un aviso. `Dialog` es lo de siempre, y la forma de toda
  aprobación anterior a la Fase 8.

### 18.5 Energía (`Application/Execution/PowerContracts.cs`, nuevo)

```csharp
public enum PowerTransition { Suspending, Resumed }
public sealed record PowerEvent(PowerTransition Transition, DateTimeOffset At, TimeSpan? Slept = null);
public interface IPowerEvents { event EventHandler<PowerEvent>? Changed; }
public interface IKeepAwake { IDisposable Hold(string reason); bool IsHeld { get; } }
```

- `PowerEvent.At` es la hora de pared a la que se vio; `Slept`, en `Resumed`, lo que el equipo durmió según lo midió la
  implementación (nulo si no vio la suspensión). El motor (WP1) reevalúa con ellos los plazos, el abandono y las esperas
  descontando el tiempo dormido (D-40); la implementación es de WP3, con un doble en las pruebas.
- `IKeepAwake.Hold` impide la suspensión por inactividad hasta que se libera lo que devuelve (con `SetThreadExecutionState`,
  sin administrador, en la implementación de WP3); puede haber varias a la vez, y se vuelve a poder dormir al liberar la
  última. No impide que tú suspendas el equipo. Solo se usa con el ajuste `PreventSleepWhileWorking` y mientras hay trabajo.
- **Los relojes en la app** (integración). Solo el motor de orquestación lleva `AwakeTimeProvider`
  (`Infrastructure/Clocks`), cuya marca de tiempo se para mientras el equipo duerme: con ella sus ejecuciones dejan fuera
  del silencio de sus intentos y de sus esperas el tiempo dormido (`Desktop/Composition/EngineClock.cs`; si la composición
  registró antes otro reloj, el de una prueba, el motor usa ese). La hora de pared y los temporizadores son los del sistema
  en los dos relojes. El resto de la app sigue con el reloj del sistema: el bloqueo de la interfaz y el de inactividad
  (`InterfaceLock.SinceLastUnlock`), que así cuentan una suspensión como tiempo sin actividad y bloquean al despertar, la
  bandeja y las marcas de tiempo de los registros.

### 18.6 Ajustes del escritorio (`Desktop/Services/AppSettings.cs` y `AppSettingsStore.cs`)

| Ajuste | Por defecto | Qué decide |
|---|---|---|
| `CloseWindow` (`CloseWindowAction`: `Ask`, `KeepInTray`, `Exit`) | `Ask` | Cerrar la ventana: con trabajo en marcha pregunta si seguir en la bandeja o salir; sin trabajo, sale (D-14) |
| `WindowsHelloForCriticalApprovals` | verdadero | Las aprobaciones críticas piden Windows Hello además del diálogo; se puede desactivar (D-13, F8-11) |
| `IdleLockEnabled`, `IdleLockMinutes` | falso, 15 | El bloqueo de la interfaz tras ese tiempo sin actividad, de 1 a 1 440 minutos (D-13, F8-10) |
| `PreventSleepWhileWorking` | falso | Impedir la suspensión por inactividad mientras hay trabajo (D-40) |
| `MainWindowPlacement` (`WindowPlacement`: `left`, `top`, `width`, `height`, `maximized`; tanda 1, 2026-10-30) | nulo | Dónde estaba la ventana principal al salir de verdad y si estaba maximizada. Nulo hasta la primera salida: la ventana se abre centrada con su tamaño por defecto. Al arrancar solo se usa si la barra de título cae en un monitor conectado (`FitTo`); si no, se abre como sin colocación |

- Los de las cuatro primeras filas son preferencias: las escribe `SavePreferences` y `Save` las conserva tal como están en el fichero
  (`WithPreferencesOf`). `WithIdleLock(enabled, minutes)` y `SavePreferences` rechazan un tiempo fuera de rango.
  `MainWindowPlacement` no es una preferencia: tiene su propio escritor (tabla de abajo).
- `CloseWindow` se guarda por su nombre. Un valor editado a mano que no tiene sentido (un nombre o un número que no existe,
  un tiempo fuera de rango, una colocación que no es un objeto de números) se lee con su valor por defecto, y el resto del
  fichero se conserva.
- Los paquetes los leen. Los escribe el documento «Ajustes» (tanda 1, 2026-10-30; antes, «Ajustes» dentro de «Consumo»)
  con un solo `SavePreferences` para sus tres secciones. Guardarlos hace que «mantener despierto» lea el ajuste otra vez; el
  bloqueo por inactividad lo lee en su siguiente comprobación y el cierre de la ventana, en cada cierre.

**Quién escribe `settings.json`** (tanda 1, 2026-10-30; `Desktop/Services/AppSettingsStore.cs`). El fichero tiene tres
escritores, cada uno con sus campos. Cada uno lee el fichero antes de escribir y deja los campos de los otros dos como
están en él: así una copia de los ajustes cargada antes no deshace lo que otro guardó después. Los tres escriben de forma
atómica.

| Escritor | Quién lo llama y cuándo | Campos que escribe |
|---|---|---|
| `Save` | El shell (`MainViewModel`): al abrir un proyecto o quitar uno que ya no existe | `LastProjectPath`, `RecentProjects` y `SkillsIndexPath` |
| `SavePreferences` | El documento «Ajustes», con su único «Guardar» (o con «Guardar» en la pregunta de cambios sin guardar): una sola escritura para las tres secciones | Los tres umbrales de contexto, `CodexQuotaReadEnabled`, `CloseWindow`, `WindowsHelloForCriticalApprovals`, `IdleLockEnabled`, `IdleLockMinutes` y `PreventSleepWhileWorking`. Antes de escribir comprueba los umbrales y los minutos del bloqueo (`ValidatePreferences`): si no valen, no escribe nada |
| `SaveWindowPlacement` | `MainWindow`, solo al salir de verdad (nunca al ocultar la ventana en la bandeja) | `MainWindowPlacement` |

### 18.7 El TFM

- `AgentStudio.Desktop` y `AgentStudio.Tests` pasan de `net10.0-windows` a `net10.0-windows10.0.22000.0`; el resto sigue en
  `net10.0`. La proyección que resuelve el SDK 10.0.303 es `Microsoft.Windows.SDK.NET.Ref` 10.0.22000.57, y trae
  `Windows.Security.Credentials.UI.UserConsentVerifierInterop.RequestVerificationForWindowAsync(nint, string)` (una prueba lo
  comprueba sin pedir Hello). `SupportedOSPlatformVersion` queda en la del TFM: Windows 11 (compilación 22000), la que exige
  esa interoperabilidad.
- **Tamaño de la salida de Desktop** (`bin\Debug`, la misma máquina y los mismos recursos de Monaco y xterm): antes,
  74 275 550 bytes en 263 ficheros; después, 100 560 602 bytes en 265 (+26 285 052, un 35 %). Lo nuevo es
  `Microsoft.Windows.SDK.NET.dll` (25 717 792 bytes) y `WinRT.Runtime.dll` (528 944). La alternativa, si no compensa, es la
  de docs/16 §6: una interoperabilidad COM mínima escrita a mano con `IUserConsentVerifierInterop`, sin la proyección.
- **Lo de la Fase 4 no cambia:** WebView2 elige sus ensamblados por `net5.0` y no por la versión de la plataforma
  (`Common.targets` de 1.0.4258.31), y Nodify y AvalonDock traen activos `net10.0-windows7.0`, compatibles. La compilación
  sigue con 0 avisos y la suite completa en verde (ConPTY, WebView2, AvalonDock y Nodify incluidos).

### 18.8 Diferencias con docs/16 y lo que queda abierto

- **Dos clases más que las de D-41:** `Completed` y `Cancelled`. F8-1 pide que cada intento termine con una clase, también el
  que terminó bien y el que cancelaste o abandonó el motor, que no son «otra».
- **`RunExitChoice.SessionEnded`**, que D-16 no nombraba como elección: un cierre de sesión o un apagado cuenta como salir
  ahora, pero nadie lo eligió, y por eso, a diferencia de `ExitNow`, deja reanudar sola una ejecución con el consentimiento.
- **`ApprovalAnsweredPayload` v2**, que la lista de P0 no nombraba: F8-11 pide que la aprobación registre el método, y sin
  ella WP4 tendría que tocar `DecisionPayloads.cs`.
- **Sin ajuste para pedir Hello al abrir la app:** la decisión 5 (a) lo descarta.
- **Los valores por defecto de los ajustes son los de docs/16** (`Ask` y Hello en las críticas), que siguen tu decisión 5 (a),
  y no los de hoy; el coordinador los confirmó el 2026-10-21. Hoy nada los lee, así que no cambian nada hasta que WP3 y WP4
  los usen.
- **Pruebas fuera de las carpetas de P0:** `tests/AgentStudio.Tests/Infrastructure/Persistence/RunOptionsStorageTests.cs`,
  nuevo, para `RunJson.cs`, que es de P0; y `tests/AgentStudio.Tests/Domain/DecisionKindCompatibilityTests.cs`, la guarda
  del enum del dominio: como hizo la Fase 5, la prueba de la Fase 5 deja de contar los tipos y la nueva de la Fase 8 los
  cuenta, sin tocar ninguna aserción de posición. Y `docs/02-arquitectura.md`, la tabla de proyectos, con el TFM nuevo.
- **`RunDispatchChanged` y `AccessChecked`, que la lista de P0 no nombraba.** P0 vio que la pausa desde el monitor solo
  vivía en memoria (`OrchestrationRun._paused`), así que tras una pausa y un cierre que no pediste nada decía que la habías
  pausado (D-39: «nunca las que pausaste»), y que F8-3 pide registrar la comprobación antes de seguir, que no tenía tipo de
  entrada. Como `DecisionKind` y los payloads son de P0, y WP1 no los puede añadir sin tocar ficheros de otro paquete, los
  propuso y el coordinador los aprobó el 2026-10-21 (§18.2). Respecto a la propuesta, la comprobación lleva la fuente como
  enum y no el comando, que se deduce de la fuente y del proveedor, y así no hay texto libre; el resultado «sin dato» se
  llama `Missing`, como en `QuotaReadStatus`, y se añade `NotExposed`; y las dos entradas llevan la hora explícita.
- **`RunResumed`, que añadió WP1** (aprobado por el coordinador el 2026-10-21): sin él, una ejecución que solo esperaba cuota
  al cerrarse no escribe nada al reanudarse, así que el motivo de los `TaskStatusChanged` no bastaba para distinguir la
  reanudación por consentimiento de la tuya. Su texto y el hash de una entrada están fijados en `Phase8PayloadTests`, con un
  script de PowerShell fuera del repositorio como implementación independiente de la forma canónica.

## 19. Tanda 2: barras en tiempo real y pulido (2026-11-03)

Lo que la tanda 2 añade o cambia en los contratos. WP-F son las barras en tiempo real (P3, P36 y P37 de
[18](18-peticiones-y-pendientes.md)); WP-G, el pulido del recorrido en vivo (V25 a V27, V29 a V31 y A1-bis). Ningún
payload del registro cambia de versión, y no hay migración.

### 19.1 El final de cada ejecución de agente (`Application/Providers/AgentRunEnds.cs`, nuevo)

```csharp
public sealed record AgentRunEnded(ProviderId Provider, AgentRunStatus? Status);   // Status nulo: no terminó bien
public interface IAgentRunEnds { event EventHandler<AgentRunEnded>? Ended; }
public sealed class AgentRunEnds : IAgentRunEnds { public void Notify(AgentRunEnded ended); }
```

- Dice que una ejecución de agente terminó, y de qué proveedor. Cubre todas las del proceso: orquestación, debate,
  auditoría, juez y auxiliares.
- Lo avisa `QuotaObservingAgentProvider`, que ya ve todas. Recibe un `AgentRunEnds?` opcional, y
  `AgentProviderPipeline.Compose(…, AgentRunEnds? ends)` y `ProjectAgentProviders` se lo pasan. Sin registro de cuota no
  hay decorador y no se avisa nada: la cuota leída después no tendría dónde guardarse.
- El evento salta en el hilo en el que terminó la ejecución. Un suscriptor que falla no toca la ejecución ni a los demás
  suscriptores.
- `AddAgentStudioObservability` registra uno para todo el proceso (`AgentRunEnds` e `IAgentRunEnds`).
- Lo oye `QuotaViewModel` (Desktop), con un `IAgentRunEnds?` nuevo en su constructor: tras una ejecución de Codex, lee la
  cuota con el permiso y como mucho una vez cada 5 minutos (`CodexReadInterval`).

### 19.2 Una sola lectura de cuota para todos (`Application/Providers/SharedQuotaReader.cs`, nuevo)

- `SharedQuotaReader : IProviderQuotaReader` envuelve al lector que lee de verdad (`Inner`). Lo que se pide mientras hay
  una lectura en marcha recibe esa misma lectura. Una petición que llega después de terminar la anterior es una lectura
  nueva.
- Quien cancela solo deja de esperar: nunca cancela la lectura de los demás. El lector tiene su propio tiempo límite.
- La lectura corre en el grupo de hilos: nada de su parte síncrona (leer el ajuste, buscar el ejecutable, arrancar el
  proceso) corre en el hilo de quien la pide.
- `AddCodexQuotaReader` registra el lector de Codex ya envuelto. El motor (al esperar un límite) y las pantallas
  comparten la misma instancia.

### 19.3 Orquestación (`Application/Orchestration`)

- **`RunSnapshot(…, int? MaxAgentRuns = null)`**: el presupuesto de la ejecución (`RunOptions.MaxAgentRuns`), para que el
  monitor y la bandeja enseñen las ejecuciones usadas frente a él. Es nulo si quien construye el snapshot no lo dice (un
  doble, un lector antiguo); entonces la barra dice «máximo sin dato».
- **`TaskQueuedEvent` también para la revisión.** El motor lo emite al lanzar la revisión del juez, con el proveedor del
  juez, antes de su intento. No hay cola: es el aviso de en qué proveedor corre, para que la pantalla siga su contexto
  como el de un agente de ese proveedor («no expuesto» en Codex desde el principio).

### 19.4 Contexto en vivo (`Observability/Context`)

- **`ModelWindowTable`** (nuevo): la tabla base de ventanas por modelo, versión `ModelWindowTable.Version`
  (`2026-10-30`). `WindowFor(model, provider)` da una ventana «estimado por Agent Studio» que nombra la tabla y su
  versión, «no expuesto» para Codex y «sin dato» para un modelo que no conoce. `Normalize` quita la fecha que añade el
  CLI y conserva el sufijo `[1m]`. Ver la adenda del 2026-11-03 de
  [ADR-0021](adr/0021-salud-del-contexto-por-paso.md).
- **`ModelWindowCatalog`**: `WithBaseline(table)`, `Baseline` (sin nada aprendido, con la tabla debajo) y
  `BaselineTable`. Lo aprendido del registro gana a la tabla.
- **`ContextHealthTracker`**: mismo contrato. Ahora lo usan las pantallas: uno por intento en el monitor y uno en la
  planificación.

### 19.5 Escritorio (`Desktop`; no son puertos, pero las pantallas dependen de ellos)

| Pieza | Fichero | Qué es |
|---|---|---|
| `MetricBar`, `MetricBarReading`, `MetricBarLevel` | `Controls/MetricBar.xaml(.cs)`, `Controls/MetricBarReading.cs` | El control común de las barras y lo que muestra: `Label`, `Value` y `Maximum`, `ValueText`, `Origin` y `OriginKind`, `IsEstimate`, `Level` y `LevelText`, `Detail`, **`Age`** («vista hace 12 min», junto a la cifra en todas sus formas, también la corta) y `Marks` (los umbrales). `Text` es la línea entera; `CompactText`, la de la barra de estado; `AccessibleName`, la de UI Automation, que también es su `ToString()` |
| `MetricBars` | `Observability/MetricBars.cs` | Construye las lecturas: `Context`, `NoObservation`, `Empty`, `Quota`, **`QuotaAfterReset`** (una ventana cuyo reinicio ya pasó: vacía, «se reinició a las…, sin dato nuevo», la cifra vieja en el detalle), `Budget` y `Tokens` |
| `ContextWindowCatalogs` | `Observability/ContextWindowCatalogs.cs` | Las ventanas de cada proyecto sobre la tabla base, leídas de su registro (`IProjectLogReader`) en el grupo de hilos, una lectura a la vez por proyecto. Una lectura que falla o un registro que no se fía no borra lo aprendido |
| `StatusStripViewModel` | `ViewModels/StatusStripViewModel.cs` | Las barras de la barra de estado: contexto de la tarea en curso más llena, Claude 5 h y 7 días, Codex con el permiso, y presupuesto. Se rehacen como mucho cada 250 ms. Bloqueado, vacía |
| `ApprovalTitles` | `Orchestrator/Approvals/ApprovalTitles.cs` | El título en castellano de cada acción que pide aprobación («Entregar a tu rama»), con las constantes de los módulos como clave. El identificador sigue en el registro y en `ApprovalRequest.Action`; el diálogo lo enseña como línea secundaria (`ActionDetail`) |
| `ApprovalDialogViewModel(…, string? projectName)` y `ApprovalPromptService(…, Func<string, string?>? projectNames)` | `Orchestrator/Approvals/` | El proyecto se nombra por su carpeta; sin nombre, no se nombra (nunca su id) |
| `IKeepAwakeLease` | `Background/KeepAwake.cs` | Un permiso de «mantener despierto» que dice por qué se suelta (`Release(why)`), para el registro de la app (V26) |
| `MarkdownLite`, `MarkdownFlowDocument` y `MarkdownToFlowDocumentConverter`, `RichTextDocument`, `JournalExcerpt` | `Markdown/` | La bitácora legible (V31): la parte del Markdown que usa la bitácora, convertida en un documento de solo lectura, y lo que se dibuja de un día largo |
| `ToString()` de los elementos de listas | Varios (A1-bis) | Cada registro o clase que sale como elemento de una lista o un selector da su etiqueta, que UI Automation usa como nombre |

### 19.6 Textos de la bitácora, la memoria y la entrega

- **`MarkdownDailyJournal.Preamble` y `ResultPreamble`** (internos): las dos líneas que la app escribe bajo el título de
  un día que crea ([docs/11 §1](11-bitacora.md#1-dónde-y-cómo); adenda de
  [ADR-0008](adr/0008-bitacora-obligatoria.md)). `DailySummaryBuilder` reconoce las del hook y las de la app.
- **`DerivationTexts`** (interno): las entradas que la memoria deriva del registro dicen «La persona…» y la hora en UTC,
  escrita: «el 29/10/2026 a las 22:40 UTC» (V27). Son deterministas: el mismo registro da el mismo texto en cualquier
  equipo. Las entradas ya ancladas conservan su texto.
- **`DeliveryService`** (`Orchestration/Integration`): la descripción de la entrega cuenta los ficheros con «Cambia 1
  fichero.», «Cambian N ficheros.» o «No cambia ningún fichero.».

## 20. Fase 12: índice semántico de la bitácora y la memoria

Lo que la Fase 12 añade o cambia en los contratos ([17](17-constructor-visual-de-agentes.md) §3 y §9.2;
[ADR-0047](adr/0047-indice-semantico-local.md) con sus adendas; la adenda del 2026-11-03 de
[ADR-0024](adr/0024-recomendador-asistido-con-mcp-local.md)). El P0 dejó los contratos, el formato de la bitácora y los
paquetes; WP1 (la bitácora como memoria), WP2 (embeddings y almacén), WP3 (búsqueda híbrida y empaquetado), WP4 (la
herramienta MCP y «Memoria») y la integración cambiaron lo que dice cada apartado. Hay un tipo nuevo del registro,
`JournalTrustChanged`, al final y en `V = 1`; los payloads anteriores se leen y se reescriben byte a byte, y no hay
migración: el índice no vive en la base del proyecto. `tests/AgentStudio.Tests/Application/Semantic/SemanticIndexContractsTests.cs`
y `Phase4ContractTests.cs` fallan si un contrato compartido cambia.

### 20.1 Dominio (`Domain/Semantic`)

- **`SemanticFragment`**: un trozo de 1 500 caracteres como mucho (`MaxCharacters`), con su fuente
  (`FragmentSource`: la memoria con el SHA-256 que anunció `MemoryRecorded`, o una entrada del registro con su hash), su
  parte, su fecha y su huella. `IdOf` da el id a partir de la fuente y la parte, así que reconstruir da los mismos ids.
- **`LexicalTerms`**: los términos de la consulta sin palabras vacías; cada uno va entre comillas y unidos con `OR`.
- **`HybridRanking.Fuse`**: la fusión por rangos recíprocos con k = 60 y el mismo peso para las dos listas (D-05; la
  adenda del 2026-11-04 quitó el peso que había puesto la segunda). Los empates, por fecha (lo reciente primero) y por id.
  `SimilarCandidate` lleva la similitud de cada puesto de la lista por significado; `FusedCandidate`, los dos puestos.

### 20.2 Memoria y bitácora (`Application/Knowledge`, `Application/Decisions`; WP1)

```csharp
// MemoryKind gana, al final: JournalEntry.
public enum JournalTrustScope { Project, Day }
public sealed record JournalTrustState(bool ProjectTrusted, IReadOnlyList<DateOnly> TrustedDays, int QuarantinedEntries);
public interface IJournalTrust
{
    Task<JournalTrustState> ReadAsync(ProjectContext project, CancellationToken ct);
    Task SetAsync(ProjectContext project, JournalTrustScope scope, DateOnly? day, bool trusted, string decidedBy, string? comment, CancellationToken ct);
}
// DecisionKind gana, al final: JournalTrustChanged, con JournalTrustChangedPayload(Scope, Day, Trusted, DecidedBy, Comment), fuera de toda ejecución.
public sealed record AgentRunAnchor(string Run, string? Task, int Attempt);       // AgentRunRequest.Anchor y PromptOutcome.Anchor
public readonly record struct JournalEntryId(DateOnly Day, int Sequence) { public string Qualified(); }   // 2026-11-03/P-0001
public sealed record JournalReceipt(DateOnly Day, int Entry, string Sha256);   // AgentRunFinishedPayload.Journal, solo si existe
```

- **El ancla de la línea «Resultado»** (`JournalResultAnchor.Format(anchor, entry)` y `TryParse`): al final,
  `` · ejecución `<run>` ``, `` · tarea `<task>` `` si la hay, `` · intento <n> `` y `` · entrada `AAAA-MM-DD/P-NNNN` ``. La
  línea de la entrada termina con `(… · entrada AAAA-MM-DD/P-NNNN)`. Se detalla en [11 §8](11-bitacora.md).
- **El recibo** (`JournalReceipt`): el SHA-256 del texto exacto de la entrada, su línea y su línea «Resultado» unidas por un
  salto de línea. Lo da `IPromptJournal` al escribir el resultado de un lanzamiento anclado, y lo guarda en
  `AgentRunFinished` quien registra el final: el motor, las auxiliares, los turnos de debate y el juez.
- **La confianza de una entrada de la bitácora la calcula el registro** (`MemoryLedger.JournalTrustOf`): la revisión de la
  propia entrada; lo heredado, en cuarentena; lo que escribió Agent Studio con su recibo, de confianza (`System`); y lo
  demás, la última decisión sobre su día o sobre el proyecto. Sin ninguna, `Quarantined`. Una decisión sobre la bitácora
  solo cuenta si es posterior al último `origin-accepted`.
- **La fuente** de una entrada es `bitacora:<día>#sha256:<hash>` (`MemorySources.Journal` y `TryParseJournal`). No se
  exporta a Markdown (`MemoryKinds.IsExported`).
- **`IMemoryDerivation`** ancla también la bitácora, entrada a entrada e idempotente. Un día que no se puede leer con
  seguridad es un problema de la derivación (`MemoryDerivationResult.Problems`), y no se ancla nada de él.

### 20.3 Embeddings e índice (`Application/Semantic/SemanticIndexContracts.cs`)

| Contrato | Qué es | Lo implementa |
|---|---|---|
| `EmbeddingModelDescriptor`, `EmbeddingModels.MultilingualE5Small`, `EmbeddingModelFile`, `SentencePieceIds` | El modelo fijado: revisión, ficheros con su tamaño y su SHA-256, prefijos, 384 dimensiones, 512 tokens y la regla de ids. `Fingerprint()` lo nombra en cada vector | P0 |
| `SemanticIndexLocation` | Dónde viven el modelo y el índice, en `%LOCALAPPDATA%`; la clave del proyecto es la de su ancla | P0 |
| `EmbeddingModelState`, `EmbeddingModelStatus` | `Missing`, `Ready`, `Mismatch` y `Unloadable`, con la carpeta y el porqué | P0 |
| `IEmbedder`, `IEmbedderSource` (`Check`, `OpenAsync`), `EmbedderOpening` | El modelo en el proceso, sin red. `Check()` no carga nada y solo vuelve a calcular el hash de un fichero si cambia su tamaño o su hora | `Infrastructure/Embeddings/Onnx/OnnxEmbedderSource` (WP2) |
| `IEmbeddingModelInstaller`, `EmbeddingModelDownloadConsent`, `EmbeddingModelDownloadProgress` | La descarga, una vez y solo con el OK de la persona; un fichero que no verifica se borra | `Infrastructure/Embeddings/HttpEmbeddingModelInstaller` (WP2) |
| `ISemanticIndexStore`, `IndexedFragment`, `IndexedFragmentState` | El almacén de un proyecto: fragmentos, FTS5 y vectores en float32. Una fila que no es un fragmento sale con la huella vacía y la reescribe la siguiente lectura | `Infrastructure/Persistence/SemanticIndexStore` (WP2) |
| `ISemanticIndex` (`RefreshAsync`, `SearchAsync`, `SearchManyAsync`, `StatusAsync`, `SnapshotAsync`, `DeleteAsync`) | El índice: la proyección de lo anclado, la búsqueda híbrida y la instantánea del planificador | `Memory/Semantic/SemanticIndex` (WP3) |
| `SemanticQuery` (`TrustedOnly`, `MayReachAPrompt`), `SemanticHit`, `SemanticSearchResult` (`Cut`, `Notice`), `MeaningOnlyCut` | La consulta y su resultado | P0 y WP3 |
| `SemanticSearchMode`, `WordsOnlyReason`, `SemanticSearchTexts` | Por qué una búsqueda fue solo por palabras, en castellano | P0; WP3 añadió `IndexUnavailable` |
| `SemanticIndexRefresh`, `SemanticIndexStatus` | Lo que hizo un refresco, y el estado del índice y del modelo | P0 |

- **WP2.** `EmbeddingModelLocation` (la carpeta del modelo; `ForCurrentUser()` y `Under(localAppData, model)`) y
  `SemanticIndexOptions` (`ForCurrentUser()`). El almacén es uno por app: su escritor único es la instancia, con una cola y
  un consumidor por índice. `Directory.Build.targets` solo copia los nativos de ONNX Runtime de win-x64 y win-arm64.
- **WP3.** `SearchManyAsync`, con una implementación por defecto que busca una a una, comparte una lectura y un embedding
  del foco por paquete. El corte de lo que entra por significado (`MeaningOnlyCut`, suelo 0,79 y margen 0,04 para e5) vive
  en `SemanticSearchTuning` por la huella del modelo. Los términos que están en más de la mitad de los fragmentos salen de
  la lista por palabras (`CommonTermFraction` = 0,5). Una entrada de la bitácora se busca por palabras en su texto anclado
  completo y por significado en su vista limpia (`JournalSearchText`, `FragmentCutter.MeaningOf`). Ver las adendas de
  ADR-0047.
- **`AddAgentStudioSemanticIndex`** (`Memory/Semantic`) registra el índice. Necesita la memoria, el almacén y el modelo,
  que no registra.

### 20.4 El paquete de contexto (`Application/Knowledge/ContextPackContracts.cs`; WP3)

```csharp
public sealed record ContextRelevance(SemanticSearchMode Mode, WordsOnlyReason? Reason = null)
{ public static ContextRelevance Hybrid { get; } public static ContextRelevance WordsOnly(WordsOnlyReason reason); public MeaningOnlyCut? Cut { get; init; } public string? Notice { get; } }
// ContextPack gana: ContextRelevance? Relevance { get; init; }, fuera de su Sha256.
```

- `ContextPacker(IProjectMemory memory, ISemanticIndex? index = null)`: con índice, los niveles por relevancia se ordenan
  por la búsqueda híbrida, y la bitácora entra en el último. Sin él, o si falla, por FTS5 como antes
  (`WordsOnly(IndexUnavailable)`). Un juez o un auditor no reciben nada, y no se pregunta al índice por ellos (F12-8).
  `Relevance` es nulo cuando no se pidió relevancia: un agente sin memoria o sin foco.

### 20.5 La herramienta del planificador (`Application/Semantic/ProjectHistorySnapshot.cs` y `ProjectHistoryTool.cs`)

```csharp
public static class ProjectHistoryNames { Tool = "search_project_history"; SnapshotArgument = "--history"; MaxTop = 10; DefaultTop = 5; MaxQueryLength = 500; MaxFragments = 2000; }
public sealed record HistoryFragment(string Id, string Label, DateTimeOffset At, string Text, float[]? Vector = null);
public sealed record ProjectHistorySnapshot(DateTimeOffset At, string? ModelFingerprint, int Dimensions, IReadOnlyList<HistoryFragment> Fragments);
public static class ProjectHistorySnapshotJson { Schema = "agentstudio-history-snapshot/1"; Write; Read }   // lectura estricta

// La integración:
public sealed record ProjectHistoryFolder(string SnapshotPath);
public interface IProjectHistoryFolders { Task<ProjectHistoryFolder> CreateAsync(string snapshotJson, CancellationToken ct); void Delete(ProjectHistoryFolder folder); }
public sealed record ProjectHistoryToolOptions(string ServerExecutable) { public int MaxFragments { get; init; } public Func<bool>? Enabled { get; init; } }
public sealed class ProjectHistoryOffer : IDisposable { McpServerSpec? Server; string? Withheld; bool Offered; }   // Dispose borra la instantánea
public interface IProjectHistoryTool { Task<ProjectHistoryOffer> OfferAsync(ProjectContext project, AgentDefinition agent, PermissionProfile permissions, CancellationToken ct); }
public sealed class ProjectHistoryTool(ISemanticIndex index, IProjectHistoryFolders folders, ProjectHistoryToolOptions options) : IProjectHistoryTool;
public static class ProjectHistoryLaunch { AllowRule; IsReadOnly(permissions); Server(executable, snapshotPath); With(request, history); }
```

- **El servidor** (`AgentStudio.SkillsMcp`, WP4) acepta `--snapshot`, `--history` o los dos, y solo ofrece las
  herramientas de lo que recibe (`SkillsMcpServer.Options(skills, history)`). `search_project_history` ordena por palabras
  y, si la instantánea trae vectores, amplía con los vecinos de sus mejores aciertos (`HistoryRanking`); nunca carga el
  modelo ni devuelve un resumen diario.
- **Solo el planificador** la recibe: un agente `Orchestrator` con `ReadSelected`, en un lanzamiento sin escrituras, shell
  ni red, en Claude Code. A cualquier otro se le da una oferta sin servidor y el porqué (`Withheld`), sin leer el índice ni
  escribir nada. Codex rechaza los servidores MCP (`codex exec` no los carga), y Gemini no los lanza.
- **Se puede apagar** en «Ajustes» («Búsqueda en el historial para el orquestador», `ProjectHistoryToolOptions.Enabled`, que
  lee `AppSettings.PlannerHistoryToolEnabled` en cada planificación; activada por defecto): apagada, ningún planificador la
  recibe (`ProjectHistoryTool.DisabledReason`). Los motivos (`Withheld`) están en castellano llano, para la persona, sin
  nombrar el protocolo: «El orquestador no tuvo la búsqueda en el historial: Codex no admite herramientas externas.».
- **`ProjectHistoryLaunch.With`** añade el servidor de Agent Studio con `--history`. Si el lanzamiento ya tenía ese
  servidor con `--snapshot`, le añade `--history` y la regla de la herramienta: un solo servidor con las dos instantáneas.
  Rechaza un perfil que escribe, tiene shell o red.
- **Si el registro no verifica**, `OfferAsync` lanza `DecisionLogIntegrityException`, como la memoria. Cualquier otro fallo
  deja al planificador sin la herramienta, nunca sin su planificación (`ProjectHistoryTool.FailedReason`).

### 20.6 Orquestación (`Application/Orchestration`, `Orchestration`)

- **`PlanProposal.HistoryToolNotice`** (`string?`, `init`): por qué el orquestador planificó sin `search_project_history`
  (su CLI no carga servidores MCP, no hay historial de confianza, la plantilla no le da memoria…). No es un error. Es nulo
  si la tuvo o si la composición no la ofrece. Si la tuvo también lo dice el prompt, capturado como evidencia, que solo
  entonces la nombra en una frase (`PlannerPrompt.HistoryToolSentence`). El documento del orquestador lo añade a la línea
  de estado de la propuesta (`ProposalStatus`) y lo guarda en el borrador (`PlanDraftProposal.HistoryToolNotice`), así que
  un borrador recuperado lo sigue diciendo.
- **`Orchestrator(…, IPowerEvents? power = null, IProjectHistoryTool? history = null)`** y `EnginePorts.History`. El
  planificador pide la oferta después de la memoria y antes de escribir nada, pasa el mismo servidor a sus dos
  lanzamientos (la corrección es otro proceso del CLI) y borra la instantánea al terminar. Nada más del motor lee el puerto:
  ni los ejecutores, ni la revisión, ni los auditores.

### 20.7 Escritorio (`Desktop`; WP4 y la integración)

| Pieza | Fichero | Qué es |
|---|---|---|
| `MemorySearchBackend(Index, JournalTrust, Installer, UiContext)` y `ModelInstalledEventArgs` | `Memory/MemorySearchBackend.cs` | Lo que «Memoria» usa además de la memoria: el índice, la confianza de la bitácora y el instalador. Es el séptimo argumento de `MemoryViewModel` |
| `MemoryViewModel.ModelInstalled` e `IndexRefreshedAsync()` | `Memory/MemoryViewModel.cs` | La descarga terminó con el modelo en su sitio; el índice se refrescó y hay que volver a leer su estado |
| `EmbeddingModelViewModel`, `JournalTrustViewModel`, `MemorySearchResults`, `RefreshQueue` | `Memory/` | El panel del modelo, el de la bitácora, los resultados de la búsqueda y la cola de refrescos de la pantalla |
| `SemanticIndexUpkeep` (`RefreshAsync`, `WarmUpAsync`, `Follow`, `Refreshed`) | `Memory/SemanticIndexUpkeep.cs` | El refresco del índice en segundo plano: una cola con un solo consumidor por proyecto, que junta lo que llega mientras corre; nunca falla a quien lo pide. Abre el modelo antes de la primera búsqueda si ya está en su sitio, y nunca descarga |
| `ProjectMemoryUpkeep(…, SemanticIndexUpkeep? index = null)` | `Memory/ProjectMemoryUpkeep.cs` | Tras cada derivación completa, pide el refresco del índice. Al abrir un proyecto, abre el modelo si está |
| `ProjectHistoryFolders` | `Memory/ProjectHistoryFolders.cs` | Las instantáneas del planificador en `%LOCALAPPDATA%\AgentStudio\history-snapshots\his-<aleatorio>\history.json`, con `CreateNew`, y se borran al terminar. Como `SkillRecommendationFolders` |
| `AppSettings.PlannerHistoryToolEnabled` y `MemorySettingsViewModel` | `Services/AppSettings.cs`, `Memory/MemorySettingsViewModel.cs` | La preferencia «Búsqueda en el historial para el orquestador» (activada por defecto) y la sección «Memoria del proyecto» de «Ajustes», que se guarda con las demás en un solo `SavePreferences` |
| `PlanDraftProposal(…, string? HistoryToolNotice = null)` | `Orchestrator/Canvas/PlanDraft.cs` | El borrador guarda por qué el orquestador planificó sin la búsqueda en el historial; uno de antes lo lee como nulo |
| `AppFolders.SemanticRoot` | `Composition/AppComposition.cs` | La carpeta que hace de `%LOCALAPPDATA%` para el índice y el modelo. Para el usuario actual es la de `SemanticIndexOptions.ForCurrentUser()`; en una prueba, la que contiene sus carpetas locales |
| `SemanticIndexServices.AddSemanticIndexServices` | `Composition/SemanticIndexServices.cs` | La composición: un solo `SemanticIndexStore`, `OnnxEmbedderSource`, `HttpEmbeddingModelInstaller` con su `HttpClient`, `AddAgentStudioSemanticIndex` y la herramienta del planificador sobre el servidor de al lado de la app |

## 21. Fase 13: constructor visual de agentes

Lo que el P0 de la Fase 13 deja fijado para los paquetes WP1 a WP8, que van en paralelo encima ([17](17-constructor-visual-de-agentes.md)
§4, §8 y §9.3; [ADR-0054](adr/0054-idiomas-y-lenguaje-claro.md) completo y los esqueletos de los ADR
[0048](adr/0048-constructor-visual-equipo-y-plan.md) a [0053](adr/0053-windows-hello-como-barrera-fija.md), que completa cada
paquete). Hay tres tipos nuevos del registro al final de `DecisionKind`, en `V = 1`; `AgentRunStarted` gana la versión 3; los
payloads anteriores se leen y se reescriben byte a byte, y el hash de las plantillas y las cabeceras guardadas no cambia
(`RosterHashTests` y `RunHeaderHashTests` lo fijan con los valores del 2026-11-06). No hay migración: lo nuevo de la plantilla
y de la cabecera va en su JSON, solo cuando se fija. `Phase13PayloadTests`, `DecisionKindCompatibilityTests` y
`Phase13StorageTests` fallan si un contrato compartido cambia.

### 21.1 Dominio (solo los tipos y sus invariantes)

| Tipo | Fichero | Qué es | Lo usa |
|---|---|---|---|
| `ReasoningEffort` (`Minimal`, `Low`, `Medium`, `High`, `XHigh`, `Max`), `ReasoningEfforts.Token`, `ReasoningEfforts.For(provider)` y `Accepts` | `Domain/Models/ReasoningEffort.cs` | La lista cerrada de esfuerzos, el valor que recibe cada CLI y la tabla por proveedor (revisión del 2026-11-08): Claude Code de `Low` a `Max`, Codex de `Minimal` a `XHigh`, otro proveedor ninguno. Los nombres entran en el hash: nunca se renombran | WP2 (lanzamiento), WP6 (inspector) |
| `ModelTier` (`Light`, `Balanced`, `Maximum`) | `Domain/Models/ModelTier.cs` | El nivel en palabras; sin clasificar es nulo | WP1, WP2, WP6 |
| `ModelCatalog`, `ModelCatalogEntry`, `ModelRecommendationRules` | `Domain/Models/ModelCatalogEntry.cs` | El catálogo local sin precios (D-23) y las reglas de partida de D-24, con su `Validate()` | WP2 (lector), WP1, WP6, WP7 |
| `ModelRecommendationInput`, `ModelPerformance` (conocido con 3 referencias), `ModelRecommendation`, `ModelRecommendationReasons` | `Domain/Models/ModelRecommendation.cs` | Entradas y resultado de la recomendación (D-24), con su razón como código. Las reglas (`ModelRecommender`) son de WP2 | WP2, WP6, WP7 |
| `AgentDefinition.Effort` (`ReasoningEffort?`, último parámetro, nulo por defecto) y las reglas `agent.effort-unknown` y `agent.effort-not-for-provider` | `Domain/Agents/AgentDefinition.cs` | El esfuerzo del agente (D-25), que tiene que ser uno de los niveles de su CLI | Todos |
| `AgentQuestion`, `QuestionOption`, `QuestionEffort`, `QuestionAnswer` (`Choosing`, `Writing`, `RecommendedByConsent`), `QuestionAnswerMethod` | `Domain/Questions/AgentQuestion.cs` | La pregunta del informe y la respuesta (D-28 a D-30). `QuestionAnswer.Task` es `TaskId?`: nulo para una pregunta del orquestador antes de planificar | WP1, WP4, WP8 |
| `QuestionRules` | `Domain/Questions/QuestionRules.cs` | Las invariantes de D-28 (de 2 a 5 opciones, ids, 400/80/300 caracteres, una recomendada, sin control ni bidi) y los topes de D-29 (3 por tarea, 10 por ejecución, 3 del orquestador) | WP4 |
| `PresenceAction`, `PresenceRequirement`, `PresenceProof`, `PresencePolicy` (`RequirementOf`, `AsksPresence`, `IsSatisfiedBy(acción o requisito, ajuste, helloAvailable, prueba)`) | `Domain/Security/PresencePolicy.cs` | La barrera fija de D-37: la tabla por acción y la regla única de qué confirmación vale. «Siempre», lea lo que lea el ajuste: con Hello en el equipo, solo Windows Hello; sin él, también el bloqueo de Windows desbloqueado después de la petición (WP5, re-revisión del P0 del 2026-11-08). «Configurable» con el ajuste: Hello y, sin Hello, también el bloqueo o el diálogo con su aviso de Hello no disponible; sin el ajuste, con el diálogo. Quien no sabe si hay Hello (la puerta) juzga con `helloAvailable: false`, el suelo | WP5, WP1, WP6 |
| `TeamGraph`, `AgentNode`, `TeamEdge`, `TeamEdgeKind`, `TeamLimits` (1, 8 y 1), `TeamRoles` | `Domain/Team/TeamGraph.cs` | La capa Equipo (D-08 a D-11): los nodos son la plantilla; los cables se comprueban (`Validate()`), y `SharesVendor` dice cuándo «Revisa» va en color de error. «Entrega» sale de la capa Plan y no es del equipo | WP6, WP1 |
| `RepositoryNode`, `RepositoryRemote` (`Cleaned`, `WithoutCredentials`) | `Domain/Team/RepositoryNode.cs` | El nodo del repositorio (D-10). Un remoto nunca guarda usuario ni contraseña: el constructor lo rechaza (F13-3) | WP6 |
| `Finding`, `FindingSeverity`, `FindingCodes` (los 16 de D-18 y, para el validador, `validator.` y su código en minúsculas con guiones: `validator.overlapping-write-scopes`), `MontageAnalysis` | `Domain/Orchestration/Analysis/Finding.cs` | Los hallazgos del analizador, con código, nodos y argumentos | WP1, WP6, WP8 |
| `Fix` y sus 13 arreglos tipados (`ChangesRoster`, `GrantsHighRiskCapability`) | `Domain/Orchestration/Analysis/Fix.cs` | Los arreglos de un clic, que se deshacen | WP1, WP6, WP3 |
| `OrderNotes`, `OrderDependencyNote`, `ParallelOrderNote`, `HarmfulOrderNote` | `Domain/Orchestration/Analysis/OrderNotes.cs` | Las notas de orden (D-19) y la respuesta de «Revisar el orden» (D-20) | WP1 |
| `MontageDraft`, `TaskContextFacts`, `QuotaWindowFacts`, `MontageRunFacts`, `MontageLearnedFacts`, `IMontageRule` | `Domain/Orchestration/Analysis/MontageDraft.cs` | Lo que lee el analizador y la forma de cada regla. Las reglas y el analizador que las junta son de WP1 | WP1, WP3, WP6 |
| `TextArguments` | `Domain/Common/TextArguments.cs` | Los argumentos de un texto con código: texto, números, instantes y duraciones | Todos |

### 21.2 Registro (`Application/Decisions`)

```csharp
// DecisionKind gana, al final: QuestionAsked, QuestionAnswered y ProjectSetupApplied.
// AgentRunPurpose gana, al final: OrderReview, ConsumptionAnalysis y PreparationChat.
// ApprovalVerification gana, al final: WindowsLock (los payloads guardan el nombre).
public sealed record OptionEstimateSnapshot(string Option, MetricSnapshot Low, MetricSnapshot High, int References);
public sealed record QuestionAskedPayload(TaskId? Task, int Attempt, AgentId Agent, string Question, EvidenceId QuestionEvidence,
    string QuestionSha256, IReadOnlyList<string> Options, string? RecommendedByAgent, IReadOnlyList<OptionEstimateSnapshot> Estimates);
public sealed record QuestionAnsweredPayload(TaskId? Task, string Question, string? ChosenOption, EvidenceId? AnswerEvidence,
    string? AnswerSha256, string DecidedBy, QuestionAnswerMethod Method) { static Answering(...); }   // una opción o tu texto
public sealed record ProjectSetupAppliedPayload(string? RosterHash, string? BriefSha256, string? VerificationProfileHash,
    string? SkillActivationsSha256, string? OptionsSha256, string DecidedBy) { static Applying(...); }   // fuera de toda ejecución
public enum ContextPartKind { Protocol, Requirements, ProjectBrief, AgentInstructions, Memory, Upstream, PreviousAttempt, HumanAnswer, Skills }
public sealed record ContextPartSnapshot(ContextPartKind Kind, int Characters);
// AgentRunStartedPayload gana IReadOnlyList<ContextPartSnapshot>? ContextParts (V = 3, ContextPartsVersion), solo si lo hay.
```

- **Ningún payload lleva texto de un agente:** la pregunta entera es evidencia (D-29), y tu texto propio, una evidencia tuya.
- **El JSON de cada uno está fijado** en `Phase13PayloadTests`, con el hash de una entrada calculado aparte. Una respuesta
  confirmada por el bloqueo de Windows se escribe con `"verification":"WindowsLock"` en la versión 2 de siempre.
- **`MemoryKind.ProjectBrief`** (al final), con `ProjectBriefs` (tema, fuente y tamaño), `ProjectBrief` e `IProjectBrief`
  (`Knowledge/MemoryContracts.cs`; lo implementa WP3). Se exporta a Markdown como `tema-ficha-del-proyecto.md`. Tiene su
  nombre donde la memoria lo escribe: el resumen de su entrada del registro (`MemoryLabels`), la etiqueta del índice semántico
  (`SemanticLabels`) y el título de su fichero exportado; `MemoryKindNamesTests` recorre todas las categorías en los tres.

### 21.3 Orquestación (`Application/Orchestration`, `Infrastructure/Persistence/Json/RunJson.cs`)

- **`RosterHash`:** el esfuerzo cuenta solo si se fija, detrás de todos los agentes, como `effort`, el agente y su valor. Una
  plantilla sin esfuerzos da el hash de siempre.
- **`RunOptions.AnswerWithRecommendedAfter`** (`TimeSpan?`, nulo por defecto; un minuto como mínimo en `EnsureValid`): el
  consentimiento de contestar con la recomendada (D-33, decisión 4 (a)). `RunHeaderHash` lo escribe al final, solo si lo das.
- **El JSON de la plantilla y de la cabecera** (`AgentJson.Effort`, `RunOptionsJson.AnswerWithRecommendedAfter`) solo los
  escribe cuando se fijan: lo guardado antes se guarda y se lee igual.
- **`IOrchestrationRun.AnswerAsync(QuestionAnswer)`:** su cuerpo por defecto lanza `NotSupportedException`. WP4 lo implementa
  en el motor; una ejecución inspectada desde su registro nunca admite respuestas.
- **`PendingQuestion`** y **`TaskView.Question`** (nulo por defecto): la pregunta que espera. Sus estimaciones
  (`IReadOnlyList<OptionEstimateSnapshot> Estimates` y `EstimateOf(option)`) son las mismas que registra `QuestionAsked`: el
  intervalo de tokens totales con su origen y sus referencias, así que lo que se enseña se reconstruye igual tras reiniciar. El
  motor convierte a ellas el `UsageEstimate` del estimador de WP2 al registrar la pregunta, con `UsageEstimate.ToSnapshot`
  (el intervalo de `TotalTokens`; sin total, sin valor y con el motivo).
  **`QuestionPendingEvent`** y **`QuestionAnsweredEvent`** (en `OrchestrationRun.cs`): lo que el monitor y la bandeja escuchan.
- **Las preguntas del orquestador antes de planificar** (D-28): `PlanProposal.Questions` (`IReadOnlyList<PendingQuestion>?`,
  `init`; con preguntas, `Plan` es nulo) y `PlanningRequest.Answers` (`IReadOnlyList<AnsweredQuestion>?`, `init`) para la
  planificación siguiente. `AnsweredQuestion(RunId Run, PendingQuestion Question, QuestionAnswer Answer)` comprueba que la
  respuesta es de esa pregunta (mismo id y misma tarea, nula en las del orquestador); `Run` es la ejecución de la
  planificación que preguntó. `AnswerAsync` no las contesta: son de la planificación, no de una ejecución. Desde WP4
  (re-revisión del P0, 2026-11-08) rechaza además una opción que la pregunta no tiene, el texto propio en una pregunta sin
  `AllowOther` y `RecommendedByConsent` en una pregunta del orquestador, que no tiene cabecera donde dar ese consentimiento.
- **Lo que WP4 añade al motor y al dominio** ([ADR-0051](adr/0051-preguntas-de-los-agentes.md)):
  - `Domain/Orchestration/RunStateReplayer.cs`: `QuestionPosed` y `QuestionResolved` (eventos nuevos), `TaskTransitioned.Block`
    (`string?`, último parámetro, nulo por defecto: quien no lo da reproduce el grafo de siempre),
    `RunStateReplayer.AwaitingAnswerBlock` y `RunReplay.PendingQuestions`, `PendingRunQuestions` y `AnsweredQuestions`.
  - `Orchestration/Engine`: el informe con `question` (`ExecutorReport.Question`, `ReportedQuestion`, `ReportedOption`,
    `AgentQuestion()`; el esquema pasa de 575 a 1 142 bytes), `BlockKind.AwaitingAnswer` (texto `AwaitingAnswer`, que el
    monitor y `DerivationTexts` tienen que nombrar), `HandoffReason.SessionNotResumable`, `humanAnswers` en el traspaso y
    `OrchestrationRun.AnswerAsync`. `Orchestrator` gana tres puertos opcionales al final del constructor, `brief`
    (`IProjectBrief`), `redactor` (`ISecretRedactor`) y `estimator` (`IUsageEstimator`), que compone `Desktop`.
  - El dosier del juez deja fuera los prompts de los ejecutores, la pregunta tal como la escribió el agente, todo informe de
    ejecutor que traiga una pregunta (registrada o no) o que no se pueda leer, y las entradas `QuestionAsked`. La decisión
    que captura el sistema (`agent-studio/question-decision/1`) solo lleva ids, huellas e instantes, ningún texto del agente.

### 21.4 Estimación, análisis, idioma y presencia (`Application`)

| Contrato | Fichero | Qué es | Lo implementa |
|---|---|---|---|
| `IUsageEstimator`, `UsageEstimate` (conocido con 3 referencias), `EstimateInterval<T>`, `PlanEstimate`, `UsageEstimateRequest`, `PlanEstimateRequest`, `WorkSize`, `EstimateAssumptions` | `Usage/EstimateContracts.cs` | El estimador de docs/16 D-20, adelantado: percentiles 25 y 75 con su origen, referencias y supuestos; nunca un número de relleno. **Revisión del 2026-11-09 (WP2):** `UsageEstimate.TotalTokens` (`EstimateInterval<long>?`, último parámetro, nulo por defecto) es el intervalo de los tokens totales calculado como tal (la entrada más la salida, como las cuenta el panel de uso: la entrada es el prompt entero, con la caché leída y escrita en Claude y la entrada en caché en Codex, y la salida de Codex incluye su razonamiento), con los percentiles de los totales y nunca la suma de los percentiles de cada parte, y `UsageEstimate.ToSnapshot(option)` lo da como el `OptionEstimateSnapshot` que registra `QuestionAsked` (sin total, o con menos de tres referencias, sin valor). `UsageEstimate.Unknown(referencias, motivo)` da una estimación desconocida que dice por qué (un registro que no verifica: `UsageEstimator.UntrustedLogReason`). `EstimateAssumptions` gana `SameModel` («aprendido del modelo»: menos de tres intentos del agente, así que cuentan los del mismo modelo y propósito de cualquier agente), `AgentsOnly` (la duración de un plan solo cuenta a los agentes) y `All`. Las reglas, en ADR-0050 | WP2 (`Observability/Estimates`, `AddAgentStudioUsageEstimator`) |
| `IConsumptionAnalyzer`, `ConsumptionReport`, `ConsumptionFinding`, `ConsumptionFindingCodes` (los 12 de D-34), `ConsumptionTarget` | `Analysis/ConsumptionAnalysisContracts.cs` | El análisis determinista del consumo | WP3 (`Observability/Analysis`) |
| `IAssistedConsumptionAnalysis`, `AssistedConsumptionRequest`, `AssistedConsumptionAnalysis`, `AssistedConsumptionSuggestion`, `AssistedConsumptionLimits` | ídem | El análisis con un agente del vendor que no produjo, con tu OK (D-35) | WP3 |
| `UserText` (`Code`, `Arguments`, `ResourceKey`), `UserTextCodes` | `Localization/UserText.cs` | Un texto como código y parámetros; su recurso es `Text_<código>` (D-47) | Las capas de abajo; lo pinta Desktop |
| `IWindowsLockConfirmation`, `WindowsLockResult`, `PresenceProofs` (`FromHello`, `FromWindowsLock`, `Verification`, `Claimed`) | `Security/UserPresenceContracts.cs` | El bloqueo de Windows comprobado, cuando no hay Hello (D-40 (a)), y la conversión de los resultados de Windows a `PresenceProof`: solo un Hello verificado o un bloqueo desbloqueado después de la petición confirman a la persona. `Claimed` lee un método registrado como la prueba que afirma (la puerta, el motor y el registrador, WP5) | WP5 (`Desktop/Lock`) |

### 21.5 Escritorio (`Desktop`; no son puertos, pero los paquetes dependen de ellos)

| Pieza | Fichero | Qué es |
|---|---|---|
| `Resources/Strings.resx` (es-ES, neutro) y `Strings.en.resx`; ficheros por área `Resources/<Área>Strings.resx` | `Resources/` | Los textos de la interfaz (ADR-0054). Cada paquete pone sus claves en su fichero de área, con su satélite inglés: ninguno comparte fichero. MSBuild genera la clase tipada de cada uno en `obj` (`GenerateTypedStrings`) |
| `IUiTextProvider`, `ResourceTextProvider`, `Texts` (`Get`, `Format`, `Render`, `UseLanguage`) | `Localization/ResourceTextProvider.cs` | El proveedor de textos. Lee todos los `*Strings` del ensamblado. `UseLanguage` lo aplica una vez, al arrancar (pendiente en `App.OnStartup`, del lead) |
| `TExtension` | `Localization/TExtension.cs` | `{l:T Clave}` en XAML |
| `UiLanguages`, `UiLanguage`, `TranslationReview`, `UiLanguages.SpanishSpeakingRegions` | `Localization/UiLanguages.cs` | Los idiomas y la regla de D-49: el elegido, el de Windows si está en la lista, castellano de España si Windows está en catalán, euskera o gallego (adenda del 2026-11-08 de ADR-0054), o inglés |
| `ContextPartTexts` | `Localization/ContextPartTexts.cs` | El nombre de cada parte del contexto, de los recursos |
| `InfoButton` (`InfoKey`, `Topic`, `TextProvider`, `MoreHelpCommand`) | `Controls/Info/InfoButton.xaml` | El botón ⓘ (D-54): textos `<InfoKey>_Title`, `_What` y `_Actions`; «Más ayuda» solo con destino |
| `IRunLiveProjection`, `RunLiveState`, `AgentLiveState`, `AgentLiveStatus`, `TaskLiveState`, `LiveContext`, `RunLiveChangedEventArgs`, `LiveProjection` (250 ms, 12 animaciones, 30 fps) | `Orchestrator/Live/IRunLiveProjection.cs` | La proyección agrupada del monitor (D-14 y D-16). La implementa WP8 en el monitor; la leen WP6 y la barra superior |
| `MetricBarSegment`, `MetricBarReading.Segments`, `SegmentEnds` y `PartsText` | `Controls/MetricBarReading.cs`, `MetricBar.xaml(.cs)` | Lo que le faltaba a la barra común para F13-6: las partes, con un hueco donde acaba cada una y sus nombres en las palabras («Por partes: …») |
| `AppSettings.Language`, `ReduceMotion` y `WindowsHelloForImportantApprovals` | `Services/AppSettings.cs`, `AppSettingsStore.cs` | Preferencias de los paneles. El ajuste de Hello se llamaba `WindowsHelloForCriticalApprovals` (§18.6): el almacén lee el nombre viejo y escribe el nuevo; si están los dos, gana el nuevo. Un fichero con una clave repetida se lee como antes, con el último valor de cada una (y entonces el nombre viejo de Hello no se traslada: queda su valor por defecto, activado). Un idioma fuera de la lista se lee como «sin elección» |
| `ObservabilitySettingsViewModel.CodexExplanation` | `Observability/ObservabilitySettingsViewModel.cs` | El texto de la cuota de Codex dice lo que se hace (F13-20): bajo demanda, al terminar una ejecución de Codex (como mucho una vez cada 5 minutos) y mientras una ejecución espera un límite de Codex |

- **Nada escrito a mano** (F13-19): `Desktop/Localization/Phase13HandWrittenTextTests.cs` revisa todo XAML y todo C# de
  `src/AgentStudio.Desktop` que no está en el inventario de antes de la fase (`DesktopFilesBeforePhase13.txt`, junto a la
  prueba: los 262 ficheros de `main` en `6cf8bf7`, que son los de `96c8aca` sin los siete que creó el P0). Cada fichero nuevo
  de un paquete entra solo, en una carpeta nueva o de antes, sin listas a mano. En XAML mira también `ToolTipService.ToolTip`,
  `StringFormat`, `FallbackValue` y `TargetNullValue` dentro de un binding y `ContentStringFormat` y los suyos; en C#, además de
  las letras fuera de ASCII, una lista corta de palabras castellanas frecuentes de interfaz sin tilde. Las reglas y sus límites
  están en ADR-0054 (punto 8 y adenda del 2026-11-08).
- **Lo que la integración tiene que hacer:** aplicar el idioma al arrancar (`Texts.UseLanguage`), y registrar en la
  composición los puertos nuevos cuando sus paquetes los implementen.

### 21.6 WP3: contexto por partes, ficha del proyecto y análisis del consumo

Lo que WP3 añade o cambia sobre lo de arriba ([ADR-0052](adr/0052-analisis-del-consumo-y-contexto-por-partes.md)). El estimador
(`IUsageEstimator`, §21.4) lo implementa WP2 ([ADR-0050](adr/0050-modelos-y-potencia.md)), no WP3.

| Contrato | Fichero | Qué es |
|---|---|---|
| `ContextPack.Brief` (`ProjectBrief?`, `init`) y `ContextPack.Parts()` | `Application/Knowledge/ContextPackContracts.cs` | La ficha vigente con el paquete, como dato con su huella, para todo agente salvo el juez y los auditores; no cuenta en el presupuesto, en `Characters` ni en `Sha256`. Las partes del paquete en caracteres: requisitos, ficha y memoria |
| `ProjectBriefBlock` (`Tag`, `Heading`, `Block`, `Section`) | ídem | La sección `## Ficha del proyecto` con su bloque `<project_brief entry=… sha256=…>`, el texto recodificado por `PromptData`. La usan los constructores de prompts (WP4 y WP1) |
| `AuxiliaryAgentRunner.IsAuxiliary` y `RecordsEndClass` | `Application/Providers/AuxiliaryAgentRunner.cs` | Admite `OrderReview`, `ConsumptionAnalysis` y `PreparationChat` como auxiliares, y les escribe `AgentRunFinished` v3 con su clase de final; las de antes siguen en v2 |
| `ProjectBriefStore : IProjectBrief` | `Memory/Context/ProjectBriefStore.cs` | La ficha sobre la memoria (origen `User`, tema `ficha-del-proyecto`, fuente `ficha:proyecto`); cada versión sustituye a la vigente. `AddAgentStudioProjectMemory` la registra, y `ContextPacker` la recibe si está |
| `ContextPacker(memory, index, brief)` | `Memory/Context/ContextPacker.cs` | El tercer parámetro, opcional: la ficha |
| `AgentInvocation.ContextParts` (interno) y `PromptParts.Measure` (interno) | `Orchestration/Engine/AgentRunner.cs` | Las partes del prompt en `AgentRunStarted` v3; medidas sobre el texto exacto y las skills si el constructor no las da |
| `ContextBreakdown`, `ContextPartFigure`, `ContextPartsLog`, `AttemptContextParts` | `Observability/Context/ContextBreakdown.cs` | El desglose de un paso (partes estimadas y conversación como resto, «no expuesto» en Codex) y las partes de cada intento leídas del registro |
| `ModelWindowCatalog.LearnLive`, `LiveWindows`, `LearnsLive`, `Learning()`; `ContextHealthTracker(…, TimeProvider? time)` | `Observability/Context/ModelWindowCatalog.cs`, `ContextHealthTracker.cs` | La ventana aprendida en vivo (F13-20); `Empty` y `Baseline` no aprenden |
| `ConsumptionAnalyzer`, `ConsumptionAnalysisOptions`, `ConsumptionRules`, `ConsumptionInput`, `ConsumptionThresholds`, `AttemptRef`, `AttemptChange`, `ConsumptionTexts` | `Observability/Analysis/` | El análisis determinista (`IConsumptionAnalyzer`) con sus umbrales y el orden de los argumentos de cada código |
| `AssistedConsumptionAnalysisService`, `AssistedConsumptionOptions` | ídem | El análisis con un agente (`IAssistedConsumptionAnalysis`) |
| `AddAgentStudioConsumptionAnalysis` | `Observability/Analysis/ConsumptionAnalysisServiceCollectionExtensions.cs` | Registra los dos análisis; el catálogo de modelos va en sus opciones |
| `Resources/ConsumptionStrings.resx` y `.en.resx` | `Desktop/Resources/` | `Text_<código>` y `Text_<código>_suggestion` de los 12 códigos (`work.discarded` no sugiere) |

- **`AgentRunStarted` v3 en el motor:** todo arranque de `AgentRunner` con partes; un arranque sin ellas sigue en v2. El juez,
  los debates y las auxiliares registran su arranque fuera y no tienen partes.
- **Para la integración:** registrar `AddAgentStudioConsumptionAnalysis` con el catálogo de WP2; que WP8 llame al análisis
  determinista al terminar cada ejecución y con su botón, y al de un agente con el OK; y que el motor (WP4) pida el paquete
  también para los agentes sin memoria si deben recibir la ficha.
