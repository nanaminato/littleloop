using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MySqlConnector;

sealed partial class ChatStore
{
    private static bool ParseProvider(string value) => value.ToLowerInvariant() switch
    {
        "sqlite" => true, "mysql" => false,
        _ => throw new ArgumentException("ChatDatabase:Provider must be MySql or Sqlite.")
    };

    private static string SqliteSettings(string connection)
    {
        var settings = new SqliteConnectionStringBuilder(connection) { ForeignKeys = true, Pooling = false };
        if (settings.Mode == SqliteOpenMode.Memory || settings.DataSource is "" or ":memory:" || settings.Cache == SqliteCacheMode.Shared)
            throw new ArgumentException("Chat SQLite requires a private, local on-disk database; memory and shared-cache modes are not supported.");
        settings.DataSource = Path.GetFullPath(settings.DataSource);
        settings.DefaultTimeout = 15;
        return settings.ConnectionString;
    }

    internal static bool IsDuplicate(DbException error) => error is MySqlException { Number: 1062 }
        || error is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 };

    private static bool IsConstraintOrBusy(DbException error) => error is MySqlException { Number: 1062 or 1213 or 1452 }
        || error is SqliteException { SqliteErrorCode: 5 or 6 or 19 };

    private static void ConfigureSqlite(SqliteConnection db)
    {
        // Existing SQL is fixed application text; all external values remain parameters.
        db.CreateFunction<string?, long, string?>("LEFT_TEXT", (s, n) => s is null ? null : s[..Math.Min(s.Length, (int)Math.Max(0, n))]);
        db.CreateFunction<string?, string?>("JSON_UNQUOTE", s =>
        {
            if (s is null) return null;
            using var value = JsonDocument.Parse(s);
            return value.RootElement.ValueKind == JsonValueKind.String ? value.RootElement.GetString() : s;
        });
        // MySQL returns JSON booleans as true/false, unlike SQLite's integer 1/0.
        // Preserve these semantics for notification and privacy preferences.
        db.CreateFunction<string?, string, string?>("MYSQL_JSON_EXTRACT", (s, path) =>
        {
            if (s is null) return null;
            if (!path.StartsWith("$.", StringComparison.Ordinal)) throw new NotSupportedException("Unsupported Chat JSON path.");
            using var value = JsonDocument.Parse(s);
            var element = value.RootElement;
            foreach (string key in path[2..].Split('.'))
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out element)) return null;
            return element.GetRawText();
        });
        db.CreateFunction<string?, long, string?>("SHA2", (s, bits) => s is null ? null : bits == 256
            ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s))) : throw new NotSupportedException("Only SHA256 is used by Chat."));
        db.CreateFunction<string?, string?, string?, bool>("REGEXP_LIKE", (s, pattern, flags) => s is not null && pattern is not null &&
            Regex.IsMatch(s, pattern, flags == "i" ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromMilliseconds(100)));
    }

    // Deliberately scoped to the SQL used by Chat, not a general SQL parser. New dialect-specific
    // statements must extend this adapter and integration tests. MySQL statements are untouched.
    internal static string SqliteSql(string sql)
    {
        const RegexOptions flags = RegexOptions.IgnoreCase;
        var indexes = new List<string>();
        if (sql.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase) && !sql.TrimEnd().EndsWith(';')) sql += ";";
        sql = Regex.Replace(sql, @"CREATE TABLE IF NOT EXISTS\s+(\w+)\s*\((.*?)\)\s*(?:CHARACTER SET utf8mb4)?;", match =>
        {
            string table = match.Groups[1].Value;
            string body = Regex.Replace(match.Groups[2].Value, @",\s*INDEX\s*(?:([a-zA-Z_][a-zA-Z0-9_]*)\s*)?\(([^)]+)\)", index =>
            {
                var columns = index.Groups[2].Value;
                indexes.Add($"CREATE INDEX IF NOT EXISTS ix_{table}_{columns.Replace(",", "_").Replace(" ", "")} ON {table}({columns});");
                return "";
            }, flags);
            return $"CREATE TABLE IF NOT EXISTS {table} ({body});";
        }, flags | RegexOptions.Singleline);
        sql = Regex.Replace(sql, @"\bBIGINT\s+AUTO_INCREMENT\s+PRIMARY KEY", "INTEGER PRIMARY KEY AUTOINCREMENT", flags);
        sql = Regex.Replace(sql, @"\s+CHARACTER SET\s+\w+(?:\s+COLLATE\s+\w+)?", "", flags);
        sql = Regex.Replace(sql, @"\b(?:UTC_TIMESTAMP|CURRENT_TIMESTAMP)\((?:6)?\)\s*\+\s*INTERVAL\s+(\d+)\s+(MINUTE|SECOND|DAY)\b", "strftime('%Y-%m-%d %H:%M:%f','now','+$1 $2')", flags);
        sql = Regex.Replace(sql, @"\b(?:UTC_TIMESTAMP|CURRENT_TIMESTAMP)\((?:6)?\)", "(strftime('%Y-%m-%d %H:%M:%f','now'))", flags);
        sql = Regex.Replace(sql, @"\bFOR (?:UPDATE(?: SKIP LOCKED)?|SHARE)\b", "", flags);
        sql = Regex.Replace(sql, @"\bINSERT IGNORE\b", "INSERT OR IGNORE", flags);
        sql = Regex.Replace(sql, @"\bON DUPLICATE KEY UPDATE\b", "ON CONFLICT DO UPDATE SET", flags);
        sql = Regex.Replace(sql, @"\bVALUES\((\w+)\)", "excluded.$1", flags);
        sql = Regex.Replace(sql, @"\bBINARY\s+", "", flags);
        sql = Regex.Replace(sql, @"\bJSON_EXTRACT\(", "MYSQL_JSON_EXTRACT(", flags);
        sql = Regex.Replace(sql, @"\bIF\(", "IIF(", flags);
        sql = Regex.Replace(sql, @"\bLEFT\(", "LEFT_TEXT(", flags);
        sql = Regex.Replace(sql, @"\bGREATEST\(", "MAX(", flags);
        sql = Regex.Replace(sql, @"\bLEAST\(", "MIN(", flags);
        // SQLite builds do not universally enable DELETE ... LIMIT.
        sql = Regex.Replace(sql, @"DELETE FROM (chat_\w+) WHERE (.*?) LIMIT (\d+)", "DELETE FROM $1 WHERE rowid IN (SELECT rowid FROM $1 WHERE $2 LIMIT $3)", flags);
        return sql + "\n" + string.Join('\n', indexes);
    }

    private FileStream AcquireSqliteLock(string purpose)
    {
        var file = new SqliteConnectionStringBuilder(connectionString).DataSource + "." + purpose + ".lock";
        try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new IOException($"Another Chat process owns the SQLite {purpose} lock. SQLite Chat supports one server process.", e); }
    }
}
