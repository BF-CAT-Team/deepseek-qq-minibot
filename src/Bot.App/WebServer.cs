using System.Text.Json;
using System.Text.Json.Nodes;
using Bot.Core.Abstractions;
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
        app.MapGet("/api/session", GetSession);
        app.MapGet("/api/prompt-framework", () => Results.Json(_host.Framework.Build(), Json));
        app.MapPost("/api/simulate", Simulate);
        app.MapPost("/api/rotate", Rotate);
        app.MapPost("/api/memory/forget", Forget);
        app.MapPost("/api/memory/rollback", Rollback);
        app.MapGet("/api/stickers", () => Results.Json(ListStickers(), Json));
        app.MapPost("/api/stickers", SaveSticker);
        app.MapPost("/api/stickers/test", TestStickers);
        app.MapGet("/api/stickers/log", (int? limit) => Results.Json(RecentStickerLog(limit ?? 50), Json));
        app.MapPost("/api/stickers/{id:int}/enabled", SetStickerEnabled);
        app.MapDelete("/api/stickers/{id:int}", DeleteSticker);
        app.MapGet("/api/stickers/{id:int}/image", GetStickerImage);

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
        var adapterStatus = DescribeAdapter();

        return new
        {
            adapter = _host.AdapterName,
            connected = adapterStatus.Online,
            adapterStatus,
            qq = new
            {
                configured = string.Equals(_host.Settings.Qq.Adapter, "qq", StringComparison.OrdinalIgnoreCase),
                appIdSet = !string.IsNullOrWhiteSpace(_host.Settings.Qq.AppId),
                secretSet = !string.IsNullOrWhiteSpace(_host.Settings.Qq.Secret),
                runningAdapter = _host.AdapterName,
            },
            persons = db.ScalarLong("SELECT COUNT(*) FROM person WHERE status='active'"),
            events = db.ScalarLong("SELECT COUNT(*) FROM event WHERE status='active'"),
            archived = db.ScalarLong("SELECT COUNT(*) FROM event WHERE status='archived'"),
            stickers = db.ScalarLong("SELECT COUNT(*) FROM sticker WHERE enabled=1"),
            todayTokens = cost.PromptTokens + cost.CompletionTokens,
            todayRequests = cost.Requests,
            cacheHitRatio = Math.Round(cost.PrefixHitRatio, 4),
            walPending = _host.Wal.Count(),
            chats = _host.Runtime.Snapshot().Select(c => DescribeChat(c)),
            warnings = _host.Config.Warnings,
            dataDirectory = AppPaths.Root,
        };
    }

    /// <summary>
    /// 把适配器的真实状态翻译成页面能直接显示的内容。
    /// <para>配置里选的和进程里跑的可能不是同一个（切换适配器要重启），这里必须说清楚，
    /// 否则用户会以为"选了 QQ 就是在收 QQ 消息"。</para>
    /// </summary>
    /// <summary>
    /// 会话列表的一行。
    /// <para>
    /// 会话因空闲/轮次/超时关闭后，运行时会把内存里的 <c>Session</c> 释放掉（<c>sessionId</c> 变成 null），
    /// 但库里的记录还在。这里补一个轻量索引，让"空闲"的聊天仍然显示上一次的轮次/前缀哈希，
    /// 并且能点回去看当时的 API 对话。
    /// </para>
    /// </summary>
    private object DescribeChat(ChatSnapshot chat)
    {
        var live = !string.IsNullOrEmpty(chat.SessionId);
        var last = live ? null : _host.Inspector.FindLatestBrief(chat.ChatId);

        DateTimeOffset? lastActivity = chat.LastActivityAt;
        if (last is not null)
        {
            lastActivity ??= DateTimeOffset.FromUnixTimeSeconds(last.LastAt);
        }

        return new
        {
            chatId = chat.ChatId,
            displayName = chat.DisplayName,
            kind = chat.Kind.ToString().ToLowerInvariant(),
            sessionId = chat.SessionId,
            lastSessionId = last?.SessionId,
            state = chat.State.ToString(),
            receivesAll = chat.ReceivesAllGroupMessages,
            turnCount = live ? chat.TurnCount : (last?.TurnCount ?? 0),
            pendingCount = chat.PendingCount,
            prefixHash = live ? (chat.PrefixHash ?? string.Empty) : (last?.PrefixHash ?? string.Empty),
            lastActivityAt = lastActivity,
        };
    }

    private AdapterStatus DescribeAdapter()
    {
        var qq = _host.Settings.Qq;
        var wantsQq = string.Equals(qq.Adapter, "qq", StringComparison.OrdinalIgnoreCase);
        var runningQq = string.Equals(_host.AdapterName, "qq", StringComparison.OrdinalIgnoreCase);
        var status = _host.Adapter.Status;

        if (wantsQq && !runningQq)
        {
            var missing = string.IsNullOrWhiteSpace(qq.AppId) || string.IsNullOrWhiteSpace(qq.Secret);
            return status with
            {
                Online = false,
                State = "error",
                Summary = missing
                    ? "配置为 QQ 官方机器人，但 AppID / AppSecret 未填齐，本次已回退为 Mock"
                    : "配置为 QQ，但当前进程仍运行 Mock（切换适配器后需要重启程序）",
                Detail = "当前生效的适配器：" + _host.AdapterName,
            };
        }

        if (!wantsQq && runningQq)
        {
            return status with
            {
                Online = false,
                State = "pending-restart",
                Summary = "配置已改为 Mock，但当前进程仍连着 QQ 网关（重启后生效）",
                Detail = "当前生效的适配器：qq",
            };
        }

        return status;
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

        // AppSecret 同样不回显：页面只需要知道"填过没有"，保存时留空 = 不修改。
        var qq = node["qq"]?.AsObject();
        if (qq is not null)
        {
            var hasSecret = !string.IsNullOrWhiteSpace(qq["secret"]?.GetValue<string>());
            qq["secret"] = string.Empty;
            qq["secretSet"] = hasSecret;
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

    /// <summary>
    /// 会话的 API 侧视图：点开"当前会话"时看的就是它。
    /// 支持 <c>sessionId</c>（精确）与 <c>chatId</c>（该聊天最近的会话），两者都给时以 sessionId 为准。
    /// </summary>
    private IResult GetSession(string? chatId, string? sessionId)
    {
        try
        {
            SessionView? view;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                view = _host.Inspector.Get(sessionId.Trim());
            }
            else if (!string.IsNullOrWhiteSpace(chatId))
            {
                view = _host.Inspector.FindLatest(chatId.Trim());
            }
            else
            {
                return Results.BadRequest(new { error = "缺少 chatId 或 sessionId" });
            }

            if (view is null)
            {
                return Results.NotFound(new { error = "没有找到这个会话（可能还没有开过会话）" });
            }

            var live = _host.Runtime.Snapshot().FirstOrDefault(c => c.ChatId == view.ChatId);
            if (live is not null)
            {
                view = view with
                {
                    ChatName = string.IsNullOrWhiteSpace(view.ChatName) ? live.DisplayName : view.ChatName,
                    Kind = live.Kind.ToString().ToLowerInvariant(),
                    // 只有当前内存里的会话就是它时，才把运行时状态贴上去；
                    // 否则看历史会话会误显示成"进行中"。
                    LiveState = string.Equals(live.SessionId, view.SessionId, StringComparison.Ordinal)
                        ? live.State.ToString().ToLowerInvariant()
                        : null,
                };
            }

            return Results.Json(view, Json);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
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

        // 等运行时走完防抖 + 打字延迟 + 表情包（表情包在正文之后才发），
        // 以「一段时间没有新消息」作为结束条件，最多 12 秒。
        var deadline = DateTime.UtcNow.AddSeconds(12);
        var lastCount = before;
        var stableSince = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(150).ConfigureAwait(false);
            var count = mock.Sent.Count;
            if (count != lastCount)
            {
                lastCount = count;
                stableSince = DateTime.UtcNow;
                continue;
            }

            if (count > before && DateTime.UtcNow - stableSince >= TimeSpan.FromMilliseconds(1500))
            {
                break;
            }
        }

        var botName = _host.Settings.Persona.BotName;
        var replies = new List<object>();
        foreach (var sent in mock.Sent.Skip(before))
        {
            if (sent.Sticker is { } sticker)
            {
                replies.Add(new
                {
                    kind = "sticker",
                    label = botName,
                    content = string.Empty,
                    stickerId = sticker.StickerId,
                    imageUrl = $"/api/stickers/{sticker.StickerId}/image",
                });
            }
            else
            {
                replies.Add(new
                {
                    kind = "text",
                    label = botName,
                    content = sent.Content,
                    stickerId = 0,
                    imageUrl = string.Empty,
                });
            }
        }

        return Results.Json(new
        {
            replies,
            reason = replies.Count > 0 ? null : "本轮没有触发回复（未 @ 且未命中关键词/疑问句，或处于冷却中）",
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

    // ------------------------------------------------------------------ 表情包

    private object ListStickers() => _host.Stickers.List().Select(row => new
    {
        id = row.StickerId,
        keywords = row.Keywords,
        enabled = row.Enabled,
        useCount = row.SendCount,
        lastSentAt = row.LastSentAt,
        originalName = row.OriginalName,
        createdAt = row.CreatedAt,
        updatedAt = row.UpdatedAt,
        imageUrl = $"/api/stickers/{row.StickerId}/image?v={row.UpdatedAt}",
    }).ToArray();

    private static object DescribeSticker(StickerRow row) => new
    {
        id = row.StickerId,
        keywords = row.Keywords,
        enabled = row.Enabled,
        useCount = row.SendCount,
        lastSentAt = row.LastSentAt,
        originalName = row.OriginalName,
        updatedAt = row.UpdatedAt,
        imageUrl = $"/api/stickers/{row.StickerId}/image?v={row.UpdatedAt}",
    };

    private async Task<IResult> SaveSticker(HttpRequest request)
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<JsonObject>(request.Body, Json).ConfigureAwait(false)
                       ?? new JsonObject();

            var id = JsonInt(body, "id", 0);
            var keywords = JsonString(body, "keywords");
            var enabled = JsonBool(body, "enabled", true);
            var image = DecodeImage(JsonString(body, "dataUrl"));
            var originalName = JsonString(body, "fileName");
            var input = new StickerInput(keywords, enabled, image,
                string.IsNullOrWhiteSpace(originalName) ? null : originalName);

            var row = id > 0 ? _host.Stickers.Update(id, input) : _host.Stickers.Create(input);
            return Results.Json(new { ok = true, sticker = DescribeSticker(row) }, Json);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private async Task<IResult> SetStickerEnabled(int id, HttpRequest request)
    {
        var body = await JsonSerializer.DeserializeAsync<JsonObject>(request.Body, Json).ConfigureAwait(false);
        var enabled = JsonBool(body, "enabled", true);
        return _host.Stickers.SetEnabled(id, enabled)
            ? Results.Json(new { ok = true }, Json)
            : Results.NotFound(new { error = "表情包不存在" });
    }

    private IResult DeleteSticker(int id) =>
        _host.Stickers.Delete(id)
            ? Results.Json(new { ok = true }, Json)
            : Results.NotFound(new { error = "表情包不存在" });

    private IResult GetStickerImage(int id)
    {
        var image = _host.Stickers.ReadImage(id);
        if (image is null)
        {
            return Results.NotFound(new { error = "图片不存在（文件可能已被删除）" });
        }

        return Results.File(image.Value.Bytes, image.Value.ContentType);
    }

    /// <summary>只算不发：拿一段文本试算每张图的得分，方便调关键词和阈值。</summary>
    private async Task<IResult> TestStickers(HttpRequest request)
    {
        var body = await JsonSerializer.DeserializeAsync<JsonObject>(request.Body, Json).ConfigureAwait(false);
        var text = JsonString(body, "text");
        var cfg = _host.Config.ForChat(string.Empty);
        var scores = StickerMatch.Score(text, _host.Stickers.List(), cfg.StickerMinWeight);
        var best = StickerMatch.Best(scores);

        return Results.Json(new
        {
            enabled = cfg.StickerEnabled,
            probability = cfg.StickerProbability,
            threshold = cfg.StickerMinWeight,
            bestId = best?.Sticker.StickerId,
            bestScore = best?.Score,
            scores = scores.OrderByDescending(score => score.Score).Select(score => new
            {
                id = score.Sticker.StickerId,
                keywords = score.Sticker.Keywords,
                enabled = score.Sticker.Enabled,
                useCount = score.Sticker.SendCount,
                score = score.Score,
                threshold = score.Threshold,
                meets = score.MeetsThreshold,
                hitText = score.HitText,
                hits = score.Hits.Select(hit => new { word = hit.Word, weight = hit.Weight, count = hit.Count }),
                imageUrl = $"/api/stickers/{score.Sticker.StickerId}/image?v={score.Sticker.UpdatedAt}",
            }),
        }, Json);
    }

    private object RecentStickerLog(int limit) => _host.Stickers.RecentLog(limit).Select(row => new
    {
        id = row.Id,
        chatId = row.ChatId,
        stickerId = row.StickerId,
        keywords = row.Keywords,
        score = row.Score,
        threshold = row.Threshold,
        probability = row.Probability,
        roll = row.Roll,
        result = row.Result,
        mode = row.Mode,
        replyExcerpt = row.ReplyExcerpt,
        createdAt = row.CreatedAt,
    }).ToArray();

    private static string JsonString(JsonObject? body, string name)
    {
        var node = body?[name];
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text ?? string.Empty : string.Empty;
    }

    private static int JsonInt(JsonObject? body, string name, int fallback)
    {
        var node = body?[name];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var number))
            {
                return number;
            }

            if (value.TryGetValue<double>(out var real))
            {
                return (int)Math.Round(real);
            }
        }

        return fallback;
    }

    private static bool JsonBool(JsonObject? body, string name, bool fallback)
    {
        var node = body?[name];
        return node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;
    }

    /// <summary>浏览器传的是 data URL（data:image/png;base64,…）；也接受裸 base64。</summary>
    private static byte[]? DecodeImage(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return null;
        }

        var text = dataUrl.Trim();
        var comma = text.IndexOf(',');
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
        {
            text = text[(comma + 1)..];
        }

        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            throw new ArgumentException("图片数据不是有效的 base64");
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
