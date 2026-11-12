-- 0007: INSERT OR REPLACE through the rowid (P45, ADR-0010 addendum of 2026-11-12).
--
-- A connection without recursive_triggers (another program's: the app turns them on) can run INSERT OR REPLACE with the
-- rowid of an existing row. REPLACE deletes the row it conflicts with by rowid without firing its DELETE trigger, and the
-- key the insert carries (an id, a sequence) is one no row has, so the *_no_replace trigger of 0001 to 0006 let it
-- through: the row changed with the triggers intact and the same schema version. Each trigger now also rejects an insert
-- whose rowid is already taken. Triggers are recreated only: no table and no row is touched.
--
-- debate_contributions and skill_activations keep their triggers: their id is an INTEGER PRIMARY KEY, that is the rowid
-- itself, and the condition on NEW.id already covers it (a NULL id allocates a fresh rowid and conflicts with nothing).

DROP TRIGGER decision_log_no_replace;
CREATE TRIGGER decision_log_no_replace BEFORE INSERT ON decision_log
WHEN EXISTS (SELECT 1 FROM decision_log WHERE project_id = NEW.project_id AND sequence = NEW.sequence) OR EXISTS (SELECT 1 FROM decision_log WHERE rowid = NEW.rowid) BEGIN
    SELECT RAISE(ABORT, 'decision_log is append-only');
END;
DROP TRIGGER evidence_no_replace;
CREATE TRIGGER evidence_no_replace BEFORE INSERT ON evidence
WHEN EXISTS (SELECT 1 FROM evidence WHERE id = NEW.id) OR EXISTS (SELECT 1 FROM evidence WHERE rowid = NEW.rowid) BEGIN
    SELECT RAISE(ABORT, 'evidence is append-only');
END;
DROP TRIGGER verdicts_no_replace;
CREATE TRIGGER verdicts_no_replace BEFORE INSERT ON verdicts
WHEN EXISTS (SELECT 1 FROM verdicts WHERE id = NEW.id) OR EXISTS (SELECT 1 FROM verdicts WHERE rowid = NEW.rowid) BEGIN
    SELECT RAISE(ABORT, 'verdicts is append-only');
END;
DROP TRIGGER requirement_sets_no_replace;
CREATE TRIGGER requirement_sets_no_replace BEFORE INSERT ON requirement_sets
WHEN EXISTS (SELECT 1 FROM requirement_sets WHERE content_hash = NEW.content_hash) OR EXISTS (SELECT 1 FROM requirement_sets WHERE rowid = NEW.rowid) BEGIN
    SELECT RAISE(ABORT, 'requirement_sets is append-only');
END;
DROP TRIGGER plans_no_replace;
CREATE TRIGGER plans_no_replace BEFORE INSERT ON plans
WHEN EXISTS (SELECT 1 FROM plans WHERE content_hash = NEW.content_hash) OR EXISTS (SELECT 1 FROM plans WHERE rowid = NEW.rowid) BEGIN
    SELECT RAISE(ABORT, 'plans is append-only');
END;
DROP TRIGGER orchestration_runs_no_replace;
CREATE TRIGGER orchestration_runs_no_replace BEFORE INSERT ON orchestration_runs
WHEN EXISTS (SELECT 1 FROM orchestration_runs WHERE id = NEW.id) OR EXISTS (SELECT 1 FROM orchestration_runs WHERE rowid = NEW.rowid) BEGIN
    SELECT RAISE(ABORT, 'orchestration_runs is append-only');
END;
DROP TRIGGER memory_entries_no_replace;
CREATE TRIGGER memory_entries_no_replace BEFORE INSERT ON memory_entries
WHEN EXISTS (SELECT 1 FROM memory_entries WHERE id = NEW.id) OR EXISTS (SELECT 1 FROM memory_entries WHERE rowid = NEW.rowid) BEGIN
    SELECT RAISE(ABORT, 'memory_entries is append-only');
END;
DROP TRIGGER debate_sessions_no_replace;
CREATE TRIGGER debate_sessions_no_replace BEFORE INSERT ON debate_sessions
WHEN EXISTS (SELECT 1 FROM debate_sessions WHERE id = NEW.id) OR EXISTS (SELECT 1 FROM debate_sessions WHERE rowid = NEW.rowid) BEGIN
    SELECT RAISE(ABORT, 'debate_sessions is append-only');
END;
DROP TRIGGER project_audits_no_replace;
CREATE TRIGGER project_audits_no_replace BEFORE INSERT ON project_audits
WHEN EXISTS (SELECT 1 FROM project_audits WHERE id = NEW.id) OR EXISTS (SELECT 1 FROM project_audits WHERE rowid = NEW.rowid) BEGIN
    SELECT RAISE(ABORT, 'project_audits is append-only');
END;
DROP TRIGGER milestones_no_replace;
CREATE TRIGGER milestones_no_replace BEFORE INSERT ON milestones
WHEN EXISTS (SELECT 1 FROM milestones WHERE id = NEW.id) OR EXISTS (SELECT 1 FROM milestones WHERE rowid = NEW.rowid) BEGIN
    SELECT RAISE(ABORT, 'milestones is append-only');
END;
DROP TRIGGER backlog_items_no_replace;
CREATE TRIGGER backlog_items_no_replace BEFORE INSERT ON backlog_items
WHEN EXISTS (SELECT 1 FROM backlog_items WHERE item_id = NEW.item_id AND version = NEW.version) OR EXISTS (SELECT 1 FROM backlog_items WHERE rowid = NEW.rowid) BEGIN
    SELECT RAISE(ABORT, 'backlog_items is append-only');
END;
