using Bot.Core.Config;
using Bot.Core.Domain;
using Bot.Core.Memory;
using Bot.Core.Text;

namespace Bot.Core.Runtime;

/// <summary>框架里的一条消息。</summary>
public sealed record PromptFrameworkMessage(string Role, string Content, int Tokens);

/// <summary>提示词框架：只展示结构和当前参数，不读取真实记忆/会话。</summary>
public sealed record PromptFramework(
    string Model,
    int ReplyLength,
    IReadOnlyList<PromptFrameworkMessage> ColdStart,
    IReadOnlyList<PromptFrameworkMessage> Summary,
    string SummaryTemplate);

/// <summary>
/// 调参页的"提示词框架"生成器。
/// <para>
/// 不查库、不建会话、不需要聊天选择：用占位符渲染出 system/user/总结请求的骨架，
/// 让用户只看结构、规则、人设和当前字数上限。真实内容去「当前会话」看。
/// </para>
/// </summary>
public sealed class PromptFrameworkService
{
    private readonly ConfigService _config;
    private readonly PromptAssembler _assembler;
    private readonly TokenEstimator _estimator;

    public PromptFrameworkService(ConfigService config, PromptAssembler assembler, TokenEstimator estimator)
    {
        _config = config;
        _assembler = assembler;
        _estimator = estimator;
    }

    public PromptFramework Build()
    {
        var cfg = _config.ForChat(string.Empty);
        var persona = _config.Raw.Persona;

        // 用一条占位人物/事件让 BuildPrefix 渲染出真实的表头、分隔符和规则文本。
        var person = new PersonRow(
            1,
            "preview-open-id",
            null,
            "<名字>",
            "none",
            "P001",
            "<性格>",
            "<评价>",
            "<记忆>",
            3,
            false,
            "active",
            string.Empty,
            1,
            null,
            0,
            0);

        var item = new EventRow(
            1,
            "<时间>",
            null,
            string.Empty,
            string.Empty,
            "<详情>",
            3,
            false,
            "active",
            null,
            0,
            "preview",
            0,
            null,
            new[] { 1 },
            Array.Empty<string>());

        var prefix = _assembler.BuildPrefix(
            "preview",
            cfg,
            persona,
            ChatKind.Group,
            new[] { person },
            new[] { item },
            "<上轮摘要>");

        var tail = $"""
            【当前】<时间>｜回复：最多 {cfg.ReplyLength} 字
            【背景】<自上次回复以来的群聊，按需出现>
            【补充记忆】<这轮提到、但前缀里没有的人/事，按需出现>
            【消息】
            <群友A>：<本轮消息>
            """;

        var template = SummaryParser.Template;

        var coldStart = new[]
        {
            Message("system", prefix.Text),
            Message("user", tail),
        };

        var summary = new[]
        {
            Message("system", prefix.Text),
            Message("user", "<历史：用户消息>"),
            Message("assistant", "<历史：机器人回复>"),
            Message("user", template),
        };

        return new PromptFramework(cfg.Model, cfg.ReplyLength, coldStart, summary, template);
    }

    private PromptFrameworkMessage Message(string role, string content) =>
        new(role, content, _estimator.EstimateMessages(new[] { content }));
}
