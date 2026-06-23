using System.Diagnostics;
using System.Text;
using AzureBackup.Crypto;

namespace AzureBackup.Core.Services.Backends;

/// <summary>
/// Orchestrates the one-time, automatic-at-unlock migration of a legacy
/// SQLCipher catalog to the new application-level encrypted snapshot format.
///
/// <para>
/// Because the legacy SQLCipher engine and the modern <c>e_sqlite3</c> engine
/// cannot coexist in one process, the actual read of the legacy database runs in
/// a SEPARATE single-engine helper process (<c>azurebackup-migrate</c>). This
/// orchestrator: locates that helper, streams the request (db path, salt,
/// password, a sibling temp output path) to its stdin, waits for it to write and
/// self-verify the new snapshot, and only then atomically swaps the snapshot
/// into place while moving the original SQLCipher artefacts aside under a
/// <c>.quarantine-...</c> suffix (never deleted, so migration is reversible).
/// </para>
///
/// <para>
/// <b>Safety.</b> The original SQLCipher database is left completely untouched
/// until the new snapshot has been written and verified by the helper AND
/// re-verified here. If anything fails, the original remains in place and the
/// migration can be retried on the next unlock.
/// </para>
/// </summary>
internal static class LegacyMigrationOrchestrator
{
    /// <summary>Default time budget for the helper process.</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Migrates the legacy SQLCipher catalog at <paramref name="databasePath"/>
    /// in place: on success, <paramref name="databasePath"/> becomes the new
    /// encrypted snapshot and the original SQLCipher file + salt are quarantined.
    /// </summary>
    /// <param name="databasePath">Path to the legacy SQLCipher database.</param>
    /// <param name="password">The user's password (used by the helper to unlock the legacy DB).</param>
    /// <param name="diag">Optional diagnostic sink.</param>
    /// <exception cref="LegacyMigrationException">Migration failed; the original catalog is intact.</exception>
    public static void Migrate(string databasePath, ReadOnlySpan<char> password, Action<string>? diag = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty.", nameof(password));

        var saltPath = databasePath + ".salt";
        if (!File.Exists(databasePath) || !File.Exists(saltPath))
            throw new LegacyMigrationException(
                $"Legacy catalog or salt sidecar missing (db={File.Exists(databasePath)}, salt={File.Exists(saltPath)}).");

        var helperPath = ResolveHelperPath()
            ?? throw new LegacyMigrationException(
                "Could not locate the migration helper executable next to the application.");

        // The helper writes to a sibling temp file; we swap it in only after both
        // the helper and this method verify it. Never the live path directly.
        var pendingSnapshotPath = databasePath + ".migrated-" + Guid.NewGuid().ToString("N");

        // The password is sent over stdin (never argv/env).
        var saltBase64 = Convert.ToBase64String(File.ReadAllBytes(saltPath));

        diag?.Invoke($"LegacyMigration: launching helper '{helperPath}' for '{databasePath}'");

        // Build the request JSON into a char[] (zeroable) so the password never
        // becomes an immutable string on the heap.
        var requestChars = BuildRequestJson(databasePath, saltBase64, password, pendingSnapshotPath);
        int exitCode;
        string stderr;
        try
        {
            (exitCode, stderr) = RunHelper(helperPath, requestChars, DefaultTimeout, diag);
        }
        finally
        {
            Array.Clear(requestChars);
        }

        if (exitCode != 0)
        {
            TryDelete(pendingSnapshotPath);
            throw new LegacyMigrationException(
                $"Migration helper failed (exit code {exitCode}): {stderr}".Trim());
        }

        // Defence in depth: re-verify the produced snapshot decrypts here too,
        // before we touch the original catalog.
        try
        {
            VerifySnapshot(pendingSnapshotPath, password);
        }
        catch (Exception ex)
        {
            TryDelete(pendingSnapshotPath);
            throw new LegacyMigrationException(
                $"Migrated snapshot failed verification: {ex.Message}", ex);
        }

        // Atomic swap + quarantine: move the original SQLCipher artefacts aside,
        // then move the verified snapshot into the live path.
        SwapInSnapshot(databasePath, saltPath, pendingSnapshotPath, diag);
        diag?.Invoke("LegacyMigration: migration completed and original catalog quarantined");
    }

