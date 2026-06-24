using AzureBackup.Core.Services;

namespace AzureBackup.Tests.Unit;

/// <summary>
/// B61: tests for <see cref="QuarantineFileLocator"/>. The locator runs
/// in the form-open code path of the rebuild-from-quarantined-catalog UI,
/// so its return shape is load-bearing: a half-populated pair would make
/// the rebuild form open with one path filled in and the other blank,
/// which is worse than opening with both blank.
/// </summary>
public sealed class QuarantineFileLocatorTests : IDisposable
{
    private readonly string _dir;

    public QuarantineFileLocatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "azbk-quar-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void FindMostRecentQuarantinedDatabase_NullDirectory_ReturnsNull()
    {
        Assert.Null(QuarantineFileLocator.FindMostRecentQuarantinedDatabase(null));
    }

    [Fact]
    public void FindMostRecentQuarantinedDatabase_WhitespaceDirectory_ReturnsNull()
    {
        Assert.Null(QuarantineFileLocator.FindMostRecentQuarantinedDatabase("   "));
    }

    [Fact]
    public void FindMostRecentQuarantinedDatabase_NonExistentDirectory_ReturnsNull()
    {
        var bogus = Path.Combine(_dir, "does-not-exist");
        Assert.Null(QuarantineFileLocator.FindMostRecentQuarantinedDatabase(bogus));
    }

    [Fact]
    public void FindMostRecentQuarantinedDatabase_EmptyDirectory_ReturnsNull()
    {
        Assert.Null(QuarantineFileLocator.FindMostRecentQuarantinedDatabase(_dir));
    }

    [Fact]
    public void FindMostRecentQuarantinedDatabase_DbOnly_ReturnsTheDb()
    {
        // The snapshot format has no .salt sidecar, so a lone quarantined
        // catalog is a complete, rebuildable input.
        var dbPath = Path.Combine(_dir, "backup.db.quarantine-20260427-153012");
        File.WriteAllText(dbPath, "x");

        Assert.Equal(dbPath, QuarantineFileLocator.FindMostRecentQuarantinedDatabase(_dir));
    }

    [Fact]
    public void FindMostRecentQuarantinedDatabase_SaltOnly_ReturnsNull()
    {
        // A lone .salt quarantine file (legacy SQLCipher leftover) is not a
        // catalog and cannot be rebuilt from.
        File.WriteAllText(Path.Combine(_dir, "backup.db.salt.quarantine-20260427-153012"), "x");

        Assert.Null(QuarantineFileLocator.FindMostRecentQuarantinedDatabase(_dir));
    }

    [Fact]
    public void FindMostRecentQuarantinedDatabase_WithLegacySaltSidecar_StillReturnsTheDb()
    {
        // A legacy quarantine event left both a db and a .salt sidecar; only
        // the catalog is returned (the sidecar is ignored).
        var dbPath = Path.Combine(_dir, "backup.db.quarantine-20260427-153012");
        File.WriteAllText(dbPath, "x");
        File.WriteAllText(Path.Combine(_dir, "backup.db.salt.quarantine-20260427-153012"), "y");

        Assert.Equal(dbPath, QuarantineFileLocator.FindMostRecentQuarantinedDatabase(_dir));
    }

    [Fact]
    public void FindMostRecentQuarantinedDatabase_MultipleEvents_ReturnsNewestStamp()
    {
        File.WriteAllText(Path.Combine(_dir, "backup.db.quarantine-20260427-100000"), "x");

        var newestDb = Path.Combine(_dir, "backup.db.quarantine-20260427-153012");
        File.WriteAllText(newestDb, "x");

        File.WriteAllText(Path.Combine(_dir, "backup.db.quarantine-20260101-000000"), "x");

        Assert.Equal(newestDb, QuarantineFileLocator.FindMostRecentQuarantinedDatabase(_dir));
    }

    [Fact]
    public void FindMostRecentQuarantinedDatabase_IgnoresWalShmJournalCompanions()
    {
        // -wal/-shm/-journal companions share the prefix backup.db but must
        // not be returned in place of the real catalog file.
        var dbPath = Path.Combine(_dir, "backup.db.quarantine-20260427-153012");
        File.WriteAllText(dbPath, "x");
        File.WriteAllText(Path.Combine(_dir, "backup.db-wal.quarantine-20260427-153012"), "wal");
        File.WriteAllText(Path.Combine(_dir, "backup.db-shm.quarantine-20260427-153012"), "shm");
        File.WriteAllText(Path.Combine(_dir, "backup.db-journal.quarantine-20260427-153012"), "journal");

        Assert.Equal(dbPath, QuarantineFileLocator.FindMostRecentQuarantinedDatabase(_dir));
    }

    [Fact]
    public void FindMostRecentQuarantinedDatabase_IgnoresUnrelatedFilesInDirectory()
    {
        // The data directory contains the live backup.db, log files, and other
        // artefacts. None of these should be misread as quarantine candidates.
        File.WriteAllText(Path.Combine(_dir, "backup.db"), "live");
        File.WriteAllText(Path.Combine(_dir, "backup.db-wal"), "wal");
        File.WriteAllText(Path.Combine(_dir, "azurebackup-2026-04-27.log"), "log");

        var dbPath = Path.Combine(_dir, "backup.db.quarantine-20260427-153012");
        File.WriteAllText(dbPath, "x");

        Assert.Equal(dbPath, QuarantineFileLocator.FindMostRecentQuarantinedDatabase(_dir));
    }

    [Fact]
    public void FindMostRecentQuarantinedDatabase_MalformedTimestamp_IsIgnored()
    {
        // A file whose suffix doesn't match the strict yyyyMMdd-HHmmss pattern
        // is skipped rather than crashing the locator.
        File.WriteAllText(Path.Combine(_dir, "backup.db.quarantine-not-a-stamp"), "x");

        Assert.Null(QuarantineFileLocator.FindMostRecentQuarantinedDatabase(_dir));
    }

    [Theory]
    [InlineData("backup.db.quarantine-20260427-153012", true)]
    [InlineData("backup.db.salt.quarantine-20260427-153012", true)]
    [InlineData("backup.db", false)]
    [InlineData("backup.db.salt", false)]
    [InlineData("azurebackup-2026-04-27.log", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsQuarantineFileName_ClassifiesEachShape(string? name, bool expected)
    {
        Assert.Equal(expected, QuarantineFileLocator.IsQuarantineFileName(name));
    }

    [Fact]
    public void TryParseQuarantineStamp_ReturnsUtcDateTimeForValidName()
    {
        var stamp = QuarantineFileLocator.TryParseQuarantineStamp("backup.db.quarantine-20260427-153012");

        Assert.NotNull(stamp);
        Assert.Equal(DateTimeKind.Utc, stamp!.Value.Kind);
        Assert.Equal(new DateTime(2026, 4, 27, 15, 30, 12, DateTimeKind.Utc), stamp.Value);
    }

    [Fact]
    public void TryParseQuarantineStamp_ReturnsNullForUnsuffixedName()
    {
        Assert.Null(QuarantineFileLocator.TryParseQuarantineStamp("backup.db"));
    }
}
