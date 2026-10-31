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

    private long _sentCount;
    private DateTimeOffset? _lastSentAt;
    private string? _lastSendError;

    /// <summary>
    /// 是否真的能听到全群：只根据平台推来的事件自动判断。
    /// 收到 GROUP_MESSAGE_CREATE = 群主开了「接收所有消息」；收到 GROUP_AT_MESSAGE_CREATE = 仅 @。
    /// 不再提供手动开关（手动开了但群没开，会误触发全量逻辑）。
    /// </summary>
    private volatile bool _receivesAll;

    public QqChatAdapter(QqSettings settings, ILogger<QqChatAdapter>? log = null, HttpClient? http = null,
        ILogger<QqGateway>? gatewayLog = null)
    {
        _log = log ?? NullLogger<QqChatAdapter>.Instance;
        _api = new QqApi(settings, http, message => _log.LogInformation("[qq] {Message}", message));
        _gateway = new QqGateway(_api, gatewayLog);
        _gateway.MessageReceived += message => RaiseAsync(message);
    }

    public string Name => "qq";

    public AdapterCapabilities Capabilities => new(ReceivesAllGroupMessages: _receivesAll);

    /// <summary>
    /// 连接状态直接来自网关：只有 READY 之后才算在线。掉线时状态是 reconnecting，
    /// 配置页因此不会把"进程还在跑"误显示成"QQ 正常"。
    /// </summary>
    public AdapterStatus Status
    {
        get
        {
            var gateway = _gateway.Snapshot();
            var state = gateway.State switch
            {
                GatewayState.Online => "online",
                GatewayState.Connecting => "connecting",
                GatewayState.Reconnecting => "reconnecting",
                _ => "stopped",
            };

            var error = gateway.LastError ?? _lastSendError;
            var summary = gateway.State switch
            {
                GatewayState.Online => _receivesAll
                    ? "QQ 网关已连接，可接收群内全部消息"
                    : "QQ 网关已连接，当前只接收 @ 消息",
                GatewayState.Connecting => "正在连接 QQ 网关…",
                GatewayState.Reconnecting => string.IsNullOrWhiteSpace(gateway.LastError)
                    ? "网关未连接，正在自动重连"
                    : "网关未连接，正在自动重连：" + Clip(gateway.LastError!),
                _ => "QQ 网关已停止",
            };

            var detail = new List<string>();
            if (!string.IsNullOrWhiteSpace(gateway.Url))
            {
                detail.Add("网关 " + gateway.Url);
            }

            if (!string.IsNullOrWhiteSpace(gateway.SessionId))
            {
                detail.Add("session " + gateway.SessionId);
            }

            if (!string.IsNullOrWhiteSpace(_lastSendError))
            {
                detail.Add("最近一次发送失败：" + Clip(_lastSendError!));
            }

            return new AdapterStatus
            {
                Name = Name,
                Online = gateway.State == GatewayState.Online,
                State = state,
                Summary = summary,
                Since = gateway.ChangedAt,
                LastInboundAt = gateway.LastInboundAt,
                LastOutboundAt = _lastSentAt,
                InboundCount = gateway.InboundCount,
                OutboundCount = Interlocked.Read(ref _sentCount),
                Reconnects = gateway.Reconnects,
                LastError = error,
                SessionId = gateway.SessionId,
                GatewayUrl = gateway.Url,
                LastHeartbeatAt = gateway.LastHeartbeatAt,
                ReceivesAllGroupMessages = _receivesAll,
                Detail = detail.Count > 0 ? string.Join("｜", detail) : null,
            };
        }
    }

    private static string Clip(string text) => text.Length <= 160 ? text : text[..160] + "…";

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
        var sequence = NextSequence(message.ReplyToMessageId ?? message.ChatId);

        try
        {
            if (message.Sticker is { } sticker)
            {
                await SendStickerAsync(message, sticker, sequence, cancellationToken).ConfigureAwait(false);
            }
            else if (message.Kind == ChatKind.Group)
            {
                await _api.SendGroupMessageAsync(message.ChatId, BuildContent(message), message.ReplyToMessageId,
                        sequence, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _api.SendC2CMessageAsync(message.ChatId, BuildContent(message), message.ReplyToMessageId,
                        sequence, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _lastSendError = ex.Message;
            throw;
        }

        _lastSendError = null;
        _lastSentAt = DateTimeOffset.UtcNow;
        Interlocked.Increment(ref _sentCount);
    }

    /// <summary>
    /// 发表情包：先上传拿 file_info，再用 msg_type=7 发出。
    /// <para>
    /// 带 <c>msg_id</c> = 被动回复（@ 模式与全量模式下的普通回复都走这条，不占主动频控）；
    /// 不带 = 主动消息（主动接话，或消息已超过 5 分钟），由运行时决定要不要发。
    /// </para>
    /// </summary>
    private async Task SendStickerAsync(OutgoingMessage message, OutgoingSticker sticker, int sequence,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(sticker.FilePath, cancellationToken).ConfigureAwait(false);
        var fileName = string.IsNullOrWhiteSpace(sticker.FileName)
            ? Path.GetFileName(sticker.FilePath)
            : sticker.FileName;

        var fileInfo = await _api.UploadImageAsync(message.Kind, message.ChatId, bytes, fileName, cancellationToken)
            .ConfigureAwait(false);

        if (message.Kind == ChatKind.Group)
        {
            await _api.SendGroupImageAsync(message.ChatId, fileInfo, message.ReplyToMessageId, sequence, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await _api.SendC2CImageAsync(message.ChatId, fileInfo, message.ReplyToMessageId, sequence, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// QQ 群文本消息（<c>msg_type=0</c>）不支持嵌入 @：官方只在 markdown 消息里解析
    /// <c>&lt;qqbot-at-user id="…" /&gt;</c>。把它拼进文本，客户端会原样显示成一串标签，
    /// 所以这里只发正文；模型用「称呼名字」表达点名。
    /// </summary>
    public static string BuildContent(OutgoingMessage message) => message.Content;

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
