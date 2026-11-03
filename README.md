# Agent Studio

IDE de escritorio para Windows (C# / .NET 10 / WPF) que coordina agentes de IA de distintos proveedores, les deja
colaborar o discrepar, y somete el resultado a un juez independiente que comprueba con evidencias que se cumplen los
requisitos.

> **Estado: Fase 0 (arquitectura y prototipo).** Lo único funcional en la app es el diagnóstico de proveedores: detecta
> Claude Code, Codex CLI y Gemini CLI, su versión y si hay sesión iniciada, sin enviar prompts ni consumir cuota.
> El resto está diseñado y con sus reglas de dominio probadas, pero no implementado. Ver
> [docs/09-informe-fase-0.md](docs/09-informe-fase-0.md).

## Requisitos

- Windows 10/11
- .NET SDK 10.0.303 o superior dentro de la banda 10.0 (fijado en `global.json`)
- Opcional, para el diagnóstico: Claude Code, Codex CLI y/o Gemini CLI instalados y con sesión iniciada por ti

## Compilar, probar y ejecutar

```powershell
dotnet build AgentStudio.slnx
dotnet test                       # Microsoft.Testing.Platform; no le pases -nologo
dotnet run --project src/AgentStudio.Desktop
```

Los tests de la categoría `LocalTools` ejecutan las sondas reales (`--version`, `claude auth status`,
`codex login status`); no envían prompts ni consumen cuota. Para excluirlos:
`dotnet test -- --filter-not-trait "Category=LocalTools"`.

## Estructura

```text
src/AgentStudio.Domain          Reglas puras: tareas y grafo, registro de decisiones, evidencias, veredictos, debates
src/AgentStudio.Application     Casos de uso y puertos (contratos entre módulos)
src/AgentStudio.Orchestration   Motor de orquestación (Fase 3)
src/AgentStudio.Debate          Debate Arena y juez (Fase 5)
src/AgentStudio.Providers       Sondas de CLIs (Fase 0) y adaptadores (Fase 2)
src/AgentStudio.Observability   Salud del contexto y consumo
src/AgentStudio.Memory          Memoria del proyecto (Fase 4)
src/AgentStudio.Security        Política de rutas, aprobaciones, credenciales
src/AgentStudio.Infrastructure  Procesos reales, localización de ejecutables, esquema SQLite
src/AgentStudio.Desktop         App WPF (raíz de composición)
tests/AgentStudio.Tests         xUnit v3
docs/                           Arquitectura, contratos, modelo de datos, plan, ADR, investigación
```

## Documentación

Empieza por [docs/README.md](docs/README.md): enlaza los doce entregables de la Fase 0, los ADR y la investigación
de proveedores.

## Principios

- Los agentes proponen; el sistema verifica. Las pruebas que cuentan las ejecuta Agent Studio, no el agente.
- Nada se borra sin rastro: decisiones, evidencias y veredictos son append-only.
- Agent Studio lanza los CLIs oficiales sin modificar y nunca toca sus credenciales.
- Cada métrica dice si es exacta, estimada o no disponible.
