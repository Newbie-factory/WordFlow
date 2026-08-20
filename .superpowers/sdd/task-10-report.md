# Task 10 report: safe configurable shortcuts

## Status and approved default reconciliation

Task 10 is implemented on `codex/wordflow-implementation`. The shortcut defaults follow the approved product spec and the already-implemented Task 8 rating contract:

| Action | Default chord | Default scope | Meaning |
|---|---|---|---|
| `Again` | `F1` | Global | 不认识 / FSRS Again |
| `Hard` | `F2` | Global | 模糊 / FSRS Hard |
| `Good` | `F3` | Global | 认识 / FSRS Good |
| `Slash` | `Shift+F3` | Global | 斩; not FSRS Easy |
| `ToggleSynonyms` | `F4` | Global | 近义辨析 |
| `ToggleConfusables` | `F5` | Global | 形近易混 |
| `Undo` | `Ctrl+Z` | Focused | 撤销; focused by default to preserve the foreground app's Ctrl+Z |

Every default can be rebound, disabled, switched between Global and Focused, restored individually, or reset as one transaction. Chords use a platform-neutral virtual-key/modifier model and display deterministically in `Ctrl+Alt+Shift+Win+Key` order. Application contains no WPF types; WPF conversion remains in `MainWindow` and `FocusedShortcutBindingBridge`.

## TDD RED evidence

Tests were added before their corresponding production behavior and observed failing:

1. Initial contract RED:
   - `dotnet test tests\WordFlow.Infrastructure.Tests\WordFlow.Infrastructure.Tests.csproj -c Release --filter FullyQualifiedName~GlobalShortcut --no-restore`
   - Failed to compile with CS0234/CS0246 because the shortcut domain, port, service, native adapter, and store did not exist.
2. Partial lifecycle rollback RED:
   - Focused filters `Partial_handle_recreation_failure` and `Partial_reset_unregistration_failure`.
   - Both failed: HWND recreation left F1 unregistered, and reset rollback attempted to duplicate an untouched F9 registration. The fix reconstructs only registrations already removed.
3. Existing Task 7 schema-uniqueness RED:
   - Filter `Sqlite_store_round_trips_disabled_duplicate`.
   - Failed with SQLite error 19 because two disabled actions may legitimately retain the same chord while `shortcut_binding.gesture` is unique. Persisted values now include a deterministic action identity suffix while parsing remains backward-compatible with three-field values.
4. Callback containment RED:
   - Filter `Callback_fault`.
   - Failed to compile because no callback-fault channel existed. Subscriber faults are now individually contained/reported and do not block later subscribers or escape `WM_HOTKEY`.
5. Rollback-cleanup RED:
   - Filter `Failed_candidate_cleanup`.
   - Failed because a rejected candidate cleanup was not reported. Failed cleanup is now tracked as a lifecycle issue, described in the operation result, retried before later edits/restores, and retried during disposal.

## Architecture and delivery

- `ShortcutAction`, `ShortcutScope`, `ShortcutModifiers`, `ShortcutChord`, and `ShortcutBinding` are Application-layer types with no WPF dependency.
- Only enabled Global bindings call Win32 `RegisterHotKey`/`UnregisterHotKey`; registration uses `MOD_NOREPEAT` and unique monotonically increasing staging IDs.
- Focused bindings never touch Win32. `FocusedShortcutBindingBridge` maintains an observable, view-neutral chord-to-action map. `MainWindow` converts WPF `Key`/`ModifierKeys` at the UI edge and forwards matches.
- `MainWindow.SourceInitialized` attaches the HWND, restores/sanitizes persisted bindings, and installs one `HwndSource` hook. `Closed` removes the hook and focused bridge. DI disposal later unregisters globals on the owning UI thread.
- `WM_HOTKEY` routes by active registration ID. Retired/staged IDs never invoke actions. Callback handlers run outside the mutation lock; one failing handler is reported through `CallbackFaulted` while later handlers continue.
- All edits and handle changes serialize behind one lock. Binding snapshots are copied before exposure, and binding-change events publish only after persistence and registration commit.
- Recorder rows expose normalized chord, scope, enabled state, conflict reason, Restore Default, and Reset All. `ShortcutLabelMap` publishes `Item[]` changes immediately so all button/help labels can bind to current settings.

## Validation and conflict policy

