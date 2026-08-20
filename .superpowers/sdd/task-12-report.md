# Task 12 Report — Completely Offline Windows Pronunciation

## Outcome

Task 12 adds a view-neutral offline pronunciation port and a production `System.Speech` implementation. The production synthesizer and every operation on it live on one long-running dedicated STA worker. Only enabled installed English voices are exposed, using stable SAPI voice IDs plus culture and accent metadata. There is no network provider, fallback, URL launch, or downloaded audio path.

The current word and every eligible related word now share the same capability-aware pronunciation owner. Speech controls are enabled only when an installed English voice is available. The Chinese unavailable copy points only to Windows offline language/speech settings. Click playback supersedes autoplay; card changes, pause, autoplay disable, and disposal cancel prior playback. Completion and failure remain asynchronous and are marshalled back to the owning UI synchronization context without leaking observer exceptions.

## Strict TDD / RED evidence

Tests were written and observed failing before production implementation:

- Initial pronunciation tests failed to compile because `IPronunciationService`, the audio namespace, engine seam, voice model, playback result, and settings view model did not exist.
- The settings tests failed for missing atomic multi-setting access and missing restore/sanitize behavior.
- The card tests failed for missing `ICardPronunciationPlayback`, card-change autoplay, pause, and disposal cancellation hooks.
- Shortcut tests failed because `ShortcutAction.Pronounce` and an unassigned chord state did not exist.
- DI ownership failed with `No service for type IPronunciationService has been registered`.
- Capability truth failed because registering a speech port incorrectly advertised availability even when it had no voice.
- Observer containment failed with `InvalidOperationException: observer fault` until each subscriber was isolated and background work observed.
- The first guarded real SAPI smoke returned `Failed`. This exposed an adapter defect: the stable `VoiceInfo.Id` had been passed to `SelectVoice`, which expects the installed voice name. The adapter now preserves ID externally and maps ID to the SAPI name only inside the STA engine.
- Existing Task 10 default-count tests failed after adding pronunciation. They were updated to require the approved action while explicitly asserting that it is disabled and has no assigned key.

Each focused RED was followed by a focused GREEN run before the next behavior was added.

## Voice selection and validation

- Filters on both `InstalledVoice.Enabled` and an English culture (`TwoLetterISOLanguageName == en`).
- Models stable voice ID, display name, culture name, and British/American/other-English accent.
- Honors an exact persisted voice ID first. Otherwise GB/US preference is deterministic; missing preferred accents fall back to the stable culture/ID ordering of any installed English voice.
- Validates nonblank text, installed voice ID, rate `-10..10`, and volume `0..100` before starting the engine.
- Engine startup faults degrade pronunciation only and do not crash application startup.

## Concurrency and lifecycle

- A serialized STA work queue creates, enumerates, speaks, cancels, unsubscribes, and disposes the synthesizer on the same thread.
- Every speech request has a unique correlation ID. Late/stale `SpeakCompleted` events cannot complete a newer request.
- Publishing a newer request atomically advances the latest-request generation and queues it; the worker cancels the active prompt before starting the new one.
- Cancellation is checked again on the worker to cover cancellation-registration/start races.
- Card generation and playback generation prevent stale autoplay and stale completion feedback. A click advances the same playback generation and therefore supersedes pending autoplay.
- Event subscribers are invoked independently; subscriber faults are contained. All tracked background tasks observe faults. Dispose cancels active work, detaches the completion handler, disposes SAPI on the STA thread, and joins the worker.

## Persistence and shortcut

Voice ID, accent preference, rate, volume, and autoplay are written to `app_setting` in one SQLite transaction. Restore sanitizes removed voices, invalid enums, out-of-range numbers, and malformed booleans, exposes a Chinese `SettingsIssue`, persists the repaired set atomically, and does not crash startup. Existing Task 7 boolean behavior remains intact.

The approved design requires a configurable pronunciation action but specifies no default key. Task 10 therefore gains `Pronounce` as an explicit disabled, unassigned binding (`Unassigned` in storage, `未分配` in the UI). No key or active scope is invented, no F1–F5/Ctrl+Z default changes, and users can record, enable, scope, persist, and reset it through the existing atomic shortcut architecture.

## Wiring

- DI owns one `IPronunciationService` and one `PronunciationSettingsViewModel`.
- The capability-aware Task 11 action host reports speech available only when a selected/fallback installed English voice exists. Current-word and related-word controls use that same truth and port.
- Successful initial load and successful card replacement notify pronunciation exactly once. Failed mutations do not autoplay. Pause gates shortcuts/actions and cancels playback; resume does not replay stale content.
- `ShortcutAction.Pronounce` dispatches the same current-word speech action as clicking the word.

## Installed-voice and offline evidence

The local probe reported only non-sensitive aggregate data:

```text
installed-english-voice-count=5
culture=en-US;count=5
```

The guarded real-adapter test spoke the single short word `test` at volume 1, waited 75 ms, then cancelled/stopped and disposed the synthesizer. It passed. No GB voice is installed on this machine, so real GB output was not exercised; GB selection and fallback are covered by the machine-independent engine seam.

Production source audit:

```text
runtime-network-reference-audit=pass; hits=0
runtime-url-launch-audit=pass; hits=0
```

The only `http://` strings in XAML are compile-time XML namespace identifiers; no `.cs` or `.csproj` runtime source contains HTTP, socket, DNS, web-client, process-launch, or URL-launch references.

## Verification

Fresh final commands are recorded immediately before commit:

```text
dotnet test tests\WordFlow.Infrastructure.Tests -c Release --filter FullyQualifiedName~Pronunciation --no-restore
  13/13 passed

dotnet test WordFlow.sln -c Release --no-restore
  454/454 passed (129 Domain, 47 Application, 189 Infrastructure, 89 App)

dotnet build WordFlow.sln -c Release --no-restore
  0 warnings, 0 errors

python -m unittest discover -s tools\vocabulary\tests -p test_*.py
  54/54 passed

git diff --check
  no whitespace errors; line-ending notices only
```

## Remaining concerns

- This machine has only `en-US` installed, so a physical `en-GB` voice smoke was not possible. Deterministic GB selection and graceful fallback are covered independently of machine state.
- Audio output depends on the health of Windows SAPI and the installed offline voice package. Runtime failures are deliberately nonblocking and surfaced as accessible feedback while learning continues.
