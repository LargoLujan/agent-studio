-- Agent Studio: Phase 3 (orchestration). Keys are TEXT unless stated; dates are ISO-8601 round-trip ('O') text.
--
-- The state of a run is NOT stored here: it is derived by replaying decision_log (ADR-0010). These tables hold what
-- never changes once written: the plans, the immutable header of each run and the history of skill activations. The
-- tables tasks, task_dependencies, task_status_history, agent_runs and usage_records of 0001 are not used in Phase 3.

CREATE TABLE plans (
    content_hash     TEXT PRIMARY KEY,
    project_id       TEXT NOT NULL REFERENCES projects(id),
    source           TEXT NOT NULL CHECK (source IN ('OrchestratorAgent', 'User')),
    author_agent_id  TEXT NULL,
    schema_version   INTEGER NOT NULL,
    created_at       TEXT NOT NULL,
    payload_json     TEXT NOT NULL
);

CREATE TABLE orchestration_runs (
    id                            TEXT PRIMARY KEY,
    project_id                    TEXT NOT NULL REFERENCES projects(id),
    created_at                    TEXT NOT NULL,
    plan_hash                     TEXT NOT NULL REFERENCES plans(content_hash),
    requirement_set_hash          TEXT NOT NULL REFERENCES requirement_sets(content_hash),
    original_request_evidence_id  TEXT NOT NULL REFERENCES evidence(id),
    base_commit                   TEXT NOT NULL,
    base_ref                      TEXT NULL,
    verification_profile_json     TEXT NOT NULL,
    roster_json                   TEXT NOT NULL,
    options_json                  TEXT NOT NULL,
    follows_run_id                TEXT NULL REFERENCES orchestration_runs(id)
);

-- Activation decisions are a history: for the same skill and scope the last row wins. Global activations are not
-- project data and live in a file in the user profile, so only Project and Agent scopes exist here.
CREATE TABLE skill_activations (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id  TEXT NOT NULL REFERENCES projects(id),
    skill_id    TEXT NOT NULL,
    scope       TEXT NOT NULL CHECK (scope IN ('Project', 'Agent')),
    scope_id    TEXT NOT NULL,
    enabled     INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    decided_by  TEXT NOT NULL,
    decided_at  TEXT NOT NULL
);

-- Where each piece of evidence and each verdict belongs inside a run. run_id is not a foreign key: the evidence of the
-- original request is captured before the run row exists.
ALTER TABLE evidence ADD COLUMN run_id TEXT NULL;
ALTER TABLE evidence ADD COLUMN task_id TEXT NULL;
ALTER TABLE evidence ADD COLUMN attempt INTEGER NULL;
ALTER TABLE evidence ADD COLUMN media_type TEXT NULL;
ALTER TABLE evidence ADD COLUMN size_bytes INTEGER NULL;
CREATE INDEX evidence_by_run ON evidence(project_id, run_id);

ALTER TABLE verdicts ADD COLUMN run_id TEXT NULL;
ALTER TABLE verdicts ADD COLUMN task_id TEXT NULL;
ALTER TABLE verdicts ADD COLUMN scope TEXT NULL;
ALTER TABLE verdicts ADD COLUMN valid INTEGER NULL CHECK (valid IN (0, 1));
ALTER TABLE verdicts ADD COLUMN validation_json TEXT NULL;
ALTER TABLE verdicts ADD COLUMN supersedes_verdict_id TEXT NULL REFERENCES verdicts(id);
CREATE INDEX verdicts_by_run ON verdicts(project_id, run_id);

-- Append-only tables, same pattern as 0001.
CREATE TRIGGER plans_no_update BEFORE UPDATE ON plans BEGIN
    SELECT RAISE(ABORT, 'plans is append-only');
END;
CREATE TRIGGER plans_no_delete BEFORE DELETE ON plans BEGIN
    SELECT RAISE(ABORT, 'plans is append-only');
END;

CREATE TRIGGER orchestration_runs_no_update BEFORE UPDATE ON orchestration_runs BEGIN
    SELECT RAISE(ABORT, 'orchestration_runs is append-only');
END;
CREATE TRIGGER orchestration_runs_no_delete BEFORE DELETE ON orchestration_runs BEGIN
    SELECT RAISE(ABORT, 'orchestration_runs is append-only');
END;

CREATE TRIGGER skill_activations_no_update BEFORE UPDATE ON skill_activations BEGIN
    SELECT RAISE(ABORT, 'skill_activations is append-only');
END;
CREATE TRIGGER skill_activations_no_delete BEFORE DELETE ON skill_activations BEGIN
    SELECT RAISE(ABORT, 'skill_activations is append-only');
END;

-- INSERT OR REPLACE deletes the conflicting row without firing DELETE triggers unless recursive_triggers is on, so a
-- duplicate primary key is rejected explicitly before the insert.
CREATE TRIGGER plans_no_replace BEFORE INSERT ON plans
WHEN EXISTS (SELECT 1 FROM plans WHERE content_hash = NEW.content_hash) BEGIN
    SELECT RAISE(ABORT, 'plans is append-only');
END;
CREATE TRIGGER orchestration_runs_no_replace BEFORE INSERT ON orchestration_runs
WHEN EXISTS (SELECT 1 FROM orchestration_runs WHERE id = NEW.id) BEGIN
    SELECT RAISE(ABORT, 'orchestration_runs is append-only');
END;
CREATE TRIGGER skill_activations_no_replace BEFORE INSERT ON skill_activations
WHEN NEW.id IS NOT NULL AND EXISTS (SELECT 1 FROM skill_activations WHERE id = NEW.id) BEGIN
    SELECT RAISE(ABORT, 'skill_activations is append-only');
END;
