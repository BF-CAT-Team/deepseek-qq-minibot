using Bot.Core.Domain;
using Bot.Core.Memory;

namespace Bot.Core.Runtime;

public sealed record CostSummary(
    int Requests,
    long PromptTokens,
    long CacheHitTokens,
    long CacheMissTokens,
    long CompletionTokens,
    double PrefixHitRatio);

/// <summary>
/// request_log 的读写。这是成本与缓存分析的<b>唯一事实来源</b>（文档 6.4 / 8.1）：
/// 看板上的数字一律由日志实算，不引用文档里的示例权重。
/// </summary>
public sealed class RequestLogStore
{
    private readonly Db _db;

    public RequestLogStore(Db db) => _db = db;

    /// <summary>发送前先写"意图"，幂等键 = session_id + seq，崩溃后据此判断不重发。</summary>
    public void Insert(RequestLogRow row)
    {
        _db.Exec(
            """
            INSERT OR REPLACE INTO request_log(
              request_id, session_id, chat_id, seq, kind, model, sent_at, prefix_hash, prefix_tokens,
              history_tokens, tail_tokens, prompt_tokens, cache_hit_tokens, cache_miss_tokens,
              completion_tokens, latency_ms, parse_ok, retry_of, idem_key, send_confirmed, error_code)
            VALUES(@rid, @sid, @cid, @seq, @kind, @model, @sent, @ph, @pt, @ht, @tt, @prt, @hit, @miss,
                   @comp, @lat, @parse, @retry, @idem, 0, @err)
            """,
            ("@rid", row.RequestId),
            ("@sid", row.SessionId),
            ("@cid", row.ChatId),
            ("@seq", row.Seq),
            ("@kind", row.Kind),
            ("@model", row.Model),
            ("@sent", row.SentAt),
            ("@ph", row.PrefixHash),
            ("@pt", row.PrefixTokens),
            ("@ht", row.HistoryTokens),
            ("@tt", row.TailTokens),
            ("@prt", row.PromptTokens),
            ("@hit", row.CacheHitTokens),
            ("@miss", row.CacheMissTokens),
            ("@comp", row.CompletionTokens),
            ("@lat", row.LatencyMs),
            ("@parse", row.ParseOk ? 1 : 0),
            ("@retry", row.RetryOf),
            ("@idem", row.IdemKey),
            ("@err", row.ErrorCode));
    }

    public void MarkConfirmed(string requestId, int completionTokens, int promptTokens, int cacheHit, int cacheMiss)
    {
        _db.Exec(
            """
            UPDATE request_log SET send_confirmed=1, completion_tokens=@c, prompt_tokens=@p,
                   cache_hit_tokens=@hit, cache_miss_tokens=@miss
            WHERE request_id=@id
            """,
            ("@c", completionTokens),
            ("@p", promptTokens),
            ("@hit", cacheHit),
            ("@miss", cacheMiss),
            ("@id", requestId));
    }

    public RequestLogRow? GetByIdemKey(string idemKey) =>
        _db.QueryOne(
            """
            SELECT request_id, session_id, chat_id, seq, kind, model, sent_at, prefix_hash, prefix_tokens,
                   history_tokens, tail_tokens, prompt_tokens, cache_hit_tokens, cache_miss_tokens,
                   completion_tokens, latency_ms, parse_ok, retry_of, idem_key, send_confirmed, error_code
            FROM request_log WHERE idem_key=@k
            """,
            MapRow,
            ("@k", idemKey));

    public IReadOnlyList<RequestLogRow> Recent(string chatId, int limit = 100) =>
        _db.Query(
            """
            SELECT request_id, session_id, chat_id, seq, kind, model, sent_at, prefix_hash, prefix_tokens,
                   history_tokens, tail_tokens, prompt_tokens, cache_hit_tokens, cache_miss_tokens,
                   completion_tokens, latency_ms, parse_ok, retry_of, idem_key, send_confirmed, error_code
            FROM request_log WHERE chat_id=@c ORDER BY sent_at DESC LIMIT @n
            """,
            MapRow,
            ("@c", chatId),
            ("@n", limit));

