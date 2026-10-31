using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bot.Core.Config;

public enum ParamType
{
    Int,
    Double,
    Bool,
    String,
    StringList,
    TimeOfDay,
}

/// <summary>一个可调参数的元信息（对应文档 5.2 的每一行，含"破坏缓存 / 需重启"两列）。</summary>
public sealed record ParamSpec(
    string Name,
    ParamType Type,
    string Category,
    string Description,
    JsonNode? Default,
    double? Min = null,
    double? Max = null,
    string[]? Choices = null,
    bool BreaksCache = false,
    bool NeedsRestart = false);

/// <summary>参数注册表：唯一事实来源。配置页、校验、预设、按群覆盖都从这里派生。</summary>
public static class ParamRegistry
{
    public const string CatTrigger = "触发与会话";
    public const string CatMemory = "记忆";
    public const string CatDialogue = "对话与输出";
    public const string CatSticker = "表情包";
    public const string CatOps = "运维";
    public const string CatRuntime = "运行时";

    private static readonly List<ParamSpec> Specs = new()
    {
        // ---------------- 触发与会话 ----------------
        new("trigger_keywords", ParamType.StringList, CatTrigger, "关键词触发；空列表=关闭。多个用逗号分隔", new JsonArray(), BreaksCache: false),
        new("trigger_by_name", ParamType.Bool, CatTrigger, "消息里出现机器人名字也算触发（与 @ 同级）", JsonValue.Create(true)),
        new("backfill_max_msgs", ParamType.Int, CatTrigger,
            "被触发时，把「自上次回复以来群里的发言」补进上下文的最大条数；0=关闭", JsonValue.Create(12), 0, 50),
        new("backfill_window_min", ParamType.Int, CatTrigger,
            "回填的时间窗：只补最近这么多分钟内的发言，更旧的不补（防止几小时前的旧话题被当成当前对话）",
            JsonValue.Create(10), 1, 120),
        new("capture_ambient", ParamType.Bool, CatTrigger,
            "会话进行中，收到的群聊直接并入对话（不再区分触发与否）", JsonValue.Create(true)),
        new("active_tick_sec", ParamType.Int, CatTrigger,
            "会话激活后，每隔多少秒主动把这段时间的群聊汇总进来并回一次；0=关闭（只被动响应）",
            JsonValue.Create(120), 0, 3600),
        new("trigger_schedule", ParamType.String, CatTrigger, "定时主动发起的 cron 表达式；空=关闭", JsonValue.Create("")),
        new("quiet_sec", ParamType.Double, CatTrigger, "静默窗口：距最后一条新消息满 N 秒即发", JsonValue.Create(2.5), 1, 10),
        new("max_wait_sec", ParamType.Double, CatTrigger, "聚合最大等待：距首条未发消息满 N 秒即发", JsonValue.Create(8d), 3, 30),
        new("adaptive_window", ParamType.Bool, CatTrigger, "按消息密度自动调整静默窗口", JsonValue.Create(true)),
        new("adaptive_min_sec", ParamType.Double, CatTrigger, "自适应窗口下限", JsonValue.Create(2d), 1, 10),
        new("adaptive_max_sec", ParamType.Double, CatTrigger, "自适应窗口上限", JsonValue.Create(5d), 1, 15),
        new("reply_probability", ParamType.Double, CatTrigger, "「可回」情形下的响应概率（关键词触发用）", JsonValue.Create(0.35), 0, 1),
        new("question_reply_probability", ParamType.Double, CatTrigger,
            "疑问句启发式独立的响应概率（与关键词概率分开）", JsonValue.Create(0.35), 0, 1),
        new("cooldown_min", ParamType.Int, CatTrigger, "连续无人接话后的静默分钟数", JsonValue.Create(10), 1, 120),
        new("retry_on_parse_fail", ParamType.Int, CatTrigger, "总结解析失败后的重试次数", JsonValue.Create(1), 0, 2),

        new("session_timeout_min", ParamType.Int, CatTrigger, "空闲多少分钟触发轮换", JsonValue.Create(5), 1, 60, BreaksCache: true),
        new("session_max_turns", ParamType.Int, CatTrigger, "轮次上限（≥ 即轮换）", JsonValue.Create(24), 4, 100, BreaksCache: true),
        new("session_token_cap", ParamType.Int, CatTrigger, "历史区 token 上限；0=按模型自动取 60%", JsonValue.Create(0), 0, 400000, BreaksCache: true),
        new("drift_threshold", ParamType.Double, CatTrigger, "话题漂移阈值，越低越容易换话题", JsonValue.Create(0.15), 0.05, 0.4, BreaksCache: true),

        // ---------------- 记忆 ----------------
        new("prefix_person_max", ParamType.Int, CatMemory,
            "前缀里最多放几个人物（0=只受前缀预算限制）；人多的群建议小一点，其余靠「按需补充」", JsonValue.Create(8), 0, 60, BreaksCache: true),
        new("on_demand_memory", ParamType.Bool, CatMemory,
            "会话中途把「前缀里没有、但这轮被提到」的人和事件补进尾部（只补一次，不进前缀）", JsonValue.Create(true)),
        new("on_demand_max_items", ParamType.Int, CatMemory,
            "每轮最多补几条；0=关闭", JsonValue.Create(4), 0, 10),
        new("person_memory_limit", ParamType.Int, CatMemory, "人物画像硬上限（字）", JsonValue.Create(100), 50, 500, BreaksCache: true),
        new("event_detail_limit", ParamType.Int, CatMemory, "事件详情上限（字）", JsonValue.Create(80), 40, 200, BreaksCache: true),
        new("event_ttl_days", ParamType.Int, CatMemory, "多久未被注入则归档（不是删除）", JsonValue.Create(30), 7, 365, BreaksCache: true),
        new("archive_purge_days", ParamType.Int, CatMemory, "归档后多久物理删除", JsonValue.Create(365), 90, 3650),
        new("event_topk", ParamType.Int, CatMemory, "每次注入的事件条数上限", JsonValue.Create(5), 1, 20, BreaksCache: true),
        new("event_score_threshold", ParamType.Double, CatMemory, "检索分数阈值；低相关即使未满 TopK 也不注入", JsonValue.Create(0.35), 0, 1, BreaksCache: true),
        new("pinned_max", ParamType.Int, CatMemory, "置顶条目注入上限", JsonValue.Create(3), 0, 10, BreaksCache: true),
        new("weight_keyword", ParamType.Double, CatMemory, "关键词归一化得分权重", JsonValue.Create(2d), 0, 10, BreaksCache: true),
        new("weight_person", ParamType.Double, CatMemory, "人物重合度权重", JsonValue.Create(3d), 0, 10, BreaksCache: true),
        new("weight_location", ParamType.Double, CatMemory, "地点匹配权重", JsonValue.Create(2d), 0, 10, BreaksCache: true),
        new("diversity_lambda", ParamType.Double, CatMemory, "结果多样性惩罚（M3 启用）", JsonValue.Create(0d), 0, 1, BreaksCache: true),
        new("max_ops_per_batch", ParamType.Int, CatMemory, "单批入库条数上限", JsonValue.Create(8), 1, 20),
        new("person_materialize_min_msgs", ParamType.Int, CatMemory, "发言多少条才真正建档", JsonValue.Create(2), 1, 10, BreaksCache: true),
        new("name_learn_confirm_count", ParamType.Int, CatMemory, "他人称呼几次才认定为名字", JsonValue.Create(2), 1, 5, BreaksCache: true),
        new("self_open_id", ParamType.String, CatMemory, "机器人自身 open_id 白名单，防自建档", JsonValue.Create("")),
        new("mask_open_id_in_logs", ParamType.Bool, CatMemory, "日志中 open_id 脱敏（前 6 后 4）", JsonValue.Create(true)),

        // ---------------- 对话与输出 ----------------
        new("model", ParamType.String, CatDialogue, "对话模型 id（换模型=换缓存域）", JsonValue.Create("deepseek-flash"), BreaksCache: true, NeedsRestart: true),
        new("thinking", ParamType.String, CatDialogue, "思考模式：disabled=关闭（省钱、响应快）", JsonValue.Create("disabled"),
            Choices: new[] { "disabled", "enabled", "auto" }, BreaksCache: true),
        new("temperature", ParamType.Double, CatDialogue, "采样温度", JsonValue.Create(0.9), 0, 1.5),
        new("reply_length", ParamType.Int, CatDialogue, "回复的最大字符数（写进规则与尾部提示，发送前硬截断）", JsonValue.Create(40), 10, 500, BreaksCache: true),
        new("split_bubbles", ParamType.Bool, CatDialogue, "把一条回复切成 1–3 条气泡", JsonValue.Create(true)),
        new("typing_jitter", ParamType.Bool, CatDialogue, "首条气泡前的拟人延迟抖动", JsonValue.Create(true)),
        new("history_keep_tokens", ParamType.Int, CatDialogue, "历史区保留 token 目标", JsonValue.Create(2400), 600, 8000, BreaksCache: true),
        new("summary_model", ParamType.String, CatDialogue, "总结用的模型；空=同 model", JsonValue.Create("")),

        // ---------------- 表情包 ----------------
        // 关键词、触发最低权重是每张图各自的（存在 sticker 表里）；这里只有全局旋钮。
        new("sticker_enabled", ParamType.Bool, CatSticker,
            "总开关：回复发出后按关键词匹配图片库并追加一张表情包（表情包不进入对话历史）",
            JsonValue.Create(true)),
        new("sticker_probability", ParamType.Double, CatSticker,
            "选出一张表情包后真正发送的概率（全局；每张图不再单独设概率）", JsonValue.Create(0.3), 0, 1),
        new("sticker_min_weight", ParamType.Double, CatSticker,
            "触发最低权重（全局）：回复命中关键词的得分达到这个值才进入候选；每张图不再单独设阈值",
            JsonValue.Create(2d), 0.1, 100),
        new("sticker_cooldown_sec", ParamType.Int, CatSticker,
            "同一个聊天两次表情包之间的最小间隔（秒）；0=不限", JsonValue.Create(45), 0, 3600),
        new("sticker_active_when_no_msg", ParamType.Bool, CatSticker,
            "全量模式下这一轮没有可被动回复的 msg_id（主动接话 / 消息超过 5 分钟）时，是否允许按主动消息发送；关掉=这种场景不发",
            JsonValue.Create(true)),

        // ---------------- 运维 ----------------
        new("archive_job_time", ParamType.TimeOfDay, CatOps, "每日归档扫描时刻 HH:mm", JsonValue.Create("04:00")),
        new("cache_alert_window_min", ParamType.Int, CatOps, "命中率统计窗口（分钟）", JsonValue.Create(30), 5, 240),
        new("prefix_drift_alert", ParamType.Bool, CatOps, "前缀哈希异常告警", JsonValue.Create(true)),
        new("daily_token_budget", ParamType.Int, CatOps, "日 token 预算；0=不限", JsonValue.Create(0), 0, int.MaxValue),
        new("qps_limit", ParamType.Int, CatOps, "全局请求速率上限", JsonValue.Create(2), 1, 20),

        // ---------------- 运行时（实现细节，同样可调） ----------------
        new("max_reply_bubbles", ParamType.Int, CatRuntime, "单条回复最多切几条气泡", JsonValue.Create(3), 1, 5),
        new("bubble_gap_min_sec", ParamType.Double, CatRuntime, "气泡之间的最小间隔", JsonValue.Create(0.4), 0, 5),
        new("bubble_gap_max_sec", ParamType.Double, CatRuntime, "气泡之间的最大间隔", JsonValue.Create(1.2), 0, 8),
        new("typing_base_sec", ParamType.Double, CatRuntime, "拟人延迟基数（秒）", JsonValue.Create(0.6), 0, 10),
        new("typing_per_char_sec", ParamType.Double, CatRuntime, "每字追加延迟（秒）", JsonValue.Create(0.02), 0, 1),
        new("prefix_token_budget", ParamType.Int, CatRuntime, "前缀区 token 预算，用于互相约束校验", JsonValue.Create(1500), 200, 8000),
        new("pending_buffer_max", ParamType.Int, CatRuntime, "总结期间待处理消息缓冲上限", JsonValue.Create(50), 1, 500),
    };

