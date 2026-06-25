using SecureCloudBackup.Core.Services.Backends;
using Xunit;

namespace SecureCloudBackup.Tests;

/// <summary>
/// B45: pin the whitelist behaviour of <see cref="DatabaseRepairClassifier"/>.
///
/// <para>
/// The classifier is the load-bearing safety check that decides whether
/// the Attempt Repair button is allowed to run REINDEX. Anything that
/// widens the "repairable" set risks running REINDEX against damage it
/// cannot fix, which would be slow but harmless; anything that narrows
/// it silently risks leaving the user with an "unrepairable" message
/// when REINDEX would actually have helped. Both directions deserve
/// regression coverage on the exact message strings SQLite emits.
/// </para>
/// </summary>
public class DatabaseRepairClassifierTests
{
    [Fact]
    public void TryClassify_WrongEntryCountInIndex_IsRepairableAndCapturesIndexName()
    {
        var input = new[] { "wrong # of entries in index sqlite_autoindex_integrity_check_runs_1" };

        var result = DatabaseRepairClassifier.TryClassify(input);

        Assert.True(result.AllRepairable);
        Assert.Equal(new[] { "sqlite_autoindex_integrity_check_runs_1" }, result.RepairableIndexNames);
        Assert.Empty(result.UnrepairableMessages);
    }

    [Fact]
    public void TryClassify_RowMissingFromIndex_IsRepairableAndCapturesIndexName()
    {
        var input = new[] { "row 42 missing from index ix_backed_up_files_path" };

        var result = DatabaseRepairClassifier.TryClassify(input);

        Assert.True(result.AllRepairable);
        Assert.Equal(new[] { "ix_backed_up_files_path" }, result.RepairableIndexNames);
    }

    [Fact]
    public void TryClassify_NonUniqueEntryInIndex_IsRepairableAndCapturesIndexName()
    {
        var input = new[] { "non-unique entry in index ix_chunk_index_hash" };

        var result = DatabaseRepairClassifier.TryClassify(input);

        Assert.True(result.AllRepairable);
        Assert.Equal(new[] { "ix_chunk_index_hash" }, result.RepairableIndexNames);
    }

    [Fact]
    public void TryClassify_LiteralOkRow_IsIgnoredNotClassified()
    {
        var input = new[] { "ok" };

        var result = DatabaseRepairClassifier.TryClassify(input);

        Assert.True(result.AllRepairable);
        Assert.Empty(result.RepairableIndexNames);
        Assert.Empty(result.UnrepairableMessages);
    }

    [Fact]
    public void TryClassify_PageLevelDamage_IsNotRepairable()
    {
        // Page-level damage is what cipher_integrity_check is supposed
        // to catch; if integrity_check reports it the structure of the
        // page itself is wrong and REINDEX cannot reconstruct it.
        var input = new[] { "Page 12: btreeInitPage() returns error code 11" };

        var result = DatabaseRepairClassifier.TryClassify(input);

        Assert.False(result.AllRepairable);
        Assert.Empty(result.RepairableIndexNames);
        Assert.Single(result.UnrepairableMessages);
    }

    [Fact]
    public void TryClassify_FreelistDamage_IsNotRepairable()
    {
        var input = new[] { "freelist count wrong in pointer map page 7" };

        var result = DatabaseRepairClassifier.TryClassify(input);

        Assert.False(result.AllRepairable);
    }

    [Fact]
    public void TryClassify_UnknownMessage_IsNotRepairable()
    {
        // A future SQLite version could change message text. We must
        // refuse rather than guess. This pins the conservative default.
        var input = new[] { "something the classifier has never seen" };

        var result = DatabaseRepairClassifier.TryClassify(input);

        Assert.False(result.AllRepairable);
        Assert.Empty(result.RepairableIndexNames);
        Assert.Single(result.UnrepairableMessages);
    }

    [Fact]
    public void TryClassify_MixedRepairableAndUnrepairable_IsNotAllRepairable()
    {
        // A SINGLE unrepairable line poisons the whole repair attempt;
        // we do not partial-repair on a mixed result because the
        // unrepairable damage might be the underlying cause of the
        // repairable damage.
        var input = new[]
        {
            "wrong # of entries in index sqlite_autoindex_integrity_check_runs_1",
            "Page 12: btreeInitPage() returns error code 11",
        };

        var result = DatabaseRepairClassifier.TryClassify(input);

        Assert.False(result.AllRepairable);
        // We still capture the repairable name so the UI can show what
        // we WOULD have repaired, but AllRepairable=false means the
        // repair will refuse.
        Assert.Single(result.RepairableIndexNames);
        Assert.Single(result.UnrepairableMessages);
    }

    [Fact]
    public void TryClassify_DuplicateRepairableEntries_AreDeduplicated()
    {
        // PRAGMA integrity_check can report the same index multiple
        // times (e.g. two distinct missing rows). We must REINDEX it
        // once, not N times.
        var input = new[]
        {
            "row 1 missing from index ix_backed_up_files_path",
            "row 7 missing from index ix_backed_up_files_path",
            "wrong # of entries in index ix_backed_up_files_path",
        };

        var result = DatabaseRepairClassifier.TryClassify(input);

        Assert.True(result.AllRepairable);
        Assert.Single(result.RepairableIndexNames);
        Assert.Equal("ix_backed_up_files_path", result.RepairableIndexNames[0]);
    }

    [Fact]
    public void TryClassify_EmptyInput_IsAllRepairableTrivially()
    {
        // Defensive: caller should never call us on an empty list, but
        // if they do we report nothing-to-do rather than throwing.
        var result = DatabaseRepairClassifier.TryClassify(System.Array.Empty<string>());

        Assert.True(result.AllRepairable);
        Assert.Empty(result.RepairableIndexNames);
        Assert.Empty(result.UnrepairableMessages);
    }

    [Fact]
    public void TryClassify_WhitespaceAndCaseVariants_AreToleratedOnKnownPatterns()
    {
        // SQLite's actual output is lowercase; the regexes are
        // IgnoreCase as defence-in-depth in case a future version
        // capitalises a word. Whitespace at the ends is trimmed.
        var input = new[]
        {
            "  wrong # of entries in index ix_a  ",
            "ROW 3 MISSING FROM INDEX ix_b",
        };

        var result = DatabaseRepairClassifier.TryClassify(input);

        Assert.True(result.AllRepairable);
        Assert.Equal(new[] { "ix_a", "ix_b" }, result.RepairableIndexNames);
    }
}
