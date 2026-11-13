---
title: Auto Doctor: is your computer ready?
screens: doctor
info: Info_Doctor, Info_Doctor_Providers, Info_Doctor_Tools, Info_Doctor_Project, Info_Doctor_DeclaredEnvironment, Info_Doctor_Containers
---
Auto Doctor checks whether your computer is ready to work on the project. It runs nothing of the project.

## What it checks
- The agent programs, their version and whether you are signed in.
- The tools of your computer, such as .NET, Node, Python, Git and WebView2.
- The project: whether it is a repository, whether its history is complete and whether it has tests defined.
- What the project says it needs, against what you have installed.
- Docker and WSL. They only count as a problem if the project uses containers.

## How it does it
Each tool is asked from an empty folder of Agent Studio, never from your project. A program that is only inside the project is not used: Auto Doctor tells you.

## What it does with what it finds
Each check ends as correct, warning, error or not checked. Not checked is not correct: it means nobody could look at it.

## The fixes
«Go to…» takes you to a screen. «How to fix it» gives you a command to copy and the official page. Agent Studio does not run that command: you run it. It never installs anything on your computer.