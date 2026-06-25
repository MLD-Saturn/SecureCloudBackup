using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SecureCloudBackup.Core.Services.Backends;
using SecureCloudBackup.Crypto;
using SecureCloudBackup.Migration;
using Xunit;

namespace SecureCloudBackup.Tests;

/// <summary>
/// Cross-process integration tests for the legacy SQLCipher to AES-256-GCM
/// snapshot migration (W-DB-enc Step 9).
///
/// <para>
/// The two SQLite native engines cannot coexist in one process: this test
/// assembly loads <c>e_sqlite3</c> (via Core) which disables SQLCipher
/// (AGENT_CONTEXT facts 61/62), so the migration -- and even SEEDING a legacy
/// catalog -- must run OUT OF PROCESS in the single-engine
/// <c>securecloudbackup-migrate</c> helper. Each test drives that helper twice: once in
/// its <c>seed</c> subcommand to write a real SQLCipher catalog, then in its
/// default migrate mode to produce the snapshot, which is finally opened in the
/// in-process <see cref="InMemorySnapshotBackend"/> (the modern engine) to assert
/// the data round-tripped.
/// </para>
/// </summary>
public sealed class LegacyCatalogMigratorTests : IDisposable
{
    private readonly string _dir;
    private readonly string _legacyDbPath;
    private readonly string _outputSnapshotPath;
    private const string Password = "legacy-migrate-password";

    public LegacyCatalogMigratorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "azbk-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _legacyDbPath = Path.Combine(_dir, "legacy.db");
        _outputSnapshotPath = Path.Combine(_dir, "backup.db.snapshot");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---- helper-process plumbing -------------------------------------------

