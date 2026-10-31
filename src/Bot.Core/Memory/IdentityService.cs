using System.Text;
using Microsoft.Data.Sqlite;
using Bot.Core.Config;
using Bot.Core.Domain;

namespace Bot.Core.Memory;

/// <summary>解析后的发言人。open_id 永不进 prompt，只在这里与数据库之间流动。</summary>
public sealed record PersonRef(
    int PersonId,
    string OpenId,
    string DisplayLabel,
    string Name,
    string NameSource,
    bool Materialized,
    string? TempLabel,
    bool IsBot);

internal static class PersonSql
{
    public const string Columns =
        "p.person_id, p.open_id, p.union_open_id, p.name, p.name_source, p.display_label, p.personality, p.evaluation, " +
        "p.memory_text, p.importance, p.pinned, p.status, p.chat_scope, p.msg_count, p.first_seen_at, p.created_at, p.updated_at";

    public static PersonRow Map(SqliteDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.GetInt32(9),
        reader.GetInt64(10) == 1,
        reader.GetString(11),
        reader.GetString(12),
        reader.GetInt32(13),
        reader.IsDBNull(14) ? null : reader.GetInt64(14),
        reader.GetInt64(15),
        reader.GetInt64(16));
}

/// <summary>
/// 身份映射：open_id → person_id → display_label（文档 3.4.8）。
/// <para>三层标识各归其位：open_id 只做数据库主键与发送目标；person_id 是记忆块里的短编号；
/// display_label 出现在历史行里。标签在会话开始时算好并冻结，只在轮换时重算。</para>
/// </summary>
public sealed class IdentityService
{
    private readonly Db _db;
    private readonly ConfigService _config;
    private readonly IClock _clock;
    private readonly MemoryOpLog _opLog;
    private readonly object _gate = new();
    private readonly Dictionary<(string ChatId, string OpenId), PersonRef> _cache = new();
    private readonly Dictionary<(string ChatId, string OpenId), int> _observations = new();
    private readonly Dictionary<(string ChatId, string OpenId), string> _tempLabels = new();
    private readonly Dictionary<string, int> _tempSeq = new(StringComparer.Ordinal);

    public IdentityService(Db db, ConfigService config, IClock clock, MemoryOpLog opLog)
    {
        _db = db;
        _config = config;
        _clock = clock;
        _opLog = opLog;
    }

    public PersonRef Resolve(IncomingMessage message, string botLabel)
    {
        var cfg = _config.ForChat(message.ChatId);
        if (message.IsSelf || (!string.IsNullOrWhiteSpace(cfg.SelfOpenId) && message.OpenId == cfg.SelfOpenId))
        {
            return new PersonRef(0, message.OpenId, botLabel, botLabel, "self", true, null, true);
        }

        lock (_gate)
        {
            var key = (message.ChatId, message.OpenId);
            if (_cache.TryGetValue(key, out var cached))
            {
                if (cached.Materialized)
                {
                    BumpMsgCount(cached.PersonId);
                }

                return cached;
            }

            var row = FindPerson(message.OpenId, message.UnionOpenId);
            if (row is not null)
            {
                row = EnsureUnion(row, message.UnionOpenId);
                EnsureOpenIdAlias(row.PersonId, message.OpenId);
                BumpMsgCount(row.PersonId);

                var ref0 = new PersonRef(
                    row.PersonId,
                    message.OpenId,
                    ResolveLabel(row, message.ChatId),
                    row.Name,
                    row.NameSource,
                    true,
                    null,
                    false);
                _cache[key] = ref0;
                return ref0;
            }

            var observed = _observations.TryGetValue(key, out var count) ? count + 1 : 1;
            _observations[key] = observed;

            var shouldMaterialize = observed >= cfg.PersonMaterializeMinMsgs
                || message.MentionsBot
                || !string.IsNullOrWhiteSpace(message.ReplyToMessageId);

            if (shouldMaterialize)
            {
                var created = Materialize(message, observed);
                _observations.Remove(key);
                _cache[key] = created;
                return created;
            }

            var temp = TempLabel(key);
            return new PersonRef(0, message.OpenId, temp, string.Empty, "none", false, temp, false);
        }
    }

