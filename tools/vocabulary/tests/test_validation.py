import json
from pathlib import Path
import tempfile
import unittest

from tools.vocabulary.io_artifacts import load_curated, load_registry


class CuratedValidationTests(unittest.TestCase):
    def test_curated_row_with_extra_columns_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "required.csv"
            path.write_text(
                "word,reason,source_key,review_status\n"
                "alpha,user_confusable,user_approved,reviewed,unexpected\n",
                encoding="utf-8",
            )

            with self.assertRaisesRegex(ValueError, "exactly four"):
                load_curated(path)

    def test_duplicate_normalized_curated_word_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "required.csv"
            path.write_text(
                "word,reason,source_key,review_status\n"
                "Café,user_confusable,user_approved,reviewed\n"
                "café,user_confusable,user_approved,reviewed\n",
                encoding="utf-8",
            )

            with self.assertRaisesRegex(ValueError, "duplicate"):
                load_curated(path)

    def test_forbidden_misspelling_in_curated_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "required.csv"
            path.write_text(
                "word,reason,source_key,review_status\n"
                "stimuate,user_confusable,user_approved,reviewed\n",
                encoding="utf-8",
            )

            with self.assertRaisesRegex(ValueError, "misspelling"):
                load_curated(path)

    def test_empty_curated_file_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "required.csv"
            path.write_text("", encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "exact header|data row"):
                load_curated(path)

    def test_header_only_curated_file_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "required.csv"
            path.write_text("word,reason,source_key,review_status\n", encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "data row"):
                load_curated(path)

    def test_bad_curated_schema_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "required.csv"
            path.write_text(
                "word,reason,source_key\nalpha,user_confusable,user_approved\n",
                encoding="utf-8",
            )

            with self.assertRaisesRegex(ValueError, "exact header"):
                load_curated(path)

    def test_blank_or_unsupported_curated_reason_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "required.csv"
            path.write_text(
                "word,reason,source_key,review_status\nalpha,unknown,user_approved,reviewed\n",
                encoding="utf-8",
            )

            with self.assertRaisesRegex(ValueError, "supported reason"):
                load_curated(path)

    def test_unreviewed_curated_row_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "required.csv"
            path.write_text(
                "word,reason,source_key,review_status\n"
                "alpha,user_confusable,user_approved,pending\n",
                encoding="utf-8",
            )

            with self.assertRaisesRegex(ValueError, "reviewed"):
                load_curated(path)


class RegistryValidationTests(unittest.TestCase):
    def test_registry_source_requires_meaningful_evidence_fields(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "registry.json"
            path.write_text(
                json.dumps({"sources": {"ecdict": {"name": "ECDICT"}}}),
                encoding="utf-8",
            )

            with self.assertRaisesRegex(ValueError, "role|license|url|path"):
                load_registry(path)

    def test_registry_source_requires_url_or_project_path(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "registry.json"
            path.write_text(
                json.dumps(
                    {
                        "sources": {
                            "ecdict": {
                                "name": "ECDICT",
                                "role": "content",
                                "license": "MIT",
                            }
                        }
                    }
                ),
                encoding="utf-8",
            )

            with self.assertRaisesRegex(ValueError, "url or path"):
                load_registry(path)

    def test_registry_url_must_be_http_or_https(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "registry.json"
            path.write_text(
                json.dumps(
                    {
                        "sources": {
                            "ecdict": {
                                "name": "ECDICT",
                                "role": "content",
                                "license": "MIT",
                                "url": "not-a-url",
                            }
                        }
                    }
                ),
                encoding="utf-8",
            )

            with self.assertRaisesRegex(ValueError, "http"):
                load_registry(path)


if __name__ == "__main__":
    unittest.main()
