-- Agent Studio: initial schema. Keys are TEXT unless stated; dates are ISO-8601 round-trip ('O') text.

CREATE TABLE projects (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    root_path   TEXT NOT NULL,
    created_at  TEXT NOT NULL
);

CREATE TABLE agents (
    id               TEXT PRIMARY KEY,
    project_id       TEXT NOT NULL REFERENCES projects(id),
    name             TEXT NOT NULL,
    role             TEXT NOT NULL,
    provider_id      TEXT NOT NULL,
    vendor_id        TEXT NOT NULL,
    model            TEXT NULL,
    definition_json  TEXT NOT NULL,
    created_at       TEXT NOT NULL
);

CREATE TABLE requirement_sets (
    content_hash  TEXT PRIMARY KEY,
    project_id    TEXT NOT NULL REFERENCES projects(id),
    frozen_at     TEXT NOT NULL,
    payload_json  TEXT NOT NULL
);

CREATE TABLE tasks (
    id                 TEXT PRIMARY KEY,
    project_id         TEXT NOT NULL REFERENCES projects(id),
    title              TEXT NOT NULL,
    kind               TEXT NOT NULL,
    status             TEXT NOT NULL,
    assigned_agent_id  TEXT NULL REFERENCES agents(id),
    max_retries        INTEGER NOT NULL,
    retry_count        INTEGER NOT NULL,
    requires_evidence  INTEGER NOT NULL,
    created_at         TEXT NOT NULL,
    last_activity_at   TEXT NOT NULL
);

CREATE TABLE task_dependencies (
    task_id         TEXT NOT NULL REFERENCES tasks(id),
    prerequisite_id TEXT NOT NULL REFERENCES tasks(id),
    PRIMARY KEY (task_id, prerequisite_id),
    CHECK (task_id <> prerequisite_id)
);

CREATE TABLE task_status_history (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    task_id      TEXT NOT NULL REFERENCES tasks(id),
    from_status  TEXT NOT NULL,
    to_status    TEXT NOT NULL,
    at           TEXT NOT NULL,
    reason       TEXT NOT NULL
);

CREATE TABLE decision_log (
    project_id         TEXT NOT NULL REFERENCES projects(id),
    sequence           INTEGER NOT NULL,
    recorded_at        TEXT NOT NULL,
    actor_kind         TEXT NOT NULL,
    actor_id           TEXT NOT NULL,
    kind               TEXT NOT NULL,
    summary            TEXT NOT NULL,
    payload_json       TEXT NOT NULL,
    corrects_sequence  INTEGER NULL,
    previous_hash      TEXT NOT NULL,
    hash               TEXT NOT NULL,
    PRIMARY KEY (project_id, sequence)
);

CREATE TABLE evidence (
    id              TEXT PRIMARY KEY,
    project_id      TEXT NOT NULL REFERENCES projects(id),
    kind            TEXT NOT NULL,
    origin          TEXT NOT NULL,
    title           TEXT NOT NULL,
    produced_by     TEXT NOT NULL,
    captured_at     TEXT NOT NULL,
    content_sha256  TEXT NOT NULL,
    storage_uri     TEXT NULL,
    test_passed     INTEGER NULL,
    test_failed     INTEGER NULL,
    test_skipped    INTEGER NULL,
    test_exit_code  INTEGER NULL
);

CREATE TABLE evidence_requirements (
    evidence_id     TEXT NOT NULL REFERENCES evidence(id),
    requirement_id  TEXT NOT NULL,
    PRIMARY KEY (evidence_id, requirement_id)
);

CREATE TABLE debates (
    id                    TEXT PRIMARY KEY,
    project_id            TEXT NOT NULL REFERENCES projects(id),
    mode                  TEXT NOT NULL,
    question              TEXT NOT NULL,
    phase                 TEXT NOT NULL,
    outcome               TEXT NOT NULL,
    requirement_set_hash  TEXT NOT NULL REFERENCES requirement_sets(content_hash),
    limits_json           TEXT NOT NULL,
    judge_agent_id        TEXT NOT NULL,
    started_at            TEXT NOT NULL,
    closed_at             TEXT NULL
);

