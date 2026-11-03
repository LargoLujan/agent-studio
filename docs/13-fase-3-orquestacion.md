# 13 · Fase 3 · Orquestación: diseño

> **Estado:** aprobado el 2026-10-15 por el orquestador de la sesión, con la delegación del usuario («así como
> dices»). Parte del código de `main` (2c1b923).
>
> Lo redactó un arquitecto independiente. Sobre su propuesta se cambió una decisión:
>
> - **Independencia del juez (decisión 7, §6).** El juez corre en un vendor distinto del orquestador **y** de los
>   ejecutores, como pide ADR-0006, que no se enmienda. Con dos vendors, el orquestador y los ejecutores comparten
>   uno y el juez usa el otro.
>
> También quedan resueltas las preguntas abiertas (§9).
>
> **Cambios posteriores a las revisiones de seguridad (2026-10-16/17).** Mandan sobre el texto de abajo:
>
> - **Integración (§4.7, §5):** las tareas se fusionan en una ref candidata `refs/agentstudio/<run>/_candidate/<tarea>`.
>   El sistema la verifica y solo entonces avanza la rama `agentstudio/run-<run>` con CAS. Las integraciones de una
>   ejecución quedan ordenadas por ascendencia. Ver las adendas de [ADR-0012](adr/0012-worktrees-fuera-del-repo.md).
> - **Verificación:** build y tests corren a integridad Low sobre una copia del commit sacada del repositorio del
>   proyecto ([ADR-0017](adr/0017-verificacion-aislada-baja-integridad.md)).
> - **Integridad del repositorio del usuario:** se comprueba antes y después de cada paso que ejecuta código de
>   agentes: agente, verificación, herramienta e integración.
> - **Evidencias:** el juez y la reanudación solo usan las evidencias que el registro anuncia. Los tests llevan el commit
>   que verificaron, y el registro tiene un ancla externa ([ADR-0010](adr/0010-estado-derivado-del-registro.md)).
> - **Proveedores:** los CLIs no cargan la configuración ejecutable del repositorio (hooks, MCP)
>   ([06](06-integracion-proveedores.md)).
> - **Entrega:** exige registro íntegro, ejecución `Succeeded` y un veredicto aprobado del mismo commit, con una
>   aprobación de un solo uso atada a su contenido.
>
> La segunda ronda adversarial dejó hallazgos abiertos (N-01 a N-07), en corrección. Ver el estado de la Fase 3 en
> [08](08-plan-y-criterios-de-aceptacion.md#fase-3--orquestación).

## 0. Las siete decisiones que vertebran la propuesta

1. **El estado de una ejecución se deriva del `DecisionLog`, que ya está encadenado por hash.** Cada cambio de estado es una entrada.
   - Reanudar tras un reinicio es `Verify()` seguido de reproducir las entradas a través de la API pública de `WorkTask`/`TaskGraph`.
   - No hay doble escritura en las tablas `tasks*` de la migración 0001.
2. **Las tareas condicionales se modelan en el dominio**, con tres piezas:
   - un estado nuevo, `WorkTaskStatus.Skipped`, terminal;
   - aristas con rama (`branch`) que salen de los nodos `Condition`;
   - una política de unión, `JoinPolicy` (`AllCompleted`/`AnyCompleted`).

   Sin esto, `TaskGraph.Analyze` daría falsos «inalcanzables» (F3-3).
3. **Un bucle de planificación de un solo hilo (actor) es el único que muta el grafo.** El resto de los trabajos corre en paralelo y le envía señales.
   - Trabajos: agente, herramienta, integración, revisión y aprobación.
   - La concurrencia por proveedor la sigue imponiendo `ConcurrencyLimitedAgentProvider`.
   - Una tarea pasa a `Running` **solo cuando `StartAsync` devuelve**: la espera en la cola del proveedor no cuenta como abandono.
4. **Los worktrees viven fuera del repo**, en `%LOCALAPPDATA%\AgentStudio\wt\…`.
   - Se crean en HEAD separado y se bloquean con `git worktree lock`.
   - Se usan refs internas ocultas (`refs/agentstudio/<run>/<tarea>`) y una rama visible `agentstudio/run-<run>` para la integración.
   - **El sistema hace commits y merges; el agente nunca.**
   - El merge se hace sin worktree: `git merge-tree --write-tree`, luego `commit-tree`, luego `update-ref` con valor esperado (CAS). Así es idempotente.
   - Un conflicto es un fallo con evidencia: **nunca se resuelve solo**.
5. **El orquestador (Claude) solo planifica.**
   - Devuelve JSON contra un esquema portable: válido en Claude y en el modo estricto de OpenAI, y de ≤ 16 KB (`ClaudeCodeArguments.MaxJsonSchemaBytes`).
   - Lo valida un `PlanValidator` determinista del dominio.
   - Se admite una corrección como máximo, y el usuario aprueba el plan antes de gastar cuota.
6. **El juez se invoca a través de un caso de uso del módulo `Debate`: `IIndependentReview`.**
   - Construye el dossier desde los almacenes, llama a `IJudge` y valida con `VerdictValidator` sin cambiarlo.
   - Admite una corrección y persiste el veredicto en modo append-only.
   - **El motor nunca construye ni ve el dossier.**
7. **Independencia del juez con solo dos vendors (F3-8).**
   - `ExcludedVendors` contiene los vendors de **todos** los que produjeron lo revisado: el orquestador, que hizo el plan,
     y los ejecutores. Se calcula desde el registro de la ejecución.
   - ADR-0006 no cambia. Con dos vendors, el orquestador y los ejecutores comparten uno y el juez usa el otro.
   - Configuración del E2E:
     - orquestador y ejecutor: Claude Code (Anthropic), que va a cargo de la suscripción Max del usuario, la de más
       cuota;
     - juez: Codex (OpenAI) con `-s read-only`, de modo que el sistema operativo impone el solo lectura.
   - `PlanValidator` rechaza una plantilla en la que el juez comparta vendor con el orquestador o con algún ejecutor
     (código `JudgeVendorNotIndependent`).

---

## 1. Alcance de la Fase 3

| ID | Entra | Pieza principal |
|---|---|---|
| F3-1 | Tareas secuenciales, paralelas, condicionales y dependientes, en orden correcto | Bucle del motor, y en el dominio `Skipped`, ramas y `JoinPolicy`. Se prueba con un proveedor guionizado |
| F3-2 | Worktree por tarea e integración con build y tests | `IWorktreeManager` (git.exe), `IIntegrator` y `IVerificationRunner`. Test con un repo git temporal |
| F3-3 | Abandono, inalcanzables, reintentos agotados y bloqueo: detectados y mostrados | `Analyze` consciente de `Skipped`; monitor de abandono; `AuditAlertRaised`; panel de salud; demo sin cuota |
| F3-4 | `decision_log` en SQLite y `Verify` correcto tras reiniciar | `SqliteDecisionLogStore` y `DecisionRecorder`, con un test de reapertura y otro de manipulación |
| F3-5 | Evidencias de build y tests capturadas por el runner, con hash | `SqliteEvidenceStore` (el hash lo calcula el almacén) y `VerificationRunner` con TRX/JUnit |
| F3-6 | Lienzo Nodify: crear, conectar y validar; ciclos rechazados | ViewModels del lienzo, `DependencyCycleException.Cycle` y `PlanValidator` |
| F3-7 | Aprobación humana de acciones destructivas | `ApprovalGate` (Security) e `IApprovalPrompt` (Desktop), con fallo cerrado y registro |
| F3-8 | Claude orquesta y ejecuta; Codex juzga en solo lectura (vendor distinto de todos los productores), con veredicto validado | Planificador, motor, revisión independiente y E2E en vivo con tu OK |
| F3-9 | Skills por agente en cada ejecución (Agente > Proyecto > Global) | `ISkillProvisioner` sobre `SkillActivationResolver` e `ISkillMaterializer` |

**Se aplaza:**

- Nodos `Debate`: el validador los rechaza con «disponible en la Fase 5».
- «Auditar proyecto completo» y el auditor del orquestador: Fase 5.
- Replanificación a mitad de ejecución (`PlanRevised` con cambios). En la Fase 3 se sustituye por una **ejecución de seguimiento**.
- Resolución de conflictos asistida por agente.
- Aprobaciones interactivas por herramienta dentro del CLI (`--permission-prompt-tool`, app-server de Codex).
- Panel de consumo (`IUsageLedger`) y proyecciones `tasks*` y `agent_runs`: Fase 4. Se podrán reconstruir desde el registro.
- Chat con el orquestador.
- Aislamiento fuerte y política de rutas con resolución de enlaces: Fase 7.
- Checkout parcial (sparse) para repos grandes.
- Gemini: aparcado.

---

## 2. Componentes y responsabilidades

Todos respetan ADR-0002: los módulos solo conocen `Application`; `Desktop` compone.

| Componente | Proyecto | Responsabilidad | Reutiliza |
|---|---|---|---|
| Semántica de condiciones, `PlanDefinition`, `PlanValidator`, `ConditionEvaluator`, `RunStateReplayer`, `WriteScope` | **Domain** (`Work/`, `Orchestration/` nuevo) | Reglas puras: plan, ramas, unión, saltos, reproducción del estado | `TaskGraph`, `WorkTask`, `WorkTaskTransitions`, `FrozenRequirementSet`, `ContentHash` |
| Puertos y registros nuevos; `DecisionRecorder`; `SkillProvisioner`; payloads versionados | **Application** | Contratos y servicios sin E/S propia | `IDecisionLogStore`, `ISkillMaterializer`, `SkillActivationResolver` |
| Motor y planificador (`Engine/`, `Planning/`) | **Orchestration** | Bucle, despacho, reintentos, abandono, recuperación y prompts. El agente orquestador solo planifica | `IProjectAgentProviders.For(root)`, `IAgentRun`, `TaskGraph.Analyze` |
| Runner de verificación (`Verification/`) | **Orchestration** (ya ubicado allí en docs/02) | Lanza build y tests con `IStreamingProcessRunner`, parsea TRX/JUnit y captura evidencias | `IStreamingProcessRunner`, `IEvidenceStore`, `TestRunSummary` |
| Integrador, entrega y limpieza (`Integration/`) | **Orchestration** | Orden de merge, conflictos, verificación de la integración y entrega aprobada | `IWorktreeManager`, `IApprovalGate` |
| Gestor de worktrees (git.exe) | **Infrastructure** (`Git/`) | Worktrees separados y bloqueados, commits del sistema, `merge-tree`/`update-ref` y guarda de propiedad | `IProcessRunner`, que mata el árbol y usa el Job Object |
| Almacenes SQLite, apertura del proyecto, catálogo y activaciones de skills | **Infrastructure** (`Persistence/`, `Skills/`) | Persistencia append-only y localización de skills vendored | `SqliteSchema`, `SkillsIndexCatalogImporter` |
| Juez, constructor del dossier y revisión independiente | **Debate** (`Judging/`, un trozo de la Fase 5 adelantado) | Dossier desde almacenes, juez de solo lectura, validación y una corrección | `IJudge`, `IJudgeDossierBuilder`, `VerdictValidator` |
| Puerta de aprobación | **Security** (`Approvals/`) | Clasifica, pregunta, registra y falla cerrado | `IApprovalGate`, `ApprovalRequest`, `RiskLevel` |
| Lienzo Nodify, monitor, aprobaciones y revisión del plan | **Desktop** | ViewModels y vistas; implementa `IApprovalPrompt`; raíz de composición | `DocumentViewModel`, patrón de `TerminalViewModel` (inyecta `SynchronizationContext`) |

**Notas de colocación.**

- `Orchestration` y `Debate` no pueden usar `RunTempFolder` ni `SkillMaterializer`, que están en `Providers`. Por eso las rutas de trabajo llegan desde Desktop en `OrchestrationPaths(WorktreeRoot, DossierRoot, SkillSessionRoot)`, todas bajo `%LOCALAPPDATA%\AgentStudio`. El materializador se alcanza por el puerto `ISkillMaterializer`; hoy no está registrado en DI y hay que registrarlo.
- El motor llama a `IIndependentReview` solo con identificadores. Construir el dossier es responsabilidad exclusiva de `Debate`. Esto mantiene la regla «el orquestador no puede filtrar lo que ve el juez» impuesta por el compilador.

---

## 3. Contratos

### 3.1 Dominio (cambios y añadidos)

```csharp
// Work/WorkTaskStatus.cs
public enum WorkTaskStatus { Pending, Running, Blocked, AwaitingApproval, Completed, Failed, Cancelled, Skipped }
// New transitions: Pending→Skipped and Blocked→Skipped. IsTerminal = Completed | Cancelled | Skipped.
public enum JoinPolicy { AllCompleted, AnyCompleted }

// Work/WorkTask.cs: new optional constructor parameter `JoinPolicy join = AllCompleted`.
// TransitionTo(Skipped) is rejected ("task.skip-via-graph"): only TaskGraph skips.

// Work/TaskGraph.cs
public void AddDependency(TaskId dependent, TaskId prerequisite, string? branch = null); // branch is required only when the prerequisite is a Condition
public void CompleteCondition(TaskId condition, string takenBranch, DateTimeOffset at, string reason); // Running→Completed and stores the branch
public string? TakenBranch(TaskId condition);
public IReadOnlyList<TaskId> PropagateSkips(DateTimeOffset at); // topological pass to a fixpoint
// Edges: satisfied = prerequisite Completed and (no branch or branch == taken);
//        dead      = prerequisite Skipped, or branch != taken;
//        broken    = Cancelled, or Failed with retries exhausted.
// ReadyTasks: AllCompleted ⇒ every edge satisfied; AnyCompleted ⇒ none pending or broken and ≥1 satisfied.
// Skip:       AllCompleted ⇒ any edge dead; AnyCompleted ⇒ every edge dead.
// A graph without conditions behaves exactly as today.

// Decisions/DecisionLog.cs: DecisionKind gains RunStarted, RunFinished, AgentRunStarted, AgentRunFinished,
// ConditionEvaluated, WorktreeCreated, WorktreeRemoved. The hash uses kind.ToString(), so existing hashes do not change.

// Orchestration/ (new)
public readonly record struct RunId(string Value);   // "20261015-a1b2c3"
public enum PlanSource { OrchestratorAgent, User }
public enum WorkspaceMode { None, ReadOnly, Write }
public sealed record DependencySpec(TaskId Prerequisite, string? Branch = null);
public abstract record ConditionPredicate;            // closed set, evaluated by the system
public sealed record TaskSucceeded(TaskId Task) : ConditionPredicate;
public sealed record TestsPassed(TaskId Task) : ConditionPredicate;
public sealed record OutputEquals(TaskId Task, string Key, string Value) : ConditionPredicate;
public sealed record PathsChanged(TaskId Task, string PathPrefix) : ConditionPredicate;
public sealed record TaskSpec(TaskId Id, string Title, WorkTaskKind Kind, AgentId? Agent, string Objective,
    string Instructions, IReadOnlyList<DependencySpec> DependsOn, JoinPolicy Join, IReadOnlyList<RequirementId> Covers,
    IReadOnlyList<string> WriteScope /* relative prefixes ('src/Calc/') or exact files */,
    IReadOnlyList<string> VerificationSteps, ConditionPredicate? Condition, string? ToolId, int MaxRetries,
    TimeSpan? Timeout, WorkspaceMode Workspace);
public sealed class PlanDefinition   // ContentHash: canonical and order-independent, like FrozenRequirementSet
{
    public static PlanDefinition Create(string summary, IReadOnlyList<TaskSpec> tasks, IReadOnlyList<string> risks, PlanSource source, AgentId? author);
    public string ContentHash { get; }
    public TaskGraph BuildGraph(DateTimeOffset at);   // throws DependencyCycleException with its path
}
public sealed record PlanLimits(int MaxTasks = 20, int MaxRetriesPerTask = 3, bool DebateNodesAvailable = false);
public sealed record PlanContext(FrozenRequirementSet Requirements, IReadOnlyList<AgentDefinition> Roster,
    IReadOnlySet<string> VerificationSteps, IReadOnlySet<string> ToolIds, PlanLimits Limits);
public sealed record PlanViolation(PlanViolationCode Code, TaskId? Task, string Message);
public static class PlanValidator { public static IReadOnlyList<PlanViolation> Validate(PlanDefinition plan, PlanContext context); }
public sealed record TaskFacts(TaskId Task, WorkTaskStatus Status, TestRunSummary? LatestTests,
    IReadOnlyDictionary<string, string> Outputs, IReadOnlyList<string> ChangedPaths);
public static class ConditionEvaluator { public static bool Evaluate(ConditionPredicate p, IReadOnlyDictionary<TaskId, TaskFacts> facts); }
public abstract record RunEvent(DateTimeOffset At);
public sealed record TaskTransitioned(DateTimeOffset At, TaskId Task, WorkTaskStatus From, WorkTaskStatus To, string Reason, IReadOnlyList<EvidenceId> Evidence) : RunEvent(At);
public sealed record ConditionResolved(DateTimeOffset At, TaskId Task, string Branch) : RunEvent(At);
public static class RunStateReplayer { public static RunReplay Replay(PlanDefinition plan, DateTimeOffset createdAt, IEnumerable<RunEvent> events); }
public sealed record RunReplay(TaskGraph Graph, IReadOnlyList<string> Problems);
```

**Reglas de `PlanValidator`.** Cada una tiene un código estable, como en `VerdictValidator`.

- Identificadores: únicos, con el formato `^[A-Za-z][A-Za-z0-9_-]{0,31}$` (seguros para refs y rutas).
- Grafo: dependencias conocidas y sin ciclos.
- Agentes: deben estar en la plantilla. El juez o auditor solo puede tener tareas `Review`; el orquestador, ninguna.
- Condiciones: un `Condition` lleva predicado y ramas; no hay ramas en aristas que no salgan de un `Condition`; un predicado solo puede citar a ancestros.
- Fan-in: **dos o más ancestros de código maximales solo se unen a través de un nodo `Integration`**.
- Alcances: los `WriteScope` de tareas que pueden correr a la vez no pueden solaparse (comparación por prefijo, conservadora). Se rechazan `..`, rutas absolutas, `.git/` y `.agentstudio/`.
- Requisitos: todo requisito obligatorio debe estar cubierto, y solo se citan requisitos existentes.
- Referencias: solo se citan pasos de verificación y herramientas del perfil.
- Límites: `Review` y `HumanApproval` tienen `MaxRetries = 0`; se respetan los límites de tareas y de reintentos.
- `Debate` se rechaza en la Fase 3.

### 3.2 Proyecto y registro de decisiones (Application)

```csharp
public sealed record ProjectContext(string ProjectId, string RootPath);
public interface IProjectWorkspace { Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken ct); }
// Creates .agentstudio/ and the DB, applies migrations, ensures the `projects` row (GUID) and adds
// .agentstudio/*.db*, evidence/ and tmp/ to .git/info/exclude (never to the user's .gitignore).

public sealed record DecisionLogState(IReadOnlyList<DecisionEntry> Entries, DecisionLogVerification Verification) { public string HeadHash { get; } }
public interface IDecisionRecorder
{
    Task<DecisionLogState> LoadAsync(string projectId, CancellationToken ct);       // DecisionLog.Load(...).Verify()
    Task<DecisionEntry> RecordAsync(string projectId, Actor actor, DecisionKind kind, string summary,
        DecisionPayload payload, long? corrects = null, CancellationToken ct = default);
}
public abstract record DecisionPayload { public int V { get; init; } = 1; public required string RunId { get; init; } }
// Examples: TaskStatusChangedPayload(TaskId, From, To, Reason, BlockReason?, Evidence[], Attempt),
// AgentRunStartedPayload(TaskId, Attempt, AgentId, Provider, Vendor, Model, WorkingDirectory, PromptEvidenceId, PromptSha256, Skills[]),
// AgentRunFinishedPayload(Status, ExitCode, SessionId, Error, Usage /* MetricValue with provenance */, ReportEvidenceId),
// InstructionsIssued, WorktreeCreated, IntegrationPerformed, ConditionEvaluated, ReviewRequested, VerdictIssued, RunStarted, RunFinished.
```

**`DecisionRecorder`** (implementación en Application, como `JournalingAgentProvider`):

- Mantiene **un solo escritor por proyecto**: un `DecisionLog` en memoria cargado desde el almacén y un `SemaphoreSlim`.
- Usa `at = max(now, last.RecordedAt)`, porque `Append` rechaza una cronología inversa.
- La secuencia la asigna el objeto de dominio, no la base de datos.
- Si `store.AppendAsync` falla, recarga el log desde el almacén.
- Los payloads se serializan con opciones fijas (camelCase, sin sangría). El texto forma parte del hash, de ahí el campo `V`.
- **No guarda prompts en el payload**: guarda el `EvidenceId` y el SHA-256 de la evidencia del prompt.

### 3.3 Orquestador

`IOrchestrator.ExecuteAsync(projectId, TaskGraph, IProgress<OrchestrationProgress>?, ct) → GraphHealthReport` **se sustituye**. Nunca se implementó, y un `TaskGraph` sin especificaciones ni `RunId` no permite reanudar. También desaparece `OrchestrationProgress`. Hay que actualizar docs/04.

```csharp
public interface IOrchestrator
{
    Task<PlanProposal> PlanAsync(PlanningRequest request, IProgress<AgentEvent>? progress, CancellationToken ct); // allocates RunId; persists the plan and PlanCreated
    Task<IOrchestrationRun> StartAsync(RunRequest request, CancellationToken ct);
    Task<IOrchestrationRun> ResumeAsync(ProjectContext project, RunId run, CancellationToken ct);
    Task<IReadOnlyList<RunSummary>> ListRunsAsync(ProjectContext project, CancellationToken ct); // replay; marks Interrumpida
}
public sealed record PlanningRequest(ProjectContext Project, string OriginalRequest, FrozenRequirementSet Requirements,
    IReadOnlyList<AgentDefinition> Roster, AgentId Orchestrator, VerificationProfile Verification, PlanLimits Limits);
public sealed record PlanProposal(RunId Run, PlanDefinition? Plan, IReadOnlyList<PlanViolation> Violations, bool Corrected,
    AgentRunStatus Status, string? Error, EvidenceId? RawPlanEvidence);
public sealed record RunRequest(ProjectContext Project, RunId? Run, string OriginalRequest, FrozenRequirementSet Requirements,
    PlanDefinition Plan, ApprovalDecision PlanApproval, VerificationProfile Verification, RunOptions Options, RunId? Follows = null);
public sealed record RunOptions(int MaxParallelTasks = 3, int MaxAgentRuns = 12, TimeSpan? StallThreshold = null /* 10 min */,
    TimeSpan? HeartbeatInterval = null /* 5 s */, TimeSpan? DefaultTaskTimeout = null, TimeSpan? MaxAutoWaitForQuota = null /* 15 min */);
public interface IOrchestrationRun : IAsyncDisposable       // same shape as IAgentRun
{
    RunId Id { get; }
    IAsyncEnumerable<OrchestrationEvent> Events { get; }      // single consumer, buffered
    Task<RunResult> Completion { get; }
    RunSnapshot Snapshot();
    Task PauseAsync(); Task ResumeDispatchAsync(); Task RetryBlockedAsync(TaskId task); Task CancelAsync(string reason);
}
public enum RunPhase { Running, Paused, WaitingForQuota, AwaitingHuman, Interrupted, Succeeded, Failed, Blocked, Cancelled }
public abstract record OrchestrationEvent(DateTimeOffset At);
// TaskStatusChangedEvent, TaskQueuedEvent ("en cola del proveedor"), AgentActivityEvent, EvidenceCapturedEvent,
// GraphHealthEvent(GraphHealthReport), ApprovalPendingEvent, RunPhaseChangedEvent, RunFinishedEvent(RunResult)
public sealed record TaskView(TaskId Id, string Title, WorkTaskKind Kind, WorkTaskStatus Status, AgentId? Agent, int Attempt,
    int RetryCount, string? WorktreePath, string? Ref, string? BlockReason, IReadOnlyList<EvidenceId> Evidence);
public sealed record RunSnapshot(RunId Id, RunPhase Phase, IReadOnlyList<TaskView> Tasks, GraphHealthReport Health, int AgentRunsUsed);
public sealed record RunResult(RunId Id, RunPhase Phase, GraphHealthReport Health, string? IntegrationCommit, string? VerdictId, string Summary);
```

### 3.4 Worktrees, integración y verificación (Application)

```csharp
public sealed record RepositoryState(string Root, string HeadCommit, string? HeadBranch, bool HasUncommittedChanges, Version GitVersion);
public sealed record WorktreeRequest(ProjectContext Project, RunId Run, string Name /* T1 | _base | _integration */, string BaseCommit);
public sealed record WorktreeLease(string Path, string Name, string RefName, string BaseCommit, RunId Run);
public sealed record MergeAttempt(bool Merged, bool AlreadyMerged, string? ResultCommit, IReadOnlyList<string> ConflictPaths, string Messages);
public interface IWorktreeManager
{
    Task<RepositoryState> InspectAsync(string repoRoot, CancellationToken ct);
    Task<WorktreeLease> EnsureAsync(WorktreeRequest request, CancellationToken ct);                  // idempotent
    Task<CommitResult> CommitAllAsync(WorktreeLease lease, string message, CancellationToken ct);     // message on stdin
    Task<IReadOnlyList<string>> ChangedPathsAsync(string repoRoot, string from, string to, CancellationToken ct);
    Task WriteDiffAsync(string repoRoot, string from, string to, Stream destination, CancellationToken ct);
    Task<MergeAttempt> MergeIntoAsync(string repoRoot, string targetRef, string expectedTarget, string source, string message, CancellationToken ct);
    Task CheckoutAsync(WorktreeLease lease, string commit, CancellationToken ct);                     // owned worktrees only
    Task<RemovalOutcome> RemoveAsync(WorktreeLease lease, bool discardChanges, CancellationToken ct);  // dirty ⇒ refused unless discardChanges
    Task<IReadOnlyList<WorktreeLease>> ListOwnedAsync(ProjectContext project, CancellationToken ct);  // from `git worktree list --porcelain` + lock reason
}
public interface IIntegrator { Task<IntegrationResult> IntegrateAsync(IntegrationRequest request, IProgress<ProcessOutputLine>? output, CancellationToken ct); }
public interface IDeliveryService { Task<DeliveryResult> DeliverAsync(ProjectContext project, RunId run, DeliveryMode mode, CancellationToken ct); } // asks IApprovalGate
public interface IWorkspaceCleanup { Task<CleanupReport> CleanAsync(ProjectContext project, RunId run, CancellationToken ct); }          // asks IApprovalGate when destructive

public enum TestReportFormat { None, Trx, JUnit }
public sealed record VerificationStep(string Id, string Title, string Executable, IReadOnlyList<string> Arguments,
    EvidenceKind Kind, TestReportFormat Report, string? ReportPath, TimeSpan Timeout);
public sealed record VerificationProfile(string Name, IReadOnlyList<VerificationStep> Steps) { public string ContentHash { get; } }
public sealed record EvidenceScope(string ProjectId, RunId? Run, TaskId? Task, int? Attempt);
public sealed record VerificationResult(string StepId, EvidenceRecord Evidence, bool Succeeded, bool TimedOut, int ExitCode);
public interface IVerificationRunner
{
    Task<IReadOnlyList<VerificationResult>> RunAsync(string workingDirectory, IReadOnlyList<VerificationStep> steps,
        EvidenceScope scope, IProgress<ProcessOutputLine>? output, CancellationToken ct);
}
```

### 3.5 Juez y revisión (Application)

```csharp
public interface IJudge                                   // existing; gains the correction round
{
    JudgeIdentity Identity { get; }
    Task<Verdict> EvaluateAsync(JudgeDossier dossier, CancellationToken ct);
    Task<Verdict> ReviseAsync(JudgeDossier dossier, Verdict previous, IReadOnlyList<VerdictViolation> violations, CancellationToken ct);
}
public sealed record DossierRequest(string ProjectId, AuditScope Scope, string? RunId = null);
public interface IJudgeDossierBuilder { Task<JudgeDossier> BuildAsync(DossierRequest request, CancellationToken ct); } // gains the run id
public enum ReviewDisposition { Approved, ChangesRequested, Rejected, NeedsHuman }
public sealed record ReviewRequest(ProjectContext Project, RunId Run, TaskId ReviewTask, AgentId Judge, string WorkspacePath);
public sealed record ReviewOutcome(string VerdictId, Verdict Verdict, IReadOnlyList<VerdictViolation> Violations,
    bool Corrected, bool DecisionLogIntact, ReviewDisposition Disposition);
public interface IIndependentReview { Task<ReviewOutcome> ReviewRunAsync(ReviewRequest request, IProgress<AgentEvent>? progress, CancellationToken ct); }
```

### 3.6 Aprobaciones, skills y almacenes (Application)

```csharp
// Security: ApprovalRequest gains (string? ProjectId = null, string? RunId = null, string? TaskId = null).
// IApprovalGate does not change.
public interface IApprovalPrompt { Task<ApprovalDecision> AskAsync(ApprovalRequest request, CancellationToken ct); } // Desktop

// Skills
public sealed record CatalogSkill(SkillEntry Entry, string? LocalPath);   // null for Linked skills: not materializable
public interface ISkillCatalog { Task<IReadOnlyList<CatalogSkill>> ListAsync(CancellationToken ct); }
public interface ISkillActivationStore
{
    Task<IReadOnlyList<SkillActivation>> ListAsync(string projectId, CancellationToken ct);
    Task AppendAsync(string projectId, SkillActivation a, string decidedBy, CancellationToken ct);
}
public sealed record ProvisionedSkills(ProviderId Cli, string? Directory, IReadOnlyList<SkillId> Skills, IReadOnlyList<string> Warnings);
public interface ISkillProvisioner   // implementation SkillProvisioner(catalog, activations, ISkillMaterializer, SkillProvisioningOptions)
{
    Task<ProvisionedSkills> ProvisionAsync(string projectId, AgentDefinition agent, string runKey /* [A-Za-z0-9_-]+ */, string workingDirectory, CancellationToken ct);
    void Release(ProvisionedSkills skills);   // ISkillMaterializer.Cleanup
}

// Stores (StoreContracts.cs)
public interface IPlanStore
{
    Task<string> SaveAsync(string projectId, PlanDefinition plan, CancellationToken ct);
    Task<PlanDefinition?> FindAsync(string projectId, string hash, CancellationToken ct);
}
public sealed record RunRecord(RunId Id, string ProjectId, DateTimeOffset CreatedAt, string PlanHash, string RequirementSetHash,
    EvidenceId OriginalRequest, string BaseCommit, string? BaseRef, VerificationProfile Verification,
    IReadOnlyList<AgentDefinition> Roster, RunOptions Options, RunId? Follows);
public interface IRunRegistry
{
    Task CreateAsync(RunRecord run, CancellationToken ct);
    Task<RunRecord?> FindAsync(string projectId, RunId id, CancellationToken ct);
    Task<IReadOnlyList<RunRecord>> ListAsync(string projectId, CancellationToken ct);
}
public sealed record StoredVerdict(string Id, Verdict Verdict, string? RunId, string? TaskId, AuditScope Scope, bool Valid,
    IReadOnlyList<VerdictViolation> Issues, string? Supersedes);
public interface IVerdictStore
{
    Task<string> AppendAsync(string projectId, StoredVerdict v, CancellationToken ct);
    Task<IReadOnlyList<StoredVerdict>> ListAsync(string projectId, string? runId, CancellationToken ct);
}
public interface IAgentRoster
{
    Task<IReadOnlyList<AgentDefinition>> ListAsync(string projectId, CancellationToken ct);
    Task SaveAsync(string projectId, AgentDefinition a, CancellationToken ct);
}
// EvidenceDraft gains (string? RunId = null, string? TaskId = null, int? Attempt = null, string? MediaType = null).
// IEvidenceStore gains ListByRunAsync(projectId, runId, ct).
```

### 3.7 Persistencia del estado: ¿se puede reanudar una ejecución tras reiniciar?

**Sí.** La reanudación funciona por reproducción del registro verificado, y la decide el usuario.

1. **Fuente de verdad.**
   - El `decision_log` contiene `RunStarted`, `TaskStatusChanged` (con evidencias y `BlockReason`), `ConditionEvaluated`, `AgentRunStarted/Finished` y `RunFinished`.
   - El plan y la cabecera inmutable de la ejecución están en `plans` y `orchestration_runs`.
   - **No se usa `task_status_history`**: no tiene triggers y obligaría a escribir dos veces.
2. **Al abrir el proyecto**, `IDecisionRecorder.LoadAsync` ejecuta `Verify()`. Si la cadena está rota, se bloquean la reanudación y las ejecuciones nuevas, y se muestra una alerta de integridad.
3. **`RunStateReplayer` reconstruye el `TaskGraph`** a partir del plan (comprobando su hash), pasando por la API pública:
   - `→Running` con `TransitionTo`;
   - `→Completed` con `Complete(evidencias del payload)` o `CompleteCondition`;
   - `Failed→Pending` con `Retry()`;
   - `→Skipped` recalculado con `PropagateSkips` y **comparado** con lo registrado.

   Cualquier `DomainRuleViolationException` deja la ejecución como no reanudable y registra un `AuditAlertRaised`.
4. **Tareas que estaban en `Running`.** Sus procesos murieron con el Job Object. La recuperación añade `Running→Blocked` con `BlockReason = Interrupted` y un `AgentRunFinished(Interrupted)` para cada intento abierto.
   - No consume reintentos: el reinicio no es culpa del agente.
   - Fuerza una reanudación explícita, que gasta cuota.
   - Las razones de bloqueo están estructuradas: `Interrupted`, `ProviderUnavailable`, `RateLimitedUntil(t)` y `AwaitingHuman`.
   - Como `Analyze` cuenta `Blocked` como ausencia de progreso, una ejecución interrumpida tendrá `IsDeadlocked=true`. La fase mostrada debe ser **«Interrumpida»**, **sin** alerta de bloqueo.
5. **Al reanudar** (`ResumeAsync`):
   - el diff parcial del intento interrumpido se captura como evidencia `CodeDiff`;
   - el worktree de la tarea vuelve a la base del intento con `CheckoutAsync` (solo worktrees propios);
   - `Blocked→Pending`;
   - el bucle continúa. Lo que ya estaba `Completed` no se repite.

---

## 4. Algoritmo del motor

### 4.1 Bucle (actor de un solo hilo, reloj `TimeProvider`)

```text
loop:
  apply signals: worker finished, started, approval answered, cancel, pause
  apply activity: ConcurrentDictionary<TaskId, last> → WorkTask.RecordActivity
  graph.PropagateSkips(now)                     → TaskStatusChanged(→Skipped) for each task
  health = graph.Analyze(now, stall)            → cancel the attempt of each stalled task; GraphHealthEvent;
                                                  AuditAlertRaised only for newly appeared problems
  if not paused: dispatch ReadyTasks() ordered by (topological index, TaskId),
                 within MaxParallelTasks, free provider slots, provider cooldowns and MaxAgentRuns
  if nothing is running and nothing can be dispatched:
       classify:  every task Completed/Skipped                  → Succeeded
                  Failed with retries exhausted or unreachable  → Failed
                  Blocked by Interrupted or human               → Interrupted / AwaitingHuman
                  only RateLimitedUntil ≤ MaxAutoWaitForQuota   → WaitingForQuota (sleep until t)
                  cooldown above that limit                     → Paused
       if terminal: RunFinished; exit
  await signal | tick(HeartbeatInterval)
```

Solo el bucle muta el grafo y registra transiciones. Los trabajadores registran hechos observacionales: `InstructionsIssued`, `AgentRunStarted/Finished`, `EvidenceRegistered` y `WorktreeCreated`. `DecisionRecorder` serializa las escrituras. La reproducción solo usa transiciones y `ConditionEvaluated`.

### 4.2 Despacho por tipo de nodo

| Tipo | Transiciones | Qué hace |
|---|---|---|
| `AgentWork` | `Pending` → (devuelve `StartAsync`) → `Running` → `Completed`/`Failed`/`Blocked` | 1. Worktree de la tarea. 2. Skills. 3. Prompt capturado como evidencia, e `InstructionsIssued`. 4. `AgentRunStarted`. 5. `StartAsync`. 6. Eventos, que actualizan la actividad. 7. Informe como evidencia. 8. Commit del sistema, diff y comprobación del alcance. 9. Verificación. 10. Señal al bucle |
| `ToolRun` | `Pending→Running→Completed`/`Failed` | Lanza un paso del perfil por `ToolId`. Nunca un comando libre del plan |
| `Integration` | Igual | `IIntegrator`: merges deterministas y verificación en `_integration` |
| `Condition` | `Pending→Running→CompleteCondition` | Se evalúa dentro del bucle con `ConditionEvaluator` sobre `TaskFacts`, registra `ConditionEvaluated` y luego `PropagateSkips` |
| `HumanApproval` | `Pending→AwaitingApproval→Completed`/`Failed` | `IApprovalGate`. Si se deniega, `Failed` con `MaxRetries=0`, y lo que depende queda inalcanzable |
| `Review` | `Running`→`Completed` / `Failed` / `AwaitingApproval` | `IIndependentReview`. `Approved` lleva a `Completed`; `ChangesRequested`/`Rejected`, a `Failed`; `NeedsHuman`, a `AwaitingApproval` |
| `Debate` | — | Rechazado por el validador hasta la Fase 5 |

### 4.3 Concurrencia por proveedor

- **No se reimplementa.** El motor busca el proveedor en `IProjectAgentProviders.For(root)` por `agent.Model.Provider`. Esos proveedores ya están compuestos como `Concurrency(Journaling(adapter))`.
- Para no bloquear la cola por el primero de la fila, el bucle lleva su propio recuento por proveedor. El límite es `(p as ConcurrencyLimitedAgentProvider)?.Limit ?? p.Capabilities.MaxConcurrentRuns`. Despacha antes una tarea de Claude que esperar a Codex.
- Si otros consumidores (el planificador o el juez) ocupan permisos, `StartAsync` espera. La tarea sigue en `Pending` y la UI muestra «En cola».

### 4.4 Fallos, reintentos y F3-3

| Clase de fallo | Transición | Reintento |
|---|---|---|
| Agente `Failed`, tiempo agotado (`LimitReached` o timeout de tarea), abandono, verificación fallida, cambio fuera de alcance | `Running→Failed` | `Retry()` mientras queden reintentos y presupuesto. El siguiente intento reanuda la sesión (`ResumeSessionId`) y recibe la evidencia del fallo como dato. Con verificación fallida o fuera de alcance se conserva el worktree; con abandono o timeout se vuelve a la base del intento |
| Fallo al lanzar (CLI ausente, sin sesión, bitácora no escribible) | `Pending→Blocked(ProviderUnavailable)` | El usuario reintenta |
| `RateLimited` (y `RateLimitSignaled.ResetsAt`) | `Running→Blocked(RateLimitedUntil)` y enfriamiento del proveedor | `Blocked→Pending` automático si la espera es ≤ 15 min; si no, `Paused`. No consume reintentos |
| El agente declara `status: blocked` | `Running→Blocked(AwaitingHuman)` | Decide el usuario |
| Cancelación del usuario | `→Cancelled` | No |

**Detección (F3-3).** `Analyze` produce cuatro listas, que se registran (`AuditAlertRaised`) y se muestran:

- `StalledTasks`: el latido es `RecordActivity` en cada `AgentEvent` o línea de proceso; el umbral por defecto es de 10 min.
- `UnreachableTasks`.
- `RetryExhaustedTasks`.
- `IsDeadlocked`, matizado por la clasificación del bucle (4.1).

Además hay un presupuesto de ejecución: `MaxAgentRuns`.

### 4.5 Timeouts, cancelación y limpieza del árbol de procesos

**Tres capas de timeout:**

1. Los adaptadores ya usan `Agent.Budget.MaxDuration` como `ProcessSpec.Timeout` y devuelven `LimitReached`.
2. `TaskSpec.Timeout` es un CTS del trabajador que llama a `run.CancelAsync()`.
3. El monitor de abandono, para un CLI que se cuelga en silencio.

**Verificación.** Usa `VerificationStep.Timeout`, que mata el árbol. El entorno incluye `MSBUILDDISABLENODEREUSE=1`, `DOTNET_CLI_USE_MSBUILD_SERVER=0` y `UseSharedCompilation=false`, para que no queden nodos reteniendo ficheros del worktree.

**Cancelación del usuario.**

1. Se deja de despachar.
2. `CancelAsync` en cada trabajador, con espera acotada.
3. `DisposeAsync`, que mata el árbol, en un `finally`.
4. `→Cancelled` y `RunFinished(Cancelled)`.
5. Los worktrees se conservan para inspección.

**Cierre de la app.** No se marca nada como cancelado. El Job Object mata a los hijos y la recuperación los trata como interrumpidos (§3.7).

### 4.6 Cómo la salida de una tarea se convierte en entrada de la siguiente

1. **Código, por git.**
   - La base de una tarea con escritura es la cabeza de su **frontera de código**: los ancestros maximales que producen código, atravesando nodos `Condition`, `Review`, `HumanApproval` y `ToolRun`.
   - Con 0 ancestros, la base es el commit base de la ejecución. Con 1, su ref. Con 2 o más, el plan no es válido salvo en un nodo `Integration`.
2. **Datos, por evidencia.**
   - El informe del ejecutor sigue un esquema fijo con `status`, `summary`, `filesChanged`, `claims`, `openIssues` y `outputs[{key,value}]`. Se guarda como `AgentStatement/ReportedByAgent`.
   - Las tareas que dependen de ella lo reciben **delimitado como dato**: `<upstream_output task=… evidence=… sha256=…>`. Es la mitigación R4: nunca se presenta como instrucciones.
3. **Hechos para condiciones.** `TaskFacts` reúne estado, último `TestRunSummary`, `outputs` y rutas cambiadas. Son deterministas y se citan en `ConditionEvaluated`.

### 4.7 Idempotencia

| Operación | Cómo se garantiza |
|---|---|
| Registro de decisiones | Secuencia del dominio, PK `(project_id, sequence)` y el trigger `decision_log_no_replace` |
| Evidencias | Direccionadas por contenido |
| `EnsureAsync` | Ruta y nombre deterministas; si ya existe y coincide (base, lock reason), se reutiliza; si no coincide, falla cerrado |
| Commit del sistema | Solo si hay cambios; lleva el trailer `Agent-Studio-Attempt`, y si ya existe se omite |
| `MergeIntoAsync` | Comprueba antes `merge-base --is-ancestor`, y el `update-ref` es CAS |
| Ejecuciones de agentes | **Como mucho una por intento.** `AgentRunStarted` se registra antes de lanzar, y nunca se relanza sola tras un reinicio |
| Juez y planificador | Igual: un intento y una corrección |

---

## 5. Worktrees

**Ubicación y nombres.**

- Ruta: `%LOCALAPPDATA%\AgentStudio\wt\<repo8>\<run6>\<nombre>`, con `nombre` = `T1`, `_base` o `_integration`.
- **Nunca dentro del repo.** Los globs de MSBuild de un `.csproj` en la raíz los incluirían, y además aparecerían como ficheros sin seguimiento.
- Rutas cortas y `-c core.longpaths=true` en cada comando, sin tocar la configuración del usuario.

**Creación.** `git worktree add --detach --lock --reason "agent-studio:<projectId>:<run>:<nombre>" <ruta> <base>`.

- La propiedad se demuestra con tres cosas: la ruta bajo la raíz propia, el motivo del bloqueo y el registro (`WorktreeCreated`).
- La base sale de `git rev-parse HEAD`. **Los cambios sin commit del usuario no entran**, y se le avisa al empezar.
- El `IsRepository` de Codex y el `ResolveCommonGitDir` del materializador ya aceptan un `.git` que sea fichero.

**Estrategia de ramas.**

- Refs ocultas por tarea: `refs/agentstudio/<run>/<tarea>`. No ensucian `git branch`, pero mantienen vivos los objetos.
- Rama visible de integración: `agentstudio/run-<run>`.
- Los commits del sistema los firma «Agent Studio» con `-c user.name/-c user.email` y el mensaje por stdin.
- Se excluye `.agents/` del commit (además de `info/exclude`) y se ejecutan con `GIT_TERMINAL_PROMPT=0`.
- Solo se parsea salida de tipo porcelain, que no depende del idioma.

**Integración de tareas paralelas.**

- Orden determinista: índice topológico y luego `TaskId`. Es reproducible para tests y para la reproducción del estado.
- Por cada fuente: `git merge-tree --write-tree --name-only --messages <integración> <fuente>`. Si sale limpio, `commit-tree -p integración -p fuente` (equivale a `--no-ff`) y `update-ref` CAS.
- Después, `_integration` se pone en el resultado (reset y clean, solo en el worktree propio) y se ejecutan build y tests.
- Requiere git ≥ 2.38; esta máquina tiene 2.45.2 y Auto Doctor lo comprobará.

**Conflictos.**

- Se previenen con el validador: los `WriteScope` de tareas concurrentes no se solapan, y el alcance se comprueba sobre el diff real.
- Si aun así ocurre, `merge-tree` devuelve las rutas en conflicto sin tocar ningún árbol. La tarea `Integration` acaba en `Failed` (`MaxRetries=0`) con una evidencia `ConflictReport`, y lo que depende de ella queda inalcanzable.
- **Lo resuelve una persona:** o a mano en `_integration`, o lanzando una **ejecución de seguimiento** con base en la cabeza de la integración parcial, a la que el orquestador recibe el informe de conflicto como dato.
- Nunca se resuelve solo.

**Build y tests como evidencia.**

- `IVerificationRunner` ejecuta los pasos del **perfil de verificación de confianza**. El perfil está en `<root>\.agentstudio\verification.json`, excluido de git y editado desde la UI. Se congela en `orchestration_runs` al empezar y nunca se lee desde un worktree.
- Los resultados se leen de **TRX o JUnit, nunca de la consola de MTP** (está traducida: «correcto: N»).
- `TestRunSummary.Succeeded` exige `Passed > 0`.

**Seguridad.** Lo único que el sistema toca del repo del usuario es:

- la base de objetos;
- `refs/agentstudio/*` y `agentstudio/run-*`;
- la administración de worktrees;
- `info/exclude`.

Nada de HEAD, índice, ramas ni árbol de trabajo del usuario. Hay dos salvedades conocidas: la bitácora (ADR-0008) y `.agentstudio/`.

Pasan por `IApprovalGate` como acciones destructivas o críticas:

- fusionar en la rama del usuario (entrega, preferiblemente `--ff-only`; git aborta por sí solo si un fichero modificado coincide);
- `worktree remove` con cambios sin commit;
- borrar refs o ramas con trabajo no integrado.

**Solo se borran refs y ramas con el prefijo `agentstudio`.** Nunca hay `push` ni operaciones de red. Cualquier comando que modifique sobre una ruta que no sea un worktree propio se rechaza.

---

## 6. Flujo F3-8 paso a paso

**Escenario.** Claude orquesta y ejecuta, y Codex juzga en solo lectura, sobre un **repo temporal de prueba**.

> **Ajuste al diseño original:** en los pasos siguientes, donde se lee «ejecutor Codex» léase «ejecutor Claude
> (`dontAsk` con reglas de ruta para su worktree, sin red; [ADR-0018](adr/0018-modelo-de-amenaza-fase-3.md))», y donde se
> lee «juez Claude», «juez Codex (`codex exec -s read-only`, `--output-schema`)».
> Por tanto, `ExcludedVendors` es `{anthropic}`. El prompt va siempre por stdin. Lo de Codex en un worktree (S2) se
> verifica igualmente, porque la plantilla es configurable.

El repo de prueba contiene:

- una librería .NET mínima y un proyecto xUnit v3 con las versiones de este repo, para que se restaure desde la caché local;
- al menos **un test que ya pasa**, porque `Succeeded` exige `Passed > 0`;
- RQ-01, obligatorio: «Añadir `Calc.Multiply(a,b)`», con el criterio c1 por `AutomatedTest`.

**Plantilla de agentes.**

- `orquestador`: Claude, `Orchestrator`, solo lectura con `Read`, `Grep` y `Glob`.
- `ejecutor`: Codex, `Executor`, `workspace-write` y sin red.
- `juez`: Claude, `Judge`, solo lectura.

| # | Paso | Decide | Registro (`DecisionKind` · actor) | Otros almacenes |
|---|---|---|---|---|
| 0 | Precondiciones: `claude.exe` y `codex.exe` nativos con sesión, git ≥ 2.38, perfil y tu OK de cuota | Usuario | — | ajustes |
| 1 | Petición literal y requisitos; congelar | Usuario | (se ancla en `RunStarted`) | `requirement_sets` (hash); evidencia de la petición (`Document/ProvidedByHuman`) |
| 2 | Worktree `_base` en HEAD | Sistema | `WorktreeCreated` · System | lock reason en git |
| 3 | Planificación: Claude lee el repo y devuelve JSON contra el esquema del plan. Enums dinámicos: agentes `["ejecutor","juez"]`, requisitos `["RQ-01"]`, pasos `["build","test"]`. Esquema de ≤ 16 KB por `--json-schema` | **Orquestador** | `AgentRunStarted/Finished` · System; `PlanCreated` · Orchestrator(`orquestador`) | bitácora P-NNNN; prompt (`Document/CapturedBySystem`) y plan crudo (`AgentStatement/ReportedByAgent`); `plans` |
| 4 | Parseo estricto (`UnmappedMemberHandling.Disallow`) y `PlanValidator`; como máximo 1 corrección con `--resume`; si persiste, se muestra al usuario | Sistema | `PlanRevised` si hubo corrección | `plans` |
| 5 | Revisión del plan en el lienzo y aprobación | Usuario | `ApprovalRequested` · System; `ApprovalGranted` · User | — |
| 6 | Inicio. Plan esperado: T1 `AgentWork(ejecutor, Write, covers RQ-01, scope ["src/Calc/","tests/Calc.Tests/"], [build,test])` → T2 `Integration` → T3 `Review(juez)` | Sistema | `RunStarted` · System (hashes de plan y requisitos, `baseCommit`, perfil, plantilla, **política del juez: excluye `openai`; `anthropic` compartido con el orquestador según ADR-0014**); `TaskCreated`×3; `TaskAssigned` · Orchestrator | `orchestration_runs` |
| 7 | T1: worktree, skills del ejecutor (`.agents/skills`) y prompt (reglas, objetivo, **requisitos literales del conjunto congelado**, alcance, esquema del informe) por stdin; `codex exec --json -s workspace-write -C <wt> --output-schema … -` | Sistema prepara; **ejecutor** implementa | `WorktreeCreated`; `InstructionsIssued` (id y sha del prompt, `covers`, alcance, skills); `AgentRunStarted`; `TaskStatusChanged(Pending→Running)`; `AgentRunFinished` (tokens exactos) | bitácora; evidencia del informe |
| 8 | Commit del sistema, rutas cambiadas ⊆ alcance, diff, build y tests en T1 | Sistema | `EvidenceRegistered` (`CodeDiff`, `BuildLog`, `TestRun`); `TaskStatusChanged(→Completed, evidencias)`. Si falla: `→Failed` y `Retry()` con el fallo como dato | `evidence/<sha256>` |
| 9 | T2: merge determinista (`merge-tree`, `commit-tree`, CAS) a `agentstudio/run-<id>`; `_integration`; build y tests | Sistema | `IntegrationPerformed`; `EvidenceRegistered`×2; `TaskStatusChanged` | ref de la rama |
| 10 | T3: `IIndependentReview` registra **antes** `ReviewRequested` y verifica el registro. Construye el dossier **desde los almacenes**: petición literal, requisitos, plan, entradas de la ejecución, cabeza, evidencias de la ejecución, tareas reproducidas y `ExcludedVendors={openai}` calculado del registro. El paquete de ficheros va a `DossierRoot`. Claude se lanza con `dontAsk`, Write, Edit y Bash denegados, `--add-dir <dossier>`, `--json-schema` del veredicto y enum de requisitos | Sistema construye; **juez** evalúa | `AgentRunStarted/Finished`; `VerdictIssued` · Judge(`juez`): id, decisión, válido, violaciones, hash de requisitos, cabeza del registro, nota de vendor compartido | `verdicts`; bitácora |
| 11 | Mapeo del JSON a `Verdict` (los campos nulos quedan nulos para que salga `MalformedVerdict`). **El sistema** pone `Judge = JudgeIdentity(juez, ModelBinding(claude-code, anthropic, modelo de SessionStarted))` e `IssuedAt`; el juez solo repite los dos hashes. `VerdictValidator.Validate` sin cambios: con `ExcludedVendors={openai}` pasa `JudgeNotIndependent`. Con violaciones, `ReviseAsync` una vez (`--resume`) | Sistema | Segundo `VerdictIssued` con `supersedes`; un veredicto inválido se guarda **tal cual** con sus incidencias, nunca se arregla en silencio | `verdicts` |
| 12 | Cierre: `Approved`→T3 `Completed`; `ChangesRequested`→`Failed`, con ejecución de seguimiento propuesta; `NeedsHuman`→`AwaitingApproval` | Sistema / usuario | `TaskStatusChanged`; `RunFinished` · System | — |
| 13 | Entrega opcional: fusionar en tu rama | Usuario (crítica) | `ApprovalRequested/Granted`; `IntegrationPerformed(entrega)` | git |

**Coste en cuota.** Lo normal son 3 ejecuciones (planificador, ejecutor y juez); el máximo, unas 7.

**Doc 07 §6 debe corregirse.** El juez de Claude usa `dontAsk` con las herramientas de escritura denegadas, no `plan`, porque `plan` usa otro modelo y cuesta más, como se verificó en la Fase 2.

---

## 7. Modelo de datos: `0002_orchestration.sql`

**Numeración.** Doc 05 reservaba la `0002` para las tablas de skills de la Fase 4. Pasa a la **`0003`** (`skills`, `skill_health_checks`), y `skill_activations` se adelanta a la `0002` porque la necesita F3-9.

**SQL explícito con Microsoft.Data.Sqlite, sin Dapper.** Es un paquete menos. Hay que anotarlo en docs/05 y en ADR-0010.

```sql
CREATE TABLE plans (content_hash TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id),
  source TEXT NOT NULL CHECK (source IN ('OrchestratorAgent','User')), author_agent_id TEXT NULL,
  schema_version INTEGER NOT NULL, created_at TEXT NOT NULL, payload_json TEXT NOT NULL);
CREATE TABLE orchestration_runs (id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id),
  created_at TEXT NOT NULL, plan_hash TEXT NOT NULL REFERENCES plans(content_hash),
  requirement_set_hash TEXT NOT NULL REFERENCES requirement_sets(content_hash),
  original_request_evidence_id TEXT NOT NULL REFERENCES evidence(id), base_commit TEXT NOT NULL, base_ref TEXT NULL,
  verification_profile_json TEXT NOT NULL, roster_json TEXT NOT NULL, options_json TEXT NOT NULL,
  follows_run_id TEXT NULL REFERENCES orchestration_runs(id));      -- immutable header; status comes from the log
CREATE TABLE skill_activations (id INTEGER PRIMARY KEY AUTOINCREMENT, project_id TEXT NOT NULL REFERENCES projects(id),
  skill_id TEXT NOT NULL, scope TEXT NOT NULL CHECK (scope IN ('Project','Agent')), scope_id TEXT NOT NULL,
  enabled INTEGER NOT NULL CHECK (enabled IN (0,1)), decided_by TEXT NOT NULL, decided_at TEXT NOT NULL); -- history: the last one wins
ALTER TABLE evidence ADD COLUMN run_id TEXT NULL;   -- also task_id, attempt, media_type, size_bytes
CREATE INDEX evidence_by_run ON evidence(project_id, run_id);
ALTER TABLE verdicts ADD COLUMN run_id TEXT NULL;   -- also task_id, scope, valid, validation_json, supersedes_verdict_id
CREATE INDEX verdicts_by_run ON verdicts(project_id, run_id);
-- plans, orchestration_runs and skill_activations: no_update / no_delete / no_replace triggers, same pattern as 0001.
```

- Las activaciones **globales** van en `%APPDATA%\AgentStudio\skill-activations.jsonl` (JSONL append-only), porque no pertenecen a un proyecto.
- Al abrir la base: `PRAGMA journal_mode=WAL` y `busy_timeout`.
- Un fichero de bloqueo en `.agentstudio` impide que dos instancias orquesten el mismo proyecto.
- **`tasks`, `task_dependencies`, `task_status_history`, `agent_runs` y `usage_records` (de la 0001) no se usan en la Fase 3.** Docs/05 lo dirá: el estado sale del registro (ADR-0010), y en la Fase 4 serán proyecciones reconstruibles a partir de `AgentRunFinished` y `TaskStatusChanged`.
- No hace falta tabla de worktrees: la propiedad está en git, en el motivo del bloqueo, y la auditoría en el registro.

---

## 8. Estrategia de pruebas

**Proveedor guionizado (solo en tests).**

- `tests\…\Orchestration\Fakes\ScriptedAgentProvider` generaliza el `FakeProvider`/`FakeRun` de `JournalingAgentProviderTests`.
- Los guiones se indexan por (agente, tarea, intento). Puede:
  - emitir eventos y uso;
  - escribir ficheros en `WorkingDirectory`;
  - devolver JSON estructurado;
  - fallar, colgarse, simular límite de uso o respetar `CancelAsync`;
  - mantenerse retenido con TCS para intercalados deterministas;
  - medir la concurrencia máxima observada.
- Se compone con el `AgentProviderPipeline.Compose` real y una bitácora en memoria.
- El tiempo se controla con `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`, misma familia y licencia MIT; versión a verificar en nuget). El `FakeClock` actual no tiene temporizadores.

