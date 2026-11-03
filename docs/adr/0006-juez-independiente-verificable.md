# ADR-0006 · Juez independiente con veredicto verificable

- **Estado:** aceptada · 2026-10-15
- **Contexto:** las secciones 7 a 9 piden un juez que detecte incumplimientos, pueda bloquear la aprobación, audite al
  orquestador y no sea infalible; y prohíben que una mayoría de agentes sustituya a las pruebas.
- **Decisión:**
  1. El juez corre por defecto en un **vendor distinto** de los participantes y del orquestador (se compara
     `VendorId`, no el adaptador). Es de solo lectura por construcción (`AgentDefinition.Validate`).
  2. Recibe un **dossier construido por el sistema** (`IJudgeDossierBuilder`), nunca por el orquestador.
  3. Devuelve un **veredicto estructurado** que el `VerdictValidator` comprueba con 21 reglas deterministas; entre
     ellas, no aprobar con la última ejecución de tests fallida ni con requisitos obligatorios apoyados solo en
     afirmaciones de agentes.
  4. Un veredicto inválido se devuelve una vez al juez; si sigue inválido, se escala al usuario. El usuario puede
     impugnar cualquier veredicto. «No resuelto» es un resultado legítimo.
- **Alternativas:** juez del mismo proveedor (sesgos compartidos); votación por mayoría (prohibida como sustituto de
  pruebas); solo tests automáticos (no cubren diseño ni documentación).
- **Consecuencias:** hacen falta dos proveedores distintos con sesión para el flujo completo (hoy hay dos: Anthropic y
  OpenAI). El coste en tokens del juez crece con el proyecto: el dossier debe ser selectivo (riesgo R8).

## Adenda (2026-10-19): debates con dos vendors

En los debates de la Fase 5, con solo dos vendors, este ADR se complementa con
[ADR-0027](0027-independencia-del-juez-en-debates-con-dos-vendors.md): por defecto, los participantes comparten vendor y
el juez es del otro, así que este ADR no cambia. Mezclar vendors es una dispensa explícita y registrada (jurado cruzado al
elegir; juez del vendor que no produjo el trabajo al revisar), y la regla de los productores
([ADR-0014](0014-independencia-del-juez-con-dos-vendors.md)) no se dispensa nunca.
