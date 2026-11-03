-- Agent Studio: Phase 5 (debates and audits, docs/15 section 6). Keys are TEXT unless stated; dates are ISO-8601 'O' text.
--
-- Like orchestration_runs, these rows hold what never changes once written: the header of a debate and of a whole-project
-- audit. Their state is derived by replaying decision_log (ADR-0010, ADR-0026), and DebateOpened / ProjectAuditStarted
-- anchor the canonical hash of each header (header_hash). The debate tables of 0001 (debates, debate_participants,
-- debate_contributions, debate_contribution_evidence) and verdicts.debate_id stay unused: they kept mutable state and the
-- text of the agents outside the evidence store. They are not dropped here.
--
-- Order of writing: the question (or the request of the audit) is captured as evidence first, with the id of the debate
-- or the audit as its run_id; then the row of the header; then DebateOpened (or ProjectAuditStarted) with its hash.

CREATE TABLE debate_sessions (
    id                     TEXT PRIMARY KEY,                       -- deb-yyyyMMdd-xxxxxx
    project_id             TEXT NOT NULL REFERENCES projects(id),
    created_at             TEXT NOT NULL,
    mode                   TEXT NOT NULL CHECK (mode IN ('Free', 'Adversarial', 'ArchitectureReview', 'ImplementationAudit', 'OrchestratorReview', 'Tribunal')),
    requirement_set_hash   TEXT NOT NULL REFERENCES requirement_sets(content_hash),
    question_evidence_id   TEXT NOT NULL REFERENCES evidence(id),
    subject_kind           TEXT NOT NULL CHECK (subject_kind IN ('None', 'Commit', 'Run', 'Verdict')),
    subject_ref            TEXT NULL,                              -- the commit, run id or verdict id
    snapshot_commit        TEXT NULL,
    challenges_verdict_id  TEXT NULL REFERENCES verdicts(id),
    header_json            TEXT NOT NULL,                          -- participants and judges with their definitions, judging, waivers, limits
    header_hash            TEXT NOT NULL
);
CREATE INDEX debate_sessions_by_project ON debate_sessions(project_id, created_at);

CREATE TABLE project_audits (
    id                     TEXT PRIMARY KEY,                       -- aud-yyyyMMdd-xxxxxx
    project_id             TEXT NOT NULL REFERENCES projects(id),
    created_at             TEXT NOT NULL,
    head_commit            TEXT NULL,
    requirement_set_hash   TEXT NOT NULL REFERENCES requirement_sets(content_hash),
    request_evidence_id    TEXT NOT NULL REFERENCES evidence(id),
    header_json            TEXT NOT NULL,                          -- verification profile, judge, producers and declared authors
    header_hash            TEXT NOT NULL
);
CREATE INDEX project_audits_by_project ON project_audits(project_id, created_at);

-- Append-only, same pattern as 0001, 0002 and 0004.
CREATE TRIGGER debate_sessions_no_update BEFORE UPDATE ON debate_sessions BEGIN
    SELECT RAISE(ABORT, 'debate_sessions is append-only');
END;
CREATE TRIGGER debate_sessions_no_delete BEFORE DELETE ON debate_sessions BEGIN
    SELECT RAISE(ABORT, 'debate_sessions is append-only');
END;
CREATE TRIGGER project_audits_no_update BEFORE UPDATE ON project_audits BEGIN
    SELECT RAISE(ABORT, 'project_audits is append-only');
END;
CREATE TRIGGER project_audits_no_delete BEFORE DELETE ON project_audits BEGIN
    SELECT RAISE(ABORT, 'project_audits is append-only');
END;

-- INSERT OR REPLACE deletes the conflicting row without firing DELETE triggers unless recursive_triggers is on, so a
-- duplicate primary key is rejected explicitly before the insert (same pattern as 0001, 0002 and 0004).
CREATE TRIGGER debate_sessions_no_replace BEFORE INSERT ON debate_sessions
WHEN EXISTS (SELECT 1 FROM debate_sessions WHERE id = NEW.id) BEGIN
    SELECT RAISE(ABORT, 'debate_sessions is append-only');
END;
CREATE TRIGGER project_audits_no_replace BEFORE INSERT ON project_audits
WHEN EXISTS (SELECT 1 FROM project_audits WHERE id = NEW.id) BEGIN
    SELECT RAISE(ABORT, 'project_audits is append-only');
END;
