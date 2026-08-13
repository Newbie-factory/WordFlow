from __future__ import annotations

import argparse
from collections import Counter
from contextlib import closing
import csv
from dataclasses import asdict, dataclass
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import sqlite3
import sys
import tempfile
import uuid
import zipfile

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from tools.vocabulary.confusable import CandidateGeneration, generate_candidates_with_metrics
from tools.vocabulary.io_artifacts import load_registry, sha256, write_json
from tools.vocabulary.models import stable_word_id
from tools.vocabulary.normalization import normalize_word


BUILD_VERSION = "wordflow-relations-v2"
OEWN_KEY = "oewn_2025"
OEWN_NAME = "Open English WordNet 2025 core JSON edition"
OEWN_URL = "https://en-word.net/downloads/english-wordnet-2025-json.zip"
OEWN_EDITION = "2025 core (without Namenet)"
OEWN_LICENSE = "CC BY 4.0 with underlying Princeton WordNet license; see data/licenses/OEWN-2025-LICENSE.md and data/licenses/WORDNET-LICENSE.txt"
OEWN_ROLE = "Sense, POS, synset, antonym and derivational evidence for published lexical relations"
OEWN_BYTES = 9_986_555
OEWN_SHA256 = "7D749F6E2C39E6970E4997839DCF6E42FD281F3C2FAE0171D2192BAE8CFA4B51"
RELATION_NAMESPACE = uuid.UUID("715948b4-5b8f-5f33-b82c-4e8f82db65ca")
CURATED_FIELDS = ("group_id", "word", "relation_kind", "contrast_zh_cn", "collocation", "source_key", "review_state")
MISSPELLING_FIELDS = ("misspelling", "target", "contrast_zh_cn", "source_key", "review_state")
SEMANTIC_KINDS = frozenset({"synonym", "antonym", "derivational"})
CURATED_KINDS = frozenset({"spelling_similar", "pronunciation_similar", "root_confusable", "topic_confusable", "antonym_confusable", "personal_confusable"})
PUBLISHED_KINDS = SEMANTIC_KINDS | CURATED_KINDS | {"misspelling"}
CANONICAL_POS = frozenset({"n", "v", "a", "r", "s"})
PUBLISHED_COLUMNS = ("relation_id", "source_entry_id", "source_spelling", "target_entry_id", "source_sense_id", "target_sense_id", "pos", "kind", "direction", "evidence_source", "evidence_detail", "score", "review_state", "contrast_zh_cn", "collocation", "build_version")
CANDIDATE_COLUMNS = ("candidate_id", "source_entry_id", "target_entry_id", "kind", "direction", "evidence_source", "evidence_detail", "score", "build_version")


def expected_metadata(policy: dict[str, object]) -> dict[str, str]:
    return {"build_version": BUILD_VERSION, "oewn_sha256": str(policy["sha256"]), "oewn_bytes": str(policy["bytes"]), "oewn_edition": OEWN_EDITION, "oewn_license": OEWN_LICENSE}


@dataclass(frozen=True, slots=True)
class RelationBuildConfig:
    vocabulary: Path
    oewn: Path
    curated: Path
    misspellings: Path
    output: Path
    report: Path
    manifest: Path
    source_registry: Path


@dataclass(frozen=True, slots=True)
class RelationBuildReport:
    sense_count: int
    relation_count: int
    published_count: int
    candidate_count: int
    reviewed_count: int
    out_of_corpus_sense_count: int
    sqlite_sha256: str
    quality_sha256: str
    manifest_sha256: str


@dataclass(frozen=True, slots=True)
class RelationVerificationReport:
    passed: bool
    errors: tuple[str, ...]
    sqlite_integrity: str = "error"
    sense_count: int = 0
    published_count: int = 0
    candidate_count: int = 0


