---
title: Skills: instrucciones que se dan a los agentes
screens: skills
info: Info_Skills, Info_SkillsDoctor
---
Una skill es un paquete de instrucciones que un agente recibe para hacer mejor una tarea concreta.

## Qué puedes hacer
- Ver el catálogo y su estado de salud.
- Activar una skill para un agente, para el proyecto o para todos tus proyectos.
- Pedir una recomendación. La versión gratuita usa reglas. La de un agente gasta el límite de uso de tu plan y te pide tu OK.

## Cuidado con el coste
Cada skill activada ocupa sitio en la memoria de trabajo del agente en todas sus ejecuciones. Activa pocas y buenas. La comprobación del tamaño dice cuánto ocupa su SKILL.md al usarla.

## Un agente que vuelve
Si quitas un agente y añades otro con el mismo nombre, las skills del anterior empiezan apagadas. La ficha lo dice.

## Quién activa
Solo tú. Una recomendación nunca activa nada sola.

## Comprobar en GitHub
Agent Studio puede preguntar a GitHub si una skill cambió en su origen o se archivó.
- Está apagado. Actívalo con la casilla de la pantalla.
- Solo pregunta cuando pulsas «Comprobar en GitHub», nunca en segundo plano.
- Se conecta solo a api.github.com. De cada skill envía el propietario, el repositorio, la rama, la carpeta y la versión fijada (commit).
- No envía nada de tu proyecto, ni rutas, ni tu nombre.

## Clave de acceso de GitHub (opcional)
Con una clave personal puedes hacer 5 000 consultas por hora en vez de 60.
- Guardarla, borrarla y usarla te pide confirmar con Windows Hello.
- Se guarda cifrada para tu usuario de Windows. No protege de un programa que corra con tu usuario.

## Scripts
Si una skill trae scripts, sale con un aviso hasta que los marques como revisados. No se ejecuta ninguno.
