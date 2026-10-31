using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bot.Core.Config;
using Bot.Core.Domain;

namespace Bot.QQ;

internal sealed class TokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    /// <summary>QQ 返回的是字符串 "7200"（不是数字），直接映射 int 会抛 JsonException。</summary>
    [JsonPropertyName("expires_in")]
    public JsonElement ExpiresIn { get; set; }

    public int ExpiresInSeconds()
    {
        return ExpiresIn.ValueKind switch
        {
            JsonValueKind.Number when ExpiresIn.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(ExpiresIn.GetString(), out var parsed) => parsed,
            _ => 7200,
        };
    }
}

internal sealed class GatewayResponse
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>
/// QQ 官方机器人 v2 HTTP 接口。Token 用 AppId + Secret 换取，有效期 7200s，提前 60s 刷新。
/// <para>没有官方 C# SDK，这里只实现本项目用到的 4 个端点：换票、取网关、发群消息、发单聊消息。</para>
/// </summary>
public sealed class QqApi
{
    public const string ProdApiBase = "https://api.sgroup.qq.com";
    public const string SandboxApiBase = "https://sandbox.api.sgroup.qq.com";
    public const string TokenUrl = "https://bots.qq.com/app/getAppAccessToken";

    /// <summary>群聊与单聊事件（GROUP_AT_MESSAGE_CREATE / C2C_MESSAGE_CREATE）。</summary>
    public const int IntentGroupAndC2C = 1 << 25;

    private readonly QqSettings _settings;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly Action<string>? _trace;

    private string? _accessToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public QqApi(QqSettings settings, HttpClient? http = null, Action<string>? trace = null)
    {
        _settings = settings;
        _trace = trace;
        // 表情包走富媒体上传：base64 后的请求体可能有好几 MB，20s 偏紧。
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>
    /// 订阅的事件位。默认只要「群与单聊事件」（1&lt;&lt;25）。
    /// 群主开启"允许接收群内所有消息"后若仍需额外位，可通过 <c>qq.extraIntents</c> 叠加。
    /// </summary>
    public int Intents => IntentGroupAndC2C | _settings.ExtraIntents;

    public string ApiBase => _settings.Sandbox ? SandboxApiBase : ProdApiBase;

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt)
            {
                return _accessToken;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = JsonContent.Create(new { appId = _settings.AppId, clientSecret = _settings.Secret }),
            };