@dataclass(frozen=True, slots=True)
class RelationArtifactVerification:
    passed: bool
    errors: tuple[str, ...]
    relation_verification: RelationVerificationReport | None = None


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
    groups: dict[str, list[dict[str, str]]] = {}
    seen: set[tuple[str, str]] = set()
    for row in _read_csv(path, CURATED_FIELDS):
        row["word"] = normalize_word(row["word"])
        key = (row["group_id"], row["word"])
        if key in seen or row["relation_kind"] not in CURATED_KINDS or row["review_state"] != "reviewed" or row["source_key"] != "curated_relations":
            raise ValueError(f"invalid reviewed curated group member: {key}")
        seen.add(key)
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
        if row["misspelling"] == row["target"] or row["misspelling"] in seen or row["review_state"] != "reviewed" or row["source_key"] != "curated_relations":
            raise ValueError("invalid reviewed directional misspelling")
        seen.add(row["misspelling"])
    return tuple(rows)


def load_oewn_policy(path: Path) -> dict[str, object]:
    registry = load_registry(path)
    if registry.get("schema_version") != 1:
        raise ValueError("source registry schema_version must be 1")
    source = registry["sources"].get(OEWN_KEY)
    if not isinstance(source, dict):
        raise ValueError("source registry must contain oewn_2025")
    expected = {"name": OEWN_NAME, "url": OEWN_URL, "license": OEWN_LICENSE, "role": OEWN_ROLE, "edition": OEWN_EDITION, "bytes": OEWN_BYTES, "sha256": OEWN_SHA256}
    mismatches = [key for key, value in expected.items() if source.get(key) != value]
    if mismatches:
        raise ValueError(f"repository OEWN policy mismatch: {mismatches}")
    if not isinstance(source.get("bytes"), int) or source["bytes"] <= 0:
        raise ValueError("repository OEWN bytes pin must be a positive integer")
    if not isinstance(source.get("sha256"), str) or len(source["sha256"]) != 64 or any(character not in "0123456789ABCDEF" for character in source["sha256"]):
        raise ValueError("repository OEWN SHA-256 pin must be uppercase hexadecimal")
    return source


def _validate_archive(path: Path, policy: dict[str, object]) -> zipfile.ZipFile:
    if path.stat().st_size != policy["bytes"]:
        raise ValueError(f"OEWN byte count mismatch: expected {policy['bytes']}, got {path.stat().st_size}")
    actual = sha256(path)
    if actual != policy["sha256"]:
        raise ValueError(f"OEWN hash mismatch: expected {policy['sha256']}, got {actual}")
    archive = zipfile.ZipFile(path)
    if archive.testzip() is not None:
        archive.close(); raise ValueError("OEWN ZIP integrity check failed")
    names = archive.namelist()
    if not any(name.startswith("entries-") and name.endswith(".json") for name in names):
        archive.close(); raise ValueError("OEWN archive has no entry JSON members")
    seen: set[str] = set()
    for name in names:
        pure = PurePosixPath(name)
        if pure.is_absolute() or ".." in pure.parts or len(pure.parts) != 1 or name in seen:
            archive.close(); raise ValueError(f"unsafe or colliding OEWN member: {name}")
        seen.add(name)
    return archive


def _load_vocabulary(path: Path) -> tuple[dict[str, str], dict[str, tuple[str, str]]]:
    with closing(sqlite3.connect(path)) as connection:
        if connection.execute("PRAGMA integrity_check").fetchone()[0] != "ok": raise ValueError("vocabulary integrity failed")
        columns = {row[1] for row in connection.execute("PRAGMA table_info(vocabulary)")}
        if not {"stable_id", "word", "phonetic", "translation_zh_cn"} <= columns: raise ValueError("vocabulary schema incomplete")
        rows = connection.execute("SELECT stable_id,word,phonetic,translation_zh_cn FROM vocabulary").fetchall()
    by_word = {normalize_word(word): stable_id for stable_id, word, _, _ in rows}
    details = {stable_id: (phonetic or "", translation or "") for stable_id, _, phonetic, translation in rows}
    if len(by_word) != len(rows) or any(stable_id != stable_word_id(word) for word, stable_id in by_word.items()): raise ValueError("invalid vocabulary stable IDs")
    return by_word, details


