using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bot.Core.Config;
using Bot.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bot.QQ;

/// <summary>网关连接状态。配置页据此显示"是否真的听得见"，而不是"进程还在"。</summary>
public enum GatewayState
{
    Stopped = 0,
    Connecting = 1,
    Online = 2,
    Reconnecting = 3,
}

/// <summary>网关状态快照（只读，供配置页轮询）。</summary>
public sealed record GatewayStatus(
    GatewayState State,
    string? Url,
    DateTimeOffset ChangedAt,
    DateTimeOffset? LastInboundAt,
    DateTimeOffset? LastEventAt,
    long InboundCount,
    int Reconnects,
    string? LastError,
    string? SessionId,
    DateTimeOffset? LastHeartbeatAt)
{
    public static readonly GatewayStatus StoppedStatus =
        new(GatewayState.Stopped, null, DateTimeOffset.UtcNow, null, null, 0, 0, null, null, null);
}

/// <summary>
/// QQ 官方机器人 WebSocket 网关。
/// <para>协议要点：op10 hello 给心跳间隔；op2 identify 带 <c>QQBot {token}</c>；op1 心跳带上次 seq；
/// op11 是 ack；op7 要求重连；op9 表示会话失效需要重新 identify。</para>
/// <para>断线必须能自动恢复——机器人是 24 小时常驻进程，一次网络抖动不能让它永久离线。</para>
/// </summary>
public sealed class QqGateway : IAsyncDisposable
{
    private const int OpDispatch = 0;
    private const int OpHeartbeat = 1;
    private const int OpIdentify = 2;
    private const int OpResume = 6;
    private const int OpReconnect = 7;
    private const int OpInvalidSession = 9;
    private const int OpHello = 10;
    private const int OpHeartbeatAck = 11;

    private readonly QqApi _api;
    private readonly ILogger<QqGateway> _log;
    private readonly ClientWebSocket _socket = new();

    private readonly object _statusGate = new();
    private GatewayState _state = GatewayState.Stopped;
    private DateTimeOffset _stateSince = DateTimeOffset.UtcNow;
    private DateTimeOffset? _lastInboundAt;
    private DateTimeOffset? _lastEventAt;
    private DateTimeOffset? _lastHeartbeatAt;
    private long _inboundCount;
    private int _reconnects;
    private string? _lastError;
    private string? _gatewayUrl;

    private int _lastSeq = -1;
    private string? _sessionId;
    private CancellationTokenSource? _heartbeatCts;

    public QqGateway(QqApi api, ILogger<QqGateway>? log = null)
    {
        _api = api;
        _log = log ?? NullLogger<QqGateway>.Instance;
    }

    /// <summary>收到的每条消息（已归一化为内部模型）。</summary>
    public event Func<IncomingMessage, Task>? MessageReceived;

    public event Action<string>? Fatal;

