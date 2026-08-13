# Task 8 report: application learning and lexical-relation use cases

## Status and baseline

- Worktree: `D:\baicizhan\.worktrees\wordflow-implementation`
- Branch: `codex/wordflow-implementation`
- Clean baseline/head before Task 8: `ea718bb` (`fix: reject missing FSRS snapshot fields`)
- Scope: view-neutral Application requests/results/handlers, required port expansions, real SQLite adapter integration, and Application/Infrastructure contract tests. No WPF types and no progress-ledger changes.

## TDD RED evidence

Tests were written before production implementation and executed with:

```powershell
dotnet test tests\WordFlow.Application.Tests --no-restore
```

The expected missing-feature RED failed compilation with `CS0234`/`CS0246`: `WordFlow.Application.Learning`, `WordFlow.Application.Relations`, `RatingShortcut`, `GetNextCard`, and `MisspellingRelation` did not exist.

A later boundary RED was captured for rating a newly queued vocabulary headword before any `card_state` row exists:

```powershell
dotnet test tests\WordFlow.Application.Tests --filter FullyQualifiedName~New_learning_headword --no-restore
```

It failed because the handler returned `NotFound<LearningTransition>` instead of success. The queue/card resolver now creates the exact initial card snapshot only for a verified learning headword; the focused regression and full suite pass.

## Interfaces and behavior

- `UseCaseResult<T>` has explicit `Success`, `Conflict`, `StorageFailure`, and `NotFound` cases.
- Expected database/I/O failures become results. Cancellation, invalid/corrupt data, illegal action values, and programming/domain errors propagate rather than being masked.
- Every handler is view-neutral and accepts `TimeProvider`; there is no WPF or ambient wall-clock dependency in Application.
- `GetNextCard` exhausts paged card/vocabulary reads, filters non-headwords, delegates ordering/caps to the Task 6 `QueuePolicy`, and supports new headwords without pre-existing snapshots.
- `SubmitRating` maps only F1/F2/F3 to Again/Hard/Good. `SlashWord` is a distinct action and cannot enter rating mapping. Both await the atomic store commit before reading the next queue state.
- Retry command IDs resolve to the original persisted result. Stale snapshots return `Conflict`; failed commits never read/advance the queue.
- Scheduled and immediate restore preserve FSRS memory. Undo appends a compensating event and never mutates/deletes history.
- `GetSynonyms` reads every relation page and groups verified synonyms by source sense and POS with stable group/word ordering.
- `GetConfusables` returns every published confusable kind and directional misspellings separately. Misspellings remain spelling corrections, never vocabulary/learning headwords.
- `UpdatePersonalRelation` supports exact directional add/suppress overrides only for confusable classifications and stamps writes from `TimeProvider`.
- Real SQLite adapters now expose idempotent commit lookup, deterministic card paging, latest undoable history, exact vocabulary lookup, sense POS, complete relation evidence with correct reverse bidirectional sense orientation, and targeted misspelling pages.

## Verification results

- `dotnet test tests\WordFlow.Application.Tests -c Release --no-restore`: 30/30 passed.
- `dotnet test tests\WordFlow.Domain.Tests -c Release --no-restore`: 129/129 passed.
- `dotnet test tests\WordFlow.Infrastructure.Tests -c Release --no-restore`: 55/55 passed.
- `dotnet test WordFlow.sln -c Release --no-restore`: all 214 discoverable tests passed (129 Domain, 30 Application, 55 Infrastructure); the existing App test assembly contains no discoverable tests.
- `dotnet build WordFlow.sln -c Release --no-restore`: success, 0 warnings, 0 errors.
- `python -m unittest discover -s tools\vocabulary\tests -p test_*.py`: 54/54 passed.
- `git diff --check`: no whitespace errors; only informational Windows LF-to-CRLF notices.

## Self-review

- Checked every brief item against an application or real-adapter test, including failure-before-advance, duplicate commands, empty and >500 relation sets, stable sense/POS grouping, personal add/suppress, illegal mappings, both restore modes, append-only undo, and non-headword exclusion.
- Confirmed relation classifications are the real corpus values and reverse bidirectional rows swap source/target sense evidence.
- Confirmed cancellation filters cannot catch `OperationCanceledException`; corrupt persisted/corpus data is not converted to a friendly storage result.
- Confirmed no arbitrary relation cap, ambient Application clock, WPF dependency, unrelated schema change, or progress-ledger edit.
- Confirmed the new store reads retain deterministic ordering and existing atomic write/idempotency behavior.

## Concerns

- `GetNextCard` currently rebuilds the bounded daily queue by reading all card and vocabulary pages. This is correct for the current approximately 12,000-word offline corpus and keeps policy centralized, but a future substantially larger corpus may warrant a dedicated query-optimized queue port.
- `WordFlow.App.Tests` remains an existing empty test assembly; all discoverable solution tests pass.

## Independent-review remediation

The review reported four Important and two Minor findings. All six are addressed.

- Atomic undo: `ILearningStore.UndoLatestAsync` now opens one immediate transaction, resolves command idempotency, selects the deterministic latest uncompensated event, validates its current projection, appends the compensation, updates the card snapshot, and commits once. Equal timestamps use `rowid DESC`; already compensated originals are skipped. Different-card actions cannot interleave after selection because the write transaction owns the complete operation.
- Displayed-state concurrency: rating and slash requests now carry the exact displayed `CardState`. The application creates `ReviewEvent.Before` from that snapshot, and the existing store transaction compares it against the persisted projection before insertion. Real SQLite tests commit an intervening update and prove stale rating/slash conflict with no second event.
- Error boundary: the Application catches only explicit `TransientStorageException`. The SQLite learning adapter maps only availability/resource codes (busy, locked, I/O, full, cannot-open, protocol) to that type. Schema error 1, constraint error 19, corrupt persisted data, cancellation, and programmer/domain faults propagate.
- Complete paging: `Page<T>` now carries mandatory-for-complete-read `HasMore` and `SnapshotId`. The helper rejects missing/changeable snapshots, changing/negative/underreported totals, continuation disagreement, premature empties, overfull pages, duplicates/ignored offsets, and non-progress. Adversarial tests cover all cases including concurrent mutation; repositories provide stable identities for each logical result set.
- Confusables: tests enumerate all six supported kinds and read 602 relations plus 501 misspellings across pages.
- Adapter evidence: reverse bidirectional synonyms assert swapped sense IDs/POS; atomic undo tests cover two cards, equal timestamps, insertion-order tie-breaking, and compensated-event exclusion.

Remediation RED evidence includes the initial compile failure for missing `UndoLearningCommand`/atomic store API and wished-for displayed-snapshot request shape, followed by failing adversarial paging and broad fake-`DbException` expectations while the old boundary remained.

Final remediation verification:

- Application Release: 37/37 passed.
- Domain Release: 129/129 passed.
- Infrastructure Release: 60/60 passed.
- Full Release solution: 226 discoverable tests passed; App test assembly remains empty.
- Release build: 0 warnings, 0 errors.
- Python vocabulary suite: 54/54 passed.
- `git diff --check`: clean apart from informational Windows line-ending notices.

No new blocking concern. Snapshot identity computation for relation pages is intentionally exhaustive because complete-read correctness is prioritized and current per-source relation sets are small.
