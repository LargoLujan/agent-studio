# ADR-0063 · El doctor de skills con red: solo `api.github.com`, apagado por defecto y a petición, y un token opcional en una bóveda DPAPI mínima

- **Estado:** aceptada · 2026-11-12 (P0 de la Fase 6); implementada por WP-S el 2026-11-13 (ver «Lo que hizo WP-S»). Recoge D-13 a D-15 de [docs/20](../20-fase-6-auto-doctor-y-auto-graph.md) y las decisiones 5, 6 y 15 de su §13
  (todas, la opción recomendada). **Amplía** [ADR-0007](0007-catalogo-de-skills.md). El P0 verifica la dependencia y fija los puertos y las validaciones; WP-S
  implementa el cliente, la cola, la caché, el comprobador y la bóveda, y añade aquí lo que haga.
- **Contexto:**
  - F6-6 pide el doctor de skills: el `upstream` desde el commit fijado, el repositorio archivado, los scripts sin revisar y el tamaño, con un token opcional de GitHub.
    `ISkillHealthChecker` no tiene implementación y `ICredentialVault` es solo un contrato.
  - Es la única pieza de la app que habla con un servidor, y lo que le llega para construir una dirección (`owner`, `repo`, rama, commit, carpeta) viene de un índice o
    de una URL: no es fiable. Un `..`, un `?` o un `#` en un dato no puede llevar a otro punto de la API con el token de la persona.
- **Decisión:**
  1. **Cuatro cerrojos** (D-13). Apagada por defecto (un ajuste en Ajustes); solo cuando se pulsa «Comprobar en GitHub»; solo `https://api.github.com`, con las
     redirecciones seguidas a mano y solo al mismo host; y se dice qué sale (propietario, repositorio y commit). El puerto que lo expresa es `ISkillsDoctorPolicy`:
     `AppSettings` vive en Desktop, así que el comprobador pregunta a un puerto antes de cada tanda y la prueba de F6-13 lo apaga con un doble.
  2. **Cada dato de la dirección se valida antes de construir nada** (D-14). `GitHubSkillOrigin` (Domain) rechaza —no corrige— un `owner`, un `repo`, una rama, un
     commit o una carpeta que no sigan las reglas de la tabla de D-14. Las comprobaciones van carácter a carácter: no hay expresión regular con `$` que deje pasar un
     salto de línea final. Lo que no pasa no se pregunta y sale «origen no válido» (`SkillHealthFailure.InvalidOrigin`). Quien construye la dirección (WP-S) escapa
     cada segmento, pone la rama y la carpeta como valores de consulta y comprueba la dirección final.
  3. **Los fallos tienen código** (`SkillHealthFailure`): sin red, 403, 404, límite, redirección rechazada y respuesta que no se entiende; su mensaje nunca lleva la
     dirección completa, las cabeceras ni el cuerpo. `ISkillHealthChecker` no cambia: la tanda es `ISkillHealthBatchChecker`, que devuelve una entrada por skill, si usó
     el token y cuándo se reinicia el límite.
  4. **Los scripts no se ejecutan nunca** (D-14): «Marcar los scripts como revisados» guarda el SHA-256 de `scripts/` (`ISkillScriptReviews`); si un script cambia, la
     skill vuelve a «sin revisar».
  5. **El token es opcional y nunca sale de los CLIs** (D-15). Sin él, 60 peticiones por hora (con las cachés basta); con él, 5 000. Se guarda en la bóveda
     (`ICredentialVault`), cifrado con DPAPI para el usuario de Windows. **`System.Security.Cryptography.ProtectedData` 10.0.13** (decisión 15 (a)): dependencia
     nueva en `AgentStudio.Infrastructure`, verificada el 2026-11-12 (licencia MIT, autor Microsoft, sin dependencias en `net10.0`, listada y sin aviso en el feed de
     vulnerabilidades de nuget.org del 2026-11-11). Se fija la 10.0.13 para alinearla con los demás paquetes de `Microsoft.Extensions.*` (la última 10.x es la 10.0.15).
     Nunca se lee `gh auth token`, el gestor de credenciales de git ni una carpeta de un CLI.
  6. **El token solo llega al comprobador por `ISkillsDoctorSecrets.UseTokenAsync`**, que pide presencia (Windows Hello; `RemoteOrSecretOperation` está en la lista fija de
     [ADR-0053](0053-windows-hello-como-barrera-fija.md)) y **solo después lee la bóveda**: con la presencia denegada, la bóveda no recibe ninguna lectura y la tanda
     sigue sin token y lo dice (`SkillsDoctorTokenState`). Guardar, sustituir o borrar el token pide Hello, y cada tanda que lo usa lo pide una vez.
  7. **El token no aparece en ningún texto:** `GitHubToken` solo lo da `Reveal()`, su `ToString()` es `[GitHub token]`, y `SkillsDoctorTokenUse` solo lleva un token con el
     estado `Granted`. No va al registro, la bitácora, un log ni un mensaje de error; `SecretRedactor` ya reconoce su forma y una prueba de WP-S busca un token centinela
     en todo lo escrito tras una tanda con fallos.
