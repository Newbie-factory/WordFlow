from __future__ import annotations

import argparse
from collections import Counter
from dataclasses import asdict
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile

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
from tools.vocabulary.normalization import normalize_word
from tools.vocabulary.selection import KNOWN_MISSPELLINGS, MINIMUM_ORDINARY_SCORE, select_entries


POLICY_VERSION = "wordflow-vocabulary-selection-v1"


def report_payload(report: BuildReport) -> dict[str, object]:
    payload = asdict(report)
    payload["output"] = str(report.output)
    payload["missing_required"] = list(report.missing_required)
    return payload


def _portable_relative(target: Path, base: Path) -> str:
    try:
        relative = Path(os.path.relpath(target, base))
    except ValueError as error:
        raise ValueError(f"path {target} cannot be represented portably relative to {base}") from error
    return relative.as_posix()


def _resolved_config(config: BuildConfig) -> BuildConfig:
    return BuildConfig(
        source=config.source.resolve(strict=True),
        curated=config.curated.resolve(strict=True),
        source_registry=config.source_registry.resolve(strict=True),
        output=config.output.resolve(strict=False),
        soft_min=config.soft_min,
        soft_max=config.soft_max,
    )


def _validate_output_collisions(inputs: tuple[Path, ...], outputs: tuple[Path, ...]) -> None:
    input_set = set(inputs)
    if len(set(outputs)) != len(outputs):
        raise ValueError("output targets collide with each other")
    for output in outputs:
        if output in input_set:
            raise ValueError(f"output target collides with input: {output}")


def _promote_files(staged_to_final: tuple[tuple[Path, Path], ...], backup_root: Path) -> None:
    backups: list[tuple[Path, Path]] = []
    promoted: list[Path] = []
    backup_root.mkdir(parents=True, exist_ok=True)
    try:
        for index, (staged, final) in enumerate(staged_to_final):
            final.parent.mkdir(parents=True, exist_ok=True)
            if final.exists():
                backup = backup_root / f"{index:02d}-{final.name}"
                os.replace(final, backup)
                backups.append((backup, final))
            os.replace(staged, final)
            promoted.append(final)
    except BaseException:
        for final in reversed(promoted):
            if final.exists():
                final.unlink()
        for backup, final in reversed(backups):
            if backup.exists():
                os.replace(backup, final)
        raise


def _build_staged(
    config: BuildConfig,
    *,
    staged_artifact_dir: Path,
    staged_report_root: Path,
    final_artifact_dir: Path,
    final_report_root: Path,
) -> BuildReport:
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
    duplicates = [
        word
        for word, count in Counter(normalize_word(row["word"]) for row in curated_rows).items()
        if count > 1
    ]
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

    staged_artifact_dir.mkdir(parents=True, exist_ok=True)
    staged_report_root.mkdir(parents=True, exist_ok=True)
    csv_path = staged_artifact_dir / "vocabulary.csv"
    sqlite_path = staged_artifact_dir / "vocabulary.sqlite3"
    quality_path = staged_report_root / "vocabulary-quality.json"
    write_csv(csv_path, result.entries)
    write_sqlite(sqlite_path, result.entries)
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
    write_json(quality_path, quality)

    manifest = {
        "dataset_name": "WordFlow Quality-Gated IELTS and Academic Vocabulary",
        "schema_version": 1,
        "policy_version": POLICY_VERSION,
        "source": {
            "key": "ecdict",
            "name": sources["ecdict"]["name"],
            "url": sources["ecdict"].get("url"),
            "license": sources["ecdict"]["license"],
            "path": _portable_relative(config.source, final_artifact_dir),
            "source_sha256": sha256(config.source),
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
                "path": _portable_relative(config.curated, final_artifact_dir),
                "sha256": sha256(config.curated),
            },
            "source_registry": {
                "path": _portable_relative(config.source_registry, final_artifact_dir),
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
            "csv": {"path": "vocabulary.csv", "bytes": csv_path.stat().st_size, "sha256": sha256(csv_path)},
            "sqlite": {
                "path": "vocabulary.sqlite3",
                "bytes": sqlite_path.stat().st_size,
                "sha256": sha256(sqlite_path),
            },
            "quality_report": {
                "path": _portable_relative(final_report_root / "vocabulary-quality.json", final_artifact_dir),
                "sha256": sha256(quality_path),
            },
        },
    }
    write_json(staged_artifact_dir / "manifest.json", manifest)
    return BuildReport(
        total=len(result.entries),
        ordinary_count=result.ordinary_count,
        closure_count=result.closure_count,
        required_count=len(curated_rows),
        required_present=required_present,
        missing_required=result.missing_required,
        csv_sha256=sha256(csv_path),
        sqlite_sha256=sha256(sqlite_path),
        source_sha256=sha256(config.source),
        curated_sha256=sha256(config.curated),
        registry_sha256=sha256(config.source_registry),
        output=final_artifact_dir,
    )


