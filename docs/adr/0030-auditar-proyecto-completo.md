# ADR-0030 · «Auditar proyecto completo»: el commit HEAD, la verificación aislada, un dossier índice y la regla de los productores con autores declarados; sin juez con el registro roto

- **Estado:** aceptada · 2026-10-20. Recoge D-28 a D-31 de [docs/15](../15-fase-5-debate-arena-y-juez.md#47-f5-5--auditar-proyecto-completo)
  y F5-5. Lo implementa WP3 en el módulo Debate (`Audit/`): `ProjectAuditService` (`IProjectAuditService`),
  `ProjectDossierBuilder`, `ProjectAuditPrompt` y `PendingProblems`. Los contratos son los de P0
  ([docs/04 §17.4](../04-contratos.md#174-auditoría-e-impugnación-applicationjudging)); la cabecera la guarda
  `IProjectAuditRegistry` (WP4).
- **Contexto:**
  - Un repositorio no cabe en un prompt: este tiene unas 63 000 líneas de código, 87 000 de pruebas y 14 000 de documentación.
  - El registro solo sabe quién produjo lo que hizo Agent Studio. El código escrito fuera (sesiones de Claude Code, a mano) no
    tiene productor en el registro.
  - Docs/07 §9 decía que, con el registro roto, la auditoría «continúa marcada como no fiable». Pero sin `AgentRunStarted` no
    hay lanzamiento (ADR-0010) y un veredicto no se puede anclar a un registro que no admite entradas.
  - En P0, `ProjectAuditRecord.Judge` y `ProjectAuditStartedPayload.Judge` no admiten nulo: una auditoría sin juez no tiene
    cabecera que anclar.
- **Decisión:**
  1. **Qué se audita.** El commit HEAD del repositorio (`IWorktreeManager.InspectAsync`); los cambios sin commit no entran, y la
     aprobación y el informe lo dicen. Contra el conjunto de requisitos congelado que eliges, por su hash. Con el perfil de
     verificación de confianza del proyecto, que el servicio lee por un puerto propio, `IAuditVerificationProfiles`, con la
     forma de `IVerificationProfileSource` del escritorio, que lo adapta.
  2. **Quién produjo el código** = los vendors productores de todas las ejecuciones del registro (`RunLog.ProducerVendors`, que
     nunca cuenta a los jueces) ∪ los autores que declaras. El juez es un `Judge` o un `Auditor` de la plantilla, con una
     definición válida, de un vendor que no está en ese conjunto. **La regla P no se dispensa**: si no queda juez, la auditoría
     no se lanza.
  3. **Preparar no escribe nada.** `HeaderHash` = `DebateHeaderHash.Proposal` de la cabecera sin lo que solo da el arranque
     (id, proyecto, hora e id de la evidencia de la petición), con el SHA-256 del texto de la petición. La aprobación
     `audit.run` (crítica) se ata a ese hash. Al auditar se prepara otra vez y tiene que dar el mismo hash: si cambiaron HEAD, los
     requisitos, el juez, los productores o el perfil, se rechaza antes de preguntar. La actividad del proyecto
     (`ProjectActivityKind.Audit`) se toma antes de la aprobación: una auditoría no comparte el proyecto con nada (D-05).
  4. **Sin juez no se escribe nada.** Con el registro roto, sin juez posible, o sin requisitos, perfil o repositorio legibles, no
     se pide la aprobación ni se ejecuta ni se escribe nada. El informe es la escalada del sistema (`EscalateToHuman`, juez
     `agent-studio`, `VerdictId` nulo, no fiable), con la integridad del registro y los problemas pendientes, leídos del
     registro tal como está (sin anclar si está roto). **Diferencia con D-29:** sin juez posible, D-29 da también «la
     verificación». No se ejecuta: la cabecera de P0 exige un juez, y una evidencia registrada en una unidad sin cabecera
     anclada no prueba nada. Cuando haya juez posible, la auditoría la hace.
  5. **El flujo, en este orden:**
     - la petición (el texto que se te enseñó) se captura como `Document`/`CapturedBySystem` y se anuncia. La escribe el
       sistema con lo que elegiste: así nunca pasa por la revisión de una persona (`HumanReview`), que pide `ProvidedByHuman`;
     - la cabecera se guarda y `ProjectAuditStarted` ancla `DebateHeaderHash.Of(cabecera)`;
     - **verificación aislada de HEAD:** los pasos `isolation: none` no se ejecutan (D-27), y la propuesta y el dossier los
       nombran. Los demás corren con `EvidenceScope(SubjectCommit = HEAD, RepositoryRoot = el proyecto)`: el verificador saca
       su propia copia del commit (ADR-0017), nunca el árbol de trabajo. Su evidencia se anuncia en la unidad de la auditoría;
     - **copia de HEAD para el juez** (`ArchiveCommitAsync`) en `<audits>\<proyecto>\<auditoría>\snapshot`, de solo lectura, que
       se borra al acabar. Si la copia falla, la verificación se queda y el juez no se lanza;
     - el registro se lee otra vez: si dejó de verificar, no se escribe nada más;
     - el dossier índice. El índice que recibe el juez se guarda también como evidencia de la auditoría
       (`Document`/`CapturedBySystem`, «Índice del dossier de la auditoría»), anunciada después de construir el dossier, así que
       no está en él. Una revisión del veredicto (D-38) reconstruye la auditoría de su registro y sus evidencias, no de un
       paquete: así tiene `ProjectAuditStarted`, la petición, la verificación de HEAD y ese índice, sin filas sin anunciar;
     - el juez (`JudgeRunRecorder.ForAudit`, tarea `juez-<agente>`; `VerdictKeeper` con
       `AuditScope.WholeProject`) y una corrección como mucho. Un juez que el proveedor sirve en un vendor excluido no se lanza
       (alerta `JudgeNotIndependent`). El límite de uso da `RateLimited`; un veredicto ilegible, `NeedsHuman`;
     - `ProjectAuditFinished` con todos los veredictos guardados, la disposición y si es fiable. Una auditoría empezada que lanza
       o se cancela se cierra igual (interrumpida), salvo que el registro se rompiera.
  6. **El dossier es un índice (R8, D-30):** los requisitos con sus criterios; la verificación de HEAD (contadores del TRX,
     código de salida y commit); cada ejecución (inicio, final, veredictos y alertas); cada debate (modo, pregunta leída con su
     hash anclado, resultado e impugnaciones); `docs/**` y `README*` con tamaño y SHA-256; los problemas pendientes; productores,
     autores declarados y pasos no ejecutados; las últimas 200 entradas de los tipos que importan, resumidas.
     - **Tope de 256 KB sobre el JSON que recibe el juez**, el sangrado del prompt y de `dossier.json`. Se recorta de forma
       determinista: primero los documentos (se quedan los primeros), luego las entradas (se quedan las últimas), los debates y
       las ejecuciones. `truncated` cuenta lo que quedó fuera, también las entradas más allá de las 200.
     - El texto va escapado (`JavaScriptEncoder`): nada del registro cierra el delimitador del prompt.
     - En el `JudgeDossier`, `CandidateCommit` = HEAD (solo cuentan los `TestRun` de HEAD) e `IntegrityProblems` = lo que el
       sistema encontró (evidencia sin anunciar o alterada, documentos ilegibles, una copia que falló). Con alguno, no hay
       aprobación.
  7. **Problemas pendientes (D-31)**, con códigos estables y mensajes en castellano: `run.failed`, `run.unfinished`,
     `review.needs-human`, `debate.unfinished`, `debate.unresolved`, `challenge.open`, `replan.pending` (hasta que una persona
     la descarta, `replan.dismiss` pedido después de ella en la ejecución, o empieza una ejecución de seguimiento, como lo lee
     la entrega), `memory.unreviewed` y `memory.pending-task`.
  8. **El brief del juez** (`ProjectAuditPrompt`): rúbrica en inglés, texto libre del veredicto en castellano y el esquema de
     veredicto de la Fase 3. Lo que afirma del código sin evidencia es una valoración suya.
- **Alternativas:**
  - **Verificar y anunciar la evidencia sin juez (D-29 al pie de la letra):** dejaría una unidad `aud-…` sin cabecera anclada.
  - **Seguir «no fiable» con el registro roto (docs/07):** no hay dónde anclar el veredicto. Lo descarta D-29.
  - **Meter el código en el dossier:** no cabe; el juez lo lee en la copia.
  - **La petición como `ProvidedByHuman`, como la petición original de una ejecución:** es texto del sistema, y se podría citar
    como revisión humana.
  - **Verificar en la copia del juez:** el verificador ya hace la suya (ADR-0017), y el juez lee una copia que no tocó nadie.
- **Consecuencias:**
  - Una auditoría cuesta una ejecución de juez («estimado por Agent Studio»: hasta 500 000 tokens) y una corrección como mucho;
    la verificación no gasta cuota.
  - Si los dos vendors produjeron código, no hay auditoría. En este repositorio se declara Anthropic y juzga Codex (D-32).
  - La composición registra `IAuditVerificationProfiles` (adaptando `FileVerificationProfileSource`). `AddAgentStudioAudits`
    deja las copias en la carpeta `audits`, al lado de la de dossiers, salvo que se registren otras `AuditOptions`.
  - Sin verificar: el registro SQLite de las cabeceras con el servicio, el verificador real a integridad Low sobre este
    repositorio (D-32) y el tiempo de listar los documentos de un repositorio grande.

## Lista de comprobación de la demostración sobre este repositorio (D-32)

Sin cuota, antes de lanzar nada:

1. Abre el repositorio en la app. Crea `.agentstudio/agentstudio.db` con el registro vacío, y `git status` no lo enseña.
2. Congela el conjunto de requisitos de la auditoría (decisión 4 de la §12: C-1 a C-4 y una selección de los de las Fases 3 y
   4) y anota su hash.
3. Guarda el perfil de verificación de confianza (`.agentstudio/verification.json`) con build y test, aislados (sin
   `isolation: none`).
4. Mide la suite a integridad Low con el verificador aislado. Si fallan pruebas por el entorno (ConPTY, Job Objects, uniones,
   WebView2), añade al paso de test el `--filter-not-trait` de esas categorías, guarda el perfil otra vez y anótalo: el
   informe lo tiene que decir.
5. Comprueba que la plantilla tiene un juez de Codex de solo lectura.
6. Prepara la auditoría con ese hash, el juez de Codex y los autores declarados = Anthropic. La propuesta debe enseñar:
   - HEAD, y si hay cambios sin confirmar (no entran);
   - ningún productor en el registro y Anthropic como autor declarado;
   - los pasos que no se ejecutan (ninguno);
   - hasta 500 000 tokens «estimado por Agent Studio»;
   - que puede juzgar.

   Con el juez de Claude, la propuesta tiene que decir que no puede juzgar (regla P).

Con cuota (una ejecución de Codex y una corrección como mucho; entre 5 y 15 minutos):

7. Aprueba `audit.run`.
8. En el registro, en este orden: la aprobación, la petición (`EvidenceRegistered`), `ProjectAuditStarted`, el `BuildLog` y el
   `TestRun` con `SubjectCommit` = HEAD, el índice del dossier, el lanzamiento del juez (tarea `juez-<agente>`, propósito
   `ProjectAudit`), `VerdictIssued` y `ProjectAuditFinished`.
9. En el informe: el commit auditado, los requisitos satisfechos, incumplidos y no verificables con sus evidencias, los
   problemas pendientes y si es fiable. Además:
   - la copia `audits\<proyecto>\<aud-…>\snapshot` ya no está;
   - el `dossier.json` del paquete ocupa 256 KB o menos;
   - la bitácora del juez lleva `#auditoria` y el id `aud-…`.
10. Anota los tokens reales de Codex frente a la estimación.