def _id(values: tuple[object, ...]) -> str:
    return str(uuid.uuid5(RELATION_NAMESPACE, "\x1f".join("" if value is None else str(value) for value in values)))


def _published_row(source_entry_id, source_spelling, target_entry_id, source_sense_id, target_sense_id, pos, kind, direction, evidence_source, evidence_detail, score, review_state, contrast="", collocation=""):
    values = (source_entry_id, source_spelling, target_entry_id, source_sense_id, target_sense_id, pos, kind, direction, evidence_source, evidence_detail, review_state, contrast, collocation, BUILD_VERSION)
    return (_id(values), source_entry_id, source_spelling, target_entry_id, source_sense_id, target_sense_id, pos, kind, direction, evidence_source, evidence_detail, score, review_state, contrast, collocation, BUILD_VERSION)


def _candidate_row(source_id, target_id, kind, direction, source, detail, score):
    values = (source_id, target_id, kind, direction, source, detail, BUILD_VERSION)
    return (_id(values), source_id, target_id, kind, direction, source, detail, score, BUILD_VERSION)


def parse_oewn(archive: zipfile.ZipFile, vocabulary: dict[str, str]):
    synsets: dict[str, dict[str, object]] = {}
    for name in sorted(n for n in archive.namelist() if not n.startswith("entries-") and n.endswith(".json") and n != "frames.json"):
        synsets.update(json.loads(archive.read(name)))
    senses: dict[str, tuple[str, str, str, str]] = {}
    raw_senses: list[dict[str, object]] = []
    out_of_corpus = 0
    pos_mismatches = 0
    for name in sorted(n for n in archive.namelist() if n.startswith("entries-") and n.endswith(".json")):
        for lemma, records in json.loads(archive.read(name)).items():
            word = normalize_word(lemma.replace("_", " "))
            for discriminator, record in records.items():
                base_pos = discriminator.split("-", 1)[0]
                for sense in record.get("sense", ()):
                    synset = synsets.get(sense["synset"])
                    if not synset: raise ValueError(f"missing OEWN synset {sense['synset']}")
                    pos = str(synset.get("partOfSpeech", ""))
                    if pos not in CANONICAL_POS or (base_pos != pos and not (base_pos == "a" and pos == "s")):
                        pos_mismatches += 1
                        continue
                    entry_id = vocabulary.get(word, "")
                    senses[sense["id"]] = (word, entry_id, pos, sense["synset"])
                    raw_senses.append(sense)
                    out_of_corpus += not bool(entry_id)
    sense_rows = sorted((sid, entry_id, pos, synset_id, " | ".join(map(str, synsets[synset_id].get("definition", ()))), "oewn_2025", BUILD_VERSION)
                        for sid, (_, entry_id, pos, synset_id) in senses.items() if entry_id)
    by_synset: dict[tuple[str, str], list[tuple[str, str]]] = {}
    for sid, (_, entry_id, pos, synset_id) in senses.items():
        if entry_id: by_synset.setdefault((synset_id, pos), []).append((sid, entry_id))
    relations = []
    for (synset_id, pos), members in sorted(by_synset.items()):
        for source_sense, source_entry in sorted(members):
            for target_sense, target_entry in sorted(members):
                if source_entry != target_entry:
                    relations.append(_published_row(source_entry, "", target_entry, source_sense, target_sense, pos, "synonym", "bidirectional", "oewn_2025", f"shared_synset:{synset_id}", 1.0, "verified"))
    semantic_filtered = Counter({"pos_mismatch": pos_mismatches, "out_of_corpus_sense": out_of_corpus, "missing_relation_target": 0, "self_relation": 0})
    for sense in raw_senses:
        source = senses.get(str(sense["id"]))
        if not source or not source[1]: continue
        for field, kind in (("antonym", "antonym"), ("derivation", "derivational")):
            for target_sid in sense.get(field, ()):
                target = senses.get(target_sid)
                if not target or not target[1]: semantic_filtered["missing_relation_target"] += 1; continue
                if target[1] == source[1]: semantic_filtered["self_relation"] += 1; continue
                relations.append(_published_row(source[1], "", target[1], str(sense["id"]), target_sid, source[2], kind, "forward", "oewn_2025", f"sense_relation:{field}", 1.0, "verified"))
    return sense_rows, sorted(set(relations)), semantic_filtered


