using System.Text.RegularExpressions;
using Bot.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bot.Core.Memory;

public enum SummaryOpKind
{
    NewPerson,
    UpdatePerson,
    NewEvent,
    Name,
    Pin,
}

public sealed record SummaryOp(SummaryOpKind Kind, IReadOnlyList<string> Fields, string RawLine);

public sealed class SummaryParseResult
{
    public List<SummaryOp> Ops { get; } = new();

    public List<string> Warnings { get; } = new();

    public int TotalLines { get; set; }

    public int RejectedLines { get; set; }

    public bool IsNone { get; set; }

    public bool IsEmpty => TotalLines == 0;

    public bool HasOps => Ops.Count > 0;
}

/// <summary>
/// 总结协议（文档 4.4）。用 ASCII 键名 + <c>|</c> 分隔 + 固定字段顺序，
/// 解析器就是一台状态机，不需要猜同义写法。
/// </summary>
public static class SummaryParser
{
    public const string Template =
        """
        【任务】把本次会话的增量写进记忆库。只输出下列行，每行一条，
        不要编号、不要解释、不要代码块、不要额外空行。
        没有可写内容就只输出一行：NONE

        NEW_PERSON|<名字或群友NN>|<性格≤40字>|<评价≤40字>|<记忆≤100字>
        UPDATE_PERSON|<名字或群友NN>|<新版完整记忆≤100字>
        NEW_EVENT|<人物,逗号分隔>|<时间>|<地点>|<关键词,逗号分隔>|<详情≤80字>
        NAME|<群友NN>|<此人自称或被称呼的名字>|<依据原文≤20字>
        PIN|<PERSON或EVENT>|<名字或event_id>|<原因≤20字>
        """;

    private static readonly (string Key, SummaryOpKind Kind, int[] Counts)[] Keys =
    {
        ("NEW_PERSON", SummaryOpKind.NewPerson, new[] { 5 }),
        // 模型有时把 UPDATE_PERSON 照抄成 NEW_PERSON 的 5 段式，两种都收。
        ("UPDATE_PERSON", SummaryOpKind.UpdatePerson, new[] { 3, 5 }),
        ("NEW_EVENT", SummaryOpKind.NewEvent, new[] { 6 }),
        ("NAME", SummaryOpKind.Name, new[] { 4 }),
        ("PIN", SummaryOpKind.Pin, new[] { 4 }),
    };

    public static SummaryParseResult Parse(string? raw)
    {
        var result = new SummaryParseResult();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        var text = raw.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        text = Regex.Replace(text, "^```[a-zA-Z]*\\s*", string.Empty);
        text = Regex.Replace(text, "```\\s*$", string.Empty);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            result.TotalLines++;

            if (string.Equals(line, "NONE", StringComparison.OrdinalIgnoreCase))
            {
                result.IsNone = true;
                continue;
            }

            var fields = line.Split('|').Select(f => f.Trim()).ToArray();
            var key = fields[0].ToUpperInvariant();
            var matched = Keys.FirstOrDefault(k => k.Key == key);

            if (matched.Key is null)
            {
                result.RejectedLines++;
                result.Warnings.Add("未知行首关键字：" + Truncate(line, 30));
                continue;
            }

            if (!matched.Counts.Contains(fields.Length))
            {
                result.RejectedLines++;
                result.Warnings.Add($"{key} 字段数应为 {string.Join('/', matched.Counts)}，实际 {fields.Length}");
                continue;
            }

            result.Ops.Add(new SummaryOp(matched.Kind, fields, line));
        }

        return result;
    }

    private static string Truncate(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";
}

public sealed record SummaryApplyResult(
    string BatchId,
    int Applied,
    int Skipped,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> AppliedDescriptions);

/// <summary>
/// 总结入库（文档 3.4.5）。程序负责编号、截断、实体消解、去重、审计；
/// 模型不产出 ID、不产出时间戳、不产出 open_id。
/// <para>每条操作独立事务：单条失败不拖垮整批（"部分成功优于全丢"），
/// 整批靠 batch_id 记账，仍可一键回滚。</para>
/// </summary>
public sealed class SummaryApplier
{
    private const int PersonalityLimit = 40;
    private const int EvaluationLimit = 40;

    private readonly Db _db;
    private readonly IdentityService _identity;
    private readonly EventStore _events;
    private readonly MemoryOpLog _opLog;
    private readonly IClock _clock;
    private readonly ILogger<SummaryApplier> _log;

