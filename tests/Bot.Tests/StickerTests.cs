using Bot.Core.Abstractions;
using Bot.Core.Domain;
using Bot.Core.Runtime;
using Bot.QQ;

namespace Bot.Tests;

/// <summary>
/// 表情包：关键词算分、阈值、全局概率、被动 / 主动两种发送方式，
/// 以及最重要的一条——表情包不进入对话历史（不追加回合、不写 message 表）。
/// </summary>
public sealed class StickerTests
{
    [Fact]
    public void ParseKeywords_SupportsWeightsAndSeparators()
    {
        var keywords = StickerMatch.ParseKeywords("笑死:2，哈哈：1.5;无语, 笑死:9");

        Assert.Equal(3, keywords.Count);
        Assert.Equal(new StickerKeyword("笑死", 2), keywords[0]);
        Assert.Equal(new StickerKeyword("哈哈", 1.5), keywords[1]);
        Assert.Equal(new StickerKeyword("无语", 1), keywords[2]);
    }

    [Fact]
    public void ParseKeywords_NonNumericSuffixStaysPartOfWord()
    {
        var keywords = StickerMatch.ParseKeywords("笑死:很多, 好:0");

        Assert.Equal(2, keywords.Count);
        Assert.Equal("笑死:很多", keywords[0].Word);
        Assert.Equal(1, keywords[0].Weight);
        // 0 不是合法权重，整串当关键词处理，随后靠文本匹配自然淘汰。
        Assert.Equal("好:0", keywords[1].Word);
    }

    [Fact]
    public void Score_CountsEveryOccurrence()
    {
        var sticker = new StickerRow(1, "a.png", string.Empty, "image/png", "笑:2", true, 0, null, 0, 0);

        var score = StickerMatch.Score("笑笑笑，笑死", sticker, threshold: 8);

        // 4 次「笑」× 2 分 = 8
        Assert.Equal(8, score.Score);
        Assert.Single(score.Hits);
        Assert.Equal(4, score.Hits[0].Count);
        Assert.True(score.MeetsThreshold);

        // 阈值是全局参数：差 0.1 就不算过。
        Assert.False(StickerMatch.Score("笑笑笑，笑死", sticker, threshold: 8.1).MeetsThreshold);
    }

    [Fact]
    public void Best_UsesGlobalThreshold_AndPicksHighestEnabledScore()
    {
        var belowThreshold = new StickerRow(1, "a.png", string.Empty, "image/png", "笑", true, 0, null, 0, 0);
        var eligible = new StickerRow(2, "b.png", string.Empty, "image/png", "哭:5", true, 0, null, 0, 0);
        var disabled = new StickerRow(3, "c.png", string.Empty, "image/png", "笑:10", false, 0, null, 0, 0);

        // 文本得分：1 号 2 分、2 号 5 分、3 号 20 分（但被停用）。全局阈值 3。
        var best = StickerMatch.Best(
            StickerMatch.Score("笑笑。哭", new[] { belowThreshold, eligible, disabled }, threshold: 3));

        Assert.NotNull(best);
        Assert.Equal(2, best!.Sticker.StickerId);
        Assert.Equal(5, best.Score);

        // 同分取 id 小的，保证结果可预期。
        var first = new StickerRow(5, "a.png", string.Empty, "image/png", "笑", true, 0, null, 0, 0);
        var second = new StickerRow(6, "b.png", string.Empty, "image/png", "笑", true, 0, null, 0, 0);
        var tie = StickerMatch.Best(StickerMatch.Score("笑", new[] { second, first }, threshold: 1));
        Assert.Equal(5, tie!.Sticker.StickerId);
    }

    [Fact]
    public async Task Runtime_SendsStickerAfterReply_WithoutTouchingConversation()
    {
        await using var harness = new Harness();
        var sticker = harness.AddSticker("好");

        var reply = await harness.TurnAsync("在吗");

        Assert.NotNull(reply);
        var stickerMessage = harness.Adapter.Sent.LastOrDefault();
        Assert.NotNull(stickerMessage);
        Assert.NotNull(stickerMessage!.Sticker);
        Assert.Equal(sticker.StickerId, stickerMessage.Sticker!.StickerId);
        Assert.True(File.Exists(stickerMessage.Sticker.FilePath));

        // 会话历史只有「动态尾部 + 回复」两条，表情包不进历史。
        var turns = harness.Db.Query("SELECT role, content FROM session_turn ORDER BY seq",
            reader => reader.GetString(1));
        Assert.Equal(2, turns.Count);
        Assert.DoesNotContain(turns, turn => turn.Contains("sticker", StringComparison.OrdinalIgnoreCase));

        // 表情包不产生新的模型请求，也不写 message 表。
        Assert.Single(harness.ChatRequests);
        Assert.Equal(1, harness.Db.ScalarLong("SELECT COUNT(*) FROM message"));

        Assert.Equal(1, harness.Db.ScalarLong("SELECT send_count FROM sticker WHERE sticker_id=@id",
            ("@id", sticker.StickerId)));
        var log = harness.Stickers.RecentLog(5);
        var entry = Assert.Single(log);
        Assert.Equal("sent", entry.Result);
        Assert.Equal("passive", entry.Mode);
    }