SCHEMA_SCRIPT = """
PRAGMA page_size=4096; PRAGMA journal_mode=DELETE; PRAGMA foreign_keys=ON;
CREATE TABLE lexical_sense (sense_id TEXT PRIMARY KEY, entry_id TEXT NOT NULL, pos TEXT NOT NULL CHECK(pos IN ('n','v','a','r','s')), synset_id TEXT NOT NULL, definition_en TEXT NOT NULL, evidence_source TEXT NOT NULL CHECK(evidence_source='oewn_2025'), build_version TEXT NOT NULL CHECK(build_version='wordflow-relations-v2'));
CREATE INDEX idx_lexical_sense_entry_pos ON lexical_sense(entry_id,pos);
CREATE TABLE published_relation_base (relation_id TEXT PRIMARY KEY, source_entry_id TEXT, source_spelling TEXT NOT NULL, target_entry_id TEXT NOT NULL, source_sense_id TEXT, target_sense_id TEXT, pos TEXT, kind TEXT NOT NULL CHECK(kind IN ('synonym','antonym','derivational','spelling_similar','pronunciation_similar','root_confusable','topic_confusable','antonym_confusable','personal_confusable','misspelling')), direction TEXT NOT NULL CHECK(direction IN ('forward','bidirectional')), evidence_source TEXT NOT NULL CHECK(evidence_source IN ('oewn_2025','curated_relations')), evidence_detail TEXT NOT NULL, score REAL NOT NULL CHECK(score>=0 AND score<=1), review_state TEXT NOT NULL CHECK(review_state IN ('verified','reviewed')), contrast_zh_cn TEXT NOT NULL, collocation TEXT NOT NULL, build_version TEXT NOT NULL CHECK(build_version='wordflow-relations-v2'), CHECK(source_entry_id IS NULL OR source_entry_id<>target_entry_id), CHECK((kind IN ('synonym','antonym','derivational') AND evidence_source='oewn_2025' AND review_state='verified' AND source_entry_id IS NOT NULL AND source_spelling='' AND source_sense_id IS NOT NULL AND target_sense_id IS NOT NULL AND pos IN ('n','v','a','r','s')) OR (kind IN ('spelling_similar','pronunciation_similar','root_confusable','topic_confusable','antonym_confusable','personal_confusable') AND evidence_source='curated_relations' AND review_state='reviewed' AND source_entry_id IS NOT NULL AND source_spelling='' AND source_sense_id IS NULL AND target_sense_id IS NULL AND pos IS NULL) OR (kind='misspelling' AND evidence_source='curated_relations' AND review_state='reviewed' AND source_entry_id IS NULL AND source_spelling<>'' AND source_sense_id IS NULL AND target_sense_id IS NULL AND pos IS NULL AND direction='forward')));
CREATE UNIQUE INDEX uq_published_relation ON published_relation_base(ifnull(source_entry_id,''),source_spelling,target_entry_id,ifnull(source_sense_id,''),ifnull(target_sense_id,''),kind,evidence_source);
CREATE INDEX idx_published_source ON published_relation_base(source_entry_id,kind);
CREATE INDEX idx_published_target ON published_relation_base(target_entry_id,kind);
CREATE INDEX idx_published_spelling ON published_relation_base(source_spelling,kind);
CREATE VIEW published_word_relation AS SELECT * FROM published_relation_base;
CREATE TABLE algorithmic_candidate (candidate_id TEXT PRIMARY KEY, source_entry_id TEXT NOT NULL, target_entry_id TEXT NOT NULL, kind TEXT NOT NULL CHECK(kind='spelling_similar'), direction TEXT NOT NULL CHECK(direction='bidirectional'), evidence_source TEXT NOT NULL CHECK(evidence_source='wordflow_spelling_v1'), evidence_detail TEXT NOT NULL, score REAL NOT NULL CHECK(score>=0 AND score<1), build_version TEXT NOT NULL CHECK(build_version='wordflow-relations-v2'), CHECK(source_entry_id<>target_entry_id));
CREATE UNIQUE INDEX uq_algorithmic_candidate ON algorithmic_candidate(source_entry_id,target_entry_id,kind,evidence_source);
CREATE INDEX idx_candidate_source ON algorithmic_candidate(source_entry_id,kind);
CREATE INDEX idx_candidate_target ON algorithmic_candidate(target_entry_id,kind);
CREATE TABLE build_metadata (key TEXT PRIMARY KEY,value TEXT NOT NULL);
"""
EXPECTED_OBJECTS = {("table", "lexical_sense"), ("index", "idx_lexical_sense_entry_pos"), ("table", "published_relation_base"), ("index", "uq_published_relation"), ("index", "idx_published_source"), ("index", "idx_published_target"), ("index", "idx_published_spelling"), ("view", "published_word_relation"), ("table", "algorithmic_candidate"), ("index", "uq_algorithmic_candidate"), ("index", "idx_candidate_source"), ("index", "idx_candidate_target"), ("table", "build_metadata")}