    private static readonly Dictionary<string, ParamSpec> ByName =
        Specs.ToDictionary(s => s.Name, StringComparer.Ordinal);

    public static IReadOnlyList<ParamSpec> All => Specs;

    public static bool TryGet(string name, out ParamSpec spec) => ByName.TryGetValue(name, out spec!);

    public static ParamSpec Require(string name) =>
        ByName.TryGetValue(name, out var spec) ? spec : throw new KeyNotFoundException($"未知参数：{name}");

    public static IEnumerable<ParamSpec> InCategory(string category) =>
        Specs.Where(s => s.Category == category);
}

/// <summary>一组已合并、已校验的参数值。所有值都是 JsonNode，便于原样序列化给配置页。</summary>
public sealed class ParamSet
{
    private readonly Dictionary<string, JsonNode?> _values = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, JsonNode?> Values => _values;

    public static ParamSet Defaults()
    {
        var set = new ParamSet();
        foreach (var spec in ParamRegistry.All)
        {
            set._values[spec.Name] = spec.Default?.DeepClone();
        }

        return set;
    }

    public ParamSet Clone()
    {
        var clone = new ParamSet();
        foreach (var (key, value) in _values)
        {
            clone._values[key] = value?.DeepClone();
        }

        return clone;
    }

    public void Set(string name, JsonNode? value) => _values[name] = value?.DeepClone();