    [Fact]
    public async Task Runtime_PassiveSticker_FollowsTheSameMsgIdAsReply()
    {
        await using var harness = new Harness();
        harness.AddSticker("好");

        await harness.TurnAsync("在吗");

        var sent = harness.Adapter.Sent;
        var bubble = sent.First(message => message.Sticker is null);
        var sticker = sent.First(message => message.Sticker is not null);

        Assert.False(string.IsNullOrWhiteSpace(bubble.ReplyToMessageId));
        Assert.Equal(bubble.ReplyToMessageId, sticker.ReplyToMessageId);
    }

    [Fact]
    public async Task Runtime_UsageCount_TracksOnlySuccessfulSends()
    {
        await using var harness = new Harness();
        var sticker = harness.AddSticker("好");

        await harness.TurnAsync("在吗");
        await harness.TurnAsync("再来一句");

        var row = harness.Stickers.Get(sticker.StickerId);
        Assert.NotNull(row);
        Assert.Equal(2, row!.SendCount);
        Assert.NotNull(row.LastSentAt);
        Assert.Equal(2, harness.Db.ScalarLong(
            "SELECT COUNT(*) FROM sticker_log WHERE sticker_id=@id AND result='sent'",
            ("@id", sticker.StickerId)));
    }

    [Fact]
    public async Task Runtime_UsageCount_DoesNotCountProbabilitySkips()
    {
        await using var harness = new Harness(tweakParameters: parameters => parameters["sticker_probability"] = 0.0);
        var sticker = harness.AddSticker("好");

        await harness.TurnAsync("在吗");

        var row = harness.Stickers.Get(sticker.StickerId);
        Assert.NotNull(row);
        Assert.Equal(0, row!.SendCount);
        Assert.Null(row.LastSentAt);
    }

    [Fact]
    public async Task Runtime_ZeroProbability_NeverSends()
    {
        await using var harness = new Harness(tweakParameters: parameters => parameters["sticker_probability"] = 0.0);
        harness.Runtime.StickerRoll = () => 0.5;
        harness.AddSticker("好");

        await harness.TurnAsync("在吗");

        Assert.DoesNotContain(harness.Adapter.Sent, message => message.Sticker is not null);
        var entry = Assert.Single(harness.Stickers.RecentLog(5));
        Assert.Equal("skipped_probability", entry.Result);
        Assert.Equal(0.5, entry.Roll, 3);
    }

    [Fact]
    public async Task Runtime_CooldownSuppressesSecondSticker()
    {
        await using var harness = new Harness(tweakParameters: parameters => parameters["sticker_cooldown_sec"] = 600);
        harness.AddSticker("好");

        await harness.TurnAsync("在吗");
        await harness.TurnAsync("再说一句");

        Assert.Equal(1, harness.Adapter.Sent.Count(message => message.Sticker is not null));
    }

    [Fact]
    public async Task Runtime_ActiveWithoutMsgId_IsSkippedWhenDisabled()
    {
        await using var harness = new Harness(tweakParameters: parameters =>
            parameters["sticker_active_when_no_msg"] = false);
        harness.AddSticker("好");

        await harness.SendAsync("在吗");
        harness.Clock.Advance(300); // 超过 4.5 分钟：没有可被动回复的 msg_id
        await harness.Runtime.TickAsync();

        Assert.DoesNotContain(harness.Adapter.Sent, message => message.Sticker is not null);
        var entry = Assert.Single(harness.Stickers.RecentLog(5));
        Assert.Equal("skipped_active", entry.Result);
        Assert.Equal("active", entry.Mode);
    }

    [Fact]
    public async Task Runtime_ActiveWithoutMsgId_SendsAsActiveMessageWhenAllowed()
    {
        await using var harness = new Harness();
        harness.AddSticker("好");

        await harness.SendAsync("在吗");
        harness.Clock.Advance(300);
        await harness.Runtime.TickAsync();

        var sticker = Assert.Single(harness.Adapter.Sent, message => message.Sticker is not null);
        Assert.Null(sticker.ReplyToMessageId); // 主动消息：没有 msg_id
        Assert.Equal("active", harness.Stickers.RecentLog(5).Single().Mode);
    }

