using Bot.Core.Domain;
using Bot.Core.Memory;
using Bot.Core.Text;

namespace Bot.Core.Runtime;

/// <summary>会话里的一条 API 消息。role 就是发给模型的 <c>system</c> / <c>user</c> / <c>assistant</c>。</summary>
public sealed record SessionTurnView(
    int Seq,
    string Role,
    string Kind,
    int Tokens,
    string Content);

/// <summary>一次模型调用（来自 request_log）；缓存命中与延迟都是真实返回值。</summary>
public sealed record SessionRequestView(
    string RequestId,
    string Kind,
    string Model,
    long SentAt,
    int? PromptTokens,
    int? CacheHitTokens,
    int? CacheMissTokens,
    int? CompletionTokens,
    int? LatencyMs,
    string? ErrorCode,
    bool ParseOk,
    bool SendConfirmed);

/// <summary>
/// 会话的轻量索引（会话列表用）：不读历史消息，只回答"这个聊天上一次/当前的会话是哪条"。
/// 会话结束后 <c>state.Session</c> 会被释放，配置页靠它仍然能点回上一段对话。
/// </summary>
public sealed record SessionBrief(
    string SessionId,
    string ChatId,
    int TurnCount,
    string PrefixHash,
    long StartedAt,
    long? EndedAt,
    string State)
{
    /// <summary>会话最近一次活动时间：结束时间优先，未结束则用开始时间。</summary>
    public long LastAt => EndedAt ?? StartedAt;
}

/// <summary>总结请求里的一条 message（与真正发给模型的 role / content 一致）。</summary>
public sealed record SessionSummaryMessageView(string Role, string Content, int Tokens);

/// <summary>
/// 一次会话结束时做的总结：<b>发给模型的请求原文 + 模型返回的总结原文 + 解析/入库结果</b>。
/// <para>
/// 只靠 request_log 只能看到一行 <c>summary</c> 的用量，看不到总结到底说了什么；
/// 用户点进会话时最容易产生的疑问正好是"它到底记住了什么"，所以这份记录必须能回看。
/// 记录从 v1.3 起才有，老会话在这里是 <c>null</c>。
/// </para>
/// </summary>
public sealed record SessionSummaryView(
    string RequestId,
    string Model,
    long CreatedAt,
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
    IReadOnlyList<SessionSummaryMessageView> Messages);

/// <summary>
/// 一个会话的"API 侧"视图：<b>发给模型的 messages 长什么样</b>。
/// <para>
/// 这不是聊天记录，而是拼装结果：system = 逐字节冻结的前缀区，user = 当轮动态尾部，
/// assistant = 机器人的回复；<c>kind</c> 标明这条是历史、动态尾部还是旁听进来的群聊。
/// 用户点进"当前会话"时看到的就是它，因此排查"它为什么这么回"不用再去翻日志。
/// </para>
/// </summary>
public sealed record SessionView(
    string SessionId,
    string ChatId,
    string? ChatName,
    string Kind,
    string State,
    long StartedAt,
    long? EndedAt,
    string EndReason,
    int TurnCount,
    string PrefixHash,
    string PrefixText,
    int PrefixTokens,
    long TokenIn,
    long TokenCached,
    long TokenOut,
    IReadOnlyList<SessionTurnView> Turns,
    IReadOnlyList<SessionRequestView> Requests)
{
    /// <summary>运行时状态（idle/active/summarizing/closed）。会话不在内存里时为 null。</summary>
    public string? LiveState { get; init; }

    /// <summary>会话结束时的总结；未总结 / v1.3 之前的老会话为 null。</summary>
    public SessionSummaryView? Summary { get; init; }
}

/// <summary>
/// 只读的会话检查器。数据全部来自库：<c>session.prefix_text</c> 是冻结的前缀副本，
/// <c>session_turn</c> 是严格 append-only 的历史，所以重启后、会话结束后仍然能看到当时发给 API 的内容。
/// </summary>
public sealed class SessionInspector
{
    private readonly Db _db;
    private readonly SessionManager _sessions;
    private readonly RequestLogStore _requests;
    private readonly SummaryRecordStore _summaryRecords;
    private readonly TokenEstimator _estimator;

    public SessionInspector(Db db, SessionManager sessions, RequestLogStore requests, TokenEstimator estimator)
    {
        _db = db;
        _sessions = sessions;
        _requests = requests;
        _summaryRecords = new SummaryRecordStore(db);
        _estimator = estimator;
    }

    /// <summary>某个群/单聊最近的会话：优先进行中的，其次最近结束的。</summary>
    public SessionView? FindLatest(string chatId)
    {
        var sessionId = FindLatestBrief(chatId)?.SessionId;
        return string.IsNullOrEmpty(sessionId) ? null : Get(sessionId);
    }

