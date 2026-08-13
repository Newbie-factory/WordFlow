from __future__ import annotations

from collections import Counter
from collections.abc import Mapping, Set

from tools.vocabulary.models import SelectedEntry, SelectionResult, SourceEntry, stable_word_id
from tools.vocabulary.normalization import normalize_word


MINIMUM_ORDINARY_SCORE = 75
KNOWN_MISSPELLINGS = frozenset({"stimuate", "dissimuate"})


def _tokens(tags: str) -> set[str]:
    return {token.casefold() for token in tags.replace(",", " ").split()}


def score_entry(entry: SourceEntry, *, is_required: bool = False) -> int:
    """Return the version-1 evidence score documented in vocabulary-policy.md."""
    tags = _tokens(entry.tags)
    score = 10 if entry.source_key else 0
    score += 100 if "ielts" in tags else 0
    score += 80 if entry.oxford is not None else 0
    score += 55 if "toefl" in tags else 0
    score += 40 if "gre" in tags else 0
    if entry.frequency_rank:
        score += 30 if entry.frequency_rank <= 5_000 else 20 if entry.frequency_rank <= 15_000 else 10
    if entry.bnc_rank:
        score += 25 if entry.bnc_rank <= 5_000 else 15 if entry.bnc_rank <= 15_000 else 5
    score += 20 if entry.translation_zh_cn.strip() else 0
    score += 10 if entry.phonetic.strip() else 0
    score += 200 if is_required else 0
    return score


def _required_map(required: Mapping[str, str] | Set[str]) -> dict[str, str]:
    if isinstance(required, Mapping):
        return {normalize_word(word): reason for word, reason in required.items()}
    return {normalize_word(word): "user_confusable" for word in required}


def _ordinary_reason(entry: SourceEntry) -> tuple[str, str]:
    tags = _tokens(entry.tags)
    if "ielts" in tags:
        return "core", "ecdict_ielts_tag"
    if entry.oxford is not None:
        return "foundation", "ecdict_oxford_core"
    if "toefl" in tags or "gre" in tags:
        return "academic_extension", "ecdict_exam_tag_quality_score"
    return "frequency_extension", "ecdict_frequency_quality_score"


def _selected(entry: SourceEntry, reason: str, tier: str, score: int) -> SelectedEntry:
    return SelectedEntry(
        stable_id=stable_word_id(entry.word),
        word=entry.word.strip(),
        phonetic=entry.phonetic.strip(),
        definition_en=entry.definition_en.strip(),
        translation_zh_cn=entry.translation_zh_cn.strip(),
        pos=entry.pos.strip(),
        collins=entry.collins,
        oxford=entry.oxford,
        tags=entry.tags.strip(),
        bnc_rank=entry.bnc_rank,
        frequency_rank=entry.frequency_rank,
        exchange=entry.exchange.strip(),
        detail=entry.detail.strip(),
        tier=tier,
        selection_reason=reason,
        source_key=entry.source_key,
        selection_score=score,
    )


def select_entries(
    entries: list[SourceEntry],
    *,
    required: Mapping[str, str] | Set[str],
    soft_min: int,
    soft_max: int,
) -> SelectionResult:
    if soft_min < 1 or soft_max < soft_min:
        raise ValueError("soft bounds must satisfy 1 <= soft_min <= soft_max")

    required_by_word = _required_map(required)
    rejected: Counter[str] = Counter()
    unique: dict[str, SourceEntry] = {}
    for entry in entries:
        key = normalize_word(entry.word)
        if not key:
            rejected["empty_word"] += 1
            continue
        if key in KNOWN_MISSPELLINGS:
            rejected["known_misspelling"] += 1
            continue
        if not entry.translation_zh_cn.strip():
            rejected["empty_chinese_translation"] += 1
            continue
        if not entry.source_key.strip():
            rejected["missing_provenance"] += 1
            continue
        current = unique.get(key)
        if current is None or score_entry(entry, is_required=key in required_by_word) > score_entry(
            current, is_required=key in required_by_word
        ):
            unique[key] = entry

    ordinary_candidates: list[tuple[int, str, SourceEntry]] = []
    for key, entry in unique.items():
        if key in required_by_word:
            continue
        score = score_entry(entry)
        if score >= MINIMUM_ORDINARY_SCORE:
            ordinary_candidates.append((score, key, entry))
        else:
            rejected["below_quality_threshold"] += 1
    ordinary_candidates.sort(key=lambda item: (-item[0], item[1]))
    ordinary_candidates = ordinary_candidates[:soft_max]

    selected: list[SelectedEntry] = []
    for score, _, source in ordinary_candidates:
        tier, reason = _ordinary_reason(source)
        selected.append(_selected(source, reason, tier, score))

    closure_count = 0
    missing_required: list[str] = []
    for key, curated_reason in sorted(required_by_word.items()):
        source = unique.get(key)
        if source is None:
            missing_required.append(key)
            continue
        closure_reason = f"{curated_reason}_closure"
        selected.append(_selected(source, closure_reason, "required_closure", score_entry(source, is_required=True)))
        closure_count += 1

    selected.sort(key=lambda item: (-item.selection_score, normalize_word(item.word)))
    return SelectionResult(
        entries=tuple(selected),
        ordinary_count=len(ordinary_candidates),
        closure_count=closure_count,
        soft_min=soft_min,
        soft_max=soft_max,
        missing_required=tuple(missing_required),
        rejection_counts=dict(sorted(rejected.items())),
    )
