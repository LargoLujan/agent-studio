# Spike: verificación aislada bajo integridad Low — 2026-10-16

> Respuesta al hallazgo crítico de la revisión adversarial: `VerificationRunner` ejecuta `dotnet build` y `dotnet test`
> sobre código escrito por un agente con el token del usuario (integridad Medium), así que un target de MSBuild o una
> prueba pueden escribir en cualquier sitio donde escriba el usuario (su repositorio, `.git/hooks`, los almacenes SQLite
> de la app). El Job Object solo mata el árbol de procesos.
> Etiquetas: **V** = verificado ejecutándolo en esta máquina; **D** = verificado en documentación oficial; **NV** = no
> verificado. El código está en [`spikes/low-il-verification/`](../../spikes/low-il-verification/README.md), fuera de
> `AgentStudio.slnx`. No se ha tocado código de producción ni se ha ejecutado ningún CLI de proveedor.
> Datos personales sustituidos: el usuario pasa a `usuario` (`C:\Users\usuario`), la carpeta de la ejecución a
> `<base>` (`C:\Users\usuario\AppData\Local\Temp\as-lowil-spike\<marca>`) y el worktree a `<repo>`.

## Respuesta corta

**Sí.** `dotnet restore`, `dotnet build` y `dotnet test` (xUnit v3 sobre Microsoft.Testing.Platform, con informe TRX)
funcionan bajo un token de integridad **Low** creado sin privilegios de administrador, y la política obligatoria de
Windows bloquea toda escritura fuera de las carpetas etiquetadas Low: un target `BeforeTargets="Build"` y un método de
prueba que intentan escribir en el «repositorio del usuario» fallan con *access denied*. El Job Object sigue conteniendo
y matando el árbol. El sobrecoste de la integridad Low en sí es despreciable; lo que cuesta es la caché NuGet en frío.

