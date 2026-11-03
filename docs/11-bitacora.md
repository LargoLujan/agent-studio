# 11 · Bitácora diaria de prompts

> Requisito del usuario (2026-10-15): un Markdown **obligatorio** por proyecto que registre **cada prompt que se
> lanza**, con un resumen breve de lo que se hace, **fecha y hora** y **tags de identificación**; **un fichero por
> día**, no uno único. Decisión en [ADR-0008](adr/0008-bitacora-obligatoria.md).

## 1. Dónde y cómo

- `<repo del proyecto>/.agentstudio/bitacora/YYYY-MM-DD.md`, según la **fecha local**.
- Se versiona con el proyecto (el `.gitignore` excluye la base de datos y las evidencias de `.agentstudio/`, no la
  bitácora).
- Append-only: nada se reescribe. Corregir es añadir una línea.

```markdown
# Bitácora · 2026-10-15

Registro automático de Agent Studio: una línea por cada petición a un agente (hora · número · quién · proveedor/modelo · etiquetas · resumen).
Cuando el agente termina, se añade una línea «Resultado» con el estado, la duración y los tokens.

- **11:42:07** · P-0007 · Backend · codex-cli/gpt-5 · #prompt #agente-backend #orquestacion · «Implementar el servicio de pedidos» (tarea T-12)
  - Resultado P-0007 · 11:44:31 · Succeeded · 144,2 s · tokens 18.300 → 2.150 (exactos)
- **11:45:02** · P-0008 · Juez · gemini-cli/gemini-2.5-pro · #prompt #juez #auditoria · «Auditar proyecto completo»
```

**Las dos líneas de debajo del título** las escribe quien crea el día, y cada escritor explica lo suyo (desde la tanda 2,
2026-11-03; V31 y adenda de [ADR-0008](adr/0008-bitacora-obligatoria.md)). Las de arriba son las de Agent Studio. El
hook de este repositorio escribe las suyas:

```markdown
Registro automático de cada prompt (hook `UserPromptSubmit`): hora · sesión · tags · extracto.
Al cerrar cada petición, Claude añade debajo una línea «Hecho:» con el resultado.
```

Antes de la tanda 2, la app también escribía estas dos del hook. Los días ya escritos así no se tocan.

## 2. Qué lo hace obligatorio

1. **En el producto:** todo prompt a un agente pasa por `IAgentProvider`, y la composición registra los proveedores
   **solo** envueltos en `JournalingAgentProvider`. El decorador escribe la entrada **antes** de lanzar el agente; si
   no puede escribirla, **el agente no se lanza** (fail closed). Al terminar (éxito, fallo o cancelación) añade el
   resultado con duración y tokens con su procedencia. El orquestador también es un agente, así que sus prompts
   quedan registrados igual.
2. **En el desarrollo de este repositorio:** el hook `UserPromptSubmit` de Claude Code
   (`.claude/hooks/bitacora.ps1`) registra cada prompt tuyo con hora, sesión, tags derivados por palabras clave y un
   extracto de 160 caracteres. Nunca bloquea el prompt. `CLAUDE.md` pide a Claude añadir la línea «Hecho:» al cerrar
   cada petición.

