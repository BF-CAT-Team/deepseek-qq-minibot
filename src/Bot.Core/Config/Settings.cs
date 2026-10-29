using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Bot.Core.Config;

/// <summary>落盘的设置。密钥类字段是明文，文件权限依赖用户目录 ACL；后续可用 DPAPI 加密。</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    public string Preset { get; set; } = Presets.Balanced;

    /// <summary>全局覆盖项（只存与默认/预设不同的项）。</summary>
    public JsonObject Parameters { get; set; } = new();

    public Dictionary<string, ChatSettings> Chats { get; set; } = new(StringComparer.Ordinal);

    public LlmSettings Llm { get; set; } = new();

    public QqSettings Qq { get; set; } = new();

    public PersonaSettings Persona { get; set; } = new();

    public ServerSettings Server { get; set; } = new();
}

public sealed class ChatSettings
{
    public string? DisplayName { get; set; }

    /// <summary>群级预设；空=跟随全局。</summary>
    public string Preset { get; set; } = string.Empty;

    /// <summary>群 / 单聊，用于日志与气泡策略。</summary>
    public string Kind { get; set; } = "group";

    /// <summary>暂停：仍然记录消息，但不回复、不调用 API。</summary>
    public bool Paused { get; set; }

    public JsonObject Parameters { get; set; } = new();
}

public sealed class LlmSettings
{
    public string BaseUrl { get; set; } = "https://api.deepseek.com";

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>单次请求超时（文档 3.7.1 默认 30s）。</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>最大输出 token，防止异常长回复烧预算。</summary>
    public int MaxOutputTokens { get; set; } = 512;
}

public sealed class QqSettings
{
    /// <summary>qq=官方机器人 v2；mock=本地模拟（无需凭据即可跑通全流程）。</summary>
    public string Adapter { get; set; } = "mock";

    public string AppId { get; set; } = string.Empty;

    public string Secret { get; set; } = string.Empty;

    /// <summary>v1 的 BotToken（走 old token 换票方式时使用）；v2 用 AppId+Secret。</summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>沙箱环境网关。</summary>
    public bool Sandbox { get; set; }

    /// <summary>
    /// 群主/管理员已在群设置里开启「允许机器人接收群内所有消息」。
    /// 打开后适配器才敢声明"能旁听全群"，关键词触发与话题漂移才有意义。
    /// </summary>
    public bool ReceivesAllMessages { get; set; }

    /// <summary>额外的订阅事件位（一般留 0）。</summary>
    public int ExtraIntents { get; set; }
}

public sealed class PersonaSettings
{
    public string BotName { get; set; } = "小助手";

    public string Persona { get; set; } =
        "二十多岁，话不多但接得住梗；对熟人放松，对生人客气；偶尔用语气词，不用客服腔，不主动说教。";

    /// <summary>追加到规则块末尾的额外约束（按群可覆盖，M4）。</summary>
    public string ExtraRules { get; set; } = string.Empty;
}

public sealed class ServerSettings
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 8765;

    public bool OpenBrowserOnStart { get; set; } = true;
}

public static class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings Load(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                var created = new AppSettings();
                Save(path, created);
                return created;
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null)
            {
                error = "设置文件为空，已使用默认设置";
                return new AppSettings();
            }

            return settings;
        }
        catch (Exception ex)
        {
            error = $"读取设置失败，已使用默认设置：{ex.Message}";
            return new AppSettings();
        }
    }

    public static void Save(string path, AppSettings settings)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>已知模型的上下文窗口，用于把 session_token_cap=0 解析成具体值。</summary>
public static class ModelCatalog
{
    private static readonly Dictionary<string, int> ContextWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        // 2026-10 实测该 Key 可用的模型：deepseek-flash / deepseek-v4-pro。
        // 上下文窗口保守取 64k：即使真实值更大，也只是让轮换略早一点，不会超限报错。
        ["deepseek-flash"] = 64_000,
        ["deepseek-v4-pro"] = 64_000,
        ["deepseek-chat"] = 64_000,
        ["deepseek-reasoner"] = 64_000,
    };

    public const int DefaultContextWindow = 64_000;

    public static int ContextWindow(string model) =>
        ContextWindows.TryGetValue(model ?? string.Empty, out var window) ? window : DefaultContextWindow;

    /// <summary>配置页下拉用。</summary>
    public static readonly string[] KnownModels = { "deepseek-flash", "deepseek-v4-pro" };
}

/// <summary>强类型视图：运行时只读这些属性，避免满代码 stringly-typed。</summary>
public sealed class EffectiveConfig
{
    public EffectiveConfig(ParamSet parameters)
    {
        Params = parameters;
    }

    public ParamSet Params { get; }

    // ---- 触发与会话 ----
    public IReadOnlyList<string> TriggerKeywords => Params.GetStringList("trigger_keywords");

    public bool TriggerByName => Params.GetBool("trigger_by_name");

    /// <summary>被触发时补进上下文的"旁听"消息上限（0=关闭）。</summary>
    public int BackfillMaxMsgs => Params.GetInt("backfill_max_msgs");

    /// <summary>回填的时间窗（分钟）：更旧的发言不算"最近群聊"。</summary>
    public int BackfillWindowMin => Params.GetInt("backfill_window_min");

    /// <summary>会话进行中，收到的群聊是否直接并入对话（不再区分触发与否）。</summary>
    public bool CaptureAmbient => Params.GetBool("capture_ambient");

