using System.Globalization;

namespace Bot.Core.Runtime;

/// <summary>一个关键词及其权重。"笑死:2" 表示每次命中算 2 分；不写权重默认 1 分。</summary>
public sealed record StickerKeyword(string Word, double Weight);

/// <summary>某个关键词在某段回复里命中的情况（用于配置页试算展示）。</summary>
public sealed record StickerHit(string Word, double Weight, int Count);

/// <summary>图片库里的一个表情包（元数据；图片文件在 data\stickers\ 下）。</summary>
public sealed record StickerRow(
    int StickerId,
    string FileName,
    string OriginalName,
    string ContentType,
    string Keywords,
    bool Enabled,
    int SendCount,
    long? LastSentAt,
    long CreatedAt,
    long UpdatedAt);

/// <summary>一张表情包对某段回复的打分结果。<paramref name="Threshold"/> 是全局阈值，不是每张图自己的。</summary>
public sealed record StickerScore(StickerRow Sticker, double Score, double Threshold, IReadOnlyList<StickerHit> Hits)
{
    public bool MeetsThreshold => Score + 1e-6 >= Threshold;

    public string HitText => string.Join('、', Hits.Select(h => h.Count > 1
        ? $"{h.Word}×{h.Count}"
        : h.Word));
}

/// <summary>
/// 表情包匹配（全部本地计算，零 token）。
/// <para>
/// 规则：回复正文里每个关键词的<b>命中次数</b> × 该词权重，累加得到这张图的得分；
/// 得分达到<b>全局</b>「触发最低权重」才进入候选；候选里取得分最高的一张（同分取 sticker_id 小的）；
/// 最后按<b>全局概率</b>掷一次骰子决定是否真的发送。
/// </para>
/// <para>
/// 匹配只看机器人这一轮的回复正文。表情包不会追加进会话历史，也不改 prompt / 前缀缓存。
/// </para>
/// </summary>
public static class StickerMatch
{
    public const double DefaultWeight = 1d;
    public const double DefaultMinWeight = 2d;
    public const double MinKeywordWeight = 0.1d;
    public const double MaxKeywordWeight = 10d;
    public const int MaxKeywords = 50;

    private static readonly char[] Separators = { ',', '，', ';', '；', '\n', '\r', '|' };
    private static readonly char[] WeightSeparators = { ':', '：', '*' };

    /// <summary>
    /// 解析关键词串。支持「笑死」「笑死:2」「笑死：2」「笑死*2」；
    /// 分隔符是逗号 / 分号 / 换行 / 竖线。重复词只保留第一次出现的写法。
    /// </summary>
    public static IReadOnlyList<StickerKeyword> ParseKeywords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<StickerKeyword>();
        }

        var result = new List<StickerKeyword>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (result.Count >= MaxKeywords)
            {
                break;
            }

            var item = raw.Trim();
            if (item.Length == 0)
            {
                continue;
            }

            var word = item;
            var weight = DefaultWeight;

            var cut = item.LastIndexOfAny(WeightSeparators);
            if (cut > 0
                && cut < item.Length - 1
                && double.TryParse(item[(cut + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var parsed)
                && parsed > 0)
            {
                var candidate = item[..cut].Trim();
                if (candidate.Length > 0)
                {
                    word = candidate;
                    weight = Math.Clamp(parsed, MinKeywordWeight, MaxKeywordWeight);
                }
            }

            if (word.Length == 0 || !seen.Add(word))
            {
                continue;
            }

            result.Add(new StickerKeyword(word, weight));
        }

        return result;
    }

    /// <summary>算一张图的得分。同一个词出现 N 次就是 N 份权重。阈值由调用方传入（全局）。</summary>
    public static StickerScore Score(string? text, StickerRow sticker, double threshold)
    {
        var hits = new List<StickerHit>();
        var score = 0d;

        if (!string.IsNullOrEmpty(text))
        {
            foreach (var keyword in ParseKeywords(sticker.Keywords))
            {
                var count = CountOccurrences(text, keyword.Word);
                if (count == 0)
                {
                    continue;
                }

                hits.Add(new StickerHit(keyword.Word, keyword.Weight, count));
                score += count * keyword.Weight;
            }
        }

        return new StickerScore(sticker, Math.Round(score, 2), threshold, hits);
    }

    public static IReadOnlyList<StickerScore> Score(string? text, IEnumerable<StickerRow> stickers, double threshold) =>
        stickers.Select(sticker => Score(text, sticker, threshold)).ToArray();

    /// <summary>候选里取得分最高的一张；同分时取 sticker_id 小的，保证结果可预期、可测试。</summary>
    public static StickerScore? Best(IEnumerable<StickerScore> scores) =>
        scores
            .Where(score => score.Sticker.Enabled && score.MeetsThreshold)
            .OrderByDescending(score => score.Score)
            .ThenBy(score => score.Sticker.StickerId)
            .FirstOrDefault();

    /// <summary>非重叠计数：回复里出现几次关键词。</summary>
    public static int CountOccurrences(string text, string word)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(word))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while (index <= text.Length - word.Length)
        {
            var found = text.IndexOf(word, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                break;
            }

            count++;
            index = found + word.Length;
        }

        return count;
    }
}
