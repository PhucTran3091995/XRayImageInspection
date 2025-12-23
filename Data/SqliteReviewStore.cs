using System.Globalization;
using Microsoft.Data.Sqlite;
using System.IO;

namespace XrayQaApp.Data;

public sealed class SqliteReviewStore
{
    private readonly string _dbPath;

    public SqliteReviewStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XrayQaApp");

        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "bga_qa.db");
    }

    private string ConnectionString => $"Data Source={_dbPath}";

    public void Initialize()
    {
        using var con = new SqliteConnection(ConnectionString);
        con.Open();

        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Reviews (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ReviewedAtUtc TEXT NOT NULL,
    Reviewer TEXT NOT NULL,
    MachineName TEXT NOT NULL,
    MachineIp TEXT NOT NULL,
    RootPath TEXT NOT NULL,
    DateFolder TEXT NOT NULL,
    Model TEXT NOT NULL,
    Filename TEXT NOT NULL,
    FullPath TEXT NOT NULL,
    LastWriteTimeUtc TEXT NOT NULL,
    FileSize INTEGER NOT NULL,
    Decision TEXT NOT NULL,
    DefectType TEXT NOT NULL,
    Comment TEXT
);

CREATE INDEX IF NOT EXISTS IX_Reviews_FileKey
ON Reviews(FullPath, LastWriteTimeUtc, FileSize);
";
        cmd.ExecuteNonQuery();
    }

    public bool IsReviewed(string fullPath, DateTime lastWriteUtc, long fileSize)
    {
        using var con = new SqliteConnection(ConnectionString);
        con.Open();

        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT 1
FROM Reviews
WHERE FullPath = $p
  AND LastWriteTimeUtc = $t
  AND FileSize = $s
LIMIT 1;";
        cmd.Parameters.AddWithValue("$p", fullPath);
        cmd.Parameters.AddWithValue("$t", lastWriteUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$s", fileSize);

        using var r = cmd.ExecuteReader();
        return r.Read();
    }
}
