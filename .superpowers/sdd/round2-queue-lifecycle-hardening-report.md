# Round 2 Queue Lifecycle Hardening Report

Date: 2026-08-22
Worktree: `D:\baicizhan\.worktrees\wordflow-implementation`

## Status

Complete. The remediation keeps queue/session state durable and append-only, adds no schema migration, and does not alter the visual design.

## Root causes confirmed

1. `DailyQueueCoordinator.GetNextAsync` enumerated all learning cards and all vocabulary candidates before `GetOrCreateAsync` could discover an existing session.
2. Future same-day `Again` work was only materialized by `EnsureDueRelearningAsync` during a normal card read; the false-complete UI had no owner that could initiate that read.
3. A mutation contained only the captured queue item ID, so a card held across local midnight could complete yesterday's row.
4. Undo selected the latest queued event for `TimeProvider.GetLocalNow()` instead of the exact event/queue row that the VM had committed.

## Implemented behavior

- Added `IDailyQueueStore.GetAsync` and made the coordinator check today's persisted session before loading queue candidates. Existing sessions resolve the one targeted word/projection directly.
- Added `IDailyQueueStore.GetNextRelearningDueAsync`. SQLite derives the next unmaterialized same-day `Again` due time from immutable review events plus the current card projection.
- Added a dispatcher-backed `IdleWakeController`. The floating window owns the timer; the VM publishes absolute UTC schedules and reloads on a due tick. Schedules are cancelled/rearmed on card change, pause, hide/show, dispose, relearning due, and local midnight. The tick path never activates or focuses the window.
- Added mandatory `NextCard.QueueDay` and mandatory mutation `QueueDay` inputs.
- Before a cross-day mutation, today's durable queue is built/restored. If the same card is still today's next item, the mutation maps to today's item and commits there. Initial-revision new cards match by card ID + revision; persisted revisions also require an exact snapshot.
- If today's actual next card differs, no event/card/queue write occurs. The result is `LearningTransitionStatus.QueueDayRolledOver`; the VM shows today's actual card and announces that yesterday's click was not submitted and must be repeated.
- Added actual `QueueDay` metadata to queued `CommitResult`/`LearningTransition` and retained the committed event ID in VM undo state.
- Replaced current-day undo selection with `UndoQueuedAsync(command, completedEventId)`. SQLite locates the exact completed queue row by immutable event ID and reopens that row's actual session.

## TDD RED/GREEN evidence

Each production change followed a focused failing test:

- Persisted fast path RED: candidate repository threw from `GetWordsAsync`; GREEN after the existing-session read path.
- Durable wake RED: `GetNextRelearningDueAsync` was absent; GREEN with exact due time and null after materialization.
- VM wake RED: wake operation/event/timer contract was absent; GREEN after advancing fake time and manually firing the wake without another mutation.
- Dispatcher lifecycle RED: floating window had no idle-wake controller; GREEN with tested hide/pause/dispose/reschedule behavior.
- Queue-day RED: `NextCard.QueueDay` was absent, then optional; GREEN as a mandatory contract.
- Wrong-card midnight RED: mutation had no queue-day/status semantics; GREEN with explicit no-write rollover.
- Same-card midnight RED: synthetic initial-card `DueAt` differed after reconstruction; GREEN by initial-revision identity matching and today's queue-item remap.
- Exact undo RED: `UndoQueuedAsync(completedEventId)` was absent; GREEN restoring day one while a newer day-two event remains committed.

## Verification

Focused regression:

- `dotnet test tests\WordFlow.Domain.Tests\WordFlow.Domain.Tests.csproj --filter "FullyQualifiedName~QueuePolicyTests" --no-restore` — 10 passed.
- `dotnet test tests\WordFlow.Application.Tests\WordFlow.Application.Tests.csproj --filter "FullyQualifiedName~DailyQueue|FullyQualifiedName~LearningUseCaseTests" --no-restore` — 34 passed.
- `dotnet test tests\WordFlow.Infrastructure.Tests\WordFlow.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SqliteDailyQueueStoreTests|FullyQualifiedName~SqliteLearningStoreTests" --no-restore` — 50 passed.
- `dotnet test tests\WordFlow.App.Tests\WordFlow.App.Tests.csproj --filter "FullyQualifiedName~FloatingCardViewModelTests|FullyQualifiedName~IdleWakeControllerTests|FullyQualifiedName~DurableDailyLearningIntegrationTests" --no-restore` — 27 passed.

Fresh full four-project gate:

- `dotnet test tests\WordFlow.Domain.Tests\WordFlow.Domain.Tests.csproj --no-restore` — 130 passed.
- `dotnet test tests\WordFlow.Application.Tests\WordFlow.Application.Tests.csproj --no-restore` — 63 passed.
- `dotnet test tests\WordFlow.Infrastructure.Tests\WordFlow.Infrastructure.Tests.csproj --no-restore` — 227 passed.
- `dotnet test tests\WordFlow.App.Tests\WordFlow.App.Tests.csproj --no-restore` — 196 passed.

Total: 616 passed, 0 failed, 0 skipped.

Final post-contract focused Release verification:

- `dotnet test tests\WordFlow.Application.Tests\WordFlow.Application.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~DailyQueueCoordinatorTests|FullyQualifiedName~DailyQueueContractTests"` — 10 passed.
- `dotnet test tests\WordFlow.Infrastructure.Tests\WordFlow.Infrastructure.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SqliteDailyQueueStoreTests|FullyQualifiedName~Queued_undo_targets_the_committed_event"` — 12 passed.
- `dotnet test tests\WordFlow.App.Tests\WordFlow.App.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~FloatingCardViewModelTests|FullyQualifiedName~IdleWakeControllerTests"` — 20 passed.
- `dotnet build src\WordFlow.App\WordFlow.App.csproj -c Release --no-restore --verbosity quiet` — succeeded with 0 warnings and 0 errors.

## Residual concerns

- The absolute-time controller bounds any single dispatcher interval to one day and uses a one-minute retry only when the durable due-time lookup fails. Normal operation schedules the exact persisted due instant.
- Automated tests prove timer ownership and no activation call in the wake path; no manual Windows system-clock rollover exercise was performed in this task.
- No materially different store architecture was required: the changes are two read methods and exact-event targeting on the existing transactional learning store.
