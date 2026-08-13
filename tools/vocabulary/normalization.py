from __future__ import annotations

import unicodedata


def normalize_word(word: str) -> str:
    return unicodedata.normalize("NFC", word.strip()).casefold()