    /// <summary>当前连接状态。无副作用，配置页可随时调用。</summary>
    public GatewayStatus Snapshot()
    {
        lock (_statusGate)
        {
            return new GatewayStatus(
                _state,
                _gatewayUrl,
                _stateSince,
                _lastInboundAt,
                _lastEventAt,
                _inboundCount,
                _reconnects,
                _lastError,
                _sessionId,
                _lastHeartbeatAt);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (attempt == 0)
                {
                    SetState(GatewayState.Connecting, null);
                }

                await ConnectAndListenAsync(cancellationToken).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(1);
                SetState(GatewayState.Reconnecting, "网关连接被关闭，准备重连");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "网关连接异常，{Backoff} 后重连", backoff);
                Fatal?.Invoke(ex.Message);
                SetState(GatewayState.Reconnecting, ex.Message);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            lock (_statusGate)
            {
                _reconnects++;
            }

            try
            {
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 1.8));
            attempt++;
        }

        SetState(GatewayState.Stopped, null);
    }

    private void SetState(GatewayState state, string? error)
    {
        lock (_statusGate)
        {
            if (_state != state)
            {
                _state = state;
                _stateSince = DateTimeOffset.UtcNow;
            }

            _lastError = error;
        }
    }

    private void MarkOnline(string? sessionId)
    {
        lock (_statusGate)
        {
            _state = GatewayState.Online;
            _stateSince = DateTimeOffset.UtcNow;
            _lastError = null;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                _sessionId = sessionId;
            }
        }
    }

    private void RecordEvent()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_statusGate)
        {
            _lastEventAt = now;
        }
    }

    private void RecordInbound()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_statusGate)
        {
            _lastEventAt = now;
            _lastInboundAt = now;
            _inboundCount++;
        }
    }

    private void RecordHeartbeatAck()
    {
        lock (_statusGate)
        {
            _lastHeartbeatAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task ConnectAndListenAsync(CancellationToken cancellationToken)
    {
        var gatewayUrl = await _api.GetGatewayAsync(cancellationToken).ConfigureAwait(false);
        var token = await _api.GetTokenAsync(cancellationToken).ConfigureAwait(false);

        lock (_statusGate)
        {
            _gatewayUrl = gatewayUrl;
        }

        if (_socket.State is WebSocketState.Open or WebSocketState.Connecting)
        {
            _socket.Abort();
        }

        _log.LogInformation("正在连接 QQ 网关 {Url}", gatewayUrl);

        var socket = new ClientWebSocket();
        // WebSocket 握手没有默认超时，网络被墙时会静默挂很久。必须自己设上限。
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await socket.ConnectAsync(new Uri(gatewayUrl), connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException("连接 QQ 网关超时（20s）");
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _log.LogInformation("已连接 QQ 网关 {Url}", gatewayUrl);
        SetState(GatewayState.Connecting, null);

        var buffer = new byte[16 * 1024];
        var assembly = new MemoryStream();

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            assembly.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _log.LogWarning("网关请求关闭：{Status} {Description}", result.CloseStatus, result.CloseStatusDescription);
                    return;
                }

                assembly.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(assembly.GetBuffer(), 0, (int)assembly.Length);
            var keepGoing = await HandlePayloadAsync(json, socket, token, cancellationToken).ConfigureAwait(false);
            if (!keepGoing)
            {
                return;
            }
        }
    }

    private async Task<bool> HandlePayloadAsync(string json, ClientWebSocket socket, string token, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var op = root.TryGetProperty("op", out var opElement) ? opElement.GetInt32() : -1;

        if (root.TryGetProperty("s", out var seqElement) && seqElement.ValueKind == JsonValueKind.Number)
        {
            _lastSeq = seqElement.GetInt32();
        }

        switch (op)
        {
            case OpHello:
            {
                var interval = 41250;
                if (root.TryGetProperty("d", out var hello) && hello.TryGetProperty("heartbeat_interval", out var hb))
                {
                    interval = hb.GetInt32();
                }

                await SendAsync(socket, new
                {
                    op = _sessionId is null && _lastSeq < 0 ? OpIdentify : OpResume,
                    d = BuildIdentifyOrResume(token),
                }, cancellationToken).ConfigureAwait(false);
                StartHeartbeat(socket, interval, cancellationToken);
                return true;
            }

            case OpDispatch:
            {
                // 任何 dispatch 都说明 socket 还活着；消息事件再单独记 last inbound。
                RecordEvent();
                var type = root.TryGetProperty("t", out var tElement) ? tElement.GetString() : null;
                if (string.Equals(type, "READY", StringComparison.Ordinal))
                {
                    string? readySession = null;
                    if (root.TryGetProperty("d", out var ready) && ready.TryGetProperty("session_id", out var sid))
                    {
                        readySession = sid.GetString();
                        _log.LogInformation("网关就绪，session={Session}", readySession);
                    }

                    MarkOnline(readySession);
                    return true;
                }

                // 每条 dispatch 都记类型：出问题时能一眼看出"是没收到事件"还是"收到了但不认识"。
                _log.LogInformation("收到 dispatch：{Type}", type ?? "(空)");

                if (type is not null && type.EndsWith("MESSAGE_CREATE", StringComparison.OrdinalIgnoreCase))
                {
                    var message = QqPayloadMapper.Map(root, type);
                    if (message is null)
                    {
                        if (QqPayloadMapper.HasTextContent(root))
                        {
                            // 有正文却映射不出来 —— 这才是真问题，把载荷打出来（open_id 已脱敏）。
                            _log.LogWarning("无法映射的消息类型 {Type}，原始载荷：{Payload}",
                                type, Truncate(Bot.Core.Config.Masking.Redact(json, true), 600));
                        }
                        else
                        {
                            _log.LogInformation("收到无文本消息（图片/语音/卡片/聊天记录），已忽略，不进入对话");
                        }
                    }
                    else
                    {
                        RecordInbound();
                        if (MessageReceived is not null)
                        {
                            try
                            {
                                await MessageReceived(message).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                // 一条消息处理失败不能让整个网关断开：断线意味着接下来所有消息都收不到。
                                _log.LogError(ex, "消息处理异常（网关继续运行）");
                            }
                        }
                    }
                }

                return true;
            }

            case OpHeartbeatAck:
                RecordHeartbeatAck();
                return true;

            case OpReconnect:
                _log.LogWarning("服务端要求重连");
                return false;

            case OpInvalidSession:
                _log.LogWarning("会话失效，下次连接重新 identify");
                lock (_statusGate)
                {
                    _sessionId = null;
                }

                _lastSeq = -1;
                return false;

            default:
                return true;
        }
    }

    private object BuildIdentifyOrResume(string token)
    {
        if (_sessionId is null || _lastSeq < 0)
        {
            var intents = _api.Intents;
            _log.LogInformation("IDENTIFY 订阅 intents=0x{Intents:X}", intents);
            return new
            {
                token = $"QQBot {token}",
                intents,
                shard = new[] { 0, 1 },
                properties = new { os = "windows", browser = "chatbot", device = "chatbot" },
            };
        }

        return new
        {
            token = $"QQBot {token}",
            session_id = _sessionId,
            seq = _lastSeq,
        };
    }

    private void StartHeartbeat(ClientWebSocket socket, int intervalMs, CancellationToken cancellationToken)
    {
        _heartbeatCts?.Cancel();
        _heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _heartbeatCts.Token;
        var delay = TimeSpan.FromMilliseconds(Math.Max(5000, intervalMs));

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    if (socket.State != WebSocketState.Open)
                    {
                        return;
                    }

                    await SendAsync(socket, new { op = OpHeartbeat, d = _lastSeq < 0 ? null : (object?)_lastSeq }, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "心跳发送失败");
                    return;
                }
            }
        }, token);
    }

    private static async Task SendAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        SetState(GatewayState.Stopped, null);
        _heartbeatCts?.Cancel();
        _heartbeatCts?.Dispose();
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        _socket.Dispose();
    }
    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + "…";
}

