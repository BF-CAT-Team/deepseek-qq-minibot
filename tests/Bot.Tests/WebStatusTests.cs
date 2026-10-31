using Bot.Core.Abstractions;
using Bot.Core.Config;
using Bot.Core.Domain;
using Bot.Core.Runtime;
using Bot.QQ;
using Xunit;

namespace Bot.Tests;

/// <summary>
/// 配置页两块新东西的回归：
/// 1) QQ 接入状态不能写死成"已连接"——必须是网关真实状态；
/// 2) "当前会话"点进去看到的，必须就是当时发给 API 的 messages。
/// </summary>
public sealed class WebStatusTests
{
    [Fact]
    public async Task Mock_adapter_reports_online_and_counts_traffic()
    {
        await using var adapter = new MockChatAdapter();
        await adapter.StartAsync(CancellationToken.None);

        var before = adapter.Status;
        Assert.True(before.Online);
        Assert.Equal("mock", before.State);

        await adapter.InjectAsync(new IncomingMessage
        {
            ChatId = "mock-group",
            Kind = ChatKind.Group,
            OpenId = Harness.UserOpenId,
            Content = "在吗",
            MentionsBot = true,
            MessageId = "m1",
            Timestamp = DateTimeOffset.UtcNow,
        });

        await adapter.SendAsync(new OutgoingMessage("mock-group", ChatKind.Group, "在的"), CancellationToken.None);

        var after = adapter.Status;
        Assert.Equal(1, after.InboundCount);
        Assert.Equal(1, after.OutboundCount);
        Assert.NotNull(after.LastInboundAt);
        Assert.NotNull(after.LastOutboundAt);
    }

    [Fact]
    public async Task Qq_adapter_is_not_online_before_the_gateway_connects()
    {
        await using var adapter = new QqChatAdapter(new QqSettings
        {
            Adapter = "qq",
            AppId = "102000000",
            Secret = "test-secret",
        });

        var status = adapter.Status;
        Assert.False(status.Online);
        Assert.Equal("stopped", status.State);
        Assert.False(status.ReceivesAllGroupMessages);
    }

    [Fact]
    public async Task Session_view_matches_the_messages_sent_to_the_api()
    {
        await using var h = new Harness();

        var reply = await h.TurnAsync("在吗");
        Assert.NotNull(reply);

        var snapshot = h.Runtime.Snapshot().Single();
        Assert.NotNull(snapshot.SessionId);
        Assert.Equal("本地测试群", snapshot.DisplayName);

        var inspector = new SessionInspector(h.Db, h.Sessions, h.Logs, h.Estimator);
        var view = inspector.Get(snapshot.SessionId!);

        Assert.NotNull(view);
        Assert.Equal("active", view!.State);
        Assert.Equal(2, view.Turns.Count);

        Assert.Equal("user", view.Turns[0].Role);
        Assert.Equal("dynamic_tail", view.Turns[0].Kind);
        Assert.Contains("在吗", view.Turns[0].Content);

        Assert.Equal("assistant", view.Turns[1].Role);
        Assert.Equal(reply!, view.Turns[1].Content);

        // 视图里的 system / 历史必须与真正发给 API 的 messages 逐字节一致。
        var request = h.Model.Requests.Single();
        Assert.Equal("system", request.Messages[0].Role);
        Assert.Equal(request.Messages[0].Content, view.PrefixText);
        Assert.Equal(request.Messages[1].Content, view.Turns[0].Content);

        Assert.Single(view.Requests);
        Assert.True(view.Requests[0].SendConfirmed);
        Assert.Equal("chat", view.Requests[0].Kind);
        Assert.Equal(120, view.Requests[0].LatencyMs);
    }

    [Fact]
    public async Task Session_inspector_finds_the_latest_session_for_a_chat()
    {
        await using var h = new Harness();
        await h.TurnAsync("第一句");
        await h.TurnAsync("第二句");

        var inspector = new SessionInspector(h.Db, h.Sessions, h.Logs, h.Estimator);
        var latest = inspector.FindLatest(Harness.ChatId);

        Assert.NotNull(latest);
        Assert.Equal(h.Runtime.Snapshot().Single().SessionId, latest!.SessionId);
        Assert.Equal(4, latest.Turns.Count);
    }

    [Fact]
    public async Task Closed_session_is_still_viewable_after_the_chat_goes_idle()
    {
        await using var h = new Harness();
        h.Model.Reply = request =>
            request.Messages[^1].Content.Contains("【任务】", StringComparison.Ordinal)
                ? "NONE"
                : "好啊，那就这么定了。";

        await h.TurnAsync("记住这句话");
        var live = h.Runtime.Snapshot().Single();
        Assert.NotNull(live.SessionId);
        var closedSessionId = live.SessionId!;

        // 强制轮换 = 会话转空闲：运行时释放上下文，但库里的对话必须还在。
        await h.Runtime.ForceRotateAsync(Harness.ChatId);

        var idle = h.Runtime.Snapshot().Single();
        Assert.Null(idle.SessionId);
        Assert.Equal(SessionState.Idle, idle.State);

        var inspector = new SessionInspector(h.Db, h.Sessions, h.Logs, h.Estimator);
        var brief = inspector.FindLatestBrief(Harness.ChatId);

        Assert.NotNull(brief);
        Assert.Equal(closedSessionId, brief!.SessionId);
        Assert.Equal("closed", brief.State);
        Assert.Equal(1, brief.TurnCount);

        var view = inspector.Get(closedSessionId);
        Assert.NotNull(view);
        Assert.Equal("closed", view!.State);
        Assert.Equal(2, view.Turns.Count);
        Assert.Contains("记住这句话", view.Turns[0].Content);
        Assert.Equal("好啊，那就这么定了。", view.Turns[1].Content);

        var summary = view.Requests.Single(r => r.Kind == "summary");
        Assert.True(summary.ParseOk);
        Assert.Equal(120, summary.LatencyMs);
    }

    [Fact]
    public async Task Gateway_status_starts_stopped()
    {
        await using var gateway = new QqGateway(new QqApi(new QqSettings()));
        Assert.Equal(GatewayState.Stopped, gateway.Snapshot().State);
    }
}
