# ADR-0033 · El alcance de la ampliación «plataforma de proyectos»: qué entra, con qué cambio de perspectiva y qué se descarta

- **Estado:** aceptada · 2026-10-20. Recoge el análisis de [docs/16](../16-ampliacion-plataforma-de-proyectos.md) y las seis
  decisiones de su §7, resueltas por el usuario con la opción recomendada (las cuatro preguntadas) o por defecto (las otras
  dos).
- **Contexto:** el usuario pidió ampliar Agent Studio con un texto de 19 secciones que describe una plataforma de proyectos
  genérica: aislamiento entre inquilinos, entornos y contenedores gestionados, Git de cuatro proveedores, un gestor de
  trabajo estilo Jira, informes de hito, bandeja del sistema, Windows Hello y reanudación automática. Agent Studio es un IDE
  personal, de un solo usuario y en Windows ([ADR-0003](0003-integracion-por-clis-oficiales.md)), cuyo valor es que el
  trabajo de los agentes sea verificable: requisitos congelados, un registro encadenado y anclado, evidencias que captura el
  sistema y un juez de otro vendor.
- **Decisión:**
  1. **Entra, como extensión de lo que existe:**
     - el trabajo desatendido: esperas por límite hasta el reinicio con 3 minutos de margen, reanudación tras reiniciar
       con un consentimiento por ejecución, el final de cada intento clasificado, el traspaso de contexto que construye el
       sistema, la bandeja y la salida gestionada (Fase 8);
     - la trazabilidad como **vistas sobre el registro**: hitos, backlog, tablero, vínculos, participación, consumo por
       hito y por actividad, estimación frente a real y «Repositorio y cambios» en local. Solo se guardan tres cosas
       nuevas, append-only y ancladas: la cabecera del hito, los elementos del backlog y los vínculos (Fase 9);
     - la detección del entorno que declara el proyecto y los diagramas generados con la versión que describen (Fase 6);
     - GitHub por `gh`, en un componente aparte de la capa git interna y con políticas por clase de operación; Azure
       DevOps solo si el usuario lo va a usar (Fase 10);
     - el aislamiento fuerte de la verificación, la bóveda DPAPI por proyecto y la vista de permisos efectivos (Fase 7);
     - Windows Hello como confirmación reforzada de las aprobaciones críticas y un bloqueo opcional que no detiene el
       trabajo (Fase 8).
  2. **Entra con un cambio de perspectiva:** donde el texto pide sistemas nuevos (un Jira, una tabla de costes, un
     cliente git), el producto los construye como proyecciones del registro verificado o sobre las capas que ya existen.
  3. **Se descarta, con su motivo en docs/16 §2:**
     - controlar la red de cada proyecto por destino, que exige administrador;
     - credenciales de los CLIs por proyecto copiadas de las del usuario, que `CLAUDE.md` prohíbe (los perfiles por
       proyecto con su propio inicio de sesión quedan como opción, decisión 6, no elegida);
     - los webhooks: una app de escritorio no tiene endpoint público; se usa sondeo condicional;
     - «simular IPs» editando `hosts`, y exponer servicios a la red local o a Internet por defecto;
     - el coste imputado de una suscripción, que sería un número inventado;
     - un cliente git completo;
     - los adaptadores de GitLab y Bitbucket mientras no haya un repositorio real con el que verificarlos.
  4. **Aplazado:** la Fase 11 (entornos y servicios con compose y Dev Containers del propio proyecto) entra cuando un
     proyecto del usuario lo necesite. Su detección ya forma parte de la Fase 6.
  5. **Orden:** 8 → 9 → 6 → 10 → 7, con la 11 aplazada. Los números 6 y 7 se conservan porque docs/08 ya los usa.
- **Enmiendas que esta ampliación trae, cada una en su ADR:**
  - ADR-0010 (nada se relanza solo): la reanudación automática con el consentimiento de la cabecera (ADR-0034);
  - ADR-0012 (sin red en git): el Git remoto en un componente aparte (ADR-0041);
  - ADR-0001 (TFM): solo Desktop y el proyecto de pruebas pasan a un TFM con el SDK de Windows (ADR-0037);
  - ADR-0017 y ADR-0018 (Low no bloquea lecturas ni red): F7-4 decide el aislamiento fuerte (ADR-0044).
- **Alternativas:**
  - Construir la plataforma tal cual: duplicaría lo que el registro ya guarda y prometería aislamientos que Windows no da
    sin administrador.
  - Rechazar la ampliación: perdería lo más valioso (que el trabajo largo termine solo y que la trazabilidad se pueda
    recorrer).
- **Consecuencias:**
  - Cuatro fases nuevas (8 a 11) y criterios nuevos en las Fases 6 y 7, en [docs/08](../08-plan-y-criterios-de-aceptacion.md).
  - Los ADR 0034 a 0046 concretan cada parte.
  - Ninguna regla de seguridad aceptada se relaja en silencio: cada choque de docs/16 §2.0 tiene su resolución explícita.
