---
title: Publish and open a pull request (PR)
screens: publishing
info: Info_Hosting_PullRequests, Info_Hosting_Permissions, Info_Hosting_MergedUnjudged
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

## Where it is
In «Repository and changes», in the tab **Pull requests (PR)**. At the top you see the GitHub repository, the session and the last sync. Below, the requests you follow, one row each.

- Press a row to see its detail: the protections of the base branch, the failing checks and the saved versions before and after merging.
- A button only shows if your version of gh can do it and the request is in a state that allows it.
- Merging, closing and deleting the remote branch first show a card with what they will do. They ask for Windows Hello every time.
- What GitHub writes (the title, the name of a check) is shown as data, exactly as it arrives.
- What Agent Studio deduces carries the mark «(deduced)»: the merge mode and a possible revert.

## Publishing permissions
A saved permission skips Windows Hello when publishing the branch and writing to the pull request. You give and withdraw them in the folding section «Publishing permissions». Merging and deleting can never be saved.

## Merged with changes the judge did not see
If someone pushed changes after the review and the request was merged like that, the work does not count as integrated. Its card on the board stays in «Done» and says so.

## If there is no connection
The action stays waiting to sync, with its reason and the time of the next try. Nothing is taken as done until GitHub confirms it.
