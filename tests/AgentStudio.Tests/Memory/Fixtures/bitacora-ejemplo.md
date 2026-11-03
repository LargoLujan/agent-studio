# Bitácora · 2026-03-10

Registro automático de cada prompt (hook `UserPromptSubmit`): hora · sesión · tags · extracto.
Al cerrar cada petición, Claude añade debajo una línea «Hecho:» con el resultado.

- **08:59:58** · P-0001 · Orquestador · claude-code/claude-opus-5 · #prompt #orquestacion #fase-4 · «Planificar la memoria del proyecto»
  - Resultado P-0001 · 09:02:10 · Succeeded · 132,0 s · tokens 18.300 → 2.150 (exactos)
- **09:05:07** · P-0002 · Backend · codex-cli/gpt-5 · #prompt #agente-backend #tests · «Implementar el almacén \[T-12\]» (tarea T-12)
- **09:06:00** · sesión `eb1c6494` · #prompt #docs #fase-4 · «Revisa el ADR-0023 y dime si falta algo»
  - Hecho: ADR-0023 revisado; faltaba el ejemplo del fallo cerrado.
  - Resultado P-0002 · 09:09:47 · Failed · 280,4 s · tokens 1.200 → n/d (estimados) · las pruebas fallan
- **09:10:00** · P-0003 · Juez · claude-code/- · #prompt #juez · «Auditar la entrega»
  - Resultado P-0003 · 09:11:00 · RateLimited · 60,0 s · tokens no disponibles
- **09:12:00** · P-0004 · Backend · codex-cli/gpt-5 · #prompt #agente-backend · «Reintentar el almacén» (tarea T-12)

## Registro retroactivo (sin hora exacta)

- **mañana** · #prompt #git · «Línea retroactiva sin hora exacta»
- **10:00:01** · sesión `eb1c6494` · #prompt #tests #fase-4 · «continua»
  - Hecho (en curso): pruebas del empaquetador escritas.
  - Hecho (00:27, cierre): todo integrado; 4700 pruebas en verde.
  - Nota: esta línea no es de ninguno de los dos escritores.
esto es una línea rota sin formato
- **11:00:00** · P-0007 · sin cerrar
- **10:30:00** · P-0005 · Orquestador · claude-code/claude-opus-5 · #prompt #orquestacion · «Resumen \*final\*»
  - Resultado P-0005 · 10:31:00 · LimitReached · 60,0 s · tokens 500 → 20 (exactos)
- **10:31:30** · P-0006 · Documentador · gemini-cli/gemini-2.5-pro · #prompt #docs · «Cancelada a medias»
  - Resultado P-0006 · 10:32:00 · Cancelled · 30,5 s · tokens no disponibles
  - Hecho (petición «memoria»): la memoria ya exporta su Markdown con `<b>` y #títulos escapados.
