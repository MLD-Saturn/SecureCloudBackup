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
    public void FindMostRecentQuarantinePair_NullDirectory_ReturnsNullPair()
    {
        var (db, salt) = QuarantineFileLocator.FindMostRecentQuarantinePair(null);
        Assert.Null(db);
        Assert.Null(salt);
    }

    [Fact]
    public void FindMostRecentQuarantinePair_WhitespaceDirectory_ReturnsNullPair()
    {
        var (db, salt) = QuarantineFileLocator.FindMostRecentQuarantinePair("   ");
        Assert.Null(db);
        Assert.Null(salt);
    }

    [Fact]
    public void FindMostRecentQuarantinePair_NonExistentDirectory_ReturnsNullPair()
    {
        var bogus = Path.Combine(_dir, "does-not-exist");
        var (db, salt) = QuarantineFileLocator.FindMostRecentQuarantinePair(bogus);
        Assert.Null(db);
        Assert.Null(salt);
    }

    [Fact]
    public void FindMostRecentQuarantinePair_EmptyDirectory_ReturnsNullPair()
    {
        var (db, salt) = QuarantineFileLocator.FindMostRecentQuarantinePair(_dir);
        Assert.Null(db);
        Assert.Null(salt);
    }

    [Fact]
    public void FindMostRecentQuarantinePair_OnlyDbHalf_ReturnsNullPair()
    {
        File.WriteAllText(Path.Combine(_dir, "backup.db.quarantine-20260427-153012"), "x");

        var (db, salt) = QuarantineFileLocator.FindMostRecentQuarantinePair(_dir);

        Assert.Null(db);
        Assert.Null(salt);
    }

    [Fact]
    public void FindMostRecentQuarantinePair_OnlySaltHalf_ReturnsNullPair()
    {
        File.WriteAllText(Path.Combine(_dir, "backup.db.salt.quarantine-20260427-153012"), "x");

        var (db, salt) = QuarantineFileLocator.FindMostRecentQuarantinePair(_dir);

        Assert.Null(db);
        Assert.Null(salt);
    }

    [Fact]
    public void FindMostRecentQuarantinePair_SinglePair_ReturnsBothPaths()
    {
        var dbPath = Path.Combine(_dir, "backup.db.quarantine-20260427-153012");
        var saltPath = Path.Combine(_dir, "backup.db.salt.quarantine-20260427-153012");
        File.WriteAllText(dbPath, "x");
        File.WriteAllText(saltPath, "y");

        var (db, salt) = QuarantineFileLocator.FindMostRecentQuarantinePair(_dir);

        Assert.Equal(dbPath, db);
        Assert.Equal(saltPath, salt);
    }

    [Fact]
    public void FindMostRecentQuarantinePair_MultipleEvents_ReturnsNewestStamp()
    {
        File.WriteAllText(Path.Combine(_dir, "backup.db.quarantine-20260427-100000"), "x");
        File.WriteAllText(Path.Combine(_dir, "backup.db.salt.quarantine-20260427-100000"), "y");

        var newestDb = Path.Combine(_dir, "backup.db.quarantine-20260427-153012");
        var newestSalt = Path.Combine(_dir, "backup.db.salt.quarantine-20260427-153012");
        File.WriteAllText(newestDb, "x");
        File.WriteAllText(newestSalt, "y");

        File.WriteAllText(Path.Combine(_dir, "backup.db.quarantine-20260101-000000"), "x");
        File.WriteAllText(Path.Combine(_dir, "backup.db.salt.quarantine-20260101-000000"), "y");

        var (db, salt) = QuarantineFileLocator.FindMostRecentQuarantinePair(_dir);

        Assert.Equal(newestDb, db);
        Assert.Equal(newestSalt, salt);
    }

    [Fact]
    public void FindMostRecentQuarantinePair_NewestDbStampMissingItsSalt_ReturnsOlderCompletePair()
    {
        // The newest stamp has only the db file; the older stamp has both halves.
        // Pairing must walk past the lonely newest and return the complete older pair
        // rather than handing back a half-populated tuple.
        File.WriteAllText(Path.Combine(_dir, "backup.db.quarantine-20260427-200000"), "x");

        var olderDb = Path.Combine(_dir, "backup.db.quarantine-20260427-100000");
        var olderSalt = Path.Combine(_dir, "backup.db.salt.quarantine-20260427-100000");
        File.WriteAllText(olderDb, "x");
        File.WriteAllText(olderSalt, "y");

        var (db, salt) = QuarantineFileLocator.FindMostRecentQuarantinePair(_dir);

        Assert.Equal(olderDb, db);
        Assert.Equal(olderSalt, salt);
    }

    [Fact]
    public void FindMostRecentQuarantinePair_IgnoresWalShmJournalCompanions()
    {
        // -wal/-shm/-journal companions are also moved by QuarantineCorruptDatabase
        // and live in the same directory; they share the prefix backup.db but must
        // not be returned in place of the real database file.
        var dbPath = Path.Combine(_dir, "backup.db.quarantine-20260427-153012");
        var saltPath = Path.Combine(_dir, "backup.db.salt.quarantine-20260427-153012");
        File.WriteAllText(dbPath, "x");
        File.WriteAllText(saltPath, "y");
        File.WriteAllText(Path.Combine(_dir, "backup.db-wal.quarantine-20260427-153012"), "wal");
        File.WriteAllText(Path.Combine(_dir, "backup.db-shm.quarantine-20260427-153012"), "shm");
        File.WriteAllText(Path.Combine(_dir, "backup.db-journal.quarantine-20260427-153012"), "journal");

        var (db, salt) = QuarantineFileLocator.FindMostRecentQuarantinePair(_dir);

        Assert.Equal(dbPath, db);
        Assert.Equal(saltPath, salt);
    }

    [Fact]
    public void FindMostRecentQuarantinePair_IgnoresUnrelatedFilesInDirectory()
    {
        // The data directory contains the live backup.db, .salt sidecar, log files,
        // and other artefacts. None of these should be misread as quarantine candidates.
        File.WriteAllText(Path.Combine(_dir, "backup.db"), "live");
        File.WriteAllText(Path.Combine(_dir, "backup.db.salt"), "live-salt");
        File.WriteAllText(Path.Combine(_dir, "backup.db-wal"), "wal");
        File.WriteAllText(Path.Combine(_dir, "azurebackup-2026-04-27.log"), "log");

        var dbPath = Path.Combine(_dir, "backup.db.quarantine-20260427-153012");
        var saltPath = Path.Combine(_dir, "backup.db.salt.quarantine-20260427-153012");
        File.WriteAllText(dbPath, "x");
        File.WriteAllText(saltPath, "y");

        var (db, salt) = QuarantineFileLocator.FindMostRecentQuarantinePair(_dir);

        Assert.Equal(dbPath, db);
        Assert.Equal(saltPath, salt);
    }

    [Fact]
    public void FindMostRecentQuarantinePair_MalformedTimestamp_IsIgnored()
    {
        // A file whose suffix doesn't match the strict yyyyMMdd-HHmmss pattern is
        // skipped rather than crashing the locator. Quarantine flows produce only
        // strict timestamps so any deviation is either a user-renamed file or
        // bit-rot of the filename itself; either way ignoring is the safe choice.
        File.WriteAllText(Path.Combine(_dir, "backup.db.quarantine-not-a-stamp"), "x");
        File.WriteAllText(Path.Combine(_dir, "backup.db.salt.quarantine-not-a-stamp"), "y");

        var (db, salt) = QuarantineFileLocator.FindMostRecentQuarantinePair(_dir);

        Assert.Null(db);
        Assert.Null(salt);
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
