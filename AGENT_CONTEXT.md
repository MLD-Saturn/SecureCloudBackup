# AGENT_CONTEXT.md — Handoff for Copilot Sessions

This file is the **persistent memory** for Copilot agent sessions on the AzureBackup repo. It is intentionally checked into source control so the agent can read it on a fresh chat (on any machine) and immediately understand the project state, conventions, recent decisions, and open questions.

## How to use this file

**At the start of every fresh Copilot chat:** read this file before doing anything else. Treat the "Active workstreams" section as a continuation of the previous session.

**Throughout the session, UPDATE this file when ANY of the following happens:**

- A user preference, coding convention, or workflow rule is established or changed.
- A non-obvious production behavior is discovered (especially memory, concurrency, or persistence).
- A benchmark is run that produces a measurable design-relevant number.
- A workstream completes, blocks, or pivots.
- A commit lands that future sessions need to know about (e.g. a new B-numbered fix).
- A "do not do this" lesson is learned (e.g. a refactor that broke something).

**Edit policy:** keep entries dated (YYYY-MM-DD), keep the "Active workstreams" list small (move completed work to the "History" section), and keep the file under ~600 lines so a fresh agent can read it in one tool call.

**Anti-pattern:** do not duplicate code or full xmldoc into this file. Reference file paths and commit hashes instead.

---

## Project at a glance

- **Name:** AzureBackup
- **Language/runtime:** C# on .NET 10 (`net10.0` TFM, SDK 10.0.203)
- **Solution:** `azurebackup.sln` at repo root
- **Layout:**
  - `src/AzureBackup.Core/` — all backup/restore/encryption/database logic (the only library project)
  - `src/AzureBackup/` — WPF UI (MainWindowViewModel + views)
  - `tests/AzureBackup.Tests/` — xUnit, **759 tests, all passing as of B25-bench**
  - `benchmarks/AzureBackup.Benchmarks/` — BenchmarkDotNet 0.15.8 (Release-only, not in CI)
- **Database backend:** SQLCipher-encrypted SQLite (production default since C-5). Legacy LiteDB code path still present for migration.
- **Authentication:** Argon2id KDF (64 MB, 8 lanes, 3 iterations) for both database key derivation and the `EncryptionService` content key.
- **Encryption envelope:** AES-256-GCM with `[magic(4) | version(1) | nonce(12) | ciphertext(N) | tag(16) | crc32(4)]`. `EncryptionService.EncryptionOverhead = 37`.
- **Chunking:** content-defined (Rabin-style rolling hash, window=48, prime=31). Per-extension chunk-size config (`.mkv` uses 1-64 MB, `.txt` uses 16-128 KB, etc.). Files >500 MB use 16-128 MB chunks regardless of extension.

---

## Hard rules (USER PREFERENCES — never violate)

These come from `.github/copilot-instructions.md` and from explicit user instruction during session work. Reproduced here because they apply to every session.

### Terminal commands

- **Single physical line, always.** Chain with `;`. No here-strings, no backticks, no multi-line.
- The user's shell is **PowerShell 7+ (`pwsh.exe`)**.
- Length is not a concern — correctness and single-line execution are.
- An intermittent terminal-output corruption appears in some sessions: the leading "Set" of `Set-Location` is shown as garbage. **Ignore it** — the actual command runs (commit hash / output line still appears). This is a tooling display artifact, not a real failure.

### Git commits