    /// <summary>
    /// Resolves the migrate helper exe from the Migration project's OWN build
    /// output, NOT the test output directory.
    ///
    /// <para>
    /// This matters: the test output directory contains BOTH native SQLite
    /// engines (e_sqlite3 from Core + e_sqlcipher from Migration, because the test
    /// project references both), and SQLitePCLRaw's native resolution then binds
    /// the PLAIN e_sqlite3 engine in that folder -- so the helper's SQLCipher
    /// PRAGMA key is silently ignored and the seeded database comes out
    /// unencrypted (AGENT_CONTEXT facts 61/62, observed at the file-copy level).
    /// The Migration project's own output is single-engine (e_sqlcipher only),
    /// exactly as the helper ships in production next to the app, so the helper
    /// must run from there to behave correctly.
    /// </para>
    /// </summary>
    private static string HelperPath()
    {
        var exeName = OperatingSystem.IsWindows() ? "securecloudbackup-migrate.exe" : "securecloudbackup-migrate";

        // Derive the build configuration + TFM from the test assembly's own path
        // (.../tests/SecureCloudBackup.Tests/bin/<Config>/<Tfm>/), then point at the
        // sibling Migration project's single-engine output for the same config+TFM.
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);                                  // e.g. net10.0
        var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);       // e.g. Debug

        // Walk up to the repository root (the folder that contains 'src').
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        Assert.True(dir is not null, $"Could not locate the repository root above '{baseDir}'.");

        var candidate = Path.Combine(
            dir!.FullName, "src", "SecureCloudBackup.Migration", "bin", config, tfm, exeName);
        Assert.True(File.Exists(candidate),
            $"Migration helper not found at '{candidate}'. Build the SecureCloudBackup.Migration project " +
            $"(its single-engine output is required; the test output dir is polluted with e_sqlite3).");
        return candidate;
    }

    /// <summary>
    /// Launches the helper with an optional subcommand, writes <paramref name="stdinJson"/>
    /// to its stdin, and returns (exitCode, stderr). Mirrors the production
    /// orchestrator's process plumbing: redirect stdin + stderr only (never the
    /// undrained stdout), write, close stdin, wait.
    /// </summary>
    private static (int exitCode, string stderr) RunHelper(string? subcommand, string stdinJson)
    {
        var psi = new ProcessStartInfo
        {
            FileName = HelperPath(),
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(subcommand))
            psi.ArgumentList.Add(subcommand);

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        Assert.True(process.Start(), "Failed to start the migration helper process.");
        process.BeginErrorReadLine();
        process.StandardInput.Write(stdinJson);
        process.StandardInput.Close();

        Assert.True(process.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds),
            "Migration helper did not exit within the time budget.");
        process.WaitForExit(); // flush async stderr
        return (process.ExitCode, stderr.ToString().Trim());
    }

    private string ReadSaltBase64() =>
        Convert.ToBase64String(File.ReadAllBytes(_legacyDbPath + ".salt"));

    private static string SeedJson(string dbPath, string saltBase64, string password, int fileCount) =>
        JsonSerializer.Serialize(new
        {
            DatabasePath = dbPath,
            SaltBase64 = saltBase64,
            Password = password,
            FileCount = fileCount,
        });

    private static string MigrateJson(string dbPath, string saltBase64, string password, string outputPath) =>
        JsonSerializer.Serialize(new
        {
            LegacyDatabasePath = dbPath,
            LegacySaltBase64 = saltBase64,
            Password = password,
            OutputSnapshotPath = outputPath,
        });

    /// <summary>
    /// Seeds a real SQLCipher catalog with <paramref name="fileCount"/> file rows
    /// via the helper's seed subcommand. Generates a fresh 16-byte salt and writes
    /// it (and the catalog) under <see cref="_legacyDbPath"/>.
    /// </summary>
    private void SeedLegacyDatabase(int fileCount)
    {
        var salt = new byte[KdfParameters.SaltSize];
        System.Security.Cryptography.RandomNumberGenerator.Fill(salt);
        var (exit, stderr) = RunHelper("seed", SeedJson(_legacyDbPath, Convert.ToBase64String(salt), Password, fileCount));
        Assert.True(exit == 0, $"Seed helper failed (exit {exit}): {stderr}");
        Assert.True(File.Exists(_legacyDbPath), "Seed did not create the legacy database.");
        Assert.True(File.Exists(_legacyDbPath + ".salt"), "Seed did not create the salt sidecar.");
    }

    // ---- happy path / empty / large ----------------------------------------

    [Fact]
    public void Migrate_LegacySqlCipherCatalog_WritesDecryptableSnapshot()
    {
        SeedLegacyDatabase(fileCount: 3);

        var (exit, stderr) = RunHelper(null, MigrateJson(_legacyDbPath, ReadSaltBase64(), Password, _outputSnapshotPath));

        Assert.True(exit == 0, $"Migrate helper failed (exit {exit}): {stderr}");
        Assert.True(File.Exists(_outputSnapshotPath));
        var bytes = File.ReadAllBytes(_outputSnapshotPath);
        Assert.True(DbSnapshotEnvelope.HasMagic(bytes), "Output must be an AZDB snapshot.");
        var image = DbSnapshotEnvelope.Decrypt(bytes, Password);
        Assert.NotEmpty(image);
    }

    [Fact]
    public void Migrate_SnapshotOpensInTheModernBackend_WithSameData()
    {
        SeedLegacyDatabase(fileCount: 5);

        var (exit, stderr) = RunHelper(null, MigrateJson(_legacyDbPath, ReadSaltBase64(), Password, _outputSnapshotPath));
        Assert.True(exit == 0, $"Migrate helper failed (exit {exit}): {stderr}");

        using var backend = new InMemorySnapshotBackend();
        backend.Initialize(_outputSnapshotPath, Password.AsSpan());

        var file = backend.GetBackedUpFile("C:/data/file-0.dat");
        Assert.NotNull(file);
        Assert.Equal(0, file!.FileSize);
        Assert.Equal("hash-0", file.FileHash);
    }

    [Fact]
    public void Migrate_EmptyCatalog_ProducesValidSnapshotWithSchemaOnly()
    {
        SeedLegacyDatabase(fileCount: 0);

        var (exit, stderr) = RunHelper(null, MigrateJson(_legacyDbPath, ReadSaltBase64(), Password, _outputSnapshotPath));
        Assert.True(exit == 0, $"Migrate helper failed (exit {exit}): {stderr}");

        using var migrated = new InMemorySnapshotBackend();
        migrated.Initialize(_outputSnapshotPath, Password.AsSpan());
        Assert.Empty(migrated.GetAllBackedUpFiles());
        Assert.NotNull(migrated.GetConfiguration());
    }

    [Fact]
    public void Migrate_LargeCatalog_RoundTripsAllRows()
    {
        const int fileCount = 1500;
        SeedLegacyDatabase(fileCount);

        var (exit, stderr) = RunHelper(null, MigrateJson(_legacyDbPath, ReadSaltBase64(), Password, _outputSnapshotPath));
        Assert.True(exit == 0, $"Migrate helper failed (exit {exit}): {stderr}");

        using var migrated = new InMemorySnapshotBackend();
        migrated.Initialize(_outputSnapshotPath, Password.AsSpan());
        var all = migrated.GetAllBackedUpFiles();
        Assert.Equal(fileCount, all.Count);
        var sample = migrated.GetBackedUpFile("C:/data/file-1499.dat");
        Assert.NotNull(sample);
        Assert.Equal(1499, sample!.FileSize);
    }

    // ---- migrate error paths (exit codes across the process boundary) -------

    [Fact]
    public void Migrate_WithWrongPassword_ReturnsInvalidPasswordAndWritesNoSnapshot()
    {
        SeedLegacyDatabase(fileCount: 2);

        var (exit, _) = RunHelper(null, MigrateJson(_legacyDbPath, ReadSaltBase64(), "wrong-password", _outputSnapshotPath));

        Assert.Equal((int)MigrationExitCode.InvalidPassword, exit);
        Assert.False(File.Exists(_outputSnapshotPath), "No snapshot should be written on a wrong password.");
    }

    [Fact]
    public void Migrate_WithMissingLegacyDatabase_ReturnsSourceNotFound()
    {
        var missing = Path.Combine(_dir, "does-not-exist.db");
        var saltBase64 = Convert.ToBase64String(new byte[KdfParameters.SaltSize]);

        var (exit, _) = RunHelper(null, MigrateJson(missing, saltBase64, Password, _outputSnapshotPath));

        Assert.Equal((int)MigrationExitCode.SourceNotFound, exit);
    }

    [Fact]
    public void Migrate_WithBadSaltLength_ReturnsBadRequest()
    {
        SeedLegacyDatabase(fileCount: 1);

        var (exit, _) = RunHelper(null, MigrateJson(_legacyDbPath, Convert.ToBase64String(new byte[8]), Password, _outputSnapshotPath));

        Assert.Equal((int)MigrationExitCode.BadRequest, exit);
    }

    [Fact]
    public void Migrate_WithNonBase64Salt_ReturnsBadRequest()
    {
        SeedLegacyDatabase(fileCount: 1);

        var (exit, _) = RunHelper(null, MigrateJson(_legacyDbPath, "not!base64!", Password, _outputSnapshotPath));

        Assert.Equal((int)MigrationExitCode.BadRequest, exit);
    }

    [Fact]
    public void Migrate_WithEmptyPassword_ReturnsBadRequest()
    {
        SeedLegacyDatabase(fileCount: 1);

        var (exit, _) = RunHelper(null, MigrateJson(_legacyDbPath, ReadSaltBase64(), string.Empty, _outputSnapshotPath));

        Assert.Equal((int)MigrationExitCode.BadRequest, exit);
    }

    // ---- interrupted migration / crash safety ------------------------------

    [Fact]
    public void Migrate_KilledMidRun_LeavesNoSnapshotAndRetrySucceeds()
    {
        // A large catalog makes the helper take long enough to kill mid-run.
        SeedLegacyDatabase(fileCount: 20000);

        var psi = new ProcessStartInfo
        {
            FileName = HelperPath(),
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using (var process = new Process { StartInfo = psi })
        {
            Assert.True(process.Start());
            process.StandardInput.Write(MigrateJson(_legacyDbPath, ReadSaltBase64(), Password, _outputSnapshotPath));
            process.StandardInput.Close();
            // Kill almost immediately so the migration cannot have completed the
            // atomic write + rename of the final snapshot.
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        // The helper writes the snapshot atomically (temp + rename), so a kill
        // must never leave a partial file at the final output path. The original
        // legacy catalog is untouched (the helper only reads it).
        Assert.False(File.Exists(_outputSnapshotPath),
            "A killed migration must not leave a snapshot at the final output path.");
        Assert.True(File.Exists(_legacyDbPath), "The legacy source must be left intact.");

        // A clean retry must now succeed end-to-end.
        var (exit, stderr) = RunHelper(null, MigrateJson(_legacyDbPath, ReadSaltBase64(), Password, _outputSnapshotPath));
        Assert.True(exit == 0, $"Retry after interrupted migration failed (exit {exit}): {stderr}");
        Assert.True(File.Exists(_outputSnapshotPath));
        using var migrated = new InMemorySnapshotBackend();
        migrated.Initialize(_outputSnapshotPath, Password.AsSpan());
        Assert.Equal(20000, migrated.GetAllBackedUpFiles().Count);
    }

    // ---- MigrationRunner stdin contract (no SQLCipher; safe in-process) -----
    // These short-circuit on a bad request BEFORE any SQLCipher engine call, so
    // they run safely in the test process via internals (InternalsVisibleTo).

    [Fact]
    public void Runner_WithEmptyStdin_ReturnsBadRequest()
    {
        using var stdin = new StringReader(string.Empty);
        using var stderr = new StringWriter();

        var code = MigrationRunner.Run(stdin, stderr);

        Assert.Equal((int)MigrationExitCode.BadRequest, code);
    }

    [Fact]
    public void Runner_WithMalformedJson_ReturnsBadRequest()
    {
        using var stdin = new StringReader("{ this is not valid json ");
        using var stderr = new StringWriter();

        var code = MigrationRunner.Run(stdin, stderr);

        Assert.Equal((int)MigrationExitCode.BadRequest, code);
    }
}