/// <summary>把 QQ 的 dispatch 载荷映射成内部消息模型。</summary>
public static class QqPayloadMapper
{
    public sealed record Mention(string OpenId, string Name, bool IsBot);

    /// <summary>
    /// 按<b>载荷形状</b>判断而不是死认事件名：
    /// 群主开启"接收所有消息"后，平台把事件名换成 GROUP_MESSAGE_CREATE（Intent 仍是 1&lt;&lt;25），
    /// 死认名字就会静默丢消息。
    /// </summary>
    public static IncomingMessage? Map(JsonElement root, string? type)
    {
        if (!root.TryGetProperty("d", out var data))
        {
            return null;
        }

        var rawContent = data.TryGetProperty("content", out var contentElement) ? contentElement.GetString() ?? string.Empty : string.Empty;
        var messageId = data.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;

        var timestamp = DateTimeOffset.UtcNow;
        if (data.TryGetProperty("timestamp", out var tsElement))
        {
            if (tsElement.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(tsElement.GetString(), out var parsed))
            {
                timestamp = parsed;
            }
        }

        if (!data.TryGetProperty("author", out var author))
        {
            return null;
        }

        var mentions = ParseMentions(data);

        // 官方文档：mentions 里带 bot 字段。被 @ 的用户列表里只要有机器人，就是 @ 了机器人。
        // 这比在 content 里找 "<qqbot-at-user>" 可靠得多——全量模式根本不发那个标签。
        var botMentioned = mentions.Any(m => m.IsBot)
                           || string.Equals(type, "GROUP_AT_MESSAGE_CREATE", StringComparison.OrdinalIgnoreCase)
                           || rawContent.Contains("<qqbot-at-user", StringComparison.OrdinalIgnoreCase);

        var content = CleanContent(rawContent, mentions);
        if (content.Length == 0)
        {
            // 纯图片/语音/卡片/合并转发：机器人看不到内容，也不能编。
            // 这类消息不进入对话（不进未读缓冲、不进历史、不触发回复）。
            return null;
        }

        var unionOpenId = author.TryGetProperty("union_openid", out var unionElement) ? unionElement.GetString() : null;
        var username = author.TryGetProperty("username", out var nameElement) ? nameElement.GetString() : null;
        var authorId = author.TryGetProperty("id", out var idInAuthor) ? idInAuthor.GetString() : null;

        // 全量模式下机器人自己发的消息也会被推回来。不认出来就会自问自答死循环。
        var authorIsBot = author.TryGetProperty("bot", out var botFlag) && botFlag.ValueKind == JsonValueKind.True;

        var groupOpenId = data.TryGetProperty("group_openid", out var groupElement) ? groupElement.GetString() : null;
        if (!string.IsNullOrEmpty(groupOpenId))
        {
            var memberOpenId = author.TryGetProperty("member_openid", out var memberElement)
                ? memberElement.GetString()
                : authorId;

            if (string.IsNullOrEmpty(memberOpenId))
            {
                return null;
            }

            return new IncomingMessage
            {
                ChatId = groupOpenId,
                Kind = ChatKind.Group,
                OpenId = memberOpenId,
                UnionOpenId = unionOpenId,
                SenderName = Trim(username),
                Content = content,
                MentionsBot = botMentioned,
                MessageId = messageId,
                Timestamp = timestamp,
                EventType = type,
                IsSelf = authorIsBot,
            };
        }

        var userOpenId = author.TryGetProperty("user_openid", out var userElement) ? userElement.GetString() : authorId;
        if (!string.IsNullOrEmpty(userOpenId))
        {
            return new IncomingMessage
            {
                ChatId = userOpenId,
                Kind = ChatKind.Direct,
                OpenId = userOpenId,
                UnionOpenId = unionOpenId,
                SenderName = Trim(username),
                Content = content,
                MentionsBot = true,
                MessageId = messageId,
                Timestamp = timestamp,
                EventType = type,
            };
        }

        return null;
    }

