using Bot.Core.Config;
using Bot.Core.Memory;
using Bot.Core.Runtime;
using Bot.Core.Text;

namespace Bot.Tests;

public sealed class RuntimeTests
{
    [Fact]
    public async Task Mention_AlwaysReplies()
    {
        await using var h = new Harness();
        h.Model.Reply = _ => "六点半吧，老地方？";
        var reply = await h.TurnAsync("在吗");
        Assert.NotNull(reply);
        Assert.Contains("六点半", reply);
    }

    [Fact]
    public async Task StatementWithoutKeyword_DoesNotReply()
    {
        await using var h = new Harness();
        var reply = await h.TurnAsync("今天天气不错", mention: false);
        Assert.Null(reply);
        Assert.Empty(h.Adapter.Sent);
    }

    [Fact]
    public async Task Question_TriggersReply_EvenWithoutMention()
    {
        // 疑问句现在有自己的概率，测"能触发"时把它拉满，否则 0.35 会让用例随机失败。
        await using var h = new Harness("""
            {"typing_jitter":false,"bubble_gap_min_sec":0,"bubble_gap_max_sec":0,
             "adaptive_window":false,"quiet_sec":1,"max_wait_sec":30,
             "question_reply_probability":1.0}
            """);
        var reply = await h.TurnAsync("明天几点集合？", mention: false);
        Assert.NotNull(reply);
    }

    [Fact]
    public async Task Keyword_WithZeroProbability_DoesNotReply()
    {
        await using var h = new Harness("""
            {"typing_jitter":false,"bubble_gap_min_sec":0,"bubble_gap_max_sec":0,
             "quiet_sec":1,"trigger_keywords":["火锅"],"reply_probability":0}
            """);

        var reply = await h.TurnAsync("周末想去吃火锅", mention: false);
        Assert.Null(reply);
    }

    [Fact]
    public async Task Keyword_WithFullProbability_Replies()
    {
        await using var h = new Harness("""
            {"typing_jitter":false,"bubble_gap_min_sec":0,"bubble_gap_max_sec":0,
             "quiet_sec":1,"trigger_keywords":["火锅"],"reply_probability":1}
            """);

        var reply = await h.TurnAsync("周末想去吃火锅", mention: false);
        Assert.NotNull(reply);
    }

    [Fact]
    public async Task Debounce_MergesBurstIntoOneRequest()
    {
        await using var h = new Harness();

        await h.SendAsync("第一条");
        h.Clock.Advance(0.3);
        await h.SendAsync("第二条");

        h.Clock.Advance(0.6); // 距最后一条 0.6s < quiet 1s
        await h.Runtime.TickAsync();
        Assert.Empty(h.Adapter.Sent);

        h.Clock.Advance(0.6); // 距最后一条 1.2s ≥ quiet
        await h.Runtime.TickAsync();

        Assert.NotEmpty(h.Adapter.Sent);
        var tail = h.Model.Requests.Single().Messages[^1].Content;
        Assert.Contains("第一条", tail);
        Assert.Contains("第二条", tail);
    }

    [Fact]
    public async Task SecondTurn_AppendsToHistory_AndKeepsPrefixFrozen()
    {
        await using var h = new Harness();
        await h.TurnAsync("明天几点集合？");
        await h.TurnAsync("那就六点半吧");

        Assert.Equal(2, h.Model.Requests.Count);
        Assert.Equal(h.Model.Requests[0].Messages[0].Content, h.Model.Requests[1].Messages[0].Content);

        // 第二次请求的消息数比第一次多（历史 +1 user +1 assistant +1 新 user）
        var first = h.Model.Requests[0].Messages.Count;
        var second = h.Model.Requests[1].Messages.Count;
        Assert.Equal(first + 2, second);
    }

    [Fact]
    public async Task EmptyReply_IsNotDelivered()
    {
        await using var h = new Harness();
        h.Model.Reply = _ => "   ";

        var reply = await h.TurnAsync("在吗");
        Assert.Null(reply);
    }

