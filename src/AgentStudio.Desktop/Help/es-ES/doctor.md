---
title: Auto Doctor: ¿está listo tu equipo?
screens: doctor
info: Info_Doctor, Info_Doctor_Providers, Info_Doctor_Tools, Info_Doctor_Project, Info_Doctor_DeclaredEnvironment, Info_Doctor_Containers
---
Auto Doctor mira si tu equipo está listo para trabajar en el proyecto. No ejecuta nada del proyecto.

## Qué comprueba
- Los programas de los agentes, su versión y si tienes la sesión iniciada.
- Las herramientas del equipo, como .NET, Node, Python, Git y WebView2.
- El proyecto: si es un repositorio, si su historial está completo y si tiene pruebas definidas.
- Lo que el proyecto dice que necesita, frente a lo que tienes instalado.
- Docker y WSL. Solo cuentan como problema si el proyecto usa contenedores.

## Cómo lo hace
Cada herramienta se pregunta desde una carpeta vacía de Agent Studio, nunca desde tu proyecto. Un programa que solo está dentro del proyecto no se usa: Auto Doctor te lo dice.

## Qué hace con lo que encuentra
Cada comprobación termina en correcto, aviso, error o sin comprobar. Sin comprobar no es correcto: significa que nadie ha podido mirarlo.

## Los arreglos
«Ir a…» te lleva a una pantalla. «Cómo arreglarlo» te da una orden para copiar y la página oficial. Agent Studio no ejecuta esa orden: la ejecutas tú. Nunca instala nada en tu equipo.