    public JsonNode? GetNode(string name) =>
        _values.TryGetValue(name, out var node) ? node : ParamRegistry.Require(name).Default;

    public bool Contains(string name) => _values.ContainsKey(name);

    public void ApplyOverrides(JsonObject? overrides, ICollection<string>? unknownKeys = null)
    {
        if (overrides is null)
        {
            return;
        }

        foreach (var (key, value) in overrides)
        {
            if (!ParamRegistry.TryGet(key, out _))
            {
                unknownKeys?.Add(key);
                continue;
            }

            _values[key] = value?.DeepClone();
        }
    }

    public int GetInt(string name)
    {
        var node = GetNode(name);
        if (TryToDouble(node, out var number))
        {
            return (int)Math.Round(number, MidpointRounding.AwayFromZero);
        }

        return 0;
    }

    public double GetDouble(string name)
    {
        var node = GetNode(name);
        return TryToDouble(node, out var number) ? number : 0;
    }

    public bool GetBool(string name) => GetNode(name) is JsonValue value && value.TryGetValue<bool>(out var b) && b;

    public string GetString(string name) =>
        GetNode(name) is JsonValue value && value.TryGetValue<string>(out var s) ? s : string.Empty;

    public IReadOnlyList<string> GetStringList(string name)
    {
        var node = GetNode(name);
        switch (node)
        {
            case JsonArray array:
                return array
                    .Select(item => item is JsonValue v && v.TryGetValue<string>(out var s) ? s : item?.ToJsonString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim())
                    .ToArray();
            case JsonValue value when value.TryGetValue<string>(out var text):
                return text.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToArray();
            default:
                return Array.Empty<string>();
        }
    }

