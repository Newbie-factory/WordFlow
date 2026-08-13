# WordFlow Windows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a completely offline Windows 10/11 vocabulary-learning application with an always-on-top floating card, FSRS-6 scheduling, a quality-controlled 10,000–12,000+ IELTS/academic vocabulary database, reversible slashing, editable global shortcuts, offline pronunciation, skinning, and full synonym/confusable-word explanations.

**Architecture:** A WPF desktop shell consumes small application use cases and domain services; SQLite infrastructure keeps immutable events and current snapshots transactionally consistent. Reproducible Python data tools build versioned, read-only vocabulary and lexical-relation databases from ECDICT, Open English WordNet 2025, curated sources, and user-approved confusable groups. The implementation advances through four independently testable milestones: data assets, learning core, Windows floating experience, then control center/release hardening.

**Tech Stack:** C# 12, .NET 8 LTS, WPF, MVVM, CommunityToolkit.Mvvm 8.4.2, Microsoft.Data.Sqlite 8.0.x, System.Speech 8.0.x, xUnit 2.9.3, Python 3.13 standard library, SQLite, Open English WordNet 2025, PowerShell, WiX Toolset v5.

## Global Constraints

- Project root is exactly `D:\baicizhan`; runtime user data must live outside the installation directory under `%LOCALAPPDATA%\WordFlow`.
- Target Windows 10 22H2 x64 and Windows 11 x64; validate 100%, 125%, 150%, and 200% DPI.
- Build `net8.0-windows10.0.19041.0`; publish self-contained `win-x64`; users must not need a preinstalled .NET runtime.
- Runtime behavior is completely offline: no HTTP clients, telemetry, online translation, network TTS, account, cloud, or remote AI calls.
- Main vocabulary is quality-gated at approximately 10,000–12,000 words and may grow for validated topic families or user confusables; never truncate solely to hit a fixed count.
- Open English WordNet 2025 core data is CC BY 4.0 with Princeton-derived terms; ship complete licenses, attribution, source version, and hashes.
- Default global shortcuts are F1 Again, F2 Hard, F3 Good, Shift+F3 Slash, F4 synonym drawer, and F5 confusable drawer. Ctrl+Z Undo defaults to floating-card-focus scope. Every action is rebindable, disableable, and scope-selectable.
- FSRS is fixed to FSRS-6 with 21 documented default parameters; desired retention defaults to 0.90 and ordinary settings allow 0.85–0.95.
- WPF image skins use `UniformToFill`, preserve aspect ratio, cover every rounded corner, and never reveal white edges.
- Treat the existing 8,000-word files as a verified migration baseline, not as the final corpus and not as files to overwrite before the replacement passes verification.
- Use test-driven development: red test, minimal implementation, green test, focused commit. Never stage unrelated user files or raw source corpora.

---

## Planned File Map

```text
D:\baicizhan\
├─ global.json                         # locks .NET 8 SDK feature band
├─ Directory.Build.props              # warnings, nullable, deterministic builds
├─ Directory.Packages.props           # central package versions
├─ WordFlow.sln
├─ src\
│  ├─ WordFlow.Domain\                # pure models, FSRS, queue/slash rules
│  ├─ WordFlow.Application\           # use cases and ports; no WPF/SQLite
│  ├─ WordFlow.Infrastructure\        # SQLite, Win32, TTS, files, backup
│  └─ WordFlow.App\                   # WPF views, view models, composition root
├─ tests\
│  ├─ WordFlow.Domain.Tests\
│  ├─ WordFlow.Application.Tests\
│  ├─ WordFlow.Infrastructure.Tests\
│  └─ WordFlow.App.Tests\
├─ tools\vocabulary\                  # reproducible offline corpus builders
├─ data\
│  ├─ curated\                        # reviewed seeds and source metadata
│  ├─ licenses\                       # ECDICT/OEWN license and attribution
│  ├─ reports\                        # generated QA reports
│  └─ ielts\                          # generated CSV/SQLite/manifest artifacts
├─ installer\                         # WiX bundle and upgrade rules
└─ docs\                              # design, plan, data provenance, user help
```

The Domain project references nothing else. Application references Domain. Infrastructure references Application and Domain. App references Application and Infrastructure. Tests follow the same boundary; build-time Python tools do not execute at app runtime.

---

## Milestone A — Reproducible Offline Data Assets

### Task 1: Toolchain Lock and Compileable Solution Skeleton

