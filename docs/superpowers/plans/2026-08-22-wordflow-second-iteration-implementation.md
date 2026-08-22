# WordFlow Second Iteration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver two differently branded but functionally identical offline Windows EXEs with smooth PNG/JPEG skins, durable daily learning sessions, six-month progress history, configurable strong topmost behavior, and 1–10 autoplay repetitions.

**Architecture:** Add a versioned SQLite daily-session ledger beside immutable review events, and make learning-event plus queue-item advancement one transaction. Split image preview from persistence so WPF only changes cached layer properties during dragging. Keep topmost and pronunciation policies behind small testable controllers, then compose them into the existing WPF windows and publish pipeline.

**Tech Stack:** .NET 8, WPF, C#, Microsoft.Data.Sqlite, System.Speech, xUnit, PowerShell, Pillow/ImageMagick-compatible icon generation through the bundled workspace Python runtime.

## Global Constraints

- Windows 10 22H2 and Windows 11, x64, completely offline.
- Preserve the existing IELTS data artifacts and FSRS-6 mathematics.
- Keep `%LOCALAPPDATA%\WordFlow` as the only user-data root and keep both EXEs on the same single-instance identity.
- No account, telemetry, HTTP client, online font, or online image dependency.
- Theme import accepts `.png`, `.jpg`, and `.jpeg` case-insensitively, with a 20 MB file cap and 8192×8192 dimension cap.
- Opacity preview range is exactly 0.00–1.00; persistence is debounced by approximately 250 ms and flushed on shutdown.
- Daily carryover fills within the configured new/review targets and never adds yesterday's remainder on top of today's targets.
- New-card autoplay repeat count is an integer from 1 through 10; manual playback remains one repetition.
- Strong topmost never activates or steals keyboard focus and stops when the card is hidden.
- Use test-first red/green cycles for every production behavior.

---

## File Structure

### Daily learning session

- Create `src/WordFlow.Application/Learning/DailyQueueModels.cs`: queue/session/history contracts.
- Create `src/WordFlow.Application/Ports/IDailyQueueStore.cs`: durable queue port and atomic mutation signatures.
- Create `src/WordFlow.Application/Learning/DailyQueueCoordinator.cs`: local-day creation, carryover, FSRS candidate classification, and next-item resolution.
- Create `src/WordFlow.Infrastructure/Data/Migrations/003_daily_sessions.sql`: strict tables, constraints, indexes, and schema migration.
- Create `src/WordFlow.Infrastructure/Data/SqliteDailyQueueStore.cs`: queue creation, carryover, history, and progress reads.
- Modify `src/WordFlow.Infrastructure/Data/SqliteLearningStore.cs`: share a transaction with queue completion/undo.
- Modify `src/WordFlow.Application/Learning/GetNextCard.cs`, `LearningMutation.cs`, and `UndoLastAction.cs`: use durable queue items.

### Progress UI

- Create `src/WordFlow.App/ViewModels/LearningHistoryViewModel.cs`: global slash progress and 183-day cells.
- Modify `src/WordFlow.App/ViewModels/ControlCenterViewModel.cs`: expose goal/history state.
- Modify `src/WordFlow.App/Views/ControlCenterWindow.xaml`: global goal card and accessible six-month calendar.

### Theme

- Rename `src/WordFlow.App/Bootstrap/PngThemeService.cs` to `ImageThemeService.cs`: generic PNG/JPEG validation/import and legacy setting migration.
- Create `src/WordFlow.App/Styling/ThemeVisualMapper.cs`: pure opacity-to-image/veil mapping.
- Create `src/WordFlow.App/Styling/ThemeImageCache.cs`: one decode per image path and frozen WPF brush.
- Modify `src/WordFlow.App/ViewModels/ThemeSettingsViewModel.cs`: immediate preview, debounced save, and flush.
- Modify both WPF windows and relation drawer controls to consume cached visuals without decoding on `ValueChanged`.

### Strong topmost

- Create `src/WordFlow.Infrastructure/Windows/TopmostPolicy.cs`: pure enabled/visible decision.
- Create `src/WordFlow.Infrastructure/Windows/StrongTopmostController.cs`: `SetWindowPos` adapter and 500 ms reassertion.
- Modify `FloatingCardWindow.xaml.cs`, `ControlCenterWindow.xaml`, `ControlCenterWindow.xaml.cs`, and `App.xaml.cs` for setting persistence and UI.

### Pronunciation

- Modify `PronunciationSettingsViewModel.cs` and its tests for a bounded repeat count and sequential cancellable autoplay.
- Modify `ControlCenterWindow.xaml` to expose a 1–10 selector.

### Branding and release

- Create `src/WordFlow.App/Assets/Icons/` with copied source PNGs and two derived multi-frame ICOs.
- Modify `WordFlow.App.csproj` to accept `WordFlowApplicationIcon` as an MSBuild property.
- Create `scripts/build-second-iteration.ps1` to produce both EXEs and a release manifest.

---

### Task 1: Pure Daily Queue Planning Contracts

**Files:**
- Create: `src/WordFlow.Application/Learning/DailyQueueModels.cs`
- Create: `src/WordFlow.Application/Ports/IDailyQueueStore.cs`
- Modify: `src/WordFlow.Domain/Learning/QueuePolicy.cs`
- Test: `tests/WordFlow.Domain.Tests/Learning/QueuePolicyTests.cs`
- Create: `tests/WordFlow.Application.Tests/Learning/DailyQueueContractTests.cs`

**Interfaces:**
- Consumes: `DailyPlan`, `CardState`, `QueuePolicy`, `DateOnly`, immutable `ReviewEvent` identifiers.
- Produces: `DailyQueueItemKind`, `DailyQueueItemStatus`, `DailyQueueSeed`, `DailyQueueItem`, `DailySessionSnapshot`, `DailyHistoryEntry`, `DailyQueuePlan`, and `IDailyQueueStore`.

