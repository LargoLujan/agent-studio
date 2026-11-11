-- Agent Studio: Phase 9 (work and traceability, docs/16 section 4.6, D-17 and D-18). Keys are TEXT unless stated; dates are ISO-8601 'O' text.
--
-- Two append-only tables, like debate_sessions and memory_entries: the immutable header of a milestone and the versions of the
-- items of the backlog. Their state is derived from decision_log (ADR-0010, ADR-0038): MilestoneOpened anchors the hash of each
-- header (header_hash), and BacklogItemRecorded announces each version with its content_sha256 before its row is stored.
-- There is no table for the links of the trace (they are entries of the log, ADR-0039), nor for the board (a projection).
--
-- Order of writing for a milestone: the frozen requirements (requirement_sets), then the row, then MilestoneOpened with its hash.

CREATE TABLE milestones (
    id                     TEXT PRIMARY KEY,                       -- mil-yyyyMMdd-xxxxxx
    project_id             TEXT NOT NULL REFERENCES projects(id),
    created_at             TEXT NOT NULL,
    name                   TEXT NOT NULL CHECK (length(trim(name)) > 0),
    objective              TEXT NOT NULL CHECK (length(trim(objective)) > 0),
    requirement_set_hash   TEXT NOT NULL REFERENCES requirement_sets(content_hash),   -- the first set; the revisions are entries of the log
    budget_json            TEXT NULL,                              -- {"maxTokens":..,"maxAgentRuns":..,"period":"Total"}, NULL when there is none
    header_hash            TEXT NOT NULL
);
CREATE INDEX milestones_by_project ON milestones(project_id, created_at);

CREATE TABLE backlog_items (
    item_id                TEXT NOT NULL,                          -- bk-yyyyMMdd-xxxxxx, the same in every version
    version                INTEGER NOT NULL CHECK (version >= 1),
    project_id             TEXT NOT NULL REFERENCES projects(id),
    created_at             TEXT NOT NULL,
    statement              TEXT NOT NULL CHECK (length(trim(statement)) > 0),
    priority               TEXT NOT NULL CHECK (priority IN ('Mandatory', 'Desirable')),   -- RequirementPriority
    criteria_json          TEXT NOT NULL,                          -- [{"id":..,"description":..,"method":..}], possibly []
    content_sha256         TEXT NOT NULL,
    PRIMARY KEY (item_id, version)
);
CREATE INDEX backlog_items_by_project ON backlog_items(project_id, created_at);

-- Append-only, same pattern as 0001, 0002, 0004 and 0005.
CREATE TRIGGER milestones_no_update BEFORE UPDATE ON milestones BEGIN
    SELECT RAISE(ABORT, 'milestones is append-only');
END;
CREATE TRIGGER milestones_no_delete BEFORE DELETE ON milestones BEGIN
    SELECT RAISE(ABORT, 'milestones is append-only');
END;
CREATE TRIGGER backlog_items_no_update BEFORE UPDATE ON backlog_items BEGIN
    SELECT RAISE(ABORT, 'backlog_items is append-only');
END;
CREATE TRIGGER backlog_items_no_delete BEFORE DELETE ON backlog_items BEGIN
    SELECT RAISE(ABORT, 'backlog_items is append-only');
END;

-- INSERT OR REPLACE deletes the conflicting row without firing DELETE triggers unless recursive_triggers is on, so a
-- duplicate key is rejected explicitly before the insert.
CREATE TRIGGER milestones_no_replace BEFORE INSERT ON milestones
WHEN EXISTS (SELECT 1 FROM milestones WHERE id = NEW.id) BEGIN
    SELECT RAISE(ABORT, 'milestones is append-only');
END;
CREATE TRIGGER backlog_items_no_replace BEFORE INSERT ON backlog_items
WHEN EXISTS (SELECT 1 FROM backlog_items WHERE item_id = NEW.item_id AND version = NEW.version) BEGIN
    SELECT RAISE(ABORT, 'backlog_items is append-only');
END;