    /// <summary>
    /// Resolves the path to the migration helper executable. It is published next
    /// to the main application; the executable name differs by platform.
    /// </summary>
    private static string? ResolveHelperPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var exeName = OperatingSystem.IsWindows() ? "azurebackup-migrate.exe" : "azurebackup-migrate";
        var candidate = Path.Combine(baseDir, exeName);
        return File.Exists(candidate) ? candidate : null;
    }

    private static char[] BuildRequestJson(
        string databasePath, string saltBase64, ReadOnlySpan<char> password, string outputPath)
    {
        // Build the JSON into a StringBuilder, then copy to a char[] and clear the
        // builder, so the password ends up only in a buffer we can zero. (A
        // char[]-only path across the process boundary is not achievable through
        // Process stdin without a custom transport; the caller zeros this buffer
        // immediately after writing it.)
        var sb = new StringBuilder();
        sb.Append('{');
        AppendJsonString(sb, "LegacyDatabasePath", databasePath); sb.Append(',');
        AppendJsonString(sb, "LegacySaltBase64", saltBase64); sb.Append(',');
        sb.Append("\"Password\":");
        AppendJsonStringValue(sb, password); sb.Append(',');
        AppendJsonString(sb, "OutputSnapshotPath", outputPath);
        sb.Append('}');

        var result = new char[sb.Length];
        sb.CopyTo(0, result, 0, sb.Length);
        sb.Clear();
        return result;
    }

    private static void AppendJsonString(StringBuilder sb, string name, string value)
    {
        sb.Append('"').Append(name).Append("\":");
        AppendJsonStringValue(sb, value);
    }

    private static void AppendJsonStringValue(StringBuilder sb, ReadOnlySpan<char> value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    private static (int exitCode, string stderr) RunHelper(
        string helperPath, char[] requestChars, TimeSpan timeout, Action<string>? diag)
    {
        var psi = new ProcessStartInfo
        {
            FileName = helperPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        if (!process.Start())
            throw new LegacyMigrationException("Failed to start the migration helper process.");

        process.BeginErrorReadLine();

        // Write the request char[] and close stdin so the helper's read completes.
        process.StandardInput.Write(requestChars, 0, requestChars.Length);
        process.StandardInput.Close();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new LegacyMigrationException($"Migration helper timed out after {timeout.TotalMinutes:N0} minutes.");
        }
        process.WaitForExit(); // ensure async stderr is flushed

        return (process.ExitCode, stderr.ToString().Trim());
    }

    private static void VerifySnapshot(string snapshotPath, ReadOnlySpan<char> password)
    {
        if (!File.Exists(snapshotPath) || new FileInfo(snapshotPath).Length == 0)
            throw new LegacyMigrationException("Migration helper did not produce a snapshot file.");

        var bytes = File.ReadAllBytes(snapshotPath);
        if (!DbSnapshotEnvelope.HasMagic(bytes))
            throw new LegacyMigrationException("Produced file is not an AZDB snapshot.");

        // Decrypt to confirm the password recovers the data. Zero the plaintext.
        var plaintext = DbSnapshotEnvelope.Decrypt(bytes, password);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
    }

    /// <summary>
    /// Moves the original SQLCipher database, its salt, and any WAL/SHM/journal
    /// companions aside under a timestamped <c>.quarantine-...</c> suffix, then
    /// moves the verified snapshot into the live path.
    /// </summary>
    private static void SwapInSnapshot(
        string databasePath, string saltPath, string pendingSnapshotPath, Action<string>? diag)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var suffix = $".quarantine-{stamp}";

        // Quarantine the legacy artefacts. The DB move is the critical one; if it
        // fails the original is left intact and the snapshot is discarded.
        try
        {
            File.Move(databasePath, databasePath + suffix);
        }
        catch (Exception ex)
        {
            TryDelete(pendingSnapshotPath);
            throw new LegacyMigrationException(
                $"Could not quarantine the legacy database (it may be locked): {ex.Message}", ex);
        }

        // Best-effort quarantine of the companions (the snapshot does not use them).
        TryMove(saltPath, saltPath + suffix);
        TryMove(databasePath + "-wal", databasePath + "-wal" + suffix);
        TryMove(databasePath + "-shm", databasePath + "-shm" + suffix);
        TryMove(databasePath + "-journal", databasePath + "-journal" + suffix);

        // Move the verified snapshot into the live path. If THIS fails, restore
        // the original so the user is not left without a catalog.
        try
        {
            File.Move(pendingSnapshotPath, databasePath);
        }
        catch (Exception ex)
        {
            try { File.Move(databasePath + suffix, databasePath); } catch { /* best effort restore */ }
            TryDelete(pendingSnapshotPath);
            throw new LegacyMigrationException(
                $"Could not move the migrated snapshot into place: {ex.Message}", ex);
        }

        diag?.Invoke($"LegacyMigration: quarantined legacy artefacts under '{suffix}'");
    }

    private static void TryMove(string from, string to)
    {
        try { if (File.Exists(from)) File.Move(from, to); } catch { /* best effort */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
