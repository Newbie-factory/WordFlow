from __future__ import annotations

import argparse
from contextlib import closing
import csv
from dataclasses import asdict, dataclass
import json
import os
from pathlib import Path, PurePosixPath
import sqlite3
import sys
import tempfile
import uuid
import zipfile

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from tools.vocabulary.confusable import generate_candidates
from tools.vocabulary.io_artifacts import sha256, write_json
from tools.vocabulary.models import stable_word_id
from tools.vocabulary.normalization import normalize_word


BUILD_VERSION = "wordflow-relations-v1"
OEWN_EDITION = "2025"
RELATION_NAMESPACE = uuid.UUID("715948b4-5b8f-5f33-b82c-4e8f82db65ca")
CURATED_FIELDS = (
    "group_id", "word", "relation_kind", "contrast_zh_cn", "collocation", "source_key", "review_state"
)
MISSPELLING_FIELDS = ("misspelling", "target", "contrast_zh_cn", "source_key", "review_state")
RELATION_KINDS = frozenset(
    {
        "synonym", "antonym", "derivational", "spelling_similar", "pronunciation_similar",
        "root_confusable", "topic_confusable", "antonym_confusable", "personal_confusable", "misspelling",
    }
)
RELATION_COLUMNS = (
    "relation_id", "source_entry_id", "source_spelling", "target_entry_id", "source_sense_id",
    "target_sense_id", "pos", "kind", "direction", "evidence_source", "evidence_detail", "score",
    "review_state", "published", "contrast_zh_cn", "collocation", "build_version",
)


@dataclass(frozen=True, slots=True)
class RelationBuildConfig:
    vocabulary: Path
    oewn: Path
    curated: Path
    misspellings: Path
    output: Path
    report: Path
    manifest: Path
    expected_oewn_sha256: str


@dataclass(frozen=True, slots=True)
class RelationBuildReport:
    sense_count: int
    relation_count: int
    published_count: int
    candidate_count: int
    reviewed_count: int
    filtered_count: int
    sqlite_sha256: str
    quality_sha256: str
    manifest_sha256: str


@dataclass(frozen=True, slots=True)
class RelationVerificationReport:
    passed: bool
    sqlite_integrity: str
    sense_count: int
    relation_count: int
    published_count: int
    candidate_count: int
    self_links: int
    duplicate_relations: int
    missing_targets: int
    unbound_published_synonyms: int
    published_candidates: int
    missing_reverse_curated: int
    missing_misspellings: int


@dataclass(frozen=True, slots=True)
class RelationArtifactVerification:
    passed: bool
    relation_verification: RelationVerificationReport
    sqlite_sha_matches_manifest: bool
    quality_sha_matches_manifest: bool
    vocabulary_sha_matches_manifest: bool
    curated_sha_matches_manifest: bool
    misspellings_sha_matches_manifest: bool
    oewn_hash_agrees_across_manifest_and_quality: bool


