namespace Bot.Core;

/// <summary>
/// 模型可见的短编号。集中在一处，避免"记忆块里是群友07、事件行里是群友7"这种漂移。
/// <para>编号会一直增长，所以这里的关键约束是：<b>变宽可以，撞号不行</b>。</para>
/// </summary>
public static class Labels
{
    /// <summary>人物编号 P001。超过 999 自然变宽成 P1000，不做截断（截断会撞号）。</summary>
    public static string Person(int personId) => "P" + personId.ToString("D3");

    /// <summary>事件编号 E001。同样只补零不截断。</summary>
    public static string Event(long eventId) => "E" + eventId.ToString("D3");

    /// <summary>
    /// 无名字人物的显示标签。
    /// <para>
    /// 99 人以内是"群友07"；到第 100 个人时改用全编号"群友107"。
    /// 如果一直取后两位，person_id 1 和 101 会同时渲染成"群友01"——
    /// 模型会把两个不同的人当成同一个，而且 <c>ResolveByLabel</c> 会随机挑中其中一个。
    /// </para>
    /// </summary>
    public static string GroupMate(int personId) =>
        personId < 100 ? "群友" + personId.ToString("D2") : "群友" + personId;

    /// <summary>把 "E003" / "#12" / " 12 " 之类的输入解析成事件 id，解析不出来返回 false。</summary>
    public static bool TryParseEventId(string? text, out long eventId)
    {
        eventId = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim().TrimStart('E', 'e', '#', 'E');
        trimmed = trimmed.Trim();
        return long.TryParse(trimmed, out eventId) && eventId > 0;
    }
}