- Enabled duplicates are rejected across all scopes because Global and Focused delivery overlap while WordFlow is focused.
- Disabled bindings may retain duplicate chords for later editing and round-trip through the Task 7 schema.
- Explicitly rejected: bare modifier virtual keys, all Windows-key combinations (documented as OS-reserved for `RegisterHotKey`), F12 (debugger reservation), Alt+Tab, Alt+Escape, Ctrl+Escape, and Ctrl+Alt+Delete.
- Ordinary Ctrl/Alt/Shift combinations remain available; the service does not silently reserve extra product keys.
- Windows error codes are returned in conflict text. Failed replacements preserve the prior binding in memory and SQLite.
- Persisted restore removes unknown commands, disables malformed/action-mismatched entries, deterministically disables overlapping entries, disables OS-conflicting globals, persists the sanitized complete set, and exposes every issue through `RestoreIssues` for the settings VM.

## Atomic replacement and failure matrix

`TryReplace` performs: validate candidate -> detect overlap -> register enabled Global candidate under a unique staging ID -> open/stage a complete SQLite replacement transaction -> unregister the prior Global registration -> commit SQLite -> publish the new in-memory binding/event. Focused candidates skip only the Win32 steps.

| Failure point | Result |
|---|---|
| Validation/reserved/duplicate | No OS, memory, or persistence mutation |
| Candidate `RegisterHotKey` | Old OS registration and persisted binding remain active |
| Store staging | Candidate is unregistered; transaction never starts; old state remains |
| Old `UnregisterHotKey` | Staged transaction rolls back; candidate is unregistered; old remains active |
| Store commit | Old chord is immediately re-registered under a fresh ID; candidate is unregistered; uncommitted DB transaction rolls back |
| Candidate rollback cleanup | Failure is explicit, tracked, ignored by message routing, retried before another operation, and retried/raised during disposal |
| Partial HWND recreation | Only successfully removed old registrations are reconstructed; no new-HWND registrations survive |
| Reset partial old removal | Only removed registrations are reconstructed; untouched registrations are not duplicated |
| Reset candidate registration/commit | All staged defaults are removed and the complete prior custom set is reconstructed; DB transaction rolls back |

Reset All uses one store transaction and one modeled registration transaction rather than a sequence of independently visible replacements.

## Fresh verification and real Win32 smoke

Commands run from `D:\baicizhan\.worktrees\wordflow-implementation`:

```powershell
dotnet test tests\WordFlow.Infrastructure.Tests\WordFlow.Infrastructure.Tests.csproj -c Release --filter FullyQualifiedName~GlobalShortcut --no-restore
dotnet test tests\WordFlow.App.Tests\WordFlow.App.Tests.csproj -c Release --filter FullyQualifiedName~ShortcutSettings --no-restore
dotnet test WordFlow.sln -c Release --no-restore
dotnet build WordFlow.sln -c Release --no-restore
python -m unittest discover -s tools\vocabulary\tests -p test_*.py
git diff --check
```

Results:

- GlobalShortcut focused: 19/19 passed.
- ShortcutSettings focused: 6/6 passed.
- Full Release: 318/318 passed (129 Domain, 44 Application, 112 Infrastructure, 33 App).
- Release build: 0 warnings, 0 errors.
- Python vocabulary: 54/54 passed.
- Diff check: no whitespace errors; only informational LF-to-CRLF notices for pre-existing tracked App files.

The real Win32 smoke used a thread hotkey with the uncommon chord `Ctrl+Alt+Shift+F23` (`MOD_NOREPEAT`) and exact IDs. First registration succeeded, `finally` cleanup succeeded, the identical chord registered again under a second ID (proving release), and the second `finally` cleanup succeeded. The PowerShell host PID was `127964` and exited normally; both `UnregisterHotKey` results were `True`, leaving no process or registration behind.

## Self-review and concerns

- Rechecked Application/WPF dependency direction, Task 8 rating reconciliation, cross-scope overlap, disabled duplicates, corrupt restore, SQLite transaction lifetime, unique staging IDs, stale-ID routing, callback containment, HWND recreation, owner-thread disposal, and exact shutdown ordering.
- Confirmed no progress-ledger edit, network dependency, silent Windows-key reservation, or WPF type in the Application port/domain boundary.
- Win32 cannot make an OS registration swap and SQLite commit one kernel-level atomic operation. The adapter minimizes the gap with a staged SQLite transaction and immediate restoration, and its fault-injected orders preserve the prior state. A different external process racing to claim the just-released old chord in the tiny commit-failure rollback window is an unavoidable OS-level race; such a failure surfaces as a Win32/lifecycle fault rather than being hidden.