- [ ] **Step 1: Write failing queue classification and contract tests**

Add tests that require separate ordered review/new lists and validate all queue invariants:

```csharp
[Fact]
public void BuildPlan_separates_risk_ordered_reviews_from_deterministic_new_cards()
{
    var plan = policy.BuildPlan(new QueueInput(dueCards, newIds, new DailyPlan(2, 1), now));
    Assert.Single(plan.ReviewCardIds);
    Assert.Equal(2, plan.NewCardIds.Count);
    Assert.DoesNotContain(plan.ReviewCardIds[0], plan.NewCardIds);
}

[Theory]
[InlineData(DailyQueueItemKind.New, DailyQueueItemStatus.Pending)]
[InlineData(DailyQueueItemKind.Review, DailyQueueItemStatus.Completed)]
[InlineData(DailyQueueItemKind.Relearning, DailyQueueItemStatus.CarriedForward)]
public void Queue_item_contract_accepts_all_persisted_states(
    DailyQueueItemKind kind, DailyQueueItemStatus status)
{
    var item = new DailyQueueItem(Guid.NewGuid(), day, 0, cardId, kind, day, null, status, null, null);
    Assert.Equal(kind, item.Kind);
    Assert.Equal(status, item.Status);
}
```

- [ ] **Step 2: Run the tests and verify red**

Run:

```powershell
dotnet test tests\WordFlow.Domain.Tests --filter FullyQualifiedName~QueuePolicyTests
dotnet test tests\WordFlow.Application.Tests --filter FullyQualifiedName~DailyQueueContractTests
```

Expected: compilation fails because `BuildPlan`, `DailyQueuePlan`, and daily queue contracts do not exist.

- [ ] **Step 3: Add the pure models and planner result**

Implement these exact public shapes:

```csharp
public enum DailyQueueItemKind { New, Review, Relearning }
public enum DailyQueueItemStatus { Pending, Completed, Slashed, CarriedForward }

public sealed record DailyQueuePlan(
    IReadOnlyList<Guid> ReviewCardIds,
    IReadOnlyList<Guid> NewCardIds);

public sealed record DailyQueueSeed(
    DateOnly LocalDay,
    DailyPlan ConfiguredPlan,
    IReadOnlyList<Guid> OrderedReviewCandidates,
    IReadOnlyList<Guid> OrderedNewCandidates,
    DateTimeOffset CreatedAt);

public sealed record DailyQueueItem(
    Guid ItemId,
    DateOnly LocalDay,
    int Ordinal,
    Guid CardId,
    DailyQueueItemKind Kind,
    DateOnly OriginDay,
    Guid? SourceItemId,
    DailyQueueItemStatus Status,
    Guid? CompletedEventId,
    DateTimeOffset? CompletedAt);

public sealed record DailySessionSnapshot(
    DateOnly LocalDay,
    DailyPlan ConfiguredPlan,
    DailyPlan EffectivePlan,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<DailyQueueItem> Items);

public sealed record DailyHistoryEntry(
    DateOnly LocalDay,
    int NewCompleted,
    int NewTarget,
    int ReviewCompleted,
    int ReviewTarget,
    int PendingRelearning,
    bool WasStarted,
    bool WasCompleted);
```

Add `QueuePolicy.BuildPlan(QueueInput)` and keep `Build(QueueInput)` as `ReviewCardIds.Concat(NewCardIds)` for compatibility.

- [ ] **Step 4: Define the durable store port**

```csharp
public interface IDailyQueueStore
{
    Task<DailySessionSnapshot> GetOrCreateAsync(DailyQueueSeed seed, CancellationToken ct);
    Task<DailyQueueItem?> GetNextPendingAsync(DateOnly localDay, DateTimeOffset now, CancellationToken ct);
    Task EnsureDueRelearningAsync(DateOnly localDay, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<DailyHistoryEntry>> GetHistoryAsync(DateOnly throughDay, int dayCount, CancellationToken ct);
    Task<GoalProgress> GetGoalProgressAsync(CancellationToken ct);
}

public sealed record GoalProgress(int SlashedWords, int TotalWords)
{
    public double Ratio => TotalWords == 0 ? 0 : Math.Clamp((double)SlashedWords / TotalWords, 0, 1);
}
```

- [ ] **Step 5: Run tests and commit**

Run both commands from Step 2 and `dotnet test tests\WordFlow.Application.Tests --no-restore`. Expected: all pass.

```powershell
git add src\WordFlow.Domain src\WordFlow.Application tests\WordFlow.Domain.Tests tests\WordFlow.Application.Tests
git commit -m "feat: define durable daily learning queue"
```

---

### Task 2: SQLite Daily Sessions, Carryover, History, and Goal Progress

