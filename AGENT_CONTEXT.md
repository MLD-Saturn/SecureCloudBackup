# AGENT_CONTEXT.md — Handoff for Copilot Sessions

This file is the **persistent memory** for Copilot agent sessions on the AzureBackup repo. It is checked into source control so the agent can read it on a fresh chat (on **any machine, any OS-supported environment**) and immediately understand the project state, conventions, recent decisions, and open questions.

## How to use this file

**At the start of every fresh Copilot chat:** read this file before doing anything else. Treat the "Active workstreams" section as a continuation of the previous session.

**Throughout the session, UPDATE this file when ANY of the following happens:**

- A user preference, coding convention, or workflow rule is established or changed.
- A non-obvious production behavior is discovered (especially memory, concurrency, or persistence).
- A benchmark is run that produces a measurable design-relevant number.
- A workstream completes, blocks, or pivots.
- A commit lands that future sessions need to know about (e.g. a new B-numbered fix).
- A "do not do this" lesson is learned (e.g. a refactor that broke something).

**Edit policy:**
- Keep entries dated (YYYY-MM-DD).
- Move completed workstreams to the "Completed workstreams" section near the bottom.
- Keep the file under ~600 lines so a fresh agent can read it in one tool call.
- **Never** record machine-specific values (free disk, CPU model, core count, terminal-tool timeouts, exact wall-clock times, OS-edition build numbers). Those mislead the next session if it is on a different PC. Record *relative* findings (deltas, percentages, comparisons) and *workload* identity (which benchmark / which workload name produced the number) instead.

**Anti-pattern:** do not duplicate code or full xmldoc into this file. Reference file paths, commit hashes, and benchmark file xmldoc tables instead.

---

## Project at a glance

- **Name:** AzureBackup — a Windows desktop application that backs up local files to Azure Blob Storage with client-side encryption and content-defined chunking for deduplication.
- **Language/runtime:** C# on .NET 10 (`net10.0` TFM).
- **Solution:** `azurebackup.sln` at repo root.
- **Project layout:**
  - `src/AzureBackup.Core/` — all backup/restore/encryption/database logic. Cross-platform-clean C# library; no UI dependencies. **All non-trivial logic lives here.**
  - `src/AzureBackup/` — WPF UI shell (`MainWindowViewModel` + views). **Windows-only.** A non-Windows session can build / test `Core` and `Benchmarks` but cannot build the WPF project.
  - `tests/AzureBackup.Tests/` — xUnit. As of B25-bench-2: 759 tests, all passing.
  - `benchmarks/AzureBackup.Benchmarks/` — BenchmarkDotNet 0.15.8. Release-only, never run in CI. See "Running tests and benchmarks" below.
- **Setup:** see `docs/SETUP.md` for clone-to-build instructions. NuGet restore handles the SQLCipher native binaries automatically.
- **User-facing docs:** `docs/USER_GUIDE.md` for the WPF app's UX (memory limit slider, watched folders, restore flow, etc.).
- **Database backend:** SQLCipher-encrypted SQLite (production default since commit `63103b1` = `C-5`). Legacy LiteDB code path still present for migration only.
- **Authentication:** Argon2id KDF (64 MB, 8 lanes, 3 iterations) for both database key derivation and the `EncryptionService` content key.
- **Encryption envelope:** AES-256-GCM with `[magic(4) | version(1) | nonce(12) | ciphertext(N) | tag(16) | crc32(4)]`. `EncryptionService.EncryptionOverhead = 37`.
- **Chunking:** content-defined (Rabin-style rolling hash, window=48, prime=31). Per-extension chunk-size config (`.mkv` uses 1-64 MB, `.txt` uses 16-128 KB, etc.). Files >500 MB use 16-128 MB chunks regardless of extension.

---

## Hard rules (USER PREFERENCES — never violate)

These come from `.github/copilot-instructions.md` and from explicit user instruction during session work. Reproduced here because they apply to every session.

### Terminal commands