**Files:**
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`
- Create: `WordFlow.sln`
- Create: `src/WordFlow.Domain/WordFlow.Domain.csproj`
- Create: `src/WordFlow.Application/WordFlow.Application.csproj`
- Create: `src/WordFlow.Infrastructure/WordFlow.Infrastructure.csproj`
- Create: `src/WordFlow.App/WordFlow.App.csproj`
- Create: `tests/WordFlow.Domain.Tests/WordFlow.Domain.Tests.csproj`
- Create: `tests/WordFlow.Application.Tests/WordFlow.Application.Tests.csproj`
- Create: `tests/WordFlow.Infrastructure.Tests/WordFlow.Infrastructure.Tests.csproj`
- Create: `tests/WordFlow.App.Tests/WordFlow.App.Tests.csproj`
- Modify: `.gitignore`

**Interfaces:**
- Consumes: Windows x64 and the project root.
- Produces: a deterministic solution whose project-reference graph enforces the architecture above.

- [ ] **Step 1: Install and prove the required SDK**

Run in an elevated terminal only if `dotnet --list-sdks` remains empty:

```powershell
winget install --id Microsoft.DotNet.SDK.8 --exact --source winget
dotnet --list-sdks
```

Expected: at least one `8.0.4xx` SDK is listed. The currently installed 8.0.8 runtime alone is insufficient to compile.

- [ ] **Step 2: Add the SDK and build-policy locks**

Create `global.json`:

```json
{
  "sdk": {
    "version": "8.0.400",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

Create `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <LangVersion>12</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
    <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
  </PropertyGroup>
</Project>
```

Create `Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageVersion Include="Microsoft.Data.Sqlite" Version="8.0.21" />
    <PackageVersion Include="System.Speech" Version="8.0.0" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="8.0.1" />
    <PackageVersion Include="Microsoft.Extensions.Logging" Version="8.0.1" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageVersion Include="coverlet.collector" Version="6.0.4" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Scaffold the solution and enforce references**

Run:

```powershell
dotnet new sln -n WordFlow
dotnet new classlib -n WordFlow.Domain -o src\WordFlow.Domain -f net8.0
dotnet new classlib -n WordFlow.Application -o src\WordFlow.Application -f net8.0
dotnet new classlib -n WordFlow.Infrastructure -o src\WordFlow.Infrastructure -f net8.0
dotnet new wpf -n WordFlow.App -o src\WordFlow.App -f net8.0
dotnet new xunit -n WordFlow.Domain.Tests -o tests\WordFlow.Domain.Tests -f net8.0
dotnet new xunit -n WordFlow.Application.Tests -o tests\WordFlow.Application.Tests -f net8.0
dotnet new xunit -n WordFlow.Infrastructure.Tests -o tests\WordFlow.Infrastructure.Tests -f net8.0
dotnet new xunit -n WordFlow.App.Tests -o tests\WordFlow.App.Tests -f net8.0
dotnet sln WordFlow.sln add (Get-ChildItem src,tests -Recurse -Filter *.csproj).FullName
dotnet add src\WordFlow.Application reference src\WordFlow.Domain
dotnet add src\WordFlow.Infrastructure reference src\WordFlow.Application src\WordFlow.Domain
dotnet add src\WordFlow.App reference src\WordFlow.Application src\WordFlow.Infrastructure
dotnet add tests\WordFlow.Domain.Tests reference src\WordFlow.Domain
dotnet add tests\WordFlow.Application.Tests reference src\WordFlow.Application
dotnet add tests\WordFlow.Infrastructure.Tests reference src\WordFlow.Infrastructure
dotnet add tests\WordFlow.App.Tests reference src\WordFlow.App
```

Modify WPF target frameworks to `net8.0-windows10.0.19041.0`, set `<UseWPF>true</UseWPF>`, and set `<Platforms>x64</Platforms>`.

Remove template-generated inline `Version` attributes because central package management owns versions. Add versionless `PackageReference` entries as follows: Domain has none; Application uses `CommunityToolkit.Mvvm`; Infrastructure uses `Microsoft.Data.Sqlite`, `System.Speech`, `Microsoft.Extensions.DependencyInjection`, and `Microsoft.Extensions.Logging`; App uses `CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection`, and `Microsoft.Extensions.Logging`; every test project uses `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, and `coverlet.collector` with runner/collector assets marked private.

- [ ] **Step 4: Add an architecture-boundary test**

Create `tests/WordFlow.Domain.Tests/Architecture/ReferenceBoundaryTests.cs`:

```csharp
namespace WordFlow.Domain.Tests.Architecture;

public sealed class ReferenceBoundaryTests
{
    [Fact]
    public void Domain_does_not_reference_outer_layers()
    {
        string[] names = typeof(WordFlow.Domain.AssemblyMarker).Assembly
            .GetReferencedAssemblies().Select(x => x.Name ?? "").ToArray();
        Assert.DoesNotContain("WordFlow.Application", names);
        Assert.DoesNotContain("WordFlow.Infrastructure", names);
        Assert.DoesNotContain("WordFlow.App", names);
    }
}
```

Add empty `AssemblyMarker` types to each production project.

- [ ] **Step 5: Restore, test, build, and commit**

Run:

```powershell
dotnet restore WordFlow.sln
dotnet test WordFlow.sln --no-restore
dotnet build WordFlow.sln --no-restore -c Release
```

Expected: restore succeeds, all tests pass, build has zero warnings and zero errors.

Commit:

```powershell
git add global.json Directory.Build.props Directory.Packages.props WordFlow.sln src tests .gitignore
git commit -m "build: scaffold WordFlow solution"
```

### Task 2: Replace the Fixed 8,000-Word Builder with a Quality-Gated Corpus

**Files:**
- Create from verified legacy logic: `tools/vocabulary/build_vocabulary.py`
- Create from verified legacy logic: `tools/vocabulary/verify_vocabulary.py`
- Create: `tools/vocabulary/models.py`
- Create: `tools/vocabulary/selection.py`
- Create: `tools/vocabulary/io_artifacts.py`
- Create: `tools/vocabulary/tests/test_selection.py`
- Create: `data/curated/required_vocabulary.csv`
- Create: `data/curated/source_registry.json`
- Create: `docs/data/vocabulary-policy.md`
- Modify: `data/ielts/manifest.json`

Keep `data/build_ielts_8000.py`, `data/verify_ielts_8000.py`, and `ielts_8000.*` unchanged as migration evidence until Task 6 proves stable-ID migration; do not delete or overwrite them in this task.

**Interfaces:**
- Consumes: `ECDICT.csv`, curated required words, source registry.
- Produces: `build_vocabulary.build(BuildConfig) -> BuildReport`, `vocabulary.csv`, `vocabulary.sqlite3`, `manifest.json`, `reports/vocabulary-quality.json`.

- [ ] **Step 1: Write failing selection tests**

Create tests that prove the builder is threshold-based, keeps required words, rejects empty Chinese definitions, and assigns stable IDs:

```python
def test_required_confusable_is_kept_beyond_soft_target():
    result = select_entries(sample_entries(), required={"herbivorous"}, soft_min=3, soft_max=3)
    assert "herbivorous" in {x.word for x in result.entries}
    assert result.reason_for("herbivorous") == "user_confusable_closure"

def test_stable_id_is_case_insensitive_and_deterministic():
    assert stable_word_id("Inhabit") == stable_word_id("inhabit")
```

- [ ] **Step 2: Run tests and see the intended failure**

Run: `python -m unittest discover -s tools\vocabulary\tests -v`

Expected: FAIL because `selection` and `stable_word_id` do not exist.

- [ ] **Step 3: Implement explicit scoring and closure rules**

Implement `stable_word_id` as UUIDv5 over normalized Unicode-casefolded spelling. Implement a documented score using source evidence, Oxford flag, IELTS/TOEFL/GRE tags, BNC/modern frequency, required-topic membership, Chinese-definition presence, and phonetic presence. `soft_min=10000` and `soft_max=12000` guide ordinary selection; required topic/user closure may exceed the maximum and must be reported rather than truncated.

The curated CSV must include all approved groups plus `omnivorous`, with columns:

```csv
word,reason,source_key,review_status
herbivorous,ielts_topic_family,idp_animals,reviewed
carnivorous,ielts_topic_family,idp_animals,reviewed
stimulate,user_confusable,user_approved_2026_08_13,reviewed
```

- [ ] **Step 4: Build artifacts without overwriting the baseline**

Run first with staging paths:

```powershell
python tools\vocabulary\build_vocabulary.py --source data\sources\ecdict\ecdict.csv --curated data\curated\required_vocabulary.csv --output artifacts\vocabulary-candidate --soft-min 10000 --soft-max 12000
python tools\vocabulary\verify_vocabulary.py --artifact-dir artifacts\vocabulary-candidate
```

Expected: SQLite integrity `ok`; unique case-insensitive words equals total; total is at least 10,000; every released row has Chinese translation and provenance; every approved legal word is present; misspellings `stimuate` and `dissimuate` are absent as headwords.

- [ ] **Step 5: Promote only after verification and commit reproducibility inputs**

Copy verified outputs to new canonical names `data/ielts/vocabulary.csv` and `data/ielts/vocabulary.sqlite3`; retain old `ielts_8000.*` until migration tests pass. Commit scripts, curated inputs, licenses/metadata, manifest, and reports; keep raw ECDICT and generated large artifacts ignored.

```powershell
git add tools\vocabulary data\curated data\ielts\manifest.json data\reports docs\data .gitignore
git commit -m "data: build quality-gated IELTS vocabulary corpus"
```

### Task 3: Build the Versioned Offline Lexical-Relation Database

**Files:**
- Create: `tools/vocabulary/download_oewn.ps1`
- Create: `tools/vocabulary/build_relations.py`
- Create: `tools/vocabulary/confusable.py`
- Create: `tools/vocabulary/tests/test_relations.py`
- Create: `data/curated/confusable_groups.csv`
- Create: `data/curated/misspellings.csv`
- Create: `data/licenses/OEWN-2025-LICENSE.md`
- Create: `data/licenses/WORDNET-LICENSE.txt`
- Create: `docs/data/lexical-relations.md`

**Interfaces:**
- Consumes: verified vocabulary DB, OEWN 2025 core JSON/XML snapshot, curated groups, misspellings.
- Produces: `relations.sqlite3` with `lexical_sense`, `word_relation`, `build_metadata`, and a machine-readable quality report.

- [ ] **Step 1: Pin and verify OEWN before parsing**

`download_oewn.ps1` downloads only during development, never at app runtime, and requires an expected SHA-256 argument:

```powershell
param([Parameter(Mandatory)][string]$ExpectedSha256)
$uri='https://en-word.net/downloads/english-wordnet-2025-json.zip'
$out='data\sources\oewn\english-wordnet-2025-json.zip'
Invoke-WebRequest -Uri $uri -OutFile $out
if ((Get-FileHash $out -Algorithm SHA256).Hash -ne $ExpectedSha256) { throw 'OEWN hash mismatch' }
```

Record the verified hash, download date, canonical URL, license, and exact edition in `source_registry.json`.

- [ ] **Step 2: Write failing relation-quality tests**

```python
def test_synonyms_are_sense_and_pos_bound():
    rows = build_wordnet_relations(fixture_wordnet())
    assert all(r.source_sense_id and r.target_sense_id and r.pos for r in rows if r.kind == "synonym")

def test_user_groups_and_misspellings_are_classified_not_flattened():
    db = build_fixture_database()
    assert db.relation("herbivorous", "carnivorous").kind == "antonym_confusable"
    assert db.misspelling("stimuate") == "stimulate"
    assert db.word("stimuate") is None
```

Run: `python -m unittest tools.vocabulary.tests.test_relations -v`

Expected: FAIL because the relation builder is absent.

- [ ] **Step 3: Implement the four layers and schema**

Use explicit relation kinds: `synonym`, `antonym`, `derivational`, `spelling_similar`, `pronunciation_similar`, `root_confusable`, `topic_confusable`, `antonym_confusable`, `personal_confusable`, and `misspelling`. Store `source_entry_id`, `target_entry_id`, optional sense IDs/POS, direction, evidence source, score, review state, contrast Chinese text, collocation, and build version. Only `reviewed` curated relations and OEWN sense-backed semantic relations are published; algorithmic candidates remain `candidate`.

- [ ] **Step 4: Seed and verify every approved group**

Populate `confusable_groups.csv` with all 18 approved groups, including Chinese contrast notes. Build and run:

```powershell
python tools\vocabulary\build_relations.py --vocabulary artifacts\vocabulary-candidate\vocabulary.sqlite3 --oewn data\sources\oewn\english-wordnet-2025-json.zip --curated data\curated\confusable_groups.csv --misspellings data\curated\misspellings.csv --output artifacts\vocabulary-candidate\relations.sqlite3
python tools\vocabulary\verify_vocabulary.py --artifact-dir artifacts\vocabulary-candidate --relations
```

Expected: no self-links or duplicates; every target exists; every approved group is reachable in both directions; semantic synonyms never cross sense/POS; low-confidence candidates are excluded from published queries; quality report counts candidates, reviewed relations, filtered relations, missing definitions, and missing phonetics. The official URL has been probed as HTTP 200 with `application/zip` and content length 9,986,555 bytes; the implementation must still pin the freshly computed full-file SHA-256 before accepting the snapshot.

- [ ] **Step 5: Commit relation builder and attribution**

```powershell
git add tools\vocabulary data\curated data\licenses docs\data data\reports
git commit -m "data: add offline synonym and confusable relation builder"
```

**Milestone A acceptance:** Running the two verification commands from a clean artifact directory produces a 10,000+ word DB and relation DB with deterministic manifests; all user-approved groups pass; no application code is required to query the SQLite files manually.

---

## Milestone B — Learning Domain and Crash-Safe Persistence

### Task 4: Implement FSRS-6 as a Pure, Deterministic Domain Service

**Files:**
- Create: `src/WordFlow.Domain/Scheduling/Rating.cs`
- Create: `src/WordFlow.Domain/Scheduling/MemoryState.cs`
- Create: `src/WordFlow.Domain/Scheduling/FsrsParameters.cs`
- Create: `src/WordFlow.Domain/Scheduling/IFsrsScheduler.cs`
- Create: `src/WordFlow.Domain/Scheduling/Fsrs6Scheduler.cs`
- Create: `src/WordFlow.Domain/Scheduling/ScheduleResult.cs`
- Create: `tests/WordFlow.Domain.Tests/Scheduling/Fsrs6GoldenVectorTests.cs`
- Create: `tests/WordFlow.Domain.Tests/Scheduling/Fsrs6InvariantTests.cs`
- Create: `docs/algorithms/fsrs6.md`

**Interfaces:**
- Produces: `ScheduleResult IFsrsScheduler.Review(MemoryState? previous, Rating rating, DateTimeOffset reviewedAt, double desiredRetention)` and `double Retrievability(MemoryState state, DateTimeOffset at)`.

- [ ] **Step 1: Add golden vectors before formulas**

Use the 21 official FSRS-6 defaults from the approved design and freeze test vectors generated from the official algorithm reference. Tests must cover first review for Again/Hard/Good, same-day review, delayed recall, lapse, retention 0.85/0.90/0.95, and UTC/date-boundary behavior. Store vector provenance and upstream commit/reference in `docs/algorithms/fsrs6.md`.

- [ ] **Step 2: Verify red tests**

Run: `dotnet test tests\WordFlow.Domain.Tests --filter FullyQualifiedName~Fsrs6`

Expected: FAIL because `Fsrs6Scheduler` is missing.

- [ ] **Step 3: Implement formulas without UI or database dependencies**

Define immutable records:

```csharp
public sealed record MemoryState(double Difficulty, double StabilityDays, DateTimeOffset LastReviewAt);
public sealed record ScheduleResult(MemoryState State, double RetrievabilityBeforeReview, TimeSpan Interval, DateTimeOffset DueAt);
public enum Rating { Again = 1, Hard = 2, Good = 3 }
```

Clamp difficulty to `[1,10]`, validate retention `[0.85,0.95]` at the application boundary, reject non-finite parameters, use elapsed total days in UTC, and round scheduling intervals only at the final boundary. Do not implement an Easy rating because the product has no Easy action; Slash is not a rating.

- [ ] **Step 4: Add invariant/property loops and pass**

Test 10,000 deterministic histories: no NaN/infinity, negative interval, backwards due time, or out-of-range difficulty; retrievability at stability is approximately 0.9; higher desired retention never yields a later due date for the same state.

Run: `dotnet test tests\WordFlow.Domain.Tests --filter FullyQualifiedName~Scheduling`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\WordFlow.Domain\Scheduling tests\WordFlow.Domain.Tests\Scheduling docs\algorithms
git commit -m "feat: implement deterministic FSRS-6 scheduler"
```

### Task 5: Add Safe On-Device FSRS Parameter Optimization

**Files:**
- Create: `src/WordFlow.Domain/Scheduling/ReviewSample.cs`
- Create: `src/WordFlow.Application/Ports/IFsrsParameterOptimizer.cs`
- Create: `src/WordFlow.Infrastructure/Scheduling/FsrsParameterOptimizer.cs`
- Create: `src/WordFlow.Application/Scheduling/OptimizeFsrsParameters.cs`
- Create: `tests/WordFlow.Infrastructure.Tests/Scheduling/FsrsParameterOptimizerTests.cs`
- Create: `tests/WordFlow.Application.Tests/Scheduling/OptimizeFsrsParametersTests.cs`

**Interfaces:**
- Consumes: ordered, non-undone review events transformed into `ReviewSample` values.
- Produces: `OptimizationResult Optimize(IReadOnlyList<ReviewSample> samples, FsrsParameters baseline, CancellationToken ct)` with training/validation loss, sample count, and parameter snapshot.

- [ ] **Step 1: Write red eligibility and optimizer tests**

Test that fewer than 400 valid reviews returns `NotEligible`; undone events, Slash events, corrupt timestamps, and duplicate command IDs are excluded; deterministic synthetic histories return finite 21-parameter results; the optimizer is deterministic for a fixed seed; and a candidate with validation log loss worse than baseline is rejected.

- [ ] **Step 2: Run the focused tests and confirm failure**

Run:

```powershell
dotnet test tests\WordFlow.Infrastructure.Tests --filter FullyQualifiedName~FsrsParameterOptimizer
dotnet test tests\WordFlow.Application.Tests --filter FullyQualifiedName~OptimizeFsrsParameters
```

Expected: FAIL because optimizer types do not exist.

- [ ] **Step 3: Implement bounded offline optimization**

Implement a deterministic, cancellable bounded optimizer over the official FSRS-6 log-loss objective. Split chronological histories into training and validation portions without leaking future reviews backward. Validate every parameter and intermediate value as finite; preserve the official default parameter snapshot; return progress through `IProgress<OptimizationProgress>`; never block the WPF dispatcher. Only accept parameters when validation loss is no worse than the current set within a documented tolerance.

- [ ] **Step 4: Implement snapshot, preview, and future-only activation**

`OptimizeFsrsParameters` stores algorithm version, source parameter set, candidate set, sample count, losses, time, and status. Show a due-date preview before activation. Activation appends a settings event and affects only schedules computed after activation; it does not rewrite past ratings or immediately reschedule every existing card. Restore reactivates a previous snapshot through another settings event.

- [ ] **Step 5: Compare against fixed fixtures, pass, and commit**

Use the same frozen histories in Python/reference FSRS tooling and C# evaluation; require predicted retrievabilities and loss within documented floating-point tolerance.

```powershell
dotnet test tests\WordFlow.Infrastructure.Tests --filter FullyQualifiedName~Scheduling
dotnet test tests\WordFlow.Application.Tests --filter FullyQualifiedName~Scheduling
git add src tests docs\algorithms
git commit -m "feat: add safe local FSRS parameter optimization"
```

### Task 6: Define Learning, Slash, Undo, and Daily-Queue Rules

**Files:**
- Create: `src/WordFlow.Domain/Learning/CardState.cs`
- Create: `src/WordFlow.Domain/Learning/LearningAction.cs`
- Create: `src/WordFlow.Domain/Learning/ReviewEvent.cs`
- Create: `src/WordFlow.Domain/Learning/SlashState.cs`
- Create: `src/WordFlow.Domain/Learning/DailyPlan.cs`
- Create: `src/WordFlow.Domain/Learning/QueuePolicy.cs`
- Create: `tests/WordFlow.Domain.Tests/Learning/QueuePolicyTests.cs`
- Create: `tests/WordFlow.Domain.Tests/Learning/SlashLifecycleTests.cs`

**Interfaces:**
- Produces: `IReadOnlyList<Guid> QueuePolicy.Build(QueueInput input)`, `CardState Slash(CardState, instant)`, `CardState Restore(CardState, RestoreMode, instant)`.

- [ ] **Step 1: Write red tests for product semantics**

Tests assert: due reviews precede new words; overdue ordering uses retrievability/risk; default new limit 40 and soft review limit 120; an Again creates a 10-minute relearning step; at most three same-day failures then hard-word protection; slashed cards disappear; restoration retains historical D/S and supports scheduled/immediate modes; undo creates a compensating event rather than deleting history.

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests\WordFlow.Domain.Tests --filter FullyQualifiedName~Learning`

Expected: FAIL with missing learning types.

- [ ] **Step 3: Implement minimal policies**

Use an injected `TimeProvider`; never call `DateTime.Now` in domain code. Model Slash separately from FSRS. Define `QueueInput` with due cards, new candidates, daily plan, and current instant. Make queue ordering stable by risk then due time then stable entry ID.

- [ ] **Step 4: Pass domain tests and commit**

```powershell
dotnet test tests\WordFlow.Domain.Tests --filter FullyQualifiedName~Learning
git add src\WordFlow.Domain\Learning tests\WordFlow.Domain.Tests\Learning
git commit -m "feat: add daily queue and reversible slash rules"
```

### Task 7: Create Versioned SQLite Stores with Atomic Events and Snapshots

**Files:**
- Create: `src/WordFlow.Application/Ports/ILearningStore.cs`
- Create: `src/WordFlow.Application/Ports/IVocabularyRepository.cs`
- Create: `src/WordFlow.Application/Ports/IRelationRepository.cs`
- Create: `src/WordFlow.Infrastructure/Data/SqliteConnectionFactory.cs`
- Create: `src/WordFlow.Infrastructure/Data/Migrations/001_initial.sql`
- Create: `src/WordFlow.Infrastructure/Data/MigrationRunner.cs`
- Create: `src/WordFlow.Infrastructure/Data/SqliteLearningStore.cs`
- Create: `src/WordFlow.Infrastructure/Data/SqliteVocabularyRepository.cs`
- Create: `src/WordFlow.Infrastructure/Data/SqliteRelationRepository.cs`
- Create: `tests/WordFlow.Infrastructure.Tests/Data/SqliteLearningStoreTests.cs`
- Create: `tests/WordFlow.Infrastructure.Tests/Data/RepositoryContractTests.cs`

**Interfaces:**
- Produces: `Task<CommitResult> ILearningStore.ApplyAsync(LearningCommand command, CancellationToken ct)`, query ports for word/sense/relation pages.

- [ ] **Step 1: Specify the transactional contract in failing tests**

Test that ApplyAsync inserts immutable event and updates snapshot in one transaction; injected failure between writes rolls back both; repeated command ID is idempotent; undo appends an inverse event; user relation overrides live only in the user DB; read-only corpus connections reject writes.

- [ ] **Step 2: Confirm tests fail**

Run: `dotnet test tests\WordFlow.Infrastructure.Tests --filter FullyQualifiedName~Data`

Expected: FAIL because stores/migrations are missing.

- [ ] **Step 3: Implement schemas and stores**

`001_initial.sql` creates `card_state`, `review_event`, `slash_event`, `daily_plan`, `app_setting`, `shortcut_binding`, `user_word_relation`, `skin_preset`, `backup_record`, and `schema_version`. Store timestamps as UTC ISO-8601 and stable IDs as 16-byte blobs or canonical strings consistently. Enable foreign keys, busy timeout, and WAL for the user DB; open vocabulary/relation DBs with `Mode=ReadOnly`.

- [ ] **Step 4: Add migration rollback and baseline-to-new-corpus mapping tests**

Verify a migration failure leaves schema/data unchanged and backup created; stable word IDs map learning records from the old 8,000 baseline to the promoted corpus without losing review/slash history.

- [ ] **Step 5: Run tests and commit**

```powershell
dotnet test tests\WordFlow.Infrastructure.Tests --filter FullyQualifiedName~Data
git add src\WordFlow.Application\Ports src\WordFlow.Infrastructure\Data tests\WordFlow.Infrastructure.Tests\Data
git commit -m "feat: add crash-safe SQLite persistence"
```

### Task 8: Implement Application Use Cases and View-Neutral State

**Files:**
- Create: `src/WordFlow.Application/Learning/GetNextCard.cs`
- Create: `src/WordFlow.Application/Learning/SubmitRating.cs`
- Create: `src/WordFlow.Application/Learning/SlashWord.cs`
- Create: `src/WordFlow.Application/Learning/RestoreSlashedWords.cs`
- Create: `src/WordFlow.Application/Learning/UndoLastAction.cs`
- Create: `src/WordFlow.Application/Relations/GetSynonyms.cs`
- Create: `src/WordFlow.Application/Relations/GetConfusables.cs`
- Create: `src/WordFlow.Application/Relations/UpdatePersonalRelation.cs`
- Create: `tests/WordFlow.Application.Tests/Learning/LearningUseCaseTests.cs`
- Create: `tests/WordFlow.Application.Tests/Relations/RelationUseCaseTests.cs`

**Interfaces:**
- Produces request/response records used by every UI; no WPF types cross the boundary.

- [ ] **Step 1: Write orchestration tests with in-memory fakes**

Test F1/F2/F3 mapping, commit-before-next-card, Slash not mapped to Easy, restore modes, undo, no-relation empty results, all verified relations returned without arbitrary limit, grouping synonyms by sense/POS, personal overrides, and misspellings excluded from headword learning.

- [ ] **Step 2: Run tests red**

Run: `dotnet test tests\WordFlow.Application.Tests`

Expected: FAIL with missing handlers.

- [ ] **Step 3: Implement one handler per use case**

Each handler accepts its ports plus `TimeProvider`, returns discriminated result records such as `Success`, `Conflict`, `StorageFailure`, or `NotFound`, and never swallows exceptions. `GetSynonyms`/`GetConfusables` return complete verified results; pagination happens only in the view if needed.

- [ ] **Step 4: Pass tests and commit**

```powershell
dotnet test tests\WordFlow.Application.Tests
git add src\WordFlow.Application tests\WordFlow.Application.Tests
git commit -m "feat: add learning and lexical relation use cases"
```

**Milestone B acceptance:** Domain and application tests replay deterministic review histories, persistence fault injection proves atomicity, and a console/test harness can learn, slash, restore, undo, and query complete synonym/confusable groups without WPF.

---

## Milestone C — Windows Floating Learning Experience

### Task 9: Bootstrap a Single-Instance Offline WPF Application

**Files:**
- Modify: `src/WordFlow.App/App.xaml`
- Modify: `src/WordFlow.App/App.xaml.cs`
- Create: `src/WordFlow.App/Bootstrap/AppPaths.cs`
- Create: `src/WordFlow.App/Bootstrap/ServiceRegistration.cs`
- Create: `src/WordFlow.Infrastructure/Windows/SingleInstanceCoordinator.cs`
- Create: `src/WordFlow.Infrastructure/Windows/TrayIconService.cs`
- Create: `tests/WordFlow.Infrastructure.Tests/Windows/SingleInstanceCoordinatorTests.cs`

**Interfaces:**
- Produces: one process owns the user DB; second launch signals the first; explicit tray Exit disposes resources.

- [ ] **Step 1: Add failing single-instance and path tests**

Verify `%LOCALAPPDATA%\WordFlow` subdirectories `Data`, `Backups`, `Skins`, `Cache`, and `Logs`; verify two coordinators cannot both become primary; verify a secondary activation callback reaches the primary.

- [ ] **Step 2: Implement named mutex plus named pipe activation**

Use user-scoped names derived from the current Windows SID. Do not use a broad machine-global mutex. Initialize directories, verify bundled DB hashes, migrate/backup user DB, then open the floating card. On fatal corpus failure show a local repair dialog; never download replacements automatically.

- [ ] **Step 3: Add tray lifecycle**

Tray actions: Show/Hide Card, Pause/Resume, Open Control Center, Today Progress, Exit. Closing a window hides it; only Exit terminates.

- [ ] **Step 4: Test, launch smoke test, and commit**

```powershell
dotnet test tests\WordFlow.Infrastructure.Tests --filter FullyQualifiedName~Windows
dotnet run --project src\WordFlow.App
```

Expected: one floating window and one tray icon; second launch activates the existing instance; no second DB writer.

Commit: `git commit -am "feat: bootstrap single-instance WPF shell"` after staging the exact task files.

### Task 10: Implement Configurable Shortcut Registration and Conflict Safety

**Files:**
- Create: `src/WordFlow.Application/Shortcuts/ShortcutAction.cs`
- Create: `src/WordFlow.Application/Shortcuts/ShortcutBinding.cs`
- Create: `src/WordFlow.Application/Ports/IShortcutService.cs`
- Create: `src/WordFlow.Infrastructure/Windows/GlobalShortcutService.cs`
- Create: `src/WordFlow.App/ViewModels/ShortcutSettingsViewModel.cs`
- Create: `tests/WordFlow.Infrastructure.Tests/Windows/GlobalShortcutServiceTests.cs`
- Create: `tests/WordFlow.App.Tests/ViewModels/ShortcutSettingsViewModelTests.cs`

**Interfaces:**
- Produces: `ShortcutRegistrationResult TryReplace(ShortcutAction action, ShortcutBinding candidate)` with atomic rollback.

- [ ] **Step 1: Write red tests for every binding rule**

Cover all default actions, application duplicate detection, global registration conflict, focus-only bindings, disable, persisted restore, reset-all, and replacement failure retaining the prior active binding.

- [ ] **Step 2: Implement registration adapter**

Use Win32 `RegisterHotKey`/`UnregisterHotKey` only for Global scope. Focused scope uses WPF input bindings. Reserve no keys silently. The service stages the candidate, registers it, persists it, then removes the old registration; any failure rolls back candidate and keeps old binding.

- [ ] **Step 3: Implement shortcut recorder VM**

Recorder shows normalized chord, scope, conflict reason, enable toggle, and Restore Default. Reject bare modifier keys and OS-reserved combinations. Publish binding-changed notifications so every button label updates immediately.

- [ ] **Step 4: Test and commit**

```powershell
dotnet test tests\WordFlow.Infrastructure.Tests --filter FullyQualifiedName~GlobalShortcut
dotnet test tests\WordFlow.App.Tests --filter FullyQualifiedName~ShortcutSettings
git add src tests
git commit -m "feat: add safe configurable shortcuts"
```

### Task 11: Build the Compact Always-on-Top Card and Relation Drawers

**Files:**
- Create: `src/WordFlow.App/Views/FloatingCardWindow.xaml`
- Create: `src/WordFlow.App/Views/FloatingCardWindow.xaml.cs`
- Create: `src/WordFlow.App/ViewModels/FloatingCardViewModel.cs`
- Create: `src/WordFlow.App/Views/Controls/RelationDrawer.xaml`
- Create: `src/WordFlow.App/ViewModels/RelationDrawerViewModel.cs`
- Create: `src/WordFlow.Infrastructure/Windows/WindowPlacementService.cs`
- Create: `tests/WordFlow.App.Tests/ViewModels/FloatingCardViewModelTests.cs`
- Create: `tests/WordFlow.App.Tests/ViewModels/RelationDrawerViewModelTests.cs`

**Interfaces:**
- Consumes: learning/relation use cases and live shortcut labels.
- Produces: approved word → phonetic → Chinese layout, rating flow, F4/F5 drawers, durable placement.

- [ ] **Step 1: Write VM tests before XAML**

Verify phonetic follows word in exposed reading order; rating disables re-entry until transaction succeeds; failed write does not switch card; F4/F5 toggle independently but only one drawer is visible; drawers reset after successful rating; complete results are preserved and searchable; no-result copy is exact; clicking relation requests TTS/details/add-to-learning.

- [ ] **Step 2: Implement the minimal accessible XAML**

Use a borderless rounded card, word as highest visual weight, phonetic directly below, Chinese below that, progress and actions at bottom. Buttons bind live gesture text. Relation drawer expands downward without moving the word block, caps visible height after five rows, and scrolls internally. Each row shows word, phonetic, precise Chinese meaning, contrast, collocation, relation badge, audio, and context menu.

- [ ] **Step 3: Implement window behavior**

Drag only blank/chrome regions. Default to primary work-area bottom-right, avoid taskbar, store monitor/device-independent coordinates, repair offscreen placement after DPI/monitor changes, and temporarily remove topmost in detected full-screen foreground apps when the setting is enabled.

- [ ] **Step 4: Add UI Automation smoke tests**

Tests locate named elements, verify tab order and automation names, open both drawers, scroll more than five items, and assert the current word remains unchanged.

- [ ] **Step 5: Run tests, visually compare with approved mockup, and commit**

```powershell
dotnet test tests\WordFlow.App.Tests --filter "FullyQualifiedName~FloatingCard|FullyQualifiedName~RelationDrawer"
dotnet run --project src\WordFlow.App
git add src\WordFlow.App src\WordFlow.Infrastructure\Windows tests\WordFlow.App.Tests
git commit -m "feat: build floating learning card and relation drawers"
```

### Task 12: Add Completely Offline Pronunciation

**Files:**
- Create: `src/WordFlow.Application/Ports/IPronunciationService.cs`
- Create: `src/WordFlow.Infrastructure/Audio/WindowsSpeechPronunciationService.cs`
- Create: `src/WordFlow.App/ViewModels/PronunciationSettingsViewModel.cs`
- Create: `tests/WordFlow.Infrastructure.Tests/Audio/WindowsSpeechPronunciationServiceTests.cs`

**Interfaces:**
- Produces: installed voice enumeration and cancellable `SpeakAsync(text, voiceId, rate, volume, ct)`.

- [ ] **Step 1: Write tests with a speech-engine seam**

Cover installed English voices, British/American preference, no-English-voice state, rate/volume validation, cancellation when switching cards, and non-blocking failure.

- [ ] **Step 2: Implement System.Speech adapter**

Create synthesizer on a dedicated STA dispatcher, enumerate only installed enabled English voices, cancel previous speech before new speech, and never attempt network fallback. The user message for no voice points to Windows offline language settings without launching a URL.

- [ ] **Step 3: Wire click, autoplay, and configurable shortcut**

Clicking current/related word speaks it; autoplay occurs after the card changes if enabled. Voice, accent preference, rate, volume, autoplay, and shortcut persist.

- [ ] **Step 4: Test with and without an English voice and commit**

```powershell
dotnet test tests\WordFlow.Infrastructure.Tests --filter FullyQualifiedName~Pronunciation
git add src tests
git commit -m "feat: add offline Windows pronunciation"
```

**Milestone C acceptance:** The app cold-starts to one draggable topmost card, works through tray, rates with editable shortcuts, opens complete F4/F5 drawers without switching words, speaks offline, survives monitor/DPI changes, and never opens a network connection.

---

## Milestone D — Control Center, Resilience, and Distribution

### Task 13: Build the Control Center Pages and 100-Row Slashed View

**Files:**
- Create: `src/WordFlow.App/Views/ControlCenterWindow.xaml`
- Create: `src/WordFlow.App/Views/Pages/DashboardPage.xaml`
- Create: `src/WordFlow.App/Views/Pages/VocabularyPage.xaml`
- Create: `src/WordFlow.App/Views/Pages/ConfusablesPage.xaml`
- Create: `src/WordFlow.App/Views/Pages/SlashedWordsPage.xaml`
- Create: `src/WordFlow.App/Views/Pages/StatisticsPage.xaml`
- Create: `src/WordFlow.App/Views/Pages/SettingsPage.xaml`
- Create: matching focused ViewModels under `src/WordFlow.App/ViewModels/Pages/`
- Create: `tests/WordFlow.App.Tests/ViewModels/Pages/SlashedWordsPageViewModelTests.cs`
- Create: `tests/WordFlow.App.Tests/ViewModels/Pages/DashboardPageViewModelTests.cs`

**Interfaces:**
- Consumes: use cases and repositories; produces the approved dashboard, library, confusable management, slash management, statistics, and settings flows.

- [ ] **Step 1: Write red pagination and dashboard tests**

Test default 100 rows, selectable 50/100/200 persisted, stable filtering/paging, visible range text, page-select versus all-filtered-select, bulk restore confirmation, scheduled/immediate restore, short undo, due/new/progress cards, and no anxiety-red for ordinary overdue counts.

- [ ] **Step 2: Implement navigation shell and focused pages**

Use one VM per page. Keep headers and pagination footer fixed while row content scrolls. `ConfusablesPage` supports searching built-in/personal relations, relation type, adding/removing personal overrides, and restoring built-in behavior. Statistics reads immutable events and never recalculates historical ratings from current settings.

- [ ] **Step 3: Complete settings categories**

Learning, Shortcuts, Floating/Appearance, Pronunciation, Skin, Data/Backup, and Advanced FSRS pages. Clamp ordinary desired retention to 0.85–0.95. Advanced parameters show source/version and require snapshot/impact explanation before replacement.

- [ ] **Step 4: Run VM/UI tests and commit**

```powershell
dotnet test tests\WordFlow.App.Tests --filter FullyQualifiedName~Pages
git add src\WordFlow.App tests\WordFlow.App.Tests
git commit -m "feat: build control center and slashed word management"
```

### Task 14: Implement Per-Surface PNG Skins with Readability Protection

**Files:**
- Create: `src/WordFlow.Application/Skins/SkinPreset.cs`
- Create: `src/WordFlow.Application/Ports/ISkinService.cs`
- Create: `src/WordFlow.Infrastructure/Skins/SkinImportService.cs`
- Create: `src/WordFlow.App/Styling/SkinResourceManager.cs`
- Create: `src/WordFlow.App/Views/Controls/SkinEditor.xaml`
- Create: `tests/WordFlow.Infrastructure.Tests/Skins/SkinImportServiceTests.cs`
- Create: `tests/WordFlow.App.Tests/Styling/SkinResourceManagerTests.cs`

**Interfaces:**
- Produces: global presets plus floating/control-center overrides and safe cached images.

- [ ] **Step 1: Write red import/render-state tests**

Cover valid PNG copy into user data, invalid signature, corrupt image, pixel/dimension/file-size limits, deterministic cache key, independent opacity/blur/overlay/content opacity/focal point, global and per-surface inheritance, missing source after import, and fallback to default.

- [ ] **Step 2: Implement safe import and cache**

Decode dimensions before full render, reject decompression bombs using configured pixel cap, copy original with content hash name, create screen-sized cache, store only relative paths, and never overwrite user source. Failed import leaves active skin unchanged.

- [ ] **Step 3: Implement WPF rendering**

Use `ImageBrush Stretch="UniformToFill"`, focal alignment, rounded clipping, full-corner coverage, and a computed protective overlay. Ensure text contrast remains readable; high-contrast mode bypasses decorative images if required.

- [ ] **Step 4: Test, inspect four corners at multiple aspect ratios, and commit**

```powershell
dotnet test tests\WordFlow.Infrastructure.Tests --filter FullyQualifiedName~Skin
dotnet test tests\WordFlow.App.Tests --filter FullyQualifiedName~Skin
git add src tests
git commit -m "feat: add safe customizable PNG skins"
```

### Task 15: Add Backup, Restore, Export, Diagnostics, and Fault Recovery

**Files:**
- Create: `src/WordFlow.Application/Ports/IBackupService.cs`
- Create: `src/WordFlow.Infrastructure/Backup/SqliteBackupService.cs`
- Create: `src/WordFlow.Infrastructure/Backup/DataExportService.cs`
- Create: `src/WordFlow.Infrastructure/Diagnostics/LocalFileLogger.cs`
- Create: `src/WordFlow.App/ViewModels/RecoveryViewModel.cs`
- Create: `tests/WordFlow.Infrastructure.Tests/Backup/SqliteBackupServiceTests.cs`
- Create: `tests/WordFlow.Infrastructure.Tests/Backup/CorruptionRecoveryTests.cs`

**Interfaces:**
- Produces: daily rolling backup (14 default), manual backup/restore, JSON/CSV export, integrity audit, local-only logs.

- [ ] **Step 1: Write fault-first tests**

Test consistent SQLite backup during reads, 14-day retention without deleting unrelated files, hash verification, corrupt source preserved, restore through a temp file plus atomic replace, schema-version rejection, read-only user directory, disk-full simulation, and export redaction policy.

- [ ] **Step 2: Implement recovery-safe operations**

Use SQLite online backup API, write manifest with schema/app versions and SHA-256, validate into a temporary DB, close active connections, atomically replace, and keep the failed original with timestamp. At first launch each local day, create one rolling backup. Never include skins unless the user selects a full archive.

- [ ] **Step 3: Implement local diagnostics**

Rotate local logs, exclude word review content by default, include actionable error codes, and provide Open Log Folder. No telemetry sink or HTTP package may be registered.

- [ ] **Step 4: Run fault tests and commit**

```powershell
dotnet test tests\WordFlow.Infrastructure.Tests --filter "FullyQualifiedName~Backup|FullyQualifiedName~Recovery"
git add src tests
git commit -m "feat: add local backup and recovery"
```

### Task 16: Full Verification Matrix and Self-Contained Installer

**Files:**
- Create: `installer/WordFlow.wixproj`
- Create: `installer/Package.wxs`
- Create: `scripts/verify-release.ps1`
- Create: `tests/WordFlow.App.Tests/EndToEnd/CriticalJourneyTests.cs`
- Create: `docs/testing/windows-matrix.md`
- Create: `docs/user-guide.md`
- Create: `THIRD-PARTY-NOTICES.md`
- Create: `LICENSES/` license copies

**Interfaces:**
- Produces: versioned self-contained x64 MSI, checksums, test/compatibility report, and recovery/uninstall semantics.

- [ ] **Step 1: Add release-gate tests**

Critical journey: first launch → set task → rate Again/Hard/Good → F4/F5 → Slash → view 100-row page → cancel Slash → undo → import skin → backup → restart → state intact. Add a network guard test that fails if production assemblies reference `System.Net.Http` or configure network clients.

- [ ] **Step 2: Create deterministic publish and MSI**

Publish command:

```powershell
dotnet publish src\WordFlow.App\WordFlow.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true -o artifacts\publish\win-x64
dotnet build installer\WordFlow.wixproj -c Release -p:PublishDir=D:\baicizhan\artifacts\publish\win-x64
```

MSI installs per-user or with clearly prompted elevation, registers Start Menu shortcut, supports upgrade, and leaves `%LOCALAPPDATA%\WordFlow` intact on ordinary uninstall. A separate explicit checkbox/action removes personal data.

- [ ] **Step 3: Execute automated release verification**

`scripts/verify-release.ps1` runs restore, all tests, Release build, data verifiers, publish, SQLite integrity, license presence, clean-install smoke test, launch-time measurement, idle-memory sample, and SHA-256 generation. It exits nonzero on any failed gate.

Run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\verify-release.ps1
```

Expected: zero failed tests, no missing license/source manifest, no network dependency, data integrity `ok`, and MSI/hash artifacts produced. Performance thresholds remain acceptance targets until measured: cold start ≤2 seconds, shortcut feedback ≤100 ms, idle working set <150 MB.

- [ ] **Step 4: Execute physical/VM compatibility matrix**

Record evidence, not impressions, for Windows 10 22H2 and current Windows 11 clean VMs; 100/125/150/200% DPI; single/dual monitor and hot unplug; taskbars on each edge; full-screen suppression; no English voice; invalid PNG; DB corruption; forced termination during write; install/upgrade/uninstall/preserve data. Any failed row blocks release.

- [ ] **Step 5: Final review and commit release engineering**

```powershell
git add installer scripts tests\WordFlow.App.Tests\EndToEnd docs THIRD-PARTY-NOTICES.md LICENSES
git commit -m "build: add verified Windows installer and release gates"
```

**Milestone D acceptance:** All automated gates pass, the Windows matrix has evidence for every row, the MSI runs offline on clean Win10/11 VMs, uninstall preserves personal data by default, and no unverified performance or corpus-completeness claim appears in the UI/docs.

---

## Final Completion Gate

Before declaring the project complete, run from `D:\baicizhan`:

```powershell
python tools\vocabulary\verify_vocabulary.py --artifact-dir data\ielts --relations
dotnet test WordFlow.sln -c Release
powershell -ExecutionPolicy Bypass -File scripts\verify-release.ps1
git status --short
```

Expected: all data checks and tests pass; release verification exits 0; Git status contains no accidental raw corpus, secrets, build outputs, runtime user data, or unrelated user files. Review the approved design specification section-by-section against executable tests and the Windows evidence matrix before making any completion claim.