    /// <summary>总结入库时用：已存在则返回，不存在则按 open_id 建档（绝不会凭空造人——open_id 必须来自会话）。</summary>
    public PersonRef EnsurePerson(string openId, string? unionOpenId, string name)
    {
        lock (_gate)
        {
            var row = FindPerson(openId, unionOpenId);
            if (row is not null)
            {
                return new PersonRef(row.PersonId, openId, ResolveLabel(row, string.Empty), row.Name, row.NameSource, true, null, false);
            }

            var now = TimeUtil.Unix(_clock.UtcNow);
            var personId = (int)_db.ScalarLong(
                """
                INSERT INTO person(open_id, union_open_id, name, name_source, display_label, importance, status,
                                   msg_count, first_seen_at, created_at, updated_at)
                VALUES(@open, @union, '', 'none', '', 3, 'active', 1, @now, @now, @now)
                RETURNING person_id
                """,
                ("@open", openId),
                ("@union", unionOpenId),
                ("@now", now));

            var label = ComputeLabel(personId, string.Empty);
            _db.Exec("UPDATE person SET display_label=@l WHERE person_id=@id", ("@l", label), ("@id", personId));
            EnsureOpenIdAlias(personId, openId);
            _opLog.Append($"materialize:{personId}", "insert", "person", personId, null,
                new { PersonId = personId, OpenId = openId, DisplayLabel = label }, "summary");

            var appliedName = string.Empty;
            if (!string.IsNullOrWhiteSpace(name))
            {
                var applied = TryApplyName(personId, name, "self");
                if (applied.Ok)
                {
                    appliedName = applied.Name;
                    label = ComputeLabel(personId, appliedName);
                }
            }

            return new PersonRef(personId, openId, label, appliedName, appliedName.Length > 0 ? "self" : "none", true, null, false);
        }
    }

    /// <summary>把总结里的"阿杰 / 群友07 / 别名"解析成 person。以 open_id 为准，名字只是线索。</summary>
    public PersonRef? ResolveByLabel(string chatId, string label)
    {
        var normalized = NormalizeLabel(label);
        if (normalized.Length == 0)
        {
            return null;
        }

        var row = _db.QueryOne(
            $"""
            SELECT {PersonSql.Columns} FROM person p
            WHERE p.status='active'
              AND (p.display_label=@l OR p.name=@l
                   OR EXISTS (SELECT 1 FROM person_label pl
                              WHERE pl.open_id=p.open_id AND pl.chat_id=@c AND pl.label=@l))
            ORDER BY p.msg_count DESC, p.person_id ASC
            LIMIT 1
            """,
            PersonSql.Map,
            ("@l", normalized),
            ("@c", chatId));

        row ??= _db.QueryOne(
            $"""
            SELECT {PersonSql.Columns} FROM person p
            JOIN person_alias a ON a.person_id = p.person_id
            WHERE p.status='active' AND a.alias=@l
            ORDER BY p.msg_count DESC, p.person_id ASC
            LIMIT 1
            """,
            PersonSql.Map,
            ("@l", normalized));

        return row is null
            ? null
            : new PersonRef(row.PersonId, row.OpenId, ResolveLabel(row, chatId), row.Name, row.NameSource, true, null, false);
    }

    public PersonRow? GetPerson(int personId) =>
        _db.QueryOne($"SELECT {PersonSql.Columns} FROM person p WHERE p.person_id=@id", PersonSql.Map, ("@id", personId));

    public PersonRow? GetPersonByOpenId(string openId) =>
        _db.QueryOne($"SELECT {PersonSql.Columns} FROM person p WHERE p.open_id=@o", PersonSql.Map, ("@o", openId));

