namespace Bot.Core.Domain;

/// <summary>聊天场所类型。QQ 群用 Group，好友/单聊用 Direct。</summary>
public enum ChatKind
{
    Group = 0,
    Direct = 1,
}

public enum TriggerSource
{
    None = 0,
    Mention = 1,
    Keyword = 2,
    Question = 3,
    Schedule = 4,
    Manual = 5,

    /// <summary>有人叫了机器人的名字（"小助手在吗"），与 @ 同级。</summary>
    Name = 6,

    /// <summary>会话激活后按节拍主动接话（把这段时间的群聊汇总进来回一次）。</summary>
    Cadence = 7,
}

/// <summary>文档 3.1.2 的三态决策：必回 / 可回 / 不回。</summary>
public enum ReplyDecision
{
    Never = 0,
    May = 1,
    Must = 2,
}

public enum SessionState
{
    Idle = 0,
    Active = 1,
    Summarizing = 2,
    Closed = 3,
}

public enum RotationReason
{
    None = 0,
    Timeout = 1,
    Drift = 2,
    Cap = 3,
    Error = 4,
    Manual = 5,
}

public enum TurnKind
{
    /// <summary>正常的会话历史（append-only）。</summary>
    History = 0,

    /// <summary>动态尾部块 + 最新消息（本轮追加）。</summary>
    DynamicTail = 1,

    /// <summary>轮换时的总结指令，追加在末尾。</summary>
    SummaryPrompt = 2,

    /// <summary>
    /// 会话进行中被"旁听"进来的群聊。它属于对话内容，但不计轮次——
    /// 否则一个话多的群会在几分钟内把 session_max_turns 撑满、频繁轮换。
    /// </summary>
    Ambient = 3,
}

/// <summary>适配器送入的一条入站消息（已归一化为内部模型）。</summary>
public sealed record IncomingMessage
{
    public required string ChatId { get; init; }

    public ChatKind Kind { get; init; } = ChatKind.Group;

    /// <summary>群事件为 member_openid，单聊为 user_openid。永不进 prompt。</summary>
    public required string OpenId { get; init; }

    /// <summary>跨群统一标识，若平台下发则优先用它与 person 表对齐。</summary>
    public string? UnionOpenId { get; init; }

    /// <summary>平台一般不下发昵称，保留字段以便 Mock / 手工指令注入。</summary>
    public string? SenderName { get; init; }

    public required string Content { get; init; }

    public bool MentionsBot { get; init; }

    public string? ReplyToMessageId { get; init; }

    public string? MessageId { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public string? ChatDisplayName { get; init; }

    /// <summary>机器人自己发的消息，用于冷却与"不抢话"判断。</summary>
    public bool IsSelf { get; init; }

    /// <summary>平台原始事件名（GROUP_MESSAGE_CREATE / GROUP_AT_MESSAGE_CREATE / …），仅用于诊断与能力推断。</summary>
    public string? EventType { get; init; }
}

public sealed record SessionTurn(string Role, string Content, TurnKind Kind = TurnKind.History)
{
    public bool IsUser => Role == "user";
}

/// <summary>
/// 会话。生命周期内<b>只追加</b>，前缀一旦冻结就逐字节不变（文档 1.3）。
/// 线程模型：同一 chat 的会话只允许在其 Actor 内访问。
/// </summary>
public sealed class Session
{
    public Session(string chatId, DateTimeOffset startedAt, string sessionId)
    {
        ChatId = chatId;
        StartedAt = startedAt;
        SessionId = sessionId;
    }

    public string SessionId { get; }

    public string ChatId { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? EndedAt { get; private set; }

    public RotationReason EndReason { get; private set; } = RotationReason.None;

    public SessionState State { get; private set; } = SessionState.Idle;

    public List<SessionTurn> Turns { get; } = new();

    /// <summary>前缀冻结副本：会话内不变，用于哈希复核与审计。</summary>
    public string? PrefixText { get; private set; }

    public string? PrefixHash { get; private set; }

    public int PrefixTokens { get; private set; }

    public long TokenIn { get; private set; }

    public long TokenCached { get; private set; }

    public long TokenOut { get; private set; }

    /// <summary>本轮对话对象（person_id），轮换时作为弱信号。</summary>
    public string? ReplyTarget { get; set; }

    public DateTimeOffset LastActivityAt { get; private set; } = DateTimeOffset.MinValue;

    public int TurnCount { get; private set; }

    public bool HasPendingMessages => _pending.Count > 0;

    private readonly List<IncomingMessage> _pending = new();

    public IReadOnlyList<IncomingMessage> Pending => _pending;

    public bool HasLiveContext => PrefixText is not null && State is SessionState.Active or SessionState.Summarizing;

    public void Activate()
    {
        if (State is SessionState.Idle or SessionState.Closed)
        {
            State = SessionState.Active;
        }
    }

    public void FreezePrefix(string text, string hash, int tokens)
    {
        // 铁律：冻结后不得再改。此处只允许一次。
        if (PrefixText is not null && !string.Equals(PrefixText, text, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("前缀已冻结，禁止改写（文档 1.3 缓存铁律）。");
        }

        PrefixText = text;
        PrefixHash = hash;
        PrefixTokens = tokens;
    }

    public void AppendTurn(SessionTurn turn)
    {
        if (State == SessionState.Closed)
        {
            throw new InvalidOperationException("会话已关闭，不能追加。");
        }

        Turns.Add(turn);
        if (turn.Kind == TurnKind.History)
        {
            TurnCount++;
        }
    }

    public void Touch(DateTimeOffset at) => LastActivityAt = at == default ? DateTimeOffset.UtcNow : at;

    public void EnqueuePending(IncomingMessage message) => _pending.Add(message);

    public IReadOnlyList<IncomingMessage> DrainPending()
    {
        var copy = _pending.ToArray();
        _pending.Clear();
        return copy;
    }

    public bool TryTakePending(out IncomingMessage message)
    {
        if (_pending.Count == 0)
        {
            message = default!;
            return false;
        }

        message = _pending[0];
        _pending.RemoveAt(0);
        return true;
    }

    public void RecordUsage(long input, long cached, long output)
    {
        TokenIn += input;
        TokenCached += cached;
        TokenOut += output;
    }

    public void BeginSummarizing() => State = SessionState.Summarizing;

    public void Close(RotationReason reason, DateTimeOffset at)
    {
        State = SessionState.Closed;
        EndReason = reason;
        EndedAt = at;
    }

    /// <summary>轮换提交后释放上下文（只清内存，不删库）。</summary>
    public void ReleaseContext()
    {
        Turns.Clear();
        PrefixText = null;
        PrefixHash = null;
        PrefixTokens = 0;
    }
}