    public TimeOnly GetTimeOfDay(string name)
    {
        var text = GetString(name);
        return TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : new TimeOnly(4, 0);
    }

    internal static bool TryToDouble(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue<double>(out value))
        {
            return true;
        }

        if (jsonValue.TryGetValue<int>(out var i))
        {
            value = i;
            return true;
        }

        if (jsonValue.TryGetValue<long>(out var l))
        {
            value = l;
            return true;
        }

        if (jsonValue.TryGetValue<string>(out var s))
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        return false;
    }
}

/// <summary>预设（文档 5.1）。先选预设，再改单项。</summary>
public static class Presets
{
    public const string Economy = "economy";
    public const string Balanced = "balanced";
    public const string Experience = "experience";

    public static readonly string[] All = { Economy, Balanced, Experience };

    public static string DisplayName(string preset) => preset switch
    {
        Economy => "省流",
        Experience => "体验",
        _ => "平衡",
    };

    public static string Normalize(string? preset) => preset switch
    {
        Economy or "省流" => Economy,
        Experience or "体验" => Experience,
        _ => Balanced,
    };

    /// <summary>预设只覆盖文档 5.1 表里的那 10 行，其余用注册表默认值。</summary>
    public static JsonObject Deltas(string preset) => Normalize(preset) switch
    {
        Economy => new JsonObject
        {
            ["quiet_sec"] = 4.0,
            ["max_wait_sec"] = 15.0,
            ["reply_probability"] = 0.15,
            ["question_reply_probability"] = 0.15,
            ["session_timeout_min"] = 3,
            ["session_max_turns"] = 12,
            ["event_topk"] = 3,
            ["event_score_threshold"] = 0.55,
            ["person_memory_limit"] = 80,
            ["history_keep_tokens"] = 1200,
            ["reply_length"] = 20,
        },
        Experience => new JsonObject
        {
            ["quiet_sec"] = 1.5,
            ["max_wait_sec"] = 4.0,
            ["reply_probability"] = 0.6,
            ["question_reply_probability"] = 0.6,
            ["session_timeout_min"] = 10,
            ["session_max_turns"] = 40,
            ["event_topk"] = 8,
            ["event_score_threshold"] = 0.2,
            ["person_memory_limit"] = 160,
            ["history_keep_tokens"] = 4000,
            ["reply_length"] = 80,
        },
        _ => new JsonObject
        {
            ["quiet_sec"] = 2.5,
            ["max_wait_sec"] = 8.0,
            ["reply_probability"] = 0.35,
            ["question_reply_probability"] = 0.35,
            ["session_timeout_min"] = 5,
            ["session_max_turns"] = 24,
            ["event_topk"] = 5,
            ["event_score_threshold"] = 0.35,
            ["person_memory_limit"] = 100,
            ["history_keep_tokens"] = 2400,
            ["reply_length"] = 40,
        },
    };
}