**Files:**
- Create: `src/WordFlow.Infrastructure/Data/Migrations/003_daily_sessions.sql`
- Create: `src/WordFlow.Infrastructure/Data/SqliteDailyQueueStore.cs`
- Modify: `src/WordFlow.Infrastructure/Data/SqliteLearningStore.cs`
- Modify: `src/WordFlow.App/Bootstrap/ServiceRegistration.cs`
- Create: `tests/WordFlow.Infrastructure.Tests/Data/SqliteDailyQueueStoreTests.cs`
- Modify: `tests/WordFlow.Infrastructure.Tests/Data/SqliteLearningStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 models and `SqliteConnectionFactory`.
- Produces: `SqliteDailyQueueStore : IDailyQueueStore`; atomic queued apply and queued undo methods on `ILearningStore`.

- [ ] **Step 1: Write failing migration and carryover tests**

Cover schema 3, idempotent same-day creation, exact ordinal restore, 27 carryover + 13 fill, carryover larger than target, `source_item_id` lineage, and 183-day history.

```csharp
[Fact]
public async Task New_day_carries_27_then_adds_13_to_a_40_item_new_target()
{
    await SeedYesterdayAsync(newTarget: 40, completedNew: 13);
    var today = await store.GetOrCreateAsync(Seed(todayDate, newTarget: 40, newCandidates: 100), default);
    Assert.Equal(40, today.EffectivePlan.NewLimit);
    Assert.Equal(27, today.Items.Count(x => x.Kind == DailyQueueItemKind.New && x.SourceItemId.HasValue));
    Assert.Equal(13, today.Items.Count(x => x.Kind == DailyQueueItemKind.New && x.SourceItemId is null));
    Assert.Equal(27, await CountYesterdayAsync(DailyQueueItemStatus.CarriedForward));
}
```

- [ ] **Step 2: Verify red**

Run:

```powershell
dotnet test tests\WordFlow.Infrastructure.Tests --filter FullyQualifiedName~SqliteDailyQueueStoreTests
```

Expected: compilation fails because `SqliteDailyQueueStore` and migration 003 do not exist.

- [ ] **Step 3: Add strict migration 003**

Create tables with the following core DDL and add CHECK constraints for every enum/date/UUID field:

```sql
CREATE TABLE daily_sessions (
    local_day TEXT PRIMARY KEY CHECK(length(local_day) = 10),
    configured_new_target INTEGER NOT NULL CHECK(configured_new_target >= 0),
    configured_review_target INTEGER NOT NULL CHECK(configured_review_target >= 0),
    effective_new_target INTEGER NOT NULL CHECK(effective_new_target >= 0),
    effective_review_target INTEGER NOT NULL CHECK(effective_review_target >= 0),
    completed_at_utc TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
) STRICT;

CREATE TABLE daily_queue_items (
    item_id TEXT PRIMARY KEY CHECK(length(item_id) = 36),
    local_day TEXT NOT NULL REFERENCES daily_sessions(local_day),
    ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
    card_id TEXT NOT NULL CHECK(length(card_id) = 36),
    kind TEXT NOT NULL CHECK(kind IN ('New','Review','Relearning')),
    origin_day TEXT NOT NULL CHECK(length(origin_day) = 10),
    source_item_id TEXT NULL REFERENCES daily_queue_items(item_id),
    status TEXT NOT NULL CHECK(status IN ('Pending','Completed','Slashed','CarriedForward')),
    completed_event_id TEXT NULL REFERENCES review_event(event_id),
    created_at_utc TEXT NOT NULL,
    completed_at_utc TEXT NULL,
    UNIQUE(local_day, ordinal)
) STRICT;

CREATE INDEX ix_daily_queue_next ON daily_queue_items(local_day, status, ordinal);
CREATE INDEX ix_daily_queue_backlog ON daily_queue_items(status, local_day, ordinal);
CREATE INDEX ix_daily_queue_card ON daily_queue_items(card_id, local_day, status);
```

- [ ] **Step 4: Implement transactional creation and reads**

`GetOrCreateAsync` must begin an immediate transaction, re-read the session, select prior pending items ordered by `(local_day, ordinal)`, cap each kind at today's target, mark selected source rows `CarriedForward`, insert linked new rows, fill remaining capacity from the seed without duplicate card IDs, compute effective targets, and commit once.

`GetNextPendingAsync` orders `Relearning` before `Review` before `New`, then by ordinal. `EnsureDueRelearningAsync` inserts only cards with a same-local-day `Again` event whose `card_state.due_at_utc <= now`, clears `daily_session.completed_at_utc`, and is idempotent by card/day/status.

- [ ] **Step 5: Add atomic queued event methods**

Extend `ILearningStore` and `SqliteLearningStore` with:

```csharp
Task<CommitResult> ApplyQueuedAsync(
    LearningCommand command,
    Guid queueItemId,
    DailyQueueItemStatus terminalStatus,
    CancellationToken ct);

Task<CommitResult> UndoLatestQueuedAsync(
    UndoLearningCommand command,
    DateOnly localDay,
    CancellationToken ct);