def _schema_contract() -> dict[tuple[str, str], str]:
    with closing(sqlite3.connect(":memory:")) as connection:
        connection.executescript(SCHEMA_SCRIPT)
        return {(row[0], row[1]): row[2] for row in connection.execute("SELECT type,name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'")}


EXPECTED_SCHEMA = _schema_contract()


def _write_sqlite(path: Path, senses, published, candidates, metadata: dict[str, str]) -> None:
    with closing(sqlite3.connect(path)) as connection:
        connection.executescript(SCHEMA_SCRIPT)
        connection.executemany("INSERT INTO lexical_sense VALUES (?,?,?,?,?,?,?)", senses)
        connection.executemany(f"INSERT INTO published_relation_base ({','.join(PUBLISHED_COLUMNS)}) VALUES ({','.join('?' for _ in PUBLISHED_COLUMNS)})", published)
        connection.executemany(f"INSERT INTO algorithmic_candidate ({','.join(CANDIDATE_COLUMNS)}) VALUES ({','.join('?' for _ in CANDIDATE_COLUMNS)})", candidates)
        connection.executemany("INSERT INTO build_metadata VALUES (?,?)", sorted(metadata.items()))
        connection.commit(); connection.execute("VACUUM")


def _expected_inputs(vocabulary: Path, oewn: Path, curated: Path, misspellings: Path, registry: Path):
    words, details = _load_vocabulary(vocabulary)
    policy = load_oewn_policy(registry)
    archive = _validate_archive(oewn, policy)
    try: senses, semantic, semantic_filters = parse_oewn(archive, words)
    finally: archive.close()
    groups, corrections = load_curated_groups(curated), load_misspellings(misspellings)
    missing = sorted({row["word"] for members in groups.values() for row in members} - set(words)) + sorted({row["target"] for row in corrections} - set(words))
    illegal = sorted({row["misspelling"] for row in corrections} & set(words))
    if missing or illegal: raise ValueError(f"relation corpus closure failed; missing={missing}, illegal_headwords={illegal}")
    curated_rows = []
    reviewed_keys = set()
    for gid, members in groups.items():
        for source in members:
            for target in members:
                if source is target: continue
                sid, tid = words[source["word"]], words[target["word"]]
                reviewed_keys.add((sid, tid, source["relation_kind"]))
                curated_rows.append(_published_row(sid, "", tid, None, None, None, source["relation_kind"], "bidirectional", "curated_relations", f"curated_group:{gid}", 1.0, "reviewed", source["contrast_zh_cn"], source["collocation"]))
    misspelling_rows = [_published_row(None, row["misspelling"], words[row["target"]], None, None, None, "misspelling", "forward", "curated_relations", "reviewed_common_misspelling", 1.0, "reviewed", row["contrast_zh_cn"]) for row in corrections]
    generation = generate_candidates_with_metrics(list(words))
    candidates = [_candidate_row(words[item.source_word], words[item.target_word], item.kind, "bidirectional", "wordflow_spelling_v1", item.evidence, item.score) for item in generation.candidates if (words[item.source_word], words[item.target_word], item.kind) not in reviewed_keys]
    return words, details, groups, senses, sorted(set(semantic + curated_rows + misspelling_rows)), sorted(set(candidates)), semantic_filters, generation


