# ADR-0053 · Windows Hello como barrera fija: una lista en el código que ningún ajuste ni fichero desactiva, y el bloqueo de Windows comprobado como alternativa

- **Estado:** aceptada · 2026-11-09. Recoge D-37 a D-41 de
  [docs/17 §4.9](../17-constructor-visual-de-agentes.md#49-windows-hello-como-barrera-fija) y la decisión 6 (a) que aceptaste
  el 2026-11-06: la lista de D-37 y, sin Hello, el bloqueo de Windows comprobado. El P0 de la Fase 13 dejó los tipos
  (`PresencePolicy`, `PresenceProof`, `IWindowsLockConfirmation`, `ApprovalVerification.WindowsLock`, el ajuste con su nombre
  nuevo) y la regla única `IsSatisfiedBy`; lo implementa **WP5**. La re-revisión del P0 (2026-11-08) añadió dos decisiones:
  con Hello en el equipo solo vale Hello, y si Windows no se deja bloquear el aviso dice cómo configurar Hello. **Enmienda**
  [ADR-0037](0037-windows-hello-y-bloqueo-de-la-interfaz.md) (su adenda del 2026-11-09) y tu decisión 5 (a) del 2026-10-20.
  Falta la demostración en tu equipo (§9.3, F13-16).
- **Contexto:**
  - P18 pide que lo que necesita permisos especiales pida siempre Windows Hello, «aunque ponga permisos en el config.json».
  - Hasta ahora el ajuste de Hello se podía apagar, y entonces decidía el diálogo solo. Lo crítico se reconocía por las
    palabras de la acción (`ApprovalRiskClassifier`). Lo destructivo nunca pedía Hello. Y dos caminos no pasaban por la
    puerta: «Aprobar plan» del lienzo y el consentimiento de la recomendación de skills (docs/17 §0.2, docs/18 P38).
  - Cualquier programa de tu sesión puede editar un fichero de ajustes, también un agente al que diste `Bash` (residual 1 de
    [ADR-0018](0018-modelo-de-amenaza-fase-3.md)). Una barrera que se apaga con un fichero no es una barrera.
- **Decisión:**
  1. **Una lista en el código, por acción y nunca por las palabras de la acción** (D-37). La tabla es la de
     `PresencePolicy.RequirementOf` (dominio, P0):

     | Requisito | Acciones |
     |---|---|
     | Siempre | `DeliverToBranch`, `AcceptHistoryAsOrigin`, `GrantHighRiskCapability`, `ApprovePlanWithHighRiskCapability`, `ChangeVerificationProfile`, `RemoveProtection`, `RemoteOrSecretOperation` |
     | Configurable (activo por defecto) | `ApprovePlan`, `OpenOrResumeDebate`, `AuditProject`, `AcceptReview`, `DiscardAgentWork` |
     | Nunca | `AnswerAgentQuestion`, `AuxiliaryQuotaConsent` |

     Cada pregunta de la puerta se reconoce por el identificador exacto de su acción, la constante del código que la pide
     (`ApprovalRiskClassifier.PresenceActionOf`, en Security). Los módulos no se referencian entre sí (ADR-0002), así que los
     identificadores se repiten allí, y una prueba compara cada uno con su constante:

     | Identificador | Constante | Acción de la barrera |
     |---|---|---|
     | `deliver-integration` | `DeliveryService.ApprovalAction` | `DeliverToBranch` (siempre) |
     | `decision-log.accept-origin` | `UnanchoredHistory.AcceptAction` | `AcceptHistoryAsOrigin` (siempre) |
     | `debate.open`, `debate.resume`, `debate.human-resolution` | `DebateCoordinator.OpenAction` y `ResumeAction`, `DebateRun.HumanResolutionAction` | `OpenOrResumeDebate` |
     | `orchestration.human-approval` | `Orchestrator.HumanApprovalAction` | `ApprovePlan` (una tarea de aprobación del plan) |
     | `orchestration.review-escalation` | `Orchestrator.ReviewEscalationAction` | `AcceptReview` |
     | `audit.run` | `ProjectAuditService.ApprovalAction` | `AuditProject` |
     | `verdict.revision.accept` | `ChallengeLog.RevisionAcceptanceAction` | `AcceptReview` |
     | `discard-run-work` | `WorkspaceCleanup.ApprovalAction` | `DiscardAgentWork` |
     | `skills.recomendacion-asistida` | `SkillsDocumentViewModel.AssistedAction` | `AuxiliaryQuotaConsent` (nunca) |

     - **Una acción que la lista no nombra** (hoy, `replan.dismiss`) va por su nivel: crítica o destructiva, configurable; baja,
       nunca. Sus palabras nunca la ponen en «Siempre» ni la quitan (decisión 6 (a): «configurable lo demás»). El nivel por
       palabras sigue sirviendo para lo que enseña el diálogo y lo que guarda el registro.
     - **Aprobar un plan** es `ApprovePlanWithHighRiskCapability` cuando `MediumIntegrityWriters.Find` encuentra algún escritor
       a integridad media en el plan, su plantilla y su perfil (consola, Codex con escritura, un CLI o herramientas sin medir,
       un paso sin aislar, un agente o un paso desconocidos), y `ApprovePlan` si no (`Desktop/Lock/PlanPresence.cs`). Sin
       plantilla o sin perfil no se pueden leer: cuenta como escritor. La barrera no tiene otra lista de capacidades.
  2. **Ningún fichero ni ajuste la desactiva** (D-38).
     - El ajuste se llama «Pedir Windows Hello también en las aprobaciones importantes»
       (`AppSettings.WindowsHelloForImportantApprovals`; el fichero de antes se lee con su nombre viejo) y solo gobierna lo
       configurable. Para «Siempre», `ReinforcedConfirmation` ni siquiera lo lee: una prueba lo comprueba con un ajuste que
       lanza una excepción.
     - Editar `%APPDATA%\AgentStudio\settings.json` a mano no cambia la lista: con el ajuste a `false`, con su nombre viejo,
       con claves inventadas (`"disableHello": true`, una «política» propia, una lista de herramientas permitidas), con un valor
       de otro tipo, con un fichero que no es JSON y con el fichero borrado, entregar y aceptar un historial piden Windows
       Hello igual. Agent Studio no tiene ningún `config.json` de permisos, y los ficheros de los CLIs no entran (docs/17 D-38).
     - La sección «Windows Hello y bloqueo» de «Ajustes» lo explica con sus textos de `Resources/HelloStrings.resx`.
  3. **Una sola regla para todos los caminos** (D-39): `PresencePolicy.IsSatisfiedBy(acción, ajuste, helloAvailable, prueba)`.
     - **La regla.** «Siempre»: con Windows Hello en el equipo, solo Hello; sin Hello, también el bloqueo de Windows
       desbloqueado después de la petición; nunca el diálogo solo ni el diálogo con su aviso. «Configurable» con el ajuste:
       Hello y, sin Hello, también el bloqueo o el diálogo con su aviso; sin el ajuste, el diálogo. «Nunca»: nada. «Hay Hello»
       es lo contrario de un «no está» claro de Windows (`HelloAnswers.IsAbsent`): si Windows está ocupado o no lo dice, se
       pide Hello y el bloqueo no cuenta.
     - **Las pruebas fuertes salen de Windows.** `PresenceProof.WindowsHello` y `WindowsLockUnlockedAfterRequest` solo los
       dan `PresenceProofs.FromHello` y `FromWindowsLock`, a partir de un resultado de Windows (`HelloOutcome.Proof`). El diálogo
       solo prueba el diálogo (`ConfirmationPlan.AnswerAloneProof`). Una prueba recorre `src` y falla si otro código nombra esas
       dos pruebas, convierte un número al tipo o lo lee de un texto.
     - **El diálogo de aprobación** (`ApprovalPromptService`, y con él la puerta y aceptar un historial):
       `ReinforcedConfirmation.PlanAsync` decide antes de preguntar si hace falta Hello o el bloqueo, y `Approves` juzga lo que
       confirmó a la persona con la regla. El diálogo dice cuándo la acción pide Hello siempre y, sin Hello, que Windows se va a
       bloquear. Sin `ReinforcedConfirmation` compuesta, una acción de la lista no se enseña: la puerta la deniega.
     - **La puerta** (`ApprovalGate`) comprueba además cada «sí»: lee el método que el diálogo registró como la prueba que
       afirma (`ApprovalRiskClassifier.ClaimedProof`) y aplica la regla con el suelo que nada baja (sin el ajuste y sin Hello).
       Un «sí» a una acción de la lista sin Hello ni bloqueo es una denegación del sistema (`agent-studio`), con el motivo
       `ApprovalGate.PresenceNotConfirmed`, venga del diálogo que venga. Lo que añaden el ajuste y la presencia de Hello lo
       comprueba el diálogo, que los conoce.
     - **«Aprobar plan» del lienzo** usa la misma `ReinforcedConfirmation`, con `PlanPresence`, y la misma regla. El plan con
       escritores avisa antes de aprobarlo de que pedirá Hello o el bloqueo, y el plan aprobado con el bloqueo lo dice.
     - **El consentimiento de la recomendación de skills** pasa por la puerta (`SkillsBackend.Approvals`): deja
       `ApprovalRequested` y `ApprovalGranted` o `ApprovalDenied` como cualquier aprobación (F13-20). No concede nada: nunca
       pide Hello, y su respuesta se escribe en la versión 1.
     - **Lo destructivo** (`discard-run-work`) pide ahora Hello mientras el ajuste está activo (configurable, D-37).
  4. **Sin Hello en el equipo: el bloqueo de Windows comprobado** (D-40 (a)). `WindowsLockConfirmation` (`Desktop/Lock`):
     - antes de bloquear, la sesión tiene que leerse desbloqueada (la persona acaba de pulsar «Aprobar»); si no, no se fía del
       estado, no bloquea y no confirma nada (`NotVerified`);
     - bloquea con `LockWorkStation`; si Windows se niega, `LockFailed`;
     - lee el estado cada 250 ms con `WTSQuerySessionInformationW` y `WTSSessionInfoEx`: el nivel 1 y su `SessionFlags`
       (`WTS_SESSIONSTATE_LOCK` = 0, `WTS_SESSIONSTATE_UNLOCK` = 1; la inversión documentada para Windows 7 y Server 2008 R2
       no aplica, porque la app pide Windows 11). Lee el nivel y esas banderas y nada más: nunca el usuario ni el dominio;
     - tiene que ver la sesión bloqueada en 10 segundos y, después, desbloqueada: ese desbloqueo llegó después de la petición
       (`UnlockedAfterRequest`). Sin verla bloqueada, `NotVerified`; sin desbloqueo en 5 minutos
       (`ReinforcedConfirmation.WindowsLockTimeout`), `TimedOut`; una lectura que falla no confirma nada;
     - no se fía de ningún aviso (`WM_WTSSESSION_CHANGE`, `SystemEvents.SessionSwitch`): cierra para la barrera el residual del
       aviso falsificado de ADR-0037, decisión 5;
     - la respuesta se registra con `"verification":"WindowsLock"` en la versión 2 de siempre;
     - **si Windows no se deja bloquear** (una directiva lo impide) **o no hay bloqueo compuesto**, nada puede aprobar una
       acción de la lista, ni siquiera la entrega. Es coherente con la barrera; el motivo de la denegación y el error de
       «Aprobar plan» terminan diciendo qué hacer: configurar Windows Hello en Configuración → Cuentas → Opciones de inicio de
       sesión (D-40 (b), `Hello_SetUpHello`).
  5. **Trabajo desatendido** (D-41), sin cambios: lo que la ejecución necesita se aprueba al principio (aprobar el plan con
     Hello cubre sus capacidades); la entrega espera al final; el plazo de 30 minutos de la puerta sigue, también mientras
     Windows espera desbloqueado.
- **Alternativas:**
  - **Las de la decisión 6:** todo lo crítico de hoy siempre (b), o todo configurable (c); sin Hello, denegar siempre (b) o
    el diálogo solo (c).
  - **Seguir reconociendo lo crítico por las palabras de la acción:** una tarea de aprobación de un plan, cuya acción escribe
    el plan, entraría o saldría de la lista según cómo se llame. D-37 pide «por acción».
  - **Aceptar el bloqueo de Windows también con Hello en el equipo** (lo que decía la regla del P0 antes de la re-revisión):
    el bloqueo es la alternativa para un equipo sin Hello, no un camino para saltárselo.
  - **Que el lienzo sin `ReinforcedConfirmation` compuesta no apruebe un plan con escritores:** la app siempre la compone, y
    obligaría a cambiar unas 12 construcciones del lienzo en pruebas de otros paquetes (WP6, Fase 8). Queda como residual.
  - **Comprobar la verificación del plan en el motor** (`Orchestrator.RecordStartAsync`): es el sitio más fuerte, pero no es
    de este paquete. Queda como residual para la integración.
  - **Tratar como «Siempre» toda acción que la lista no nombre:** contradice «configurable lo demás» de la decisión 6 (a).
- **Consecuencias:**
  - **Pruebas (F13-16 y F13-20):** `FixedBarrierTests` (los ficheros de ajustes manipulados, el ajuste nunca leído para
    «Siempre», el bloqueo de Windows sin Hello y su `WindowsLock` en el registro, el bloqueo que no vale con Hello, el aviso
    para configurar Hello, el diálogo sin confirmación compuesta); `PresenceBarrierGateTests` (las constantes, las acciones
    que la lista no nombra, un diálogo que aprueba sin Hello denegado por el sistema, el JSON de `WindowsLock`);
    `PresenceProofSourceTests`; `WindowsLockConfirmationTests` (con un doble de la sesión, y una de `LocalTools` que lee la
    sesión real sin bloquearla); `PresencePolicyTests` con `helloAvailable`; `PlanApprovalHelloTests`; y la de skills que
    deja su `ApprovalGranted`. Ninguna pide el Windows Hello real ni bloquea la sesión.
  - **Medido en tu equipo (Windows 11, compilación 26300):** con la sesión bloqueada (`LogonUI.exe` en la sesión 1),
    `WindowsSessionState.Read` da `Locked`: la disposición de `WTSINFOEXW` y el valor 0 de `SessionFlags` son los esperados.
    La lectura desbloqueada y el desbloqueo con PIN tras `LockWorkStation` quedan para tu demostración.
  - **Residuales:**
    - el motor registra la verificación que trae la aprobación del plan sin comprobarla contra sus escritores
      (`Orchestrator.RecordStartAsync`); hoy solo la construye el lienzo, con la regla;
    - un lienzo compuesto sin `ReinforcedConfirmation` (nunca en la app) aprueba con el clic, como antes de la Fase 8;
    - la puerta se fía del método que registra el diálogo: es código de la app, no de un agente;
    - el residual 1 de ADR-0018: un programa de tu sesión puede actuar sin pasar por Agent Studio;
    - entre comprobar si hay Hello y verificar, el resultado puede cambiar: entonces se deniega.
  - **Pendiente fuera de WP5:** los caminos nuevos de D-39 que guardan una plantilla con una capacidad de alto riesgo
    (`GrantHighRiskCapability`) o un perfil de verificación (`ChangeVerificationProfile`), entre ellos «Usar perfil propuesto»
    del lienzo y el configurador (WP6 y WP7), tienen que llamar a `ReinforcedConfirmation.PlanAsync` con su acción,
    `ConfirmAsync` tras un «sí» y `Approves`. El aviso de la bandeja «La entrega espera tu Windows Hello» (D-41) es de la
    integración.
