from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
import uuid

from tools.vocabulary.normalization import normalize_word


WORD_ID_NAMESPACE = uuid.UUID("85e2bd5f-cb11-5f2e-a068-51ccf3ec6123")


def stable_word_id(word: str) -> str:
    return str(uuid.uuid5(WORD_ID_NAMESPACE, normalize_word(word)))


@dataclass(frozen=True, slots=True)
class SourceEntry:
    word: str
    phonetic: str
    definition_en: str
    translation_zh_cn: str
    pos: str
    collins: int | None
    oxford: int | None
    tags: str
    bnc_rank: int | None
    frequency_rank: int | None
    exchange: str
    detail: str
    source_key: str


@dataclass(frozen=True, slots=True)
class SelectedEntry:
    stable_id: str
    word: str
    phonetic: str
    definition_en: str
    translation_zh_cn: str
    pos: str
    collins: int | None
    oxford: int | None
    tags: str
    bnc_rank: int | None
    frequency_rank: int | None
    exchange: str
    detail: str
    tier: str
    selection_reason: str
    source_key: str
    selection_score: int


@dataclass(frozen=True, slots=True)
class SelectionResult:
    entries: tuple[SelectedEntry, ...]
    ordinary_count: int
    closure_count: int
    soft_min: int
    soft_max: int
    missing_required: tuple[str, ...] = ()
    rejection_counts: dict[str, int] = field(default_factory=dict)

    @property
    def exceeded_soft_max(self) -> bool:
        return len(self.entries) > self.soft_max

    def reason_for(self, word: str) -> str | None:
        key = normalize_word(word)
        return next(
            (entry.selection_reason for entry in self.entries if normalize_word(entry.word) == key),
            None,
        )


@dataclass(frozen=True, slots=True)
class BuildConfig:
    source: Path
    curated: Path
    source_registry: Path
    output: Path
    soft_min: int = 10_000
    soft_max: int = 12_000


@dataclass(frozen=True, slots=True)
class BuildReport:
    total: int
    ordinary_count: int
    closure_count: int
    required_count: int
    required_present: int
    missing_required: tuple[str, ...]
    csv_sha256: str
    sqlite_sha256: str
    source_sha256: str
    curated_sha256: str
    registry_sha256: str
    output: Path
