---
title: Publish and open a pull request (PR)
screens: publishing
---
Publishing uploads to GitHub the work of a run that an independent reviewer (judge) approved. Then it opens a pull request (PR) so someone can review it.

## What it does
- It uploads the branch of the run to GitHub. Your branch does not change.
- It opens the pull request with a title and a text that you can change first.
- It publishes the result of the review as a status and a comment. It never publishes it as an approval from you.
- If you allow it, now and then it checks how the request is going. That way it knows whether it was merged, whether it was closed or whether there is a deployment.

## What it needs
- The gh program of GitHub, with a session started. Agent Studio uses that session and never sees your key.
- That git can sign in to GitHub with your credential manager.
- That the repository does not define anything that changes where it is sent or how you sign in.

## What it does not do
- It does not merge unless you ask and confirm with Windows Hello each time.
- It does not force any upload and it does not delete branches on its own.
- It does not upload Git LFS files. You upload those with your own git program.

## If there is no connection
The action stays waiting to sync, with its reason and the time of the next try. Nothing is taken as done until GitHub confirms it.