```

`ApplyQueuedAsync` inserts the immutable event, updates `card_state`, completes the pending queue row, and recomputes session completion inside the existing write transaction. `UndoLatestQueuedAsync` appends the compensating event, restores the matching queue row to `Pending`, clears its completion columns, and clears session completion in the same transaction.

- [ ] **Step 6: Implement history and global progress**

`GetHistoryAsync(throughDay, 183)` returns one entry for every date, filling absent sessions with `WasStarted=false`. `GetGoalProgressAsync` counts distinct currently slashed card rows and the attached vocabulary database's `is_learning_headword=1` rows.

- [ ] **Step 7: Run tests and commit**

```powershell
dotnet test tests\WordFlow.Infrastructure.Tests --filter "FullyQualifiedName~SqliteDailyQueueStoreTests|FullyQualifiedName~SqliteLearningStoreTests"
git add src\WordFlow.Infrastructure src\WordFlow.Application\Ports src\WordFlow.App\Bootstrap tests\WordFlow.Infrastructure.Tests
git commit -m "feat: persist daily sessions and carryover"
```

---

### Task 3: Resume the Exact Current Card and Advance the Durable Queue

**Files:**
- Create: `src/WordFlow.Application/Learning/DailyQueueCoordinator.cs`
- Modify: `src/WordFlow.Application/Learning/GetNextCard.cs`
- Modify: `src/WordFlow.Application/Learning/LearningMutation.cs`
- Modify: `src/WordFlow.Application/Learning/SubmitRating.cs`
- Modify: `src/WordFlow.Application/Learning/SlashWord.cs`
- Modify: `src/WordFlow.Application/Learning/UndoLastAction.cs`
- Modify: `src/WordFlow.App/ViewModels/FloatingCardViewModel.cs`
- Modify: `src/WordFlow.App/Bootstrap/ServiceRegistration.cs`
- Create: `tests/WordFlow.Application.Tests/Learning/DailyQueueCoordinatorTests.cs`
- Modify: `tests/WordFlow.Application.Tests/Learning/LearningUseCaseTests.cs`
- Modify: `tests/WordFlow.App.Tests/ViewModels/FloatingCardViewModelTests.cs`

**Interfaces:**
- Consumes: `IDailyQueueStore`, atomic queued store methods, `QueuePolicy`, repositories, `TimeProvider`.
- Produces: `NextCard.QueueItemId`, restart-safe advancement, local-day rollover, and queue-aware undo.

- [ ] **Step 1: Write restart, rollover, atomic failure, and undo tests**

```csharp
[Fact]
public async Task Reopening_after_thirteen_of_forty_returns_the_same_fourteenth_card()
{
    var firstRun = await coordinator.GetNextAsync(plan, default);
    for (int i = 0; i < 13; i++)
        firstRun = await RateAndAdvanceAsync(firstRun!, RatingShortcut.F3);
    Guid expected = firstRun!.Word.WordId;

    var reopened = NewCoordinatorAgainstSameDatabase();
    var resumed = await reopened.GetNextAsync(plan, default);

    Assert.Equal(expected, resumed!.Word.WordId);
}
```

Also verify that a failure after event insertion rolls back both event and queue status, and that undo returns the prior card as pending.

- [ ] **Step 2: Verify red**

```powershell
dotnet test tests\WordFlow.Application.Tests --filter "FullyQualifiedName~DailyQueueCoordinatorTests|FullyQualifiedName~LearningUseCaseTests"
```

Expected: failures because `NextCard` has no queue identity and `GetNextCard` rebuilds the queue on every call.

- [ ] **Step 3: Implement the coordinator**

Use this public surface:

```csharp
public sealed class DailyQueueCoordinator
{
    public Task<NextCard?> GetNextAsync(DailyPlan plan, CancellationToken ct);
    public DateOnly CurrentLocalDay => DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
}

public sealed record NextCard(
    CardState Card,
    Guid Revision,
    VocabularyWord Word,
    CurrentPrimarySense? PrimarySense,
    Guid QueueItemId,
    DailyQueueItemKind QueueKind);
```

The coordinator loads cards and headwords, calls `QueuePolicy.BuildPlan`, creates/restores today's durable session, reconciles due relearning, fetches the next pending row, then resolves the vocabulary and card projection.
With a 40-word new target, after 13 successful queue-item transitions a fresh process must resolve the persisted 14th item rather than rebuilding a different headword order.

- [ ] **Step 4: Route mutations through atomic queue advancement**

Change `LearningMutation.CommitAndAdvanceAsync` to require the captured `QueueItemId` and call `ApplyQueuedAsync`. Slash passes `DailyQueueItemStatus.Slashed`; ratings pass `Completed`. Undo calls `UndoLatestQueuedAsync` with the current local day. Only call `GetNextAsync` after the transaction succeeds.

- [ ] **Step 5: Remove the visible instructional/undo row while retaining the command**

Change `FloatingCardViewModel.ProgressText` usage so no XAML depends on it. Keep `UndoCommand`, `UndoGesture`, `HandleShortcutAsync(ShortcutAction.Undo)`, and the existing default `Ctrl+Z` binding intact.

- [ ] **Step 6: Run tests and commit**

```powershell
dotnet test tests\WordFlow.Application.Tests --filter "FullyQualifiedName~DailyQueue|FullyQualifiedName~LearningUseCase"
dotnet test tests\WordFlow.App.Tests --filter FullyQualifiedName~FloatingCardViewModelTests
git add src\WordFlow.Application src\WordFlow.App tests\WordFlow.Application.Tests tests\WordFlow.App.Tests
git commit -m "feat: resume and advance durable daily learning"
```

---

### Task 4: Total Slash Goal and Six-Month Learning History UI

**Files:**
- Create: `src/WordFlow.App/ViewModels/LearningHistoryViewModel.cs`
- Modify: `src/WordFlow.App/ViewModels/ControlCenterViewModel.cs`
- Modify: `src/WordFlow.App/Views/ControlCenterWindow.xaml`
- Create: `tests/WordFlow.App.Tests/ViewModels/LearningHistoryViewModelTests.cs`
- Modify: `tests/WordFlow.App.Tests/Views/ControlCenterWindowChromeTests.cs`

**Interfaces:**
- Consumes: `IDailyQueueStore.GetHistoryAsync`, `GetGoalProgressAsync`.
- Produces: `GoalProgressText`, `GoalProgressPercentText`, `GoalProgressRatio`, and 183 `LearningDayCellViewModel` items.

- [ ] **Step 1: Write failing view-model and XAML structure tests**

```csharp
[Fact]
public async Task Load_exposes_slash_ratio_and_exactly_183_accessible_day_cells()
{
    await viewModel.LoadAsync(default);
    Assert.Equal("3 / 12046", viewModel.GoalProgressText);
    Assert.Equal(183, viewModel.Days.Count);
    Assert.Equal("已完成", viewModel.Days.Single(x => x.Day == completedDay).StatusText);
}
```

The XAML test requires a progress bar bound one-way to `GoalProgressRatio`, an items control bound to `LearningHistory.Days`, and an automation name bound to each cell's `AutomationName`.

- [ ] **Step 2: Verify red**

```powershell
dotnet test tests\WordFlow.App.Tests --filter "FullyQualifiedName~LearningHistoryViewModelTests|FullyQualifiedName~ControlCenterWindowChromeTests"
```

Expected: compilation/structure failures because the history VM and bindings do not exist.

- [ ] **Step 3: Implement the history view model**

```csharp
public sealed record LearningDayCellViewModel(
    DateOnly Day,
    string StatusText,
    string AutomationName,
    string Tooltip,
    string FillKey,
    bool IsToday);

