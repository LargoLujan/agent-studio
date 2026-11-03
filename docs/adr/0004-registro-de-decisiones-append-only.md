# ADR-0004 · Registro de decisiones y evidencias append-only con cadena de hashes

- **Estado:** aceptada · 2026-10-15
- **Contexto:** la sección 9 exige que ninguna decisión del orquestador borre o altere evidencias sin trazabilidad, y el
  juez necesita material que no se pueda retocar después de examinarlo.
- **Decisión:** `DecisionLog` encadena cada entrada con SHA-256 de su contenido canónico y del hash anterior; `Verify`
  detecta modificación, borrado intermedio, reordenación y cronología imposible. Las correcciones son entradas nuevas
  (`Correction`) que referencian la anterior. Las evidencias son inmutables y direccionadas por contenido. En SQLite,
  triggers `BEFORE UPDATE/DELETE` abortan sobre `decision_log`, `evidence`, `verdicts` y `debate_contributions`.
  Cada veredicto guarda el hash de cabeza del registro que examinó.
- **Alternativas:** tabla normal con auditoría (no detecta manipulación); firma digital o libro mayor externo (más
  fuerte, más complejo; Fase 7).
  Triggers `BEFORE INSERT` impiden además `INSERT OR REPLACE` sobre una clave existente (que en SQLite borra y
  reinserta sin disparar los triggers de borrado), y la conexión activa `recursive_triggers`. `requirement_sets` es
  también append-only.
- **Consecuencias:** la manipulación es **evidente, no imposible**: quien controle el fichero de la base de datos
  puede borrar los triggers (`DROP TRIGGER`), reescribir toda la cadena o truncar el final. Los triggers protegen
  frente a errores y escrituras indebidas de la propia aplicación; la cadena de hashes detecta las ediciones; la
  cabeza se ancla fuera (veredictos, exportaciones) y la firma se evalúa en la Fase 7. Corregir siempre cuesta una
  entrada nueva.
