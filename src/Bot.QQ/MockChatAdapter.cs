using Bot.Core.Abstractions;
using Bot.Core.Domain;

namespace Bot.QQ;

/// <summary>
/// 本地模拟适配器：没有 AppId / Secret 也能把整条链路跑通。
/// <para>能力声明为"能收到全量群消息"，因此关键词触发、疑问句触发、漂移检测都能在这里验证；
/// 换成官方适配器后这些路径会自动被网关能力约束降级。</para>
/// </summary>
public sealed class MockChatAdapter : IChatAdapter
{
    private readonly List<OutgoingMessage> _sent = new();
    private readonly object _gate = new();

    private long _inboundCount;
    private long _outboundCount;
    private DateTimeOffset? _lastInboundAt;
    private DateTimeOffset? _lastOutboundAt;
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public string Name => "mock";

    public AdapterCapabilities Capabilities => AdapterCapabilities.Mock;

    public AdapterStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new AdapterStatus
                {
                    Name = Name,
                    Online = true,
                    State = "mock",
                    Summary = "本地模拟适配器：QQ 未接入，回复不会离开这台电脑",
                    Since = _startedAt,
                    LastInboundAt = _lastInboundAt,
                    LastOutboundAt = _lastOutboundAt,
                    InboundCount = _inboundCount,
                    OutboundCount = _outboundCount,
                    Reconnects = 0,
                    ReceivesAllGroupMessages = true,
                    Detail = "用于本地试聊与自动化测试；切到 QQ 官方机器人需要 AppID / AppSecret 并重启程序。",
                };
            }
        }
    }

    public event Func<IncomingMessage, Task>? MessageReceived;

    public IReadOnlyList<OutgoingMessage> Sent
    {
        get
        {
            lock (_gate)
            {
                return _sent.ToArray();
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _startedAt = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    public Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _sent.Add(message);
            _outboundCount++;
            _lastOutboundAt = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    /// <summary>模拟一条入站消息（本地调试 / 自动化测试用）。</summary>
    public async Task InjectAsync(IncomingMessage message)
    {
        lock (_gate)
        {
            _inboundCount++;
            _lastInboundAt = message.Timestamp == default ? DateTimeOffset.UtcNow : message.Timestamp;
        }

        var handler = MessageReceived;
        if (handler is not null)
        {
            await handler(message).ConfigureAwait(false);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _sent.Clear();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
