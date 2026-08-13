# Offline lexical-relation data

WordFlow builds `relations.sqlite3` during development from the verified vocabulary database, the pinned Open English WordNet 2025 core JSON archive, reviewed confusable groups, and reviewed misspellings. The desktop application never downloads or modifies these base relations at runtime.

## Layers and publication

- OEWN semantic relations are published only when their source and target senses exist. Synonyms share an OEWN synset and POS; antonyms and derivations preserve explicit sense references.
- Algorithmic spelling similarities are stored with `review_state=candidate` and `published=0`. They are review leads, not verified relations.
- The 18 design-section 8.2 groups are reviewed, classified relations with Chinese contrast notes and collocations. Each ordered pair is stored for bidirectional discovery.
- Misspellings are directional mappings from invalid spelling text to a valid stable entry ID. Invalid spellings are not vocabulary entries.
- Personal relations belong in the user database; the immutable base relation database declares the supported `personal_confusable` kind but does not manufacture user data.

## Reproducible build

```powershell
powershell -ExecutionPolicy Bypass -File tools\vocabulary\download_oewn.ps1 -ExpectedSha256 7D749F6E2C39E6970E4997839DCF6E42FD281F3C2FAE0171D2192BAE8CFA4B51
python tools\vocabulary\build_relations.py --vocabulary data\ielts\vocabulary.sqlite3 --oewn data\sources\oewn\english-wordnet-2025-json.zip --curated data\curated\confusable_groups.csv --misspellings data\curated\misspellings.csv --output data\ielts\relations.sqlite3 --report data\reports\relations-quality.json --manifest data\ielts\relations-manifest.json --expected-oewn-sha256 7D749F6E2C39E6970E4997839DCF6E42FD281F3C2FAE0171D2192BAE8CFA4B51
```

The builder checks the complete archive hash and ZIP structure before parsing, validates stable vocabulary IDs and corpus closure, stages all outputs, verifies SQLite integrity, and records deterministic hashes and counts. See the shipped OEWN and Princeton WordNet license files in `data/licenses`.
