from __future__ import annotations

import argparse
from collections import Counter
from dataclasses import asdict
import json
from pathlib import Path
import sys

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from tools.vocabulary.io_artifacts import (
    load_curated,
    load_registry,
    load_source,
    sha256,
    write_csv,
    write_json,
    write_sqlite,
)
from tools.vocabulary.models import BuildConfig, BuildReport
from tools.vocabulary.selection import KNOWN_MISSPELLINGS, MINIMUM_ORDINARY_SCORE, normalize_word, select_entries


POLICY_VERSION = "wordflow-vocabulary-selection-v1"


def report_payload(report: BuildReport) -> dict[str, object]:
    payload = asdict(report)
    payload["output"] = str(report.output)
    payload["missing_required"] = list(report.missing_required)
    return payload


def build(config: BuildConfig) -> BuildReport:
    if not config.source.is_file():
        raise FileNotFoundError(f"source corpus not found: {config.source}")
    if not config.curated.is_file():
        raise FileNotFoundError(f"curated vocabulary not found: {config.curated}")
    if not config.source_registry.is_file():
        raise FileNotFoundError(f"source registry not found: {config.source_registry}")

    registry = load_registry(config.source_registry)
    sources = registry["sources"]
    if "ecdict" not in sources:
        raise ValueError("source registry does not document ecdict")
    curated_rows = load_curated(config.curated)
    invalid_status = [row["word"] for row in curated_rows if row["review_status"] != "reviewed"]
    if invalid_status:
        raise ValueError(f"all required vocabulary must be reviewed: {invalid_status}")
    undocumented = sorted({row["source_key"] for row in curated_rows if row["source_key"] not in sources})
    if undocumented:
        raise ValueError(f"undocumented curated source keys: {undocumented}")
    duplicates = [word for word, count in Counter(normalize_word(row["word"]) for row in curated_rows).items() if count > 1]
    if duplicates:
        raise ValueError(f"duplicate curated words: {duplicates}")
    forbidden = sorted({normalize_word(row["word"]) for row in curated_rows} & KNOWN_MISSPELLINGS)
    if forbidden:
        raise ValueError(f"known misspellings cannot be curated headwords: {forbidden}")

    required = {row["word"]: row["reason"] for row in curated_rows}
    result = select_entries(
        load_source(config.source),
        required=required,
        soft_min=config.soft_min,
        soft_max=config.soft_max,
    )
    if result.ordinary_count < config.soft_min:
        raise RuntimeError(
            f"only {result.ordinary_count} ordinary entries passed the quality gate; soft minimum is {config.soft_min}"
        )
    if result.missing_required:
        raise RuntimeError(f"required words missing from releasable source rows: {list(result.missing_required)}")

    config.output.mkdir(parents=True, exist_ok=True)
    csv_path = config.output / "vocabulary.csv"
    sqlite_path = config.output / "vocabulary.sqlite3"
    write_csv(csv_path, result.entries)
    write_sqlite(sqlite_path, result.entries)
    csv_hash = sha256(csv_path)
    sqlite_hash = sha256(sqlite_path)
    source_hash = sha256(config.source)
    tier_counts = dict(sorted(Counter(entry.tier for entry in result.entries).items()))
    required_present = len(curated_rows) - len(result.missing_required)

    quality = {
        "policy_version": POLICY_VERSION,
        "passed_build_gates": True,
        "counts": {
            "total": len(result.entries),
            "ordinary": result.ordinary_count,
            "required_closure": result.closure_count,
            "tiers": tier_counts,
            "with_chinese_translation": sum(bool(entry.translation_zh_cn) for entry in result.entries),
            "with_phonetic": sum(bool(entry.phonetic) for entry in result.entries),
            "with_provenance": sum(bool(entry.source_key) for entry in result.entries),
        },
        "bounds": {
            "soft_min": config.soft_min,
            "soft_max": config.soft_max,
            "exceeded_soft_max_due_to_closure": result.exceeded_soft_max,
        },
        "required": {
            "total": len(curated_rows),
            "present": required_present,
            "missing": list(result.missing_required),
            "words": sorted(normalize_word(row["word"]) for row in curated_rows),
            "source_keys": dict(sorted(Counter(row["source_key"] for row in curated_rows).items())),
        },
        "forbidden_headwords": {
            "words": sorted(KNOWN_MISSPELLINGS),
            "present": sorted({normalize_word(entry.word) for entry in result.entries} & KNOWN_MISSPELLINGS),
        },
        "rejections": result.rejection_counts,
    }
    quality_path = config.output / "reports" / "vocabulary-quality.json"
    write_json(quality_path, quality)

    manifest = {
        "dataset_name": "WordFlow Quality-Gated IELTS and Academic Vocabulary",
        "schema_version": 1,
        "policy_version": POLICY_VERSION,
        "source": {
            "key": "ecdict",
            "name": sources["ecdict"].get("name", "ECDICT"),
            "url": sources["ecdict"].get("url"),
            "license": sources["ecdict"].get("license"),
            "source_sha256": source_hash,
        },
        "selection_policy": {
            "soft_min": config.soft_min,
            "soft_max": config.soft_max,
            "minimum_ordinary_score": MINIMUM_ORDINARY_SCORE,
            "required_closure_may_exceed_soft_max": True,
            "disclaimer": (
                "IELTS does not publish an official exhaustive vocabulary list. "
                "This corpus is a reproducible, quality-gated learning selection from documented evidence."
            ),
        },
        "inputs": {
            "curated_vocabulary": {
                "path": "data/curated/required_vocabulary.csv",
                "sha256": sha256(config.curated),
            },
            "source_registry": {
                "path": "data/curated/source_registry.json",
                "sha256": sha256(config.source_registry),
            },
        },
        "counts": {
            "total": len(result.entries),
            "ordinary": result.ordinary_count,
            "required_closure": result.closure_count,
            "required_present": required_present,
            "tiers": tier_counts,
        },
        "artifacts": {
            "csv": {"path": "vocabulary.csv", "bytes": csv_path.stat().st_size, "sha256": csv_hash},
            "sqlite": {
                "path": "vocabulary.sqlite3",
                "bytes": sqlite_path.stat().st_size,
                "sha256": sqlite_hash,
            },
            "quality_report": {"path": "reports/vocabulary-quality.json", "sha256": sha256(quality_path)},
        },
    }
    write_json(config.output / "manifest.json", manifest)
    return BuildReport(
        total=len(result.entries),
        ordinary_count=result.ordinary_count,
        closure_count=result.closure_count,
        required_count=len(curated_rows),
        required_present=required_present,
        missing_required=result.missing_required,
        csv_sha256=csv_hash,
        sqlite_sha256=sqlite_hash,
        source_sha256=source_hash,
        curated_sha256=sha256(config.curated),
        registry_sha256=sha256(config.source_registry),
        output=config.output,
    )


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build the WordFlow quality-gated vocabulary corpus")
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--curated", type=Path, required=True)
    parser.add_argument("--source-registry", type=Path, default=Path("data/curated/source_registry.json"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--soft-min", type=int, default=10_000)
    parser.add_argument("--soft-max", type=int, default=12_000)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    report = build(
        BuildConfig(
            source=args.source,
            curated=args.curated,
            source_registry=args.source_registry,
            output=args.output,
            soft_min=args.soft_min,
            soft_max=args.soft_max,
        )
    )
    print(json.dumps(report_payload(report), ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