CREATE TABLE debate_participants (
    debate_id    TEXT NOT NULL REFERENCES debates(id),
    agent_id     TEXT NOT NULL,
    role         TEXT NOT NULL,
    provider_id  TEXT NOT NULL,
    vendor_id    TEXT NOT NULL,
    model        TEXT NULL,
    PRIMARY KEY (debate_id, agent_id)
);

CREATE TABLE debate_contributions (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    debate_id        TEXT NOT NULL REFERENCES debates(id),
    round            INTEGER NOT NULL,
    author_agent_id  TEXT NOT NULL,
    kind             TEXT NOT NULL,
    content          TEXT NOT NULL,
    replies_to       INTEGER NULL,
    at               TEXT NOT NULL
);

CREATE TABLE debate_contribution_evidence (
    contribution_id  INTEGER NOT NULL REFERENCES debate_contributions(id),
    evidence_id      TEXT NOT NULL REFERENCES evidence(id),
    PRIMARY KEY (contribution_id, evidence_id)
);

CREATE TABLE verdicts (
    id                        TEXT PRIMARY KEY,
    project_id                TEXT NOT NULL REFERENCES projects(id),
    debate_id                 TEXT NULL REFERENCES debates(id),
    decision                  TEXT NOT NULL,
    summary                   TEXT NOT NULL,
    confidence_level          TEXT NOT NULL,
    confidence_justification  TEXT NOT NULL,
    requires_human            INTEGER NOT NULL,
    judge_agent_id            TEXT NOT NULL,
    judge_provider_id         TEXT NOT NULL,
    judge_vendor_id           TEXT NOT NULL,
    judge_model               TEXT NULL,
    requirement_set_hash      TEXT NOT NULL,
    decision_log_head_hash    TEXT NOT NULL,
    payload_json              TEXT NOT NULL,
    issued_at                 TEXT NOT NULL
);

CREATE TABLE agent_runs (
    id           TEXT PRIMARY KEY,
    project_id   TEXT NOT NULL REFERENCES projects(id),
    agent_id     TEXT NOT NULL,
    task_id      TEXT NULL,
    provider_id  TEXT NOT NULL,
    model        TEXT NULL,
    started_at   TEXT NOT NULL,
    ended_at     TEXT NULL,
    status       TEXT NOT NULL,
    exit_code    INTEGER NULL,
    session_id   TEXT NULL,
    error        TEXT NULL
);

CREATE TABLE usage_records (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id       TEXT NOT NULL REFERENCES agent_runs(id),
    metric       TEXT NOT NULL,
    value        REAL NULL,
    provenance   TEXT NOT NULL CHECK (provenance IN ('Exact', 'Estimated', 'Unavailable')),
    source       TEXT NOT NULL,
    recorded_at  TEXT NOT NULL
);

CREATE TABLE memory_entries (
    id             TEXT PRIMARY KEY,
    project_id     TEXT NOT NULL REFERENCES projects(id),
    kind           TEXT NOT NULL,
    topic          TEXT NOT NULL,
    content        TEXT NOT NULL,
    source         TEXT NOT NULL,
    created_at     TEXT NOT NULL,
    superseded_by  TEXT NULL
);

-- Full-text index over memory_entries (external content: rows live only in memory_entries).
CREATE VIRTUAL TABLE memory_entries_fts USING fts5(
    topic,
    content,
    content = 'memory_entries',
    content_rowid = 'rowid'
);

CREATE TRIGGER memory_entries_ai AFTER INSERT ON memory_entries BEGIN
    INSERT INTO memory_entries_fts(rowid, topic, content) VALUES (new.rowid, new.topic, new.content);
END;

CREATE TRIGGER memory_entries_ad AFTER DELETE ON memory_entries BEGIN
    INSERT INTO memory_entries_fts(memory_entries_fts, rowid, topic, content) VALUES ('delete', old.rowid, old.topic, old.content);
