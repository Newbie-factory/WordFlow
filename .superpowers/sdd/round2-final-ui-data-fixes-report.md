# Round 2 final UI/data fixes report

## Scope and outcome

- Baseline: `77dd88d` (`fix: harden durable queue lifecycle`).
- Dashboard daily and seven-day counts now read terminal `daily_queue_items` by durable `local_day`; UTC-midnight drift, pending/restored queue rows, undo, and non-queue restore events no longer inflate the count. The public dashboard contract is unchanged.
- Card hide now invokes a pronunciation-specific cancellation path. It cancels the active repetition token without changing `Autoplay` or its repeat count, hidden late-arriving cards remain silent, and showing the existing card does not restart playback.
- A singleton `LearningDataChangeNotifier` connects committed card mutations to an already-open control center. The control center marshals scheduling to its owner context, debounces refreshes by 75 ms, refreshes dashboard plus `LearningHistory`, unsubscribes/cancels on dispose, and ignores failed or day-rollover operations.
- History tooltip and automation text now include `PendingRelearning`; `IsToday` adds a visible two-pixel outline trigger.

## TDD evidence

- SQLite RED: local UTC+08 completion returned `0` instead of `1`; undo plus non-queue slash/restore returned `3` instead of `0`.
- Pronunciation RED: the focused test failed compilation because the hide-specific `OnCardHidden` contract did not exist.
- Shared-notification RED: focused card tests initially failed compilation because `LearningDataChangeNotifier` and its VM injection did not exist.
- The same focused cases pass after the minimal implementation.

## Fresh verification

- `dotnet test tests/WordFlow.Infrastructure.Tests/WordFlow.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SqliteLearningStoreTests.Daily_statistics"`: 2/2 passed.
- Focused Release App filter covering hide cancellation/show behavior, slash/undo notification and rejection cases, live open-dashboard refresh/disposal, pending-relearning accessibility, and today-outline XAML: 7/7 passed.
- `dotnet build WordFlow.sln -c Release --no-restore`: succeeded, 0 warnings, 0 errors.
- `git diff --check`: no whitespace errors; Git emitted only informational LF-to-CRLF notices.

## Self-review and concern

- Confirmed only successful committed transitions and successful undo publish; queue-day rollover and storage failures do not. Dispose removes the control-center subscription and cancels pending refresh work.
- Confirmed every hide route reaches `PrepareForHide` or the visibility fallback, both converge on `SetVisible(false)`, and duplicate hide notifications are gated by state.
- No full UI automation or republish was run, per request. The remaining verification limitation is therefore visual/runtime observation of the outline and real Windows speech cancellation; focused VM/XAML tests and the Release compile cover their contracts.