    /// <summary>
    /// 会话的轻量索引：会话列表每 5s 轮询一次，不能为此把整段历史都读出来。
    /// 会话关闭后这条记录仍然在库里，所以"空闲"的聊天还能点回上一段对话。
    /// </summary>
    public SessionBrief? FindLatestBrief(string chatId) =>
        _db.QueryOne(
            """
            SELECT s.session_id, s.chat_id, s.turn_count, COALESCE(s.prefix_hash, ''),
                   s.started_at, s.ended_at
            FROM session s WHERE s.chat_id=@c
            ORDER BY (s.ended_at IS NULL) DESC, s.started_at DESC LIMIT 1
            """,
            reader => new SessionBrief(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(5) ? "active" : "closed"),
            ("@c", chatId));

    public SessionView? Get(string sessionId)
    {
        var row = _db.QueryOne(
            """
            SELECT s.session_id, s.chat_id, s.started_at, s.ended_at, COALESCE(s.end_reason, ''),
                   s.turn_count, COALESCE(s.prefix_hash, ''), COALESCE(s.prefix_text, ''),
                   s.token_in, s.token_cached, s.token_out,
                   COALESCE(c.display_name, ''), COALESCE(c.kind, '')
            FROM session s LEFT JOIN chat c ON c.chat_id = s.chat_id
            WHERE s.session_id = @id
            """,
            reader => new
            {
                SessionId = reader.GetString(0),
                ChatId = reader.GetString(1),
                StartedAt = reader.GetInt64(2),
                EndedAt = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3),
                EndReason = reader.GetString(4),
                TurnCount = reader.GetInt32(5),
                PrefixHash = reader.GetString(6),
                PrefixText = reader.GetString(7),
                TokenIn = reader.GetInt64(8),
                TokenCached = reader.GetInt64(9),
                TokenOut = reader.GetInt64(10),
                ChatName = reader.GetString(11),
                Kind = reader.GetString(12),
            },
            ("@id", sessionId));

        if (row is null)
        {
            return null;
        }

        var turns = _sessions.LoadTurns(sessionId)
            .Select((turn, index) => new SessionTurnView(
                index + 1,
                turn.Role,
                KindName(turn.Kind),
                _estimator.EstimateMessages(new[] { turn.Content }),
                turn.Content))
            .ToArray();

        var requests = _requests.RecentBySession(sessionId)
            .Select(r => new SessionRequestView(
                r.RequestId,
                r.Kind,
                r.Model,
                r.SentAt,
                r.PromptTokens,
                r.CacheHitTokens,
                r.CacheMissTokens,
                r.CompletionTokens,
                r.LatencyMs,
                r.ErrorCode,
                r.ParseOk,
                r.SendConfirmed))
            .ToArray();

        var state = row.EndedAt is null ? "active" : "closed";
        var kind = string.IsNullOrEmpty(row.Kind) ? ChatKind.Group.ToString().ToLowerInvariant() : row.Kind;

        var summary = BuildSummary(sessionId);

        return new SessionView(
            row.SessionId,
            row.ChatId,
            string.IsNullOrEmpty(row.ChatName) ? null : row.ChatName,
            kind,
            state,
            row.StartedAt,
            row.EndedAt,
            row.EndReason,
            row.TurnCount,
            row.PrefixHash,
            row.PrefixText,
            string.IsNullOrEmpty(row.PrefixText) ? 0 : _estimator.EstimateMessages(new[] { row.PrefixText }),
            row.TokenIn,
            row.TokenCached,
            row.TokenOut,
            turns,
            requests)
        {
            Summary = summary,
        };
    }

    private SessionSummaryView? BuildSummary(string sessionId)
    {
        var record = _summaryRecords.Get(sessionId);
        if (record is null)
        {
            return null;
        }

        return new SessionSummaryView(
            record.RequestId,
            record.Model,
            record.CreatedAt,
            record.Attempts,
            record.Reply,
            record.ParseOk,
            record.IsNone,
            record.ErrorCode,
            record.Applied,
            record.Skipped,
            record.BatchId,
            record.Warnings,
            record.AppliedLines,
            record.Messages
                .Select(m => new SessionSummaryMessageView(m.Role, m.Content,
                    string.IsNullOrEmpty(m.Content) ? 0 : _estimator.EstimateMessages(new[] { m.Content })))
                .ToArray());
    }

    /// <summary>前端用的 snake_case 名称（<c>TurnKind.ToString()</c> 会给出 Dynamictail）。</summary>
    private static string KindName(TurnKind kind) => kind switch
    {
        TurnKind.DynamicTail => "dynamic_tail",
        TurnKind.SummaryPrompt => "summary_prompt",
        TurnKind.Ambient => "ambient",
        _ => "history",
    };
}
