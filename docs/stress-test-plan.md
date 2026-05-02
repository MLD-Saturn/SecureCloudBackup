# Stress Test Plan & Analysis Prompts

## Goal

Generate throughput metrics and corruption diagnostics data that can be fed back
to Copilot for performance tuning and root-cause analysis of CRC / integrity
failures.

---

## Architecture Notes for Test Design

Operations now share common core methods:

| Operation | Backup Core | Restore Core |
|---|---|---|
| **Backup Selected Files** | `BackupFilesCoreAsync` — 8-way file parallelism, shared `MemoryBudget` | N/A |
| **Restore Selected Files** | N/A | `RestoreFilesBatchCoreAsync` — two-tier parallelism (32× small ≤16 MB, 16× large), size-descending scheduling |
| **Mirror to Azure** | `BackupFilesCoreAsync` (same core as backup) | N/A |
| **Mirror to Local** | N/A | `RestoreFilesBatchCoreAsync` (same core as restore) — preceded by parallel classification phase |
| **Delete from Azure** | N/A | N/A — 128-way parallel blob deletion |

Key concurrency constants:
- `MaxParallelFileBackups = 8` — concurrent files during backup
- `MaxParallelChunkUploads = 6` — concurrent chunk uploads per file
- `MaxParallelFileRestores = 16` — concurrent large file restores
- `MaxParallelSmallFiles = 32` — concurrent small file (≤16 MB) restores
- `DefaultParallelChunkDownloads = 12` — base chunk concurrency per file (adaptive: 4–24)
- Channel buffer = `effectiveConcurrency × 4` — decouple network from disk I/O

When `MemoryBudget` is **unlimited** (disabled in settings):
- `AcquireAsync` is a no-op — zero overhead
- `StallCount` stays at 0
- Concurrency is bounded only by the constants above
- Peak memory ≈ `MaxParallelFileRestores × effectiveConcurrency × 2 × maxChunkSize`

---

## Files Produced by a Test Run

| File | Location | Content |
|---|---|---|
| `throughput-{date}.jsonl` | `{DataDirectory}/metrics/` | Per-file and operation-level throughput, context, and corruption records |
| `*.diag` | `{DataDirectory}/diagnostics/` | Human-readable per-file diagnostic logs (written on error or corrupted recovery) |
| `*.diag.jsonl` | `{DataDirectory}/diagnostics/` | Machine-readable chunk-level companion to each `.diag` file |
| `azurebackup-{date}.log` | `{DataDirectory}/` | Crash-safe application log with all diagnostic events |

### Record Types in `throughput-{date}.jsonl`

| Type | When Written | Key Fields |
|---|---|---|
| `ctx` | Start of backup/restore/mirror operations | `processors`, `total_ram_mb`, `memory_budget_mb`, `memory_budget_enabled`, `is_64_bit`, `os` |
| `file` (backup) | Each file that actually uploads | `chunks`, `chunk_min`, `chunk_max`, `new_chunks`, `dedup_chunks`, `tier`, `throughput_mbytes_per_sec` |
| `file` (restore) | Each file downloaded (single-chunk and multi-chunk) | `effective_concurrency`, `budget_stalls`, `retries`, `reorder_max`, `throughput_mbytes_per_sec` |
| `op` | End of each batch operation | `files`, `succeeded`, `failed`, `bytes`, `elapsed_seconds`, `throughput_mbytes_per_sec`, `file_concurrency`, `memory_budget_mb`, `budget_stalls` |
| `corruption` | Each corrupted recovery event | `total_chunks`, `recovered_chunks`, `unrecoverable_chunks`, `trigger_error`, `diag_file` |

> **Throughput field rename (B43).** Prior to B43 the JSON key was
> `throughput_mbps`. The label was wrong: the field has always been computed
> as `bytes / elapsed_seconds / 1048576`, i.e. **megabytes per second** (MB/s),
> not megabits per second (Mbps). The on-disk key is now
> `throughput_mbytes_per_sec` and the C# property is `ThroughputMBps`. Any
> historical JSONL files written before B43 contain the old `throughput_mbps`
> key but the same MB/s values; no value conversion is needed for trend
> analysis, only a key rename in the consumer.

