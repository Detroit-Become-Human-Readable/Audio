using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace DetroitAudio.Tests;

public sealed class SqliteNativeDependencyTests
{
    private readonly ITestOutputHelper output;

    public SqliteNativeDependencyTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void BundledNativeSqliteIsPatchedAndIncludesFts5()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT sqlite_version(), sqlite_source_id()";
        using var reader = versionCommand.ExecuteReader();
        Assert.True(reader.Read());
        var version = Version.Parse(reader.GetString(0));
        var sourceId = reader.GetString(1);
        output.WriteLine($"Native SQLite {version}; source id: {sourceId}");

        Assert.True(version >= new Version(3, 50, 2), $"Native SQLite {version} is below 3.50.2 (source id: {sourceId}).");
        Assert.False(string.IsNullOrWhiteSpace(sourceId));

        using var ftsCommand = connection.CreateCommand();
        ftsCommand.CommandText = "CREATE VIRTUAL TABLE documents USING fts5(content); INSERT INTO documents VALUES ('native SQLite works'); SELECT count(*) FROM documents WHERE documents MATCH 'SQLite';";
        Assert.Equal(1L, ftsCommand.ExecuteScalar());
    }
}
