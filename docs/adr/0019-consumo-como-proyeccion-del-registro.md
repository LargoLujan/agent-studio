# ADR-0019 · El consumo es una proyección del registro de decisiones: payloads v2, deduplicación por sesión y seis orígenes de cada cifra

- **Estado:** aceptada · 2026-10-18. Recoge D-01 a D-08 de [docs/14](../14-fase-4-observabilidad-y-memoria.md#41-f4-1-y-f4-2--consumo-y-exportación).
  El paquete P0 deja el dominio, los contratos, los payloads v2 y `AuxiliaryAgentRunner`; WP1 escribe la v2 en el motor y
  en el juez, y WP2 hace la proyección, la agregación y la exportación.
- **Contexto:**
  - F4-1 pide un panel de consumo por proveedor, modelo, sesión, proyecto y periodo, con su procedencia; F4-2, exportarlo
    a CSV y JSON.
  - El registro de decisiones ya guarda el uso de cada intento: `AgentRunFinished` v1 lleva un `UsageSnapshot` con la
    procedencia de cada cifra, encadenado por hash y anclado fuera del proyecto
    ([ADR-0004](0004-registro-de-decisiones-append-only.md), [ADR-0010](0010-estado-derivado-del-registro.md)).
  - Los CLIs informan acumulados, no el gasto de cada intento:
    - en Claude, `result.usage` suma los pasos de la ejecución, y una sesión reanudada incluye lo que gastó antes
      (documentado desde 2.1.277);
    - en Codex, `turn.completed.usage` es el total del hilo (código de 0.160.0).

    Los reintentos, la corrección del planificador y la del juez reanudan la sesión: sumar intentos contaría dos veces.
  - La procedencia de tres niveles (exacto, estimado, no disponible) no dice quién dio la cifra, ni distingue lo que un
    CLI no ofrece de lo que no llegó.
  - La 0001 trae `agent_runs` y `usage_records`, que no son append-only y nadie usa desde la Fase 3. El contrato
    `IUsageLedger.RecordAsync` de la Fase 0 suponía escribir el uso dos veces.
- **Decisión:**
  1. **El consumo es una proyección del registro verificado.**
     - `AgentRunStarted` y `AgentRunFinished` son el único registro del uso. No hay tabla de uso ni doble escritura:
       `IUsageLedger` y `UsageRecord` desaparecen, y la 0004 no crea ninguna tabla.
     - `IUsageReport` proyecta en memoria y lee cada registro con `IProjectLogReader`: solo lectura, sin cerrojo, sin
       migrar y sin mover anclas.
     - Un proyecto cuyo registro no verifica, o cuyo historial no tiene un ancla reconocida, se lista como excluido con su
       motivo (`ExcludedProject`). Nunca se mezcla.
     - Una caché, solo si la medición de WP2 con un registro grande la pide.
  2. **Payloads v2.** Los campos nuevos van al final y son opcionales, y `V` sube. Una entrada v1 se sigue leyendo y se
     reescribe byte a byte, así que su hash no cambia.
     - `AgentRunStarted` v2 añade el propósito (`AgentRunPurpose`: `Planner`, `Executor`, `Judge`, `SkillRecommendation`,
       `DailySummary`) y el paquete de memoria que recibió el agente (`ContextPackReference`,
       [ADR-0022](0022-memoria-append-only-anclada.md)).
     - `AgentRunFinished` v2 añade:
       - el uso con el origen de cada cifra, las escrituras de caché, los tokens de razonamiento y el desglose por modelo
         (`UsageSnapshot.WithBreakdown`, `ModelUsageSnapshot`);
       - `LaunchedAt`, la hora en que arrancó el proceso, después de la cola del proveedor: la duración se mide desde ahí;
       - `ReportedModel`, el modelo que informó el CLI;
       - `ResumedSession`, la sesión que reanudó el intento;
       - `ContextPeak`, el paso con más contexto;
       - `Compactions` (nulo es «no expuesto», nunca cero) y `Turns`.
     - Los escritores de la Fase 3 (`AgentRunner`, `JudgeRunRecorder`) siguen escribiendo la v1 con
       `UsageSnapshot.From` hasta que WP1 los pase a la v2.
  3. **Seis orígenes de cada cifra** (`MetricOrigin`): informado por el CLI, medido por Agent Studio, estimado por el
     CLI, estimado por Agent Studio, no expuesto y sin dato, de más fuerte a más débil.
     - La procedencia de tres niveles se deriva del origen, así que el código que la lee no cambia.
     - Los creadores que ya existían no cambian de significado: `Exact` es informado por el CLI, `Estimated` es estimado
       por el CLI y `Unavailable` es no expuesto, como decía su comentario. `Missing` es nuevo.
     - Una cifra v1 no lleva origen. Se lee por su procedencia (`MetricSnapshot.ResolvedOrigin`): todas venían de un
       analizador, con esos tres creadores.
  4. **Deduplicación por sesión** (D-02). Una sesión es la pareja (proveedor, `SessionId`).
     - Uso propio de un intento: su instantánea menos la del último intento anterior de la misma sesión que terminó con
       resultado.
     - Si alguna cifra baja, el CLI no acumuló: el intento cuenta su instantánea entera, «estimado por Agent Studio».
     - Total de la sesión: su última instantánea. Un intento sin resultado (interrumpido, cancelado, tiempo agotado) es
       «sin dato».
  5. **Agregados con cobertura** (`MetricAggregate<T>`): la suma comprobada de lo que hay, cuántos valores había y cuántos
     faltaban, y el origen más débil. Con huecos, la cifra sale «parcial (n de m)»; sin ningún valor no hay suma, nunca un
     cero.
  6. **Ejecuciones auxiliares** (D-08). La recomendación asistida de skills y el resumen narrativo pasan por
     `AuxiliaryAgentRunner`, que las registra como al juez:
     - el prompt como evidencia y `AgentRunStarted` v2 antes del lanzamiento;
     - `AgentRunFinished` v2 al terminar;
     - un `RunId` propio, `aux-yyyyMMdd-xxxxxx`, que ninguna orquestación puede tener y que no está en
       `orchestration_runs`: el panel lo cuenta y la lista de ejecuciones no lo enseña;
     - con un registro que no verifica no captura ni lanza nada.
  7. **Coste** (D-06). El de Claude es «estimado por el CLI (precio de lista)», con su `costBasis`; el de Codex, «no
     expuesto». Nunca hay una tabla de precios propia.
- **Alternativas:**
  - Una tabla de uso propia, escrita por un decorador del proveedor: dos escrituras que pueden divergir, en una tabla que
    no es append-only ni está anclada.
  - La bitácora: es texto para personas, con pérdidas, y es contenido del repositorio
    ([ADR-0018](0018-modelo-de-amenaza-fase-3.md), punto 1). Un repositorio de terceros traería cifras falsas.
  - Los ficheros de sesión de los CLIs: Agent Studio nunca lee sus carpetas.
  - Sumar los intentos: contaría dos veces lo que gastó una sesión reanudada.
  - Quedarse con tres niveles de procedencia: no separa «no expuesto» de «sin dato», ni el CLI de Agent Studio.
- **Consecuencias:**
  - Hay una sola fuente: lo que enseña el panel sale de la misma cadena que verifica el juez.
  - Cada consulta lee el registro entero. WP2 lo midió con un registro sintético grande, y el dato decidió que sí hace
    falta caché (adenda de abajo).
  - Una entrada v1 sale «sin desglose (registro anterior a la Fase 4)»: `UsageAttempt.RecordedVersion` lo dice.
  - Cambiar otra vez un payload exige subir `V`. `DecisionPayloadTests` fija el texto de la v1 (con un hash calculado
    con una implementación independiente de la forma canónica) y el de la v2.
  - La ocupación del contexto toma el origen más débil de sus dos cifras (`ContextHealthEvaluator`, ya en
    `Domain/Metrics`): una ventana vista en una ejecución anterior la hace «estimado por Agent Studio». La salud del
    contexto es ADR-0021.
  - `agent_runs` y `usage_records` siguen sin uso. Se pueden retirar más adelante con su propia migración.
  - **Sin verificar:** qué informan los acumulados al reanudar tras un intento matado (Claude 2.1.290) y con
    `codex exec resume`. La regla del punto 4 lo absorbe con «estimado» cuando una cifra baja; lo confirmará la primera
    ejecución real con reintento.

## Adenda (2026-10-18, integración de la Fase 4): la proyección se conserva mientras el registro no cambia

La medición de WP2 (`UsageProjectionMeasurementTests`, registro sintético, máquina compartida con otras suites):

| Entradas | Proyectar | Consulta | Reagrupar el mismo registro |
|---|---|---|---|
| 10 000 | 160 ms | 230 ms | 91 ms |
| 100 000 | 805 ms | 948 ms | 161 ms |
| 200 000 | 2,1 s | 2,0 s | 0,30 s |
| 500 000 | 7,5 s | 5,0 s | 1,0 s |

Un registro intenso de unos seis meses (unas 200 000 entradas) costaría segundos en cada consulta. Por eso:

- `UsageProjector` conserva las proyecciones de los últimos cuatro registros mientras no cambian (raíz, proyecto,
  longitud y hash de cabeza). **Leer el registro sigue siendo por consulta**: lo que se ahorra es volver a proyectar.
- Los payloads se leen por bloques de 8 192 entradas, en paralelo, siempre con `DecisionPayloadSerializer`.
- El cálculo va en un hilo del pool, nunca en el de la interfaz.

Sigue sin haber tabla de uso: la caché vive en memoria y se reconstruye desde el registro.
