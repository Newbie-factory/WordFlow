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

## Independent-review remediation

The follow-up review findings I1–I3 and M1–M2 are closed in this change. This addendum supersedes the earlier real-smoke description above: automated real-SAPI verification now calls `SetOutputToNull` before prompt creation and never writes to an audio endpoint. Production continues to use the default audio device.

### Additional RED / GREEN evidence

- Inventory-state tests first failed to compile because `PronunciationInventoryState` and fault metadata did not exist. Factory, event-handler registration, and enumeration faults are now `UnavailableFault`; only a successful empty inventory is `AuthoritativeEmpty` and receives offline voice-install guidance.
- The persisted-ID recovery tests first failed against the old destructive restore path. A valid voice ID now survives throwing factory/enumerator startup plus unrelated setting repair, and restores after a later authoritative inventory. A successful authoritative empty inventory still removes and atomically persists a genuinely missing ID.
- The delayed-cancel autoplay test reproduced false-at-card-change → true-before-cancel completion. Autoplay state is now captured causally and every autoplay transition invalidates pending playback; enabling does not replay the current card.
- Duplicate inventory tests failed before normalization. Conflicting stable IDs and ambiguous selectable names are quarantined, identical records collapse, culture case is canonicalized, and final ordering is deterministic.
- Owner-context restore tests exposed off-context notification behavior and an exception-throwing observer. SQLite read/repair writes now execute off the UI thread; an immutable snapshot is applied on the captured context, with per-observer containment.
- Converting restore to owner-context application exposed a synchronous WPF startup deadlock. The bootstrap/card callback regression first failed to compile against the synchronous callback and now proves asynchronous card initialization is awaited; startup no longer calls `GetResult()` for pronunciation or the adjacent app-setting read.
- A synchronous engine-completion regression verifies the current request is installed before an immediate completion event is correlated.

### Review behavior and safety

- Fault metadata exposes only the exception type, never exception messages, paths, registry data, or voice names. Transient speech faults use nonblocking local-failure copy and do not suggest reinstalling a language pack.
- Inventory normalization keeps metadata faithful to the actual name accepted by `SpeechSynthesizer.SelectVoice`; ambiguous ID/name mappings are not exposed.
- The real-engine test constructs `SystemSpeechEngineFactory(SpeechOutputPolicy.Null)`, enumerates and selects a real installed voice, creates a short `test` prompt, exercises cancellation/correlation, then detaches and disposes the synthesizer on the STA owner. The normal factory retains `DefaultAudioDevice`.
- The production runtime-source scan remains free of HTTP/network clients, sockets, DNS, process launch, and URL launch references.

### Fresh final verification after review fixes

```text
dotnet test tests\WordFlow.Infrastructure.Tests\WordFlow.Infrastructure.Tests.csproj -c Release --filter FullyQualifiedName~WindowsSpeechPronunciationServiceTests
  18/18 passed (includes safe real installed-voice null-output smoke)

dotnet test tests\WordFlow.App.Tests\WordFlow.App.Tests.csproj -c Release --filter "FullyQualifiedName~PronunciationSettingsViewModelTests|FullyQualifiedName~BootstrapSequenceTests"
  14/14 passed

dotnet test WordFlow.sln -c Release --no-restore
  465/465 passed (129 Domain, 47 Application, 194 Infrastructure, 95 App)

dotnet build WordFlow.sln -c Release --no-restore
  0 warnings, 0 errors

python -m unittest discover -s tools\vocabulary\tests -p test_*.py
  54/54 passed

runtime-network-reference-audit=pass;hits=0
runtime-url-launch-audit=pass;hits=0
installed-english-voice-count=5
culture=en-US;count=5
```

### Remaining concern after review

- The verification host has no installed `en-GB` voice. Real SAPI selection/cancellation/disposal was exercised with installed `en-US` voices and a null output sink; deterministic GB choice and cross-accent fallback remain machine-independent seam tests.

## Re-review remediation — inventory authority, publication ordering, and cancellation

The second independent review findings I1, I2, and M1 are closed in this change.

### Conflict-aware inventory authority

