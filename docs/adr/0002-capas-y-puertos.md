# ADR-0002 · Once proyectos con dominio puro y puertos en Application

- **Estado:** aceptada · 2026-10-15
- **Contexto:** la sección 17 fija once proyectos y pide justificar sus dependencias. El juez y el auditor deben ser
  independientes del orquestador también en el código.
- **Decisión:** `Domain` sin dependencias; `Application` (casos de uso y puertos) depende solo de `Domain`; los siete
  módulos (`Orchestration`, `Debate`, `Providers`, `Observability`, `Memory`, `Security`, `Infrastructure`) dependen
  solo de `Application` y **no se referencian entre sí**; `Desktop` es la única raíz de composición; un único proyecto
  `Tests` organizado por carpetas.
- **Alternativas:** módulos que se llaman directamente (más simple, pero permite que `Debate` dependa de lo que
  `Orchestration` decida mostrar); un proyecto de tests por módulo (más aislamiento, más fricción; se reconsidera si el
  tiempo de compilación de los tests crece).
- **Consecuencias:** el compilador impide atajos entre módulos; toda colaboración pasa por un puerto conectado en
  `Desktop`. Los módulos se prueban con dobles de los puertos. El proyecto de tests es WPF (para probar ViewModels y
  estilos), así que no tiene el `using System.IO` implícito.
