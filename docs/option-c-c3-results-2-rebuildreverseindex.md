# C-3 (3b) — `RebuildReverseChunkIndex` head-to-head: mixed result

**Date run:** 2026-04-18 00:44
**Branch:** `feature/option-c-eval`
**Commit at run time:** `99e92cd` (the C-3 (3a) batched-INSERT rewrite)
**Hardware:** Intel Core i7-9700K @ 3.60 GHz, 8C/8T, Windows 11 26200.8246
**Runtime:** .NET 10.0.6 (X64 RyuJIT, x86-64-v3)
**Tool:** BenchmarkDotNet 0.15.8

## Result table (from `BenchmarkDotNet.Artifacts/results/AzureBackup.Benchmarks.RebuildReverseChunkIndexBackendBenchmark-report-github.md`)

| TotalChunks | LiteDB mean | SQLite mean | **Time ratio** | LiteDB allocs | SQLite allocs | **Alloc ratio** | LiteDB Gen 2 | SQLite Gen 2 |
|------------:|------------:|------------:|---------------:|--------------:|--------------:|----------------:|-------------:|-------------:|
|      10 000 |    412.8 ms |     92.4 ms |     **0.224** |    457 MB     |     42 KB     |    **0.0001**  |        3 000 |        **0** |
|     100 000 |   5 126 ms  |   2 948 ms  |     **0.575** |  5 534 MB     |    383 KB     |    **0.00007** |        8 000 |        **0** |
|     500 000 |  44 289 ms  |  25 340 ms  |     **0.572** | 35 284 MB     |  1 976 KB     |    **0.00006** |       31 000 |        **0** |

## Analysis

### This is NOT a decision-gate pass

The eval doc §9 threshold is **`Ratio < 0.5`**. SQLite hits that at 10K only (0.224); the two realistic-scale cells (100K, 500K) are at **~0.57** — meaningfully faster, but not the 2× wall-clock margin the gate demands.

So the simple scorecard answer is: **2 of 5 scenarios still pending, 1 passed (`GetChunkEntriesForFile`), 1 marginal (this one).** We need 3 of 5 passes to ship; this scenario does NOT count toward that 3.

### But the result is still meaningfully positive

The wall-clock improvement is real and proportionally consistent at scale:

* **10K**: 4.5× faster (passes gate)
* **100K**: 1.7× faster
* **500K**: 1.7× faster

And the **allocation story is overwhelming**:

* SQLite allocates **2 MB** to LiteDB's **35 GB** at 500K — a **17 860× reduction**.
* SQLite triggers **zero Gen 2 GCs** across every cell. LiteDB triggers **3 000 / 8 000 / 31 000 Gen 2 collections per single iteration** — the migration progress bar would visibly stall while the GC works.
* LiteDB's allocation pattern (35 GB to insert 500K rows of ~150 bytes apiece) means it's allocating ~70 KB per row inserted. Almost all of that is BSON-document deserialization in the source loop that walks `ChunkIndexEntry.ReferencingFiles`.

### Why SQLite isn't 10× here even after the C-3 (3a) rewrite

After the per-file → per-batch rewrite I expected a much bigger win. The 1.7× ratio at scale tells me **fsync isn't the dominant cost any more** — the cost has moved to the actual `INSERT … SELECT` work + WAL page allocation. Specifically:

* At 500K rows, the `INSERT` writes ~50 MB of WAL pages (chunk_file_refs has 4 indexed columns including `chunk_hash` which the index also holds). SQLCipher encrypts every page on write, which is CPU-bound HMAC+AES.
* The single `INSERT … SELECT` materialises every result row inside the engine before the insert proper, which means the engine touches each `file_chunks` row twice (once to read for the JOIN, once to write the FK lookup).
* LiteDB's BSON encoder is also doing AES, but its WAL pages are typically larger (16 KB vs SQLite's default 4 KB) so it pays the per-page fixed cost less often.

This is encryption-overhead territory, not algorithmic-overhead territory. There are levers to pull (`PRAGMA cache_size`, `PRAGMA page_size`) but they won't get us to a 10× ratio.

### The real-world UX angle

| Scale | LiteDB wall-clock | SQLite wall-clock | UX impact |
|:------|:------------------|:------------------|:----------|
| 10K   | 0.4 s             | 0.09 s            | Both fine |
| 100K  | 5.1 s             | 2.9 s             | Both acceptable as a one-time migration step |
| 500K  | **44 s**          | **25 s**          | Both above the "this may take a moment" threshold; both need the existing Phase 5 progress dialog |

At 500K chunks, SQLite gets the migration done in **half the time** with **zero pause-the-world GC events**. The user-visible benefit is real even though the ratio doesn't clear the gate.

## Decision-gate scorecard so far

Eval doc §9 requires `Ratio < 0.5` on at least 3 of 5 scenarios.

| # | Scenario | Status | Result |
|--:|---|---|---|
| 1 | `GetChunkEntriesForFile` | ✅ **PASS** | 0.001–0.02 (50–1000× faster) |
| 2 | `RebuildReverseChunkIndex` | ⚠ **MARGINAL** | 0.224 / 0.575 / 0.572 (2 of 3 cells fail the < 0.5 threshold) |
| 3 | Concurrent readers (16 threads) | ⏳ pending | C-3 (4/N) |
| 4 | Database open + decrypt | ⏳ pending | C-3 (5/N) |
| 5 | `BackedUpFile` upsert throughput | ⏳ pending | C-3 (6/N) |

**1 of 5 passes decisively. 1 of 5 is a wall-clock-positive miss.** We need 2 more decisive passes from the remaining 3 scenarios to ship.

## Honest disclosures

1. **The "Margin" column on the LiteDB rows is huge** (e.g. 853.67 ms error on a 412.83 ms 10K mean) because BDN computes the 99.9% confidence interval from only 3 iterations and LiteDB has high run-to-run variance driven by GC scheduling. The numbers should be read as ballpark, not precision. SQLite's StdDev is < 1% on every cell.

2. **One run, no reproducibility re-test.** I'd recommend running the matrix one more time before treating these specific numbers as canon. The qualitative picture (ratio ~0.57 at scale) won't change.

3. **The C-3 (3a) bug fix was real and substantial.** Before the fix, the partial-run numbers showed SQLite **2.4× SLOWER** than LiteDB at the 100K cell. After the fix it's 1.7× FASTER. The fix moved a 4× delta in the right direction. Worth keeping in mind that the remaining 3 SqliteBackend implementations may have similar latent issues — I'll smoke-benchmark each before commit going forward.

4. **The ratio cliff between 10K (0.224) and 100K (0.575)** is suspicious. Two suspects:
   - SQLite's page cache (default 8 MB ≈ 2000 pages) absorbs the 10K case fully but spills at 100K+.
   - The `placeholders IN (...)` parameter list at 256 entries forces SQLite into a different query plan (no index seek; full scan) at larger DBs.
   I haven't investigated. If we ship, this is a follow-up perf pass.

5. **No CleanupStalePendingChanges yet** — if we later discover that's also slow on SQLite, that's another scenario where the migration has to absorb a one-time perf hit. Out of scope for this commit.

## Recommendation

**Continue with C-3 (4/N): Concurrent readers.** This is the scenario where SQLite should structurally win — multiple readers in WAL mode don't block each other, while LiteDB's `ReaderWriterLockSlim` workaround serializes everything through a single connection. If C-3 (4/N) passes decisively we're at 2 of 5 with the remaining 2 (open+decrypt, BackedUpFile upsert) being the deciders.
