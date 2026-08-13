# WordFlow vocabulary selection policy

Policy version: `wordflow-vocabulary-selection-v1`

## Scope and claim

The released corpus is a reproducible learning selection from documented evidence. It is not described as an official or exhaustive IELTS list because IELTS publishes no single closed exhaustive vocabulary list. English and Chinese entry content comes only from the locally supplied, MIT-licensed ECDICT corpus. Curated sources establish required membership; they do not provide copied definitions.

## Release gates

Every released row must have a non-empty normalized headword, a non-empty Simplified Chinese translation, and a documented content provenance key. Case-insensitive duplicate headwords are collapsed before scoring. `stimuate` and `dissimuate` are known misspellings and are always rejected as headwords, even if accidentally placed in a curated input.

Stable IDs are UUIDv5 values in namespace `85e2bd5f-cb11-5f2e-a068-51ccf3ec6123`, using the Unicode NFC-normalized, trimmed, case-folded spelling as the name. The IDs therefore do not depend on row order, casing, output location, or build time.

## Version 1 evidence score

Ordinary candidates must score at least 75 points. Scores are explicit and additive:

| Evidence | Points |
| --- | ---: |
| documented content source | 10 |
| ECDICT IELTS tag | 100 |
| ECDICT Oxford flag | 80 |
| ECDICT TOEFL tag | 55 |
| ECDICT GRE tag | 40 |
| modern-frequency rank 1–5,000 / 5,001–15,000 / lower | 30 / 20 / 10 |
| BNC rank 1–5,000 / 5,001–15,000 / lower | 25 / 15 / 5 |
| non-empty Chinese translation | 20 |
| non-empty phonetic | 10 |
| reviewed required-topic or user closure | 200 |

Ordinary candidates are ordered by descending score and then normalized spelling, making ties deterministic. At most `soft_max` ordinary rows are selected. A build fails if fewer than `soft_min` ordinary rows pass the gate. The default guidance interval is 10,000–12,000.

## Reviewed closure

Every `reviewed` row in `data/curated/required_vocabulary.csv` is required when a releasable ECDICT entry exists. Required rows are selected after ordinary ranking, receive a reason-specific `_closure` selection reason, and are never truncated to preserve the soft maximum. Consequently, reviewed topic and user-confusable closure may cause the final total to exceed `soft_max`; the quality report records this explicitly. A missing required source row, missing Chinese translation, missing provenance, unreviewed row, duplicate curated word, undocumented source key, or curated known misspelling fails the build.

## Reproducibility and verification

The builder accepts explicit source, curated, registry, and output paths. It emits deterministic `vocabulary.csv` and `vocabulary.sqlite3` artifacts, SHA-256 hashes, a manifest, and `reports/vocabulary-quality.json`. The verifier independently checks SQLite integrity, case-insensitive uniqueness, row counts, required coverage, translations, provenance, forbidden spellings, CSV/SQLite agreement, and every recorded artifact hash.
