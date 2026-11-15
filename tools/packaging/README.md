# MSIX de desarrollo para revisión (F7-6 parcial)

Propuesta de herramientas; no acredita un paquete instalado, firmado, publicado ni actualizado. El script no ejecuta
`dotnet publish`, restaura dependencias, descarga assets, crea certificados ni cambia controles o configuración de Windows.

## Qué necesita

- Windows y PowerShell 7.2 o posterior; SDK de Windows con MakeAppx x64 10.0.19041.0 o posterior en su ruta estándar.
  El script exige firma Authenticode válida de Microsoft y registra su hash; nunca busca el ejecutable en PATH.
- Carpeta **publish revisada y quieta**, destinada a `win-x64`, con apphost, DLL, deps y runtimeconfig de Desktop,
  SkillsMcp y GraphWorker, los assets locales de Monaco/xterm y las bibliotecas nativas SQLite/ONNX/WebView2.
  La preparación de esa carpeta es un paso anterior, autorizado y verificado aparte. No usar un checkout ni copiar
  perfiles, sesiones, configuración personal, bitácora o cachés. Los targets de build pueden descargar assets si faltan:
  esta herramienta no los invoca ni considera esos assets disponibles por el mero hecho de compilar.
- Logos PNG **revisados por una persona**: `StoreLogo.png` 50×50, `Square44x44Logo.png` 44×44 y
  `Square150x150Logo.png` 150×150. Hasta disponer de ellos no hay paquete de producto. Se comprueban firma PNG,
  dimensiones, tamaño máximo de 1 MiB y decodificación con GDI+ (`System.Drawing.Common` de PowerShell en Windows).
  Las capturas de documentación del repositorio no son logos. Los tests generan imágenes sintéticas solo como fixtures.
- Versión MSIX explícita de cuatro componentes (0–65535) y Publisher DN explícito que coincida con el certificado
  que la persona empleará después. No hay identidad de Store: el nombre fijo es `AgentStudio.Development`.

## Revisión y creación, siempre manuales

1. Revisar código, build/tests y la carpeta de publicación concreta en la vía de validación. Comprobar su procedencia,
   licencias, contenido y ausencia de secretos, incluso embebidos en binarios. Los rechazos por nombres/rutas son una
   ayuda contra copias accidentales, **no un análisis exhaustivo de secretos**. Se rechazan enlaces, material de desarrollo,
   credenciales habituales, scripts de shell y paquetes anteriores; solo se admiten los tres apphosts de Agent Studio,
   nunca los ejecutables de proveedores o del sistema. No se ejecuta nada del payload.
2. Desde PowerShell se puede obtener el inventario de solo lectura e inspeccionar todos los ficheros y su SHA-256:

   ```powershell
   Import-Module .\tools\packaging\ReviewMsix.Validation.psm1
   $review = Get-MsixPublishInventory 'C:\AS-Packaging\publish-reviewed'
   $review.files
   $review.sha256
   ```

