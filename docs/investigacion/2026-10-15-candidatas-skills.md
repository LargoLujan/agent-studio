# Propuesta: skills para cubrir los huecos del catálogo — 2026-10-15

> **Estado: propuesta pendiente de tu aprobación.** No se ha dado de alta nada. Datos verificados con la API de
> GitHub el 2026-10-15 (frontmatter `name` y `description` leído en todas; ninguna archivada). SHA = último commit de
> la carpeta, truncado; al dar de alta se fija el SHA completo. Contexto: tras importar tu índice
> (`C:/skills-index`) quedaban **arquitectura de software 0**, arquitectura de datos 1, documentación 1 y
> backend 2 ([10](../10-catalogo-de-skills.md)).

## Recomendadas para el primer alta (11)

| # | Skill | Para qué | Repositorio / carpeta | Licencia | SHA | Sector |
|---|---|---|---|---|---|---|
| 1 | architecture | Crear y evaluar ADR | anthropics/knowledge-work-plugins `engineering/skills/architecture` | Apache-2.0 | 2d6f7e22dd | Arquitectura de software |
| 2 | system-design | Diseño de sistemas y servicios | anthropics/knowledge-work-plugins `engineering/skills/system-design` | Apache-2.0 | 4fa3cb92e2 | Arquitectura de software |
| 3 | architecture-patterns | Clean, Hexagonal y DDD | wshobson/agents `plugins/backend-development/skills/architecture-patterns` | MIT | be57c0b2e3 | Arquitectura de software |
| 4 | domain-modeling | Modelo de dominio, glosario y ADR | mattpocock/skills `skills/engineering/domain-modeling` | MIT | d80fa0f4eb | Arquitectura de software |
| 5 | documentation | Documentación técnica y README | anthropics/knowledge-work-plugins `engineering/skills/documentation` | Apache-2.0 | 4fa3cb92e2 | Documentación |
| 6 | runbook | Runbooks operativos | anthropics/knowledge-work-plugins `operations/skills/runbook` | Apache-2.0 | 2d6f7e22dd | Documentación |
| 7 | postgresql-table-design | Diseño de esquemas PostgreSQL | wshobson/agents `plugins/database-design/skills/postgresql-table-design` | MIT | c80732de69 | Arquitectura de datos |
| 8 | cosmosdb-datamodeling | Modelado NoSQL para Cosmos DB | github/awesome-copilot `skills/cosmosdb-datamodeling` | MIT | caab1f623b | Arquitectura de datos |
| 9 | aspnet-core | Construir y revisar apps ASP.NET Core | openai/skills `skills/.curated/aspnet-core` | Apache-2.0 (LICENSE.txt de la carpeta) | c207989386 | Backend |
| 10 | dotnet-webapi | Web API en .NET | dotnet/skills `plugins/dotnet-aspnetcore/skills/dotnet-webapi` | MIT | 2d163bb600 | Backend |
| 11 | api-design-principles | Diseño de APIs REST y GraphQL | wshobson/agents `plugins/backend-development/skills/api-design-principles` | MIT | be57c0b2e3 | Backend |

## Opcionales

| Skill | Repositorio / carpeta | Nota |
|---|---|---|
| architecture-decision-records | wshobson/agents `plugins/documentation-generation/skills/architecture-decision-records` | Solapa con #1 |
| cloud-design-patterns | github/awesome-copilot `skills/cloud-design-patterns` | 42 patrones cloud |
| improve-codebase-architecture | mattpocock/skills `skills/engineering/improve-codebase-architecture` | Revisión de arquitectura |
| documentation-writer | github/awesome-copilot `skills/documentation-writer` | Diátaxis |
| openapi-spec-generation | wshobson/agents `plugins/documentation-generation/skills/openapi-spec-generation` | OpenAPI 3.1 |
| docs-sync-audit | github/awesome-copilot `skills/docs-sync-audit` | **Trae `scripts/docs_drift.py`: revisar antes** |
| optimizing-ef-core-queries | dotnet/skills `plugins/dotnet-data/skills/optimizing-ef-core-queries` | EF Core |
| nodejs-backend-patterns | wshobson/agents `plugins/javascript-typescript/skills/nodejs-backend-patterns` | Node |
| protobuf-grpc-api-review | github/awesome-copilot `skills/protobuf-grpc-api-review` | gRPC |
| doc-coauthoring | anthropics/skills `skills/doc-coauthoring` | **Licencia sin determinar** (sin LICENSE en repo ni carpeta) |

## Último recurso (poca tracción)

`data_vault_2` (ivanshamaev/de-agent-skills, 21 estrellas, **sin licencia**), `model-star-schema` (DATA-AI-XYZ/Atelier,
0 estrellas, MIT), `architecting-data` (ancoleman/ai-design-components, 525 estrellas, MIT, sin cambios desde
2025-12). No se encontraron skills de calidad oficial para Kimball, Data Vault, data mesh ni contratos de datos: es
un hueco real que quizá merezca una skill propia.

## Descartadas

Duplicadas con tu índice (`fastapi`, `fastapi-router-py`, `data-quality-frameworks`, `dbt-transformation-patterns`,
`sql-optimization`, `spark-optimization`); agregadores comunitarios de calidad dudosa; `obra/superpowers` y
`anthropics/claude-plugins-official` no aportan skills de estos sectores.

## Pendiente antes de dar de alta

- Leer el `SKILL.md` completo y las carpetas `references/`/`assets/` (solo se leyó el frontmatter y el primer nivel).
- Fijar el SHA completo (`gh api repos/{o}/{r}/commits?path=<carpeta>&per_page=1`).
- Decidir dónde se dan de alta: en tu índice `C:/skills-index` (con `index-review`), que Agent Studio
  importa, o directamente en el catálogo de Agent Studio cuando exista su persistencia (Fase 4).
