# ADR-0001 · WPF sobre .NET 10 (LTS)

- **Estado:** aceptada · 2026-10-15
- **Contexto:** el prompt pide C# como lenguaje principal, .NET moderno estable, Windows primero y WPF + MVVM salvo
  alternativa mejor justificada. En la máquina hay SDK 8, 10 y 11 preview.
- **Decisión:** WPF sobre .NET 10 (LTS, soporte hasta noviembre de 2028), MVVM con CommunityToolkit.Mvvm 8.4.2,
  Generic Host para DI y ciclo de vida, WebView2 solo donde la web aporta ventaja clara (Monaco, xterm.js y, si hace
  falta, grafos). `global.json` fija el SDK 10.0.303 para que no se use el 11 preview, y declara
  Microsoft.Testing.Platform como runner de `dotnet test` (xUnit v3).
- **Alternativas:** WinUI 3 (menos controles de docking y grafos), Avalonia (WebView inmaduro en Windows),
  Electron/Tauri (contradicen «C# para todo el núcleo»). Tabla completa en [02](../02-arquitectura.md#7-decisiones-y-alternativas).
- **Consecuencias:** solo Windows en la UI; dominio y aplicación en `net10.0` para no cerrar la puerta a otra UI. Hay
  que construir el tema oscuro (no hay tema VS2022 oscuro para AvalonDock). WPF no tiene bindings compilados: los
  errores de binding se cubren con tests de ViewModel y, donde importa, tests de estilo en hilo STA.
