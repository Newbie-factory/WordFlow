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

from tools.vocabulary.io_artifacts import CSV_FIELDS, load_curated, load_registry, sha256
from tools.vocabulary.normalization import normalize_word
from tools.vocabulary.selection import KNOWN_MISSPELLINGS, stable_word_id


SQLITE_SCHEMA = (
    ("stable_id", "TEXT", 0, 1),
    ("word", "TEXT", 1, 0),
    ("phonetic", "TEXT", 1, 0),
    ("definition_en", "TEXT", 0, 0),
    ("translation_zh_cn", "TEXT", 1, 0),
    ("pos", "TEXT", 0, 0),
    ("collins", "INTEGER", 0, 0),
    ("oxford", "INTEGER", 0, 0),
    ("tags", "TEXT", 0, 0),
    ("bnc_rank", "INTEGER", 0, 0),
    ("frequency_rank", "INTEGER", 0, 0),
    ("exchange", "TEXT", 0, 0),
    ("detail", "TEXT", 0, 0),
    ("tier", "TEXT", 1, 0),
    ("selection_reason", "TEXT", 1, 0),
    ("source_key", "TEXT", 1, 0),
    ("selection_score", "INTEGER", 1, 0),
)


@dataclass(frozen=True, slots=True)
class VerificationReport:
    passed: bool
    sqlite_integrity: str
    total: int
    unique_case_insensitive: int
    empty_word_csv: int
    empty_word_sqlite: int
    empty_translation_csv: int
    empty_translation_sqlite: int
    empty_provenance_csv: int
    empty_provenance_sqlite: int
    empty_selection_reason_csv: int
    empty_selection_reason_sqlite: int
    invalid_stable_id_csv: int
    invalid_stable_id_sqlite: int
    undocumented_provenance: tuple[str, ...]
    required_total: int
    required_present: int
    missing_required: tuple[str, ...]
    forbidden_present: tuple[str, ...]
    curated_sha_matches_manifest: bool
    registry_sha_matches_manifest: bool
    quality_required_matches_curated: bool
    csv_schema_valid: bool
    sqlite_schema_valid: bool
    csv_sha_matches_manifest: bool
    sqlite_sha_matches_manifest: bool
    quality_sha_matches_manifest: bool
    csv_rows_match_sqlite: bool


def _resolved(path: Path) -> Path:
    return path.resolve(strict=False)