### Fields in `.diag.jsonl` (per-chunk records)

Each line is a `ChunkDiagRecord`:
```json
{"phase":"BestEffortOK","index":3,"hash":"A1B2C3...","plain_size":4194304,"encrypted_size":4194341,"crc_valid":false,"extra":"aesGcm=OK"}
```

Fields: `phase`, `index` (-1 if blob-level only), `hash`, `plain_size`, `encrypted_size`, `crc_valid`, `extra`.

### Deriving Skipped File Counts

The `op` record's `succeeded` count includes both uploaded and skipped (unchanged)
files. To get the actual upload count:

```
uploaded_files = count of "file" records with matching operation in the same JSONL
skipped_files = op.succeeded - uploaded_files
```

---

## Stress Test Scenarios

### Scenario 1: Large-Scale Backup (Throughput Baseline)

**Purpose:** Establish throughput baseline and validate CDC chunk distribution.

1. Configure a watched folder with 100+ files of mixed sizes:
   - 50 files under 1 MB (text, config, small images)
   - 30 files between 1–50 MB (documents, source archives)
   - 15 files between 50–500 MB (videos, databases, disk images)
   - 5 files over 500 MB (ISOs, VM images)
2. Set memory budget to 2 GB (moderate).
3. Run **Backup Selected Local Files** on all files.
4. Wait for completion.
5. Collect `throughput-{date}.jsonl`.

**What to look for:**
- `ctx` record shows system specs and `memory_budget_mb: 2048`.
- Each `file` record shows `chunks`, `chunk_min`, `chunk_max` — uneven
  distribution means CDC boundaries may need tuning.
- `dedup_chunks > 0` on first backup means chunk hash collisions (unexpected).
- `throughput_mbytes_per_sec` variance across file sizes reveals bottleneck boundaries.
- Compare count of `file` records vs `op.succeeded` to see how many files were
  skipped as unchanged.

---

### Scenario 2: Full Restore Under Memory Pressure

**Purpose:** Stress the bounded parallel download pipeline and memory budget.

1. With 100+ files already backed up from Scenario 1.
2. Set memory budget to **512 MB** (tight — forces budget stalls).
3. Select all files in Azure panel.
4. Restore to a new empty directory.
5. Collect `throughput-{date}.jsonl`.

**What to look for:**
- `ctx` record shows `memory_budget_mb: 512`, `memory_budget_enabled: true`.
- `budget_stalls > 0` on per-file records confirms the budget is actually
  throttling. The `op` record also has an aggregate `budget_stalls` from
  `MemoryBudget.StallCount` (exact — counted at the slow-path entry point
  in `AcquireAsync`).
- `reorder_max` close to `effective_concurrency × 4` means the channel was
  near capacity.
- `effective_concurrency` values — do they match expectations from
  `ComputeAdaptiveChunkConcurrency`? (Formula: `384 MB / maxChunkSize`,
  clamped to 4–24.)
- Compare restore `throughput_mbytes_per_sec` against backup — restore should be faster
  (no CDC, no encryption) unless memory budget is the bottleneck.

---

### Scenario 3: Full Restore with Unlimited Memory Budget

**Purpose:** Establish the maximum restore throughput ceiling and confirm
zero stalls when the budget is disabled.

1. With 100+ files already backed up from Scenario 1.
2. **Disable** memory budget in Settings (uncheck "Enable Memory Limit").
3. Select all files in Azure panel.
4. Restore to a new empty directory.
5. Collect `throughput-{date}.jsonl`.

**What to look for:**
- `ctx` record shows `memory_budget_enabled: false`, `memory_budget_mb: 0`.
- **Every** per-file `budget_stalls` must be `0` — if any are non-zero, the
  unlimited code path has a bug.
- `op.budget_stalls` must also be `0`.
- `effective_concurrency` should be at maximum for small chunks (24) and
  moderate for large chunks (4–8) — the adaptive formula is independent of
  memory budget.
