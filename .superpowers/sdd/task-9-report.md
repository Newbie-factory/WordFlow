# Task 9 report: single-instance offline WPF bootstrap

## Recovery status and baseline

- Status: recovered, audited, completed, and ready for the Task 9 commit.
- Worktree: `D:\baicizhan\.worktrees\wordflow-implementation`
- Branch: `codex/wordflow-implementation`
- Baseline and recovery-start head: `50390be` (`fix: complete learning concurrency boundaries`).
- Resulting head: the commit containing this report, with message `feat: bootstrap single-instance WPF shell`.
- The interrupted agent left all Task 9 work uncommitted. It was preserved and audited in place; no inherited change was discarded wholesale.
- There was no pre-existing `task-9-report.md` or persistent Task 9 build/smoke artifact. `.superpowers\sdd\progress.md` was not edited.

## Inherited changes audit

The inherited implementation already contained the main shell, path model, corpus verifier, startup sequence, primary-only service composition, named mutex/pipe coordinator, tray service/controller, and most tests. Focused recovery checks found the following:

- Correct and retained: exact `%LOCALAPPDATA%\WordFlow` root and `Data`, `Backups`, `Skins`, `Cache`, `Logs` directories; real bundle paths under `Data\ielts`; both real manifest formats; directories -> SHA-256 verification -> versioned migration/backup -> window order; primary-only service provider/DB access; explicit-shutdown WPF lifecycle; exact tray labels; local repair instructions with no download path; SID-derived names and `CurrentUserOnly` pipe security.
- Missing: the inherited `LocalLifecycleLogTests` referenced a class that did not exist, so app tests did not compile and launch behavior was not durably observable.
- Defects found and fixed: abandoned mutexes were treated as live owners; cancellation racing with election leaked ownership; a departing owner between mutex election and pipe activation caused a false timeout; window-close hide desynchronized the tray toggle.
- Verified existing migration behavior: embedded, ordered `001_initial.sql` and `002_harden_history.sql`; a pre-migration SQLite online backup is created for an existing versioned database; migration remains transactional. The general rolling-backup feature belongs to later Task 15.
- Verified composition: the secondary returns before building services, initializing/migrating the user DB, or opening UI, so only the elected primary can write the user DB.

## TDD RED evidence

The recovery used focused RED/GREEN cycles:

1. `dotnet test tests\WordFlow.App.Tests\WordFlow.App.Tests.csproj -c Release --filter FullyQualifiedName~Bootstrap --no-restore`
   - RED: `CS0246`, missing `LocalLifecycleLog` referenced by the inherited test.
   - GREEN: timestamped append-only local lifecycle events are implemented and the focused log test passes.
2. `dotnet test tests\WordFlow.Infrastructure.Tests\WordFlow.Infrastructure.Tests.csproj -c Release --filter FullyQualifiedName~Abandoned_user_scoped_mutex --no-restore`
   - RED: `TimeoutException` while signaling a nonexistent primary after a mutex-owning thread was abandoned.
   - GREEN: election explicitly performs `WaitOne(0)` and treats `AbandonedMutexException` as recovered primary ownership.
3. `... --filter FullyQualifiedName~Cancellation_racing_with_election`
   - RED: the replacement timed out because cancellation could leave the newly owning mutex thread alive.
   - GREEN: the coordinator registers its mutex thread before awaiting election and always cancels/joins it on failed or secondary startup; 32 repeated races pass.
4. `... --filter FullyQualifiedName~Window_close_hide`
   - RED: `CS1061`, no way to synchronize an externally hidden window with tray toggle state.
   - GREEN: close-hide synchronizes visibility, and the next Show/Hide action shows the card in one click.
5. `... --filter FullyQualifiedName~Secondary_re_elects`
   - RED: `TimeoutException` when an owner disappeared after election but before pipe activation.
   - GREEN: one bounded re-election recovers ownership; a persistently live/unresponsive primary still ends in a timeout rather than unbounded recursion.

## Architecture, security, and lifecycle

- Instance names hash `applicationId + NUL + current Windows SID` with SHA-256 and use a 128-bit hex identity. The mutex is `Local\...` (not a broad machine-global mutex); the pipe uses the same SID-derived identity and `PipeOptions.CurrentUserOnly`, which applies the current-user ACL.
- Concurrent election permits exactly one mutex owner. Abandoned ownership, disposal/reacquisition, election cancellation, activation timeout/cancellation, callback exceptions, ignored callback cancellation, and owner-departure races have focused regressions.
- The pipe listener serializes activation commands, contains callback faults so later activations still work, and uses cancellation-aware asynchronous I/O. The WPF callback marshals through `Dispatcher.InvokeAsync` before showing/restoring/activating the card.
- The elected primary alone creates the DI container and `SqliteConnectionFactory`. The secondary only receives an acknowledgement and exits with code 0.
- Startup order is: elect primary -> create exact directories/local log -> verify vocabulary and relations SHA-256 manifests -> execute embedded versioned migrations and pre-upgrade backup -> create floating card and tray.
- Corpus verification fails closed on malformed manifests, path substitution, size/hash mismatch, missing/unreadable files, or cancellation. Fatal corpus failure shows local repair/reinstall guidance and registers no network client or automatic replacement path.
- Tray actions are exactly `Show/Hide Card`, `Pause/Resume`, `Open Control Center`, `Today Progress`, and `Exit`. Closing the card cancels close and hides it. `ShutdownMode=OnExplicitShutdown`; normal shutdown/disposal is reachable only through Exit. Fatal startup errors also terminate with a nonzero code.
- Local lifecycle logging records only process lifecycle identifiers/events and provides smoke observability without telemetry or network access.

