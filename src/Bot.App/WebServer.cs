using System.Text.Json;
using System.Text.Json.Nodes;
using Bot.Core.Config;
using Bot.Core.Domain;
using Bot.Core.Memory;
using Bot.Core.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bot.App;

/// <summary>
/// 本地配置页 / 看板。只监听 127.0.0.1，不对外暴露；它是"记忆浏览视图"与配置入口，
/// 让用户能看见机器人到底记住了什么（文档 3.4.6 第 4 条）。
/// </summary>
public sealed class WebServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = SettingsStore.JsonOptions;

    private readonly BotHost _host;
    private readonly ServerSettings _server;
    private WebApplication? _app;

    public WebServer(BotHost host, ServerSettings server)
    {
        _host = host;
        _server = server;
    }

    public string Url => $"http://{_server.Host}:{_server.Port}";

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(Url);

        var app = builder.Build();

        app.MapGet("/", () => Results.Content(WebPage.Html, "text/html; charset=utf-8"));
        app.MapGet("/api/status", () => Results.Json(BuildStatus(), Json));
        app.MapGet("/api/config", () => Results.Json(BuildConfig(), Json));
        app.MapPost("/api/config", SaveConfig);
        app.MapGet("/api/params", () => Results.Json(BuildParams(), Json));
        app.MapPost("/api/params", SaveParams);
        app.MapPost("/api/test", TestConnection);
        app.MapGet("/api/persons", () => Results.Json(ListPersons(), Json));
        app.MapGet("/api/events", (string? q) => Results.Json(ListEvents(q), Json));
        app.MapPost("/api/simulate", Simulate);
        app.MapPost("/api/rotate", Rotate);
        app.MapPost("/api/memory/forget", Forget);
        app.MapPost("/api/memory/rollback", Rollback);

        _app = app;
        await app.StartAsync().ConfigureAwait(false);
        _host.Log.LogInformation("配置页已启动：{Url}", Url);
    }

    public async Task StopAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }

    // ------------------------------------------------------------------ 状态

    private object BuildStatus()
    {
        var db = _host.Db;
        var since = new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds();
        var cost = new RequestLogStore(db).Summarize(null, since);

        return new
        {
            adapter = _host.AdapterName,
            connected = true,
            persons = db.ScalarLong("SELECT COUNT(*) FROM person WHERE status='active'"),
            events = db.ScalarLong("SELECT COUNT(*) FROM event WHERE status='active'"),
            archived = db.ScalarLong("SELECT COUNT(*) FROM event WHERE status='archived'"),
            todayTokens = cost.PromptTokens + cost.CompletionTokens,
            todayRequests = cost.Requests,
            cacheHitRatio = Math.Round(cost.PrefixHitRatio, 4),
            walPending = _host.Wal.Count(),
            chats = _host.Runtime.Snapshot().Select(c => new
            {
                chatId = c.ChatId,
                state = c.State.ToString(),
                turnCount = c.TurnCount,
                pendingCount = c.PendingCount,
                prefixHash = c.PrefixHash ?? string.Empty,
            }),
            warnings = _host.Config.Warnings,
            dataDirectory = AppPaths.Root,
        };
    }

    private JsonNode BuildConfig()
    {
        var node = JsonSerializer.SerializeToNode(_host.Settings, Json)!.AsObject();
        var llm = node["llm"]?.AsObject();
        if (llm is not null)
        {
            var hasKey = !string.IsNullOrWhiteSpace(llm["apiKey"]?.GetValue<string>());
            llm["apiKey"] = string.Empty;
            llm["apiKeySet"] = hasKey;
        }

        return node;
    }

    private async Task<IResult> SaveConfig(HttpRequest request)
    {
        try
        {
            var incoming = await JsonSerializer.DeserializeAsync<AppSettings>(request.Body, Json).ConfigureAwait(false);
            if (incoming is null)
            {
                return Results.BadRequest(new { error = "请求体为空" });
            }

            // 密码框留空 = 不修改（避免用户为了改别的字段把密钥清掉）。
            if (string.IsNullOrWhiteSpace(incoming.Llm.ApiKey))
            {
                incoming.Llm.ApiKey = _host.Settings.Llm.ApiKey;
            }

            if (string.IsNullOrWhiteSpace(incoming.Qq.Secret))
            {
                incoming.Qq.Secret = _host.Settings.Qq.Secret;
            }

            incoming.Preset = Presets.Normalize(incoming.Preset);
            _host.SaveSettings(incoming);

            var warnings = _host.Config.Warnings.ToList();
            if (!string.Equals(_host.AdapterName, incoming.Qq.Adapter, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("适配器已切换，需要重启程序才能生效");
            }

            return Results.Json(new { ok = true, warnings }, Json);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    // ------------------------------------------------------------------ 参数

    private object BuildParams()
    {
        var effective = _host.Config.ForChat(string.Empty);
        return ParamRegistry.All.Select(spec =>
        {
            var node = effective.Params.GetNode(spec.Name);
            object? value = spec.Type switch
            {
                ParamType.Bool => effective.Params.GetBool(spec.Name),
                ParamType.Int => effective.Params.GetInt(spec.Name),
                ParamType.Double => effective.Params.GetDouble(spec.Name),
                ParamType.StringList => effective.Params.GetStringList(spec.Name),
                _ => effective.Params.GetString(spec.Name),
            };

            return new
            {
                name = spec.Name,
                category = spec.Category,
                description = spec.Description,
                type = spec.Type switch
                {
                    ParamType.Bool => "bool",
                    ParamType.Int => "int",
                    ParamType.Double => "double",
                    ParamType.StringList => "list",
                    _ => "string",
                },
                value,
                min = spec.Min,
                max = spec.Max,
                choices = spec.Choices ?? Array.Empty<string>(),
                breaksCache = spec.BreaksCache,
                needsRestart = spec.NeedsRestart,
                raw = node?.ToJsonString(),
            };
        }).ToArray();
    }

    private async Task<IResult> SaveParams(HttpRequest request)
    {
        try
        {
            var incoming = await JsonSerializer.DeserializeAsync<JsonObject>(request.Body, Json).ConfigureAwait(false);
            if (incoming is null)
            {
                return Results.BadRequest(new { error = "请求体为空" });
            }

            var settings = _host.Settings;
            var baseline = ConfigResolver.Resolve(settings.Preset, null, null);
            var overrides = settings.Parameters ?? new JsonObject();
            var warnings = new List<string>();

            foreach (var (key, value) in incoming)
            {
                if (!ParamRegistry.TryGet(key, out _))
                {
                    warnings.Add($"未知参数：{key}");
                    continue;
                }

                // 只存"与预设不同"的项（文档 5.3：config_json 仅存覆盖项）。
                var baselineValue = baseline.GetNode(key)?.ToJsonString();
                if (string.Equals(baselineValue, value?.ToJsonString(), StringComparison.Ordinal))
                {
                    overrides.Remove(key);
                }
                else
                {
                    overrides[key] = value?.DeepClone();
                }
            }

            settings.Parameters = overrides;
            _host.SaveSettings(settings);
            warnings.AddRange(_host.Config.Warnings);

            return Results.Json(new { ok = true, warnings }, Json);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// 连通性自检：真的发一个 8 token 的请求。
    /// 用户点一下就知道"是 key 错了、模型名错了，还是网络不通"，不用去翻日志。
    /// </summary>
    private async Task<IResult> TestConnection()
    {
        var settings = _host.Settings;
        if (string.IsNullOrWhiteSpace(settings.Llm.ApiKey))
        {
            return Results.Json(new { ok = false, error = "还没有配置 API Key（先点保存）" }, Json);
        }

        var cfg = _host.Config.ForChat(string.Empty);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var result = await _host.Models.Get(settings.Llm).CompleteAsync(
                new Bot.Core.Llm.ChatCompletionRequest(
                    cfg.Model,
                    new[] { new Bot.Core.Llm.PromptMessage("user", "只回复两个字：在的") },
                    0.1,
                    64,
                    cfg.Thinking),
                cts.Token).ConfigureAwait(false);

            return Results.Json(new
            {
                ok = result.Ok,
                model = cfg.Model,
                thinking = cfg.Thinking,
                latencyMs = result.LatencyMs,
                text = result.Text,
                error = result.Ok ? null : $"{result.ErrorCode}: {result.ErrorMessage}",
                usage = new
                {
                    prompt = result.Usage.PromptTokens,
                    hit = result.Usage.CacheHitTokens,
                    miss = result.Usage.CacheMissTokens,
                    completion = result.Usage.CompletionTokens,
                },
            }, Json);
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = ex.Message }, Json);
        }
    }

    // ------------------------------------------------------------------ 记忆
    private object ListPersons() =>
        _host.Identity.ListActive(500).Select(p => new
        {
            personId = p.PersonId,
            openId = p.OpenId,
            name = p.Name,
            personality = p.Personality,
            evaluation = p.Evaluation,
            memoryText = p.MemoryText,
            msgCount = p.MsgCount,
            pinned = p.Pinned,
        }).ToArray();

    private object ListEvents(string? query)
    {
        var rows = string.IsNullOrWhiteSpace(query)
            ? _host.Events.LoadAll(null, 200)
            : _host.Events.Search(query, 100);

        return rows.Select(e => new
        {
            eventId = e.EventId,
            timeText = e.TimeText,
            detail = e.Detail,
            location = e.Location,
            keywords = e.Keywords,
            status = e.Status,
            pinned = e.Pinned,
            injectedCount = e.InjectedCount,
        }).ToArray();
    }

    private async Task<IResult> Simulate(HttpRequest request)
    {
        var mock = _host.MockAdapter;
        if (mock is null)
        {
            return Results.BadRequest(new { error = "当前不是 Mock 适配器，请先切到 Mock 再试聊" });
        }

        var body = await JsonSerializer.DeserializeAsync<JsonObject>(request.Body, Json).ConfigureAwait(false);
        var content = body?["content"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return Results.BadRequest(new { error = "内容为空" });
        }

        var mention = body?["mention"]?.GetValue<bool>() ?? true;
        var before = mock.Sent.Count;

        await mock.InjectAsync(new IncomingMessage
        {
            ChatId = "mock-group",
            Kind = ChatKind.Group,
            OpenId = "mock_user_001",
            Content = content,
            MentionsBot = mention,
            MessageId = Guid.NewGuid().ToString("N")[..16],
            Timestamp = DateTimeOffset.UtcNow,
            ChatDisplayName = "本地测试群",
        }).ConfigureAwait(false);

        // 等运行时走完防抖 + 打字延迟，最多 9 秒。
        for (var i = 0; i < 45; i++)
        {
            await Task.Delay(200).ConfigureAwait(false);
            if (mock.Sent.Count > before)
            {
                break;
            }
        }

        var replies = mock.Sent.Skip(before)
            .Select(m => new { label = _host.Settings.Persona.BotName, content = m.Content })
            .ToArray();

        return Results.Json(new
        {
            replies,
            reason = replies.Length > 0 ? null : "本轮没有触发回复（未 @ 且未命中关键词/疑问句，或处于冷却中）",
        }, Json);
    }

    private async Task<IResult> Rotate(HttpRequest request)
    {
        var body = await JsonSerializer.DeserializeAsync<JsonObject>(request.Body, Json).ConfigureAwait(false);
        var chatId = body?["chatId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(chatId))
        {
            return Results.BadRequest(new { error = "缺少 chatId" });
        }

        await _host.Runtime.ForceRotateAsync(chatId).ConfigureAwait(false);
        return Results.Json(new { ok = true }, Json);
    }

    private async Task<IResult> Forget(HttpRequest request)
    {
        var body = await JsonSerializer.DeserializeAsync<JsonObject>(request.Body, Json).ConfigureAwait(false);
        var openId = body?["openId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(openId))
        {
            return Results.BadRequest(new { error = "缺少 openId" });
        }

        var removed = _host.Identity.Forget(openId);
        return Results.Json(new { ok = true, removed }, Json);
    }

    private async Task<IResult> Rollback(HttpRequest request)
    {
        var body = await JsonSerializer.DeserializeAsync<JsonObject>(request.Body, Json).ConfigureAwait(false);
        var batchId = body?["batchId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(batchId))
        {
            return Results.BadRequest(new { error = "缺少 batchId" });
        }

        var reverted = _host.OpLog.Revert(batchId);
        return Results.Json(new { ok = true, reverted }, Json);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
