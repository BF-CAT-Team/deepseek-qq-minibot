using System.Text.RegularExpressions;
using Bot.Core.Config;
using Bot.Core.Text;

namespace Bot.Core.Runtime;

public sealed record ReplyPlan(IReadOnlyList<string> Bubbles, string? MentionLabel);

/// <summary>
/// 出站处理（文档 3.5.4）：一次请求、多条气泡；拟人打字延迟；@ 归属由程序构造。
/// <para>"@名字" 是模型给的语义提示，解析失败<b>不影响投递</b>，只影响归属统计。</para>
/// </summary>
public static partial class Delivery
{
    private static readonly Regex MentionPattern = new(@"^\s*[@＠]([^\s:：,，]{1,12})[\s:：,，]?", RegexOptions.Compiled);

    private static readonly char[] SentenceEnds = { '。', '！', '？', '!', '?', '；', ';', '…', '\n' };

    public static ReplyPlan Plan(string reply, EffectiveConfig cfg)
    {
        // 先清掉"回复："与 markdown 之类的前缀，再解析 @ —— 顺序反了就会漏掉 @。
        var text = StripFormatting(reply ?? string.Empty);
        string? mention = null;

        var match = MentionPattern.Match(text);
        if (match.Success)
        {
            mention = match.Groups[1].Value.Trim();
            text = text[match.Length..].TrimStart();
        }

        // reply_length 是硬上限：提示词只能降低概率，不能保证模型不超长。
        // 这里在切气泡之前截断，保证发出去的总字数不超。
        if (cfg.ReplyLength > 0 && text.Length > cfg.ReplyLength)
        {
            text = TruncateToLength(text, cfg.ReplyLength);
        }

        if (text.Length == 0)
        {
            return new ReplyPlan(Array.Empty<string>(), mention);
        }

        var bubbles = cfg.SplitBubbles
            ? BubbleSplitter.Split(text, cfg.MaxReplyBubbles)
            : new[] { text };

        return new ReplyPlan(bubbles.Where(b => b.Length > 0).ToArray(), mention);
    }

    /// <summary>超长回复截断：优先在句子边界收尾，实在没有才硬切并补省略号。</summary>
    internal static string TruncateToLength(string text, int limit)
    {
        if (limit <= 0 || text.Length <= limit)
        {
            return text;
        }

        var head = text[..limit];
        var cut = head.LastIndexOfAny(SentenceEnds);
        if (cut >= 0 && cut + 1 >= limit * 6 / 10)
        {
            return head[..(cut + 1)].TrimEnd();
        }

        var keep = Math.Max(0, limit - 1);
        return head[..keep].TrimEnd() + "…";
    }

    public static string StripFormatting(string text)
    {
        var trimmed = text.Trim();
        trimmed = Regex.Replace(trimmed, @"^回复\s*[:：]\s*", string.Empty);
        trimmed = Regex.Replace(trimmed, @"^\*\*|\*\*$", string.Empty);
        trimmed = trimmed.Replace("```", string.Empty);
        return trimmed.Trim();
    }

    /// <summary>拟人延迟：0.6 + 0.02×字数 秒，再叠加 ±30% 抖动（文档 3.5.4 第 2 条）。</summary>
    public static TimeSpan TypingDelay(string firstBubble, EffectiveConfig cfg, Func<double>? random = null)
    {
        if (!cfg.TypingJitter)
        {
            return TimeSpan.Zero;
        }

        var roll = random?.Invoke() ?? Random.Shared.NextDouble();
        var baseSeconds = cfg.TypingBaseSec + (cfg.TypingPerCharSec * firstBubble.Length);
        var jitter = 0.7 + (roll * 0.6); // 0.7x – 1.3x
        var seconds = Math.Clamp(baseSeconds * jitter, 0, 8);
        return TimeSpan.FromSeconds(seconds);
    }

    public static TimeSpan BubbleGap(EffectiveConfig cfg, Func<double>? random = null)
    {
        var min = Math.Min(cfg.BubbleGapMinSec, cfg.BubbleGapMaxSec);
        var max = Math.Max(cfg.BubbleGapMinSec, cfg.BubbleGapMaxSec);
        if (max - min <= 0)
        {
            return TimeSpan.FromSeconds(min);
        }

        var roll = random?.Invoke() ?? Random.Shared.NextDouble();
        return TimeSpan.FromSeconds(min + ((max - min) * roll));
    }
}
