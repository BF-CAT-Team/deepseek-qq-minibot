using Bot.Core.Config;

namespace Bot.Core.Runtime;

/// <summary>
/// 聚合窗口（文档 3.1.3）：<c>发出条件 = 距最后一条新消息 ≥ quiet_sec 或 距首条未发消息 ≥ max_wait_sec</c>。
/// 固定轮询在单条消息场景下会平白增加延迟，双条件触发能同时兼顾"合并连发"和"别一直等"。
/// </summary>
public static class Debounce
{
    public static double QuietSeconds(EffectiveConfig cfg, int messagesInLastMinute)
    {
        if (!cfg.AdaptiveWindow)
        {
            return cfg.QuietSec;
        }

        // 消息密集时多等一会儿，把连发合并成一批；安静时尽快回。
        return messagesInLastMinute >= 5 ? cfg.AdaptiveMaxSec : cfg.AdaptiveMinSec;
    }

    public static DateTimeOffset FireAt(
        DateTimeOffset firstPendingAt,
        DateTimeOffset lastMessageAt,
        EffectiveConfig cfg,
        int messagesInLastMinute)
    {
        var quiet = QuietSeconds(cfg, messagesInLastMinute);
        var quietCandidate = lastMessageAt.AddSeconds(quiet);
        var waitCandidate = firstPendingAt.AddSeconds(cfg.MaxWaitSec);
        return quietCandidate <= waitCandidate ? quietCandidate : waitCandidate;
    }

    public static TimeSpan Remaining(
        DateTimeOffset firstPendingAt,
        DateTimeOffset lastMessageAt,
        DateTimeOffset now,
        EffectiveConfig cfg,
        int messagesInLastMinute)
    {
        var fireAt = FireAt(firstPendingAt, lastMessageAt, cfg, messagesInLastMinute);
        var remaining = fireAt - now;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }
}