    public CostSummary Summarize(string? chatId, long sinceUnix)
    {
        var where = chatId is null ? "sent_at >= @since" : "chat_id=@c AND sent_at >= @since";
        var args = chatId is null
            ? new (string, object?)[] { ("@since", sinceUnix) }
            : new (string, object?)[] { ("@c", chatId), ("@since", sinceUnix) };

        var row = _db.Query(
            $"""
             SELECT COUNT(*), COALESCE(SUM(prompt_tokens),0), COALESCE(SUM(cache_hit_tokens),0),
                    COALESCE(SUM(cache_miss_tokens),0), COALESCE(SUM(completion_tokens),0)
             FROM request_log WHERE {where}
             """,
            reader => new
            {
                Count = reader.GetInt32(0),
                Prompt = reader.GetInt64(1),
                Hit = reader.GetInt64(2),
                Miss = reader.GetInt64(3),
                Completion = reader.GetInt64(4),
            },
            args);

        if (row.Count == 0 || row.Count == 1 && row[0].Count == 0)
        {
            return new CostSummary(0, 0, 0, 0, 0, 0);
        }

        var item = row[0];
        var total = item.Hit + item.Miss;
        return new CostSummary(
            item.Count,
            item.Prompt,
            item.Hit,
            item.Miss,
            item.Completion,
            total > 0 ? item.Hit / (double)total : 0);
    }

    private static RequestLogRow MapRow(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt32(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetInt64(6),
        reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetInt32(8),
        reader.IsDBNull(9) ? null : reader.GetInt32(9),
        reader.IsDBNull(10) ? null : reader.GetInt32(10),
        reader.IsDBNull(11) ? null : reader.GetInt32(11),
        reader.IsDBNull(12) ? null : reader.GetInt32(12),
        reader.IsDBNull(13) ? null : reader.GetInt32(13),
        reader.IsDBNull(14) ? null : reader.GetInt32(14),
        reader.IsDBNull(15) ? null : reader.GetInt32(15),
        reader.GetInt64(16) == 1,
        reader.IsDBNull(17) ? null : reader.GetString(17),
        reader.IsDBNull(18) ? null : reader.GetString(18),
        reader.GetInt64(19) == 1,
        reader.IsDBNull(20) ? null : reader.GetString(20));
}

public sealed record WalItem(long Id, string SessionId, string Kind, string Payload, int Retries);

/// <summary>
/// 崩溃恢复队列（文档 3.6 / 3.7.1）。遵守一条铁律：<b>已生成未确认的消息不重发</b>，
/// 宁可少一条回复，也不能把同一句话发两遍。
/// </summary>
public sealed class WalStore
{
    public const string KindSummary = "summary";
    public const string KindSend = "send";
    public const string KindMemoryWrite = "memory_write";
    public const string KindSummaryRaw = "summary_raw";

    private readonly Db _db;

    public WalStore(Db db) => _db = db;

    public long Add(string sessionId, string kind, string payload) =>
        _db.ScalarLong(
            "INSERT INTO wal_pending(session_id, kind, payload, created_at, retries) VALUES(@s, @k, @p, @t, 0) RETURNING id",
            ("@s", sessionId),
            ("@k", kind),
            ("@p", payload),
            ("@t", TimeUtil.NowUnix()));

    public IReadOnlyList<WalItem> Pending(int limit = 50) =>
        _db.Query(
            "SELECT id, session_id, kind, payload, retries FROM wal_pending ORDER BY id LIMIT @n",
            reader => new WalItem(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)),
            ("@n", limit));

    public void Remove(long id) => _db.Exec("DELETE FROM wal_pending WHERE id=@id", ("@id", id));

    public void Fail(long id, string error) =>
        _db.Exec("UPDATE wal_pending SET retries=retries+1, last_error=@e WHERE id=@id",
            ("@e", error.Length > 500 ? error[..500] : error),
            ("@id", id));

    public int Count() => (int)_db.ScalarLong("SELECT COUNT(*) FROM wal_pending");
}