- `throughput_mbytes_per_sec` should be the highest of all restore scenarios — this is
  the ceiling.
- Monitor system memory via Task Manager during the run. Peak memory should be
  roughly `MaxParallelFileRestores × effectiveConcurrency × 2 × maxChunkSize`.
  For 16 files × 12 concurrent × 2 × 4 MB chunks = ~1.5 GB.
  For files with 32 MB chunks, peak could reach ~8 GB.

---

### Scenario 4: Corruption Recovery Stress Test

**Purpose:** Trigger and diagnose CRC / integrity failures at scale.

1. Backup 50+ files (include large multi-chunk files).
2. Restore all files — confirm zero corruption baseline.
3. **Modify some files locally** (append bytes, rename, change content) and
   re-backup to create second-generation chunks.
4. Restore all files again.
5. If corruption occurs: collect all `.diag`, `.diag.jsonl`, and
   `throughput-{date}.jsonl` files.
6. If no corruption: increase concurrency pressure:
   - Set memory budget to 512 MB.
   - Backup and restore 200+ files simultaneously.
   - Run backup and restore operations in rapid succession (backup, immediately
     restore, immediately re-backup modified versions).
7. Repeat step 6 with unlimited memory budget to determine if corruption
   correlates with memory pressure.

**What to look for in corruption events:**
- `corruption` records in `throughput-{date}.jsonl` — `recovered_chunks` vs
  `unrecoverable_chunks` ratio.
- `.diag.jsonl` files — per-chunk `phase` and `crc_valid` fields identify
  exactly which chunks fail and at which stage.
- Pattern: if all unrecoverable chunks have `crc_valid: false` but `extra`
  contains `aesGcm=OK`, the corruption is in the CRC envelope only.
- Pattern: if `extra` contains `aesGcm=FAIL`, the ciphertext or tag was
  altered — points to a blob storage or memory corruption issue.
- Compare corruption rates between 512 MB budget vs unlimited — if corruption
  only occurs under memory pressure, it suggests a buffer reuse or
  use-after-return bug in the `ArrayPool` rental paths.

---

### Scenario 5: Deduplication Effectiveness

**Purpose:** Validate CDC dedup across modified files.

1. Backup a folder with 20 large files (>100 MB each).
2. Modify 5 of the files (append 1 KB to the end of each).
3. Re-backup the folder.
4. Collect `throughput-{date}.jsonl`.

**What to look for:**
- Modified files should show `dedup_chunks` close to total `chunks` — most
  chunks should be reused.
- `new_chunks` should be 1–2 per modified file (the chunk at the edit point
  and possibly the final chunk).
- If `new_chunks` is high relative to file size, CDC boundaries are not stable
  under small edits.

---

### Scenario 6: Mirror Operation Parity Verification

**Purpose:** Verify that mirror operations now use the same code paths as
standalone backup/restore and achieve comparable throughput.

1. Backup 50+ files using **Backup Selected Local Files**.
2. Note the `op` record's `throughput_mbytes_per_sec` and `file_concurrency`.
3. Delete the backup from the database (reset).
4. Mirror the same folder to Azure using **Mirror Sync to Azure**.
5. Compare the `op` records side by side.

**What to verify:**
- `mirror-to-azure` op record shows `file_concurrency` matching
  `MaxParallelFileBackups` (8) — not 1 (the old sequential behavior).
- `mirror-to-azure` op has a `ctx` record with `memory_budget_mb` > 0
  (confirming memory budget is active).
- Throughput should be comparable (±20%) to the standalone backup.
- Repeat for restore: compare `mirror` op (Mirror Sync to Local) vs
  `restore` op (Restore Selected Files) throughput for the same file set.
  Both should show two-tier parallelism in the logs.

---

### Scenario 7: Unlimited Budget Backup + Mirror Comparison

**Purpose:** Confirm that unlimited memory budget produces zero stalls
during backup and mirror operations.

1. **Disable** memory budget in Settings.
2. Backup 100+ files via **Backup Selected Local Files**.
3. Mirror the same folder to Azure via **Mirror Sync to Azure**.
4. Collect both `op` records.

