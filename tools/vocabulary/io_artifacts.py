from __future__ import annotations

import csv
import hashlib
import json
from pathlib import Path
import sqlite3
import sys
from typing import Any
from urllib.parse import urlparse

from tools.vocabulary.models import SelectedEntry, SourceEntry
from tools.vocabulary.normalization import normalize_word


CSV_FIELDS = (
    "stable_id",
    "word",
    "phonetic",
    "definition_en",
    "translation_zh_cn",
    "pos",
    "collins",
    "oxford",
    "tags",
    "bnc_rank",
    "frequency_rank",
    "exchange",
    "detail",
    "tier",
    "selection_reason",
    "source_key",
    "selection_score",
)
CURATED_FIELDS = ("word", "reason", "source_key", "review_status")
SUPPORTED_CURATED_REASONS = frozenset({"ielts_topic_family", "user_confusable"})


def positive_integer(value: str | None) -> int | None:
    text = (value or "").strip()
    return int(text) if text.isdigit() and int(text) > 0 else None


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def load_source(path: Path, source_key: str = "ecdict") -> list[SourceEntry]:
    csv.field_size_limit(sys.maxsize)
    entries: list[SourceEntry] = []
    with path.open("r", encoding="utf-8-sig", newline="") as stream:
        for row in csv.DictReader(stream):
            entries.append(
                SourceEntry(
                    word=(row.get("word") or "").strip(),
                    phonetic=(row.get("phonetic") or "").strip(),
                    definition_en=(row.get("definition") or "").strip(),
                    translation_zh_cn=(row.get("translation") or "").strip(),
                    pos=(row.get("pos") or "").strip(),
                    collins=positive_integer(row.get("collins")),
                    oxford=positive_integer(row.get("oxford")),
                    tags=(row.get("tag") or "").strip(),
                    bnc_rank=positive_integer(row.get("bnc")),
                    frequency_rank=positive_integer(row.get("frq")),
                    exchange=(row.get("exchange") or "").strip(),
                    detail=(row.get("detail") or "").strip(),
                    source_key=source_key,
                )
            )
    return entries


def load_curated(path: Path) -> list[dict[str, str]]:
    with path.open("r", encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if tuple(reader.fieldnames or ()) != CURATED_FIELDS:
            raise ValueError(f"curated vocabulary must have exact header {CURATED_FIELDS}")
        rows = []
        for row_number, raw in enumerate(reader, start=2):
            if None in raw:
                raise ValueError(f"curated row {row_number} must contain exactly four columns")
            rows.append({key: (value or "").strip() for key, value in raw.items()})
    if not rows:
        raise ValueError("curated vocabulary has no data rows")
    for row_number, row in enumerate(rows, start=2):
        blank_fields = [field for field in CURATED_FIELDS if not row[field]]
        if blank_fields:
            raise ValueError(f"curated row {row_number} has blank fields: {blank_fields}")
        if row["reason"] not in SUPPORTED_CURATED_REASONS:
            raise ValueError(
                f"curated row {row_number} reason must be a supported reason: "
                f"{sorted(SUPPORTED_CURATED_REASONS)}"
            )
        if row["review_status"] != "reviewed":
            raise ValueError(f"curated row {row_number} review_status must be reviewed")
    normalized_words = [normalize_word(row["word"]) for row in rows]
    duplicates = sorted({word for word in normalized_words if normalized_words.count(word) > 1})
    if duplicates:
        raise ValueError(f"curated vocabulary contains duplicate normalized words: {duplicates}")
    misspellings = sorted(set(normalized_words) & {"stimuate", "dissimuate"})
    if misspellings:
        raise ValueError(f"curated vocabulary contains forbidden misspellings: {misspellings}")
    return rows


def load_registry(path: Path) -> dict[str, Any]:
    registry = json.loads(path.read_text(encoding="utf-8"))
    sources = registry.get("sources")
    if not isinstance(sources, dict) or not sources:
        raise ValueError("source registry must contain a nonempty sources object")
    for source_key, record in sources.items():
        if not isinstance(source_key, str) or not source_key.strip() or not isinstance(record, dict):
            raise ValueError("source registry keys must map to evidence objects")
        for field in ("name", "role", "license"):
            if not isinstance(record.get(field), str) or not record[field].strip():
                raise ValueError(f"source {source_key!r} requires nonblank {field}")
        locations = [field for field in ("url", "path") if isinstance(record.get(field), str) and record[field].strip()]
        if not locations:
            raise ValueError(f"source {source_key!r} requires a nonblank url or path")
        if "url" in locations and urlparse(record["url"]).scheme not in {"http", "https"}:
            raise ValueError(f"source {source_key!r} url must use http or https")
    return registry


def _row(entry: SelectedEntry) -> dict[str, object]:
    return {field: getattr(entry, field) for field in CSV_FIELDS}


def write_csv(path: Path, entries: tuple[SelectedEntry, ...]) -> None:
    with path.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=CSV_FIELDS, lineterminator="\n")
        writer.writeheader()
        writer.writerows(_row(entry) for entry in entries)


def write_sqlite(path: Path, entries: tuple[SelectedEntry, ...]) -> None:
    if path.exists():
        path.unlink()
    connection = sqlite3.connect(path)
    try:
        connection.executescript(
            """
            PRAGMA page_size = 4096;
            PRAGMA journal_mode = DELETE;
            CREATE TABLE vocabulary (
                stable_id TEXT PRIMARY KEY,
                word TEXT NOT NULL COLLATE NOCASE UNIQUE,
                phonetic TEXT NOT NULL,
                definition_en TEXT,
                translation_zh_cn TEXT NOT NULL,
                pos TEXT,
                collins INTEGER,
                oxford INTEGER,
                tags TEXT,
                bnc_rank INTEGER,
                frequency_rank INTEGER,
                exchange TEXT,
                detail TEXT,
                tier TEXT NOT NULL,
                selection_reason TEXT NOT NULL,
                source_key TEXT NOT NULL,
                selection_score INTEGER NOT NULL
            );
            CREATE INDEX idx_vocabulary_tier ON vocabulary(tier);
            CREATE INDEX idx_vocabulary_frequency ON vocabulary(frequency_rank);
            CREATE INDEX idx_vocabulary_bnc ON vocabulary(bnc_rank);
            """
        )
        placeholders = ",".join("?" for _ in CSV_FIELDS)
        connection.executemany(
            f"INSERT INTO vocabulary ({','.join(CSV_FIELDS)}) VALUES ({placeholders})",
            ([getattr(entry, field) for field in CSV_FIELDS] for entry in entries),
        )
        connection.commit()
        connection.execute("VACUUM")
    finally:
        connection.close()


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
