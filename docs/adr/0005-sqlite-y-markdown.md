# ADR-0005 · SQLite con SQL explícito para la estructura, Markdown para las personas

- **Estado:** aceptada · 2026-10-15 · revisada el 2026-10-16: sin Dapper, ver [ADR-0010](0010-estado-derivado-del-registro.md)
- **Contexto:** la sección 14 pide SQLite para persistencia estructurada y Markdown para documentación legible, con
  registro cronológico y temático y recuperación selectiva.
- **Decisión:** una base SQLite por proyecto de usuario (`.agentstudio/agentstudio.db`) con Microsoft.Data.Sqlite
  10.0.13, migraciones SQL numeradas como recursos embebidos, claves foráneas activadas en cada conexión, triggers
  append-only y FTS5 para la memoria. Repositorios con SQL explícito (Dapper cuando lleguen, Fases 3-4). La memoria se
  exporta a Markdown (cronológico y temático); el Markdown no es la fuente de verdad.
- **Alternativas:** EF Core (cómodo, pero oculta triggers, FTS5 e índices, justo lo que importa aquí); ficheros
  JSON/Markdown como única persistencia (sin integridad ni consultas); LiteDB (sin SQL ni FTS5 comparable).
- **Consecuencias:** el esquema se prueba directamente (tablas, triggers, FK, FTS5). Cada cambio de esquema es un
  fichero de migración nuevo.
- **Revisión (2026-10-16):** los almacenes de la Fase 3 se escribieron con SQL explícito sobre Microsoft.Data.Sqlite,
  sin Dapper, como decide [ADR-0010](0010-estado-derivado-del-registro.md): un paquete menos y el SQL a la vista donde
  importan los triggers. Las conexiones abren con `busy_timeout` y `journal_mode=WAL` además de las claves foráneas.
