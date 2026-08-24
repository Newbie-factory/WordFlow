# Third-Party Notices

WordFlow distributes a curated offline vocabulary database and lexical-relation database. The application code and project-authored curation are distinct from the third-party resources below.

## ECDICT

- Project: [skywind3000/ECDICT](https://github.com/skywind3000/ECDICT)
- Role: English headwords, phonetics, English definitions, Simplified Chinese translations and source metadata
- License: MIT

The upstream MIT license is reproduced at `data/licenses/ECDICT-LICENSE.txt`.

The runtime vocabulary database is a filtered and transformed subset selected by WordFlow's documented vocabulary policy. The upstream project and its license remain authoritative.

## Open English WordNet 2025

- Project: [Open English WordNet](https://en-word.net/)
- Edition: 2025 core JSON, without Namenet
- Role: sense, part-of-speech, synset, antonym and derivational evidence used by the lexical-relation database
- License: CC BY 4.0, with underlying Princeton WordNet licensing conditions

The complete notices bundled with this repository are available at:

- `data/licenses/OEWN-2025-LICENSE.md`
- `data/licenses/WORDNET-LICENSE.txt`

## Project-authored material

The following data is curated or generated specifically for WordFlow:

- `data/curated/confusable_groups.csv`
- `data/curated/misspellings.csv`
- `data/curated/relation_approval_policy.json`
- `tools/vocabulary/`

See `data/curated/source_registry.json`, `data/ielts/manifest.json`, and `data/ielts/relations-manifest.json` for exact source identifiers, input hashes, artifact hashes and transformation versions.