def _quality_payload(details, groups, senses, published, candidates, semantic_filters, generation: CandidateGeneration, inputs):
    semantic_count = sum(row[9] == "oewn_2025" for row in published)
    reviewed_count = len(published) - semantic_count
    expected_reviewed = sum(len(m)*(len(m)-1) for m in groups.values()) + 2
    generated_pairs = {(candidate.source_word, candidate.target_word, candidate.kind) for candidate in generation.candidates}
    reviewed_pairs = {(row[1], row[3], row[7]) for row in published if row[9] == "curated_relations" and row[7] != "misspelling"}
    generated_id_pairs = {(stable_word_id(source), stable_word_id(target), kind) for source, target, kind in generated_pairs}
    confirmed = len(reviewed_pairs & generated_id_pairs)
    candidate_denominator = len(candidates) + confirmed
    candidate_metrics = dict(generation.metrics)
    candidate_metrics["reviewed_overlap_filtered_directed"] = len(generation.candidates) - len(candidates)
    return {"schema_version": 2, "build_version": BUILD_VERSION,
        "counts": {"lexical_senses": len(senses), "published_relations": len(published), "algorithmic_candidates": len(candidates), "verified_semantic": semantic_count, "reviewed_relations": reviewed_count, "missing_definitions": sum(not row[4] for row in senses), "missing_phonetics": sum(not value[0] for value in details.values()), "out_of_corpus_senses": semantic_filters["out_of_corpus_sense"]},
        "filter_reasons": {"semantic": dict(sorted(semantic_filters.items())), "candidate": dict(sorted(candidate_metrics.items()))},
        "coverage": {"reviewed_expected": expected_reviewed, "reviewed_present": reviewed_count, "reviewed_coverage_rate": reviewed_count/expected_reviewed, "candidate_confirmation_denominator": candidate_denominator, "candidate_confirmed": confirmed, "candidate_confirmation_rate": confirmed/candidate_denominator if candidate_denominator else 0.0},
        "fixed_groups": {"expected": len(groups), "bidirectionally_reachable": len(groups)}, "inputs": inputs}


def _schema(connection):
    return {(row[0], row[1]): row[2] for row in connection.execute("SELECT type,name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'")}


