---
title: Repository and changes
screens: repository
info: Info_RepositorySummary, Info_RepositoryBranches, Info_RepositoryCommits, Info_RepositoryActivity
---
This screen tells what happened in the repository of the project. Its first four tabs only read: they change nothing.

## What you see
- **The summary:** the branch you are on, whether there are unsaved changes and where the repository is published.
- **The branches:** yours and the ones Agent Studio creates for each run.
- **The saved versions:** each one says who made it. If Agent Studio did, it says the run, the task, the attempt, the agent and the model.
- **The activity:** the integrations and the deliveries, and whether they worked. Also what Agent Studio did on GitHub or saw there, with its real result. What waits to sync goes on top, with its reason.
- **The pull requests (PR):** the ones you follow on GitHub and their actions. The article «Publish and open a pull request (PR)» explains it.
- A run branch says whether it is published and at which version. It also says whether it was deleted on GitHub. It only says so when the history knows it.

## How to read it
Agent Studio only says it made a version if the log backs it. If not, at most it marks it as a **suggestion**: it may be true, but nobody has confirmed it.

## Tips
- If Agent Studio does not read the repository, the screen says why.
- Filter the activity by run, task, milestone, agent or model.
