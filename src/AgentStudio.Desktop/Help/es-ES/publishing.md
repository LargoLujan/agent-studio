---
title: Publicar y abrir una petición de fusión (PR)
screens: publishing
---
Publicar sube a GitHub el trabajo de una ejecución que un revisor independiente (juez) aprobó. Después abre una petición de fusión (PR) para que alguien la revise.

## Qué hace
- Sube la rama de la ejecución a GitHub. Tu rama no cambia.
- Abre la petición de fusión con un título y un texto que puedes cambiar antes.
- Publica el resultado de la revisión como un estado y un comentario. Nunca lo publica como una aprobación tuya.
- Si lo permites, mira de vez en cuando cómo va la petición. Así sabe si se fusionó, si la cerraron o si hay un despliegue.

## Qué necesita
- El programa gh de GitHub, con la sesión iniciada. Agent Studio usa esa sesión y nunca ve tu clave.
- Que git pueda identificarse en GitHub con tu gestor de credenciales.
- Que el repositorio no defina nada que cambie adónde se envía o cómo te identificas.

## Qué no hace
- No fusiona sin que lo pidas y confirmes con Windows Hello cada vez.
- No fuerza ningún envío y no borra ramas por su cuenta.
- No sube ficheros de Git LFS. Esos los subes tú con tu programa de git.

## Si no hay conexión
La acción queda pendiente de sincronizar, con su motivo y la hora del siguiente intento. Nada se da por hecho hasta que GitHub lo confirma.
