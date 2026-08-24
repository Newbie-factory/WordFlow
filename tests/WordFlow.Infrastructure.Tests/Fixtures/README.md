# Legacy vocabulary fixture

`ielts_vocabulary.sqlite3` is the retained 8,000-entry ECDICT-derived baseline used only to verify migration from early WordFlow learning histories to stable UUIDv5 word identifiers.

- Upstream: [ECDICT](https://github.com/skywind3000/ECDICT)
- Upstream license: MIT; see `data/licenses/ECDICT-LICENSE.txt`
- Fixture SHA-256: `687B437F894DCF2C5D7A8B3E3203958F9D2E16CC7B632EB87020E8361E206B7A`
- Selection: 5,040 IELTS-tagged entries, 1,893 Oxford foundation entries, and 1,067 TOEFL-tagged academic extensions

This is a compatibility test fixture, not the runtime corpus. The application ships the quality-gated 12,046-entry corpus under `data/ielts/`.

The real Windows speech smoke test is opt-in because headless CI runners do not provide a reliable interactive SAPI session. Set `WORDFLOW_RUN_REAL_SPEECH_TESTS=1` when running tests on an interactive Windows machine to enable it; all fake-engine speech behavior tests run everywhere.
