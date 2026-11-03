using System.Text.Json;
using Bot.Core.Memory;

namespace Bot.Core.Runtime;

/// <summary>总结请求里的一条 message（与真正发给模型的 role/content 一致）。</summary>
public sealed record SummaryMessage(string Role, string Content);

/// <summary>
/// 一次会话结束时的总结记录（<c>session_summary</c>）。
/// <para>
/// request_log 只记 token 与延迟，看不到"总结问了什么、回了什么"。这里把总结请求的
/// messages、模型返回原文、解析与入库结果一并落库，配置页的「当前会话」才能像展示
/// 普通对话那样展示总结——否则用户只能看到一行 <c>summary</c> 用量，不知道记住了什么。
/// </para>
/// </summary>
public sealed record SessionSummaryRecord(
    string SessionId,
    string ChatId,
    string RequestId,
    string Model,
    long CreatedAt,
    long UpdatedAt,
    int Attempts,
    string? Reply,
    bool ParseOk,
    bool IsNone,
    string? ErrorCode,
    int Applied,
    int Skipped,
    string? BatchId,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> AppliedLines,
    IReadOnlyList<SummaryMessage> Messages);

/// <summary>session_summary 的读写。一个会话最多一条记录，重复补总结时就地覆盖。</summary>
public sealed class SummaryRecordStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Db _db;

    public SummaryRecordStore(Db db) => _db = db;

    /// <summary>
    /// 请求发出前先落一条"意图"：即使进程在等模型时被杀，页面上也能看到当时发了什么。
    /// 本会话已有记录时整体覆盖（补总结只保留最后一次）。
    /// </summary>
    public void Begin(string sessionId, string chatId, string requestId, string model,
        IReadOnlyList<SummaryMessage> messages, long now)
    {
        _db.Exec(
            """
            INSERT OR REPLACE INTO session_summary(
              session_id, chat_id, request_id, model, created_at, updated_at,
              attempts, reply, parse_ok, is_none, error_code, applied, skipped, batch_id,
              warnings, applied_lines, messages)
            VALUES(@sid, @cid, @rid, @model, @now, @now, 0, NULL, 0, 0, NULL, 0, 0, NULL,
                   '[]', '[]', @messages)
            """,
            ("@sid", sessionId),
            ("@cid", chatId),
            ("@rid", requestId),
            ("@model", model),
            ("@now", now),
            ("@messages", JsonSerializer.Serialize(messages, Json)));
    }

    /// <summary>总结请求本身失败（网络 / 超时 / 空回复）：留下错误码，原文为空。</summary>
    public void Fail(string sessionId, string? errorCode, long now) =>
        _db.Exec(
            "UPDATE session_summary SET error_code=@err, updated_at=@now WHERE session_id=@sid",
            ("@err", string.IsNullOrWhiteSpace(errorCode) ? "E_UNKNOWN" : errorCode),
            ("@now", now),
            ("@sid", sessionId));

    /// <summary>模型返回了内容：记录原文与解析结果。入库统计由 <see cref="MarkApplied"/> 补。</summary>
    public void Complete(string sessionId, string? reply, int attempts, bool parseOk, bool isNone,
        int applied, int skipped, string? batchId, IReadOnlyList<string> warnings,
        IReadOnlyList<string> appliedLines, long now) =>
        _db.Exec(
            """
            UPDATE session_summary
            SET reply=@reply, attempts=@attempts, parse_ok=@ok, is_none=@none, error_code=NULL,
                applied=@applied, skipped=@skipped, batch_id=@batch,
                warnings=@warnings, applied_lines=@lines, updated_at=@now
            WHERE session_id=@sid
            """,
            ("@reply", reply),
            ("@attempts", attempts),
            ("@ok", parseOk ? 1 : 0),
            ("@none", isNone ? 1 : 0),
            ("@applied", applied),
            ("@skipped", skipped),
            ("@batch", batchId),
            ("@warnings", JsonSerializer.Serialize(warnings, Json)),
            ("@lines", JsonSerializer.Serialize(appliedLines, Json)),
            ("@now", now),
            ("@sid", sessionId));

    /// <summary>WAL 重放 / 补入库之后修正统计（记录不存在时什么也不做）。</summary>
    public void MarkApplied(string sessionId, string? batchId, int applied, int skipped,
        IReadOnlyList<string> warnings, IReadOnlyList<string> appliedLines, long now) =>
        _db.Exec(
            """
            UPDATE session_summary
            SET batch_id=@batch, applied=@applied, skipped=@skipped,
                warnings=@warnings, applied_lines=@lines, updated_at=@now
            WHERE session_id=@sid
            """,
            ("@batch", batchId),
            ("@applied", applied),
            ("@skipped", skipped),
            ("@warnings", JsonSerializer.Serialize(warnings, Json)),
            ("@lines", JsonSerializer.Serialize(appliedLines, Json)),
            ("@now", now),
            ("@sid", sessionId));

    public SessionSummaryRecord? Get(string sessionId) =>
        _db.QueryOne(
            """
            SELECT session_id, chat_id, request_id, model, created_at, updated_at, attempts,
                   reply, parse_ok, is_none, error_code, applied, skipped, batch_id,
                   warnings, applied_lines, messages
            FROM session_summary WHERE session_id=@sid
            """,
            reader => new SessionSummaryRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt64(8) == 1,
                reader.GetInt64(9) == 1,
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetInt32(11),
                reader.GetInt32(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                ParseStringList(reader.IsDBNull(14) ? null : reader.GetString(14)),
                ParseStringList(reader.IsDBNull(15) ? null : reader.GetString(15)),
                ParseMessages(reader.IsDBNull(16) ? null : reader.GetString(16))),
            ("@sid", sessionId));

    private static IReadOnlyList<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, Json) ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<SummaryMessage> ParseMessages(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<SummaryMessage>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<SummaryMessage>>(json, Json)
                   ?? (IReadOnlyList<SummaryMessage>)Array.Empty<SummaryMessage>();
        }
        catch (JsonException)
        {
            return Array.Empty<SummaryMessage>();
        }
    }
}