public sealed class LearningHistoryViewModel : INotifyPropertyChanged
{
    public ObservableCollection<LearningDayCellViewModel> Days { get; } = [];
    public int SlashedWords { get; private set; }
    public int TotalWords { get; private set; }
    public double GoalProgressRatio { get; private set; }
    public string GoalProgressText => $"{SlashedWords} / {TotalWords}";
    public string GoalProgressPercentText => GoalProgressRatio.ToString("P2", CultureInfo.CurrentCulture);
    public Task LoadAsync(CancellationToken ct);
}
```

Map complete to green plus check glyph, started/incomplete to amber plus dot, and absent to neutral plus hollow dot. Generate descriptive Tooltip and automation text for every cell.

- [ ] **Step 4: Integrate the dashboard**

Replace the existing “累计已斩” summary with a goal card that contains exact counts, percent, and a progress bar. Add a “最近六个月” card below the seven-day section using a `WrapPanel` with 14–16 DIP cells and a compact legend. Load history in dashboard refresh and after slash restore.

- [ ] **Step 5: Run tests, inspect at 920 and 1120 DIP widths, and commit**

```powershell
dotnet test tests\WordFlow.App.Tests --filter "FullyQualifiedName~LearningHistory|FullyQualifiedName~ControlCenter"
git add src\WordFlow.App tests\WordFlow.App.Tests
git commit -m "feat: show total slash goal and six month history"
```

---

### Task 5: Generic PNG/JPEG Theme Import and Legacy Restore

**Files:**
- Rename: `src/WordFlow.App/Bootstrap/PngThemeService.cs` → `src/WordFlow.App/Bootstrap/ImageThemeService.cs`
- Rename: `tests/WordFlow.App.Tests/PngThemeServiceTests.cs` → `tests/WordFlow.App.Tests/ImageThemeServiceTests.cs`
- Modify: `src/WordFlow.App/Bootstrap/ServiceRegistration.cs`
- Modify: `src/WordFlow.App/ViewModels/ThemeSettingsViewModel.cs`

**Interfaces:**
- Consumes: `AppPaths`, `SqliteAppSettingStore`, WPF bitmap decoders.
- Produces: `ImageTheme`, `ImageThemeService.ValidateImage`, safe import, opacity 0–1, and compatibility with `theme.png_path`.

- [ ] **Step 1: Write failing JPEG, signature, range, and migration tests**

Generate real 2×2 PNG and JPEG files in tests. Require `.JPG`/`.JPEG` acceptance, renamed fake rejection, corrupt JPEG rejection, 0/1 opacity preservation, and restoration from the legacy key.

```csharp
[Theory]
[InlineData("skin.jpg")]
[InlineData("skin.JPEG")]
public void Validator_accepts_real_jpeg_case_insensitively(string name)
{
    string path = WriteJpeg(name);
    Assert.True(ImageThemeService.TryValidateImage(path, out var format, out var error), error);
    Assert.Equal(ThemeImageFormat.Jpeg, format);
}
```

- [ ] **Step 2: Verify red**

```powershell
dotnet test tests\WordFlow.App.Tests --filter FullyQualifiedName~ImageThemeServiceTests
```

Expected: compilation fails because `ImageThemeService` and JPEG support do not exist.

- [ ] **Step 3: Implement generic validation and import**

Use PNG signature `89 50 4E 47 0D 0A 1A 0A` and JPEG prefix `FF D8 FF`, then decode with `BitmapDecoder.Create(..., BitmapCacheOption.OnLoad)`. Copy to `theme-{Guid:N}.png` or `.jpg` according to detected format, never according to the untrusted source extension.

```csharp
public sealed record ImageTheme(string ImagePath, double Opacity)
{
    public bool IsDefault => string.IsNullOrWhiteSpace(ImagePath);
    public static ImageTheme Default => new(string.Empty, 1d);
}

public static double ClampOpacity(double value) =>
    double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : 1d;
```

Read `theme.image_path` first, fall back to `theme.png_path`, and write only the new key after successful restore/import.

- [ ] **Step 4: Run tests and commit**

```powershell
dotnet test tests\WordFlow.App.Tests --filter FullyQualifiedName~ImageThemeServiceTests
git add src\WordFlow.App tests\WordFlow.App.Tests
git commit -m "feat: support png and jpeg skins"
```

---

### Task 6: Jank-Free Theme Preview and Card Cleanup

**Files:**
- Create: `src/WordFlow.App/Styling/ThemeVisualMapper.cs`
- Create: `src/WordFlow.App/Styling/ThemeImageCache.cs`
- Modify: `src/WordFlow.App/ViewModels/ThemeSettingsViewModel.cs`
- Modify: `src/WordFlow.App/Views/FloatingCardWindow.xaml`
- Modify: `src/WordFlow.App/Views/FloatingCardWindow.xaml.cs`
- Modify: `src/WordFlow.App/Views/ControlCenterWindow.xaml`
- Modify: `src/WordFlow.App/Views/ControlCenterWindow.xaml.cs`
- Modify: `src/WordFlow.App/Views/Controls/RelationDrawer.xaml.cs`
- Create: `tests/WordFlow.App.Tests/Styling/ThemeVisualMapperTests.cs`
- Create: `tests/WordFlow.App.Tests/Styling/ThemeImageCacheTests.cs`
- Modify: `tests/WordFlow.App.Tests/Views/FloatingCardAccessibilityTests.cs`

**Interfaces:**
- Consumes: `ImageTheme`, `ImageThemeService.SaveAsync`.
- Produces: cached/frozen `ImageBrush`, immediate preview events, debounced persistence, and `FlushAsync`.

- [ ] **Step 1: Write failing visual mapping, cache, debounce, and XAML tests**

```csharp
[Theory]
[InlineData(0.0, 0.0, 0.88)]
[InlineData(1.0, 1.0, 0.12)]
public void Map_uses_full_image_range_and_inverse_readability_veil(
    double slider, double expectedImage, double expectedVeil)
{
    var state = ThemeVisualMapper.Map(slider);
    Assert.Equal(expectedImage, state.ImageOpacity, 3);
    Assert.Equal(expectedVeil, state.VeilOpacity, 3);
}
```

Use a counting decoder and counting store to prove 100 rapid opacity changes decode once and persist once after the debounce interval. XAML tests require `Minimum="0"`, `Maximum="1"`, `IsMoveToPointEnabled="True"`, and absence of the old progress/undo row.

- [ ] **Step 2: Verify red**

```powershell
dotnet test tests\WordFlow.App.Tests --filter "FullyQualifiedName~ThemeVisual|FullyQualifiedName~ThemeImageCache|FullyQualifiedName~FloatingCardAccessibility"
```

Expected: missing types, old 0.15 minimum, and visible undo row failures.

- [ ] **Step 3: Implement the pure mapping and image cache**

```csharp
public sealed record ThemeVisualState(double ImageOpacity, double VeilOpacity);

