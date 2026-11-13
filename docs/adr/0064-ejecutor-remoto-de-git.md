# ADR-0064 · El ejecutor remoto de git: separado de la capa interna, sin credenciales propias, con dos operaciones y el destino construido por el código

- **Estado:** propuesta · 2026-11-13, pendiente de las decisiones 3, 4, 5, 13 y 16 de [docs/21 §14](../21-fase-10-git-remoto.md#14-decisiones-que-necesitan-a-la-persona)
  (se redacta con la opción recomendada de cada una). Recoge D-01 y D-03 a D-08 de [docs/21](../21-fase-10-git-remoto.md) y
  la D-24 de [docs/16](../16-ampliacion-plataforma-de-proyectos.md). Ocupa el lugar del ADR-0041 que docs/16 reservó y que
  nunca se escribió. **Enmienda** la frase «Nunca hay `push` ni red» de [ADR-0012](0012-worktrees-fuera-del-repo.md) **solo
  para este componente:** para la capa interna sigue valiendo al pie de la letra. **Amplía**
  [ADR-0018](0018-modelo-de-amenaza-fase-3.md). El P0 fija los puertos; WP1 implementa y añade aquí lo que mida.
- **Contexto:**
  - F10-3 pide publicar la rama de una ejecución con `git push` y el gestor de credenciales de la persona; F10-10, que la
    capa git interna siga sin red ni credenciales.
  - La capa interna (`GitCommandRunner`) se cerró en nueve rondas adversariales: cada orden lleva
    `GIT_ALLOW_PROTOCOL=-`, `credential.helper=` y todos los transportes en `never`. Abrirle un «modo remoto» metería la
    red y las credenciales de la persona justo ahí.
  - La configuración del repositorio es contenido no fiable (ADR-0018, punto 1), y para un `push` decide cosas que antes
    no importaban: a qué dirección se envía (`url.*.insteadOf`, `remote.*.pushurl`), qué programa recibe el token
    (`credential.helper`), por qué intermediario pasa (`http.proxy`, `core.sshCommand`) y qué corre durante la orden
    (`pre-push`, `core.fsmonitor`).
- **Decisión:**
  1. **Un ejecutor aparte** (`IRemoteGitRunner`, en `Infrastructure/Git/Remote/`), que no usa `GitCommandRunner` ni
     comparte con él la lista de argumentos ni el constructor del entorno. La capa interna no cambia en nada. Dos pruebas
     lo fijan: una de comportamiento (cada orden interna lleva `GIT_ALLOW_PROTOCOL=-` y los ayudantes vacíos) y una de
     arquitectura (ningún fichero de `Infrastructure/Git/` fuera de `Remote/` nombra `push`, `fetch`, `pull`, `clone` ni
     `ls-remote`).
  2. **Dos operaciones con red y ninguna más:** `push` de un commit a `refs/heads/agentstudio/run-<id>` y `ls-remote` de
     esa rama. **No hay `git fetch`:** escribe `refs/remotes/*` y `FETCH_HEAD` en el repositorio de la persona, y el estado
     del remoto lo da la plataforma (ADR-0067). El puerto tiene tres miembros (inspeccionar, leer una rama y publicar) y
     ninguna sobrecarga con argumentos libres.
  3. **Sin credenciales propias.** Agent Studio no lee, guarda ni pasa ningún token: `git` usa el gestor de credenciales
     de la configuración **global y de sistema** de la persona. La bóveda (`ICredentialVault`) no se usa.
  4. **El destino lo construye el código.** De la URL del remoto (no fiable) solo se extrae un identificador validado
     carácter a carácter (`RemoteRepository`: host `github.com`, propietario y nombre). La dirección del `push` es siempre
     `https://github.com/<propietario>/<nombre>.git`; nunca el nombre del remoto ni su URL tal cual. El refspec lo escribe
     una sola función: `<commit>:refs/heads/agentstudio/run-<id>`, sin `+`. Solo HTTPS.
  5. **La configuración se inspecciona por ámbito y por origen** (`git config --show-scope --show-origin`, que no abre
     ningún transporte) antes de cada operación. Se rechaza si una clave de la lista de docs/21 D-05 (`credential.*`,
     `url.*.insteadOf` y `pushInsteadOf`, `remote.*.pushurl` y afines, `http.*`, `core.sshCommand`, `core.gitProxy`,
     `core.fsmonitor`, `core.hooksPath`, `push.*`, `submodule.recurse`…) tiene ámbito `local` o `worktree`, o viene de un
     fichero dentro del repositorio (un `include` de la configuración global que apunte dentro). También si un `insteadOf`
     global reescribe la dirección construida: lo que se enseña es lo que se usa. Fallo cerrado. La pantalla nombra la
     clave y su fichero, nunca su valor.
  6. **Las claves que mandan se fijan en la línea de órdenes:** `core.hooksPath` al fichero vacío de siempre y
     `--no-verify` (ningún hook, tampoco `pre-push`), `protocol.https.allow=always` con todos los demás en `never`,
     `http.followRedirects=false`, `core.sshCommand=`, `core.gitProxy=`, `push.default=nothing`, `push.followTags=false`,
     `push.recurseSubmodules=no`, `push.gpgSign=false` y `submodule.recurse=false`. `credential.helper` **no** se vacía.
  7. **Entorno por lista blanca** (como la puerta de Auto Doctor, ADR-0061): las variables de Windows que hacen falta, el
     PATH filtrado, temporales propios y `GIT_TERMINAL_PROMPT=0`, `GIT_OPTIONAL_LOCKS=0`, `GIT_NO_LAZY_FETCH=1`,
     `GIT_ALLOW_PROTOCOL=https` y `GCM_INTERACTIVE=never`. Ninguna otra `GIT_*`, `GCM_*` ni variable de proxy llega.
  8. **Nunca forzar, por construcción:** la petición (`RemotePush`) no tiene campo para forzar, borrar ni elegir refspec;
     los argumentos son una constante; y una prueba recorre cada `ProcessSpec` lanzado y falla ante `--force`, `-f`,
     `--force-with-lease`, `--mirror`, `--prune`, `--delete`, `--all`, `--tags`, `--push-option` o un refspec que empiece
     por `+` o por `:`. Un `push` que no es avance rápido es un resultado («la rama del remoto cambió»), no un reintento.
  9. **Antes y después.** Antes: solo se publica el commit que `IDeliveryService.CheckAsync` da por aprobado; se cuenta lo
     que sube (también los commits de la persona que aún no estaban en el remoto) y se enseña. Después: `ls-remote` tiene
     que dar exactamente ese commit, o el resultado es «no confirmado».
  10. **Sin texto libre.** La orden no lleva mensaje ni título: no hay entrada estándar ni argumento que escriba una
      persona o un agente. El ejecutable es el `git.exe` del sistema, resuelto fuera de los proyectos abiertos.
  11. **El error estándar no se guarda ni se muestra:** se clasifica en un código y pasa por `SecretRedactor` antes de
      cualquier diagnóstico.
- **Límites que se dicen:**
  - **Git LFS:** sin el hook `pre-push` los objetos no suben. Un proyecto cuyo envío llevaría objetos LFS no se publica
    desde Agent Studio, y se explica.
  - **SSH:** un remoto por SSH se identifica, pero se publica por HTTPS; sin credenciales para HTTPS, se dice.
  - **«Delante y detrás»** de una rama remota solo se calcula si sus commits ya están en el equipo.
- **Alternativas descartadas:** un «modo remoto» en `GitCommandRunner` (la red en el componente más costoso de cerrar);
  `git fetch` normal o a refs propias (escribe en el repositorio y añade una operación con red para un dato que la
  plataforma da); usar el remoto por su nombre (`remote.*.pushurl` y `remote.*.push` decidirían el destino); guardar un
  token en la bóveda y pasarlo a git (Agent Studio tocaría la credencial); publicar por SSH en esta entrega (más
  superficie: `ssh.exe`, su configuración y su agente).
- **Residuales aceptados:**
  - La configuración global y de sistema de git y el gestor de credenciales de la persona son de confianza: un programa
    de su usuario que los cambie engaña a este componente (el residual del proceso concurrente de ADR-0018).
  - `git push` arranca con la carpeta de trabajo en el repositorio, y el gestor de credenciales corre como hijo suyo.
  - GitHub ve la dirección IP y la cuenta.
- **Sin verificar al escribirlo** (lo mide WP1 y lo anota aquí): que un `push` a una dirección no escribe nada en el
  repositorio local; que `GCM_INTERACTIVE=never` hace fallar al gestor en vez de abrir su ventana; que
  `http.followRedirects=false` rechaza un repositorio renombrado; la salida de `--porcelain`; y cómo detectar LFS sin
  ejecutar `git lfs`.
