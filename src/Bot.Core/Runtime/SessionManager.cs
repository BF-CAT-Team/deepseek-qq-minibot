using Bot.Core.Config;
using Bot.Core.Domain;
using Bot.Core.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bot.Core.Runtime;

public sealed record RotationCheck(bool ShouldRotate, RotationReason Reason, string Detail)
{
    public static readonly RotationCheck No = new(false, RotationReason.None, string.Empty);
}

/// <summary>
/// 会话状态机的持久化与轮换判据（文档 3.2 / 3.3）。
/// <para>轮换是唯一的压缩与重建时机：会话内严格 append-only，压缩只在轮换时发生，
/// 那次冷启动本来就要付，压缩的代价被吸收。</para>
/// </summary>
public sealed class SessionManager
{
    private readonly Db _db;
    private readonly IClock _clock;
    private readonly ILogger<SessionManager> _log;

    public SessionManager(Db db, IClock clock, ILogger<SessionManager>? log = null)
    {
        _db = db;
        _clock = clock;
        _log = log ?? NullLogger<SessionManager>.Instance;
    }

    public Session Start(string chatId)
    {
        var session = new Session(chatId, _clock.UtcNow, "s_" + Guid.NewGuid().ToString("N")[..16]);
        session.Touch(_clock.UtcNow);
        _db.Exec(
            "INSERT INTO session(session_id, chat_id, started_at, turn_count) VALUES(@id, @chat, @at, 0)",
            ("@id", session.SessionId),
            ("@chat", chatId),
            ("@at", TimeUtil.Unix(session.StartedAt)));
        return session;
    }

    public void SavePrefix(Session session)
    {
        _db.Exec(
            "UPDATE session SET prefix_hash=@h, prefix_text=@t WHERE session_id=@id",
            ("@h", session.PrefixHash),
            ("@t", session.PrefixText),
            ("@id", session.SessionId));
    }

    public void SaveTurn(Session session, SessionTurn turn, int seq)
    {
        _db.Exec(
            """
            INSERT OR REPLACE INTO session_turn(session_id, seq, role, content, kind)
            VALUES(@id, @seq, @role, @content, @kind)
            """,
            ("@id", session.SessionId),
            ("@seq", seq),
            ("@role", turn.Role),
            ("@content", turn.Content),
            ("@kind", turn.Kind.ToString().ToLowerInvariant()));
    }

    public void SaveProgress(Session session)
    {
        _db.Exec(
            """
            UPDATE session SET turn_count=@turns, token_in=@tin, token_cached=@tc, token_out=@tout, reply_target=@target
            WHERE session_id=@id
            """,
            ("@turns", session.TurnCount),
            ("@tin", session.TokenIn),
            ("@tc", session.TokenCached),
            ("@tout", session.TokenOut),
            ("@target", session.ReplyTarget),
            ("@id", session.SessionId));
    }

    public void MarkClosed(Session session)
    {
        _db.Exec(
            "UPDATE session SET ended_at=@at, end_reason=@reason, turn_count=@turns WHERE session_id=@id",
            ("@at", TimeUtil.Unix(session.EndedAt ?? _clock.UtcNow)),
            ("@reason", session.EndReason.ToString().ToLowerInvariant()),
            ("@turns", session.TurnCount),
            ("@id", session.SessionId));
    }

    /// <summary>重启后恢复：把未结束的会话重新装配成内存对象（含历史回合）。</summary>
    public Session? TryResume(string sessionId)
    {
        var row = _db.QueryOne(
            "SELECT session_id, chat_id, started_at, prefix_hash, prefix_text FROM session WHERE session_id=@id",
            reader => new
            {
                Id = reader.GetString(0),
                ChatId = reader.GetString(1),
                StartedAt = reader.GetInt64(2),
                Hash = reader.IsDBNull(3) ? null : reader.GetString(3),
                Text = reader.IsDBNull(4) ? null : reader.GetString(4),
            },
            ("@id", sessionId));

        if (row is null)
        {
            return null;
        }

        var session = new Session(row.ChatId, TimeUtil.FromUnix(row.StartedAt), row.Id);
        foreach (var turn in LoadTurns(sessionId))
        {
            session.AppendTurn(turn);
        }

        if (row.Text is not null && row.Hash is not null)
        {
            session.FreezePrefix(row.Text, row.Hash, 0);
        }

        session.Activate();
        return session;
    }

    public IReadOnlyList<SessionTurn> LoadTurns(string sessionId) =>
        _db.Query(
            "SELECT role, content, kind FROM session_turn WHERE session_id=@id ORDER BY seq",
            reader => new SessionTurn(
                reader.GetString(0),
                reader.GetString(1),
                Enum.TryParse<TurnKind>(reader.GetString(2), true, out var kind) ? kind : TurnKind.History),
            ("@id", sessionId));

    /// <summary>启动扫描：未结束但已有回合的会话 → 降级为 SUMMARIZING 补总结。</summary>
    public IReadOnlyList<(string SessionId, string ChatId)> ListOrphans() =>
        _db.Query(
            """
            SELECT s.session_id, s.chat_id FROM session s
            WHERE s.ended_at IS NULL
              AND EXISTS (SELECT 1 FROM session_turn t
                          WHERE t.session_id = s.session_id AND t.kind = 'history')
            ORDER BY s.started_at DESC
            """,
            reader => (reader.GetString(0), reader.GetString(1)));

    public Session? LoadLatestOpen(string chatId) =>
        _db.QueryOne(
            "SELECT session_id FROM session WHERE chat_id=@c AND ended_at IS NULL ORDER BY started_at DESC LIMIT 1",
            reader => new { Id = reader.GetString(0) },
            ("@c", chatId)) is { } row
            ? TryResume(row.Id)
            : null;

    public RotationCheck Check(Session session, EffectiveConfig cfg, int historyTokens, DateTimeOffset now)
    {
        if (session.State is SessionState.Idle)
        {
            return RotationCheck.No;
        }

        if (session.TurnCount >= cfg.SessionMaxTurns)
        {
            return new RotationCheck(true, RotationReason.Cap, $"轮次达到 {session.TurnCount}/{cfg.SessionMaxTurns}");
        }

        if (historyTokens >= cfg.EffectiveSessionTokenCap)
        {
            return new RotationCheck(true, RotationReason.Cap,
                $"历史区 {historyTokens} token 超过上限 {cfg.EffectiveSessionTokenCap}");
        }

        var idle = now - session.LastActivityAt;
        if (idle >= TimeSpan.FromMinutes(cfg.SessionTimeoutMin))
        {
            return new RotationCheck(true, RotationReason.Timeout,
                $"空闲 {idle.TotalMinutes:F1} 分钟 ≥ {cfg.SessionTimeoutMin}");
        }

        return RotationCheck.No;
    }
}
