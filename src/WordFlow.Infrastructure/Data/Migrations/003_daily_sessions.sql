CREATE TABLE daily_sessions (
    local_day TEXT PRIMARY KEY
        CHECK(length(local_day) = 10 AND local_day GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]' AND date(local_day) IS local_day),
    configured_new_target INTEGER NOT NULL CHECK(configured_new_target >= 0),
    configured_review_target INTEGER NOT NULL CHECK(configured_review_target >= 0),
    effective_new_target INTEGER NOT NULL CHECK(effective_new_target >= 0),
    effective_review_target INTEGER NOT NULL CHECK(effective_review_target >= 0),
    completed_at_utc TEXT NULL CHECK(completed_at_utc IS NULL OR (
        length(completed_at_utc) = 28 AND completed_at_utc NOT GLOB '*[^0-9TZ:.-]*'
        AND substr(completed_at_utc, 11, 1) = 'T' AND substr(completed_at_utc, 14, 1) = ':' AND substr(completed_at_utc, 17, 1) = ':'
        AND substr(completed_at_utc, 20, 1) = '.' AND substr(completed_at_utc, 28, 1) = 'Z'
        AND date(substr(completed_at_utc, 1, 10)) IS substr(completed_at_utc, 1, 10)
        AND time(substr(completed_at_utc, 12, 8)) IS substr(completed_at_utc, 12, 8)
        AND substr(completed_at_utc, 21, 7) NOT GLOB '*[^0-9]*')),
    created_at_utc TEXT NOT NULL CHECK(
        length(created_at_utc) = 28 AND created_at_utc NOT GLOB '*[^0-9TZ:.-]*'
        AND substr(created_at_utc, 11, 1) = 'T' AND substr(created_at_utc, 14, 1) = ':' AND substr(created_at_utc, 17, 1) = ':'
        AND substr(created_at_utc, 20, 1) = '.' AND substr(created_at_utc, 28, 1) = 'Z'
        AND date(substr(created_at_utc, 1, 10)) IS substr(created_at_utc, 1, 10)
        AND time(substr(created_at_utc, 12, 8)) IS substr(created_at_utc, 12, 8)
        AND substr(created_at_utc, 21, 7) NOT GLOB '*[^0-9]*'),
    updated_at_utc TEXT NOT NULL CHECK(
        length(updated_at_utc) = 28 AND updated_at_utc NOT GLOB '*[^0-9TZ:.-]*'
        AND substr(updated_at_utc, 11, 1) = 'T' AND substr(updated_at_utc, 14, 1) = ':' AND substr(updated_at_utc, 17, 1) = ':'
        AND substr(updated_at_utc, 20, 1) = '.' AND substr(updated_at_utc, 28, 1) = 'Z'
        AND date(substr(updated_at_utc, 1, 10)) IS substr(updated_at_utc, 1, 10)
        AND time(substr(updated_at_utc, 12, 8)) IS substr(updated_at_utc, 12, 8)
        AND substr(updated_at_utc, 21, 7) NOT GLOB '*[^0-9]*')
) STRICT;

CREATE TABLE daily_queue_items (
    item_id TEXT PRIMARY KEY
        CHECK(length(item_id) = 36 AND lower(item_id) = item_id AND length(replace(item_id, '-', '')) = 32 AND replace(item_id, '-', '') NOT GLOB '*[^0-9a-f]*' AND substr(item_id, 9, 1) = '-' AND substr(item_id, 14, 1) = '-' AND substr(item_id, 19, 1) = '-' AND substr(item_id, 24, 1) = '-'),
    local_day TEXT NOT NULL REFERENCES daily_sessions(local_day)
        CHECK(length(local_day) = 10 AND local_day GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]' AND date(local_day) IS local_day),
    ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
    card_id TEXT NOT NULL
        CHECK(length(card_id) = 36 AND lower(card_id) = card_id AND length(replace(card_id, '-', '')) = 32 AND replace(card_id, '-', '') NOT GLOB '*[^0-9a-f]*' AND substr(card_id, 9, 1) = '-' AND substr(card_id, 14, 1) = '-' AND substr(card_id, 19, 1) = '-' AND substr(card_id, 24, 1) = '-'),
    kind TEXT NOT NULL CHECK(kind IN ('New','Review','Relearning')),
    origin_day TEXT NOT NULL
        CHECK(length(origin_day) = 10 AND origin_day GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]' AND date(origin_day) IS origin_day),
    source_item_id TEXT NULL REFERENCES daily_queue_items(item_id)
        CHECK(source_item_id IS NULL OR (length(source_item_id) = 36 AND lower(source_item_id) = source_item_id AND length(replace(source_item_id, '-', '')) = 32 AND replace(source_item_id, '-', '') NOT GLOB '*[^0-9a-f]*' AND substr(source_item_id, 9, 1) = '-' AND substr(source_item_id, 14, 1) = '-' AND substr(source_item_id, 19, 1) = '-' AND substr(source_item_id, 24, 1) = '-')),
    status TEXT NOT NULL CHECK(status IN ('Pending','Completed','Slashed','CarriedForward')),
    completed_event_id TEXT NULL REFERENCES review_event(event_id)
        CHECK(completed_event_id IS NULL OR (length(completed_event_id) = 36 AND lower(completed_event_id) = completed_event_id AND length(replace(completed_event_id, '-', '')) = 32 AND replace(completed_event_id, '-', '') NOT GLOB '*[^0-9a-f]*' AND substr(completed_event_id, 9, 1) = '-' AND substr(completed_event_id, 14, 1) = '-' AND substr(completed_event_id, 19, 1) = '-' AND substr(completed_event_id, 24, 1) = '-')),
    created_at_utc TEXT NOT NULL CHECK(
        length(created_at_utc) = 28 AND created_at_utc NOT GLOB '*[^0-9TZ:.-]*'
        AND substr(created_at_utc, 11, 1) = 'T' AND substr(created_at_utc, 14, 1) = ':' AND substr(created_at_utc, 17, 1) = ':'
        AND substr(created_at_utc, 20, 1) = '.' AND substr(created_at_utc, 28, 1) = 'Z'
        AND date(substr(created_at_utc, 1, 10)) IS substr(created_at_utc, 1, 10)
        AND time(substr(created_at_utc, 12, 8)) IS substr(created_at_utc, 12, 8)
        AND substr(created_at_utc, 21, 7) NOT GLOB '*[^0-9]*'),
    completed_at_utc TEXT NULL CHECK(completed_at_utc IS NULL OR (
        length(completed_at_utc) = 28 AND completed_at_utc NOT GLOB '*[^0-9TZ:.-]*'
        AND substr(completed_at_utc, 11, 1) = 'T' AND substr(completed_at_utc, 14, 1) = ':' AND substr(completed_at_utc, 17, 1) = ':'
        AND substr(completed_at_utc, 20, 1) = '.' AND substr(completed_at_utc, 28, 1) = 'Z'
        AND date(substr(completed_at_utc, 1, 10)) IS substr(completed_at_utc, 1, 10)
        AND time(substr(completed_at_utc, 12, 8)) IS substr(completed_at_utc, 12, 8)
        AND substr(completed_at_utc, 21, 7) NOT GLOB '*[^0-9]*')),
    UNIQUE(local_day, ordinal)
) STRICT;

CREATE INDEX ix_daily_queue_next ON daily_queue_items(local_day, status, ordinal);
CREATE INDEX ix_daily_queue_backlog ON daily_queue_items(status, local_day, ordinal);
CREATE INDEX ix_daily_queue_card ON daily_queue_items(card_id, local_day, status);