def _read_csv(path: Path, fields: tuple[str, ...]) -> list[dict[str, str]]:
    with path.open("r", encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if tuple(reader.fieldnames or ()) != fields:
            raise ValueError(f"{path.name} must have exact header {fields}")
        rows = []
        for number, raw in enumerate(reader, 2):
            if None in raw:
                raise ValueError(f"{path.name} row {number} has extra columns")
            row = {key: (value or "").strip() for key, value in raw.items()}
            if any(not row[field] for field in fields):
                raise ValueError(f"{path.name} row {number} contains blank fields")
            rows.append(row)
    if not rows:
        raise ValueError(f"{path.name} has no data rows")
    return rows


def load_curated_groups(path: Path) -> dict[str, tuple[dict[str, str], ...]]:
    rows = _read_csv(path, CURATED_FIELDS)
    groups: dict[str, list[dict[str, str]]] = {}
    seen: set[tuple[str, str]] = set()
    for row in rows:
        word = normalize_word(row["word"])
        key = (row["group_id"], word)
        if key in seen:
            raise ValueError(f"duplicate curated group member: {key}")
        seen.add(key)
        if row["relation_kind"] not in RELATION_KINDS - {"synonym", "antonym", "derivational", "misspelling"}:
            raise ValueError(f"unsupported curated relation kind: {row['relation_kind']}")
        if row["review_state"] != "reviewed":
            raise ValueError("curated relations must be reviewed")
        row["word"] = word
        groups.setdefault(row["group_id"], []).append(row)
    if any(len(members) < 2 for members in groups.values()):
        raise ValueError("every curated group requires at least two words")
    return {key: tuple(value) for key, value in sorted(groups.items())}


def load_misspellings(path: Path) -> tuple[dict[str, str], ...]:
    rows = _read_csv(path, MISSPELLING_FIELDS)
    seen: set[str] = set()
    for row in rows:
        row["misspelling"] = normalize_word(row["misspelling"])
        row["target"] = normalize_word(row["target"])
        if row["misspelling"] == row["target"] or row["misspelling"] in seen:
            raise ValueError("misspellings must be unique directional corrections")
        if row["review_state"] != "reviewed":
            raise ValueError("misspellings must be reviewed")
        seen.add(row["misspelling"])
    return tuple(rows)


def _validate_archive(path: Path, expected_sha256: str) -> zipfile.ZipFile:
    actual = sha256(path)
    if actual.casefold() != expected_sha256.strip().casefold():
        raise ValueError(f"OEWN hash mismatch: expected {expected_sha256.upper()}, got {actual}")
    archive = zipfile.ZipFile(path)
    if archive.testzip() is not None:
        archive.close()
        raise ValueError("OEWN ZIP integrity check failed")
    names = archive.namelist()
    if not names or not any(name.startswith("entries-") and name.endswith(".json") for name in names):
        archive.close()
        raise ValueError("OEWN archive has no entry JSON members")
    seen: set[str] = set()
    for name in names:
        pure = PurePosixPath(name)
        if pure.is_absolute() or ".." in pure.parts or len(pure.parts) != 1 or name in seen:
            archive.close()
            raise ValueError(f"unsafe or colliding OEWN member: {name}")
        seen.add(name)
    return archive


def _load_vocabulary(path: Path) -> tuple[dict[str, str], dict[str, tuple[str, str]]]:
    with closing(sqlite3.connect(path)) as connection:
        if connection.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
            raise ValueError("vocabulary SQLite integrity check failed")
        columns = {row[1] for row in connection.execute("PRAGMA table_info(vocabulary)")}
        required = {"stable_id", "word", "phonetic", "translation_zh_cn"}
        if not required <= columns:
            raise ValueError(f"vocabulary table misses columns: {sorted(required - columns)}")
        rows = connection.execute("SELECT stable_id, word, phonetic, translation_zh_cn FROM vocabulary").fetchall()
    by_word = {normalize_word(word): stable_id for stable_id, word, _, _ in rows}
    details = {stable_id: (phonetic or "", translation or "") for stable_id, _, phonetic, translation in rows}
    if len(by_word) != len(rows) or any(stable_id != stable_word_id(word) for word, stable_id in by_word.items()):
        raise ValueError("vocabulary entries must be unique and use stable UUIDv5 IDs")
    return by_word, details


def _relation_id(values: tuple[object, ...]) -> str:
    return str(uuid.uuid5(RELATION_NAMESPACE, "\x1f".join("" if v is None else str(v) for v in values)))


def _row(*, source_entry_id: str | None, source_spelling: str, target_entry_id: str,
         source_sense_id: str | None, target_sense_id: str | None, pos: str | None,
         kind: str, direction: str, evidence_source: str, evidence_detail: str,
         score: float, review_state: str, published: int, contrast: str = "", collocation: str = "") -> tuple[object, ...]:
    identity = (source_entry_id, source_spelling, target_entry_id, source_sense_id, target_sense_id, pos, kind,
                direction, evidence_source, review_state, contrast, collocation, BUILD_VERSION)
    return (_relation_id(identity), source_entry_id, source_spelling, target_entry_id, source_sense_id,
            target_sense_id, pos, kind, direction, evidence_source, evidence_detail, score, review_state,
            published, contrast, collocation, BUILD_VERSION)


def build_wordnet_relations(archive: zipfile.ZipFile, vocabulary: dict[str, str]):
    senses: dict[str, tuple[str, str, str, str]] = {}
    sense_rows: list[tuple[str, str, str, str, str, str, str]] = []
    for name in sorted(n for n in archive.namelist() if n.startswith("entries-") and n.endswith(".json")):
        entries = json.loads(archive.read(name))
        for lemma, by_pos in entries.items():
            word = normalize_word(lemma.replace("_", " "))
            entry_id = vocabulary.get(word)
            for pos, record in by_pos.items():
                for sense in record.get("sense", ()):
                    sense_id = sense["id"]
                    senses[sense_id] = (word, entry_id or "", pos, sense["synset"])
                    if entry_id:
                        sense_rows.append((sense_id, entry_id, pos, sense["synset"], "", "oewn_2025", BUILD_VERSION))
    synset_data: dict[str, dict[str, object]] = {}
    for name in sorted(n for n in archive.namelist() if not n.startswith("entries-") and n.endswith(".json") and n != "frames.json"):
        synset_data.update(json.loads(archive.read(name)))
    sense_rows = [
        (sense_id, entry_id, pos, synset_id,
         " | ".join(str(x) for x in synset_data.get(synset_id, {}).get("definition", ())), source, version)
        for sense_id, entry_id, pos, synset_id, _, source, version in sense_rows
    ]
    relations: list[tuple[object, ...]] = []
    by_synset: dict[tuple[str, str], list[tuple[str, str]]] = {}
    for sense_id, (word, entry_id, pos, synset_id) in senses.items():
        if entry_id:
            by_synset.setdefault((synset_id, pos), []).append((sense_id, entry_id))
    for (synset_id, pos), members in sorted(by_synset.items()):
        for source_sense, source_entry in sorted(members):
            for target_sense, target_entry in sorted(members):
                if source_entry == target_entry:
                    continue
                relations.append(_row(source_entry_id=source_entry, source_spelling="", target_entry_id=target_entry,
                    source_sense_id=source_sense, target_sense_id=target_sense, pos=pos, kind="synonym",
                    direction="bidirectional", evidence_source="oewn_2025", evidence_detail=f"shared_synset:{synset_id}",
                    score=1.0, review_state="verified", published=1))
    entry_records: list[dict[str, object]] = []
    for name in sorted(n for n in archive.namelist() if n.startswith("entries-") and n.endswith(".json")):
        entries = json.loads(archive.read(name))
        for by_pos in entries.values():
            for record in by_pos.values():
                entry_records.extend(record.get("sense", ()))
    for sense in entry_records:
        source = senses.get(str(sense["id"]))
        if not source or not source[1]:
            continue
        for field, kind in (("antonym", "antonym"), ("derivation", "derivational")):
            for target_sense_id in sense.get(field, ()):
                target = senses.get(target_sense_id)
                if not target or not target[1] or target[1] == source[1]:
                    continue
                relations.append(_row(source_entry_id=source[1], source_spelling="", target_entry_id=target[1],
                    source_sense_id=str(sense["id"]), target_sense_id=target_sense_id,
                    pos=source[2], kind=kind, direction="forward", evidence_source="oewn_2025",
                    evidence_detail=f"sense_relation:{field}", score=1.0, review_state="verified", published=1))
    filtered_count = sum(not entry_id for _, entry_id, _, _ in senses.values())
    return sorted(set(sense_rows)), relations, filtered_count


def _write_sqlite(path: Path, senses, relations) -> None:
    connection = sqlite3.connect(path)
    try:
        connection.executescript("""
            PRAGMA page_size=4096;
            PRAGMA journal_mode=DELETE;
            CREATE TABLE lexical_sense (
              sense_id TEXT PRIMARY KEY, entry_id TEXT NOT NULL, pos TEXT NOT NULL, synset_id TEXT NOT NULL,
              definition_en TEXT NOT NULL, evidence_source TEXT NOT NULL, build_version TEXT NOT NULL);
            CREATE INDEX idx_lexical_sense_entry_pos ON lexical_sense(entry_id, pos);
            CREATE TABLE word_relation (
              relation_id TEXT PRIMARY KEY, source_entry_id TEXT, source_spelling TEXT NOT NULL,
              target_entry_id TEXT NOT NULL, source_sense_id TEXT, target_sense_id TEXT, pos TEXT,
              kind TEXT NOT NULL, direction TEXT NOT NULL, evidence_source TEXT NOT NULL,
              evidence_detail TEXT NOT NULL, score REAL NOT NULL, review_state TEXT NOT NULL,
              published INTEGER NOT NULL CHECK(published IN (0,1)), contrast_zh_cn TEXT NOT NULL,
              collocation TEXT NOT NULL, build_version TEXT NOT NULL,
              CHECK(source_entry_id IS NOT NULL OR kind='misspelling'),
              CHECK(source_entry_id IS NULL OR source_entry_id<>target_entry_id));
            CREATE UNIQUE INDEX uq_word_relation_semantic ON word_relation(
              ifnull(source_entry_id,''), source_spelling, target_entry_id, ifnull(source_sense_id,''),
              ifnull(target_sense_id,''), kind, evidence_source, review_state);
            CREATE INDEX idx_word_relation_source ON word_relation(source_entry_id, published, kind);
            CREATE INDEX idx_word_relation_target ON word_relation(target_entry_id, published, kind);
            CREATE INDEX idx_word_relation_source_spelling ON word_relation(source_spelling, kind);
            CREATE TABLE build_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """)
        connection.executemany("INSERT INTO lexical_sense VALUES (?,?,?,?,?,?,?)", sorted(senses))
        connection.executemany(f"INSERT INTO word_relation ({','.join(RELATION_COLUMNS)}) VALUES ({','.join('?' for _ in RELATION_COLUMNS)})", sorted(set(relations)))
        connection.executemany("INSERT INTO build_metadata VALUES (?,?)", (
            ("build_version", BUILD_VERSION), ("oewn_edition", OEWN_EDITION),
            ("oewn_license", "CC BY 4.0 + underlying Princeton WordNet license"),
        ))
        connection.commit()
        connection.execute("VACUUM")
    finally:
        connection.close()


def verify_relations(path: Path, *, vocabulary: Path, curated: Path, misspellings: Path) -> RelationVerificationReport:
    vocabulary_words, _ = _load_vocabulary(vocabulary)
    valid_ids = set(vocabulary_words.values())
    groups = load_curated_groups(curated)
    corrections = load_misspellings(misspellings)
    with closing(sqlite3.connect(path)) as connection:
        integrity = connection.execute("PRAGMA integrity_check").fetchone()[0]
        sense_count = connection.execute("SELECT count(*) FROM lexical_sense").fetchone()[0]
        relation_count = connection.execute("SELECT count(*) FROM word_relation").fetchone()[0]
        published = connection.execute("SELECT count(*) FROM word_relation WHERE published=1").fetchone()[0]
        candidates = connection.execute("SELECT count(*) FROM word_relation WHERE review_state='candidate'").fetchone()[0]
        self_links = connection.execute("SELECT count(*) FROM word_relation WHERE source_entry_id=target_entry_id").fetchone()[0]
        duplicate_relations = connection.execute("""SELECT count(*) FROM (
          SELECT ifnull(source_entry_id,''),source_spelling,target_entry_id,ifnull(source_sense_id,''),
          ifnull(target_sense_id,''),kind,evidence_source,review_state,count(*) c FROM word_relation
          GROUP BY 1,2,3,4,5,6,7,8 HAVING c>1)""").fetchone()[0]
        targets = {row[0] for row in connection.execute("SELECT DISTINCT target_entry_id FROM word_relation")}
        sources = {row[0] for row in connection.execute("SELECT DISTINCT source_entry_id FROM word_relation WHERE source_entry_id IS NOT NULL")}
        missing_targets = len((targets | sources) - valid_ids)
        unbound = connection.execute("""SELECT count(*) FROM word_relation WHERE kind='synonym' AND published=1
          AND (source_sense_id IS NULL OR target_sense_id IS NULL OR pos IS NULL)""").fetchone()[0]
        published_candidates = connection.execute("SELECT count(*) FROM word_relation WHERE review_state='candidate' AND published=1").fetchone()[0]
        curated_pairs = set(connection.execute("SELECT source_entry_id,target_entry_id,kind FROM word_relation WHERE evidence_source='curated_relations' AND published=1"))
        missing_reverse = 0
        for members in groups.values():
            for source in members:
                for target in members:
                    if source is target:
                        continue
                    expected = (vocabulary_words[source["word"]], vocabulary_words[target["word"]], source["relation_kind"])
                    missing_reverse += expected not in curated_pairs
        actual_misspellings = set(connection.execute("SELECT source_spelling,target_entry_id FROM word_relation WHERE kind='misspelling' AND published=1"))
        missing_misspellings = sum((row["misspelling"], vocabulary_words[row["target"]]) not in actual_misspellings for row in corrections)
    values = dict(sqlite_integrity=integrity, sense_count=sense_count, relation_count=relation_count,
        published_count=published, candidate_count=candidates, self_links=self_links,
        duplicate_relations=duplicate_relations, missing_targets=missing_targets,
        unbound_published_synonyms=unbound, published_candidates=published_candidates,
        missing_reverse_curated=missing_reverse, missing_misspellings=missing_misspellings)
    return RelationVerificationReport(passed=integrity == "ok" and not any((self_links, duplicate_relations,
        missing_targets, unbound, published_candidates, missing_reverse, missing_misspellings)), **values)


def verify_relation_artifacts(path: Path, *, vocabulary: Path, curated: Path, misspellings: Path,
                              quality: Path, manifest: Path) -> RelationArtifactVerification:
    relation_verification = verify_relations(path, vocabulary=vocabulary, curated=curated, misspellings=misspellings)
    data = json.loads(manifest.read_text(encoding="utf-8"))
    quality_data = json.loads(quality.read_text(encoding="utf-8"))
    quality_inputs = quality_data.get("inputs", {}) if isinstance(quality_data, dict) else {}
    values = {
        "sqlite_sha_matches_manifest": sha256(path) == data["artifacts"]["relations.sqlite3"],
        "quality_sha_matches_manifest": sha256(quality) == data["artifacts"]["relations-quality.json"],
        "vocabulary_sha_matches_manifest": sha256(vocabulary) == data["inputs"]["vocabulary_sha256"],
        "curated_sha_matches_manifest": sha256(curated) == data["inputs"]["curated_sha256"],
        "misspellings_sha_matches_manifest": sha256(misspellings) == data["inputs"]["misspellings_sha256"],
        "oewn_hash_agrees_across_manifest_and_quality": (
            data["source"]["sha256"] == data["inputs"]["oewn_sha256"]
            and data["source"]["sha256"] == quality_inputs.get("oewn_sha256")
        ),
    }
    return RelationArtifactVerification(
        passed=relation_verification.passed and all(values.values()),
        relation_verification=relation_verification,
        **values,
    )


def build(config: RelationBuildConfig) -> RelationBuildReport:
    inputs = [config.vocabulary.resolve(strict=True), config.oewn.resolve(strict=True),
              config.curated.resolve(strict=True), config.misspellings.resolve(strict=True)]
    targets = [config.output.resolve(strict=False), config.report.resolve(strict=False), config.manifest.resolve(strict=False)]
    if len(set(targets)) != len(targets) or set(inputs) & set(targets):
        raise ValueError("relation inputs and outputs must not collide")
    for target in targets:
        target.parent.mkdir(parents=True, exist_ok=True)
    vocabulary, details = _load_vocabulary(inputs[0])
    groups = load_curated_groups(inputs[2])
    corrections = load_misspellings(inputs[3])
    missing = sorted({row["word"] for members in groups.values() for row in members} - set(vocabulary))
    missing += sorted({row["target"] for row in corrections} - set(vocabulary))
    illegal_heads = sorted({row["misspelling"] for row in corrections} & set(vocabulary))
    if missing or illegal_heads:
        raise ValueError(f"relation corpus closure failed; missing={missing}, illegal_headwords={illegal_heads}")
    archive = _validate_archive(inputs[1], config.expected_oewn_sha256)
    try:
        senses, relations, filtered_count = build_wordnet_relations(archive, vocabulary)
    finally:
        archive.close()
    reviewed_keys: set[tuple[str, str, str]] = set()
    for group_id, members in groups.items():
        for source in members:
            for target in members:
                if source is target:
                    continue
                source_id, target_id = vocabulary[source["word"]], vocabulary[target["word"]]
                reviewed_keys.add((source_id, target_id, source["relation_kind"]))
                relations.append(_row(source_entry_id=source_id, source_spelling="", target_entry_id=target_id,
                    source_sense_id=None, target_sense_id=None, pos=None, kind=source["relation_kind"],
                    direction="bidirectional", evidence_source=source["source_key"], evidence_detail=f"curated_group:{group_id}",
                    score=1.0, review_state="reviewed", published=1, contrast=source["contrast_zh_cn"], collocation=source["collocation"]))
    for correction in corrections:
        relations.append(_row(source_entry_id=None, source_spelling=correction["misspelling"],
            target_entry_id=vocabulary[correction["target"]], source_sense_id=None, target_sense_id=None, pos=None,
            kind="misspelling", direction="forward", evidence_source=correction["source_key"],
            evidence_detail="reviewed_common_misspelling", score=1.0, review_state="reviewed", published=1,
            contrast=correction["contrast_zh_cn"]))
    for candidate in generate_candidates(list(vocabulary)):
        source_id, target_id = vocabulary[candidate.source_word], vocabulary[candidate.target_word]
        if (source_id, target_id, candidate.kind) in reviewed_keys:
            continue
        relations.append(_row(source_entry_id=source_id, source_spelling="", target_entry_id=target_id,
            source_sense_id=None, target_sense_id=None, pos=None, kind=candidate.kind, direction="bidirectional",
            evidence_source="wordflow_spelling_v1", evidence_detail=candidate.evidence, score=candidate.score,
            review_state="candidate", published=0))
    with tempfile.TemporaryDirectory(prefix="wordflow-relations-", dir=config.output.parent) as temp_name:
        temp = Path(temp_name)
        staged_db, staged_report, staged_manifest = temp / "relations.sqlite3", temp / "quality.json", temp / "manifest.json"
        _write_sqlite(staged_db, senses, relations)
        verification = verify_relations(staged_db, vocabulary=inputs[0], curated=inputs[2], misspellings=inputs[3])
        if not verification.passed:
            raise ValueError(f"staged relation verification failed: {asdict(verification)}")
        with closing(sqlite3.connect(staged_db)) as connection:
            counts = dict(connection.execute("SELECT review_state,count(*) FROM word_relation GROUP BY review_state"))
            published = connection.execute("SELECT count(*) FROM word_relation WHERE published=1").fetchone()[0]
            missing_definitions = connection.execute("SELECT count(*) FROM lexical_sense WHERE definition_en='' ").fetchone()[0]
        quality = {"schema_version": 1, "build_version": BUILD_VERSION,
            "counts": {"lexical_senses": len(senses), "relations": len(set(relations)), "published": published,
                "candidates": counts.get("candidate", 0), "reviewed": counts.get("reviewed", 0),
                "verified_semantic": counts.get("verified", 0), "filtered": filtered_count,
                "missing_definitions": missing_definitions,
                "missing_phonetics": sum(not details[entry_id][0] for entry_id in details)},
            "fixed_groups": {"expected": len(groups), "bidirectionally_reachable": len(groups)},
            "verification": asdict(verification),
            "inputs": {"vocabulary_sha256": sha256(inputs[0]), "oewn_sha256": sha256(inputs[1]),
                "curated_sha256": sha256(inputs[2]), "misspellings_sha256": sha256(inputs[3])}}
        write_json(staged_report, quality)
        manifest = {"schema_version": 1, "build_version": BUILD_VERSION,
            "source": {"name": "Open English WordNet", "edition": OEWN_EDITION,
                "url": "https://en-word.net/downloads/english-wordnet-2025-json.zip",
                "license": "CC BY 4.0 + underlying Princeton WordNet license", "sha256": sha256(inputs[1])},
            "inputs": quality["inputs"], "artifacts": {"relations.sqlite3": sha256(staged_db),
                "relations-quality.json": sha256(staged_report)}}
        write_json(staged_manifest, manifest)
        for staged, final in ((staged_db, config.output), (staged_report, config.report), (staged_manifest, config.manifest)):
            os.replace(staged, final)
    return RelationBuildReport(sense_count=len(senses), relation_count=len(set(relations)), published_count=published,
        candidate_count=counts.get("candidate", 0), reviewed_count=counts.get("reviewed", 0), filtered_count=filtered_count,
        sqlite_sha256=sha256(config.output), quality_sha256=sha256(config.report), manifest_sha256=sha256(config.manifest))


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description="Build WordFlow's versioned offline lexical-relation database")
    parser.add_argument("--vocabulary", type=Path, required=True)
    parser.add_argument("--oewn", type=Path, required=True)
    parser.add_argument("--curated", type=Path, required=True)
    parser.add_argument("--misspellings", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--expected-oewn-sha256", required=True)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--manifest", type=Path)
    return parser.parse_args(argv)


def main(argv=None) -> int:
    args = parse_args(argv)
    report = build(RelationBuildConfig(args.vocabulary, args.oewn, args.curated, args.misspellings, args.output,
        args.report or args.output.with_name("relations-quality.json"),
        args.manifest or args.output.with_name("relations-manifest.json"), args.expected_oewn_sha256))
    print(json.dumps(asdict(report), indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
