# ADR-0063 · El doctor de skills con red: solo `api.github.com`, apagado por defecto y a petición, y un token opcional en una bóveda DPAPI mínima

- **Estado:** aceptada · 2026-11-12 (P0 de la Fase 6). Recoge D-13 a D-15 de [docs/20](../20-fase-6-auto-doctor-y-auto-graph.md) y las decisiones 5, 6 y 15 de su §13
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