3. Tras revisar ese inventario, crear una entrega **nueva y privada**, fuera de checkouts o dentro de
   `.agentstudio/tmp/packaging` de esta copia (excluida de Git). Ejemplo de forma; sustituir rutas, versión, DN y hash por valores revisados:

   ```powershell
   pwsh -NoProfile -File .\tools\packaging\New-ReviewMsix.ps1 `
     -PublishDirectory 'C:\AS-Packaging\publish-reviewed' `
     -DestinationRoot 'C:\AS-Packaging\private-reviews' `
     -LogoDirectory 'C:\AS-Packaging\reviewed-product-logos' `
     -Version '1.0.0.0' -Publisher 'CN=SUJETO REAL DEL CERTIFICADO' `
     -ExpectedPayloadHash '<SHA256 REVISADO>'
   ```

   El hash esperado es opcional; usarlo vincula la revisión anterior a estos bytes. La herramienta compara el origen
   antes/después de copiar y empaquetar, y cada fichero del staging con su hash. No sobrescribe entregas: cada intento
   usa un GUID y conserva staging, manifest, log y `review.json` con resultado, runtimes y hashes. Si falla antes de crear
   la entrega solo devuelve error y salida 1. No usar un MSIX parcial de un intento fallido.
4. Revisar `review.json`, el inventario, `staging/AppxManifest.xml`, `makeappx.log` y el hash del MSIX sin firma.
   `PackedUnsignedForHumanReview` significa únicamente que MakeAppx terminó y los bytes comprobados se conservaron.
   No es el veredicto Verified del relevo ni una prueba de instalación o funcionamiento.
5. La persona hace una **copia nueva** del MSIX para firmarla con su certificado de firma de código autorizado.
   Con SignTool del SDK, usando su huella explícita del almacén de certificados (sin contraseña en argumentos):

   ```powershell
   & '<SDK REVISADO>\x64\signtool.exe' sign /fd SHA256 /sha1 '<HUELLA DEL CERTIFICADO>' '<COPIA PARA FIRMAR>.msix'
   & '<SDK REVISADO>\x64\signtool.exe' verify /pa /v '<COPIA FIRMADA>.msix'
   ```

   No se crea ni importa un certificado con estas herramientas. La confianza y su distribución las decide y realiza
   la persona; Publisher debe coincidir con el Subject del certificado. Conservar también el hash del fichero firmado.
6. En un equipo limpio de prueba Windows 11 x64 (build 22000 o posterior), la persona comprueba la confianza del
   certificado y los requisitos siguientes, y decide instalar el **MSIX firmado** con el instalador de Windows o
   `Add-AppxPackage -Path '<COPIA FIRMADA>.msix'`. Registrar salida y recorrido real: arranque, editor, terminal,
   auxiliares MCP/GraphWorker, apertura de proyecto, carpetas de datos, Hello y salida. Cualquier incompatibilidad queda
   pendiente: el script no añade redirecciones, excepciones ni permisos para ocultarla.

## Runtime y permisos reales

La app es WPF/WinForms `net10.0-windows10.0.22000.0`. Si su runtimeconfig declara dependencia de
`Microsoft.WindowsDesktop.App` 10, el equipo necesita el .NET 10 Desktop Runtime x64 compatible con su política de
roll-forward; el SDK de desarrollo no sustituye esta comprobación. Los auxiliares también requieren lo que declaren
sus runtimeconfig. El informe distingue dependiente del framework y autocontenido por cada entrada; para este último
comprueba `coreclr.dll`, `hostfxr.dll` y `hostpolicy.dll`, sin afirmar que sustituya la prueba en equipo limpio.
WebView2 Runtime sigue siendo necesario aunque .NET vaya autocontenido; no basta asumir que Edge esté instalado.
Los CLIs oficiales, Git y herramientas opcionales siguen teniendo sus requisitos externos y su inicio de sesión manual:
no se incluyen ejecutables ni perfiles del usuario. Tampoco se incluyen modelos descargados ni índices personales.

El manifest exige `runFullTrust`, necesario para esta app de escritorio y sus procesos. **MSIX no confina la app ni
impide sus lecturas o su red.** Omitir `internetClient` o `broadFileSystemAccess` no garantiza esos bloqueos en FullTrust;
siguen contando las compuertas e invariantes del producto. La identidad y virtualización de MSIX pueden cambiar las rutas
efectivas: comprobarlas en el recorrido limpio y no migrar secretos o datos silenciosamente.

## Actualización manual y validación pendiente

Para actualizar, preparar y revisar otra entrega con el mismo Name/Publisher/arquitectura y una **versión mayor**,
firmarla y verificarla de nuevo; la persona aplica ese MSIX firmado y comprueba conservación de datos y funcionamiento.
No hay AppInstaller, URL, tarea programada ni actualización automática. Conservar la entrega previa; no asumir que una
versión inferior se pueda reinstalar como rollback ni desinstalar la anterior sin revisar antes sus datos.

`Test-ReviewMsixValidation.ps1` contiene comprobaciones deterministas de inventario, mutaciones, archivos privados,
componente ausente, arquitectura, logos y manifest/capabilities. El contrato del manifest fija Windows.Desktop 22000,
una sola aplicación y `runFullTrust`, y rechaza extensiones, nodos extranjeros, duplicados y atributos extra. Compara
el nombre local y la URI del espacio de nombres: cambiar un prefijo no elude la validación ni invalida un alias equivalente.
Solo escribe fixtures nuevos en TEMP, los conserva y
no llama MakeAppx, certificados, dotnet ni app. Su salida no acredita una instalación. Ejecutarlo corresponde al
principal cuando libere su carril. El 2026-11-15 el principal ejecutó las 36 comprobaciones con resultado correcto;
no se llamó a MakeAppx ni se produjo, firmó o instaló un paquete. El resultado solo acredita estas validaciones con fixtures.

Fuentes técnicas: [manifest de escritorio](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion),
[MakeAppx](https://learn.microsoft.com/en-us/windows/msix/package/create-app-package-with-makeappx-tool),
[firma con SignTool](https://learn.microsoft.com/en-us/windows/msix/package/sign-app-package-using-signtool),
[publicación .NET](https://learn.microsoft.com/en-us/dotnet/core/deploying/) y
[distribución WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution).