**What to verify:**
- Neither `op` record should have `budget_stalls > 0`.
- No `ctx` record should show `memory_budget_enabled: true`.
- If stalls appear with unlimited budget, there is a bug in the
  `MemoryBudget.IsUnlimited` fast-path or the budget is being
  created incorrectly from config.

---

## Analysis Prompts

After running the scenarios above, use these prompts with Copilot. Provide the
referenced files alongside each prompt.

### Prompt 1: Throughput Analysis

> Here is the throughput metrics JSONL from a backup of {N} files ({X} GB total)
> and a subsequent restore of the same set. Analyze the pipeline efficiency and
> answer:
>
> 1. Are there files where throughput is significantly below the operation
>    average? What file sizes correlate with low throughput?
> 2. Is the adaptive chunk concurrency formula choosing appropriate values? Show
>    the `effective_concurrency` vs `chunk_max` relationship.
> 3. How many budget stalls occurred? Is the memory budget too tight for the
>    workload? Compare the per-file `budget_stalls` against the `op`-level
>    aggregate `budget_stalls` to verify consistency.
> 4. What concrete changes to `MaxParallelFileBackups`,
>    `MaxParallelFileRestores`, `DefaultParallelChunkDownloads`, or the channel
>    buffer multiplier would improve throughput based on this data?
> 5. How many files were skipped as unchanged? (Derive from
>    `op.succeeded - count(file records)`.)
>
> **Provide:** `throughput-{date}.jsonl`
>
> **Code references (already accessible):**
> - `src/AzureBackup.Core/Services/RestoreService.cs` — `ComputeAdaptiveChunkConcurrency`, `DefaultParallelChunkDownloads`, channel capacity
> - `src/AzureBackup.Core/Services/BackupOrchestrator.cs` — `MaxParallelChunkUploads`, `MaxParallelFileBackups`
> - `src/AzureBackup.Core/Services/BackupOrchestrator.Operations.cs` — `BackupFilesCoreAsync` (shared by backup and mirror-to-azure)
> - `src/AzureBackup.Core/Services/RestoreService.Batch.cs` — `RestoreFilesBatchCoreAsync` (shared by restore and mirror-to-local)
> - `src/AzureBackup.Core/Services/MemoryBudget.cs` — acquire/release logic, `StallCount`
> - `src/AzureBackup.Core/Services/ChunkingService.cs` — CDC min/avg/max chunk size parameters

### Prompt 2: Corruption Root-Cause Analysis

> Here are the corruption diagnostics from a restore operation that triggered
> {N} corrupted recovery events. Analyze the data and determine:
>
> 1. Is the corruption in the CRC envelope only, or is AES-GCM decryption also
>    failing? Check the `.diag.jsonl` records for `crc_valid` and `extra` fields.
> 2. Is there a pattern in which chunks fail? (always the last chunk? always
>    chunks above a certain size? always the same hash across different files?)
> 3. Does the corruption correlate with high memory pressure? (Check `ctx`
>    record's `memory_budget_mb` and `memory_budget_enabled`, and the
>    `budget_stalls` on both the affected per-file records and the `op` record.)
> 4. Could this be a race condition in the producer-consumer pipeline? (Check
>    `reorder_max` and `effective_concurrency` on affected files.)
> 5. Did the corruption occur with memory budget enabled or unlimited? If only
>    with budget enabled, it suggests a stall-related buffer reuse issue.
> 6. What is the most likely root cause and what code changes would fix it?
>
> **Provide:**
> - `throughput-{date}.jsonl` (contains `corruption`, `ctx`, and `op` records)
> - All `.diag.jsonl` files from `{DataDirectory}/diagnostics/`
> - Optionally: the corresponding `.diag` files for human-readable context
>
> **Code references (already accessible):**
> - `src/AzureBackup.Core/Services/EncryptionService.cs` — `Encrypt`, `DecryptInto`, `DecryptBestEffort`, `ValidateCrc`, envelope format
> - `src/AzureBackup.Core/Services/AzureBlobService.Download.cs` — `DownloadChunkStreamingAsync`, `DownloadChunkBestEffortAsync`
> - `src/AzureBackup.Core/Services/RestoreService.cs` — `RestoreWithBoundedParallelDownloadsAsync`, `VerifyChunkIntegrity`
> - `src/AzureBackup.Core/Services/RestoreService.Batch.cs` — `RestoreFilesBatchCoreAsync`, `AttemptCorruptedRecoveryAsync`
> - `src/AzureBackup.Core/Services/MemoryBudget.cs` — `StallCount`, acquire slow-path