Lo que **no** resuelve: Low IL no impide **leer** (cualquier fichero que lea el usuario) ni usar la **red**, y no
separa entre sí dos verificaciones Low que corran a la vez. Ver [Riesgos y huecos](#riesgos-y-huecos).

| Prueba | Resultado | Estado |
|---|---|---|
| Token | El hijo corre con `S-1-16-4096` (Low); el padre con `S-1-16-8192` (Medium) | V |
| a | `restore` en frío, `build` y `test` bajo Low terminan con 0 y el TRX se escribe (3 correctas) | V |
| b | Target MSBuild malicioso → `MSB3491 ... Access to the path ... is denied`; prueba maliciosa → `UnauthorizedAccessException`; nada aparece en la carpeta Medium | V |
| c | Escribir dentro de la carpeta Low funciona (desde una prueba, un target y un proceso normal) | V |
| d | Leer un fichero Medium **funciona**; DNS y TCP saliente (443) **funcionan** | V (riesgo residual) |
| e | El Job Object con *kill-on-close* mata al hijo Low y a su nieto al cerrarse | V |
| Sobrecoste | Restore en frío Low ≈ Medium (7,5–11,2 s frente a 7,8–9,5 s, dominado por el orden); build idéntico | V |

## Entorno

| Elemento | Valor |
|---|---|
| Windows | 11 Pro 10.0.26300 |
| .NET SDK | 10.0.303 (`global.json` con `test.runner = Microsoft.Testing.Platform`) |
| Paquetes de prueba | `xunit.v3` 4.0.2, `Microsoft.NET.Test.Sdk` 18.11.0, `xunit.runner.visualstudio` 4.0.1 (22 paquetes, 114 MB en frío) |
| Cuenta | Administrador filtrado por UAC: `BUILTIN\Administradores` como «grupo usado solo para denegar» |
| Integridad del shell | `Etiqueta obligatoria\Nivel obligatorio medio` (`S-1-16-8192`) |
| Privilegios del shell | Solo `SeShutdown`, `SeChangeNotify`, `SeUndock`, `SeIncreaseWorkingSet`, `SeTimeZone`. **Sin** `SeAssignPrimaryToken`, `SeIncreaseQuota` ni `SeImpersonate` |
| Windows Sandbox | No instalado (`C:\Windows\System32\WindowsSandbox.exe` no existe) |
| Modo desarrollador | Activado (`AllowDevelopmentWithoutDevLicense = 1`); relevante para los enlaces simbólicos |

Es decir: las pruebas se hicieron exactamente en el caso que importa, un usuario sin elevar y sin privilegios
especiales.

## Enfoque

El arnés (`LowIlVerification.exe run`, a integridad Medium) prepara esta estructura y lanza cada paso con un token Low:

```text
<base>\low-work      Low  (etiqueta OICI)  copia del proyecto de prueba, bin/obj y el TRX
<base>\low-nuget     Low                   NUGET_PACKAGES dedicado (vacío al empezar)
<base>\low-home      Low                   DOTNET_CLI_HOME
<base>\low-temp      Low                   TEMP y TMP
<base>\medium-repo   Medium (sin etiqueta) hace de repositorio del usuario: readable.txt y destino de los ataques
<base>\logs          Medium                salida de cada hijo (fichero abierto por el padre y heredado)
```

El proyecto de prueba (`Fixtures/TinyTests`) es xUnit v3 con tres pruebas (`LowWorkFolderIsWritable`,
`MediumFolderIsNotWritable`, `MediumFileIsReadable`) y un target `EvilBeforeBuild` (`BeforeTargets="Build"`, con
`WriteLinesToFile` a la ruta que diga `-p:EvilPath`). Se copia a `low-work` antes de cada ejecución, fuera del árbol
del repositorio, así que no hereda `Directory.Build.props` ni `Directory.Packages.props`; lleva su propio `global.json`
(modo MTP) y un `nuget.config` con solo nuget.org.

## APIs exactas

Todo con `LibraryImport` (generador de código fuente) y sin APIs obsoletas (`Native/NativeMethods.cs`).

### Token Low y lanzamiento (`Native/LowIntegrityLauncher.cs`)

1. `OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_ADJUST_DEFAULT | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY)`.
2. `DuplicateTokenEx(token, 0, NULL, SecurityImpersonation, TokenPrimary)` → token primario nuevo.
3. `ConvertStringSidToSidW("S-1-16-4096")` y `SetTokenInformation(nuevo, TokenIntegrityLevel, TOKEN_MANDATORY_LABEL { Sid, SE_GROUP_INTEGRITY }, 16)`.
4. `CreateProcessAsUserW(nuevo, <exe>, <línea de órdenes en un búfer escribible>, NULL, NULL, TRUE,
   CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW | CREATE_SUSPENDED, <bloque de entorno ordenado>, <directorio>,
   STARTUPINFOW { STARTF_USESTDHANDLES, stdin = NUL, stdout = stderr = fichero heredable })`.
5. `AssignProcessToJobObject(job, hProcess)` y `ResumeThread(hThread)`: el hijo entra en el Job antes de ejecutar nada.

- **D** — `CreateProcessAsUser` no necesita `SE_ASSIGNPRIMARYTOKEN_NAME` cuando el token es «una versión restringida
  del token primario del llamador»
  ([CreateProcessAsUserW](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessasuserw),
  [Restricted Tokens](https://learn.microsoft.com/windows/win32/secauthz/restricted-tokens)).
  **V** — Funciona con el token de esta cuenta, que no tiene `SeAssignPrimaryToken` ni `SeIncreaseQuota`.
- **D** — `CreateProcessWithTokenW` exige `SE_IMPERSONATE_NAME`
  ([documentación](https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-createprocesswithtokenw)), que este
  usuario no tiene: no sirve aquí y no hizo falta.
- **V** — Bajar el nivel de integridad de un duplicado del propio token funciona sin ningún privilegio.
- **D** — Un objeto sin etiqueta cuenta como Medium y por defecto todo objeto tiene `NO_WRITE_UP`, así que un proceso
  Low no puede escribir en él aunque la DACL se lo permita
  ([Mandatory Integrity Control](https://learn.microsoft.com/windows/win32/secauthz/mandatory-integrity-control)).

### Etiqueta Low en carpetas (`Native/IntegrityLabeler.cs`)

1. `ConvertStringSecurityDescriptorToSecurityDescriptorW("S:(ML;OICI;NW;;;LW)", SDDL_REVISION_1)`: ACE de etiqueta
   obligatoria, nivel Low (`LW`), *no-write-up* (`NW`), heredable por ficheros y carpetas (`OICI`).
2. `GetSecurityDescriptorSacl` para sacar la SACL.
3. `SetNamedSecurityInfoW(ruta, SE_FILE_OBJECT, LABEL_SECURITY_INFORMATION, NULL, NULL, NULL, sacl)`; `LocalFree`.

- **V** — Un usuario estándar lo hace sobre carpetas que ha creado: la llamada devuelve 0. El usuario tiene control
  total sobre sus carpetas de `%TEMP%`, lo que incluye `WRITE_OWNER` (el derecho que pide cambiar la etiqueta), y el
  nivel nuevo es inferior al suyo. Lectura posterior con `icacls`:

```text
labelled Low: <base>\low-work
  icacls: Etiqueta obligatoria\Nivel obligatorio bajo:(OI)(CI)(NW)
labelled Low: <base>\low-nuget        (igual para low-home y low-temp)
medium-repo (sin etiqueta): (ninguna -> Medium por defecto)
```

- **V** — Lo que se crea dentro hereda la etiqueta: el TRX que escribe el host de pruebas y el `project.assets.json`
  muestran `Nivel obligatorio bajo:(I)(NW)`, igual que los 706 ficheros del árbol del repositorio extraídos en una carpeta
  Low (ver [Recomendación](#recomendación-de-integración)). No hace falta etiquetar fichero a fichero.
- **V** — Windows ya trae carpetas Low: `%USERPROFILE%\AppData\LocalLow` y `%TEMP%\Low` muestran
  `Nivel obligatorio bajo:(OI)(CI)(NW)`. Se leyeron con `icacls`; no se tocaron.

### Job Object (`Native/KillOnCloseJob.cs`)

`CreateJobObjectW` + `SetInformationJobObject(JobObjectExtendedLimitInformation, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)`,
igual que `src/AgentStudio.Infrastructure/Processes/JobObject.cs`. En la versión final del arnés **cada** lanzamiento
va en su propio Job, que se cierra al terminar el paso: así un nodo de MSBuild, un servidor de compilación o un nieto
desligado no sobreviven, y el tiempo límite mata el árbol entero.

### Tres trampas encontradas al implementarlo (V)

1. **`ERROR_BAD_LENGTH` (24) en `SetTokenInformation`.** `TOKEN_MANDATORY_LABEL` es `{ PSID; DWORD }`; en x64 el
   puntero fuerza alineación a 8 y `sizeof` es 16, no 12. Con 12 la llamada falla.
2. **El hijo pierde sus argumentos con `ref string`.** Declarar `lpCommandLine` como
   `[MarshalUsing(typeof(Utf16StringMarshaller))] ref string` hace que el generador pase `ushort**` (se ve en el stub
   generado, `&__commandLine_native`), no `LPWSTR`. `CreateProcessAsUser` arranca el proceso igualmente, pero sin
   argumentos: `dotnet` imprimió su ayuda y salió con `0x80008081`, y el propio arnés, al no recibir verbo, se relanzó a
   sí mismo en bucle hasta los tiempos límite (todos los procesos acabaron cerrados; ahora el arnés exige un verbo
   explícito). Arreglo: un búfer propio y escribible (`Marshal.StringToHGlobalUni`) pasado como `nint`.
3. **`SYSLIB1051`.** Un campo `bool` en `SECURITY_ATTRIBUTES` hace la estructura no blittable y `LibraryImport` la
   rechaza; se declara como `int`.

Una cuarta observación con consecuencias de diseño: **los handles heredados se saltan la comprobación de integridad.**
La etiqueta se comprueba al abrir; el fichero de salida lo abre el padre (Medium) en una carpeta Medium y el hijo Low
escribe en él sin problema a través del handle heredado (V: así se capturó toda la salida). Es justo lo que se quiere
para stdout/stderr, pero obliga a que el lanzador de producción herede **solo** esos handles (ver la recomendación).

## Entorno del proceso hijo

| Variable | Valor | Por qué |
|---|---|---|
| `NUGET_PACKAGES` | carpeta Low | La caché global de paquetes tiene que ser escribible por el restore Low |
| `DOTNET_CLI_HOME` | carpeta Low | Centinelas de primera ejecución, `.dotnet\tools`, cachés del CLI |
| `TEMP`, `TMP` | carpeta Low | MSBuild, NuGet (`NuGetScratch`) y el host de pruebas escriben temporales |
| `DOTNET_CLI_TELEMETRY_OPTOUT`, `DOTNET_NOLOGO` | `1` | Sin telemetría ni banner |
| `DOTNET_SKIP_FIRST_TIME_EXPERIENCE` | `1` | Pedido en el encargo, pero **D** — ya no se admite desde .NET Core 3.0 ([variables de entorno de .NET](https://learn.microsoft.com/dotnet/core/tools/dotnet-environment-variables)); no evita nada |
| `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH` | `false` | **Necesaria**: ver la lección siguiente |
| `DOTNET_GENERATE_ASPNET_CERTIFICATE` | `false` | La primera ejecución no intenta crear el certificado de desarrollo |
| `MSBUILDDISABLENODEREUSE`, `DOTNET_CLI_USE_MSBUILD_SERVER` | `1`, `0` | Ningún nodo sobrevive al paso (ya en `StepEnvironment`) |
| `UseSharedCompilation` | `false` | Sin servidor de compilación residente (ya en `StepEnvironment`) |
| `DOTNET_CLI_UI_LANGUAGE` | `en` | Consola en inglés (ya en `StepEnvironment`) |

**Lección (V): la primera ejecución del SDK escribe en el PATH del usuario.** Con un `DOTNET_CLI_HOME` nuevo, el SDK
añade `<DOTNET_CLI_HOME>\.dotnet\tools` al PATH persistente del usuario (`HKCU\Environment`)
(**D**: «This location is added to the user's path when the SDK is first run»,
[.NET tools](https://learn.microsoft.com/dotnet/core/tools/global-tools)).

- Bajo Low la escritura se bloqueó y el SDK lo avisó:
  `Failed to add '<base>\low-home\.dotnet\tools' to the PATH environment variable.` Es una prueba más de que Low
  tampoco puede escribir en el registro del usuario.
- La comparación Medium de una ejecución intermedia del arnés usó un `DOTNET_CLI_HOME` nuevo **a Medium** y el SDK sí
  escribió el PATH del usuario. Por eso la versión final no lanza nunca un `dotnet` Medium con un CLI home nuevo (la
  línea base Medium usa el del usuario, ya inicializado) y pasa `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false` en ambos lados.
  Con esa variable el aviso desaparece también bajo Low (V), y tras la medición del sobrecoste el PATH no tenía entradas
  nuevas (V).
- Las dos entradas que dejaron las ejecuciones intermedias (`...\as-lowil-spike\<marca>\normal-home\.dotnet\tools`)
  siguen en el PATH de usuario y hay que quitarlas a mano: el spike no modifica el registro del usuario.

## Resultados

Ejecución final: `LowIlVerification.exe run`, 12 comprobaciones, 0 fallos. Extractos (sin recortar salvo las rutas).

### Token: el hijo es Low

```text
parent integrity SID: S-1-16-8192
-- probe-token: exit=0 timedOut=False elapsed=0,5s
   INTEGRITY_SID=S-1-16-4096
   USER=usuario
```

El hijo lee su propio `TokenIntegrityLevel` con `GetTokenInformation`. Es el mismo usuario: el token es un duplicado
del del padre con otro nivel de integridad.

### a — restore, build y test bajo Low

```text
dotnet restore <base>\low-work\TinyTests --no-http-cache
     Determining projects to restore...
     Restored <base>\low-work\TinyTests\TinyTests.csproj (in 9,14 sec).
-- restore-low: exit=0 elapsed=15,8s

dotnet build <base>\low-work\TinyTests --no-restore -c Debug
     TinyTests -> <base>\low-work\TinyTests\bin\Debug\net10.0\TinyTests.dll
   Build succeeded.  0 Warning(s)  0 Error(s)  Time Elapsed 00:00:12.75

dotnet test <base>\low-work\TinyTests --no-build -c Debug --results-directory <base>\low-work\trx
            -- --report-xunit-trx --report-xunit-trx-filename tiny.trx --zero-tests-policy strict
   Running tests from <base>\low-work\TinyTests\bin\Debug\net10.0\TinyTests.dll (net10.0|x64)
     In process file artifacts produced:
       - <base>\low-work\trx\tiny.trx
   Test run summary: Passed!  total: 3  failed: 0  succeeded: 3  skipped: 0
   trx: TinyTests.VerificationProofs.MediumFolderIsNotWritable -> Passed
   trx: TinyTests.VerificationProofs.LowWorkFolderIsWritable -> Passed
   trx: TinyTests.VerificationProofs.MediumFileIsReadable -> Passed
```

- **V** — El restore en frío descarga los 22 paquetes de nuget.org a la caché Low dedicada; el build y el test usan las
  banderas de `DotNetVerificationProfile` (S4) y el TRX queda en la carpeta Low con la etiqueta heredada.

### b — escribir fuera de la carpeta Low falla

Target MSBuild malicioso (`-p:EnableEvilTarget=true -p:EvilPath=<base>\medium-repo\pwned-build.txt`):

```text
     EVIL target attempting to write <base>\medium-repo\pwned-build.txt
   <base>\low-work\TinyTests\TinyTests.csproj(28,5): error MSB3491: Could not write lines to file
     "<base>\medium-repo\pwned-build.txt". Access to the path '<base>\medium-repo\r2tkc22b.wyj~' is denied.
   Build FAILED.
-- build-evil-medium: exit=1
```

(`WriteLinesToFile` escribe primero un temporal junto al destino; es ese el que se deniega.)

Método de prueba: `MediumFolderIsNotWritable` intenta `File.WriteAllText(<base>\medium-repo\pwned-by-test.txt)`,
exige `UnauthorizedAccessException` y que el fichero no exista; sale `Passed` en el TRX de arriba. Un proceso Low
normal (`probe-write`) da lo mismo:

```text
WRITE_MEDIUM blocked UnauthorizedAccessException: Access to the path '<base>\medium-repo\probe-write-medium.txt' is denied.
```

Al final, `medium-repo` solo contiene `readable.txt`: ningún ataque dejó rastro (V).

**Contraprueba (V).** El mismo proyecto ejecutado a Medium, como hoy hace `VerificationRunner` (verbo `overhead`): la
prueba `MediumFolderIsNotWritable` **falla** y `pwned-by-test.txt` aparece en la carpeta Medium de control. Sin el
token Low el ataque funciona; con él, no.

### c — escribir dentro de la carpeta Low funciona

```text
WRITE_LOW ok -> <base>\low-work\probe-write-low.txt
     EVIL target attempting to write <base>\low-work\pwned-build-low.txt
     EVIL target wrote <base>\low-work\pwned-build-low.txt
   Build succeeded.
```

y `LowWorkFolderIsWritable` (`Passed`) crea `low-write-proof.txt`. El mismo target que falla contra Medium funciona contra
Low: lo que bloquea es la etiqueta, no la lógica del target.

### d — leer y usar la red siguen funcionando (riesgo residual)

- **V** — `MediumFileIsReadable` lee `<base>\medium-repo\readable.txt` (Medium) y pasa. Low es *no-write-up*, no
  *no-read-up*: el código de un agente que corre bajo Low lee todo lo que lee el usuario.
- **V** — Red desde un proceso Low:

```text
DNS api.nuget.org -> 150.171.109.83
TCP api.nuget.org:443 CONNECTED
TCP 127.0.0.1:1 SocketException ConnectionRefused
```

El `ConnectionRefused` de un puerto local cerrado muestra que el bucle local también es alcanzable (lo rechaza la pila,
no una política). Y el propio restore en frío descargó 114 MB desde el proceso Low. Juntas, lectura y red permiten
**exfiltrar** lo que el usuario pueda leer.

### e — el Job Object contiene al hijo Low

```text
== Job Object kill-on-close sobre el hijo Low ==
child PID=47848 grandchild PID=31316 running-before-close=True
after job close: child dead=True (exit code 0), grandchild dead=True
```

El hijo (`spawn-tree`, Low) se crea suspendido, entra en el Job, se reanuda y lanza `ping` (nieto, también Low). Al
cerrar el único handle del Job el sistema mata a los dos. **V** — Un proceso matado así sale con **código 0**: el código
de salida no distingue un *kill*. `StreamingProcessRunner` ya devuelve −1 cuando es él quien mata, y la verificación juzga
por el TRX, así que no le afecta, pero un lanzador nuevo debe conservar esa regla.

## Sobrecoste

Verbo `overhead`: el mismo proyecto restaurado en carpetas de paquetes vacías con `--no-http-cache`, alternando qué
lado va primero, tras un calentamiento que no se mide. El lado Medium usa el CLI home del usuario (ya inicializado) y
una caché de paquetes nueva, nunca la real.

| Fase | Low (s) | Medium (s) | Tiempo de NuGet Low / Medium |
|---|---|---|---|
| Restore en frío, ronda 1 (Low primero) | 11,2 | 7,8 | 6,62 / 5,34 |
| Restore en frío, ronda 2 (Medium primero) | 7,5 | 9,5 | 5,24 / 5,78 |
| Restore en caliente (`--force`, paquetes ya presentes) | 3,8 | 3,3 | 0,85 / 0,73 |
| Primer build (`--no-restore`) | 12,4 | 12,3 | — |
| Test (`--no-build`) | 6,4 (3 correctas) | 9,0 (1 fallo, la contraprueba) | — |

- **V** — El lado que va primero es el lento en las dos rondas: el orden pesa más que la integridad. De media el restore
  en frío tarda 9,4 s bajo Low y 8,7 s a Medium, dentro del ruido; el build es idéntico. La integridad Low en sí no
  añade un coste medible.
- **V** — Lo que sí cuesta es la caché: hoy `VerificationRunner` restaura contra la caché caliente del usuario (≈ 3 s
  aquí); con una caché Low **por ejecución** cada verificación paga un restore en frío (7,5–11,2 s para 22 paquetes y
  114 MB; crece con el grafo de paquetes del repositorio). Una caché Low persistente vuelve al coste en caliente, pero
  ver el riesgo de envenenamiento más abajo.
- **V** — Copiar el árbol del repositorio a la carpeta Low (opción recomendada): `git archive` de HEAD (706 ficheros,
  tar de 9,1 MB) tarda 3,2 s y extraerlo con `tar -xf` en una carpeta Low 2,2–3,6 s (tres repeticiones). Los ficheros
  heredan la etiqueta.
- **V** — En la ejecución intermedia, sin `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH` ni `DOTNET_GENERATE_ASPNET_CERTIFICATE`, el
  primer restore Low tardó 26,3 s frente a 15,8 s en la final. **NV** — Atribuirlo a esos pasos de primera ejecución.

## Riesgos y huecos

### Lecturas (V, riesgo residual principal)

Un proceso Low lee todo lo que lee el usuario: otros repositorios, `%USERPROFILE%`, la configuración de NuGet (que puede
llevar credenciales de feeds), los perfiles de navegador y las credenciales de los CLIs de proveedor. Este spike no ha
leído ninguno de esos ficheros; la consecuencia sale de la prueba d. Opciones:

- **AppContainer** bloquea también la lectura (ver la comparativa).
- La etiqueta obligatoria admite `NO_READ_UP` (**D**,
  [SYSTEM_MANDATORY_LABEL_ACE](https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-system_mandatory_label_ace)).
  Agent Studio podría etiquetar **sus propias** carpetas de datos (`.agentstudio`, evidencias, SQLite) como
  `S:(ML;OICI;NWNR;;;ME)` para que una verificación Low no pueda leerlas. **NV** — No probado, y nunca sobre carpetas del
  usuario.

### Red (V)

DNS, TCP saliente y el bucle local funcionan bajo Low. No hay forma sin administrador de cortar la red a un proceso Low:
las reglas de firewall necesitan elevación. AppContainer sin la capacidad `internetClient` no tiene red (**D**,
[Launch an AppContainer](https://learn.microsoft.com/windows/win32/secauthz/implementing-an-appcontainer)), pero el
restore la necesita, y el restore también ejecuta MSBuild del agente (puede engancharse a sus targets): no es un paso de
confianza. Un diseño en dos fases (restore con red, build y test sin red) queda como posible siguiente spike (**NV**).

### Enlaces simbólicos, *junctions* y enlaces duros (NV)

No se han probado. El riesgo no es que el proceso Low escriba hacia arriba a través de un enlace (la comprobación de
integridad se hace sobre el objeto que se abre, el destino), sino el **ayudante confundido**: que Agent Studio (Medium)
siga, al leer, copiar o borrar la carpeta Low, un enlace que el código del agente dejó ahí y acabe leyendo o borrando
algo del usuario. En esta máquina el modo desarrollador está activado, lo que permite crear enlaces simbólicos sin
privilegio a procesos Medium; si también a procesos Low es **NV**. Mitigaciones para la integración:

- Leer el TRX solo cuando el Job ya se cerró (ningún proceso Low vivo), abriéndolo sin seguir puntos de reparación y
  comprobando que es un fichero regular dentro de la carpeta de resultados y con un tamaño acotado.
- Borrar la carpeta Low con un borrado que no siga enlaces (la capa de git ya aparta los worktrees a `.trash` y los
  borra así; reutilizarlo).
- No copiar nunca nada de la carpeta Low al worktree ni al repositorio.

### Verificaciones Low simultáneas (D, por diseño)

Todas las verificaciones Low, y cualquier otro proceso Low del usuario, son el mismo usuario con el mismo nivel: la
integridad no las separa entre sí ni de ellos. Con
implementadores en paralelo, el código del agente de la tarea A podría escribir en la carpeta Low de la tarea B mientras
se verifica (su TRX, sus binarios) y falsear el veredicto del juez de B. Hasta tener una identidad por ejecución
(AppContainer por ejecución, o un token restringido con un SID propio), las verificaciones aisladas deben ir **de una
en una** y la raíz Low se vacía entre ejecuciones.

### Caché NuGet compartida (diseño)

Una caché Low reutilizada entre ejecuciones es escribible por cualquier build Low: un agente puede alterar el `.targets`
o una DLL de un paquete y comprometer las verificaciones siguientes (que ejecutan ese paquete). Por eso la recomendación
es una caché por ejecución. Alternativa a probar (**NV**): usar la caché real del usuario como *fallback* de solo
lectura (es Medium, Low no puede escribirla; propiedad `RestoreAdditionalProjectFallbackFolders`) más una carpeta Low
por ejecución para lo que falte.

### Otras superficies

- **D** — UIPI bloquea los mensajes de ventana por encima de `WM_USER` que vienen de un proceso de menor integridad
  ([ChangeWindowMessageFilter](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-changewindowmessagefilter)).
- **D** — COM impide por defecto que un cliente Low se enlace a servidores COM ya en marcha
  ([The COM Elevation Moniker](https://learn.microsoft.com/windows/win32/com/the-com-elevation-moniker)).
- **V** — Low no pudo escribir `HKCU\Environment` (el aviso del SDK de arriba).
- **NV** — Pipes con nombre, RPC u otros servicios de procesos Medium del usuario que acepten clientes Low por su cuenta.
- **NV** — Agent Studio elevado: el token duplicado conservaría los grupos de administrador. El lanzador debe negarse a
  aislar desde un proceso con integridad High o superior.
- Fuera del alcance: el propio CLI del agente sigue corriendo a Medium; su aislamiento depende del sandbox de cada
  proveedor.

## Alternativas comparadas

| Opción | Sin admin | Bloquea escrituras | Bloquea lecturas | Bloquea red | Coste de adopción |
|---|---|---|---|---|---|
| **Integridad Low** (elegida) | Sí (V) | Sí (V) | No (V) | No (V) | Bajo: un lanzador nativo y carpetas etiquetadas; el SDK funciona con unas variables (V) |
| AppContainer | Sí (D) | Sí | Sí, salvo lo concedido a su SID (D) | Sí, sin capacidades; tampoco bucle local (D) | Alto: perfil (`CreateAppContainerProfile`), ACE para el SID del contenedor en cada carpeta que use el SDK y la caché, capacidad de red para el restore; compatibilidad de MSBuild/MTP **NV** |
| Token restringido (`CreateRestrictedToken`) | Sí (D) | Sí, si la DACL no concede al SID restrictivo (D) | Solo con SIDs restrictivos completos, que rompen el acceso al perfil y a `HKCU` | No | Medio-alto: hay que conceder ACE en cada carpeta escribible y el SDK lee mucho del perfil |
| Windows Sandbox | No: activar la característica exige admin (D) | Sí (VM) | Sí, salvo carpetas mapeadas (D) | Configurable (D) | Alto: no está instalado aquí (V); una VM por arranque, el SDK no está dentro, un solo Sandbox a la vez (D) |

Sobre el token restringido: en esta máquina `%TEMP%` y `LocalLow` tienen ACE de SID sintéticos y de un grupo local
`CodexSandboxUsers` (V), que por el nombre parecen de un sandbox basado en DACL (atribución **NV**). Ilustra su coste:
obliga a tocar las ACL de carpetas compartidas del usuario.

Low IL es la única opción que se ha comprobado de punta a punta sin administrador y resuelve exactamente el hallazgo
(escrituras fuera del área de trabajo). AppContainer es la mejora natural cuando importen las lecturas y la red, y la
forma de separar verificaciones simultáneas.

## Recomendación de integración

1. **Contrato (`AgentStudio.Application`).** `ProcessSpec` gana un campo opcional `ProcessIsolation Isolation =
   ProcessIsolation.None`, con `None` y `LowIntegrity`. Solo `VerificationRunner` lo pide; el resto de llamadas no cambia.
2. **Lanzador (`AgentStudio.Infrastructure/Processes`).** `System.Diagnostics.Process` no admite un token, así que
   `ProcessLaunch.Start` deriva a un `LowIntegrityProcessLauncher` cuando `Isolation = LowIntegrity`:
   - token Low una vez por proceso, como en el spike;
   - `CreateProcessAsUserW` con `CREATE_SUSPENDED`, Job propio por sesión (anidado en el compartido), `ResumeThread`;
   - stdout/stderr por tuberías anónimas y `STARTUPINFOEX` con `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` para heredar **solo**
     esas dos (D, `UpdateProcThreadAttribute`; **NV** en el spike, que usó un fichero y `bInheritHandles`): Agent
     Studio tiene muchos handles abiertos y uno heredado por error sería escribible desde Low;
   - línea de órdenes con las mismas reglas de comillas que `ArgumentList` y las validaciones actuales de `ProcessLaunch`;
   - `ProcessSession` pasa a trabajar sobre un handle de proceso y dos flujos en lugar de `Process`; matar es cerrar o
     terminar el Job, y la sesión devuelve −1 cuando mata (un proceso muerto por el Job sale con 0, V);
   - **fallar cerrado**: si no se puede crear el token, si el propio proceso es High o System, o si el directorio de
     trabajo no tiene etiqueta Low, el paso no arranca. Nunca se cae en silencio a Medium.
3. **Carpetas (`VerificationRunner`).** Una raíz Low corta. Preferible `%LOCALAPPDATA%Low\AgentStudio\v`
   (`FOLDERID_LocalAppDataLow`): ya está etiquetada por Windows, así que no hay que tocar ACL, y la ruta es corta.
   Alternativa: una carpeta propia etiquetada una vez con `SetNamedSecurityInfo`. Ninguna está aislada: cualquier
   carpeta Low (`LocalLow`, `%TEMP%\Low` o una propia) es escribible por todos los procesos Low del usuario; de ahí el
   punto 6 y el vaciado de la raíz entre ejecuciones. Por ejecución:
   `<raíz>\<id>\{src, results, temp, cli-home, nuget}`; `{results}` (ya existe el token `ResultsFolderToken`) apunta a
   `results`, que tiene que ser Low para que el host de pruebas cree el TRX. Entorno: `StepEnvironment` más la tabla de
   arriba. Al terminar se cierra el Job, se lee el informe con las precauciones de los enlaces y se borra `<raíz>\<id>`.
4. **Contenido: copiar, no etiquetar el worktree.** Se materializa en `src` exactamente el árbol que se va a verificar
   (`git archive` del commit o del árbol del sistema, o una copia filtrada sin `.git`, `bin` ni `obj`). Motivos:
   - si se etiquetara el worktree, la verificación podría reescribir sus ficheros, o el fichero `.git` (superficie de
     ataque ya documentada en [la revisión de la capa git](2026-10-15-spikes-fase-3.md#revisión-de-seguridad-de-la-capa-git)),
     después de que el juez viera el diff, y el commit del sistema recogería lo escrito;
   - con una copia el worktree sigue siendo Medium e intocable, y lo verificado es una instantánea fija;
   - la etiqueta Low se queda en la carpeta de verificación y no en los ficheros del usuario, y limpiar es borrar una
     carpeta;
   - coste medido: unos 5–7 s para este repositorio (706 ficheros, 9 MB), y los ficheros heredan la etiqueta solos.
   Contrapartida: sin `.git`, lo que dependa de git en el build (SourceLink, versionado por git) no funciona; para este
   repositorio no aplica.
5. **Caché NuGet por ejecución** por defecto; medir después la variante de *fallback* de solo lectura.
6. **Verificaciones aisladas de una en una** hasta tener identidad por ejecución.
7. **ADR** al integrarlo (CLAUDE.md): «Verificación bajo integridad Low», con este documento como evidencia.

Próximo paso sugerido antes de integrar: ejecutar la solución real de Agent Studio (WPF, generadores de código, más
paquetes) con este arnés, que solo ha probado un proyecto pequeño.

## No verificado

- Enlaces simbólicos, *junctions* y enlaces duros creados desde Low, y el borrado o la lectura desde Medium que no los
  sigue.
- `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` con tuberías anónimas (el spike usó un fichero heredado).
- La solución real de Agent Studio bajo Low (solo se probó `TinyTests`).
- AppContainer: compatibilidad de MSBuild, NuGet y MTP.
- La caché del usuario como *fallback* de solo lectura.
- Lanzar desde un proceso elevado.
- Servicios o pipes de procesos Medium que acepten clientes Low.
- Que los pasos de primera ejecución expliquen la diferencia de 26,3 s a 15,8 s del primer restore.

## Cómo reproducirlo

```powershell
dotnet build spikes/low-il-verification/LowIlVerification.csproj -c Debug
spikes/low-il-verification/bin/Debug/net10.0-windows/LowIlVerification.exe run        # pruebas a-e
spikes/low-il-verification/bin/Debug/net10.0-windows/LowIlVerification.exe overhead   # sobrecoste
```

Necesita red, no necesita administrador y no abre ventanas. Cada verbo crea `%TEMP%\as-lowil-spike\<marca>-<verbo>` y
deja ahí `harness.log` u `overhead.log`; esas carpetas no se versionan y se pueden borrar sin más.
