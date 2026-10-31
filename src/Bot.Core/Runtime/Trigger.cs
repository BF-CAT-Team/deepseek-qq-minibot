using System.Text.RegularExpressions;
using Bot.Core.Config;
using Bot.Core.Domain;

namespace Bot.Core.Runtime;

public sealed record TriggerContext(
    DateTimeOffset Now,
    IReadOnlyList<IncomingMessage> Recent,
    bool ReceivesAllMessages,
    int ConsecutiveUnanswered = 0,
    DateTimeOffset? CooldownUntil = null,
    string? BotName = null);

public sealed record TriggerOutcome(bool Triggered, TriggerSource Source, ReplyDecision Decision, string Reason)
{
    public static readonly TriggerOutcome SkipNothing = new(false, TriggerSource.None, ReplyDecision.Never, "未触发");
}

/// <summary>
/// 触发与回复决策（文档 3.1.1 / 3.1.2）。全部本地计算，零 token。
/// <para>三点容易被实现者忽略：触发 ≠ 必回；"不回"也是产品行为；纯陈述命中关键词只给概率。</para>
/// </summary>
public sealed class TriggerEngine
{
    private const int AnswerWindowSeconds = 10;
    private const int QuestionWaitSeconds = 30;
    private const int BotSilenceThreshold = 3;

    /// <summary>平台/适配器塞进正文的占位符，不代表任何可读内容。</summary>
    private static readonly Regex PlaceholderPattern = new(
        @"\[(?:表情|图片|语音|视频|动画表情|文件|链接|位置|名片|红包|转账|音乐|小程序|聊天记录)\]",
        RegexOptions.Compiled);

    private readonly Func<double> _roll;

    public TriggerEngine(Func<double>? roll = null) => _roll = roll ?? (() => Random.Shared.NextDouble());

    public TriggerOutcome EvaluateBatch(IReadOnlyList<IncomingMessage> batch, TriggerContext context, EffectiveConfig cfg)
    {
        if (batch.Count == 0)
        {
            return TriggerOutcome.SkipNothing;
        }

        if (context.CooldownUntil is { } until && context.Now < until)
        {
            return new TriggerOutcome(false, TriggerSource.None, ReplyDecision.Never,
                $"静默冷却至 {TimeText.Format(until)}");
        }

        if (context.ConsecutiveUnanswered >= BotSilenceThreshold)
        {
            return new TriggerOutcome(false, TriggerSource.None, ReplyDecision.Never, "连续多条无人接话，进入冷却");
        }

        var meaningful = batch.Where(m => !m.IsSelf && !IsOnlyNoise(m.Content)).ToArray();
        if (meaningful.Length == 0)
        {
            return new TriggerOutcome(false, TriggerSource.None, ReplyDecision.Never, "纯表情/空内容");
        }

        // 1) @机器人 / 回复机器人消息 —— 最高优先级，必回
        var mentioned = meaningful.Any(m => m.MentionsBot)
            || meaningful.Any(m => m.ReplyToMessageId is not null
                && context.Recent.Any(r => r.IsSelf && r.MessageId == m.ReplyToMessageId));
        if (mentioned)
        {
            return new TriggerOutcome(true, TriggerSource.Mention, ReplyDecision.Must, "@了机器人");
        }

        if (!context.ReceivesAllMessages)
        {
            // 没开全量时，平台只会推 @ 消息；走到这里说明这是条非 @ 消息（例如 Mock 调试）。
            return new TriggerOutcome(false, TriggerSource.None, ReplyDecision.Never, "平台不下发非 @ 消息");
        }

        if (meaningful.Any(m => IsRepeatedSpam(m, context.Recent)))
        {
            return new TriggerOutcome(false, TriggerSource.None, ReplyDecision.Never, "重复刷屏");
        }

        var last = meaningful[^1];
        var text = last.Content;
        var isQuestion = LooksLikeQuestion(text);

        // 2) 叫名字 —— 和 @ 同级，默认开启（trigger_by_name）。群里没人会每次都打 @。
        if (cfg.TriggerByName && NamedInBatch(meaningful, context.BotName))
        {
            return new TriggerOutcome(true, TriggerSource.Name, ReplyDecision.Must,
                $"叫了机器人名字「{context.BotName}」");
        }

        // 3) 关键词 —— 整批里任意一条命中即可（连发时关键词常出现在前一条）
        var keyword = meaningful
            .Select(m => MatchKeyword(m.Content, cfg.TriggerKeywords))
            .FirstOrDefault(k => k is not null);

        if (keyword is not null)
        {
            if (isQuestion)
            {
                if (SomeoneAnsweredWithin(last, context, AnswerWindowSeconds))
                {
                    return new TriggerOutcome(false, TriggerSource.Keyword, ReplyDecision.Never, "别人已在 10s 内回应");
                }

                return new TriggerOutcome(true, TriggerSource.Keyword, ReplyDecision.Must, $"关键词「{keyword}」+ 提问");
            }

            var roll = _roll();
            return roll <= cfg.ReplyProbability
                ? new TriggerOutcome(true, TriggerSource.Keyword, ReplyDecision.May, $"关键词「{keyword}」命中")
                : new TriggerOutcome(false, TriggerSource.Keyword, ReplyDecision.Never,
                    $"关键词命中但按概率 {cfg.ReplyProbability:P0} 不接话");
        }

        if (isQuestion)
        {
            if (SomeoneAnsweredWithin(last, context, QuestionWaitSeconds))
            {
                return new TriggerOutcome(false, TriggerSource.Question, ReplyDecision.Never, "疑问句但已有人回答");
            }

            // 疑问句有自己独立的概率，和关键词的 reply_probability 不再是同一个旋钮。
            var roll = _roll();
            return roll <= cfg.QuestionReplyProbability
                ? new TriggerOutcome(true, TriggerSource.Question, ReplyDecision.May,
                    $"疑问句且一段时间无人应答（概率 {cfg.QuestionReplyProbability:P0}）")
                : new TriggerOutcome(false, TriggerSource.Question, ReplyDecision.Never,
                    $"疑问句但按概率 {cfg.QuestionReplyProbability:P0} 不接话");
        }

        return TriggerOutcome.SkipNothing;
    }

