using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Option C / C-5: <see cref="DatabaseBackendFactory.ShouldUseSqlite"/>
/// must return <c>true</c> by default (SQLite is the production
/// backend) and honour the <see cref="System.Threading.AsyncLocal{T}"/>
/// override that tests use to opt INTO the legacy LiteDB code path.
///
/// <para>
/// The C-1-era <c>AZBK_USE_SQLITE</c> environment variable was
/// removed in C-5; tests that exercised it (<c>IsTruthy</c> token
/// matrix, <c>EnvVarScope</c> helper) went with it.
/// </para>
/// </summary>
public class DatabaseBackendFactoryTests
{
    [Fact]
    public void ShouldUseSqlite_NoOverride_ReturnsTrue()
    {
        // Belt-and-braces: clear the AsyncLocal override in case a
        // sibling test left one behind (it shouldn't - every scope
        // disposes - but the assertion is too small to be picky).
        DatabaseBackendFactory.SetAsyncLocalOverride(null);

        Assert.True(DatabaseBackendFactory.ShouldUseSqlite(),
            "SQLite must be the production default after C-5.");
    }

    [Fact]
    public void ShouldUseSqlite_WithLiteDbOverride_ReturnsFalse()
    {
        using var _ = new BackendOverrideScope(useSqlite: false);
        Assert.False(DatabaseBackendFactory.ShouldUseSqlite(),
            "Tests that opt into the legacy LiteDB path must see the override honoured.");
    }

    [Fact]
    public void ShouldUseSqlite_WithSqliteOverride_ReturnsTrue()
    {
        using var _ = new BackendOverrideScope(useSqlite: true);
        Assert.True(DatabaseBackendFactory.ShouldUseSqlite());
    }

    [Fact]
    public void ShouldUseSqlite_AfterScopeDisposed_ReturnsToProductionDefault()
    {
        using (var _ = new BackendOverrideScope(useSqlite: false))
        {
            Assert.False(DatabaseBackendFactory.ShouldUseSqlite());
        }
        Assert.True(DatabaseBackendFactory.ShouldUseSqlite(),
            "Disposing the scope must restore the production default (true).");
    }
}