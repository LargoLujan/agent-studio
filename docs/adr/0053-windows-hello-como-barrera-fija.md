# ADR-0053 · Windows Hello como barrera fija: una lista en el código que ningún ajuste ni fichero desactiva, y el bloqueo de Windows comprobado como alternativa

- **Estado:** propuesto · 2026-11-06. Esqueleto del P0 de la Fase 13; lo completa **WP5** (barrera de Windows Hello). Recoge
  D-37 a D-41 de [docs/17 §4.9](../17-constructor-visual-de-agentes.md#49-windows-hello-como-barrera-fija) y la decisión 6 (a)
  que aceptaste el 2026-11-06 (la lista y, sin Hello, el bloqueo de Windows comprobado). **Enmienda**
  [ADR-0037](0037-windows-hello-y-bloqueo-de-la-interfaz.md) y tu decisión 5 (a) del 2026-10-20.
- **Contexto:**
  - P18 pide que lo crítico pida siempre Windows Hello, también si un fichero de configuración da permisos.
  - Hoy el ajuste de Hello se puede apagar, y entonces decide el diálogo solo; lo destructivo nunca pide Hello; y dos caminos
    no pasan por la puerta: «Aprobar plan» del lienzo y el consentimiento de la recomendación de skills (docs/17 §0.2).
- **Decisión** (la de docs/17; WP5 la detalla aquí):
  1. **Una lista en el código, por acción** (D-37): `Domain/Security/PresencePolicy.cs` (P0). «Siempre»: entregar a tu rama,
     aceptar un historial como nuevo origen, dar a un agente shell, red, Codex con escritura o más carpetas, aprobar un plan
     con algo de eso, cambiar el perfil de verificación, quitar una protección, y lo remoto y los secretos de las Fases 10 y
     11. «Configurable» (activo por defecto): aprobar un plan sin nada de eso, debates, auditorías, revisiones y descartar
     trabajo de un agente. «Nunca»: responder a una pregunta y el OK de cuota. Las capacidades las calcula
     `MediumIntegrityWriters`, sin otra lista.
  2. **Ningún fichero ni ajuste la desactiva** (D-38): el ajuste pasa a llamarse «Pedir Windows Hello también en las
     aprobaciones importantes» (`AppSettings.WindowsHelloForImportantApprovals`; el fichero de antes se lee con su nombre
     viejo; P0) y solo gobierna lo configurable. Hasta que WP5 conecte la barrera, gobierna lo de la Fase 8.
  3. **Una sola regla para todos los caminos** (D-39): la puerta, «Aprobar plan», aceptar un historial, guardar una plantilla
     con una capacidad de alto riesgo o un perfil, y el consentimiento de skills, que deja por fin su `ApprovalGranted`.
  4. **Sin Hello en el equipo** (D-40 (a)): la app bloquea Windows y comprueba con el estado de la sesión que se desbloqueó
     después de la petición (`IWindowsLockConfirmation`, `WindowsLockResult`; se anota como
     `ApprovalVerification.WindowsLock`; P0).
  5. **Trabajo desatendido** (D-41): lo que la ejecución necesita se aprueba al principio; la entrega espera al final; el plazo
     de 30 minutos sigue.
- **Alternativas:** las de la decisión 6 (todo lo crítico siempre; todo configurable; sin Hello, denegar o el diálogo solo).
- **Consecuencias:** pendientes de WP5 (las pruebas con ajustes y ficheros manipulados de F13-16, el estado de la sesión tras
  desbloquear con PIN medido en tu equipo, y la demostración).