**Pruebas del motor.**

- F3-1: secuencial; paralelo con solapamiento observado; condicional con las dos ramas, propagación de saltos y unión `AnyCompleted`; dependiente.
- F3-3:
  - abandono: cancelar, fallar y reintentar;
  - denegación que deja tareas inalcanzables y la ejecución en `Failed` con informe;
  - reintentos agotados;
  - bloqueo real frente a «Interrumpida» o «En espera de cuota» (sin falsa alerta);
  - límite de uso sin consumir reintentos.
- F3-9: skills distintas por agente con precedencia; `Release` también ante fallo o cancelación; skills recogidas en `AgentRunStarted`. Con `SkillProvisioner` y `SkillActivationResolver` reales y un materializador que registra las llamadas.
- Presupuesto, timeouts y cancelación con limpieza.

**Propiedades.** Siguen la convención del repo: bucles con `Random` sembrado, sin FsCheck. Con unos 200 planes aleatorios por semilla:

1. Ninguna tarea empieza antes de que sus aristas estén satisfechas, según el orden del registro.
2. La ejecución siempre termina y cada tarea no terminal está explicada por `Analyze` o por su `BlockReason`.
3. Nunca se supera el límite por proveedor.
4. `Verify()` es válido tras cada ejecución.
5. **La reproducción del registro da la misma instantánea que la ejecución en vivo.**
6. Toda tarea `Completed` con `RequiresEvidence` tiene evidencias.
7. Un fallo provocado en la entrada k, seguido de recuperación y reanudación, no repite intentos completados.