def verify_relations(path: Path, *, vocabulary: Path, curated: Path, misspellings: Path, oewn: Path, source_registry: Path) -> RelationVerificationReport:
    errors=[]; integrity="error"; sense_count=published_count=candidate_count=0
    try:
        _, _, _, expected_senses, expected_published, expected_candidates, _, _ = _expected_inputs(vocabulary,oewn,curated,misspellings,source_registry)
        with closing(sqlite3.connect(path)) as connection:
            integrity=connection.execute("PRAGMA integrity_check").fetchone()[0]
            if integrity!="ok": errors.append("sqlite_integrity")
            if _schema(connection)!=EXPECTED_SCHEMA: errors.append("schema_contract")
            actual_senses=connection.execute("SELECT sense_id,entry_id,pos,synset_id,definition_en,evidence_source,build_version FROM lexical_sense ORDER BY 1").fetchall()
            actual_published=connection.execute(f"SELECT {','.join(PUBLISHED_COLUMNS)} FROM published_word_relation ORDER BY 1").fetchall()
            actual_candidates=connection.execute(f"SELECT {','.join(CANDIDATE_COLUMNS)} FROM algorithmic_candidate ORDER BY 1").fetchall()
            metadata=dict(connection.execute("SELECT key,value FROM build_metadata"))
            sense_count,published_count,candidate_count=map(len,(actual_senses,actual_published,actual_candidates))
            if actual_senses!=expected_senses: errors.append("lexical_sense_exact_set")
            if actual_published!=expected_published: errors.append("published_relation_exact_set")
            if actual_candidates!=expected_candidates: errors.append("candidate_exact_set")
            policy=load_oewn_policy(source_registry)
            if metadata != expected_metadata(policy): errors.append("build_metadata")
    except Exception as error: errors.append(f"verification_error:{type(error).__name__}:{error}")
    return RelationVerificationReport(not errors,tuple(errors),integrity,sense_count,published_count,candidate_count)


def verify_relation_artifacts(path: Path, *, vocabulary: Path, curated: Path, misspellings: Path, quality: Path, manifest: Path, oewn: Path, source_registry: Path) -> RelationArtifactVerification:
    errors=[]; relation=None
    try:
        manifest_data=json.loads(manifest.read_text(encoding="utf-8")); quality_data=json.loads(quality.read_text(encoding="utf-8"))
        policy=load_oewn_policy(source_registry)
        relation=verify_relations(path,vocabulary=vocabulary,curated=curated,misspellings=misspellings,oewn=oewn,source_registry=source_registry)
        if not relation.passed: errors.extend(relation.errors)
        _,details,groups,senses,published,candidates,semantic_filters,generation=_expected_inputs(vocabulary,oewn,curated,misspellings,source_registry)
        inputs={"vocabulary_sha256":sha256(vocabulary),"oewn_sha256":sha256(oewn),"oewn_bytes":oewn.stat().st_size,"curated_sha256":sha256(curated),"misspellings_sha256":sha256(misspellings),"source_registry_sha256":sha256(source_registry)}
        expected_quality=_quality_payload(details,groups,senses,published,candidates,semantic_filters,generation,inputs)
        if quality_data!=expected_quality: errors.append("quality_exact_recomputation")
        if set(manifest_data) != {"schema_version","build_version","source","inputs","artifacts"} or manifest_data.get("schema_version")!=2 or manifest_data.get("build_version")!=BUILD_VERSION: errors.append("manifest_schema")
        if manifest_data.get("source")!={"key":OEWN_KEY,"name":OEWN_NAME,"url":OEWN_URL,"edition":OEWN_EDITION,"license":OEWN_LICENSE,"bytes":policy["bytes"],"sha256":policy["sha256"]}: errors.append("manifest_source_pin")
        if manifest_data.get("inputs")!=inputs: errors.append("manifest_inputs")
        artifacts=manifest_data.get("artifacts",{})
        if artifacts!={"relations.sqlite3":sha256(path),"relations-quality.json":sha256(quality)}: errors.append("manifest_artifact_hashes")
    except Exception as error: errors.append(f"artifact_error:{type(error).__name__}:{error}")
    return RelationArtifactVerification(not errors,tuple(errors),relation)


def _promote_files(staged_to_final, backup_root: Path, *, replace=os.replace):
    backups=[]; promoted=[]; backup_root.mkdir(parents=True,exist_ok=True)
    try:
        for index,(staged,final) in enumerate(staged_to_final):
            final.parent.mkdir(parents=True,exist_ok=True)
            if final.exists():
                backup=backup_root/f"{index:02d}-{final.name}"; replace(final,backup); backups.append((backup,final))
            replace(staged,final); promoted.append(final)
    except BaseException:
        for final in reversed(promoted):
            if final.exists(): final.unlink()
        for backup,final in reversed(backups):
            if backup.exists(): os.replace(backup,final)
        raise


