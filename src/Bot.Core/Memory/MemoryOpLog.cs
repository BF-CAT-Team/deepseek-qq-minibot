using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Bot.Core.Memory;

/// <summary>
/// 记忆操作审计（文档 3.4.5 / 3.4.6）：每批写入都留前像，支持按 batch_id 一键回滚。
/// 只有 person / event 两张表参与回滚——关联表随主表级联。
/// </summary>
public sealed class MemoryOpLog
{
    private static readonly Dictionary<string, (string IdColumn, string[] Columns)> Revertible = new(StringComparer.Ordinal)
    {
        ["person"] = ("person_id", new[]
        {
            "person_id", "open_id", "union_open_id", "name", "name_source", "display_label", "personality",
            "evaluation", "memory_text", "importance", "pinned", "status", "chat_scope", "msg_count",
            "first_seen_at", "created_at", "updated_at",
        }),
        ["event"] = ("event_id", new[]
        {
            "event_id", "time_text", "time_norm", "location", "location_label", "detail", "importance", "pinned",
            "status", "last_used_at", "injected_count", "content_hash", "created_at", "archived_at",
        }),
    };

    private readonly Db _db;

    public MemoryOpLog(Db db) => _db = db;

    /// <summary>
    /// 整行快照（列名与数据库一致）。回滚必须能 100% 还原，
    /// 所以前像不能只记"改动的字段"——那样 INSERT OR REPLACE 会触发 NOT NULL 约束。
    /// </summary>
    public static JsonObject PersonSnapshot(PersonRow row) => new()
    {
        ["person_id"] = row.PersonId,
        ["open_id"] = row.OpenId,
        ["union_open_id"] = row.UnionOpenId,
        ["name"] = row.Name,
        ["name_source"] = row.NameSource,
        ["display_label"] = row.DisplayLabel,
        ["personality"] = row.Personality,
        ["evaluation"] = row.Evaluation,
        ["memory_text"] = row.MemoryText,
        ["importance"] = row.Importance,
        ["pinned"] = row.Pinned,
        ["status"] = row.Status,
        ["chat_scope"] = row.ChatScope,
        ["msg_count"] = row.MsgCount,
        ["first_seen_at"] = row.FirstSeenAt,
        ["created_at"] = row.CreatedAt,
        ["updated_at"] = row.UpdatedAt,
    };

    public static JsonObject EventSnapshot(EventRow row) => new()
    {
        ["event_id"] = row.EventId,
        ["time_text"] = row.TimeText,
        ["time_norm"] = row.TimeNorm,
        ["location"] = row.Location,
        ["location_label"] = row.LocationLabel,
        ["detail"] = row.Detail,
        ["importance"] = row.Importance,
        ["pinned"] = row.Pinned,
        ["status"] = row.Status,
        ["last_used_at"] = row.LastUsedAt,
        ["injected_count"] = row.InjectedCount,
        ["content_hash"] = row.ContentHash,
        ["created_at"] = row.CreatedAt,
        ["archived_at"] = row.ArchivedAt,
    };

    public void Append(string batchId, string op, string table, long rowId, object? before, object? after, string source)
    {
        _db.Exec(
            """
            INSERT INTO memory_op_log(batch_id, op, tbl, row_id, before_json, after_json, source, created_at, reverted)
            VALUES(@b, @op, @tbl, @row, @before, @after, @src, @at, 0)
            """,
            ("@b", batchId),
            ("@op", op),
            ("@tbl", table),
            ("@row", rowId),
            ("@before", JsonUtil.ToJson(before)),
            ("@after", JsonUtil.ToJson(after)),
            ("@src", source),
            ("@at", TimeUtil.NowUnix()));
    }

    /// <summary>按 batch_id 回滚。倒序执行，返回实际回滚的条数。</summary>
    public int Revert(string batchId)
    {
        var ops = _db.Query(
            """
            SELECT op_id, op, tbl, row_id, before_json FROM memory_op_log
            WHERE batch_id=@b AND reverted=0 ORDER BY op_id DESC
            """,
            reader => new
            {
                OpId = reader.GetInt64(0),
                Op = reader.GetString(1),
                Table = reader.GetString(2),
                RowId = reader.GetInt64(3),
                Before = reader.IsDBNull(4) ? null : reader.GetString(4),
            },
            ("@b", batchId));

        if (ops.Count == 0)
        {
            return 0;
        }

        return _db.InTransaction((connection, transaction) =>
        {
            var count = 0;
            foreach (var item in ops)
            {
                if (!Revertible.TryGetValue(item.Table, out var schema))
                {
                    continue;
                }

                using var command = connection.CreateCommand();
                command.Transaction = transaction;

                if (string.IsNullOrEmpty(item.Before))
                {
                    // 只有"插入"没有前像 → 回滚即删除。
                    command.CommandText = $"DELETE FROM {item.Table} WHERE {schema.IdColumn}=@id";
                    command.Parameters.AddWithValue("@id", item.RowId);
                    command.ExecuteNonQuery();
                }
                else
                {
                    RestoreRow(connection, transaction, item.Table, schema, item.Before);
                }

                using var mark = connection.CreateCommand();
                mark.Transaction = transaction;
                mark.CommandText = "UPDATE memory_op_log SET reverted=1 WHERE op_id=@id";
                mark.Parameters.AddWithValue("@id", item.OpId);
                mark.ExecuteNonQuery();
                count++;
            }

            return count;
        });
    }

    public IReadOnlyList<(long OpId, string Op, string Table, long RowId, string? Before, string? After, string Source, long At, bool Reverted)>
        ListBatch(string batchId) =>
        _db.Query(
            """
            SELECT op_id, op, tbl, row_id, before_json, after_json, source, created_at, reverted
            FROM memory_op_log WHERE batch_id=@b ORDER BY op_id
            """,
            reader => (
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetInt64(7),
                reader.GetInt64(8) == 1),
            ("@b", batchId));

    private static void RestoreRow(SqliteConnection connection, SqliteTransaction transaction, string table,
        (string IdColumn, string[] Columns) schema, string beforeJson)
    {
        var node = JsonUtil.Parse(beforeJson) as JsonObject;
        if (node is null)
        {
            return;
        }

        // 列名来自白名单，值全部走参数，JSON 键不会进 SQL。
        var columns = schema.Columns.Where(c => node.ContainsKey(c)).ToArray();
        if (columns.Length == 0)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var placeholders = string.Join(", ", columns.Select((_, i) => "@p" + i));
        command.CommandText = $"INSERT OR REPLACE INTO {table} ({string.Join(", ", columns)}) VALUES ({placeholders})";
        for (var i = 0; i < columns.Length; i++)
        {
            command.Parameters.AddWithValue("@p" + i, ToDbValue(node[columns[i]]));
        }

        command.ExecuteNonQuery();
    }

    private static object ToDbValue(JsonNode? node) => node switch
    {
        null => DBNull.Value,
        JsonValue value when value.TryGetValue<bool>(out var b) => b ? 1 : 0,
        JsonValue value when value.TryGetValue<long>(out var l) => l,
        JsonValue value when value.TryGetValue<int>(out var i) => i,
        JsonValue value when value.TryGetValue<double>(out var d) => d,
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };
}