En el dominio, además: las aristas con rama nunca crean ciclos, y `PlanValidator` rechaza el fan-in sin `Integration` y los alcances solapados.

**Integración con repo git temporal** (`LocalTools`: git y pwsh).

- `TempGitRepo` con su configuración local de identidad.
- F3-2: dos tareas paralelas en worktrees distintos con la misma base; merges en orden determinista; evidencias con hash que se vuelven a calcular desde el almacén; **el worktree principal queda intacto** (`status --porcelain`, HEAD y ramas sin cambios).
- Conflicto: rutas detectadas y nada a medio fusionar.
- Idempotencia de `EnsureAsync` y `MergeIntoAsync`.
- La guarda de propiedad rechaza el worktree principal.
- Borrado con cambios: no se hace sin aprobación.
- La verificación usa un `verify.ps1` que escribe JUnit, para no depender de NuGet. Los parsers de TRX se prueban con un TRX real de este repo.

**Persistencia.**

- F3-4: escribir con el recorder sobre una base temporal, cerrar, abrir y comprobar `Verify` válido con la misma cabeza; manipular (quitar trigger y hacer UPDATE) y que falle en la secuencia N; escrituras concurrentes con secuencias contiguas.
- F3-5: el hash lo calcula el almacén; el fichero se llama por su sha; al leer se recalcula y detecta manipulación; deduplicación; captura en streaming.

