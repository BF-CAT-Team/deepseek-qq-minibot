using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bot.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bot.Core.Llm;

public sealed record PromptMessage(string Role, string Content);

public sealed record LlmUsage(int PromptTokens, int CacheHitTokens, int CacheMissTokens, int CompletionTokens);

public sealed record ChatCompletionRequest(
    string Model,
    IReadOnlyList<PromptMessage> Messages,
    double Temperature,
    int MaxTokens,
    string Thinking = "auto");

public sealed record LlmResult(
    bool Ok,
    string? Text,
    LlmUsage Usage,
    int LatencyMs,
    string? ErrorCode,
    string? ErrorMessage,
    int Attempts = 1)
{
    public static LlmResult Fail(string code, string message, int latencyMs = 0, int attempts = 1) =>
        new(false, null, new LlmUsage(0, 0, 0, 0), latencyMs, code, message, attempts);
}

public interface IChatModel
{
    Task<LlmResult> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken);
}

public static class ErrorCodes
{
    public const string Timeout = "E_TIMEOUT";
    public const string Rate = "E_RATE";
    public const string Server = "E_5XX";
    public const string Empty = "E_EMPTY";
    public const string Parse = "E_PARSE";
    public const string Db = "E_DB";
    public const string Budget = "E_BUDGET";
    public const string PrefixDrift = "E_PREFIX_DRIFT";
    public const string Auth = "E_AUTH";
    public const string Network = "E_NETWORK";
    public const string Config = "E_CONFIG";
}

/// <summary>
/// DeepSeek 官方 API 客户端（OpenAI 兼容协议）。
/// <para>usage 里的 prompt_cache_hit_tokens / prompt_cache_miss_tokens 就是文档 6.1 需要的两个量，
/// 直接落 request_log，不做事后估算。</para>
/// </summary>
public sealed class DeepSeekChatModel : IChatModel, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly LlmSettings _settings;
    private readonly ILogger<DeepSeekChatModel> _log;
    private readonly int _maxRetries;

    public DeepSeekChatModel(LlmSettings settings, ILogger<DeepSeekChatModel>? log = null, HttpClient? http = null)
    {
        _settings = settings;
        _log = log ?? NullLogger<DeepSeekChatModel>.Instance;
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 300));
        _maxRetries = 3;
    }

    public async Task<LlmResult> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            return LlmResult.Fail(ErrorCodes.Config, "未配置 API Key");
        }

        var stopwatch = Stopwatch.StartNew();
        var attempt = 0;
        var delay = TimeSpan.FromSeconds(1);

        while (true)
        {
            attempt++;
            try
            {
                using var httpRequest = BuildRequest(request);
                using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var parsed = ParseResponse(body, (int)stopwatch.ElapsedMilliseconds, attempt);
                    if (parsed.Ok || parsed.ErrorCode == ErrorCodes.Empty)
                    {
                        if (!parsed.Ok && attempt <= 1 && _maxRetries > 0)
                        {
                            continue; // 空回复重试 1 次
                        }
                    }

                    return parsed;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var wait = GetRetryAfter(response) ?? delay;
                    if (wait > TimeSpan.FromSeconds(60))
                    {
                        wait = TimeSpan.FromSeconds(60);
                    }

                    if (attempt > 2)
                    {
                        return LlmResult.Fail(ErrorCodes.Rate, "限流重试耗尽", (int)stopwatch.ElapsedMilliseconds, attempt);
                    }

                    _log.LogWarning("429 限流，{Wait} 后重试", wait);
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if ((int)response.StatusCode >= 500)
                {
                    if (attempt > _maxRetries)
                    {
                        return LlmResult.Fail(ErrorCodes.Server, $"服务端错误 {(int)response.StatusCode}",
                            (int)stopwatch.ElapsedMilliseconds, attempt);
                    }

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    delay *= 1.8;
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return LlmResult.Fail(ErrorCodes.Auth, "鉴权失败，请检查 API Key", (int)stopwatch.ElapsedMilliseconds, attempt);
                }

                return LlmResult.Fail(ErrorCodes.Server, $"HTTP {(int)response.StatusCode}: {Truncate(body, 200)}",
                    (int)stopwatch.ElapsedMilliseconds, attempt);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt > 1)
                {
                    return LlmResult.Fail(ErrorCodes.Timeout, "请求超时", (int)stopwatch.ElapsedMilliseconds, attempt);
                }

                _log.LogWarning("请求超时，重试一次");
                continue;
            }
            catch (HttpRequestException ex)
            {
                if (attempt > _maxRetries)
                {
                    return LlmResult.Fail(ErrorCodes.Network, ex.Message, (int)stopwatch.ElapsedMilliseconds, attempt);
                }

                _log.LogWarning(ex, "网络错误，{Delay} 后重试", delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay *= 1.8;
            }
        }
    }

    private HttpRequestMessage BuildRequest(ChatCompletionRequest request)
    {
        var payload = new ChatCompletionPayload
        {
            Model = request.Model,
            Messages = request.Messages.Select(m => new ChatCompletionMessage { Role = m.Role, Content = m.Content }).ToArray(),
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
            Stream = false,
        };

        // 思考模式开关。只对 deepseek 系模型发，避免把未知字段塞给别的兼容端点。
        if (request.Thinking is "disabled" or "enabled"
            && request.Model.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase))
        {
            payload.Thinking = new { type = request.Thinking };
        }

        // 预先序列化成字符串再发：JsonContent 默认走 chunked，某些网关/代理不认，
        // 显式 Content-Length 兼容性更好，也方便在日志里看请求体积。
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildUri())
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        return httpRequest;
    }

    private Uri BuildUri()
    {
        var baseUrl = string.IsNullOrWhiteSpace(_settings.BaseUrl) ? "https://api.deepseek.com" : _settings.BaseUrl.TrimEnd('/');
        return new Uri(baseUrl + "/chat/completions");
    }

    private static LlmResult ParseResponse(string body, int latencyMs, int attempts)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return LlmResult.Fail(ErrorCodes.Parse, "响应中没有 choices", latencyMs, attempts);
            }

            var message = choices[0].GetProperty("message");
            var content = message.TryGetProperty("content", out var contentElement) ? contentElement.GetString() : null;

            var usage = new LlmUsage(0, 0, 0, 0);
            if (root.TryGetProperty("usage", out var usageElement))
            {
                usage = new LlmUsage(
                    GetInt(usageElement, "prompt_tokens"),
                    GetInt(usageElement, "prompt_cache_hit_tokens"),
                    GetInt(usageElement, "prompt_cache_miss_tokens"),
                    GetInt(usageElement, "completion_tokens"));
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return new LlmResult(false, null, usage, latencyMs, ErrorCodes.Empty, "空回复", attempts);
            }

            return new LlmResult(true, content.Trim(), usage, latencyMs, null, null, attempts);
        }
        catch (JsonException ex)
        {
            return LlmResult.Fail(ErrorCodes.Parse, "响应解析失败：" + ex.Message, latencyMs, attempts);
        }
    }

    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            return date - DateTimeOffset.UtcNow;
        }

        return null;
    }

    private static string Truncate(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";

    public void Dispose() => _http.Dispose();

    private sealed class ChatCompletionPayload
    {
        public string Model { get; set; } = string.Empty;

        public ChatCompletionMessage[] Messages { get; set; } = Array.Empty<ChatCompletionMessage>();

        public double Temperature { get; set; }

        public int MaxTokens { get; set; }

        public bool Stream { get; set; }

        public object? Thinking { get; set; }
    }

    private sealed class ChatCompletionMessage
    {
        public string Role { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;
    }
}
