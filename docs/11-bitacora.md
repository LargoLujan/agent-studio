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
   extracto de 160 caracteres. El extracto y los tags salen de lo que escribiste, sin los bloques que el sistema pone
   alrededor ([§9](#9-el-extracto-del-hook-sin-los-bloques-del-sistema)). Nunca bloquea el prompt. `CLAUDE.md` pide a
   Claude añadir la línea «Hecho:» al cerrar cada petición.

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

El extracto del prompt queda en el repositorio, que es público. No escribas secretos en los prompts. Lo que pegas no se
copia: desde el 2026-11-06, el hook pone «[pegado]» en su lugar
([§9](#9-el-extracto-del-hook-sin-los-bloques-del-sistema)). El decorador del producto registra el resumen de la tarea
(`Purpose`), no el prompt completo.

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
| Ancla de la línea «Resultado» (Fase 12) | P0 de la Fase 12: el formato, el escritor y su paso por el motor, las auxiliares, los debates y el juez, con tests. WP1 añadió la entrada con su día (`AAAA-MM-DD/P-NNNN`) y el recibo en `AgentRunFinished`, y la derivación lo exige; integrado en la rama de la Fase 12 ([§8](#8-la-línea-resultado-nombra-su-arranque-fase-12)) |
| La bitácora como memoria (Fase 12) | WP1 la ancla como `JournalEntry`, con su confianza; WP4 pone en «Memoria» la búsqueda y la decisión de confiar en ella, por días o para todo el proyecto. Integrado en la rama de la Fase 12, con tests; pendiente tu demostración |
| Líneas propias de la app bajo el título (tanda 2, V31) | Integrado el 2026-11-03, con tests del escritor y del resumen diario; pendiente de verificar en vivo |
| «Bitácora de hoy» como documento legible (tanda 2, V31) | Integrado el 2026-11-03: títulos, listas, negrita y código, sin las barras invertidas del escapado. Un día largo enseña sus entradas más recientes, y «Ver todo», el día completo como texto. Pendiente de verificar en vivo ([docs/03 §2](03-pantallas-y-navegacion.md#2-ventana-principal)) |
| Extracto del hook sin los bloques del sistema (P41) | Hecho el 2026-11-06, con pruebas del hook en pwsh y en Windows PowerShell (`BitacoraHookTests`, categoría `LocalTools`, fuera de la CI). Las entradas anteriores no se tocan ([§9](#9-el-extracto-del-hook-sin-los-bloques-del-sistema)) |

## 8. La línea «Resultado» nombra su arranque (Fase 12)

Decisión en [ADR-0047](adr/0047-indice-semantico-local.md) (docs/17 D-02), con su adenda del 2026-11-03. Desde la Fase
12, la bitácora entra en la memoria del proyecto como `JournalEntry`. Para saber qué escribió Agent Studio, el escritor del
producto termina la línea «Resultado» con el `AgentRunStarted` del lanzamiento y con la entrada de la que es el resultado:

```markdown
- **11:42:07** · P-0007 · ejecutor · claude-code/sonnet · #prompt #orquestacion · «20261015-a1b2c3 · T-12 · intento 1: Tarea T-12» (tarea T-12 · entrada 2026-10-15/P-0007)
  - Resultado P-0007 · 11:44:31 · Succeeded · 144,2 s · tokens 18.300 → 2.150 (exactos) · ejecución `20261015-a1b2c3` · tarea `T-12` · intento 1 · entrada `2026-10-15/P-0007`
  - Resultado P-0001 · 00:00:12 · Failed · 3,0 s · tokens no disponibles · boom · ejecución `20261015-a1b2c3` · intento 2 · entrada `2026-10-15/P-0042`
```

- **El ancla va al final**, detrás de la nota: `` · ejecución `<ejecución>` ``, después `` · tarea `<tarea>` `` si el
  arranque tiene tarea, después `` · intento <n> `` y, por último, `` · entrada `AAAA-MM-DD/P-NNNN` ``. La ejecución y la
  tarea se escriben con `Uri.EscapeDataString`, así que cualquier valor se recupera tal cual.
- **La entrada lleva su día.** Un resultado va al fichero del día en que termina el agente: después de medianoche está en
  el del día siguiente, cuyos `P-NNNN` son otros (la segunda línea del ejemplo es el resultado, escrito pasada la medianoche,
  de la entrada P-0042 del día anterior). La línea de la entrada también lo dice, al final del paréntesis:
  `(… · entrada AAAA-MM-DD/P-NNNN)`. La derivación junta el resultado con su entrada, y el resumen diario lo cuenta como un
  resultado del día en que se escribe, sin atribuírselo a la entrada de ese día que tenga el mismo número.
- **La nota no puede imitarla:** el escritor cambia los acentos graves de la nota, y el ancla los necesita alrededor de la
  ejecución. La lee y la escribe una sola pieza, `JournalResultAnchor` (`Format(ancla, entrada)` y `TryParse`).
- **El recibo.** El fichero está versionado y cualquiera puede copiar un final de línea, así que el ancla sola no prueba
  nada. Al escribir la línea «Resultado» de un lanzamiento anclado, el escritor da el recibo de la entrada
  (`JournalReceipt`): su día, su número y el SHA-256 de su texto exacto, su línea y su línea «Resultado» unidas por un salto
  de línea. Quien registra el final del lanzamiento (el motor, las ejecuciones auxiliares, los turnos de debate y el juez)
  lo guarda en su `AgentRunFinished`, solo si existe.
- **Qué es de confianza.** Una entrada solo tiene origen `System` si es del producto, con una sola línea «Resultado» que la
  nombra a ella, y el `AgentRunFinished` de esa ejecución, tarea e intento, en una ejecución propia del registro, tiene el
  recibo con su día, su número y el hash de su texto. Copiar el final en otra entrada, o cambiar la entrada antes o después
  de anclarla, da otro hash. Si dos entradas reclaman el mismo lanzamiento, ninguna lo consigue. Las líneas «Hecho» y lo
  demás que va debajo de una entrada del producto son una entrada aparte (`… · notas`), de origen `Agent`.
- **El resumen diario no cambia:** su línea «Resultado» ya admitía un añadido. Una prueba lo comprueba con la bitácora de
  ejemplo y con un día escrito por el propio escritor, con el ancla y sin ella.
- **Las entradas sin ancla o sin recibo** son las de antes de la Fase 12, las del hook, las de un lanzamiento que nadie
  anunció (una sonda) y las de un lanzamiento que no llegó a escribir su final. Como memoria quedan en cuarentena hasta que
  las aceptes, una a una, por días o con «Confiar en la bitácora de este proyecto», en «Memoria»
  ([docs/03 §5.3](03-pantallas-y-navegacion.md#53-memoria-del-proyecto-y-resumen-del-día-documento-memoria)).

## 9. El extracto del hook, sin los bloques del sistema

Decisión en la adenda del 2026-11-06 de [ADR-0008](adr/0008-bitacora-obligatoria.md) (P41 de
[docs/18](18-peticiones-y-pendientes.md)).

**El problema.** Claude Code y los programas desde los que lo usas (la app de escritorio, el navegador, el editor) meten
bloques propios en el texto del prompt, delante o alrededor del tuyo. El hook los copiaba tal cual y después recortaba a
160 caracteres, así que el bloque ocupaba el extracto y tu texto no se veía. Pasó en cuatro entradas: 15-10 a las
15:41:35 y a las 15:50:03, 16-10 a las 08:16:43 y 30-10 a las 09:22:47. Se quedan como están, porque la bitácora es
append-only.

**Desde el 2026-11-06**, el hook quita esos bloques antes de hacer el extracto:

- **Qué bloques.** Una lista cerrada: `<browser_instruction>`, `<system-reminder>`, `<ide_opened_file>`,
  `<ide_selection>` y `<pasted_content>`. Un bloque nuevo hay que añadirlo al hook.
- **Con su contenido.** Cada apertura va con el cierre más cercano de su nombre, sin otra apertura del mismo nombre en
  medio. Así, lo que escribes entre dos bloques se queda.
- **Sin cierre, no se quita nada.** Si falta el cierre, no se sabe dónde acaba el bloque. La etiqueta y todo lo que la
  sigue se quedan, para no comerse tu texto.
- **Lo pegado no se copia.** Lo que pegas (`<pasted_content>`) no es tu texto y puede contener cualquier cosa, y la
  bitácora es pública. En su lugar va «[pegado]». Si solo pegaste, el extracto es «[pegado]».
- **Un pegado sin cierre no deja pasar nada.** Es la excepción a la regla anterior: desde su etiqueta hasta el final del
  prompt, todo pasa a «[pegado]», porque lo que sigue puede ser lo pegado.
- **Si no queda nada**, el extracto es «(solo bloques del sistema)». «(prompt vacío)» queda para un prompt vacío de
  verdad.
- **Los tags y la fase** se calculan sobre el texto limpio: lo pegado o un recordatorio ya no añaden `#git` o `#tests`.
- **Lo que no es tuyo sigue sin registrarse:** la respuesta de un subagente (`<agent-message>`) y los avisos
  (`<task-notification>`, `[SYSTEM NOTIFICATION`). Ahora se reconocen también detrás de un bloque.
- **Un `<system-reminder>` al principio ya no descarta el prompt.** Antes, un prompt que empezaba así no se registraba.
  Ahora se registra lo que escribiste detrás. Si detrás no hay nada, sigue sin registrarse: es un aviso del sistema.

| Lo que llega al hook | Extracto |
|---|---|
| `<browser_instruction>[Browser disconnected…]</browser_instruction>` y debajo «los cinco agentes revisan la rama» | «los cinco agentes revisan la rama» |
| «Revisa el plan `<system-reminder>…</system-reminder>` y luego dime» | «Revisa el plan y luego dime» |
| «Mira esto: `<pasted_content id="…">…</pasted_content>` y dime qué pasa» | «Mira esto: [pegado] y dime qué pasa» |
| Solo `<pasted_content>…</pasted_content>` | «[pegado]» |
| Solo `<browser_instruction>…</browser_instruction>` | «(solo bloques del sistema)» |
| «`<browser_instruction>`sin cierre, sigue con la prueba» | «`<browser_instruction>`sin cierre, sigue con la prueba» |
| «Mira esto: `<pasted_content id="…">`clave y más texto», sin cierre | «Mira esto: [pegado]» |

**Límites.**

- Si escribes en el prompt una apertura y un cierre de esos nombres, por ejemplo al hablar del propio hook, lo que va
  entre ellos se quita.
- Si escribes `<pasted_content>` sin cierre, lo que va detrás pasa a «[pegado]».

**Pruebas.** `BitacoraHookTests` (categoría `LocalTools`, fuera de la CI) lanza el hook como Claude Code, en pwsh y en
Windows PowerShell. Cubre un bloque al principio, uno en medio, varios, uno sin cierre, algo pegado, algo pegado sin
cierre, solo lo pegado, solo bloques, un recordatorio delante de tu texto y los mensajes del sistema que no se registran.
