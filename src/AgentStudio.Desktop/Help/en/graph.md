---
title: Auto Graph: how the project is built
screens: graph
---
Auto Graph draws how a C# solution is built: its projects, its namespaces and what each one uses.

## What you see
- The graph, for one specific saved version of the project. The screen says which.
- The diagrams as text, which carry the version they describe and warn you when they are out of date.
- The discrepancies: broken links, documents that cite files that no longer exist and requirements that no task covers.

## How it reads
It reads the files as text, without building or running anything of the project. What it cannot resolve without running anything is marked as partial, with its reason.

## What an AI proposes
The relations an AI proposes are drawn differently and never count as read. They are only asked for with your OK each time.
