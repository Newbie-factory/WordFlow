CREATE TRIGGER IF NOT EXISTS fsrs_parameter_snapshot_insert_identity
BEFORE INSERT ON fsrs_parameter_snapshot
WHEN EXISTS (SELECT 1 FROM fsrs_parameter_snapshot WHERE snapshot_id=NEW.snapshot_id)
BEGIN
    SELECT RAISE(ABORT, 'fsrs_parameter_snapshot identity is immutable');
END;

CREATE TRIGGER IF NOT EXISTS fsrs_parameter_snapshot_update_guard
BEFORE UPDATE ON fsrs_parameter_snapshot
WHEN OLD.status <> 'PendingPreview'
  OR NEW.status NOT IN ('ReadyForActivation','PreviewFailed')
  OR json_remove(OLD.snapshot_json, '$.Status') <> json_remove(NEW.snapshot_json, '$.Status')
  OR json_extract(NEW.snapshot_json, '$.Status') <> NEW.status
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
