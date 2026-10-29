using Bot.Core.Domain;

namespace Bot.Core.Abstractions;

public sealed record OutgoingMessage(
    string ChatId,
    ChatKind Kind,
    string Content,
    string? MentionOpenId = null,
    string? ReplyToMessageId = null,
    string? IdempotencyKey = null);

/// <summary>
/// 适配器能力。QQ 官方机器人只下发 @ 消息，这个标志让触发层知道自己能不能"旁听"，
/// 从而在能力不足时不去伪造关键词触发。
/// </summary>
public sealed record AdapterCapabilities(bool ReceivesAllGroupMessages, bool SupportsDirectMessage = true)
{
    public static readonly AdapterCapabilities OfficialQq = new(ReceivesAllGroupMessages: false);

    public static readonly AdapterCapabilities Mock = new(ReceivesAllGroupMessages: true);
}

public interface IChatAdapter : IAsyncDisposable
{
    string Name { get; }

    AdapterCapabilities Capabilities { get; }

    /// <summary>适配器收到消息时触发。<b>必须尽快返回</b>，业务处理由运行时排队。</summary>
    event Func<IncomingMessage, Task>? MessageReceived;

    Task StartAsync(CancellationToken cancellationToken);

    Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken);
}
