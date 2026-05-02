using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace AzureBackup.Core.Services.Backends;

/// <summary>
/// B44: surface for verifying the on-disk SQLite/SQLCipher database file
/// itself (NOT the user-visible Data Integrity feature, which checks
/// backed-up files against Azure storage).
/// </summary>
internal sealed partial class SqliteBackend
{
    /// <summary>
    /// Runs <c>PRAGMA cipher_integrity_check</c> followed by
    /// <c>PRAGMA integrity_check</c> against the open connection and
    /// returns a structured result. See
    /// <see cref="DatabaseFileIntegrityResult"/> for the meaning of
    /// each pragma's output.
    /// </summary>
    public DatabaseFileIntegrityResult CheckDatabaseFileIntegrity()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        EmitDiag("CheckDatabaseFileIntegrity: enter");

        // Take the WRITE lock for the duration. Both pragmas can take
        // many seconds on a large database and they walk every page;
        // letting a concurrent writer run alongside would either
        // serialize behind us anyway or, worse, mutate the page
        // images mid-scan and produce a false positive.
        return InWriteLock(() =>
        {
            var cipherMessages = RunPragmaIntegrityCheck("cipher_integrity_check");
            var sqliteMessages = RunPragmaIntegrityCheck("integrity_check");
            var result = new DatabaseFileIntegrityResult(
                CipherIntegrityMessages: cipherMessages,
                SqliteIntegrityMessages: sqliteMessages);
            EmitDiag(
                $"CheckDatabaseFileIntegrity: complete (cipherOk={result.CipherOk}, sqliteOk={result.SqliteOk}, " +
                $"cipherRows={cipherMessages.Count}, sqliteRows={sqliteMessages.Count})");
            return result;
        });
    }

    private List<string> RunPragmaIntegrityCheck(string pragmaName)
    {
        var messages = new List<string>();
        using var cmd = _connection!.CreateCommand();
        // PRAGMA names cannot be parameterised; the only inputs are
        // hard-coded literals on the call site so the SQL string is
        // safe by construction.
        cmd.CommandText = $"PRAGMA {pragmaName};";
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                messages.Add(reader.GetString(0));
            }
        }
        catch (SqliteException ex)
        {
            // Pragmas can themselves fail when the corruption is bad
            // enough that the engine cannot even read the page that
            // would describe the result row. Surface this as a
            // synthetic failure message so the caller sees a clear
            // signal rather than an empty list that looks like "ok".
            messages.Add($"pragma {pragmaName} failed: SQLite Error {ex.SqliteErrorCode}: {ex.Message}");
        }
        return messages;
    }

    /// <summary>
    /// B45: attempts an in-place repair of the catalog file by running
    /// <c>REINDEX</c> against every index that <see cref="CheckDatabaseFileIntegrity"/>
    /// flagged as damaged in a way <c>REINDEX</c> can fix.
    ///
    /// <para>
    /// The repair is intentionally narrow: it only acts on a strict
    /// whitelist of <c>integrity_check</c> failure shapes that are known
    /// to be index-only damage (see
    /// <see cref="DatabaseRepairClassifier.TryClassify"/>). Anything else
    /// -- cipher-pragma failures, page-level damage, freelist damage,
    /// or messages the classifier doesn't recognise -- aborts the
    /// repair attempt and returns a result whose
    /// <see cref="DatabaseRepairResult.WasAttempted"/> is <c>false</c>
    /// with an explanation.
    /// </para>
    ///
    /// <para>
    /// <c>REINDEX</c> rewrites an index from the underlying table data.
    /// It cannot make the database worse: if the table itself is intact
    /// the resulting index is correct; if the table is also damaged
    /// the <c>REINDEX</c> fails cleanly with no partial write to the
    /// index page. Each <c>REINDEX</c> runs in its own implicit
    /// transaction so a failure on one index does not roll back
    /// successful repairs of others -- partial recovery is strictly
    /// better than no recovery, and the post-repair re-verification
    /// makes any remaining damage visible.
    /// </para>
    /// </summary>
    /// <param name="diagnosis">
    /// The result of a prior <see cref="CheckDatabaseFileIntegrity"/>
    /// call. The repair refuses to run on a stale or healthy diagnosis.
    /// </param>
    public DatabaseRepairResult ReindexCorruptIndexes(DatabaseFileIntegrityResult diagnosis)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        EmitDiag("ReindexCorruptIndexes: enter");

        // Refuse on cipher-level damage. REINDEX cannot help when the
        // page bytes themselves do not decrypt; attempting the repair
        // would just race the same bad pages.
        if (!diagnosis.CipherOk)
        {
            EmitDiag("ReindexCorruptIndexes: aborted - cipher pragma reported failures");
            return DatabaseRepairResult.NotAttempted(
                "Repair refused: PRAGMA cipher_integrity_check reported failures, " +
                "which means the database FILE BYTES are damaged on disk. " +
                "REINDEX cannot fix page-level ciphertext damage. Restore the " +
                "catalog from a backup, or rebuild the chunk index from Azure metadata.");
        }

        if (diagnosis.IsHealthy)
        {
            EmitDiag("ReindexCorruptIndexes: aborted - diagnosis reported healthy");
            return DatabaseRepairResult.NotAttempted(
                "Repair not needed: the supplied diagnosis reports the database is healthy.");
        }

        var classification = DatabaseRepairClassifier.TryClassify(diagnosis.SqliteIntegrityMessages);
        if (!classification.AllRepairable)
        {
            EmitDiag(
                "ReindexCorruptIndexes: aborted - one or more findings are outside the REINDEX-safe whitelist " +
                $"(unrepairableCount={classification.UnrepairableMessages.Count})");
            return DatabaseRepairResult.NotAttempted(
                "Repair refused: at least one integrity_check finding is outside " +
                "the set of damage shapes REINDEX can safely fix. The unrepairable " +
                "findings are:" + Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, classification.UnrepairableMessages));
        }

        if (classification.RepairableIndexNames.Count == 0)
        {
            EmitDiag("ReindexCorruptIndexes: aborted - classifier extracted zero index names");
            return DatabaseRepairResult.NotAttempted(
                "Repair refused: integrity_check reported failures but the classifier " +
                "could not extract any specific index names to repair. This indicates " +
                "an unfamiliar message format; please file a support request with the " +
                "verification report.");
        }

        return InWriteLock(() =>
        {
            // Validate every name against sqlite_master BEFORE issuing
            // the REINDEX so a poisoned message (e.g. an injected
            // index name) cannot push arbitrary SQL through the loop.
            // sqlite_master.name comes back parameterized; the name we
            // emit into the REINDEX statement is the value we just
            // confirmed exists as type='index'.
            var knownIndexNames = ReadAllIndexNames();
            var attempted = new List<string>();
            var succeeded = new List<string>();
            var failed = new List<string>();

            foreach (var name in classification.RepairableIndexNames)
            {
                if (!knownIndexNames.Contains(name))
                {
                    failed.Add($"{name}: not present in sqlite_master (skipped, not reindexed)");
                    continue;
                }

                attempted.Add(name);
                try
                {
                    using var cmd = _connection!.CreateCommand();
                    // Identifier quoting via double-quotes is the SQLite
                    // standard for identifiers; the embedded "" escape
                    // is defence-in-depth even though we already
                    // confirmed the name came from sqlite_master.
                    cmd.CommandText = $"REINDEX \"{name.Replace("\"", "\"\"")}\";";
                    cmd.ExecuteNonQuery();
                    succeeded.Add(name);
                    EmitDiag($"ReindexCorruptIndexes: REINDEX {name} succeeded");
                }
                catch (SqliteException ex)
                {
                    failed.Add($"{name}: SQLite Error {ex.SqliteErrorCode}: {ex.Message}");
                    EmitDiag($"ReindexCorruptIndexes: REINDEX {name} failed: {ex.Message}");
                }
            }

            // Re-run BOTH pragmas inside the same write lock so the
            // before/after comparison is taken without any other
            // writer in between. Calling the public method would
            // re-enter the (non-recursive) write lock and deadlock,
            // so run the helpers directly here.
            var postCipher = RunPragmaIntegrityCheck("cipher_integrity_check");
            var postSqlite = RunPragmaIntegrityCheck("integrity_check");
            var postDiagnosis = new DatabaseFileIntegrityResult(postCipher, postSqlite);

            EmitDiag(
                "ReindexCorruptIndexes: complete " +
                $"(attempted={attempted.Count}, succeeded={succeeded.Count}, failed={failed.Count}, " +
                $"postIsHealthy={postDiagnosis.IsHealthy})");

            return DatabaseRepairResult.Attempted(
                attemptedIndexes: attempted,
                succeededIndexes: succeeded,
                failedIndexes: failed,
                postRepairDiagnosis: postDiagnosis);
        });
    }

    private HashSet<string> ReadAllIndexNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = _connection!.CreateCommand();
        // Both real and auto-indexes (sqlite_autoindex_*) appear in
        // sqlite_master with type='index'; both are valid REINDEX
        // targets. NULL names (extremely rare; partial-write) are
        // skipped so we never try to quote a null.
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name IS NOT NULL;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            names.Add(reader.GetString(0));
        }
        return names;
    }
}

