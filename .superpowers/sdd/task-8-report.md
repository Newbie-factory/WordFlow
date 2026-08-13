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

## Final re-review remediation

The final re-review reported four remaining edge conditions and one integration-test gap. All are covered by focused RED/GREEN cycles and real SQLite regressions.

- Immutable displayed revision: `CardProjection` exposes the persisted `last_event_id`, with `Guid.Empty` as the explicit initial-card sentinel. `NextCard`, rating requests, and slash requests carry that revision through to `LearningCommand`; the SQLite write transaction compares both value and revision. Real A-to-B-to-A undo tests prove stale rating and slash fail without appending, while handler tests prove `Conflict` and zero queue-page reads.
- Complete transient boundary: every public learning-store operation translates only SQLite primary codes 5, 6, 10, 13, 14, and 15 across open/configuration, transaction begin, reads/writes, and commit. Lock and cannot-open tests prove expected storage translation. Cancellation and real SQLite schema error 1, identity constraint error 19, and corrupt-database errors remain unmasked.
- Independently proved exhaustion: complete paging now requires a repository end probe tied to the same snapshot identity. It rejects internally consistent terminal underreports at 500, 200, and zero rows, as well as all earlier malformed-page cases, without arbitrary iteration caps or non-progress loops.
- Coherent mutable pages: card count, ordered rows, and review-event snapshot identity are read in one deferred SQLite transaction. A deterministic hook commits another card after the rows are materialized but before token lookup; the first page retains the old rows/token and a later page sees the complete new state.
- Real undo/application interleavings: simultaneous retries of the same atomic undo command produce exactly one applied and one idempotent result with one compensation event. Application ABA tests return `Conflict` and never invoke `GetCardsAsync`.
- Corpus invariant: promoted vocabulary, sense, and misspelling databases are explicitly immutable for a repository lifetime, which makes their count-based snapshot identities coherent. Mutable user-relation overrides use a complete merged-evidence identity.

Additional RED evidence:

- The wished-for `CardProjection`/revision request shape initially failed compilation because the contracts did not exist.
- Terminal `500/false` and `200/false` underreport tests initially returned success instead of `InvalidDataException`.
- The later zero-row hidden-data regression likewise failed with `Assert.Throws` reporting no exception; after the empty-terminal path was made to probe exhaustion, all nine malformed paging theories pass.

Final verification after this remediation:

- Domain Release: 129/129 passed.
- Application Release: 42/42 passed.
- Infrastructure Release: 67/67 passed.
- Full Release solution: 238 discoverable tests passed; the existing App test assembly remains empty.
- Release build: 0 warnings, 0 errors.
- Python vocabulary suite: 54/54 passed.
- `git diff --check`: no whitespace errors; only informational Windows LF-to-CRLF notices.

Self-review found no progress-ledger edits, WPF dependencies, arbitrary paging caps, swallowed cancellation, or broad SQLite exception mapping. The existing full-corpus queue scan remains the only documented non-blocking scale concern.

## Final independent-review closure

The last independent review identified two Important and two Minor gaps. This pass closes each with focused RED/GREEN evidence.

- Consecutive same-card undo now validates the actual compensation lineage. If the current revision is not the selected original, it must be an Undo on the same card whose compensated event's `Before` and compensation `After` both equal the selected original's `After`. A real equal-time A-to-B-to-C sequence now undoes C-to-B and then B-to-A atomically, leaving four immutable rows and two correctly ordered compensation links. Card-value mismatch, unrelated revision lineage, and writers racing the transaction still conflict/serialize.
- Every public `SqliteVocabularyRepository` and `SqliteRelationRepository` operation is wrapped across connection open/configuration, reads/probes, override autocommit write, and commit in the shared exact-code boundary. Only SQLite primary codes 5, 6, 10, 13, 14, and 15 become `TransientStorageException`; cancellation, schema error 1, corrupt databases, and programmer argument errors propagate. Real cannot-open vocabulary/relation reads and a locked override write cover the expected failures.
- `LearningCommand.ExpectedRevision` is now required and non-nullable for all interactive mutations. `RestoreSlashedWordRequest` carries the displayed revision, reads a `CardProjection`, and stale restore returns `Conflict` without appending. Legacy history uses the separately named internal `TrustedReplayCommand`/`ApplyTrustedReplayBatchAsync` path, and all legacy import regressions remain green.
- A deterministic `UndoEventSelected` hook launches a different-card writer after selection while the immediate transaction is held. The writer serializes after the compensation and becomes the true next latest action; the following undo compensates that writer. Same-command retry remains idempotent.

RED evidence:

- Same-card second undo failed with `LearningConcurrencyException` at the old `original.EventId` revision equality check.
- Cannot-open corpus reads escaped as raw SQLite error 14 and locked override escaped as raw error 5.
- The wished-for restore request failed compilation because no five-argument revision-bearing constructor existed.
- The interactive-command contract test failed because `ExpectedRevision` was `Guid?` rather than `Guid`.
- The selection-barrier test failed compilation because the wished-for `UndoEventSelected` hook did not exist.

Final fresh verification:

- Domain Release: 129/129 passed.
- Application Release: 44/44 passed.
- Infrastructure Release: 73/73 passed.
- Full Release solution: 246 discoverable tests passed; the existing App test assembly remains empty.
- Release build: 0 warnings, 0 errors.
- Python vocabulary suite: 54/54 passed.
- `git diff --check`: no whitespace errors; only informational Windows LF-to-CRLF notices.

Self-review found no ledger edit, revisionless interactive constructor, broad SQLite catch, mutable history operation, arbitrary paging cap, or WPF dependency. The documented full-corpus queue-scan scale concern remains unchanged.