            _trace?.Invoke($"请求 access_token（appId={_settings.AppId}）");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"获取 access_token 失败：HTTP {(int)response.StatusCode} {body}");
            }

            var token = JsonSerializer.Deserialize<TokenResponse>(body);
            if (string.IsNullOrWhiteSpace(token?.AccessToken))
            {
                throw new InvalidOperationException("获取 access_token 失败：响应中没有 access_token");
            }

            _accessToken = token.AccessToken;
            var lifetime = token.ExpiresInSeconds();
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, lifetime - 60));
            _trace?.Invoke($"已获取 access_token（长度 {_accessToken.Length}，有效期 {lifetime}s）");
            return _accessToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    public async Task<string> GetGatewayAsync(CancellationToken cancellationToken)
    {
        _trace?.Invoke("请求网关地址 /gateway");
        using var response = await SendAuthorizedAsync(HttpMethod.Get, "/gateway", null, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"获取网关失败：HTTP {(int)response.StatusCode} {body}");
        }

        var gateway = JsonSerializer.Deserialize<GatewayResponse>(body);
        if (string.IsNullOrWhiteSpace(gateway?.Url))
        {
            throw new InvalidOperationException("获取网关失败：响应中没有 url");
        }

        _trace?.Invoke($"网关地址 {gateway.Url}");
        return gateway.Url;
    }

    /// <summary>
    /// 发群消息。<paramref name="msgId"/> 非空时为被动回复（5 分钟内有效，不占主动推送额度），
    /// 同一条 msg_id 下的多条气泡必须用递增的 msg_seq，否则会被判重复。
    /// </summary>
    public async Task SendGroupMessageAsync(string groupOpenId, string content, string? msgId, int msgSeq,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["content"] = content,
            ["msg_type"] = 0,
        };
        if (!string.IsNullOrWhiteSpace(msgId))
        {
            payload["msg_id"] = msgId;
            payload["msg_seq"] = msgSeq;
        }

        using var response = await SendAuthorizedAsync(HttpMethod.Post, $"/v2/groups/{groupOpenId}/messages", payload, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendC2CMessageAsync(string openId, string content, string? msgId, int msgSeq,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["content"] = content,
            ["msg_type"] = 0,
        };
        if (!string.IsNullOrWhiteSpace(msgId))
        {
            payload["msg_id"] = msgId;
            payload["msg_seq"] = msgSeq;
        }

        using var response = await SendAuthorizedAsync(HttpMethod.Post, $"/v2/users/{openId}/messages", payload, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ 富媒体（表情包）

    /// <summary>
    /// 上传图片并拿回 <c>file_info</c>。QQ 的富媒体是两步：先上传，再用 <c>msg_type=7</c> 发出去。
    /// <para>群和单聊的上传接口互相独立，传错端点会直接失败，所以 <paramref name="kind"/> 必须准确。</para>
    /// </summary>
    public async Task<string> UploadImageAsync(ChatKind kind, string openId, byte[] bytes, string fileName,
        CancellationToken cancellationToken)
    {
        var path = MediaUploadPath(kind, openId);
        var payload = BuildMediaUploadPayload(bytes, fileName);

        using var response = await SendAuthorizedAsync(HttpMethod.Post, path, payload, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"QQ 图片上传失败：HTTP {(int)response.StatusCode} {body}");
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("file_info", out var info)
            || string.IsNullOrWhiteSpace(info.GetString()))
        {
            throw new InvalidOperationException($"QQ 图片上传失败：响应里没有 file_info（{body}）");
        }

        _trace?.Invoke($"已上传表情包 {fileName}（{bytes.Length} 字节）");
        return info.GetString()!;
    }

    public static string MediaUploadPath(ChatKind kind, string openId) =>
        kind == ChatKind.Group
            ? $"/v2/groups/{Uri.EscapeDataString(openId)}/files"
            : $"/v2/users/{Uri.EscapeDataString(openId)}/files";

    /// <summary>上传请求体：file_type=1（图片）+ base64 的 file_data；srv_send_msg=false 只换 file_info。</summary>
    public static Dictionary<string, object?> BuildMediaUploadPayload(byte[] bytes, string fileName) => new()
    {
        ["file_type"] = 1,
        ["file_name"] = string.IsNullOrWhiteSpace(fileName) ? "sticker.png" : fileName,
        ["file_data"] = Convert.ToBase64String(bytes),
        ["srv_send_msg"] = false,
    };

    /// <summary>
    /// 发图请求体。<paramref name="msgId"/> 非空 = 被动回复（5 分钟内、不占主动频控）；
    /// 为空 = 主动消息（全量模式下主动接话 / 消息超过 5 分钟时的唯一选择）。
    /// </summary>
    public static Dictionary<string, object?> BuildMediaMessagePayload(string fileInfo, string? msgId, int msgSeq)
    {
        var payload = new Dictionary<string, object?>
        {
            ["msg_type"] = 7,
            ["media"] = new Dictionary<string, object?> { ["file_info"] = fileInfo },
        };

        if (!string.IsNullOrWhiteSpace(msgId))
        {
            payload["msg_id"] = msgId;
            payload["msg_seq"] = msgSeq;
        }

        return payload;
    }

    public async Task SendGroupImageAsync(string groupOpenId, string fileInfo, string? msgId, int msgSeq,
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, $"/v2/groups/{groupOpenId}/messages",
            BuildMediaMessagePayload(fileInfo, msgId, msgSeq), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendC2CImageAsync(string openId, string fileInfo, string? msgId, int msgSeq,
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, $"/v2/users/{openId}/messages",
            BuildMediaMessagePayload(fileInfo, msgId, msgSeq), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpMethod method, string path, object? payload,
        CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        var request = new HttpRequestMessage(method, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException($"QQ 接口失败：HTTP {(int)response.StatusCode} {body}");
    }
}
