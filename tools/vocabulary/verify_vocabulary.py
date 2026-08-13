from __future__ import annotations

import argparse
import csv
from contextlib import closing
from dataclasses import asdict, dataclass
import json
import os
from pathlib import Path
import sqlite3
import sys

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from tools.vocabulary.io_artifacts import sha256
from tools.vocabulary.selection import KNOWN_MISSPELLINGS, normalize_word


@dataclass(frozen=True, slots=True)
class VerificationReport:
    passed: bool
    sqlite_integrity: str
    total: int
    unique_case_insensitive: int
    empty_word: int
    empty_translation_zh_cn: int
    empty_provenance: int
    required_total: int
    required_present: int
    missing_required: tuple[str, ...]
    forbidden_present: tuple[str, ...]
    csv_sha_matches_manifest: bool
    sqlite_sha_matches_manifest: bool
    quality_sha_matches_manifest: bool
    csv_rows_match_sqlite: bool


def resolve_artifact_path(artifact_dir: Path, manifest: dict[str, object], key: str) -> Path:
    artifact = manifest["artifacts"][key]
    return Path(os.path.normpath(artifact_dir / Path(artifact["path"])))


def verify(artifact_dir: Path, *, minimum_total: int = 10_000) -> VerificationReport:
    manifest_path = artifact_dir / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    quality_path = resolve_artifact_path(artifact_dir, manifest, "quality_report")
    csv_path = resolve_artifact_path(artifact_dir, manifest, "csv")
    sqlite_path = resolve_artifact_path(artifact_dir, manifest, "sqlite")
    quality = json.loads(quality_path.read_text(encoding="utf-8"))
    with csv_path.open("r", encoding="utf-8-sig", newline="") as stream:
        csv_rows = list(csv.DictReader(stream))
    with closing(sqlite3.connect(sqlite_path)) as connection:
        integrity = connection.execute("PRAGMA integrity_check").fetchone()[0]
        rows = connection.execute("SELECT word, translation_zh_cn, source_key FROM vocabulary").fetchall()
        sqlite_ids = set(connection.execute("SELECT stable_id, word FROM vocabulary").fetchall())
    words = [row[0] for row in rows]
    normalized = [normalize_word(word) for word in words]
    required_words = {normalize_word(word) for word in quality["required"]["words"]}
    present_words = set(normalized)
    missing_required = tuple(sorted(required_words - present_words))
    required_total = len(required_words)
    required_present = required_total - len(missing_required)
    forbidden_present = tuple(sorted(set(normalized) & KNOWN_MISSPELLINGS))
    csv_hash_ok = sha256(csv_path) == manifest["artifacts"]["csv"]["sha256"]
    sqlite_hash_ok = sha256(sqlite_path) == manifest["artifacts"]["sqlite"]["sha256"]
    quality_hash_ok = sha256(quality_path) == manifest["artifacts"]["quality_report"]["sha256"]
    csv_rows_match = len(csv_rows) == len(rows) and {
        (row["stable_id"], normalize_word(row["word"])) for row in csv_rows
    } == {
        (stable_id, normalize_word(word)) for stable_id, word in sqlite_ids
    }
    report_values = {
        "sqlite_integrity": integrity,
        "total": len(rows),
        "unique_case_insensitive": len(set(normalized)),
        "empty_word": sum(not word.strip() for word in words),
        "empty_translation_zh_cn": sum(not row[1].strip() for row in rows),
        "empty_provenance": sum(not row[2].strip() for row in rows),
        "required_total": required_total,
        "required_present": required_present,
        "missing_required": missing_required,
        "forbidden_present": forbidden_present,
        "csv_sha_matches_manifest": csv_hash_ok,
        "sqlite_sha_matches_manifest": sqlite_hash_ok,
        "quality_sha_matches_manifest": quality_hash_ok,
        "csv_rows_match_sqlite": csv_rows_match,
    }
    passed = (
        integrity == "ok"
        and len(rows) >= minimum_total
        and len(set(normalized)) == len(rows)
        and report_values["empty_word"] == 0
        and report_values["empty_translation_zh_cn"] == 0
        and report_values["empty_provenance"] == 0
        and required_present == required_total
        and not missing_required
        and not forbidden_present
        and csv_hash_ok
        and sqlite_hash_ok
        and quality_hash_ok
        and csv_rows_match
    )
    return VerificationReport(passed=passed, **report_values)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Verify WordFlow vocabulary artifacts")
    parser.add_argument("--artifact-dir", type=Path, required=True)
    parser.add_argument("--minimum-total", type=int, default=10_000)
    args = parser.parse_args(argv)
    report = verify(args.artifact_dir, minimum_total=args.minimum_total)
    print(json.dumps(asdict(report), ensure_ascii=False, indent=2))
    return 0 if report.passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
