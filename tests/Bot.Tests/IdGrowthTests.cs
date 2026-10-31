using Bot.Core;
using Bot.Core.Config;
using Bot.Core.Domain;
using Bot.Core.Memory;
using Bot.Core.Runtime;
using Bot.Core.Text;

namespace Bot.Tests;

/// <summary>
/// 编号增长的边界：E999 → E1000、群友01 → 群友101 这一类"跑久了才会撞上"的问题。
/// </summary>
public sealed class IdGrowthTests
{
    [Fact]
    public void Labels_WidenButNeverCollide()
    {
        Assert.Equal("P001", Labels.Person(1));
        Assert.Equal("P999", Labels.Person(999));
        Assert.Equal("P1000", Labels.Person(1000));
        Assert.Equal("P123456", Labels.Person(123456));

        Assert.Equal("E001", Labels.Event(1));
        Assert.Equal("E999", Labels.Event(999));
        Assert.Equal("E1000", Labels.Event(1000));

        Assert.Equal("群友07", Labels.GroupMate(7));
        Assert.Equal("群友99", Labels.GroupMate(99));
        Assert.Equal("群友100", Labels.GroupMate(100));
        Assert.Equal("群友107", Labels.GroupMate(107));

        // 这条是重点：取后两位会让 1 和 101 撞成同一个"群友01"。
        Assert.NotEqual(Labels.GroupMate(1), Labels.GroupMate(101));
        Assert.NotEqual(Labels.GroupMate(7), Labels.GroupMate(107));
        Assert.NotEqual(Labels.GroupMate(7), Labels.GroupMate(207));
    }

    [Fact]
    public void Prefix_RendersEventsPastThousand_WithoutBreaking()
    {
        var assembler = new PromptAssembler(new TokenEstimator());
        var cfg = new EffectiveConfig(ConfigResolver.Resolve(Presets.Balanced, null, null));

        var events = Enumerable.Range(1, 1200).Select(i => MakeEvent(i)).ToArray();

        var prefix = assembler.BuildPrefix("c1", cfg, new PersonaSettings(), ChatKind.Group,
            Array.Empty<PersonRow>(), events, null);

        Assert.Contains("第 1 条事件详情", prefix.Text, StringComparison.Ordinal);
        Assert.Contains("第 999 条事件详情", prefix.Text, StringComparison.Ordinal);
        Assert.Contains("第 1000 条事件详情", prefix.Text, StringComparison.Ordinal);
        Assert.Contains("第 1200 条事件详情", prefix.Text, StringComparison.Ordinal);

        // 事件不再渲染 E 编号，但顺序仍然按 event_id 升序（999 在 1000 前面）。
        Assert.True(prefix.Text.IndexOf("第 999 条事件详情", StringComparison.Ordinal)
                    < prefix.Text.IndexOf("第 1000 条事件详情", StringComparison.Ordinal));

        // 输入顺序不应影响哈希：渲染前统一按 id 排序。
        var again = assembler.BuildPrefix("c1", cfg, new PersonaSettings(), ChatKind.Group,
            Array.Empty<PersonRow>(), events.Reverse().ToArray(), null);
        Assert.Equal(prefix.Hash, again.Hash);
    }

    [Fact]
    public void Pin_AcceptsBothNumericAndPrefixedEventIds()
    {
        Assert.True(Labels.TryParseEventId("E1000", out var a));
        Assert.Equal(1000, a);

        Assert.True(Labels.TryParseEventId("1000", out var b));
        Assert.Equal(1000, b);

        Assert.True(Labels.TryParseEventId(" e003 ", out var c));
        Assert.Equal(3, c);

        Assert.False(Labels.TryParseEventId("EVENT", out _));
        Assert.False(Labels.TryParseEventId("", out _));
        Assert.False(Labels.TryParseEventId("0", out _));
    }