def _inside(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
    except ValueError:
        return False
    return True


def resolve_artifact_path(
    artifact_dir: Path,
    manifest: dict[str, object],
    key: str,
    *,
    allowed_root: Path,
) -> Path:
    raw = Path(manifest["artifacts"][key]["path"])
    if raw.is_absolute():
        raise ValueError(f"manifest {key} path must be relative")
    resolved = _resolved(artifact_dir / raw)
    root = _resolved(allowed_root)
    if not _inside(resolved, root):
        raise ValueError(f"manifest {key} path escapes its allowed root")
    return resolved


def _manifest_input_matches(path: Path, manifest: dict[str, object], key: str) -> bool:
    record = manifest["inputs"][key]
    return sha256(path) == record["sha256"]


def _canonical_value(value: object) -> str:
    return "" if value is None else str(value)


def verify(
    artifact_dir: Path,
    *,
    curated: Path,
    source_registry: Path,
    report_root: Path | None = None,
    manifest_path_base: Path | None = None,
    minimum_total: int = 10_000,
) -> VerificationReport:
    artifact_dir = _resolved(artifact_dir)
    curated = _resolved(curated)
    source_registry = _resolved(source_registry)
    report_root = _resolved(report_root or (artifact_dir / "reports"))
    manifest_path_base = _resolved(manifest_path_base or artifact_dir)
    manifest = json.loads((artifact_dir / "manifest.json").read_text(encoding="utf-8"))
    csv_path = resolve_artifact_path(artifact_dir, manifest, "csv", allowed_root=artifact_dir)
    sqlite_path = resolve_artifact_path(artifact_dir, manifest, "sqlite", allowed_root=artifact_dir)
    quality_path = resolve_artifact_path(artifact_dir, manifest, "quality_report", allowed_root=report_root)

    curated_rows = load_curated(curated)
    registry = load_registry(source_registry)
    required_words = {normalize_word(row["word"]) for row in curated_rows}
    quality = json.loads(quality_path.read_text(encoding="utf-8"))
    quality_required = {normalize_word(word) for word in quality.get("required", {}).get("words", [])}
    curated_record_path = _resolved(manifest_path_base / Path(manifest["inputs"]["curated_vocabulary"]["path"]))
    registry_record_path = _resolved(manifest_path_base / Path(manifest["inputs"]["source_registry"]["path"]))

    with csv_path.open("r", encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        csv_schema_valid = tuple(reader.fieldnames or ()) == CSV_FIELDS
        csv_rows = list(reader) if csv_schema_valid else []

    with closing(sqlite3.connect(sqlite_path)) as connection:
        integrity = connection.execute("PRAGMA integrity_check").fetchone()[0]
        table_info = connection.execute("PRAGMA table_info(vocabulary)").fetchall()
        sqlite_declarations = tuple((row[1], row[2].upper(), row[3], row[5]) for row in table_info)
        sqlite_schema_valid = sqlite_declarations == SQLITE_SCHEMA
        sqlite_rows = (
            connection.execute(f"SELECT {','.join(CSV_FIELDS)} FROM vocabulary ORDER BY rowid").fetchall()
            if sqlite_schema_valid
            else []
        )

    csv_word_values = [row["word"] for row in csv_rows]
    sqlite_word_values = [str(row[1]) for row in sqlite_rows]
    normalized_words = [normalize_word(word) for word in sqlite_word_values]
    present_words = set(normalized_words)
    missing_required = tuple(sorted(required_words - present_words))
    forbidden_present = tuple(sorted(present_words & KNOWN_MISSPELLINGS))
    csv_tuples = [tuple(_canonical_value(row[field]) for field in CSV_FIELDS) for row in csv_rows]
    sqlite_tuples = [tuple(_canonical_value(value) for value in row) for row in sqlite_rows]
    invalid_ids_csv = sum(row["stable_id"] != stable_word_id(row["word"]) for row in csv_rows)
    invalid_ids_sqlite = sum(row[0] != stable_word_id(str(row[1])) for row in sqlite_rows)
    provenance_keys = {
        value.strip()
        for value in [*(row["source_key"] for row in csv_rows), *(str(row[15]) for row in sqlite_rows)]
        if value.strip()
    }
    undocumented_provenance = tuple(sorted(provenance_keys - set(registry["sources"])))

    values = {
        "sqlite_integrity": integrity,
        "total": len(sqlite_rows),
        "unique_case_insensitive": len(present_words),
        "empty_word_csv": sum(not value.strip() for value in csv_word_values),
        "empty_word_sqlite": sum(not value.strip() for value in sqlite_word_values),
        "empty_translation_csv": sum(not row["translation_zh_cn"].strip() for row in csv_rows),
        "empty_translation_sqlite": sum(not str(row[4]).strip() for row in sqlite_rows),
        "empty_provenance_csv": sum(not row["source_key"].strip() for row in csv_rows),
        "empty_provenance_sqlite": sum(not str(row[15]).strip() for row in sqlite_rows),
        "empty_selection_reason_csv": sum(not row["selection_reason"].strip() for row in csv_rows),
        "empty_selection_reason_sqlite": sum(not str(row[14]).strip() for row in sqlite_rows),
        "invalid_stable_id_csv": invalid_ids_csv,
        "invalid_stable_id_sqlite": invalid_ids_sqlite,
        "undocumented_provenance": undocumented_provenance,
        "required_total": len(required_words),
        "required_present": len(required_words) - len(missing_required),
        "missing_required": missing_required,
        "forbidden_present": forbidden_present,
        "curated_sha_matches_manifest": (
            curated_record_path == curated and _manifest_input_matches(curated, manifest, "curated_vocabulary")
        ),
        "registry_sha_matches_manifest": (
            registry_record_path == source_registry
            and _manifest_input_matches(source_registry, manifest, "source_registry")
        ),
        "quality_required_matches_curated": quality_required == required_words,
        "csv_schema_valid": csv_schema_valid,
        "sqlite_schema_valid": sqlite_schema_valid,
        "csv_sha_matches_manifest": sha256(csv_path) == manifest["artifacts"]["csv"]["sha256"],
        "sqlite_sha_matches_manifest": sha256(sqlite_path) == manifest["artifacts"]["sqlite"]["sha256"],
        "quality_sha_matches_manifest": sha256(quality_path) == manifest["artifacts"]["quality_report"]["sha256"],
        "csv_rows_match_sqlite": csv_tuples == sqlite_tuples,
    }
    passed = (
        integrity == "ok"
        and len(sqlite_rows) >= minimum_total
        and len(sqlite_rows) == len(present_words)
        and all(not values[key] for key in (
            "empty_word_csv",
            "empty_word_sqlite",
            "empty_translation_csv",
            "empty_translation_sqlite",
            "empty_provenance_csv",
            "empty_provenance_sqlite",
            "empty_selection_reason_csv",
            "empty_selection_reason_sqlite",
            "invalid_stable_id_csv",
            "invalid_stable_id_sqlite",
        ))
        and not missing_required
        and not forbidden_present
        and not undocumented_provenance
        and all(values[key] for key in (
            "curated_sha_matches_manifest",
            "registry_sha_matches_manifest",
            "quality_required_matches_curated",
            "csv_schema_valid",
            "sqlite_schema_valid",
            "csv_sha_matches_manifest",
            "sqlite_sha_matches_manifest",
            "quality_sha_matches_manifest",
            "csv_rows_match_sqlite",
        ))
    )
    return VerificationReport(passed=passed, **values)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Verify WordFlow vocabulary artifacts")
    parser.add_argument("--artifact-dir", type=Path, required=True)
    parser.add_argument("--curated", type=Path, required=True)
    parser.add_argument("--source-registry", type=Path, required=True)
    parser.add_argument("--report-root", type=Path)
    parser.add_argument("--minimum-total", type=int, default=10_000)
    args = parser.parse_args(argv)
    report = verify(
        args.artifact_dir,
        curated=args.curated,
        source_registry=args.source_registry,
        report_root=args.report_root,
        minimum_total=args.minimum_total,
    )
    print(json.dumps(asdict(report), ensure_ascii=False, indent=2))
    return 0 if report.passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
