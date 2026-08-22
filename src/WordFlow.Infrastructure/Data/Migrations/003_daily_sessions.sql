CREATE TABLE daily_sessions (
    local_day TEXT PRIMARY KEY
        CHECK(length(local_day) = 10 AND local_day GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]' AND date(local_day) = local_day),
    configured_new_target INTEGER NOT NULL CHECK(configured_new_target >= 0),
    configured_review_target INTEGER NOT NULL CHECK(configured_review_target >= 0),
    effective_new_target INTEGER NOT NULL CHECK(effective_new_target >= 0),
    effective_review_target INTEGER NOT NULL CHECK(effective_review_target >= 0),
    completed_at_utc TEXT NULL CHECK(completed_at_utc IS NULL OR (length(completed_at_utc) = 28 AND substr(completed_at_utc, 11, 1) = 'T' AND substr(completed_at_utc, 28, 1) = 'Z')),
    created_at_utc TEXT NOT NULL CHECK(length(created_at_utc) = 28 AND substr(created_at_utc, 11, 1) = 'T' AND substr(created_at_utc, 28, 1) = 'Z'),
    updated_at_utc TEXT NOT NULL CHECK(length(updated_at_utc) = 28 AND substr(updated_at_utc, 11, 1) = 'T' AND substr(updated_at_utc, 28, 1) = 'Z')
) STRICT;

CREATE TABLE daily_queue_items (
    item_id TEXT PRIMARY KEY
        CHECK(length(item_id) = 36 AND substr(item_id, 9, 1) = '-' AND substr(item_id, 14, 1) = '-' AND substr(item_id, 19, 1) = '-' AND substr(item_id, 24, 1) = '-'),
    local_day TEXT NOT NULL REFERENCES daily_sessions(local_day)
        CHECK(length(local_day) = 10 AND local_day GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]' AND date(local_day) = local_day),
    ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
    card_id TEXT NOT NULL
        CHECK(length(card_id) = 36 AND substr(card_id, 9, 1) = '-' AND substr(card_id, 14, 1) = '-' AND substr(card_id, 19, 1) = '-' AND substr(card_id, 24, 1) = '-'),
    kind TEXT NOT NULL CHECK(kind IN ('New','Review','Relearning')),
    origin_day TEXT NOT NULL
        CHECK(length(origin_day) = 10 AND origin_day GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]' AND date(origin_day) = origin_day),
    source_item_id TEXT NULL REFERENCES daily_queue_items(item_id)
        CHECK(source_item_id IS NULL OR (length(source_item_id) = 36 AND substr(source_item_id, 9, 1) = '-' AND substr(source_item_id, 14, 1) = '-' AND substr(source_item_id, 19, 1) = '-' AND substr(source_item_id, 24, 1) = '-')),
    status TEXT NOT NULL CHECK(status IN ('Pending','Completed','Slashed','CarriedForward')),
    completed_event_id TEXT NULL REFERENCES review_event(event_id)
        CHECK(completed_event_id IS NULL OR (length(completed_event_id) = 36 AND substr(completed_event_id, 9, 1) = '-' AND substr(completed_event_id, 14, 1) = '-' AND substr(completed_event_id, 19, 1) = '-' AND substr(completed_event_id, 24, 1) = '-')),
    created_at_utc TEXT NOT NULL CHECK(length(created_at_utc) = 28 AND substr(created_at_utc, 11, 1) = 'T' AND substr(created_at_utc, 28, 1) = 'Z'),
    completed_at_utc TEXT NULL CHECK(completed_at_utc IS NULL OR (length(completed_at_utc) = 28 AND substr(completed_at_utc, 11, 1) = 'T' AND substr(completed_at_utc, 28, 1) = 'Z')),
    UNIQUE(local_day, ordinal)
) STRICT;

CREATE INDEX ix_daily_queue_next ON daily_queue_items(local_day, status, ordinal);
CREATE INDEX ix_daily_queue_backlog ON daily_queue_items(status, local_day, ordinal);
CREATE INDEX ix_daily_queue_card ON daily_queue_items(card_id, local_day, status);
