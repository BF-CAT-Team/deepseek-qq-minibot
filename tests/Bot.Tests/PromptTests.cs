using System.Text.Json.Nodes;
using Bot.Core.Config;
using Bot.Core.Domain;
using Bot.Core.Memory;
using Bot.Core.Runtime;
using Xunit;

namespace Bot.Tests;

/// <summary>提示词格式、回复长度上限、以及调参页的提示词框架。</summary>
public sealed class PromptTests
{
    [Fact]
    public void Reply_length_is_numeric_and_presets_follow()
    {
        var spec = ParamRegistry.Require("reply_length");
        Assert.Equal(ParamType.Int, spec.Type);
        Assert.Equal(40, spec.Default!.GetValue<int>());
        Assert.True(spec.BreaksCache); // 规则里写了具体字数，改了必须重建前缀

        Assert.Equal(20, Presets.Deltas(Presets.Economy)["reply_length"]!.GetValue<int>());
        Assert.Equal(40, Presets.Deltas(Presets.Balanced)["reply_length"]!.GetValue<int>());
        Assert.Equal(80, Presets.Deltas(Presets.Experience)["reply_length"]!.GetValue<int>());
    }

    [Fact]
    public void Rules_and_tail_follow_reply_length_and_drop_the_group_name()
    {
        var cfg = new EffectiveConfig(ConfigResolver.Resolve(
            Presets.Balanced,
            new JsonObject { ["reply_length"] = 30 },
            null));
        var assembler = new PromptAssembler(new Bot.Core.Text.TokenEstimator());

        var prefix = assembler.BuildPrefix(
            "c1", cfg, new PersonaSettings(), ChatKind.Group,
            Array.Empty<PersonRow>(), Array.Empty<EventRow>(), null);

        Assert.Contains("回复字数：30 字内", prefix.Text);
        Assert.Contains("若回答的内容较长，可以委婉地说自己不知道", prefix.Text);
        Assert.Contains("想点名谁就称呼其名字，没有名字就用群友", prefix.Text);

        var tail = assembler.BuildTail(new PromptTailContext(
            "c1", "本地测试群", ChatKind.Group, DateTimeOffset.Now,
            new[] { new TailMessage("群友01", "在吗") }, 30));

        Assert.DoesNotContain("群「", tail.Text);
        Assert.DoesNotContain("本地测试群", tail.Text);
        Assert.DoesNotContain("近10分钟", tail.Text);
        Assert.DoesNotContain("上一条来自", tail.Text);
        Assert.Contains("回复：最多 30 字", tail.Text);
    }