    public IReadOnlyList<PersonRow> ListActive(int limit = 200) =>
        _db.Query($"SELECT {PersonSql.Columns} FROM person p WHERE p.status='active' ORDER BY p.person_id LIMIT @n",
            PersonSql.Map, ("@n", limit));

    /// <summary>人物记忆在前缀里按 person_id 升序拼接（文档 1.3 铁律 4）。</summary>
    public IReadOnlyList<PersonRow> ListForPrefix(string chatId, int limit = 60) =>
        _db.Query(
            $"""
            SELECT {PersonSql.Columns} FROM person p
            WHERE p.status='active' AND (p.chat_scope='' OR p.chat_scope=@c)
            ORDER BY p.person_id ASC LIMIT @n
            """,
            PersonSql.Map,
            ("@c", chatId),
            ("@n", limit));

    /// <summary>
    /// 只返回这些 open_id 对应的人物画像。
    /// <para>
    /// 冷启动时用：前缀里只放"这次触发时真的在聊天记录里出现的人"，
    /// 而不是把群里所有历史画像都塞进去——那些和当前对话无关，白占 token 还会干扰模型。
    /// </para>
    /// </summary>
    public IReadOnlyList<PersonRow> ListForOpenIds(string chatId, IEnumerable<string> openIds)
    {
        var ids = new HashSet<int>();
        foreach (var openId in openIds)
        {
            if (string.IsNullOrWhiteSpace(openId))
            {
                continue;
            }

            var row = FindPerson(openId, null);
            if (row is not null)
            {
                ids.Add(row.PersonId);
            }
        }

        if (ids.Count == 0)
        {
            return Array.Empty<PersonRow>();
        }

        var result = new List<PersonRow>(ids.Count);
        foreach (var id in ids)
        {
            var row = _db.QueryOne(
                $"""
                 SELECT {PersonSql.Columns} FROM person p
                 WHERE p.person_id=@id AND p.status='active' AND (p.chat_scope='' OR p.chat_scope=@c)
                 """,
                PersonSql.Map,
                ("@id", id),
                ("@c", chatId));

            if (row is not null)
            {
                result.Add(row);
            }
        }

        return result.OrderBy(person => person.PersonId).ToArray();
    }

    /// <summary>
    /// 在聊天内容里找"被点到名"的已建档人物。
    /// <para>
    /// 匹配名字、群内标签（群友NN）、以及学到的别名；单字名字不参与匹配，避免"好""在"这种字误命中。
    /// </para>
    /// </summary>
    public IReadOnlyList<PersonRow> ListMentioned(string chatId, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<PersonRow>();
        }

