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

## Qué necesita
El programa gh de GitHub, con la sesión iniciada, y que git pueda identificarse con tu gestor de credenciales.

## Qué no hace
- No fusiona sin que lo pidas y confirmes con Windows Hello cada vez.

## Dónde está
En «Repositorio y cambios», pestaña **Peticiones de fusión (PR)**. Pulsa una fila para ver su detalle y sus acciones.
- Un botón solo sale si tu versión de gh sabe hacerlo y la petición lo admite.
- Fusionar, cerrar y borrar la rama remota enseñan antes una tarjeta. Piden Windows Hello cada vez.
- Solo se fusiona la versión que juzgó el revisor.
- **Actualizar el PR…** vuelve a publicar la ejecución con el título y el texto que cambies. Solo sirve para una petición abierta que GitHub encuentre.
- Lo que Agent Studio deduce lleva la marca «(deducido)».

## La casilla «Sincronizar con GitHub»
Está en la cabecera y solo se activa con un proyecto. Si la marcas, Agent Studio pregunta a GitHub cada pocos minutos por tus peticiones. Sale el nombre del repositorio y sus números; nada más. Solo tu clic la cambia.

## Permisos de publicación
Un permiso guardado evita Windows Hello al publicar la rama y al escribir en la petición. Fusionar y borrar nunca se guardan.

## Si no hay conexión
La acción queda pendiente de sincronizar, con su motivo.