    [Fact]
    public async Task PinCommand_WorksWithEPrefix()
    {
        await using var h = new Harness();
        await h.TurnAsync("昨天吃火锅了");

        var eventId = h.Events.Add(
            new EventDraft("昨天", null, Harness.ChatId, "约在周五吃火锅", new[] { "火锅" }, Array.Empty<int>()),
            "b1", "test");

        var cfg = h.ConfigFor();
        var parsed = SummaryParser.Parse($"PIN|EVENT|E{eventId:D3}|很重要");
        var applied = h.Applier.Apply(Harness.ChatId, "s-pin", parsed, cfg, new Dictionary<string, string>());

        Assert.Equal(1, applied.Applied);
        Assert.True(h.Events.GetById(eventId)!.Pinned);
    }

    [Fact]
    public async Task PersonLabels_DoNotCollideAcrossHundreds()
    {
        await using var h = new Harness();

        // 直接造 101 个 open_id，验证第 101 个人的标签不会退化成第 1 个人的。
        string? first = null;
        string? hundredFirst = null;

        for (var i = 1; i <= 101; i++)
        {
            var person = h.Identity.Resolve(new IncomingMessage
            {
                ChatId = Harness.ChatId,
                OpenId = $"seed-open-id-{i:D4}",
                Content = $"消息 {i}",
                MentionsBot = true,
                Timestamp = h.Clock.UtcNow,
            }, "小助手");

            if (i == 1)
            {
                first = person.DisplayLabel;
            }
            else if (i == 101)
            {
                hundredFirst = person.DisplayLabel;
            }
        }

        Assert.NotNull(first);
        Assert.NotNull(hundredFirst);
        Assert.NotEqual(first, hundredFirst);
        Assert.Equal("群友01", first);
        Assert.Equal("群友101", hundredFirst);
    }

    [Fact]
    public void Prefix_TrimsPersonsWhenOverBudget()
    {
        var assembler = new PromptAssembler(new TokenEstimator());
        var cfg = new EffectiveConfig(ConfigResolver.Resolve(Presets.Balanced, null, null));

        // 40 个长记忆的人：不做预算裁剪的话前缀会远超 prefix_token_budget。
        var persons = Enumerable.Range(1, 40).Select(i => MakePersonWithMemory(i, pinned: i == 40)).ToArray();
        Assert.Equal(1500, cfg.PrefixTokenBudget);

        var prefix = assembler.BuildPrefix("c1", cfg, new PersonaSettings(), ChatKind.Group,
            persons, Array.Empty<EventRow>(), null);

        Assert.True(prefix.PersonsTrimmed > 0, "应该裁掉一部分人物");
        Assert.True(prefix.Tokens <= cfg.PrefixTokenBudget + 60,
            $"前缀 {prefix.Tokens} token 超出预算 {cfg.PrefixTokenBudget}");

        // 置顶的人必须在场，哪怕他的 person_id 排在最后。
        Assert.Contains("P040｜", prefix.Text, StringComparison.Ordinal);

        // 保留下来的人依然按 person_id 升序渲染。
        var first = prefix.Text.IndexOf("P001｜", StringComparison.Ordinal);
        if (first >= 0)
        {
            var p040 = prefix.Text.IndexOf("P040｜", StringComparison.Ordinal);
            Assert.True(first < p040);
        }
    }

    private static PersonRow MakePersonWithMemory(int id, bool pinned) => new(
        id,
        $"open-{id:D4}",
        null,
        $"成员{id}",
        "learned",
        $"成员{id}",
        "话不多，偶尔接梗",
        "爱开玩笑",
        new string('记', 100),
        5,
        pinned,
        "active",
        string.Empty,
        10,
        null,
        0,
        0);

    private static EventRow MakeEvent(int id) => new(
        id,
        "昨天",
        null,
        "c1",
        "测试群",
        $"第 {id} 条事件详情",
        3,
        false,
        "active",
        null,
        0,
        "hash-" + id,
        0,
        null,
        Array.Empty<int>(),
        new[] { "关键词" });
}