- **Lo que protege DPAPI, dicho con honestidad:** el fichero no sirve en otro equipo ni a otro usuario. **No** protege de un proceso que corra con el usuario de Windows:
  cualquiera puede pedir que lo descifre. Hello protege la interfaz de Agent Studio, no el fichero. Frente a una variable de entorno (opción (b)), que está en claro y la hereda
  cada proceso que se arranque, incluidos los CLIs de los agentes, la bóveda exige ir a por un fichero y llamar a DPAPI adrede; las dos caen ante un proceso malicioso del
  usuario, la bóveda no lo regala. El token es de solo lectura de datos públicos y opcional.
- **Lo que fija el P0** ([docs/04 §25](../04-contratos.md#25-fase-6-auto-doctor-auto-graph-entorno-declarado-y-diagramas)): la dependencia (`Directory.Packages.props` y la
  referencia de Infrastructure), `GitHubSkillOrigin` con sus pruebas adversarias, `GitHubToken`, `SkillsDoctorTokenUse` y los puertos `ISkillHealthBatchChecker`,
  `ISkillsDoctorPolicy`, `ISkillScriptReviews` e `ISkillsDoctorSecrets`.
- **Alternativas descartadas:** una comprobación al día en segundo plano (red sin que la persona lo pida); sin red en esta fase (F6-6 quedaba a medias); `compare` de la API de
  GitHub (su respuesta trae el parche de cada fichero y puede pasar de varios megas: se hacen dos peticiones de `commits` pequeñas, y la del commit fijado no cambia nunca);
  una llamada directa a `CryptProtectData` (decenas de líneas de interoperabilidad propias que revisar alrededor de un secreto).
- **Residuales aceptados:** GitHub ve la dirección IP y qué repositorios públicos se consultan; el token de la bóveda lo puede descifrar cualquier proceso del mismo usuario.

- **Lo que hizo WP-S** (2026-11-13). Todo en `AgentStudio.Infrastructure` salvo la pantalla y el permiso:
  1. **Un solo constructor de direcciones** (`Skills/GitHub/GitHubApiAddress`). Recibe un `GitHubSkillOrigin`, nunca un texto suelto; escapa cada segmento con `Uri.EscapeDataString`;
     pone la rama, el commit y la carpeta como valores de la consulta; y comprueba la dirección final (`https`, host `api.github.com`, puerto por defecto, sin credenciales ni
     fragmento, ruta exacta). `IsAllowed` mira además **cómo se escribió** la dirección (`Uri` colapsa `..` y `%2e%2e` en `AbsolutePath`): una ruta escrita con ellos no pasa. Las
     direcciones que construye son exactamente tres: `GET /repos/{owner}/{repo}` y `GET /repos/{owner}/{repo}/commits?sha={commit|rama}&path={carpeta}&per_page=1` (sin `path` si la
     carpeta es la raíz).
  2. **Un `HttpClient` propio** (`GitHubApiClient`) con `GitHubHostGuardHandler` justo encima de la red: cualquier petición que no sea un `GET` sin cuerpo a `https://api.github.com/repos/…`
     (o `/repositories/…`, adonde GitHub manda un repositorio renombrado) lanza sin enviar nada. `AllowAutoRedirect = false`, sin cookies, sin credenciales del sistema, `User-Agent`
     fijo (`AgentStudio-SkillsDoctor`), cabeceras de respuesta de 32 KB como mucho, **10 s para todo el intercambio, cuerpo incluido**, y cuerpo de 1 MB como mucho (por `Content-Length` y
     además leyendo con tope). Una cola con un solo consumidor las envía en serie. Las redirecciones (301, 302, 307, 308) se siguen a mano, como mucho 3, solo a `https`, al mismo host y bajo
     `/repos/` o `/repositories/`.
  3. **El token solo va en la primera petición.** Corrige D-14, que decía que la cabecera se pone «tras esa comprobación»: ni siquiera una redirección al mismo host la lleva, y tampoco el
     `ETag`. Lo que llega por una redirección no se guarda en la caché (su `ETag` es de otra dirección).
  4. **Los fallos son un `GitHubApiException` sin la excepción que los causó** (su mensaje podría llevar lo enviado), con un mensaje fijo por código. `429`, `403` con
     `x-ratelimit-remaining: 0` y `retry-after` son `RateLimited` con la hora de reinicio: la tanda se para y las skills que faltan salen «límite de GitHub, se reinicia a las HH:MM» sin más
     peticiones. Si la última respuesta dijo «0 restantes», tampoco se envía la siguiente. Sin conexión también para la tanda.
  5. **Las respuestas son datos no fiables:** `JsonDocument` con profundidad 16, solo los campos de D-14 con su tipo (`archived`: booleano; `sha`: 40 hexadecimales en minúscula). Ningún texto
     de GitHub llega a un mensaje, y ninguna dirección de la respuesta se sigue salvo `Location` de una redirección, con las condiciones de arriba. La caché (`cache.json`) guarda el `ETag`
     y esos dos valores, nunca el cuerpo, y se lee como no fiable (1 MB, 2 000 entradas, `ETag` de ASCII visible).
  6. **Orden de una tanda** (`GitHubSkillHealthChecker`): permiso de Ajustes (`ISkillsDoctorPolicy`; apagado: ni una petición ni una llamada a los secretos) → validar cada origen (lo que no
     pasa, o no tiene commit fijado, es «origen no válido») → si no queda nada que preguntar, fin: no se pide Hello por una tanda que no envía nada → `UseTokenAsync` una vez → las skills en serie.
  7. **La bóveda** (`Security/DpapiCredentialVault`): un fichero `<nombre>.dpapi` por secreto, cifrado con `ProtectedData` (`DataProtectionScope.CurrentUser`, entropía fija de la app), nombre
     `[a-z0-9._-]{1,64}`, escritura a un temporal y movimiento, lectura con tope de 64 KB, carpeta inyectable (por defecto `%LOCALAPPDATA%\AgentStudio\vault`). Para saber si hay token
     **sin descifrarlo** se añade `ICredentialVaultProbe.ContainsAsync` (`ICredentialVault` no cambia): el orden de `SkillsDoctorSecrets` es ¿hay token? → Hello → `ReadAsync`.
     Sin token no hay Hello; con Hello denegado, o que falla al preguntarse, la bóveda no recibe ninguna lectura. Guardar y borrar piden Hello antes de escribir.
  8. **Hello** se pide con `ISkillsDoctorPresence`, que el escritorio implementa sobre el mismo `ReinforcedConfirmation` de las aprobaciones con `PresenceAction.RemoteOrSecretOperation`
     («siempre»). Sin `ReinforcedConfirmation` en la composición, toda confirmación es un no.
  9. **El permiso** vive en `AppSettings.SkillsDoctorNetworkEnabled` (apagado) y se enciende en la propia pantalla de Skills; sin ajustes en la composición, la red nunca está permitida.
  10. **Scripts y tamaño, en local:** `SkillScriptsInspector` da la huella SHA-256 de `scripts/` (rutas ordenadas y contenido; un enlace cuenta por su nombre y no se abre; tope de 500 ficheros) y `SkillScriptReviewStore` guarda la que la persona marcó en `skill-script-reviews.json`, junto a las decisiones globales. `scripts`: sin carpeta, sana; con ella, aviso hasta
      marcarla y otra vez si cambia. `size` añade el coste en contexto del `SKILL.md` entero (estimado por Agent Studio).
  11. **Qué se registra:** nada en el registro de decisiones. D-14 no pide una clase de entrada nueva (`DecisionKind` crece en dos valores, ambos de otras piezas), y un registro con «se comprobó y dio X»
      sería un dato de un servidor externo en un registro anclado. Lo que queda es la caché (se puede borrar) y lo que muestra la pantalla; ninguno lleva el token.
- **Residuales de esta implementación:** el nombre del repositorio y la rama viajan en la consulta (la tarjeta de la pantalla lo dice); un proxy del sistema ve el destino; el texto del
  ⓘ y del artículo de ayuda dicen que el fichero de la bóveda no protege de un programa del mismo usuario.

- **Retoques de la revisión independiente** (2026-11-13):
  1. `GitHubSkillOrigin.IsRepository` rechaza `..` en cualquier sitio (`a..b`, `...`): el constructor de direcciones los rechaza todos, y un origen «válido» no puede hacer que falle. Además, las tres direcciones se construyen **antes** de la primera petición y, si el constructor se niega, solo esa skill sale «origen no válido»: la tanda sigue.
  2. **Los textos salen como códigos** (ADR-0054): `SkillHealthCheck` lleva `TextCode` y `TextArguments` (tipos de `TextArguments`: una hora es un instante, no un texto con formato) y `Message` es solo el respaldo técnico (el código). `UserText` vive en Application y Domain no puede verlo: el escritorio compone `UserText(TextCode, TextArguments)` y lo pinta (`Text_skills_*` en `SkillsStrings*.resx`; los códigos, en `Application/Skills/SkillsUserTexts.cs`). Pasan a códigos todas las frases de `GitHubSkillHealthChecker` y las de `SkillHealthReader` que son suyas. **Adenda del 2026-11-13 (P65):** las frases de `SkillFrontmatter.Validate`, `SkillHealthChecks.CheckLicense/CheckPinned` (Domain) también son códigos, en `SkillHealthTextCodes` (Domain no ve Application; `SkillsUserTexts.All` los suma a los suyos). El coste en contexto del `SKILL.md` que se había quitado vuelve como un argumento numérico de `size` (`size-ok-cost` y `size-long-cost`; `Validate(folderName, contextTokens)`), no como un trozo de frase.
  3. La huella de `scripts/` hace el hash **completo** de cada fichero, en bloques, sea cual sea su tamaño: un fichero largo no se «revisa» por su longitud.
  4. Un `401` es `SkillHealthFailure.InvalidToken` («la clave guardada no vale o se revocó»): la tanda se corta y las demás skills salen con ese motivo, en vez de un «no dejó leer este repositorio» por cada una.
  5. «Cancelar la comprobación» es un botón real (siempre visible, activo solo durante la tanda); cerrar la pantalla también la cancela. Una tanda cancelada deja las skills que faltaban sin comprobar.
  6. `DpapiCredentialVault.StoreAsync` borra el temporal si la escritura o el movimiento fallan o se cancelan.
  7. Cada script de la tarjeta es un enlace que lo abre en el editor de la app (`MainViewModel.OpenFileAsync`), tras volver a comprobar que sigue siendo un fichero normal dentro de `scripts/`, no un enlace. Sin ese mecanismo (una composición que no lo pasa) la lista sigue siendo texto.
