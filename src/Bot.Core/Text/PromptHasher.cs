using System.Security.Cryptography;
using System.Text;

namespace Bot.Core.Text;

/// <summary>
/// 前缀哈希（文档 8.2）。规范化必须确定，否则"无变更却报漂移"会天天误报。
/// </summary>
public static class PromptHasher
{
    /// <summary>
    /// 规范化：统一换行、去掉行尾空白、去掉首尾空白与 BOM、折叠连续空行。
    /// 不做全角半角转换——那会改变实际发送的字节。
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = normalized.TrimStart('\uFEFF');

        var builder = new StringBuilder(normalized.Length);
        var blankRun = 0;
        foreach (var line in normalized.Split('\n'))
        {
            var trimmed = line.TrimEnd(' ', '\t');
            if (trimmed.Length == 0)
            {
                blankRun++;
                if (blankRun > 1)
                {
                    continue;
                }
            }
            else
            {
                blankRun = 0;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(trimmed);
        }

        return builder.ToString().Trim();
    }

    public static string Compute(string? normalizedPrefix)
    {
        var bytes = Encoding.UTF8.GetBytes(Normalize(normalizedPrefix));
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>返回首个差异的字符下标，-1 表示完全相同。用于告警定位（文档 8.2）。</summary>
    public static int FirstDifference(string? expected, string? actual)
    {
        var a = expected ?? string.Empty;
        var b = actual ?? string.Empty;
        var limit = Math.Min(a.Length, b.Length);
        for (var i = 0; i < limit; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return a.Length == b.Length ? -1 : limit;
    }

    /// <summary>差异位置前后各 40 字符，便于人工定位污染源。</summary>
    public static string DescribeDifference(string? expected, string? actual, int radius = 40)
    {
        var index = FirstDifference(expected, actual);
        if (index < 0)
        {
            return "无差异";
        }

        static string Slice(string? s, int start, int len)
        {
            s ??= string.Empty;
            if (start >= s.Length)
            {
                return "<结尾>";
            }

            return s.Substring(start, Math.Min(len, s.Length - start));
        }

        var from = Math.Max(0, index - radius);
        var tail = Math.Min(radius, Math.Min(expected?.Length ?? 0, actual?.Length ?? 0) - index);
        tail = Math.Max(0, tail);
        return $"首个差异 @ {index}\n  期望: …{Slice(expected, from, radius + tail)}…\n  实际: …{Slice(actual, from, radius + tail)}…";
    }
}