    public static string? MatchKeyword(string content, IReadOnlyList<string> keywords)
    {
        if (keywords.Count == 0 || string.IsNullOrEmpty(content))
        {
            return null;
        }

        foreach (var keyword in keywords)
        {
            if (keyword.Length > 0 && content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return keyword;
            }
        }

        return null;
    }

    /// <summary>
    /// 有没有人叫了机器人的名字。名字至少两个字才算，否则单字名会把整群消息都点亮。
    /// </summary>
    public static bool NamedInBatch(IReadOnlyList<IncomingMessage> batch, string? botName)
    {
        if (string.IsNullOrWhiteSpace(botName))
        {
            return false;
        }

        var name = botName.Trim();
        if (name.Length < 2)
        {
            return false;
        }

        return batch.Any(m => !string.IsNullOrEmpty(m.Content)
                              && m.Content.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool LooksLikeQuestion(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        if (content.Contains('?') || content.Contains('？'))
        {
            return true;
        }

        string[] markers =
        {
            "吗", "呢", "怎么", "为什么", "为啥", "什么", "啥", "哪", "多少", "几点", "几个",
            "如何", "能不能", "可不可以", "是不是", "有没有", "要不要", "行不行",
        };

        return markers.Any(marker => content.Contains(marker, StringComparison.Ordinal));
    }

    private static bool SomeoneAnsweredWithin(IncomingMessage message, TriggerContext context, int seconds)
    {
        var deadline = message.Timestamp.AddSeconds(seconds);
        return context.Recent.Any(other =>
            !other.IsSelf
            && other.OpenId != message.OpenId
            && other.Timestamp > message.Timestamp
            && other.Timestamp <= deadline
            && !IsOnlyNoise(other.Content));
    }

    private static bool IsRepeatedSpam(IncomingMessage message, IReadOnlyList<IncomingMessage> recent)
    {
        if (message.Content.Length < 2)
        {
            return false;
        }

        var same = recent
            .Where(r => r.OpenId == message.OpenId && !r.IsSelf)
            .TakeLast(5)
            .Count(r => string.Equals(r.Content.Trim(), message.Content.Trim(), StringComparison.Ordinal));

        return same >= 3;
    }

    /// <summary>纯表情 / 纯标点 / 纯占位符（[表情]）→ 不值得回应，也不进对话。</summary>
    public static bool IsOnlyNoise(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return true;
        }

        // 平台把表情转成 [表情]、图片转成 [图片]；这些占位符本身没有语义。
        var meaningful = PlaceholderPattern.Replace(content, string.Empty);
        foreach (var ch in meaningful)
        {
            if (char.IsLetterOrDigit(ch))
            {
                return false;
            }
        }

        return true;
    }
}
