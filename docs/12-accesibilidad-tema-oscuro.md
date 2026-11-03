# 12 · Accesibilidad del tema oscuro (criterio F1-6)

> 2026-10-15. Revisión del tema oscuro con la skill `accessibility-review` (WCAG 2.1 AA): contraste de texto, de
> componentes y de estados, y foco de teclado. El contraste lo comprueban tests que miden los **recursos reales** de la
> aplicación, no una copia de sus colores: un cambio de token que lo rompa hace fallar `dotnet test`. Lector de pantalla
> y mapa completo de teclado quedan para la Fase 7 ([§7](#7-fuera-de-alcance-hasta-la-fase-7)).

## 1. Resultado

**F1-6: Cumplido.** 291 pares de colores medidos (más 19 exentos y listados con su motivo): 50 mediciones por debajo
del umbral antes de la revisión, **0 ahora**.

| Zona | Pares exigidos | Exentos | Fallaban antes | Fallan ahora |
|---|---|---|---|---|
| Paleta (tokens de `Dark.xaml`) | 17 | 0 | 2 | 0 |
| Estilos de texto | 13 | 0 | 0 | 0 |
| Botones, en cada estado | 13 | 3 | 6 | 0 |
| Controles y selección | 21 | 2 | 8 | 0 |
| AvalonDock (pestañas, títulos, conmutador) | 42 | 8 | 7 | 0 |
| Editor (Monaco) | 153 | 5 | 16 | 0 |
| Terminal (xterm.js) | 23 | 1 | 5 | 0 |
| Foco de teclado | 9 | 0 | 6 | 0 |
| **Total** | **291** | **19** | **50** | **0** |

«Fallaban antes» cuenta también el estado de foco del botón primario (`PrimaryButton.focused`, 3,61:1), que ya no existe
como par porque el anillo de foco sustituyó al cambio de relleno: por eso la fila de botones tiene 13 pares exigidos y 6
mediciones previas bajo el umbral. Las cifras «antes» de Monaco, xterm.js y foco se calcularon con la misma fórmula
sobre los valores anteriores (apartado 2.3).

Capturas de la aplicación real (ventana principal con un fichero abierto y el terminal, mismo tamaño y escala):
[antes](evidencias/fase1-contraste-antes.png) y [después](evidencias/fase1-contraste-despues.png). Foco de teclado sobre
«Abrir carpeta»: [antes](evidencias/fase1-contraste-foco-antes.png) y [después](evidencias/fase1-contraste-foco-despues.png).

Lo que cambió, en corto (detalle en [§5](#5-correcciones)):

- El blanco sobre el acento llegaba a 4,499:1 (no pasa 4,5) y el botón primario bajo el ratón a 3,61:1. El acento
  se oscurece 1 a 6 unidades por canal y el tono «hover» pasa a ser **más oscuro** que el base.
- Nuevo token `BorderStrongColor` para lo que tiene que verse: contorno de botones, pulgar de las barras de
  desplazamiento y selección sin foco. `BorderColor` queda solo para separadores decorativos.
- El estado «seleccionado» de las pestañas y del Explorador ya no depende de un relleno de 1,0 a 1,4:1.
- Seis colores de sintaxis de Monaco y cuatro del terminal ANSI estaban por debajo de 4,5:1; se aclaran lo mínimo.
- Foco de teclado: un anillo de 2 px en todos los controles interactivos (WPF dibuja por defecto un anillo punteado
  negro, invisible sobre fondo oscuro).

## 2. Método

### 2.1 Criterios y umbrales

| Criterio WCAG | Qué exige | Umbral |
|---|---|---|
| 1.4.3 Contraste (mínimo) | Texto contra su fondo | 4,5:1 (3:1 para texto grande: ≥ 24 px, o ≥ 18,66 px en negrita) |
| 1.4.11 Contraste de no texto | Contorno de componentes, iconos, gráficos, rellenos de estado | 3:1 contra los colores adyacentes |
| 2.4.7 Foco visible y 1.4.11 | Indicador de foco de teclado | Visible y 3:1 contra lo que lo rodea |

En la interfaz **no hay texto grande**: el título (22 px) es seminegrita, no negrita, así que cuenta como texto normal.
La razón de contraste es la de WCAG 2.x (luminancia relativa, sin redondear antes de comparar): 4,499:1 **no** cumple
4,5:1 aunque redondeado se lea 4,5.

### 2.2 Qué se mide: los pares que la interfaz dibuja de verdad

Cada par es un primer plano sobre el fondo que tiene detrás, incluidos los estados (reposo, ratón, seleccionado, foco,
pulsado, desactivado). Fuentes inventariadas:

- Tokens y estilos de `src/AgentStudio.Desktop/Themes/Dark.xaml`, y su uso en `MainWindow.xaml`, `Views/*.xaml`,
  `Controls/*/*.xaml` y `Themes/DocumentTemplates.xaml`.
- El chrome de AvalonDock: los 90 pinceles del tema VS2013 oscuro tras el repintado de
  `Theming/DockThemeTokens.cs` (el test falla si aparece un pincel sin revisar).
- La página de Monaco (`Assets/editor/editor.js`, `editor.html`): colores del tema, las 35 reglas de sintaxis y los
  fondos translúcidos que Monaco pinta detrás del texto (selección, resaltado de palabra, coincidencias de búsqueda).
- La página del terminal (`Assets/terminal/terminal.js`, `terminal.css`): tema de xterm.js con sus 16 colores ANSI
  sobre el fondo del terminal.

### 2.3 Cómo se mide: tests en `tests/AgentStudio.Tests/Desktop/Contrast/`

| Test | Qué comprueba |
|---|---|
| `WcagMathTests` | La fórmula, contra valores publicados (negro sobre blanco 21:1, `#767676` sobre blanco 4,54:1, un caso que redondea a 4,5 y no pasa) |
| `ThemeContrastTests` | Paleta y estilos: lee los `Setter` y los disparadores de plantilla de `PrimaryButton`, `GhostButton`, `ActivityButton`, `MonoViewer`, `StatusDot`, `ScrollBar`, los estilos de texto y los colores de selección del sistema. Desde la tanda 2, también `ReadingViewer`, el visor de la bitácora ([§10](#10-ampliación-de-la-tanda-2-2026-11-03)) |
| `MetricBarContrastTests` | Tanda 2: la barra común (`MetricBar`) en cada superficie y cada nivel ([§10](#10-ampliación-de-la-tanda-2-2026-11-03)) |
| `DockThemeContrastTests` | Los pares de AvalonDock tras aplicar `DockThemeTokens`, por nombre de pincel real |
| `WebThemeContrastTests` | Extrae del `editor.js` y del `terminal.js` que se despliegan los literales `THEME_COLORS`, `THEME_RULES` y `TERMINAL_THEME`; comprueba contraste, que cada color espeja su token y que los fondos de las páginas y de los `WebView2` coinciden con `BackgroundColor` |
| `FocusVisualTests` | El anillo de foco (contraste y grosor), que cada estilo enfocable lo use y que los controles sin estilo propio lo reciban |

Cada test reúne **todos** los fallos y los muestra de una vez, con el par, los dos colores y la razón. Si la variable de
entorno `AGENTSTUDIO_CONTRAST_REPORT` apunta a una carpeta, cada test escribe además su tabla en TSV (las cifras de este
documento salen de ahí). Para ejecutarlos: `dotnet test -- --filter-namespace "AgentStudio.Tests.Desktop.Contrast"`.

Verificación de que los tests detectan lo que dicen detectar: la suite final, ejecutada contra el código original
(commit `2c1b923`), falla en 15 de 33 tests. Los de WPF dan las cifras de la columna «Antes»; los de Monaco y xterm.js
fallan porque el código original no tiene los literales de tema que analizan, así que sus cifras «antes» se calcularon
con la misma fórmula sobre los valores anteriores. Además, reintroducir tres valores originales (acento `#2F6BFF`,
comentario `#608B4E`, azul ANSI `#3465A4`) pone en rojo los tests con el par exacto.

### 2.4 Qué queda exento, y por qué

Se mide igualmente y se lista con su razón; solo no hace fallar.

| Caso | Motivo | Ejemplos |
|---|---|---|
| Controles desactivados | WCAG no exige contraste a componentes inactivos (1.4.3, 1.4.11) | Botones con `IsEnabled=false`; las entradas de fases futuras de la barra de actividad |
| Decorativo | Separa regiones que ya se distinguen por su contenido | `BorderColor` en bordes de tarjetas, separadores y guías de sangría; puntos de arrastre de los títulos |
| Solo bajo el ratón | Un efecto de hover no es información necesaria (el texto y los glifos sobre el relleno de hover sí se miden) | Bordes de botones bajo el ratón |
| Transitorio | Solo existe mientras se arrastra | Guías de acople y vista previa de AvalonDock |
| ANSI 0 (negro) como texto | Los programas lo usan de fondo para líneas de estado de color: no puede ser a la vez negro y legible. `minimumContrastRatio: 4.5` lo eleva al dibujarlo | `black` de xterm.js |

### 2.5 Límites del método

- Mide los colores que el repositorio define. Los que Monaco pinta por defecto y no se sobrescriben (paleta de
  comandos, vista de referencias, decoraciones de margen, minimapa, pistas en línea) o los controles nativos de WPF
  (la lista desplegable del terminal) usan los valores de Monaco o de Windows y no se auditaron aquí.
- El contraste de texto sobre un resaltado translúcido (selección, coincidencia de búsqueda) se exige a 3:1 para los
  colores de sintaxis y a 4,5:1 para el color de texto por defecto: el resaltado es un estado transitorio del texto
  y 4,5:1 sobre él obligaría a aclarar toda la paleta de sintaxis.
- Las capturas salen de un arnés desechable fuera del repositorio que levanta la composición real (ventana, Monaco,
  xterm) con una carpeta de datos temporal; el «antes» se construyó desde el commit `2c1b923` con el mismo arnés.

## 3. Hallazgos (formato de `accessibility-review`)

**Estándar:** WCAG 2.1 AA · **Fecha:** 2026-10-15 · **Hallazgos:** 12, todos corregidos (8 mayores y 4 menores).

### Perceptible

| # | Hallazgo | Criterio | Gravedad | Estado |
|---|---|---|---|---|
| 1 | Blanco sobre el acento: 4,499:1 (botón primario, fila seleccionada, título y pestaña activos, conmutador de ventanas) | 1.4.3 | Mayor | Corregido |
| 2 | Botón primario bajo el ratón: 3,61:1 | 1.4.3 | Mayor | Corregido |
| 3 | Contorno del botón secundario: 1,30 a 1,56:1; sin él, el botón es solo texto | 1.4.11 | Mayor | Corregido |
| 4 | Pulgar de las barras de desplazamiento (WPF, Monaco, xterm): 1,21 a 1,72:1, y están dibujadas por la aplicación | 1.4.11 | Mayor | Corregido |
| 5 | Fila seleccionada del Explorador sin foco: relleno a 1,42:1 | 1.4.11 | Mayor | Corregido |
| 6 | Pestañas seleccionadas sin foco (documentos y paneles) iguales a las no seleccionadas (1,00:1); pestaña de panel seleccionada con foco a 1,10:1 | 1.4.11 | Mayor | Corregido |
| 7 | Seis colores de sintaxis de Monaco bajo 4,5:1 sobre la línea actual (el peor, `variable.predefined`, 3,32:1 sobre el fondo) | 1.4.3 | Menor | Corregido |
| 8 | Colores ANSI `red` 3,22, `blue` 3,20, `magenta` 2,88 y `brightBlack` 2,60:1 (PowerShell escribe parámetros y predicciones en `brightBlack`) | 1.4.3 | Mayor | Corregido |
| 9 | Contorno de los campos de los widgets de Monaco: 1,30:1 | 1.4.11 | Menor | Corregido |

### Operable

| # | Hallazgo | Criterio | Gravedad | Estado |
|---|---|---|---|---|
| 10 | Anillo de foco por defecto de WPF: punteado, negro, 1 px; 1,1 a 1,3:1 sobre las superficies | 2.4.7, 1.4.11 | Mayor | Corregido |
| 11 | El botón primario solo cambiaba de relleno al recibir el foco (1,25:1) y lo conservaba tras un clic | 2.4.7 | Menor | Corregido |

### Comprendible y Robusto

| # | Hallazgo | Criterio | Gravedad | Estado |
|---|---|---|---|---|
| 12 | Estado «seleccionado» expresado solo por relleno en pestañas y filas (ver 5 y 6); el estado de los proveedores ya lleva texto además del punto de color | 1.4.1, 1.4.11 | Menor | Corregido el relleno; el texto ya existía |

Nombres accesibles, roles y mapa de teclado: no revisados, [§7](#7-fuera-de-alcance-hasta-la-fase-7).

## 4. Pares revisados (antes → después)

Razones de contraste con coma decimal. «Falla» marca lo que no llegaba al mínimo. Cuando una fila agrupa varios pares,
los valores van en el orden del enunciado. La lista exhaustiva, par a par, es la de los tests.

### 4.1 Tokens, estilos y controles de WPF

| Par | Mín. | Antes | Después |
|---|---|---|---|
| Texto principal sobre fondo / superficie / tarjeta | 4,5 | 16,27 / 14,87 / 13,55 | 16,27 / 14,87 / 13,55 |
| Texto secundario sobre fondo / superficie / tarjeta | 4,5 | 8,62 / 7,87 / 7,18 | 8,62 / 7,87 / 7,18 |
| Error sobre fondo / superficie | 4,5 | 6,32 / 5,77 | 6,32 / 5,77 |
| Advertencia sobre superficie | 4,5 | 9,55 | 9,55 |
| Blanco sobre Acento (botón primario, selección activa, título y pestaña activos) | 4,5 | 4,499 (falla) | 4,70 |
| Blanco sobre Acento bajo el ratón (botón primario) | 4,5 | 3,61 (falla) | 6,21 |
| Punto de estado éxito / advertencia / error sobre superficie | 3 | 8,67 / 9,55 / 5,77 | 8,67 / 9,55 / 5,77 |
| Punto de estado éxito / advertencia / error sobre tarjeta | 3 | 7,90 / 8,70 / 5,26 | 7,90 / 8,70 / 5,26 |
| Estilos `BodyText`, `SecondaryText`, `ErrorText`, `TitleText`, `SectionTitleText`, en cada superficie donde se usan (13 pares) | 4,5 | de 5,77 a 16,27 | de 5,77 a 16,27 |
| Botón primario: relleno contra barra superior / bienvenida | 3 | 3,85 / 4,21 | 3,68 / 4,03 |
| Botón secundario: texto sobre fondo / superficie | 4,5 | 16,27 / 14,87 | 16,27 / 14,87 |
| Botón secundario: contorno contra fondo / superficie / tarjeta (bajo el ratón) | 3 | 1,56 (falla) / 1,42 (falla) / 1,30 (falla) | 3,95 / 3,61 / 3,29 |
| Botón secundario bajo el ratón: texto | 4,5 | 13,55 | 13,55 |
| Icono de la barra de actividad: reposo / bajo el ratón | 3 | 7,87 / 13,55 | 7,87 / 13,55 |
| Barra de foco de la barra de actividad contra la barra | 3 | 3,85 | 3,68 |
| Visor de ficheros y bitácora: texto / cursor | 4,5 / 3 | 16,27 / 16,27 | 16,27 / 16,27 |
| Puntos de estado dibujados por `StatusDot` (4 estados por 2 superficies) | 3 | de 5,26 a 9,55 | de 5,26 a 9,55 |
| Pulgar de la barra de desplazamiento (vertical y horizontal) contra fondo / superficie / tarjeta | 3 | 1,56 (falla) / 1,42 (falla) / 1,30 (falla) | 3,95 / 3,61 / 3,29 |
| Barra de progreso contra su pista | 3 | 3,51 | 3,36 |
| Fila seleccionada del Explorador con foco: texto / relleno contra el panel | 4,5 / 3 | 4,499 (falla) / 3,85 | 4,70 / 3,68 |
| Fila seleccionada del Explorador sin foco: texto / relleno contra el panel | 4,5 / 3 | 10,45 / 1,42 (falla) | 4,79 / 3,61 |

Exentos (con la razón de [§2.4](#24-qué-queda-exento-y-por-qué)): botones desactivados (`Opacity` 0,45 y 0,4; de 2,3 a 4,0:1
efectivos) y los bordes decorativos de las tarjetas y de los separadores (1,42:1).

### 4.2 AvalonDock

| Par | Mín. | Antes | Después |
|---|---|---|---|
| Pestaña de documento seleccionada con foco: texto | 4,5 | 4,499 (falla) | 4,70 |
| Pestaña de documento seleccionada sin foco: texto | 4,5 | 7,18 | 4,79 |
| Pestaña de documento no seleccionada / bajo el ratón: texto | 4,5 | 7,18 / 10,45 | 7,18 / 10,45 |
| Botón cerrar de pestaña (9 estados: reposo, ratón y pulsado, en pestaña seleccionada con y sin foco y no seleccionada) | 3 | de 3,10 a 10,45 | de 4,04 a 10,45 |
| Botón de pestañas ocultas: reposo / ratón / pulsado | 3 | 8,62 / 10,45 / 3,10 | 8,62 / 10,45 / 5,33 |
| Título de panel con foco / sin foco: texto | 4,5 | 4,499 (falla) / 7,18 | 4,70 / 7,18 |
| Botones del título con foco (anclar, cerrar, menú): reposo / ratón / pulsado | 3 | 4,50 / 3,61 / 3,61 | 4,70 / 6,21 / 6,21 |
| Botones del título sin foco: reposo / ratón / pulsado | 3 | 7,18 / 10,45 / 3,10 | 7,18 / 10,45 / 5,33 |
| Pestaña de panel seleccionada con foco: texto | 4,5 | 14,87 | 4,70 |
| Pestaña de panel seleccionada sin foco: texto | 4,5 | 7,18 | 4,79 |
| Pestaña de panel no seleccionada / bajo el ratón: texto | 4,5 | 7,18 / 10,45 | 7,18 / 10,45 |
| Pestañas de paneles ocultables: reposo / ratón | 4,5 | 7,18 / 10,45 | 7,18 / 10,45 |
| Conmutador de ventanas: lista / elemento seleccionado | 4,5 | 7,87 / 4,499 (falla) | 7,87 / 4,70 |
| Texto por defecto del chrome (fondo, paneles flotantes, documentos flotantes) | 4,5 | 8,62 / 7,87 / 7,87 | 8,62 / 7,87 / 7,87 |
| Estado: pestaña de documento seleccionada con foco, frente a las demás / frente a la barra de pestañas | 3 | 3,51 / 4,21 | 3,36 / 4,03 |
| Estado: pestaña de documento seleccionada sin foco, frente a las demás / frente a la barra | 3 | 1,00 (falla) / 1,20 (falla) | 3,29 / 3,95 |
| Estado: pestaña de panel seleccionada con foco, frente a las demás | 3 | 1,10 (falla) | 3,36 |
| Estado: pestaña de panel seleccionada sin foco, frente a las demás | 3 | 1,00 (falla) | 3,29 |
| Estado: título del panel con foco, frente a los demás | 3 | 3,51 | 3,36 |

Hay una bajada deliberada: el texto de la pestaña de panel seleccionada con foco pasa de 14,87 a 4,70:1 porque ahora va
en blanco sobre el acento (como las pestañas de documento) para que la selección se distinga; sigue por encima de 4,5.
Exentos: bordes de paneles y ventanas flotantes, puntos de arrastre, bordes de hover y las guías de acople.

### 4.3 Editor (Monaco)

Colores de sintaxis que cambian (fondo / línea actual / peor resaltado; mínimos 4,5 / 4,5 / 3):

| Color de sintaxis | Antes | Después |
|---|---|---|
| `comment` `#608B4E` → `#669055` | 4,78 / 4,37 (falla) / 2,93 (falla) | 5,13 / 4,69 / 3,19 |
| `delimiter.html`, `delimiter.xml` `#808080` → `#848484` | 4,80 / 4,38 (falla) / 2,94 (falla) | 5,07 / 4,63 / 3,15 |
| `regexp` `#B46695` → `#B76C99` | 4,76 / 4,35 (falla) / 2,91 (falla) | 5,07 / 4,63 / 3,15 |
| `string.sql` `#FF0000` → `#FF5C6C` (el token de error) | 4,74 / 4,33 (falla) / 2,90 (falla) | 6,32 / 5,77 / 3,93 |
| `variable.predefined` `#4864AA` → `#6D83BB` | 3,32 (falla) / 3,03 (falla) / 2,03 (falla) | 5,07 / 4,63 / 3,15 |

Los otros 29 colores de sintaxis no cambian: ya cumplían. Se quitaron las reglas de pug, scss, swift y php (lenguajes
que el editor no abre; `tag.id.pug` y `tag.class.pug` estaban a 4,08 y 3,73:1). El resto del editor:

| Par | Mín. | Antes | Después |
|---|---|---|---|
| Texto del editor sobre fondo / línea actual | 4,5 | 16,27 / 14,87 | 16,27 / 14,87 |
| Texto del editor bajo cada uno de los 12 resaltados translúcidos, sobre fondo y sobre línea actual (24 pares) | 4,5 | de 9,96 a 13,57 (los 10 que ya existían) | de 10,11 a 14,89 |
| Números de línea / línea activa sobre el fondo | 4,5 | 4,89 / 16,27 | 4,89 / 16,27 |
| Texto de widgets (buscar) / sugerencias / información | 4,5 | por defecto de Monaco | 13,55 / 13,55 / 13,55 |
| Letras coincidentes de las sugerencias: normal / seleccionada | 4,5 | por defecto de Monaco | 6,50 / 4,70 |
| Sugerencia seleccionada: texto | 4,5 | por defecto de Monaco | 4,70 |
| Campos de los widgets: texto / ayuda | 4,5 | por defecto de Monaco | 14,87 / 7,87 |
| Cursor de texto sobre fondo / línea actual | 3 | 5,26 / 4,80 | 4,03 / 3,68 |
| Contorno de foco contra editor / widgets / campos | 3 | 4,21 / 3,51 / 3,85 | 4,03 / 3,36 / 3,68 |
| Contorno de los campos contra el widget | 3 | 1,30 (falla) | 3,29 |
| Pulgar de la barra de desplazamiento: reposo / ratón / arrastrando | 3 | 1,21 (falla) / 1,94 (falla) / por defecto | 3,95 / 5,65 / 4,03 |
| Sugerencia seleccionada frente a las demás | 3 | por defecto de Monaco | 3,36 |
| Contorno de coincidencia de búsqueda / de paréntesis pareja | 3 | por defecto de Monaco | 7,81 / 7,81 |

«Por defecto de Monaco» significa que el color no estaba en el repositorio y no se podía comprobar. Exentos: guías de
sangría y bordes de widgets (decorativos).

### 4.4 Terminal (xterm.js 6.0.0)

El tema no definía la paleta ANSI, así que valía la de xterm.js por defecto (Tango). Cada color como texto sobre
`#0A1020`, mínimo 4,5:

| Color ANSI | Antes | Después |
|---|---|---|
| `black` `#2E3436` | 1,50 (exento) | 1,50 (exento) |
| `red` `#CC0000` → `#DB4C4C` | 3,22 (falla) | 4,65 |
| `green` `#4E9A06` | 5,37 | 5,37 |
| `yellow` `#C4A000` | 7,56 | 7,56 |
| `blue` `#3465A4` → `#5981B4` | 3,20 (falla) | 4,71 |
| `magenta` `#75507B` → `#927597` | 2,88 (falla) | 4,71 |
| `cyan` `#06989A` | 5,38 | 5,38 |
| `white` `#D3D7CF` | 12,99 | 12,99 |
| `brightBlack` `#555753` → `#7C7E7B` | 2,60 (falla) | 4,63 |
| `brightRed` `#EF2929` | 4,53 | 4,53 |
| `brightGreen` `#8AE234`, `brightYellow` `#FCE94F`, `brightBlue` `#729FCF`, `brightMagenta` `#AD7FA8`, `brightCyan` `#34E2E2`, `brightWhite` `#EEEEEC` | de 5,76 a 16,32 | de 5,76 a 16,32 |

| Par | Mín. | Antes | Después |
|---|---|---|---|
| Texto del terminal sobre el fondo | 4,5 | 16,27 | 16,27 |
| Carácter bajo el cursor de bloque | 4,5 | 4,67 | 4,70 |
| Texto seleccionado | 4,5 | 10,45 (solo con el color por defecto) | 10,45 (con cualquier color) |
| Cursor contra el fondo | 3 | 4,21 | 4,03 |
| Pulgar de la barra de desplazamiento: reposo / ratón / arrastrando | 3 | 1,72 (falla) / 3,45 / 4,72 | 3,95 / 5,65 / 4,03 |
| Mensaje de error de la página (`terminal.css`) | 4,5 | 6,32 | 6,32 |

`black` queda oscuro a propósito (ver [§2.4](#24-qué-queda-exento-y-por-qué)). Además, `minimumContrastRatio: 4.5` hace
que xterm.js eleve al dibujarlo cualquier texto que quede por debajo, sea cual sea el par de colores que escoja un
programa (256 colores, color verdadero, negro sobre fondo oscuro): en la captura «después» las palabras `black`, `blue` y
`magenta` ya se leen.

### 4.5 Foco de teclado

| Par | Mín. | Antes | Después |
|---|---|---|---|
| Anillo exterior contra fondo / superficie / tarjeta | 3 | 1,11 (falla) / 1,21 (falla) / 1,33 (falla) | 16,27 / 14,87 / 13,55 |
| Anillo exterior contra el relleno del botón primario / bajo el ratón | 3 | 4,67 / 5,82 | 4,04 / 5,33 |
| Anillo exterior contra el contorno del botón secundario | 3 | — | 4,12 |
| Anillo interior contra fondo / superficie / tarjeta | 3 | 1,11 (falla) / 1,21 (falla) / 1,33 (falla) | 16,27 / 14,87 / 13,55 |

«Antes» es el anillo punteado por defecto de WPF (`SystemColors.ControlText`, negro, 1 px).

## 5. Correcciones

Principios: cambiar lo mínimo para conservar el aspecto, mantener los colores dentro de los tokens y que los colores de
las páginas web espejen los tokens (el test lo comprueba).

| Dónde | Antes | Después | Por qué |
|---|---|---|---|
| `AccentColor` | `#2F6BFF` | `#2E68F9` | Blanco sobre el acento: 4,499 → 4,70:1. Cambio de 1 a 6 unidades por canal, imperceptible |
| `AccentHoverColor` | `#4A80FF` | `#2557D3` | El tono «hover» más claro no puede llevar texto blanco (3,61:1). Ahora es más oscuro que el base: 6,21:1. Los dos cursores (Monaco y terminal) pasan a usar `AccentColor` |
| `BorderStrongColor` (nuevo) | — | `#5F7396` | Contorno de botones, pulgares y selección sin foco: de 3,29 a 3,95:1 sobre las tres superficies y blanco encima a 4,79:1. `BorderColor` se queda como separador decorativo |
| `FocusRingBrush` (nuevo) | — | `TextPrimaryColor` | Anillo de foco: 13,55 a 16,27:1 sobre las superficies y 4,04:1 sobre el acento |
| `GhostButton` | contorno `BorderBrush` | contorno `BorderStrongBrush`; sin disparador de borde de acento al enfocar | 3,29 a 3,95:1; el anillo sustituye al borde, que además se quedaba tras un clic |
| `PrimaryButton` | relleno de hover al enfocar | anillo; sin disparador al enfocar | El cambio de relleno era de 1,25:1 |
| Barras de desplazamiento (WPF) | pulgar `BorderBrush` | `BorderStrongBrush` | 1,30 a 1,56 → 3,29 a 3,95:1 |
| Selección sin foco (`SystemColors.InactiveSelectionHighlight*`) | `BorderColor` con texto principal | `BorderStrongColor` con `OnAccentColor` | 1,42:1 de relleno → 3,61:1 y texto a 4,79:1 |
| `DockThemeTokens` | Pestaña seleccionada sin foco = relleno de las no seleccionadas | Seleccionada con foco = acento, sin foco = `BorderStrong`, siempre texto blanco (documentos y paneles); glifo del botón cerrar a juego | La pestaña seleccionada ya se distingue a 3:1; es la misma regla que en el Explorador |
| `editor.js` | Heredaba las reglas de `vs-dark`; colores sin documentar | `inherit: false` con las 37 reglas (35 con color) y los 43 colores en dos literales (`THEME_RULES`, `THEME_COLORS`); 6 colores de sintaxis aclarados; resaltados translúcidos con ≤ 33 % de acento | Con la herencia, los colores de sintaxis no estaban en el fichero y ningún test podía verlos |
| `editor.js` (widgets) | Por defecto de Monaco | Colores explícitos de widgets, sugerencias, información y campos, derivados de los tokens | Quedan dentro del conjunto medido |
| `terminal.js` | Cuatro colores ANSI por defecto bajo 4,5; sin `selectionForeground` | Paleta explícita (4 colores aclarados), `selectionForeground`, `cursorAccent`, pulgar de desplazamiento y `minimumContrastRatio: 4.5` | Ver [§4.4](#44-terminal-xtermjs-600) |
| `Dark.xaml` (foco) | Anillo punteado negro por defecto | Estilos `FocusVisual` (exterior, también como `SystemParameters.FocusVisualStyleKey`) y `FocusVisualInset` (interior); estilos implícitos de `Button`, `ToggleButton`, `CheckBox`, `RadioButton` y `ComboBox` que solo fijan `FocusVisualStyle` | Ver [§6](#6-foco-de-teclado) |
| `Dark.xaml` (cabecera) | «primary ~15:1, secondary ~8:1» | Los umbrales que se hacen cumplir y el papel de cada token | La nota antigua era una promesa sin comprobar |

Páginas y controles que repiten `#0A1020` a mano (`editor.html`, `terminal.css`, `CodeEditorControl.xaml` y el
`DefaultBackgroundColor` de los dos `WebView2`): no se tocan los XAML; el test comprueba que siguen valiendo
`BackgroundColor`.

## 6. Foco de teclado

El anillo de WPF por defecto es un rectángulo punteado en `SystemColors.ControlText` (negro). Sobre las superficies
oscuras mide 1,1 a 1,3:1, así que el foco de un botón secundario, de la barra de actividad o del visor era invisible.
`Dark.xaml` define ahora un anillo de 2 px en el color de texto principal y lo aplica así:

| Control | Indicador de foco | Comprobado |
|---|---|---|
| «Abrir carpeta», «Volver a comprobar» (`PrimaryButton`) | Anillo exterior, `FocusVisualStyle` explícito | `FocusVisualTests` + [captura](evidencias/fase1-contraste-foco-despues.png) |
| «Inicio», «Actualizar», proyectos recientes (`GhostButton`) | Anillo exterior | ídem |
| Barra de actividad (`ActivityButton`) | Anillo interior y barra de acento de 2 px a la izquierda | `ActivityButton.focused`: 3,68:1 |
| Visor de ficheros y bitácora (`MonoViewer`) | Anillo interior; además el cursor de texto, visible aunque sea de solo lectura. Desde la tanda 2, la bitácora se lee en `ReadingViewer` (mismo anillo y mismo cursor de lectura) y `MonoViewer` solo la enseña con «Ver todo» ([§10](#10-ampliación-de-la-tanda-2-2026-11-03)) | Test del estilo |
| Botones y lista de shells del terminal (`Button`, `ComboBox` sin estilo propio) | Anillo exterior por estilo implícito: sus plantillas de WPF llevan su propio anillo y no usan la clave del sistema | `FocusVisualTests`, con una instancia real de cada tipo |
| Filas del Explorador (`TreeViewItem`) | La propia selección: 3,68:1 con foco y 3,61:1 sin él. Desde la tanda 1 la fila ocupa todo el ancho del árbol ([§9](#9-ampliación-de-la-tanda-1-2026-10-30)) | `selection.*` y `ControlStylesContrastTests` |

`TreeViewItem` y `ComboBox` viven en archivos de otros módulos (`Views/ExplorerView.xaml`, `Controls/Terminal/TerminalControl.xaml`):
no se modifican; reciben el anillo por el diccionario del tema. Los estilos implícitos de `CheckBox` y `RadioButton`
usan además un disparador de estilo, porque la plantilla de WPF cambia su anillo cuando tienen contenido. Se
comprobó con una ventana de prueba y la tecla Tab real: botón por defecto, lista desplegable, botón primario, secundario,
icono de actividad, casilla y visor muestran el anillo.

## 7. Fuera de alcance hasta la Fase 7

- **Lector de pantalla** (NVDA, Narrador): nombres, roles y valores (4.1.2). Hay `AutomationProperties.Name` en
  varios controles, pero no se ha recorrido la interfaz con un lector; Monaco y xterm.js tienen sus propios modos de
  accesibilidad por activar y probar. Desde la tanda 1 (hallazgo A1 de [18](18-peticiones-y-pendientes.md)) las pestañas
  de documentos y paneles, las de los paneles plegados en un lado y varias listas tienen nombre; el recorrido con un
  lector sigue pendiente. Desde la tanda 2, también los elementos de las listas y los selectores (A1-bis) y cada barra
  ([§10](#10-ampliación-de-la-tanda-2-2026-11-03)).
- **Mapa completo de teclado** (2.1.1, 2.4.3): todas las funciones sin ratón, orden de tabulación, atajos y su
  documentación, y la navegación por teclado de pestañas y paneles de AvalonDock (qué se enfoca al mover una pestaña).
- **Modo de alto contraste de Windows**: el tema usa colores fijos y no sigue `SystemColors`.
- **Zoom y reflujo** (1.4.4, 1.4.10): comportamiento al 200 % y con textos más grandes.
- **Movimiento y parpadeo**: cursor intermitente de Monaco y xterm.js, barra de progreso indeterminada.
- **Tamaño de los objetivos** (2.5.8 de WCAG 2.2): no medido.
- **Formularios** (3.3.2): cuando existan diálogos con campos.

## 8. Cómo mantenerlo

- Cambiar un token de `Dark.xaml`, un color de `editor.js` o de `terminal.js`: ejecutar los tests de contraste; dicen
  qué par falla y con qué razón. Si un color de las páginas web cambia, debe cambiar también su token (y al revés).
- Un par nuevo (una vista o un estado nuevos): añadirlo a la tabla del test que corresponda, con su umbral. Si es
  decorativo, listarlo como exento con la razón, no omitirlo.
- Un pincel nuevo de AvalonDock tras actualizar el paquete: `Every_dock_brush_is_reviewed` lo señala.
- Una regla de sintaxis o un color translúcido nuevo en `editor.js`: el test exige revisarlo (los resaltados
  translúcidos se declaran en `Overlays`).
- Actualizar Monaco: las reglas de sintaxis salen de `vs-dark` 0.57.0; si una versión nueva añade tokens, aparecerán con
  el color por defecto del editor (que cumple) hasta que se añadan a `THEME_RULES`.
- Un estilo implícito nuevo en `Dark.xaml`, o un estado nuevo de uno que ya existe: añadir sus pares a
  `ControlStylesContrastTests` ([§9](#9-ampliación-de-la-tanda-1-2026-10-30)).
- Actualizar .NET: comprobar que `ImplicitStylesTests` sigue encontrando los tipos internos del menú de los campos de
  texto; si WPF los renombra, ese menú vuelve a salir claro y la prueba lo señala.

## 9. Ampliación de la tanda 1 (2026-10-30)

El recorrido en vivo del 2026-10-30 ([18 §3](18-peticiones-y-pendientes.md#3-hallazgos-del-recorrido-en-vivo-2026-10-30))
encontró controles que seguían con el aspecto claro de Windows (V1, V13), filas del Explorador que solo respondían sobre
el texto (V7) y ventanas abiertas desde la bandeja donde `Esc` no hacía nada (B5/B9). WP-A los corrigió. Las cifras de
[§1](#1-resultado) no cambian: son las de F1-6. Lo nuevo se mide en sus propias pruebas, con las mismas reglas de
[§2](#2-método) (texto 4,5:1; contornos, glifos, rellenos de estado y foco 3:1; lo desactivado y lo decorativo, exento y
listado).

**Zonas nuevas que se miden o se comprueban:**

| Zona | Qué cubre | Dónde se comprueba |
|---|---|---|
| Estilos implícitos de los controles de WPF | `Button`, `CheckBox`, `RadioButton`, `ComboBox` y sus elementos, `TextBox`, `ToolTip`, `ContextMenu`, `MenuItem`, `ListBox` y sus filas, y `Expander`, en cada estado que dibujan: reposo, ratón, pulsado, marcado, seleccionado, resaltado, foco y desactivado. Antes estos estilos solo fijaban el anillo de foco ([§5](#5-correcciones)); ahora tienen plantilla propia con los tokens del tema. Los estilos con clave (`PrimaryButton`, `GhostButton`, `ActivityButton`…) conservan la suya | `ControlStylesContrastTests` (contraste, leído de los estilos y plantillas reales), `ControlStylesRenderTests` (render fuera de pantalla) e `ImplicitStylesTests` |
| Filas del Explorador | La fila entera, con la sangría dentro: clic, selección y doble clic en todo el ancho. Seleccionada: acento con foco y `BorderStrong` sin él, con texto `OnAccent`; la propia selección hace de indicador de foco | `ControlStylesContrastTests` |
| Menú Cortar, Copiar y Pegar de los campos de texto | WPF lo construye con tipos internos que un estilo implícito no alcanza. `TextBoxMenuTheme` les pone, al arrancar, los mismos estilos de `ContextMenu` y `MenuItem` del tema. Depende de los nombres internos de WPF: si cambian, el menú vuelve a salir claro | `ImplicitStylesTests` |
| Menús de las pestañas de AvalonDock | Toman el menú oscuro del tema; los botones y filas propios de AvalonDock conservan su estilo | `ImplicitStylesTests` |
| Barra de título | Oscura en todas las ventanas, también la principal y los paneles flotantes: `ThemedWindows` pide a DWM el modo oscuro (`DWMWA_USE_IMMERSIVE_DARK_MODE`) antes del primer pintado. Sin DWM, la barra queda como la dibuje Windows y la ventana funciona igual | `ThemedWindowsTests` (con una llamada a DWM simulada) |
| Foco inicial | Ver abajo | `InitialFocusTests` y `ThemedWindowsTests` |

**Foco inicial** (WCAG 2.1.1 y 2.4.3). Una ventana que se activa sin ningún control con el teclado (WPF se lo da a la
propia ventana cuando la abre otro proceso, como el menú de la bandeja) no deja que `Esc` ni `Intro` lleguen a un botón.
Ahora, en cada activación y al pintarse por primera vez, el teclado va a:

1. el botón por defecto;
2. si no hay, el botón de cancelar;
3. si tampoco hay, el primer control al que llega `Tab`.

Solo actúa si nada de la ventana tiene ya el teclado, y nunca quita el foco de un WebView2. Vale para los diálogos y
paneles de la app: el panel de la bandeja, la pregunta de salir (el foco va a «Cancelar»), la de cambios sin guardar y
las aprobaciones, donde va a «Denegar» y nunca a «Aprobar». No vale para la ventana principal, que deja el teclado donde
lo tenías, ni para los paneles flotantes de AvalonDock, que llevan su propio foco. Que la activación desde otro proceso
llegue de verdad solo se ve en vivo: una ventana que no se enseña nunca se activa.

**Más foco en la ventana principal** (WP-B):

- La barra lateral ya no dibuja un anillo alrededor de toda ella; el anillo va en el botón que tiene el teclado (V10).
- Al cerrar una pestaña, el teclado vuelve al documento activo o a la ventana, y los atajos funcionan sin hacer clic (V9).
- Un panel abierto desde la barra lateral, su atajo o el resumen de proveedores recibe el teclado en su primer control.

**Límites.** Ya no queda fuera del método la lista desplegable del terminal ([§2.5](#25-límites-del-método)): la cubre el
estilo implícito de `ComboBox`. Sigue fuera lo que pinta Monaco por su cuenta. Nada de esto se ha recorrido todavía con
un lector de pantalla ([§7](#7-fuera-de-alcance-hasta-la-fase-7)), y la tanda 1 está pendiente de verificar en vivo.

## 10. Ampliación de la tanda 2 (2026-11-03)

La tanda 2 trajo las barras en tiempo real (P3 de [18](18-peticiones-y-pendientes.md)) y un pulido del recorrido en vivo
(V29 a V31 y A1-bis). Las cifras de [§1](#1-resultado) no cambian. Lo nuevo se mide con las mismas reglas de
[§2](#2-método): texto 4,5:1; contornos, rellenos y foco 3:1; lo decorativo, exento y listado.

**Zonas nuevas que se miden o se comprueban:**

| Zona | Qué cubre | Dónde se comprueba |
|---|---|---|
| La barra común (`Controls/MetricBar.xaml`) | Contexto, cuota, presupuesto y tokens, en cada pantalla que los enseña como barra. Ver abajo | `MetricBarContrastTests` y las pruebas del control |
| El visor de la bitácora (`ReadingViewer`, en `Dark.xaml`) | La bitácora se ve como documento: títulos, párrafos, listas, negrita y código. Ver abajo | `ThemeContrastTests` (pares `ReadingViewer.*`) y `JournalRenderTests` |
| El rincón de las dos barras de desplazamiento (V30) | WPF lo pintaba con `SystemColors.Control`, un cuadrado claro en «Skills». El estilo implícito de `ScrollViewer` cambia solo ese pincel al color de superficie; la plantilla sigue siendo la de WPF, y un `ScrollViewer` con estilo propio no cambia | `ScrollViewerCornerTests` (render fuera de pantalla) |
| Nombres de los elementos de las listas (A1-bis) | Ver abajo | `ListItemNamesTests` |

**La barra común.**

- **Contraste**, medido con los recursos reales (los tokens del tema y los pinceles del propio control):
  - el borde de la pista (`BorderStrongBrush`) sobre la ventana, las barras y paneles, y las tarjetas: 3:1, para que una
    barra vacía se vea en cualquier sitio;
  - el relleno de cada nivel (sano, aviso, compresión recomendada, crítico y neutro) frente a la pista vacía: 3:1. También
    con la trama de una estimación encima;
  - la cifra y el texto del origen y el nivel, en las tres superficies: 4,5:1;
  - exentas y listadas: las rayas de la trama sobre el relleno y las marcas de los umbrales sobre el relleno. Repiten lo
    que ya dicen las palabras («~», «estimado», el nivel).
- **Nada se dice solo con color.** El nivel va escrito («sano», «crítico», «presupuesto agotado»). Lo estimado lleva «~»,
  la palabra «estimado» y una trama. Sin cifra, la pista sale vacía con el motivo escrito, nunca una barra a cero.
- **Lector de pantalla.** UI Automation ve cada barra como una barra de progreso. Su nombre es la línea entera: qué mide,
  la cifra, el origen, el nivel, la antigüedad y el detalle. Solo tiene valor (patrón `RangeValue`, de solo lectura)
  cuando hay cifra: una barra vacía nunca se lee como cero. Al cambiar, avisa del nuevo nombre y del nuevo valor.
- **El teclado no se para en ella:** no es un control que se pueda usar.

**El visor de la bitácora.**

- **Pares medidos:** el texto sobre el fondo; las líneas secundarias (la cabecera y las líneas «Resultado» y «Hecho»); el
  código sobre su fondo, también en una línea secundaria; el cursor de lectura; y el texto seleccionado (la selección se
  pinta encima, translúcida), tanto el principal como el secundario.
- **Foco:** el mismo anillo interior y el mismo cursor de lectura que `MonoViewer`. El teclado se mueve y selecciona con
  ese cursor.
- **No imprime ni busca.** `Ctrl+P` y `Ctrl+F` siguen hasta la ventana, donde `Ctrl+P` es «Ir al fichero».
- **Un día largo** solo dibuja sus entradas más recientes (200 líneas y 40 000 caracteres como mucho), porque dibujar un
  documento largo bloquea la interfaz. Lo dice en una línea que el lector anuncia. «Ver todo» enseña el día completo en
  `MonoViewer`, como texto sin formato. Es el único uso que le queda en la bitácora.

**Nombres de los elementos de las listas (A1-bis).** En el recorrido en vivo, UI Automation leía la opción del modelo de
un agente como «ModelOption { Label = haiku, … }». Un elemento de una lista que no es un control se nombra con su
`ToString()`. Ahora más de 20 tipos de elementos dan su etiqueta: opciones de modelo y de agente, filtros, ejecuciones
anteriores, la paleta y la leyenda del lienzo, problemas del plan, «Ir al fichero», proyectos recientes, el árbol y las
comprobaciones de «Skills», shells, las líneas de la salida gestionada y las barras. La prueba los pone en una lista y en
un selector, fuera de pantalla, y comprueba el nombre de cada uno.

**Límites.** Igual que en la tanda 1: nada de esto se ha recorrido con un lector de pantalla, y la tanda 2 está pendiente
de verificar en vivo.
