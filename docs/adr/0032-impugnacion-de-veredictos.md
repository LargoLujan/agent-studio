# ADR-0032 · Impugnación: un debate de revisión que hereda quién puede juzgar y cambia de juez; sus efectos en la entrega

- **Estado:** aceptada · 2026-10-20. Recoge D-38 a D-40 de [docs/15](../15-fase-5-debate-arena-y-juez.md#49-f5-7--impugnar-un-veredicto)
  y F5-7. WP3 implementa `VerdictChallengeService` (`IVerdictChallenges`) y `ChallengeLog` en el módulo Debate
  (`Challenges/`). El coordinador de debates (WP1) abre la revisión, el juicio de debates (WP2) guarda sus veredictos y la
  entrega (WP5) aplica sus efectos. La adenda de ADR-0012 sobre la entrega es de WP5.
- **Contexto:**
  - Un veredicto guardado puede estar equivocado, y el juez de la Fase 3 no tiene segunda instancia.
  - Con dos vendors, añadir el vendor del juez original a los excluidos dejaría sin juez una ejecución de Claude.
  - `VerdictChallengedPayload.RevisionDebate` no admite nulo, y el id del debate lo genera el coordinador dentro de `OpenAsync`.
- **Decisión:**
  1. **Qué se impugna.** Cualquier veredicto guardado de una ejecución, un debate o una auditoría, válido o no, que el registro
     anuncia (`VerdictIssued`) en su unidad. Mientras una impugnación del mismo veredicto no se cierra, no se abre otra.
  2. **La revisión** es un debate adversarial sobre el veredicto (`DebateSubjectKind.Verdict`), con los requisitos del
     veredicto impugnado (por su hash) y **tus razones, literales, como pregunta**: la ronda 1 parte de ellas. El proponente
     defiende el veredicto y el crítico la impugnación.
  3. **Quién la juzga** (D-39):
     - **hereda los `ExcludedVendors` del objeto**, leídos del registro verificado: de una ejecución, `RunLog.ProducerVendors`;
       de un debate, sus productores ∪ (vendors de sus participantes − dispensados); de una auditoría, los productores ∪ los
       autores declarados. El vendor del juez original no se añade;
     - **se juzga como el objeto:** el veredicto de un jurado cruzado, con jurado cruzado; el de un debate que elige, con juez
       independiente o, si no cabe, jurado; el de un trabajo revisado (ejecución, auditoría), con juez independiente o, si no
       cabe, juez no productor;
     - **cambia el juez.** Los candidatos son los jueces y auditores de la plantilla (o los que ofrece la tarjeta), nunca en un
       vendor excluido, ordenados así: otros agentes primero, los del jurado original después y el que emitió el veredicto el
       último; a igualdad, otro modelo. Si solo queda el mismo agente, juzga en una sesión nueva y la propuesta lo anota;
     - el coordinador prepara con esa disposición y hereda lo mismo. La impugnación añade un problema si la propuesta no
       contiene los vendors heredados, sienta un juez en uno de ellos o no es jurado cuando el veredicto era de un jurado. Una
       propuesta con problemas no se abre.
  4. **El orden de lo que se escribe.** El contrato de P0 dice «`VerdictChallenged`, después el debate». No es posible: el payload
     necesita el id de la revisión. **Diferencia:**
     - primero `OpenAsync` del coordinador: la aprobación `debate.open` atada a la propuesta, `DebateOpened` con
       `Challenges` = el veredicto y el veredicto impugnado como `AgentStatement`;
     - después, tus razones como `HumanNote`/`ProvidedByHuman` de la persona que aprobó la apertura, capturadas con el id de la
       revisión y anunciadas en ella (`EvidenceRegistered` versión 2): así su juez las puede citar, y una fila con el id del
       debate que el debate no anuncia bloquearía su aprobación;
     - y, en la unidad del veredicto impugnado, `VerdictChallenged` (actor `User`) con el veredicto, el id de la revisión y las
       razones por su id y su SHA-256;
     - si no se puede anclar, la revisión se aborta.

     Si el proceso cae entre `DebateOpened` y `VerdictChallenged`, `ChallengeLog` cuenta igualmente como impugnación un debate
     abierto contra el veredicto (`Pending`). La entrega de WP5 lee solo `VerdictChallenged`: se le recomienda leer también
     `DebateOpened.Challenges`.
  5. **Los veredictos de la revisión los guarda el juicio de debates** (WP2, `DebateJudgment`): en la unidad y el ámbito del
     veredicto impugnado, con `Supersedes` = el impugnado, y anunciados con `VerdictIssued` en esa unidad. La impugnación no
     escribe ninguno: sería un duplicado. La cadena de veredictos de una unidad sigue siendo una.
  6. **Dónde está una impugnación** (`ChallengeStatus`), del registro verificado; la última impugnación es la que vale:
     - `None`: nunca se impugnó;
     - `Pending`: la revisión no abrió, sigue abierta, se abortó, cerró sin decisión (`disagree`, `invalid`) o sus veredictos
       no cuadran con el registro. Falla cerrado;
     - `DecidedByUser`: la cerraste tú («Decidir yo», regla `user`);
     - si la cerró su juez o un jurado de acuerdo, se compara con el impugnado, cuya aprobación cuenta solo si el almacén la
       guarda válida y el registro la anunció válida: aprobación confirmada, `Confirmed`; aprobación retirada, `Revoked`;
       vuelta hacia la aprobación, `AwaitingAcceptance` hasta tu aceptación y `Accepted` después; ni antes ni después una
       aprobación, `Confirmed`.
  7. **Aceptar una vuelta hacia la aprobación** (`AcceptRevisionAsync`): solo la última revisión que espera aceptación, y uno de
     los veredictos que la cerraron. La aprobación `verdict.revision.accept` (crítica) se pide en el debate de revisión y se ata
     a ese veredicto: SHA-256 canónico del proyecto, el impugnado, el debate, el veredicto y el hash de su `VerdictIssued`.
     Cuenta, como la lee la entrega, el sí de una persona (`ApprovalGranted` de un actor `User`) a una petición
     (`ApprovalRequested`) hecha después del cierre, en la unidad del veredicto o en el debate. Una denegación lo deja esperando.
  9. **Las acciones** (`verdict.revision.accept`, `debate.open`, `replan.dismiss`) son las constantes de `DeliveryService`
     (WP5). El módulo Debate no puede referenciar Orchestration (ADR-0002), así que guarda sus valores, y una prueba falla si
     alguna vez difieren. Si el integrador quiere una sola definición, el sitio es Application.
  8. **Efectos en la entrega** (D-40, WP5): una impugnación pendiente la bloquea, una aprobación retirada también, una vuelta
     hacia la aprobación solo cuenta aceptada y una confirmación la deja pasar. En un debate o una auditoría, el resultado
     vigente es el de la revisión y el original queda como «impugnado».
- **Alternativas:**
  - **Que la impugnación copie el veredicto de la revisión a la unidad original:** el juicio de debates ya lo guarda allí, como
    pide el contrato de `VerdictKeeper` de P0. Habría dos copias.
  - **Registrar `VerdictChallenged` antes de abrir, sin id de revisión:** la entrega no sabría qué revisión mirar y quedaría
    bloqueada para siempre.
  - **Guardar las razones en memoria entre preparar e impugnar:** se pierden al reiniciar. La pregunta ya las lleva.
  - **Excluir también el vendor del juez original:** con dos vendors nadie podría juzgar una ejecución de Claude.
- **Consecuencias:**
  - Tus razones son evidencia `ProvidedByHuman` de la revisión: su juez las puede citar, también en un criterio
    `HumanReview`. Son tus palabras, pero no una revisión del trabajo. No entran en el dossier de la ejecución original.
  - La impugnación de una revisión funciona igual: su veredicto vive en la unidad original, y el debate que lo dio se encuentra
    por `DebateClosed.Verdicts`.
  - Sin verificar: el coordinador (WP1), el juicio (WP2) y la entrega (WP5) reales con este servicio, de principio a fin. Las
    pruebas usan un doble del coordinador que registra lo que registra el real al abrir, y leen los estados con las mismas
    reglas que la entrega.
