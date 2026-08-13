CREATE TEMP TABLE snapshot_integrity_check (
    invalid_count INTEGER NOT NULL CHECK(invalid_count = 0)
);

INSERT INTO snapshot_integrity_check(invalid_count)
SELECT COUNT(*)
FROM fsrs_parameter_snapshot
WHERE json_valid(snapshot_json) IS NOT 1
   OR json_type(snapshot_json, '$.Id') IS NOT 'text'
   OR lower(json_extract(snapshot_json, '$.Id')) IS NOT lower(snapshot_id)
   OR json_type(snapshot_json, '$.Status') IS NOT 'text'
   OR json_extract(snapshot_json, '$.Status') IS NOT status
   OR json_type(snapshot_json, '$.CreatedAt') IS NOT 'text'
   OR json_extract(snapshot_json, '$.CreatedAt') IS NOT created_at_utc;

DROP TABLE snapshot_integrity_check;

CREATE TRIGGER IF NOT EXISTS fsrs_parameter_snapshot_insert_consistency
BEFORE INSERT ON fsrs_parameter_snapshot
WHEN json_valid(NEW.snapshot_json) IS NOT 1
  OR json_type(NEW.snapshot_json, '$.Id') IS NOT 'text'
  OR lower(json_extract(NEW.snapshot_json, '$.Id')) IS NOT lower(NEW.snapshot_id)
  OR json_type(NEW.snapshot_json, '$.Status') IS NOT 'text'
  OR json_extract(NEW.snapshot_json, '$.Status') IS NOT NEW.status
  OR json_type(NEW.snapshot_json, '$.CreatedAt') IS NOT 'text'
  OR json_extract(NEW.snapshot_json, '$.CreatedAt') IS NOT NEW.created_at_utc
BEGIN
    SELECT RAISE(ABORT, 'fsrs_parameter_snapshot row and JSON identity must agree');
END;

CREATE TRIGGER IF NOT EXISTS fsrs_parameter_snapshot_insert_identity
BEFORE INSERT ON fsrs_parameter_snapshot
WHEN EXISTS (SELECT 1 FROM fsrs_parameter_snapshot WHERE snapshot_id=NEW.snapshot_id)
BEGIN
    SELECT RAISE(ABORT, 'fsrs_parameter_snapshot identity is immutable');
END;

CREATE TRIGGER IF NOT EXISTS fsrs_parameter_snapshot_update_guard
BEFORE UPDATE ON fsrs_parameter_snapshot
WHEN OLD.status IS NOT 'PendingPreview'
  OR NEW.status NOT IN ('ReadyForActivation','PreviewFailed')
  OR NEW.snapshot_id IS NOT OLD.snapshot_id
  OR NEW.created_at_utc IS NOT OLD.created_at_utc
  OR json_valid(NEW.snapshot_json) IS NOT 1
  OR json_remove(OLD.snapshot_json, '$.Status') IS NOT json_remove(NEW.snapshot_json, '$.Status')
  OR json_type(NEW.snapshot_json, '$.Id') IS NOT 'text'
  OR lower(json_extract(NEW.snapshot_json, '$.Id')) IS NOT lower(NEW.snapshot_id)
  OR json_type(NEW.snapshot_json, '$.Status') IS NOT 'text'
  OR json_extract(NEW.snapshot_json, '$.Status') IS NOT NEW.status
  OR json_type(NEW.snapshot_json, '$.CreatedAt') IS NOT 'text'
  OR json_extract(NEW.snapshot_json, '$.CreatedAt') IS NOT NEW.created_at_utc
  OR EXISTS (SELECT 1 FROM fsrs_parameter_activation WHERE snapshot_id=OLD.snapshot_id)
BEGIN
    SELECT RAISE(ABORT, 'fsrs_parameter_snapshot payload and activated history are immutable');
END;

CREATE TRIGGER IF NOT EXISTS fsrs_parameter_snapshot_delete_guard
BEFORE DELETE ON fsrs_parameter_snapshot
BEGIN
    SELECT RAISE(ABORT, 'fsrs_parameter_snapshot history is immutable');
END;

CREATE TRIGGER IF NOT EXISTS review_event_insert_identity
BEFORE INSERT ON review_event
WHEN EXISTS (SELECT 1 FROM review_event WHERE event_id=NEW.event_id OR command_id=NEW.command_id)
BEGIN
    SELECT RAISE(ABORT, 'review_event identity is immutable');
END;

CREATE TRIGGER IF NOT EXISTS slash_event_insert_identity
BEFORE INSERT ON slash_event
WHEN EXISTS (SELECT 1 FROM slash_event WHERE event_id=NEW.event_id)
BEGIN
    SELECT RAISE(ABORT, 'slash_event identity is immutable');
END;

CREATE TRIGGER IF NOT EXISTS fsrs_parameter_activation_insert_identity
BEFORE INSERT ON fsrs_parameter_activation
WHEN EXISTS (SELECT 1 FROM fsrs_parameter_activation WHERE activation_id=NEW.activation_id)
BEGIN
    SELECT RAISE(ABORT, 'fsrs_parameter_activation identity is immutable');
END;
