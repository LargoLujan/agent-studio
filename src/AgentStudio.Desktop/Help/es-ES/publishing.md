---
title: Publicar y abrir una petición de fusión (PR)
screens: publishing
info: Info_Hosting_PullRequests, Info_Hosting_Permissions, Info_Hosting_MergedUnjudged
---
Publicar sube a GitHub el trabajo de una ejecución que un revisor independiente (juez) aprobó. Después abre una petición de fusión (PR) para que alguien la revise.

## Qué hace
- Sube la rama de la ejecución a GitHub. Tu rama no cambia.
- Abre la petición con un título y un texto que puedes cambiar antes.
- Publica el resultado de la revisión como un estado y un comentario. Nunca como una aprobación tuya.
- Si lo permites, mira de vez en cuando cómo va la petición.

## Qué necesita
- El programa gh de GitHub, con la sesión iniciada. Agent Studio nunca ve tu clave.
- Que git pueda identificarse en GitHub con tu gestor de credenciales.

## Qué no hace
- No fusiona sin que lo pidas y confirmes con Windows Hello cada vez.
- No fuerza ningún envío y no borra ramas por su cuenta.
- No sube ficheros de Git LFS.

## Dónde está
En «Repositorio y cambios», pestaña **Peticiones de fusión (PR)**. Pulsa una fila para ver su detalle y sus acciones.
- Un botón solo sale si tu versión de gh sabe hacerlo y la petición lo admite.
- Fusionar, cerrar y borrar la rama remota enseñan antes una tarjeta. Piden Windows Hello cada vez.
- Solo se fusiona la versión que juzgó el revisor. «Actualizar el PR…» publica otra vez con el texto que cambies.
- Lo que Agent Studio deduce lleva la marca «(deducido)».

## Permisos de publicación
Un permiso guardado evita Windows Hello al publicar la rama y al escribir en la petición. Fusionar y borrar nunca se guardan.

## Fusionado con cambios que el juez no vio
Si alguien subió cambios tras la revisión y se fusionó así, el trabajo no cuenta como integrado.

## Si no hay conexión
La acción queda pendiente de sincronizar, con su motivo. Nada se da por hecho hasta que GitHub lo confirma.