## Review remediation (C1, I1-I7)

This section supersedes the earlier implementation notes where they differ. The original default mapping and persistence encoding remain compatible.

### Additional TDD RED evidence

The review fixes were driven by focused failing tests before production changes:

- C1/I1/I5 initially failed compilation because the service had no dispatcher seam, lifecycle snapshot, bounded allocator, or chord-aware stale-message routing.
- The A0-A5 modifier theory produced six failures because left/right Shift, Ctrl, and Alt virtual keys were accepted as standalone chords.
- Commit failure + old-chord theft + candidate cleanup failure exposed split OS/SQLite/memory truth. Partial restore removal and terminal reconciliation notification also failed.
- Reentrant Reset/Restore delivered an older Hard binding after a callback committed a newer one. A two-subscriber same-action test later showed subscriber B receiving version 2 after nested version 3.
- Fake transaction `Dispose()` faults escaped `TryReplace`/`ResetAll` before rollback or adoption. A commit-that-writes-then-throws test proved SQLite could retain the candidate while memory/Win32 reverted to the old binding.
- A one-shot `UnregisterHotKey` failure was reported terminal even though the immediate tracked retry released every registration.
- Repeated failed Reset All with the same reason did not raise `ResetConflictReason` again.

Each case was observed red under its focused filter and then green after the corresponding implementation change.

### Lifecycle, atomicity, and thread model

- All public native lifecycle, mutation, message-routing, disposal, and event work passes through platform-neutral `IShortcutDispatcher`. Production injects the WPF HWND-owner `Dispatcher`; the non-WPF default explicitly rejects off-owner calls. Dedicated-dispatcher tests assert native calls and binding events never execute on worker callers.
- The service exposes `Ready`, `Degraded`, `Terminal`, and `Disposed` lifecycle snapshots. A recoverable external loss disables the affected binding and persists that actual state. If reconciliation itself cannot be persisted, the service enters Terminal and rejects further mutation rather than hiding disagreement.
- `TryReplace`, Reset All, restore, and HWND recreation use active, staged, retired, and pending-cleanup registration sets. Staged/pending IDs are never routable. Active routing is removed immediately before unregister; it is restored only if native unregister says the registration still exists.
- IDs are allocated only in `0..0xBFFF`, exclude all active/staged/pending/retired IDs, report explicit exhaustion, and are quarantined after confirmed unregister until successful HWND recreation. `WM_HOTKEY` additionally validates lParam modifiers/VK, preventing a delayed message from invoking an action after safe ID reuse.
- Rollback always cleans or tracks every candidate, reconstructs removed old registrations by exact ID, disables any registration an external process stole, and explicitly persists the reconciled complete snapshot. It no longer assumes that a thrown `Commit()` implies SQLite did not commit.
- A pre-commit transaction/dispose fault rolls back and re-persists the reconciled old truth. A post-commit `Dispose()` fault adopts the committed candidate/default/restore truth and exposes Degraded state. Persistent native cleanup failure is non-routable, tracked, Terminal on disposal, and retryable; a transient first failure that the immediate retry clears ends Disposed without a false exception.
- App shutdown disposes shortcuts on the UI dispatcher before destroying the HWND. Real SQLite transactions use immediate acquisition, bounded busy timeout, rollback-on-dispose, and connection cleanup in `finally`.

### Reconciled failure matrix

| Failure/order | Observable terminal truth |
|---|---|
| Candidate register or store staging | Old OS/SQLite/memory unchanged; staged ID released or tracked non-routable |
| Old unregister fails | Old remains active; candidate cleaned/tracked; old snapshot explicitly persisted |
| Commit fails before write | Candidate cleaned, removed old reconstructed, old snapshot explicitly re-persisted |
| Commit writes then throws | Same explicit rollback persistence overwrites the ambiguous candidate; old OS/SQLite/memory agree |
| Commit plus transaction disposal fail | Disposal cannot bypass `finally`; candidate cleanup and old reconstruction still run |
| Commit succeeds, transaction disposal fails | Candidate/default/restore is adopted in OS/SQLite/memory; lifecycle reports Degraded |
| Old reconstruction partially fails | Stolen actions are disabled in memory and SQLite; surviving exact registrations remain active; lifecycle Degraded |
| Reconciliation persistence fails | Actual reconstructed/disabled memory state is published; lifecycle Terminal identifies unresolved SQLite reconciliation |
| Candidate cleanup fails | ID remains reserved, non-routable, and retryable; lifecycle/result expose the pending cleanup |
| HWND recreation partially fails | New-HWND candidates are cleaned/tracked; old HWND registrations reconstruct or become explicitly disabled |
| Persistent disposal cleanup fails | Active routing is empty; pending native IDs remain tracked; Dispose throws and lifecycle is Terminal until retry succeeds |

