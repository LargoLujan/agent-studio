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
- If you allow it, now and then it checks how the request is going.

## What it needs
- The gh program of GitHub, with a session started. Agent Studio never sees your key.
- That git can sign in to GitHub with your credential manager.

## What it does not do
- It does not merge unless you ask and confirm with Windows Hello each time.
- It does not force any upload and does not delete branches on its own.
- It does not upload Git LFS files.

## Where it is
In «Repository and changes», tab **Pull requests (PR)**. Press a row to see its detail and its actions.
- A button only shows if your version of gh can do it and the request allows it.
- Merging, closing and deleting the remote branch first show a card. They ask for Windows Hello every time.
- What GitHub writes is shown as data, exactly as it arrives.
- What Agent Studio deduces carries the mark «(deduced)».

## Publishing permissions
A saved permission skips Windows Hello when publishing the branch and writing to the pull request. Merging and deleting are never saved.

## Merged with changes the judge did not see
If someone pushed changes after the review and it was merged like that, the work is not integrated.

## If there is no connection
The action stays waiting to sync, with its reason. Nothing is taken as done until GitHub confirms it.
