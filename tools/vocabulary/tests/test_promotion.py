import json
from pathlib import Path
import tempfile
import unittest

from tools.vocabulary.build_vocabulary import build, build_canonical
from tools.vocabulary.io_artifacts import sha256
from tools.vocabulary.models import BuildConfig
from tools.vocabulary.tests.test_verifier import evidence_registry
from tools.vocabulary.verify_vocabulary import verify


class PromotionFixture(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.source = self.root / "inputs" / "source.csv"
        self.curated = self.root / "inputs" / "custom-required.csv"
        self.registry = self.root / "inputs" / "custom-registry.json"
        self.source.parent.mkdir()
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

    def tearDown(self) -> None:
        self.temporary.cleanup()


class TruthfulManifestTests(PromotionFixture):
    def test_custom_input_paths_are_truthful_and_resolve_to_actual_inputs(self) -> None:
        output = self.root / "candidate"

        build(
            BuildConfig(
                source=self.source,
                curated=self.curated,
                source_registry=self.registry,
                output=output,
                soft_min=1,
                soft_max=1,
            )
        )

        manifest = json.loads((output / "manifest.json").read_text(encoding="utf-8"))
        curated_path = (output / manifest["inputs"]["curated_vocabulary"]["path"]).resolve()
        registry_path = (output / manifest["inputs"]["source_registry"]["path"]).resolve()
        self.assertEqual(curated_path, self.curated.resolve())
        self.assertEqual(registry_path, self.registry.resolve())


class CanonicalPromotionTests(PromotionFixture):
    def test_canonical_promotion_is_reproducible_and_verifiable(self) -> None:
        first = build_canonical(
            source=self.source,
            curated=self.curated,
            source_registry=self.registry,
            canonical_root=self.root,
            soft_min=1,
            soft_max=1,
        )
        first_manifest = (self.root / "data" / "ielts" / "manifest.json").read_bytes()
        first_csv_hash = sha256(self.root / "data" / "ielts" / "vocabulary.csv")
        first_sqlite_hash = sha256(self.root / "data" / "ielts" / "vocabulary.sqlite3")

        second = build_canonical(
            source=self.source,
            curated=self.curated,
            source_registry=self.registry,
            canonical_root=self.root,
            soft_min=1,
            soft_max=1,
        )
        verification = verify(
            self.root / "data" / "ielts",
            curated=self.curated,
            source_registry=self.registry,
            report_root=self.root / "data" / "reports",
            minimum_total=1,
        )

        self.assertEqual(first_manifest, (self.root / "data" / "ielts" / "manifest.json").read_bytes())
        self.assertEqual(first_csv_hash, sha256(self.root / "data" / "ielts" / "vocabulary.csv"))
        self.assertEqual(first_sqlite_hash, sha256(self.root / "data" / "ielts" / "vocabulary.sqlite3"))
        self.assertEqual(first.csv_sha256, second.csv_sha256)
        self.assertTrue((self.root / "data" / "reports" / "vocabulary-quality.json").is_file())
        self.assertFalse((self.root / "data" / "ielts" / "reports").exists())
        self.assertTrue(verification.passed)


class CollisionSafetyTests(PromotionFixture):
    def test_input_output_collision_is_rejected_before_writing(self) -> None:
        output = self.root / "collision"
        output.mkdir()
        curated = output / "vocabulary.csv"
        curated.write_bytes(self.curated.read_bytes())
        before = curated.read_bytes()

        with self.assertRaisesRegex(ValueError, "collides with input"):
            build(
                BuildConfig(
                    source=self.source,
                    curated=curated,
                    source_registry=self.registry,
                    output=output,
                    soft_min=1,
                    soft_max=1,
                )
            )

        self.assertEqual(curated.read_bytes(), before)
        self.assertFalse((output / "vocabulary.sqlite3").exists())


if __name__ == "__main__":
    unittest.main()