**Aprobaciones (F3-7).**

- Lo destructivo o crítico siempre pregunta.
- Denegar, agotar el tiempo, cancelar o una excepción equivalen a denegar.
- Petición y respuesta quedan en el registro y en orden.
- Sin `Approved` no se ejecuta la acción.

**ViewModels (F3-6).**

- Crear un nodo de cada tipo permitido y conectar.
- Ciclo rechazado con el mensaje del camino.
- Ramas Sí/No en `Condition`.
- Borrar en cascada.
- «Validar» muestra los mensajes de `PlanValidator`.
- «Ejecutar» queda desactivado si el plan no es válido.
- Todo con `SynchronizationContext` inyectable.

**Pruebas en vivo con cuota** (`[Trait("Category","ProviderLive")]` y `Assert.SkipUnless(AGENTSTUDIO_PROVIDER_TESTS=="1")`):

| Prueba | Ejecuciones de agente |
|---|---|
| Planificador (Claude con herramientas y `--json-schema`) | 1–2 |
| Codex con `workspace-write` y `--output-schema` **dentro de un worktree** | 1 |
| Juez (Claude y esquema de veredicto sobre un dossier preparado) | 1–2 |
| **E2E F3-8**, con una segunda variable `AGENTSTUDIO_E2E=1` | 3–7 |

