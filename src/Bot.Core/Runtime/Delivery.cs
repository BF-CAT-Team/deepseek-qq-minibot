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

        if (text.Length == 0)
        {
            return new ReplyPlan(Array.Empty<string>(), mention);
        }

        var bubbles = cfg.SplitBubbles
            ? BubbleSplitter.Split(text, cfg.MaxReplyBubbles)
            : new[] { text };

        return new ReplyPlan(bubbles.Where(b => b.Length > 0).ToArray(), mention);
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