## Verification and smoke evidence

Focused commands:

```powershell
dotnet test tests\WordFlow.Infrastructure.Tests\WordFlow.Infrastructure.Tests.csproj -c Release --filter FullyQualifiedName~Windows --no-restore
dotnet test tests\WordFlow.App.Tests\WordFlow.App.Tests.csproj -c Release --filter FullyQualifiedName~Bootstrap --no-restore
```

Results: Windows 14/14 passed; App Bootstrap 9/9 passed.

Packaged corpus audit used the Release output at `src\WordFlow.App\bin\Release\net8.0-windows10.0.19041.0\Data\ielts`. It contained exactly `vocabulary.sqlite3`, `relations.sqlite3`, `manifest.json`, and `relations-manifest.json`; both database hashes matched, and vocabulary bytes were `5570560` in both file and manifest.

The real smoke used the exact executable path and the following launch form for both processes:

```powershell
$exe = (Resolve-Path 'src\WordFlow.App\bin\Release\net8.0-windows10.0.19041.0\WordFlow.App.exe').Path
$primary = Start-Process -FilePath $exe -WindowStyle Hidden -PassThru
$secondary = Start-Process -FilePath $exe -WindowStyle Hidden -PassThru
```

Before launch, `Win32_Process.ExecutablePath -eq $exe` returned zero processes. Observations:

- Primary PID `124572`: remained alive, logged `primary-ready pid=124572`, exposed main window handle `791096` and title `WordFlow`.
- Exact-path owner list before secondary: `[124572]`.
- Secondary PID `125352`: exited within 10 seconds with code `0`.
- Primary logged `activation-received pid=124572`.
- Exact-path owner list after secondary: `[124572]`, proving no second primary/DB owner.
- Tray-shell visual enumeration is not exposed by this automation environment; runtime creation reached `primary-ready`, and exact tray actions/lifecycle are covered by focused tests.

Cleanup used only recorded PIDs:

```powershell
Stop-Process -Id 125352  # only if still alive; it had already exited
Stop-Process -Id 124572
```

Follow-up checks returned `PrimaryPidExists=False`, `SecondaryPidExists=False`, and exact executable count `0`. No broad process-name kill was used.

Final gates:

```powershell
dotnet test WordFlow.sln -c Release --no-restore
dotnet build WordFlow.sln -c Release --no-restore
python -m unittest discover -s tools\vocabulary\tests -p test_*.py
git diff --check
```

- Full Release tests: 269/269 passed (129 Domain, 44 Application, 87 Infrastructure, 9 App).
- Release build: success, 0 warnings, 0 errors.
- Python vocabulary suite: 54/54 passed.
- Diff check: no whitespace errors; only informational LF-to-CRLF notices from Git on Windows.

## Files

- App shell/composition: `src/WordFlow.App/App.xaml`, `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `WordFlow.App.csproj`.
- App bootstrap: `AppPaths.cs`, `BootstrapSequence.cs`, `CorpusIntegrityVerifier.cs`, `LocalLifecycleLog.cs`, `ServiceRegistration.cs`.
- Windows infrastructure: `SingleInstanceCoordinator.cs`, `TrayIconService.cs`, `TrayLifecycleController.cs`, plus the Infrastructure target/WinForms project update.
- Tests: four App bootstrap test files, two Windows test files, and the Infrastructure test target update.
- Documentation: this report only; no progress-ledger edit.

## Self-review and concerns

- Reviewed every inherited and recovery diff against the Task 9 brief and strict recovery requirements, including actual packaged manifests, versioned migrations, primary-only composition, SID/ACL naming, abandoned mutex, disposal/cancellation races, listener callback faults, dispatcher activation, close/tray state, and offline fatal repair behavior.
- Confirmed production source adds no `System.Net.Http`, HTTP client, URL, telemetry, downloader, or automatic repair endpoint.
- Confirmed the worktree was based on `50390be`, only Task 9 files/report are intended for staging, and `.superpowers\sdd\progress.md` is unchanged.
- Non-blocking concern: the tray icon uses the standard Windows application icon until a branded icon asset is supplied. Direct automated clicking of the notification-area icon was unavailable; exact controller/menu behavior and disposal are automated, while the real smoke verified that tray construction completed with the primary window.