Ambos escriben el mismo título y conviven en el mismo fichero. Las dos líneas de debajo del título son las de quien crea
el día ([§1](#1-dónde-y-cómo)).

## 3. Tags de identificación

| Tag | Origen |
|---|---|
| `#prompt` | Siempre |
| `#agente-<id>` | Agente que recibe el prompt |
| `#fase-N`, `#docs`, `#tests`, `#git`, `#skills`, `#juez`, `#ui`… | Palabras clave (hook) o tags de la tarea (producto) |
| `P-NNNN` | Identificador de la entrada en el día; el resultado lo cita |

## 4. Relación con el resto de la memoria

- La bitácora es el **registro cronológico** que pide la sección 14 del prompt. El **temático** es la memoria del
  proyecto (`memory_entries` + exportación Markdown).
- El **registro de decisiones** (`decision_log`, encadenado por hash) sigue siendo la fuente de verdad para el juez;
  la bitácora es para personas: breve y legible.
- Los **resúmenes diarios** (Fase 4) se generan a partir de la bitácora del día, sin agente y sin cuota, y quedan en la
  memoria del proyecto como `DailySummary` ([§6](#6-resumen-diario-fase-4)).

## 5. Privacidad

El extracto del prompt queda en el repositorio. Es un repo privado y de uso personal; aun así, no pegues secretos en
los prompts. El decorador del producto registra el resumen de la tarea (`Purpose`), no el prompt completo.

## 6. Resumen diario (Fase 4)

Decisión en [ADR-0025](adr/0025-resumen-diario-determinista.md). Lo hace `DailySummaryService`
(`src/AgentStudio.Memory/Summary/`), detrás de `IDailySummaryService`.

- **Determinista.** Cuenta y cita; no interpreta ni lanza ningún agente, así que no gasta cuota. Los mismos bytes dan
  siempre el mismo resumen.
- **Lectura comprobada.** El día se lee con `IProjectStorageReader.OpenForRead`, con la misma política que las
  escrituras del almacén: una junction en `.agentstudio` o en `bitacora`, o un día que es un enlace duro de un fichero de
  fuera, se rechazan y no se resume nada. Leer no crea nada.
- **Los dos escritores.** Se reconocen las entradas del producto (`P-NNNN`) y sus líneas «Resultado», las del hook (con
  `sesión`, y también las escritas a mano sin sesión y con una palabra en lugar de la hora) y las líneas «Hecho» en todas
  sus formas (`Hecho:`, `Hecho (en curso):`, `Hecho (00:27, cierre):`…). Los títulos, las dos líneas de debajo y las
  líneas en blanco son parte del formato; de lo demás (notas, líneas rotas) solo se da el número. Desde la tanda 2, las
  líneas de debajo del título pueden ser las del hook (también las que escribía la app antes) o las nuevas de la app: se
  reconocen las cuatro, y ninguna cuenta como «sin reconocer».
- **Qué dice.** Los prompts por actor, proveedor y modelo; los resultados por estado y su duración total; los tokens,
  «exactos» solo si todos los prompts del producto dieron cifras exactas y «parcial» si no; los fallos y límites; las
  cinco etiquetas más frecuentes; y las líneas «Hecho», literales y escapadas. Todo lo que sale de la bitácora va
  redactado y escapado, y el resumen cabe en una entrada de memoria (4 000 caracteres): las «Hecho» que no caben se
  cuentan.
- **Días.** El día es el local.
  - El de hoy es **provisional**: se enseña y no se guarda.
  - Un día cerrado va a la memoria como `DailySummary`, de origen sistema, una vez por contenido (la fuente lleva el
    SHA-256 de los bytes del día). Si después se le añade una línea (una «Hecho» escrita tras medianoche), el resumen
    nuevo sustituye al anterior.
  - Un día sin bitácora no deja nada.
- **Dónde acaba.** En la memoria y en su exportación a Markdown. Nunca en un prompt: el empaquetador no incluye los
  `DailySummary` ([ADR-0023](adr/0023-empaquetado-de-contexto-por-niveles.md)).
- **Pruebas.** Una bitácora de ejemplo con los dos escritores, las variantes de «Hecho», líneas rotas y la cabecera
  (`tests/AgentStudio.Tests/Memory/Fixtures/bitacora-ejemplo.md`) se compara con un resumen esperado escrito a mano.

## 7. Estado

| Pieza | Estado |
|---|---|
| Hook de desarrollo en este repo | Implementado y probado con entradas de ejemplo (se activa desde el siguiente prompt) |
| `IPromptJournal`, `MarkdownDailyJournal`, `JournalingAgentProvider` | Implementado en este incremento, con tests |
| Registro obligatorio de proveedores reales a través del decorador | Fase 2, al llegar los adaptadores |
| Resúmenes diarios | Fase 4 (WP3): `DailySummaryService` y la lectura comprobada de la bitácora, con tests. El panel y la vista de «Bitácora de hoy» por la lectura comprobada son de WP5 |
| Ancla de la línea «Resultado» (Fase 12) | P0 de la Fase 12: el formato, el escritor y su paso por el motor, las auxiliares, los debates y el juez, con tests ([§8](#8-la-línea-resultado-nombra-su-arranque-fase-12)) |
| Líneas propias de la app bajo el título (tanda 2, V31) | Integrado el 2026-11-03, con tests del escritor y del resumen diario; pendiente de verificar en vivo |
| «Bitácora de hoy» como documento legible (tanda 2, V31) | Integrado el 2026-11-03: títulos, listas, negrita y código, sin las barras invertidas del escapado. Un día largo enseña sus entradas más recientes, y «Ver todo», el día completo como texto. Pendiente de verificar en vivo ([docs/03 §2](03-pantallas-y-navegacion.md#2-ventana-principal)) |

## 8. La línea «Resultado» nombra su arranque (Fase 12)

Decisión en [ADR-0047](adr/0047-indice-semantico-local.md) (docs/17 D-02). Desde la Fase 12, la bitácora entra en la
memoria del proyecto como `JournalEntry`. Para saber qué escribió Agent Studio, el escritor del producto termina la línea
«Resultado» con el `AgentRunStarted` del lanzamiento:

```markdown
  - Resultado P-0007 · 11:44:31 · Succeeded · 144,2 s · tokens 18.300 → 2.150 (exactos) · ejecución `20261015-a1b2c3` · tarea `T-12` · intento 1
  - Resultado P-0008 · 11:50:02 · Failed · 3,0 s · tokens no disponibles · boom · ejecución `aux-20261015-0a1b2c` · intento 1
```

- **Va al final**, detrás de la nota: `` · ejecución `<ejecución>` ``, después `` · tarea `<tarea>` `` si el arranque
  tiene tarea, y después `` · intento <n> ``. La ejecución y la tarea se escriben con `Uri.EscapeDataString`, así que
  cualquier valor se recupera tal cual.
- **La nota no puede imitarla:** el escritor cambia los acentos graves de la nota, y el ancla los necesita alrededor de la
  ejecución. La lee y la escribe una sola pieza, `JournalResultAnchor`.
- **El resumen diario no cambia:** su línea «Resultado» ya admitía un añadido. Una prueba lo comprueba con la bitácora de
  ejemplo y con un día escrito por el propio escritor, con el ancla y sin ella.
- **Las entradas sin ancla** son las de antes de la Fase 12, las del hook y las de un lanzamiento que nadie anunció (una
  sonda). Como memoria quedan en cuarentena hasta que las aceptes.
