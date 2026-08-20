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
