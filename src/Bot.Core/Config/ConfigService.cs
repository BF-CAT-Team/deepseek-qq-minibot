using System.Text.Json.Nodes;

namespace Bot.Core.Config;

/// <summary>
/// 配置的运行时视图：全局预设 + 全局覆盖 + 按群覆盖，三层合并后按群缓存。
/// 改动只在 <see cref="Update"/> 时整体替换，读路径因此无锁竞争。
/// </summary>
public sealed class ConfigService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EffectiveConfig> _cache = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = new();
    private AppSettings _settings;
    private Dictionary<string, JsonObject> _globalSnapshot = new(StringComparer.Ordinal);

    public ConfigService(AppSettings settings)
    {
        _settings = settings;
        Rebuild();
    }

    public event Action? Changed;

    public AppSettings Raw
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_gate)
            {
                return _warnings.ToArray();
            }
        }
    }

    public string Preset
    {
        get
        {
            lock (_gate)
            {
                return _settings.Preset;
            }
        }
    }

    public EffectiveConfig ForChat(string chatId)
    {
        lock (_gate)
        {
            return _cache.TryGetValue(chatId, out var cached) ? cached : BuildLocked(chatId);
        }
    }

    /// <summary>整体替换设置（保存配置页后调用）。</summary>
    public void Update(AppSettings settings)
    {
        lock (_gate)
        {
            _settings = settings;
            RebuildLocked();
        }

        Changed?.Invoke();
    }

    /// <summary>只改某个群的覆盖项，其余不动。</summary>
    public void UpdateChat(string chatId, Action<ChatSettings> mutate)
    {
        lock (_gate)
        {
            if (!_settings.Chats.TryGetValue(chatId, out var chat))
            {
                chat = new ChatSettings();
                _settings.Chats[chatId] = chat;
            }

            mutate(chat);
            RebuildLocked();
        }

        Changed?.Invoke();
    }

    /// <summary>把"会破坏前缀缓存"的参数改动登记下来，由轮换服务在空闲时执行。</summary>
    public IReadOnlyList<string> CacheBreakingChangesTo(EffectiveConfig candidate, string chatId)
    {
        var current = ForChat(chatId);
        return ConfigResolver.CacheBreakingChanges(current.Params, candidate.Params);
    }

    private void Rebuild()
    {
        lock (_gate)
        {
            RebuildLocked();
        }
    }

    private void RebuildLocked()
    {
        _cache.Clear();
        _warnings.Clear();
        _globalSnapshot = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        var probe = new List<string>();
        ConfigResolver.Resolve(_settings.Preset, _settings.Parameters, null, probe);
        _warnings.AddRange(probe);
    }

    private EffectiveConfig BuildLocked(string chatId)
    {
        JsonObject? chatParameters = null;
        var preset = _settings.Preset;
        if (_settings.Chats.TryGetValue(chatId, out var chat))
        {
            chatParameters = chat.Parameters.Count == 0 ? null : chat.Parameters;
            if (!string.IsNullOrWhiteSpace(chat.Preset))
            {
                preset = Presets.Normalize(chat.Preset);
            }
        }

        var warnings = new List<string>();
        var set = ConfigResolver.Resolve(preset, _settings.Parameters, chatParameters, warnings);
        foreach (var warning in warnings)
        {
            var tagged = $"[{chatId}] {warning}";
            if (!_warnings.Contains(tagged))
            {
                _warnings.Add(tagged);
            }
        }

        var effective = new EffectiveConfig(set);
        _cache[chatId] = effective;
        return effective;
    }
}

public static class Masking
{
    /// <summary>不透明标识的形态：24 位以上连续十六进制（QQ 的 open_id 是 32 位）。</summary>
    private static readonly System.Text.RegularExpressions.Regex OpaqueId =
        new("[A-Fa-f0-9]{24,}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>open_id 脱敏：保留前 6 后 4（文档 3.4.7）。</summary>
    public static string MaskOpenId(string? openId)
    {
        if (string.IsNullOrEmpty(openId))
        {
            return string.Empty;
        }

        if (openId.Length <= 12)
        {
            return openId[..Math.Min(3, openId.Length)] + "***";
        }

        return string.Concat(openId.AsSpan(0, 6), "…", openId.AsSpan(openId.Length - 4));
    }

    public static bool LooksOpaque(string? text) => !string.IsNullOrEmpty(text) && OpaqueId.IsMatch(text);

    /// <summary>
    /// 最后一道保险：把 prompt 里出现的任何 open_id 形态替换成 <c>&lt;id&gt;</c>。
    /// <para>
    /// 文档 1.2 是硬性指标（prompt 中 open_id 出现次数 = 0），而 QQ 群不下发群名，
    /// 一旦哪里用 chat_id 兜底当展示名，就会悄悄把 32 位串写进每一轮请求。
    /// 替换是确定性的，因此不会破坏前缀哈希的稳定性。
    /// </para>
    /// </summary>
    public static string ScrubOpaqueIds(string? text, out int count)
    {
        if (string.IsNullOrEmpty(text))
        {
            count = 0;
            return string.Empty;
        }

        var replaced = 0;
        var result = OpaqueId.Replace(text, _ =>
        {
            replaced++;
            return "<id>";
        });

        count = replaced;
        return result;
    }

    /// <summary>日志里统一走这里，避免 open_id 泄漏到控制台/文件。</summary>
    public static string Redact(string? text, bool mask)
    {
        if (!mask || string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        return OpaqueId.Replace(text, match => MaskOpenId(match.Value));
    }
}