    private static List<Mention> ParseMentions(JsonElement data)
    {
        var list = new List<Mention>();
        if (!data.TryGetProperty("mentions", out var mentions) || mentions.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in mentions.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var openId = item.TryGetProperty("member_openid", out var member) ? member.GetString()
                : item.TryGetProperty("user_openid", out var user) ? user.GetString()
                : item.TryGetProperty("id", out var id) ? id.GetString()
                : null;

            var name = item.TryGetProperty("username", out var nameElement) ? nameElement.GetString() : null;
            var isBot = item.TryGetProperty("bot", out var botElement) && botElement.ValueKind == JsonValueKind.True;

            if (!string.IsNullOrEmpty(openId))
            {
                list.Add(new Mention(openId, Trim(name) ?? "某人", isBot));
            }
        }

        return list;
    }

    /// <summary>
    /// 把平台占位符还原成人能读的样子，同时清掉表情与其它标签。
    /// <para>content 里的 @ 是 <c>&lt;@成员openid&gt;</c>，直接留着会污染 prompt（等于把 open_id 写进去）。</para>
    /// </summary>
    public static string CleanContent(string? content, IReadOnlyList<Mention>? mentions = null)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var cleaned = content;

        // 先把 @ 占位换成 @昵称，避免下一步的正则把它们清成空白。
        if (mentions is not null)
        {
            foreach (var mention in mentions)
            {
                cleaned = cleaned.Replace($"<@{mention.OpenId}>", $"@{mention.Name}", StringComparison.Ordinal);
            }
        }

        cleaned = cleaned.Replace("<@all>", "@全体成员", StringComparison.OrdinalIgnoreCase);
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "<@([^>]*)>", "@某人");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "<qqbot-at-user[^>]*/>", string.Empty);
        // 群里的表情会以 <faceType=1,faceId="314",ext="..."> 形式下发，留着只会白烧 token。
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "<faceType=[^>]*>", "[表情]");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"(\[表情\])+", "[表情]");

        return cleaned.Trim();
    }

    /// <summary>
    /// content 可能是空的：纯图片/语音/卡片/合并转发都会这样。
    /// 机器人看不到这些内容，也不该把"对方发了张图"编进对话，所以这类消息直接丢弃。
    /// 这个判定放在网关侧，用来区分"解析失败"和"本来就是无文本消息"，避免日志刷警告。
    /// </summary>
    public static bool HasTextContent(JsonElement root)
    {
        if (!root.TryGetProperty("d", out var data))
        {
            return false;
        }

        return data.TryGetProperty("content", out var contentElement)
               && contentElement.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(contentElement.GetString());
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
