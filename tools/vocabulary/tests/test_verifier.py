import csv
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest

from tools.vocabulary.build_vocabulary import build
from tools.vocabulary.io_artifacts import CSV_FIELDS, sha256
from tools.vocabulary.models import BuildConfig
from tools.vocabulary.verify_vocabulary import resolve_artifact_path, verify


def evidence_registry() -> dict[str, object]:
    return {
        "sources": {
            "ecdict": {
                "name": "ECDICT",
                "role": "Licensed entry content",
                "license": "MIT",
                "url": "https://github.com/skywind3000/ECDICT",
            },
            "user_approved": {
                "name": "Approved requirements",
                "role": "Required closure membership",
                "license": "Project-authored use",
                "path": "docs/spec.md",
            },
        }
    }


class VerifierFixture(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.source = self.root / "ecdict.csv"
        self.curated = self.root / "required.csv"
        self.registry = self.root / "registry.json"
        self.output = self.root / "candidate"
        self.source.write_text(
            "word,phonetic,definition,translation,pos,collins,oxford,tag,bnc,frq,exchange,detail\n"
            "alpha,a,first,第一,n.,1,1,ielts,100,100,,\n"
            "beta,b,second,第二,n.,,,,,,,\n",
            encoding="utf-8",
        )
        self.curated.write_text(
            "word,reason,source_key,review_status\n"
            "beta,user_confusable,user_approved,reviewed\n",
            encoding="utf-8",
        )
        self.registry.write_text(json.dumps(evidence_registry()), encoding="utf-8")
        build(
            BuildConfig(
                source=self.source,
                curated=self.curated,
                source_registry=self.registry,
                output=self.output,
                soft_min=1,
                soft_max=1,
            )
        )

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def verify(self):
        return verify(
            self.output,
            curated=self.curated,
            source_registry=self.registry,
            minimum_total=1,
        )

    def manifest(self) -> dict[str, object]:
        return json.loads((self.output / "manifest.json").read_text(encoding="utf-8"))

    def write_manifest(self, manifest: dict[str, object]) -> None:
        (self.output / "manifest.json").write_text(
            json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8"
        )

    def rehash(self, key: str, path: Path) -> None:
        manifest = self.manifest()
        manifest["artifacts"][key]["sha256"] = sha256(path)
        self.write_manifest(manifest)

    def mutate_csv(self, field: str, value: str) -> None:
        path = self.output / "vocabulary.csv"
        with path.open("r", encoding="utf-8-sig", newline="") as stream:
            rows = list(csv.DictReader(stream))
        rows[0][field] = value
        with path.open("w", encoding="utf-8-sig", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=CSV_FIELDS, lineterminator="\n")
            writer.writeheader()
            writer.writerows(rows)
        self.rehash("csv", path)


class IndependentClosureTests(VerifierFixture):
    def test_forged_quality_required_list_does_not_hide_curated_requirement(self) -> None:
        quality_path = self.output / "reports" / "vocabulary-quality.json"
        quality = json.loads(quality_path.read_text(encoding="utf-8"))
        quality["required"]["words"] = ["alpha"]
        quality_path.write_text(json.dumps(quality, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        self.rehash("quality_report", quality_path)

        report = self.verify()

        self.assertFalse(report.passed)
        self.assertFalse(report.quality_required_matches_curated)

    def test_curated_hash_is_verified_from_separate_input(self) -> None:
        self.curated.write_text(
            self.curated.read_text(encoding="utf-8").replace("beta", "gamma"),
            encoding="utf-8",
        )

        report = self.verify()

        self.assertFalse(report.passed)
        self.assertFalse(report.curated_sha_matches_manifest)


class SemanticArtifactTests(VerifierFixture):
    def test_arbitrary_stable_ids_are_rejected_even_when_artifacts_agree(self) -> None:
        forged = "00000000-0000-0000-0000-000000000000"
        self.mutate_csv("stable_id", forged)
        with closing(sqlite3.connect(self.output / "vocabulary.sqlite3")) as connection:
            connection.execute("UPDATE vocabulary SET stable_id = ? WHERE word = 'alpha'", (forged,))
            connection.commit()
        self.rehash("sqlite", self.output / "vocabulary.sqlite3")

        report = self.verify()

        self.assertFalse(report.passed)
        self.assertEqual(report.invalid_stable_id_csv, 1)
        self.assertEqual(report.invalid_stable_id_sqlite, 1)

    def test_arbitrary_csv_stable_id_is_independently_rejected(self) -> None:
        self.mutate_csv("stable_id", "00000000-0000-0000-0000-000000000000")

        report = self.verify()

        self.assertFalse(report.passed)
        self.assertEqual(report.invalid_stable_id_csv, 1)
        self.assertEqual(report.invalid_stable_id_sqlite, 0)

    def test_empty_selection_reason_is_rejected_in_both_artifacts(self) -> None:
        self.mutate_csv("selection_reason", "")
        with closing(sqlite3.connect(self.output / "vocabulary.sqlite3")) as connection:
            connection.execute("UPDATE vocabulary SET selection_reason = '' WHERE word = 'alpha'")
            connection.commit()
        self.rehash("sqlite", self.output / "vocabulary.sqlite3")

        report = self.verify()

        self.assertFalse(report.passed)
        self.assertEqual(report.empty_selection_reason_csv, 1)
        self.assertEqual(report.empty_selection_reason_sqlite, 1)

    def test_one_differing_csv_field_breaks_full_row_agreement(self) -> None:
        self.mutate_csv("translation_zh_cn", "不一致")

        report = self.verify()

        self.assertFalse(report.passed)
        self.assertFalse(report.csv_rows_match_sqlite)

    def test_undocumented_provenance_key_is_rejected(self) -> None:
        self.mutate_csv("source_key", "invented_source")
        with closing(sqlite3.connect(self.output / "vocabulary.sqlite3")) as connection:
            connection.execute("UPDATE vocabulary SET source_key = 'invented_source' WHERE word = 'alpha'")
            connection.commit()
        self.rehash("sqlite", self.output / "vocabulary.sqlite3")

        report = self.verify()

        self.assertFalse(report.passed)
        self.assertEqual(report.undocumented_provenance, ("invented_source",))

    def test_sqlite_declared_type_mismatch_is_rejected(self) -> None:
        database = self.output / "vocabulary.sqlite3"
        with closing(sqlite3.connect(database)) as connection:
            connection.execute("ALTER TABLE vocabulary RENAME COLUMN selection_score TO selection_score_old")
            connection.commit()
        self.rehash("sqlite", database)

        report = self.verify()

        self.assertFalse(report.passed)
        self.assertFalse(report.sqlite_schema_valid)

    def test_csv_schema_mismatch_is_rejected(self) -> None:
        path = self.output / "vocabulary.csv"
        text = path.read_text(encoding="utf-8-sig")
        path.write_text(text.replace("selection_score", "wrong_score", 1), encoding="utf-8-sig")
        self.rehash("csv", path)

        report = self.verify()

        self.assertFalse(report.passed)
        self.assertFalse(report.csv_schema_valid)


class ManifestPathTests(VerifierFixture):
    def test_absolute_manifest_artifact_path_is_rejected(self) -> None:
        manifest = self.manifest()
        manifest["artifacts"]["csv"]["path"] = str((self.output / "vocabulary.csv").resolve())

        with self.assertRaisesRegex(ValueError, "relative"):
            resolve_artifact_path(self.output, manifest, "csv", allowed_root=self.output)

    def test_traversal_manifest_artifact_path_is_rejected(self) -> None:
        manifest = self.manifest()
        manifest["artifacts"]["csv"]["path"] = "../outside.csv"

        with self.assertRaisesRegex(ValueError, "allowed root"):
            resolve_artifact_path(self.output, manifest, "csv", allowed_root=self.output)


if __name__ == "__main__":
    unittest.main()