    public SummaryApplier(Db db, IdentityService identity, EventStore events, MemoryOpLog opLog, IClock clock,
        ILogger<SummaryApplier>? log = null)
    {
        _db = db;
        _identity = identity;
        _events = events;
        _opLog = opLog;
        _clock = clock;
        _log = log ?? NullLogger<SummaryApplier>.Instance;
    }

    public SummaryApplyResult Apply(
        string chatId,
        string sessionId,
        SummaryParseResult parsed,
        EffectiveConfig cfg,
        IReadOnlyDictionary<string, string> labelToOpenId)
    {
        var rawBatchId = $"sum:{sessionId}:{DateTime.UtcNow:yyyyMMddHHmmss}:{Guid.NewGuid():N}";
        var batchId = rawBatchId.Length <= 64 ? rawBatchId : rawBatchId[..64];
        var warnings = new List<string>(parsed.Warnings);
        var applied = 0;
        var skipped = 0;
        var descriptions = new List<string>();

        // 同批多条 UPDATE_PERSON 指向同一人 → 取最长的一条，其余记 warning。
        var updateCandidates = parsed.Ops
            .Where(op => op.Kind == SummaryOpKind.UpdatePerson)
            .GroupBy(op => op.Fields[1])
            .ToDictionary(g => g.Key, g => g.OrderByDescending(op => op.Fields[2].Length).First());

        foreach (var op in parsed.Ops.Take(cfg.MaxOpsPerBatch))
        {
            try
            {
                var ok = op.Kind switch
                {
                    SummaryOpKind.NewPerson => ApplyNewPerson(op, chatId, cfg, labelToOpenId, batchId, warnings),
                    SummaryOpKind.UpdatePerson => ApplyUpdatePerson(op, chatId, cfg, labelToOpenId, batchId, warnings, updateCandidates),
                    SummaryOpKind.NewEvent => ApplyNewEvent(op, chatId, cfg, labelToOpenId, batchId, warnings),
                    SummaryOpKind.Name => ApplyName(op, chatId, labelToOpenId, batchId, warnings),
                    SummaryOpKind.Pin => ApplyPin(op, chatId, labelToOpenId, batchId, warnings),
                    _ => false,
                };

                if (ok)
                {
                    applied++;
                    descriptions.Add(op.RawLine.Length > 60 ? op.RawLine[..60] + "…" : op.RawLine);
                }
                else
                {
                    skipped++;
                }
            }
            catch (Exception ex)
            {
                skipped++;
                warnings.Add($"{op.Kind} 写入失败：{ex.Message}");
                _log.LogError(ex, "记忆写入失败：{Line}", op.RawLine);
            }
        }

        var duplicateUpdates = parsed.Ops
            .Where(op => op.Kind == SummaryOpKind.UpdatePerson)
            .GroupBy(op => op.Fields[1])
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        foreach (var duplicate in duplicateUpdates)
        {
            warnings.Add($"同一人物多条 UPDATE_PERSON，只取最长一条：{duplicate}");
        }

        return new SummaryApplyResult(batchId, applied, skipped, warnings, descriptions);
    }

    private bool ApplyNewPerson(SummaryOp op, string chatId, EffectiveConfig cfg,
        IReadOnlyDictionary<string, string> labelToOpenId, string batchId, List<string> warnings)
    {
        var name = op.Fields[1];
        var personality = op.Fields[2];
        var evaluation = op.Fields[3];
        var memory = op.Fields[4];

        if (MemoryGuard.Inspect(evaluation, GuardKind.Evaluation) is { Ok: false } evaluationGuard)
        {
            warnings.Add($"NEW_PERSON 评价被拒：{evaluationGuard.Reason}");
            evaluation = string.Empty;
        }

        var existing = _identity.ResolveByLabel(chatId, name);
        if (existing is not null)
        {
            // 已有档案：降级为"更新记忆"，避免重复建档。
            return UpdateProfile(existing.PersonId, personality, evaluation, memory, cfg, batchId, warnings);
        }

        if (!labelToOpenId.TryGetValue(name, out var openId))
        {
            // 模型凭空造人：本会话里没有这个发言人，丢弃并计数（文档 3.4.5）。
            warnings.Add($"NEW_PERSON 无法消解（本会话无此发言人）：{name}");
            return false;
        }

        if (MemoryGuard.Inspect(name, GuardKind.Name) is { Ok: false } nameGuard)
        {
            warnings.Add($"NEW_PERSON 名字被拒：{nameGuard.Reason}");
            return false;
        }

        var person = _identity.EnsurePerson(openId, null, name);
        return UpdateProfile(person.PersonId, personality, evaluation, memory, cfg, batchId, warnings);
    }