### Prompt 3: CDC Deduplication Tuning

> Here is the throughput metrics JSONL from a backup-modify-rebackup cycle.
> Analyze deduplication effectiveness:
>
> 1. For modified files, what is the ratio of `new_chunks` to total `chunks`?
> 2. Are CDC chunk sizes well-distributed? Show `chunk_min` / `chunk_max`
>    statistics across all files.
> 3. Would changing the CDC target chunk size improve dedup for this workload?
> 4. Are there files where dedup is unexpectedly poor (high `new_chunks` despite
>    small modifications)?
>
> **Provide:** `throughput-{date}.jsonl`
>
> **Code references (already accessible):**
> - `src/AzureBackup.Core/Services/ChunkingService.cs` — CDC parameters, `ChunkAndStreamChangedAsync`

### Prompt 4: Memory Budget Tuning

> Here is the throughput metrics JSONL from three restore runs of the same file
> set: one with 512 MB memory budget, one with 4 GB, and one with unlimited
> (budget disabled). Compare:
>
> 1. How does `budget_stalls` differ between the runs? The unlimited run must
>    show exactly 0 stalls — if not, explain the bug.
> 2. Is there a throughput difference? Quantify the MB/s impact of the tighter
>    budget vs the unlimited ceiling.
> 3. What is the minimum memory budget that avoids stalls for this workload?
>    (Look for the budget size where `budget_stalls` drops to 0.)
> 4. Should the adaptive concurrency formula account for memory budget size?
>    Currently it targets ~384 MB in-flight regardless of budget.
> 5. For the unlimited run, what was the peak system memory usage implied by
>    `effective_concurrency × 2 × chunk_max` across concurrent files?
>
> **Provide:** All three `throughput-{date}.jsonl` files (labelled by budget
> setting: 512 MB / 4 GB / unlimited)
>
> **Code references (already accessible):**
> - `src/AzureBackup.Core/Services/MemoryBudget.cs` — `StallCount`, `IsUnlimited`, acquire/release
> - `src/AzureBackup.Core/Services/RestoreService.cs` — `ComputeAdaptiveChunkConcurrency`
> - `src/AzureBackup.Core/Services/RestoreService.Batch.cs` — `RestoreFilesBatchCoreAsync`

### Prompt 5: Mirror Parity Verification

> Here are throughput metrics JSONL files from four operations on the same file
> set: standalone backup, mirror-to-azure, standalone restore, and
> mirror-to-local. Verify that the mirror operations achieve the same
> performance as their standalone equivalents:
>
> 1. Does mirror-to-azure show `file_concurrency` matching
>    `MaxParallelFileBackups` (8)? Compare its `throughput_mbytes_per_sec` against the
>    standalone backup.
> 2. Does mirror-to-local show the two-tier parallelism in the log? Compare its
>    `throughput_mbytes_per_sec` against the standalone restore.
> 3. Do both mirror operations have `ctx` records with correct
>    `memory_budget_mb`?
> 4. Are corruption metrics recorded during mirror-to-local the same way as
>    during standalone restore? (Check for `corruption` records.)
>
> **Provide:** All four `throughput-{date}.jsonl` files (labelled: backup /
> mirror-to-azure / restore / mirror-to-local)
>
> **Code references (already accessible):**
> - `src/AzureBackup.Core/Services/BackupOrchestrator.Operations.cs` — `BackupFilesCoreAsync`, `MirrorSyncToAzureAsync`
> - `src/AzureBackup.Core/Services/RestoreService.Batch.cs` — `RestoreFilesBatchCoreAsync`, `MirrorSyncToLocalAsync`
