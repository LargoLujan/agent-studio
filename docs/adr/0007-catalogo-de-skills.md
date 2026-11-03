# ADR-0007 · Catálogo de skills por sector, con tags, ámbitos y salud

- **Estado:** aceptada · 2026-10-15
- **Contexto:** el usuario quiere un índice de las mejores skills oficiales y de la comunidad, clasificado en carpetas
  por sector TI, con tags para que los agentes las recomienden, activable por gusto y con indicador de salud. Las tres
  CLIs adoptan el estándar Agent Skills, pero cada una las descubre en rutas distintas, y cada skill activada consume
  contexto en todas las ejecuciones. Ya existe un índice curado del usuario (120 skills fijadas por commit).
- **Decisión:**
  1. Un **sector principal por skill** (carpeta) de una taxonomía fija de 16 sectores, y **tags libres** para el
     resto (tecnologías, sectores secundarios, prácticas).
  2. **Fuente fijada por commit** y licencia registrada; altas y actualizaciones llegan como propuestas revisadas.
     El índice local del usuario es la semilla (importador propio).
  3. **Activación por ámbitos** Global < Proyecto < Agente; por defecto, desactivada.
  4. **Materialización por ejecución** en un directorio de sesión (plugin con `--plugin-dir` en Claude Code;
     `.agents/skills/` del worktree en Codex y Gemini), **sin escribir nunca** en la configuración global del usuario.
  5. **Recomendación** determinista y explicable en el dominio; la asistida por agente usa una herramienta MCP local
     y solo propone.
  6. **Salud** como el peor estado de comprobaciones concretas (frontmatter, licencia, fijación, upstream, archivado,
     scripts, tamaño).
- **Alternativas:** usar los marketplaces de cada CLI (tres sistemas distintos y escritura en la configuración
  global); activar todo lo del catálogo (coste de contexto y riesgo); recomendación solo por LLM (no explicable, con
  coste en cada consulta).
- **Consecuencias:** hace falta mantener el mapeo de rutas por CLI y verificarlo en cada versión (Fase 2); las skills
  globales que el usuario ya tenga seguirán cargándose y la UI debe advertirlo.
