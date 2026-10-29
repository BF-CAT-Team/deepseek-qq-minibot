using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Bot.Core.Memory;

public sealed record PersonRow(
    int PersonId,
    string OpenId,
    string? UnionOpenId,
    string Name,
    string NameSource,
    string DisplayLabel,
    string Personality,
    string Evaluation,
    string MemoryText,
    int Importance,
    bool Pinned,
    string Status,
    string ChatScope,
    int MsgCount,
    long? FirstSeenAt,
    long CreatedAt,
    long UpdatedAt);

public sealed record EventRow(
    long EventId,
    string TimeText,
    long? TimeNorm,
    string Location,
    string LocationLabel,
    string Detail,
    int Importance,
    bool Pinned,
    string Status,
    long? LastUsedAt,
    int InjectedCount,
    string ContentHash,
    long CreatedAt,
    long? ArchivedAt,
    IReadOnlyList<int> PersonIds,
    IReadOnlyList<string> Keywords);

public sealed record RequestLogRow(
    string RequestId,
    string SessionId,
    string ChatId,
    int Seq,
    string Kind,
    string Model,
    long SentAt,
    string PrefixHash,
    int? PrefixTokens,
    int? HistoryTokens,
    int? TailTokens,
    int? PromptTokens,
    int? CacheHitTokens,
    int? CacheMissTokens,
    int? CompletionTokens,
    int? LatencyMs,
    bool ParseOk,
    string? RetryOf,
    string? IdemKey,
    bool SendConfirmed,
    string? ErrorCode);

/// <summary>
/// SQLite 访问层：WAL + busy_timeout + 外键，连接从池中借还。
/// 文档要求"单群单写线程"，这里由上层 Actor 保证；数据库层靠 WAL 与 busy_timeout 兜并发。
/// </summary>
public sealed class Db : IDisposable
{
    private readonly string _connectionString;

    public Db(string path)
    {
        Path = path;
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 10,
        }.ToString();
    }

    public string Path { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=8000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Initialize()
    {
        using var connection = Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }

        using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = Schema.Ddl;
            ddl.ExecuteNonQuery();
        }

        // FTS5 external content 表首次建库后必须 rebuild 一次（文档 3.6 实现注记）。
        var built = ScalarString("SELECT value FROM schema_meta WHERE key='fts_built'");
        if (built is null)
        {
            Exec("INSERT INTO event_fts(event_fts) VALUES('rebuild')");
            Exec("INSERT OR REPLACE INTO schema_meta(key,value) VALUES('fts_built','1')");
        }

        Exec("INSERT OR REPLACE INTO schema_meta(key,value) VALUES('schema_version',@v)", ("@v", Schema.CurrentVersion.ToString()));
    }

    public int Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, args);
        return command.ExecuteNonQuery();
    }

    public long ScalarLong(string sql, params (string Name, object? Value)[] args)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, args);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    public double ScalarDouble(string sql, params (string Name, object? Value)[] args)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, args);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToDouble(result);
    }

    public string? ScalarString(string sql, params (string Name, object? Value)[] args)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, args);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, args);
        using var reader = command.ExecuteReader();
        var list = new List<T>();
        while (reader.Read())
        {
            list.Add(map(reader));
        }

        return list;
    }

    public T? QueryOne<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
        where T : class
    {
        var list = Query(sql, map, args);
        return list.Count == 0 ? null : list[0];
    }

    public void InTransaction(Action<SqliteConnection, SqliteTransaction> body)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            body(connection, transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public T InTransaction<T>(Func<SqliteConnection, SqliteTransaction, T> body)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            var result = body(connection, transaction);
            transaction.Commit();
            return result;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public static void Bind(SqliteCommand command, params (string Name, object? Value)[] args)
    {
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}

public static class JsonUtil
{
    private static readonly JsonSerializerOptions StorageOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public static string? ToJson(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value, StorageOptions);

    public static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch
        {
            return null;
        }
    }
}
