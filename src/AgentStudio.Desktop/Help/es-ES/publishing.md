---
title: Publicar y abrir una petición de fusión (PR)
screens: publishing
info: Info_Hosting_PullRequests, Info_Hosting_Permissions, Info_Hosting_MergedUnjudged
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

## Dónde está
En «Repositorio y cambios», en la pestaña **Peticiones de fusión (PR)**. Arriba ves el repositorio de GitHub, la sesión y la última sincronización. Debajo, las peticiones que sigues, una fila cada una.

- Pulsa una fila para ver su detalle: las protecciones de la rama base, los checks que fallan y las versiones guardadas antes y después de fusionar.
- Cada botón solo sale si tu versión de gh sabe hacerlo y la petición está en un estado que lo admite.
- Fusionar, cerrar y borrar la rama remota enseñan primero una tarjeta con lo que harán. Piden Windows Hello cada vez.
- Lo que escribe GitHub (el título, el nombre de un check) se enseña como dato, tal cual llega.
- Lo que Agent Studio deduce lleva la marca «(deducido)»: el modo de fusión y una posible reversión.

## Permisos de publicación
Un permiso guardado evita Windows Hello al publicar la rama y al escribir en la petición. Los das y los retiras en el apartado plegable «Permisos de publicación». Fusionar y borrar nunca se pueden guardar.

## Fusionado con cambios que el juez no vio
Si alguien subió cambios después de la revisión y la petición se fusionó así, el trabajo no cuenta como integrado. Su tarjeta del tablero sigue en «Hecho» y lo dice.

## Si no hay conexión
La acción queda pendiente de sincronizar, con su motivo y la hora del siguiente intento. Nada se da por hecho hasta que GitHub lo confirma.