### Events, focused delivery, and recorder truth

- Binding changes carry monotonic versions. Per-action current-version checks occur before every subscriber callback, suppressing stale concurrent, batch, and same-action reentrant delivery. Settings rows and the label map also ignore older versions, so final labels equal `service.Bindings`.
- Global callback and Focused bridge subscribers are isolated individually; later handlers still run, and callback-fault observers are themselves contained. Focused mappings never call Win32 and no WPF type crosses the Application port boundary.
- WPF capture maps left/right modifier keys at the edge; validation rejects generic modifiers and VK `0xA0..0xA5` while retaining ordinary user chords. Existing documented OS-reserved policy is unchanged.
- Failed chord/scope/enable/default/reset operations notify every affected property. `ShortcutSettingsViewModel` implements `INotifyPropertyChanged`, including repeated identical `ResetConflictReason` failures, and all getters continue to read the service's actual binding.

### Review verification

Fresh commands run from `D:\baicizhan\.worktrees\wordflow-implementation`:

```powershell
dotnet test tests\WordFlow.Infrastructure.Tests\WordFlow.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~GlobalShortcut|FullyQualifiedName~ShortcutPersistenceIntegration"
dotnet test tests\WordFlow.App.Tests\WordFlow.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ShortcutSettings"
dotnet test WordFlow.sln -c Release --no-restore
dotnet build WordFlow.sln -c Release --no-restore
python -m unittest discover -s tools\vocabulary\tests -p test_*.py
git diff --check
```

Results:

- Shortcut lifecycle/persistence/real HWND focused: 52/52 passed.
- Shortcut settings focused: 17/17 passed.
- Full Release: 362/362 passed (129 Domain, 44 Application, 145 Infrastructure, 44 App).
- Release build: 0 warnings, 0 errors.
- Python vocabulary: 54/54 passed.
- `git diff --check`: no whitespace errors; only Git's informational LF-to-CRLF notices.
- The guarded automated smoke created a real message-only HWND on its STA owner thread, registered `Ctrl+Alt+Shift+F23`, verified a duplicate registration was rejected, disposed the service, then registered the identical chord under a probe ID. The probe was unregistered in `finally`, the HWND was destroyed on its owner thread, and the test process exited normally.

### Remaining concern

No user-space design can prevent another process from claiming the old chord after its required `UnregisterHotKey` and before rollback reconstruction. That external theft is now an explicit modeled outcome: the action is disabled, OS/SQLite/memory are reconciled, and lifecycle is Degraded. If SQLite reconciliation also fails, lifecycle becomes Terminal; the service never reports the hidden old binding as active.

## Second re-review remediation (C1, I1-I3)

### Additional RED evidence

- Non-allowlisted `InvalidOperationException`-derived boundary faults from `BeginReplace`, `Commit`, and transaction `Dispose` escaped before candidate cleanup, old reconstruction, or committed-state adoption. Focused tests observed staged registrations left native, old registrations missing, and memory disagreeing with persisted state.
- A restore `BeginReplace` fault left newly staged registrations native but non-routable. A reconciliation `BeginReplace` fault left lifecycle Ready while Windows could no longer reconstruct the advertised binding.
- Pending cleanup recovery followed by a successful replacement left lifecycle Degraded. Conversely, recreating the HWND cleared an unrelated persistence-transaction-dispose degradation.
- Degraded and Terminal rollback `BindingChanged` callbacks synchronously waiting for a worker-thread `Bindings` snapshot timed out because rollback invoked them while holding the mutation gate.
- `WM_HOTKEY` with zero lParam routed by ID alone.
- The shared production focused/global fault hub test initially failed compilation because no composition-level reporter existed.

All failures were observed under focused filters before production changes.

### Unconditional persistence-boundary compensation

