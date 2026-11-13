---
title: Auto Graph: how the project is built
screens: graph
info: Info_Graph, Info_Graph_Map, Info_Graph_Coverage, Info_Graph_Propose
---
Auto Graph draws how a C# solution is built: its projects, its namespaces and what each one uses.

## What you see
- The graph, for one specific saved version of the project. The screen says which, and warns you if your folder has changes.
- The diagrams as text, which carry the version they describe and warn you when they are out of date.
- The discrepancies: broken links, documents that cite files that no longer exist and requirements that no task covers.

## How it reads
It reads the files as text, without building or running anything of the project. What it cannot resolve without running anything is marked as partial, with its reason and its line.

## What you can do in the graph
- Choose a box to see its files and the tasks that changed files of that project.
- Open the file of a project or of a namespace, or go to the task.
- Unfold a project to see its namespaces.
- Use the list below, which says the same as the drawing, with the keyboard.

## What an AI proposes
The relations an AI proposes are drawn with a dashed line and the label AI, and never count as read. They are only asked for with your OK each time. They spend the usage limit of your plan.
