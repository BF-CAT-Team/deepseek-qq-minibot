using Xunit;

namespace Bot.Tests;

/// <summary>
/// 入站长度闸门（max_message_chars）：超过上限的消息在进入运行时之前就被整条丢弃，
/// 不能建档、不能进 message 表 / Recent / Pending，更不能触发一次模型调用。
/// </summary>
public sealed class MessageLengthTests
{
    private static Harness WithLimit(int limit) =>
        new(tweakParameters: p => p["max_message_chars"] = limit);

    [Fact]
    public async Task Overlong_message_is_dropped_before_any_buffer_or_model_call()
    {
        await using var h = WithLimit(20);

        var reply = await h.TurnAsync(new string('长', 21));

        Assert.Null(reply);
        Assert.Empty(h.Model.Requests);
        Assert.Empty(h.Runtime.Snapshot());
        Assert.Equal(0, h.Db.ScalarLong("SELECT COUNT(*) FROM message"));
        Assert.Equal(0, h.Db.ScalarLong("SELECT COUNT(*) FROM session"));
        Assert.Empty(h.Identity.ListActive());
    }

    [Fact]
    public async Task Message_exactly_at_the_limit_still_goes_through()
    {
        await using var h = WithLimit(20);

        var reply = await h.TurnAsync(new string('长', 20));

        Assert.NotNull(reply);
        Assert.Single(h.Model.Requests);
        Assert.Equal(1, h.Db.ScalarLong("SELECT COUNT(*) FROM message"));
    }

    [Fact]
    public async Task Zero_disables_the_limit()
    {
        await using var h = WithLimit(0);

        var reply = await h.TurnAsync(new string('长', 2000));

        Assert.NotNull(reply);
        Assert.Single(h.Model.Requests);
    }

    [Fact]
    public async Task Dropped_message_does_not_become_backfill_context_for_the_next_turn()
    {
        await using var h = WithLimit(20);

        // 超长消息先被丢掉，紧接着一条正常消息：模型只应看到后一条。
        await h.TurnAsync(new string('长', 40));
        var reply = await h.TurnAsync("在吗");

        Assert.NotNull(reply);
        var request = h.Model.Requests.Single();
        var tail = request.Messages[^1].Content;
        Assert.Contains("在吗", tail);
        Assert.DoesNotContain("长长长", tail);
    }
}
