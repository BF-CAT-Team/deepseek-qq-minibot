using Bot.Core.Domain;

namespace Bot.Core.Abstractions;

/// <summary>
/// 要作为一条独立消息发出去的表情包。它不进对话历史、不改 prompt，
/// 只是回复正文发完之后追加的一张图；因此和文本消息共用同一条发送通道。
/// </summary>
public sealed record OutgoingSticker(
    int StickerId,
    string FilePath,
    string ContentType,
    string FileName);

public sealed record OutgoingMessage(
    string ChatId,
    ChatKind Kind,
    string Content,
    string? MentionOpenId = null,
    string? ReplyToMessageId = null,
    string? IdempotencyKey = null,
    OutgoingSticker? Sticker = null);

/// <summary>
/// 适配器能力。QQ 官方机器人只下发 @ 消息，这个标志让触发层知道自己能不能"旁听"，
/// 从而在能力不足时不去伪造关键词触发。
/// </summary>
public sealed record AdapterCapabilities(bool ReceivesAllGroupMessages, bool SupportsDirectMessage = true)
{
    public static readonly AdapterCapabilities OfficialQq = new(ReceivesAllGroupMessages: false);

    public static readonly AdapterCapabilities Mock = new(ReceivesAllGroupMessages: true);
}

/// <summary>
/// 适配器连接状态快照（配置页展示用）。
/// <para>
/// 「已连接」必须是网关真的连着，而不是「进程还活着」：QQ 网关掉线后进程当然还在，
/// 把 <c>connected = true</c> 写死会让用户以为机器人听得见，实际它已经哑了。
/// </para>
/// </summary>
public sealed record AdapterStatus
{
    public string Name { get; init; } = "none";

    /// <summary>是否真的在线（网关 READY 且 socket 打开 / Mock 常驻可用）。</summary>
    public bool Online { get; init; }

    /// <summary>online | connecting | reconnecting | stopped | mock。</summary>
    public string State { get; init; } = "stopped";

    /// <summary>一句人话，直接显示给用户。</summary>
    public string Summary { get; init; } = "未启动";

    /// <summary>当前状态从什么时候开始。</summary>
    public DateTimeOffset? Since { get; init; }

    public DateTimeOffset? LastInboundAt { get; init; }

    public DateTimeOffset? LastOutboundAt { get; init; }

    public long InboundCount { get; init; }

    public long OutboundCount { get; init; }

    /// <summary>断线重连次数（含首次连接失败后的重试）。</summary>
    public int Reconnects { get; init; }

    public string? LastError { get; init; }

    public string? SessionId { get; init; }

    public string? GatewayUrl { get; init; }

    /// <summary>心跳最后一次被服务端 ack 的时间；长期不更新说明连接已经假死。</summary>
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    public bool ReceivesAllGroupMessages { get; init; }

    /// <summary>补充说明（配置要求 vs 实际运行、能力位等）。</summary>
    public string? Detail { get; init; }
}

public interface IChatAdapter : IAsyncDisposable
{
    string Name { get; }

    AdapterCapabilities Capabilities { get; }

    /// <summary>当前连接状态。必须无副作用，配置页每 5s 读一次。</summary>
    AdapterStatus Status { get; }

    /// <summary>适配器收到消息时触发。<b>必须尽快返回</b>，业务处理由运行时排队。</summary>
    event Func<IncomingMessage, Task>? MessageReceived;

    Task StartAsync(CancellationToken cancellationToken);

    Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken);
}