Normalization now returns the safe selectable voices together with deterministic quarantined stable IDs and the raw eligible-English count. A successfully enumerated inventory containing English records that all quarantine is `UnavailableConflict`, uses truthful local conflict copy, and never uses the no-English/install guidance. A partial inventory remains usable for its nonconflicted voices while carrying per-ID quarantine metadata.

Restore preserves a persisted ID only when it is currently exposed, explicitly quarantined, or inventory enumeration faulted. A quarantined ID produces the non-destructive issue `检测到本机语音清单冲突，已保留原语音选择`; unrelated corrupt settings can still be repaired transactionally without erasing that ID. An absent nonquarantined ID remains authoritatively removable. Tests cover raw English count 2 → normalized count 0, partial conflict preservation, absent-ID sanitization, and later recovery when the conflicting inventory becomes clean.

### Ordered playback publication

Card change, click, pause/resume, autoplay transitions, card switch, stop, and dispose now enter one FIFO playback command actor. Each new command synchronously cancels the preceding command token; the same token is passed through `IPronunciationService.SpeakAsync`, whose worker rechecks it before starting SAPI. Every queued command still executes its cancellation step, but stale commands cannot publish speech.

Speech completion is observed outside the actor, so a long prompt cannot delay pause/cancel and owner-context callbacks cannot deadlock command ordering. Generation checks remain for stale feedback. Deterministic service-acceptance barriers reproduced the former check-to-publication race for click, pause, autoplay disable, card switch, and dispose; all five now reject the blocked stale autoplay publication.

### Restore and startup cancellation

Owner-context apply and cancellation now have one atomic linearization point. Cancellation checks occur before posting, and the posted callback must atomically claim apply ownership before mutating the immutable snapshot. If cancellation wins, `OperationCanceledException` propagates and a callback drained later performs no mutation or notification.

Bootstrap now awaits pronunciation-settings preparation as a distinct phase before card/tray creation. Lifetime cancellation is rethrown from pronunciation restore and the adjacent app-setting read, reaches the startup exit/cleanup path, and never continues to card/tray creation. Tests cover cancellation after owner `Post` and cancellation during the preparation phase.

### Additional strict TDD evidence

- The all-conflict inventory test first failed to compile because conflict state and quarantine metadata did not exist; the partial persisted-ID test failed with `Actual: null`.
- All five publication-boundary theory cases first failed because `automatic` appeared after the newer transition.
- The bootstrap cancellation test first failed to compile against the former single card callback. After adding the preparation phase, the owner-post test failed with restored volume `55` instead of untouched `100`, proving the late apply.
- A focused regression then caught superseded actor commands skipping their cancellation action. The actor now executes every command while token-gating only publication; the existing cancellation/lifecycle test and all five adversarial cases pass together.

### Fresh final verification after re-review fixes

```text
dotnet test tests\WordFlow.Infrastructure.Tests\WordFlow.Infrastructure.Tests.csproj -c Release --filter FullyQualifiedName~WindowsSpeechPronunciationServiceTests
  19/19 passed (includes safe real installed-voice null-output smoke)

dotnet test tests\WordFlow.App.Tests\WordFlow.App.Tests.csproj -c Release --filter "FullyQualifiedName~PronunciationSettingsViewModelTests|FullyQualifiedName~BootstrapSequenceTests"
  23/23 passed

dotnet test WordFlow.sln -c Release --no-restore
  475/475 passed (129 Domain, 47 Application, 195 Infrastructure, 104 App)

dotnet build WordFlow.sln -c Release --no-restore
  0 warnings, 0 errors

python -m unittest discover -s tools\vocabulary\tests -p test_*.py
  54/54 passed

runtime-network-reference-audit=pass;hits=0
runtime-url-launch-audit=pass;hits=0
installed-english-voice-count=5
culture=en-US;count=5
git diff --check
  no whitespace errors; line-ending notices only
```

### Remaining concern after re-review

- The host still has no installed `en-GB` voice. The real null-output SAPI path covers installed `en-US`; GB selection, conflict behavior, and fallback remain deterministic machine-independent engine tests.
