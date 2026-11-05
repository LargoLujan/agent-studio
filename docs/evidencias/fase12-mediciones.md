# Fase 12 · Mediciones (2026-11-04 y 2026-11-05)

Evidencia de los criterios F12-4, F12-5 y F12-9 ([08](../08-plan-y-criterios-de-aceptacion.md), [17](../17-constructor-visual-de-agentes.md) §9.2,
[ADR-0047](../adr/0047-indice-semantico-local.md)). Rutas del equipo sustituidas por `%TEMP%`, `%LOCALAPPDATA%` y `%USERPROFILE%`.

## F12-4 · Búsqueda híbrida frente a FTS5

Criterio cambiado el 2026-11-04 por decisión del usuario: nunca peor que FTS5 en ningún tipo de pregunta y al menos 10
puntos más en total con preguntas nuevas. Métrica: la respuesta entre las 5 primeras. Modelo real (multilingual-e5-small,
int8).

| Conjunto | FTS5 como hoy | Híbrida | Puntos |
|---|---|---|---|
| Calibración (50 originales, sintético) | 26/50 | 41/50 | +30 |
| Validación ciega final (60 nuevas, sintético; quien las escribió no calibró) | 35/60 | 43/60 | +13,3 |
| Bitácora real de este repositorio (40 preguntas, 56 entradas del 15-10 al 04-11) | 17/40 | 23/40 | +15 |

Por tipo en la validación ciega: paráfrasis 8/30 → 14/30, identificador con significado 15/15 → 15/15, metadatos 12/15 →
14/15. Por tipo en la bitácora real: paráfrasis 1/20 → 7/20, identificador con significado 7/8 → 7/8, metadatos 9/12 →
9/12 (empate exacto en los dos últimos: una pregunta que cambie de lado rompería el criterio). Las mejoras que salieron de
la bitácora real están en [18](../18-peticiones-y-pendientes.md), P40. Las preguntas sobre la bitácora real no se
versionan porque citan los prompts.

## F12-5 · Velocidad de la búsqueda de la app

`SemanticIndexEndToEndMeasurementTests` en Release, con el equipo en reposo: 10 000 entradas de confianza grabadas por
el camino del producto (7 500 de memoria y 2 500 de bitácora), el almacén y el modelo reales, 300 consultas.
`SearchAsync` p95 75 ms (objetivo < 200 ms); un paquete con sus tres niveles, p95 92 ms. Antes de las cachés de la memoria
y del almacén, p95 606 ms. El informe completo: [fase12-f12-5-medida.txt](fase12-f12-5-medida.txt).

## F12-9 · La herramienta del historial en un lanzamiento real

`ProjectHistoryLaunchLiveTests` con Claude Code 2.1.290 y haiku, por el lanzamiento del planificador: solo el servidor
`agentstudio`, sus herramientas de lectura más `search_project_history`, una llamada que responde con el fragmento sembrado,
nada escrito y la instantánea borrada. 5 turnos y 0,047 USD (D-30 en la Fase 4: 5 turnos y 0,034 USD). El registro:
[fase12-f12-9-evidencia.log](fase12-f12-9-evidencia.log).
