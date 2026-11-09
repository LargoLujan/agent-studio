# ADR-0049 · El analizador del montaje y el orden: reglas deterministas con arreglos tipados; las notas de orden y «Revisar el orden» solo sugieren, y decide el validador

- **Estado:** propuesto · 2026-11-06. Esqueleto del P0 de la Fase 13; lo completa **WP1** (analizador, orden y chat con el
  orquestador). Recoge D-18 a D-21 y D-44 de [docs/17 §4.4 y §4.10](../17-constructor-visual-de-agentes.md#44-análisis-en-vivo-y-orden-entre-agentes)
  y la decisión 3 (a) que aceptaste el 2026-11-06. **Amplía** [ADR-0013](0013-protocolo-del-orquestador.md) y
  [ADR-0031](0031-auditor-determinista-del-orquestador.md).
- **Contexto:**
  - P10 pide decidir cuántos agentes y que el orquestador compruebe el orden, lo dañino y lo que puede ir en paralelo.
  - Hoy `PlanValidator` valida en cada cambio (unos 40 códigos), sin arreglos ni explicación del orden, y no hay deshacer.
- **Decisión** (la de docs/17; WP1 la detalla aquí):
  1. **El analizador del montaje** (D-18) es determinista, gratis e instantáneo. Lee el borrador (`MontageDraft`: equipo, plan,
     contexto del plan, opciones, contexto de cada tarea, cuota, notas de orden, catálogo y lo aprendido) y devuelve
     hallazgos (`Finding`: código, gravedad, nodos, argumentos y un `Fix` tipado). Envuelve los códigos del validador sin
     duplicarlos (`validator.<código>`) y añade los 16 de D-18 (`FindingCodes`). Cada regla es un `IMontageRule` en
     `Domain/Orchestration/Analysis/Rules/`. Los tipos son del P0; las reglas y el analizador que las junta, de WP1.
  2. **Cada arreglo es una edición que se deshace;** uno que cambia la plantilla avisa (`Fix.ChangesRoster`) y uno que concede
     una capacidad de alto riesgo pide Windows Hello (`Fix.GrantsHighRiskCapability`, ADR-0053).
  3. **Las notas de orden** (D-19) van dentro del plan (`orderNotes`, `OrderNotes` en el P0), sin ejecución extra, y el esquema
     sigue siendo portable y de 16 KB como mucho.
  4. **«Revisar el orden»** (D-20) es una auxiliar de solo lectura con tu OK (`AgentRunPurpose.OrderReview`). Sus sugerencias
     vuelven a pasar por `TaskGraph` y se ofrecen como arreglos; nada se aplica solo.
  5. **El analizador no sustituye a nadie** (D-21): el validador decide si se puede aprobar.
  6. **El chat de preparación** (D-44): el orquestador, de solo lectura, con tu OK por mensaje
     (`AgentRunPurpose.PreparationChat`); sus sugerencias nunca se aplican solas.
- **Alternativas:** las de la decisión 3 (sin notas en el esquema; solo el analizador). WP1 añade las que descarte.
- **Consecuencias:** pendientes de WP1 (tamaño medido del esquema con las notas, coste de cada regla y su prueba, F13-9 y
  F13-10).
