from __future__ import annotations

import csv
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
import zipfile

from tools.vocabulary.models import stable_word_id
from tools.vocabulary.io_artifacts import sha256
from tools.vocabulary.confusable import generate_candidates


VOCABULARY_WORDS = (
    "carnivorous",
    "degrade",
    "degenerate",
    "herbivorous",
    "stimulate",
    "stimulant",
)


def write_vocabulary(path: Path) -> None:
    connection = sqlite3.connect(path)
    connection.execute(
        "CREATE TABLE vocabulary (stable_id TEXT PRIMARY KEY, word TEXT NOT NULL COLLATE NOCASE UNIQUE, "
        "phonetic TEXT NOT NULL, translation_zh_cn TEXT NOT NULL)"
    )
    connection.executemany(
        "INSERT INTO vocabulary VALUES (?, ?, ?, ?)",
        ((stable_word_id(word), word, "", f"{word}中文") for word in VOCABULARY_WORDS),
    )
    connection.commit()
    connection.close()


def write_oewn(path: Path) -> None:
    entries = {
        "degrade": {
            "v": {
                "sense": [
                    {"id": "degrade%2:30:00::", "synset": "001-v", "antonym": ["upgrade%2:30:00::"]}
                ]
            }
        },
        "degenerate": {
            "v": {"sense": [{"id": "degenerate%2:30:00::", "synset": "001-v"}]},
            "n": {"sense": [{"id": "degenerate%1:18:00::", "synset": "002-n"}]},
        },
        "stimulate": {
            "v": {
                "sense": [
                    {"id": "stimulate%2:32:00::", "synset": "003-v", "derivation": ["stimulation%1:04:00::"]}
                ]
            }
        },
        "upgrade": {"v": {"sense": [{"id": "upgrade%2:30:00::", "synset": "004-v"}]}},
        "stimulation": {"n": {"sense": [{"id": "stimulation%1:04:00::", "synset": "005-n"}]}},
    }
    synsets = {
        "001-v": {"members": ["degrade", "degenerate"], "partOfSpeech": "v", "definition": ["decline"]},
        "002-n": {"members": ["degenerate"], "partOfSpeech": "n", "definition": ["a person"]},
        "003-v": {"members": ["stimulate"], "partOfSpeech": "v", "definition": ["encourage"]},
        "004-v": {"members": ["upgrade"], "partOfSpeech": "v", "definition": ["improve"]},
        "005-n": {"members": ["stimulation"], "partOfSpeech": "n", "definition": ["encouragement"]},
    }
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("entries-d.json", json.dumps(entries, sort_keys=True))
        archive.writestr("verb.change.json", json.dumps(synsets, sort_keys=True))


def write_curated(path: Path) -> None:
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream, lineterminator="\n")
        writer.writerow(
            ("group_id", "word", "relation_kind", "contrast_zh_cn", "collocation", "source_key", "review_state")
        )
        writer.writerows(
            (
                ("g01", "degenerate", "spelling_similar", "退化；勿与降低混淆", "degenerate into", "curated_relations", "reviewed"),
                ("g01", "degrade", "spelling_similar", "降低；勿与退化混淆", "degrade quality", "curated_relations", "reviewed"),
                ("g02", "herbivorous", "antonym_confusable", "食草的", "herbivorous animals", "curated_relations", "reviewed"),
                ("g02", "carnivorous", "antonym_confusable", "食肉的", "carnivorous plants", "curated_relations", "reviewed"),
            )
        )


def write_misspellings(path: Path) -> None:
    path.write_text(
        "misspelling,target,contrast_zh_cn,source_key,review_state\n"
        "stimuate,stimulate,常见漏写 l 的误拼,curated_relations,reviewed\n",
        encoding="utf-8",
    )


class RelationBuilderTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        root = Path(self.temp.name)
        self.vocabulary = root / "vocabulary.sqlite3"
        self.oewn = root / "oewn.zip"
        self.curated = root / "groups.csv"
        self.misspellings = root / "misspellings.csv"
        self.output = root / "relations.sqlite3"
        write_vocabulary(self.vocabulary)
        write_oewn(self.oewn)
        write_curated(self.curated)
        write_misspellings(self.misspellings)

    def tearDown(self) -> None:
        self.temp.cleanup()

    def build(self):
        from tools.vocabulary.build_relations import RelationBuildConfig, build

        return build(
            RelationBuildConfig(
                vocabulary=self.vocabulary,
                oewn=self.oewn,
                curated=self.curated,
                misspellings=self.misspellings,
                output=self.output,
                report=self.output.with_suffix(".quality.json"),
                manifest=self.output.with_suffix(".manifest.json"),
                expected_oewn_sha256=sha256(self.oewn),
            )
        )

    def test_synonyms_are_sense_and_pos_bound(self) -> None:
        report = self.build()
        connection = sqlite3.connect(self.output)
        rows = connection.execute(
            "SELECT source_sense_id, target_sense_id, pos FROM word_relation WHERE kind='synonym'"
        ).fetchall()
        connection.close()
        self.assertTrue(rows)
        self.assertTrue(all(source and target and pos == "v" for source, target, pos in rows))
        self.assertGreater(report.filtered_count, 0)

    def test_reviewed_relations_publish_but_candidates_do_not(self) -> None:
        self.build()
        connection = sqlite3.connect(self.output)
        curated = connection.execute(
            "SELECT review_state, published FROM word_relation WHERE evidence_source='curated_relations'"
        ).fetchall()
        candidates = connection.execute(
            "SELECT review_state, published FROM word_relation WHERE review_state='candidate'"
        ).fetchall()
        connection.close()
        self.assertTrue(curated)
        self.assertTrue(candidates)
        self.assertTrue(all(row == ("reviewed", 1) for row in curated))
        self.assertTrue(all(row == ("candidate", 0) for row in candidates))

    def test_misspelling_is_directional_and_not_a_headword(self) -> None:
        self.build()
        connection = sqlite3.connect(self.output)
        row = connection.execute(
            "SELECT source_spelling, target_entry_id, direction, kind FROM word_relation WHERE kind='misspelling'"
        ).fetchone()
        connection.close()
        self.assertEqual(row, ("stimuate", stable_word_id("stimulate"), "forward", "misspelling"))
        vocabulary = sqlite3.connect(self.vocabulary)
        self.assertIsNone(vocabulary.execute("SELECT 1 FROM vocabulary WHERE word='stimuate'").fetchone())
        vocabulary.close()

    def test_curated_groups_are_bidirectionally_reachable_and_classified(self) -> None:
        self.build()
        connection = sqlite3.connect(self.output)
        pairs = set(
            connection.execute(
                "SELECT source_entry_id, target_entry_id, kind FROM word_relation WHERE evidence_source='curated_relations'"
            ).fetchall()
        )
        connection.close()
        herbivore = stable_word_id("herbivorous")
        carnivore = stable_word_id("carnivorous")
        self.assertIn((herbivore, carnivore, "antonym_confusable"), pairs)
        self.assertIn((carnivore, herbivore, "antonym_confusable"), pairs)

    def test_build_is_byte_deterministic_and_verifiable(self) -> None:
        from tools.vocabulary.build_relations import RelationBuildConfig, build
        from tools.vocabulary.build_relations import verify_relations

        first = self.build()
        second_output = self.output.with_name("relations-second.sqlite3")
        second = build(
            RelationBuildConfig(
                vocabulary=self.vocabulary,
                oewn=self.oewn,
                curated=self.curated,
                misspellings=self.misspellings,
                output=second_output,
                report=second_output.with_suffix(".quality.json"),
                manifest=second_output.with_suffix(".manifest.json"),
                expected_oewn_sha256=sha256(self.oewn),
            )
        )
        self.assertEqual(first.sqlite_sha256, second.sqlite_sha256)
        verification = verify_relations(
            self.output,
            vocabulary=self.vocabulary,
            curated=self.curated,
            misspellings=self.misspellings,
        )
        self.assertTrue(verification.passed)
        self.assertEqual(verification.sqlite_integrity, "ok")

    def test_oewn_hash_must_match_before_parsing(self) -> None:
        from tools.vocabulary.build_relations import RelationBuildConfig, build

        with self.assertRaisesRegex(ValueError, "OEWN hash mismatch"):
            build(
                RelationBuildConfig(
                    vocabulary=self.vocabulary,
                    oewn=self.oewn,
                    curated=self.curated,
                    misspellings=self.misspellings,
                    output=self.output,
                    report=self.output.with_suffix(".quality.json"),
                    manifest=self.output.with_suffix(".manifest.json"),
                    expected_oewn_sha256="0" * 64,
                )
            )
        self.assertFalse(self.output.exists())

    def test_manifest_and_quality_hashes_are_independently_verified(self) -> None:
        from tools.vocabulary.build_relations import verify_relation_artifacts

        self.build()
        quality = self.output.with_suffix(".quality.json")
        manifest = self.output.with_suffix(".manifest.json")
        report = verify_relation_artifacts(
            self.output,
            vocabulary=self.vocabulary,
            curated=self.curated,
            misspellings=self.misspellings,
            quality=quality,
            manifest=manifest,
        )
        self.assertTrue(report.passed)
        quality.write_text("{}\n", encoding="utf-8")
        tampered = verify_relation_artifacts(
            self.output,
            vocabulary=self.vocabulary,
            curated=self.curated,
            misspellings=self.misspellings,
            quality=quality,
            manifest=manifest,
        )
        self.assertFalse(tampered.passed)

    def test_candidate_layer_is_conservative_and_bounded(self) -> None:
        words = [f"pre{letter}ation" for letter in "abcdefghijkl"]
        candidates = generate_candidates(words)
        self.assertLessEqual(len(candidates), len(words) * 4)
        pairs = {(candidate.source_word, candidate.target_word) for candidate in candidates}
        self.assertTrue(all((target, source) in pairs for source, target in pairs))


if __name__ == "__main__":
    unittest.main()
