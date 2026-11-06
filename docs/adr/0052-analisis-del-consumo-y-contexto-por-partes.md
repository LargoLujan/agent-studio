# ADR-0052 · El análisis del consumo, determinista siempre y con un agente opcional del vendor que no produjo; el contexto por partes

- **Estado:** propuesto · 2026-11-06. Esqueleto del P0 de la Fase 13; lo completa **WP3** (contexto, estimación y análisis del
  consumo). Recoge D-13 y D-34 a D-36 de [docs/17 §4.2 y §4.8](../17-constructor-visual-de-agentes.md#48-análisis-del-consumo),
  la decisión 5 (a) que aceptaste el 2026-11-06 y el estimador de F9-7, que se adelanta. **Amplía**
  [ADR-0019](0019-consumo-como-proyeccion-del-registro.md) y [ADR-0021](0021-salud-del-contexto-por-paso.md).
- **Contexto:**
  - P6 pide un análisis (un botón) de si el contexto fue escaso o algo se consumió sin sentido. P5 pide el contexto por partes.
  - Hoy hay consumo por intento con seis orígenes, pico y compactaciones, y el auditor de 9 códigos; no hay desglose por
    partes, ni estimador, ni la ventana aprendida en vivo (docs/17 §0.2).
- **Decisión** (la de docs/17; WP3 la detalla aquí):
  1. **El contexto por partes** (D-13): Agent Studio sabe en caracteres lo que pone en el prompt antes de lanzar (protocolo,
     requisitos, ficha, instrucciones, memoria, otras tareas, intento anterior, respuesta y skills). Cada parte es un segmento,
     estimado por Agent Studio; lo que el CLI informa de más es la conversación. Se guarda en `AgentRunStarted` versión 3
     (`ContextParts`, `ContextPartSnapshot`, `ContextPartKind`; P0), solo cuando lo hay. La barra lo pinta con sus partes
     (`MetricBarReading.Segments`, P0).
  2. **El análisis determinista** (D-34): siempre y gratis, al terminar cada ejecución y bajo demanda, sobre el registro
     verificado (`IConsumptionAnalyzer`, `ConsumptionReport`, `ConsumptionFinding` y los 12 códigos de `ConsumptionFindingCodes`;
     P0). Cada hallazgo dice lo que costó con su origen, nunca un ahorro inventado.
  3. **El análisis con un agente** (D-35): opcional, una auxiliar de solo lectura del vendor que no produjo
     (`AgentRunPurpose.ConsumptionAnalysis`), sin memoria y en una carpeta vacía, con tu OK cada vez
     (`IAssistedConsumptionAnalysis`; P0). Lo que estima el agente sale como «lo dice el agente».
  4. **Fuera del juicio** (D-36): el juez no recibe el consumo.
  5. **El estimador** (docs/16 D-20, adelantado): `IUsageEstimator` (P0) da intervalos del percentil 25 al 75 con su origen,
     sus referencias y sus supuestos; «desconocida» con menos de tres. Lo usan la recomendación de modelos, el analizador
     (`cost.over-budget`) y el coste de cada opción de una pregunta.
  6. **La ventana aprendida en vivo:** WP3 conecta `ModelWindowCatalog` en la ejecución (hallazgo de §0.2).
- **Alternativas:** el análisis dentro de la ejecución del juez (decisión 5 (b)); solo el determinista (decisión 5 (c)).
- **Consecuencias:** pendientes de WP3 (umbrales de cada código, F13-6 y F13-15).
