using System.Text.RegularExpressions;

namespace Bot.Core.Memory;

public enum GuardKind
{
    /// <summary>记忆条目正文（事件详情 / 人物记忆）：防止存储型 prompt 注入。</summary>
    Entry,

    /// <summary>名字：防止"傻子"被当成永久称呼。</summary>
    Name,

    /// <summary>性格 / 评价：只能描述互动风格，不得涉及敏感属性。</summary>
    Evaluation,
}

public sealed record GuardResult(bool Ok, string? Reason)
{
    public static readonly GuardResult Pass = new(true, null);

    public static GuardResult Reject(string reason) => new(false, reason);
}

/// <summary>
/// 记忆安全（文档 3.4.6 / 3.4.7 / 3.4.8）。记忆区是持久攻击面：
/// 一条被诱导写进去的"以后都骂人"会进入此后每个会话的前缀，看起来还像正常记忆。
/// </summary>
public static partial class MemoryGuard
{
    /// <summary>指令性短语。只拦"对着模型说话"的句式，避免把"他忽略了我的消息"误判。</summary>
    private static readonly Regex[] InstructionPatterns =
    {
        new(@"(忽略|无视|忘记|跳过)\s*((之前|以上|上述|前面|所有|全部|你|的|这些)\s*){1,3}(指令|规则|设定|提示|要求)",
            RegexOptions.IgnoreCase),
        new(@"(从现在起|从今以后|今后|以后)\s*你?\s*(必须|要|只能|不得)"),
        new(@"(你|系统|模型|助手)\s*(必须|只能|不得|禁止)\s*(永远|一直|始终)?"),
        new(@"(system|assistant|developer|系统|提示词)\s*[:：]", RegexOptions.IgnoreCase),
        new(@"\b(ignore|disregard)\s+(all\s+)?(previous|above|prior)", RegexOptions.IgnoreCase),
        new(@"<\s*\|[^|]*\|\s*>"),
    };

    /// <summary>敏感属性词。只用于评价/性格字段——事件详情里出现"生病"是正常聊天。</summary>
    private static readonly string[] SensitiveTerms =
    {
        "艾滋病", "性病", "癌症", "抑郁", "精神病", "精神分裂", "残疾", "怀孕", "流产",
        "同性恋", "性取向", "性生活", "卖淫", "嫖娼", "吸毒", "赌博", "坐牢", "犯罪",
        "工资", "收入", "负债", "欠债", "存款", "房贷", "信用卡", "股票",
        "党员", "政治", "信仰", "宗教", "民族", "种族",
    };

    /// <summary>贬损词。名字与评价都要拦——骂一句就可能变成永久称呼。</summary>
    private static readonly string[] InsultTerms =
    {
        "傻逼", "傻子", "蠢货", "废物", "垃圾", "智障", "脑残", "白痴", "贱", "sb", "傻b",
        "去死", "滚蛋", "畜生", "狗东西",
    };

    public static GuardResult Inspect(string? text, GuardKind kind)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return GuardResult.Pass;
        }

        if (text.Length > 2000)
        {
            return GuardResult.Reject("内容过长");
        }

        if (text.Any(char.IsControl) && text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
        {
            return GuardResult.Reject("含控制字符");
        }

        if (kind != GuardKind.Name)
        {
            foreach (var pattern in InstructionPatterns)
            {
                if (pattern.IsMatch(text))
                {
                    return GuardResult.Reject("疑似指令注入：" + pattern);
                }
            }
        }

        var lowered = text.ToLowerInvariant();
        foreach (var term in InsultTerms)
        {
            if (lowered.Contains(term, StringComparison.Ordinal))
            {
                return GuardResult.Reject("含贬损词：" + term);
            }
        }

        if (kind is GuardKind.Evaluation or GuardKind.Name)
        {
            foreach (var term in SensitiveTerms)
            {
                if (lowered.Contains(term, StringComparison.Ordinal))
                {
                    return GuardResult.Reject("触及敏感属性：" + term);
                }
            }
        }

        return GuardResult.Pass;
    }

    /// <summary>长度约束下的截断：优先切在句末标点，不切在句子中间（文档 3.4.1 的程序保险）。</summary>
    public static string TruncateAtSentence(string? text, int limit)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        if (limit <= 0 || trimmed.Length <= limit)
        {
            return trimmed;
        }

        var window = trimmed[..limit];
        var lastBreak = window.LastIndexOfAny(new[] { '。', '！', '？', '；', '.', '!', '?', ';', '，', ',' });
        if (lastBreak >= limit / 2)
        {
            return window[..(lastBreak + 1)].TrimEnd();
        }

        return window.TrimEnd();
    }

    /// <summary>评价字段的措辞约束提示，写进 prompt（文档 3.4.7）。</summary>
    public const string EvaluationHint = "评价只说互动风格（爱开玩笑、回复简短），不写人身贬损、健康、财务、政治、性取向等敏感属性。";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
