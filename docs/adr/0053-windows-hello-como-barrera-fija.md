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

     - **Una acción que la lista no nombra** va por su nivel, del lado seguro (revisión de seguridad de WP5, 2026-11-09:
       «que sea una barrera de seguridad siempre»): crítica o destructiva, declarada o por sus palabras, pide Hello **siempre**;
       de riesgo bajo (hoy, `replan.dismiss`), nunca. Las palabras pueden subir una acción a «Siempre», nunca sacar de la lista
       una que la lista nombra. Una prueba recorre `src` (`CriticalApprovalCoverageTests`): toda aprobación nombra su acción con
       una constante `…Action`, y toda constante así está en la lista o es de riesgo bajo, así que ninguna aprobación crítica
       del código se juzga solo por su nivel.
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
  3. **Una sola regla para todos los caminos** (D-39): `PresencePolicy.IsSatisfiedBy(acción, ajuste, helloAvailable, prueba)`,
     que es la misma regla por requisito (`IsSatisfiedBy(requisito, …)`) con el requisito de la acción; con esa juzgan las
     aprobaciones que la lista no nombra.
     - **La regla.** «Siempre»: con Windows Hello en el equipo, solo Hello; sin Hello, también el bloqueo de Windows
       desbloqueado después de la petición; nunca el diálogo solo ni el diálogo con su aviso. «Configurable» con el ajuste:
       Hello y, sin Hello, también el bloqueo o el diálogo con su aviso; sin el ajuste, el diálogo. «Nunca»: nada. «Hay Hello»
       es lo contrario de un «no está» claro de Windows (`HelloAnswers.IsAbsent`): si Windows está ocupado o no lo dice, se
       pide Hello y el bloqueo no cuenta.
     - **Las pruebas fuertes salen de Windows.** `PresenceProof.WindowsHello` y `WindowsLockUnlockedAfterRequest` solo los
       dan `PresenceProofs.FromHello` y `FromWindowsLock`, a partir de un resultado de Windows (`HelloOutcome.Proof`). El diálogo
       solo prueba el diálogo (`ConfirmationPlan.AnswerAloneProof`). Quien solo ve el método registrado (la puerta, el motor, el
       registrador) lo lee como la prueba que afirma con `PresenceProofs.Claimed`, la inversa de `Verification`.
       `PresenceProofSourceTests` recorre `src` línea a línea, sin comentarios, y falla si fuera de su sitio alguien nombra esas
       dos pruebas, convierte un número al tipo, lo lee de un texto, hace aritmética o bit a bit con un valor del tipo, escribe
       un resultado que confirma como literal en una conversión (`FromHello(UserPresenceResult.Verified)`), construye un
       `HelloOutcome` (o le da un resultado que confirma) o un `ConfirmationPlan`, o fija con `with` lo que decide el plan.
       **Límite:** no ve un alias del tipo, una variable del tipo incrementada, la reflexión, `dynamic`, `Unsafe.As` ni una
       expresión partida en varias líneas; eso queda para la revisión.
     - **Una sola confirmación a la vez** (revisión de seguridad de WP5): `PresenceConfirmationSlot`, un canal de capacidad
       uno con un solo consumidor, en `ReinforcedConfirmation` (lo comparten la puerta, aceptar un historial y «Aprobar plan»)
       y en `WindowsLockConfirmation`. Mientras una confirmación espera (Hello o el desbloqueo de Windows), otra se rechaza al
       momento, sin cola: la del bloqueo da `NotVerified` y no bloquea nada; la de la aprobación no aprueba y dice «Ya hay una
       confirmación pendiente». Así un desbloqueo de Windows o un Hello nunca confirman dos aprobaciones. Una pregunta
       retirada (la interfaz se bloquea, caduca) libera la confirmación al momento: su respuesta ya no cuenta.
     - **El diálogo de aprobación** (`ApprovalPromptService`, y con él la puerta y aceptar un historial):
       `ReinforcedConfirmation.PlanAsync` decide antes de preguntar si hace falta Hello o el bloqueo, y `Approves` juzga lo que
       confirmó a la persona con la regla. El diálogo dice cuándo la acción pide Hello siempre y, sin Hello, que Windows se va a
       bloquear. Sin `ReinforcedConfirmation` compuesta, una acción de la lista no se enseña: la puerta la deniega.
     - **La puerta** (`ApprovalGate`) comprueba además cada «sí»: lee el método que el diálogo registró como la prueba que
       afirma (`PresenceProofs.Claimed`) y aplica la regla, con el requisito de la petición, con el suelo que nada baja (sin el
       ajuste y sin Hello). Un «sí» a lo que es «Siempre» sin Hello ni bloqueo es una denegación del sistema (`agent-studio`), con el motivo
       `ApprovalGate.PresenceNotConfirmed`, venga del diálogo que venga. Lo que añaden el ajuste y la presencia de Hello lo
       comprueba el diálogo, que los conoce.
     - **«Aprobar plan» del lienzo** usa la misma `ReinforcedConfirmation`, con `PlanPresence`, y la misma regla. El plan con
       escritores avisa antes de aprobarlo de que pedirá Hello o el bloqueo, y el plan aprobado con el bloqueo lo dice.
     - **El consentimiento de la recomendación de skills** pasa por la puerta (`SkillsBackend.Approvals`): deja
       `ApprovalRequested` y `ApprovalGranted` o `ApprovalDenied` como cualquier aprobación (F13-20). No concede nada: nunca
       pide Hello, y su respuesta se escribe en la versión 1.
     - **Lo destructivo** (`discard-run-work`) pide ahora Hello mientras el ajuste está activo (configurable, D-37).
     - **Comprobaciones de respaldo** (revisión de seguridad de WP5), con la regla y el suelo que nada baja:
       - el motor no arranca un plan con escritores a integridad media si su aprobación no trae Windows Hello ni el bloqueo
         comprobado (`Orchestrator.EnsureApprovedWarning`);
       - el motor no reanuda una ejecución cuyo `RunStarted` ancló escritores y cuya aprobación del plan no confirmó a la
         persona (una aprobada con el diálogo antes de esta barrera): escribe la alerta `run.not-resumable` y pide aprobar el
         plan otra vez, con Hello, como una ejecución de seguimiento (`RunNeedsApprovalException`);
       - el registrador no acepta un historial como nuevo origen con una aprobación que no confirmó a la persona
         (`DecisionRecorder.AcceptAsNewOriginAsync`).
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
  - **Dejar configurable lo crítico que la lista no nombra** (lo que decía la primera versión de WP5, por «configurable lo
    demás» de la decisión 6 (a)): una aprobación crítica nueva que nadie añadiera a la lista quedaría a merced del ajuste.
    La revisión de seguridad eligió el lado seguro.
  - **Poner en cola la segunda confirmación** en lugar de rechazarla: la esperaría el mismo desbloqueo o el mismo Hello que
    la primera, y la pantalla de bloqueo de Windows no dice qué se aprueba.