    [Fact]
    public async Task Runtime_SkipsSticker_WhenPassiveRepliesAlreadyUseAllFiveSlots()
    {
        // QQ 同一个 msg_id 最多回 5 次：5 条气泡已用满，再补一张图会被接口拒掉。
        await using var harness = new Harness(tweakParameters: parameters => parameters["max_reply_bubbles"] = 5);
        harness.Model.Reply = _ => "第一句话。第二句话。第三句话。第四句话。第五句话。";
        harness.AddSticker("话");

        await harness.TurnAsync("在吗");

        Assert.Equal(5, harness.Adapter.Sent.Count(message => message.Sticker is null));
        Assert.DoesNotContain(harness.Adapter.Sent, message => message.Sticker is not null);
        Assert.Empty(harness.Stickers.RecentLog(5));
    }

    [Fact]
    public void StickerStore_ReadsLegacyTableThatStillHasPerImageThreshold()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "testdata", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var dbPath = Path.Combine(dir, "bot.db");

            // 上一版（每图阈值）建的库：sticker 多一列 min_weight NOT NULL。
            // 新版不再读写这一列，但升级旧库时插入不能失败（靠列默认值兜底）。
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE sticker (
                      sticker_id    INTEGER PRIMARY KEY AUTOINCREMENT,
                      file_name     TEXT NOT NULL,
                      original_name TEXT NOT NULL DEFAULT '',
                      content_type  TEXT NOT NULL DEFAULT 'image/png',
                      keywords      TEXT NOT NULL DEFAULT '',
                      min_weight    REAL NOT NULL DEFAULT 1.5,
                      enabled       INTEGER NOT NULL DEFAULT 1,
                      send_count    INTEGER NOT NULL DEFAULT 0,
                      last_sent_at  INTEGER,
                      created_at    INTEGER NOT NULL,
                      updated_at    INTEGER NOT NULL
                    );
                    """;
                command.ExecuteNonQuery();
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            using var db = new Bot.Core.Memory.Db(dbPath);
            db.Initialize();
            var store = new StickerStore(db, Path.Combine(dir, "stickers"));

            var row = store.Create(new StickerInput("笑", true, Harness.TestPng, "a.png"));
            Assert.Equal("笑", row.Keywords);
            Assert.True(row.Enabled);

            store.MarkSent(row.StickerId, DateTimeOffset.UtcNow);
            Assert.Equal(1, store.Get(row.StickerId)!.SendCount);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 临时目录清理失败可忽略
            }
        }
    }

    [Fact]
    public void StickerStore_RejectsBadImages_AndDeletesFileWithRow()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "testdata", Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new Bot.Core.Memory.Db(Path.Combine(dir, "bot.db"));
            db.Initialize();
            var store = new StickerStore(db, Path.Combine(dir, "stickers"));

            Assert.Throws<ArgumentException>(() =>
                store.Create(new StickerInput(string.Empty, true, Harness.TestPng, "a.png")));
            Assert.Throws<ArgumentException>(() =>
                store.Create(new StickerInput("笑", true, new byte[] { 1, 2, 3 }, "a.png")));

            var row = store.Create(new StickerInput("笑", true, Harness.TestPng, "a.png"));
            var path = store.ResolvePath(row);
            Assert.NotNull(path);
            Assert.True(File.Exists(path));

            Assert.True(store.Delete(row.StickerId));
            Assert.False(File.Exists(path));
            Assert.Null(store.Get(row.StickerId));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 临时目录清理失败可忽略
            }
        }
    }

    [Fact]
    public void QqMediaPayloads_MatchOfficialShape()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var upload = QqApi.BuildMediaUploadPayload(bytes, "a.png");
        Assert.Equal(1, upload["file_type"]);
        Assert.Equal("a.png", upload["file_name"]);
        Assert.Equal(Convert.ToBase64String(bytes), upload["file_data"]);
        Assert.Equal(false, upload["srv_send_msg"]);

        var passive = QqApi.BuildMediaMessagePayload("FILE-INFO", "MSG-1", 3);
        Assert.Equal(7, passive["msg_type"]);
        Assert.False(passive.ContainsKey("content"));
        var media = Assert.IsType<Dictionary<string, object?>>(passive["media"]);
        Assert.Equal("FILE-INFO", media["file_info"]);
        Assert.Equal("MSG-1", passive["msg_id"]);
        Assert.Equal(3, passive["msg_seq"]);

        var active = QqApi.BuildMediaMessagePayload("FILE-INFO", null, 1);
        Assert.Equal(7, active["msg_type"]);
        Assert.False(active.ContainsKey("msg_id"));
        Assert.False(active.ContainsKey("msg_seq"));

        Assert.Equal("/v2/groups/G1/files", QqApi.MediaUploadPath(ChatKind.Group, "G1"));
        Assert.Equal("/v2/users/U1/files", QqApi.MediaUploadPath(ChatKind.Direct, "U1"));
    }
}
