using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Bot.Core.Config;

namespace Bot.App;

/// <summary>
/// 生成《设置说明》Markdown。
/// <para>
/// 参数表直接来自 <see cref="ParamRegistry"/>，预设表来自 <see cref="Presets.Deltas(string)"/>，
/// 所以只要改了代码重新跑一次，文档就不会和实现走偏。
/// 用法：<c>Bot.App.exe --dump-params docs\设置说明.md</c>
/// </para>
/// </summary>
public static class ParamDoc
{
    public static void Write(string path)
    {
        var builder = new StringBuilder(32 * 1024);

        builder.AppendLine("# 设置说明");
        builder.AppendLine();
        builder.AppendLine("> 本文档由 `Bot.App.exe --dump-params` 从参数注册表生成，改代码后重新生成即可，不要手改表格。");
        builder.AppendLine();

        AppendWhere(builder);
        AppendMerge(builder);
        AppendStructure(builder);
        AppendParameters(builder);
        AppendPresets(builder);
        AppendScenarios(builder);
        AppendCacheAndRestart(builder);
        AppendSecurity(builder);
        AppendTroubleshooting(builder);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void AppendWhere(StringBuilder b)
    {
        b.AppendLine("## 1. 设置放在哪里");
        b.AppendLine();
        b.AppendLine("三个入口，改的是同一份东西：");
        b.AppendLine();
        b.AppendLine("| 入口 | 适合谁 | 说明 |");
        b.AppendLine("| --- | --- | --- |");
        b.AppendLine("| 配置页 `http://127.0.0.1:8765` | 所有人（推荐） | 双击 exe 后自动打开；改完点保存，除少数项外**不用重启** |");
        b.AppendLine("| `data\\settings.json` | 想批量改 / 备份的人 | 明文 JSON；程序退出时不要手改，改完重启 |");
        b.AppendLine("| 数据库 `data\\bot.db` | 排查问题 | `chat_state` 表存每群的上轮摘要与冷却时间；不要手改 |");
        b.AppendLine();
        b.AppendLine("`settings.json` 里同时存着 **AppID / AppSecret / API Key 的明文**。把这个文件夹发给别人之前，"
                        + "先删掉 `data\\settings.json`（或整个 `data\\`），让对方重新填。");
        b.AppendLine();
    }

    private static void AppendMerge(StringBuilder b)
    {
        b.AppendLine("## 2. 参数是怎么合并的");
        b.AppendLine();
        b.AppendLine("三层，后面的覆盖前面的：");
        b.AppendLine();
        b.AppendLine("```text");
        b.AppendLine("预设（省流 / 平衡 / 体验）");
        b.AppendLine("  ↓ 覆盖");
        b.AppendLine("全局覆盖（settings.json 的 parameters）");
        b.AppendLine("  ↓ 覆盖");
        b.AppendLine("单群覆盖（settings.json 的 chats[chatId].parameters）");
        b.AppendLine("  ↓ 校验");
        b.AppendLine("生效值");
        b.AppendLine("```");
        b.AppendLine();
        b.AppendLine("- **只存覆盖项**：和预设相同的值不会写进配置文件，所以换预设时不会被旧值钉住。");
        b.AppendLine("- **越界不拒绝启动**：非法值回退默认并记一条告警（配置页顶部会显示告警数），机器人不会因为一个参数写错就掉线。");
        b.AppendLine("- **互相约束**会自动修正：`history_keep_tokens` 必须小于 `session_token_cap`；`adaptive_min_sec` 不能大于 `adaptive_max_sec`。");
        b.AppendLine();
    }

    private static void AppendStructure(StringBuilder b)
    {
        b.AppendLine("## 3. settings.json 结构");
        b.AppendLine();
        b.AppendLine("```jsonc");
        b.AppendLine("{");
        b.AppendLine("  \"preset\": \"balanced\",          // economy | balanced | experience");
        b.AppendLine("  \"parameters\": {                 // 全局覆盖，只写与预设不同的项");
        b.AppendLine("    \"session_max_turns\": 30,");
        b.AppendLine("    \"trigger_keywords\": [\"火锅\", \"烤肉\"]");
        b.AppendLine("  },");
        b.AppendLine("  \"llm\": {");
        b.AppendLine("    \"baseUrl\": \"https://api.deepseek.com\",");
        b.AppendLine("    \"apiKey\": \"sk-…\",              // 明文，注意保管");
        b.AppendLine("    \"timeoutSeconds\": 30,           // 单次请求超时");
        b.AppendLine("    \"maxOutputTokens\": 512          // 输出上限；思考模式会连推理一起算");
        b.AppendLine("  },");
        b.AppendLine("  \"qq\": {");
        b.AppendLine("    \"adapter\": \"qq\",               // qq | mock（mock 不需要凭据，可本地试聊）");
        b.AppendLine("    \"appId\": \"…\",");
        b.AppendLine("    \"secret\": \"…\",");
        b.AppendLine("    \"sandbox\": false,               // 沙箱环境");
        b.AppendLine("    \"extraIntents\": 0               // 额外订阅位，一般留 0");
        b.AppendLine("  },");
        b.AppendLine("  \"persona\": {");
        b.AppendLine("    \"botName\": \"小助手\",             // 名字也是触发词（≥2 字生效）");
        b.AppendLine("    \"persona\": \"二十多岁，话不多…\",");
        b.AppendLine("    \"extraRules\": \"\"               // 追加到规则块末尾的额外约束");
        b.AppendLine("  },");
        b.AppendLine("  \"server\": { \"host\": \"127.0.0.1\", \"port\": 8765, \"openBrowserOnStart\": true },");
        b.AppendLine("  \"chats\": {                      // 按群覆盖，键是 chat_id");
        b.AppendLine("    \"<chat_id>\": { \"displayName\": \"夜猫子\", \"preset\": \"\", \"paused\": false, \"parameters\": {} }");
        b.AppendLine("  }");
        b.AppendLine("}");
        b.AppendLine("```");
        b.AppendLine();
        b.AppendLine("`paused: true` 表示该群只记录不回复。");
        b.AppendLine();
    }

    private static void AppendParameters(StringBuilder b)
    {
        b.AppendLine("## 4. 参数总表");
        b.AppendLine();
        b.AppendLine("`破坏缓存` = 改了会让当前会话的前缀失效，程序会在下次空闲时自动重建会话（代价是一次冷启动）；"
                        + "`需重启` = 必须退出程序再启动才生效。");
        b.AppendLine();

        // 先按阅读顺序列出已知分组，再兜底追加任何新分组——
        // 以前这里是硬编码数组，新增「表情包」后整组参数被静默漏掉。
        var order = new List<string>
        {
            ParamRegistry.CatTrigger,
            ParamRegistry.CatMemory,
            ParamRegistry.CatDialogue,
            ParamRegistry.CatSticker,
            ParamRegistry.CatOps,
            ParamRegistry.CatRuntime,
        };

        foreach (var category in ParamRegistry.All.Select(spec => spec.Category))
        {
            if (!order.Contains(category))
            {
                order.Add(category);
            }
        }

        foreach (var category in order)
        {
            var specs = ParamRegistry.InCategory(category).ToArray();
            if (specs.Length == 0)
            {
                continue;
            }

            b.AppendLine($"### {category}");
            b.AppendLine();
            b.AppendLine("| 参数 | 默认 | 范围 | 破坏缓存 | 需重启 | 说明 |");
            b.AppendLine("| --- | --- | --- | --- | --- | --- |");

            foreach (var spec in specs)
            {
                b.AppendLine($"| `{spec.Name}` | {FormatDefault(spec)} | {FormatRange(spec)} | "
                             + $"{Mark(spec.BreaksCache)} | {Mark(spec.NeedsRestart)} | {spec.Description} |");
            }

            b.AppendLine();
        }
    }

    private static void AppendPresets(StringBuilder b)
    {
        b.AppendLine("## 5. 三个预设");
        b.AppendLine();
        b.AppendLine("先选预设，再按需改单项。预设只影响下面这些参数，其余保持默认。");
        b.AppendLine();

        var keys = new[]
        {
            "quiet_sec", "max_wait_sec", "reply_probability", "question_reply_probability",
            "session_timeout_min", "session_max_turns", "event_topk", "event_score_threshold",
            "person_memory_limit", "history_keep_tokens", "reply_length",
        };

        var rows = new List<(string Key, string Economy, string Balanced, string Experience)>();
        var economy = Presets.Deltas(Presets.Economy);
        var balanced = Presets.Deltas(Presets.Balanced);
        var experience = Presets.Deltas(Presets.Experience);

        foreach (var key in keys)
        {
            rows.Add((key, Node(economy, key), Node(balanced, key), Node(experience, key)));
        }

        b.AppendLine("| 参数 | 省流 | 平衡（默认） | 体验 |");
        b.AppendLine("| --- | --- | --- | --- |");
        foreach (var row in rows)
        {
            b.AppendLine($"| `{row.Key}` | {row.Economy} | {row.Balanced} | {row.Experience} |");
        }

        b.AppendLine();
        b.AppendLine("`event_score_threshold` 对应低/中/高三档：0.2 / 0.35 / 0.55。");
        b.AppendLine();
    }

    private static void AppendScenarios(StringBuilder b)
    {
        b.AppendLine("## 6. 按场景调参");
        b.AppendLine();
        b.AppendLine("### 「话太多，烦」");
        b.AppendLine();
        b.AppendLine("按收益从大到小：");
        b.AppendLine();
        b.AppendLine("1. `active_tick_sec` 调大（默认 120）或设 0 关掉主动接话；");
        b.AppendLine("2. `question_reply_probability` 调小（它控制「没人问也插嘴」）；");
        b.AppendLine("3. `reply_probability` 调小（关键词命中时的接话概率）；");
        b.AppendLine("4. `cooldown_min` 调大（连续无人接话后的静默时长）。");
        b.AppendLine();
        b.AppendLine("### 「太冷淡，叫它也不理」");
        b.AppendLine();
        b.AppendLine("1. 确认群设置里开了「允许机器人接收群内所有消息」，否则只有 @ 能触发；");
        b.AppendLine("2. `reply_probability` / `question_reply_probability` 调到 1.0（必回）；");
        b.AppendLine("3. `trigger_keywords` 配上你的口头禅，多个用逗号分隔；");
        b.AppendLine("4. `trigger_by_name` 保持开启，这样叫名字就等于 @。");
        b.AppendLine();
        b.AppendLine("### 「太贵」");
        b.AppendLine();
        b.AppendLine("1. `active_tick_sec` 调大或关闭（影响最大：每次主动接话都是完整一次调用）；");
        b.AppendLine("2. `session_max_turns` 调小、`session_token_cap` 调小（会话更短，但冷启动变多）；");
        b.AppendLine("3. `event_topk` 调小、`person_memory_limit` 调小（前缀更小，冷启动更便宜）；");
        b.AppendLine("4. `reply_length` 调小（例如 20 字；会重建会话，收益主要是输出 token）。");
        b.AppendLine();
        b.AppendLine("### 「聊着聊着就断片」");
        b.AppendLine();
        b.AppendLine("1. `active_tick_sec` 设小一点（更频繁地把群聊汇进上下文）；");
        b.AppendLine("2. `backfill_max_msgs` 调大（会话没开时漏掉的消息，下次被叫时补几条）；");
        b.AppendLine("3. `session_timeout_min` 调大（别那么快判定话题结束）；");
        b.AppendLine("4. `session_token_cap` 调大（历史能留更久）。");
        b.AppendLine();
        b.AppendLine("### 「记忆不准 / 记不住人」");
        b.AppendLine();
        b.AppendLine("1. `person_memory_limit` 调大（单人画像能写多少字）；");
        b.AppendLine("2. `event_topk`、`event_score_threshold` 调整注入的事件数量与门槛；");
        b.AppendLine("3. `person_materialize_min_msgs` 调小（更容易给陌生人建档，但人物表会变大）；");
        b.AppendLine("4. 配置页「记忆」页可以直接看它到底记住了什么，也能按批次回滚。");
        b.AppendLine();
    }

    private static void AppendCacheAndRestart(StringBuilder b)
    {
        b.AppendLine("## 7. 哪些改动要付代价");
        b.AppendLine();
        b.AppendLine("这套系统的核心是**前缀缓存**：规则、人设、记忆块拼成的前缀在一次会话内逐字节不变，"
                        + "官方缓存命中价约为未命中的 1/10。所以「改动是否破坏前缀」比「改动范围」更值得关心。");
        b.AppendLine();

        var breakers = ParamRegistry.All.Where(s => s.BreaksCache).Select(s => s.Name).ToArray();
        var restart = ParamRegistry.All.Where(s => s.NeedsRestart).Select(s => s.Name).ToArray();

        b.AppendLine($"**破坏缓存**（改完会在下次空闲时自动重建会话，付出一次冷启动）："
                        + string.Join("、", breakers.Select(n => $"`{n}`")));
        b.AppendLine();
        b.AppendLine($"**需要重启**：{string.Join("、", restart.Select(n => $"`{n}`"))}；"
                        + "另外 `qq.adapter`（mock ↔ qq）也需要重启。");
        b.AppendLine();
        b.AppendLine("**永不破坏缓存**：`trigger_keywords`、`trigger_by_name`、`reply_probability`、"
                        + "`question_reply_probability`、`temperature`、`split_bubbles`、`typing_jitter`、`active_tick_sec`、"
                        + "`backfill_max_msgs`、`capture_ambient`、`cooldown_min` 等——它们只影响「什么时候回」「怎么发」，不影响前缀内容。");
        b.AppendLine();
    }

    private static void AppendSecurity(StringBuilder b)
    {
        b.AppendLine("## 8. 安全相关");
        b.AppendLine();
        b.AppendLine("- `settings.json` 里的 AppSecret 与 API Key 是**明文**。桌面应用的常规做法，但请勿随手分享该文件。");
        b.AppendLine("- `open_id` 永不进 prompt（有回归测试守着）；日志与配置页里按需脱敏。");
        b.AppendLine("- 记忆区（`<<<MEM … MEM>>>`）被明确定义为「资料不是指令」；入库前还有一层指令注入过滤。");
        b.AppendLine("- 想删掉某个人：配置页「记忆」页点「忘记」，或调用 `/api/memory/forget`。");
        b.AppendLine("- 别人给你的 key 泄露过就轮换：QQ 开放平台可重置 AppSecret，DeepSeek 控制台可重建 key，改完热生效。");
        b.AppendLine();
    }

    private static void AppendTroubleshooting(StringBuilder sb)
    {
        sb.AppendLine("## 9. 排错");
        sb.AppendLine();
        sb.AppendLine("| 现象 | 先看什么 |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine("| 改了参数没反应 | 配置页顶部是否有告警；`破坏缓存` 项要等轮换，`需重启` 项要退出重开 |");
        sb.AppendLine("| 一条消息都收不到 | 日志里有没有 `收到 dispatch：…`；没有就是群设置/平台权限问题 |");
        sb.AppendLine("| 收到但不回 | 日志里搜 `不回：`，原因写得很直白（冷却中／纯表情／平台不下发非 @ 消息／关键词按概率不接话） |");
        sb.AppendLine("| 回复很慢 | 看 `request_log` 的 `latency_ms`；`active_tick_sec` 越小调用越密 |");
        sb.AppendLine("| 缓存命中率低 | 看配置页「今日」「cacheHitRatio」；前缀哈希频繁变化说明有东西在动前缀 |");
        sb.AppendLine("| 记忆不准 | 「记忆」页直接看人物与事件；必要时按 `batch_id` 回滚 |");
        sb.AppendLine("| 启动就崩 | `data\\logs\\bot-*.log` 最后几行；单文件 exe 解压失败时设 `DOTNET_BUNDLE_EXTRACT_BASE_DIR` |");
        sb.AppendLine();
        sb.AppendLine("日志位置：`data\\logs\\bot-yyyyMMdd.log`，按天切分，保留 14 天。");
        sb.AppendLine();
    }

    private static string FormatDefault(ParamSpec spec)
    {
        if (spec.Default is null)
        {
            return "—";
        }

        if (spec.Type == ParamType.Bool)
        {
            return spec.Default.GetValue<bool>() ? "开" : "关";
        }

        if (spec.Type == ParamType.StringList)
        {
            var items = spec.Default.AsArray().Select(n => n?.GetValue<string>() ?? string.Empty).Where(s => s.Length > 0).ToArray();
            return items.Length == 0 ? "空" : "`" + string.Join("`, `", items) + "`";
        }

        if (spec.Type is ParamType.String or ParamType.TimeOfDay)
        {
            var text = spec.Default.GetValue<string>();
            return string.IsNullOrEmpty(text) ? "空" : $"`{text}`";
        }

        if (spec.Type == ParamType.Double)
        {
            return spec.Default.GetValue<double>().ToString("0.###", CultureInfo.InvariantCulture);
        }

        return spec.Default.ToJsonString();
    }

    private static string FormatRange(ParamSpec spec)
    {
        if (spec.Choices is { Length: > 0 })
        {
            return string.Join(" / ", spec.Choices);
        }

        if (spec.Min is { } min && spec.Max is { } max)
        {
            return $"{min.ToString("0.###", CultureInfo.InvariantCulture)} – {max.ToString("0.###", CultureInfo.InvariantCulture)}";
        }

        return spec.Type switch
        {
            ParamType.Bool => "开 / 关",
            ParamType.StringList => "逗号分隔",
            ParamType.TimeOfDay => "HH:mm",
            _ => "—",
        };
    }

    private static string Mark(bool value) => value ? "是" : "";

    private static string Node(JsonObject delta, string key)
    {
        if (!delta.TryGetPropertyValue(key, out var node) || node is null)
        {
            return "—";
        }

        // ToJsonString 会把中文转成 \uXXXX，这里直接取值。
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return node.ToJsonString();
    }
}