    /// <summary>
    /// 会话激活后的"主动接话"节拍（秒）。0=关闭，只在被触发时响应。
    /// <para>开启后，每隔这么久把"这段时间的所有群聊"汇总成一批交给模型并回复一次，直到静默超时。</para>
    /// </summary>
    public int ActiveTickSec => Params.GetInt("active_tick_sec");

    public string TriggerSchedule => Params.GetString("trigger_schedule");

    public double QuietSec => Params.GetDouble("quiet_sec");

    public double MaxWaitSec => Params.GetDouble("max_wait_sec");

    public bool AdaptiveWindow => Params.GetBool("adaptive_window");

    public double AdaptiveMinSec => Params.GetDouble("adaptive_min_sec");

    public double AdaptiveMaxSec => Params.GetDouble("adaptive_max_sec");

    public double ReplyProbability => Params.GetDouble("reply_probability");

    /// <summary>疑问句启发式的响应概率，与关键词概率完全独立。</summary>
    public double QuestionReplyProbability => Params.GetDouble("question_reply_probability");

    public int CooldownMin => Params.GetInt("cooldown_min");

    public int RetryOnParseFail => Params.GetInt("retry_on_parse_fail");

    public int SessionTimeoutMin => Params.GetInt("session_timeout_min");

    public int SessionMaxTurns => Params.GetInt("session_max_turns");

    public int SessionTokenCap => Params.GetInt("session_token_cap");

    public double DriftThreshold => Params.GetDouble("drift_threshold");

    // ---- 记忆 ----
    public int PersonMemoryLimit => Params.GetInt("person_memory_limit");

    /// <summary>前缀里最多放几个人物（0=只受预算限制）。</summary>
    public int PrefixPersonMax => Params.GetInt("prefix_person_max");

    /// <summary>会话中途按需补记忆（补进尾部，不动前缀）。</summary>
    public bool OnDemandMemory => Params.GetBool("on_demand_memory") && Params.GetInt("on_demand_max_items") > 0;

    /// <summary>每轮最多补几条。</summary>
    public int OnDemandMaxItems => Params.GetInt("on_demand_max_items");

    public int EventDetailLimit => Params.GetInt("event_detail_limit");

    public int EventTtlDays => Params.GetInt("event_ttl_days");

    public int ArchivePurgeDays => Params.GetInt("archive_purge_days");

    public int EventTopK => Params.GetInt("event_topk");

    public double EventScoreThreshold => Params.GetDouble("event_score_threshold");

    public int PinnedMax => Params.GetInt("pinned_max");

    public double WeightKeyword => Params.GetDouble("weight_keyword");

    public double WeightPerson => Params.GetDouble("weight_person");

    public double WeightLocation => Params.GetDouble("weight_location");

    public double DiversityLambda => Params.GetDouble("diversity_lambda");

    public int MaxOpsPerBatch => Params.GetInt("max_ops_per_batch");

    public int PersonMaterializeMinMsgs => Params.GetInt("person_materialize_min_msgs");

    public int NameLearnConfirmCount => Params.GetInt("name_learn_confirm_count");

    public string SelfOpenId => Params.GetString("self_open_id");

    public bool MaskOpenIdInLogs => Params.GetBool("mask_open_id_in_logs");

    // ---- 对话与输出 ----
    public string Model => Params.GetString("model");

    /// <summary>
    /// 思考模式。deepseek-flash 默认会先产出 reasoning_content，两个字的回复也可能烧掉几十个推理 token；
    /// 聊天场景不需要思考过程，默认关掉。
    /// </summary>
    public string Thinking => Params.GetString("thinking");

    public double Temperature => Params.GetDouble("temperature");

    public string ReplyLength => Params.GetString("reply_length");

    public bool SplitBubbles => Params.GetBool("split_bubbles");

    public bool TypingJitter => Params.GetBool("typing_jitter");

    public int HistoryKeepTokens => Params.GetInt("history_keep_tokens");

    public string SummaryModel => Params.GetString("summary_model");

    // ---- 运维 ----
    public TimeOnly ArchiveJobTime => Params.GetTimeOfDay("archive_job_time");

    public int CacheAlertWindowMin => Params.GetInt("cache_alert_window_min");

    public bool PrefixDriftAlert => Params.GetBool("prefix_drift_alert");

    public int DailyTokenBudget => Params.GetInt("daily_token_budget");

    public int QpsLimit => Params.GetInt("qps_limit");

    // ---- 运行时 ----
    public int MaxReplyBubbles => Params.GetInt("max_reply_bubbles");

    public double BubbleGapMinSec => Params.GetDouble("bubble_gap_min_sec");

    public double BubbleGapMaxSec => Params.GetDouble("bubble_gap_max_sec");

    public double TypingBaseSec => Params.GetDouble("typing_base_sec");

    public double TypingPerCharSec => Params.GetDouble("typing_per_char_sec");

    public int PrefixTokenBudget => Params.GetInt("prefix_token_budget");

    public int PendingBufferMax => Params.GetInt("pending_buffer_max");

    /// <summary>session_token_cap=0 时按模型上下文的 60% 解析。</summary>
    public int EffectiveSessionTokenCap =>
        SessionTokenCap > 0 ? SessionTokenCap : (int)(ModelCatalog.ContextWindow(Model) * 0.6);
}
