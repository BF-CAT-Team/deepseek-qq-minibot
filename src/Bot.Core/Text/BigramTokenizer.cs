using System.Text;

namespace Bot.Core.Text;

public interface ITokenizer
{
    /// <summary>切出用于检索比对的词元（去重、保序、小写化）。</summary>
    IReadOnlyList<string> Tokenize(string text);
}

/// <summary>
/// 无外部词典的中文切词：CJK 连续串取二元组（bigram），ASCII 取单词。
/// <para>
/// 为什么不引 jieba：事件关键词由模型在总结时给出（文档 4.4 的 NEW_EVENT 行），
/// 本地分词只服务于"检索重合度"与"IDF 统计"两处。bigram 在这种短文本重合度计算上
/// 表现稳定，且零依赖、可直接单文件发布；同时避免 JiebaNet 引入的 Newtonsoft.Json 11
/// 高危漏洞（GHSA-5crp-9r3c-p9vr）。
/// </para>
/// <para>若 M3 的漂移检测误判率超标，替换本实现即可（对外只暴露 ITokenizer）。</para>
/// </summary>
public sealed class BigramTokenizer : ITokenizer
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "我们", "你们", "他们", "她们", "这个", "那个", "这些", "那些", "就是", "然后",
        "什么", "怎么", "因为", "所以", "但是", "如果", "现在", "可以", "没有", "一个",
        "不是", "这样", "那样", "还是", "已经", "自己", "大家", "时候", "感觉", "觉得",
    };

    public IReadOnlyList<string> Tokenize(string text) => Tokenize(text, includeStopwords: false);

    public IReadOnlyList<string> Tokenize(string text, bool includeStopwords)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cjkRun = new StringBuilder();
        var asciiRun = new StringBuilder();

        void FlushCjk()
        {
            if (cjkRun.Length == 1)
            {
                var single = cjkRun.ToString();
                if ((includeStopwords || !IsStopword(single)) && seen.Add(single))
                {
                    result.Add(single);
                }
            }
            else if (cjkRun.Length >= 2)
            {
                for (var i = 0; i + 1 < cjkRun.Length; i++)
                {
                    var bigram = cjkRun.ToString(i, 2);
                    if ((includeStopwords || !IsStopword(bigram)) && seen.Add(bigram))
                    {
                        result.Add(bigram);
                    }
                }
            }

            cjkRun.Clear();
        }

        void FlushAscii()
        {
            if (asciiRun.Length >= 2)
            {
                var word = asciiRun.ToString().ToLowerInvariant();
                if ((includeStopwords || !IsStopword(word)) && seen.Add(word))
                {
                    result.Add(word);
                }
            }

            asciiRun.Clear();
        }

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                FlushAscii();
                cjkRun.Append(ch);
            }
            else if (char.IsLetterOrDigit(ch) && ch < 0x2E80)
            {
                FlushCjk();
                asciiRun.Append(ch);
            }
            else
            {
                FlushCjk();
                FlushAscii();
            }
        }

        FlushCjk();
        FlushAscii();
        return result;
    }

    /// <summary>会话关键词集 K_s 用：只要实词，按出现顺序。</summary>
    public IReadOnlyList<string> ExtractKeywords(string text, int max = 16)
    {
        var tokens = Tokenize(text);
        if (tokens.Count <= max)
        {
            return tokens;
        }

        return tokens.Take(max).ToArray();
    }

    public static bool IsCjk(char ch) =>
        ch is >= '\u4E00' and <= '\u9FFF'
            or >= '\u3400' and <= '\u4DBF'
            or >= '\uF900' and <= '\uFAFF';

    private static bool IsStopword(string token) => Stopwords.Contains(token);
}
