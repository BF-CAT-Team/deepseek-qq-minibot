using System.Text.Json.Nodes;
using Bot.Core.Config;
using Bot.Core.Domain;
using Bot.Core.Runtime;

namespace Bot.Tests;

/// <summary>
/// 触发点的行为契约：@ / 名字 / 关键词 各算什么强度、多个关键词怎么配、老模式还能不能用。
/// </summary>
public sealed class TriggerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 21, 0, 0, TimeSpan.FromHours(8));

    private static EffectiveConfig Config(string? json = null) =>
        new(ConfigResolver.Resolve(Presets.Balanced, json is null ? null : JsonNode.Parse(json)!.AsObject(), null));

    private static IncomingMessage Msg(string content, bool mentions = false, string openId = "U1", int secondsAgo = 0) =>
        new()
        {
            ChatId = "G1",
            Kind = ChatKind.Group,
            OpenId = openId,
            Content = content,
            MentionsBot = mentions,
            MessageId = Guid.NewGuid().ToString("N")[..8],
            Timestamp = Now.AddSeconds(-secondsAgo),
        };

    private static TriggerContext Ctx(bool receivesAll, params IncomingMessage[] recent) =>
        new(Now, recent, receivesAll, BotName: "小助手");

    /// <summary>alwaysReply=true 时随机数恒为 0（必定接话），false 时恒为 1（必定不接）。</summary>
    private static TriggerEngine Engine(bool alwaysReply = true) => new(() => alwaysReply ? 0.0 : 0.999);

    [Fact]
    public void At_IsMustReply()
    {
        var message = Msg("@小助手 在吗", mentions: true);
        var outcome = Engine().EvaluateBatch(new[] { message }, Ctx(true, message), Config());

        Assert.True(outcome.Triggered);
        Assert.Equal(TriggerSource.Mention, outcome.Source);
        Assert.Equal(ReplyDecision.Must, outcome.Decision);
    }

    [Fact]
    public void BotName_IsMustReply_EvenWithoutAt()
    {
        var message = Msg("小助手你在干嘛");
        var outcome = Engine().EvaluateBatch(new[] { message }, Ctx(true, message), Config());

        Assert.True(outcome.Triggered);
        Assert.Equal(TriggerSource.Name, outcome.Source);
        Assert.Equal(ReplyDecision.Must, outcome.Decision);
    }

    [Fact]
    public void BotName_CanBeDisabled()
    {
        var message = Msg("小助手你在干嘛");
        var cfg = Config("""{"trigger_by_name": false}""");
        var outcome = Engine().EvaluateBatch(new[] { message }, Ctx(true, message), cfg);

        Assert.False(outcome.Triggered);
    }

    [Fact]
    public void BotName_SingleCharacter_DoesNotTrigger()
    {
        // 名字只有一个字时不能当触发词，否则"小"会点亮整群。
        // 特意用一句非疑问句，避免疑问句启发式把结果搅混。
        var message = Msg("小同学来了");
        var ctx = new TriggerContext(Now, new[] { message }, true, BotName: "小");
        var outcome = Engine().EvaluateBatch(new[] { message }, ctx, Config());

        Assert.False(outcome.Triggered);
        Assert.Equal(TriggerSource.None, outcome.Source);
    }

    [Fact]
    public void Keyword_IsMayReply_AndHonoursProbability()
    {
        var cfg = Config("""{"trigger_keywords": ["火锅"]}""");
        var message = Msg("周末去吃火锅");

        var may = Engine(alwaysReply: true).EvaluateBatch(new[] { message }, Ctx(true, message), cfg);
        Assert.True(may.Triggered);
        Assert.Equal(TriggerSource.Keyword, may.Source);
        Assert.Equal(ReplyDecision.May, may.Decision);

        var never = Engine(alwaysReply: false).EvaluateBatch(new[] { message }, Ctx(true, message), cfg);
        Assert.False(never.Triggered);
    }

    [Fact]
    public void MultipleKeywords_AllWork()
    {
        var cfg = Config("""{"trigger_keywords": ["火锅", "烤肉", "自助"]}""");

        foreach (var text in new[] { "去吃火锅吧", "要不烤肉", "自助怎么样" })
        {
            var message = Msg(text);
            var outcome = Engine().EvaluateBatch(new[] { message }, Ctx(true, message), cfg);
            Assert.True(outcome.Triggered, text);
            Assert.Equal(TriggerSource.Keyword, outcome.Source);
        }
    }

    [Fact]
    public void Keyword_MatchesAnyMessageInBatch_NotJustTheLast()
    {
        var cfg = Config("""{"trigger_keywords": ["火锅"]}""");
        var first = Msg("想去吃火锅", secondsAgo: 2);
        var second = Msg("你几点下班", secondsAgo: 1);

        var outcome = Engine().EvaluateBatch(new[] { first, second }, Ctx(true, first, second), cfg);

        Assert.True(outcome.Triggered);
        Assert.Equal(TriggerSource.Keyword, outcome.Source);
    }

    [Fact]
    public void KeywordPlusQuestion_IsMustReply()
    {
        var cfg = Config("""{"trigger_keywords": ["火锅"]}""");
        var message = Msg("火锅店几点关门？");

        var outcome = Engine(alwaysReply: false).EvaluateBatch(new[] { message }, Ctx(true, message), cfg);

        Assert.True(outcome.Triggered);
        Assert.Equal(ReplyDecision.Must, outcome.Decision);
        Assert.Contains("提问", outcome.Reason);
    }

    [Fact]
    public void Question_WithoutKeywordIsMayReply()
    {
        var message = Msg("明天几点集合？");
        var outcome = Engine().EvaluateBatch(new[] { message }, Ctx(true, message), Config());

        Assert.True(outcome.Triggered);
        Assert.Equal(TriggerSource.Question, outcome.Source);
        Assert.Equal(ReplyDecision.May, outcome.Decision);
    }

    [Fact]
    public void AtOnlyMode_StillWorksForAt_ButIgnoresPlainMessages()
    {
        var cfg = Config("""{"trigger_keywords": ["火锅"], "trigger_by_name": true}""");

        var at = Msg("@小助手 在吗", mentions: true);
        var atOutcome = Engine().EvaluateBatch(new[] { at }, Ctx(false, at), cfg);
        Assert.True(atOutcome.Triggered);
        Assert.Equal(TriggerSource.Mention, atOutcome.Source);

        // 仅 @ 模式下，平台根本不会把这条推给我们；即便推了也不该触发。
        var plain = Msg("小助手 火锅");
        var plainOutcome = Engine().EvaluateBatch(new[] { plain }, Ctx(false, plain), cfg);
        Assert.False(plainOutcome.Triggered);
        Assert.Contains("非 @", plainOutcome.Reason);
    }

    [Fact]
    public void BotOwnEcho_IsIgnored()
    {
        var echo = Msg("六点半吧") with { IsSelf = true };
        var outcome = Engine().EvaluateBatch(new[] { echo }, Ctx(true, echo), Config());
        Assert.False(outcome.Triggered);
    }

    [Fact]
    public void QuestionAndKeyword_HaveIndependentProbabilities()
    {
        var question = Msg("明天几点集合？");

        // 关键词概率 0、疑问句概率 1：纯疑问句仍应触发。
        var questionOn = Config("""{"reply_probability": 0, "question_reply_probability": 1}""");
        Assert.True(Engine(alwaysReply: false).EvaluateBatch(new[] { question }, Ctx(true, question), questionOn).Triggered);

        // 关键词概率 1、疑问句概率 0：纯疑问句不该触发。
        var questionOff = Config("""{"reply_probability": 1, "question_reply_probability": 0}""");
        var outcome = Engine(alwaysReply: false).EvaluateBatch(new[] { question }, Ctx(true, question), questionOff);
        Assert.False(outcome.Triggered);
        Assert.Contains("疑问句", outcome.Reason);

        // 同一套配置下，关键词仍然按自己的概率走。
        var keyword = Msg("去吃火锅");
        var keywordCfg = Config("""{"trigger_keywords": ["火锅"], "reply_probability": 1, "question_reply_probability": 0}""");
        Assert.True(Engine(alwaysReply: false).EvaluateBatch(new[] { keyword }, Ctx(true, keyword), keywordCfg).Triggered);
    }

    [Fact]
    public void Cooldown_BlocksEverything()
    {
        var message = Msg("@小助手 在吗", mentions: true);
        var ctx = Ctx(true, message) with { CooldownUntil = Now.AddMinutes(5) };

        var outcome = Engine().EvaluateBatch(new[] { message }, ctx, Config());

        Assert.False(outcome.Triggered);
        Assert.Contains("冷却", outcome.Reason);
    }
}