- Commit immediately at logical stopping points.
- Use `git add .; git commit -m "..."` chained on one line, with multiple `-m` flags (one per paragraph). **Never** write the message to a file. **Never** use `git commit -F`.
- **No emoji, no non-ASCII** in commit messages.
- Escape `"` and `\` properly so the command parses cleanly.
- Commit message convention in this repo: `B<number>: <imperative summary>`. See `git log --oneline` for the running B-series. Current head: `614825a` = `B25-bench`.

### Tool usage

- Prefer `replace_string_in_file` / `multi_replace_string_in_file` over re-creating files. Re-create only when the file is small or being rewritten end-to-end.
- Read files before editing them.
- The `nuget_get-nuget-solver*` and `start_modernization` tools are NEVER appropriate for this project's typical work — do not invoke them unless the user asks for a package upgrade or .NET version change.
- Do not call `code_search` in parallel; do not call `run_command_in_terminal` in parallel.

---

## Architectural facts to load into working memory

These are non-obvious and have caused real bugs when forgotten.

1. **`SqliteBackend` is single-connection, single-threaded.** Post-B23 every read AND write goes through one `SQLiteConnection` serialized by a single `_writeLock`. Adding more file-level parallelism cannot help SqliteBackend throughput; it just adds contention.

2. **`FileOperationDiagnostics` (`.diag` files)** are written by 4 producers: `BackupOrchestrator.BackupFileAsync`, `RestoreService.RestoreFileAsync`, `RestoreService.Batch.cs` (CorruptedRecovery), `IntegrityCheckService.CheckOneFileAsync`. Post-B23 + B24, **all four only emit on real errors**. The `FlushAllLive` shutdown hook in `FileOperationDiagnostics` snapshots any diag still in the live `_live` registry at process exit — so the success/cancel paths in producers MUST call `Discard()` to deregister.

3. **`MemoryBudget` is a soft cap.** When `MemoryLimitEnabled=false` the budget is unlimited and the orchestrator can hold many GB of in-flight chunk buffers. Production telemetry (logs from 2026-04-23) showed an 11+ GB workingSet on a 7,313-file backup with `memoryBudget=unlimited`. The pipeline's per-file consumer count (default 6) × per-chunk size (up to 64 MB for media) × file-level concurrency (default 8) is the dominant memory cost.

4. **CPU is NOT the production bottleneck.** Same production session showed CPU at ~3% during the entire backup. The bottleneck is somewhere downstream (network, disk, or the SqliteBackend write lock). Any "make it more parallel" hypothesis must be measured rather than reasoned about.

5. **`InMemoryBlobService`** (`src/AzureBackup.Core/Services/InMemoryBlobService.cs`) is a production-quality fake of `IBlobStorageService`. Used by 10 test files and by all 4 backup benchmarks. Constructor takes optional `simulatedLatencyMs` and `failureRate`. Honors deduplication. Tracks `TotalBytesUploaded` / `TotalOperations`. **This is the single biggest enabler of network-free testing in the project.**

6. **`AZBK_USE_SQLITE=1` env var** forces the SQLite backend. Set by benchmark `[GlobalSetup]`. Read once at `LocalDatabaseService.Initialize`; flipping mid-process is a no-op.

7. **`BackupOrchestrator` constructor** signature: `(LocalDatabaseService, EncryptionService, ChunkingService, IBlobStorageService, FileWatcherService)`. The `FileWatcherService` is required even when no folders are watched — it can be constructed without ever being started.

---

## Active workstreams (as of 2026-04-24, end of session)

### W1 (B25-bench): Backup pipeline benchmarks — IN PROGRESS but suite-complete

Five files now exist under `benchmarks/AzureBackup.Benchmarks/`:

| File | Purpose | Status |
|---|---|---|
| `BackupBenchmarkBase.cs` | Shared harness (workload generation, peak-WS capture, service teardown) | Done |
| `BackupThroughputBenchmark.cs` | Baseline / regression detector | Run + numbers in xmldoc |
| `LargestFirstSchedulingBenchmark.cs` | Tests LPT (sort by size DESC) | Run + numbers in xmldoc |
| `TwoTierFileSplitBenchmark.cs` | Sweeps file concurrency 8/16/32 (uses `MaxParallelFileBackupsOverride`) | Run + numbers in xmldoc |
| `AdaptiveChunkConcurrencyBenchmark.cs` | Sweeps per-file chunk concurrency 4/6/12 (uses `MaxParallelChunkUploadsOverride`) | Run + numbers in xmldoc |

**Production code seam added** in `BackupOrchestrator.cs`: two optional `int? *Override` properties (`MaxParallelChunkUploadsOverride`, `MaxParallelFileBackupsOverride`) plus internal `EffectiveMax*` getters that fall back to the production constants. **All call sites switched to the effective getter** (verified via build + 759/759 test pass). Strictly additive — production behavior unchanged when overrides are unset.

**Headline measured findings (i7-9700K, 8 cores, no simulated latency):**

- **A — Largest-first sort**: -16% to -25% on `large-skew` workloads BUT **+15% to +17% REGRESSION** on `mixed-realistic-1000` and `realistic-large-200`. **Do not ship a blanket sort.** Hypothesis for the regression: input-order interleaves large+small so steady-state pipelining is preserved; LPT front-loads the long tasks and leaves 7 of 8 workers idle once the small files finish.
- **B — File concurrency 16-way**: Pareto improvement over 8-way. Biggest win is `uniform-1MB-1000` at -18%, no regressions, +0.6% peak workingSet on average. **Recommend changing the production default from 8 to 16.**
- **B — File concurrency 32-way**: workload-dependent. Wins on small-file workloads, regresses `large-skew-100` by +10%. Not a unilateral win.
- **C — Chunk concurrency**: production default of 6 is close to optimal. Workload-specific wins exist (4-way for `mixed-realistic-1000` at -9%, 12-way for `realistic-large-200` at -10%) but require true adaptive logic mirroring `RestoreService.ComputeAdaptiveChunkConcurrency` (~20 LOC). **Recommend either status quo OR implementing per-file adaptive concurrency** based on `config.MaxChunkSize`.

**Memory observations validated user concern**: `realistic-large-200` hit **11,194 MB process peak WS** at 8-way file concurrency. Scaling to 32-way only added ~5%. The dominant memory cost is per-file × per-chunk, not file-worker count. ArrayPool reuse + MemoryBudget keep allocations bounded.

**What remains in W1 before this is fully landed:**

1. **Working-tree state on this PC has uncommitted edits**: see `git status` — modifications to `BackupOrchestrator.cs`, `BackupOrchestrator.Operations.cs`, `BackupThroughputBenchmark.cs`, plus 4 NEW files (the 3 design benchmarks + the base class), plus benchmark result artifacts. **This needs to be committed as B25-bench-2 before continuing.** Suggested commit message structure (multi `-m`):
   - Title: `B25-bench-2: design-decision benchmarks for backup parallelism + LPT scheduling`
   - Para 1: motivation (measure A/B/C from previous discussion)
   - Para 2: production seam (Override properties, additive, defaults preserve behavior)
   - Para 3: headline findings (numbers above)
   - Para 4: build + 759/759 tests pass

2. **Unrelated edit in `src/AzureBackup.Core/Services/Backends/SqliteBackend.ChunkFileRefs.cs`**: 3 lines changed by an editor auto-fix (added `?.` operators on `_connection` access at line 365-378). The connection is null-checked upstream so this is unnecessary defensive code. **Decide whether to revert** before committing the benchmarks. Diff:
   ```
   -            using (var countCmd = _connection.CreateCommand())
   +            using (var countCmd = _connection?.CreateCommand())
   -                countCmd.CommandText = "..."
   +                countCmd?.CommandText = "..."
   -                total = Convert.ToInt32(countCmd.ExecuteScalar());
   +                total = Convert.ToInt32(countCmd?.ExecuteScalar());
   ```
   Recommendation: revert. The `?.CommandText =` pattern is a no-op assignment to a discarded value — it doesn't even compile-warn but it's nonsensical.

3. **Unrelated edit in `src/AzureBackup.Core/Services/RestoreService.cs`** also showed in `git status`. Likely the B24 commit (which IS in `git log` already as `e7a1ab1`). Run `git diff src/AzureBackup.Core/Services/RestoreService.cs` to confirm — if it's just whitespace or doc-comment trivia, leave or revert; if it's the actual B24 fix already committed, the `M` is stale (run `git update-index --really-refresh`).

### W2 (potential B26): act on the W1 findings

Three follow-up PRs are now defensible based on measured data:

- **B26a (cheap)**: change `MaxParallelFileBackups` from 8 to 16. One line. Re-run `BackupThroughputBenchmark` and `TwoTierFileSplitBenchmark` after to confirm no regression. Document the choice with a comment citing the benchmark file.
- **B26b (medium)**: implement per-file adaptive chunk concurrency in `BackupOrchestrator.BackupFileAsync` that reads the chunker's per-file `MaxChunkSize` and scales inversely. Mirror `RestoreService.ComputeAdaptiveChunkConcurrency` (file `RestoreService.cs` line 98). Re-run `AdaptiveChunkConcurrencyBenchmark` to confirm both wins survive.
- **B26c (DO NOT DO)**: blanket largest-first sort. The benchmark proved this regresses the typical workload.

### W3 (possible future): investigate the LPT regression

The +17% regression of `mixed-realistic-1000` under LPT was unexpected. The most likely root cause is the SqliteBackend write lock losing steady-state pipelining when all small files finish in a burst. **Worth investigating only if** someone wants to ship a workload-aware scheduler. Otherwise, the answer is "leave it alone and document why."

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

## Local environment quirks

- **Disk**: dev machine reports ~24-45 GB free on `C:`. Benchmarks honor this — `realistic-large` profile capped at 100 MB max file size to fit. If a future workload-large benchmark is added, check `Get-PSDrive C` first.
- **CPU**: i7-9700K, 8 logical / 8 physical cores. Coffee Lake. **Not** 32 cores (an earlier draft of this file got that wrong). Significant for interpreting parallelism benchmarks: 16-way and 32-way file concurrency are 2x and 4x oversubscribed.
- **Tool timeout for `run_command_in_terminal`**: ~10 minutes. Long benchmarks must either run within that window or be split. The workaround used in W1 was to set `--warmupCount 0 --iterationCount 1` to fit a 24-row matrix in ~12 minutes (sometimes succeeds, sometimes times out — the artifacts are written even on tool-side timeout because BDN runs the child processes independently).
- **BenchmarkDotNet `-p Workload=...` filter is ignored on this version (0.15.8)**. Don't rely on it to subset a run; the full param matrix executes regardless. Plan benchmark cost as if every param combo will run.

---

## Open questions / things future sessions might ask

- "Why didn't largest-first work like theory said it would?" → see W3 above.
- "Should we change the production default to 16-way file concurrency?" → yes per the data, but pending the B26a commit.
- "Should we implement adaptive chunk concurrency for backup like restore has?" → defensible per the data; see B26b. ~20 LOC plus a re-benchmark.
- "The user asked for benchmark results on different hardware" → workloads are deterministic (Random seed 42); results scale with CPU but the deltas should be directionally consistent. If re-running on different hardware, capture the new numbers in the per-benchmark xmldoc tables and date-stamp the row.
- "Why is there an 11 GB peak workingSet?" → see architectural fact #3 above. Answer: `MemoryBudget` is soft / disabled by default and per-file × per-chunk × file-worker dominates.

---

## Recent commit history (for fast catch-up)

| Hash | Message |
|---|---|
| `614825a` | B25-bench: add BackupThroughputBenchmark for end-to-end backup pipeline |
| `e7a1ab1` | B24: discard restore diag on success and on cancellation |
| `31f467b` | B23: serialize all SqliteBackend reads against the shared connection; fix diag noise from successful and cancelled file backups |
| `5dfa40a` | B22: split SqliteBackend.cs into 11 partial files and gate diag logging on DIAGNOSTICLOG |
| `c0c33fc` | B21: add permanent Copilot instructions for single-line PowerShell and git commit policy |
| `0ecd72f` | B20: fix Check Now / Cancel / Re-check / Backfill buttons stuck at initial enabled state |
| `3b4a64f` | B19: stable per-file fileIndex in parallel backup progress; fix Active Files panel + small-file completion counts |
| `73bfc1f` | B18: harden SqliteBackend against close-during-write race; serialize Close via _writeLock |
| `c4b8b9d` | B17: fix unbounded List capacity OOM in CleanupStalePendingChanges |

`git log --oneline` for full history. Series numbering goes back to at least B9.

---

## Maintenance log for THIS file

- **2026-04-24** — Initial creation. Captures B25-bench-2 in-progress state, all four benchmarks measured, two production seams added, working-tree dirty pending commit. Author: Copilot agent session (Claude Sonnet 4.5).
- _(Add a dated bullet here every session that touches this file. One line per session, summarize what changed in the file.)_
