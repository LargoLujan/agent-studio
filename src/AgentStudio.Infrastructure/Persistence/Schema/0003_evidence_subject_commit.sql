-- Agent Studio: Phase 3 (judge integrity). Keys are TEXT unless stated; dates are ISO-8601 round-trip ('O') text.
--
-- Every build and test run is bound to the exact commit it verified: the one checked out in the worktree where the
-- verification ran. The judge's validator only lets a test run of the commit under review support an approval, so a
-- green run of an older commit can no longer approve a newer one. NULL for evidence that is not about one commit (a
-- prompt, an agent report) and for every row written before this migration.
--
-- The column needs no trigger of its own: evidence_no_update, evidence_no_delete and evidence_no_replace (0001) abort
-- any change to any column of the row, so the commit is as immutable as the rest of the evidence. The skills catalog
-- and health tables that docs/05 had earmarked for 0003 move to 0004 (Phase 4).

ALTER TABLE evidence ADD COLUMN subject_commit TEXT NULL;
