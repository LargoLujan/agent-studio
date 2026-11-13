---
title: Skills: instructions given to the agents
screens: skills
info: Info_Skills, Info_SkillsDoctor
---
A skill is a package of instructions an agent receives to do a specific task better.

## What you can do
- See the catalog and its health.
- Turn a skill on for one agent, for the project or for all your projects.
- Ask for a recommendation. The free version uses rules. The agent version uses the usage limit of your plan and asks for your OK.

## Mind the cost
Each active skill takes room in the agent's working memory in all its runs. Turn on few and good ones. The size check says how much its SKILL.md takes when used.

## An agent that comes back
If you remove an agent and add another with the same name, the first one's skills start off. The card says so.

## Who turns them on
Only you. A recommendation never turns anything on by itself.

## Check on GitHub
Agent Studio can ask GitHub whether a skill changed at its source or was archived.
- It is off. Turn it on with the box on the screen.
- It asks only when you press "Check on GitHub", never in the background.
- It connects only to api.github.com. For each skill it sends the owner, the repository, the branch, the folder and the pinned version (commit).
- It sends nothing of your project, no paths and not your name.

## GitHub access key (optional)
With a personal key you can make 5,000 requests an hour instead of 60.
- Saving it, deleting it and using it asks you to confirm with Windows Hello.
- It is stored encrypted for your Windows user. It does not protect from a program running as your user.

## Scripts
If a skill brings scripts, it shows a warning until you mark them as reviewed. None is run.