---

## 9. Riesgos, preguntas abiertas y ADR

**Riesgos.**

| Riesgo | Mitigación |
|---|---|
| Codex `workspace-write` en un worktree (`.git` es un fichero) no está verificado; la captura 02 usó un repo normal | Spike S2: 1 ejecución con tu OK |
| Claude con `--json-schema` **y** herramientas no está verificado; la captura 03 usó `--tools ""` | Spike S3 |
| Portabilidad del esquema (estricto de OpenAI) y límite de 16 KB | Esquemas construidos en código, con test de forma estricta y de tamaño. El validador es la autoridad |
| Bloqueo de ficheros en Windows (MSBuild, VBCSCompiler, antivirus) al reiniciar o borrar worktrees | Variables de entorno, reintentos y limpieza diferida |
| `MAX_PATH` | Rutas cortas y `core.longpaths` en cada comando |
| Cuota desbocada | Aprobación del plan, `MaxAgentRuns`, topes de reintentos, `Review` sin reintentos, nada se relanza solo tras reiniciar, pruebas en vivo protegidas |
| Inyección de instrucciones desde el repo hacia el plan | Aprobación humana, predicados cerrados, `ToolId` como enum, alcance comprobado sobre el diff, datos delimitados, juez |
| Falsos abandonos en llamadas largas a herramientas | Umbral generoso y configurable, y solo en tareas `Running` |
| Evolución de los payloads | `V` y fixtures «golden» de registros |
| Juez del mismo vendor que el orquestador (sesgo hacia el plan) | Eliminado: el juez usa el otro vendor (decisión 7) y `PlanValidator` lo impone |
| Disco: checkout completo por tarea y refs ocultas | Limpieza con aprobación |
| Bitácora versionada que deja sucio el árbol del usuario (ADR-0008) | Entregar con `--ff-only` o con la comprobación propia de git |