def build(config: RelationBuildConfig) -> RelationBuildReport:
    config=RelationBuildConfig(*(Path(value).resolve(strict=index<4 or index==7) for index,value in enumerate((config.vocabulary,config.oewn,config.curated,config.misspellings,config.output,config.report,config.manifest,config.source_registry))))
    inputs=(config.vocabulary,config.oewn,config.curated,config.misspellings,config.source_registry); targets=(config.output,config.report,config.manifest)
    if len(set(targets))!=3 or set(inputs)&set(targets): raise ValueError("relation inputs and outputs must not collide")
    for target in targets: target.parent.mkdir(parents=True,exist_ok=True)
    policy=load_oewn_policy(config.source_registry)
    _,details,groups,senses,published,candidates,semantic_filters,generation=_expected_inputs(*inputs[:4],config.source_registry)
    input_hashes={"vocabulary_sha256":sha256(config.vocabulary),"oewn_sha256":sha256(config.oewn),"oewn_bytes":config.oewn.stat().st_size,"curated_sha256":sha256(config.curated),"misspellings_sha256":sha256(config.misspellings),"source_registry_sha256":sha256(config.source_registry)}
    staging_root=Path(tempfile.mkdtemp(prefix=".relations.staging-",dir=config.output.parent)); backup_root=staging_root/".backups"
    staged_db,staged_quality,staged_manifest=staging_root/"relations.sqlite3",staging_root/"relations-quality.json",staging_root/"relations-manifest.json"
    try:
        _write_sqlite(staged_db,senses,published,candidates,expected_metadata(policy))
        quality=_quality_payload(details,groups,senses,published,candidates,semantic_filters,generation,input_hashes); write_json(staged_quality,quality)
        manifest={"schema_version":2,"build_version":BUILD_VERSION,"source":{"key":OEWN_KEY,"name":OEWN_NAME,"url":OEWN_URL,"edition":OEWN_EDITION,"license":OEWN_LICENSE,"bytes":policy["bytes"],"sha256":policy["sha256"]},"inputs":input_hashes,"artifacts":{"relations.sqlite3":sha256(staged_db),"relations-quality.json":sha256(staged_quality)}}; write_json(staged_manifest,manifest)
        verification=verify_relation_artifacts(staged_db,vocabulary=config.vocabulary,curated=config.curated,misspellings=config.misspellings,quality=staged_quality,manifest=staged_manifest,oewn=config.oewn,source_registry=config.source_registry)
        if not verification.passed: raise RuntimeError(f"staged verification failed: {verification.errors}")
        _promote_files(((staged_db,config.output),(staged_quality,config.report),(staged_manifest,config.manifest)),backup_root)
    finally: shutil.rmtree(staging_root,ignore_errors=True)
    reviewed=sum(row[9]=="curated_relations" for row in published)
    return RelationBuildReport(len(senses),len(published)+len(candidates),len(published),len(candidates),reviewed,semantic_filters["out_of_corpus_sense"],sha256(config.output),sha256(config.report),sha256(config.manifest))


def parse_args(argv=None):
    parser=argparse.ArgumentParser(description="Build WordFlow's pinned offline lexical relations")
    for name in ("vocabulary","oewn","curated","misspellings","output","source-registry"): parser.add_argument(f"--{name}",type=Path,required=True)
    parser.add_argument("--report",type=Path); parser.add_argument("--manifest",type=Path)
    return parser.parse_args(argv)


def main(argv=None):
    args=parse_args(argv); report=build(RelationBuildConfig(args.vocabulary,args.oewn,args.curated,args.misspellings,args.output,args.report or args.output.with_name("relations-quality.json"),args.manifest or args.output.with_name("relations-manifest.json"),args.source_registry)); print(json.dumps(asdict(report),indent=2)); return 0


if __name__=="__main__": raise SystemExit(main())
