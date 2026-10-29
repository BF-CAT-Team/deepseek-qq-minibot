namespace Bot.Core;

/// <summary>可替换的时钟。定时逻辑（防抖、冷却、归档）全部经由它，测试可注入假时钟。</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public static class TimeUtil
{
    public static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static long Unix(DateTimeOffset time) => time.ToUnixTimeSeconds();

    public static DateTimeOffset FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);

    public static long Minutes(int minutes) => minutes * 60L;
}