**Preguntas abiertas: resueltas el 2026-10-15.**

1. **Hooks y firma GPG.** En los commits internos de `refs/agentstudio/*` se desactivan, con `-c commit.gpgsign=false`
   y `--no-verify`, y se documenta en ADR-0012. En la entrega a la rama del usuario se respetan.
2. **Conflictos.** La tarea falla, deja evidencia y se propone una ejecución de seguimiento. Nunca se resuelven solos.
3. **Entrega a tu rama.** Entra en la Fase 3 como acción opcional, siempre con aprobación y `--ff-only`. Ningún
   criterio de la fase depende de ella.
4. **Requisitos.** Los escribe y congela el usuario; el orquestador solo puede sugerirlos.
5. **Presupuestos por defecto.** `MaxAgentRuns` 12, timeout de tarea de 20 min y abandono a los 10 min. Se pueden
   configurar.
6. **Primer E2E.** Sobre un repo temporal de prueba, no sobre Agent Studio.
7. **Refs internas.** No se muestran: no hay panel de Git en la Fase 3.

**ADR a escribir.**

| ADR | Tema | Paquete |
|---|---|---|
| 0010 | Estado derivado del registro y reanudación; SQL sin Dapper | P0 |
| 0011 | Tareas condicionales (`Skipped`, ramas, unión) | P0 |
| 0012 | Worktrees fuera del repo, refs y `merge-tree`, entrega con aprobación | WP3 |
| 0013 | Protocolo del agente orquestador (esquema portable, validador, una corrección, aprobación) | WP4 |
| 0014 | Independencia del juez con dos vendors: orquestador y ejecutores en uno, juez en el otro (ADR-0006 sin cambios) | WP5 |
| 0015 | Lienzo Nodify (cierra la D6 tras el spike) | WP6 |
| 0016 | Perfil de verificación de confianza (congelado por ejecución, TRX/JUnit) | WP3 |