- **Consecuencias:**
  - **Pruebas (F13-16 y F13-20):** `FixedBarrierTests` (los ficheros de ajustes manipulados, el ajuste nunca leído para
    «Siempre», el bloqueo de Windows sin Hello y su `WindowsLock` en el registro, el bloqueo que no vale con Hello, el aviso
    para configurar Hello, el diálogo sin confirmación compuesta); `PresenceBarrierGateTests` (las constantes, las acciones
    que la lista no nombra, un diálogo que aprueba sin Hello denegado por el sistema, el JSON de `WindowsLock`);
    `PresenceProofSourceTests`; `CriticalApprovalCoverageTests`; `WindowsLockConfirmationTests` (con un doble de la sesión,
    una segunda petición rechazada mientras otra espera, y una de `LocalTools` que lee la sesión real sin bloquearla);
    `PresencePolicyTests` con `helloAvailable` y la regla por requisito; `PlanApprovalHelloTests`; `PresenceBackupTests` (el
    motor); las de `DecisionRecorderTests` sobre aceptar un historial; y la de skills que deja su `ApprovalGranted`. Ninguna
    pide el Windows Hello real ni bloquea la sesión. Los arneses de las pruebas del motor y de integración aprueban con
    Windows Hello lo que tiene escritores, como lo hace el lienzo.
  - **Medido en tu equipo (Windows 11, compilación 26300):** con la sesión bloqueada (`LogonUI.exe` en la sesión 1),
    `WindowsSessionState.Read` da `Locked`: la disposición de `WTSINFOEXW` y el valor 0 de `SessionFlags` son los esperados.
    La lectura desbloqueada y el desbloqueo con PIN tras `LockWorkStation` quedan para tu demostración.
  - **Residuales:**
    - **la pantalla de bloqueo de Windows no dice qué se aprueba:** quien desbloquea confirma que es la persona, no qué
      acción. Lo mitiga que solo haya una confirmación a la vez y que el diálogo de la app lo diga antes de bloquear;
    - un lienzo compuesto sin `ReinforcedConfirmation` (nunca en la app) aprueba con el clic, como antes de la Fase 8; el
      motor lo frena si el plan tiene escritores;
    - la puerta se fía del método que registra el diálogo: es código de la app, no de un agente;
    - el residual 1 de ADR-0018: un programa de tu sesión puede actuar sin pasar por Agent Studio;
    - entre comprobar si hay Hello y verificar, el resultado puede cambiar: entonces se deniega.
  - **Pendiente fuera de WP5:** los caminos nuevos de D-39 que guardan una plantilla con una capacidad de alto riesgo
    (`GrantHighRiskCapability`) o un perfil de verificación (`ChangeVerificationProfile`), entre ellos «Usar perfil propuesto»
    del lienzo y el configurador (WP6 y WP7), tienen que llamar a `ReinforcedConfirmation.PlanAsync` con su acción,
    `ConfirmAsync` tras un «sí» y `Approves`. El aviso de la bandeja «La entrega espera tu Windows Hello» (D-41) es de la
    integración.