    private bool ApplyUpdatePerson(SummaryOp op, string chatId, EffectiveConfig cfg,
        IReadOnlyDictionary<string, string> labelToOpenId, string batchId, List<string> warnings,
        Dictionary<string, SummaryOp> candidates)
    {
        var name = op.Fields[1];
        if (candidates.TryGetValue(name, out var chosen) && !ReferenceEquals(chosen, op))
        {
            return false; // 只处理最长的那条
        }

        var person = _identity.ResolveByLabel(chatId, name);
        if (person is null && labelToOpenId.TryGetValue(name, out var openId))
        {
            person = _identity.EnsurePerson(openId, null, name);
        }

        if (person is null)
        {
            warnings.Add($"UPDATE_PERSON 未知别名：{name}");
            return false;
        }

        // 3 段式：名字 + 新版记忆；5 段式：名字 + 性格 + 评价 + 记忆。
        return op.Fields.Count >= 5
            ? UpdateProfile(person.PersonId, op.Fields[2], op.Fields[3], op.Fields[4], cfg, batchId, warnings)
            : UpdateProfile(person.PersonId, null, null, op.Fields[2], cfg, batchId, warnings);
    }

    private bool ApplyNewEvent(SummaryOp op, string chatId, EffectiveConfig cfg,
        IReadOnlyDictionary<string, string> labelToOpenId, string batchId, List<string> warnings)
    {
        var personsField = op.Fields[1];
        var timeText = op.Fields[2];

        // 模型会把"群名"写成它看到的东西；QQ 不下发群名时它看到的就是 chat_id。
        // 这种不透明串绝不能进记忆块（那是每轮都要发的前缀）。
        var locationLabel = string.IsNullOrWhiteSpace(op.Fields[3])
                            || Masking.LooksOpaque(op.Fields[3])
                            || string.Equals(op.Fields[3], chatId, StringComparison.Ordinal)
            ? "本群"
            : MemoryGuard.TruncateAtSentence(op.Fields[3], 20);

        var keywords = SplitList(op.Fields[4]);
        var detail = op.Fields[5];

        if (MemoryGuard.Inspect(detail, GuardKind.Entry) is { Ok: false } guard)
        {
            warnings.Add($"NEW_EVENT 被拒：{guard.Reason}");
            return false;
        }

        var personIds = new List<int>();
        foreach (var label in SplitList(personsField))
        {
            var person = _identity.ResolveByLabel(chatId, label);
            if (person is null && labelToOpenId.TryGetValue(label, out var openId))
            {
                person = _identity.EnsurePerson(openId, null, label);
            }

            if (person is null)
            {
                warnings.Add($"NEW_EVENT 人物无法消解：{label}");
                continue;
            }

            personIds.Add(person.PersonId);
        }

        var draft = new EventDraft(
            MemoryGuard.TruncateAtSentence(timeText, 40),
            TimeNorm: null,
            Location: chatId,                                   // 作用域键永远是 chat_id
            MemoryGuard.TruncateAtSentence(detail, cfg.EventDetailLimit),
            keywords,
            personIds,
            LocationLabel: locationLabel);                      // 展示名来自模型

        _events.Add(draft, batchId, "summary");
        return true;
    }

    private bool ApplyName(SummaryOp op, string chatId, IReadOnlyDictionary<string, string> labelToOpenId,
        string batchId, List<string> warnings)
    {
        var label = op.Fields[1];
        var name = op.Fields[2];
        var evidence = op.Fields[3];

        if (MemoryGuard.Inspect(name, GuardKind.Name) is { Ok: false } guard)
        {
            warnings.Add($"NAME 被拒（{name}）：{guard.Reason}");
            return false;
        }

        var person = _identity.ResolveByLabel(chatId, label);
        if (person is null && labelToOpenId.TryGetValue(label, out var openId))
        {
            person = _identity.EnsurePerson(openId, null, label);
        }

        if (person is null)
        {
            warnings.Add($"NAME 未知发言人：{label}");
            return false;
        }

        // 依据原文里出现"自称/自述/我叫" → 高可信，直接写；否则按他人称呼累计。
        var selfClaim = evidence.Contains("自称", StringComparison.Ordinal)
            || evidence.Contains("自述", StringComparison.Ordinal)
            || evidence.Contains("我叫", StringComparison.Ordinal)
            || evidence.Contains("叫我", StringComparison.Ordinal);

        if (selfClaim)
        {
            var result = _identity.TryApplyName(person.PersonId, name, "self");
            if (!result.Ok)
            {
                warnings.Add($"NAME 被拒（{name}）：{result.Message}");
            }

            return result.Ok;
        }

        return _identity.RecordNameEvidence(person.OpenId, chatId, name);
    }