    [Fact]
    public async Task Triggered_Reply_BackfillsRecentChatterAsBackground()
    {
        await using var h = new Harness();

        // 三段没人叫它的闲聊：都被丢弃、进不了上下文，但要留在"未读"缓冲里。
        // 注意别写疑问句——那样会命中疑问句启发式，反而触发回复。
        foreach (var text in new[] { "今天天气不错", "我吃了面", "晚上打战地五" })
        {
            await h.SendAsync(text, mention: false);
            h.Clock.Advance(5);
            await h.Runtime.TickAsync();
        }

        await h.SendAsync("@小助手 你觉得呢", mention: true);
        h.Clock.Advance(5);
        await h.Runtime.TickAsync();

        var tail = h.Model.Requests[^1].Messages[^1].Content;
        Assert.Contains("【背景】", tail);
        Assert.Contains("今天天气不错", tail);
        Assert.Contains("晚上打战地五", tail);

        // 背景必须在【消息】之前（时间顺序），否则模型会把旧话当成新话。
        Assert.True(tail.IndexOf("【背景】", StringComparison.Ordinal)
                    < tail.IndexOf("【消息】", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Backfill_IsClearedAfterReply_SoItIsNotRepeated()
    {
        await using var h = new Harness();

        await h.SendAsync("今天天气不错", mention: false);
        h.Clock.Advance(5);
        await h.Runtime.TickAsync();

        await h.SendAsync("@小助手 在吗", mention: true);
        h.Clock.Advance(5);
        await h.Runtime.TickAsync();
        Assert.Contains("今天天气不错", h.Model.Requests[^1].Messages[^1].Content);

        // 第二次触发时，上一轮已经补过的内容不该再出现。
        await h.SendAsync("@小助手 再说句", mention: true);
        h.Clock.Advance(5);
        await h.Runtime.TickAsync();

        var tail = h.Model.Requests[^1].Messages[^1].Content;
        Assert.DoesNotContain("今天天气不错", tail);
        Assert.DoesNotContain("【背景】", tail);
    }

    [Fact]
    public async Task Backfill_CanBeDisabled()
    {
        await using var h = new Harness("""
            {"typing_jitter":false,"bubble_gap_min_sec":0,"bubble_gap_max_sec":0,
             "adaptive_window":false,"quiet_sec":1,"max_wait_sec":30,"backfill_max_msgs":0}
            """);

        await h.SendAsync("今天天气不错", mention: false);
        h.Clock.Advance(5);
        await h.Runtime.TickAsync();

        await h.SendAsync("@小助手 在吗", mention: true);
        h.Clock.Advance(5);
        await h.Runtime.TickAsync();

        var tail = h.Model.Requests[^1].Messages[^1].Content;
        Assert.DoesNotContain("【背景】", tail);
        Assert.DoesNotContain("今天天气不错", tail);
    }

    [Fact]
    public async Task Backfill_RespectsMessageCap()
    {
        await using var h = new Harness("""
            {"typing_jitter":false,"bubble_gap_min_sec":0,"bubble_gap_max_sec":0,
             "adaptive_window":false,"quiet_sec":1,"max_wait_sec":30,"backfill_max_msgs":2}
            """);

        for (var i = 1; i <= 5; i++)
        {
            await h.SendAsync($"闲聊第{i}条", mention: false);
            h.Clock.Advance(5);
            await h.Runtime.TickAsync();
        }

        await h.SendAsync("@小助手 在吗", mention: true);
        h.Clock.Advance(5);
        await h.Runtime.TickAsync();

        var tail = h.Model.Requests[^1].Messages[^1].Content;
        Assert.Contains("闲聊第4条", tail);
        Assert.Contains("闲聊第5条", tail);
        Assert.DoesNotContain("闲聊第1条", tail);
    }

    [Fact]
    public async Task ActiveSession_CapturesNonTriggeredChatter()
    {
        // 这里测的是"没有节拍"时的旁听并入（active_tick_sec=0）。
        await using var h = new Harness("""
            {"typing_jitter":false,"bubble_gap_min_sec":0,"bubble_gap_max_sec":0,
             "adaptive_window":false,"quiet_sec":1,"max_wait_sec":30,
             "active_tick_sec":0,"capture_ambient":true,"backfill_max_msgs":12}
            """);

        await h.TurnAsync("@小助手 在吗"); // 开会话并回复
        var requestsBefore = h.ChatRequests.Count;

        // 一句没人叫它的闲聊：不该触发请求，但要并进对话。
        await h.SendAsync("群友闲聊一句", mention: false);
        h.Clock.Advance(5);
        await h.Runtime.TickAsync();

        Assert.Equal(requestsBefore, h.ChatRequests.Count);
        Assert.Equal(1, h.Runtime.Snapshot().Single().TurnCount); // 旁听不计轮次

        await h.SendAsync("@小助手 刚才说到哪了", mention: true);
        h.Clock.Advance(5);
        await h.Runtime.TickAsync();

        var messages = h.Model.Requests[^1].Messages;
        var last = messages[^1];

        // 它必须作为历史里的一条独立消息存在，而不是塞在本轮尾巴的【背景】里。
        Assert.DoesNotContain("群友闲聊一句", last.Content);
        Assert.Contains(messages.Take(messages.Count - 1), m => m.Content.Contains("群友闲聊一句"));
    }

    [Fact]
    public async Task AmbientCapture_CanBeDisabled()
    {
        await using var h = new Harness("""
            {"typing_jitter":false,"bubble_gap_min_sec":0,"bubble_gap_max_sec":0,
             "adaptive_window":false,"quiet_sec":1,"max_wait_sec":30,
             "active_tick_sec":0,"capture_ambient":false,"backfill_max_msgs":0}
            """);

        await h.TurnAsync("@小助手 在吗");

        await h.SendAsync("群友闲聊一句", mention: false);
        h.Clock.Advance(5);
        await h.Runtime.TickAsync();

        await h.SendAsync("@小助手 刚才说到哪了", mention: true);
        h.Clock.Advance(5);
        await h.Runtime.TickAsync();

        var messages = h.Model.Requests[^1].Messages;
        Assert.DoesNotContain(messages, m => m.Content.Contains("群友闲聊一句"));
    }

    [Fact]
    public async Task ActiveSession_CadenceRepliesWithAllChatterSinceLastReply()
    {
        await using var h = new Harness("""
            {"typing_jitter":false,"bubble_gap_min_sec":0,"bubble_gap_max_sec":0,
             "adaptive_window":false,"quiet_sec":1,"max_wait_sec":30,
             "active_tick_sec":60,"backfill_max_msgs":12}
            """);

        await h.TurnAsync("@小助手 在吗"); // 激活会话
        var requestsBefore = h.ChatRequests.Count;

        // 会话激活后的两段群聊：都不触发，但要留着等节拍。
        // 注意别写疑问词（啥/吗/几点），否则会命中疑问句启发式直接回复。
        await h.SendAsync("今天好热", mention: false);
        await h.SendAsync("晚上想去吃面", mention: false);
        h.Clock.Advance(5);
        await h.Runtime.TickAsync();

        Assert.Equal(requestsBefore, h.ChatRequests.Count); // 还没到点，不主动开口

        h.Clock.Advance(70); // 超过 active_tick_sec=60
        await h.Runtime.TickAsync();

        Assert.Equal(requestsBefore + 1, h.ChatRequests.Count);
        var tail = h.Model.Requests[^1].Messages[^1].Content;
        Assert.Contains("今天好热", tail);
        Assert.Contains("晚上想去吃面", tail);
    }

    [Fact]
    public async Task Cadence_DoesNotFireWhenIdle()
    {
        await using var h = new Harness("""
            {"typing_jitter":false,"bubble_gap_min_sec":0,"bubble_gap_max_sec":0,
             "adaptive_window":false,"quiet_sec":1,"max_wait_sec":30,"active_tick_sec":60}
            """);

        await h.TurnAsync("@小助手 在吗");
        var requestsBefore = h.ChatRequests.Count;

        // 没人说话，就算到了点也不该自言自语。
        h.Clock.Advance(70);
        await h.Runtime.TickAsync();

        Assert.Equal(requestsBefore, h.ChatRequests.Count);
    }

    [Fact]
    public async Task Backfill_IgnoresStaleMessages()
    {
        // 回归：整晚没被触发，2 小时前的旧话题不能当成"最近群聊"补进新会话。
        // 真环境踩过：00:18 被 @ 时把 22:10 的聊天记录当背景端了上去。
        await using var h = new Harness();

        await h.SendAsync("两小时前的旧话题", mention: false);
        h.Clock.Advance(5);
        await h.Runtime.TickAsync(); // 批次被丢弃，但留在未读缓冲里

        h.Clock.Advance(120 * 60); // 两小时没人叫它
        await h.SendAsync("@小助手 在吗", mention: true);
        h.Clock.Advance(5);
        await h.Runtime.TickAsync();

        var tail = h.Model.Requests[^1].Messages[^1].Content;
        Assert.DoesNotContain("两小时前的旧话题", tail);
        Assert.DoesNotContain("【背景】", tail);
    }

    [Fact]
    public async Task Backfill_KeepsRecentMessagesWithinWindow()
    {
        await using var h = new Harness("""
            {"typing_jitter":false,"bubble_gap_min_sec":0,"bubble_gap_max_sec":0,
             "adaptive_window":false,"quiet_sec":1,"max_wait_sec":30,
             "backfill_max_msgs":12,"backfill_window_min":10,"active_tick_sec":0}
            """);

        await h.SendAsync("十分钟内的话题", mention: false);
        h.Clock.Advance(5);
        await h.Runtime.TickAsync();

        h.Clock.Advance(60); // 一分钟后被叫，仍应补上
        await h.SendAsync("@小助手 在吗", mention: true);
        h.Clock.Advance(5);
        await h.Runtime.TickAsync();

        var tail = h.Model.Requests[^1].Messages[^1].Content;
        Assert.Contains("十分钟内的话题", tail);
    }

    [Fact]
    public async Task ColdStart_KeepsPrefixSmall_AndInjectsPeopleOnDemand()
    {
        await using var h = new Harness("""
            {"typing_jitter":false,"bubble_gap_min_sec":0,"bubble_gap_max_sec":0,
             "adaptive_window":false,"quiet_sec":1,"max_wait_sec":30,
             "active_tick_sec":0,"prefix_person_max":2,
             "on_demand_memory":true,"on_demand_max_items":4}
            """);

        // 先让 4 个人各自建档（每人两条，避开疑问句/名字/关键词，不触发回复）
        for (var i = 1; i <= 4; i++)
        {
            await h.SendAsync($"我是{i}号", openId: $"open-{i}", mention: false);
            await h.SendAsync($"再说一句{i}", openId: $"open-{i}", mention: false);
            h.Clock.Advance(5);
            await h.Runtime.TickAsync();
        }

        Assert.Equal(4, h.Identity.ListActive().Count);
        // 给 4 个人都塞一点记忆（改名会命中"标签会话内冻结"的缓存，直接用 SQL 更贴近本意）
        h.Db.Exec("UPDATE person SET memory_text=@m WHERE person_id IN (1,2,3,4)", ("@m", "爱开玩笑，常约饭"));

        // 冷启动：只加载触发消息里出现过的人（open-1），其他有画像但没触发的人不该进来
        await h.TurnAsync("@小助手 在吗", openId: "open-1");
        var prefix = h.Model.Requests[^1].Messages[0].Content;
        Assert.Contains("P001｜", prefix);
        Assert.DoesNotContain("P002｜", prefix);
        Assert.DoesNotContain("P003｜", prefix);
        Assert.DoesNotContain("P004｜", prefix);

        // 后来没进前缀的人开口了：补进尾部（不动前缀）
        await h.TurnAsync("@小助手 还记得我吗", openId: "open-4");
        var tail = h.Model.Requests[^1].Messages[^1].Content;
        Assert.Contains("【补充记忆】", tail);
        Assert.Contains("P004｜", tail);
        Assert.Contains("爱开玩笑，常约饭", tail);
        Assert.Equal(prefix, h.Model.Requests[^1].Messages[0].Content); // 前缀逐字节未变

        // 补过一次就不再重复补
        await h.TurnAsync("@小助手 再问一句", openId: "open-4");
        Assert.DoesNotContain("P004｜", h.Model.Requests[^1].Messages[^1].Content);
    }

    [Fact]
    public async Task ColdStart_LoadsPeopleMentionedInTheMessage()
    {
        await using var h = new Harness("""
            {"typing_jitter":false,"bubble_gap_min_sec":0,"bubble_gap_max_sec":0,
             "adaptive_window":false,"quiet_sec":1,"max_wait_sec":30,
             "active_tick_sec":0,"prefix_person_max":8,
             "on_demand_memory":true,"on_demand_max_items":4}
            """);

        for (var i = 1; i <= 3; i++)
        {
            await h.SendAsync($"我是{i}号", openId: $"open-{i}", mention: false);
            await h.SendAsync($"再说一句{i}", openId: $"open-{i}", mention: false);
            h.Clock.Advance(5);
            await h.Runtime.TickAsync();
        }

        h.Db.Exec("UPDATE person SET memory_text='爱接梗' WHERE person_id=1");
        h.Db.Exec("UPDATE person SET name='寒阳', display_label='寒阳', memory_text='爱立flag' WHERE person_id=2");
        h.Db.Exec("UPDATE person SET memory_text='爱潜水' WHERE person_id=3");

        // 发言者是 1 号，但内容点名了寒阳（P002）和群友03（P003）：
        // 这两个没发言的人也应该被当作本轮相关人加载。
        await h.TurnAsync("@小助手 寒阳和群友03说的火锅那事", openId: "open-1");
        var prefix = h.Model.Requests[^1].Messages[0].Content;

        Assert.Contains("P001｜", prefix);
        Assert.Contains("P002｜寒阳", prefix);
        Assert.Contains("P003｜", prefix);
    }

    [Fact]
    public async Task OnDemand_AddsMentionedPersonBeyondPrefixCap()
    {
        await using var h = new Harness("""
            {"typing_jitter":false,"bubble_gap_min_sec":0,"bubble_gap_max_sec":0,
             "adaptive_window":false,"quiet_sec":1,"max_wait_sec":30,
             "active_tick_sec":0,"prefix_person_max":1,
             "on_demand_memory":true,"on_demand_max_items":2}
            """);

        for (var i = 1; i <= 2; i++)
        {
            await h.SendAsync($"我是{i}号", openId: $"open-{i}", mention: false);
            await h.SendAsync($"再说一句{i}", openId: $"open-{i}", mention: false);
            h.Clock.Advance(5);
            await h.Runtime.TickAsync();
        }

        h.Db.Exec("UPDATE person SET memory_text='爱接梗' WHERE person_id=1");
        h.Db.Exec("UPDATE person SET name='寒阳', display_label='寒阳', memory_text='爱立flag' WHERE person_id=2");

        // 前缀只放 1 个人（按 person_id 先选 P001）；被点名的寒阳在前缀之外，走尾部按需补充。
        await h.TurnAsync("@小助手 寒阳在吗", openId: "open-1");
        var messages = h.Model.Requests[^1].Messages;
        var prefix = messages[0].Content;
        var tail = messages[^1].Content;

        Assert.Contains("P001｜", prefix);
        Assert.DoesNotContain("P002｜", prefix);
        Assert.Contains("【补充记忆】", tail);
        Assert.Contains(PromptAssembler.PersonHeader, tail);
        Assert.Contains("P002｜寒阳", tail);
    }

    [Fact]
    public async Task ChatSnapshot_ShowsReceiveModePerChat()
    {
        await using var h = new Harness();

        await h.SendAsync("@小助手 在吗", chatId: "g-at", openId: "open-a",
            eventType: "GROUP_AT_MESSAGE_CREATE");
        await h.SendAsync("随便聊聊", chatId: "g-all", openId: "open-b", mention: false,
            eventType: "GROUP_MESSAGE_CREATE");

        var chats = h.Runtime.Snapshot().ToDictionary(c => c.ChatId);

        // 两个群的事件类型不同：不能因为一个群开了全量，就把另一个群也当成全量。
        Assert.False(chats["g-at"].ReceivesAllGroupMessages ?? true);
        Assert.True(chats["g-all"].ReceivesAllGroupMessages ?? false);
    }

    [Fact]
    public async Task Cadence_DoesNotReplyWhileMentionIsPending()
    {
        await using var h = new Harness("""
            {"typing_jitter":false,"bubble_gap_min_sec":0,"bubble_gap_max_sec":0,
             "adaptive_window":false,"quiet_sec":5,"max_wait_sec":30,
             "active_tick_sec":1,"question_reply_probability":0,"reply_probability":0}
            """);

        await h.TurnAsync("@小助手 第一句");
        Assert.Single(h.Adapter.Sent);

        // 节拍已到期，但第二条 @ 还在防抖等待：节拍不能抢先汇总回复，否则会 @ 回一次 + 节拍回一次。
        h.Clock.Advance(2);
        await h.SendAsync("@小助手 第二句", mention: true);
        await h.Runtime.TickAsync();
        Assert.Single(h.Adapter.Sent);

        // 防抖结束后只回这一条 @，没有额外的节拍回复。
        h.Clock.Advance(6);
        await h.Runtime.TickAsync();
        Assert.Equal(2, h.Adapter.Sent.Count);
    }

    [Fact]
    public void BubbleSplitter_CutsAtSentenceBoundary()
    {
        var bubbles = BubbleSplitter.Split("好的。我六点到。你先去占座。", maxBubbles: 3, softLimit: 48);

        Assert.Equal(3, bubbles.Count);
        Assert.Equal("好的。", bubbles[0]);
        Assert.Equal("我六点到。", bubbles[1]);
    }

    [Fact]
    public void BubbleSplitter_KeepsEllipsisTogether()
    {
        var bubbles = BubbleSplitter.Split("这个嘛……再说吧。");
        Assert.Equal(2, bubbles.Count);
        Assert.Equal("这个嘛……", bubbles[0]);
        Assert.Equal("再说吧。", bubbles[1]);
    }

    [Fact]
    public void Delivery_StripsReplyPrefixAndMention()
    {
        var cfg = new EffectiveConfig(ConfigResolver.Resolve(Presets.Balanced, null, null));
        var plan = Delivery.Plan("回复：@阿杰 六点半见。", cfg);

        Assert.Equal("阿杰", plan.MentionLabel);
        Assert.Single(plan.Bubbles);
        Assert.Equal("六点半见。", plan.Bubbles[0]);
    }

    [Fact]
    public void Debounce_AdaptiveWindowWidensWhenBusy()
    {
        var cfg = new EffectiveConfig(ConfigResolver.Resolve(Presets.Balanced, null, null));

        Assert.Equal(cfg.AdaptiveMinSec, Debounce.QuietSeconds(cfg, 1));
        Assert.Equal(cfg.AdaptiveMaxSec, Debounce.QuietSeconds(cfg, 10));
    }

    [Fact]
    public void Trigger_QuestionHeuristic()
    {
        Assert.True(TriggerEngine.LooksLikeQuestion("明天几点集合？"));
        Assert.True(TriggerEngine.LooksLikeQuestion("你觉得这样行吗"));
        Assert.False(TriggerEngine.LooksLikeQuestion("今天好累"));
    }

    [Fact]
    public async Task Message_OnlyEmojiPlaceholder_IsDropped()
    {
        await using var h = new Harness();

        await h.SendAsync("[表情]", mention: true);

        Assert.Empty(h.Runtime.Snapshot());
        Assert.Equal(0, h.Db.ScalarLong("SELECT COUNT(*) FROM message"));
        Assert.Empty(h.Model.Requests);
        Assert.Empty(h.Adapter.Sent);
    }

    [Fact]
    public async Task NameCommand_ChangesName_WithoutCallingModel()
    {
        await using var h = new Harness();

        await h.SendAsync("名字=寒阳", mention: true);

        Assert.Empty(h.Model.Requests);
        Assert.Equal(0, h.Db.ScalarLong("SELECT COUNT(*) FROM message"));
        Assert.Equal(0, h.Db.ScalarLong("SELECT COUNT(*) FROM session"));
        Assert.Single(h.Adapter.Sent);
        Assert.Contains("寒阳", h.Adapter.Sent[0].Content);
        Assert.NotNull(h.Adapter.Sent[0].ReplyToMessageId); // 被动回复，未开全量的群也能发出去

        var person = h.Identity.GetPersonByOpenId(Harness.UserOpenId);
        Assert.NotNull(person);
        Assert.Equal("寒阳", person!.Name);
    }

    [Fact]
    public async Task NameCommand_EnforcesTwoToSixCharacters()
    {
        await using var h = new Harness();

        await h.SendAsync("名字=寒", mention: true);
        Assert.Contains("2–6", h.Adapter.Sent[^1].Content);
        Assert.Null(h.Identity.GetPersonByOpenId(Harness.UserOpenId));

        await h.SendAsync("名字=六六大顺好运来", mention: true);
        Assert.Contains("2–6", h.Adapter.Sent[^1].Content);
        Assert.Null(h.Identity.GetPersonByOpenId(Harness.UserOpenId));

        await h.SendAsync("名字=寒阳", mention: true);
        var person = h.Identity.GetPersonByOpenId(Harness.UserOpenId);
        Assert.NotNull(person);
        Assert.Equal("寒阳", person!.Name);
    }

    [Fact]
    public async Task NameCommand_RejectsPlaceholderAndDuplicateNames()
    {
        await using var h = new Harness();

        await h.SendAsync("名字=路人A", mention: true);
        Assert.Null(h.Identity.GetPersonByOpenId(Harness.UserOpenId));
        Assert.Contains("名字没改成", h.Adapter.Sent[^1].Content);

        await h.SendAsync("名字=寒阳", openId: "open-a", mention: true);
        var first = h.Identity.GetPersonByOpenId("open-a");
        Assert.NotNull(first);
        Assert.Equal("寒阳", first!.Name);

        await h.SendAsync("名字=寒阳", openId: "open-b", mention: true);
        var second = h.Identity.GetPersonByOpenId("open-b");
        Assert.NotNull(second);
        Assert.Equal(string.Empty, second!.Name);
        Assert.Contains("已经", h.Adapter.Sent[^1].Content);
    }

    [Fact]
    public async Task NameChange_ReplacesOldNameInEventsAndPersonMemory()
    {
        await using var h = new Harness();

        // 先让 1 号建档，但不触发回复。
        await h.SendAsync("我是1号", openId: "open-1", mention: false);
        await h.SendAsync("再说一句", openId: "open-1", mention: false);
        h.Clock.Advance(5);
        await h.Runtime.TickAsync();

        var person = h.Identity.GetPersonByOpenId("open-1");
        Assert.NotNull(person);
        var personId = person!.PersonId;

        h.Db.Exec("UPDATE person SET memory_text='自称群友01，爱立flag' WHERE person_id=@id", ("@id", personId));

        var eventId = h.Events.Add(new EventDraft("昨天", null, Harness.ChatId,
            "群友01约周五吃火锅", new[] { "群友01", "火锅" }, new[] { personId }), "b1", "test");

        await h.SendAsync("名字=寒阳", openId: "open-1", mention: true);

        var updated = h.Identity.GetPerson(personId);
        Assert.NotNull(updated);
        Assert.Equal("寒阳", updated!.Name);
        Assert.Contains("寒阳", updated.MemoryText);
        Assert.DoesNotContain("群友01", updated.MemoryText);

        var item = h.Events.GetById(eventId);
        Assert.NotNull(item);
        Assert.Contains("寒阳", item!.Detail);
        Assert.DoesNotContain("群友01", item.Detail);
        Assert.Contains("寒阳", item.Keywords);
    }

    [Fact]
    public async Task CleanupDuplicateNames_KeepsLowestPersonId()
    {
        await using var h = new Harness();

        for (var i = 1; i <= 3; i++)
        {
            await h.SendAsync($"我是{i}号", openId: $"open-{i}", mention: false);
            await h.SendAsync($"再说一句{i}", openId: $"open-{i}", mention: false);
            h.Clock.Advance(5);
            await h.Runtime.TickAsync();
        }

        h.Db.Exec("UPDATE person SET name='寒阳', display_label='寒阳', name_source='summary' WHERE person_id IN (1,3)");
        h.Db.Exec("UPDATE person SET name='阿懒', display_label='阿懒', name_source='summary' WHERE person_id=2");

        var cleared = h.Identity.CleanupDuplicateNames();

        Assert.Equal(1, cleared);
        Assert.Equal("寒阳", h.Identity.GetPerson(1)!.Name);
        Assert.Equal(string.Empty, h.Identity.GetPerson(3)!.Name);
        Assert.Equal("群友03", h.Identity.GetPerson(3)!.DisplayLabel);
    }

    [Fact]
    public void Trigger_PureEmojiIsNoise()
    {
        Assert.True(TriggerEngine.IsOnlyNoise("😂😂😂"));
        Assert.True(TriggerEngine.IsOnlyNoise("[表情]"));
        Assert.True(TriggerEngine.IsOnlyNoise("[表情][图片]"));
        Assert.False(TriggerEngine.IsOnlyNoise("哈哈😂"));
        Assert.False(TriggerEngine.IsOnlyNoise("[表情]在吗"));
    }
}
