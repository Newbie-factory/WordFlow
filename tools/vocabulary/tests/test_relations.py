from __future__ import annotations

import csv
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest.mock import patch
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
    "accede",
    "bow",
    "able",
    "capable",
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
        "accede": {"v-1": {"sense": [{"id": "accede%2:32:00::", "synset": "006-v"}]}},
        "bow": {"v-2": {"sense": [{"id": "bow%2:32:00::", "synset": "006-v"}]}},
        "able": {"a": {"sense": [{"id": "able%5:00:00:competent:00", "synset": "007-s"}]}},
        "capable": {"s": {"sense": [{"id": "capable%5:00:00:competent:00", "synset": "007-s"}]}},
    }
    synsets = {
        "001-v": {"members": ["degrade", "degenerate"], "partOfSpeech": "v", "definition": ["decline"]},
        "002-n": {"members": ["degenerate"], "partOfSpeech": "n", "definition": ["a person"]},
        "003-v": {"members": ["stimulate"], "partOfSpeech": "v", "definition": ["encourage"]},
        "004-v": {"members": ["upgrade"], "partOfSpeech": "v", "definition": ["improve"]},
        "005-n": {"members": ["stimulation"], "partOfSpeech": "n", "definition": ["encouragement"]},
        "006-v": {"members": ["accede", "bow"], "partOfSpeech": "v", "definition": ["yield"]},
        "007-s": {"members": ["able", "capable"], "partOfSpeech": "s", "definition": ["competent"]},
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


def write_registry(path: Path, archive: Path) -> None:
    path.write_text(json.dumps({
        "schema_version": 1,
        "sources": {"oewn_2025": {
            "name": "Open English WordNet 2025 core JSON edition",
            "url": "https://en-word.net/downloads/english-wordnet-2025-json.zip",
            "license": "CC BY 4.0 with underlying Princeton WordNet license; see data/licenses/OEWN-2025-LICENSE.md and data/licenses/WORDNET-LICENSE.txt",
            "role": "Sense, POS, synset, antonym and derivational evidence for published lexical relations",
            "edition": "2025 core (without Namenet)",
            "bytes": archive.stat().st_size,
            "sha256": sha256(archive),
        }}
    }, indent=2) + "\n", encoding="utf-8")


def write_approval_policy(path: Path, curated: Path, misspellings: Path) -> None:
    path.write_text(json.dumps({
        "schema_version": 1,
        "policy_version": "test-reviewed-relations-v1",
        "curated_file_sha256": sha256(curated),
        "misspellings_file_sha256": sha256(misspellings),
        "groups": [
            {"group_id": "g01", "relation_kind": "spelling_similar", "members": ["degenerate", "degrade"]},
            {"group_id": "g02", "relation_kind": "antonym_confusable", "members": ["herbivorous", "carnivorous"]},
        ],
        "misspellings": [{"misspelling": "stimuate", "target": "stimulate"}],
    }, indent=2) + "\n", encoding="utf-8")


class RelationBuilderTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        root = Path(self.temp.name)
        self.vocabulary = root / "vocabulary.sqlite3"
        self.oewn = root / "oewn.zip"
        self.curated = root / "groups.csv"
        self.misspellings = root / "misspellings.csv"
        self.registry = root / "source_registry.json"
        self.approval_policy = root / "relation_approval_policy.json"
        self.output = root / "relations.sqlite3"
        write_vocabulary(self.vocabulary)
        write_oewn(self.oewn)
        write_curated(self.curated)
        write_misspellings(self.misspellings)
        write_registry(self.registry, self.oewn)
        write_approval_policy(self.approval_policy, self.curated, self.misspellings)
        self.approval_sha = sha256(self.approval_policy)

    def tearDown(self) -> None:
        self.temp.cleanup()

    def build(self):
        from tools.vocabulary.build_relations import RelationBuildConfig, build

        with patch("tools.vocabulary.build_relations.OEWN_BYTES", self.oewn.stat().st_size), patch(
            "tools.vocabulary.build_relations.OEWN_SHA256", sha256(self.oewn)
        ), patch(
            "tools.vocabulary.build_relations.APPROVAL_POLICY_SHA256", self.approval_sha
        ), patch(
            "tools.vocabulary.build_relations.APPROVAL_POLICY_VERSION", "test-reviewed-relations-v1"
        ):
            return build(RelationBuildConfig(
                vocabulary=self.vocabulary,
                oewn=self.oewn,
                curated=self.curated,
                misspellings=self.misspellings,
                output=self.output,
                report=self.output.with_suffix(".quality.json"),
                manifest=self.output.with_suffix(".manifest.json"),
                source_registry=self.registry,
                approval_policy=self.approval_policy,
            ))

    def test_synonyms_are_sense_and_pos_bound(self) -> None:
        report = self.build()
        connection = sqlite3.connect(self.output)
        rows = connection.execute(
            "SELECT source_sense_id, target_sense_id, pos FROM published_word_relation WHERE kind='synonym'"
        ).fetchall()
        self.assertTrue(rows)
        self.assertTrue(all(source and target and pos in {"n", "v", "a", "r", "s"} for source, target, pos in rows))
        self.assertGreater(report.out_of_corpus_sense_count, 0)
        self.assertIn(
            (stable_word_id("accede"), stable_word_id("bow"), "v"),
            set(connection.execute("SELECT source_entry_id,target_entry_id,pos FROM published_word_relation WHERE kind='synonym'")),
        )
        self.assertIn(
            (stable_word_id("able"), stable_word_id("capable"), "s"),
            set(connection.execute("SELECT source_entry_id,target_entry_id,pos FROM published_word_relation WHERE kind='synonym'")),
        )
        connection.close()

    def test_reviewed_relations_publish_but_candidates_do_not(self) -> None:
        self.build()
        connection = sqlite3.connect(self.output)
        curated = connection.execute("SELECT evidence_source FROM published_word_relation WHERE evidence_source='curated_relations'").fetchall()
        candidates = connection.execute("SELECT evidence_source FROM algorithmic_candidate").fetchall()
        self.assertTrue(curated)
        self.assertTrue(candidates)
        self.assertTrue(all(row == ("curated_relations",) for row in curated))
        self.assertTrue(all(row == ("wordflow_spelling_v1",) for row in candidates))
        with self.assertRaises(sqlite3.OperationalError):
            connection.execute("UPDATE algorithmic_candidate SET published=1")
        with self.assertRaises(sqlite3.IntegrityError):
            connection.execute("UPDATE algorithmic_candidate SET evidence_source='curated_relations'")
        with self.assertRaises(sqlite3.OperationalError):
            connection.execute("UPDATE published_word_relation SET score=0")
        connection.close()

    def test_misspelling_is_directional_and_not_a_headword(self) -> None:
        self.build()
        connection = sqlite3.connect(self.output)
        row = connection.execute(
            "SELECT source_spelling, target_entry_id, direction, kind FROM published_word_relation WHERE kind='misspelling'"
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
                "SELECT source_entry_id, target_entry_id, kind FROM published_word_relation WHERE evidence_source='curated_relations'"
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
        with patch("tools.vocabulary.build_relations.OEWN_BYTES", self.oewn.stat().st_size), patch(
            "tools.vocabulary.build_relations.OEWN_SHA256", sha256(self.oewn)
        ), patch(
            "tools.vocabulary.build_relations.APPROVAL_POLICY_SHA256", self.approval_sha
        ), patch(
            "tools.vocabulary.build_relations.APPROVAL_POLICY_VERSION", "test-reviewed-relations-v1"
        ):
            second = build(RelationBuildConfig(
                vocabulary=self.vocabulary,
                oewn=self.oewn,
                curated=self.curated,
                misspellings=self.misspellings,
                output=second_output,
                report=second_output.with_suffix(".quality.json"),
                manifest=second_output.with_suffix(".manifest.json"),
                source_registry=self.registry,
                approval_policy=self.approval_policy,
            ))
        self.assertEqual(first.sqlite_sha256, second.sqlite_sha256)
        with patch("tools.vocabulary.build_relations.OEWN_BYTES", self.oewn.stat().st_size), patch(
            "tools.vocabulary.build_relations.OEWN_SHA256", sha256(self.oewn)
        ), patch(
            "tools.vocabulary.build_relations.APPROVAL_POLICY_SHA256", self.approval_sha
        ), patch(
            "tools.vocabulary.build_relations.APPROVAL_POLICY_VERSION", "test-reviewed-relations-v1"
        ):
            verification = verify_relations(self.output, vocabulary=self.vocabulary, curated=self.curated,
                misspellings=self.misspellings, oewn=self.oewn, source_registry=self.registry,
                approval_policy=self.approval_policy)
        self.assertTrue(verification.passed)
        self.assertEqual(verification.sqlite_integrity, "ok")

    def test_oewn_hash_must_match_before_parsing(self) -> None:
        from tools.vocabulary.build_relations import RelationBuildConfig, build

        registry=json.loads(self.registry.read_text(encoding='utf-8'))
        registry['sources']['oewn_2025']['sha256']='0'*64
        self.registry.write_text(json.dumps(registry)+'\n',encoding='utf-8')
        with self.assertRaisesRegex(ValueError, "repository OEWN policy mismatch"):
            build(
                RelationBuildConfig(
                    vocabulary=self.vocabulary,
                    oewn=self.oewn,
                    curated=self.curated,
                    misspellings=self.misspellings,
                    output=self.output,
                    report=self.output.with_suffix(".quality.json"),
                    manifest=self.output.with_suffix(".manifest.json"),
                    source_registry=self.registry,
                    approval_policy=self.approval_policy,
                )
            )
        self.assertFalse(self.output.exists())

    def test_manifest_and_quality_hashes_are_independently_verified(self) -> None:
        from tools.vocabulary.build_relations import verify_relation_artifacts

        self.build()
        quality = self.output.with_suffix(".quality.json")
        manifest = self.output.with_suffix(".manifest.json")
        report = self._verify()
        self.assertTrue(report.passed)
        quality.write_text("{}\n", encoding="utf-8")
        tampered = self._verify()
        self.assertFalse(tampered.passed)

    def test_candidate_layer_is_conservative_and_bounded(self) -> None:
        words = [f"pre{letter}ation" for letter in "abcdefghijkl"]
        candidates = generate_candidates(words)
        self.assertLessEqual(len(candidates), len(words) * 4)
        pairs = {(candidate.source_word, candidate.target_word) for candidate in candidates}
        self.assertTrue(all((target, source) in pairs for source, target in pairs))

    def test_repository_pin_rejects_substitute_archive(self) -> None:
        from tools.vocabulary.build_relations import RelationBuildConfig, build

        pinned_bytes, pinned_hash = self.oewn.stat().st_size, sha256(self.oewn)
        self.oewn.write_bytes(self.oewn.read_bytes() + b"substitute")
        with patch("tools.vocabulary.build_relations.OEWN_BYTES", pinned_bytes), patch(
            "tools.vocabulary.build_relations.OEWN_SHA256", pinned_hash
        ), patch(
            "tools.vocabulary.build_relations.APPROVAL_POLICY_SHA256", self.approval_sha
        ), patch(
            "tools.vocabulary.build_relations.APPROVAL_POLICY_VERSION", "test-reviewed-relations-v1"
        ):
            with self.assertRaisesRegex(ValueError, "OEWN (byte count|hash) mismatch"):
                build(RelationBuildConfig(self.vocabulary, self.oewn, self.curated, self.misspellings,
                    self.output, self.output.with_suffix('.quality.json'), self.output.with_suffix('.manifest.json'),
                    self.registry, self.approval_policy))

    def _verify(self, approval_sha: str | None = None):
        from tools.vocabulary.build_relations import verify_relation_artifacts
        with patch("tools.vocabulary.build_relations.OEWN_BYTES", self.oewn.stat().st_size), patch(
            "tools.vocabulary.build_relations.OEWN_SHA256", sha256(self.oewn)
        ), patch(
            "tools.vocabulary.build_relations.APPROVAL_POLICY_SHA256", approval_sha or self.approval_sha
        ), patch(
            "tools.vocabulary.build_relations.APPROVAL_POLICY_VERSION", "test-reviewed-relations-v1"
        ):
            return verify_relation_artifacts(self.output, vocabulary=self.vocabulary, curated=self.curated,
                misspellings=self.misspellings, quality=self.output.with_suffix('.quality.json'),
                manifest=self.output.with_suffix('.manifest.json'), oewn=self.oewn, source_registry=self.registry,
                approval_policy=self.approval_policy)

    def _rehash_database(self) -> None:
        manifest = self.output.with_suffix('.manifest.json')
        data = json.loads(manifest.read_text(encoding='utf-8'))
        data['artifacts']['relations.sqlite3'] = sha256(self.output)
        manifest.write_text(json.dumps(data, indent=2, sort_keys=True) + '\n', encoding='utf-8')

    def test_verifier_rejects_forged_semantic_evidence_and_quality(self) -> None:
        self.build()
        connection = sqlite3.connect(self.output)
        connection.execute("PRAGMA foreign_keys=OFF")
        connection.execute("UPDATE published_relation_base SET source_sense_id='fake' WHERE kind='synonym' AND rowid=(SELECT min(rowid) FROM published_relation_base WHERE kind='synonym')")
        connection.commit(); connection.close(); self._rehash_database()
        self.assertFalse(self._verify().passed)

        self.build()
        quality = self.output.with_suffix('.quality.json')
        forged = json.loads(quality.read_text(encoding='utf-8')); forged['counts'] = {}; forged['fixed_groups'] = {'expected': 999}
        quality.write_text(json.dumps(forged) + '\n', encoding='utf-8')
        manifest = self.output.with_suffix('.manifest.json'); data=json.loads(manifest.read_text(encoding='utf-8'))
        data['artifacts']['relations-quality.json']=sha256(quality); manifest.write_text(json.dumps(data,sort_keys=True,indent=2)+'\n',encoding='utf-8')
        self.assertFalse(self._verify().passed)

    def test_verifier_rejects_schema_enums_extra_curated_and_sense_drift(self) -> None:
        self.build()
        connection=sqlite3.connect(self.output)
        with self.assertRaises(sqlite3.IntegrityError):
            connection.execute("UPDATE published_relation_base SET direction='garbage'")
        with self.assertRaises(sqlite3.IntegrityError):
            connection.execute("UPDATE published_relation_base SET kind='garbage'")
        with self.assertRaises(sqlite3.IntegrityError):
            connection.execute("UPDATE lexical_sense SET pos='garbage'")
        candidate = connection.execute("SELECT source_entry_id,target_entry_id FROM algorithmic_candidate LIMIT 1").fetchone()
        with self.assertRaises(sqlite3.IntegrityError):
            connection.execute("INSERT INTO published_relation_base VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                ('forged',candidate[0],'',candidate[1],None,None,None,'antonym','forward','oewn_2025','forged',1.0,'verified','','','wordflow-relations-v2'))
        connection.execute("UPDATE lexical_sense SET synset_id='garbage' WHERE rowid=(SELECT min(rowid) FROM lexical_sense)")
        connection.commit(); connection.close(); self._rehash_database()
        self.assertFalse(self._verify().passed)

        self.build(); connection=sqlite3.connect(self.output)
        source=stable_word_id('degrade'); target=stable_word_id('herbivorous')
        connection.execute("INSERT INTO published_relation_base VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
            ('extra-curated',source,'',target,None,None,None,'spelling_similar','bidirectional','curated_relations','extra',1.0,'reviewed','extra','extra','wordflow-relations-v2'))
        connection.execute("INSERT INTO published_relation_base VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
            ('extra-misspelling',None,'wrongword',target,None,None,None,'misspelling','forward','curated_relations','extra',1.0,'reviewed','extra','','wordflow-relations-v2'))
        connection.commit(); connection.close(); self._rehash_database()
        self.assertFalse(self._verify().passed)

        self.build(); connection=sqlite3.connect(self.output); connection.execute('DROP INDEX idx_candidate_target'); connection.commit(); connection.close(); self._rehash_database()
        self.assertFalse(self._verify().passed)

        self.build(); connection=sqlite3.connect(self.output); connection.execute("INSERT INTO build_metadata VALUES ('forged','value')"); connection.commit(); connection.close(); self._rehash_database()
        self.assertFalse(self._verify().passed)

    def test_checked_in_registry_owns_official_oewn_pin(self) -> None:
        from tools.vocabulary.build_relations import load_oewn_policy, OEWN_BYTES, OEWN_SHA256
        registry = Path('data/curated/source_registry.json')
        policy = load_oewn_policy(registry)
        self.assertEqual((policy['bytes'], policy['sha256']), (OEWN_BYTES, OEWN_SHA256))

    def test_checked_in_approval_policy_anchors_all_reviewed_inputs(self) -> None:
        from tools.vocabulary.build_relations import load_approval_policy, validate_approved_inputs

        repository = Path(__file__).resolve().parents[3]
        policy_path = repository / "data/curated/relation_approval_policy.json"
        policy = load_approval_policy(policy_path)
        self.assertEqual([group["group_id"] for group in policy["groups"]], [f"g{number:02d}" for number in range(1, 19)])
        self.assertEqual(len(policy["groups"]), 18)
        self.assertEqual(policy["misspellings"], [
            {"misspelling": "stimuate", "target": "stimulate"},
            {"misspelling": "dissimuate", "target": "dissimulate"},
        ])
        validate_approved_inputs(
            repository / "data/curated/confusable_groups.csv",
            repository / "data/curated/misspellings.csv",
            policy_path,
        )

    def test_approved_group_contract_rejects_deletion_relabel_member_and_content_edits(self) -> None:
        from tools.vocabulary.build_relations import validate_approved_inputs

        repository = Path(__file__).resolve().parents[3]
        policy = repository / "data/curated/relation_approval_policy.json"
        source = repository / "data/curated/confusable_groups.csv"
        approved_misspellings = repository / "data/curated/misspellings.csv"
        with source.open("r", encoding="utf-8-sig", newline="") as stream:
            fieldnames = tuple(csv.DictReader(stream).fieldnames or ())
            stream.seek(0)
            original = list(csv.DictReader(stream))

        mutations = {
            "g18 deletion": lambda rows: [row for row in rows if row["group_id"] != "g18"],
            "g02 relabel": lambda rows: [dict(row, relation_kind="spelling_similar") if row["group_id"] == "g02" else row for row in rows],
            "member substitution": lambda rows: [dict(row, word="substitute") if index == 0 else row for index, row in enumerate(rows)],
            "contrast edit": lambda rows: [dict(row, contrast_zh_cn=row["contrast_zh_cn"] + "篡改") if index == 0 else row for index, row in enumerate(rows)],
            "collocation edit": lambda rows: [dict(row, collocation=row["collocation"] + " tampered") if index == 0 else row for index, row in enumerate(rows)],
        }
        for label, mutate in mutations.items():
            with self.subTest(label=label):
                candidate = Path(self.temp.name) / f"{label.replace(' ', '-')}.csv"
                with candidate.open("w", encoding="utf-8", newline="") as stream:
                    writer = csv.DictWriter(stream, fieldnames=fieldnames, lineterminator="\n")
                    writer.writeheader(); writer.writerows(mutate([dict(row) for row in original]))
                with self.assertRaisesRegex(ValueError, "approved curated input"):
                    validate_approved_inputs(candidate, approved_misspellings, policy)

    def test_approved_misspelling_contract_rejects_replaced_deleted_and_extra_pairs(self) -> None:
        from tools.vocabulary.build_relations import validate_approved_inputs

        repository = Path(__file__).resolve().parents[3]
        policy = repository / "data/curated/relation_approval_policy.json"
        approved_curated = repository / "data/curated/confusable_groups.csv"
        source = repository / "data/curated/misspellings.csv"
        with source.open("r", encoding="utf-8-sig", newline="") as stream:
            reader = csv.DictReader(stream); fieldnames = tuple(reader.fieldnames or ()); original = list(reader)
        extra = dict(original[0], misspelling="stimulatee")
        mutations = {
            "replaced": [dict(original[0], target="stipulate"), *original[1:]],
            "deleted": original[:-1],
            "extra": [*original, extra],
        }
        for label, rows in mutations.items():
            with self.subTest(label=label):
                candidate = Path(self.temp.name) / f"misspellings-{label}.csv"
                with candidate.open("w", encoding="utf-8", newline="") as stream:
                    writer = csv.DictWriter(stream, fieldnames=fieldnames, lineterminator="\n")
                    writer.writeheader(); writer.writerows(rows)
                with self.assertRaisesRegex(ValueError, "approved misspelling input"):
                    validate_approved_inputs(approved_curated, candidate, policy)

    def test_builder_and_verifier_reject_policy_tamper_and_malformed_policy_cleanly(self) -> None:
        self.approval_policy.write_text("{}\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "approval policy SHA-256 mismatch"):
            self.build()
        self.assertFalse(self.output.exists())

        write_approval_policy(self.approval_policy, self.curated, self.misspellings)
        self.build()
        self.approval_policy.write_text("{", encoding="utf-8")
        result = self._verify(sha256(self.approval_policy))
        self.assertFalse(result.passed)
        self.assertTrue(any("malformed approval policy" in error for error in result.errors))

    def test_malformed_manifest_returns_clean_failure(self) -> None:
        self.build(); self.output.with_suffix('.manifest.json').write_text('{}\n',encoding='utf-8')
        result=self._verify(); self.assertFalse(result.passed); self.assertTrue(result.errors)

    def test_promotion_rolls_back_every_boundary(self) -> None:
        from tools.vocabulary.build_relations import _promote_files
        root=Path(self.temp.name)
        for fail_at in (1,2,3):
            staged=[]
            for index in range(3):
                source=root/f's{fail_at}-{index}'; final=root/f'f{fail_at}-{index}'
                source.write_text('new',encoding='utf-8'); final.write_text('old',encoding='utf-8'); staged.append((source,final))
            real_replace = __import__('os').replace; calls=0
            def failing_replace(source, target):
                nonlocal calls; calls += 1
                if calls == fail_at * 2: raise OSError('injected')
                return real_replace(source,target)
            with self.assertRaises(OSError):
                _promote_files(tuple(staged), root/f'backup-{fail_at}', replace=failing_replace)
            self.assertTrue(all(final.read_text(encoding='utf-8') == 'old' for _,final in staged))


if __name__ == "__main__":
    unittest.main()
