using System.Collections.Concurrent;
using Bot.Core.Abstractions;
using Bot.Core.Config;
using Bot.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bot.QQ;

/// <summary>官方机器人适配器。能力上只收 @ 消息（群）与全部单聊消息。</summary>
public sealed class QqChatAdapter : IChatAdapter
{
    private readonly QqApi _api;
    private readonly QqGateway _gateway;
    private readonly ILogger<QqChatAdapter> _log;
    private readonly ConcurrentDictionary<string, int> _sequence = new(StringComparer.Ordinal);
    private Task? _run;

    /// <summary>
    /// 是否真的能听到全群。除了配置里的手动开关，收到 GROUP_MESSAGE_CREATE 也等于自己承认了
    /// ——平台只有在群主开启「接收所有消息」后才会推这个事件，不需要用户再勾一次。
    /// 反过来，如果又收到 GROUP_AT_MESSAGE_CREATE，说明该群退回了仅 @ 模式。
    /// </summary>
    private volatile bool _receivesAll;

    private readonly bool _configuredReceivesAll;

    public QqChatAdapter(QqSettings settings, ILogger<QqChatAdapter>? log = null, HttpClient? http = null,
        ILogger<QqGateway>? gatewayLog = null)
    {
        _log = log ?? NullLogger<QqChatAdapter>.Instance;
        _configuredReceivesAll = settings.ReceivesAllMessages;
        _receivesAll = settings.ReceivesAllMessages;
        _api = new QqApi(settings, http, message => _log.LogInformation("[qq] {Message}", message));
        _gateway = new QqGateway(_api, gatewayLog);
        _gateway.MessageReceived += message => RaiseAsync(message);
    }

    public string Name => "qq";

    public AdapterCapabilities Capabilities => new(ReceivesAllGroupMessages: _receivesAll);

    public event Func<IncomingMessage, Task>? MessageReceived;

    public event Action<string>? Faulted
    {
        add => _gateway.Fatal += value;
        remove => _gateway.Fatal -= value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("启动 QQ 官方机器人网关");
        _run = _gateway.RunAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public async Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        var content = BuildContent(message);
        var sequence = NextSequence(message.ReplyToMessageId ?? message.ChatId);

        if (message.Kind == ChatKind.Group)
        {
            await _api.SendGroupMessageAsync(message.ChatId, content, message.ReplyToMessageId, sequence, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await _api.SendC2CMessageAsync(message.ChatId, content, message.ReplyToMessageId, sequence, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>出站 @ 由程序构造：模型给的"@名字"只是语义提示，真正的 at 必须用 open_id。</summary>
    public static string BuildContent(OutgoingMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.MentionOpenId))
        {
            return message.Content;
        }

        return $"<qqbot-at-user id=\"{message.MentionOpenId}\" /> {message.Content}";
    }

    private int NextSequence(string key) => _sequence.AddOrUpdate(key, 1, (_, current) => current + 1);

    private async Task RaiseAsync(IncomingMessage message)
    {
        if (!_receivesAll
            && string.Equals(message.EventType, "GROUP_MESSAGE_CREATE", StringComparison.OrdinalIgnoreCase))
        {
            _receivesAll = true;
            _log.LogInformation("收到全量群消息事件（GROUP_MESSAGE_CREATE），自动开启『能旁听全群』能力位："
                                + "名字触发 / 关键词触发 / 疑问句触发从现在起生效。");
        }
        else if (_receivesAll
                 && !_configuredReceivesAll
                 && string.Equals(message.EventType, "GROUP_AT_MESSAGE_CREATE", StringComparison.OrdinalIgnoreCase))
        {
            // 该群退回"仅 @"模式了，能力位跟着收回来，避免一直以为能旁听。
            _receivesAll = false;
            _log.LogInformation("收到仅 @ 事件（GROUP_AT_MESSAGE_CREATE），该群已退回仅 @ 模式，关闭『能旁听全群』能力位。");
        }

        var handler = MessageReceived;
        if (handler is not null)
        {
            await handler(message).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gateway.DisposeAsync().ConfigureAwait(false);
        if (_run is not null)
        {
            try
            {
                await _run.ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }
    }
}
