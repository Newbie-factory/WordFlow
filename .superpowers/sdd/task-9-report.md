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

## Independent-review remediation

Review baseline: `2ce5742` (`feat: bootstrap single-instance WPF shell`). The requested C1, I1-I4, and M1-M2 findings are all addressed without editing `.superpowers\sdd\progress.md`.

### C1 and I1: independent corpus trust and format validation

- `TrustedCorpusHashPins` compiles the reviewed vocabulary SHA-256 `7B0D2ED0161EC0BC1F4B238A84DFD9D20AAB0A413C412A64501EAD4E52FA1BB9` and relations SHA-256 `A6ACD3A73A2354929B083D81C06EB5B5B39FA28F4CD428FA1F3F9840B25851D3` into the application assembly, independent of the adjacent mutable manifests.
- Production verification requires each adjacent declared hash to equal its compiled pin and hashes the actual database against the same pin. Tests replace each database and update its adjacent manifest consistently; both paired attacks are rejected.
- A repository-root test hashes the two actual packaged source databases and proves the compiled pins match them exactly.
- Both manifests now require an object root, positive integer `schema_version`, correctly typed `artifacts`, exact artifact members, nonempty strings, positive vocabulary byte count, exact vocabulary path, and 64-hex SHA-256 values. Missing members, wrong containers/types, invalid values, JSON errors, file I/O, and crypto failures become `CorpusIntegrityException` and therefore use the local repair dialog. Cancellation and invalid compiled-pin/runtime defects still propagate.

The initial App Bootstrap RED failed compilation because the wished-for `CorpusHashPins`/`TrustedCorpusHashPins` contract did not exist. The combined tamper, malformed-schema/type, actual-pin, and valid synthetic bundle regressions now pass.

### I2 and I4: reliable activation framing and listener ownership

- Protocol responses distinguish `ActivationSucceeded` from `ActivationFailed`. A callback that throws or faults is explicitly failure-framed; the secondary throws `ActivationFailedException` and cannot report `ActivationWasSignaled=true`. A later successful activation still succeeds through the same listener.
- A disconnect or invalid response receives one bounded re-election/retry. Framed callback rejection is reported immediately and is not misclassified as an owner-departure race.
- Disconnecting and malformed clients are contained per connection; the listener recreates the server and accepts later valid activation.
- `Completion` surfaces a terminal server-creation/listener fault. A terminal post-readiness regression injects such a fault, observes the exact `IOException`, verifies mutex ownership is relinquished, and elects a replacement.
- The WPF app observes `Completion`; terminal failure is logged and dispatched to the serialized fatal exit path. Background callback/I/O faults are observed and locally logged.
- If shutdown stops waiting for a callback that ignores cancellation, a fault-only continuation explicitly observes any later exception. The late-fault regression disposes the primary, faults the detached callback, observes the exception, and verifies takeover.

The first Windows RED failed compilation for the wished-for failure frame/exception, injectable server dependency, `Completion`, and background observer. Subsequent focused runs exposed and corrected terminal factory errors being mistakenly treated as per-connection I/O. Windows-focused tests are now 19/19.

### I3: serialized completion-safe exit

- `ApplicationExitCoordinator` memoizes one exit task, so concurrent requests share the first exit code and cleanup executes once.
- Each cleanup step is isolated. Lifetime cancellation, tray disposal, card close, service disposal, coordinator disposal, lifetime disposal, and `Application.Shutdown` are all attempted in order even if earlier steps fail. Failures are observed and logged rather than escaping `async void` startup or tray event paths.
- Tray Exit awaits the serialized task through its event callback. Fatal startup errors await the same path. Coordinator release and `Shutdown` remain attempted on both.
- Focused tests inject normal tray-disposal failure and fatal-startup cancellation/coordinator failures, prove every later step and shutdown still runs, and prove concurrent Exit requests share one task.

The focused RED was `CS0246` because `ApplicationExitCoordinator` did not exist; both disposal-fault regressions now pass.

### M1: repeatable cross-process evidence

- Added `WordFlow.SingleInstance.TestHost`, built by the solution, and an automated exact-executable regression. It proves one primary-only `bootstrap` event, secondary activation and exit 0, real process-death mutex abandonment, replacement takeover, and exact recorded-process cleanup.
- The harness test initially failed because the helper executable did not exist. The first full-suite run then exposed a real file-sharing poll race; polling now retries bounded `IOException`, and the helper is a first-class solution project built in the requested configuration.
- Durable command/output is committed at `tests\WordFlow.Infrastructure.Tests\Windows\task-9-process-smoke.json`.
- Recorded durable helper smoke: primary `124120`, secondary `125996` exit 0, killed-primary exit `-1`, replacement `128764` exit 0, two primary-only bootstrap events across abandonment, and `recordedPidsRemaining=[]`. Cleanup used only those exact PIDs.

### M2: dedicated migration backup root

- `MigrationRunner` accepts an optional backup root while preserving adjacent-backup defaults for existing callers.
- Primary application composition passes `AppPaths.BackupsDirectory`; it creates `wordflow.sqlite3.v<version>.<timestamp>.backup` under `%LOCALAPPDATA%\WordFlow\Backups`, never beside the live DB under `Data`.
- The focused composition regression seeds schema v1, resolves the real registered runner, migrates to v2, and proves exactly one backup under `Backups` and none under `Data`.

### Fresh verification and smoke

```powershell
dotnet test tests\WordFlow.Infrastructure.Tests\WordFlow.Infrastructure.Tests.csproj -c Release --filter FullyQualifiedName~Windows --no-restore
dotnet test tests\WordFlow.App.Tests\WordFlow.App.Tests.csproj -c Release --filter FullyQualifiedName~Bootstrap --no-restore
dotnet test WordFlow.sln -c Release --no-restore
dotnet build WordFlow.sln -c Release --no-restore
python -m unittest discover -s tools\vocabulary\tests -p test_*.py
git diff --check
```

- Windows focused: 19/19 passed, including the cross-process executable harness.
- App Bootstrap focused: 21/21 passed.
- Full Release: 286/286 passed (129 Domain, 44 Application, 92 Infrastructure, 21 App).
- Release build: success, including the helper in Release; 0 warnings, 0 errors.
- Python vocabulary: 54/54 passed.
- Diff check: no whitespace errors; only informational Git LF-to-CRLF notices.

Fresh real WPF smoke used exact `Start-Process -WindowStyle Hidden -PassThru` launches. Primary PID `130728` reached `primary-ready`, exposed handle `332334` and title `WordFlow`; exact-path owner list was `[130728]`. Secondary PID `128500` exited 0 after the primary logged activation; exact-path ownership remained `[130728]`. Cleanup stopped only the recorded PIDs, with `RemainingRecorded=[]` and `ExactProcessesRemaining=[]`.

### Remediation self-review and remaining concern

- Rechecked compiled pins against actual source/output hashes, fail-closed ordering, corpus exception boundaries, pipe success/failure framing, retry bounds, per-connection versus terminal errors, mutex relinquishment, detached task observation, dispatcher failure handling, serialized best-effort cleanup, backup placement, and exact-PID process cleanup.
- No network client, downloader, remote repair, telemetry sink, or progress-ledger edit was introduced.
- The only unchanged non-blocking concern is direct Explorer notification-area clicking: exact tray menu/controller/disposal behavior remains automated, and real WPF smoke proves tray construction completes, but the environment does not expose reliable tray-shell click automation.
