# Fase 1 · Demostración de F1-2 y F1-5

> Lista para seguir a mano en la aplicación real. La parte automática ya la cubren los tests: F1-2 con
> `EditorBridgeIntegrationTests` (Monaco real en WebView2, en una ventana que nunca se muestra) y F1-5 con los tests de
> `FileLayoutStore` y `MainWindow`. Aquí se comprueba lo que solo se ve en pantalla. Marca cada casilla y, si algo
> falla, anota qué viste.

- Fecha: ____________
- Commit (`git log -1 --format=%h`): ____________

## Preparación

1. Compila y arranca la aplicación desde la raíz del repositorio:

   ```powershell
   dotnet build AgentStudio.slnx
   & .\src\AgentStudio.Desktop\bin\Debug\net10.0-windows\AgentStudio.Desktop.exe
   ```

2. En otra consola de PowerShell 7, crea una carpeta con un fichero de cada lenguaje (tres con BOM y CRLF, tres sin
   BOM y con LF) y la función que muestra su codificación. Deja la consola abierta: se usa al final de F1-2.

   ```powershell
   $demo = Join-Path $env:TEMP 'agentstudio-demo-f1'
   New-Item -ItemType Directory -Force -Path $demo | Out-Null
   function New-DemoFile([string] $Name, [string[]] $Lines, [bool] $Bom, [string] $Eol) {
       [IO.File]::WriteAllText((Join-Path $demo $Name), ($Lines -join $Eol) + $Eol, [Text.UTF8Encoding]::new($Bom))
   }
   New-DemoFile 'Programa.cs' @('public sealed class Saludo', '{', '    // Comentario del año', '    public string Hola(int veces) => "hola" + veces;', '}') $true "`r`n"
   New-DemoFile 'script.py' @('def hola(veces: int) -> str:', '    # Comentario del año', "    return 'hola' * veces") $false "`n"
   New-DemoFile 'consulta.sql' @('SELECT id, nombre', 'FROM usuarios', 'WHERE id = 42; -- el año') $true "`r`n"
   New-DemoFile 'ajustes.json' @('{', '  "nombre": "año",', '  "total": 3,', '  "activo": true', '}') $false "`n"
   New-DemoFile 'pipeline.yaml' @('nombre: agente # el año', 'elementos:', '  - uno', '  - dos') $false "`n"
   New-DemoFile 'LEEME.md' @('# Título', '', 'Texto del año con **negrita** y `código`.') $true "`r`n"
   function Show-DemoEncoding {
       Get-ChildItem $demo -File | ForEach-Object {
           $bytes = [IO.File]::ReadAllBytes($_.FullName)
           $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
           $eol = if ([Text.Encoding]::UTF8.GetString($bytes).Contains("`r`n")) { 'CRLF' } else { 'LF' }
           '{0,-14} BOM={1,-5} {2}' -f $_.Name, $bom, $eol
       }
   }
   Show-DemoEncoding
   ```

   Debe salir `BOM=True` y `CRLF` para `Programa.cs`, `consulta.sql` y `LEEME.md`, y `BOM=False` y `LF` para
   `script.py`, `ajustes.json` y `pipeline.yaml`.

## F1-2 · Editor: seis lenguajes con resaltado, editar y guardar

1. Pulsa **Abrir carpeta** y elige la carpeta de `$demo` (`%TEMP%\agentstudio-demo-f1`). El explorador muestra los
   seis ficheros.
2. Abre cada fichero con doble clic (o Intro). Comprueba que se ve con los colores de su lenguaje y no todo de un
   color: palabras clave (`public`, `def`, `SELECT`), comentarios, cadenas, claves de JSON y de YAML, el título de
   Markdown.
3. En cada fichero, cambia `año` por `años` y pulsa **Ctrl+S**.
4. Cierra todas las pestañas. No debe preguntar por cambios sin guardar.
5. En la consola, ejecuta:

   ```powershell
   Show-DemoEncoding
   (Select-String -Path (Join-Path $demo '*') -Pattern 'años' -List).Filename
   ```

   La codificación debe ser la misma que en la preparación, y deben salir los seis ficheros con `años`.

| Fichero | Lenguaje | Resaltado | Edita y guarda | BOM y finales de línea iguales | Notas |
|---|---|---|---|---|---|
| `Programa.cs` | C# | ☐ sí ☐ no | ☐ sí ☐ no | ☐ sí ☐ no | |
| `script.py` | Python | ☐ sí ☐ no | ☐ sí ☐ no | ☐ sí ☐ no | |
| `consulta.sql` | SQL | ☐ sí ☐ no | ☐ sí ☐ no | ☐ sí ☐ no | |
| `ajustes.json` | JSON | ☐ sí ☐ no | ☐ sí ☐ no | ☐ sí ☐ no | |
| `pipeline.yaml` | YAML | ☐ sí ☐ no | ☐ sí ☐ no | ☐ sí ☐ no | |
| `LEEME.md` | Markdown | ☐ sí ☐ no | ☐ sí ☐ no | ☐ sí ☐ no | |

Resultado de F1-2: ☐ cumplido ☐ no cumplido. Observaciones: ____________

## F1-5 · Paneles: redimensionar, reordenar, ocultar y restaurar por proyecto

Con la carpeta de prueba abierta:

1. **Redimensionar.** Arrastra el separador entre el explorador y el editor, y el que hay entre el editor y la
   terminal.
2. **Reordenar.** En el panel de la derecha, arrastra la pestaña **Bitácora de hoy** delante de **Auto Doctor**.
3. **Ocultar.** En la barra de actividad (la columna de iconos de la izquierda), pulsa **Auto Doctor**: el panel
   desaparece. Vuelve a pulsarlo para comprobar que reaparece, y déjalo oculto.
4. Fíjate en cómo ha quedado (o haz una captura).
5. Cierra la aplicación con la ✕ de la ventana y vuelve a abrirla. Se abre el mismo proyecto con los paneles como los
   dejaste.
6. Crea una segunda carpeta y ábrela con **Abrir carpeta**. Debe verse el diseño por defecto. Cambia algo distinto,
   por ejemplo oculta **Historial · bitácora** desde la barra de actividad.

   ```powershell
   New-Item -ItemType Directory -Force -Path (Join-Path $env:TEMP 'agentstudio-demo-f1-b') | Out-Null
   ```

7. Pulsa **Inicio** y abre `agentstudio-demo-f1` desde los proyectos recientes: vuelve su diseño del paso 4. Abre de
   nuevo `agentstudio-demo-f1-b`: vuelve el suyo del paso 6.
8. Cierra y reabre la aplicación otra vez: se abre el último proyecto con su diseño.

| Paso | Esperado | Resultado | Notas |
|---|---|---|---|
| 1 | Los paneles cambian de tamaño | ☐ sí ☐ no | |
| 2 | Las pestañas cambian de orden | ☐ sí ☐ no | |
| 3 | Auto Doctor se oculta y reaparece | ☐ sí ☐ no | |
| 5 | Al reabrir, mismo diseño del proyecto | ☐ sí ☐ no | |
| 6 | Proyecto nuevo con el diseño por defecto | ☐ sí ☐ no | |
| 7 | Cada proyecto recupera su diseño | ☐ sí ☐ no | |
| 8 | Al reabrir, el último proyecto con su diseño | ☐ sí ☐ no | |

Resultado de F1-5: ☐ cumplido ☐ no cumplido. Observaciones: ____________

## Limpieza

```powershell
Remove-Item -Recurse -Force (Join-Path $env:TEMP 'agentstudio-demo-f1'), (Join-Path $env:TEMP 'agentstudio-demo-f1-b')
```

Los diseños guardados quedan en `%APPDATA%\AgentStudio\layouts` (un XML por proyecto) y se pueden borrar a mano. Al
arrancar, la aplicación quita de los recientes un último proyecto que ya no existe.
