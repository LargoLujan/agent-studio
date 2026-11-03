# Spike: verificación aislada bajo integridad Low (sin admin)

Arnés desechable que comprueba si `dotnet build` / `dotnet test` pueden ejecutarse bajo un **token de integridad
Low** (S-1-16-4096) sin privilegios de administrador, de modo que la política obligatoria de Windows
(*no-write-up*) bloquee las escrituras fuera de carpetas Low dedicadas y, aun así, la compilación y las pruebas
funcionen. Es la evidencia de `docs/investigacion/2026-10-16-spike-verificacion-aislada.md`.

No forma parte de `AgentStudio.slnx` y no se compila con la solución. Usa solo la BCL y P/Invoke
(`LibraryImport`, sin APIs obsoletas); no referencia paquetes.

## Qué hace

1. Crea un directorio de trabajo bajo `%TEMP%\as-lowil-spike\<marca>` con carpetas Low (trabajo, caché NuGet
   dedicada, `DOTNET_CLI_HOME`, `TEMP`) y una carpeta Medium (`medium-repo`) que hace de repositorio del usuario.
2. Etiqueta las carpetas Low con `SetNamedSecurityInfo` (ACE de etiqueta obligatoria, SDDL `S:(ML;OICI;NW;;;LW)`).
3. Construye un token primario Low duplicando el propio y bajándole el nivel de integridad, y lanza procesos hijo
   con `CreateProcessAsUser`, cada uno dentro de su propio Job Object con *kill-on-close*.
4. Verbo `run`: ejecuta las pruebas a-e (solo lanza hijos Low; ningún `dotnet` a integridad Medium).
5. Verbo `overhead`: mide restore en frío y en caliente, build y test bajo Low frente a una línea base Medium que usa
   el `DOTNET_CLI_HOME` del usuario y una caché de paquetes nueva (nunca la caché real).

## Componentes

| Fichero | Papel |
|---|---|
| `Native/NativeMethods.cs` | Superficie P/Invoke (`LibraryImport`): tokens, `CreateProcessAsUser`, etiqueta obligatoria y Job Object. |
| `Native/LowIntegrityLauncher.cs` | Baja el token y lanza hijos Low con entorno, directorio y stdout/stderr redirigidos. |
| `Native/IntegrityLabeler.cs` | Aplica la etiqueta Low a una carpeta con `SetNamedSecurityInfo`. |
| `Native/KillOnCloseJob.cs` | Job Object con *kill-on-close* (igual que `src/.../Processes/JobObject.cs`). |
| `Harness.cs` | Orquesta las pruebas a-e (`harness.log`) y la medición del sobrecoste (`overhead.log`). |
| `Program.cs` | Punto de entrada y modos hijo (`probe-token`, `probe-net`, `probe-write`, `spawn-tree`). |
| `Fixtures/TinyTests/` | Proyecto xUnit v3 (MTP) copiado a la carpeta Low; incluye un target MSBuild malicioso guardado por propiedad. |

## Cómo ejecutarlo

```powershell
dotnet build spikes/low-il-verification/LowIlVerification.csproj -c Debug
spikes/low-il-verification/bin/Debug/net10.0-windows/LowIlVerification.exe run
spikes/low-il-verification/bin/Debug/net10.0-windows/LowIlVerification.exe overhead
```

Requiere red (el *restore* en frío descarga de nuget.org). No necesita administrador. No abre ventanas y cierra
todos los procesos que arranca. El resultado se imprime y se guarda en `%TEMP%\as-lowil-spike\<marca>-<verbo>\`;
esas carpetas no se versionan y se pueden borrar.

Nota: no ejecutar a mano un `dotnet` a integridad Medium con un `DOTNET_CLI_HOME` nuevo sin
`DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false`: la primera ejecución del SDK añade `<DOTNET_CLI_HOME>\.dotnet\tools` al PATH
persistente del usuario.
