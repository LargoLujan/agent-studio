# Salidas grabadas de las herramientas del doctor

Las de nombre `real-*` se capturaron el 2026-11-12 en la máquina de desarrollo (Windows en castellano), lanzadas desde una
carpeta vacía fuera del repositorio, con `DOCKER_CONFIG` en una carpeta vacía y `WSL_UTF8=1`. En esta máquina Docker Desktop
estaba con el motor parado (código de salida 1, `"Server":null`) y la única distribución de WSL era `docker-desktop`.
`wsl --status` da UTF-16 sin `WSL_UTF8=1` y UTF-8 con él (comprobado).

Las de nombre `handmade-*` **no son capturas**: esta máquina no puede producirlas (Docker en marcha, WSL sin distribución,
Windows en inglés, WSL no instalado). Están escritas a mano según el formato de las reales y la documentación; si la
herramienta real difiere, el lector las dará como «sin comprobar» y estas pruebas dejarán de representar la realidad.