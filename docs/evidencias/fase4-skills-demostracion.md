# Fase 4 · Demostración de F4-7 y F4-8 (skills)

> Lista para seguir a mano en la aplicación real, cuando la integración haya activado la entrada «Skills» de la barra de
> actividad. La parte automática ya la cubren los tests: `SkillsDocumentViewModelTests` y `SkillsRenderTests` (la
> pantalla, dibujada sin mostrarse), `SkillRecommendationServiceTests`, `SkillsMcpServerTests` y `SkillsMcpProcessTests`
> (el servidor MCP sobre flujos en memoria y como ejecutable real). Aquí se comprueba lo que solo se ve en pantalla y lo
> que gasta cuota. Marca cada casilla y, si algo falla, anota qué viste.

- Fecha: ____________
- Commit (`git log -1 --format=%h`): ____________

## Preparación

1. Compila y prueba desde la raíz del repositorio. No pases `-nologo` a `dotnet test`: no se ejecutaría ningún test.

   ```powershell
   dotnet build AgentStudio.slnx
   dotnet test
   ```

2. Comprueba que el servidor MCP está junto a la aplicación (lo copia la integración):

   ```powershell
   Test-Path .\src\AgentStudio.Desktop\bin\Debug\net10.0-windows\AgentStudio.SkillsMcp.exe
   ```

   Debe salir `True`.

3. Arranca la aplicación y abre una carpeta de proyecto que tenga un `.csproj` o un `.slnx` (por ejemplo, este
   repositorio):

   ```powershell
   & .\src\AgentStudio.Desktop\bin\Debug\net10.0-windows\AgentStudio.Desktop.exe
   ```

4. El catálogo es el `catalog.json` del índice de skills de tus ajustes o, si no nombran ninguno, el de
   `C:\skills-index` si existe.

## F4-7 · Pantalla del catálogo

- [ ] Abre «Skills» en la barra de actividad. Se abre como un documento.
- [ ] La cabecera dice cuántas skills hay y en cuántos sectores; el árbol muestra cada sector con su recuento, en el
      orden de la taxonomía.
- [ ] Los avisos dicen las enlazadas que no tienen copia local y, si tu plantilla tiene un agente de Codex, que Codex carga
      además tus skills globales.
- [ ] Escribe `spark` en «Buscar»: solo quedan las skills con «spark» en el nombre, el resumen o las etiquetas, y los
      recuentos bajan. Prueba con mayúsculas y sin acentos (`diseno`): da lo mismo. Borra la búsqueda.
- [ ] Elige una skill vendorizada. La ficha enseña el repositorio, la ruta, el commit corto y la licencia, las etiquetas
      con `#`, el coste («~N tokens … estimado por Agent Studio») y las comprobaciones de salud.
- [ ] En la salud, «Cambios en origen», «Origen archivado» y «Scripts» salen «Sin comprobar (Fase 6)», y el punto de la
      skill es gris aunque la cabecera, la licencia y la fijación estén bien.
- [ ] Busca una skill con aviso o crítica (por ejemplo, sin licencia o con la cabecera mal): su punto es ámbar o rojo.
- [ ] Marca «Proyecto». La etiqueta del árbol pasa a «Proy.», y cada agente dice «Activada (por el proyecto)». La sección
      «Lo que recibe cada agente» suma su coste.
- [ ] Marca y desmarca el interruptor de un agente: queda «Desactivada (por el agente)» aunque el proyecto la tenga
      activada.
- [ ] Elige una skill enlazada: sus interruptores están desactivados y la ficha explica que no se puede dar a un agente.
- [ ] Cierra la aplicación, vuelve a abrirla y abre «Skills»: las decisiones siguen ahí (las de proyecto y agente en la base
      del proyecto; las globales en `%APPDATA%\AgentStudio\skill-activations.jsonl`).

## F4-8 · Recomendación determinista

- [ ] «Stack del proyecto» muestra lo detectado por los nombres de los ficheros (`#csharp #dotnet` en este repositorio).
- [ ] Escribe una tarea («Una pantalla nueva con MVVM») y pulsa «Recomendar (determinista, sin cuota)». Salen skills con
      sus puntos y razones («coincide con #dotnet»).
- [ ] Pulsa una recomendación: se selecciona en el árbol y se abre su ficha. No se ha activado nada.

## F4-8 · Recomendación asistida (gasta cuota: una ejecución corta de haiku)

- [ ] Sin tocar nada, «Pedir a un agente» está desactivado: la asistida está apagada por defecto.
- [ ] Marca «Permitir la recomendación asistida». El botón se activa.
- [ ] Pulsa «Pedir a un agente». Sale el diálogo de aprobación, que dice que se lanza Claude Code con haiku, de solo
      lectura, sin red y en una carpeta vacía, y que gasta cuota. Pulsa «Denegar»: el estado dice que no se pidió nada y
      no se gastó cuota.
- [ ] Vuelve a pulsar y esta vez aprueba. En unos segundos salen las propuestas del agente, cada una con su ámbito y
      «Razón del agente», el identificador de la ejecución (`aux-…`) y los avisos de lo descartado, si los hay.
- [ ] Ninguna propuesta está activada: los interruptores de su ficha siguen como estaban.
- [ ] Pulsa otra vez: vuelve a pedir el OK (cada uso necesita el suyo).
- [ ] En «Bitácora de hoy» aparece el prompt de la recomendación asistida de skills.
- [ ] `%LOCALAPPDATA%\AgentStudio\skill-recommendations` no guarda la carpeta de la ejecución: se borra al terminar.
- [ ] `git status` del proyecto no cambia por la recomendación.

## Medición del lanzamiento MCP (D-30, con tu OK)

Una ejecución real de haiku que comprueba que solo se carga el servidor de Agent Studio, que sus herramientas se llaman
como se espera, que `dontAsk` deniega una herramienta MCP sin regla y que no se escribe nada fuera del directorio de
trabajo. Necesita el `--mcp-config` del adaptador (WP1):

```powershell
$env:AGENTSTUDIO_PROVIDER_TESTS = '1'
$env:AGENTSTUDIO_CAPTURE_DIR = "$env:TEMP\agentstudio-capturas-mcp"
dotnet test -- --filter-class "AgentStudio.Tests.SkillsMcp.SkillsMcpLaunchLiveTests"
Remove-Item Env:\AGENTSTUDIO_PROVIDER_TESTS
```

- [ ] El test pasa. Anota en ADR-0024 la versión de Claude Code, los turnos y el coste que deja `evidence.log` en la
      carpeta de capturas.
