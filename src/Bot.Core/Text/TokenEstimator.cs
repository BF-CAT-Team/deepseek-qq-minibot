namespace Bot.Core.Text;

/// <summary>
/// token 估算。官方没有公开 tokenizer，这里用字符启发式 + 用真实 usage 回填校准。
/// <para>基线：1 个中文字符 ≈ 0.6 token，1 个其它字符 ≈ 0.3 token，每条消息另有 ~4 token 的封装开销。</para>
/// <para>校准来自 request_log：每次拿到 API 返回的 prompt_tokens 就喂回来，EWMA 收敛后阈值就可信。</para>
/// </summary>
public sealed class TokenEstimator
{
    private const double CjkCharCost = 0.6;
    private const double OtherCharCost = 0.3;
    private const double DefaultMessageOverhead = 4.0;

    private readonly object _gate = new();
    private double _calibration = 1.0;
    private int _samples;

    public TokenEstimator()
    {
    }

    public TokenEstimator(double calibration)
    {
        _calibration = calibration is > 0.2 and < 5 ? calibration : 1.0;
    }

    public double Calibration
    {
        get
        {
            lock (_gate)
            {
                return _calibration;
            }
        }
    }

    public int Samples
    {
        get
        {
            lock (_gate)
            {
                return _samples;
            }
        }
    }

    public int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var cjk = 0;
        var other = 0;
        foreach (var ch in text)
        {
            if (BigramTokenizer.IsCjk(ch))
            {
                cjk++;
            }
            else
            {
                other++;
            }
        }

        var raw = (cjk * CjkCharCost) + (other * OtherCharCost);
        return Math.Max(1, (int)Math.Ceiling(raw * Calibration));
    }

    /// <summary>一组消息的估算（含每条消息的封装开销）。</summary>
    public int EstimateMessages(IEnumerable<string?> contents)
    {
        var total = 0.0;
        var any = false;
        foreach (var content in contents)
        {
            any = true;
            total += Estimate(content) + DefaultMessageOverhead;
        }

        return any ? (int)Math.Ceiling(total) : 0;
    }

    /// <summary>用真实 usage 反向校准。estimated 为本轮自估 prompt_tokens，actual 为 API 返回。</summary>
    public void Observe(int estimated, int actual)
    {
        if (estimated <= 0 || actual <= 0)
        {
            return;
        }

        var ratio = (double)actual / estimated;
        if (ratio is < 0.3 or > 3.0)
        {
            // 明显异常（例如换了模型/不同模板）时不污染校准值。
            return;
        }

        lock (_gate)
        {
            _samples++;
            var weight = _samples < 10 ? 0.35 : 0.08;
            _calibration = (_calibration * (1 - weight)) + (ratio * weight);
            _calibration = Math.Clamp(_calibration, 0.4, 3.0);
        }
    }
}