/// <summary>
/// 三层合并：预设 → 全局覆盖 → 群覆盖（文档 5.3）。
/// 校验失败的参数回退默认值并记 warning，<b>不拒绝启动</b>——机器人不能因一个参数写错就掉线。
/// </summary>
public static class ConfigResolver
{
    public static ParamSet Resolve(string preset, JsonObject? globalOverrides, JsonObject? chatOverrides,
        ICollection<string>? warnings = null)
    {
        var set = ParamSet.Defaults();
        set.ApplyOverrides(Presets.Deltas(preset), warnings);
        set.ApplyOverrides(globalOverrides, warnings);
        set.ApplyOverrides(chatOverrides, warnings);
        Validate(set, warnings);
        return set;
    }

    public static void Validate(ParamSet set, ICollection<string>? warnings)
    {
        foreach (var spec in ParamRegistry.All)
        {
            if (!set.Contains(spec.Name))
            {
                set.Set(spec.Name, spec.Default?.DeepClone());
                continue;
            }

            var node = set.GetNode(spec.Name);
            switch (spec.Type)
            {
                case ParamType.Int:
                case ParamType.Double:
                    if (!ParamSet.TryToDouble(node, out var number))
                    {
                        Warn(warnings, $"参数 {spec.Name} 不是数字，已回退默认值");
                        set.Set(spec.Name, spec.Default?.DeepClone());
                        break;
                    }

                    var min = spec.Min ?? double.MinValue;
                    var max = spec.Max ?? double.MaxValue;
                    if (number < min || number > max)
                    {
                        var clamped = Math.Clamp(number, min, max);
                        Warn(warnings, $"参数 {spec.Name} 超出范围 [{min},{max}]，已收敛到 {clamped}");
                        set.Set(spec.Name, spec.Type == ParamType.Int
                            ? JsonValue.Create((int)Math.Round(clamped))
                            : JsonValue.Create(clamped));
                    }

                    break;

                case ParamType.Bool:
                    if (node is not JsonValue || !node.AsValue().TryGetValue<bool>(out _))
                    {
                        Warn(warnings, $"参数 {spec.Name} 不是布尔值，已回退默认值");
                        set.Set(spec.Name, spec.Default?.DeepClone());
                    }

                    break;

                case ParamType.TimeOfDay:
                    var text = set.GetString(spec.Name);
                    if (!TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    {
                        Warn(warnings, $"参数 {spec.Name} 不是 HH:mm 格式，已回退默认值");
                        set.Set(spec.Name, spec.Default?.DeepClone());
                    }

                    break;

                case ParamType.String:
                    if (node is not null && node is not JsonValue)
                    {
                        Warn(warnings, $"参数 {spec.Name} 不是字符串，已回退默认值");
                        set.Set(spec.Name, spec.Default?.DeepClone());
                    }
                    else if (spec.Choices is { Length: > 0 })
                    {
                        var current = set.GetString(spec.Name);
                        if (!spec.Choices.Contains(current, StringComparer.Ordinal))
                        {
                            Warn(warnings, $"参数 {spec.Name} 取值 {current} 不在 {string.Join('/', spec.Choices)} 内，已回退默认值");
                            set.Set(spec.Name, spec.Default?.DeepClone());
                        }
                    }

                    break;

                case ParamType.StringList:
                    if (node is not null and not JsonArray && node is not JsonValue)
                    {
                        Warn(warnings, $"参数 {spec.Name} 不是列表，已回退默认值");
                        set.Set(spec.Name, spec.Default?.DeepClone());
                    }

                    break;
            }
        }

        // 互相约束（文档 5.3）
        var historyKeep = set.GetInt("history_keep_tokens");
        var cap = set.GetInt("session_token_cap");
        if (cap > 0 && historyKeep >= cap)
        {
            Warn(warnings, "history_keep_tokens 应小于 session_token_cap，已自动下调");
            set.Set("history_keep_tokens", JsonValue.Create(Math.Max(600, cap - 200)));
        }

        var adaptiveMin = set.GetDouble("adaptive_min_sec");
        var adaptiveMax = set.GetDouble("adaptive_max_sec");
        if (adaptiveMin > adaptiveMax)
        {
            Warn(warnings, "adaptive_min_sec 大于 adaptive_max_sec，已交换");
            set.Set("adaptive_min_sec", JsonValue.Create(adaptiveMax));
            set.Set("adaptive_max_sec", JsonValue.Create(adaptiveMin));
        }

        var budget = set.GetInt("prefix_token_budget");
        var estimate = (set.GetInt("event_topk") + set.GetInt("pinned_max")) * 28
            + set.GetInt("person_memory_limit") * 2;
        if (estimate > budget)
        {
            Warn(warnings, $"事件/置顶条目估算 {estimate} token 超过前缀预算 {budget}，已下调 event_topk");
            set.Set("event_topk", JsonValue.Create(Math.Max(1, set.GetInt("event_topk") - 1)));
        }
    }

    /// <summary>对比两套参数，返回"改动会破坏前缀缓存"的参数名（文档 5.2 最后一列）。</summary>
    public static IReadOnlyList<string> CacheBreakingChanges(ParamSet before, ParamSet after)
    {
        var changed = new List<string>();
        foreach (var spec in ParamRegistry.All)
        {
            if (!spec.BreaksCache)
            {
                continue;
            }

            var a = before.GetNode(spec.Name)?.ToJsonString();
            var b = after.GetNode(spec.Name)?.ToJsonString();
            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                changed.Add(spec.Name);
            }
        }

        return changed;
    }

    private static void Warn(ICollection<string>? warnings, string message) => warnings?.Add(message);
}
