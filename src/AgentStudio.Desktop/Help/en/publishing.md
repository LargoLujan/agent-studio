---
title: Publish and open a pull request (PR)
screens: publishing
info: Info_Hosting_PullRequests, Info_Hosting_Permissions, Info_Hosting_MergedUnjudged
---
Publishing uploads to GitHub the work of a run that an independent reviewer (judge) approved. Then it opens a pull request (PR) so someone can review it.

## What it does
- It uploads the branch of the run to GitHub. Your branch does not change.
- It opens the request with a title and a text that you can change first.
- It publishes the result of the review as a status and a comment. Never as an approval from you.

## What it needs
The gh program of GitHub, with a session started, and git able to sign in with your credential manager.

## What it does not do
- It does not merge unless you ask and confirm with Windows Hello each time.

## Where it is
In «Repository and changes», tab **Pull requests (PR)**. Press a row to see its detail and its actions.
- A button only shows if your version of gh can do it and the request allows it.
- Merging, closing and deleting the remote branch first show a card. They ask for Windows Hello every time.
- Only the version the reviewer judged is merged.
- **Update the PR…** publishes the run again with the title and text you change. It only works for an open request that GitHub finds.
- What Agent Studio deduces carries the mark «(deduced)».

## The box «Sync with GitHub»
It is in the header and only works with a project. If you tick it, Agent Studio asks GitHub every few minutes about your requests. Only the repository name and its numbers go out. Only your click changes it.

## Publishing permissions
A saved permission skips Windows Hello when publishing the branch and writing to the pull request. Merging and deleting are never saved.

## If there is no connection
The action stays waiting to sync, with its reason.
