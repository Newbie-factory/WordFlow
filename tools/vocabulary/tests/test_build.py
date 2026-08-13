import csv
from contextlib import closing
import json
import os
from pathlib import Path
import sqlite3
import tempfile
import unittest

from tools.vocabulary.build_vocabulary import build, report_payload
from tools.vocabulary.models import BuildConfig, BuildReport
from tools.vocabulary.verify_vocabulary import resolve_artifact_path, verify


def evidence_record(name: str, *, project_path: str) -> dict[str, str]:
    return {
        "name": name,
        "role": "Test evidence",
        "license": "Test fixture use",
        "path": project_path,
    }


class BuildTests(unittest.TestCase):
    def test_manifest_does_not_depend_on_input_file_mtime(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "ecdict.csv"
            curated = root / "required.csv"
            registry = root / "source_registry.json"
            source.write_text(
                "word,phonetic,definition,translation,pos,collins,oxford,tag,bnc,frq,exchange,detail\n"
                "alpha,a,first,第一,n.,1,1,ielts,100,100,,\n"
                "beta,b,second,第二,n.,,,,,,,\n",
                encoding="utf-8",
            )
            curated.write_text(
                "word,reason,source_key,review_status\n"
                "beta,user_confusable,user_approved,reviewed\n",
                encoding="utf-8",
            )
            registry.write_text(
                json.dumps(
                    {
                        "sources": {
                            "ecdict": evidence_record("ECDICT", project_path="source.csv"),
                            "user_approved": evidence_record("Approved", project_path="spec.md"),
                        }
                    }
                ),
                encoding="utf-8",
            )
            first = root / "first"
            second = root / "second"
            config_values = {
                "source": source,
                "curated": curated,
                "source_registry": registry,
                "soft_min": 1,
                "soft_max": 1,
            }
            build(BuildConfig(output=first, **config_values))
            os.utime(source, (source.stat().st_atime + 100, source.stat().st_mtime + 100))
            build(BuildConfig(output=second, **config_values))

            self.assertEqual(
                (first / "manifest.json").read_bytes(),
                (second / "manifest.json").read_bytes(),
            )

    def test_verifier_resolves_manifest_relative_artifact_paths(self) -> None:
        artifact_dir = Path("data/ielts")
        manifest = {"artifacts": {"quality_report": {"path": "../reports/vocabulary-quality.json"}}}

        result = resolve_artifact_path(
            artifact_dir,
            manifest,
            "quality_report",
            allowed_root=Path("data/reports"),
        )

        self.assertEqual(result, Path("data/reports/vocabulary-quality.json").resolve())

    def test_slotted_build_report_has_json_safe_payload(self) -> None:
        report = BuildReport(
            total=2,
            ordinary_count=1,
            closure_count=1,
            required_count=1,
            required_present=1,
            missing_required=(),
            csv_sha256="CSV",
            sqlite_sha256="SQLITE",
            source_sha256="SOURCE",
            curated_sha256="CURATED",
            registry_sha256="REGISTRY",
            output=Path("candidate"),
        )

        payload = report_payload(report)

        self.assertEqual(payload["total"], 2)
        self.assertEqual(payload["output"], "candidate")
        self.assertEqual(payload["missing_required"], [])

    def test_build_writes_consistent_reproducible_artifacts(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "ecdict.csv"
            curated = root / "required.csv"
            registry = root / "source_registry.json"
            output = root / "candidate"
            with source.open("w", encoding="utf-8", newline="") as stream:
                writer = csv.DictWriter(
                    stream,
                    fieldnames=(
                        "word",
                        "phonetic",
                        "definition",
                        "translation",
                        "pos",
                        "collins",
                        "oxford",
                        "tag",
                        "bnc",
                        "frq",
                        "exchange",
                        "detail",
                    ),
                )
                writer.writeheader()
                writer.writerow(
                    {
                        "word": "alpha",
                        "phonetic": "al-fa",
                        "definition": "first",
                        "translation": "第一",
                        "pos": "n.",
                        "collins": "1",
                        "oxford": "1",
                        "tag": "ielts",
                        "bnc": "100",
                        "frq": "100",
                    }
                )
                writer.writerow(
                    {
                        "word": "herbivorous",
                        "phonetic": "her-biv-or-ous",
                        "definition": "feeding on plants",
                        "translation": "食草的",
                        "pos": "adj.",
                        "tag": "",
                    }
                )
            curated.write_text(
                "word,reason,source_key,review_status\n"
                "herbivorous,user_confusable,user_approved,reviewed\n",
                encoding="utf-8",
            )
            registry.write_text(
                json.dumps(
                    {
                        "sources": {
                            "ecdict": evidence_record("ECDICT", project_path="source.csv"),
                            "user_approved": evidence_record("Approved", project_path="spec.md"),
                        }
                    }
                ),
                encoding="utf-8",
            )

            report = build(
                BuildConfig(
                    source=source,
                    curated=curated,
                    source_registry=registry,
                    output=output,
                    soft_min=1,
                    soft_max=1,
                )
            )
            verification = verify(
                output,
                curated=curated,
                source_registry=registry,
                minimum_total=1,
            )

            self.assertEqual(report.total, 2)
            self.assertEqual(report.required_present, 1)
            self.assertTrue(verification.passed)
            self.assertEqual(verification.sqlite_integrity, "ok")
            self.assertEqual(verification.total, verification.unique_case_insensitive)
            self.assertTrue((output / "vocabulary.csv").is_file())
            self.assertTrue((output / "vocabulary.sqlite3").is_file())
            self.assertTrue((output / "manifest.json").is_file())
            self.assertTrue((output / "reports" / "vocabulary-quality.json").is_file())

            manifest = json.loads((output / "manifest.json").read_text(encoding="utf-8"))
            quality = json.loads(
                (output / "reports" / "vocabulary-quality.json").read_text(encoding="utf-8")
            )
            self.assertEqual(manifest["counts"]["total"], 2)
            self.assertEqual(quality["required"]["missing"], [])
            self.assertEqual(quality["required"]["words"], ["herbivorous"])
            self.assertEqual(manifest["artifacts"]["csv"]["sha256"], report.csv_sha256)
            self.assertEqual(manifest["inputs"]["curated_vocabulary"]["sha256"], report.curated_sha256)
            self.assertEqual(manifest["inputs"]["source_registry"]["sha256"], report.registry_sha256)

            with closing(sqlite3.connect(output / "vocabulary.sqlite3")) as connection:
                columns = {
                    row[1] for row in connection.execute("PRAGMA table_info(vocabulary)").fetchall()
                }
                provenance = connection.execute(
                    "SELECT source_key FROM vocabulary WHERE word = 'herbivorous'"
                ).fetchone()[0]
            self.assertIn("stable_id", columns)
            self.assertEqual(provenance, "ecdict")


if __name__ == "__main__":
    unittest.main()
