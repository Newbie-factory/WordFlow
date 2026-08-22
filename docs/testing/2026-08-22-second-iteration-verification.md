# WordFlow Second-Iteration Release Verification

Status: **PASS_WITH_CONCERNS**. The fresh full verifier reached `status: passed` only after all four executable gates, release/UI checks, exact-PID cleanup, and verified temp-root deletion succeeded from source commit `9fdab96869ae64017cbef3839c25b2b7236e72a6`. The concerns are the native file-picker automation limitation and two historical exploratory launches that reached the real user profile before the guarded isolation seam was added; both are detailed below.

## Environment and evidence source

- Executed 2026-08-22 beginning `2026-08-22T10:07:20.1009956Z` in Windows interactive session 1.
- Windows PowerShell `5.1.26100.9168`; no package download or update was requested. Publish restore reported all projects already up to date.
- UI DPI: 144 DPI (150%).
- Machine-readable evidence: ignored build artifact `artifacts/verification/second-iteration-evidence.json`.
- Verifier: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-second-iteration.ps1`.

## Fail-fast gates

| Order | Command | Exit | Time | Exact result |
|---:|---|---:|---:|---|
| 1 | `python tools\vocabulary\verify_vocabulary.py --artifact-dir data\ielts --curated data\curated\required_vocabulary.csv --source-registry data\curated\source_registry.json --report-root data\reports --relations --relations-curated data\curated\confusable_groups.csv --misspellings data\curated\misspellings.csv --oewn data\sources\oewn\english-wordnet-2025-json.zip --relations-quality data\reports\relations-quality.json --relations-manifest data\ielts\relations-manifest.json` | 0 | 10,781 ms | vocabulary `passed: true`, integrity `ok`; relations `passed: true`, integrity `ok` |
| 2 | `dotnet test WordFlow.sln -c Release --no-restore` | 0 | 9,975 ms | 604 passed, 0 failed, 0 skipped (130 Domain + 58 Application + 225 Infrastructure + 191 App); four project summaries parsed from actual output |
| 3 | `dotnet build WordFlow.sln -c Release --no-restore` | 0 | 2,126 ms | 0 warnings, 0 errors |
| 4 | `powershell -ExecutionPolicy Bypass -File scripts\build-second-iteration.ps1` | 0 | 21,846 ms | both self-contained single-file variants published |

Vocabulary totals were 12,046 total and 12,046 case-insensitively unique, with all 46 required items present. Relations contained 43,947 senses, 47,934 published relations, and 19,520 candidates. All empty/invalid/missing/undocumented verifier counters were zero.

## Critical journey and named behavior coverage

`SecondIterationJourneyTests.Critical_journey_resumes_carries_within_target_and_updates_goal_and_history` uses a temporary user SQLite database, disposable service providers, and a mutable UTC/local fake clock. In the 604-test gate it proved:

- day 1 configured target `40/120`, effective fresh queue `40/0`;
- 13 new completions followed by disposal/recreation and recovery of the exact same 14th word and queue-item IDs;
- next local day contains exactly 27 carried new items plus 13 fresh new items, within the 40-new target;
- slash increments the global numerator by one and scheduled restore decrements it by one;
- history contains exactly 183 consecutive local-date cells.

Fresh focused commands passed 7/7 strong-topmost tests and 51/51 pronunciation/verifier-contract tests. The hidden-tick test now asserts zero z-order calls immediately after the hidden tick. Autoplay count 10 completes all ten `SpeakAsync` calls in invocation order with maximum concurrency one, no eleventh call, and one final Completed publication; count 1, mid-sequence cancellation, and manual-single-play coverage also pass.

## Windows UI automation

- Both real WPF theme sliders exposed range `0..1`. UIA set `0`, `0.37`, and `1`; physical pointer clicks at each Thumb center preserved the requested value.
- Floating-card Thumb x deltas from the linear expected position were `0`, `0.08`, and `0` pixels. Control-center deltas were `0`, `0.37`, and `0` pixels.
- PNG, `.JPG`, and `.JPEG` inputs were imported through the guarded isolated startup seam. Stored names were respectively `theme-1eaf9692b2b649fea7b8d55b9fd87f12.png`, `theme-5ba5af20824a4fdca55b775eb2851277.jpg`, and `theme-ae339dbdacf74c488e4cdd5ff30c9ff3.jpg`.
- Restart restored `theme-ae339dbdacf74c488e4cdd5ff30c9ff3.jpg` and value `0.37` on both real sliders.
- A controlled topmost cover began above the card. WordFlow returned above it in 198 ms; the foreground handle remained exactly `8523846`, so WordFlow did not steal focus. After disabling the setting, the cover remained above for the full 750 ms observation.
- All six control-center pages and all six settings tabs were selected through UIA; the process remained alive.
- Visible undo elements: 0. Invoking Good changed the card, then focused `Ctrl+Z` through the real shortcut dispatcher restored the original `bumptious` card.

The native `OpenFileDialog` did not surface in this desktop/UIA session after `InvokePattern`, a physical button click, or focused Space. Therefore image format/import/restart behavior used `WORDFLOW_VERIFICATION_MODE=isolated`, `WORDFLOW_VERIFICATION_LOCAL_APP_DATA=<system-temp-root>`, and `WORDFLOW_VERIFICATION_IMPORT=<system-temp-image>`. Production code rejects an import unless all three guarded conditions are present and both paths are below the system temp directory. This is deterministic application-level import evidence, but not evidence of native picker interaction; overall status is consequently `PASS_WITH_CONCERNS`.

## Release and single-instance evidence

Release directory: `D:\baicizhan\release\WordFlow-second-iteration`; manifest source commit and verifier HEAD were both `9fdab96869ae64017cbef3839c25b2b7236e72a6`. The verifier matched both exact filenames, byte sizes, and SHA-256 values against the manifest.

| File | Bytes | SHA-256 |
|---|---:|---|
| `WordFlow-Photo.exe` | 191,383,417 | `9e28619f6992f027a82f18d86e819dd69c8e69d341c35328f82338f7632e8602` |
| `WordFlow-Classic.exe` | 191,178,617 | `6a5b42e086ba253c998b21755233f1918bd979b13a2c128a9d99a2f8709dfb37` |

Embedded icon artifact hashes also differ: photo `05fac549624c5eed05205a5e009b3961f85ab6657ae71c460c073c51a74dde32`; classic `895c0d6a90e418c245a2d926d6eca67a9bf1b2ff02a3e9a14e467d47745fa092`.

- Photo then Classic: primary PID 44648, secondary PID 43740 exited 0, exactly one remaining PID 44648 after 28 ms convergence.
- Classic then Photo: primary PID 27128, secondary PID 43060 exited 0, exactly one remaining PID 27128 after 15 ms convergence.
- Cleanup was a final required gate: ten exact PIDs were recorded, `remainingPids` was empty, the start-time PID-reuse guard remained active, `tempRootRemoved` was true, and cleanup errors were empty. Only then was overall status written as `passed`.

## `UiOnly` evidence isolation

A separate diagnostic run used the default `artifacts/verification/second-iteration-ui-evidence.json`. It exited 0 with status `partial-ui-only`, zero full-gate records, successful exact-PID/temp-root cleanup, and no residual processes or roots. The full evidence SHA-256 was `A770D8FA06CF458086F68FD18821031516391AAACC868C6DFB2A35338A8D3479` both before and after the diagnostic run, proving `UiOnly` did not overwrite it.

## Limitations and incident record

- Windows exclusive fullscreen, secure desktop, and higher-integrity windows can outrank an ordinary desktop topmost window; those contexts were not claimed as supported.
- Before the guarded data-root seam existed, exploratory PIDs 7968 and 45012 were launched with only a child `LOCALAPPDATA` override. Windows known-folder lookup ignored it, so startup used `C:\Users\Administrator\AppData\Local\WordFlow`. Lifecycle evidence is `primary-elected/primary-ready` at `09:06:56Z/09:06:57Z` and `09:10:12Z/09:10:13Z`. The real DB and placement file last-write times became `09:10:13.3708844Z` and `09:10:13.7943843Z`; no learning action was invoked. The existing `2026-08-22` daily session was created at `09:06:05.3604467Z`, before PID 7968 elected primary. No destructive rollback was attempted. All later launches used the guarded temp root.
