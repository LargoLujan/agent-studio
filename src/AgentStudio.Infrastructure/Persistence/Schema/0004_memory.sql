-- Agent Studio: Phase 4 (memory, docs/14 section 6). Keys are TEXT unless stated; dates are ISO-8601 round-trip ('O') text.
--
-- memory_entries becomes append-only: its rows are immutable content, and everything that changes (anchoring, review,
-- supersession, retraction, quarantine) is derived from the decision log (ADR-0010 pattern, ADR-0022). Each row is
-- announced in the log by MemoryRecorded with the canonical SHA-256 of its kind, topic and content before it is stored,
-- and recall only returns rows announced with the same hash.
--
-- Only memory: there is no usage table (usage is a projection of AgentRunFinished, D-01, ADR-0019) and no skills tables
-- (the catalog belongs to the user's profile, D-27). agent_runs and usage_records of 0001 stay unused.
--
-- superseded_by (0001) stays NULL forever: a supersession is the new entry (supersedes_entry_id) and its MemoryRecorded.
-- The table was never written before this migration, so there are no rows without an origin; the columns are NULL-able
-- only because SQLite cannot add a NOT NULL column without a default.

ALTER TABLE memory_entries ADD COLUMN origin TEXT NULL CHECK (origin IN ('System', 'User', 'Agent'));
ALTER TABLE memory_entries ADD COLUMN content_sha256 TEXT NULL;
ALTER TABLE memory_entries ADD COLUMN supersedes_entry_id TEXT NULL REFERENCES memory_entries(id);
CREATE INDEX memory_by_project ON memory_entries(project_id, kind, created_at);

-- Updates and deletes are aborted before they happen, so the full-text triggers for them never fire again; the one of
-- insert (memory_entries_ai, 0001) keeps the index in step with the rows.
DROP TRIGGER memory_entries_au;
DROP TRIGGER memory_entries_ad;
CREATE TRIGGER memory_entries_no_update BEFORE UPDATE ON memory_entries BEGIN
    SELECT RAISE(ABORT, 'memory_entries is append-only');
END;
CREATE TRIGGER memory_entries_no_delete BEFORE DELETE ON memory_entries BEGIN
    SELECT RAISE(ABORT, 'memory_entries is append-only');
END;

-- INSERT OR REPLACE deletes the conflicting row without firing DELETE triggers unless recursive_triggers is on, so a
-- duplicate primary key is rejected explicitly before the insert (same pattern as 0001 and 0002).
CREATE TRIGGER memory_entries_no_replace BEFORE INSERT ON memory_entries
WHEN EXISTS (SELECT 1 FROM memory_entries WHERE id = NEW.id) BEGIN
    SELECT RAISE(ABORT, 'memory_entries is append-only');
END;
