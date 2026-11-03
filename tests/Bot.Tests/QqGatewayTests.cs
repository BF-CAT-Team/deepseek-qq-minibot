using Bot.QQ;
using Xunit;

namespace Bot.Tests;

/// <summary>
/// 网关自愈的两个判据：哪些关闭码必须清会话（不能 RESUME）、心跳 ack 多久算掉线。
/// 这两个分支决定了"断开之后能不能自己连回来"，所以单独断言。
/// </summary>
public sealed class QqGatewayTests
{
    [Theory]
    [InlineData(4006)]
    [InlineData(4007)]
    [InlineData(4009)]
    public void Session_invalidating_close_codes_force_a_fresh_identify(int code) =>
        Assert.True(QqGateway.ShouldResetSession(code));

    [Theory]
    [InlineData(1000)]
    [InlineData(4004)]
    [InlineData(4014)]
    public void Other_close_codes_keep_the_session(int code) =>
        Assert.False(QqGateway.ShouldResetSession(code));

    [Fact]
    public void Null_close_code_keeps_the_session() => Assert.False(QqGateway.ShouldResetSession(null));

    [Fact]
    public void Heartbeat_ack_is_tolerated_for_two_and_a_half_intervals()
    {
        var interval = TimeSpan.FromSeconds(41.25);
        var start = DateTimeOffset.UtcNow;

        Assert.False(QqGateway.HeartbeatTimedOut(start, start.AddSeconds(100), interval));
        Assert.True(QqGateway.HeartbeatTimedOut(start, start.AddSeconds(105), interval));
    }
}
