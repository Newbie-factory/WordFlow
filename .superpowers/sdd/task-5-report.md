# Task 5 report: safe on-device FSRS parameter optimization

## Status and scope

Complete. Task 5 adds an in-process C# optimizer, focused Application ports/use case, tests, and pinned algorithm provenance. It does not add SQLite persistence, network/Python runtime calls, WPF behavior, rating-history rewriting, or bulk rescheduling. Task 4 scheduling formulas and the official 21 parameter bounds were not changed.

## Baseline

- Clean worktree at `a66ba398bb1a478d21105684218457b49cdf9495` (`fix: validate FSRS-6 scheduler boundaries`).
- Infrastructure Release tests: no tests existed.
- Application Release tests: no tests existed.
- `dotnet build WordFlow.sln -c Release --no-restore`: success, 0 warnings, 0 errors.
- `python -m unittest discover -s tools\vocabulary\tests`: 54/54 passed.

## TDD red-green record

- RED: added the two requested Task 5 test files before production code. Both specified focused commands failed with CS0234/CS0246 because the optimizer, sample, port, and application types did not exist.
- First GREEN: Application passed 4/4; Infrastructure passed 5/7. The remaining failures exposed two fixture issues: the hand-entered expected loss did not match the pinned continuous-time reference, and duplicate command validity needed an explicit all-occurrences policy.
- Reference GREEN: generated the loss fixture independently from pinned py-fsrs commit `854a51496993d088175f6d7da0bbfaa414034c61`; C# matched both predictions and mean loss to 12 decimal places. All Infrastructure tests passed 7/7.
- Audit RED/GREEN: added a rejected-candidate audit test. It first failed because `OptimizationResult`/snapshot did not retain attempted candidate parameters and candidate training loss; the port and snapshot were extended, and the focused test passed.
- Restore safety RED/GREEN: a rejected snapshot could initially be restored; the activation gate now permits restore only for accepted or explicit restored-source snapshots. Focused test passed.
- Cancellation coverage: pre-cancel and cancel-during-search paths both pass. Final focused counts are Infrastructure 8/8 and Application 4/4.

## Validity, split, and objective

Eligibility is exactly 400 valid review events after deterministic input-order filtering. Invalid events are blank command/card IDs, undone or inverse events, Slash/non-rating events, rating values outside 1/2/3, every occurrence of a duplicated command ID, and per-card timestamps not strictly increasing relative to the previous accepted event. Exact 399/400 behavior is tested.

After filtering and UTC normalization, the optimizer selects the 80th-percentile chronological event instant. The split unit is the whole card history:

- cards wholly before the boundary form training;
- cards wholly at/after the boundary form validation;
- cards spanning the boundary are excluded from both objectives.

This conservative temporal gap prevents future leakage and prevents the same card identity/history appearing in both partitions even when histories interleave. Eligibility uses the pre-split valid count; an empty/non-evaluable partition returns `NotEligible`.

The first review initializes a card and has no supervised observation. Later reviews predict retrievability immediately before the review using the unchanged Task 4 continuous UTC scheduler. Again is failure (`y=0`); Hard/Good are recall (`y=1`). Both baseline and candidate use mean Bernoulli log loss. Prediction values are clamped only to `[1e-15, 1-1e-15]` at the logarithm boundary; scheduler state/formulas are unchanged.

## Optimizer algorithm and acceptance

- Algorithm version: `wordflow-fsrs6-local-v1`.
- Runtime: local C# only; no Python process, network, SQLite, or dispatcher dependency.
- Search: deterministic bounded coordinate search, default seed `0x5F3759DF`, four rounds, both directions for all 21 parameters, decreasing multiplicative step.
- Bounds: every trial is constructed via Task 4 `FsrsParameters`; invalid out-of-bound trials are discarded. Bounds were neither copied loosely nor changed.
- Safety: cancellation during filtering, sample evaluation, and coordinate evaluation; plain `IProgress<OptimizationProgress>` reports progress up to 1.0.
- Finite validation: empty or non-finite objectives are rejected as unusable.
- Overfitting guard: a training-selected candidate is accepted only when candidate validation loss is no worse than baseline validation loss plus the documented `1e-12` tolerance. Otherwise the active result is the baseline while the attempted candidate and its losses remain auditable.

Meaningful deterministic synthetic fixture metrics (seed 1776, two search rounds):

- baseline training loss: `0.7987672271654828`
- candidate training loss: `0.714310348311343`
- baseline validation loss: `0.8360383590744767`
- candidate validation loss: `0.7451070192829333`
- status: Accepted

The fixed rejected candidate test uses an official-boundary vector and verifies its validation loss is worse than baseline by more than `1e-12`, so the baseline is retained.

## Snapshot, preview, activation, restore

The Application use case stores a snapshot containing algorithm version, source parameters, attempted candidate parameters, applied/result losses, candidate losses, sample count, UTC time, and status. Accepted results are previewed via `IFsrsDueDatePreviewer` before activation. Activation and restore append `FsrsParametersActivated` events to the focused store port. There is deliberately no rewrite-history or mass-reschedule API; fake counters remain zero. Rejected snapshots cannot be activated or restored. No Task 7 persistence implementation was pre-built.

## Pinned reference fixture

- Source: py-fsrs v6.3.0, commit `854a51496993d088175f6d7da0bbfaa414034c61`.
- Generator: `docs\algorithms\generate_fsrs6_optimizer_fixture.py`.
- Frozen file: `docs\algorithms\fsrs6-optimizer-fixture.json`.
- Predictions: `0.7696434024095495`, `0.8888277865983849`.
- Mean binary recall log loss: `1.2292513965155736`.
- C# assertions use 12 decimal places; regeneration hash/content comparison reported `FIXTURE_MATCH=True`.
- Python is development-only provenance tooling, never invoked by the runtime optimizer.

