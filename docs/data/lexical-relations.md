# Offline lexical-relation data

WordFlow builds `relations.sqlite3` during development from the verified vocabulary database, the pinned Open English WordNet 2025 core JSON archive, reviewed confusable groups, and reviewed misspellings. The desktop application never downloads or modifies these base relations at runtime.

## Layers and publication

- OEWN semantic relations are published only when their source and target senses exist. Synonyms share an OEWN synset and POS; antonyms and derivations preserve explicit sense references.
- Algorithmic spelling similarities live only in `algorithmic_candidate`. Published relations live in constrained `published_relation_base` and are exposed through the read-only `published_word_relation` view. Candidates cannot be relabelled into that view by an ordinary update.
- The 18 design-section 8.2 groups are reviewed, classified relations with Chinese contrast notes and collocations. Each ordered pair is stored for bidirectional discovery.
- Misspellings are directional mappings from invalid spelling text to a valid stable entry ID. Invalid spellings are not vocabulary entries.
- Personal relations belong in the user database; the immutable base relation database declares the supported `personal_confusable` kind but does not manufacture user data.

## Reproducible build

```powershell
powershell -ExecutionPolicy Bypass -File tools\vocabulary\download_oewn.ps1 -ExpectedSha256 7D749F6E2C39E6970E4997839DCF6E42FD281F3C2FAE0171D2192BAE8CFA4B51
python tools\vocabulary\build_relations.py --vocabulary data\ielts\vocabulary.sqlite3 --oewn data\sources\oewn\english-wordnet-2025-json.zip --curated data\curated\confusable_groups.csv --misspellings data\curated\misspellings.csv --source-registry data\curated\source_registry.json --output data\ielts\relations.sqlite3 --report data\reports\relations-quality.json --manifest data\ielts\relations-manifest.json
```

The builder loads the repository-owned OEWN hash, byte count, URL, core edition, and license from the strict source registry; callers cannot substitute a hash. It checks archive integrity before parsing, uses synset `partOfSpeech` as canonical POS, stages and independently reconstructs all relation sets and metrics, and promotes database/report/manifest with rollback and manifest last. See the shipped OEWN and Princeton WordNet license files in `data/licenses`.