- **Single physical line, always.** Chain with `;`. No here-strings, no backticks, no multi-line.
- The user's shell is **PowerShell 7+ (`pwsh.exe`)**. Other shells will be flagged immediately.
- Length is not a concern — correctness and single-line execution are.
- Occasionally a tooling display artifact mangles the leading characters of the command in the echoed output (e.g. `Set-Location` shown as `t-Location`). The command **still runs correctly**; check the actual output / git state, not the echo.

### Git commits

- Commit immediately at logical stopping points.
- Use `git add .; git commit -m "..."` chained on one line, with multiple `-m` flags (one per paragraph). **Never** write the message to a file. **Never** use `git commit -F`.
- **No emoji, no non-ASCII** in commit messages.
- Escape `"` and `\` properly so the command parses cleanly.
- Commit message convention in this repo: `B<number>: <imperative summary>` (or the older `C-<number>:`). Pick the next free B-number — see `git log --oneline | Select-Object -First 1`.
- Push when work reaches a natural review point or when the user is about to switch machines.

### Tool usage

- Prefer `replace_string_in_file` / `multi_replace_string_in_file` over re-creating files. Re-create only when the file is small or being rewritten end-to-end.
- Read files before editing them.
- The `nuget_get-nuget-solver*` and `start_modernization` tools are NEVER appropriate for this project's typical work — do not invoke them unless the user explicitly asks for a package upgrade or .NET version change.
- Do not call `code_search` in parallel; do not call `run_command_in_terminal` in parallel.
- Long-running operations (benchmarks, large test sweeps) may exceed the terminal tool's timeout (whatever it is in the current environment). BenchmarkDotNet writes its result artifacts on each child-process completion, so even when the orchestrating call appears to time out you can usually find the artifacts under `BenchmarkDotNet.Artifacts/results/`. Always check there before assuming a benchmark didn't run.

---

## Architectural facts to load into working memory

These are non-obvious and have caused real bugs when forgotten.

1. **`SqliteBackend` is single-connection, single-threaded.** Post-B23 every read AND write goes through one `SQLiteConnection` serialized by a single `_writeLock`. Adding more file-level parallelism cannot help SqliteBackend throughput; it just adds contention. This is a known constraint of the chosen backend, not a bug.

2. **`FileOperationDiagnostics` (`.diag` files)** are written by 4 producers: `BackupOrchestrator.BackupFileAsync`, `RestoreService.RestoreFileAsync`, `RestoreService.Batch.cs` (CorruptedRecovery), `IntegrityCheckService.CheckOneFileAsync`. Post-B23 + B24, **all four only emit on real errors**. The `FlushAllLive` shutdown hook in `FileOperationDiagnostics` snapshots any diag still in the live `_live` registry at process exit — so the success/cancel paths in producers MUST call `Discard()` to deregister.

3. **`MemoryBudget` is a soft cap, configured via the WPF app's memory-limit slider** (`MemoryLimitEnabled` + `MemoryLimitMB` in the Configuration table). When `MemoryLimitEnabled=false` the budget is unlimited and the orchestrator can hold many GB of in-flight chunk buffers — confirmed by production telemetry showing multi-GB process working sets on large workloads with `memoryBudget=unlimited`. The dominant memory cost is `EffectiveMaxParallelChunkUploads (default 6) × per-chunk size (up to 64 MB for media) × EffectiveMaxParallelFileBackups (default 8)`.

4. **CPU is NOT the production bottleneck.** Production telemetry on a 7,313-file backup showed CPU usage well below 5%. The actual bottleneck is somewhere downstream: disk read throughput, the SqliteBackend write lock, or HttpClient/TLS state. Any "make this more parallel" hypothesis must be measured rather than reasoned about — the benchmarks under `benchmarks/AzureBackup.Benchmarks/` exist precisely for this.

5. **`InMemoryBlobService`** (`src/AzureBackup.Core/Services/InMemoryBlobService.cs`) is a production-quality fake of `IBlobStorageService`. Used by ~10 test files and by all 4 backup benchmarks. Constructor takes optional `simulatedLatencyMs` and `failureRate`. Honors deduplication. Tracks `TotalBytesUploaded` / `TotalOperations`. **This is the single biggest enabler of network-free testing in the project.** Use it instead of inventing a new mock.

6. **`AZBK_USE_SQLITE=1` env var** forces the SQLite backend. Set by benchmark `[GlobalSetup]`. Read once at `LocalDatabaseService.Initialize`; flipping mid-process is a no-op.

7. **`BackupOrchestrator` constructor** signature: `(LocalDatabaseService, EncryptionService, ChunkingService, IBlobStorageService, FileWatcherService)`. The `FileWatcherService` is required even when no folders are watched — it can be constructed without ever being started.

8. **`BackupOrchestrator` parallelism overrides (B25-bench-2 seam):** two optional `int?` properties — `MaxParallelChunkUploadsOverride` (default null → 6) and `MaxParallelFileBackupsOverride` (default null → 8). Read once per backup operation via internal `EffectiveMax*` getters so they cannot change mid-pipeline. Production behavior is unchanged when overrides are unset; the seam exists for the parallelism benchmarks. If you want to change the production *default*, change the constants in `BackupOrchestrator.cs`, not the override.

---

## Active workstreams (as of 2026-04-24)

### W1 (potential B26): act on the B25-bench-2 measured findings

The B25-bench-2 commit (`171f07e`) added three design-decision benchmarks under `benchmarks/AzureBackup.Benchmarks/`. Headline findings (full numbers in each benchmark file's xmldoc result table — go there for the authoritative data captured at commit time):

- **Largest-first file scheduling** (`LargestFirstSchedulingBenchmark`): helps `large-skew` workloads but **regresses** `mixed-realistic-1000` and `realistic-large-200`. **Do not ship a blanket sort.** See the benchmark's xmldoc for the regression hypothesis.
- **File-level concurrency 16-way** (`TwoTierFileSplitBenchmark`): appears Pareto-better than the current 8-way default — biggest win on `uniform-1MB-1000`, no regressions vs 8-way, negligible peak-memory impact. **Defensible to change the production default from 8 to 16.**
- **File-level concurrency 32-way**: workload-dependent. Wins on small-file workloads, regresses `large-skew-100`. Not a unilateral win.
- **Per-file chunk concurrency** (`AdaptiveChunkConcurrencyBenchmark`): production default of 6 is close to optimal. Workload-specific wins exist (4-way for small-file workloads, 12-way for large-file workloads) but require true adaptive logic.

Three follow-up commits are now defensible based on measured data:

- **B26a (cheap, high confidence)**: change `MaxParallelFileBackups` constant in `BackupOrchestrator.cs` from 8 to 16. Re-run `BackupThroughputBenchmark` and `TwoTierFileSplitBenchmark` afterward to confirm no regression on the new hardware. Document the choice with a comment citing `TwoTierFileSplitBenchmark.cs`.
- **B26b (medium effort)**: implement per-file adaptive chunk concurrency in `BackupOrchestrator.BackupFileAsync` that reads the per-file `MaxChunkSize` from the chunker config and scales concurrency inversely. Mirror `RestoreService.ComputeAdaptiveChunkConcurrency` (~20 LOC). Re-run `AdaptiveChunkConcurrencyBenchmark` to confirm both the small-file and large-file wins survive.
- **B26c (DO NOT DO)**: blanket largest-first sort. Benchmark proved this regresses the typical workload.

### W2 (possible future): investigate the LPT regression

`LargestFirstSchedulingBenchmark` showed that LPT scheduling regresses `mixed-realistic-1000` and `realistic-large-200` against input-order. The current best hypothesis is that input-order interleaves large+small files so steady-state pipelining keeps the SqliteBackend write lock contended at a healthy rate, while LPT front-loads long tasks and leaves most workers idle once the small files finish. Worth investigating only if someone wants to ship a workload-aware scheduler. Otherwise the answer is "leave production scheduling alone and document why."

---

## Completed workstreams (recent)

- **B25-bench-2 (commit `171f07e`, 2026-04-24)**: Added `BackupBenchmarkBase`, `LargestFirstSchedulingBenchmark`, `TwoTierFileSplitBenchmark`, `AdaptiveChunkConcurrencyBenchmark`. Added strictly-additive `MaxParallelChunkUploadsOverride` / `MaxParallelFileBackupsOverride` seams on `BackupOrchestrator`. All 759 tests still pass.
- **B25-bench (commit `614825a`)**: First end-to-end backup throughput benchmark. Established the `InMemoryBlobService`-based pipeline test pattern.

---

## Conventions / patterns observed in this codebase

- **Numbered fixes**: every commit message starts with `B<n>:`. The current series is at B25. Pick the next free number when committing a new fix or feature.
- **`[Conditional("DIAGNOSTICLOG")]` logging**: services use `Log(string)` methods that are completely no-ops in Release without the `DIAGNOSTICLOG` constant set. The B16 commit also adds a title-bar marker showing whether the build has DEBUG/DIAG on.
- **Diagnostic events forwarded by name**: services raise `EventHandler<string>? DiagnosticLog` events; `MainWindowViewModel.OnDiagnosticLog` aggregates them all.
- **Crash-safe atomic operations**: file moves during DB migration use a sentinel `.upgrade-pending` JSON file plus 4-step rename dance. See `LocalDatabaseService.Migration.cs` and `.Migration.Sqlite.cs`. Do not invent new file-rename code without reading those.
- **`PaddedLong` for hot atomic counters**: cache-line-padded structs prevent false sharing. See `BackupOrchestrator` line ~592 for example. If adding new hot counters under parallel workers, use `PaddedLong`.
- **`ArrayPool<byte>` discipline**: producer/consumer code transfers ownership of pooled buffers via the channel. The `ChunkPayload` record carries the rented buffer and the consumer is responsible for `ArrayPool<byte>.Shared.Return(payload.Data)` after upload completes. Do not double-return.
- **No emoji** anywhere in the codebase or commit messages.
- **xmldoc style is heavy and discursive** — multi-paragraph `<remarks>` blocks explaining WHY a design decision was made, with B-numbers cross-referencing commits. Match this style when adding new public surface.

---

## Running tests and benchmarks

### Tests

`dotnet test tests/AzureBackup.Tests/AzureBackup.Tests.csproj -c Debug` from the repo root. All 759 tests pass deterministically.

If running from inside Visual Studio (which is the typical user environment), the `run_tests` / `get_tests` MCP tools work directly against Test Explorer. Use `Project=AzureBackup.Tests` as the filter.

### Benchmarks

```
dotnet build benchmarks/AzureBackup.Benchmarks/AzureBackup.Benchmarks.csproj -c Release
dotnet run -c Release --no-build --project benchmarks/AzureBackup.Benchmarks -- --filter "AzureBackup.Benchmarks.<ClassName>.*"
```

Each benchmark generates a synthetic workload on disk under the OS temp directory (path of the form `%TEMP%/azbk-backup-bench-<guid>/`) then runs the backup pipeline against `InMemoryBlobService`. The base class `BackupBenchmarkBase` cleans up stale `azbk-backup-bench-*` directories at the top of `[GlobalSetup]` so a previous run's leak (e.g. from a disk-full crash) does not poison the next run.

Results land under `BenchmarkDotNet.Artifacts/results/<BenchmarkClassName>-report-github.md` (markdown table) and `.csv` / `.html` siblings. Per-iteration peak working-set is emitted to the BDN console log under `BenchmarkDotNet.Artifacts/*.log` with the prefix `[peakWS workload=...]` because BDN's built-in `MemoryDiagnoser` reports cumulative allocated bytes, not peak resident.

Notes:
- BenchmarkDotNet 0.15.8's `-p Workload=<value>` filter is **silently ignored** — the full param matrix runs regardless. Plan benchmark cost as if every (param × iteration) combo will execute.
- Disk usage scales with workload size. Check the workload definitions in `BackupBenchmarkBase.cs` and lower the file-count or per-file-size caps locally before running on a constrained machine. The `realistic-large-200` workload is the largest of the standard set.
- Workloads are deterministic (`Random` seeded with `42`) so re-running on different hardware produces identical input bytes. **Absolute timings will vary with CPU/disk; relative deltas between two configurations should be directionally consistent across hardware** — that is the property the benchmarks are designed to measure.

---

## Open questions / things future sessions might ask

- "Why didn't largest-first work like theory said it would?" → see W2.
- "Should we change the production default to 16-way file concurrency?" → yes per the data, pending B26a.
- "Should we implement adaptive chunk concurrency for backup like restore has?" → defensible per the data; see B26b.
- "Can I re-run the benchmarks on this machine and compare?" → yes, results are under `BenchmarkDotNet.Artifacts/results/`. The committed numbers in each benchmark's xmldoc table are tied to whatever hardware ran them at commit time; a re-run on different hardware may show different absolute values but the deltas between configurations should be directionally consistent. Add a dated note in the maintenance log of this file if the deltas materially differ.
- "Why does the WPF project fail to build on this machine?" → it's Windows-only. Build `AzureBackup.Core` and `AzureBackup.Benchmarks` instead.
- "What does `MemoryLimitMB` map to in the UI?" → see `docs/USER_GUIDE.md`. Soft cap on in-flight chunk buffers; disabled by default.

---

## Recent commit history (for fast catch-up)

| Hash | Message |
|---|---|
| `171f07e` | B25-bench-2: design-decision benchmarks for backup parallelism plus AGENT_CONTEXT handoff |
| `614825a` | B25-bench: add BackupThroughputBenchmark for end-to-end backup pipeline |
| `e7a1ab1` | B24: discard restore diag on success and on cancellation |
| `31f467b` | B23: serialize all SqliteBackend reads against the shared connection; fix diag noise from successful and cancelled file backups |
| `5dfa40a` | B22: split SqliteBackend.cs into 11 partial files and gate diag logging on DIAGNOSTICLOG |
| `c0c33fc` | B21: add permanent Copilot instructions for single-line PowerShell and git commit policy |
| `0ecd72f` | B20: fix Check Now / Cancel / Re-check / Backfill buttons stuck at initial enabled state |
| `3b4a64f` | B19: stable per-file fileIndex in parallel backup progress; fix Active Files panel + small-file completion counts |
| `73bfc1f` | B18: harden SqliteBackend against close-during-write race; serialize Close via _writeLock |
| `c4b8b9d` | B17: fix unbounded List capacity OOM in CleanupStalePendingChanges |

Run `git log --oneline | Select-Object -First 20` for the latest. Earlier history (153 commits total as of this writing) includes a `C-`series and an unnumbered prefix.

---

## Maintenance log for THIS file

- **2026-04-24** — Initial creation. Captures B25-bench-2 in-progress state, all four benchmarks measured, two production seams added, working-tree dirty pending commit. Author: Copilot agent session (Claude Sonnet 4.5).
- **2026-04-24** — User pointed out that PC-specific data (CPU model, free disk, terminal-tool timeout, hardware-tied benchmark numbers, workstream status that referred to uncommitted edits already long-since committed as `171f07e`) misleads sessions on other machines. Removed all such data. Removed the entire stale "what remains in W1" subsection. Added explicit "never record machine-specific values" rule to the edit policy at the top. Added pointers to `docs/SETUP.md` and `docs/USER_GUIDE.md` in the project-at-a-glance section. Replaced the hardware-tied "Local environment quirks" section with a hardware-neutral "Running tests and benchmarks" section so a fresh agent on a fresh clone knows the entry-point commands. Added an explicit Windows-only note about the WPF project. Added architectural fact #8 covering the new B25-bench-2 override seam on `BackupOrchestrator`. Promoted B25-bench / B25-bench-2 into a "Completed workstreams" section. Renumbered the active W2 (LPT investigation) since the old W2/W3 collapsed. Updated the recent-commit-history table to include `171f07e`. Author: Copilot agent session.
- _(Add a dated bullet here every session that touches this file. One line per session, summarize what changed in the file.)_
