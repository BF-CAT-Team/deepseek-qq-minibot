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

    public string Name => "mock";

    public AdapterCapabilities Capabilities => AdapterCapabilities.Mock;

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

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _sent.Add(message);
        }

        return Task.CompletedTask;
    }

    /// <summary>模拟一条入站消息（本地调试 / 自动化测试用）。</summary>
    public async Task InjectAsync(IncomingMessage message)
    {
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