---

## 10. Desglose de trabajo

P0 es serie y se fusiona a `main` antes de abrir los paquetes. Después, WP2 a WP6 van en paralelo en worktrees distintos.

**Propietarios únicos de ficheros compartidos:**

- `Directory.Packages.props`, todos los `*.csproj` y la migración: P0.
- `src\AgentStudio.Infrastructure\DependencyInjection.cs`, `src\AgentStudio.Desktop\Composition\PlatformServices.cs`, `docs\08-…`, `docs\README.md` y la bitácora: el lead en la integración.

**Reglas para todos los paquetes.**

- Cada módulo expone su propia extensión `AddAgentStudioX()` dentro de su carpeta.
- Ningún paquete toca ficheros de otro.
- «Hecho» exige siempre: `dotnet build AgentStudio.slnx` con 0 avisos, `dotnet test` en verde sin ProviderLive, ADR y docs propios, y nada simulado en la UI.

### P0 · Contratos, dominio y migración (tamaño M-L; camino crítico; el lead o un agente)

**Propiedad (rutas absolutas):**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.Domain\Work\WorkTaskStatus.cs`, `…\Work\WorkTask.cs` y `…\Work\TaskGraph.cs`.
- `C:\Personal Projects\Agent Studio\src\AgentStudio.Domain\Decisions\DecisionLog.cs`: solo el enum.
- `C:\Personal Projects\Agent Studio\src\AgentStudio.Domain\Orchestration\**`.
- `C:\Personal Projects\Agent Studio\src\AgentStudio.Application\{Orchestration,Decisions,Projects,Git,Verification}\**`.
- `C:\Personal Projects\Agent Studio\src\AgentStudio.Application\Coordination\CoordinationContracts.cs`, `…\Persistence\StoreContracts.cs`, `…\Judging\JudgingContracts.cs`, `…\Security\SecurityContracts.cs`, `…\Skills\SkillPorts.cs` y `…\Skills\SkillProvisioner.cs`.
- `C:\Personal Projects\Agent Studio\src\AgentStudio.Infrastructure\Persistence\Schema\0002_orchestration.sql`.
- `C:\Personal Projects\Agent Studio\Directory.Packages.props` (Nodify 7.3.0 y TimeProvider.Testing, con versión verificada) y todos los `*.csproj`:
  - DI abstractions en Orchestration, Debate y Security;
  - Nodify en Desktop;
  - TimeProvider.Testing y globs de fixtures en Tests.
- Tests: `C:\Personal Projects\Agent Studio\tests\AgentStudio.Tests\Domain\**`, `…\tests\AgentStudio.Tests\Application\**` (nuevos) y `…\tests\AgentStudio.Tests\Infrastructure\SqliteSchemaTests.cs`.
- Docs: `docs\04-contratos.md`, ADR-0010 y ADR-0011.

**Hecho cuando:**

- `Skipped`, ramas, unión, `CompleteCondition` y `PropagateSkips` están probados. Los grafos sin condiciones se comportan igual, y `TestData.TaskIn` cubre `Skipped`.
- `PlanValidator` tiene un test por código.
- `RunStateReplayer` está probado: ida y vuelta, y detección de corrupción.
- `DecisionRecorder` está probado: concurrencia, resincronización y reloj que retrocede.
- `SkillProvisioner` está probado.
- La migración 0002 es idempotente sobre una base 0001, y sus triggers están probados.
- Docs/04 está actualizado.

**Arranque anticipado permitido antes de que P0 se fusione:** el spike de Nodify (WP6), y en WP3 los parsers TRX/JUnit, `TempGitRepo` y el spike de `merge-tree`.

### WP2 · Persistencia, proyecto y skills (M; depende de P0)

**Propiedad:**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.Infrastructure\Persistence\**`, salvo `Schema\*.sql` (incluye `SqliteSchema.cs` para WAL y `busy_timeout`).
- Clases: `ProjectWorkspace`, `ProjectDatabases`, los stores SQLite de decisiones, evidencias, requisitos, planes, veredictos, ejecuciones y plantilla, y `PersistenceServiceCollectionExtensions`, que también registra `IDecisionRecorder`.
- `C:\Personal Projects\Agent Studio\src\AgentStudio.Infrastructure\Skills\SkillCatalog.cs`, `SkillActivationStore.cs` y `SkillsServiceCollectionExtensions.cs`.
- `C:\Personal Projects\Agent Studio\src\AgentStudio.Providers\DependencyInjection.cs`, para registrar `ISkillMaterializer`.
- Tests: `C:\Personal Projects\Agent Studio\tests\AgentStudio.Tests\Infrastructure\Persistence\**` e `…\Infrastructure\Skills\{SkillCatalogTests,SkillActivationStoreTests}.cs`.
- Docs: `docs\05-modelo-de-datos.md`.

