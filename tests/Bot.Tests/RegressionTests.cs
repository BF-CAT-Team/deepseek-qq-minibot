using System.Text.RegularExpressions;
using Bot.Core.Config;
using Bot.Core.Domain;
using Bot.Core.Memory;
using Bot.Core.Runtime;
using Bot.Core.Text;

namespace Bot.Tests;

/// <summary>文档第 9 节的五条"每次提交必跑"回归断言。</summary>
public sealed class RegressionTests
{
    // 1. 同一会话连续 5 轮：prefix_hash 必须完全一致。
    [Fact]
    public async Task PrefixHash_IsStable_AcrossFiveTurns()
    {
        await using var h = new Harness();

        for (var i = 1; i <= 5; i++)
        {
            var reply = await h.TurnAsync($"第 {i} 条消息，随便聊聊");
            Assert.NotNull(reply);
        }

        var hashes = h.ChatRequests.Select(r => r.PrefixHash).Distinct().ToArray();
        Assert.Equal(5, h.ChatRequests.Count);
        Assert.Single(hashes);
    }

    // 2. 记忆按 ID 乱序插入后组装，输出必须一致（排序稳定性）。
    [Fact]
    public void PrefixAssembly_IsOrderIndependent()
    {
        var assembler = new PromptAssembler(new TokenEstimator());
        var cfg = new EffectiveConfig(ConfigResolver.Resolve(Presets.Balanced, null, null));
        var persona = new PersonaSettings();

        var persons = new List<PersonRow>
        {
            MakePerson(7, "阿杰", "话少"),
            MakePerson(2, "小李", "学生"),
            MakePerson(5, "", ""),
        };

        var forward = assembler.BuildPrefix("c1", cfg, persona, ChatKind.Group, persons, Array.Empty<EventRow>(), null);
        var shuffled = assembler.BuildPrefix("c1", cfg, persona, ChatKind.Group,
            new[] { persons[2], persons[0], persons[1] }, Array.Empty<EventRow>(), null);
        var reversed = assembler.BuildPrefix("c1", cfg, persona, ChatKind.Group,
            persons.AsEnumerable().Reverse().ToArray(), Array.Empty<EventRow>(), null);

        Assert.Equal(forward.Text, shuffled.Text);
        Assert.Equal(forward.Hash, shuffled.Hash);
        Assert.Equal(forward.Hash, reversed.Hash);

        // 顺序必须是 person_id 升序，而不是输入顺序。
        var idx2 = forward.Text.IndexOf("P002", StringComparison.Ordinal);
        var idx5 = forward.Text.IndexOf("P005", StringComparison.Ordinal);
        var idx7 = forward.Text.IndexOf("P007", StringComparison.Ordinal);
        Assert.True(idx2 < idx5 && idx5 < idx7);
    }

    // 3. 在历史中段人为修改一个字符，断言哈希告警触发（守护自检）。
    [Fact]
    public void PrefixHash_DetectsSingleCharacterMutation()
    {
        const string prefix = "【规则】\n1. 你在群聊里扮演「小助手」\n【记忆·事件】\n阿杰｜昨天｜聚餐\n";
        var original = PromptHasher.Compute(prefix);

        var mutated = prefix.Replace("聚餐", "聚参");
        var mutatedHash = PromptHasher.Compute(mutated);

        Assert.NotEqual(original, mutatedHash);
        Assert.True(PromptHasher.FirstDifference(prefix, mutated) >= 0);
        Assert.Contains("首个差异", PromptHasher.DescribeDifference(prefix, mutated));

        // 会话自身的保险：前缀冻结后不允许改写。
        var session = new Session("c1", DateTimeOffset.UtcNow, "s1");
        session.FreezePrefix(prefix, original, 100);
        Assert.Throws<InvalidOperationException>(() => session.FreezePrefix(mutated, mutatedHash, 100));
    }

    // 4. 总结请求本身不改变前缀哈希。
    [Fact]
    public async Task SummaryRequest_DoesNotChangePrefix()
    {
        await using var h = new Harness();
        h.Model.Reply = request => IsSummaryRequest(request)
            ? "NEW_EVENT|群友01|昨天下午|本地测试群|聚餐,火锅|约在周五晚上七点"
            : "六点半吧，老地方？";

        await h.TurnAsync("明天几点集合？");
        await h.TurnAsync("那就六点半");
        await h.TurnAsync("我可能晚点");

        var chatPrefix = h.ChatRequests.Select(r => r.PrefixHash).Distinct().Single();

        var beforeCount = h.Model.Requests.Count;
        await h.Runtime.ForceRotateAsync(Harness.ChatId);

        var summaryRequests = h.Model.Requests.Skip(beforeCount).Where(IsSummaryRequest).ToArray();
        Assert.Single(summaryRequests);

        // 总结请求复用的是同一个前缀（system），一个字节都没动。
        var chatSystem = h.Model.Requests.First(r => !IsSummaryRequest(r)).Messages[0].Content;
        Assert.Equal(chatSystem, summaryRequests[0].Messages[0].Content);

        var summaryLog = h.Logs.Recent(Harness.ChatId, 50).Single(r => r.Kind == "summary");
        Assert.Equal(chatPrefix, summaryLog.PrefixHash);
    }

    // 5. 组装结果做正则断言：不得匹配 open_id 格式，命中即失败。
    [Fact]
    public async Task Prompt_NeverContainsOpenId()
    {
        await using var h = new Harness();
        await h.TurnAsync("在吗？");
        await h.TurnAsync("周末一起吃饭？");

        var openIdPattern = new Regex("[A-Fa-f0-9]{30,}", RegexOptions.Compiled);

        lock (h.Model.Requests)
        {
            foreach (var request in h.Model.Requests)
            {
                foreach (var message in request.Messages)
                {
                    Assert.DoesNotMatch(openIdPattern, message.Content);
                }
            }
        }

        // 但 open_id 必须真的存在于数据库里（映射层不能把它弄丢了）。
        var person = h.Identity.GetPersonByOpenId(Harness.UserOpenId);
        Assert.NotNull(person);
        Assert.Equal(Harness.UserOpenId, person!.OpenId);
    }

    // 5b. 群聊用 QQ 的 32 位群 open_id 作为 chat_id 时，它也不能进 prompt。
    //     这是真环境踩出来的：QQ 不下发群名，代码曾拿 chat_id 兜底当群名。
    [Fact]
    public async Task Prompt_NeverContainsOpaqueGroupId()
    {
        await using var h = new Harness();
        const string opaqueChatId = "7CFF2FD251E3D14DC6C6F8534824A373";

        h.Model.Reply = _ => "在的";
        await h.SendAsync("在吗", chatId: opaqueChatId);
        h.Clock.Advance(5);
        await h.Runtime.TickAsync();

        var pattern = new Regex("[A-Fa-f0-9]{24,}", RegexOptions.Compiled);
        Assert.NotEmpty(h.Model.Requests);
        foreach (var request in h.Model.Requests)
        {
            foreach (var message in request.Messages)
            {
                Assert.DoesNotMatch(pattern, message.Content);
            }
        }
    }

    private static bool IsSummaryRequest(Core.Llm.ChatCompletionRequest request) =>
        request.Messages[^1].Content.Contains("【任务】", StringComparison.Ordinal);

    private static PersonRow MakePerson(int id, string name, string personality) => new(
        id, $"open{id:0000000000000000000000000000}", null, name, name.Length > 0 ? "self" : "none",
        name.Length > 0 ? name : $"群友{id % 100:D2}", personality, string.Empty, $"记忆{id}",
        3, false, "active", string.Empty, 5, null, 0, 0);
}
