CREATE TABLE notebook_entry (
    word_id TEXT PRIMARY KEY CHECK(length(word_id) = 36),
    added_at_utc TEXT NOT NULL
) STRICT;