**Hecho cuando:**

- Las pruebas de F3-4 y F3-5 (§8) están en verde.
- Hay una prueba de recuperación que falla con un dossier sin persistir.
- `LocalPath` de las skills vendored se resuelve dentro de la raíz del índice y con `SKILL.md`; las enlazadas dan aviso.
- `info/exclude` es idempotente.
- Docs/05 está actualizado: numeración 0002/0003 y tablas de la 0001 sin uso.

### WP3 · Worktrees, integración, verificación y entrega (M-L; depende de P0)

**Propiedad:**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.Infrastructure\Git\**`.
- `C:\Personal Projects\Agent Studio\src\AgentStudio.Orchestration\Verification\**` y `…\Orchestration\Integration\**` (`Integrator`, `DeliveryService`, `WorkspaceCleanup` y sus extensiones de DI).
- Tests: `C:\Personal Projects\Agent Studio\tests\AgentStudio.Tests\Infrastructure\Git\**` (incluye el test en vivo de Codex en un worktree), `…\Orchestration\{Verification,Integration,Fixtures}\**`.
- Docs: ADR-0012 y ADR-0016.

**Hecho cuando:**

- Los escenarios de git de §8 están en verde: componentes reales sobre un repo temporal.
- La guarda de propiedad funciona.
- Hay `merge-tree` CAS idempotente.
- Conflicto: evidencia y nada a medio fusionar.
- Los timeouts matan el árbol.
- Una ejecución con 0 tests no se da por superada.
- La entrega y el borrado pasan por `IApprovalGate` (falso en los tests).
- Spikes S4 (flag TRX de xUnit v3/MTP) y S5 hechos.

### WP4 · Motor y agente orquestador (L; depende de P0; usa WP2, WP3 y WP5 solo por puertos o dobles)

**Propiedad:**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.Orchestration\Engine\**`, `…\Orchestration\Planning\**` y `…\Orchestration\OrchestrationServiceCollectionExtensions.cs`.
- Tests: `C:\Personal Projects\Agent Studio\tests\AgentStudio.Tests\Orchestration\{Engine,Planning,Fakes,Live}\**`.
- Docs: ADR-0013.

**Hecho cuando:**

- F3-1, F3-3 y F3-9, propiedades 1–7, recuperación y reanudación, y presupuesto, en verde.
- Esquema del plan: compatible con el modo estricto y de ≤ 16 KB; el parseo estricto rechaza campos desconocidos; una corrección y luego escalado.
- `Pending→Running` solo tras `StartAsync`.
- Test en vivo del planificador escrito (se ejecuta con tu OK).

### WP5 · Juez, revisión independiente y aprobaciones (M; depende de P0)

**Propiedad:**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.Debate\Judging\**` y `…\Debate\JudgingServiceCollectionExtensions.cs`.
- `C:\Personal Projects\Agent Studio\src\AgentStudio.Security\Approvals\**`.
- Tests: `C:\Personal Projects\Agent Studio\tests\AgentStudio.Tests\Debate\**` y `…\Security\Approvals\**`.
- Docs: ADR-0014 y `docs\07-debate-arena-y-juez.md` (§6 con `dontAsk`, §9 por ejecución).

**Hecho cuando:**

- El dossier solo se construye desde almacenes: la API recibe identificadores.
- `ExcludedVendors` se calcula desde el registro.
- La identidad del juez la pone el sistema.
- Una corrección como máximo; lo inválido se persiste tal cual.
- `VerdictIssued` lleva la cabeza del registro.
- Un registro roto da `NeedsHuman`.
- Pruebas de F3-7 en verde.
- Test en vivo del juez escrito.

### WP6 · Escritorio: lienzo Nodify, monitor, aprobaciones y revisión del plan (M-L; depende de P0; la conexión final después de WP2 a WP5)

**Propiedad:**

- `C:\Personal Projects\Agent Studio\src\AgentStudio.Desktop\Orchestrator\**`: ViewModels, `ApprovalPromptService : IApprovalPrompt`, almacén del diseño del lienzo y fachada de sesión.
- `C:\Personal Projects\Agent Studio\src\AgentStudio.Desktop\Views\Orchestrator\**`, `…\Desktop\Themes\Nodify.xaml` y `…\Desktop\Composition\OrchestrationUiServices.cs`.
- Modifica: `…\Desktop\ViewModels\MainViewModel.cs`, `…\Desktop\MainWindow.xaml` (y `.cs`), `…\Desktop\Themes\DocumentTemplates.xaml`, `…\Desktop\App.xaml` y `…\Desktop\Composition\ShellServices.cs`.
- Tests: `C:\Personal Projects\Agent Studio\tests\AgentStudio.Tests\Desktop\Orchestrator\**` y `…\Desktop\MainViewModelTests.cs`.
- Docs: ADR-0015 y `docs\03-pantallas-y-navegacion.md`.

**Hecho cuando:**

- El spike D6 está cerrado el primer día.
- Las pruebas de ViewModel de F3-6 están en verde.
- Aprobaciones con fallo cerrado.
- El monitor refleja los eventos y la salud (con un `IOrchestrationRun` falso), con «Interrumpida» distinta de bloqueo.
- Los nodos `Debate` aparecen como «Fase 5».
- Hay una lista de comprobación para la demostración.

### Integración (el lead, tras las fusiones)

- `PlatformServices.cs`: añadir una línea por módulo.
- `C:\Personal Projects\Agent Studio\tests\AgentStudio.Tests\Integration\Phase3\**`:
  - F3-2 completo con el motor;
  - recuperación con git;
  - composición: `IOrchestrator` se resuelve y todos los proveedores están en la bitácora.
- Demo de F3-3 sin cuota, usando nodos `ToolRun`, `Condition` y `HumanApproval`.
- **E2E de F3-8 con tu OK**, con evidencias en `docs\evidencias\fase3-*`.
- Actualizar docs/08, README y la bitácora.

### Critical Files for Implementation

- C:\Personal Projects\Agent Studio\src\AgentStudio.Domain\Work\TaskGraph.cs
- C:\Personal Projects\Agent Studio\src\AgentStudio.Domain\Decisions\DecisionLog.cs
- C:\Personal Projects\Agent Studio\src\AgentStudio.Application\Coordination\CoordinationContracts.cs
- C:\Personal Projects\Agent Studio\src\AgentStudio.Application\Persistence\StoreContracts.cs
- C:\Personal Projects\Agent Studio\src\AgentStudio.Infrastructure\Persistence\Schema\0001_initial.sql
