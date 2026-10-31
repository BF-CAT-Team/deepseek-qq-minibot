using System.Text.RegularExpressions;

namespace Bot.Core.Memory;

/// <summary>名字校验结果。<see cref="Ok"/> 为 false 时 <see cref="Reason"/> 可以直接回给用户。</summary>
public sealed record NameCheck(bool Ok, string Name, string? Reason)
{
    public static NameCheck Pass(string name) => new(true, name, null);

    public static NameCheck Fail(string name, string reason) => new(false, name, reason);
}

/// <summary>一次改名尝试的结果：<see cref="Ok"/>=false 时 <see cref="Message"/> 可直接回复用户。</summary>
public sealed record NameChangeResult(bool Ok, bool Changed, string Name, string Message, PersonRow? Person);

/// <summary>
/// 名字写入规则（所有入口共用：总结入库、名字学习、`名字=XXX` 指令）。
/// <para>
/// 目标不是"严格"，而是避免两类脏数据：占位名（路人A / 群友04 / P016）和重名。
/// 重名会造成 <c>ResolveByLabel</c> 随机命中、事件相关人显示错人，所以宁可不写。
/// </para>
/// </summary>
public static class NameRules
{
    public const int MinLength = 2;

    public const int MaxLength = 6;

    private static readonly Regex Placeholder = new(
        """
        ^(?:
            路人[\sA-Za-z0-9_\-]* |
            群友[\sA-Za-z0-9_?？\-]* |
            网友[\sA-Za-z0-9_\-]* |
            用户[\sA-Za-z0-9_\-]* |
            匿名[\sA-Za-z0-9_\-]* |
            user[\sA-Za-z0-9_\-]* |
            guest[\sA-Za-z0-9_\-]* |
            anonymous |
            unknown |
            某人 |
            未知 |
            [Pp]\d+ |
            [Ee]\d+ |
            [A-Za-z] |
            \d+
        )$
        """,
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    public static NameCheck Check(string? raw, string? botName = null)
    {
        var name = Clean(raw);
        if (name.Length == 0)
        {
            return NameCheck.Fail(name, "名字不能为空");
        }

        if (name.Length < MinLength || name.Length > MaxLength)
        {
            return NameCheck.Fail(name, $"名字长度需要在 {MinLength}–{MaxLength} 个字之间");
        }

        if (name.Any(char.IsControl))
        {
            return NameCheck.Fail(name, "名字里不能有换行或控制字符");
        }

        if (name.Contains('|') || name.Contains('｜'))
        {
            return NameCheck.Fail(name, "名字里不能有竖线");
        }

        if (!name.Any(char.IsLetter))
        {
            return NameCheck.Fail(name, "名字至少要有一个汉字或字母");
        }

        if (Placeholder.IsMatch(name))
        {
            return NameCheck.Fail(name, "「路人A」「群友04」这类占位名不能当名字");
        }

        if (!string.IsNullOrWhiteSpace(botName)
            && string.Equals(name, Clean(botName), StringComparison.OrdinalIgnoreCase))
        {
            return NameCheck.Fail(name, "不能和机器人同名");
        }

        return NameCheck.Pass(name);
    }

    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var name = raw.Trim();
        name = name.Trim('"', '\'', '“', '”', '‘', '’', '「', '」', '『', '』');
        name = name.TrimEnd('。', '！', '？', '!', '?', '，', ',', '；', ';', '：', ':');
        return name.Trim();
    }
}
