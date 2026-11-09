# ADR-0055 · El configurador por proyecto: un asistente de siete pasos que propone sin red y aplica cada parte por la puerta de su pantalla de siempre

- **Estado:** aceptada · 2026-11-09 (WP7 de la Fase 13). Recoge D-26 y D-27 de
  [docs/17 §4.6](../17-constructor-visual-de-agentes.md#46-configurador-por-proyecto) y el criterio F13-12 (§9.3). Usa lo de
  WP2 ([ADR-0050](0050-modelos-y-potencia.md)), la barrera de WP5 ([ADR-0053](0053-windows-hello-como-barrera-fija.md)) y el
  registro del P0 (`ProjectSetupApplied`). **Numeración:** docs/17 §10 reservaba el 0055 para la ayuda de la Fase 14 y no
  asignaba el configurador a ningún ADR; la ayuda pasa al siguiente número libre al integrar (§10 permite correr los números).
- **Contexto:**
  - P8 pide un configurador: abrir un proyecto y que el sistema proponga cómo prepararlo, con cada propuesta revisable.
  - Hoy cada cosa se prepara en su pantalla: la plantilla en la preparación del orquestador, el perfil de verificación a mano
    (`.agentstudio/verification.json`, con una propuesta solo para .NET), las skills en su catálogo. Y guardar una plantilla
    no deja rastro en el registro.
  - Lo crítico ya tiene su barrera: el perfil de verificación y dar a un agente una shell, la red, Codex escribiendo o
    carpetas con escritura están en la lista fija de Windows Hello (ADR-0053). Un asistente no puede ser un camino más corto.
- **Decisión:**
  1. **El configurador no tiene puertas propias** (D-27). «Aplicar» llama a lo que llaman las pantallas de siempre:
     - la ficha, `IProjectBrief.SaveAsync` (una versión nueva de la persona);
     - la plantilla (y con ella el modelo y el esfuerzo), `IAgentRoster.SaveAsync`: cambia `RosterHash`, y quien lee
       `SetupApplyResult.RosterChanged` retira la aprobación del plan que espera, como hace hoy `OnRosterSavedAsync`;
     - el perfil, `VerificationProfileFile.SaveAsync` por `IProjectStorageFiles`, con Windows Hello;
     - las skills, `ISkillActivationStore.AppendAsync`, una decisión de la persona por agente y nunca activada de partida;
     - las opciones del proyecto, en su almacén.
  2. **Nada se aplica solo.** Cada paso se puede saltar, y un paso saltado va como `null` a `SetupSelection`. De partida solo
     está activo lo que la persona revisa de todos modos: las altas de la plantilla y un perfil que cambiaría. La ficha, los
     modelos, las skills y el presupuesto empiezan apagados y se encienden cuando la persona los toca. Nada se escribe hasta
     «Aplicar los cambios», que está en el resumen, con lo de antes al lado.
  3. **Propone sin red, sin agente y sin cuota.** El paso 1 lee los nombres de los ficheros (el índice del explorador, que no
     sigue enlaces) y lo que dice git por la capa endurecida; no ejecuta nada del repositorio. Los modelos son
     `ModelRecommender` con su razón; las skills, `SkillRecommender` (docs/14 D-28). El recomendador asistido por un agente
     queda fuera, como dice D-26 («el asistido, con tu OK»): es otra petición con su OK de cuota.
  4. **Tamaño y plantilla.** Pequeño por debajo de 300 ficheros, mediano de 300 a 2 000 y grande por encima de 2 000 (el límite
     de D-24 para el orquestador «máximo»). La pequeña es orquestador, un ejecutor y juez; la mediana añade `ejecutor-ligero`
     (para documentación y pruebas, con el nivel «ligero» y su razón propia) y la grande añade `revisor`, un crítico de Codex de
     solo lectura. La persona puede elegir otro tamaño.
     - **Solo añade y cambia.** El almacén de la plantilla no borra, así que pasar de siete agentes a tres no quita ninguno. Un
       agente que el proyecto ya tiene conserva su definición: puede haberlo preparado la persona.
  5. **Windows Hello donde manda la lista, por la misma regla.** `HelloSetupPresenceGate` usa el `ReinforcedConfirmation` de las
     aprobaciones, así que hay una sola regla y ningún ajuste ni fichero la apaga.
     - **El perfil** pide `ChangeVerificationProfile` (siempre) antes de escribirse. Si es el que ya tiene el proyecto, no
       pregunta nada.
     - **La plantilla** pide `GrantHighRiskCapability` (siempre) cuando un agente gana una capacidad de la lista
       (`SetupCapabilities.Granted`). Es la lectura de lo que dice la propia plantilla: herramientas, programa y papel, y
       carpetas con escritura; la misma base que `MediumIntegrityWriters` lee para un plan. Que un ejecutor de Claude Code que
       escribe esté confinado se decide al aprobar un plan, con la versión instalada (`PlanPresence`), y no se pregunta otra vez
       aquí. Las plantillas que propone el configurador no tienen ninguna de estas capacidades: la persona las da desde la capa
       Equipo.
     - **Sin barrera compuesta**, esas dos partes se niegan (fail closed). Un no (cancelado, sin confirmar, otra confirmación
       en curso, Windows que no responde) deja la parte sin escribir y dice por qué con un código.
  6. **Cada parte es su propia escritura.** Lo que falla o no se confirma no deshace lo demás, y `SetupApplyResult` dice qué hizo
     cada parte (`Applied`, `Unchanged`, `Skipped`, `Denied`, `Failed`). Hay dos excepciones de orden, para no dejar nada a
     medias: antes de guardar el primer agente se validan todos, y un fichero de perfil que no se puede usar no se sobrescribe
     nunca (lo arregla o lo borra la persona).
  7. **`ProjectSetupApplied`** (D-27) lo escribe el servicio, con `ActorKind.User` y fuera de toda ejecución, con la huella de
     cada cosa escrita y `null` en lo demás. Sin nada escrito no hay entrada. Si el registro no se verifica, lo escrito queda y
     el resultado dice que la entrada falta; no se reintenta a ciegas.
  8. **Perfiles de verificación de Node y de Python** (paso 5). Se detectan por los nombres de los ficheros de la raíz
     (`package.json`; `pyproject.toml` o `requirements.txt`), con precedencia .NET, Node, Python. Los ejecuta el sistema como los
     de .NET: aislados en una copia del commit, cada paso requiere el anterior y el informe es un JUnit en `{results}`.
     - **Node:** `install` (`npm ci` con `package-lock.json`, `npm install --no-package-lock` sin él; siempre
       `--ignore-scripts` y sin avisos), `build` (`npm run build --if-present`) y `test` con el ejecutor de pruebas de Node y su
       informador JUnit.
       - **npm arranca con node, no por su shim.** En Windows `npm` es `npm.cmd`, y el lanzamiento aislado
         (`LowIntegrityProcess.ResolveExecutable`) solo añade `.exe`; tampoco queremos pasar por `cmd` (los CLIs de npm son shims).
         Los pasos son `node <carpeta de node.exe>\node_modules\npm\bin\npm-cli.js <argumentos>`, con `node.exe` resuelto como
         ya se hacía. Si `npm-cli.js` no está junto a node (o no hay node), `install` y `build` pasan a ser un paso que falla y
         dice por qué en su salida (`StackStepSupport.Failing`, `cmd /d /c "echo … & exit /b 1"`, que solo imprime): nunca un
         fallo a ciegas al arrancar.
     - **Python:** `build` (`python -m compileall -q -x <carpetas> .`) y `test` (`pytest` con `--junitxml`). No instala nada:
       `pip` no se ejecuta nunca, y los paquetes son los del Python del equipo.
       - **`compileall` escribe** `__pycache__` en la copia, y es lo que hace (la copia se borra al acabar); por eso el perfil ya no
         lleva `-B` en ese paso. Salta `node_modules`, `.venv`, `venv` y `.git` con `-x`.
       - **El alias de la Tienda.** Si el `python` del `PATH` es el alias de `%LOCALAPPDATA%\Microsoft\WindowsApps`, que puede
         fallar en integridad baja, o no hay ninguno, `build` es un paso que falla y lo dice (`PythonVerificationProfile.InterpreterProblem`).
         Se arregla instalando Python desde python.org o editando el perfil.
     - **Un entorno por perfil.** Además del de .NET, el ejecutor da a un paso aislado de `node`, `npm` o `npx`: `npm_config_cache`,
       `npm_config_userconfig` (un fichero vacío en el área), `npm_config_prefix`, `npm_config_update_notifier=false` y
       `npm_config_fund=false`; y a uno de `python`, `python3`, `py`, `pip` o `pip3`: `PIP_CACHE_DIR`, `PIP_CONFIG_FILE` (vacío),
       `PYTHONUSERBASE` y `PYTHONDONTWRITEBYTECODE=1`. A los dos, `HOME`, `USERPROFILE`, `APPDATA` y `LOCALAPPDATA` en la carpeta de
       «casa» del área. Los ficheros vacíos se crean solo si no existen (nunca se escribe a través de una ruta que ya está).
     - **Lo que sigue sin limitarse**, igual que en .NET: la zona aislada es integridad baja, no una caja. Un proceso del paso
       puede **leer** lo que el perfil del usuario deje legible (claves y configuraciones fuera de las variables de entorno que
       cambiamos, como credenciales que lea por su ruta absoluta) y usa **la red** con las del usuario. Lo que sí impide es
       escribir en tu repositorio y en tu perfil. Por eso el perfil es código que corre en tu equipo y guardarlo pide Windows Hello.
     - **Pruebas:** las unitarias usan procesos guionizados y repositorios temporales de ficheros vacíos, sin `npm` ni `pip`. Además
       hay pruebas `LocalTools` (`StackVerificationLocalToolsTests`) que lanzan de verdad, en integridad baja y sin red, `node --test`,
       npm con un `package.json` sin dependencias y `--ignore-scripts`, `compileall` y `pytest`; se saltan si la herramienta no está.
  9. **Las opciones del proyecto** (paso 7: ejecuciones por ejecución, tareas a la vez y tiempo por intento) viven en la carpeta
     de la app de este equipo (`%LOCALAPPDATA%\AgentStudio\setup\<id>.json`), no en el repositorio: son de la persona, y un
     fichero más en `.agentstudio` contaría como cambio suyo sin guardar (`RepositoryState.HasUncommittedChanges`) y haría
     fallar las ejecuciones. El consentimiento de reanudarse sola no está aquí: es por ejecución (ADR-0034), y el paso lo dice.
  10. **Textos (ADR-0054).** Todo texto nuevo sale de `Resources/SetupStrings.resx` y `SetupStrings.en.resx`, con el prefijo
      `Setup_` y, para los códigos de las capas de abajo, `Text_setup_…`. Application habla con `UserText`. La pantalla trae su ⓘ
      (`InfoKey="Setup_Info"`).
- **Alternativas:**
  - **Un asistente con sus propias aprobaciones o una aprobación única:** sería un camino más corto que el de las pantallas.
    Descartada: D-27.
  - **Aplicar al terminar cada paso:** escribiría antes de que la persona viera el resumen. Descartada: «Nada se aplica solo».
  - **Una transacción, todo o nada:** `IAgentRoster`, el perfil y las skills son tres almacenes sin transacción común. Se prefirió
    el resultado por parte, con las dos comprobaciones previas que evitan lo que sí se puede evitar.
  - **Las opciones en `.agentstudio/`:** obligaría a tocar `GitExclude` y `AgentStudioFiles` (de otros paquetes y con efecto
    sobre la comprobación del repositorio). Descartada por el punto 9.
  - **Que el perfil de Node use `npm test`:** depende de cada proyecto y de un informador JUnit que no todos tienen. Se prefirió el
    ejecutor de Node, que lo trae, y editar el paso en los demás.
- **Consecuencias:**
  - **Lo medido y lo que no:** en este equipo (2026-11-10), con las pruebas `LocalTools`, `node --test` con el informador JUnit,
    npm por `node npm-cli.js`, `compileall` con `-x` y `pytest` corrieron en integridad baja y el informe se leyó. Sin verificar
    (docs/17 §13): la versión de Node desde la que existe el informador JUnit, un proyecto con dependencias reales (necesita red y
    un registro) y un Python de otra instalación.
  - **Un registro con un `package.json` dependiente de scripts propios** (`npm run build`) ejecuta lo que el repositorio diga: es
    el perfil, y por eso va tras Windows Hello.
  - **Los remotos del paso 1 salen vacíos** hasta que haya un lector por la capa git endurecida, que hoy no los expone.
  - **No hay estimación de consumo** junto a las opciones del paso 7 ni la diferencia de consumo entre modelos que D-24 pide
    «si el estimador tiene referencias»: ambas piden construir una petición de estimación por agente y se dejan para cuando
    haya un uso real que las mida.
  - **Las razones del recomendador de skills** salen como las escribe el dominio, en castellano: Fase 14 las convierte en
    códigos (ADR-0054).
  - **La integración** tiene que dar de alta `AddAgentStudioSetup`, alojar la vista, leer las opciones del proyecto en la
    pestaña del orquestador y suscribirse a `SetupViewModel.Applied` (docs/04 §21.9).