/// <summary>
/// B45: classifies <c>PRAGMA integrity_check</c> output rows into
/// "REINDEX can fix this" vs "REINDEX cannot fix this".
///
/// <para>
/// The whitelist is deliberately narrow. Each pattern below corresponds
/// to a documented SQLite integrity_check message shape that names a
/// specific index and describes index-only damage. Page-level damage,
/// freelist damage, table-row damage, and any unfamiliar message all
/// route to the unrepairable bucket so a future change to SQLite's
/// message text cannot silently widen the repair surface.
/// </para>
/// </summary>
public static class DatabaseRepairClassifier
{
    // "wrong # of entries in index ix_name"
    private static readonly Regex WrongEntryCountRegex = new(
        @"^wrong\s+#\s+of\s+entries\s+in\s+index\s+(?<name>\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "row N missing from index ix_name"
    private static readonly Regex RowMissingFromIndexRegex = new(
        @"^row\s+\d+\s+missing\s+from\s+index\s+(?<name>\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "non-unique entry in index ix_name"
    private static readonly Regex NonUniqueEntryRegex = new(
        @"^non-unique\s+entry\s+in\s+index\s+(?<name>\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Categorises every line of an integrity_check result. A line is
    /// "repairable" only if it matches one of the known index-only
    /// damage patterns. The literal <c>"ok"</c> row is treated as
    /// neither repairable nor unrepairable (it just means the rest of
    /// the structure is fine).
    /// </summary>
    public static DatabaseRepairClassification TryClassify(IReadOnlyList<string> integrityCheckMessages)
    {
        ArgumentNullException.ThrowIfNull(integrityCheckMessages);

        var repairableIndexes = new List<string>();
        var unrepairable = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in integrityCheckMessages)
        {
            var line = raw?.Trim() ?? string.Empty;
            if (line.Length == 0) continue;

            // The literal "ok" row from a healthy DB is informational,
            // not a finding. (It also means we should never have been
            // called, but be tolerant.)
            if (string.Equals(line, "ok", StringComparison.Ordinal))
                continue;

            var match = WrongEntryCountRegex.Match(line);
            if (!match.Success) match = RowMissingFromIndexRegex.Match(line);
            if (!match.Success) match = NonUniqueEntryRegex.Match(line);

            if (match.Success)
            {
                var name = match.Groups["name"].Value;
                if (seen.Add(name))
                    repairableIndexes.Add(name);
            }
            else
            {
                unrepairable.Add(line);
            }
        }

        return new DatabaseRepairClassification(
            RepairableIndexNames: repairableIndexes,
            UnrepairableMessages: unrepairable);
    }
}

/// <summary>
/// Result of <see cref="DatabaseRepairClassifier.TryClassify"/>.
/// </summary>
/// <param name="RepairableIndexNames">
/// Distinct index names extracted from index-only-damage messages.
/// </param>
/// <param name="UnrepairableMessages">
/// Verbatim integrity_check rows that the classifier did NOT recognise
/// as REINDEX-safe. Non-empty means the repair must be refused.
/// </param>
public sealed record DatabaseRepairClassification(
    IReadOnlyList<string> RepairableIndexNames,
    IReadOnlyList<string> UnrepairableMessages)
{
    public bool AllRepairable => UnrepairableMessages.Count == 0;
}

/// <summary>
/// Result of <see cref="SqliteBackend.ReindexCorruptIndexes"/>.
/// </summary>
public sealed record DatabaseRepairResult
{
    /// <summary>
    /// <c>false</c> when the repair was refused before any REINDEX ran
    /// (e.g. cipher damage, unrepairable findings, healthy diagnosis).
    /// In that case <see cref="RefusalReason"/> explains why.
    /// </summary>
    public bool WasAttempted { get; init; }

    /// <summary>
    /// Human-readable explanation when <see cref="WasAttempted"/> is
    /// <c>false</c>; empty otherwise.
    /// </summary>
    public string RefusalReason { get; init; } = string.Empty;

    public IReadOnlyList<string> AttemptedIndexes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SucceededIndexes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FailedIndexes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Result of re-running both pragmas after the repair attempt.
    /// <c>null</c> when the repair was refused before running.
    /// </summary>
    public DatabaseFileIntegrityResult? PostRepairDiagnosis { get; init; }

    public bool PostRepairIsHealthy => PostRepairDiagnosis?.IsHealthy ?? false;

    public static DatabaseRepairResult NotAttempted(string refusalReason) =>
        new()
        {
            WasAttempted = false,
            RefusalReason = refusalReason,
        };

    public static DatabaseRepairResult Attempted(
        IReadOnlyList<string> attemptedIndexes,
        IReadOnlyList<string> succeededIndexes,
        IReadOnlyList<string> failedIndexes,
        DatabaseFileIntegrityResult postRepairDiagnosis) =>
        new()
        {
            WasAttempted = true,
            AttemptedIndexes = attemptedIndexes,
            SucceededIndexes = succeededIndexes,
            FailedIndexes = failedIndexes,
            PostRepairDiagnosis = postRepairDiagnosis,
        };
}

/// <summary>
/// Structured result of <see cref="SqliteBackend.CheckDatabaseFileIntegrity"/>.
///
/// <para>
/// <c>cipher_integrity_check</c> (SQLCipher) verifies the HMAC of every
/// page on disk. <b>It returns zero rows when every page verifies and
/// one row per failure otherwise</b> -- this is the opposite of stock
/// SQLite's <c>integrity_check</c> and the most common pitfall when
/// consuming its output. Failures here mean the ciphertext was
/// truncated or bit-flipped after it was written, OR the wrong
/// SQLCipher parameters are in use (e.g. a forced page-size
/// mismatch).
/// </para>
///
/// <para>
/// <c>integrity_check</c> (stock SQLite) walks the b-tree and reports
/// rowid / page-link / index-row inconsistencies. It returns a single
/// row containing the literal <c>"ok"</c> when the database is healthy
/// or one row per problem otherwise. Failures here mean the
/// <i>plaintext</i> image is malformed even though every page decrypted
/// cleanly -- the symptom that surfaces as SQLite error 11
/// ("database disk image is malformed") on the next write attempt.
/// </para>
/// </summary>
/// <param name="CipherIntegrityMessages">
/// Rows returned by <c>PRAGMA cipher_integrity_check</c>. An empty
/// list means every page HMAC verified.
/// </param>
/// <param name="SqliteIntegrityMessages">
/// Rows returned by <c>PRAGMA integrity_check</c>. A single <c>"ok"</c>
/// row means the b-tree is structurally valid.
/// </param>
public sealed record DatabaseFileIntegrityResult(
    IReadOnlyList<string> CipherIntegrityMessages,
    IReadOnlyList<string> SqliteIntegrityMessages)
{
    public bool CipherOk => CipherIntegrityMessages.Count == 0;

    public bool SqliteOk =>
        SqliteIntegrityMessages.Count == 1 &&
        string.Equals(SqliteIntegrityMessages[0], "ok", StringComparison.Ordinal);

    public bool IsHealthy => CipherOk && SqliteOk;
}

