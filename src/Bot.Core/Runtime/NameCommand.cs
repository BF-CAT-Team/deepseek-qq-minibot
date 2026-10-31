using System.Text.RegularExpressions;

namespace Bot.Core.Runtime;

/// <summary>
/// `名字=XXX` 指令解析。
/// <para>
/// 指令消息不进对话、不触发模型：识别到就由程序直接改档案并回一句。
/// 允许前面带一个 @机器人（QQ 群里的常见写法）。
/// </para>
/// </summary>
public static class NameCommand
{
    private static readonly Regex Pattern = new(
        @"^\s*(?:@[^\s]{1,32}\s+)?名字\s*[=＝]\s*(?<name>[^\r\n]{1,32})\s*$",
        RegexOptions.Compiled);

    public static bool TryParse(string? content, out string requestedName)
    {
        requestedName = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var match = Pattern.Match(content.Trim());
        if (!match.Success)
        {
            return false;
        }

        requestedName = match.Groups["name"].Value.Trim();
        return requestedName.Length > 0;
    }
}