END;

CREATE TRIGGER memory_entries_au AFTER UPDATE ON memory_entries BEGIN
    INSERT INTO memory_entries_fts(memory_entries_fts, rowid, topic, content) VALUES ('delete', old.rowid, old.topic, old.content);
    INSERT INTO memory_entries_fts(rowid, topic, content) VALUES (new.rowid, new.topic, new.content);
END;

-- Append-only tables: history is never rewritten.
CREATE TRIGGER decision_log_no_update BEFORE UPDATE ON decision_log BEGIN
    SELECT RAISE(ABORT, 'decision_log is append-only');
END;
CREATE TRIGGER decision_log_no_delete BEFORE DELETE ON decision_log BEGIN
    SELECT RAISE(ABORT, 'decision_log is append-only');
END;

CREATE TRIGGER evidence_no_update BEFORE UPDATE ON evidence BEGIN
    SELECT RAISE(ABORT, 'evidence is append-only');
END;
CREATE TRIGGER evidence_no_delete BEFORE DELETE ON evidence BEGIN
    SELECT RAISE(ABORT, 'evidence is append-only');
END;

CREATE TRIGGER verdicts_no_update BEFORE UPDATE ON verdicts BEGIN
    SELECT RAISE(ABORT, 'verdicts is append-only');
END;
CREATE TRIGGER verdicts_no_delete BEFORE DELETE ON verdicts BEGIN
    SELECT RAISE(ABORT, 'verdicts is append-only');
END;

CREATE TRIGGER debate_contributions_no_update BEFORE UPDATE ON debate_contributions BEGIN
    SELECT RAISE(ABORT, 'debate_contributions is append-only');
END;
CREATE TRIGGER debate_contributions_no_delete BEFORE DELETE ON debate_contributions BEGIN
    SELECT RAISE(ABORT, 'debate_contributions is append-only');
END;

CREATE TRIGGER requirement_sets_no_update BEFORE UPDATE ON requirement_sets BEGIN
    SELECT RAISE(ABORT, 'requirement_sets is append-only');
END;
CREATE TRIGGER requirement_sets_no_delete BEFORE DELETE ON requirement_sets BEGIN
    SELECT RAISE(ABORT, 'requirement_sets is append-only');
END;

-- INSERT OR REPLACE deletes the conflicting row without firing DELETE triggers unless recursive_triggers is on,
-- so a duplicate primary key is rejected explicitly before the insert.
CREATE TRIGGER decision_log_no_replace BEFORE INSERT ON decision_log
WHEN EXISTS (SELECT 1 FROM decision_log WHERE project_id = NEW.project_id AND sequence = NEW.sequence) BEGIN
    SELECT RAISE(ABORT, 'decision_log is append-only');
END;
CREATE TRIGGER evidence_no_replace BEFORE INSERT ON evidence
WHEN EXISTS (SELECT 1 FROM evidence WHERE id = NEW.id) BEGIN
    SELECT RAISE(ABORT, 'evidence is append-only');
END;
CREATE TRIGGER verdicts_no_replace BEFORE INSERT ON verdicts
WHEN EXISTS (SELECT 1 FROM verdicts WHERE id = NEW.id) BEGIN
    SELECT RAISE(ABORT, 'verdicts is append-only');
END;
CREATE TRIGGER debate_contributions_no_replace BEFORE INSERT ON debate_contributions
WHEN NEW.id IS NOT NULL AND EXISTS (SELECT 1 FROM debate_contributions WHERE id = NEW.id) BEGIN
    SELECT RAISE(ABORT, 'debate_contributions is append-only');
END;
CREATE TRIGGER requirement_sets_no_replace BEFORE INSERT ON requirement_sets
WHEN EXISTS (SELECT 1 FROM requirement_sets WHERE content_hash = NEW.content_hash) BEGIN
    SELECT RAISE(ABORT, 'requirement_sets is append-only');
END;