def _execute_build(config: BuildConfig, *, final_report_root: Path) -> BuildReport:
    config = _resolved_config(config)
    final_artifact_dir = config.output
    final_report_root = final_report_root.resolve(strict=False)
    final_targets = (
        final_artifact_dir / "vocabulary.csv",
        final_artifact_dir / "vocabulary.sqlite3",
        final_report_root / "vocabulary-quality.json",
        final_artifact_dir / "manifest.json",
    )
    _validate_output_collisions(
        (config.source, config.curated, config.source_registry),
        final_targets,
    )
    staging_parent = final_artifact_dir.parent
    staging_parent.mkdir(parents=True, exist_ok=True)
    staging_root = Path(tempfile.mkdtemp(prefix=f".{final_artifact_dir.name}.staging-", dir=staging_parent))
    backup_root = staging_root / ".backups"
    common_root = Path(os.path.commonpath((final_artifact_dir, final_report_root)))
    staged_artifact_dir = staging_root / final_artifact_dir.relative_to(common_root)
    staged_report_root = staging_root / final_report_root.relative_to(common_root)
    try:
        report = _build_staged(
            config,
            staged_artifact_dir=staged_artifact_dir,
            staged_report_root=staged_report_root,
            final_artifact_dir=final_artifact_dir,
            final_report_root=final_report_root,
        )
        from tools.vocabulary.verify_vocabulary import verify

        verification = verify(
            staged_artifact_dir,
            curated=config.curated,
            source_registry=config.source_registry,
            report_root=staged_report_root,
            manifest_path_base=final_artifact_dir,
            minimum_total=config.soft_min,
        )
        if not verification.passed:
            raise RuntimeError(f"staged vocabulary verification failed: {verification}")
        _promote_files(
            (
                (staged_artifact_dir / "vocabulary.csv", final_targets[0]),
                (staged_artifact_dir / "vocabulary.sqlite3", final_targets[1]),
                (staged_report_root / "vocabulary-quality.json", final_targets[2]),
                (staged_artifact_dir / "manifest.json", final_targets[3]),
            ),
            backup_root,
        )
        return report
    finally:
        shutil.rmtree(staging_root, ignore_errors=True)


def build(config: BuildConfig) -> BuildReport:
    return _execute_build(config, final_report_root=config.output / "reports")


def build_canonical(
    *,
    source: Path,
    curated: Path,
    source_registry: Path,
    canonical_root: Path,
    soft_min: int = 10_000,
    soft_max: int = 12_000,
) -> BuildReport:
    root = canonical_root.resolve(strict=False)
    return _execute_build(
        BuildConfig(
            source=source,
            curated=curated,
            source_registry=source_registry,
            output=root / "data" / "ielts",
            soft_min=soft_min,
            soft_max=soft_max,
        ),
        final_report_root=root / "data" / "reports",
    )


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build the WordFlow quality-gated vocabulary corpus")
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--curated", type=Path, required=True)
    parser.add_argument("--source-registry", type=Path, required=True)
    destination = parser.add_mutually_exclusive_group(required=True)
    destination.add_argument("--output", type=Path)
    destination.add_argument("--canonical-root", type=Path)
    parser.add_argument("--soft-min", type=int, default=10_000)
    parser.add_argument("--soft-max", type=int, default=12_000)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    if args.canonical_root:
        report = build_canonical(
            source=args.source,
            curated=args.curated,
            source_registry=args.source_registry,
            canonical_root=args.canonical_root,
            soft_min=args.soft_min,
            soft_max=args.soft_max,
        )
    else:
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
