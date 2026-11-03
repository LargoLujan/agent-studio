# ADR-0027 · Independencia del juez en los debates con dos vendors: la regla de los productores no se dispensa; la de los participantes, solo de forma explícita y registrada (adenda de ADR-0006)

- **Estado:** aceptada · 2026-10-19. Recoge D-12 a D-15 y D-26 de [docs/15](../15-fase-5-debate-arena-y-juez.md#43-independencia-del-juez-con-dos-vendors)
  y la decisión 1 de su §12, opción (a). Es una adenda de [ADR-0006](0006-juez-independiente-verificable.md) para los
  debates: ADR-0006 y [ADR-0014](0014-independencia-del-juez-con-dos-vendors.md) no cambian. El paquete P0 deja las reglas en
  el dominio (`DebateJudgingRules`, `JudgingArrangement`, `JudgingPanel`) y la comprobación de la identidad del juez en
  `DebateSession.Close`; WP1 elige la disposición al abrir y WP2 juzga.
- **Contexto:**
  - ADR-0006 pide un juez de un vendor distinto de todos los participantes, y F5-1 pide que la configuración admita
    participantes de proveedores distintos. Con dos vendors (Anthropic y OpenAI; Gemini está aparcado,
    [ADR-0009](0009-google-aparcado.md)) un solo juez no puede cumplir las dos cosas.
  - La literatura sobre jueces LLM documenta la autopreferencia y el sesgo de posición, y que un jurado de familias distintas
    tiene menos sesgo propio (docs/15 §3.3). No mide a Claude ni a Codex en esta aplicación.
  - `AgentJudge` pone en el veredicto el modelo que anuncia la sesión del CLI: en Claude es el nombre completo
    (`claude-haiku-4-5-…`), no el alias configurado (`haiku`). `DebateSession.Close` de la Fase 0 exigía el mismo modelo, así
    que el veredicto real de un juez de Claude nunca habría cerrado un debate.
- **Decisión:**
  1. **Regla P, de los productores** (ADR-0014): ningún juez corre en un vendor que produjo el objeto que se juzga, cuando lo
     hay: en una ejecución, sus productores (`RunLog.ProducerVendors`); en una auditoría de proyecto, los del registro y los
     autores que declares. **Nunca se dispensa**: una dispensa de un productor es una violación (`debate.waiver-of-producer`).
  2. **Regla Q, de los participantes** (ADR-0006, decisión 1): ningún juez corre en el vendor de un participante salvo que ese
     vendor esté dispensado. La dispensa es explícita: va en la cabecera, en `DebateOpened` y en tu aprobación, y la tarjeta
     del veredicto la enseña.
  3. **Las familias.** Un debate elige entre sus contribuciones (libre, adversarial, revisión de arquitectura y tribunal, sin
     objeto o sobre un commit) o revisa un trabajo que produjeron otros (auditoría de implementación, revisión del orquestador,
     un tribunal sobre una ejecución). En los de elección las contribuciones son el trabajo, así que Q es allí la regla de sus
     productores. Una impugnación se juzga como el objeto del veredicto que impugna (D-39), que el dominio no ve: se le ofrecen
     las dos familias, la elige el servicio de impugnación, y P y la forma de cada disposición se comprueban igual.
  4. **Tres disposiciones** (`DebateJudgingKind`), que `DebateJudgingRules.Possible` calcula con el modo, el objeto, los
     participantes, los productores y los jueces de la plantilla, la de por defecto primero:
     - **Juez independiente**, por defecto: uno, de un vendor que no está en P ni entre los participantes, sin dispensa.
     - **Jurado cruzado**, solo en los de elección y con participantes de varios vendors: un juez de cada vendor de los
       participantes, con Q dispensada para todos ellos.
     - **Juez no productor**, solo en los de revisión: uno, de un vendor de los participantes que no produjo el trabajo, con Q
       dispensada solo para ese vendor.

     `DebateJudgingRules.Violations` da los códigos estables de lo que incumple una disposición (`debate.judge-producer`,
     `debate.judge-participant-not-waived`, `debate.panel-shape`, `debate.panel-in-review`, `debate.non-producer-shape`,
     `debate.non-producer-in-choice`…), y `DebateSession.Open` no abre un debate con ninguno.
  5. **Lo que se excluye.** `JudgingArrangement.ExcludedVendors` = P ∪ (vendors de los participantes − dispensados). Es el
     `ExcludedVendors` del dossier, el que usan el validador (`JudgeNotIndependent`) y el coordinador, que no lanza un juez
     excluido, como la revisión de la Fase 3.
  6. **El jurado cruzado no recupera la independencia: es una dispensa con desempate humano.** `JudgingPanel.Combine`:
     - los veredictos de todos los jueces, válidos, que repiten la misma cabeza del registro y los mismos requisitos y
       coinciden en la decisión y en la propuesta elegida, deciden, con la menor de las confianzas;
     - dos cabezas (o dos conjuntos de requisitos) distintas, un juez sin veredicto o un veredicto de otro son un conjunto
       inválido, nunca un acuerdo;
     - cualquier otra cosa (una discrepancia, un veredicto inválido) es «no resuelto» y decides tú.
  7. **La identidad del juez se compara por agente, proveedor y vendor, nunca por modelo** (`JudgingPanel.SameJudge`, D-26).
     `DebateSession.Close` recibe lo que decidió `Combine` (o tu decisión, «Decidir yo») y los veredictos válidos, y comprueba
     en cada uno el juez y el conjunto de requisitos, que cada juez esté una sola vez y que todos repitan la misma cabeza del
     registro.
- **Alternativas:**
  - **Mezclar con un solo juez de uno de los dos vendors, a ciegas:** barato, pero el juez decide solo sobre la propuesta de su
    propio vendor, y el validador no frena el sesgo en los criterios de valoración.
  - **No mezclar nunca:** F5-1 dejaría de cumplirse.
  - **Un tercer vendor (Gemini con API key):** independencia completa, con coste por uso y la privacidad del nivel gratuito;
    ADR-0009 lo aparca. El diseño lo admite sin cambios: con un tercer vendor, el juez independiente vuelve a ser posible.
  - **Comparar también el modelo:** con Claude ningún veredicto real cerraría un debate.
- **Consecuencias:**
  - Un debate por defecto no enfrenta a Claude con Codex: los participantes comparten vendor, con modelos distintos, y juzga
    el otro. Mezclar es posible, pero nunca a escondidas.
  - El jurado añade un juez y da más «no resuelto»: es un resultado legítimo.
  - Si los dos vendors produjeron el trabajo, nadie puede juzgarlo (P no se dispensa) y la revisión o la auditoría lo dicen.
  - La prueba de la Fase 0 que exigía el mismo modelo cambia en ese sentido: el mismo agente en el mismo proveedor y vendor
    con otro modelo cierra el debate; otro proveedor u otro vendor, no.