## Final commands and results

- `dotnet test tests\WordFlow.Infrastructure.Tests -c Release --filter FullyQualifiedName~Scheduling --no-restore`: 8/8 passed.
- `dotnet test tests\WordFlow.Application.Tests -c Release --filter FullyQualifiedName~Scheduling --no-restore`: 4/4 passed.
- `dotnet test WordFlow.sln -c Release --no-restore`: Domain 107/107, Infrastructure 8/8, Application 4/4; App project currently has no tests (119 tests passed total).
- `dotnet build WordFlow.sln -c Release --no-restore`: success, 0 warnings, 0 errors.
- `python -m unittest discover -s tools\vocabulary\tests`: 54/54 passed.
- Independent pinned optimizer fixture regeneration: exact match.
- `git diff --check`: clean apart from the expected Windows LF-to-CRLF informational warning for the existing Markdown file.

## Self-review and concerns

- Architecture is correct: Domain owns review sample/scheduling values; Application owns optimizer and snapshot ports/use case; Infrastructure implements the optimizer and references Application. Application does not reference Infrastructure.
- Task 4's scheduler and parameter bounds were not modified.
- All parameter outputs are 21 finite values accepted by `FsrsParameters`.
- The split trades sample efficiency for leakage safety by dropping boundary-crossing cards. Highly interleaved datasets can therefore become ineligible even with 400 valid events; this is intentional and documented.
- The bounded coordinate search is conservative and deterministic, not a global optimizer. Held-out validation protects activation from a worse candidate; future algorithms can increment the version while preserving snapshots.
- `IProgress<T>` itself may capture a synchronization context if a caller chooses `Progress<T>`; the optimizer has no UI dependency and never blocks or invokes a dispatcher. Callers needing worker-thread callbacks can provide a synchronous/custom progress sink as the tests do.
- The focused store is an Application port plus test fake only. Durable activation semantics still require the explicitly deferred Task 7 SQLite adapter.

## Independent-review remediation

The independent review reported six Important and two Minor findings. All were addressed with test-first changes.

### Explicit ineligibility and complete counts (I1, I6)

`OptimizationResult` and the persisted snapshot now carry the exact optimizer status (`NotEligible`, `InvalidData`, `Accepted`, or `BaselineRetained`), input/valid/filtered event counts, training/validation event counts, training/validation supervised observation counts, and a rejection reason. The legacy sample fields remain populated consistently for compatibility. Four hundred singleton cards now return `NotEligible` with 400 valid events and zero observations instead of throwing. A boundary-spanning fixture reports 400 valid events, 240 training events/120 observations, and zero validation events/observations, then returns `NotEligible`.

### Corrupt instants and numerical failure containment (I2, I3)

Filtering rejects instants after `DateTimeOffset.MaxValue - 1 day`, which cannot honor Task 4's minimum one-day due interval; such events increment the filtered count and no longer abort an otherwise eligible run. Every trial and final candidate evaluation is isolated from arithmetic/domain/representability exceptions. Unusable trials are skipped; an unusable final candidate retains the usable baseline with its candidate and exception reason audited. A baseline that cannot replay is classified `InvalidData`, never mislabeled as a candidate rejection. Tests exercise these paths through the real Task 4 scheduler with near-maximum instants and extreme but officially bounded parameter sets—there are no test-only evaluator hooks in production.

### Preview safety and restore provenance (I4, I5)

Accepted optimizer results are appended as `PendingPreview`. Preview success is required before the store transitions the snapshot to `ReadyForActivation`; preview exception or cancellation transitions it to `PreviewFailed`. If the ready transition itself fails, the durable snapshot remains pending and nonactivatable. Tests cover ordinary preview exceptions, preview cancellation, and ready-transition store failure.

Activation events now have their own IDs. `Restore` accepts only a prior activation-event ID returned by the store, resolves its snapshot, and appends a new activation carrying `SourceActivationId`. Snapshot IDs—including ready but never activated snapshots—and unknown/forged IDs are rejected. Trusted initial settings are represented by a trusted snapshot plus an initial activation event in the fake, not by a caller-constructible `RestoredSource` shortcut.

### Cancellation and zero-bound coordinates (M1, M2)

Explicit cancellation checks now cover duplicate counting, the filtering pass, pre/post sort, partition history construction, grouping, final partition sorts, replay, and candidate loops. Named tests cover cancellation before duplicate discovery and partitioning. Coordinates at zero now receive a deterministic positive additive step; all other coordinates retain decreasing multiplicative steps. A real optimization from parameter 14 at zero moves it positive, remains deterministic, and passes the unchanged official `FsrsParameters` bounds.

### Remediation verification

- `dotnet test tests\WordFlow.Infrastructure.Tests -c Release --filter FullyQualifiedName~Scheduling --no-restore`: 15/15 passed.
- `dotnet test tests\WordFlow.Application.Tests -c Release --filter FullyQualifiedName~Scheduling --no-restore`: 9/9 passed.
- `dotnet test WordFlow.sln -c Release --no-restore`: Domain 107/107, Infrastructure 15/15, Application 9/9; 131 tests passed total (App test assembly still has no tests).
- `dotnet build WordFlow.sln -c Release --no-restore`: 0 warnings, 0 errors.
- `python -m unittest discover -s tools\vocabulary\tests`: 54/54 passed.
- Pinned optimizer fixture regeneration: exact content match.
- `git diff --check`: clean except informational Windows line-ending warnings.

Remediation concern: the Task 5 port/fake proves state-machine intent but not durable transactional semantics. A Task 7 store must atomically enforce snapshot transitions and activation-event provenance; no SQLite adapter was added here.