    [Fact]
    public void Legacy_enum_reply_length_is_migrated_to_a_number()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "testdata", "settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        try
        {
            File.WriteAllText(path, """{"parameters":{"reply_length":"极简"}}""");
            var settings = SettingsStore.Load(path, out var error);
            Assert.Null(error);
            Assert.Equal(20, settings.Parameters["reply_length"]!.GetValue<int>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Delivery_truncates_long_replies_to_the_character_limit()
    {
        var cfg = new EffectiveConfig(ConfigResolver.Resolve(
            Presets.Balanced,
            new JsonObject { ["reply_length"] = 30, ["split_bubbles"] = true },
            null));

        var reply = "这是一段足够长的第一句，用来测试截断。" + new string('啊', 100) + "。最后一句。";
        var plan = Delivery.Plan(reply, cfg);

        var total = string.Concat(plan.Bubbles);
        Assert.True(total.Length <= 30, $"回复长度 {total.Length} 超过上限：{total}");
        Assert.EndsWith("。", total);
        Assert.DoesNotContain("最后一句", total);
    }

    [Fact]
    public void Prefix_renders_person_and_event_headers_without_location_or_keywords()
    {
        var assembler = new PromptAssembler(new Bot.Core.Text.TokenEstimator());
        var cfg = new EffectiveConfig(ConfigResolver.Resolve(Presets.Balanced, null, null));

        var person = MakePerson(16, "寒阳", "爱追问，先喊肥鱼再问名字，爱发怪表情，爱立flag",
            "有点烦人但不算坏", "问是不是大肥鱼被怼，得知叫小鲸鱼；自称寒阳不许叫群友04");
        var item = MakeEvent(3, "昨天下午", "约在周五见面", locationLabel: "本群",
            keywords: new[] { "聚餐", "火锅" });

        var prefix = assembler.BuildPrefix(
            "c1", cfg, new PersonaSettings(), ChatKind.Group, new[] { person }, new[] { item }, null);

        Assert.Contains(PromptAssembler.PersonHeader, prefix.Text);
        Assert.Contains(PromptAssembler.EventHeader, prefix.Text);
        Assert.Contains("P016｜寒阳｜爱追问", prefix.Text);
        Assert.Contains("寒阳｜昨天下午｜约在周五见面", prefix.Text);
        Assert.DoesNotContain("E003", prefix.Text);

        // 事件地点固定是本群、关键词不进 prompt。
        Assert.DoesNotContain("本群", prefix.Text);
        Assert.DoesNotContain("聚餐", prefix.Text);
        Assert.DoesNotContain("火锅", prefix.Text);
    }

    [Fact]
    public void Prefix_omits_headers_when_there_is_no_memory()
    {
        var assembler = new PromptAssembler(new Bot.Core.Text.TokenEstimator());
        var cfg = new EffectiveConfig(ConfigResolver.Resolve(Presets.Balanced, null, null));

        var prefix = assembler.BuildPrefix(
            "c1", cfg, new PersonaSettings(), ChatKind.Group,
            Array.Empty<PersonRow>(), Array.Empty<EventRow>(), null);

        Assert.DoesNotContain(PromptAssembler.PersonHeader, prefix.Text);
        Assert.DoesNotContain(PromptAssembler.EventHeader, prefix.Text);
        Assert.Contains("（暂无）", prefix.Text);
    }

    [Fact]
    public void Prompt_framework_contains_structure_and_parameters()
    {
        var settings = new AppSettings
        {
            Preset = Presets.Balanced,
            Parameters = new JsonObject { ["reply_length"] = 30 },
        };
        var config = new ConfigService(settings);
        var estimator = new Bot.Core.Text.TokenEstimator();
        var service = new PromptFrameworkService(config, new PromptAssembler(estimator), estimator);

        var framework = service.Build();

        Assert.Equal(30, framework.ReplyLength);

        var system = framework.ColdStart[0].Content;
        Assert.Contains("回复字数：30 字内", system);
        Assert.Contains(PromptAssembler.PersonHeader, system);
        Assert.Contains(PromptAssembler.EventHeader, system);
        Assert.Contains("<名字>", system);
        Assert.Contains("<上轮摘要>", system);

        var tail = framework.ColdStart[1].Content;
        Assert.Contains("【当前】<时间>", tail);
        Assert.Contains("最多 30 字", tail);
        Assert.DoesNotContain("群「", tail);

        Assert.Equal("system", framework.Summary[0].Role);
        Assert.Equal("user", framework.Summary[^1].Role);
        Assert.Equal(SummaryParser.Template, framework.Summary[^1].Content);
    }

    private static PersonRow MakePerson(int id, string name, string personality, string evaluation, string memory) => new(
        id,
        $"open-{id:D4}",
        null,
        name,
        "learned",
        name,
        personality,
        evaluation,
        memory,
        5,
        false,
        "active",
        string.Empty,
        10,
        null,
        0,
        0);

    private static EventRow MakeEvent(int id, string time, string detail, string locationLabel, string[] keywords) => new(
        id,
        time,
        null,
        "c1",
        locationLabel,
        detail,
        3,
        false,
        "active",
        null,
        0,
        "hash-" + id,
        0,
        null,
        new[] { 16 },
        keywords);
}