    private bool ApplyPin(SummaryOp op, string chatId, IReadOnlyDictionary<string, string> labelToOpenId,
        string batchId, List<string> warnings)
    {
        var targetType = op.Fields[1].ToUpperInvariant();
        var target = op.Fields[2];

        if (targetType.StartsWith("EVENT", StringComparison.Ordinal))
        {
            // 模型有时写 "E003"、有时写 "3"，两种都接受。
            if (!Labels.TryParseEventId(target, out var eventId))
            {
                warnings.Add("PIN 的 event_id 解析失败：" + target);
                return false;
            }

            var before = _events.GetById(eventId);
            if (before is null)
            {
                warnings.Add("PIN 找不到事件：" + target);
                return false;
            }

            _db.Exec("UPDATE event SET pinned=1 WHERE event_id=@id", ("@id", eventId));
            _opLog.Append(batchId, "update", "event", eventId, MemoryOpLog.EventSnapshot(before), null, "summary");
            return true;
        }

        var person = _identity.ResolveByLabel(chatId, target);
        if (person is null && labelToOpenId.TryGetValue(target, out var openId))
        {
            person = _identity.EnsurePerson(openId, null, target);
        }

        if (person is null)
        {
            warnings.Add("PIN 找不到人物：" + target);
            return false;
        }

        var row = _identity.GetPerson(person.PersonId);
        _db.Exec("UPDATE person SET pinned=1 WHERE person_id=@id", ("@id", person.PersonId));
        _opLog.Append(batchId, "update", "person", person.PersonId,
            row is null ? null : MemoryOpLog.PersonSnapshot(row), null, "summary");
        return true;
    }

    private bool UpdateProfile(int personId, string? personality, string? evaluation, string? memory,
        EffectiveConfig cfg, string batchId, List<string> warnings)
    {
        var row = _identity.GetPerson(personId);
        if (row is null)
        {
            return false;
        }

        var newPersonality = personality;
        if (!string.IsNullOrWhiteSpace(personality))
        {
            if (MemoryGuard.Inspect(personality, GuardKind.Evaluation) is { Ok: false } guard)
            {
                warnings.Add($"性格被拒：{guard.Reason}");
                newPersonality = null;
            }
            else
            {
                newPersonality = MemoryGuard.TruncateAtSentence(personality, PersonalityLimit);
            }
        }

        var newEvaluation = evaluation;
        if (!string.IsNullOrWhiteSpace(evaluation))
        {
            if (MemoryGuard.Inspect(evaluation, GuardKind.Evaluation) is { Ok: false } guard)
            {
                warnings.Add($"评价被拒：{guard.Reason}");
                newEvaluation = null;
            }
            else
            {
                newEvaluation = MemoryGuard.TruncateAtSentence(evaluation, EvaluationLimit);
            }
        }

        var newMemory = memory is null ? null : MemoryGuard.TruncateAtSentence(memory, cfg.PersonMemoryLimit);

        if (newMemory is not null && newMemory.Length > 0 && MemoryGuard.Inspect(newMemory, GuardKind.Entry) is { Ok: false } memoryGuard)
        {
            warnings.Add($"人物记忆被拒：{memoryGuard.Reason}");
            newMemory = null;
        }

        var finalPersonality = newPersonality ?? row.Personality;
        var finalEvaluation = newEvaluation ?? row.Evaluation;
        var finalMemory = newMemory ?? row.MemoryText;

        if (finalPersonality == row.Personality && finalEvaluation == row.Evaluation && finalMemory == row.MemoryText)
        {
            return false;
        }

        var before = MemoryOpLog.PersonSnapshot(row);
        _db.Exec(
            """
            UPDATE person SET personality=@p, evaluation=@e, memory_text=@m, updated_at=@t
            WHERE person_id=@id
            """,
            ("@p", finalPersonality),
            ("@e", finalEvaluation),
            ("@m", finalMemory),
            ("@t", TimeUtil.Unix(_clock.UtcNow)),
            ("@id", personId));

        _opLog.Append(batchId, "update", "person", personId, before, null, "summary");
        return true;
    }

    private static IReadOnlyList<string> SplitList(string value) =>
        value.Split(new[] { ',', '，', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToArray();
}
