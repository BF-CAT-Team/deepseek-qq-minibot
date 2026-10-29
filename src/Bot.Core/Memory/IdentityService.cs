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

            if (!string.IsNullOrWhiteSpace(name))
            {
                ApplyName(personId, name, "self");
                label = ComputeLabel(personId, name.Trim());
            }

            return new PersonRef(personId, openId, label, name, name.Length > 0 ? "self" : "none", true, null, false);
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

    public void ApplyName(int personId, string name, string nameSource)
    {
        var row = GetPerson(personId);
        if (row is null)
        {
            return;
        }

        if (row.NameSource == "manual" && nameSource != "manual")
        {
            return; // 人工配置最高，不被自动覆盖
        }

        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var label = ComputeLabel(personId, trimmed);
        var before = MemoryOpLog.PersonSnapshot(row);
        var now = TimeUtil.Unix(_clock.UtcNow);

        _db.Exec(
            "UPDATE person SET name=@n, name_source=@s, display_label=@l, updated_at=@t WHERE person_id=@id",
            ("@n", trimmed),
            ("@s", nameSource),
            ("@l", label),
            ("@t", now),
            ("@id", personId));

        _opLog.Append($"name:{personId}", "update", "person", personId, before, null,
            nameSource == "manual" ? "manual" : "summary");
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

        ApplyName(row.PersonId, candidate, "learned");
        return true;
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
