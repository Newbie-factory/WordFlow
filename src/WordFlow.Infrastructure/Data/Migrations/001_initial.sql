CREATE TABLE schema_version (
    version INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    applied_at_utc TEXT NOT NULL
) STRICT;

CREATE TABLE card_state (
    card_id TEXT PRIMARY KEY CHECK(length(card_id) = 36),
    difficulty REAL NULL,
    stability_days REAL NULL,
    last_review_at_utc TEXT NULL,
    due_at_utc TEXT NOT NULL,
    is_slashed INTEGER NOT NULL CHECK(is_slashed IN (0, 1)),
    slashed_at_utc TEXT NULL,
    restored_at_utc TEXT NULL,
    same_day_failure_count INTEGER NOT NULL CHECK(same_day_failure_count >= 0),
    failure_day_utc TEXT NULL,
    hard_word_protected_until_utc TEXT NULL,
    last_event_id TEXT NOT NULL UNIQUE CHECK(length(last_event_id) = 36),
    CHECK((difficulty IS NULL AND stability_days IS NULL AND last_review_at_utc IS NULL)
       OR (difficulty IS NOT NULL AND stability_days IS NOT NULL AND last_review_at_utc IS NOT NULL))
) STRICT;

CREATE TABLE review_event (
    event_id TEXT PRIMARY KEY CHECK(length(event_id) = 36),
    command_id TEXT NOT NULL UNIQUE CHECK(length(command_id) = 36),
    card_id TEXT NOT NULL CHECK(length(card_id) = 36),
    occurred_at_utc TEXT NOT NULL,
    action TEXT NOT NULL CHECK(action IN ('Again','Hard','Good','Slash','RestoreScheduled','RestoreImmediate','Undo')),
    compensates_event_id TEXT NULL REFERENCES review_event(event_id),
    before_json TEXT NOT NULL,
    after_json TEXT NOT NULL
) STRICT;

CREATE INDEX ix_review_event_card_occurred ON review_event(card_id, occurred_at_utc, event_id);

CREATE TRIGGER review_event_immutable_update
BEFORE UPDATE ON review_event
BEGIN
    SELECT RAISE(ABORT, 'review_event rows are immutable');
END;

CREATE TRIGGER review_event_immutable_delete
BEFORE DELETE ON review_event
BEGIN
    SELECT RAISE(ABORT, 'review_event rows are immutable');
END;

CREATE TABLE slash_event (
    event_id TEXT PRIMARY KEY REFERENCES review_event(event_id) ON DELETE RESTRICT,
    card_id TEXT NOT NULL CHECK(length(card_id) = 36),
    action TEXT NOT NULL CHECK(action IN ('Slash','RestoreScheduled','RestoreImmediate','Undo')),
    occurred_at_utc TEXT NOT NULL
) STRICT;

CREATE TRIGGER slash_event_immutable_update
BEFORE UPDATE ON slash_event
BEGIN
    SELECT RAISE(ABORT, 'slash_event rows are immutable');
END;

CREATE TRIGGER slash_event_immutable_delete
BEFORE DELETE ON slash_event
BEGIN
    SELECT RAISE(ABORT, 'slash_event rows are immutable');
END;

CREATE TABLE daily_plan (
    plan_day_utc TEXT PRIMARY KEY,
    new_limit INTEGER NOT NULL CHECK(new_limit >= 0),
    soft_review_limit INTEGER NOT NULL CHECK(soft_review_limit >= 0),
    updated_at_utc TEXT NOT NULL
) STRICT;

CREATE TABLE app_setting (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
) STRICT;

CREATE TABLE shortcut_binding (
    command TEXT PRIMARY KEY,
    gesture TEXT NOT NULL UNIQUE
) STRICT;

CREATE TABLE user_word_relation (
    source_word_id TEXT NOT NULL CHECK(length(source_word_id) = 36),
    target_word_id TEXT NOT NULL CHECK(length(target_word_id) = 36),
    relation_type TEXT NOT NULL,
    is_enabled INTEGER NOT NULL CHECK(is_enabled IN (0, 1)),
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY(source_word_id, target_word_id, relation_type)
) STRICT;

CREATE TABLE skin_preset (
    preset_id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
) STRICT;

CREATE TABLE backup_record (
    backup_id TEXT PRIMARY KEY CHECK(length(backup_id) = 36),
    file_path TEXT NOT NULL,
    from_schema_version INTEGER NOT NULL,
    created_at_utc TEXT NOT NULL
) STRICT;

CREATE TABLE fsrs_parameter_snapshot (
    snapshot_id TEXT PRIMARY KEY CHECK(length(snapshot_id) = 36),
    status TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
) STRICT;

CREATE TABLE fsrs_parameter_activation (
    activation_id TEXT PRIMARY KEY CHECK(length(activation_id) = 36),
    snapshot_id TEXT NOT NULL REFERENCES fsrs_parameter_snapshot(snapshot_id),
    activated_at_utc TEXT NOT NULL,
    reason TEXT NOT NULL,
    source_activation_id TEXT NULL REFERENCES fsrs_parameter_activation(activation_id)
) STRICT;

CREATE TRIGGER fsrs_parameter_activation_immutable_update
BEFORE UPDATE ON fsrs_parameter_activation
BEGIN
    SELECT RAISE(ABORT, 'fsrs_parameter_activation rows are immutable');
END;

CREATE TRIGGER fsrs_parameter_activation_immutable_delete
BEFORE DELETE ON fsrs_parameter_activation
BEGIN
    SELECT RAISE(ABORT, 'fsrs_parameter_activation rows are immutable');
END;
