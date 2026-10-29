using System.Text;

namespace Bot.Core.Text;

/// <summary>
/// 气泡切分（文档 3.5.4 第 1 条）：一次请求拿到的整段回复切成 1–3 条依次发出。
/// token 不变，观感差别极大，是性价比最高的一条"真人感"优化。
/// </summary>
public static class BubbleSplitter
{
    private static readonly char[] Terminators = { '。', '！', '？', '!', '?', '；', ';', '…', '\n' };

    public static IReadOnlyList<string> Split(string? text, int maxBubbles = 3, int softLimit = 48)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        maxBubbles = Math.Max(1, maxBubbles);
        softLimit = Math.Max(8, softLimit);

        var segments = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            var piece = Cleanup(current.ToString());
            if (piece.Length > 0)
            {
                segments.Add(piece);
            }

            current.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            current.Append(ch);

            if (Array.IndexOf(Terminators, ch) < 0)
            {
                continue;
            }

            // 连续的省略号/叹号归入同一句，避免"……"被切成两条。
            while (i + 1 < text.Length && Array.IndexOf(Terminators, text[i + 1]) >= 0)
            {
                i++;
                current.Append(text[i]);
            }

            Flush();
        }

        Flush();

        if (segments.Count == 0)
        {
            return Array.Empty<string>();
        }

        // 只合并真正意义上的碎片（"嗯。""好。"），完整句不合并——否则"好的。我六点到。"
        // 会被粘成一条，反而失去了多气泡的观感。
        var merged = new List<string>();
        foreach (var segment in segments)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (last.Length + segment.Length <= 6)
                {
                    merged[^1] = last + segment;
                    continue;
                }
            }

            merged.Add(segment);
        }

        if (merged.Count <= maxBubbles)
        {
            return merged;
        }

        // 超出上限：把尾部并进最后一条，避免"话说一半被截断"。
        var head = merged.Take(maxBubbles - 1).ToList();
        var tailText = string.Concat(merged.Skip(maxBubbles - 1));
        head.Add(Cleanup(tailText));
        return head.Where(s => s.Length > 0).ToList();
    }

    private static string Cleanup(string value)
    {
        var trimmed = value.Trim();
        trimmed = trimmed.TrimStart('\n', '\r');
        return trimmed.TrimEnd('\n', '\r');
    }
}
