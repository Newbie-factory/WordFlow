import unittest

from tools.vocabulary.models import SourceEntry
from tools.vocabulary.selection import select_entries, stable_word_id


def entry(
    word: str,
    *,
    translation: str = "中文释义",
    phonetic: str = "test",
    tags: str = "ielts",
    oxford: int | None = None,
    bnc_rank: int | None = 1_000,
    frequency_rank: int | None = 1_000,
) -> SourceEntry:
    return SourceEntry(
        word=word,
        phonetic=phonetic,
        definition_en="English definition",
        translation_zh_cn=translation,
        pos="n.",
        collins=None,
        oxford=oxford,
        tags=tags,
        bnc_rank=bnc_rank,
        frequency_rank=frequency_rank,
        exchange="",
        detail="",
        source_key="ecdict",
    )


class SelectionTests(unittest.TestCase):
    def test_required_confusable_is_kept_beyond_soft_target(self) -> None:
        entries = [entry("alpha"), entry("beta"), entry("gamma"), entry("herbivorous", tags="")]

        result = select_entries(
            entries,
            required={"herbivorous": "user_confusable"},
            soft_min=3,
            soft_max=3,
        )

        self.assertIn("herbivorous", {item.word for item in result.entries})
        self.assertEqual(result.reason_for("herbivorous"), "user_confusable_closure")
        self.assertEqual(len(result.entries), 4)
        self.assertTrue(result.exceeded_soft_max)

    def test_stable_id_is_case_insensitive_and_deterministic(self) -> None:
        self.assertEqual(stable_word_id("Inhabit"), stable_word_id("inhabit"))
        self.assertEqual(stable_word_id("  INHABIT  "), stable_word_id("inhabit"))
        self.assertEqual(stable_word_id("cafe\u0301"), stable_word_id("caf\u00e9"))

    def test_reason_for_uses_nfc_normalization(self) -> None:
        result = select_entries(
            [entry("caf\u00e9")],
            required={"caf\u00e9": "user_confusable"},
            soft_min=1,
            soft_max=1,
        )

        self.assertEqual(result.reason_for("cafe\u0301"), "user_confusable_closure")

    def test_empty_chinese_definition_is_rejected_even_when_tagged(self) -> None:
        result = select_entries(
            [entry("valid"), entry("empty", translation="")],
            required={},
            soft_min=1,
            soft_max=10,
        )

        self.assertEqual([item.word for item in result.entries], ["valid"])
        self.assertEqual(result.rejection_counts["empty_chinese_translation"], 1)

    def test_ordinary_selection_obeys_quality_threshold_and_soft_max(self) -> None:
        entries = [
            entry("ieltsword"),
            entry("oxfordword", tags="", oxford=1),
            entry("toeflword", tags="toefl"),
            entry("unproven", tags="", phonetic="", bnc_rank=None, frequency_rank=None),
        ]

        result = select_entries(entries, required={}, soft_min=2, soft_max=2)

        self.assertEqual(len(result.entries), 2)
        self.assertNotIn("unproven", {item.word for item in result.entries})
        self.assertEqual(result.ordinary_count, 2)

    def test_missing_required_word_is_reported_not_invented(self) -> None:
        result = select_entries(
            [entry("present")],
            required={"absent": "user_confusable"},
            soft_min=1,
            soft_max=10,
        )

        self.assertEqual(result.missing_required, ("absent",))

    def test_known_misspellings_are_never_headwords(self) -> None:
        result = select_entries(
            [entry("stimulate"), entry("stimuate"), entry("dissimuate")],
            required={"stimuate": "user_confusable"},
            soft_min=1,
            soft_max=10,
        )

        self.assertEqual([item.word for item in result.entries], ["stimulate"])
        self.assertIn("stimuate", result.missing_required)
        self.assertEqual(result.rejection_counts["known_misspelling"], 2)


if __name__ == "__main__":
    unittest.main()