public static ThemeVisualState Map(double opacity)
{
    double value = ImageThemeService.ClampOpacity(opacity);
    return new(value, 0.88d - (0.76d * value));
}
```

`ThemeImageCache.Get(path)` decodes on load, creates `ImageBrush { Stretch = Stretch.Fill }`, freezes the bitmap and brush, and returns the same brush reference for the same normalized path until the path changes.

- [ ] **Step 4: Split preview from persistence**

`ThemeSettingsViewModel.Opacity` updates the in-memory model and raises `ThemeChanged` synchronously on the owning UI context. It cancels and replaces a 250 ms `CancellationTokenSource` for persistence. Add:

```csharp
public Task FlushAsync(CancellationToken ct = default);
public event EventHandler? ThemeChanged;
```

`FlushAsync` awaits the last debounce and persists the latest snapshot exactly once. Import/reset cancel pending opacity writes before their own durable write.

- [ ] **Step 5: Update WPF rendering and remove the card row**

Reuse the cached brush when only opacity changes. Apply `ThemeVisualMapper` to the image and veil layers. Set both sliders to 0–1 with `IsMoveToPointEnabled=True`, bind one-way-to-source-safe values, and update only the numeric layers during `ValueChanged`.

Remove the `DockPanel` containing `ProgressText` and `UndoButton`; reduce `StableWordBlock` height and keep all rating/relation tab indices contiguous. Keep `UndoCommand` reachable by shortcut.

- [ ] **Step 6: Run tests and commit**

```powershell
dotnet test tests\WordFlow.App.Tests --filter "FullyQualifiedName~Theme|FullyQualifiedName~FloatingCardAccessibility"
git add src\WordFlow.App tests\WordFlow.App.Tests
git commit -m "perf: make skin opacity preview continuous"
```

---

### Task 7: Configurable Strong Topmost Without Focus Theft

**Files:**
- Create: `src/WordFlow.Infrastructure/Windows/TopmostPolicy.cs`
- Create: `src/WordFlow.Infrastructure/Windows/StrongTopmostController.cs`
- Modify: `src/WordFlow.App/Views/FloatingCardWindow.xaml.cs`
- Modify: `src/WordFlow.App/Views/ControlCenterWindow.xaml`
- Modify: `src/WordFlow.App/Views/ControlCenterWindow.xaml.cs`
- Modify: `src/WordFlow.App/App.xaml.cs`
- Create: `tests/WordFlow.Infrastructure.Tests/Windows/StrongTopmostControllerTests.cs`
- Modify: `tests/WordFlow.App.Tests/Views/FloatingCardUiSmokeModelTests.cs`

**Interfaces:**
- Consumes: HWND, `SqliteAppSettingStore`, card visibility.
- Produces: `AlwaysOnTopEnabled`, `IWindowZOrder`, 500 ms reassertion, `HWND_NOTOPMOST` downgrade.

- [ ] **Step 1: Write failing policy/controller tests**

Use a fake `IWindowZOrder` and manual tick source. Verify enabled+visible calls `SetTopmost(noActivate:true)`, disabled calls `SetNotTopmost`, hidden ticks do nothing, repeated visible ticks reassert, and dispose stops all calls.

- [ ] **Step 2: Verify red**

```powershell
dotnet test tests\WordFlow.Infrastructure.Tests --filter FullyQualifiedName~StrongTopmostControllerTests
```

Expected: compilation fails because the controller does not exist.

- [ ] **Step 3: Implement the controller and native adapter**

```csharp
public interface IWindowZOrder
{
    void SetTopmost(nint hwnd, bool noActivate);
    void SetNotTopmost(nint hwnd, bool noActivate);
}

