# ADR-0050 · Modelos y potencia: un catálogo local sin precios, lo aprendido del registro, el esfuerzo en la cabecera y una recomendación determinista que dice por qué

- **Estado:** propuesto · 2026-11-06. Esqueleto del P0 de la Fase 13; lo completa **WP2** (modelos). Recoge D-22 a D-25 de
  [docs/17 §4.5](../17-constructor-visual-de-agentes.md#45-modelos-y-potencia).
- **Contexto:**
  - P7 pide elegir el modelo y la potencia de cada agente, con una recomendación según el proyecto.
  - Hoy el modelo es texto libre (`CliArgumentGuard`), no hay esfuerzo ni catálogo, y las ventanas aprendidas del registro no
    se usan en vivo (docs/17 §0.2). No hay tabla de precios, y no la habrá (docs/14 D-06, ADR-0019).
- **Decisión** (la de docs/17; WP2 la detalla aquí):
  1. **La potencia es nivel, esfuerzo y ventana, cada uno con su origen** (D-22). El nivel se dice con palabras
     (`ModelTier`: ligero, equilibrado, máximo; P0).
  2. **El catálogo local** (D-23) es un JSON versionado dentro de la app (`models.catalog.json`), por CLI y versión probada,
     sin precios (`ModelCatalog`, `ModelCatalogEntry` y `ModelRecommendationRules`; P0). Su lector es de WP2
     (`Infrastructure/Models`), y lo aprendido del registro, también (`Observability/Models`). Un modelo que no clasifica sale
     «sin clasificar».
  3. **La recomendación es determinista y dice por qué** (D-24): entradas y resultado en el P0 (`ModelRecommendationInput`,
     `ModelPerformance`, «desconocido» con menos de tres referencias, y `ModelRecommendation` con su código de razón). Las
     reglas (`ModelRecommender`) son de WP2.
  4. **El esfuerzo va en la cabecera** (D-25): `AgentDefinition.Effort` (`ReasoningEffort?`, de una lista cerrada; P0). Entra
     en `RosterHash` y se guarda en la plantilla solo cuando se fija, así que el hash de las plantillas y las cabeceras
     guardadas no cambia (pruebas con los valores del 2026-11-06). Claude recibe `--effort` y Codex
     `-c model_reasoning_effort=…` (`ReasoningEfforts.Token`; el lanzamiento es de WP2), nunca texto libre.
- **Alternativas:** una tabla de precios propia (descartada, docs/14 D-06); pedir la recomendación a un agente (no añade nada y
  gasta cuota); la lista de modelos de Codex por su app-server (`model/list`, aplazada hasta medirla).
- **Consecuencias:** pendientes de WP2 (versión probada de cada CLI, qué esfuerzos admite cada modelo, F13-11).