        return _db.Query(
            $"""
             SELECT {PersonSql.Columns} FROM person p
             WHERE p.status='active' AND (p.chat_scope='' OR p.chat_scope=@c)
               AND (
                 (length(trim(p.name)) >= 2 AND instr(lower(@t), lower(trim(p.name))) > 0)
                 OR (length(trim(p.display_label)) >= 2 AND instr(lower(@t), lower(trim(p.display_label))) > 0)
                 OR EXISTS (
                   SELECT 1 FROM person_alias a
                   WHERE a.person_id = p.person_id
                     AND length(trim(a.alias)) >= 2
                     AND instr(lower(@t), lower(trim(a.alias))) > 0
                 )
               )
             ORDER BY p.person_id
             """,
            PersonSql.Map,
            ("@c", chatId),
            ("@t", text));
    }

    /// <summary>
    /// 这一轮真正相关的人：发言者 + 内容里点名的人。冷启动据此挑前缀，会话中途据此做按需补充。
    /// </summary>
    public IReadOnlyList<PersonRow> ListInvolved(string chatId, IEnumerable<string> speakerOpenIds, string text)
    {
        var byId = new Dictionary<int, PersonRow>();

        foreach (var person in ListForOpenIds(chatId, speakerOpenIds))
        {
            byId[person.PersonId] = person;
        }

        foreach (var person in ListMentioned(chatId, text))
        {
            byId[person.PersonId] = person;
        }

        return byId.Values.OrderBy(person => person.PersonId).ToArray();
    }

    /// <summary>
    /// 改名（所有入口共用）。返回详细结果，指令系统需要把失败原因回给用户。
    /// 规则：空/占位名/重名/机器人同名/安全检查不过 → 不写。
    /// </summary>
    public NameChangeResult TryApplyName(int personId, string rawName, string nameSource)
    {
        var row = GetPerson(personId);
        if (row is null)
        {
            return new NameChangeResult(false, false, NameRules.Clean(rawName), "找不到这个人的档案", null);
        }

        var check = NameRules.Check(rawName, _config.Raw.Persona.BotName);
        if (!check.Ok)
        {
            return new NameChangeResult(false, false, check.Name, check.Reason ?? "这个名字不合适", row);
        }

        var name = check.Name;
        if (string.Equals(row.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
        {
            return new NameChangeResult(true, false, name, "名字已经是这个了", row);
        }

        if (row.NameSource == "manual" && nameSource != "manual")
        {
            return new NameChangeResult(false, false, name, "这个名字是人工设置的，自动流程不能改", row);
        }

        var owner = FindNameOwner(name, personId);
        if (owner is not null)
        {
            return new NameChangeResult(false, false, name,
                $"「{name}」已经被 {Labels.Person(owner.PersonId)} 用了", row);
        }

        var guard = MemoryGuard.Inspect(name, GuardKind.Name);
        if (!guard.Ok)
        {
            return new NameChangeResult(false, false, name, guard.Reason ?? "名字没通过安全检查", row);
        }

        var oldNames = CollectOldNames(personId, row, name);
        var label = ComputeLabel(personId, name);
        var before = MemoryOpLog.PersonSnapshot(row);
        var now = TimeUtil.Unix(_clock.UtcNow);

        _db.Exec(
            "UPDATE person SET name=@n, name_source=@s, display_label=@l, updated_at=@t WHERE person_id=@id",
            ("@n", name),
            ("@s", nameSource),
            ("@l", label),
            ("@t", now),
            ("@id", personId));

        _opLog.Append($"name:{personId}", "update", "person", personId, before, null,
            nameSource == "manual" ? "manual" : "summary");

        // 名字变了，历史事件/人物记忆里还在用旧称呼（群友30、旧名字）的地方一起换掉。
        ReplaceNameReferences(personId, oldNames, name);

        return new NameChangeResult(true, true, name, $"名字已改为「{name}」", GetPerson(personId) ?? row);
    }

    /// <summary>兼容旧调用：只关心写没写成。</summary>
    public bool ApplyName(int personId, string name, string nameSource) =>
        TryApplyName(personId, name, nameSource).Ok;

    /// <summary>收集这个人以前可能被写进记忆里的称呼：旧名字、群友NN、群内旧标签。</summary>
    private IReadOnlyList<string> CollectOldNames(int personId, PersonRow row, string newName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length < 2)
            {
                return;
            }

            if (trimmed is "—" or "（暂无）" || string.Equals(trimmed, newName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            names.Add(trimmed);
        }

        Add(row.Name);
        Add(row.DisplayLabel);
        Add(Labels.GroupMate(personId));

        foreach (var label in _db.Query(
                     """
                     SELECT DISTINCT label FROM person_label
                     WHERE open_id IN (SELECT open_id FROM person_open_id WHERE person_id=@id)
                     """,
                     reader => reader.GetString(0),
                     ("@id", personId)))
        {
            Add(label);
        }

        // 长的先替换，避免 "张三(P001)" 先被 "张三" 换掉后留下 "(P001)"。
        return names.OrderByDescending(name => name.Length).ToArray();
    }

    /// <summary>把旧称呼从人物记忆和事件详情/关键词里替换成新名字。</summary>
    private void ReplaceNameReferences(int personId, IReadOnlyList<string> oldNames, string newName)
    {
        if (oldNames.Count == 0)
        {
            return;
        }

        var person = GetPerson(personId);
        if (person is not null && !string.IsNullOrWhiteSpace(person.MemoryText))
        {
            var memory = person.MemoryText;
            foreach (var oldName in oldNames)
            {
                memory = ReplaceIgnoreCase(memory, oldName, newName);
            }

            if (!string.Equals(memory, person.MemoryText, StringComparison.Ordinal))
            {
                _db.Exec(
                    "UPDATE person SET memory_text=@m, updated_at=@t WHERE person_id=@id",
                    ("@m", memory),
                    ("@t", TimeUtil.Unix(_clock.UtcNow)),
                    ("@id", personId));
            }
        }

        foreach (var oldName in oldNames)
        {
            var events = _db.Query(
                "SELECT event_id, detail FROM event WHERE instr(lower(detail), lower(@n)) > 0",
                reader => (EventId: reader.GetInt64(0), Detail: reader.GetString(1)),
                ("@n", oldName));

            foreach (var item in events)
            {
                var detail = ReplaceIgnoreCase(item.Detail, oldName, newName);
                if (string.Equals(detail, item.Detail, StringComparison.Ordinal))
                {
                    continue;
                }

                var hash = EventStore.ContentHash(detail);
                var collision = _db.ScalarLong(
                    "SELECT COUNT(*) FROM event WHERE content_hash=@h AND event_id<>@id",
                    ("@h", hash), ("@id", item.EventId));

                if (collision > 0)
                {
                    // 极少数情况下改名后详情与另一条事件完全相同；保留旧 hash，避免撞唯一索引。
                    _db.Exec("UPDATE event SET detail=@d WHERE event_id=@id",
                        ("@d", detail), ("@id", item.EventId));
                }
                else
                {
                    _db.Exec("UPDATE event SET detail=@d, content_hash=@h WHERE event_id=@id",
                        ("@d", detail), ("@h", hash), ("@id", item.EventId));
                }
            }

            _db.Exec(
                """
                INSERT OR IGNORE INTO event_keyword(event_id, word)
                SELECT event_id, @new FROM event_keyword WHERE lower(trim(word))=lower(@old)
                """,
                ("@new", newName), ("@old", oldName));

            _db.Exec(
                "DELETE FROM event_keyword WHERE lower(trim(word))=lower(@old)",
                ("@old", oldName));
        }
    }

    private static string ReplaceIgnoreCase(string text, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(oldValue))
        {
            return text;
        }

        var index = text.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);
        var start = 0;
        while (index >= 0)
        {
            builder.Append(text, start, index - start).Append(newValue);
            start = index + oldValue.Length;
            index = text.IndexOf(oldValue, start, StringComparison.OrdinalIgnoreCase);
        }

        builder.Append(text, start, text.Length - start);
        return builder.ToString();
    }

    /// <summary>这个名字是否已经被别的 active 人物占用。</summary>
    public PersonRow? FindNameOwner(string name, int? excludePersonId = null)
    {
        var sql = $"""
            SELECT {PersonSql.Columns} FROM person p
            WHERE p.status='active'
              AND (lower(trim(p.name))=lower(@n) OR lower(trim(p.display_label))=lower(@n))
              {(excludePersonId is null ? string.Empty : "AND p.person_id<>@id")}
            ORDER BY p.person_id LIMIT 1
            """;

        return excludePersonId is null
            ? _db.QueryOne(sql, PersonSql.Map, ("@n", name))
            : _db.QueryOne(sql, PersonSql.Map, ("@n", name), ("@id", excludePersonId.Value));
    }

    /// <summary>
    /// 一次性清理历史重名：同名人物保留 person_id 最小的，后面的清掉 name / 对应别名 / 标签证据。
    /// 返回被清理的人数。幂等，启动时跑一次即可。
    /// </summary>
    public int CleanupDuplicateNames()
    {
        var rows = _db.Query(
            """
            SELECT person_id, trim(name) FROM person
            WHERE status='active' AND trim(name) <> ''
            ORDER BY lower(trim(name)), person_id
            """,
            reader => (PersonId: reader.GetInt32(0), Name: reader.GetString(1)));

        var kept = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cleared = 0;
        var now = TimeUtil.Unix(_clock.UtcNow);

        foreach (var (personId, name) in rows)
        {
            if (kept.TryGetValue(name, out _))
            {
                _db.Exec(
                    "UPDATE person SET name='', name_source='none', display_label=@l, updated_at=@t WHERE person_id=@id",
                    ("@l", Labels.GroupMate(personId)),
                    ("@t", now),
                    ("@id", personId));

                _db.Exec(
                    "DELETE FROM person_alias WHERE person_id=@id AND lower(trim(alias))=lower(@n)",
                    ("@id", personId), ("@n", name));

                _db.Exec(
                    """
                    DELETE FROM name_evidence
                    WHERE lower(trim(candidate))=lower(@n)
                      AND open_id IN (SELECT open_id FROM person_open_id WHERE person_id=@id)
                    """,
                    ("@n", name), ("@id", personId));

                _db.Exec(
                    """
                    DELETE FROM person_label
                    WHERE lower(trim(label))=lower(@n)
                      AND open_id IN (SELECT open_id FROM person_open_id WHERE person_id=@id)
                    """,
                    ("@n", name), ("@id", personId));

                cleared++;
                continue;
            }

            kept[name] = personId;

            // 旧数据可能因为重名把 display_label 写成「名字(Pxxx)」；清掉后恢复成纯名字。
            _db.Exec(
                "UPDATE person SET display_label=@l WHERE person_id=@id AND display_label<>@l",
                ("@l", name), ("@id", personId));
        }

        return cleared;
    }

    /// <summary>
    /// 名字学习：他人称呼需累计 N 次才写 name（文档 3.4.8 的可信度表）。
    /// 返回 true 表示本次采纳为正式名字，false 表示只记了证据。
    /// </summary>
    public bool RecordNameEvidence(string openId, string chatId, string candidate)
    {
        var row = GetPersonByOpenId(openId);
        if (row is null)
        {
            return false;
        }

        var check = NameRules.Check(candidate, _config.Raw.Persona.BotName);
        if (!check.Ok)
        {
            return false;
        }

        candidate = check.Name;
        if (FindNameOwner(candidate, row.PersonId) is not null)
        {
            return false;
        }

        if (MemoryGuard.Inspect(candidate, GuardKind.Name) is { Ok: false })
        {
            return false;
        }

        var cfg = _config.ForChat(chatId);
        var now = TimeUtil.Unix(_clock.UtcNow);

        _db.Exec(
            """
            INSERT INTO name_evidence(open_id, chat_id, candidate, hits, updated_at)
            VALUES(@o, @c, @n, 1, @t)
            ON CONFLICT(open_id, chat_id, candidate)
            DO UPDATE SET hits = hits + 1, updated_at = @t
            """,
            ("@o", openId),
            ("@c", chatId),
            ("@n", candidate),
            ("@t", now));

        var hits = _db.ScalarLong(
            "SELECT hits FROM name_evidence WHERE open_id=@o AND chat_id=@c AND candidate=@n",
            ("@o", openId), ("@c", chatId), ("@n", candidate));

        if (hits < cfg.NameLearnConfirmCount)
        {
            // 单次称呼只进别名，不改 name。
            _db.Exec("INSERT OR IGNORE INTO person_alias(alias, person_id, source, hits) VALUES(@a, @p, 'learned', 1)",
                ("@a", candidate), ("@p", row.PersonId));
            return false;
        }

        return TryApplyName(row.PersonId, candidate, "learned").Ok;
    }

    /// <summary>重算 display_label（重名冲突时才追加编号）。轮换时调用。</summary>
    public string RecomputeLabel(int personId)
    {
        var row = GetPerson(personId);
        if (row is null)
        {
            return string.Empty;
        }

        var label = ComputeLabel(personId, row.Name);
        if (!string.Equals(label, row.DisplayLabel, StringComparison.Ordinal))
        {
            _db.Exec("UPDATE person SET display_label=@l, updated_at=@t WHERE person_id=@id",
                ("@l", label), ("@t", TimeUtil.Unix(_clock.UtcNow)), ("@id", personId));
        }

        return label;
    }

    /// <summary>"忘记我"：按 open_id 删除该人全部记忆，级联 alias / label / 事件关联。</summary>
    public int Forget(string openId)
    {
        lock (_gate)
        {
            var personId = _db.ScalarLong("SELECT person_id FROM person_open_id WHERE open_id=@o", ("@o", openId));
            if (personId == 0)
            {
                personId = _db.ScalarLong("SELECT person_id FROM person WHERE open_id=@o", ("@o", openId));
            }

            if (personId == 0)
            {
                return 0;
            }

            _db.Exec("DELETE FROM person WHERE person_id=@id", ("@id", personId));
            _db.Exec("DELETE FROM person_open_id WHERE person_id=@id", ("@id", personId));
            _db.Exec("DELETE FROM name_evidence WHERE open_id=@o", ("@o", openId));

            foreach (var key in _cache.Keys.Where(k => k.OpenId == openId).ToArray())
            {
                _cache.Remove(key);
            }

            _opLog.Append($"forget:{openId}", "delete", "person", personId, null, null, "manual");
            return 1;
        }
    }

    /// <summary>轮换后调用：清掉标签缓存与临时标签序列，让新会话重新计算标签。</summary>
    public void ResetSession(string chatId)
    {
        lock (_gate)
        {
            foreach (var key in _cache.Keys.Where(k => k.ChatId == chatId).ToArray())
            {
                _cache.Remove(key);
            }

            foreach (var key in _tempLabels.Keys.Where(k => k.ChatId == chatId).ToArray())
            {
                _tempLabels.Remove(key);
            }

            _tempSeq.Remove(chatId);
            foreach (var person in ListForPrefix(chatId))
            {
                RecomputeLabel(person.PersonId);
            }
        }
    }

    private PersonRef Materialize(IncomingMessage message, int msgCount)
    {
        var now = TimeUtil.Unix(_clock.UtcNow);
        var personId = (int)_db.ScalarLong(
            """
            INSERT INTO person(open_id, union_open_id, name, name_source, display_label, importance, status,
                               msg_count, first_seen_at, created_at, updated_at)
            VALUES(@open, @union, '', 'none', '', 3, 'active', @cnt, @now, @now, @now)
            RETURNING person_id
            """,
            ("@open", message.OpenId),
            ("@union", message.UnionOpenId),
            ("@cnt", msgCount),
            ("@now", now));

        var label = ComputeLabel(personId, string.Empty);
        _db.Exec("UPDATE person SET display_label=@l WHERE person_id=@id", ("@l", label), ("@id", personId));
        EnsureOpenIdAlias(personId, message.OpenId);

        _opLog.Append($"materialize:{personId}", "insert", "person", personId, null,
            new { PersonId = personId, OpenId = message.OpenId, DisplayLabel = label }, "auto");

        return new PersonRef(personId, message.OpenId, label, string.Empty, "none", true, null, false);
    }

    private PersonRow? FindPerson(string openId, string? unionOpenId)
    {
        var byAlias = _db.QueryOne(
            $"SELECT {PersonSql.Columns} FROM person p JOIN person_open_id po ON po.person_id=p.person_id WHERE po.open_id=@o LIMIT 1",
            PersonSql.Map,
            ("@o", openId));
        if (byAlias is not null)
        {
            return byAlias;
        }

        var byOpenId = _db.QueryOne(
            $"SELECT {PersonSql.Columns} FROM person p WHERE p.open_id=@o LIMIT 1",
            PersonSql.Map,
            ("@o", openId));
        if (byOpenId is not null)
        {
            return byOpenId;
        }

        if (string.IsNullOrWhiteSpace(unionOpenId))
        {
            return null;
        }

        return _db.QueryOne(
            $"SELECT {PersonSql.Columns} FROM person p WHERE p.union_open_id=@u LIMIT 1",
            PersonSql.Map,
            ("@u", unionOpenId));
    }

    private PersonRow EnsureUnion(PersonRow row, string? unionOpenId)
    {
        if (string.IsNullOrWhiteSpace(unionOpenId) || row.UnionOpenId == unionOpenId)
        {
            return row;
        }

        try
        {
            _db.Exec("UPDATE person SET union_open_id=@u WHERE person_id=@id AND (union_open_id IS NULL OR union_open_id='')",
                ("@u", unionOpenId), ("@id", row.PersonId));
            return row with { UnionOpenId = unionOpenId };
        }
        catch (SqliteException)
        {
            // 该 union_open_id 已绑定到另一条 person（历史数据不一致），保留原值并继续。
            return row;
        }
    }

    private void EnsureOpenIdAlias(int personId, string openId) =>
        _db.Exec(
            "INSERT OR IGNORE INTO person_open_id(open_id, person_id, first_seen_at) VALUES(@o, @p, @t)",
            ("@o", openId),
            ("@p", personId),
            ("@t", TimeUtil.Unix(_clock.UtcNow)));

    private void BumpMsgCount(int personId) =>
        _db.Exec("UPDATE person SET msg_count = msg_count + 1, updated_at=@t WHERE person_id=@id",
            ("@t", TimeUtil.Unix(_clock.UtcNow)), ("@id", personId));

    private string ResolveLabel(PersonRow row, string chatId)
    {
        var manual = _db.ScalarString(
            "SELECT label FROM person_label WHERE open_id=@o AND chat_id=@c",
            ("@o", row.OpenId), ("@c", chatId));
        if (!string.IsNullOrWhiteSpace(manual))
        {
            return manual;
        }

        if (!string.IsNullOrWhiteSpace(row.DisplayLabel))
        {
            return row.DisplayLabel;
        }

        return ComputeLabel(row.PersonId, row.Name);
    }

    /// <summary>标签规则：有名字用名字；重名追加 (Pxxx)；没名字用 群友NN。</summary>
    private string ComputeLabel(int personId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Labels.GroupMate(personId);
        }

        var duplicates = _db.ScalarLong(
            "SELECT COUNT(*) FROM person WHERE name=@n AND person_id<>@id AND status='active'",
            ("@n", name), ("@id", personId));

        return duplicates > 0 ? $"{name}({Labels.Person(personId)})" : name;
    }

    private string TempLabel((string ChatId, string OpenId) key)
    {
        if (_tempLabels.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var seq = _tempSeq.TryGetValue(key.ChatId, out var current) ? current : 0;
        _tempSeq[key.ChatId] = seq + 1;
        var label = "路人" + Ordinal(seq);
        _tempLabels[key] = label;
        return label;
    }

    private static string Ordinal(int index)
    {
        var letter = (char)('A' + (index % 26));
        var round = index / 26;
        return round == 0 ? letter.ToString() : letter + round.ToString();
    }

    private static string NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        var trimmed = label.Trim();
        trimmed = trimmed.TrimStart('@');
        trimmed = trimmed.TrimStart('＠');
        return trimmed.Trim();
    }
}