public sealed class StrongTopmostController : IDisposable
{
    public bool Enabled { get; set; }
    public bool Visible { get; set; }
    public void Reassert();
    public void Dispose();
}
```

The native implementation uses `SetWindowPos(HWND_TOPMOST, ...)` with `SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE`, and `SetWindowPos(HWND_NOTOPMOST, ...)` when disabled. It never calls `SetForegroundWindow`.

- [ ] **Step 4: Integrate settings and card lifecycle**

Persist `floating_card.always_on_top`, default `true`. In the appearance settings add a checkbox bound to the view model. Start/reassert on show, `Activated`, `Deactivated`, and every 500 ms. Set `Visible=false` before hide. When disabled, set the card and both relation popups to not-topmost. Retire fullscreen suppression from the user-facing behavior without deleting legacy data.

- [ ] **Step 5: Run tests and commit**

```powershell
dotnet test tests\WordFlow.Infrastructure.Tests --filter FullyQualifiedName~Topmost
dotnet test tests\WordFlow.App.Tests --filter "FullyQualifiedName~FloatingCardUiSmoke|FullyQualifiedName~ControlCenter"
git add src\WordFlow.Infrastructure src\WordFlow.App tests
git commit -m "feat: add configurable strong topmost"
```

---

### Task 8: Sequential 1–10 New-Card Autoplay Repetitions

**Files:**
- Modify: `src/WordFlow.App/ViewModels/PronunciationSettingsViewModel.cs`
- Modify: `src/WordFlow.App/Views/ControlCenterWindow.xaml`
- Modify: `tests/WordFlow.App.Tests/ViewModels/PronunciationSettingsViewModelTests.cs`

**Interfaces:**
- Consumes: existing `IPronunciationService` cancellation and playback result.
- Produces: `AutoplayRepeatCount`, persisted key, sequential cancellable repeat loop.

- [ ] **Step 1: Write failing persistence and playback tests**

```csharp
[Theory]
[InlineData("0", 1)]
[InlineData("11", 1)]
[InlineData("10", 10)]
public async Task Restore_sanitizes_repeat_count_to_one_through_ten(string stored, int expected)
{
    await settings.SetRawAsync(PronunciationSettingsViewModel.AutoplayRepeatCountKey, stored);
    await viewModel.RestoreAsync();
    Assert.Equal(expected, viewModel.AutoplayRepeatCount);
}

[Fact]
public async Task Autoplay_ten_repetitions_are_serial_and_card_change_cancels_the_remainder()
{
    viewModel.Autoplay = true;
    viewModel.AutoplayRepeatCount = 10;
    viewModel.OnCardChanged(cardA, "alpha", false);
    await speech.WaitForCallsAsync(1);
    speech.Complete(0, PronunciationPlaybackResult.Completed("alpha"));
    await speech.WaitForCallsAsync(2);
    viewModel.OnCardChanged(cardB, "beta", false);
    Assert.True(speech.Pending[1].CancellationToken.IsCancellationRequested);
}
```

Also assert manual `Execute` produces one call even when repeat count is 10.

- [ ] **Step 2: Verify red**

```powershell
dotnet test tests\WordFlow.App.Tests --filter FullyQualifiedName~PronunciationSettingsViewModelTests
```

Expected: missing repeat property/key and one-call autoplay behavior failures.

- [ ] **Step 3: Implement bounded persistence and sequential playback**

Add `public const string AutoplayRepeatCountKey = "pronunciation.autoplay_repeat_count";`, default 1, validation 1–10, restore repair, save, and property notification.

Replace autoplay publication with:

```csharp
private async Task PlayAutoplaySequenceAsync(PlaybackOperation operation, string word, int count)
{
    var voice = ResolveVoice();
    if (voice is null) { PublishIfCurrent(operation.Generation, PronunciationPlaybackResult.Unavailable(service.Availability.Message)); return; }
    for (int repetition = 0; repetition < count; repetition++)
    {
        operation.Cancellation.Token.ThrowIfCancellationRequested();
        var result = await service.SpeakAsync(word, voice.Id, Rate, Volume, operation.Cancellation.Token).ConfigureAwait(false);
        if (result.Status != PronunciationPlaybackStatus.Completed) { PublishIfCurrent(operation.Generation, result); return; }
    }
    PublishIfCurrent(operation.Generation, PronunciationPlaybackResult.Completed(word));
}
```

Keep `Execute` on the existing one-call manual path.

- [ ] **Step 4: Add the 1–10 selector and run tests**

Add a labeled `ComboBox` or integer selector whose items are exactly 1 through 10 and whose selected value binds to `Pronunciation.AutoplayRepeatCount`. Disable it visually when autoplay is off but retain its selected value.

```powershell
dotnet test tests\WordFlow.App.Tests --filter FullyQualifiedName~PronunciationSettingsViewModelTests
git add src\WordFlow.App tests\WordFlow.App.Tests
git commit -m "feat: add configurable autoplay repetitions"
```

---

### Task 9: Generate Two Square Multi-Resolution Icons and Two EXEs

**Files:**
- Create: `src/WordFlow.App/Assets/Icons/photo-source.png`
- Create: `src/WordFlow.App/Assets/Icons/wordflow-source.png`
- Create: `src/WordFlow.App/Assets/Icons/wordflow-photo.ico`
- Create: `src/WordFlow.App/Assets/Icons/wordflow-classic.ico`
- Modify: `src/WordFlow.App/WordFlow.App.csproj`
- Create: `tools/branding/build_icons.py`
- Create: `scripts/build-second-iteration.ps1`
- Create: `tests/WordFlow.App.Tests/Bootstrap/BrandingArtifactTests.cs`

**Interfaces:**
- Consumes: the two user-provided source PNGs and normal Release publish inputs.
- Produces: reproducible ICO assets, `WordFlow-Photo.exe`, `WordFlow-Classic.exe`, and `release-manifest.json`.

- [ ] **Step 1: Write the failing branding artifact test**

The test parses both ICO directories and requires frames `[16,24,32,48,64,128,256]`, square dimensions, nonempty alpha/visual content, and different SHA-256 hashes.

- [ ] **Step 2: Verify red**

```powershell
dotnet test tests\WordFlow.App.Tests --filter FullyQualifiedName~BrandingArtifactTests
```

Expected: fails because ICO assets do not exist.

- [ ] **Step 3: Copy sources and implement deterministic icon generation**

Use Pillow with LANCZOS resampling. Center-crop the photo to a square. Place the complete WordFlow wordmark on a transparent 512×512 canvas with 8% horizontal safe margin. Save ICO frames in all required sizes. The script accepts explicit `--photo`, `--wordflow`, and `--out-dir` paths and never writes to `D:\baicizhan\icon`.

- [ ] **Step 4: Make the application icon build-selectable**

Add:

```xml
<PropertyGroup>
  <WordFlowApplicationIcon Condition="'$(WordFlowApplicationIcon)' == ''">Assets\Icons\wordflow-classic.ico</WordFlowApplicationIcon>
  <ApplicationIcon>$(WordFlowApplicationIcon)</ApplicationIcon>