- The store boundary now captures every `Exception` from staging, commit, and dispose. Expected IO/SQLite/access failures remain result-based. Unexpected programmer/boundary failures are retained and rethrown only after cleanup, transaction-dispose observation, exact old reconstruction, complete-snapshot reconciliation, state/event queuing, and lifecycle calculation.
- Begin/stage failure no longer assumes persistence was untouched: the staged native candidate is cleaned or tracked and the prior complete snapshot is explicitly reconciled before either returning or rethrowing.
- Commit failure, including a fake that writes then throws, cleans the candidate, reconstructs removed exact registrations, and overwrites ambiguous persistence with the reconciled snapshot.
- A post-commit dispose failure adopts the committed OS/SQLite/memory truth before propagating an unexpected exception. A pre-commit dispose failure rolls back first. A failed original transaction close remains its own degradation cause even when a separate reconciliation transaction closes successfully.
- Reconciliation failure applies and queues the actual disabled binding, enters Terminal, and only then propagates a non-allowlisted reconciliation exception. Tests cover Replace, Reset, Restore, commit-after-write ambiguity, pre/post-commit dispose, and reconciliation failure.

### Cause-aware lifecycle

Lifecycle degradation is recomputed from independent reason tokens:

- `[native-cleanup]` exists only while a non-routable staged registration remains pending. Successful retry clears only this token.
- `[persistence-transaction-dispose]` records transaction-close ambiguity and survives same/different HWND attachment. A later successful persistence operation with a clean close clears it.
- `[binding-reconstruction:<Action>]` records an action Windows could not reconstruct. A successful replacement for that action clears only its token; successful Reset clears reconstructed-binding tokens as a batch.

Ready is reported only when no degradation causes remain. Tests exercise pending recovery through Replace and same/recreated HWND, transaction recovery through Replace/Reset/Restore, unrelated-cause survival through HWND changes, and final disposal.

### Callback and message boundaries

- Mutations queue versioned `BindingChanged` records while holding the gate. The public dispatcher operation drains the queue in `finally` only after the core method has unwound the lock, including degraded, Terminal, and fatal-rethrow rollback paths.
- Cross-thread snapshot tests now complete from both degraded and Terminal callbacks. Reentrant rollback callbacks commit their later version after the outer result is constructed; current-version checks suppress stale delivery and final labels/bindings agree.
- `ShortcutCallbackFaultHub` connects both `IShortcutService.CallbackFaulted` and the private `FocusedShortcutBindingBridge.CallbackFaulted`. `MainWindow` owns the connection and `App` attaches the same `LocalLifecycleLog` sink once. Hub observers are individually contained and later observers receive both global and focused faults.
- Production `ProcessWindowMessage` now requires the real WM_HOTKEY lParam and rejects zero. Tests pass explicit encoded modifiers/VK; WPF already forwards the native lParam.

### Fresh second re-review verification

Commands:

```powershell
dotnet test tests\WordFlow.Infrastructure.Tests\WordFlow.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~GlobalShortcut|FullyQualifiedName~ShortcutPersistenceIntegration"
dotnet test tests\WordFlow.App.Tests\WordFlow.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ShortcutSettings|FullyQualifiedName~Shared_fault_hub"
dotnet test WordFlow.sln -c Release --no-restore
dotnet build WordFlow.sln -c Release --no-restore
python -m unittest discover -s tools\vocabulary\tests -p test_*.py
git diff --check
```

Results:

- Shortcut lifecycle/persistence/real HWND focused: 71/71 passed.
- Shortcut settings and shared fault composition focused: 18/18 passed.
- Full Release: 382/382 passed (129 Domain, 44 Application, 164 Infrastructure, 45 App).
- Release build: 0 warnings, 0 errors.
- Python vocabulary: 54/54 passed.
- Diff check: no whitespace errors; only informational LF-to-CRLF notices.
- The guarded real message-only HWND/RegisterHotKey test ran inside the focused and full suites and again proved exact `Ctrl+Alt+Shift+F23` release/reacquisition with `finally` cleanup.

### Remaining concern

The irreducible external chord-theft race remains unchanged and explicitly modeled as disabled plus `[binding-reconstruction:<Action>]` Degraded state. Unexpected store exceptions are deliberately rethrown after compensation so programming faults stay visible; if reconciliation cannot establish persistence truth, Terminal accurately marks the unresolved boundary.