</PropertyGroup>
```

Ensure `TrayIconService` extracts the associated icon from `Environment.ProcessPath` so each build has a matching tray icon.

- [ ] **Step 5: Implement the dual publish script**

The script publishes twice to temporary subfolders with the selected icon, copies/renames the single-file outputs to one release folder, copies the shared `Data` directory once, and writes SHA-256, size, icon source hash, commit, and build timestamp to `release-manifest.json`.

Final names:

```text
D:\baicizhan\release\WordFlow-second-iteration\WordFlow-Photo.exe
D:\baicizhan\release\WordFlow-second-iteration\WordFlow-Classic.exe
```

- [ ] **Step 6: Run tests, build both, and commit**

```powershell
dotnet test tests\WordFlow.App.Tests --filter FullyQualifiedName~BrandingArtifactTests
powershell -ExecutionPolicy Bypass -File scripts\build-second-iteration.ps1
git add src\WordFlow.App tools\branding scripts tests\WordFlow.App.Tests
git commit -m "build: publish dual icon WordFlow executables"
```

---

### Task 10: End-to-End Release Verification

**Files:**
- Create: `tests/WordFlow.App.Tests/EndToEnd/SecondIterationJourneyTests.cs`
- Create: `scripts/verify-second-iteration.ps1`
- Create: `docs/testing/2026-08-22-second-iteration-verification.md`
- Modify: `.light/passport.yaml`

**Interfaces:**
- Consumes: all Tasks 1–9 and the dual release folder.
- Produces: executable verification report with hashes, counts, UI evidence, and known platform limitations.

- [ ] **Step 1: Add a critical-journey test**

The test uses a temporary user database and fake time provider:

```text
create day 1 target 40/120
complete 13 new items
dispose all services
recreate services against the same database
assert the same 14th card
advance local time one day
assert 27 carried + 13 filled new items
slash one card and assert global numerator increases
restore it and assert numerator decreases
load exactly 183 history cells
```

- [ ] **Step 2: Create the verification script**

Run these gates in order and exit nonzero on the first failure:

```powershell
python tools\vocabulary\verify_vocabulary.py --artifact-dir data\ielts --curated data\curated\required_vocabulary.csv --source-registry data\curated\source_registry.json --report-root data\reports --relations --relations-curated data\curated\confusable_groups.csv --misspellings data\curated\misspellings.csv --oewn data\sources\oewn\english-wordnet-2025-json.zip --relations-quality data\reports\relations-quality.json --relations-manifest data\ielts\relations-manifest.json
dotnet test WordFlow.sln -c Release --no-restore
dotnet build WordFlow.sln -c Release --no-restore
powershell -ExecutionPolicy Bypass -File scripts\build-second-iteration.ps1
```

Then launch each EXE normally, assert it remains alive, launch the other while the first runs, assert only one WordFlow instance remains, and terminate only the exact test PIDs.

- [ ] **Step 3: Execute Windows UI automation**

Verify both sliders expose range 0–1, set values 0, 0.37, and 1, and confirm the thumb bounding rectangle tracks the requested pointer/value. Import one PNG and two JPEG extension variants, restart, and assert image name plus slider value restore.

Use a controlled topmost test window to cover WordFlow, wait at most 750 ms, and verify WordFlow returns above it without becoming foreground. Turn off the setting and verify it no longer reasserts.

Navigate all six control-center pages and all settings tabs; confirm the process remains alive. Verify the visible undo row is absent and `Ctrl+Z` still triggers undo through the shortcut dispatcher.

- [ ] **Step 4: Record fresh evidence**

Write exact test totals, build warnings/errors, vocabulary counts, relation counts, both EXE hashes/sizes, UI automation results, DPI used, and the exclusive-fullscreen limitation to `docs/testing/2026-08-22-second-iteration-verification.md`.

- [ ] **Step 5: Final clean-tree check and commit**

```powershell
git diff --check
git status --short
git add tests\WordFlow.App.Tests\EndToEnd scripts\verify-second-iteration.ps1 docs\testing\2026-08-22-second-iteration-verification.md .light\passport.yaml
git commit -m "test: verify WordFlow second iteration"
git status --short
```

Expected: all automated gates pass, both EXEs exist with different icon hashes, UI automation has no crash, and Git contains no accidental runtime database, imported user skin, raw temporary icon, secret, or unrelated user file.

---

## Completion Gate

Do not declare the second iteration complete until all of the following have fresh evidence in the current execution:

- [ ] The complete vocabulary/relations verifier reports `passed: true` and both SQLite integrity checks report `ok`.
- [ ] `dotnet test WordFlow.sln -c Release --no-restore` reports zero failures.
- [ ] `dotnet build WordFlow.sln -c Release --no-restore` reports zero warnings and zero errors.
- [ ] Theme drag tests prove one decode per path and debounced persistence; real WPF slider automation succeeds.
- [ ] Restart and day-rollover tests prove exact current-card recovery and target-within carryover.
- [ ] Six-month history contains exactly 183 local dates and total slash progress responds to slash/restore.
- [ ] Strong topmost reasserts without focus theft when enabled and stops when disabled/hidden.
- [ ] Autoplay performs 1 and 10 sequential repetitions, cancels on card change, and manual playback remains one.
- [ ] Both release EXEs start, share one instance/data set, carry different embedded icons, and have recorded SHA-256 values.
- [ ] The worktree is clean after the final verification commit.
